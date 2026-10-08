using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// One weather front as the map draws it (#9739): its kind, centre and radius in world metres, its
/// strength now (0..1), and where its centre will be over the coming hours.
/// </summary>
internal readonly record struct WeatherMarker(string Kind, Vector2 Centre, float RadiusMetres, float Strength, Vector2[] Track);

/// <summary>
/// Weather fronts on the faceted map (#9739, Den <c>design/weather-and-environment</c>): each a soft,
/// translucent disc at cloud height over the relief, sized to the front and denser as it strengthens,
/// with a trail of dots where it is heading, so a player can see a storm coming and judge its path.
/// </summary>
internal sealed class MapWeather : IDisposable
{
    /// <summary>A front is a low dome: this tall for its radius, full opacity to <see cref="CoreShare"/> of the radius, fading to nothing at the edge.</summary>
    private const float DomeHeight = 0.18f, CoreShare = 0.6f;
    private static readonly float[] Rings = [0.3f, CoreShare, 0.8f, 1f];
    private const int Segments = 48;
    /// <summary>Opacity at the core of a front at full strength; weaker fronts are drawn fainter, in steps.</summary>
    private const float CoreAlpha = 0.7f;
    private const int StrengthSteps = 3;
    private const float DiscRoughness = 1f;
    /// <summary>How brightly a front glows by itself, so it reads at night as well as by day.</summary>
    private const float NightGlow = 0.25f;
    /// <summary>Clouds float this far above the ground under their centre, in world metres.</summary>
    private const double CloudLiftMetres = 300;
    private const float TrackScalePerDistance = 0.005f, MinimumTrackScale = 0.2f;

    private readonly IEngineContext engine;
    private readonly Func<double, double, Vector3> surface;
    private readonly double cellMetres, metresPerUnit;
    private readonly Dictionary<string, Material> discMaterials = [];
    private readonly Dictionary<(string Kind, int Step), (MeshResource Mesh, Appearance Look)> discs = [];
    private readonly Dictionary<string, Appearance> dots = [];
    private IReadOnlyList<WeatherMarker> fronts = [];

