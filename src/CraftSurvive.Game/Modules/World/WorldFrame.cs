using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The product's one conversion between world coordinates and the spatial session's local frame.
///
/// Engine positions - rays, perception, navigation boxes, character and appearance transforms -
/// are local to the session's world origin, while voxel addresses stay global cells. Product
/// state keeps world coordinates; anything handed to the Engine as a position goes through
/// <see cref="ToLocal(Vector3)"/>. The player owns rebasing and commits it here, and every
/// module that holds local positions subscribes to <see cref="Rebased"/>.
/// </summary>
internal sealed class WorldFrame
{
    private long originX;
    private long originY;
    private long originZ;

    /// <summary>Raised after a committed rebase, with the translation local positions moved by.</summary>
    internal event Action<Vector3>? Rebased;

    internal (long X, long Y, long Z) Origin => (originX, originY, originZ);

    internal Vector3 ToLocal(double worldX, double worldY, double worldZ) => new(
        (float)(worldX - originX),
        (float)(worldY - originY),
        (float)(worldZ - originZ));

    internal Vector3 ToLocal(Vector3 world) => ToLocal(world.X, world.Y, world.Z);

    internal Vector3 ToWorld(Vector3 local) => new(
        (float)(local.X + (double)originX),
        (float)(local.Y + (double)originY),
        (float)(local.Z + (double)originZ));

    /// <summary>Takes the origin the Engine reports, without announcing a rebase.</summary>
    internal void Observe(WorldOriginReadout origin) => SetOrigin(origin.CellX, origin.CellY, origin.CellZ);

    /// <summary>Records a committed rebase and tells every subscriber how far local positions moved.</summary>
    internal void Commit(long cellX, long cellY, long cellZ)
    {
        Vector3 translation = new(originX - cellX, originY - cellY, originZ - cellZ);
        SetOrigin(cellX, cellY, cellZ);
        Rebased?.Invoke(translation);
    }

    private void SetOrigin(long cellX, long cellY, long cellZ)
    {
        originX = cellX;
        originY = cellY;
        originZ = cellZ;
    }
}
