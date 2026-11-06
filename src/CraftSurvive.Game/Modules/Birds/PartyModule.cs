using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.Wildlife;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Birds;

/// <summary>
/// Small birds on the ground (#10639): drawn, not simulated, and kept nowhere. A party appears out of the
/// player's view, feeds, and goes up when the player comes near, swapping each bird's folded body for its
/// spread one. Nothing lands in view: a party that has stayed its time goes while no bird of it can be seen.
/// See <see cref="PartyRules"/>. An instance is disposed before the snapshot that drops its fact, and its
/// appearance after that snapshot, as the Engine requires.
/// </summary>
internal sealed class PartyModule(IEngineContext engine, ProductContent content, TerrainWorld terrain, PlayerController player, WorldFrame frame, Func<WorldTime> clock) : IProductModule
{
    private const float HalfTurn = MathF.PI;
    private const double HoursPerDay = 24;
    private const float FullWeight = 1;
    private const float PlaybackRate = 1;
    private const float FadeSeconds = 0.15f;
    private const float SmallestSize = 0.01f;
    private const double GroundHeadroom = 0.02;
    private const double GladeCanopyLeast = 0.3;

    private readonly List<Party> parties = [];
    private readonly List<Appearance> retiring = [];
    private readonly Dictionary<string, RenderResource> meshes = [];
    private readonly Random draws = new(terrain.Recipe.Contract.Seed.GetHashCode() + 1);
    private PartyCatalog? catalog;
    private CyclingSlots? slots;
    private bool started;
    private double nextPartyIn;
    private AppearanceFact[] facts = [];
    private (long Updates, double Spent, double Worst) cost;

    private sealed class Body(int slot, ulong objectId, PartyBird bird)
    {
        internal int Slot { get; } = slot;
        internal ulong ObjectId { get; } = objectId;
        internal PartyBird Bird { get; } = bird;
        internal Appearance? Appearance { get; set; }
        internal AnimationInstance? Instance { get; set; }
        internal string? Clip { get; set; }
        internal bool Gone { get; set; }
        internal float FromGround { get; set; }
        internal float ToGround { get; set; }
    }

    private sealed class Party(PartyKind kind, Vector2 spot, double stay, Body[] bodies)
    {
        internal PartyKind Kind { get; } = kind;
        internal Vector2 Spot { get; } = spot;
        internal Body[] Bodies { get; } = bodies;
        internal double StayLeft { get; set; } = stay;
    }

    private PartyCatalog Catalog => catalog ?? throw new InvalidOperationException("parties have not started");

    internal IReadOnlyList<AppearanceFact> AppearanceFacts => facts;

    public void Start()
    {
        if (started) return;
        catalog ??= PartyCatalog.Parse(content.ReadText(BirdCatalog.ContentPath));
        slots ??= new CyclingSlots(Math.Min(Catalog.MostBirds, (int)ProductIds.PartyAppearanceLimit));
        // Opened now, so that a party's first appearance and first flight cost no load in the middle of play.
        foreach (PartyKind kind in Catalog.Kinds)
        {
            Open(kind.Mesh.Ground);
            Open(kind.Mesh.Air);
        }

        nextPartyIn = Catalog.EverySeconds.At(draws.NextDouble());
        started = true;
    }

    public void Update(ProductStep time)
    {
        if (!started) return;
        if (player.InSeparateSpace)
        {
            Clear();
            return;
        }

        long began = Stopwatch.GetTimestamp();
        double seconds = time.ElapsedSeconds;
        Vector3 at = player.WorldPosition;
        Vector2 here = new(at.X, at.Z);
        nextPartyIn -= seconds;
        if (nextPartyIn <= 0)
        {
            nextPartyIn = Catalog.EverySeconds.At(draws.NextDouble());
            if (parties.Count < Catalog.MostParties) Send(here);
        }

        List<AppearanceFact> next = [];
        foreach (Party party in parties.ToArray())
        {
            party.StayLeft -= seconds;
            if (PartyRules.Spooked(party.Bodies.Where(body => !body.Gone).Select(body => body.Bird), party.Kind, here))
            {
                PartyRules.Flush(party.Bodies.Select(body => body.Bird), party.Kind, here, draws.NextDouble);
            }

            foreach (Body body in party.Bodies)
            {
                if (!body.Gone) Step(party, body, seconds, here);
            }

            if (party.Bodies.All(body => body.Gone) || Leaves(party, here))
            {
                Land(party);
                continue;
            }

            foreach (Body body in party.Bodies)
            {
                if (!body.Gone) Pose(party, body, here, next);
            }
        }

        facts = [.. next];
        double spent = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        cost = (cost.Updates + 1, cost.Spent + spent, Math.Max(cost.Worst, spent));
    }