    /// <param name="surface">The drawn relief at a world point, in map units.</param>
    /// <param name="cellMetres">World metres per map unit across.</param>
    /// <param name="metresPerUnit">World metres per map unit up (the relief's exaggeration).</param>
    internal MapWeather(IEngineContext engine, Func<double, double, Vector3> surface, double cellMetres, double metresPerUnit)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.surface = surface ?? throw new ArgumentNullException(nameof(surface));
        this.cellMetres = cellMetres;
        this.metresPerUnit = metresPerUnit;
    }

    internal int Shown => fronts.Count;

    /// <summary>How many fronts are drawn, and where the nearest-listed one floats, for the readout.</summary>
    internal string Readout() => fronts.Count == 0 ? "weather=0"
        : FormattableString.Invariant($"weather={fronts.Count};firstFront={fronts[0].Kind}@{Aloft(fronts[0].Centre).X:F1},{Aloft(fronts[0].Centre).Y:F1},{Aloft(fronts[0].Centre).Z:F1}r{fronts[0].RadiusMetres / cellMetres:F1}");

    internal void Show(IReadOnlyList<WeatherMarker> markers) => fronts = markers;

    /// <summary>The discs and trails, numbered from the map's weather range; <paramref name="cameraDistance"/> sizes the trail dots.</summary>
    internal IEnumerable<AppearanceFact> Facts(float cameraDistance)
    {
        int disc = 0, dot = 0;
        foreach (WeatherMarker front in fronts.Take(ProductIds.WorldMapWeatherLimit))
        {
            Vector3 centre = Aloft(front.Centre);
            float across = (float)(front.RadiusMetres / cellMetres);
            yield return new AppearanceFact(ProductIds.WorldMapWeatherBase + (ulong)disc++, false, 0,
                new Transform(centre, Quaternion.Identity, new Vector3(across, across, across)), Disc(front.Kind, front.Strength), true, RenderLayer.Scene, ShadowCasting.None);
            float scale = Math.Max(MinimumTrackScale, cameraDistance * TrackScalePerDistance);
            foreach (Vector2 ahead in front.Track)
            {
                if (dot >= ProductIds.WorldMapWeatherTrackLimit) break;
                yield return new AppearanceFact(ProductIds.WorldMapWeatherTrackBase + (ulong)dot++, false, 0,
                    new Transform(Aloft(ahead), Quaternion.Identity, Vector3.One * scale), Dot(front.Kind), true, RenderLayer.Scene, ShadowCasting.None);
            }
        }
    }

    public void Dispose()
    {
        foreach ((MeshResource mesh, Appearance look) in discs.Values)
        {
            look.Dispose();
            mesh.Dispose();
        }

        foreach (Appearance look in dots.Values) look.Dispose();
        discs.Clear();
        dots.Clear();
        foreach (Material material in discMaterials.Values) material.Dispose();
        discMaterials.Clear();
    }

    private Vector3 Aloft(Vector2 world) => surface(world.X, world.Y) + (Vector3.UnitY * (float)(CloudLiftMetres / metresPerUnit));

    private Appearance Disc(string kind, float strength)
    {
        int step = Math.Clamp((int)MathF.Ceiling(strength * StrengthSteps), 1, StrengthSteps);
        if (discs.TryGetValue((kind, step), out var built)) return built.Look;
        Color colour = MapPalette.Weather(kind);
        float alpha = CoreAlpha * step / StrengthSteps;
        // The top, then each ring outward: a dome h = DomeHeight (1 - r²), opaque to the core, clear at the rim.
        List<Vector3> positions = [new(0, DomeHeight, 0)];
        List<Vector3> normals = [Vector3.UnitY];
        List<Color> colours = [colour with { A = alpha }];
        List<uint> indices = [];
        foreach (float ring in Rings)
        {
            float fade = ring <= CoreShare ? 1 : 1 - ((ring - CoreShare) / (1 - CoreShare));
            for (int segment = 0; segment < Segments; segment++)
            {
                float angle = segment * MathF.Tau / Segments;
                Vector2 way = new(MathF.Cos(angle), MathF.Sin(angle));
                positions.Add(new Vector3(way.X * ring, DomeHeight * (1 - (ring * ring)), way.Y * ring));
                normals.Add(Vector3.Normalize(new Vector3(way.X * 2 * DomeHeight * ring, 1, way.Y * 2 * DomeHeight * ring)));
                colours.Add(colour with { A = alpha * fade });
            }
        }

        for (int segment = 0; segment < Segments; segment++)
        {
            uint next = (uint)((segment + 1) % Segments);
            indices.AddRange([0, 1 + next, 1 + (uint)segment]);
        }

        for (int ring = 0; ring < Rings.Length - 1; ring++)
        {
            uint inner = (uint)(1 + (ring * Segments)), outer = inner + Segments;
            for (int segment = 0; segment < Segments; segment++)
            {
                uint a = (uint)segment, b = (uint)((segment + 1) % Segments);
                indices.AddRange([inner + a, inner + b, outer + b, inner + a, outer + b, outer + a]);
            }
        }

        MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(positions.ToArray(),
            normals.ToArray(), Enumerable.Repeat(Vector2.Zero, positions.Count).ToArray(),
            colours.ToArray(), indices.ToArray(), new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, Material(kind, colour)) }));
        Appearance look = engine.Graphics.CreateMeshAppearance(mesh);
        discs[(kind, step)] = (mesh, look);
        return look;
    }

    /// <summary>A kind's cloud material: lit by the map's sun, with a faint glow of its own so a front still shows at night.</summary>
    private Material Material(string kind, Color colour)
    {
        if (discMaterials.TryGetValue(kind, out Material? built)) return built;
        Color white = new(1, 1, 1, 1);
        Material material = engine.Graphics.CreateMaterial(new MaterialRequest(white, default(RenderResourceReference), DiscRoughness,
            white, new Vector3(colour.R, colour.G, colour.B), NightGlow, true) with { AlphaMode = MaterialAlphaMode.Blend });
        discMaterials[kind] = material;
        return material;
    }

    private Appearance Dot(string kind)
    {
        if (dots.TryGetValue(kind, out Appearance? built)) return built;
        Appearance look = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, MapPalette.Weather(kind)));
        dots[kind] = look;
        return look;
    }
}
