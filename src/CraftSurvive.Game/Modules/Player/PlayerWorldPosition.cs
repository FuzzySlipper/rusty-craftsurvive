using System.Numerics;
using Rusty.Engine;
using TerrainVoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>Canonical product global coordinate paired with Engine world-origin facts.</summary>
internal readonly record struct PlayerWorldPosition(long CellX, long CellY, long CellZ, double OffsetX, double OffsetY, double OffsetZ)
{
    internal static PlayerWorldPosition FromWorld(Vector3 position) => new(
        FloorCell(position.X), FloorCell(position.Y), FloorCell(position.Z),
        Fraction(position.X), Fraction(position.Y), Fraction(position.Z));

    internal static PlayerWorldPosition FromWorld(double x, double y, double z) => new(
        FloorCell(x), FloorCell(y), FloorCell(z), Fraction(x), Fraction(y), Fraction(z));

    internal static PlayerWorldPosition FromLocal(WorldOriginReadout origin, Vector3 local) => new(
        checked(origin.CellX + FloorCell(local.X)),
        checked(origin.CellY + FloorCell(local.Y)),
        checked(origin.CellZ + FloorCell(local.Z)),
        Fraction(local.X),
        Fraction(local.Y),
        Fraction(local.Z));

    internal WorldOriginGlobalPosition ToEngine() => new(CellX, CellY, CellZ,
        OffsetX, OffsetY, OffsetZ);

    internal Vector3 ToLocal(WorldOriginReadout origin) => new(
        checked((float)((CellX - origin.CellX) + OffsetX)),
        checked((float)((CellY - origin.CellY) + OffsetY)),
        checked((float)((CellZ - origin.CellZ) + OffsetZ)));

    internal Vector3 ToWorldVector() => new(
        checked((float)(CellX + OffsetX)),
        checked((float)(CellY + OffsetY)),
        checked((float)(CellZ + OffsetZ)));

    internal TerrainVoxelAddress FloorVoxel() => new(CellX, CellY, CellZ);

    internal double WorldX => CellX + OffsetX;

    internal double WorldY => CellY + OffsetY;

    internal double WorldZ => CellZ + OffsetZ;

    private static long FloorCell(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "World positions must be finite.");
        }

        return checked((long)Math.Floor(value));
    }

    private static double Fraction(double value) => value - Math.Floor(value);
}