    public void Restart() => Clear();

    public void Dispose()
    {
        Clear();
        AfterAppearanceSnapshot();
        foreach (RenderResource mesh in meshes.Values) mesh.Dispose();
        meshes.Clear();
        started = false;
    }

    /// <summary>Called by the product root once it has published a snapshot of <see cref="AppearanceFacts"/>.</summary>
    internal void AfterAppearanceSnapshot()
    {
        foreach (Appearance appearance in retiring) appearance.Dispose();
        retiring.Clear();
    }

    /// <summary>Test aid: puts a party of a kind straight ahead of the player at this distance, wherever and whenever, for looking at.</summary>
    internal string SendNow(string kindId, double metres)
    {
        if (Catalog.Kinds.FirstOrDefault(kind => kind.Id == kindId) is not { } kind) return $"no party '{kindId}'; parties: {string.Join(",", Catalog.Kinds.Select(k => k.Id))}";
        if (parties.Count >= Catalog.MostParties) return $"the ground is full; {Readout()}";
        Vector3 at = player.WorldPosition;
        Vector2 ahead = Vector2.Normalize(new Vector2(player.AimForward.X, player.AimForward.Z));
        Launch(kind, new Vector2(at.X, at.Z) + (ahead * (float)metres));
        return Readout();
    }

    /// <summary>Test aid: the nearest ground with trees about it that a party of the first kind would take, on a grid around the player.</summary>
    internal string FindGlade(double radiusMetres, double stepMetres)
    {
        Vector3 at = player.WorldPosition;
        PartyKind kind = Catalog.Kinds[0];
        (double Metres, double X, double Z, double Canopy)? best = null;
        for (double x = -radiusMetres; x <= radiusMetres; x += stepMetres)
        {
            for (double z = -radiusMetres; z <= radiusMetres; z += stepMetres)
            {
                double metres = Math.Sqrt((x * x) + (z * z));
                if (best is { } held && metres >= held.Metres) continue;
                double wx = at.X + x, wz = at.Z + z;
                double canopy = terrain.Recipe.Forest.Canopy(wx, wz, terrain.Recipe.Map.Sample(wx, wz));
                if (canopy >= GladeCanopyLeast && canopy <= kind.MostCanopy && Suits(kind, new Vector2((float)wx, (float)wz))) best = (metres, wx, wz, canopy);
            }
        }

        return best is { } found ? string.Create(CultureInfo.InvariantCulture, $"glade at {found.X:F0} {found.Z:F0} canopy={found.Canopy:F2} d={found.Metres:F0}") : "no glade near";
    }

    /// <summary>Test aid: every party goes now.</summary>
    internal string ClearNow()
    {
        Clear();
        return Readout();
    }

    /// <summary>The parties on the ground, and what the updates have cost since the last readout (which it resets).</summary>
    internal string Readout()
    {
        Vector3 at = player.WorldPosition;
        Vector2 here = new(at.X, at.Z);
        double mean = cost.Updates > 0 ? cost.Spent / cost.Updates : 0;
        string text = string.Create(CultureInfo.InvariantCulture,
            $"parties={parties.Count} birds={parties.Sum(party => party.Bodies.Count(body => !body.Gone))} nextIn={nextPartyIn:F0} updates={cost.Updates} meanMs={mean:F3} worstMs={cost.Worst:F3}");
        cost = default;
        return text + string.Concat(parties.Select(party =>
        {
            Body[] left = [.. party.Bodies.Where(body => !body.Gone)];
            return string.Create(CultureInfo.InvariantCulture,
                $" | {party.Kind.Id} at={party.Spot.X:F0},{party.Spot.Y:F0} d={Vector2.Distance(party.Spot, here):F0} n={left.Length} flying={left.Count(body => body.Bird.Flying is not null)} stay={party.StayLeft:F0} seen={left.Count(body => Seen(Point(body)))}");
        }));
    }

