namespace CraftSurvive.Game.Modules.Horizon;

/// <summary>
/// Where the horizon backdrop is sunk under the far field (#9779), and why moving the sunk zone is
/// never seen. The far field keeps its chunk columns within its reach of the player's column, so it
/// always covers at least <c>reach - FarChunkMetres</c> about the player (Chebyshev). The sunk zone's
/// centre lags the player by less than <see cref="FollowMetres"/>. So the sink fades to nothing by
/// <see cref="CoveredMetres"/> from its centre: everything it lowers, before and after a move, lies
/// under the far field at the moment the zone moves, and ground the player can see never changes.
/// </summary>
internal static class HorizonSink
{
    /// <summary>The backdrop is this far below the true ground where fully sunk: below the far field's own error.</summary>
    internal const double DepthMetres = 60;
    /// <summary>The sunk zone moves onto the player once they have walked this far from its centre.</summary>
    internal const double FollowMetres = 256;
    /// <summary>The sink eases out over this far, inside the covered radius.</summary>
    internal const double RiseMetres = 128;

    /// <summary>The radius about the sunk zone's centre that the far field covers wherever the player stands within the follow step.</summary>
    internal static double CoveredMetres(double reach, double farChunkMetres) => Math.Max(0, reach - farChunkMetres - FollowMetres);

    /// <summary>How far the backdrop is sunk at a point <paramref name="away"/> metres (Chebyshev) from the zone's centre.</summary>
    internal static double At(double away, double reach, double farChunkMetres)
    {
        double covered = CoveredMetres(reach, farChunkMetres);
        double t = Math.Clamp((away - (covered - RiseMetres)) / RiseMetres, 0d, 1d);
        return DepthMetres * (1d - (t * t * (3d - (2d * t))));
    }
}
