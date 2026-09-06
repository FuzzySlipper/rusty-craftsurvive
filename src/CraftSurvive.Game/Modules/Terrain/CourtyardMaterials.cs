using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Owns the small authored material closure for the ruined-courtyard surface
/// recipes. It selects product assets; Engine admits the catalog, resolves
/// sampler policy, and owns the realized renderer resources.
/// </summary>
internal sealed class CourtyardMaterials : IDisposable
{
    internal const string StoneContentPath = "textures/courtyard/courtyard-stone.png";
    internal const string MortarContentPath = "textures/courtyard/courtyard-mortar.png";
    internal const string PlasterContentPath = "textures/courtyard/courtyard-plaster.png";
    internal const string GroundContentPath = "textures/courtyard/courtyard-ground.png";
    internal const string MossContentPath = "textures/courtyard/courtyard-moss.png";
    internal const string WoodContentPath = "textures/courtyard/courtyard-wood.png";

    private const uint TexturePixels = 32;
    private const uint Version = 1;
    private const float Roughness = 0.9f;
    private const float Alpha = 1f;
    private const float NoEmission = 0f;
    private const string StoneTextureId = "texture/courtyard-stone";
    private const string MortarTextureId = "texture/courtyard-mortar";
    private const string PlasterTextureId = "texture/courtyard-plaster";
    private const string GroundTextureId = "texture/courtyard-ground";
    private const string MossTextureId = "texture/courtyard-moss";
    private const string WoodTextureId = "texture/courtyard-wood";
    private const string StoneMaterialId = "material/courtyard-stone";
    private const string MortarMaterialId = "material/courtyard-mortar";
    private const string PlasterMaterialId = "material/courtyard-plaster";
    private const string GroundMaterialId = "material/courtyard-ground";
    private const string MossMaterialId = "material/courtyard-moss";
    private const string WoodMaterialId = "material/courtyard-wood";

    private readonly AuthoredCatalog catalog;
    private readonly Material[] materials;
    private readonly Material darkNeutral;
    private bool disposed;

    internal CourtyardMaterials(IEngineContext engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        AuthoredCatalog? admittedCatalog = null;
        Material[]? admittedMaterials = null;
        Material? createdDarkNeutral = null;
        try
        {
            RenderResourceInfo[] textures = OpenTextures(engine);
            admittedCatalog = engine.AuthoredContent.AdmitCatalogPayload(CreatePayload());
            ValidateCatalog(engine.AuthoredContent.ReadCatalog(admittedCatalog));
            admittedMaterials =
            [
                CreateAuthoredMaterial(engine, admittedCatalog, StoneMaterialId, textures[0]),
                CreateAuthoredMaterial(engine, admittedCatalog, MortarMaterialId, textures[1]),
                CreateAuthoredMaterial(engine, admittedCatalog, PlasterMaterialId, textures[2]),
                CreateAuthoredMaterial(engine, admittedCatalog, GroundMaterialId, textures[3]),
                CreateAuthoredMaterial(engine, admittedCatalog, MossMaterialId, textures[4]),
                CreateAuthoredMaterial(engine, admittedCatalog, WoodMaterialId, textures[5]),
            ];
            createdDarkNeutral = engine.Graphics.CreateMaterial(new MaterialRequest(
                new Color(0.12f, 0.11f, 0.10f, Alpha),
                default,
                Roughness,
                new Color(Alpha, Alpha, Alpha, Alpha),
                Vector3.Zero,
                NoEmission,
                false,
                MaterialAlphaMode.Opaque,
                0.5f));

            catalog = admittedCatalog;
            materials = admittedMaterials;
            darkNeutral = createdDarkNeutral;
        }
        catch
        {
            createdDarkNeutral?.Dispose();
            if (admittedMaterials is not null)
            {
                for (int index = admittedMaterials.Length - 1; index >= 0; index--)
                {
                    admittedMaterials[index].Dispose();
                }
            }

            admittedCatalog?.Dispose();
            throw;
        }
    }

    internal Material Stone => MaterialAt(0);

    internal Material Mortar => MaterialAt(1);

    internal Material Plaster => MaterialAt(2);

    internal Material Ground => MaterialAt(3);

    internal Material Moss => MaterialAt(4);

    internal Material Wood => MaterialAt(5);

    internal Material DarkNeutral
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return darkNeutral;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        darkNeutral.Dispose();
        for (int index = materials.Length - 1; index >= 0; index--)
        {
            materials[index].Dispose();
        }

