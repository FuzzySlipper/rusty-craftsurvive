using System.Numerics;
using CraftSurvive.Game.Modules.Birds;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.Wildlife;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

// Wildlife's rules (#10567), checked without a host: the kinds the product ships, a herd keeping
// together, alarm spreading, every pressed response, deterministic region seeding, and a hunted
// population recovering slowly.

const ulong Seed = 0x5EED_10567UL;
const double Tick = 0.1;
WildlifeCatalog catalog = WildlifeKinds.Parse(ReadKinds());
int nextId = 0;
int NextId() => ++nextId;

// A herd woken at a point, awake from the given time of day.
WildlifeHerd Herd(string kindId, Vector2 at, int count, string key = "test")
{
    WildlifeKind kind = catalog.Kind(kindId);
    return WildlifeRules.Wake(new HerdRecord(key, kind.Id, at, count), kind, catalog, Seed, at, count, NextId);
}

// Runs a herd for some seconds, the player standing where the function says at each moment.
void Run(WildlifeHerd herd, double seconds, WorldTime start, Func<double, Vector2?> player, Action<double>? each = null, Vector2? water = null)
{
    for (double t = 0; t < seconds; t += Tick)
    {
        WorldTime time = start with { DayFraction = start.DayFraction + (t / WorldClock.DaySeconds) };
        WildlifeRules.Decide(herd, new HerdFacts(player(t), time, Seed, water), catalog, Tick);
        WildlifeRules.Integrate(herd, Tick);
        each?.Invoke(t);
    }
}

WorldTime Morning = new(3, 6.5 / 24);
WorldTime Midnight = new(3, 0.5 / 24);

double SpreadRadius(WildlifeHerd herd) => herd.Members.Max(m => Vector2.Distance(m.Position, herd.Leader.Position));

// Between grown animals: the young run about among them.
double ClosestPair(WildlifeHerd herd)
{
    double closest = double.PositiveInfinity;
    WildlifeMember[] grown = [.. herd.Members.Where(member => !member.Young)];
    for (int i = 0; i < grown.Length; i++)
        for (int j = i + 1; j < grown.Length; j++)
            closest = Math.Min(closest, Vector2.Distance(grown[i].Position, grown[j].Position));
    return closest;
}

// How far a herd of n may spread around its leader and still be one herd.
double CohesionBound(WildlifeKind kind, int count) =>
    kind.SpacingMetres * ((0.75 * Math.Sqrt(count - 1)) + catalog.Behaviour.CatchUpSpacings + catalog.Behaviour.SlotJitterShare);

Check.Section("birds", () =>
{
    // Patch, 2026-11-05: birds are drawn, not simulated. They come from beyond sight and cross the sky, never speeding towards the player.
    BirdCatalog birds = BirdCatalog.Parse(ReadResource("Wildlife.birds.json"));
    BirdKind soarer = birds.Kinds[0];
    Check.That(birds.Kinds.All(kind => kind.Mesh.Glide != kind.Mesh.Flap), "a bird's glide and flap are different clips");
    Check.That(soarer.FliesAt(MapBiome.Desert, 12, Season.Summer), "a soaring bird flies over the desert at noon");
    Check.That(!soarer.FliesAt(MapBiome.Desert, 3, Season.Summer), "it does not fly in the night");
    Check.That(!soarer.FliesAt(MapBiome.Grassland, 12, Season.Summer), "it keeps to its own country");
    Check.That(FlightRules.Choose([], MapBiome.Desert, 0.5) is null, "with nothing flying here no flight is sent");
    Check.That(FlightRules.Choose(birds.Kinds, MapBiome.Desert, 0.5) == soarer, "a kind is chosen for the ground");

    Random random = new(10630);
    Vector2 player = new(5000, -3000);
    for (int trial = 0; trial < 200; trial++)
    {
        Flight flight = FlightRules.Plan(soarer, birds, player, random.NextDouble, (x, z) => 900 + (x % 40));
        foreach (FlightSlot slot in flight.Slots)
        {
            BirdPose first = FlightRules.At(flight, slot, 0), last = FlightRules.At(flight, slot, flight.Seconds);
            double circle = (flight.Radius * (1 + soarer.Flock.RadiusShare)) + 1;
            Check.That(Vector2.Distance(first.Ground, player) >= birds.ReachMetres - circle, "a flight starts beyond sight");
            Check.That(Vector2.Distance(last.Ground, player) >= birds.ReachMetres - circle, "a flight ends beyond sight");
            Check.That(first.Position.Y >= 900 + soarer.Flight.HeightMetres.Least, "a flight is high above the ground it crosses");
        }

        Check.That(FlightRules.Passes(flight, player) >= birds.MissMetres.Least - 1, "a flight's line misses the player");
        Check.That(FlightRules.Passes(flight, player) <= birds.MissMetres.Most + 1, "and passes within the sky above them");
        Check.That(flight.Slots.Count >= soarer.Flock.Least && flight.Slots.Count <= soarer.Flock.Most, "a flock is its kind's size");
    }

    Flight circling = FlightRules.Plan(soarer, birds, player, random.NextDouble, (_, _) => 900);
    FlightSlot only = circling.Slots[0];
    BirdPose a = FlightRules.At(circling, only, 100), b = FlightRules.At(circling, only, 100.1);
    Vector2 facing = WildlifeRules.Facing(a.Yaw);
    Check.That(Vector2.Dot(facing, Vector2.Normalize(b.Ground - a.Ground)) > 0.99f, "a bird faces the way it is going");
    Check.That(Math.Sign(a.Bank) == Math.Sign(circling.TurnRadiansPerSecond), "it leans into its turn");
    int flapping = Enumerable.Range(0, 900).Count(second => FlightRules.Flapping(circling, only, second));
    Check.That(flapping > 0 && flapping < 200, "it soars, and flaps now and then");
    Check.Throws<InvalidDataException>(() => BirdCatalog.Parse(ReadResource("Wildlife.birds.json").Replace("\"Desert\"", "\"Dessert\"")), "an unknown biome is refused");
});

Check.Section("ragdolls", () =>
{
    // A body has to go while the ground under it is resident: 8 requested chunks of 16 m (TerrainConstants).
    const double ResidentMetres = 128;
    Check.That(catalog.Animation.CorpseMetres > 0 && catalog.Animation.CorpseMetres <= ResidentMetres, "a body goes before the ground under it does");
    Check.That(catalog.Animation.Ragdoll.BlowMetresPerSecond >= 0, "a killing blow drives a body away from the player");

    foreach (WildlifeKind kind in catalog.Kinds.Where(kind => kind.Mesh is not null))
    {
        string path = kind.Mesh!.Ragdoll ?? throw new InvalidDataException($"{kind.Id} has a mesh and no ragdoll");
        WildlifeRagdollRig rig = WildlifeRagdollRig.Parse(ReadResource($"Wildlife.{Path.GetFileName(path)}"));
        string[] joints = [.. rig.Bones.Select(bone => bone.Joint)];

        Check.That(rig.Bones[0].Joint == "hips", $"{kind.Id}'s ragdoll starts from its hips, which the fall follows");
        Check.That(joints[rig.HitBone] == "chest", $"{kind.Id}'s killing blow lands on its chest");
        Check.That(rig.Bones.All(bone => bone.Radius > 0 && bone.Mass > 0), $"{kind.Id}'s bones have a body");
        Check.That(rig.Bones.All(bone => bone.End is null ? bone.Length > 0 : joints.Contains(bone.End)), $"{kind.Id}'s bones end at a bone or in a length");

        // One link to each bone but the first, so the body hangs together as one tree.
        int[] children = [.. rig.Links.Select(link => link.Child).Order()];
        Check.That(children.SequenceEqual(Enumerable.Range(1, rig.Bones.Count - 1)), $"{kind.Id}'s bones are joined into one body");

        // Limits count from the rest pose, so a limit that leaves it out would throw the body at the first step.
        Check.That(rig.Links.All(link => link.Least <= 0 && link.Most >= 0), $"{kind.Id}'s joints can stand at rest");
        Check.That(rig.Links.All(link => link.Swing is null or > 0), $"{kind.Id}'s cones can swing");
        Check.That(rig.Links.All(link => MathF.Abs(link.Axis.Length() - 1) < 0.01f), $"{kind.Id}'s joint axes are units");
    }

    Check.Throws<InvalidDataException>(() => WildlifeRagdollRig.Parse("""{"hitBone":0,"bones":[],"links":[]}"""), "a ragdoll with no bones is refused");
    Check.Throws<InvalidDataException>(() => WildlifeRagdollRig.Parse("""{"hitBone":3,"bones":[{"joint":"hips","end":null,"length":1,"radius":0.1,"mass":1}],"links":[]}"""), "a hit bone it does not have is refused");
});

