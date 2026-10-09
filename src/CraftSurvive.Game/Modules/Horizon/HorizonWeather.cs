using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Horizon;

/// <summary>
/// Weather fronts on the horizon (#9751, H2 #9780). A front's cloud is the sky's own: each front within
/// reach is a cloud region where it stands (<see cref="Sky.DayNightSky.CloudRegions"/>), in its kind
/// and darker as it strengthens, so a storm on the horizon is cloud in the same layer as the one
/// overhead. Beneath its cloud, every front within <see cref="RangeMetres"/> that the player stands
/// outside of is drawn in the backdrop where the map has it, as a translucent curtain of its kind:
/// <list type="bullet">
/// <item>rain and snow hanging from the cloud base to the ground;</item>
/// <item>the glass storm's rose-lit fall;</item>
/// <item>blown sand as a brown wall rising from the ground;</item>
/// <item>a fog bank lying on the land.</item>
/// </list>
/// The haze reaches each at its world-equivalent distance, so a far storm is a pale shape and a near
/// one a wall. A front over the player is the sky's precipitation and fog, so it has no curtain.
/// </summary>
internal sealed class HorizonWeather : IDisposable
{
    /// <summary>Fronts are drawn out to this far from the player.</summary>
    internal const double RangeMetres = 150_000;
    /// <summary>A front's curtain is drawn once the player is this share of its radius or more from its centre: outside it, or at its ragged edge.</summary>
    private const double OutsideShare = 0.9;
    /// <summary>The Engine keeps at most this many cloud regions: the nearest fronts take them.</summary>
    private const int MaximumRegions = 32;
    /// <summary>The fronts are listed again after the clock has moved this many game hours or the player this far.</summary>
    private const double RefreshHours = 0.05, RefreshMetres = 200;
    private const int Segments = 32;
    private const int StrengthSteps = 3;
    private const float Roughness = 1f;
    /// <summary>How brightly a curtain glows by itself, so it still reads against a dusk sky; the glass storm glows more.</summary>
    private const float Glow = 0.08f, ArcaneGlow = 0.6f;
    /// <summary>A front's cloud covers at least this much of the sky where it stands, more as it strengthens.</summary>
    private const float RegionCoverage = 0.55f, RegionCoverageByStrength = 0.4f;

    /// <summary>
    /// How each kind stands. Its curtain: top in metres (the cloud base for what falls), radius as a
    /// share of the front's, and opacity at the ground and at the top. Its cloud region, if it has one:
    /// the cloud's kind, thickness and darkness.
    /// </summary>
    /// <remarks>
    /// What falls also has a cloud body above the curtain, to <c>BodyMetres</c> above the cloud base,
    /// drawn as an open wall (no cap, so a near storm is a wall, not a ceiling), darkest at its base.
    /// </remarks>
    private sealed record Look(double TopMetres, float Radius, float GroundAlpha, float TopAlpha, CloudKind? Cloud, float Thickness, float Darkness,
        double BodyMetres = 0, float BodyAlpha = 0);

    private static readonly Dictionary<string, Look> Looks = new()
    {
        ["rain"] = new(Sky.DayNightSky.CloudBaseMetres, 0.75f, 0.5f, 0.75f, CloudKind.Cumulonimbus, 6_000, 0.65f, 3_500, 0.9f),
        ["snow"] = new(Sky.DayNightSky.CloudBaseMetres, 0.85f, 0.6f, 0.75f, CloudKind.Stratus, 2_500, 0.25f, 1_500, 0.85f),
        ["glass"] = new(Sky.DayNightSky.CloudBaseMetres, 0.65f, 0.5f, 0.75f, CloudKind.Cumulonimbus, 9_000, 0.35f, 5_000, 0.85f),
        ["sand"] = new(1_200, 0.9f, 0.85f, 0f, null, 0, 0),
        ["fog"] = new(350, 0.9f, 0.85f, 0f, null, 0, 0),
    };

