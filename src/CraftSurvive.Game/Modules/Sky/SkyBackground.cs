using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Sky;

/// <summary>
/// Owns CraftSurvive's authored sky selection. The Engine admits, retains,
/// realizes, and clears the renderer resource; this domain only republishes
/// the selected copied resource handle across product lifecycle boundaries.
/// </summary>
internal sealed class SkyBackground : IDisposable
{
    internal const string SkyPanoramaContentPath = "textures/sky-panorama.png";

    private readonly IEngineContext engine;
    private readonly RenderResourceInfo resource;
    private bool published;

    internal SkyBackground(IEngineContext engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        resource = engine.Graphics.OpenResource(new RenderResourceRequest(SkyPanoramaContentPath));

        if (resource.Kind != RenderResourceKind.Texture)
        {
            throw new InvalidOperationException(
                $"CraftSurvive sky resource '{SkyPanoramaContentPath}' must be an Engine texture.");
        }

        if (resource.ByteLength == 0)
        {
            throw new InvalidOperationException(
                $"CraftSurvive sky resource '{SkyPanoramaContentPath}' must not be empty.");
        }
    }

    internal void Start()
    {
        Publish();
    }

    /// <summary>Republishes the retained Engine background for a fresh host attachment.</summary>
    internal void Attach()
    {
        EnsurePublished();
        Publish();
    }

    /// <summary>Retains the selected authored sky through the product restart policy.</summary>
    internal void Restart()
    {
        EnsurePublished();
        Publish();
    }

    /// <summary>
    /// Clears the Engine-owned presentation before dependent product teardown.
    /// Render resources have no product-side disposal API.
    /// </summary>
    public void Dispose()
    {
        if (!published)
        {
            return;
        }

        engine.CameraView.ClearSkyBackground(new ClearSkyBackgroundRequest(0U));
        published = false;
    }

    private void Publish()
    {
        engine.CameraView.SetSkyBackground(resource.Handle);
        published = true;
    }

    private void EnsurePublished()
    {
        if (!published)
        {
            throw new InvalidOperationException("CraftSurvive sky background has not started.");
        }
    }
}
