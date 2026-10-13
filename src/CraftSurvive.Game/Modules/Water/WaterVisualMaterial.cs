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
    private static readonly Color White = new(1, 1, 1, 1);
    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly RenderResource foam, ripples, shader;
    private int mode = 1;

    internal WaterVisualMaterial(IEngineContext engine, WorldFrame frame)
    {
        this.engine = engine;
        this.frame = frame;
        foam = TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterFoamContentPath);
        ripples = TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterRipplesContentPath);
        shader = engine.Graphics.OpenResource(new RenderResourceRequest(ShaderPath)).Handle;
        Material = engine.Graphics.CreateMaterial(Request());
        frame.Rebased += Rebased;
    }

    internal Material Material { get; }
    internal string Mode => mode switch { 0 => "baseline", 2 => "velocity", _ => "flow" };

    internal string SetMode(string value)
    {
        int selected = value.ToLowerInvariant() switch { "baseline" => 0, "flow" => 1, "velocity" => 2, _ => -1 };
        if (selected < 0) return "Choose baseline, flow or velocity.";
        mode = selected;
        Refresh();
        return $"waterVisual={Mode}";
    }

    private MaterialRequest Request() => new(White with { A = mode == 0 ? 1 : ShallowOpacity },
        default(RenderResourceReference), mode == 0 ? OriginalRoughness : FlowRoughness, White, Vector3.Zero, 0, false)
    {
        AlphaMode = MaterialAlphaMode.Blend,
        Water = TerrainAtlasCatalog.WaterLook(foam, ripples),
        Shader = new MaterialShader(shader, new Vector4(mode, RippleStrength, FoamStrength, FlowCycleSeconds),
            new Vector4(frame.Origin.X, frame.Origin.Z, 0, 0), Vector4.Zero, Vector4.Zero),
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
