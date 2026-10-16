using Rusty.Engine;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Debugging;

/// <summary>Live-debug adapter over the renderer settings the game chooses: vegetation occlusion, for comparisons (#10033).</summary>
public sealed class RendererDebugModule : IDebugCommandModule
{
    private readonly IEngineContext engine;

    internal RendererDebugModule(IEngineContext engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    [DebugCommand("craft.renderer.occlusion", Description = "Assisted (#10033): turns the renderer's vegetation occlusion on or off for the session (the game's default is on), for comparisons, and reads it back.")]
    public string Occlusion(string state)
    {
        bool? wanted = state switch { "on" => true, "off" => false, "read" => null, _ => throw new ArgumentException($"Expected on, off or read, not {state}.", nameof(state)) };
        if (wanted is bool on) engine.RendererSettings.Set(engine.RendererSettings.Read().Requested with { OcclusionCulling = on });
        return $"occlusionCulling={engine.RendererSettings.Read().Requested.OcclusionCulling}";
    }
}
