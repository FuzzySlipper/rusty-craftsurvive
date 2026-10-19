using Rusty.Engine;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Debugging;

/// <summary>Live-debug adapter over the renderer settings the game chooses: vegetation occlusion, for comparisons (#10033).</summary>
public sealed class RendererDebugModule : IDebugCommandModule
{
    private readonly IEngineContext engine;

    internal RendererDebugModule(IEngineContext engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    [DebugCommand("craft.renderer.ambientocclusion", Description = "Assisted (#10094): chooses the renderer's ambient occlusion for the session - disabled, screen or field (distance fields through voxel chunks) - and reads it back.")]
    public string AmbientOcclusion(string mode)
    {
        AmbientOcclusionMode? wanted = mode switch
        {
            "disabled" => AmbientOcclusionMode.Disabled,
            "screen" => AmbientOcclusionMode.ScreenSpace,
            "field" => AmbientOcclusionMode.DistanceField,
            "read" => null,
            _ => throw new ArgumentException($"Expected disabled, screen, field or read, not {mode}.", nameof(mode)),
        };
        if (wanted is AmbientOcclusionMode next) engine.RendererSettings.Set(engine.RendererSettings.Read().Requested with { AmbientOcclusion = next });
        return $"ambientOcclusion={engine.RendererSettings.Read().Requested.AmbientOcclusion}";
    }

    [DebugCommand("craft.renderer.occlusion", Description = "Assisted (#10033): turns the renderer's vegetation occlusion on or off for the session (the game's default is on), for comparisons, and reads it back.")]
    public string Occlusion(string state)
    {
        bool? wanted = state switch { "on" => true, "off" => false, "read" => null, _ => throw new ArgumentException($"Expected on, off or read, not {state}.", nameof(state)) };
        if (wanted is bool on) engine.RendererSettings.Set(engine.RendererSettings.Read().Requested with { OcclusionCulling = on });
        return $"occlusionCulling={engine.RendererSettings.Read().Requested.OcclusionCulling}";
    }
}
