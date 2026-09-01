using System.Numerics;
using System.Text;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Microvoxels;

/// <summary>
/// Owns one product-selected MagicaVoxel object's content admission, palette
/// materials, and retained Engine presentation. Geometry and lighting remain
/// Engine-owned; this module only selects, tunes, and places the object.
/// </summary>
internal sealed class MicrovoxelPresentation : IDisposable
{
    private static readonly Color OpaqueWhite = new(1f, 1f, 1f, 1f);
    private static readonly Vector3 NoEmission = Vector3.Zero;

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly MicrovoxelConfiguration configuration;
    private readonly Dictionary<uint, Material> materials = [];
    private VoxelObject? voxelObject;
    private VoxelObjectPresentation? presentation;
    private uint runtimeFrame;
    private bool presentationUpdatePending;
    private bool started;

    internal MicrovoxelPresentation(
        IEngineContext engine,
        ProductContent content,
        MicrovoxelConfiguration configuration)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.configuration = configuration;
    }

    internal void Start()
    {
        if (started)
        {
            return;
        }

        try
        {
            ProductContentFile source = SelectContentFile(configuration.ContentPath);
            voxelObject = engine.VoxelContent.AdmitMagicaVoxelObject(new AdmitMagicaVoxelObjectRequest(
                source.Bytes,
                configuration.AssetId,
                configuration.ContentPath,
                configuration.CellSize,
                configuration.PivotPolicy,
                0d,
                0d,
                0d,
                MagicaVoxelOrientation.XRightYUpNegativeZForward,
                configuration.MaximumSourceBytes,
                configuration.MaximumDimension,
                configuration.MaximumVoxelCount,
                configuration.MaximumSourceChunks,
                configuration.MaximumMaterialSlots));
            CreatePaletteMaterials(engine.VoxelContent.ReadMagicaVoxelPalette(voxelObject).Palette.Span);
            runtimeFrame = engine.VoxelContent.SelectDefaultObjectFrame(voxelObject).RuntimeFrame;
            presentation = engine.VoxelContent.ProjectObject(new ProjectVoxelObjectRequest(
                voxelObject,
                runtimeFrame,
                configuration.PresentationTransform,
                Visible: true,
                MaterialBindings()));
            presentationUpdatePending = true;
            started = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// Commits a retained-presentation refresh through the generated service
    /// after its initial projection or an explicit product restart.
    /// </summary>
    internal void Update()
    {
        EnsureStarted();
        if (!presentationUpdatePending)
        {
            return;
        }

        PublishPresentation();
        presentationUpdatePending = false;
    }

    internal void Restart()
    {
        EnsureStarted();
        presentationUpdatePending = true;
    }

    /// <summary>Republishes this retained object for a fresh Engine presentation attachment.</summary>
    internal void Attach()
    {
        EnsureStarted();
        PublishPresentation();
        presentationUpdatePending = false;
    }

    public void Dispose()
    {
        presentation?.Dispose();
        presentation = null;
        foreach (Material material in materials.Values)
        {
            material.Dispose();
        }

        materials.Clear();
        voxelObject?.Dispose();
        voxelObject = null;
        runtimeFrame = 0U;
        presentationUpdatePending = false;
        started = false;
    }

    private VoxelObjectPresentation Presentation => presentation
        ?? throw new InvalidOperationException("Microvoxel presentation is unavailable.");

    private ProductContentFile SelectContentFile(string expectedPath)
    {
        foreach (ProductContentFile candidate in content.Files.Span)
        {
            if (string.Equals(Encoding.UTF8.GetString(candidate.Path.Span), expectedPath,
                    StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"CraftSurvive product content does not include the selected microvoxel source `{expectedPath}`.");
    }

    private void CreatePaletteMaterials(ReadOnlySpan<MagicaVoxelPaletteRow> palette)
    {
        if (palette.IsEmpty)
        {
            throw new InvalidOperationException("The selected MagicaVoxel source has no admitted palette rows.");
        }

        foreach (MagicaVoxelPaletteRow row in palette)
        {
            if (materials.ContainsKey(row.MaterialSlot))
            {
                throw new InvalidOperationException(
                    $"The selected MagicaVoxel source repeated palette material slot {row.MaterialSlot}.");
            }

            materials.Add(row.MaterialSlot, engine.Appearance.CreateMaterial(new MaterialRequest(
                ToColor(row),
                default,
                configuration.Roughness,
                OpaqueWhite,
                NoEmission,
                0f,
                DoubleSided: false)));
        }
    }

    private ReadOnlyMemory<VoxelObjectMaterialBinding> MaterialBindings() => materials
        .OrderBy(static pair => pair.Key)
        .Select(static pair => new VoxelObjectMaterialBinding(pair.Key, pair.Value))
        .ToArray();

    private static Color ToColor(MagicaVoxelPaletteRow row) => new(
        row.Red / 255f,
        row.Green / 255f,
        row.Blue / 255f,
        row.Alpha / 255f);

    private void PublishPresentation() => engine.VoxelContent.UpdateObjectPresentation(
        new UpdateVoxelObjectPresentationRequest(
            Presentation,
            runtimeFrame,
            configuration.PresentationTransform,
            Visible: true,
            MaterialBindings()));

    private void EnsureStarted()
    {
        if (!started)
        {
            throw new InvalidOperationException("Microvoxel presentation has not started.");
        }
    }
}
