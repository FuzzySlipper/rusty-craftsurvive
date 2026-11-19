using System.Numerics;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// What the creature rules ask of where a creature lives (#10876), Engine-free. The walking session (the overworld and the
/// caves under it) and each separate space provide it (<see cref="CreatureSpace"/>). Every point here is in the space's own
/// world metres, and nothing in the rules knows which kind of space it is.
/// </summary>
internal interface ICreatureGround
{
    /// <summary>Whether the space is being simulated now: the player is in it. Its creatures sleep while it is not.</summary>
    bool Live { get; }

    /// <summary>Whether the ground under a point is loaded: drawn, and solid to collision and sight.</summary>
    bool IsResident(Vector3 world);

    /// <summary>The first floor under a body that stood at <paramref name="feet"/> and can climb <paramref name="climb"/> metres; null where none is loaded.</summary>
    float? FloorBelow(double x, double z, double feet, double climb);

    /// <summary>Whether rock may stand over a point: a cast from a little above it can meet a ceiling from inside the rock.</summary>
    bool UnderRock(Vector3 feet);
}

/// <summary>A creature's life in its space, as rules over <see cref="ICreatureGround"/> (#10876): the same in every space.</summary>
internal static class CreatureSpaceRules
{
    /// <summary>A floor found this little above a creature's feet is the floor it stands on, not one it would rise onto.</summary>
    internal const float RiseToleranceMetres = 0.05f;

    /// <summary>A creature is awake while its space is live and the block it stands on is loaded; otherwise it lies dormant.</summary>
    internal static bool Awake(ICreatureGround space, Vector3 feet)
    {
        ArgumentNullException.ThrowIfNull(space);
        return space.Live && space.IsResident(feet - Vector3.UnitY);
    }

    /// <summary>
    /// Where a creature that has not walked stands next (#9734): on the first floor below its reach from its feet, so under a
    /// roof it stays on the floor it walked onto. Where rock may stand over it, it only ever drops: a surface above its feet is
    /// a ceiling met from inside the rock, and rising onto it would carry it up through the rock (#10876). Null keeps its feet.
    /// </summary>
    internal static float? Followed(ICreatureGround space, Vector3 feet, double climb)
    {
        ArgumentNullException.ThrowIfNull(space);
        float? below = space.FloorBelow(feet.X, feet.Z, feet.Y, climb);
        if (below > feet.Y + RiseToleranceMetres && space.UnderRock(feet)) return null;
        return below;
    }
}

/// <summary>Ground for a creature that lives nowhere in particular (a rule check, or a creature placed before it has a space): always live and loaded, with no floor to find.</summary>
internal sealed class AnyGround : ICreatureGround
{
    internal static AnyGround Instance { get; } = new();

    public bool Live => true;

    public bool IsResident(Vector3 world) => true;

    public float? FloorBelow(double x, double z, double feet, double climb) => null;

    public bool UnderRock(Vector3 feet) => false;
}
