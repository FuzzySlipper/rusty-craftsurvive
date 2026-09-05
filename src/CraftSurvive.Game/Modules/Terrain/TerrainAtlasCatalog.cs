using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Owns CraftSurvive's small authored terrain-material closure. The product
/// selects canonical asset identities and face policy; Engine owns validation,
/// resource admission, material realization, and renderer lifetime.
/// </summary>
internal sealed class TerrainAtlasCatalog : IDisposable
{
    internal const string AtlasContentPath = "textures/terrain-atlas.png";
    private const string AtlasHash = "d722b7c2af1168cd3d77a5bbcc351156a94c3b77c803fd4641e53eb98d41bf2f";
    private const string TextureId = "texture/terrain-atlas";
    private const string AtlasId = "sprite-sheet/terrain";
    private const string GrassSideMaterialId = "material/terrain-grass-side";
    private const string GrassTopMaterialId = "material/terrain-grass-top";
    private const string DirtMaterialId = "material/terrain-dirt";
    private const string StoneMaterialId = "material/terrain-stone";
    private const uint Version = 1;
    private const uint AtlasPixels = 128;
    private const uint TilePixels = 64;
    private const ushort NoPadding = 0;
    private const float TileScale = 1f;
    private const float TileOrigin = 0f;
    private const float NoEmission = 0f;
    private const float Roughness = TerrainConstants.TerrainRoughness;
    private const float Alpha = TerrainConstants.MaterialAlpha;

    private readonly AuthoredCatalog catalog;
    private readonly List<Material> materials = [];
    private bool disposed;

    internal TerrainAtlasCatalog(IEngineContext engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        AuthoredCatalog? admittedCatalog = null;
        try
        {
            RenderResourceInfo texture = engine.Graphics.OpenResource(new RenderResourceRequest(AtlasContentPath));
            if (texture.Kind != RenderResourceKind.Texture || texture.ByteLength == 0 || texture.Handle.Value == 0)
            {
                throw new InvalidOperationException("CraftSurvive terrain atlas must open as a non-empty Engine texture resource.");
            }

            admittedCatalog = engine.AuthoredContent.AdmitCatalogPayload(CreatePayload());
            ValidateCatalog(engine.AuthoredContent.ReadCatalog(admittedCatalog));

            materials.Add(CreateMaterial(engine, admittedCatalog, GrassSideMaterialId, texture.Handle));
            materials.Add(CreateMaterial(engine, admittedCatalog, GrassTopMaterialId, texture.Handle));
            materials.Add(CreateMaterial(engine, admittedCatalog, DirtMaterialId, texture.Handle));
            materials.Add(CreateMaterial(engine, admittedCatalog, StoneMaterialId, texture.Handle));
            catalog = admittedCatalog;
        }
        catch
        {
            for (int index = materials.Count - 1; index >= 0; index--)
            {
                materials[index].Dispose();
            }

            admittedCatalog?.Dispose();
            throw;
        }
    }

    internal Material GrassSide => MaterialAt(0);

    internal Material GrassTop => MaterialAt(1);

    internal Material Dirt => MaterialAt(2);

    internal Material Stone => MaterialAt(3);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        for (int index = materials.Count - 1; index >= 0; index--)
        {
            materials[index].Dispose();
        }

