using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Owns CraftSurvive's authored block-material closure for the voxel world. The
/// payload tables are built from <see cref="BlockRegistry"/> and the checked atlas
/// layout rather than listed here, so adding a block is one registry entry plus one
/// generated tile. The product selects canonical asset identities, block meaning
/// and face policy; Engine owns validation, resource admission, material
/// realization, and renderer lifetime.
/// </summary>
internal sealed class TerrainAtlasCatalog : IDisposable
{
    internal const string AtlasContentPath = TerrainAtlasLayout.ImageContentPath;
    private const uint Version = 1;
    private const ushort NoPadding = 0;
    private const float TileScale = 1f;
    private const float TileOrigin = 0f;
    private const float NoEmission = 0f;
    private const float CutoutThreshold = 0.5f;
    private const float Roughness = TerrainConstants.TerrainRoughness;
    private const float Alpha = TerrainConstants.MaterialAlpha;
    private const float LampRed = 1f;
    private const float LampGreen = 0.85f;
    private const float LampBlue = 0.54f;
    private const int FixedEntryCount = 2;

    /// <summary>
    /// Authored materials the directional voxel projection accepts per scene, raised
    /// from three base bindings by `rusty-engine` #8667. The product still refuses
    /// an over-capacity binding here, with a message that names the limit, because
    /// exceeding it fails inside the Engine with a bare status.
    /// </summary>
    private const int MaximumMaterials = 16;

    private readonly AuthoredCatalog catalog;
    private readonly TerrainAtlasLayout layout;

    /// <summary>
    /// The admitted atlas image as a render reference. One resource serves the whole atlas, and it
    /// is what every material is built from - so it is also what an effect outside the voxel
    /// renderer can point a sprite at, rather than opening a second copy of the same image.
    /// </summary>
    private RenderResourceReference atlasReference;
    private readonly List<Material> materials = [];
    private readonly Dictionary<BlockId, Material> baseMaterials = [];
    private readonly Dictionary<BlockId, Material> topMaterials = [];
    private bool disposed;

    internal TerrainAtlasCatalog(IEngineContext engine, ProductContent content)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(content);