Check.Section("hostile animals", () =>
{
    // Patch, 2026-11-05: the desert's second hunter is a large rangy bird, bigger than the gaunt.
    WildlifeKind beak = catalog.Kind("axe-beak");
    Check.That(beak.Pressed == PressedResponse.Hunt, "the axe-beak hunts");
    Check.That(beak.Body.HeightMetres > catalog.Kind("gaunt").Body.HeightMetres, "the axe-beak stands over the gaunt");
    Check.That(catalog.SizeClasses[beak.Body.SizeClass].Height > catalog.SizeClasses[WildlifeSizeClass.Huge].Height, "its body is as tall as it is");
    // Patch, 2026-11-05: the drift-runner hunts as a pack, in a basic form to start.
    WildlifeKind runner = catalog.Kind("drift-runner");
    WildlifePack pack = runner.Hunt!.Pack ?? throw new InvalidDataException("the drift-runner hunts as a pack");
    Check.That(runner.GroupMinimum >= 3, "a pack is more than a pair");
    Check.That(catalog.Kind("gaunt").Hunt!.Pack is null, "the gaunt hunts alone");
    Check.That(Enumerable.Range(0, 5).Select(HunterRules.SideOf).SequenceEqual([0, 1, -1, 2, -2]), "a pack spreads to both sides of its first");

    Vector2 player = Vector2.Zero, far = new(0, 40), near = new(0, 8);
    Check.That(HunterRules.Flank(far, player, 0, pack) is null, "the first of a pack comes straight in");
    Check.That(HunterRules.Flank(near, player, 1, pack) is null, "near the player they all go straight in");
    Vector2 left = HunterRules.Flank(far, player, 1, pack)!.Value, right = HunterRules.Flank(far, player, -1, pack)!.Value;
    Check.That(left.X * right.X < 0, "pack animals swing out to opposite sides");
    foreach (int side in new[] { 1, -1, 2, -2, 9 })
    {
        // Followed all the way, an animal spreads from the line it started on and arrives at the edge of the close.
        Vector2 walking = far;
        int walked = 0;
        while (HunterRules.Flank(walking, player, side, pack) is Vector2 aim && walked++ < 500)
        {
            double before = walking.Length();
            walking += Vector2.Normalize(aim - walking) * 0.5f;
            Check.That(walking.Length() < before, $"a flanking animal on side {side} keeps closing");
        }

        Check.That(walked < 500, $"an animal on side {side} gets there");
        Check.That(Math.Abs(walking.X) > 2 && Math.Sign(walking.X) != Math.Sign(far.X) || walking.X != 0, $"an animal on side {side} arrives away from the straight line");
    }

    HerdRecord den = new("runners", runner.Id, Vector2.Zero, 3), other = new("others", runner.Id, Vector2.Zero, 1);
    WildlifeMember Runner(int id) => WildlifeRules.Wake(den, runner, catalog, 7, Vector2.Zero, 1, () => id).Leader;
    Dictionary<int, Hunter> packed = new()
    {
        [1] = new Hunter(runner, den, Runner(1), 0),
        [2] = new Hunter(runner, den, Runner(2), 1),
        [3] = new Hunter(runner, other, Runner(3)),
    };
    Dictionary<string, double> alarms = [];
    bool InSight(int _) => true;
    bool OutOfSight(int _) => false;
    Check.That(HunterRules.Alerted([], packed, alarms, 0.1, InSight).Count == 0, "a pack that has seen nothing stays as it was");
    Check.That(HunterRules.Alerted([1], packed, alarms, 0.1, OutOfSight).SetEquals([1]) && alarms.Count == 0, "a clear line beyond an animal's own sight calls no one");
    Check.That(HunterRules.Alerted([1], packed, alarms, 0.1, InSight).SetEquals([1, 2]), "what one of a pack sees they all come for, and no one else does");
    Check.That(HunterRules.Alerted([], packed, alarms, pack.AlarmSeconds / 2, InSight).SetEquals([1, 2]), "a pack keeps coming when the ground hides the player for a moment");
    Check.That(HunterRules.Alerted([1], packed, alarms, pack.AlarmSeconds / 4, OutOfSight).SetEquals([1, 2]), "a call runs on while it lasts");
    Check.That(HunterRules.Alerted([1], packed, alarms, pack.AlarmSeconds / 2, OutOfSight).SetEquals([1]), "a line beyond sight does not renew a call, so it dies away");
    Check.That(alarms.Count == 0, "a dead call leaves nothing behind");

    WildlifeMember second = WildlifeRules.Wake(den, runner, catalog, 7, Vector2.Zero, 1, () => 1, null, 1).Leader;
    WildlifeMember first = WildlifeRules.Wake(den, runner, catalog, 7, Vector2.Zero, 1, () => 2).Leader;
    Check.That(first.Coat != second.Coat || first.Size != second.Size, "animals of a pack differ by their place in it");

    // Navigation holds one body: it has to clear the tallest hunter, whichever was met first.
    WildlifeCapsule gauntBody = catalog.SizeClasses[catalog.Kind("gaunt").Body.SizeClass], beakBody = catalog.SizeClasses[beak.Body.SizeClass];
    (float, float, double) gauntThenBeak = HunterRules.Envelope(gauntBody.Radius, gauntBody.Height, beakBody.Radius, beakBody.Height, 1);
    (float, float, double) beakThenGaunt = HunterRules.Envelope(beakBody.Radius, beakBody.Height, gauntBody.Radius, gauntBody.Height, 1);
    Check.That(gauntThenBeak == (1f, 4.6f, 2d), "a gaunt then an axe-beak are routed for the bird's height, on the same 2 m cells");
    Check.That(beakThenGaunt == gauntThenBeak, "the other way round holds the same body");
    (float standing, float tilt) = WildlifeClips.Stance(10, 12, 8, -1.08, -1.08, catalog.Animation);
    Check.That(standing == 10 && tilt == 0, "a body whose feet stand in one place is not pitched by the ground");

    WildlifeKind gaunt = catalog.Kind("gaunt");
    WildlifeHunt hunt = gaunt.Hunt ?? throw new InvalidDataException("the gaunt must hunt");
    Check.That(catalog.Kinds.All(k => (k.Pressed == PressedResponse.Hunt) == (k.Hunt is not null)), "a kind hunts exactly when it has a hunt block");

    // Patch, 2026-11-04: hostile animals keep to empty country, away from the lush ground neutral herds make idyllic.
    MapBiome[] lush = [MapBiome.Grassland, MapBiome.TemperateForest, MapBiome.Rainforest, MapBiome.BorealForest, MapBiome.Shrubland];
    Check.That(lush.All(biome => gaunt.HabitatWeight(biome) == 0), "a hostile animal lives on none of the lush country");
    Check.That(gaunt.HabitatWeight(MapBiome.Alpine) > 0 && gaunt.HabitatWeight(MapBiome.Desert) > 0, "it lives high and dry");

    int Gaunts(MapBiome biome) => Enumerable.Range(0, 2000)
        .SelectMany(i => WildlifeSeeding.Seed(new WildlifeRegion(i, 7), 41, catalog, (_, _) => biome).Herds)
        .Count(herd => herd.KindId == "gaunt");
    Check.Equal(0, Gaunts(MapBiome.Grassland), "no hostile animal is seeded on grassland");
    int alpine = Gaunts(MapBiome.Alpine);
    Check.That(alpine is > 0 and < 60, $"it is rare where it lives: {alpine} homes in 2000 square kilometres of mountain");

    CreatureKind creature = HunterRules.KindOf(gaunt);
    Check.Equal(hunt.ReachMetres, creature.ReachMetres, "the creature rules bite at the content's reach");
    Check.Equal(gaunt.Speeds.Flee, creature.PursueSpeedMetresPerSecond, "it hunts at its running speed");
    Check.Equal(CreatureDisposition.Hostile, creature.Tuning.Disposition, "it is hostile");
    Check.That(creature.HaltDistanceMetres < creature.ReachMetres, "it halts inside its reach");

    string manifest = ReadResource("Wildlife.gaunt.manifest.json");
    WildlifeRig rig = WildlifeRig.For(gaunt.Mesh!, CreatureManifest.Parse(manifest));
    foreach (WildlifeClipRole role in new[] { WildlifeClipRole.Threat, WildlifeClipRole.Bite, WildlifeClipRole.Hurt, WildlifeClipRole.Die, WildlifeClipRole.Run })
        Check.That(rig.Clips.ContainsKey(role), $"the gaunt has a {role} clip");
    Check.That(rig.Clip(WildlifeClipRole.Bite).Seconds > hunt.BiteWindupSeconds, "the jaws close before the bite ends");

    // The capsule holds the legs (R10615-1): the mesh stands ahead of it, so all four feet lie within its radius.
    WildlifeCapsule capsule = catalog.SizeClasses[gaunt.Body.SizeClass];
    CreatureRig stance = CreatureManifest.Parse(manifest);
    Vector2 drawn = HunterRules.Drawn(Vector2.Zero, 0, stance.HindMetres, stance.ForeMetres, 1);
    Vector2 facing = WildlifeRules.Facing(0);
    Check.That(Vector2.Dot(drawn, facing) > 0, "the mesh stands ahead of the capsule");
    double hindFoot = Vector2.Dot(drawn + (facing * (float)stance.HindMetres), facing), foreFoot = Vector2.Dot(drawn + (facing * (float)stance.ForeMetres), facing);
    Check.That(Math.Abs(hindFoot + foreFoot) < 1e-4, $"the capsule stands between the feet: {hindFoot:F2} and {foreFoot:F2}");
    Check.That(Math.Abs(hindFoot) < capsule.Radius && gaunt.Body.WidthMetres / 2 < capsule.Radius, "the feet lie within its radius");
    Check.That(capsule.Height >= gaunt.Body.HeightMetres, "and it is as tall as the animal's back");
    Check.That(hunt.HaltMetres > capsule.Radius, "it halts clear of its own capsule");

    Hunter Fresh() => new(gaunt, new HerdRecord("gaunt@test", "gaunt", Vector2.Zero, 1),
        new WildlifeMember(1, Vector2.Zero, Vector2.Zero, CombatantState.Fresh(gaunt.MaximumHealth, gaunt.Defence)));
    const double Runs = 3.7, Threat = 2.4, Step = 1d / 60;

    Hunter hunter = Fresh();
    Check.Equal(WildlifeClipRole.Idle, HunterRules.Want(hunter, CreatureState.Idle, 0, Runs), "an unaware hunter idles");
    Check.Equal(WildlifeClipRole.Walk, HunterRules.Want(hunter, CreatureState.Pursuing, 2, Runs), "slowly, it walks");
    Check.Equal(WildlifeClipRole.Run, HunterRules.Want(hunter, CreatureState.Pursuing, 7, Runs), "fast, it runs");
    Check.Equal(WildlifeClipRole.Look, HunterRules.Want(hunter, CreatureState.Attacking, 0, Runs), "standing at the player between bites, it watches them");

    HunterRules.Advance(hunter, CreatureState.Pursuing, 30, Threat, Step);
    Check.That(hunter.Holding, "a hunter that has seen the player stands to show its threat");
    Check.Equal(WildlifeClipRole.Threat, HunterRules.Want(hunter, CreatureState.Pursuing, 0, Runs), "the threat shows");
    for (int i = 0; i < 200; i++) HunterRules.Advance(hunter, CreatureState.Pursuing, 30, Threat, Step);
    Check.That(!hunter.Holding, "the threat ends, and the hunt goes on");
    HunterRules.Advance(hunter, CreatureState.Pursuing, 30, Threat, Step);
    Check.That(!hunter.Holding, "it threatens once a hunt");
    HunterRules.Advance(hunter, CreatureState.Idle, 60, Threat, Step);
    HunterRules.Advance(hunter, CreatureState.Pursuing, 30, Threat, Step);
    Check.That(hunter.Holding, "a hunter that lost interest threatens again on the next hunt");

    Hunter close = Fresh();
    HunterRules.Advance(close, CreatureState.Pursuing, hunt.ReachMetres - 0.1, Threat, Step);
    Check.That(!close.Holding, "a player already in reach gets no threat");

    Hunter biting = Fresh();
    AttackProfile blow = new(hunt.Accuracy, hunt.Power, DamageType.Piercing);
    HunterRules.Bite(biting, blow, 1.0);
    Check.That(biting.Holding, "it stands to bite");
    Check.Equal(WildlifeClipRole.Bite, HunterRules.Want(biting, CreatureState.Attacking, 0, Runs), "the bite shows");
    int closesAt = 0;
    for (int i = 1; i <= 60 && closesAt == 0; i++)
    {
        if (HunterRules.Advance(biting, CreatureState.Attacking, 2, Threat, Step) is not null) closesAt = i;
    }

    Check.That(Math.Abs((closesAt * Step) - hunt.BiteWindupSeconds) <= Step, $"the blow lands as the jaws close: step {closesAt}");
    Check.That(HunterRules.Lands(hunt, hunt.ReachMetres + hunt.DodgeMetres - 0.01, seesPlayer: true), "a player still in reach is bitten");
    Check.That(!HunterRules.Lands(hunt, hunt.ReachMetres + hunt.DodgeMetres + 0.01, seesPlayer: true), "a player who stepped away in time is not");
    Check.That(!HunterRules.Lands(hunt, hunt.ReachMetres - 0.5, seesPlayer: false), "a bite begun in sight does not land round a corner, however near the player stands");

    biting.HurtSeconds = 0.3;
    Check.Equal(WildlifeClipRole.Hurt, HunterRules.Want(biting, CreatureState.Attacking, 0, Runs), "a blow shows over the bite");

    Check.That(Math.Abs(HunterRules.Turned(0, new Vector2(1, 0), 90, 10) - (MathF.PI / 2)) < 1e-4, "it comes round to face east");
    Check.That(Math.Abs(HunterRules.Turned(0, new Vector2(1, 0), 90, 0.1) - (MathF.PI / 20)) < 1e-4, "no faster than it can turn");
    Check.Equal(1f, HunterRules.Turned(1, Vector2.Zero, 90, 1), "with nowhere to face it stays as it was");
});

