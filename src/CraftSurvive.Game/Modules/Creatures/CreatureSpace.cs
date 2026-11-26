using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Wildlife;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// A space creatures live in (#10876, Do 1): what a creature needs from where it is - the spatial session its body steps,
/// routes, sight and fall are on, its coordinates, residency, ground, the water its routes keep out of, and whether the
/// space is live. The walking session provides one (<see cref="WalkingCreatureSpace"/>: the overworld and the caves under
/// it), and each separate space its own (a loaded dungeon). Creatures, navigation and the dead reach Engine services only
/// through it.
/// </summary>
internal abstract class CreatureSpace : ICreatureGround
{
    /// <summary>A short name for readouts.</summary>
    internal abstract string Name { get; }

    /// <summary>The session the space's collision, navigation and perception live in.</summary>
    internal abstract SpatialSession Session { get; }

    public abstract bool Live { get; }

    /// <summary>Whether the space has been let go: its creatures go with it.</summary>
    internal virtual bool Gone => false;

    /// <summary>A point of the space's world metres in its session's local frame.</summary>
    internal abstract Vector3 ToLocal(Vector3 world);

    internal Vector3 ToLocal(double x, double y, double z) => ToLocal(new Vector3((float)x, (float)y, (float)z));

    /// <summary>A point of the session's local frame in the space's world metres.</summary>
    internal abstract Vector3 ToWorld(Vector3 local);

    public abstract bool IsResident(Vector3 world);

    /// <summary>The ground of a column from above: where a creature is first set down when nothing else says.</summary>
    internal abstract float GroundAt(double x, double z);

    public abstract float? FloorBelow(double x, double z, double feet, double climb);

    public abstract bool UnderRock(Vector3 feet);

    /// <summary>The water over a column (its top in world metres, depth over its bed, frozen), or null for none.</summary>
    internal virtual (double Top, double Depth, bool Ice)? WaterAt(long x, long z) => null;

    /// <summary>Changes whenever the water may have: samples are asked again.</summary>
    internal virtual long WaterRevision => 0;

    /// <summary>The ruin doorways in this space (#10961), placed in its own metres: what a mender seals. None by default.</summary>
    internal virtual IReadOnlyList<Dungeons.RuinDoorway> Doorways => [];

    /// <summary>Changes whenever the collision may have: navigation is published again, and columns are explained again.</summary>
    internal abstract object CollisionRevision { get; }

    /// <summary>The most two points of the space can differ in height: the sight query's reach covers it.</summary>
    internal abstract double MaximumHeightDifference { get; }

    /// <summary>Raised when the space's local frame moves under its world metres (an origin rebase): bodies and published routes are set afresh.</summary>
    internal event Action? Rebased;

    /// <summary>Raised with the Engine's receipt when the origin moves, for the dead's physics world to move with it.</summary>
    internal event Action<WorldOriginCommitReceipt>? Committed;

    protected void RaiseRebased() => Rebased?.Invoke();

    protected void RaiseCommitted(WorldOriginCommitReceipt receipt) => Committed?.Invoke(receipt);
}

/// <summary>
/// The walking session as a space for creatures (#10876): the overworld and the caves under it, one session that the
/// terrain streams and the world's origin carries. It is live while the player walks it, not while they are in a separate
/// space.
/// </summary>
internal sealed class WalkingCreatureSpace : CreatureSpace
{
    /// <summary>A point this far under its column's overworld ground is under rock (a cave).</summary>
    private const float UnderRockMetres = 4f;

    /// <summary>A standing eye above the ground, so sight between the world's lowest and highest points is reached.</summary>
    private const double EyeHeightMetres = 1.5;

    private readonly TerrainWorld terrain;
    private readonly WorldFrame frame;
    private readonly Func<bool> live;

    internal WalkingCreatureSpace(TerrainWorld terrain, WorldFrame frame, Func<bool> live)
    {
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.live = live ?? throw new ArgumentNullException(nameof(live));
        frame.Rebased += _ => RaiseRebased();
        frame.Committed += RaiseCommitted;
    }

    internal override string Name => "walking";

    internal override SpatialSession Session => terrain.Session;

    public override bool Live => live();

    internal override Vector3 ToLocal(Vector3 world) => frame.ToLocal(world);

    internal override Vector3 ToWorld(Vector3 local) => frame.ToWorld(local);

    public override bool IsResident(Vector3 world) =>
        terrain.IsResident(new VoxelAddress((long)Math.Floor(world.X), (long)Math.Floor(world.Y), (long)Math.Floor(world.Z)));

    internal override float GroundAt(double x, double z) => terrain.GroundAt(x, z);

    public override float? FloorBelow(double x, double z, double feet, double climb) => terrain.GroundBelow(x, z, feet, climb);

    public override bool UnderRock(Vector3 feet) =>
        feet.Y < terrain.Recipe.SurfaceAt((long)Math.Floor(feet.X), (long)Math.Floor(feet.Z)) - UnderRockMetres;

    internal override (double Top, double Depth, bool Ice)? WaterAt(long x, long z)
    {
        Water.WaterSample sample = terrain.Water.Sample(x, z);
        return sample.Wet ? (sample.Top, sample.Depth, sample.Ice) : null;
    }

    internal override long WaterRevision => terrain.WaterRevision;

    /// <summary>The ruin doorways of the caves under the world admitted near the player, in world metres (#11097).</summary>
    internal Func<IReadOnlyList<Dungeons.RuinDoorway>>? CaveDoorways { get; set; }

    internal override IReadOnlyList<Dungeons.RuinDoorway> Doorways => CaveDoorways?.Invoke() ?? [];

    internal override object CollisionRevision => (terrain.EditRevision, terrain.StaticCollisionRevision, frame.Origin);

    internal override double MaximumHeightDifference => terrain.Recipe.MaximumMaterialY - terrain.Recipe.MinimumMaterialY + EyeHeightMetres;
}

/// <summary>A hostile animal as wildlife's bodies draw it this update, and the space it lives in (#10876): its position and feet are that space's world metres.</summary>
internal readonly record struct WildlifeHunter(WildlifeKind Kind, WildlifeMember Member, float Feet, CreatureSpace Space);
