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

/// <summary>Physical ground identities stay distinct; Engine interpolates their visual layers (four a chunk at most).</summary>
internal static class TerrainLayers
{
    internal static readonly string[] Names = ["sage-ground", "ochre-rock", "dune-sand", "frost-stone", "dry-meadow", "burnt-ground"];

    /// <summary>
    /// Every layer the walking session blends, in order (#10889): the overworld's ground, then the cave's blended
    /// blocks, so where a tendril meets its cave the two blend too.
    /// </summary>
    internal static readonly string[] AllNames = [.. Names, .. Dungeons.CaveMaterials.Blended.Select(b => b.Map)];

    private static readonly uint[] Slots = [.. BlockRegistry.MaterialBlocks
        .Where(block => Layer(block.Id) >= 0).Select(block => (uint)block.Id), .. Dungeons.CaveMaterials.Blended.Select(b => (uint)b.Block)];
    private static readonly uint[] Layers = Slots.Select(slot => (uint)AnyLayer((BlockId)slot)).ToArray();

    /// <summary>A block's layer in the walking session: the overworld's ground's, or after them a cave block's.</summary>
    internal static int AnyLayer(BlockId block)
    {
        int ground = Layer(block);
        if (ground >= 0) return ground;
        int cave = Array.FindIndex(Dungeons.CaveMaterials.Blended, b => b.Block == block);
        return cave < 0 ? -1 : Names.Length + cave;
    }

    internal static int Layer(BlockId block) => block switch
    {
        BlockId.Grass or BlockId.Dirt => 0,
        BlockId.Stone => 1,
        BlockId.Sand => 2,
        BlockId.Snow or BlockId.Gravel => 3,
        BlockId.DryGrass => 4,
        BlockId.BurntGround => 5,
        _ => -1,
    };

    internal static void Configure(IEngineContext engine, SpatialSession session, uint transitionCells) =>
        engine.Voxel.ConfigureTerrainLayers(new VoxelTerrainLayerRequest(session, Slots, transitionCells, Layers));
}
