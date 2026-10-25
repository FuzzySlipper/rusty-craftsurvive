using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// Map-scale clutter for the map's detail patch: generated low-poly meshes (trees, rocks),
/// not voxels, scattered deterministically by environment over a jittered grid and set on the
/// drawn surface. Instances are appearance facts sharing one appearance per model. Each model is a
/// unit-height textured prop mesh (<see cref="PropMesh"/>, #9831): the rock and cactus are painted tiles
/// over a generated shape, the trees the first-person trees' distant-band meshes (scripts/generate-flora.py).
/// </summary>
internal sealed class MapClutter : IDisposable
{
    internal enum Model { Pine, Broadleaf, Palm, Rock, Cactus, DeadTree }

    private const double SpacingMetres = 24;
    private const double JitterFraction = 0.8;
    private const double RockyGround = 0.5;
    private const double RockChance = 0.12;
    /// <summary>Instance height in presentation units (one unit is one 32 m map cell across).</summary>
    private const float TreeHeight = 0.7f;
    private const float RockHeight = 0.35f;
    private const float SizeVariation = 0.25f;
    private const ulong ScatterSalt = 0xA0761D6478BD642FUL;
    /// <summary>Salts the draw that thins clutter toward the patch's edge, apart from the draws that place and choose it.</summary>
    private const ulong ThinSalt = 6;
    /// <summary>Stone, bark and leaves: matt, and at map scale nothing moves in the wind.</summary>
    private const float PropRoughness = 0.95f;

    private readonly Dictionary<Model, Appearance> appearances = [];
    private readonly List<MeshResource> meshes = [];
    private readonly PropMaterials materials;
    private readonly WorldMap map;
    private readonly Func<double, double, MapSample> geography;
    private AppearanceFact[] shown = [], hidden = [];

