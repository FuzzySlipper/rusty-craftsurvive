using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// One planned charge: the sphere it reaches, what the policy says about its size, and the cells
/// it actually clears once each block's resistance is weighed against the charge's strength there.
/// The plan is pure - it reads materials through the caller's function - so the same charge on the
/// same world always clears the same cells.
/// </summary>
internal sealed class BlastCharge
{
    private BlastCharge(VoxelAddress centre, int radius, BlastAdmission admission, VoxelAddress[] cleared)
    {
        Centre = centre;
        Radius = radius;
        Admission = admission;
        Cleared = cleared;
    }

    internal VoxelAddress Centre { get; }

    internal int Radius { get; }

    /// <summary>The policy's decision, made on the sphere's size rather than on what it clears.</summary>
    internal BlastAdmission Admission { get; }

    /// <summary>The solid cells the charge is strong enough to break. Empty for a refused charge.</summary>
    internal IReadOnlyList<VoxelAddress> Cleared { get; }

    /// <summary>
    /// Plans a charge. A refused charge is planned and reported rather than silently shrunk, so the
    /// caller can say why nothing happened.
    /// </summary>
    internal static BlastCharge Plan(VoxelAddress centre, int radius, Func<VoxelAddress, ushort> materialAt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        ArgumentNullException.ThrowIfNull(materialAt);

        VoxelAddress[] sphere = Sphere(centre, radius);
        BlastAdmission admission = BlastPolicy.Decide(sphere.Length);
        if (!admission.Applies)
        {
            return new BlastCharge(centre, radius, admission, []);
        }

        List<VoxelAddress> cleared = [];
        foreach (VoxelAddress cell in sphere)
        {
            if (BlockRegistry.TryGetBySlot(materialAt(cell), out BlockDefinition block)
                && BlastPolicy.Breaks(block, Distance(centre, cell), radius))
            {
                cleared.Add(cell);
            }
        }

        return new BlastCharge(centre, radius, admission, cleared.ToArray());
    }

    /// <summary>The cells of a solid sphere: the volume a charge reaches.</summary>
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

    private static float Distance(VoxelAddress from, VoxelAddress to)
    {
        long dx = to.X - from.X;
        long dy = to.Y - from.Y;
        long dz = to.Z - from.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