Check.Section("kinds", () =>
{
    string[] firstWave = ["ground-sloth", "hipparion", "giant-deer", "entelodont", "giant-beaver", "cave-goat", "glyptodon", "mammoth"];
    string[] secondWave = ["ancylotherium", "paraceratherium", "diprotodon", "megacerops", "sivatherium"];
    Check.That(firstWave.Concat(secondWave).All(id => catalog.TryKind(id, out _)), "the first- and second-wave kinds must load");
    string[] hunters = ["gaunt", "axe-beak", "drift-runner"];
    Check.Equal(firstWave.Length + secondWave.Length + hunters.Length, catalog.Kinds.Count, "the content holds the two waves, the hunters and nothing else");
    Check.That(Enum.GetValues<PressedResponse>().All(r => catalog.Kinds.Any(k => k.Pressed == r)), "every pressed response must have a kind that shows it");
    Check.That(Enum.GetValues<WildlifeSizeClass>().All(c => catalog.SizeClasses.ContainsKey(c)), "every size class must have a capsule");
    WildlifeKind sloth = catalog.Kind("ground-sloth");
    Check.Equal(1.0, sloth.HabitatWeight(MapBiome.Grassland), "sloths live on grassland");
    Check.Equal(0.0, sloth.HabitatWeight(MapBiome.Desert), "a biome a kind does not list carries none of it");
    Check.Equal(PressedResponse.Stand, sloth.Pressed, "a crowded sloth stands");
    Check.Equal(WildlifeSizeClass.Huge, sloth.Body.SizeClass, "a sloth is huge");
    Check.Equal(0x8a6a48u, sloth.Body.SrgbColour, "a body colour is read as sRGB hex");
    Check.That(catalog.Kinds.All(k => k.Senses.ActMetres < k.Senses.NoticeMetres), "every kind notices before it acts");
    Check.That(catalog.Kinds.All(k => k.Loot.Entries.All(e => e.ItemId is "meat" or "hide")), "wildlife drops only items the inventory knows");
    Check.Equal(WildlifeActivity.Rest, sloth.Schedule.Steps[sloth.Schedule.StepAt(0.5 / 24)].Activity, "a day grazer rests after midnight");
    Check.Equal(WildlifeActivity.Graze, sloth.Schedule.Steps[sloth.Schedule.StepAt(6.0 / 24)].Activity, "a day grazer grazes at dawn");

    string text = ReadKinds();
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(text.Replace("\"noticeMetres\": 25", "\"noticeMetres\": 5")),
        "a kind that acts before it notices must be refused");
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(text.Replace("\"Desert\": 1.0", "\"Dessert\": 1.0")),
        "an unknown biome must be refused");
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(text.Replace("\"pressed\": \"hunker\"", "\"pressed\": \"burrow\"")),
        "an unknown pressed response must be refused");
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(text.Replace("\"sleepMetres\": 360", "\"sleepMetres\": 100")),
        "herds must sleep further out than they wake");
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(text.Replace("\"huge\": { \"radius\": 1.0, \"height\": 3.2 }", "\"huge\": { \"radius\": 1.0, \"height\": 1.5 }")),
        "a size class must be a capsule taller than its end caps");
});

Check.Section("cohesion", () =>
{
    WildlifeKind sloth = catalog.Kind("ground-sloth");
    WildlifeHerd herd = Herd("ground-sloth", new Vector2(100, 100), 7);
    // Scatter them, as a herd wakes or comes out of a scare.
    for (int i = 0; i < herd.Members.Count; i++) herd.Members[i].Position += new Vector2((i * 7 % 5) - 2, (i * 3 % 7) - 3) * 6;
    Vector2 startCentre = herd.Centre;
    // A game day is twenty minutes: a hundred seconds is two hours of the morning's grazing.
    Run(herd, 100, Morning, _ => null);
    Check.Equal(WildlifeActivity.Graze, herd.Activity, "a sloth herd grazes in the morning");
    Check.That(SpreadRadius(herd) <= CohesionBound(sloth, herd.Members.Count),
        $"a grazing herd keeps together around its leader (spread {SpreadRadius(herd):F1} m)");
    Check.That(ClosestPair(herd) >= sloth.SpacingMetres * 0.4, $"herd-mates keep their spacing (closest {ClosestPair(herd):F1} m)");
    Check.That(Vector2.Distance(herd.Centre, startCentre) > 20, "a grazing herd drifts across its range");
    Check.That(Vector2.Distance(herd.Leader.Position, herd.Home) <= sloth.Population.RangeMetres + 5, "the leader stays in the home range");
    Check.That(herd.Members.All(m => m.Stage == Wariness.Calm), "with no player about, every animal stays calm");

    WildlifeHerd resting = Herd("ground-sloth", new Vector2(0, 0), 5, "resting");
    Vector2 before = resting.Leader.Position;
    Run(resting, 120, Midnight, _ => null);
    Check.Equal(WildlifeActivity.Rest, resting.Activity, "a sloth herd rests after midnight");
    Check.That(Vector2.Distance(resting.Leader.Position, before) < 0.5, "a resting leader stays put");

    // The leader is killed: the next one leads and the herd keeps its shape around it.
    herd.Remove(herd.Leader);
    Check.Equal(Vector2.Zero, herd.Leader.Slot, "the new leader's slot is the herd's centre");
    Run(herd, 300, Morning with { DayFraction = 7.0 / 24 }, _ => null);
    Check.That(SpreadRadius(herd) <= CohesionBound(sloth, herd.Members.Count), "a herd that lost its leader keeps together on the next");
});

Check.Section("alarm spread", () =>
{
    WildlifeKind horse = catalog.Kind("hipparion");
    WildlifeHerd band = Herd("hipparion", new Vector2(0, 0), 12, "band");
    Run(band, 5, Morning, _ => null);
    // The player comes up on the east edge: only the nearest animal is inside its act distance.
    WildlifeMember edge = band.Members.MaxBy(m => m.Position.X)!;
    Vector2 player = edge.Position + new Vector2((float)(horse.Senses.ActMetres - 2), 0);
    Check.That(band.Members.Count(m => Vector2.Distance(m.Position, player) <= horse.Senses.ActMetres) < band.Members.Count,
        "the test's player is inside act distance of some of the band, not all of it");
    Dictionary<int, double> fled = [];
    Run(band, 10, Morning, _ => player, t =>
    {
        foreach (WildlifeMember m in band.Members.Where(m => m.Stage == Wariness.Respond)) fled.TryAdd(m.Id, t);
    });
    Check.That(fled.Count == band.Members.Count, $"the whole band must take flight, not only those the player pressed ({fled.Count}/{band.Members.Count})");
    double first = fled.Values.Min();
    Check.That(fled.Values.Max() - first <= 3, "the alarm must reach the far side of the band within a few seconds");
    Check.That(first >= horse.Senses.LookSeconds + horse.Senses.WarnSeconds - Tick, "an animal looks and warns before it bolts");

    // A herd with heads up waits for its watchers rather than walking away from them.
    WildlifeHerd watched = Herd("ground-sloth", new Vector2(500, 0), 6, "watched");
    WildlifeKind sloth = watched.Kind;
    WildlifeMember edgeSloth = watched.Members.MaxBy(m => m.Position.X)!;
    Vector2 watcher = edgeSloth.Position + new Vector2((float)(sloth.Senses.NoticeMetres - 1), 0);
    Run(watched, 60, Morning, _ => watcher);
    Check.That(watched.Members.Any(m => m.Stage == Wariness.Look), "a herd-mate within notice distance keeps watching");
    Check.That(SpreadRadius(watched) <= CohesionBound(sloth, watched.Members.Count), $"a watched herd keeps together (spread {SpreadRadius(watched):F1} m)");

    // A charging kind's partner displays rather than charges.
    WildlifeHerd pair = Herd("entelodont", new Vector2(0, 0), 2, "pair");
    WildlifeKind hog = pair.Kind;
    Vector2 near = pair.Members[0].Position + new Vector2((float)(hog.Senses.ActMetres - 1), 0);
    Run(pair, 7, Morning, _ => near);
    WildlifeMember partner = pair.Members.MaxBy(m => Vector2.Distance(m.Position, near))!;
    Check.That(partner.Stage >= Wariness.Warn, "an entelodont's partner warns too");
});

