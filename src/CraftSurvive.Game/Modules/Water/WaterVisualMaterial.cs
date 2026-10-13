using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>CraftSurvive's experimental surface look, including a live comparison with the original.</summary>
internal sealed class WaterVisualMaterial : IDisposable
{
    private const string ShaderPath = "shaders/water-flow.wgsl";
    private const float OriginalRoughness = 0.1f, FlowRoughness = 0.18f, ShallowOpacity = 0.48f;
    private const float RippleStrength = 0.24f, FoamStrength = 0.45f, FlowCycleSeconds = 5;
    private const float FlowShorelineWidth = 0.14f, FlowFoamThreshold = 0.75f;
    private static readonly Color White = new(1, 1, 1, 1);
    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly RenderResource foam, ripples, shader;
    private int mode = 3;
    private float amplitude = WaterWaveSettings.DefaultAmplitude;
    private float choppiness = WaterWaveSettings.DefaultChoppiness;
    private Vector4 waveBounds;

    internal WaterVisualMaterial(IEngineContext engine, WorldFrame frame, Vector4 waveBounds)
    {
        this.engine = engine;
        this.frame = frame;
        this.waveBounds = waveBounds;
        foam = TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterFoamContentPath);
        ripples = TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterRipplesContentPath);
        shader = engine.Graphics.OpenResource(new RenderResourceRequest(ShaderPath)).Handle;
        Material = engine.Graphics.CreateMaterial(Request());
        frame.Rebased += Rebased;
    }

    internal Material Material { get; }
    internal string Mode => mode switch { 0 => "baseline", 1 => "flow", 2 => "velocity", _ => "waves" };
    internal string WaveReadout => FormattableString.Invariant($"waveAmplitude={amplitude:F2}m choppiness={choppiness:F2} meshSpacing={WaterWaveSettings.MeshSpacingMetres}m");

    internal string SetMode(string value)
    {
        int selected = value.ToLowerInvariant() switch { "baseline" => 0, "flow" => 1, "velocity" => 2, "waves" or "gerstner" => 3, _ => -1 };
        if (selected < 0) return "Choose baseline, flow, waves or velocity.";
        mode = selected;
        Refresh();
        return $"waterVisual={Mode} {WaveReadout}";
    }

    internal string SetWaves(float amplitudeMetres, float horizontalScale)
    {
        // The installed hook culls with the original mesh bounds. Keep this study's displacement
        // below a metre, rather than offering storm-sized waves whose bounds the SDK cannot name.
        amplitude = Math.Clamp(amplitudeMetres, 0, WaterWaveSettings.MaximumAmplitude);
        choppiness = Math.Clamp(horizontalScale, 0, WaterWaveSettings.MaximumChoppiness);
        mode = 3;
        Refresh();
        return $"waterVisual={Mode} {WaveReadout}";
    }

    internal void SetBounds(Vector4 bounds)
    {
        if (bounds == waveBounds) return;
        waveBounds = bounds;
        Refresh();
    }

    private MaterialRequest Request() => new(White with { A = mode == 0 ? 1 : ShallowOpacity },
        default(RenderResourceReference), mode == 0 ? OriginalRoughness : FlowRoughness, White, Vector3.Zero, 0, false)
    {
        AlphaMode = MaterialAlphaMode.Blend,
        Water = mode == 0 ? TerrainAtlasCatalog.WaterLook(foam, ripples)
            : TerrainAtlasCatalog.WaterLook(foam, ripples) with
            {
                ShorelineWidth = FlowShorelineWidth,
                FoamThreshold = FlowFoamThreshold,
            },
        Shader = new MaterialShader(shader, new Vector4(mode, RippleStrength, FoamStrength, FlowCycleSeconds),
            new Vector4(frame.Origin.X, frame.Origin.Z, 0, 0),
            new Vector4(amplitude, choppiness, WaterWaveSettings.EdgeFadeMetres, 0), waveBounds),
    };

    private void Rebased(Vector3 translation) => Refresh();
    private void Refresh() => engine.Graphics.UpdateMaterial(new MaterialUpdateRequest(Material, Request()));

    public void Dispose()
    {
        frame.Rebased -= Rebased;
        Material.Dispose();
        shader.Dispose();
        ripples.Dispose();
        foam.Dispose();
    }
}