        catalog.Dispose();
    }

    private static RenderResourceInfo[] OpenTextures(IEngineContext engine)
    {
        string[] paths = [StoneContentPath, MortarContentPath, PlasterContentPath, GroundContentPath, MossContentPath, WoodContentPath];
        RenderResourceInfo[] textures = new RenderResourceInfo[paths.Length];
        for (int index = 0; index < paths.Length; index++)
        {
            RenderResourceInfo texture = engine.Graphics.OpenResource(new RenderResourceRequest(paths[index], TextureFilter.Nearest, TextureWrap.Repeat));
            if (texture.Kind != RenderResourceKind.Texture || texture.ByteLength == 0 || texture.Handle.Value == 0)
            {
                throw new InvalidOperationException($"Courtyard texture '{paths[index]}' must open as a non-empty Engine texture resource.");
            }

            textures[index] = texture;
        }

        return textures;
    }

    private static Material CreateAuthoredMaterial(IEngineContext engine, AuthoredCatalog catalog, string materialId,
        RenderResourceInfo texture) => engine.Graphics.CreateAuthoredMaterial(
            new AuthoredMaterialAppearanceRequest(catalog, materialId, texture.Handle));

    private static AuthoredCatalogPayloadAdmitRequest CreatePayload() => new(
        Entries(),
        Dependencies(),
        Materials(),
        Textures(),
        Array.Empty<AuthoredVoxelAtlasInput>(),
        Array.Empty<AuthoredAtlasRegionInput>(),
        Array.Empty<AuthoredVoxelSurfaceInput>());

    private static AuthoredCatalogEntryInput[] Entries() =>
    [
        TextureEntry(StoneTextureId, StoneContentPath, "Courtyard stone grain"),
        TextureEntry(MortarTextureId, MortarContentPath, "Courtyard mortar grain"),
        TextureEntry(PlasterTextureId, PlasterContentPath, "Courtyard plaster grain"),
        TextureEntry(GroundTextureId, GroundContentPath, "Courtyard ground grain"),
        TextureEntry(MossTextureId, MossContentPath, "Courtyard moss grain"),
        TextureEntry(WoodTextureId, WoodContentPath, "Courtyard wood grain"),
        MaterialEntry(StoneMaterialId, "Courtyard stone"),
        MaterialEntry(MortarMaterialId, "Courtyard mortar"),
        MaterialEntry(PlasterMaterialId, "Courtyard plaster"),
        MaterialEntry(GroundMaterialId, "Courtyard ground"),
        MaterialEntry(MossMaterialId, "Courtyard moss"),
        MaterialEntry(WoodMaterialId, "Courtyard wood"),
    ];

    private static AuthoredCatalogEntryInput TextureEntry(string id, string path, string label) =>
        new(id, Version, true, TextureHash(id), true, path, true, label);

    private static AuthoredCatalogEntryInput MaterialEntry(string id, string label) =>
        new(id, Version, false, string.Empty, false, string.Empty, true, label);

    private static AuthoredCatalogDependencyInput[] Dependencies() =>
    [
        Dependency(StoneMaterialId, StoneTextureId),
        Dependency(MortarMaterialId, MortarTextureId),
        Dependency(PlasterMaterialId, PlasterTextureId),
        Dependency(GroundMaterialId, GroundTextureId),
        Dependency(MossMaterialId, MossTextureId),
        Dependency(WoodMaterialId, WoodTextureId),
    ];

    private static AuthoredCatalogDependencyInput Dependency(string owner, string texture) =>
        new(owner, texture, AssetVersionRequirementKind.Exact, Version, true, TextureHash(texture));

    private static AuthoredMaterialInput[] Materials() =>
    [
        Material(StoneMaterialId, StoneTextureId),
        Material(MortarMaterialId, MortarTextureId),
        Material(PlasterMaterialId, PlasterTextureId),
        Material(GroundMaterialId, GroundTextureId),
        Material(MossMaterialId, MossTextureId),
        Material(WoodMaterialId, WoodTextureId),
    ];

    private static AuthoredMaterialInput Material(string id, string texture) => new(
        id,
        true,
        true,
        true,
        AuthoredStructuralClass.Solid,
        new Color(Alpha, Alpha, Alpha, Alpha),
        true,
        texture,
        AssetVersionRequirementKind.Exact,
        Version,
        true,
        TextureHash(texture),
        Roughness,
        new Color(Alpha, Alpha, Alpha, Alpha),
        new Color(NoEmission, NoEmission, NoEmission, Alpha),
        NoEmission,
        AuthoredUvStrategy.Planar);

    private static AuthoredTextureInput[] Textures() =>
    [
        Texture(StoneTextureId),
        Texture(MortarTextureId),
        Texture(PlasterTextureId),
        Texture(GroundTextureId),
        Texture(MossTextureId),
        Texture(WoodTextureId),
    ];

    private static AuthoredTextureInput Texture(string id) => new(
        id, TexturePixels, TexturePixels, AuthoredTextureFilter.Nearest, AuthoredTextureWrap.Repeat);

    private static string TextureHash(string textureId) => textureId switch
    {
        StoneTextureId => "848fa498a979486b1628a1fafe4ab2d29388fe003d41e797480bdb20c695e549",
        MortarTextureId => "109bf8a26bf7ff94c5d41015c58de02f0e6df2c8b4941caf95a03e2a905c3e1b",
        PlasterTextureId => "b6e052c1ffe3bed26894a14813d0b15a98a1faf98063fc27298682dbbf342373",
        GroundTextureId => "f9b33f25140f50b2e644e5591e5932d45e173220995e60cb78f9bb1864d5c5c0",
        MossTextureId => "e61d6339f5a74ec369334f2fe29d5ff5dc20fcf5a68429929141af5075319bcf",
        WoodTextureId => "231772a462efd32626bb41e322bb5aea81d35ec26267c7e2814cba8afbb18052",
        _ => throw new ArgumentOutOfRangeException(nameof(textureId), textureId, "Unknown courtyard texture."),
    };

    private static void ValidateCatalog(AuthoredCatalogReadoutLeaseReceipt readout)
    {
        if (readout.Entries.Length != 12 || readout.Materials.Length != 6 || readout.Textures.Length != 6
            || readout.VoxelAtlases.Length != 0 || readout.AtlasRegions.Length != 0 || readout.VoxelSurfaces.Length != 0
            || string.IsNullOrWhiteSpace(readout.CanonicalHash))
        {
            throw new InvalidOperationException("Engine did not retain the complete Courtyard material catalog.");
        }
    }

    private Material MaterialAt(int index)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return materials[index];
    }
}