Check.Section("pressed responses", () =>
{
    // Flee: the band runs clear of the player, then regroups and calms.
    WildlifeKind horse = catalog.Kind("hipparion");
    WildlifeHerd band = Herd("hipparion", new Vector2(0, 0), 8, "flee");
    Vector2 player = band.Centre + new Vector2(20, 0);
    double startDistance = Vector2.Distance(band.Centre, player);
    bool allFled = false;
    Run(band, 20, Morning, _ => player, _ => allFled |= band.Members.All(m => m.Stage == Wariness.Respond));
    Check.That(allFled, "a pressed band flees as one");
    Check.That(Vector2.Distance(band.Centre, player) > startDistance + 40, "a fleeing band ends far from the player");
    Run(band, 30, Morning, _ => player);
    Check.That(band.Members.All(m => m.Stage is Wariness.Calm or Wariness.Regroup), "clear of the player, a band stops fleeing");
    Check.That(SpreadRadius(band) <= CohesionBound(horse, band.Members.Count), "a band regroups after fleeing");

    // Stand: a crowded sloth holds its ground facing the player, and calms once the player leaves.
    WildlifeHerd sloths = Herd("ground-sloth", new Vector2(0, 0), 3, "stand");
    WildlifeMember sloth = sloths.Members[0];
    Vector2 crowd = sloth.Position + new Vector2(5, 0);
    Run(sloths, 0.5, Midnight, _ => null);
    Vector2 stood = sloth.Position;
    Run(sloths, 10, Midnight, _ => crowd);
    Check.Equal(Wariness.Respond, sloth.Stage, "a crowded sloth stands");
    Check.That(sloths.Members.All(m => m.Stage == Wariness.Respond), "its herd stands with it");
    Check.That(Vector2.Distance(sloth.Position, stood) < 1, "a standing sloth holds its ground");
    Check.That(Math.Abs(WildlifeRules.Yaw(crowd - sloth.Position) - sloth.Yaw) < 0.01, "a standing sloth faces the player");
    Run(sloths, 20, Midnight, _ => crowd + new Vector2(200, 0));
    Check.That(sloths.Members.All(m => m.Stage == Wariness.Calm), "once the player leaves, the sloths calm");

    // Display then charge: the entelodont displays, charges to contact, then displays again.
    WildlifeHerd pair = Herd("entelodont", new Vector2(0, 0), 2, "charge");
    WildlifeKind hog = pair.Kind;
    WildlifeMember boar = pair.Members[0];
    Vector2 standing = boar.Position + new Vector2((float)(hog.Senses.ActMetres - 2), 0);
    List<Wariness> stages = [];
    double closest = double.PositiveInfinity;
    Run(pair, 15, Morning, _ => standing, _ =>
    {
        if (stages.Count == 0 || stages[^1] != boar.Stage) stages.Add(boar.Stage);
        closest = Math.Min(closest, Vector2.Distance(boar.Position, standing));
    });
    int warn = stages.IndexOf(Wariness.Warn);
    int charge = stages.IndexOf(Wariness.Respond);
    Check.That(warn >= 0 && charge > warn, $"an entelodont displays before it charges ({string.Join(">", stages)})");
    Check.That(stages.Skip(charge + 1).Contains(Wariness.Warn), "after a charge it displays again");
    Check.That(closest <= WildlifeRules.ContactMetres(hog, catalog.Behaviour) + 0.5, $"a charge closes to contact (closest {closest:F1} m)");

    // Hunker: the glyptodon stops where it is and stays down until the player has gone.
    WildlifeHerd shell = Herd("glyptodon", new Vector2(0, 0), 1, "hunker");
    WildlifeMember glyptodon = shell.Members[0];
    Vector2 poke = glyptodon.Position + new Vector2(4, 0);
    Run(shell, 6, Morning, _ => poke);
    Vector2 hunkered = glyptodon.Position;
    Run(shell, 10, Morning, _ => poke);
    Check.Equal(Wariness.Respond, glyptodon.Stage, "a pressed glyptodon hunkers");
    Check.That(Vector2.Distance(glyptodon.Position, hunkered) < 0.01, "a hunkered glyptodon does not move");
    Run(shell, 2, Morning, _ => poke + new Vector2(100, 0));
    Check.Equal(Wariness.Regroup, glyptodon.Stage, "a glyptodon comes out once the player has gone");

    // Struck: an animal answers a blow as its kind does when pressed, at once.
    WildlifeHerd struck = Herd("cave-goat", new Vector2(0, 0), 6, "struck");
    Run(struck, 1, Morning, _ => null);
    struck.Members[2].Struck = true;
    Run(struck, Tick, Morning, _ => struck.Members[2].Position + new Vector2(1, 0));
    Check.Equal(Wariness.Respond, struck.Members[2].Stage, "a struck goat flees at once");
});

