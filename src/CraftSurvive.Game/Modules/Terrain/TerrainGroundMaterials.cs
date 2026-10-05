using System.Text.Json;
using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// A set of repeating ground maps: a content folder holding materials.json and blending.json,
/// the four names that become terrain layers (base first), and a namespace for their ids.
/// </summary>
internal sealed record GroundTextureSet(string Root, IReadOnlyList<string> LayerNames, string Namespace)
{
    internal static GroundTextureSet Walking { get; } = new("textures/terrain-studies/", TerrainLayers.Names, "ground");
}

/// <summary>Authored repeating terrain maps. Engine owns texture sampling and triplanar blending.</summary>
internal sealed class TerrainGroundMaterials : IDisposable
{
    private const uint Version = 1;
    private const float Roughness = 0.95f;
    private const float Unit = 1f;
    private const float Zero = 0f;
    private readonly List<RenderResource> textures = [];
    private readonly Dictionary<string, Material> materials = [];
    private AuthoredCatalog? catalog;
    private Material? blended;
    internal TerrainBlendSettings Settings { get; }

    internal TerrainGroundMaterials(IEngineContext engine, ProductContent content, double voxelSize = TerrainConstants.VoxelSize,
        GroundTextureSet? set = null)
    {
        set ??= GroundTextureSet.Walking;
        string root = set.Root;
        Settings = TerrainBlendSettings.Parse(content.ReadText(root + "blending.json"));
        using JsonDocument manifest = JsonDocument.Parse(content.ReadText(root + "materials.json"));
        List<AuthoredCatalogEntryInput> entries = [];
        List<AuthoredCatalogDependencyInput> dependencies = [];
        List<AuthoredMaterialInput> definitions = [];
        List<AuthoredTextureInput> textureDefinitions = [];
        List<AuthoredVoxelSurfaceInput> surfaces = [];
        List<(string Name, string Material, string Path, float Sharpness)> maps = [];
        foreach (JsonElement item in manifest.RootElement.EnumerateArray())
        {
            string name = item.GetProperty("id").GetString()!;
            // A set may reuse another set's image through an explicit content path.
            string path = item.TryGetProperty("path", out JsonElement explicitPath) ? explicitPath.GetString()! : root + name + ".png";
            string texture = $"texture/{set.Namespace}/" + name;
            string material = $"material/{set.Namespace}/" + name;
            string hash = item.GetProperty("sha256").GetString()!;
            // Engine repeat scales are tile widths in voxel cells, not repeats per cell.
            float scale = item.GetProperty("metresPerTile").GetSingle() * Settings.TextureScale / (float)voxelSize;
            entries.Add(new(texture, Version, true, hash, true, path, true, name));
            entries.Add(new(material, Version, false, string.Empty, false, string.Empty, true, name));
            dependencies.Add(new(material, texture, AssetVersionRequirementKind.Exact, Version, true, hash));
            textureDefinitions.Add(new(texture, item.GetProperty("width").GetUInt32(),
                item.GetProperty("height").GetUInt32(), AuthoredTextureFilter.Linear, AuthoredTextureWrap.Repeat));
            definitions.Add(new(material, true, true, true, AuthoredStructuralClass.Solid,
                new Color(Unit, Unit, Unit, Unit), true, texture, AssetVersionRequirementKind.Exact,
                Version, true, hash, Roughness, new Color(Unit, Unit, Unit, Unit),
                new Color(Zero, Zero, Zero, Unit), Zero, AuthoredUvStrategy.Planar));
            surfaces.Add(new(material, Version, AuthoredVoxelSurfaceMappingKind.Repeat,
                texture, AssetVersionRequirementKind.Exact, Version, true, hash,
                string.Empty, AssetVersionRequirementKind.Any, 0, false, string.Empty, string.Empty,
                scale, scale, Zero, Zero, AuthoredVoxelAlphaModeKind.Opaque, Zero));
            maps.Add((name, material, path, item.GetProperty("triplanarSharpness").GetSingle() * Settings.ProjectionSharpnessScale));
        }

        try
        {
            catalog = engine.AuthoredContent.AdmitCatalogPayload(new(entries.ToArray(), dependencies.ToArray(),
                definitions.ToArray(), textureDefinitions.ToArray(), ReadOnlyMemory<AuthoredVoxelAtlasInput>.Empty,
                ReadOnlyMemory<AuthoredAtlasRegionInput>.Empty, surfaces.ToArray()));
            foreach (var map in maps)
            {
                RenderResource texture = engine.Graphics.OpenResource(new RenderResourceRequest(
                    map.Path, TextureFilter.Linear, TextureWrap.Repeat)).Handle;
                textures.Add(texture);
                materials.Add(map.Name, engine.Graphics.CreateAuthoredMaterial(
                    new AuthoredMaterialAppearanceRequest(catalog, map.Material, texture) { TriplanarSharpness = map.Sharpness }));
            }
            blended = engine.Graphics.CreateTerrainLayerMaterial(new TerrainLayerMaterialRequest(
                materials[set.LayerNames[0]], set.LayerNames.Skip(1).Select(name => materials[name]).ToArray(), Settings.Contrast));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The terrain-layer material blending this set's four layers.</summary>
    internal Material Layered => blended ?? throw new ObjectDisposedException(nameof(TerrainGroundMaterials));

    /// <summary>One map of the set on its own, unblended.</summary>
    internal Material Plain(string name) => materials[name];

    internal Material? For(BlockId block, bool blend = true) => TerrainLayers.Layer(block) is int layer && layer >= 0
        ? blend ? blended : materials[TerrainLayers.Names[layer]] : null;

    internal void Configure(IEngineContext engine, SpatialSession session) =>
        TerrainLayers.Configure(engine, session, Settings.TransitionCells);

    public void Dispose()
    {
        blended?.Dispose();
        blended = null;
        foreach (Material material in materials.Values) material.Dispose();
        materials.Clear();
        foreach (RenderResource texture in textures) texture.Dispose();
        textures.Clear();
        catalog?.Dispose();
        catalog = null;
    }
}
