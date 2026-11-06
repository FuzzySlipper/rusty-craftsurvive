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
/// Birds in the sky (#10630): drawn, not simulated. They have no pathfinding, collision, herd record or
/// save. Every so often, by the ground under the player, the hour and the season, a flight sets out
/// from beyond sight, crosses the sky on a line that misses the player, and leaves. See
/// <see cref="FlightRules"/>. An instance is disposed before the snapshot that drops its fact, and its
/// appearance after that snapshot, as the Engine requires.
/// </summary>
internal sealed class BirdModule(IEngineContext engine, ProductContent content, TerrainWorld terrain, PlayerController player, WorldFrame frame, Func<WorldTime> clock) : IProductModule
{
    private const float HalfTurn = MathF.PI;
    private const double HoursPerDay = 24;
    private const float FullWeight = 1;
    private const float PlaybackRate = 1;
    private const float FadeSeconds = 0.6f;
    private const float SmallestSize = 0.01f;

    private readonly List<Flying> flights = [];
    private readonly List<Appearance> retiring = [];
    private readonly Dictionary<string, RenderResource> meshes = [];
    private readonly Random draws = new(terrain.Recipe.Contract.Seed.GetHashCode());
    private BirdCatalog? catalog;
    private CyclingSlots? slots;
    private bool started;
    private double nextFlightIn;
    private AppearanceFact[] facts = [];
    private (long Updates, double Spent, double Worst, double Plan) cost;

    private sealed class Body(int slot, ulong objectId, Appearance appearance, AnimationInstance instance)
    {
        internal int Slot { get; } = slot;
        internal ulong ObjectId { get; } = objectId;
        internal Appearance Appearance { get; } = appearance;
        internal AnimationInstance Instance { get; } = instance;
        internal bool? Flapping { get; set; }
    }

    private sealed record Flying(Flight Flight, Body[] Bodies);

    private BirdCatalog Catalog => catalog ?? throw new InvalidOperationException("birds have not started");

    internal IReadOnlyList<AppearanceFact> AppearanceFacts => facts;

