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
/// These numbers come from **the running product**, not from the substrate proof. The proof
/// measured the same edit route at 0.48 ms per cell (123 cells in 59.06 ms), but a live charge
/// staged into two transactions of about 129 cells reported a **worst stage of 241.44 ms** - near
/// 1.9 ms per cell, four times the proof's figure, because a live session has the renderer,
/// collision, residency and presentation work sitting behind the edit. The budget is a promise
/// about *the product's* update latency, so the product is the measurement that counts; the
/// proof's figure is recorded here only as the thing that turned out to be too optimistic.
///
/// At 1.9 ms per cell a 100 ms update holds about 50 cells, which is why the single-transaction
/// ceiling is 48 rather than the 216 first guessed from the proof. Staging still costs the fixed
/// cost of a scene read, an overlay save and a presentation refresh per transaction, so the stage
/// ceiling is a real price rather than a formality. A charge beyond the staged ceiling is refused
/// rather than truncated: a blast that quietly removes less than it was asked to is worse than one
/// that does not fire.
/// </summary>
internal static class BlastPolicy
{
    /// <summary>How long one blast's edit may hold an update. Above this the charge is staged or refused.</summary>
    internal const int SingleTransactionBudgetMilliseconds = 100;

    /// <summary>
    /// The most cells one blast may remove: four stages at the single-transaction ceiling. About
    /// 360 ms of work in total, spread across four updates so no single one is held longer than
    /// the budget allows.
    /// </summary>
    internal const int MaximumCells = 192;

    /// <summary>
    /// At or below this many cells, one transaction is inside the budget: 48 cells at the measured
    /// 1.9 ms per cell is about 90 ms, inside the 100 ms the budget promises.
    /// </summary>
    internal const int SingleTransactionCells = 48;

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
