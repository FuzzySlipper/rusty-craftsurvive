using System.Numerics;
using CraftSurvive.Game.Modules.Creatures;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A loaded dungeon as a space for creatures (#10876, Do 1): its own finite session, never rebased or streamed, its origin at
/// zero so its local and world metres agree. All of it is loaded while it is live, its floors are found by casting down its
/// collision, it has no water the routes keep out of, and it is let go when the dungeon is.
/// </summary>
internal sealed class DungeonCreatureSpace(IEngineContext engine, DungeonSpace space, Func<bool> live) : CreatureSpace
{
    /// <summary>A column's ground is looked for from this far over the space's highest point.</summary>
    private const float AboveTopMetres = 1f;

    /// <summary>A cast below a body looks this far under its reach.</summary>
    private const double SearchBelowMetres = 16d;

    private readonly IEngineContext engine = engine ?? throw new ArgumentNullException(nameof(engine));
    private readonly DungeonSpace space = space ?? throw new ArgumentNullException(nameof(space));
    private readonly Func<bool> live = live ?? throw new ArgumentNullException(nameof(live));
    private bool gone;

    internal override string Name => $"dungeon:{space.Layout.Name}";

    internal override SpatialSession Session => space.Session;

    public override bool Live => !gone && live();

    internal override bool Gone => gone;

    /// <summary>The dungeon has been let go: its creatures go with it.</summary>
    internal void Let() => gone = true;

    internal override Vector3 ToLocal(Vector3 world) => world;

    internal override Vector3 ToWorld(Vector3 local) => local;

    /// <summary>A dungeon is loaded whole before the player enters it: every point of it is resident while it is live.</summary>
    public override bool IsResident(Vector3 world) => !gone;

    internal override float GroundAt(double x, double z)
    {
        double top = Top + AboveTopMetres;
        return Cast(x, z, top, top - Bottom) ?? (float)Bottom;
    }

    public override float? FloorBelow(double x, double z, double feet, double climb) => Cast(x, z, feet + climb, climb + SearchBelowMetres);

    /// <summary>A dungeon is rock or building all round: a floor is only ever dropped to, not risen onto, without walking.</summary>
    public override bool UnderRock(Vector3 feet) => true;

    internal override object CollisionRevision => space;

    internal override double MaximumHeightDifference => Top - Bottom;

    /// <summary>The session's height span: the volume's from its origin.</summary>
    private double Bottom => DungeonSpace.Origin.Y;

    private double Top => DungeonSpace.Origin.Y + (space.Layout.Volume.SizeY * space.Layout.VoxelSize);

    private float? Cast(double x, double z, double from, double length)
    {
        SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(
            space.Session, new Vector3((float)x, (float)from, (float)z), -Vector3.UnitY, (float)length,
            new SpatialQueryFilter(Terrain.TerrainConstants.CollisionGroupAll, Terrain.TerrainConstants.CollisionMaskAll),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty,
            ReadOnlyMemory<SpatialEntityCollider>.Empty));
        return hit.Present ? (float)(from - hit.Distance) : null;
    }
}