    /// <param name="geography">Where clutter reads the ground: a continent's region tiles, else the map.</param>
    internal MapClutter(IEngineContext engine, ProductContent content, WorldMap map, Func<double, double, MapSample>? geography = null)
    {
        this.map = map;
        this.geography = geography ?? map.Sample;
        materials = new PropMaterials(engine, _ => new PropMaterials.Finish(PropRoughness, 0, 0));
        try
        {
            foreach (Model model in Enum.GetValues<Model>())
                appearances[model] = Create(engine, content, ContentPath(model));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Scatter over a world rectangle. Placement is decided per grid cell from world coordinates,
    /// so a moved rectangle keeps every instance it still covers exactly where it was.
    /// </summary>
    /// <param name="surface">The drawn surface in presentation space at a world X/Z, in metres.</param>
    /// <param name="density">The share of the clutter kept at a point (0 to 1): the patch thins it toward its edge (#9952), so the trees do not stop in a straight line.</param>
    internal void Scatter(Vector2 minimum, Vector2 maximum, Func<double, double, Vector3> surface, Func<double, double, double> density)
    {
        ulong seed = map.Configuration.Contract.GeographyNoiseSeed ^ ScatterSalt;
        List<AppearanceFact> facts = [];
        ulong hash = 0xCBF29CE484222325UL;
        void Mix(ulong value) => hash = unchecked((hash ^ value) * 0x100000001B3UL);
        long x0 = (long)Math.Floor(minimum.X / SpacingMetres), x1 = (long)Math.Floor(maximum.X / SpacingMetres);
        long z0 = (long)Math.Floor(minimum.Y / SpacingMetres), z1 = (long)Math.Floor(maximum.Y / SpacingMetres);
        for (long gz = z0; gz <= z1 && facts.Count < ProductIds.WorldMapClutterLimit; gz++)
        for (long gx = x0; gx <= x1 && facts.Count < ProductIds.WorldMapClutterLimit; gx++)
        {
            double x = (gx + 0.5 + (MapNoise.Unit(seed, gx, gz) - 0.5) * JitterFraction) * SpacingMetres;
            double z = (gz + 0.5 + (MapNoise.Unit(seed ^ 1, gx, gz) - 0.5) * JitterFraction) * SpacingMetres;
            if (x < minimum.X || z < minimum.Y || x >= maximum.X || z >= maximum.Y) continue;
            if (MapNoise.Unit(seed ^ ThinSalt, gx, gz) >= density(x, z)) continue;
            MapSample sample = geography(x, z);
            if (sample.InRiver || sample.Elevation < GenerationConstants.WaterLevel || sample.Protection > 0.5) continue;
            if (Choose(sample, MapNoise.Unit(seed ^ 2, gx, gz), MapNoise.Unit(seed ^ 3, gx, gz)) is not Model model) continue;
            float size = (model == Model.Rock ? RockHeight : TreeHeight) * (1 + SizeVariation * (2 * (float)MapNoise.Unit(seed ^ 4, gx, gz) - 1));
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(MapNoise.Unit(seed ^ 5, gx, gz) * 2 * Math.PI));
            Vector3 at = surface(x, z);
            Mix((ulong)model);
            Mix(BitConverter.SingleToUInt32Bits(at.X));
            Mix(BitConverter.SingleToUInt32Bits(at.Y));
            Mix(BitConverter.SingleToUInt32Bits(at.Z));
            Mix(BitConverter.SingleToUInt32Bits(size));
            facts.Add(new(ProductIds.WorldMapClutterBase + (ulong)facts.Count, false, 0,
                new(at, turn, Vector3.One * size), appearances[model], true, RenderLayer.Scene));
        }
        Digest = hash;
        shown = [.. facts];
        hidden = [.. facts.Select(fact => fact with { Visible = false })];
    }

    internal int Count => shown.Length;

    /// <summary>A digest of the last scatter's instances (model, place and size, in order), so a scatter that kept ground can be compared with one read afresh (#10354).</summary>
    internal ulong Digest { get; private set; }

    internal ReadOnlySpan<AppearanceFact> Facts(bool visible) => visible ? shown : hidden;

    public void Dispose()
    {
        foreach (Appearance appearance in appearances.Values) appearance.Dispose();
        appearances.Clear();
        foreach (MeshResource mesh in meshes) mesh.Dispose();
        meshes.Clear();
        materials.Dispose();
    }

    private Appearance Create(IEngineContext engine, ProductContent content, string path)
    {
        MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, path,
            (role, texture) => materials.For(role, texture ?? throw new InvalidOperationException($"CraftSurvive map clutter '{path}' part '{role}' names no texture."))));
        meshes.Add(mesh);
        return engine.Graphics.CreateMeshAppearance(mesh);
    }

    internal static string ContentPath(Model model) => model switch
    {
        Model.Pine => "models/map/pine" + PropMesh.Suffix,
        Model.Broadleaf => "models/map/broadleaf" + PropMesh.Suffix,
        Model.Palm => "models/map/palm" + PropMesh.Suffix,
        Model.Rock => "models/map/rock" + PropMesh.Suffix,
        Model.Cactus => "models/map/cactus" + PropMesh.Suffix,
        _ => "models/map/deadtree" + PropMesh.Suffix,
    };

    /// <summary>What, if anything, grows or stands here: density and kind follow the environment.</summary>
    private static Model? Choose(MapSample sample, double presence, double kind)
    {
        if (sample.Rock >= RockyGround) return presence < RockChance ? Model.Rock : null;
        (double density, Model first, Model second, double secondShare) = WorldMap.Biome(sample) switch
        {
            MapBiome.BorealForest => (0.6, Model.Pine, Model.Pine, 0.0),
            MapBiome.TemperateForest => (0.6, Model.Broadleaf, Model.Pine, 0.15),
            MapBiome.Rainforest => (0.7, Model.Palm, Model.Broadleaf, 0.35),
            MapBiome.Grassland => (0.06, Model.Broadleaf, Model.Rock, 0.2),
            MapBiome.Shrubland => (0.06, Model.Cactus, Model.Broadleaf, 0.4),
            MapBiome.Desert => (0.035, Model.Cactus, Model.Rock, 0.3),
            MapBiome.ColdSteppe => (0.05, Model.Pine, Model.DeadTree, 0.5),
            MapBiome.Tundra => (0.04, Model.DeadTree, Model.Rock, 0.3),
            MapBiome.Alpine => (0.1, Model.Rock, Model.Rock, 0.0),
            _ => (0.0, Model.Rock, Model.Rock, 0.0),
        };
        if (presence >= density) return null;
        return kind < secondShare ? second : first;
    }
}
