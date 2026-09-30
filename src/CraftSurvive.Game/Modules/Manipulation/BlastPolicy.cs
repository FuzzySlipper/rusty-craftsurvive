using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>What the product decides to do with a charge of a given size.</summary>
internal enum BlastDisposition
{
    /// <summary>Resolve as one edit transaction - the only way a charge resolves.</summary>
    Single,

    /// <summary>Too large to resolve at all. The charge is refused, not silently truncated.</summary>
    Refused,
}

/// <summary>The decision for one charge, with the sphere size that produced it.</summary>
internal readonly record struct BlastAdmission(BlastDisposition Disposition, int Cells)
{
    internal bool Applies => Disposition != BlastDisposition.Refused;
}

/// <summary>
/// How large a charge may be and what it can break. A charge resolves as one edit transaction;
/// its size is bounded by a sanity ceiling, and a charge past the ceiling is refused rather than
/// truncated - a blast that quietly removes less than it was asked to is worse than one that does
/// not fire.
/// </summary>
internal static class BlastPolicy
{
    /// <summary>
    /// The most cells one charge's sphere may reach. A sanity ceiling well inside the edit route's
    /// own transaction limit.
    /// </summary>
    internal const int MaximumCells = 512;

    /// <summary>
    /// A charge's strength at its rim. Strength falls off linearly from the centre, where it is
    /// <c>radius + RimStrength</c>, to this value at the sphere's edge.
    /// </summary>
    internal const float RimStrength = 1f;

    internal static BlastAdmission Decide(int cells)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cells);
        return cells <= MaximumCells
            ? new BlastAdmission(BlastDisposition.Single, cells)
            : new BlastAdmission(BlastDisposition.Refused, cells);
    }

    /// <summary>The charge's strength at a distance from its centre.</summary>
    internal static float StrengthAt(float distance, int radius) =>
        radius + RimStrength - distance;

    /// <summary>
    /// Whether a charge breaks a block. Only solid blocks are broken - a charge in a lake does not
    /// drain it - and only where the charge's strength there reaches the block's resistance, so
    /// masonry survives the rim that clears dirt and bedrock survives everything.
    /// </summary>
    internal static bool Breaks(BlockDefinition block, float distance, int radius) =>
        block.Solid && block.BlastResistance <= StrengthAt(distance, radius);
}
