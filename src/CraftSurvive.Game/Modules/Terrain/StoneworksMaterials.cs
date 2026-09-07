using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Owns the authored material closure for the reusable stoneworks recipes.
/// Engine admits and realizes the catalog; this product selects the palette.
/// </summary>
internal sealed class StoneworksMaterials : IDisposable
{
    internal const string LimestoneContentPath = "textures/stoneworks/stoneworks-limestone.png";
    internal const string BrickContentPath = "textures/stoneworks/stoneworks-brick.png";
    internal const string MortarContentPath = "textures/stoneworks/stoneworks-mortar.png";
    internal const string PlasterContentPath = "textures/stoneworks/stoneworks-plaster.png";
    internal const string PavingContentPath = "textures/stoneworks/stoneworks-paving.png";
    internal const string MossContentPath = "textures/stoneworks/stoneworks-moss.png";
    internal const string TimberContentPath = "textures/stoneworks/stoneworks-timber.png";
    internal const string BronzeContentPath = "textures/stoneworks/stoneworks-bronze.png";

    private const uint TexturePixels = 32;
    private const uint Version = 1;
    private const float Roughness = 0.9f;
    private const float Alpha = 1f;
    private const float NoEmission = 0f;
    private static readonly MaterialDefinition[] Definitions =
    [
        new("limestone", LimestoneContentPath, "Warm ivory limestone", "e1dda27a90568ba6f908a2e067201eb6247a6db1b270e6083339a2e76100d44f"),
        new("brick", BrickContentPath, "Muted terracotta brick", "30c7af4ddaa5a922669047585854a4413044621d70c0f5389b040365b837168c"),
        new("mortar", MortarContentPath, "Cool grey mortar", "950bf3feb72443c00c4b39eb5b6e8826e14f43da3830826116652648e26289ca"),
        new("plaster", PlasterContentPath, "Warm ivory plaster", "d6bf71292f77557ff161518da3e99bfb1b9bc47c7f95f9afa303047f1ed60aed"),
        new("paving", PavingContentPath, "Desaturated blue-grey paving", "84272022a72eee10b2cacf6e5d2e8222208dcb65ee0b34bbdbaa04ed537cd7ac"),
        new("moss", MossContentPath, "Deep olive moss", "1d3b676fa2953822d2b7c5c4164f2a81628db0ba7cd5e4eb886bd7ed9e1f67d2"),
        new("timber", TimberContentPath, "Dark walnut timber", "0dda31e5f942a11e737834164313447b651e838e4193b03246e45722852ebd85"),
        new("bronze", BronzeContentPath, "Oxidized bronze accent", "c962220e515a00af9eb1625ba0aef75913b2f8da2facfe8fa5d0c8bd69b543fc"),
    ];

    private readonly AuthoredCatalog catalog;
    private readonly Material[] materials;
    private bool disposed;

    internal StoneworksMaterials(IEngineContext engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        AuthoredCatalog? admittedCatalog = null;
        Material[]? admittedMaterials = null;
        try
        {
            RenderResourceInfo[] textures = OpenTextures(engine);
            admittedCatalog = engine.AuthoredContent.AdmitCatalogPayload(CreatePayload());
            ValidateCatalog(engine.AuthoredContent.ReadCatalog(admittedCatalog));
            admittedMaterials = new Material[Definitions.Length];
            for (int index = 0; index < Definitions.Length; index++)
            {
                admittedMaterials[index] = engine.Graphics.CreateAuthoredMaterial(
                    new AuthoredMaterialAppearanceRequest(admittedCatalog, MaterialId(Definitions[index]), textures[index].Handle));
            }

            catalog = admittedCatalog;
            materials = admittedMaterials;
        }
        catch
        {
            if (admittedMaterials is not null)
            {
                for (int index = admittedMaterials.Length - 1; index >= 0; index--)
                {
                    admittedMaterials[index]?.Dispose();
                }
            }

            admittedCatalog?.Dispose();
            throw;
        }
    }

