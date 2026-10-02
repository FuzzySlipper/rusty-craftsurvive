using CraftSurvive.Game.Modules.Content;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// Untextured materials for a dungeon meshed as a smooth surface from its voxels. The Engine draws
/// a dual-contoured or marching-cubes voxel surface only with untextured materials, so each block a
/// dungeon uses is given one flat colour - near its texture's average - to judge the shapes by.
/// </summary>
internal sealed class DungeonFlatMaterials : IDisposable
{
    /// <summary>No texture: the material is its colour alone.</summary>
    private static readonly RenderResourceReference NoTexture = new(0UL);

    private const float Roughness = 0.9f;

    private static readonly Color Fallback = new(0.5f, 0.5f, 0.5f, 1f);

    /// <summary>Each block's flat colour, chosen near the average of its atlas tile.</summary>
    private static readonly IReadOnlyDictionary<BlockId, Color> Colours = new Dictionary<BlockId, Color>
    {
        [BlockId.Grass] = new(0.33f, 0.5f, 0.24f, 1f),
        [BlockId.Dirt] = new(0.45f, 0.33f, 0.22f, 1f),
        [BlockId.Stone] = new(0.46f, 0.46f, 0.47f, 1f),
        [BlockId.Sand] = new(0.78f, 0.72f, 0.52f, 1f),
        [BlockId.Gravel] = new(0.52f, 0.49f, 0.47f, 1f),
        [BlockId.Cobblestone] = new(0.4f, 0.4f, 0.41f, 1f),
        [BlockId.Brick] = new(0.55f, 0.3f, 0.25f, 1f),
        [BlockId.Log] = new(0.4f, 0.3f, 0.19f, 1f),
        [BlockId.Planks] = new(0.62f, 0.49f, 0.31f, 1f),
        [BlockId.Leaves] = new(0.25f, 0.45f, 0.2f, 1f),
        [BlockId.Glass] = new(0.75f, 0.85f, 0.9f, 1f),
        [BlockId.Lamp] = new(1f, 0.85f, 0.55f, 1f),
        [BlockId.Bedrock] = new(0.2f, 0.2f, 0.21f, 1f),
        [BlockId.Snow] = new(0.92f, 0.94f, 0.96f, 1f),
    };

    private readonly List<Material> made = [];

    internal DungeonFlatMaterials(IEngineContext engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        List<VoxelSceneMaterialBinding> bindings = [];
        foreach (BlockDefinition block in BlockRegistry.BoundBlocks)
        {
            Material material = engine.Graphics.CreateMaterial(new MaterialRequest(
                Colours.GetValueOrDefault(block.Id, Fallback), NoTexture, Roughness, new Color(1f, 1f, 1f, 1f),
                System.Numerics.Vector3.Zero, 0f, false, MaterialAlphaMode.Opaque, 0f));
            made.Add(material);
            bindings.Add(new VoxelSceneMaterialBinding(block.Slot, material));
        }

        Bindings = bindings.ToArray();
    }

    internal ReadOnlyMemory<VoxelSceneMaterialBinding> Bindings { get; }

    public void Dispose()
    {
        foreach (Material material in made)
        {
            material.Dispose();
        }

        made.Clear();
    }
}