Check.Section("bodies", () =>
{
    WildlifeKind kind = catalog.Kind("ground-sloth");
    WildlifeAnimation animation = catalog.Animation;
    CreatureRig built = CreatureManifest.Parse(ReadResource("Wildlife.sloth.manifest.json"));
    WildlifeRig rig = WildlifeRig.For(kind.Mesh!, built);
    Check.That(catalog.Kinds.All(k => k.Mesh is not null), "every kind has its real body");
    Check.Equal("alert", rig.Clip(WildlifeClipRole.Look).Name, "a looking sloth plays its alert clip");
    Check.Equal("idle", rig.Clip(WildlifeClipRole.Hunker).Name, "a role with no clip falls back to idling");
    Check.Throws<InvalidDataException>(() => WildlifeRig.For(kind.Mesh! with { Clips = new Dictionary<WildlifeClipRole, string> { [WildlifeClipRole.Idle] = "yawn" } }, built),
        "a clip the build does not have must be refused");

    // What the herd's state calls for.
    // A herd of grown animals, so the checks below are about the clips and not the young.
    WildlifeKind grown = kind with { Young = null };
    WildlifeHerd herd = WildlifeRules.Wake(new HerdRecord("clips", kind.Id, Vector2.Zero, 3), grown, catalog, Seed, Vector2.Zero, 3, NextId);
    WildlifeMember one = herd.Members[1];
    herd.Activity = WildlifeActivity.Graze;
    Check.Equal(WildlifeClipRole.Graze, WildlifeClips.Want(one, herd, rig), "a standing grazer feeds");
    herd.Activity = WildlifeActivity.Rest;
    Check.Equal(WildlifeClipRole.Idle, WildlifeClips.Want(one, herd, rig), "a resting animal idles");
    one.Velocity = new Vector2(0.75f, 0);
    Check.Equal(WildlifeClipRole.Walk, WildlifeClips.Want(one, herd, rig), "at walking pace it walks");
    Check.Equal(1f, WildlifeClips.Rate(WildlifeClipRole.Walk, 0.75f, rig, animation), "at the clip's own ground speed it plays at its own rate");
    one.Velocity = new Vector2(2.1f, 0);
    Check.Equal(WildlifeClipRole.Run, WildlifeClips.Want(one, herd, rig), "at fleeing pace it runs");
    Check.That(WildlifeClips.Rate(WildlifeClipRole.Run, 3, rig, animation) <= animation.MaximumRate, "a clip's rate stays within its limit");
    one.Velocity = Vector2.Zero;
    one.Enter(Wariness.Look);
    Check.Equal(WildlifeClipRole.Look, WildlifeClips.Want(one, herd, rig), "a looking animal is alert");
    one.Enter(Wariness.Warn);
    Check.Equal(WildlifeClipRole.Warn, WildlifeClips.Want(one, herd, rig), "a warning animal warns");
    Check.Equal("alert", rig.Clip(WildlifeClipRole.Warn).Name, "a kind with no warning clip warns by looking");
    one.Enter(Wariness.Respond);
    Check.Equal(WildlifeClipRole.Stand, WildlifeClips.Want(one, herd, rig), "a pressed sloth swipes");

    // A new clip waits out the hold unless the animal is alarmed.
    WildlifeMember settling = herd.Members[2];
    settling.Clip = WildlifeClipRole.Idle;
    settling.Velocity = new Vector2(0.75f, 0);
    Check.That(!WildlifeClips.Advance(settling, herd, rig, animation, 0.1), "a clip just begun holds against a walk");
    Check.That(WildlifeClips.Advance(settling, herd, rig, animation, animation.ClipHoldSeconds), "after the hold the walk starts");
    settling.Velocity = Vector2.Zero;
    settling.Enter(Wariness.Look);
    Check.That(WildlifeClips.Advance(settling, herd, rig, animation, 0.01), "alarm does not wait for the hold");

    // Heading turns at the kind's rate, the short way round.
    WildlifeHerd turning = Herd("ground-sloth", Vector2.Zero, 1, "turning");
    WildlifeMember turner = turning.Members[0];
    turner.Yaw = 3f;
    Vector2 east = turner.Position + new Vector2(100, 0);
    WildlifeRules.Decide(turning, new HerdFacts(null, Morning, Seed), catalog, 0.1);
    turner.Yaw = MathF.PI - 0.1f;
    turner.Stage = Wariness.Look;
    Vector2 behind = turner.Position + (WildlifeRules.Facing(-MathF.PI + 0.1f) * 10);
    WildlifeRules.Decide(turning, new HerdFacts(behind, Morning, Seed), catalog, 0.1);
    float turned = MathF.IEEERemainder(turner.Yaw - (MathF.PI - 0.1f), 2 * MathF.PI);
    Check.That(turned > 0 && turned <= kind.Speeds.TurnDegrees * Math.PI / 180 * 0.1 + 1e-4, $"a body turns the short way, at its rate ({turned:F3} rad)");

    // A grazing herd walks at its walking pace, in turn with feeding, and covers its graze-drift speed.
    WildlifeHerd grazers = Herd("ground-sloth", new Vector2(0, 0), 4, "stop-and-go");
    int walking = 0, standing = 0;
    Vector2 start = grazers.Leader.Position;
    Run(grazers, 60, Morning, _ => null, _ =>
    {
        float speed = grazers.Leader.Velocity.Length();
        if (speed == 0) standing++;
        else if (Math.Abs(speed - kind.Speeds.Walk) < 0.01) walking++;
    });
    Check.That(walking > 0 && standing > 0 && walking + standing >= 590, $"a grazing leader stands or walks, never creeps ({walking} walking, {standing} standing)");
    double drift = Vector2.Distance(grazers.Leader.Position, start) / 60;
    Check.That(drift > kind.Speeds.GrazeDrift * 0.6 && drift < kind.Speeds.GrazeDrift * 1.4, $"over a cycle it keeps its graze-drift speed ({drift:F2} m/s)");

    // How a body sits on the ground.
    // A column's height stands at its centre, so a plane comes back exactly between columns.
    double Plane(long x, long z) => (0.25 * (x + 0.5)) - (0.1 * (z + 0.5));
    Check.That(Math.Abs(WildlifeClips.DrawnGround(12.3, -7.8, Plane) - ((0.25 * 12.3) + (0.1 * 7.8))) < 1e-9, "the drawn ground blends the columns around a point");
    // Blocks rise in whole metres: a gentle 5% slope drawn smooth must not tip a long body as its steps would.
    double Slope(long x, long z) => 0.05 * (x + 0.5);
    double Blocks(long x, long z) => Math.Round(Slope(x, z));
    (_, float onSteps) = WildlifeClips.Stance(0.5f, (float)Blocks(9, 0), (float)Blocks(11, 0), -1.2, 1.1, animation);
    (_, float onDrawn) = WildlifeClips.Stance(0.5f, (float)WildlifeClips.DrawnGround(8.8, 0, Slope), (float)WildlifeClips.DrawnGround(11.1, 0, Slope), -1.2, 1.1, animation);
    Check.That(Math.Abs(onDrawn - (animation.PitchShare * Math.Atan(0.05))) < 1e-3 && Math.Abs(onSteps) > 2 * Math.Abs(onDrawn), $"a body on the drawn ground takes its share of the slope ({onDrawn:F3} rad), far less than the blocks there would give ({onSteps:F3})");
    (float height, float pitch) = WildlifeClips.Stance(10, 10, 10.5f, -0.12, 1.6, animation);
    Check.That(pitch > 0.15 && pitch < 0.2 && height > 10 && height < 10.1, $"rising ground pitches the nose up by a share of the slope, the origin on it ({pitch:F2} rad at {height:F2})");
    (float high, float steep) = WildlifeClips.Stance(10, 10, 14, -0.12, 1.6, animation);
    double limit = animation.MostPitchDegrees * Math.PI / 180;
    Check.That(Math.Abs(steep) < limit && high > 10, $"a cast landing on a stone cannot stand a body on end ({steep * 180 / Math.PI:F0} degrees)");
    (float held, float most) = WildlifeClips.Stance(10, 9, 11, -0.2, 0.3, animation);
    Check.That(Math.Abs(most - limit) < 1e-4 && held == 10, $"a short body between two casts pitches no further than the limit ({most * 180 / Math.PI:F0} degrees)");
    Check.Equal((10f, 0f), WildlifeClips.Stance(10, 10, 10, -0.12, 1.6, animation), "level ground is level");

    // Planting feet is what a crowd costs, so only the nearest few are planted.
    double[] many = [.. Enumerable.Range(1, animation.MostPlanted + 5).Select(i => i * 3.0)];
    Check.Equal(animation.MostPlanted * 3.0, WildlifeClips.PlantWithin(many, animation), "only the nearest few animals are planted");
    Check.Equal(double.PositiveInfinity, WildlifeClips.PlantWithin([4, 9], animation), "a few animals are all planted");

    // Planted feet hold only while the body's placement in the session does.
    Vector3 at = new(5, 10, 5);
    Quaternion facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
    Check.That(WildlifeClips.FeetHold(at, facing, at, facing, animation), "a body standing as it stood keeps its feet");
    Check.That(WildlifeClips.FeetHold(at, facing, at, -facing, animation), "a turn and its negative are the same turn");
    Check.That(WildlifeClips.FeetHold(at, facing, at + new Vector3(0.01f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.005f), animation),
        "a hair's movement keeps them, so a standing body costs nothing");
    Check.That(!WildlifeClips.FeetHold(at, facing, at, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.2f), animation), "a body turning on the spot plants again");
    Check.That(!WildlifeClips.FeetHold(at, facing, at, Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f), facing), animation), "and one pitching to the ground");
    Check.That(!WildlifeClips.FeetHold(at, facing, at - new Vector3(1024, 0, 0), facing, animation), "and one the world's origin moved out from under");

    // Ids are recycled: a body belongs to the animal it was made for, not to the number.
    List<string> retired = [];
    OwnedBodies<string> owned = new();
    WildlifeMember Animal(int id) => new(id, Vector2.Zero, Vector2.Zero, default);
    void Retire(string body) => retired.Add(body);
    WildlifeMember sloth = Animal(7), goat = Animal(7), later = Animal(7);
    Check.Equal("sloth", owned.Take(sloth, () => "sloth", Retire), "an animal gets a body");
    Check.Equal("sloth", owned.Take(sloth, () => "another", Retire), "and keeps it");
    // The sloth sleeps and a placeholder takes its id: it draws no body, and is not among the owners.
    owned.Keep(new HashSet<WildlifeMember>(), Retire);
    Check.That(retired.SequenceEqual(["sloth"]) && owned.Count == 0, "a sloth whose id went to a placeholder has its body retired");
    Check.Equal("calf", owned.Take(later, () => "calf", Retire), "and the id serves the next animal afresh");
    Check.Equal("adult", owned.Take(goat, () => "adult", Retire), "another animal under the id gets a body of its own");
    Check.That(retired.SequenceEqual(["sloth", "calf"]), "the old owner's is retired first, not handed on");
    owned.Keep(new HashSet<WildlifeMember> { goat }, Retire);
    Check.That(owned.Count == 1 && retired.Count == 2, "a body with its owner awake stays");

    // An object number goes round before it comes back: the last snapshot still holds a retired body's.
    CyclingSlots slots = new(3);
    int first = slots.Take(), second = slots.Take();
    slots.Release(first);
    Check.That(slots.Take() != first, "a number just released is not handed straight back");
    Check.Equal(first, slots.Take(), "it comes round in turn");
    Check.Throws<InvalidOperationException>(() => slots.Take(), "bodies past the range are refused rather than sharing a number");
    slots.Release(second);
    Check.Equal(second, slots.Take(), "and one in use is skipped");

    // How much of its animation an animal runs at each distance.
    Check.Equal(WildlifeTier.Full, WildlifeClips.Tier(10, WildlifeTier.Full, animation), "near, everything runs");
    Check.Equal(WildlifeTier.Play, WildlifeClips.Tier(100, WildlifeTier.Full, animation), "further, the clip plays");
    Check.Equal(WildlifeTier.Held, WildlifeClips.Tier(300, WildlifeTier.Play, animation), "further still, the pose holds");
    Check.Equal(WildlifeTier.Hidden, WildlifeClips.Tier(animation.DrawMetres + 50, WildlifeTier.Held, animation), "past the draw distance it is not drawn");
    double line = animation.FullMetres + 1;
    Check.Equal(WildlifeTier.Full, WildlifeClips.Tier(line, WildlifeTier.Full, animation), "an animal keeps the nearer tier for the margin");
    Check.Equal(WildlifeTier.Play, WildlifeClips.Tier(line, WildlifeTier.Play, animation), "and the further one until it is back inside the line");
});

