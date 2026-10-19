using System.Numerics;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>The living (and mineral) things a cave grows that make their own light (#10129).</summary>
internal enum EcologyKind
{
    /// <summary>Caps that glow on damp, quiet floors, where water stands or drips but does not run.</summary>
    GlowFungi,

    /// <summary>Threads hanging from ceilings near running water, each with a glowing bead at its tip.</summary>
    GlowWorms,

    /// <summary>Faintly glowing prisms on old, dry levels: almost the only thing there.</summary>
    CrystalBloom,

    /// <summary>A great hall's giant fungus: a tall stalk under a broad cap glowing from beneath, lighting the floor round it.</summary>
    GiantFungus,

    /// <summary>A great hall's crystal bloom hanging from the vault: the brightest light there, a sun for the hall.</summary>
    VaultBloom,
}

/// <summary>A low mist lying in a natural space (#10094), in layout metres: an ellipsoid turned about the vertical.</summary>
internal readonly record struct MistVolume(Vector3 Centre, Vector3 HalfExtents, float YawDegrees);

/// <summary>One growth: what it is, where it stands, which way its surface faces, how big it is, and its own seed.</summary>
internal readonly record struct EcologyPlacement(EcologyKind Kind, Vector3 At, Vector3 Normal, float Size, uint Seed);
