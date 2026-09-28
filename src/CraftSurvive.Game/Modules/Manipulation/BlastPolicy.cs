namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>What the product decides to do with a charge of a given size.</summary>
internal enum BlastDisposition
{
    /// <summary>Resolve as one transaction - the only way a charge resolves.</summary>
    Single,

    /// <summary>Too large to resolve at all. The charge is refused, not silently truncated.</summary>
    Refused,
}

/// <summary>The decision for one charge, with the bound that produced it.</summary>
internal readonly record struct BlastAdmission(BlastDisposition Disposition, int Cells)
{
    internal bool Applies => Disposition != BlastDisposition.Refused;
}

/// <summary>
/// How large a blast may be, and what it costs - both measured on the running product.
///
/// A charge resolves as **one** transaction. That is not a simplification, it is what the
/// measurements say: timing the edit route end to end showed the cost is a **step**, not a slope.
/// `engine.Voxel.ApplyEdits` alone took **198.1 ms** for a 41-cell transaction, while everything
/// the product does around it - admission, three before-reads, overlay apply and save, residency,
/// presentation, UI - totalled **22.1 ms**. The same step appears at 7 cells and at 123: roughly
/// **240 ms per transaction whatever its size**.
///
/// An earlier version of this policy split large charges across updates, on the theory that a
/// smaller transaction would be quicker. It is the opposite: each stage pays the full Engine cost
/// again, so a three-stage charge of 123 cells costs about **710 ms** where one transaction costs
/// about **236 ms**. Staging made the stall three times worse while looking like the careful
/// choice, and only the measurement showed it.
///
/// So a charge is bounded by a **cell ceiling** for sanity - the route itself allows 4096 - and its
/// real bound is the **stall the presentation must cover**, stated here as a measured constant
/// rather than hidden as a per-cell rate. A charge beyond the ceiling is refused rather than
/// truncated: a blast that quietly removes less than it was asked to is worse than one that does
/// not fire.
///
/// The five hypotheses retired on the way here, for whoever re-derives this: 0.48 ms/cell from the
/// substrate proof, 1.9 ms/cell from one cold sample, 4.6 ms/cell from four charges at one size,
/// the overlay save, and the before-reads. Each was fitted to a slope that does not exist.
/// </summary>
internal static class BlastPolicy
{
    /// <summary>
    /// The most cells one charge may remove. A sanity ceiling well inside the edit route's own
    /// 4096-cell limit, not a latency bound - latency does not scale with this.
    /// </summary>
    internal const int MaximumCells = 512;

    /// <summary>
    /// What one charge costs, measured: the stall a charge's presentation has to cover. Roughly
    /// constant with volume, because the Engine's transaction cost is per transaction.
    /// </summary>
    internal const int MeasuredTransactionMilliseconds = 240;

    internal static BlastAdmission Decide(int cells)
    {
        if (cells < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cells), "A blast cannot have a negative cell count.");
        }

        return cells <= MaximumCells
            ? new BlastAdmission(BlastDisposition.Single, cells)
            : new BlastAdmission(BlastDisposition.Refused, cells);
    }
}
