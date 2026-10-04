namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// A charge's identity, which its smoke and debris are seeded from (<see cref="Feedback.Bursts.BlastSmoke"/>,
/// <see cref="Feedback.Bursts.BlastDebris"/>): the same blast in the same place produces the same dust,
/// as the product's generation draws do.
/// </summary>
internal static class BlastDust
{
    private const ulong IdentityOffsetBasis = 0xCBF2_9CE4_8422_2325UL;
    private const ulong IdentityPrime = 0x0000_0100_0000_01B3UL;

    /// <summary>
    /// A charge's identity: every coordinate of its centre is mixed whole, so negative and distant
    /// centres stay distinct.
    /// </summary>
    internal static ulong ChargeIdentity(CraftSurvive.Game.Modules.Terrain.VoxelAddress centre)
    {
        ulong hash = IdentityOffsetBasis;
        foreach (long coordinate in (ReadOnlySpan<long>)[centre.X, centre.Y, centre.Z])
        {
            hash = unchecked((hash ^ (ulong)coordinate) * IdentityPrime);
        }

        return hash;
    }
}