    private void Send(Vector2 here)
    {
        MapBiome biome = WorldMap.Biome(terrain.Recipe.Map.Sample(here.X, here.Y));
        WorldTime now = clock();
        double hour = now.DayFraction * HoursPerDay;
        Season season = Seasons.At((now.Day * HoursPerDay) + hour);
        PartyKind[] feeding = [.. Catalog.Kinds.Where(kind => kind.FeedsAt(biome, hour, season))];
        if (PartyRules.Choose(feeding, biome, draws.NextDouble()) is not { } kind) return;
        Vector3 forward = player.AimForward;
        Vector2? spot = PartyRules.Place(Catalog, here, player.WorldEyePosition, forward, player.FieldOfViewDegrees, draws.NextDouble, point => GroundOf(point), point => Suits(kind, point));
        if (spot is { } found) Launch(kind, found);
    }

    /// <summary>Open dry ground: a glade under a thin canopy.</summary>
    private bool Suits(PartyKind kind, Vector2 point)
    {
        long x = (long)Math.Floor(point.X), z = (long)Math.Floor(point.Y);
        if (terrain.Recipe.WaterTopAt(x, z) > terrain.Recipe.SurfaceAt(x, z)) return false;
        return terrain.Recipe.Forest.Canopy(point.X, point.Y, terrain.Recipe.Map.Sample(point.X, point.Y)) <= kind.MostCanopy;
    }

    private void Launch(PartyKind kind, Vector2 spot)
    {
        Body[] bodies = [.. PartyRules.Scatter(kind, spot, draws.NextDouble).Select(bird =>
        {
            int slot = slots!.Take();
            Body body = new(slot, ProductIds.PartyAppearanceBase + (ulong)slot, bird);
            body.FromGround = body.ToGround = GroundOf(bird.At);
            Dress(body, kind.Mesh.Ground);
            return body;
        })];
        parties.Add(new Party(kind, spot, Catalog.StaySeconds.At(draws.NextDouble()), bodies));
    }

    private void Dress(Body body, string file)
    {
        Undress(body);
        body.Appearance = engine.Animation.CreateAnimatedMeshAppearance(new(Open(file)));
        body.Instance = engine.Animation.CreateInstance(new(body.Appearance, body.ObjectId));
        body.Clip = null;
    }

    private void Undress(Body body)
    {
        body.Instance?.Dispose();
        if (body.Appearance is not null) retiring.Add(body.Appearance);
        body.Instance = null;
        body.Appearance = null;
    }

    private void Step(Party party, Body body, double seconds, Vector2 here)
    {
        PartyBird bird = body.Bird;
        if (bird.Flying is { } flying)
        {
            bird.Flying = flying + seconds;
            if (PartyRules.Size(Vector2.Distance(PartyRules.Burst(bird, party.Kind).Ground, here), Catalog) <= 0) Leave(body);
            return;
        }

        if (bird.FlushIn is { } wait)
        {
            bird.FlushIn = wait - seconds;
            if (bird.FlushIn > 0) return;
            bird.Launch = new Vector3(bird.At.X, body.ToGround, bird.At.Y);
            bird.Flying = 0;
            Dress(body, party.Kind.Mesh.Air);
            return;
        }

        bool wasHopping = bird.HopLeft > 0;
        PartyRules.Feed(bird, party.Kind, party.Spot, seconds, draws.NextDouble);
        if (!wasHopping && bird.HopLeft > 0)
        {
            body.FromGround = GroundOf(bird.HopFrom);
            body.ToGround = GroundOf(bird.HopTo);
        }
    }

    private void Leave(Body body)
    {
        Undress(body);
        slots!.Release(body.Slot);
        body.Gone = true;
    }

