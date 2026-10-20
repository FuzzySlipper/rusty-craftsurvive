namespace CraftSurvive.Game.Modules.Content;

/// <summary>
/// The V1 block floor of fifteen blocks plus air (campaign #8595), and dry grass after it (#10041). Every block
/// here owns exactly one tile in the world's single atlas, except grass, which owns a base tile and a top-face tile;
/// a block added after the floor takes a tile in the atlas's added rows (scripts/generate-terrain-atlas.mjs).
///
/// The world's material slot is the block's numeric id. Append only.
///
/// A face is climbable when it offers holds: earth, rock, masonry and timber. Loose sand and
/// gravel, snow, tree cores, glass and lamps are not, so a player reads a climb from what a wall is
/// made of.
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
            BlastResistance: 0.6f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.Dirt, "dirt", "dirt", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.5f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.Stone, "stone", "stone", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1.5f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.Sand, "sand", "sand", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.5f, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.Gravel, "gravel", "gravel", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.6f, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.Cobblestone, "cobblestone", "cobblestone", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 2f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.Brick, "brick", "brick", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 2f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.Log, "log", "log", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1.2f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.Planks, "planks", "planks", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        // A generated tree's trunk core: the tree is drawn as a mesh standing on it, so the core
        // is solid to bodies but draws nothing (its atlas tile is fully transparent), hides no
        // neighbour and passes light. Blasting it fells the tree.
        new(BlockId.TreeCore, "tree-core", "tree-core", null, BlockTransparency.Cutout,
            Solid: true, Collidable: true, Occludes: false, Replaceable: false,
            BlastResistance: 1.2f, LightEmission: 0f, LightAttenuation: 0f),

        // Water is a volume, not an obstacle: nothing collides with it and the
        // player's interaction with it is Engine swim mode over a product volume.
        new(BlockId.Water, "water", "water", null, BlockTransparency.Translucent,
            Solid: false, Collidable: false, Occludes: false, Replaceable: true,
            BlastResistance: 0f, LightEmission: 0f, LightAttenuation: 0.3f),

        new(BlockId.Glass, "glass", "glass", null, BlockTransparency.Translucent,
            Solid: true, Collidable: true, Occludes: false, Replaceable: false,
            BlastResistance: 0.3f, LightEmission: 0f, LightAttenuation: 0.1f),

        // A lit lamp's own glow stays under white so its fire (Fires) reads over it under bloom.
        new(BlockId.Lamp, "lamp", "lamp", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.3f, LightEmission: 0.35f, LightAttenuation: 1f),

        // Bedrock is the world's floor and border: placeable by generation only.
        new(BlockId.Bedrock, "bedrock", "bedrock", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: float.MaxValue, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.Snow, "snow", "snow", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.3f, LightEmission: 0f, LightAttenuation: 1f),

        new(BlockId.DryGrass, "dry-grass", "dry-grass", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 0.6f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        // The cave's own rock (#10095): drawn in caves by the cave materials; their atlas tiles (tinted stone) are
        // stand-ins, as they are never placed in the open world.
        new(BlockId.WashedRock, "washed-rock", "washed-rock", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1.5f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.PatinatedRock, "patinated-rock", "patinated-rock", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1.5f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.Flowstone, "flowstone", "flowstone", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1.2f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.Rubble, "rubble", "rubble", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        // The cave's growth (#10129): rock coated in life; their atlas tiles are stand-ins as the cave rock's are.
        new(BlockId.MossyRock, "mossy-rock", "mossy-rock", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1.5f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.SlimyRock, "slimy-rock", "slimy-rock", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1.5f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),

        new(BlockId.Moonmilk, "moonmilk", "moonmilk", null, BlockTransparency.Opaque,
            Solid: true, Collidable: true, Occludes: true, Replaceable: false,
            BlastResistance: 1f, LightEmission: 0f, LightAttenuation: 1f,
            Climbable: true),
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
