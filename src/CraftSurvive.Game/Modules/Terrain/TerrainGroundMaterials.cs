using System.Text.Json;
using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>Authored repeating terrain maps. Engine owns texture sampling and triplanar blending.</summary>
internal sealed class TerrainGroundMaterials : IDisposable
{
    private const string Root = "textures/terrain-studies/";
    private const string Manifest = Root + "materials.json";
    private const uint Version = 1;
    private const float Roughness = 0.95f;
    private const float Unit = 1f;
    private const float Zero = 0f;
    private readonly List<RenderResource> textures = [];
    private readonly Dictionary<string, Material> materials = [];
    private AuthoredCatalog? catalog;
    private Material? blended;
    internal TerrainBlendSettings Settings { get; }

    internal TerrainGroundMaterials(IEngineContext engine, ProductContent content, double voxelSize = TerrainConstants.VoxelSize)
    {
        Settings = TerrainBlendSettings.Parse(content.ReadText(TerrainBlendSettings.ContentPath));
        using JsonDocument manifest = JsonDocument.Parse(content.ReadText(Manifest));
        List<AuthoredCatalogEntryInput> entries = [];
        List<AuthoredCatalogDependencyInput> dependencies = [];
        List<AuthoredMaterialInput> definitions = [];
        List<AuthoredTextureInput> textureDefinitions = [];
        List<AuthoredVoxelSurfaceInput> surfaces = [];
        List<(string Name, string Material, string Path, float Sharpness)> maps = [];
        foreach (JsonElement item in manifest.RootElement.EnumerateArray())
        {
            string name = item.GetProperty("id").GetString()!;
            string path = Root + name + ".png";
            string texture = "texture/ground/" + name;
            string material = "material/ground/" + name;
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
                materials[TerrainLayers.Names[0]], TerrainLayers.Names.Skip(1).Select(name => materials[name]).ToArray(), Settings.Contrast));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

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
