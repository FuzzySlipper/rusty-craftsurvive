using System.Text.Json;
using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>Independent texture/projection controls and the Engine's bounded transition controls.</summary>
internal readonly record struct TerrainBlendSettings(uint TransitionCells, float Contrast,
    float TextureScale, float ProjectionSharpnessScale)
{
    internal const string ContentPath = "textures/terrain-studies/blending.json";
    private const uint MinimumTransitionCells = 1, MaximumTransitionCells = 4;
    private const float MinimumContrast = 1;

    internal static TerrainBlendSettings Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        TerrainBlendSettings settings = new(root.GetProperty("transitionCells").GetUInt32(),
            root.GetProperty("weightContrast").GetSingle(), root.GetProperty("textureScale").GetSingle(),
            root.GetProperty("projectionSharpnessScale").GetSingle());
        if (settings.TransitionCells is < MinimumTransitionCells or > MaximumTransitionCells
            || !float.IsFinite(settings.Contrast) || settings.Contrast < MinimumContrast
            || !float.IsFinite(settings.TextureScale) || settings.TextureScale <= 0
            || !float.IsFinite(settings.ProjectionSharpnessScale) || settings.ProjectionSharpnessScale <= 0)
            throw new InvalidDataException("Terrain blending needs 1–4 transition cells, contrast >= 1, and positive finite texture/projection scales.");
        return settings;
    }
}

/// <summary>Physical ground identities stay distinct; Engine interpolates their four visual layers.</summary>
internal static class TerrainLayers
{
    internal static readonly string[] Names = ["sage-ground", "ochre-rock", "dune-sand", "frost-stone"];
    private static readonly uint[] Slots = BlockRegistry.MaterialBlocks
        .Where(block => Layer(block.Id) >= 0).Select(block => (uint)block.Id).ToArray();
    private static readonly uint[] Layers = Slots.Select(slot => (uint)Layer((BlockId)slot)).ToArray();

    internal static int Layer(BlockId block) => block switch
    {
        BlockId.Grass or BlockId.Dirt => 0,
        BlockId.Stone => 1,
        BlockId.Sand => 2,
        BlockId.Snow or BlockId.Gravel => 3,
        _ => -1,
    };

    internal static void Configure(IEngineContext engine, SpatialSession session, uint transitionCells) =>
        engine.Voxel.ConfigureTerrainLayers(new VoxelTerrainLayerRequest(session, Slots, transitionCells, Layers));
}