    internal Material Limestone => MaterialAt(0);
    internal Material Brick => MaterialAt(1);
    internal Material Mortar => MaterialAt(2);
    internal Material Plaster => MaterialAt(3);
    internal Material Paving => MaterialAt(4);
    internal Material Moss => MaterialAt(5);
    internal Material Timber => MaterialAt(6);
    internal Material Bronze => MaterialAt(7);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (int index = materials.Length - 1; index >= 0; index--) materials[index].Dispose();
        catalog.Dispose();
    }

    private static RenderResourceInfo[] OpenTextures(IEngineContext engine)
    {
        RenderResourceInfo[] textures = new RenderResourceInfo[Definitions.Length];
        for (int index = 0; index < Definitions.Length; index++)
        {
            RenderResourceInfo texture = engine.Graphics.OpenResource(
                new RenderResourceRequest(Definitions[index].ContentPath, TextureFilter.Nearest, TextureWrap.Repeat));
            if (texture.Kind != RenderResourceKind.Texture || texture.ByteLength == 0 || texture.Handle.Value == 0)
            {
                throw new InvalidOperationException($"Stoneworks texture '{Definitions[index].ContentPath}' must open as a non-empty Engine texture resource.");
            }

            textures[index] = texture;
        }

        return textures;
    }

    private static AuthoredCatalogPayloadAdmitRequest CreatePayload() => new(
        Definitions.SelectMany(definition => new[]
        {
            TextureEntry(definition),
            MaterialEntry(definition),
        }).ToArray(),
        Definitions.Select(definition => new AuthoredCatalogDependencyInput(
            MaterialId(definition), TextureId(definition), AssetVersionRequirementKind.Exact, Version, true, definition.Hash)).ToArray(),
        Definitions.Select(definition => new AuthoredMaterialInput(
            MaterialId(definition), true, true, true, AuthoredStructuralClass.Solid,
            new Color(Alpha, Alpha, Alpha, Alpha), true, TextureId(definition), AssetVersionRequirementKind.Exact,
            Version, true, definition.Hash, Roughness, new Color(Alpha, Alpha, Alpha, Alpha),
            new Color(NoEmission, NoEmission, NoEmission, Alpha), NoEmission, AuthoredUvStrategy.Planar)).ToArray(),
        Definitions.Select(definition => new AuthoredTextureInput(
            TextureId(definition), TexturePixels, TexturePixels, AuthoredTextureFilter.Nearest, AuthoredTextureWrap.Repeat)).ToArray(),
        Array.Empty<AuthoredVoxelAtlasInput>(),
        Array.Empty<AuthoredAtlasRegionInput>(),
        Array.Empty<AuthoredVoxelSurfaceInput>());

    private static AuthoredCatalogEntryInput TextureEntry(MaterialDefinition definition) => new(
        TextureId(definition), Version, true, definition.Hash, true, definition.ContentPath, true, definition.Label);

    private static AuthoredCatalogEntryInput MaterialEntry(MaterialDefinition definition) => new(
        MaterialId(definition), Version, false, string.Empty, false, string.Empty, true, definition.Label);

    private static string TextureId(MaterialDefinition definition) => $"texture/stoneworks-{definition.Name}";

    private static string MaterialId(MaterialDefinition definition) => $"material/stoneworks-{definition.Name}";

    private static void ValidateCatalog(AuthoredCatalogReadoutLeaseReceipt readout)
    {
        if (readout.Entries.Length != Definitions.Length * 2 || readout.Materials.Length != Definitions.Length
            || readout.Textures.Length != Definitions.Length || readout.VoxelAtlases.Length != 0
            || readout.AtlasRegions.Length != 0 || readout.VoxelSurfaces.Length != 0 || string.IsNullOrWhiteSpace(readout.CanonicalHash))
        {
            throw new InvalidOperationException("Engine did not retain the complete Stoneworks material catalog.");
        }
    }

    private Material MaterialAt(int index)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return materials[index];
    }

    private sealed record MaterialDefinition(string Name, string ContentPath, string Label, string Hash);
}
