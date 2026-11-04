using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.Persistence;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// Wildlife in the running product (#10567), apart from the hostile walkers. Each update it reads
/// the player and the world's clock, sleeps herds that left the player's reach or resident ground,
/// wakes seeded herds whose range puts them near, lets <see cref="WildlifeRules"/> decide every
/// awake animal, applies the decided velocities as body steps against the world's collision, and
/// publishes placeholder bodies.
///
/// It owns the population ledger (what each seeded herd has lost) and the awake herds; bodies and
/// appearances are derived from them. Every number it behaves by comes from the kinds' content.
/// </summary>
internal sealed class WildlifeModule : IProductModule
{
    /// <summary>Herds wake and sleep this often (updates): the ledger is walked, not every frame.</summary>
    private const int WakeEverySteps = 30;

    /// <summary>A body is aimed this far past where it should get to, so the Engine's step never brakes short of it.</summary>
    private const float AimPastMetres = 1f;

    /// <summary>Moving less than this in an update is standing still: no body step is asked for.</summary>
    private const double StillMetres = 1e-3;

    /// <summary>A shore is found by stepping back from the water toward the herd's home this far at a time.</summary>
    private const double ShoreStepMetres = 2;

    /// <summary>A debug herd starts this far beyond its kind's notice distance east of the player, calm.</summary>
    private const double SpawnBeyondNoticeMetres = 10;

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly WorldFrame frame;
    private readonly Func<WorldTime> clock;
    private readonly Cues cues;
    private readonly WildlifePresentation presentation;
    private readonly List<WildlifeHerd> awake = [];
    private readonly Dictionary<int, MemberBody> bodies = [];
    private readonly Dictionary<string, CharacterControllerConfig> configs = [];
    private readonly Dictionary<string, Vector2?> waters = [];
    private readonly Stack<int> freeIds = [];
    private readonly ProductSaveSlot<IReadOnlyList<HerdLoss>> slot;
    private WildlifeCatalog? catalog;
    private WildlifeLedger? ledger;
    private int nextId;
    private ulong bodySequence;
    private long step;
    private long swings;
    private bool started;
    private string lastEvent = "none";

    /// <summary>What the updates have cost since the last readout: wall time, body steps asked and refused, and moves along the ground.</summary>
    private (double Total, int Count, double Worst, long Steps, long Refused, long Glides) cost;

    /// <summary>The worst a wake pass and a herd's water search have cost since the last readout: both run now and then, not every update.</summary>
    private (double Wake, double Water) parts;

    /// <summary>The Engine-side state of one awake animal: the body its steps carry and the height its feet stand at.</summary>
    private sealed class MemberBody
    {
        internal CreatureBodyState? Body { get; set; }

        internal float Feet { get; set; }
    }