    /// <summary>A party goes once it has stayed its time or been left behind, but never while a bird of it could be seen on the ground.</summary>
    private bool Leaves(Party party, Vector2 here)
    {
        if (party.StayLeft > 0 && Vector2.Distance(party.Spot, here) < Catalog.GoneMetres) return false;
        return !party.Bodies.Any(body => !body.Gone && Seen(Point(body)));
    }

    private bool Seen(Vector3 point) => PartyRules.InView(point, player.WorldEyePosition, player.AimForward, player.FieldOfViewDegrees, Catalog.ViewMarginDegrees);

    private static Vector3 Point(Body body) =>
        body.Bird.Flying is null ? new Vector3(body.Bird.At.X, body.ToGround, body.Bird.At.Y) : body.Bird.Launch;

    private void Pose(Party party, Body body, Vector2 here, List<AppearanceFact> into)
    {
        PartyBird bird = body.Bird;
        PartyMesh mesh = party.Kind.Mesh;
        Vector3 position;
        float facing, scale;
        bool visible = true;
        string clip;
        if (bird.Flying is not null)
        {
            BirdPose pose = PartyRules.Burst(bird, party.Kind);
            float size = PartyRules.Size(Vector2.Distance(pose.Ground, here), Catalog);
            position = pose.Position;
            facing = pose.Yaw;
            scale = mesh.AirScale * MathF.Max(size, SmallestSize);
            visible = size > 0;
            clip = mesh.Flap;
        }
        else
        {
            float share = bird.HopLeft > 0 ? (float)(1 - (bird.HopLeft / party.Kind.Gait.HopSeconds)) : 1;
            float ground = float.Lerp(body.FromGround, body.ToGround, share);
            position = new Vector3(bird.At.X, ground + (float)(PartyRules.HopHeight(bird, party.Kind) + GroundHeadroom), bird.At.Y);
            facing = (float)bird.Facing;
            scale = mesh.GroundScale;
            clip = bird.Clip switch { PartyClip.Hop => mesh.Hop, PartyClip.Peck => mesh.Peck, _ => mesh.Idle };
        }

        if (clip != body.Clip)
        {
            engine.Animation.SetPlayback(new(body.Instance!, AnimationPlaybackKind.Play, clip, AnimationLoopMode.Repeat, PlaybackRate, FullWeight, body.Clip is null, FadeSeconds, true, 0));
            body.Clip = clip;
        }

        // The mesh faces +Z; yaw 0 faces north (-Z) and a quarter turn east (+X).
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, HalfTurn - facing);
        Transform placement = new(frame.ToLocal(position), turn, new Vector3(scale));
        into.Add(new AppearanceFact(body.ObjectId, false, 0, placement, body.Appearance!, Visible: visible, RenderLayer.Scene, ShadowCasting.Cast));
    }

    private void Land(Party party)
    {
        foreach (Body body in party.Bodies)
        {
            if (!body.Gone) Leave(body);
        }

        parties.Remove(party);
    }

    private void Clear()
    {
        foreach (Party party in parties.ToArray()) Land(party);
        facts = [];
    }

    /// <summary>The ground at a point: the Engine's standing surface on resident ground, else the drawn far field's height.</summary>
    private float GroundOf(Vector2 point)
    {
        long x = (long)Math.Floor(point.X), z = (long)Math.Floor(point.Y);
        long surface = terrain.Recipe.SurfaceAt(x, z);
        return terrain.IsResident(new Terrain.VoxelAddress(x, surface, z)) ? terrain.GroundAt(point.X, point.Y)
            : (float)(WildlifeClips.DrawnGround(point.X, point.Y, terrain.Recipe.ContinuousHeightAt) + FarGround.GroundTopFace);
    }

    private RenderResource Open(string file)
    {
        if (meshes.TryGetValue(file, out RenderResource? mesh)) return mesh;
        using ContentReference reference = engine.Content.OpenReference(new(file));
        return meshes[file] = engine.Animation.OpenAnimatedMeshFromContent(new(reference));
    }
}
