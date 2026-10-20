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

/// <summary>
/// A stream falling from a passage's window into a chasm (#10094), in layout metres: the lip it pours over, which way it
/// pours (across the floor), how far it falls to where it lands (or to the bottom of the space, when it falls on into the
/// abyss), how much water it carries (0 to 1), and how much of the sky's light reaches the lip (0 to 1).
/// </summary>
internal readonly record struct Waterfall(Vector3 Lip, Vector3 Out, float Drop, bool Lands, float Flow, float SkyShare);

/// <summary>
/// A mist lying in a natural space (#10094), in layout metres: an ellipsoid turned about the vertical, how thick it is at
/// its heart (extinction per metre: a great hall's floor mist is thin, a sump's heavy), and the light it gives of itself
/// per unit of density (linear; none for a hall's floor mist, a faint glow for a sump's, so its mouth reads from outside).
/// </summary>
internal readonly record struct MistVolume(Vector3 Centre, Vector3 HalfExtents, float YawDegrees, float Density, Vector3 Glow = default);

/// <summary>One growth: what it is, where it stands, which way its surface faces, how big it is, and its own seed.</summary>
internal readonly record struct EcologyPlacement(EcologyKind Kind, Vector3 At, Vector3 Normal, float Size, uint Seed);