Check.Section("water and ledges", () =>
{
    WildlifeBehaviour behaviour = catalog.Behaviour;
    Check.Equal(PressedResponse.TakeToWater, catalog.Kind("giant-beaver").Pressed, "a beaver takes to the water");
    Vector2 shore = new(30, 0);

    // Pressed, a beaver runs for its shore, slips in and goes under.
    WildlifeHerd family = Herd("giant-beaver", new Vector2(0, 0), 3, "dive");
    Vector2 hunter = family.Centre - new Vector2(5, 0);
    bool slipped = false;
    Run(family, 30, Morning, _ => hunter, _ => slipped |= family.Members.Any(m => m.DiveSeconds is not null && !m.Under), shore);
    Check.That(slipped, "a beaver is seen slipping in before it goes under");
    Check.That(family.Members.All(m => m.Under), "a pressed beaver with water goes under");
    double slid = behaviour.SlipMetresPerSecond * behaviour.SlipSeconds;
    Check.That(family.Members.All(m => m.Position.X > shore.X - behaviour.ShoreArrivalMetres && Vector2.Distance(m.Position, shore) <= behaviour.ShoreArrivalMetres + slid + 0.5), "it goes in at its shore, sliding on past it into the water");
    Check.That(family.Members.All(m => m.Velocity == Vector2.Zero), "under the water it does not move");

    // It stays down while the player waits on the bank, and comes up when the player has gone.
    Run(family, 60, Morning, _ => shore + new Vector2(0, 4), water: shore);
    Check.That(family.Members.All(m => m.Under && m.Stage == Wariness.Respond), "a player waiting at the shore keeps it under");
    Vector2 where = family.Leader.Position;
    Run(family, 2, Morning, _ => shore + new Vector2(0, 400), water: shore);
    Check.That(family.Members.All(m => !m.Under && m.DiveSeconds is null && m.Stage == Wariness.Regroup), "it comes up once the player has gone");
    Check.That(Vector2.Distance(family.Leader.Position, where) < 3, "it comes up where it went in");

    // Going in at different moments, a family still comes up together and stays up.
    WildlifeHerd scattered = Herd("giant-beaver", new Vector2(0, 0), 4, "scattered");
    Vector2 shock = scattered.Centre - new Vector2(5, 0);
    Run(scattered, 25, Morning, _ => shock, water: new Vector2(30, 0));
    scattered.Members[0].DiveSeconds += 5;
    Vector2 gone = new(900, 0);
    Run(scattered, 40, Morning, _ => gone, water: new Vector2(30, 0));
    Check.That(scattered.Members.All(m => !m.Under && m.DiveSeconds is null && m.Stage is Wariness.Regroup or Wariness.Calm), "a family comes up together, and the alarm does not send it back down");

    // A player leaving at once does not bring it straight back up.
    WildlifeHerd brief = Herd("giant-beaver", new Vector2(0, 0), 1, "brief");
    WildlifeMember diver = brief.Members[0];
    Run(brief, 10, Morning, _ => diver.Position - new Vector2(5, 0), water: new Vector2(10, 0));
    double wentUnder = diver.DiveSeconds ?? double.NaN;
    Check.That(diver.Under && wentUnder < behaviour.SlipSeconds + behaviour.LeastUnderSeconds, $"a beaver near water has just gone under ({wentUnder:F1} s)");
    Run(brief, 1, Morning, _ => new Vector2(500, 0), water: new Vector2(10, 0));
    Check.That(diver.Under, "it stays down its least time even with the player gone");

    // A far shore: it has long outrun the player by then, and goes in all the same.
    WildlifeHerd far = Herd("giant-beaver", new Vector2(0, 0), 2, "far");
    Vector2 startled = far.Centre - new Vector2(5, 0);
    bool wentIn = false;
    Run(far, 90, Morning, _ => startled, _ => wentIn |= far.Members.All(m => m.Under), new Vector2(150, 0));
    Check.That(wentIn, "a beaver that has made for the water goes in, however far it had to run");

    // One held back from the water gives up, and does not keep the rest under for ever.
    WildlifeHerd held = Herd("giant-beaver", new Vector2(0, 0), 3, "held");
    Vector2 near = held.Centre - new Vector2(5, 0);
    Vector2 stuckAt = held.Members[2].Position;
    Run(held, 200, Morning, _ => near, _ => held.Members[2].Position = stuckAt, new Vector2(25, 0));
    Check.That(!held.Members[2].ToWater && held.Members[2].DiveSeconds is null, "a beaver that cannot reach the water gives up");
    Run(held, 60, Morning, _ => new Vector2(900, 0), _ => held.Members[2].Position = stuckAt, new Vector2(25, 0));
    Check.That(held.Members.All(m => !m.Under), "and its family comes up all the same");

    // With no water in its range it flees like any other.
    WildlifeHerd dry = Herd("giant-beaver", new Vector2(0, 0), 3, "dry");
    Vector2 from = dry.Centre - new Vector2(5, 0);
    Run(dry, 12, Morning, _ => from);
    Check.That(dry.Members.All(m => m.DiveSeconds is null && !m.Under), "with no water it never dives");
    Check.That(dry.Centre.X > 10, "with no water it runs from the player");

    // A hurt beaver still takes to the water: it does not turn into a lone runner.
    WildlifeMember hurt = Herd("giant-beaver", new Vector2(0, 0), 1, "hurt").Members[0];
    hurt.Combat = hurt.Combat with { Health = 1 };
    Check.Equal(PressedResponse.TakeToWater, WildlifeRules.Response(hurt, catalog.Kind("giant-beaver")), "a hurt beaver makes for the water");

    // A goat springs at a rise too steep to walk, and lands before it springs again.
    WildlifeMember goat = Herd("cave-goat", new Vector2(0, 0), 1, "hop").Members[0];
    Check.That(WildlifeClips.Hops(goat, 0.6f, behaviour), "a rise past the walk makes a goat spring");
    Check.That(!WildlifeClips.Hops(goat, 0.2f, behaviour), "a walkable rise does not");
    goat.HopSeconds = 0.2;
    Check.That(!WildlifeClips.Hops(goat, 1f, behaviour), "it does not spring in the air");
    goat.HopSeconds = 0;
    goat.SinceHop = behaviour.HopEverySeconds / 2;
    Check.That(!WildlifeClips.Hops(goat, 1f, behaviour), "it lands before it springs again");

    // The rules time what the clips show.
    double ClipSeconds(string kind, string clip) => CreatureManifest.Parse(ReadResource($"Wildlife.{kind}.manifest.json")).Clips[clip].Seconds;

    Check.That(Math.Abs(ClipSeconds("giant-beaver", "slip") - behaviour.SlipSeconds) < 0.05, "the slip lasts as long as its clip");
    Check.That(Math.Abs(ClipSeconds("cave-goat", "ledge-hop") - behaviour.HopSeconds) < 0.05, "the hop lasts as long as its clip");
});

Check.Section("variety", () =>
{
    WildlifeVariety variety = catalog.Variety;
    List<WildlifeMember> all = [];
    for (int i = 0; i < 40; i++) all.AddRange(Herd("ground-sloth", Vector2.Zero, 8, $"look{i}").Members);
    Check.That(all.All(m => Math.Abs(m.Size - 1) <= variety.SizeShare + 1e-4), "sizes stay within their share");
    double most = (1 + variety.ValueShare) * (1 + variety.WarmthShare), least = (1 - variety.ValueShare) * (1 - variety.WarmthShare);
    Check.That(all.All(m => m.Coat.X <= most + 1e-4 && m.Coat.Z >= least - 1e-4 && m.Coat.Y > 0), "coats stay within theirs");
    Check.That(all.Select(m => m.Size).Distinct().Count() > all.Count / 2, "animals differ in size");
    Check.That(all.Select(m => m.Coat).Distinct().Count() > all.Count / 2, "and in coat");
    Check.That(Math.Abs(all.Average(m => m.Size) - 1) < 0.01 && Math.Abs(all.Average(m => m.Coat.Y) - 1) < 0.02, "a herd centres on its kind's own size and paint");
    Check.That(all.Any(m => m.Coat.X > m.Coat.Z) && all.Any(m => m.Coat.X < m.Coat.Z), "some coats run warmer, some cooler");
    Check.That(all.Any(m => m.Young) && all.Where(m => m.Young).Select(m => m.Coat).Distinct().Count() > 3, "the young vary too");

    // An animal looks the same each time its herd wakes.
    WildlifeHerd once = Herd("ground-sloth", Vector2.Zero, 6, "same");
    WildlifeHerd again = Herd("ground-sloth", new Vector2(50, 50), 6, "same");
    Check.That(once.Members.Zip(again.Members).All(pair => pair.First.Size == pair.Second.Size && pair.First.Coat == pair.Second.Coat && pair.First.Young == pair.Second.Young),
        "a herd wakes with the same animals in it");

    // A body's size carries through to its gait and its clips.
    WildlifeKind kind = catalog.Kind("ground-sloth");
    WildlifeMember grown = once.Members.First(m => !m.Young);
    Check.Equal(grown.Size, WildlifeClips.Scale(grown, kind), "a grown animal is drawn at its own size");
    WildlifeMember small = all.First(m => m.Young);
    Check.That(Math.Abs(WildlifeClips.Scale(small, kind) - (small.Size * kind.Young!.Scale)) < 1e-5, "a young one at its size on top of its age");
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(ReadKinds().Replace("\"sizeShare\": 0.07", "\"sizeShare\": 1.5")), "a variety share past the whole is refused");
});

Check.Section("hurt", () =>
{
    // A sloth stands until it is badly hurt, then runs by itself; the herd goes on standing.
    WildlifeKind kind = catalog.Kind("ground-sloth");
    WildlifeHerd herd = Herd("ground-sloth", new Vector2(0, 0), 5, "hurt");
    WildlifeMember sloth = herd.Members.First(member => !member.Young && member != herd.Leader);
    Vector2 crowd = herd.Leader.Position + new Vector2(5, 0);
    Run(herd, 10, Midnight, _ => crowd);
    Check.Equal(PressedResponse.Stand, WildlifeRules.Response(sloth, kind), "a whole sloth stands");
    int bad = (int)(sloth.Combat.MaximumHealth * kind.Senses.FleeBelowHealth) - 1;
    sloth.Combat = sloth.Combat with { Health = bad };
    Check.Equal(PressedResponse.Flee, WildlifeRules.Response(sloth, kind), "a badly hurt sloth runs");
    double before = Vector2.Distance(sloth.Position, crowd);
    Run(herd, 6, Midnight, _ => crowd);
    Check.That(Vector2.Distance(sloth.Position, crowd) > before + 8, "it runs from the player");
    Check.Equal(Wariness.Respond, herd.Leader.Stage, "and the herd goes on standing");
    Check.That(herd.Leader.Velocity == Vector2.Zero, "where it stood");

    // Its flight is its own: once it is clear the herd calms when the player goes, not held in alarm by it.
    Run(herd, 40, Midnight, _ => crowd + new Vector2(300, 0));
    Check.That(herd.Members.All(member => member.Stage is Wariness.Calm or Wariness.Regroup), "the herd calms once the player has gone");

    // A hurt glyptodon stays in its shell.
    WildlifeHerd shell = Herd("glyptodon", Vector2.Zero, 1, "shell");
    shell.Leader.Combat = shell.Leader.Combat with { Health = 1 };
    Check.Equal(PressedResponse.Hunker, WildlifeRules.Response(shell.Leader, shell.Kind), "a shell is no place to run from");
    Check.That(catalog.Kind("mammoth").Senses.FleeBelowHealth > 0 && catalog.Kind("entelodont").Senses.FleeBelowHealth > 0, "the other kinds that hold their ground break too");
});

