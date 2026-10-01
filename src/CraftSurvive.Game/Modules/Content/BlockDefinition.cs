namespace CraftSurvive.Game.Modules.Content;

/// <summary>How a block's faces are drawn, which decides its Engine alpha mode.</summary>
internal enum BlockTransparency
{
    /// <summary>Fully opaque; the surface hides what is behind it.</summary>
    Opaque,

    /// <summary>Hard-edged holes, used for leaves; drawn with an alpha mask.</summary>
    Cutout,

    /// <summary>Partially transparent, used for water and glass.</summary>
    Translucent,
}

/// <summary>
/// One block's product meaning. The atlas catalog projects these facts onto the
/// Engine's authored-material fields, so this is the single place a block's
/// behaviour is declared rather than a parallel model beside the Engine's.
/// Properties that later slices read - replaceability, blast resistance, light
/// attenuation, whether a face can be climbed - are declared here from the start instead of
/// appearing as scattered constants the first time a slice needs them.
/// </summary>
internal readonly record struct BlockDefinition(
    BlockId Id,
    string Name,
    string BaseRegion,
    string? TopRegion,
    BlockTransparency Transparency,
    bool Solid,
    bool Collidable,
    bool Occludes,
    bool Replaceable,
    float BlastResistance,
    float LightEmission,
    float LightAttenuation,
    bool Climbable = false)
{
    /// <summary>The world's material slot for this block.</summary>
    internal ushort Slot => (ushort)Id;

    internal string MaterialId => $"material/block-{Name}";

    internal string TopMaterialId => $"material/block-{Name}-top";

    internal bool IsAir => Id == BlockId.Air;
}
