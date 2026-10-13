namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// Where the near water is drawn (#9877): a square of 64 m tiles about a centre (the far field's sink centre),
/// reaching past the far field's sunk band. The far field leaves its water out strictly inside it, so the two
/// meet edge to edge at the waterline. Engine-free; the near surfaces and the far field both ask it.
/// </summary>
internal static class NearWaterSquare
{
    /// <summary>A near water tile's edge (metres).</summary>
    internal const int TileMetres = 64;

    /// <summary>
    /// The square of tiles the near water covers: those within this many tiles (Chebyshev) of the tile
    /// holding the centre, enough to reach past the far field's sunk band (<paramref name="outer"/>, metres).
    /// The far field draws no water strictly inside the same square (<see cref="CoversInside"/>), so the two
    /// meet edge to edge at the same level.
    /// </summary>
    internal static int TileRadius(double outer) => Math.Max(1, (int)Math.Ceiling((outer - (TileMetres / 2.0)) / TileMetres));

    /// <summary>Whether the near water's square about a centre covers a point (its tiles hold the point).</summary>
    internal static bool Covers((double X, double Z) centre, double outer, double x, double z)
    {
        int radius = TileRadius(outer);
        long cx = (long)Math.Floor(centre.X / TileMetres), cz = (long)Math.Floor(centre.Z / TileMetres);
        return Math.Abs((long)Math.Floor(x / TileMetres) - cx) <= radius && Math.Abs((long)Math.Floor(z / TileMetres) - cz) <= radius;
    }

    /// <summary>
    /// Whether a point lies strictly inside the near water's square: the rule the far field leaves its water
    /// out by. A far vertex on the square's edge keeps the water's height, so the far field's last cell
    /// meets the near mesh's edge at the waterline on all four sides (#9877 review), not at the bed.
    /// </summary>
    internal static bool CoversInside((double X, double Z) centre, double outer, double x, double z)
    {
        int radius = TileRadius(outer);
        long cx = (long)Math.Floor(centre.X / TileMetres), cz = (long)Math.Floor(centre.Z / TileMetres);
        double minX = (cx - radius) * (double)TileMetres, maxX = (cx + radius + 1) * (double)TileMetres;
        double minZ = (cz - radius) * (double)TileMetres, maxZ = (cz + radius + 1) * (double)TileMetres;
        return x > minX && x < maxX && z > minZ && z < maxZ;
    }

    /// <summary>
    /// Whether a flood may reach a tile (#9880 review): whether any of its corners lies by a floodplain. A tile
    /// is smaller than a cell of the floodplain's lattice, so the cells its corners lie in are every cell it touches.
    /// </summary>
    internal static bool Floodable(Func<double, double, bool> floodable, (long X, long Z) tile)
    {
        double x0 = tile.X * (double)TileMetres, z0 = tile.Z * (double)TileMetres;
        return floodable(x0, z0) || floodable(x0 + TileMetres, z0) || floodable(x0, z0 + TileMetres) || floodable(x0 + TileMetres, z0 + TileMetres);
    }
}
