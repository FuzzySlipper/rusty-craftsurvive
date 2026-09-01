using Rusty.Engine;

namespace CraftSurvive.Game.Modules.GhostPlate;

/// <summary>
/// Owns one ordinary retained source appearance and its Engine ghost plate.
/// This type publishes product facts; renderer resources and directional
/// selection remain entirely inside the Engine presentation service.
/// </summary>
internal sealed class GhostPlateActor : IDisposable
{
    private readonly IEngineContext engine;
    private readonly GhostPlateConfiguration configuration;
    private readonly Appearance sourceAppearance;
    private GhostPlatePresentation? presentation;
    private GhostPlatePlacement placement;
    private bool started;
    private bool disposed;

    internal GhostPlateActor(IEngineContext engine, GhostPlateConfiguration configuration)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.configuration = configuration;

        RenderResourceHandle sourceResource = engine.Animation.OpenAnimatedMesh(
            new AnimatedMeshResourceRequest(configuration.SourceContentPath));
        sourceAppearance = engine.Animation.CreateAnimatedMeshAppearance(
            new AnimatedMeshAppearanceRequest(sourceResource));
        placement = configuration.Placement;
    }

    internal ulong SourceObjectId => configuration.SourceObjectId;

    internal GhostPlatePlacement Placement => placement;

    /// <summary>
    /// The hidden source is still part of the complete retained Appearance
    /// snapshot so the Engine can capture it by product object identity.
    /// </summary>
    internal AppearanceFact SourceAppearanceFact => new(
        configuration.SourceObjectId,
        placement.Transform,
        sourceAppearance,
        Visible: false,
        RenderLayer.Scene);

    internal void Start()
    {
        EnsureNotDisposed();
        if (started)
        {
            return;
        }

        presentation = engine.Presentation.CreateGhostPlate(
            new CreateGhostPlatePresentationRequest(
                configuration.SourceObjectId,
                placement,
                configuration.Capture,
                configuration.Config));
        started = true;
    }

    /// <summary>
    /// Applies a changed product placement through the named Engine service.
    /// The normal static configuration produces no redundant capture-bank
    /// replacement on every update.
    /// </summary>
    internal void Update(GhostPlatePlacement nextPlacement)
    {
        EnsureStarted();
        if (nextPlacement == placement)
        {
            return;
        }

        engine.Presentation.UpdateGhostPlate(new UpdateGhostPlatePresentationRequest(
            Presentation,
            nextPlacement,
            configuration.Config));
        placement = nextPlacement;
    }

    /// <summary>Explicit product recapture policy used by a product restart.</summary>
    internal void Recapture()
    {
        EnsureStarted();
        engine.Presentation.RecaptureGhostPlate(new RecaptureGhostPlatePresentationRequest(
            Presentation,
            configuration.Capture));
    }

    /// <summary>
    /// Attachment is deliberately side-effect free. Publishing the complete
    /// source snapshot lets the Engine rebase its retained ghost projector;
    /// this owner never recreates capture or emulates attachment in C#.
    /// </summary>
    internal void Attach()
    {
        EnsureStarted();
    }

    internal GhostPlatePresentationReadout Readout()
    {
        EnsureStarted();
        return engine.Presentation.ReadGhostPlate(Presentation);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        DisposePresentation();
        DisposeSourceAppearance();
    }

    /// <summary>
    /// Releases the retained renderer owner while its source is still present.
    /// The product root may then publish its next complete Appearance snapshot.
    /// </summary>
    internal void DisposePresentation()
    {
        if (disposed)
        {
            return;
        }

        presentation?.Dispose();
        presentation = null;
        started = false;
    }

    /// <summary>
    /// Releases the ordinary source only after the product root has removed it
    /// from any live complete Appearance snapshot.
    /// </summary>
    internal void DisposeSourceAppearance()
    {
        if (disposed)
        {
            return;
        }
        if (presentation is not null)
        {
            throw new InvalidOperationException(
                "Dispose the ghost plate presentation before its source appearance.");
        }

        sourceAppearance.Dispose();
        disposed = true;
    }

    private GhostPlatePresentation Presentation => presentation
        ?? throw new InvalidOperationException("CraftSurvive ghost plate is unavailable.");

    private void EnsureStarted()
    {
        EnsureNotDisposed();
        if (!started)
        {
            throw new InvalidOperationException("CraftSurvive ghost plate has not started.");
        }
    }

    private void EnsureNotDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(GhostPlateActor));
        }
    }
}
