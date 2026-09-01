using System.Globalization;
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
    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly MicrovoxelConfiguration configuration;
    private readonly Dictionary<uint, Material> materials = [];
    private readonly Dictionary<uint, Color> paletteColors = [];
    private MicrovoxelPresentationPreset desired;
    private MicrovoxelPresentationPreset applied;
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
        desired = configuration.AcceptedPreset;
        applied = desired;
    }

    internal string SelectedObjectAssetId => configuration.AssetId;

    internal string SelectedSourceContentPath => configuration.ContentPath;

    internal string SelectedPreset => desired.Name;

    internal MicrovoxelPresentationPreset Desired => desired;

    internal PresentationReadout EnginePresentation => engine.Appearance.ReadPresentation();

    /// <summary>
    /// Replaces the product's desired named look. The retained Engine
    /// presentation is changed by the next ordinary <see cref="Update"/>.
    /// </summary>
    internal bool TrySetPreset(string name, out string canonicalName)
    {
        if (!MicrovoxelConfiguration.TryGetPreset(name, out canonicalName, out MicrovoxelPresentationPreset preset))
        {
            return false;
        }

        desired = preset;
        presentationUpdatePending = true;
        return true;
    }

    internal void SetPlacement(Vector3 placement)
    {
        desired = desired with { Placement = placement };
        presentationUpdatePending = true;
    }

    internal void SetScale(Vector3 scale)
    {
        desired = desired with { Scale = scale };
        presentationUpdatePending = true;
    }

    internal void SetMaterial(MicrovoxelMaterialSettings material)
    {
        desired = desired with { Material = material };
        presentationUpdatePending = true;
    }

    internal void SetRoughness(float roughness)
    {
        SetMaterial(desired.Material with { Roughness = roughness });
    }

    internal void SetTextureTint(Color textureTint)
    {
        SetMaterial(desired.Material with { TextureTint = textureTint });
    }

    internal void SetEmission(Vector3 color, float intensity)
    {
        SetMaterial(desired.Material with
        {
            EmissionColor = color,
            EmissionIntensity = intensity,
        });
    }

    internal void SetDoubleSided(bool doubleSided)
    {
        SetMaterial(desired.Material with { DoubleSided = doubleSided });
    }

    internal void SetVisible(bool visible)
    {
        desired = desired with { Visible = visible };
        presentationUpdatePending = true;
    }

    /// <summary>Queues a named look for application by the normal update.</summary>
    internal string QueuePreset(string name)
    {
        EnsureStarted();
        if (!TrySetPreset(name, out _))
        {
            throw new ArgumentException(
                "Microvoxel preset must be accepted, close, or compact.",
                nameof(name));
        }

        return PendingSummary();
    }

    /// <summary>Queues translation while retaining the selected object's scale.</summary>
    internal string QueuePlacement(float x, float y, float z)
    {
        EnsureStarted();
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new ArgumentException("Microvoxel placement coordinates must be finite.");
        }
        SetPlacement(new Vector3(x, y, z));
        return PendingSummary();
    }

    /// <summary>Queues an object scale through the retained Engine presentation.</summary>
    internal string QueueScale(float x, float y, float z)
    {
        EnsureStarted();
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)
            || x <= 0f || y <= 0f || z <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(x), "Microvoxel scale values must be finite and positive.");
        }
        SetScale(new Vector3(x, y, z));
        return PendingSummary();
    }

    /// <summary>Queues the product's matte roughness value.</summary>
    internal string QueueMaterial(float roughness)
    {
        EnsureStarted();
        if (!float.IsFinite(roughness) || roughness is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(roughness), "Microvoxel roughness must be between 0 and 1.");
        }
        SetRoughness(roughness);
        return PendingSummary();
    }

    /// <summary>Queues retained-presentation visibility; Update performs the Engine call.</summary>
    internal string QueueVisibility(bool visible)
    {
        EnsureStarted();
        SetVisible(visible);
        return PendingSummary();
    }

    /// <summary>
    /// Returns typed product selection, desired values, and the current Engine
    /// aggregate readout for a live debug command.
    /// </summary>
    internal MicrovoxelPresentationReadout Readout()
    {
        PresentationReadout enginePresentation = started
            ? EnginePresentation
            : default;
        return new MicrovoxelPresentationReadout(
            SelectedObjectAssetId,
            SelectedSourceContentPath,
            SelectedPreset,
            started,
            presentationUpdatePending,
            desired.Placement,
            desired.Scale,
            desired.Material,
            desired.Visible,
            enginePresentation);
    }

    /// <summary>
    /// Formats the compact live command readout without creating a second
    /// product state or renderer observation path.
    /// </summary>
    internal string DebugReadout()
    {
        MicrovoxelPresentationReadout readout = Readout();
        PresentationReadout engineReadout = readout.EnginePresentation;
        MicrovoxelMaterialSettings material = readout.Material;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"source={readout.SelectedSourceContentPath};object={readout.SelectedObjectAssetId};preset={readout.SelectedPreset};started={readout.Started};pending={readout.UpdatePending};visible={readout.Visible};placement={readout.Placement.X:F2},{readout.Placement.Y:F2},{readout.Placement.Z:F2};scale={readout.Scale.X:F2},{readout.Scale.Y:F2},{readout.Scale.Z:F2};roughness={material.Roughness:F3};textureTint={material.TextureTint.R:F2},{material.TextureTint.G:F2},{material.TextureTint.B:F2},{material.TextureTint.A:F2};emission={material.EmissionColor.X:F2},{material.EmissionColor.Y:F2},{material.EmissionColor.Z:F2}:{material.EmissionIntensity:F2};doubleSided={material.DoubleSided};engine={engineReadout.RetainedObjectCount}/{engineReadout.AppearanceCount}/{engineReadout.MaterialCount}/{engineReadout.ResourceCount}");
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
                desired.Transform,
                desired.Visible,
                MaterialBindings()));
            applied = desired;
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

        ApplyMaterialSettings();
        PublishPresentation();
        applied = desired;
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
        ApplyMaterialSettings();
        PublishPresentation();
        applied = desired;
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
        paletteColors.Clear();
        voxelObject?.Dispose();
        voxelObject = null;
        runtimeFrame = 0U;
        presentationUpdatePending = false;
        applied = desired;
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

            Color paletteColor = ToColor(row);
            paletteColors.Add(row.MaterialSlot, paletteColor);
            materials.Add(row.MaterialSlot, engine.Appearance.CreateMaterial(MaterialRequestFor(paletteColor)));
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

    private MaterialRequest MaterialRequestFor(Color paletteColor) => new(
        paletteColor,
        default,
        desired.Material.Roughness,
        desired.Material.TextureTint,
        desired.Material.EmissionColor,
        desired.Material.EmissionIntensity,
        desired.Material.DoubleSided);

    private void ApplyMaterialSettings()
    {
        if (desired.Material == applied.Material)
        {
            return;
        }

        foreach ((uint materialSlot, Material material) in materials)
        {
            engine.Appearance.UpdateMaterial(new MaterialUpdateRequest(
                material,
                MaterialRequestFor(paletteColors[materialSlot])));
        }
    }

    private void PublishPresentation() => engine.VoxelContent.UpdateObjectPresentation(
        new UpdateVoxelObjectPresentationRequest(
            Presentation,
            runtimeFrame,
            desired.Transform,
            desired.Visible,
            MaterialBindings()));

    private void EnsureStarted()
    {
        if (!started)
        {
            throw new InvalidOperationException("Microvoxel presentation has not started.");
        }
    }

    private string PendingSummary()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"queued source={SelectedSourceContentPath};object={SelectedObjectAssetId};preset={SelectedPreset};visible={desired.Visible};pending={presentationUpdatePending}");
}