    internal WildlifeModule(IEngineContext engine, ProductContent content, ProductStore store, SaveIdentity identity, TerrainWorld terrain,
        PlayerController player, WorldFrame frame, Func<WorldTime> clock, Cues cues)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.cues = cues ?? throw new ArgumentNullException(nameof(cues));
        slot = new ProductSaveSlot<IReadOnlyList<HerdLoss>>(engine, store, SaveManifest.WildlifeLedger, new WildlifeLedgerCodec(identity));
        presentation = new WildlifePresentation(engine, frame);
        // A body's step carries session-local positions; when the world's origin moves, each is set down afresh.
        frame.Rebased += _ =>
        {
            foreach (MemberBody body in bodies.Values) body.Body = null;
        };
    }

    /// <summary>Ground no herd calls home or heads for (#9895): the towns' sanctuary. The product decides where towns stand.</summary>
    internal Func<double, double, bool>? Sanctuary { get; set; }

    internal IReadOnlyList<AppearanceFact> AppearanceFacts => presentation.Facts;

    private WildlifeCatalog Catalog => catalog ?? throw new InvalidOperationException("wildlife has not started");

    private WildlifeLedger Ledger => ledger ?? throw new InvalidOperationException("wildlife has not started");

    private ulong Seed => terrain.Recipe.Contract.Seed;

    public void Start()
    {
        if (started) return;
        catalog ??= WildlifeKinds.Parse(content.ReadText(WildlifeKinds.ContentPath));
        if (ledger is null)
        {
            ledger = new WildlifeLedger(catalog, Seed, BiomeAt, (x, z) => Sanctuary?.Invoke(x, z) == true);
            if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: IReadOnlyList<HerdLoss> losses }) ledger.Restore(losses);
        }

        started = true;
    }

    public void Update(ProductStep time)
    {
        if (!started || player.InSeparateSpace) return;
        long began = Stopwatch.GetTimestamp();
        step = time.Step;
        WorldTime now = clock();
        Vector3 at = player.WorldPosition;
        if (step % WakeEverySteps == 0 || awake.Count == 0)
        {
            long waking = Stopwatch.GetTimestamp();
            Sleep(at);
            Wake(at, now);
            parts.Wake = Math.Max(parts.Wake, Stopwatch.GetElapsedTime(waking).TotalMilliseconds);
        }

        Vector2 here = new(at.X, at.Z);
        Vector2? sensed = player.Vitals.IsDown ? null : here;
        foreach (WildlifeHerd herd in awake)
        {
            WildlifeRules.Decide(herd, new HerdFacts(sensed, now, Seed, WaterFor(herd), Allowed), Catalog, time.ElapsedSeconds);
            Apply(herd, here, time.ElapsedSeconds);
        }

        presentation.Sync(awake, FeetOf);
        double milliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        cost = (cost.Total + milliseconds, cost.Count + 1, Math.Max(cost.Worst, milliseconds), cost.Steps, cost.Refused, cost.Glides);
    }

    /// <summary>A fresh session over the same world: the herds sleep, and what the ledger holds stays, as the world's other saves do.</summary>
    public void Restart()
    {
        foreach (WildlifeHerd herd in awake.ToArray()) Release(herd);
        presentation.Sync(awake, FeetOf);
        lastEvent = "restarted";
    }

    public void Dispose()
    {
        awake.Clear();
        bodies.Clear();
        presentation.Dispose();
        started = false;
    }

    /// <summary>
    /// The biome under a point, from the continent's own map: a region is a kilometre across, so its
    /// biomes need no finer detail, and the walked geography would build region tiles to answer.
    /// </summary>
    private MapBiome BiomeAt(double x, double z) => WorldMap.Biome(terrain.Recipe.Map.Sample(x, z));

    private bool Allowed(Vector2 point) => Sanctuary?.Invoke(point.X, point.Y) != true;

    private float FeetOf(WildlifeMember member) => bodies.TryGetValue(member.Id, out MemberBody? body) ? body.Feet : terrain.GroundAt(member.Position.X, member.Position.Y);

    private static double Day(WorldTime time) => time.Day + time.DayFraction;

    /// <summary>Whether the block under a point is resident: a herd over ground the world has not loaded sleeps.</summary>
    private bool Resident(Vector2 position, float feet) =>
        terrain.IsResident(new VoxelAddress((long)Math.Floor(position.X), (long)Math.Floor(feet) - 1, (long)Math.Floor(position.Y)));

    /// <summary>Herds whose leader has gone beyond the sleep distance, or over ground no longer resident, sleep.</summary>
    private void Sleep(Vector3 at)
    {
        Vector2 here = new(at.X, at.Z);
        foreach (WildlifeHerd herd in awake.ToArray())
        {
            WildlifeMember leader = herd.Leader;
            if (Vector2.Distance(leader.Position, here) > Catalog.Waking.SleepMetres || !Resident(leader.Position, FeetOf(leader)))
            {
                Release(herd);
                lastEvent = $"{herd.Key} slept";
            }
        }
    }

    /// <summary>
    /// Wakes the herds whose range puts them within the wake distance now, on resident ground, nearest
    /// first, up to the most that may be awake. A herd wakes with the animals its ledger has left.
    /// </summary>
    private void Wake(Vector3 at, WorldTime now)
    {
        WildlifeWaking waking = Catalog.Waking;
        if (awake.Count >= waking.MostAwakeHerds) return;
        double reach = waking.WakeMetres + Catalog.Kinds.Max(kind => kind.Population.RangeMetres);
        Vector2 here = new(at.X, at.Z);
        HashSet<string> keys = [.. awake.Select(herd => herd.Key)];
        var candidates = Ledger.HomesWithin(at.X, at.Z, reach)
            .Where(record => !keys.Contains(record.Key))
            .Select(record => (Record: record, Kind: Catalog.Kind(record.KindId)))
            .Select(entry => (entry.Record, entry.Kind,
                Alive: entry.Record.Alive(Day(now), entry.Kind.Population.RecoveryDays),
                Point: WildlifeRules.WakePoint(entry.Record, entry.Kind, now, Seed, Catalog.Placement.HomeAttempts, Allowed)))
            .Where(entry => entry.Alive > 0 && Vector2.Distance(entry.Point, here) <= waking.WakeMetres)
            .OrderBy(entry => Vector2.Distance(entry.Point, here));
        foreach (var entry in candidates)
        {
            if (awake.Count >= waking.MostAwakeHerds) break;
            if (!Resident(entry.Point, terrain.GroundAt(entry.Point.X, entry.Point.Y))) continue;
            WakeAt(entry.Record, entry.Kind, entry.Point, entry.Alive);
        }
    }

    private WildlifeHerd WakeAt(HerdRecord record, WildlifeKind kind, Vector2 point, int alive)
    {
        WildlifeHerd herd = WildlifeRules.Wake(record, kind, Catalog, Seed, point, alive, TakeId);
        foreach (WildlifeMember member in herd.Members)
        {
            bodies[member.Id] = new MemberBody { Feet = terrain.GroundAt(member.Position.X, member.Position.Y) };
        }

        awake.Add(herd);
        lastEvent = string.Create(CultureInfo.InvariantCulture, $"{record.Key} woke with {alive} at {point.X:F0},{point.Y:F0}");
        return herd;
    }

    private void Release(WildlifeHerd herd)
    {
        foreach (WildlifeMember member in herd.Members) Forget(member);
        awake.Remove(herd);
    }

    private void Forget(WildlifeMember member)
    {
        bodies.Remove(member.Id);
        freeIds.Push(member.Id);
    }

    private int TakeId()
    {
        if (freeIds.Count > 0) return freeIds.Pop();
        if (nextId >= WildlifePresentation.MostMembers)
            throw new InvalidOperationException($"more than {WildlifePresentation.MostMembers} animals awake at once");
        return nextId++;
    }

    /// <summary>
    /// Where a herd drinks: the nearest open river or lake to its home inside its range, stepped back
    /// to the shore toward home, found once per herd. Null when its range has none (it then roams).
    /// </summary>
    private Vector2? WaterFor(WildlifeHerd herd)
    {
        if (waters.TryGetValue(herd.Key, out Vector2? found)) return found;
        long began = Stopwatch.GetTimestamp();
        WildlifePlacement placement = Catalog.Placement;
        double range = herd.Kind.Population.RangeMetres;
        Vector2? nearest = null;
        for (int ring = 1; ring <= placement.WaterRings && nearest is null; ring++)
        {
            double radius = range * ring / placement.WaterRings;
            for (int heading = 0; heading < placement.WaterHeadings; heading++)
            {
                double angle = 2 * Math.PI * heading / placement.WaterHeadings;
                Vector2 point = herd.Home + new Vector2((float)(radius * Math.Sin(angle)), (float)(radius * -Math.Cos(angle)));
                if (Drinkable(point) && Allowed(point))
                {
                    nearest = point;
                    break;
                }
            }
        }

        Vector2? shore = null;
        if (nearest is Vector2 water)
        {
            Vector2 back = Vector2.Normalize(herd.Home - water);
            double distance = Vector2.Distance(herd.Home, water);
            for (double walked = ShoreStepMetres; walked < distance; walked += ShoreStepMetres)
            {
                Vector2 point = water + (back * (float)walked);
                if (!terrain.Water.Sample(point.X, point.Y).Wet)
                {
                    shore = point;
                    break;
                }
            }
        }

        waters[herd.Key] = shore;
        parts.Water = Math.Max(parts.Water, Stopwatch.GetElapsedTime(began).TotalMilliseconds);
        return shore;
    }

    private bool Drinkable(Vector2 point)
    {
        Water.WaterSample sample = terrain.Water.Sample(point.X, point.Y);
        return sample.Wet && !sample.Ice && sample.Body is WaterBody.River or WaterBody.Lake;
    }

    private CharacterControllerConfig Config(WildlifeKind kind)
    {
        if (configs.TryGetValue(kind.Id, out CharacterControllerConfig config)) return config;
        WildlifeCapsule capsule = Catalog.SizeClasses[kind.Body.SizeClass];
        config = CreatureBody.Configure(
            CreatureBody.Capsule(engine.Spatial.DefaultCharacterControllerConfig(), capsule.Radius, capsule.Height),
            (float)kind.Speeds.Flee);
        configs[kind.Id] = config;
        return config;
    }

    /// <summary>
    /// Moves each animal by the velocity the rules decided. Near the player the Engine's character step
    /// carries its size class's capsule against the world's collision, so it stops at what it meets and
    /// stands on what the step finds. Further out, or where the Engine finds the body lodged, it follows
    /// the ground instead: a step costs far more than a cast down. A still animal asks for neither.
    /// </summary>
    private void Apply(WildlifeHerd herd, Vector2 here, double seconds)
    {
        CharacterControllerConfig config = Config(herd.Kind);
        foreach (WildlifeMember member in herd.Members)
        {
            MemberBody state = bodies[member.Id];
            float speed = member.Velocity.Length();
            double travel = speed * seconds;
            if (travel < StillMetres) continue;
            if (Vector2.Distance(member.Position, here) > Catalog.Waking.BodyMetres)
            {
                Glide(member, state, seconds);
                continue;
            }

            CreatureBodyState body = state.Body ?? new CreatureBodyState(
                CreatureBody.CentreOver(frame.ToLocal(member.Position.X, state.Feet, member.Position.Y), config),
                CreatureBody.AtRest(CreatureBody.CentreOver(frame.ToLocal(member.Position.X, state.Feet, member.Position.Y), config)));
            Vector2 target = member.Position + (member.Velocity / speed * (float)(travel + AimPastMetres));
            Vector3 toward = frame.ToLocal(target.X, 0, target.Y);
            cost.Steps++;
            if (CreatureBody.Step(engine.Spatial, terrain.Session, body, config, new Vector2(toward.X, toward.Z), travel, seconds, () => ++bodySequence)
                is not CreatureBodyState moved)
            {
                cost.Refused++;
                Glide(member, state, seconds);
                continue;
            }

            state.Body = moved;
            Vector3 feet = frame.ToWorld(CreatureBody.FeetUnder(moved.Centre, config));
            state.Feet = feet.Y;
            member.Position = new Vector2(feet.X, feet.Z);
        }
    }

    /// <summary>
    /// Moves an animal along the ground by its velocity without a body: its feet take the first surface
    /// below its step reach, or the column's ground where that is not resident. Its body is set down
    /// afresh when it next steps.
    /// </summary>
    private void Glide(WildlifeMember member, MemberBody state, double seconds)
    {
        cost.Glides++;
        Vector2 next = member.Position + (member.Velocity * (float)seconds);
        state.Feet = terrain.GroundBelow(next.X, next.Y, state.Feet, CreatureNavigation.ClimbMetres) ?? terrain.GroundAt(next.X, next.Y);
        state.Body = null;
        member.Position = next;
    }

    /// <summary>
    /// The player's swing at the nearest awake animal within reach of its flank (#10567), when no
    /// hostile creature was in reach; null when there is none. A blow, landed or not, presses the
    /// animal; a kill takes one from its herd's ledger and pays out as a creature's does.
    /// </summary>
    internal string? PlayerSwing()
    {
        if (!started || player.Vitals.IsDown || player.InSeparateSpace) return null;
        Vector3 at = player.WorldPosition;
        Vector2 here = new(at.X, at.Z);
        (WildlifeHerd Herd, WildlifeMember Member, double Gap)? nearest = awake
            .SelectMany(herd => herd.Members.Select(member => (Herd: herd, Member: member,
                Gap: Vector2.Distance(member.Position, here) - (herd.Kind.Body.WidthMetres / 2))))
            .Where(entry => entry.Gap <= CharacterSheet.UnarmedReachMetres)
            .OrderBy(entry => entry.Gap)
            .Select(entry => ((WildlifeHerd, WildlifeMember, double)?)entry)
            .FirstOrDefault();
        if (nearest is not (WildlifeHerd herd, WildlifeMember member, _)) return null;

        swings++;
        int roll = KeyedDraws.AttackRoll(Seed, string.Create(CultureInfo.InvariantCulture, $"player:wildlife-swing:{swings}"));
        (CombatantState struck, AttackOutcome outcome) = EncounterResolutionRules.Strike(roll, player.Sheet.Unarmed, member.Combat, step);
        member.Struck = true;
        Vector3 local = frame.ToLocal(member.Position.X, FeetOf(member) + (float)(herd.Kind.Body.HeightMetres / 2), member.Position.Y);
        if (!outcome.Hit)
        {
            cues.Raise(Cue.Swing);
            return $"missed {herd.Kind.Id} {member.Id}: roll {outcome.Roll} vs defence {outcome.Defence}";
        }

        member.Combat = struck;
        cues.RaiseAt(struck.IsDown ? Cue.Defeat : Cue.Strike, local);
        if (!struck.IsDown)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"hit {herd.Kind.Id} {member.Id}: {struck.Health}/{struck.MaximumHealth} for {outcome.Damage}");
        }

        double day = Day(clock());
        herd.Remove(member);
        Forget(member);
        herd.Record.Kill(day, herd.Kind.Population.RecoveryDays);
        slot.Save(Ledger.Losses(day));
        if (herd.Members.Count == 0) awake.Remove(herd);
        presentation.Sync(awake, FeetOf);
        EncounterReward reward = EncounterResolutionRules.Reward(herd.Kind.ExperienceAward, herd.Kind.Loot,
            string.Create(CultureInfo.InvariantCulture, $"wildlife:{herd.Key}:{swings}"), KeyedDraws.For(Seed));
        ProgressionOutcome progression = player.Progress.Award(reward);
        lastEvent = $"killed a {herd.Kind.Id} of {herd.Key}";
        return string.Create(CultureInfo.InvariantCulture,
            $"killed {herd.Kind.Id} {member.Id}; {herd.Key} has {herd.Record.Alive(day, herd.Kind.Population.RecoveryDays)}/{herd.Record.Founding}; xp={progression.Experience} drops={string.Join(",", reward.Drops.Select(drop => $"{drop.ItemId}:{drop.Quantity}"))}");
    }

    /// <summary>Places a herd of a kind (middle of its group size) a distance east and south of the player, awake at once; it joins no region's population.</summary>
    internal string Spawn(string kindId, double? east = null, double? south = null)
    {
        if (!started) return "wildlife: not started";
        if (!Catalog.TryKind(kindId, out WildlifeKind kind)) return $"no kind '{kindId}'; kinds: {string.Join(",", Catalog.Kinds.Select(k => k.Id))}";
        Vector3 at = player.WorldPosition;
        Vector2 point = new(at.X + (float)(east ?? (kind.Senses.NoticeMetres + SpawnBeyondNoticeMetres)), at.Z + (float)(south ?? 0));
        int size = (kind.GroupMinimum + kind.GroupMaximum) / 2;
        if (awake.Count >= Catalog.Waking.MostAwakeHerds) Release(awake.OrderByDescending(herd => Vector2.Distance(herd.Leader.Position, new Vector2(at.X, at.Z))).First());
        WildlifeHerd herd = WakeAt(Ledger.Place(kind, point, size), kind, point, size);
        presentation.Sync(awake, FeetOf);
        return Readout(herd);
    }

    /// <summary>Puts every awake herd to sleep; the ledger keeps them.</summary>
    internal string SleepAll()
    {
        foreach (WildlifeHerd herd in awake.ToArray()) Release(herd);
        presentation.Sync(awake, FeetOf);
        return "all herds asleep";
    }

    /// <summary>The awake herds, their activity, alarm and animals, and what updates have cost since the last readout (which it resets).</summary>
    internal string Readout()
    {
        if (!started) return "wildlife: not started";
        Vector3 at = player.WorldPosition;
        WorldTime now = clock();
        string costs = string.Create(CultureInfo.InvariantCulture,
            $"updates={cost.Count} meanMs={(cost.Count == 0 ? 0 : cost.Total / cost.Count):F3} worstMs={cost.Worst:F3} bodySteps/update={(cost.Count == 0 ? 0 : cost.Steps / (double)cost.Count):F1} glides/update={(cost.Count == 0 ? 0 : cost.Glides / (double)cost.Count):F1} refused={cost.Refused} wakePassWorstMs={parts.Wake:F2} waterSearchWorstMs={parts.Water:F2}");
        cost = default;
        parts = default;
        return string.Create(CultureInfo.InvariantCulture,
            $"step={step} day={now.Day} hour={now.DayFraction * WildlifeSchedule.HoursPerDay:F2} herds={awake.Count} animals={awake.Sum(herd => herd.Members.Count)} regions={Ledger.Regions.Count()} player={at.X:F0},{at.Z:F0} {costs} last={lastEvent} asleep={Asleep(at, now)} | ")
            + string.Join(" | ", awake.Select(Readout));
    }

    /// <summary>The nearest sleeping herds and why each sleeps: where its range puts it now, how far that is, and whether that ground is resident.</summary>
    private string Asleep(Vector3 at, WorldTime now)
    {
        Vector2 here = new(at.X, at.Z);
        HashSet<string> keys = [.. awake.Select(herd => herd.Key)];
        return string.Join(",", Ledger.HomesWithin(at.X, at.Z, Catalog.Waking.WakeMetres + Catalog.Kinds.Max(kind => kind.Population.RangeMetres))
            .Where(record => !keys.Contains(record.Key))
            .Select(record => (Record: record, Point: WildlifeRules.WakePoint(record, Catalog.Kind(record.KindId), now, Seed, Catalog.Placement.HomeAttempts, Allowed)))
            .OrderBy(entry => Vector2.Distance(entry.Point, here))
            .Take(MostFound)
            .Select(entry => string.Create(CultureInfo.InvariantCulture,
                $"{entry.Record.Key}@{entry.Point.X:F0},{entry.Point.Y:F0}={Vector2.Distance(entry.Point, here):F0}m{(Resident(entry.Point, terrain.GroundAt(entry.Point.X, entry.Point.Y)) ? "" : "(unloaded)")}")));
    }

    private string Readout(WildlifeHerd herd)
    {
        Vector3 at = player.WorldPosition;
        Vector2 here = new(at.X, at.Z);
        string stages = string.Join(",", Enum.GetValues<Wariness>().Select(stage => $"{stage}={herd.Members.Count(m => m.Stage == stage)}"));
        Vector2? water = waters.GetValueOrDefault(herd.Key);
        return string.Create(CultureInfo.InvariantCulture,
            $"{herd.Key} kind={herd.Kind.Id} n={herd.Members.Count} activity={herd.Activity} alarm={herd.Alarm} [{stages}] leader={herd.Leader.Position.X:F1},{FeetOf(herd.Leader):F1},{herd.Leader.Position.Y:F1} d={Vector2.Distance(herd.Leader.Position, here):F1} goal={herd.Goal.X:F0},{herd.Goal.Y:F0} water={(water is Vector2 w ? $"{w.X:F0},{w.Y:F0}" : "none")} spread={herd.Members.Max(m => Vector2.Distance(m.Position, herd.Leader.Position)):F1} ids={string.Join(",", herd.Members.Select(m => $"{m.Id}:{m.Stage}"))}");
    }

    /// <summary>The population ledger: each seeded region's biomes and herds, with what each has left now.</summary>
    internal string RegionReadout()
    {
        if (!started) return "wildlife: not started";
        double day = Day(clock());
        Vector3 at = player.WorldPosition;
        WildlifeRegion here = WildlifeRegion.At(at.X, at.Z, Catalog.Placement.RegionMetres);
        IEnumerable<string> regions = Ledger.Regions.OrderBy(region => Math.Abs(region.Region.X - here.X) + Math.Abs(region.Region.Z - here.Z)).Select(region =>
            $"region {region.Region}{(region.Region == here ? " (here)" : "")} biomes={string.Join(",", region.Biomes.OrderByDescending(b => b.Value).Select(b => $"{b.Key}:{b.Value}"))} herds="
            + (region.Herds.Count == 0 ? "none" : string.Join(";", region.Herds.Select(herd => HerdLine(herd, day)))));
        IEnumerable<string> placed = Ledger.Placed.Select(herd => "placed " + HerdLine(herd, day));
        string saved = string.Create(CultureInfo.InvariantCulture,
            $"short={Ledger.Losses(day).Count} restore={slot.RestoreOutcome} saves={slot.Saves} failure={slot.LastFailure ?? "none"}");
        return string.Join(" | ", regions.Concat(placed).Prepend(saved));
    }

    /// <summary>The nearest seeded herds of a kind within some kilometres of the player, nearest region first, seeding the regions it searches.</summary>
    internal string Find(string kindId, double kilometres)
    {
        if (!started) return "wildlife: not started";
        if (!Catalog.TryKind(kindId, out WildlifeKind kind)) return $"no kind '{kindId}'";
        Vector3 at = player.WorldPosition;
        Vector2 here = new(at.X, at.Z);
        double day = Day(clock());
        List<HerdRecord> found = [.. Ledger.HomesWithin(at.X, at.Z, kilometres * MetresPerKilometre)
            .Where(herd => herd.KindId == kind.Id)
            .OrderBy(herd => Vector2.Distance(herd.Home, here))
            .Take(MostFound)];
        return found.Count == 0 ? $"no {kind.Id} within {kilometres} km"
            : string.Join(" | ", found.Select(herd => string.Create(CultureInfo.InvariantCulture,
                $"{HerdLine(herd, day)} d={Vector2.Distance(herd.Home, here):F0} biome={BiomeAt(herd.Home.X, herd.Home.Y)}")));
    }

    private const double MetresPerKilometre = 1000;
    private const int MostFound = 5;

    private string HerdLine(HerdRecord herd, double day)
    {
        WildlifeKind kind = Catalog.Kind(herd.KindId);
        return string.Create(CultureInfo.InvariantCulture,
            $"{herd.Key} home={herd.Home.X:F0},{herd.Home.Y:F0} alive={herd.Alive(day, kind.Population.RecoveryDays)}/{herd.Founding}{(awake.Any(h => h.Key == herd.Key) ? " awake" : "")}");
    }

    /// <summary>
    /// What a collision-navigation projection costs for each size class the kinds use (#10567), over the
    /// creatures' box around the player. The session holds one projection, so each replaces the last;
    /// <paramref name="measure"/> publishes one and reports it.
    /// </summary>
    internal string NavigationCost(Func<CharacterControllerConfig, string> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (!started) return "wildlife: not started";
        CharacterControllerConfig baseline = engine.Spatial.DefaultCharacterControllerConfig();
        return string.Join(" | ", Catalog.Kinds.Select(kind => kind.Body.SizeClass).Distinct().Order().Select(sizeClass =>
        {
            WildlifeCapsule capsule = Catalog.SizeClasses[sizeClass];
            return string.Create(CultureInfo.InvariantCulture, $"{sizeClass} r={capsule.Radius} h={capsule.Height}: ")
                + measure(CreatureBody.Capsule(baseline, capsule.Radius, capsule.Height));
        }));
    }
}
