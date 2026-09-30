namespace CraftSurvive.Game.Modules.Content;

/// <summary>
/// The V1 block floor: fifteen blocks plus air, which is the settled content
/// floor of twelve to sixteen for campaign #8595. The atlas is the binding
/// constraint, so every block here owns exactly one tile in the world's single
/// atlas, except grass, which owns a base tile and a top-face tile.
///
/// The world's material slot is the block's numeric id. Append only.
/// </summary>
internal static class BlockRegistry
{
    private static readonly BlockDefinition[] Definitions =
    [
        new(BlockId.Air, "air", string.Empty, null, BlockTransparency.Opaque,
            Solid: false, Collidable: false, Occludes: false, Replaceable: true,
            BlastResistance: 0f, LightEmission: 0f, LightAttenuation: 0f),

        new(BlockId.Grass, "grass", "grass-side", "grass-top", BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.6f, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.Dirt, "dirt", "dirt", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.5f, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.Stone, "stone", "stone", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1.5f, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.Sand, "sand", "sand", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.5f, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.Gravel, "gravel", "gravel", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.6f, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.Cobblestone, "cobblestone", "cobblestone", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 2f, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.Brick, "brick", "brick", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 2f, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.Log, "log", "log", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1.2f, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.Planks, "planks", "planks", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1f, LightEmission: 0f, LightAttenuation: 1f),

        // Canopy: leaves are solid for collision and movement, do not occlude,
        // and are cheap to blast, so a tree comes apart without opening a hole in
        // the light or the skyline.
        new(BlockId.Leaves, "leaves", "leaves", null, BlockTransparency.Cutout,
            Solid: true, Collidable: true, Occludes: false, Replaceable: false,
            BlastResistance: 0.2f, LightEmission: 0f, LightAttenuation: 0.4f),

        // Water is a volume, not an obstacle: nothing collides with it and the
        // player's interaction with it is Engine swim mode over a product volume.
        new(BlockId.Water, "water", "water", null, BlockTransparency.Translucent,
            Solid: false, Collidable: false, Occludes: false, Replaceable: true,
            BlastResistance: 0f, LightEmission: 0f, LightAttenuation: 0.3f),

        new(BlockId.Glass, "glass", "glass", null, BlockTransparency.Translucent,
            Solid: true, Collidable: true, Occludes: false, Replaceable: false,
            BlastResistance: 0.3f, LightEmission: 0f, LightAttenuation: 0.1f),

        new(BlockId.Lamp, "lamp", "lamp", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.3f, LightEmission: 1f, LightAttenuation: 1f),

        // Bedrock is the world's floor and border: placeable by generation only.
        new(BlockId.Bedrock, "bedrock", "bedrock", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: float.MaxValue, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.Snow, "snow", "snow", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.3f, LightEmission: 0f, LightAttenuation: 1f),
    ];

    /// <summary>Every block, air first, in slot order.</summary>
    internal static ReadOnlySpan<BlockDefinition> All => Definitions;

    /// <summary>
    /// Blocks that need a material and an atlas tile. Every one of them owns a
    /// tile in the world's atlas; the audit and the layout both check that.
    /// </summary>
    internal static IEnumerable<BlockDefinition> MaterialBlocks =>
        Definitions.Where(definition => !definition.IsAir);

    /// <summary>
    /// Blocks whose materials are bound to the world's voxel scene: every block with a material.
    /// The Engine's retained palette holds 65,536 source slots, so the registry, not the Engine,
    /// bounds the count.
    /// </summary>
    internal static IEnumerable<BlockDefinition> BoundBlocks => MaterialBlocks;

    internal static ushort MaximumSlot => (ushort)(Definitions.Length - 1);

    /// <summary>
    /// Whether a block's material can be bound to the scene today. Generation may
    /// only place blocks that are bound, because a voxel whose slot has no material
    /// fails the scene projection.
    /// </summary>
    internal static bool IsBound(BlockId id)
    {
        foreach (BlockDefinition block in BoundBlocks)
        {
            if (block.Id == id)
            {
                return true;
            }
        }

        return false;
    }

    internal static BlockDefinition Get(BlockId id)
    {
        int index = (int)id;
        return index >= 0 && index < Definitions.Length && Definitions[index].Id == id
            ? Definitions[index]
            : throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown block id.");
    }

    internal static bool TryGetBySlot(ushort slot, out BlockDefinition definition)
    {
        if (slot < Definitions.Length && (ushort)Definitions[slot].Id == slot)
        {
            definition = Definitions[slot];
            return true;
        }

        definition = default;
        return false;
    }
}