        AuthoredCatalog? admittedCatalog = null;
        try
        {
            layout = TerrainAtlasLayout.Read(content);
            RequireBindableMaterialCount();
            RenderResourceInfo texture = engine.Graphics.OpenResource(new RenderResourceRequest(AtlasContentPath));
            if (texture.Kind != RenderResourceKind.Texture || texture.ByteLength == 0 || texture.Handle.Handle.Value == 0)
            {
                throw new InvalidOperationException("CraftSurvive terrain atlas must open as a non-empty Engine texture resource.");
            }

            // The resource itself converts implicitly to a reference. Building one by hand from
            // the inner handle's numeric value is what segfaulted the host: the reference wants the
            // outer resource, not the handle inside it.
            atlasReference = texture.Handle;

            admittedCatalog = engine.AuthoredContent.AdmitCatalogPayload(CreatePayload());
            ValidateCatalog(engine.AuthoredContent.ReadCatalog(admittedCatalog));

            foreach (BlockDefinition block in BlockRegistry.BoundBlocks)
            {
                baseMaterials[block.Id] = AdmitMaterial(engine, admittedCatalog, block.MaterialId, texture.Handle);
                if (block.TopRegion is null)
                {
                    continue;
                }

                topMaterials[block.Id] = AdmitMaterial(engine, admittedCatalog, block.TopMaterialId, texture.Handle);
            }

            catalog = admittedCatalog;
        }
        catch
        {
            DisposeMaterials();
            admittedCatalog?.Dispose();
            throw;
        }
    }

    /// <summary>The atlas layout the materials were bound from.</summary>
    internal TerrainAtlasLayout Layout => layout;

    /// <summary>The atlas image as a render reference, for effects that draw from the same content.</summary>
    internal RenderResourceReference AtlasReference => atlasReference;

    /// <summary>The material for a block's non-overridden faces.</summary>
    internal Material BaseMaterial(BlockId id) => Lookup(baseMaterials, id);

    /// <summary>The +Y material for a block that overrides its top face, if any.</summary>
    internal Material? TopMaterial(BlockId id) => topMaterials.TryGetValue(id, out Material? material) ? material : null;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DisposeMaterials();
        catalog.Dispose();
    }

    private void DisposeMaterials()
    {
        for (int index = materials.Count - 1; index >= 0; index--)
        {
            materials[index].Dispose();
        }

        materials.Clear();
        baseMaterials.Clear();
        topMaterials.Clear();
    }

    private static void RequireBindableMaterialCount()
    {
        int bindable = 0;
        foreach (BlockDefinition block in BlockRegistry.BoundBlocks)
        {
            bindable += block.TopRegion is null ? 1 : 2;
        }

        if (bindable > MaximumMaterials)
        {
            throw new InvalidOperationException(
                $"Block binding needs {bindable} materials, and the Engine's directional voxel projection " +
                $"accepts {MaximumMaterials} per scene.");
        }
    }

    private static Material Lookup(Dictionary<BlockId, Material> table, BlockId id) =>
        table.TryGetValue(id, out Material? material)
            ? material
            : throw new InvalidOperationException($"Block '{BlockRegistry.Get(id).Name}' has no admitted material.");

    private Material AdmitMaterial(IEngineContext engine, AuthoredCatalog admittedCatalog, string materialId,
        RenderResource texture)
    {
        Material material = engine.Graphics.CreateAuthoredMaterial(
            new AuthoredMaterialAppearanceRequest(admittedCatalog, materialId, texture));
        materials.Add(material);
        return material;
    }

    private AuthoredCatalogPayloadAdmitRequest CreatePayload() => new(
        Entries(),
        Dependencies(),
        Materials(),
        new AuthoredTextureInput[]
        {
            new(TerrainAtlasLayout.TextureId, layout.ExtentX, layout.ExtentY, AuthoredTextureFilter.Nearest,
                AuthoredTextureWrap.Clamp),
        },
        new AuthoredVoxelAtlasInput[]
        {
            new(TerrainAtlasLayout.AtlasId, Version, TerrainAtlasLayout.TextureId, AssetVersionRequirementKind.Exact,
                Version, true, layout.ContentHash),
        },
        Regions(),
        Surfaces());

    private AuthoredCatalogEntryInput[] Entries()
    {
        List<AuthoredCatalogEntryInput> entries =
        [
            new(TerrainAtlasLayout.TextureId, Version, true, layout.ContentHash, true,
                TerrainAtlasLayout.ImageContentPath, true, "CraftSurvive terrain atlas"),
            // The atlas is the authored layout over the selected PNG, so its
            // stable identity is pinned to that source artifact as well.
            new(TerrainAtlasLayout.AtlasId, Version, true, layout.ContentHash, false, string.Empty, true,
                "CraftSurvive terrain atlas layout"),
        ];

        foreach (BlockDefinition block in BlockRegistry.BoundBlocks)
        {
            entries.Add(new AuthoredCatalogEntryInput(
                block.MaterialId, Version, false, string.Empty, false, string.Empty, true, $"CraftSurvive {block.Name}"));
            if (block.TopRegion is not null)
            {
                entries.Add(new AuthoredCatalogEntryInput(
                    block.TopMaterialId, Version, false, string.Empty, false, string.Empty, true,
                    $"CraftSurvive {block.Name} top"));
            }
        }

        return [.. entries];
    }

    private AuthoredCatalogDependencyInput[] Dependencies()
    {
        List<AuthoredCatalogDependencyInput> dependencies =
        [
            // The atlas is a layout over the image, so it depends on it directly.
            Dependency(TerrainAtlasLayout.AtlasId, TerrainAtlasLayout.TextureId, true),
        ];

        foreach (BlockDefinition block in BlockRegistry.BoundBlocks)
        {
            dependencies.Add(Dependency(block.MaterialId, TerrainAtlasLayout.TextureId, true));
            dependencies.Add(Dependency(block.MaterialId, TerrainAtlasLayout.AtlasId, true));
            if (block.TopRegion is not null)
            {
                dependencies.Add(Dependency(block.TopMaterialId, TerrainAtlasLayout.TextureId, true));
                dependencies.Add(Dependency(block.TopMaterialId, TerrainAtlasLayout.AtlasId, true));
            }
        }

        return [.. dependencies];
    }

    private AuthoredCatalogDependencyInput Dependency(string owner, string reference, bool hasHash)
        => new(owner, reference, AssetVersionRequirementKind.Exact, Version, hasHash,
            hasHash ? layout.ContentHash : string.Empty);

    private AuthoredMaterialInput[] Materials()
    {
        List<AuthoredMaterialInput> declared = [];
        foreach (BlockDefinition block in BlockRegistry.BoundBlocks)
        {
            declared.Add(Material(block.MaterialId, block, isTopFace: false));
            if (block.TopRegion is not null)
            {
                declared.Add(Material(block.TopMaterialId, block, isTopFace: true));
            }
        }

        return [.. declared];
    }

    private AuthoredMaterialInput Material(string materialId, BlockDefinition block, bool isTopFace) => new(
        materialId,
        block.Solid,
        block.Collidable,
        block.Occludes,
        StructuralClass(block),
        new Color(Alpha, Alpha, Alpha, Alpha),
        true,
        TerrainAtlasLayout.TextureId,
        AssetVersionRequirementKind.Exact,
        Version,
        true,
        layout.ContentHash,
        Roughness,
        new Color(Alpha, Alpha, Alpha, Alpha),
        isTopFace || block.LightEmission <= 0f
            ? new Color(NoEmission, NoEmission, NoEmission, Alpha)
            : new Color(LampRed, LampGreen, LampBlue, Alpha),
        block.LightEmission,
        AuthoredUvStrategy.Atlas);

    private AuthoredAtlasRegionInput[] Regions()
    {
        List<AuthoredAtlasRegionInput> declared = [];
        foreach (TerrainAtlasRegion region in layout.Regions)
        {
            declared.Add(new AuthoredAtlasRegionInput(
                TerrainAtlasLayout.AtlasId, region.Id, region.X, region.Y, layout.TileWidth, layout.TileHeight,
                NoPadding, NoPadding, NoPadding, NoPadding, AuthoredAtlasInset.HalfTexel));
        }

        return [.. declared];
    }

    private AuthoredVoxelSurfaceInput[] Surfaces()
    {
        List<AuthoredVoxelSurfaceInput> declared = [];
        foreach (BlockDefinition block in BlockRegistry.BoundBlocks)
        {
            declared.Add(Surface(block.MaterialId, block, block.BaseRegion));
            if (block.TopRegion is not null)
            {
                declared.Add(Surface(block.TopMaterialId, block, block.TopRegion));
            }
        }

        return [.. declared];
    }

    private AuthoredVoxelSurfaceInput Surface(string materialId, BlockDefinition block, string region) => new(
        materialId,
        Version,
        AuthoredVoxelSurfaceMappingKind.Atlas,
        string.Empty,
        AssetVersionRequirementKind.Any,
        0,
        false,
        string.Empty,
        TerrainAtlasLayout.AtlasId,
        AssetVersionRequirementKind.Exact,
        Version,
        true,
        layout.ContentHash,
        region,
        TileScale,
        TileScale,
        TileOrigin,
        TileOrigin,
        AlphaMode(block.Transparency),
        block.Transparency == BlockTransparency.Cutout ? CutoutThreshold : NoEmission);

    private static AuthoredStructuralClass StructuralClass(BlockDefinition block) =>
        block.Id == BlockId.Bedrock ? AuthoredStructuralClass.Structural
        : block.Solid && block.Occludes ? AuthoredStructuralClass.Solid
        : AuthoredStructuralClass.Decorative;

    private static AuthoredVoxelAlphaModeKind AlphaMode(BlockTransparency transparency) => transparency switch
    {
        BlockTransparency.Opaque => AuthoredVoxelAlphaModeKind.Opaque,
        BlockTransparency.Cutout => AuthoredVoxelAlphaModeKind.Mask,
        BlockTransparency.Translucent => AuthoredVoxelAlphaModeKind.Blend,
        _ => throw new ArgumentOutOfRangeException(nameof(transparency), transparency, "Unsupported block transparency."),
    };

    private void ValidateCatalog(AuthoredCatalogReadoutLeaseReceipt readout)
    {
        int materialsExpected = 0;
        foreach (BlockDefinition block in BlockRegistry.BoundBlocks)
        {
            materialsExpected += block.TopRegion is null ? 1 : 2;
        }
        if (readout.Entries.Length != FixedEntryCount + materialsExpected ||
            readout.Materials.Length != materialsExpected ||
            readout.Textures.Length != 1 ||
            readout.VoxelAtlases.Length != 1 ||
            readout.AtlasRegions.Length != layout.Regions.Count ||
            readout.VoxelSurfaces.Length != materialsExpected ||
            string.IsNullOrWhiteSpace(readout.CanonicalHash))
        {
            throw new InvalidOperationException(
                $"Engine did not retain the complete CraftSurvive block catalog: {readout.Entries.Length} entries, " +
                $"{readout.Materials.Length} materials, {readout.Textures.Length} textures, " +
                $"{readout.VoxelAtlases.Length} atlases, {readout.AtlasRegions.Length} regions, " +
                $"{readout.VoxelSurfaces.Length} surfaces.");
        }
    }
}
