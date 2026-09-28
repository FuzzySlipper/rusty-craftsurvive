using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// One charge, resolved over as many updates as its size demands.
///
/// A blast is a decided volume - the cells are computed once, here, not aimed at - and it is
/// resolved through the product's single revision-checked edit route in bounded transactions.
/// The sequence is what keeps a large charge from holding one update for longer than the budget
/// allows: <see cref="BlastPolicy.Decide"/> says how many transactions it takes, and this hands
/// out one per update until the charge is spent.
///
/// Staging is not free - each transaction pays the fixed cost of a scene read, an overlay save
/// and a presentation refresh - so the policy's stage ceiling is a real price, not a formality.
/// </summary>
internal sealed class BlastSequence
{
    private readonly VoxelAddress[] cells;
    private int applied;

    private BlastSequence(VoxelAddress centre, VoxelAddress[] cells, BlastAdmission admission)
    {
        Centre = centre;
        this.cells = cells;
        Admission = admission;
    }

    internal VoxelAddress Centre { get; }

    internal BlastAdmission Admission { get; }

    /// <summary>True while the charge still has cells to remove.</summary>
    internal bool Pending => applied < cells.Length && Admission.Applies;

    internal int CellsRemaining => cells.Length - applied;

    internal int StagesApplied { get; private set; }

    /// <summary>
    /// Plans a charge: the sphere it removes, and what the policy says about resolving it. A
    /// refused charge is planned and reported rather than silently shrunk, so the caller can say
    /// why nothing happened.
    /// </summary>
    internal static BlastSequence Plan(VoxelAddress centre, int radius)
    {
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), "A charge cannot have a negative radius.");
        }

        VoxelAddress[] cells = Sphere(centre, radius);
        return new BlastSequence(centre, cells, BlastPolicy.Decide(cells.Length));
    }

    /// <summary>
    /// Applies the whole charge through the caller's edit route and reports whether it reached the
    /// world. There is one call because there is one transaction: splitting a charge across updates
    /// was measured to cost more, not less, since the Engine charges per transaction.
    /// </summary>
    internal bool Advance(Func<IReadOnlyList<VoxelAddress>, bool> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (!Pending)
        {
            return false;
        }

        if (!apply(cells))
        {
            return false;
        }

        applied = cells.Length;
        StagesApplied = 1;
        return true;
    }

    /// <summary>The cells of a solid sphere: the volume a charge removes.</summary>
    internal static VoxelAddress[] Sphere(VoxelAddress centre, int radius)
    {
        List<VoxelAddress> found = [];
        long squared = (long)radius * radius;
        for (long x = -radius; x <= radius; x++)
        {
            for (long y = -radius; y <= radius; y++)
            {
                for (long z = -radius; z <= radius; z++)
                {
                    if ((x * x) + (y * y) + (z * z) <= squared)
                    {
                        found.Add(new VoxelAddress(centre.X + x, centre.Y + y, centre.Z + z));
                    }
                }
            }
        }

        return found.ToArray();
    }
}
