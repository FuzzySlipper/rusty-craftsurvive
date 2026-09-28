namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>What the product decides to do with a charge of a given size.</summary>
internal enum BlastDisposition
{
    /// <summary>Small enough to resolve as one transaction.</summary>
    Single,

    /// <summary>Large: resolve as several bounded transactions, in order, over successive updates.</summary>
    Staged,

    /// <summary>Too large to resolve at all. The charge is refused, not silently truncated.</summary>
    Refused,
}

/// <summary>The decision for one charge, with the bound that produced it.</summary>
internal readonly record struct BlastAdmission(BlastDisposition Disposition, int Cells, int Stages, int CellsPerStage)
{
    internal bool Applies => Disposition != BlastDisposition.Refused;
}

/// <summary>
/// How large a blast may be, decided from measurement rather than taste.
///
/// Two points were measured on the live substrate proof through the product's own edit route:
/// a 32-cell transaction through the view-aimed brush took 72.19 ms, a 123-cell decided volume
/// took 59.06 ms, and undoing those 123 cells exactly took 62.68 ms. The decided route is
/// therefore nearer 0.5 ms per cell at that volume, with a fixed cost near 57 ms that dominates
/// small edits - the scene read, the receipt handling, the overlay save and the presentation
/// refresh are paid once per transaction whatever its size.
///
/// That shape is what these numbers encode. The budget below is a promise about how long one
/// update may be held, and because the fixed cost is paid per transaction, splitting a large
/// charge pays it repeatedly - so staging buys a bounded stall at a measured price rather than
/// being free. A charge beyond the staged ceiling is refused rather than truncated: a blast that
/// quietly removes less than it was asked to is worse than one that does not fire.
/// </summary>
internal static class BlastPolicy
{
    /// <summary>How long one blast's edit may hold an update. Above this the charge is staged or refused.</summary>
    internal const int SingleTransactionBudgetMilliseconds = 100;

    /// <summary>
    /// The most cells one blast may remove, from the measured curve: 512 cells is roughly twice
    /// the 123-cell measurement, which stays inside a few hundred milliseconds.
    /// </summary>
    internal const int MaximumCells = 512;

    /// <summary>At or below this many cells, one transaction is inside the budget.</summary>
    internal const int SingleTransactionCells = 216;

    /// <summary>The most stages a staged charge may take. More than this is refused outright.</summary>
    internal const int MaximumStages = 4;

    internal static BlastAdmission Decide(int cells)
    {
        if (cells < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cells), "A blast cannot have a negative cell count.");
        }

        if (cells <= SingleTransactionCells)
        {
            return new BlastAdmission(BlastDisposition.Single, cells, 1, cells);
        }

        if (cells > MaximumCells)
        {
            return new BlastAdmission(BlastDisposition.Refused, cells, 0, 0);
        }

        int stages = (cells + SingleTransactionCells - 1) / SingleTransactionCells;
        if (stages > MaximumStages)
        {
            return new BlastAdmission(BlastDisposition.Refused, cells, 0, 0);
        }

        return new BlastAdmission(BlastDisposition.Staged, cells, stages,
            (cells + stages - 1) / stages);
    }
}
