using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// The near water's look: a flowing surface displaced by the sea state's waves (#9902), with keyword
/// variants of the same shader to compare - flat flowing water, the current as colour, and the Engine's
/// water alone. Each update hands the shader the sea state (<see cref="Follow"/>): its wind and the
/// trains' phases, which the product's clock drives, so what rides the water reads the same surface.
/// </summary>
internal sealed class WaterVisualMaterial : IDisposable
{
    private const string ShaderPath = "shaders/water-flow.wgsl";
    private const float OriginalRoughness = 0.1f, FlowRoughness = 0.18f, ShallowOpacity = 0.48f;
    private const float FlowShorelineWidth = 0.14f, FlowFoamThreshold = 0.75f;
    private static readonly Color White = new(1, 1, 1, 1);

    /// <summary>The comparison modes, each a keyword variant of the one shader (none for the waves).</summary>
    private static readonly (string Name, string Keyword)[] Modes = [("waves", ""), ("flow", "FLAT"), ("velocity", "VELOCITY"), ("baseline", "BASELINE")];

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly RenderResource foam, ripples;
    private RenderResource shader;
    private int mode;
    private Vector4 waveBounds, wind, phasesLow, phasesHigh;

    internal WaterVisualMaterial(IEngineContext engine, WorldFrame frame, Vector4 waveBounds)
    {
        this.engine = engine;
        this.frame = frame;
        this.waveBounds = waveBounds;
        foam = TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterFoamContentPath);
        ripples = TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterRipplesContentPath);
        shader = Open(mode);
        Material = engine.Graphics.CreateMaterial(Request());
    }

    internal Material Material { get; }
    internal string Mode => Modes[mode].Name;

    /// <summary>Whether the drawn water waves: the comparisons without waves leave it at rest.</summary>
    internal bool Displaces => Modes[mode].Keyword.Length == 0;

    internal string SetMode(string value)
    {
        int selected = Array.FindIndex(Modes, m => m.Name == value.ToLowerInvariant() || (value == "gerstner" && m.Name == "waves"));
        if (selected < 0) return "Choose waves, flow, velocity or baseline.";
        if (selected != mode)
        {
            RenderResource old = shader;
            shader = Open(selected);
            mode = selected;
            Refresh();
            old.Dispose();
        }

        return $"waterVisual={Mode}";
    }

    internal void SetBounds(Vector4 bounds)
    {
        if (bounds == waveBounds) return;
        waveBounds = bounds;
        Refresh();
    }

    /// <summary>Hands the shader the sea state now: its wind and the trains' phases, measured from the frame's origin.</summary>
    internal void Follow(SeaState sea)
    {
        (long ox, _, long oz) = frame.Origin;
        wind = new((float)ox, (float)oz, (float)(sea.Wind.X * sea.WindSpeed), (float)(sea.Wind.Y * sea.WindSpeed));
        phasesLow = sea.ShaderPhases(0, ox, oz);
        phasesHigh = sea.ShaderPhases(4, ox, oz);
        Refresh();
    }

    private RenderResource Open(int index) => engine.Graphics.OpenResource(Modes[index].Keyword.Length == 0
        ? new RenderResourceRequest(ShaderPath)
        : new RenderResourceRequest(ShaderPath) { ShaderKeywords = Modes[index].Keyword }).Handle;

    private bool Baseline => Modes[mode].Keyword == "BASELINE";

    private MaterialRequest Request() => new(White with { A = Baseline ? 1 : ShallowOpacity },
        default(RenderResourceReference), Baseline ? OriginalRoughness : FlowRoughness, White, Vector3.Zero, 0, false)
    {
        AlphaMode = MaterialAlphaMode.Blend,
        Water = Baseline ? TerrainAtlasCatalog.WaterLook(foam, ripples)
            : TerrainAtlasCatalog.WaterLook(foam, ripples) with
            {
                ShorelineWidth = FlowShorelineWidth,
                FoamThreshold = FlowFoamThreshold,
            },
        Shader = new MaterialShader(shader, wind, phasesLow, phasesHigh, waveBounds),
    };

    private void Refresh() => engine.Graphics.UpdateMaterial(new MaterialUpdateRequest(Material, Request()));

    public void Dispose()
    {
        Material.Dispose();
        shader.Dispose();
        ripples.Dispose();
        foam.Dispose();
    }
}