        materials.Clear();
        catalog.Dispose();
    }

    private Material MaterialAt(int index)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return materials[index];
    }

    private static Material CreateMaterial(IEngineContext engine, AuthoredCatalog catalog, string materialId,
        RenderResourceHandle texture)
        => engine.Graphics.CreateAuthoredMaterial(new AuthoredMaterialAppearanceRequest(catalog, materialId, texture));

    private static AuthoredCatalogPayloadAdmitRequest CreatePayload() => new(
        Entries(),
        Dependencies(),
        Materials(),
        new AuthoredTextureInput[]
        {
            new(TextureId, AtlasPixels, AtlasPixels, AuthoredTextureFilter.Nearest, AuthoredTextureWrap.Clamp),
        },
        new AuthoredVoxelAtlasInput[]
        {
            new(AtlasId, Version, TextureId, AssetVersionRequirementKind.Exact, Version, true, AtlasHash),
        },
        Regions(),
        Surfaces());

    private static AuthoredCatalogEntryInput[] Entries() =>
    [
        new(TextureId, Version, true, AtlasHash, true, AtlasContentPath, true, "CraftSurvive terrain atlas"),
        // The atlas is the authored layout over the selected PNG, so its
        // stable identity is pinned to that source artifact as well.
        new(AtlasId, Version, true, AtlasHash, false, string.Empty, true, "CraftSurvive terrain atlas layout"),
        new(GrassSideMaterialId, Version, false, string.Empty, false, string.Empty, true, "CraftSurvive grass side"),
        new(GrassTopMaterialId, Version, false, string.Empty, false, string.Empty, true, "CraftSurvive grass top"),
        new(DirtMaterialId, Version, false, string.Empty, false, string.Empty, true, "CraftSurvive dirt"),
        new(StoneMaterialId, Version, false, string.Empty, false, string.Empty, true, "CraftSurvive stone"),
    ];

    private static AuthoredCatalogDependencyInput[] Dependencies() =>
    [
        Dependency(AtlasId, TextureId, true),
        Dependency(GrassSideMaterialId, TextureId, true),
        Dependency(GrassSideMaterialId, AtlasId, true),
        Dependency(GrassTopMaterialId, TextureId, true),
        Dependency(GrassTopMaterialId, AtlasId, true),
        Dependency(DirtMaterialId, TextureId, true),
        Dependency(DirtMaterialId, AtlasId, true),
        Dependency(StoneMaterialId, TextureId, true),
        Dependency(StoneMaterialId, AtlasId, true),
    ];

    private static AuthoredCatalogDependencyInput Dependency(string owner, string reference, bool hasHash)
        => new(owner, reference, AssetVersionRequirementKind.Exact, Version, hasHash, hasHash ? AtlasHash : string.Empty);

    private static AuthoredMaterialInput[] Materials() =>
    [
        Material(GrassSideMaterialId),
        Material(GrassTopMaterialId),
        Material(DirtMaterialId),
        Material(StoneMaterialId),
    ];

    private static AuthoredMaterialInput Material(string id) => new(
        id,
        true,
        true,
        true,
        AuthoredStructuralClass.Solid,
        new Color(Alpha, Alpha, Alpha, Alpha),
        true,
        TextureId,
        AssetVersionRequirementKind.Exact,
        Version,
        true,
        AtlasHash,
        Roughness,
        new Color(Alpha, Alpha, Alpha, Alpha),
        new Color(NoEmission, NoEmission, NoEmission, Alpha),
        NoEmission,
        AuthoredUvStrategy.Atlas);

    private static AuthoredAtlasRegionInput[] Regions() =>
    [
        Region("grass-top", 0, 0),
        Region("grass-side", TilePixels, 0),
        Region("dirt", 0, TilePixels),
        Region("stone", TilePixels, TilePixels),
    ];

    private static AuthoredAtlasRegionInput Region(string id, uint x, uint y)
        => new(AtlasId, id, x, y, TilePixels, TilePixels, NoPadding, NoPadding, NoPadding, NoPadding,
            AuthoredAtlasInset.HalfTexel);

    private static AuthoredVoxelSurfaceInput[] Surfaces() =>
    [
        Surface(GrassSideMaterialId, "grass-side"),
        Surface(GrassTopMaterialId, "grass-top"),
        Surface(DirtMaterialId, "dirt"),
        Surface(StoneMaterialId, "stone"),
    ];

    private static AuthoredVoxelSurfaceInput Surface(string materialId, string region)
        => new(materialId, Version, AuthoredVoxelSurfaceMappingKind.Atlas,
            string.Empty, AssetVersionRequirementKind.Any, 0, false, string.Empty,
            AtlasId, AssetVersionRequirementKind.Exact, Version, true, AtlasHash,
            region, TileScale, TileScale, TileOrigin, TileOrigin, AuthoredVoxelAlphaModeKind.Opaque, NoEmission);

    private static void ValidateCatalog(AuthoredCatalogReadoutLeaseReceipt readout)
    {
        if (readout.Entries.Length != 6 || readout.Materials.Length != 4 || readout.Textures.Length != 1 ||
            readout.VoxelAtlases.Length != 1 || readout.AtlasRegions.Length != 4 || readout.VoxelSurfaces.Length != 4 ||
            string.IsNullOrWhiteSpace(readout.CanonicalHash))
        {
            throw new InvalidOperationException("Engine did not retain the complete CraftSurvive terrain atlas catalog.");
        }
    }
}