    public void Start()
    {
        if (started) return;
        catalog ??= BirdCatalog.Parse(content.ReadText(BirdCatalog.ContentPath));
        slots ??= new CyclingSlots(Math.Min(Catalog.MostBirds, (int)ProductIds.BirdAppearanceLimit));
        foreach (BirdKind kind in Catalog.Kinds) Open(kind);
        nextFlightIn = Catalog.EverySeconds.At(draws.NextDouble());
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
        nextFlightIn -= seconds;
        if (nextFlightIn <= 0)
        {
            nextFlightIn = Catalog.EverySeconds.At(draws.NextDouble());
            if (flights.Count < Catalog.MostFlights) Send(here);
        }

        List<AppearanceFact> next = [];
        foreach (Flying flying in flights.ToArray())
        {
            FlightRules.Advance(flying.Flight, Catalog, seconds, GroundAt);
            if (FlightRules.Gone(flying.Flight, here, Catalog.ReachMetres))
            {
                Land(flying);
                continue;
            }

            Pose(flying, here, next);
        }

        facts = [.. next];
        double spent = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        cost = (cost.Updates + 1, cost.Spent + spent, Math.Max(cost.Worst, spent), cost.Plan);
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

    /// <summary>Test aid: sends a flight of a kind now, wherever the player is, and how far from the start of it the birds are.</summary>
    /// <param name="overhead">Sends it past the player at its nearest, already level with them, for looking at.</param>
    internal string SendNow(string kindId, bool overhead)
    {
        if (Catalog.Kinds.FirstOrDefault(kind => kind.Id == kindId) is not { } kind) return $"no bird '{kindId}'; birds: {string.Join(",", Catalog.Kinds.Select(k => k.Id))}";
        if (!Fits(kind)) return $"no room in the sky for a flock of {kind.Id}; {Readout()}";
        Vector3 at = player.WorldPosition;
        Flying flying = Launch(kind, new Vector2(at.X, at.Z));
        if (overhead) flying.Flight.Age = flying.Flight.Seconds / 2;
        return Readout();
    }

    /// <summary>Test aid: every flight goes now.</summary>
    internal string ClearNow()
    {
        Clear();
        return Readout();
    }

    /// <summary>The flights in the air, and what the updates have cost since the last readout (which it resets).</summary>
    internal string Readout()
    {
        Vector3 at = player.WorldPosition;
        Vector2 here = new(at.X, at.Z);
        double mean = cost.Updates > 0 ? cost.Spent / cost.Updates : 0;
        string text = string.Create(CultureInfo.InvariantCulture,
            $"flights={flights.Count} birds={flights.Sum(flying => flying.Bodies.Length)} nextIn={nextFlightIn:F0} updates={cost.Updates} meanMs={mean:F3} worstMs={cost.Worst:F3} planMs={cost.Plan:F2}");
        cost = default;
        return text + string.Concat(flights.Select(flying =>
        {
            Flight flight = flying.Flight;
            BirdPose pose = FlightRules.At(flight, flight.Slots[0], flight.Age);
            Vector2 end = FlightRules.End(flight);
            return string.Create(CultureInfo.InvariantCulture,
                $" | {flight.Kind.Id} n={flight.Slots.Count} age={flight.Age:F0}/{flight.Seconds:F0} at={pose.Position.X:F0},{pose.Position.Y:F0},{pose.Position.Z:F0} d={Vector2.Distance(pose.Ground, here):F0} size={FlightRules.Size(pose, here, Catalog):F2} climbed={flight.Climbed:F0} ends={end.X:F0},{end.Y:F0} passes={FlightRules.Passes(flight, here):F0} radius={flight.Radius:F0} flapping={flying.Bodies.Count(body => body.Flapping == true)}");
        }));
    }

    private void Send(Vector2 here)
    {
        MapBiome biome = WorldMap.Biome(terrain.Recipe.Map.Sample(here.X, here.Y));
        WorldTime now = clock();
        double hour = now.DayFraction * HoursPerDay;
        Season season = Seasons.At((now.Day * HoursPerDay) + hour);
        BirdKind[] flying = [.. Catalog.Kinds.Where(kind => kind.FliesAt(biome, hour, season))];
        if (FlightRules.Choose(flying, biome, draws.NextDouble()) is { } kind && Fits(kind)) Launch(kind, here);
    }

    /// <summary>Whether a flock of this kind's largest size has room in the sky.</summary>
    private bool Fits(BirdKind kind) => flights.Sum(flying => flying.Bodies.Length) + kind.Flock.Most <= Catalog.MostBirds;

    private Flying Launch(BirdKind kind, Vector2 here)
    {
        long began = Stopwatch.GetTimestamp();
        Flight flight = FlightRules.Plan(kind, Catalog, here, draws.NextDouble, GroundAt);
        Flying flying = new(flight, [.. flight.Slots.Select(_ => Create(kind))]);
        flights.Add(flying);
        cost = cost with { Plan = Math.Max(cost.Plan, Stopwatch.GetElapsedTime(began).TotalMilliseconds) };
        return flying;
    }

    private double GroundAt(double x, double z) => terrain.Recipe.ContinuousHeightAt((long)Math.Floor(x), (long)Math.Floor(z));

    private Body Create(BirdKind kind)
    {
        int slot = slots!.Take();
        ulong objectId = ProductIds.BirdAppearanceBase + (ulong)slot;
        Appearance appearance = engine.Animation.CreateAnimatedMeshAppearance(new(Open(kind)));
        return new Body(slot, objectId, appearance, engine.Animation.CreateInstance(new(appearance, objectId)));
    }

    private void Pose(Flying flying, Vector2 here, List<AppearanceFact> into)
    {
        Flight flight = flying.Flight;
        for (int index = 0; index < flying.Bodies.Length; index++)
        {
            Body body = flying.Bodies[index];
            FlightSlot slot = flight.Slots[index];
            bool flapping = FlightRules.Flapping(flight, slot, flight.Age);
            if (flapping != body.Flapping)
            {
                string clip = flapping ? flight.Kind.Mesh.Flap : flight.Kind.Mesh.Glide;
                engine.Animation.SetPlayback(new(body.Instance, AnimationPlaybackKind.Play, clip, AnimationLoopMode.Repeat, PlaybackRate, FullWeight, body.Flapping is null, FadeSeconds, true, 0));
                body.Flapping = flapping;
            }

            BirdPose pose = FlightRules.At(flight, slot, flight.Age);
            float size = FlightRules.Size(pose, here, Catalog);
            // The mesh faces +Z; yaw 0 faces north (-Z) and a quarter turn east (+X). A positive turn about its own Z lifts its left wing.
            Quaternion lean = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, pose.Bank);
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, HalfTurn - pose.Yaw);
            Transform placement = new(frame.ToLocal(pose.Position), Quaternion.Concatenate(lean, turn), new Vector3(flight.Kind.Mesh.Scale * MathF.Max(size, SmallestSize)));
            into.Add(new AppearanceFact(body.ObjectId, false, 0, placement, body.Appearance, Visible: size > 0, RenderLayer.Scene, ShadowCasting.None));
        }
    }

    private void Land(Flying flying)
    {
        foreach (Body body in flying.Bodies)
        {
            body.Instance.Dispose();
            retiring.Add(body.Appearance);
            slots!.Release(body.Slot);
        }

        flights.Remove(flying);
    }

    private void Clear()
    {
        foreach (Flying flying in flights.ToArray()) Land(flying);
        facts = [];
    }

    private RenderResource Open(BirdKind kind)
    {
        if (meshes.TryGetValue(kind.Mesh.File, out RenderResource? mesh)) return mesh;
        using ContentReference reference = engine.Content.OpenReference(new(kind.Mesh.File));
        return meshes[kind.Mesh.File] = engine.Animation.OpenAnimatedMeshFromContent(new(reference));
    }
}
