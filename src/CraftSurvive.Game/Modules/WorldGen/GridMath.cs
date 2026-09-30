namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>Integer lattice arithmetic that rounds toward negative infinity, as a grid needs.</summary>
internal static class GridMath
{
    /// <summary>The lattice cell a coordinate falls in: floor(value / divisor), for negative values too.</summary>
    internal static long FloorDivide(long value, long divisor)
    {
        long quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }

    /// <summary>The coordinate's offset within its cell, always in [0, divisor).</summary>
    internal static long PositiveMod(long value, long divisor)
    {
        long remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }
}
