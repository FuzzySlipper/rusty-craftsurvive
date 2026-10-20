using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A cave's still water drawn (#10094; owner, 2026-10-20: lakes and pools for mood and variety): each lake's or pool's
/// open surface as one flat mesh at its water's level, a square for every half-metre column of it, in the world's water
/// look (depth tint and shore foam from the scene behind it). The space publishes the meshes with its growths; this owns
/// the material and its textures. Mist sumps are not water and are not drawn here.
/// </summary>
internal sealed class CaveWaterSurfaces : IDisposable
{
    /// <summary>The side of each surface square: the cave field's step.</summary>
    private const float Column = KarstCave.FieldStep;

    /// <summary>How glossy the water is (the open world's water study's).</summary>
    private const float Roughness = 0.1f;

    private readonly IEngineContext engine;
    private RenderResource? foam, ripples;
    private Material? material;

    internal CaveWaterSurfaces(IEngineContext engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>One surface mesh for each lake and pool, in the session.</summary>
    internal IEnumerable<MeshResourceCreateRequest> Requests(IReadOnlyList<WaterZone> zones)
    {
        foreach (WaterZone zone in zones.Where(z => z.Kind is WaterZoneKind.Lake or WaterZoneKind.Pool && z.SurfaceColumns.Count > 0))
        {
            yield return Surface(zone, Material());
        }
    }

    private Material Material()
    {
        foam ??= TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterFoamContentPath);
        ripples ??= TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterRipplesContentPath);
        Color white = new(1f, 1f, 1f, 1f);
        return material ??= engine.Graphics.CreateMaterial(new MaterialRequest(white, default(RenderResourceReference), Roughness, white, Vector3.Zero, 0f, false)
            with { AlphaMode = MaterialAlphaMode.Blend, Water = TerrainAtlasCatalog.WaterLook(foam, ripples) });
    }

    private static MeshResourceCreateRequest Surface(WaterZone zone, Material material)
    {
        List<Vector3> positions = [];
        List<uint> indices = [];
        float half = Column * 0.5f;
        foreach (Vector2 c in zone.SurfaceColumns)
        {
            Vector3 centre = DungeonSpace.InSession(new Vector3(c.X, zone.Surface, c.Y));
            uint first = (uint)positions.Count;
            positions.Add(centre + new Vector3(-half, 0f, -half));
            positions.Add(centre + new Vector3(half, 0f, -half));
            positions.Add(centre + new Vector3(half, 0f, half));
            positions.Add(centre + new Vector3(-half, 0f, half));
            indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
        }

        Color white = new(1f, 1f, 1f, 1f);
        return new MeshResourceCreateRequest(positions.ToArray(), Enumerable.Repeat(Vector3.UnitY, positions.Count).ToArray(),
            positions.Select(p => new Vector2(p.X, p.Z)).ToArray(), Enumerable.Repeat(white, positions.Count).ToArray(), indices.ToArray(),
            new[] { new MeshGroup(0, 0, (uint)indices.Count) }, new[] { new MeshMaterialBinding(0, material) });
    }

    public void Dispose()
    {
        material?.Dispose();
        material = null;
        foam?.Dispose();
        ripples?.Dispose();
        foam = ripples = null;
    }
}
