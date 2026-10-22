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

    /// <summary>Still water glowing of the life in it (#10129): its light hangs over the water; the water itself is the glowing thing.</summary>
    LuminousPool,

    /// <summary>Tree roots hanging in a curtain from a ceiling close under the surface (#10129): life from above; they give no light.</summary>
    RootCurtain,

    /// <summary>A vent orchard's seep (#10096): warm, nutrient-bearing water welling up through a glowing crust, the orchard's light.</summary>
    VentSeep,

    /// <summary>An inverted grove's crown (#10096): its filaments' faintly glowing amber tips (painted on the hanger) give this light; no mesh of its own.</summary>
    GroveCrown,
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

/// <summary>
/// One plant of a cave's forest under its sky (#10129), in layout metres: the flora catalog species by name, which of its
/// variants, where its foot stands, its turn about the vertical (degrees) and its size.
/// </summary>
internal readonly record struct ForestPlacement(string Species, int Variant, Vector3 At, float YawDegrees, float Scale);

/// <summary>What a moss or ivy card on a cave's rock is (#10092).</summary>
internal enum MossCardKind
{
    /// <summary>A mat lying on a mossy floor.</summary>
    Mat,

    /// <summary>A cushion standing at a mossy wall's foot.</summary>
    Cushion,

    /// <summary>A curtain hanging over a lip: moss, or ivy over an opening's rim.</summary>
    Curtain,

    /// <summary>Ivy lying flat on a wall's face, hung from a little above its anchor so its top is leafy (the places' face card).</summary>
    Face,

    /// <summary>A leaf cluster standing out of an ivy blanket (the places' cluster card): a splayed pair of leafy cards.</summary>
    Cluster,
}

/// <summary>
/// One moss or ivy card on a cave's rock (#10092; the overworld's painted cards, #10044), in layout metres: what card,
/// where it is anchored, the horizontal its face looks out along, the surface's normal there, and its pick (size, cell).
/// </summary>
internal readonly record struct MossCard(MossCardKind Kind, Vector3 At, Vector3 Out, Vector3 Normal, float Pick);

/// <summary>One growth: what it is, where it stands, which way its surface faces, how big it is, and its own seed.</summary>
internal readonly record struct EcologyPlacement(EcologyKind Kind, Vector3 At, Vector3 Normal, float Size, uint Seed);