Check.Section("young", () =>
{
    WildlifeKind kind = catalog.Kind("ground-sloth");
    WildlifeYoung young = kind.Young!;
    CreatureRig built = CreatureManifest.Parse(ReadResource("Wildlife.sloth.manifest.json"));
    WildlifeRig rig = WildlifeRig.For(kind.Mesh!, built);

    // A fair share of animals are young, and no herd is led by one.
    int total = 0, youngCount = 0;
    for (int i = 0; i < 200; i++)
    {
        WildlifeHerd sample = Herd("ground-sloth", Vector2.Zero, 6, $"count{i}");
        Check.That(!sample.Leader.Young, "a herd is led by a grown animal");
        total += sample.Members.Count - 1;
        youngCount += sample.Members.Count(member => member.Young);
    }

    Check.That(Math.Abs((youngCount / (double)total) - young.Share) < 0.08, $"young make their share of a herd ({youngCount} of {total})");
    Check.That(catalog.Kind("hipparion").Young is null, "a kind without young has none");

    WildlifeHerd herd = YoungHerd(7, "playful");
    WildlifeMember calf = herd.Members.First(member => member.Young);
    Check.That(calf.Combat.MaximumHealth < kind.MaximumHealth / 2, "the young are weaker");
    Check.That(young.Loot.Entries.All(entry => entry.Maximum <= kind.Loot.Entries.First(adult => adult.ItemId == entry.ItemId).Maximum / 2 + 1), "and yield less");

    // Among the adults they move faster, and they change direction, and stop.
    int moving = 0, still = 0, turns = 0;
    Vector2 last = Vector2.Zero;
    double farthest = 0, fastest = 0;
    Run(herd, 100, Morning, _ => null, _ =>
    {
        float speed = calf.Velocity.Length();
        if (speed == 0) still++;
        else
        {
            moving++;
            fastest = Math.Max(fastest, speed);
            if (last != Vector2.Zero && Vector2.Dot(Vector2.Normalize(last), Vector2.Normalize(calf.Velocity)) < 0.7f) turns++;
            last = calf.Velocity;
        }

        farthest = Math.Max(farthest, Vector2.Distance(calf.Position, herd.NearestAdult(calf.Position).Position));
    });
    Check.That(fastest <= young.DartSpeed + 0.01, $"a young animal keeps to its dart speed ({fastest:F2} m/s)");
    Check.That(young.DartSpeed > kind.Speeds.Walk, "the young run faster than the adults walk");
    Check.That(moving > 100 && still > 100, $"it darts and stops ({moving} moving, {still} still)");
    Check.That(turns >= 4, $"it changes direction ({turns} turns)");
    Check.That(farthest <= young.DartMetres * 2.5, $"it stays among the adults (at most {farthest:F1} m from one)");

    // Its legs turn over faster for its size, and it runs where an adult at that speed would walk.
    calf.Velocity = new Vector2((float)young.DartSpeed, 0);
    calf.Enter(Wariness.Calm);
    Check.Equal(WildlifeClipRole.Run, WildlifeClips.Want(calf, herd, rig), "at its dart speed it runs");
    Check.That(WildlifeClips.Rate(WildlifeClipRole.Run, (float)young.DartSpeed, rig, catalog.Animation, young.Scale) > 1.2f, "a small body's stride turns over faster");

    // Pressed, it keeps behind an adult from the player, while the adults stand.
    WildlifeHerd pressed = YoungHerd(7, "pressed");
    WildlifeMember small = pressed.Members.First(member => member.Young);
    Vector2 player = pressed.Leader.Position + new Vector2(6, 0);
    Run(pressed, 16, Midnight, _ => player);
    WildlifeMember shield = pressed.NearestAdult(small.Position);
    Check.Equal(Wariness.Respond, small.Stage, "the young are pressed with the herd");
    Check.That(Vector2.Distance(small.Position, player) > Vector2.Distance(shield.Position, player), "it stands behind an adult, from the player");
    Check.That(Vector2.Distance(small.Position, shield.Position) <= young.ShelterMetres * 2, "and close to it");
    Check.That(WildlifeClips.Want(small, pressed, rig) != WildlifeClipRole.Stand, "and it does not swipe");

    // The leader dies: a grown animal takes over.
    pressed.Remove(pressed.Leader);
    Check.That(!pressed.Leader.Young, "a grown animal leads when the leader falls");
    Check.Equal(Vector2.Zero, pressed.Leader.Slot, "from the herd's centre");

    WildlifeHerd YoungHerd(int count, string key)
    {
        for (int i = 0; ; i++)
        {
            WildlifeHerd candidate = Herd("ground-sloth", new Vector2(100, 100), count, $"{key}{i}");
            int small = candidate.Members.Count(member => member.Young);
            if (small >= 1 && small <= 3) return candidate;
        }
    }
});

Check.Section("region seeding", () =>
{
    // A made-up world: grassland west of x = 0, desert east of it, sea south of z = -20 km.
    MapBiome BiomeAt(double x, double z) => z < -20_000 ? MapBiome.Sea : x < 0 ? MapBiome.Grassland : MapBiome.Desert;
    WildlifeRegion region = new(-3, 2);
    RegionPopulation once = WildlifeSeeding.Seed(region, Seed, catalog, BiomeAt);
    RegionPopulation again = WildlifeSeeding.Seed(region, Seed, catalog, BiomeAt);
    Check.Equal(string.Join(";", once.Herds.Select(h => $"{h.Key}:{h.Home}:{h.Founding}")),
        string.Join(";", again.Herds.Select(h => $"{h.Key}:{h.Home}:{h.Founding}")), "the same seed seeds a region the same way");

    double size = catalog.Placement.RegionMetres;
    List<HerdRecord> west = [], east = [], sea = [], otherSeed = [];
    for (int i = 0; i < 20; i++)
    {
        for (int j = 0; j < 20; j++)
        {
            west.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(-1 - i, j), Seed, catalog, BiomeAt).Herds);
            east.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(i, j), Seed, catalog, BiomeAt).Herds);
            sea.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(i, -40 - j), Seed, catalog, BiomeAt).Herds);
            otherSeed.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(-1 - i, j), Seed + 1, catalog, BiomeAt).Herds);
        }
    }

    // A kind that takes to water lives on a river's bank, and not where there is none.
    const double RiverX = 500, RiverHalfWidth = 12;
    IReadOnlyList<RiverBank> RiverIn(double minX, double minZ, double maxX, double maxZ) =>
        minX <= RiverX && RiverX < maxX ? [.. Enumerable.Range(0, 20).Select(i => new RiverBank(RiverX, minZ + (i * 50), RiverHalfWidth, 1, 0))] : [];
    MapBiome Forest(double x, double z) => MapBiome.BorealForest;
    List<HerdRecord> byRiver = [], byNothing = [], noDrainage = [];
    for (int i = 0; i < 144; i++)
    {
        byRiver.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(0, i), Seed, catalog, Forest, null, RiverIn).Herds.Where(h => h.KindId == "giant-beaver"));
        byNothing.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(5, i), Seed, catalog, Forest, null, RiverIn).Herds.Where(h => h.KindId == "giant-beaver"));
        noDrainage.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(5, i), Seed, catalog, Forest).Herds.Where(h => h.KindId == "giant-beaver"));
    }

    double OffBank(HerdRecord herd) => Math.Abs(herd.Home.X - RiverX) - RiverHalfWidth;
    Check.That(byRiver.Count > 0, "beavers settle along a river");
    Check.That(byRiver.All(h => Math.Abs(OffBank(h) - catalog.Placement.BankMetres) < 0.01), "a beaver's home is on the bank, clear of the water");
    Check.That(byRiver.Any(h => h.Home.X > RiverX) && byRiver.Any(h => h.Home.X < RiverX), "beavers settle on either bank");
    Check.Equal(0, byNothing.Count, "no beavers settle where there is no river");
    Check.That(noDrainage.Count > 0, "a world with no drainage places them as any other kind");
    Check.That(sea.Count == 0, "nothing is seeded at sea");
    Check.That(west.All(h => catalog.Kind(h.KindId).HabitatWeight(MapBiome.Grassland) > 0), "only grassland kinds are seeded on grassland");
    Check.That(east.All(h => catalog.Kind(h.KindId).HabitatWeight(MapBiome.Desert) > 0), "only desert kinds are seeded in desert");
    Check.That(east.Any(h => h.KindId == "glyptodon") && west.Any(h => h.KindId == "ground-sloth"), "each country holds its own kinds");
    double squareKilometres = 400 * size * size / 1_000_000;
    int sloths = west.Count(h => h.KindId == "ground-sloth");
    double expected = catalog.Kind("ground-sloth").Population.HerdsPerSquareKilometre * squareKilometres;
    Check.That(Math.Abs(sloths - expected) <= 3 * Math.Sqrt(expected), $"sloth herds come at the kinds' sparse density ({sloths} for {expected:F0} expected)");
    Check.That(west.All(h => h.Founding >= catalog.Kind(h.KindId).GroupMinimum && h.Founding <= catalog.Kind(h.KindId).GroupMaximum),
        "every herd's size is within its kind's group size");
    Check.That(west.Select(h => h.Home).Except(otherSeed.Select(h => h.Home)).Any(), "another world seed seeds other herds");
    Check.That(once.Herds.All(h => WildlifeRegion.At(h.Home.X, h.Home.Y, size) == region), "a herd's home is in its own region");

    // Towns keep their sanctuary: no home within it.
    Vector2 town = new(-1500, 2500);
    const double Sanctuary = 160;
    bool Excluded(double x, double z) => Vector2.Distance(new Vector2((float)x, (float)z), town) < Sanctuary;
    WildlifeLedger ledger = new(catalog, Seed, BiomeAt, Excluded);
    List<HerdRecord> nearTown = [.. ledger.HomesWithin(town.X, town.Y, 3000)];
    Check.That(nearTown.Count > 0 && nearTown.All(h => Vector2.Distance(h.Home, town) >= Sanctuary), "no herd makes its home in a town's sanctuary");
    Check.That(ledger.HomesWithin(town.X, town.Y, 3000).First() == nearTown[0], "the ledger keeps a seeded herd, so what happens to it lasts");

    // Waking: a herd wakes where its range puts it for the time of day, the same each time.
    HerdRecord herd = nearTown[0];
    WildlifeKind kind = catalog.Kind(herd.KindId);
    Vector2 wake = WildlifeRules.WakePoint(herd, kind, Morning, Seed, catalog.Placement.HomeAttempts, null);
    Check.Equal(wake, WildlifeRules.WakePoint(herd, kind, Morning, Seed, catalog.Placement.HomeAttempts, null), "a herd wakes in the same place for the same hour");
    Check.That(Vector2.Distance(wake, herd.Home) <= kind.Population.RangeMetres, "a herd wakes inside its home range");
    Check.That(wake != WildlifeRules.WakePoint(herd, kind, Morning with { Day = Morning.Day + 1 }, Seed, catalog.Placement.HomeAttempts, null),
        "a herd wakes elsewhere on another day");
});

