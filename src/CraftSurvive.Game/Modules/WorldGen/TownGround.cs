namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The ground a town stands on (#9895; owner decision 2026-10-14): a seat beside its water, the way to
/// that water, and the level its pad is flattened to. A town's frame has +X toward the water; the pad
/// is flat out to <see cref="FlatMetres"/> and eases back into the natural ground over
/// <see cref="BlendMetres"/>, but never past <see cref="Waterfront"/> on the water side, so the river
/// or the sea beside a quay is left as it is. A town's authored layout (product content) is composed
/// in this frame and must stand within the flat disc on the land side of the waterfront.
/// </summary>
internal readonly record struct TownGround(double X, double Z, double FacingX, double FacingZ, double Height)
{
    /// <summary>The flat pad's radius about the seat (m).</summary>
    internal const double FlatMetres = 24;

    /// <summary>How far past the flat pad the ground eases back to its natural height (m).</summary>
    internal const double BlendMetres = 30;

    /// <summary>Where the pad meets the water: this far from the seat toward it (m, the frame's +X).</summary>
    internal const double Waterfront = 12;

    /// <summary>How far either side of the waterfront the pad fades out (m).</summary>
    internal const double ShoreFade = 2;

    /// <summary>How far the pad stands above the water beside it (m): a quay's height.</summary>
    internal const double QuayMetres = 1.2;

    /// <summary>The farthest the pad reaches from its seat (m).</summary>
    internal const double ReachMetres = FlatMetres + BlendMetres;

    /// <summary>The trodden yard about the seat (m), on the land side: bare earth, where nothing wild grows.</summary>
    internal const double YardMetres = 28;

    /// <summary>A point in the town's frame: along the facing toward the water, and across it.</summary>
    internal (double Along, double Across) Local(double x, double z)
    {
        double dx = x - X, dz = z - Z;
        return ((dx * FacingX) + (dz * FacingZ), (-dx * FacingZ) + (dz * FacingX));
    }

    /// <summary>How much the pad holds a point to its level: 1 on the flat, easing to 0 at its reach and the water.</summary>
    internal double Weight(double x, double z)
    {
        double dx = x - X, dz = z - Z, squared = (dx * dx) + (dz * dz);
        if (squared >= ReachMetres * ReachMetres) return 0;
        double along = Local(x, z).Along;
        double land = 1 - Ease(Waterfront - ShoreFade, Waterfront + ShoreFade, along);
        return land * (1 - Ease(FlatMetres, ReachMetres, Math.Sqrt(squared)));
    }

    /// <summary>Whether a point is on the town's yard: near its seat, on the land side of the waterfront.</summary>
    internal bool InYard(double x, double z)
    {
        double dx = x - X, dz = z - Z;
        return (dx * dx) + (dz * dz) < YardMetres * YardMetres && Local(x, z).Along < Waterfront;
    }

    /// <summary>The ground at a point with the pad applied.</summary>
    internal double Level(double ground, double x, double z)
    {
        double weight = Weight(x, z);
        return weight <= 0 ? ground : ground + ((Height - ground) * weight);
    }

    private static double Ease(double start, double end, double value)
    {
        double t = Math.Clamp((value - start) / (end - start), 0, 1);
        return t * t * (3 - (2 * t));
    }
}