    private readonly IEngineContext engine;
    private readonly WorldMap map;
    private readonly WeatherField field;
    private readonly Func<double> hours;
    private readonly Func<Vector3> playerWorld;
    private readonly Func<Vector2, Vector2> toLocal;
    private readonly Action<IReadOnlyList<CloudRegionRequest>> regions;
    /// <summary>The sky's daylight (0 night to 1 day): particles take no light, so a body is dimmed by it.</summary>
    internal Func<double> Daylight { get; set; } = () => 1;
    /// <summary>The least a body is lit, by night; the glass storm keeps its own glow.</summary>
    private const float NightBody = 0.12f, NightArcaneBody = 0.25f;
    private readonly Dictionary<(string Kind, int Step), (MeshResource Mesh, Appearance Look)> curtains = [];
    private readonly Dictionary<string, Material> materials = [];
    private readonly List<(WeatherFront Front, double Strength)> outside = [];
    private double listedHours = double.NegativeInfinity;
    private Vector2 listedAt;
    private int listedRevision = -1;
    private int regionCount;
    /// <summary>Moves on each time the fronts are listed again: the cloud bodies follow only then.</summary>
    private int listVersion, bodiesVersion = -1;
    private bool bodiesShown;

    /// <param name="toLocal">A world point (x, z) in the renderer's frame, where cloud regions are placed.</param>
    /// <param name="regions">Where the fronts' cloud regions go: the sky, which owns the camera view's clouds.</param>
    internal HorizonWeather(IEngineContext engine, WorldMap map, WeatherField field, Func<double> hours, Func<Vector3> playerWorld,
        Func<Vector2, Vector2> toLocal, Action<IReadOnlyList<CloudRegionRequest>> regions)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.map = map ?? throw new ArgumentNullException(nameof(map));
        this.field = field ?? throw new ArgumentNullException(nameof(field));
        this.hours = hours ?? throw new ArgumentNullException(nameof(hours));
        this.playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        this.toLocal = toLocal ?? throw new ArgumentNullException(nameof(toLocal));
        this.regions = regions ?? throw new ArgumentNullException(nameof(regions));
    }

    /// <summary>Lists the fronts again once the clock or the player has moved, and places their clouds; none while <paramref name="shown"/> is false.</summary>
    internal void Update(bool shown)
    {
        double now = hours();
        Vector3 at = playerWorld();
        Vector2 here = new(at.X, at.Z);
        bool stale = Math.Abs(now - listedHours) >= RefreshHours || Vector2.Distance(here, listedAt) >= RefreshMetres || listedRevision != field.Revision;
        if (!shown)
        {
            if (regionCount > 0 || outside.Count > 0)
            {
                outside.Clear();
                regions([]);
                regionCount = 0;
                listedHours = double.NegativeInfinity;
            }

            return;
        }

        if (!stale) return;
        listedHours = now;
        listedAt = here;
        listedRevision = field.Revision;
        outside.Clear();
        List<(WeatherFront Front, double Strength, double Distance)> near = [];
        foreach (WeatherFront front in field.Alive(here.X, here.Y, now, RangeMetres))
        {
            double strength = front.Strength(now);
            double distance = Vector2.Distance(front.Centre(now), here);
            if (strength <= 0 || distance - front.RadiusMetres > RangeMetres) continue;
            near.Add((front, strength, distance));
        }

        near.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        List<CloudRegionRequest> clouds = [];
        uint id = 1;
        foreach ((WeatherFront front, double strength, double distance) in near)
        {
            if (!Looks.TryGetValue(front.Kind.Id, out Look? look)) continue;
            if (distance >= front.RadiusMetres * OutsideShare) outside.Add((front, strength));
            if (look.Cloud is CloudKind kind && clouds.Count < MaximumRegions)
            {
                clouds.Add(new CloudRegionRequest(id++, toLocal(front.Centre(now)), (float)front.RadiusMetres,
                    RegionCoverage + (RegionCoverageByStrength * (float)strength), look.Darkness * (float)strength, Vector2.Zero,
                    kind, look.Thickness * (float)field.Scale.Lengths));
            }
        }

        regions(clouds);
        regionCount = clouds.Count;
        listVersion++;
    }

    internal int Shown => outside.Count;

    /// <summary>The curtains for the appearance snapshot, in backdrop units of <paramref name="scale"/> metres.</summary>
    internal IEnumerable<AppearanceFact> Facts(double scale)
    {
        double now = hours();
        int index = 0;
        foreach ((WeatherFront front, double strength) in outside)
        {
            if (index >= ProductIds.HorizonFrontLimit) yield break;
            Look look = Looks[front.Kind.Id];
            Vector2 centre = front.Centre(now);
            double ground = Math.Max(map.Sample(centre.X, centre.Y).Elevation, GenerationConstants.WaterLevel);
            // What falls hangs from the cloud base, wherever the ground is; what lies on the land rises from it.
            double top = look.Cloud is null ? ground + (look.TopMetres * field.Scale.Lengths) : CloudBase(ground, look);
            float radius = (float)(front.RadiusMetres * look.Radius / scale);
            float height = (float)(Math.Max(top - ground, 1) / scale);
            yield return new AppearanceFact(ProductIds.HorizonFrontBase + (ulong)index++, false, 0,
                new Transform(new Vector3((float)(centre.X / scale), (float)(ground / scale), (float)(centre.Y / scale)), Quaternion.Identity,
                    new Vector3(radius, height, radius)),
                Curtain(front.Kind, strength), true, RenderLayer.Backdrop, ShadowCasting.None);
        }
    }

    /// <summary>Where a falling kind's cloud stands: at the sky's cloud base, or above high ground.</summary>
    private static double CloudBase(double ground, Look look) => Math.Max(look.TopMetres, ground + (look.TopMetres * 0.25));

    // A falling front's cloud body: soft puffs in the backdrop (#9750 backdrop particles), filling a
    // mass above its cloud base, shaded dark beneath and pale on top.
    private const string PuffContentPath = "textures/fire-smoke.png";
    private const int PuffsPerFront = 96;
    /// <summary>
    /// The nearest falling fronts with a cloud body: the Engine's particle budget (4,096 reserved,
    /// shared with fires and every other effect) holds this many bodies of <see cref="PuffsPerFront"/>.
    /// </summary>
    private const int MaximumBodies = 8;
    /// <summary>A body's puffs are this share of its height across (at most <see cref="PuffRadiusShare"/> of its radius), growing a little as they age, so a body is a wide wall of billows, not one ball.</summary>
    private const float PuffSize = 0.7f, PuffRadiusShare = 0.2f, PuffGrowth = 1.3f;
    /// <summary>
    /// Distance hazes a body toward the air's colour: by <c>1 - exp(-distance / HazeMetres)</c> of the
    /// way, and fainter by up to <see cref="HazeFade"/>, since the backdrop's particles take no fog of their own.
    /// </summary>
    private const double HazeMetres = 45_000;
    private const float HazeFade = 0.6f;
    private static readonly Vector3 HazeColour = new(0.74f, 0.8f, 0.86f);
    /// <summary>A body's own colour, muted beside the map's markers: storm grey, snow white, the glass storm's pale rose.</summary>
    private static readonly Dictionary<string, Vector3> BodyColours = new()
    {
        ["rain"] = new(0.52f, 0.57f, 0.64f),
        ["snow"] = new(0.88f, 0.9f, 0.95f),
        ["glass"] = new(0.93f, 0.76f, 0.86f),
    };
    private const float PuffLifeSeconds = 90f, PuffFade = 0.15f;
    private const string PuffSignal = "craftsurvive.horizon.front";
    private readonly Dictionary<FrontKey, (PresentationEmitter Emitter, int Slot)> bodies = [];
    /// <summary>Each cloud body's logical id is a slot of its own, free again once its front goes.</summary>
    private readonly Stack<int> freeSlots = new(Enumerable.Range(0, MaximumBodies).Reverse());

    /// <summary>Cloud bodies the Engine refused (its particle budget spent), and why the last was.</summary>
    internal int BodiesRefused { get; private set; }

    internal string? LastRefusal { get; private set; }
    private RenderResource? puff;

    /// <summary>Places a cloud body over each falling front shown, and lifts those no longer shown.</summary>
    internal void UpdateBodies(double scale, bool shown)
    {
        if (bodiesVersion == listVersion && bodiesShown == shown) return;
        bodiesVersion = listVersion;
        bodiesShown = shown;
        HashSet<FrontKey> keep = [];
        if (shown)
        {
            double now = hours();
            foreach ((WeatherFront front, double strength) in outside)
            {
                Look look = Looks[front.Kind.Id];
                if (look.BodyMetres <= 0 || keep.Count >= MaximumBodies) continue;
                keep.Add(front.Key);
                if (bodies.TryGetValue(front.Key, out var placed))
                {
                    engine.Presentation.UpdateEmitter(placed.Emitter, Body(front, strength, look, now, scale, placed.Slot));
                }
                else if (freeSlots.TryPop(out int slot))
                {
                    try
                    {
                        bodies[front.Key] = (engine.Presentation.CreateEmitter(Body(front, strength, look, now, scale, slot)), slot);
                    }
                    catch (EngineCallException refused)
                    {
                        // The particle budget is shared: when it is spent, this front keeps its curtain and cloud region only.
                        freeSlots.Push(slot);
                        keep.Remove(front.Key);
                        BodiesRefused++;
                        LastRefusal = refused.Message;
                    }
                }
            }
        }

        foreach (FrontKey gone in bodies.Keys.Where(key => !keep.Contains(key)).ToList())
        {
            bodies[gone].Emitter.Dispose();
            freeSlots.Push(bodies[gone].Slot);
            bodies.Remove(gone);
        }
    }

    private PresentationParticleDescriptor Body(WeatherFront front, double strength, Look look, double now, double scale, int slot)
    {
        puff ??= engine.Graphics.OpenResource(new RenderResourceRequest(PuffContentPath, TextureFilter.Linear, TextureWrap.Clamp)).Handle;
        Vector2 centre = front.Centre(now);
        double ground = Math.Max(map.Sample(centre.X, centre.Y).Elevation, GenerationConstants.WaterLevel);
        float radius = (float)(front.RadiusMetres * look.Radius / scale);
        float body = (float)(look.BodyMetres * field.Scale.Lengths / scale);
        Vector3 at = new((float)(centre.X / scale), (float)((CloudBase(ground, look) / scale) + (body * 0.5f)), (float)(centre.Y / scale));
        // Puffs leave the middle and drift outward over their life, filling the mass about it.
        Vector3 spread = new(radius / PuffLifeSeconds, body * 0.5f / PuffLifeSeconds, radius / PuffLifeSeconds);
        double distance = Vector2.Distance(centre, new Vector2(playerWorld().X, playerWorld().Z));
        float haze = (float)(1 - Math.Exp(-distance / HazeMetres));
        Vector3 own = BodyColours.GetValueOrDefault(front.Kind.Id, HazeColour);
        Vector3 top = Vector3.Lerp(own, HazeColour, haze);

        float alpha = look.BodyAlpha * (0.5f + (0.5f * (float)strength)) * (1f - (HazeFade * haze));
        // One colour: a puff's age says nothing of its height, so the body is not shaded by it.
        float night = front.Kind.Arcane ? NightArcaneBody : NightBody;
        top *= night + ((1f - night) * (float)Math.Clamp(Daylight(), 0, 1));
        Color pale = new(top.X, top.Y, top.Z, alpha);
        float puffSize = Math.Min(body * PuffSize, radius * PuffRadiusShare);
        return new PresentationParticleDescriptor
        {
            LogicalId = ProductIds.HorizonFrontBase + ProductIds.HorizonFrontLimit + (ulong)slot,
            SignalId = PuffSignal,
            Visible = true,
            Seed = (ulong)(front.Key.GetHashCode() & 0x7FFF_FFFF),
            Anchor = new PresentationAnchor { Kind = PresentationAnchorKind.World, Position = at },
            Visual = PresentationParticleVisual.Billboard,
            Sprite = puff,
            SpriteFrameCount = 1,
            SizeMode = PresentationParticleSizeMode.World,
            Blend = PresentationParticleBlendMode.Alpha,
            RatePerSecond = PuffsPerFront / PuffLifeSeconds,
            BurstCount = PuffsPerFront,
            MaxParticles = PuffsPerFront,
            LifetimeMinSeconds = PuffLifeSeconds * 0.8f,
            LifetimeMaxSeconds = PuffLifeSeconds,
            VelocityMin = -spread,
            VelocityMax = spread,
            SizeCurve = new PresentationParticleScalarKey[] { new(0f, puffSize), new(1f, puffSize * PuffGrowth) },
            ColorCurve = new PresentationParticleColorKey[]
            {
                new(0f, pale with { A = 0f }), new(PuffFade, pale), new(1f - PuffFade, pale), new(1f, pale with { A = 0f }),
            },
            Backdrop = true,
        };
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture, $"fronts={outside.Count} clouds={regionCount} bodies={bodies.Count} bodiesRefused={BodiesRefused}")
        + (outside.Count == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
            $" nearest={outside[0].Front.Kind.Id}@{Vector2.Distance(outside[0].Front.Centre(hours()), new Vector2(playerWorld().X, playerWorld().Z)) / 1000:F1}km"));

    public void Dispose()
    {
        foreach ((PresentationEmitter emitter, int _) in bodies.Values) emitter.Dispose();
        bodies.Clear();
        puff?.Dispose();
        puff = null;
        foreach ((MeshResource mesh, Appearance look) in curtains.Values)
        {
            look.Dispose();
            mesh.Dispose();
        }

        curtains.Clear();
        foreach (Material material in materials.Values) material.Dispose();
        materials.Clear();
    }

    /// <summary>A kind's curtain at a strength step: an open cylinder of unit radius and height, its opacity from ground to top by kind, fainter when weak.</summary>
    private Appearance Curtain(WeatherKind kind, double strength)
    {
        int step = Math.Clamp((int)Math.Ceiling(strength * StrengthSteps), 1, StrengthSteps);
        if (curtains.TryGetValue((kind.Id, step), out var built)) return built.Look;
        Look look = Looks[kind.Id];
        Color colour = MapPalette.Weather(kind.Id);
        float weight = (float)step / StrengthSteps;
        // Rings up the curtain, then (for what falls) up the cloud body: (height share, opacity, shade).
        // What falls hangs from its cloud, fading into it at the top; what lies on the land fades upward.
        List<(float Height, float Alpha, float Shade)> rings =
        [
            (0f, look.GroundAlpha * 0.6f, 1f),
            (0.15f, look.GroundAlpha, 1f),
            (0.6f, (look.GroundAlpha + look.TopAlpha) / 2, 1f - (look.Darkness * 0.5f)),
            (1f, look.Cloud is null ? 0f : look.TopAlpha * 0.1f, 1f - look.Darkness),
        ];

        List<Vector3> positions = [];
        List<Vector3> normals = [];
        List<Color> colours = [];
        List<uint> indices = [];
        foreach ((float height, float alpha, float shade) in rings)
        {
            for (int segment = 0; segment < Segments; segment++)
            {
                float angle = segment * MathF.Tau / Segments;
                Vector2 way = new(MathF.Cos(angle), MathF.Sin(angle));
                positions.Add(new Vector3(way.X, height, way.Y));
                normals.Add(new Vector3(way.X, 0, way.Y));
                colours.Add(new Color(colour.R * shade, colour.G * shade, colour.B * shade, alpha * weight));
            }
        }

        for (int ring = 0; ring < rings.Count - 1; ring++)
        {
            uint lower = (uint)(ring * Segments), upper = lower + Segments;
            for (int segment = 0; segment < Segments; segment++)
            {
                uint a = (uint)segment, b = (uint)((segment + 1) % Segments);
                indices.AddRange([lower + a, upper + b, lower + b, lower + a, upper + a, upper + b]);
            }
        }

        MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(),
            Enumerable.Repeat(Vector2.Zero, positions.Count).ToArray(), colours.ToArray(), indices.ToArray(),
            new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, Material(kind, colour)) }));
        Appearance appearance = engine.Graphics.CreateMeshAppearance(mesh);
        curtains[(kind.Id, step)] = (mesh, appearance);
        return appearance;
    }

    /// <summary>A kind's material: lit by the sun, double-sided and blended, with a faint glow of its own (the glass storm's is strong).</summary>
    private Material Material(WeatherKind kind, Color colour)
    {
        if (materials.TryGetValue(kind.Id, out Material? built)) return built;
        Color white = new(1, 1, 1, 1);
        Material material = engine.Graphics.CreateMaterial(new MaterialRequest(white, default(RenderResourceReference), Roughness,
            white, new Vector3(colour.R, colour.G, colour.B), kind.Arcane ? ArcaneGlow : Glow, true) with { AlphaMode = MaterialAlphaMode.Blend });
        materials[kind.Id] = material;
        return material;
    }
}
