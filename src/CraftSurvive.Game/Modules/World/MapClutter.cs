using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// Map-scale clutter for the faceted map's detail patch: generated low-poly meshes (trees, rocks),
/// not voxels, scattered deterministically by environment over a jittered grid and set on the
/// drawn surface. Instances are appearance facts sharing one appearance per model.
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
    private static readonly Color Untinted = new(1, 1, 1, 1);

    private readonly Dictionary<Model, Appearance> appearances = [];
    private readonly AppearanceFact[] shown, hidden;

    /// <param name="surface">The drawn surface in presentation space at a world X/Z, in metres.</param>
    internal MapClutter(IEngineContext engine, WorldMap map, Vector2 minimum, Vector2 maximum, Func<double, double, Vector3> surface)
    {
        try
        {
            foreach (Model model in Enum.GetValues<Model>())
                appearances[model] = engine.Graphics.CreateStaticMeshFromContent(new(ContentPath(model), Untinted));
        }
        catch
        {
            Dispose();
            throw;
        }
        ulong seed = map.Configuration.Contract.GeographyNoiseSeed ^ ScatterSalt;
        List<AppearanceFact> facts = [];
        long x0 = (long)Math.Floor(minimum.X / SpacingMetres), x1 = (long)Math.Floor(maximum.X / SpacingMetres);
        long z0 = (long)Math.Floor(minimum.Y / SpacingMetres), z1 = (long)Math.Floor(maximum.Y / SpacingMetres);
        for (long gz = z0; gz <= z1 && facts.Count < ProductIds.WorldMapClutterLimit; gz++)
        for (long gx = x0; gx <= x1 && facts.Count < ProductIds.WorldMapClutterLimit; gx++)
        {
            double x = (gx + 0.5 + (MapNoise.Unit(seed, gx, gz) - 0.5) * JitterFraction) * SpacingMetres;
            double z = (gz + 0.5 + (MapNoise.Unit(seed ^ 1, gx, gz) - 0.5) * JitterFraction) * SpacingMetres;
            if (x < minimum.X || z < minimum.Y || x >= maximum.X || z >= maximum.Y) continue;
            MapSample sample = map.Sample(x, z);
            if (sample.InRiver || sample.Elevation < GenerationConstants.WaterLevel || sample.Protection > 0.5) continue;
            if (Choose(sample, MapNoise.Unit(seed ^ 2, gx, gz), MapNoise.Unit(seed ^ 3, gx, gz)) is not Model model) continue;
            float size = (model == Model.Rock ? RockHeight : TreeHeight) * (1 + SizeVariation * (2 * (float)MapNoise.Unit(seed ^ 4, gx, gz) - 1));
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(MapNoise.Unit(seed ^ 5, gx, gz) * 2 * Math.PI));
            facts.Add(new(ProductIds.WorldMapClutterBase + (ulong)facts.Count, false, 0,
                new(surface(x, z), turn, Vector3.One * size), appearances[model], true, RenderLayer.Scene));
        }
        shown = [.. facts];
        hidden = [.. facts.Select(fact => fact with { Visible = false })];
    }

    internal int Count => shown.Length;

    internal ReadOnlySpan<AppearanceFact> Facts(bool visible) => visible ? shown : hidden;

    public void Dispose()
    {
        foreach (Appearance appearance in appearances.Values) appearance.Dispose();
        appearances.Clear();
    }

    internal static string ContentPath(Model model) => model switch
    {
        Model.Pine => "models/map/pine.static-mesh.json",
        Model.Broadleaf => "models/map/broadleaf.static-mesh.json",
        Model.Palm => "models/map/palm.static-mesh.json",
        Model.Rock => "models/map/rock.static-mesh.json",
        Model.Cactus => "models/map/cactus.static-mesh.json",
        _ => "models/map/deadtree.static-mesh.json",
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