Check.Section("sanctuary", () =>
{
    const float Radius = 160;
    Vector2 town = Vector2.Zero;
    bool Inside(Vector2 point) => Vector2.Distance(point, town) < Radius;
    double step = catalog.Behaviour.PathCheckMetres;

    // Runs a herd with the town in force, failing if any animal ever stands in it.
    int Intrusions(WildlifeHerd herd, double seconds, WorldTime start, Func<double, Vector2?> player)
    {
        int found = 0;
        for (double t = 0; t < seconds; t += Tick)
        {
            WorldTime time = start with { DayFraction = (start.DayFraction + (t / WorldClock.DaySeconds)) % 1 };
            WildlifeRules.Decide(herd, new HerdFacts(player(t), time, Seed, null, point => !Inside(point), Inside), catalog, Tick);
            WildlifeRules.Integrate(herd, Tick);
            found += herd.Members.Count(member => Inside(member.Position));
        }

        return found;
    }

    WildlifeHerd Beside(string kindId, Vector2 at, int count, string key)
    {
        WildlifeKind kind = catalog.Kind(kindId);
        return WildlifeRules.Wake(new HerdRecord(key, kind.Id, at, count), kind, catalog, Seed, at, count, NextId, Inside);
    }

    // A herd woken right on the edge: the slots that fall inside are drawn out of it.
    WildlifeHerd edge = Beside("mammoth", new Vector2(161, 0), 10, "edge");
    Check.That(!edge.Members.Any(member => Inside(member.Position)), "no animal wakes in a sanctuary, however near the edge its herd does");
    Check.Equal(0, Intrusions(edge, 30, Morning, _ => null), "an edge herd settling into its spacing stays out");

    // Goals across the town: a straight walk there would cross it.
    Check.That(!WildlifeRules.PathClear(new Vector2(-200, 0), new Vector2(200, 0), step, Inside), "a path across a sanctuary is not clear");
    Check.That(WildlifeRules.PathClear(new Vector2(-200, 170), new Vector2(200, 170), step, Inside), "a path past it is");
    WildlifeHerd drifting = Beside("mammoth", new Vector2(-200, 0), 7, "across");
    Vector2 started = drifting.Leader.Position;
    Check.Equal(0, Intrusions(drifting, 900, Morning, _ => null), "a herd drifting through a day with a town in its range never enters it");
    Check.That(Vector2.Distance(drifting.Leader.Position, started) > 20, "and it is not stuck at the edge");

    // Fleeing straight at the town: the band goes round.
    WildlifeHerd band = Beside("hipparion", new Vector2(190, 0), 8, "flight");
    Vector2 pressing = band.Centre + new Vector2(20, 0);
    Check.Equal(0, Intrusions(band, 30, Morning, _ => pressing), "a band bolting toward a town turns along its edge");
    Check.That(Vector2.Distance(band.Centre, pressing) > 40, "and still gets away");

    // Charging a player who stands inside: it stops at the edge.
    WildlifeHerd pair = Beside("entelodont", new Vector2(170, 0), 2, "charge");
    Vector2 safe = new(Radius - 5, 0);
    Check.Equal(0, Intrusions(pair, 20, Morning, _ => safe), "a charge stops at the sanctuary's edge");

    // A town founded on an animal: it may walk out, and it does not stay caught.
    WildlifeHerd caught = Herd("hipparion", new Vector2(150, 0), 1, "caught");
    Vector2 behind = new(130, 0);
    Intrusions(caught, 30, Morning, _ => behind);
    Check.That(!Inside(caught.Leader.Position), "a pressed animal already inside can flee out");
    WildlifeHerd calm = Herd("hipparion", new Vector2(150, 0), 1, "calm-inside");
    // It keeps its day on the way, resting and all, so it is given one; once out, it stays out.
    for (int hour = 0; hour < 24 && Inside(calm.Leader.Position); hour++)
    {
        Intrusions(calm, 50, Morning with { DayFraction = ((6.5 + hour) % 24) / 24 }, _ => null);
    }
    Check.That(!Inside(calm.Leader.Position), "a calm animal already inside walks out too");
    Check.Equal(0, Intrusions(calm, 120, Morning with { DayFraction = 19.0 / 24 }, _ => null), "and it does not walk back in");
});

Check.Section("population", () =>
{
    const double RecoveryDays = 12;
    HerdRecord herd = new("ledger", "ground-sloth", Vector2.Zero, 6);
    Check.Equal(6, herd.Alive(0, RecoveryDays), "a herd starts at its founding size");
    herd.Kill(10, RecoveryDays);
    Check.Equal(5, herd.Alive(10, RecoveryDays), "a kill takes one from the herd");
    Check.Equal(5, herd.Alive(21.9, RecoveryDays), "nothing comes back before the recovery days are up");
    Check.Equal(6, herd.Alive(22, RecoveryDays), "one comes back after the recovery days");
    herd.Kill(14, RecoveryDays);
    herd.Kill(15, RecoveryDays);
    Check.Equal(3, herd.Alive(15, RecoveryDays), "three kills in a few days leave three");
    Check.Equal(4, herd.Alive(22, RecoveryDays), "recovery counts on from the first loss, one at a time");
    Check.Equal(5, herd.Alive(34, RecoveryDays), "a second comes back a recovery period later");
    Check.Equal(6, herd.Alive(1000, RecoveryDays), "a herd never recovers past its founding size");
    HerdRecord wiped = new("wiped", "glyptodon", Vector2.Zero, 2);
    wiped.Kill(1, RecoveryDays);
    wiped.Kill(1, RecoveryDays);
    wiped.Kill(1, RecoveryDays);
    Check.Equal(0, wiped.Alive(1, RecoveryDays), "a herd killed out has none, and a further kill takes nothing");
    Check.Equal(1, wiped.Alive(13, RecoveryDays), "even a herd killed out comes back, slowly");

    // A session's losses carry to the next: a new ledger over the same world holds the same herds, as hunted.
    MapBiome Grass(double x, double z) => MapBiome.Grassland;
    double size = catalog.Placement.RegionMetres;
    WildlifeLedger first = new(catalog, Seed, Grass);
    List<HerdRecord> near = [.. first.HomesWithin(0, 0, size * 4)];
    HerdRecord hunted = near[0], untouched = near[1];
    double recovery = catalog.Kind(hunted.KindId).Population.RecoveryDays;
    hunted.Kill(100, recovery);
    hunted.Kill(100.5, recovery);
    IReadOnlyList<HerdLoss> saved = first.Losses(101);
    Check.Equal(1, saved.Count, "only a herd short of animals is saved");
    Check.Equal(HerdLoss.IdOf(hunted.Key), saved[0].Herd, "the saved loss names the hunted herd");

    WildlifeLedger second = new(catalog, Seed, Grass);
    second.Restore(saved);
    Check.Equal(saved.Count, second.Losses(101).Count, "a loss in a region not yet seeded is kept for the next save");
    HerdRecord again = second.HomesWithin(0, 0, size * 4).First(herd => herd.Key == hunted.Key);
    Check.Equal(hunted.Founding - 2, again.Alive(101, recovery), "a hunted herd is still hunted next session");
    Check.Equal(hunted.Founding - 1, again.Alive(100 + recovery, recovery), "and its recovery runs on from the same day");
    Check.Equal(hunted.Founding, again.Alive(100 + (2 * recovery), recovery), "until it is whole");
    Check.Equal(untouched.Founding, second.HomesWithin(0, 0, size * 4).First(herd => herd.Key == untouched.Key).Alive(101, recovery), "other herds are untouched");
    Check.Equal(0, second.Losses(100 + (2 * recovery)).Count, "a herd that has recovered drops out of the save");

    WildlifeLedger other = new(catalog, Seed, Grass);
    other.Place(catalog.Kind("ground-sloth"), Vector2.Zero, 4).Kill(1, recovery);
    Check.Equal(0, other.Losses(1).Count, "a herd placed by live debug is in no population and is not saved");
});

return Check.Finish("wildlife");

static string ReadKinds() => ReadResource("Wildlife.kinds.json");

static string ReadResource(string name)
{
    using Stream stream = typeof(Check).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"{name} is not embedded");
    using StreamReader reader = new(stream);
    return reader.ReadToEnd();
}
