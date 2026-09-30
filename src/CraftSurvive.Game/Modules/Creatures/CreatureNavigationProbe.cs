using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// Asks the Engine whether a creature could walk to the player, on request only. Creatures chase
/// in a straight line today; this is how that route question is inspected until navigation drives
/// movement. It publishes collision navigation around the player and evaluates one bounded step
/// from the creature's feet, both in the session's local frame as the Engine's world-aligned grid
/// requires.
/// </summary>
internal sealed class CreatureNavigationProbe
{
    private const ulong GridId = 1UL;
    private const uint ChunkSize = 16U;
    private const uint MaximumStepCells = 1U;
    private const uint MaximumCells = 65_536U;
    private const double AgentRadius = 0.3d;
    private const double AgentHeight = 1.8d;
    private const double MaximumSlopeDegrees = 45d;
    private const uint MaximumVisitedCells = 4_096U;

    /// <summary>Half the width of the published box: it must hold the creature as well as the player.</summary>
    private const float HalfExtent = 64f;

    private const float DepthBelow = 4f;
    private const float HeightAbove = 8f;

    /// <summary>How far one evaluated step may propose to move.</summary>
    private const float MaximumStepMetres = 4f;

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly WorldFrame frame;

    internal CreatureNavigationProbe(IEngineContext engine, TerrainWorld terrain, WorldFrame frame)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    /// <summary>Publishes navigation around the player and evaluates one step from the creature to them.</summary>
    internal string Probe(Vector3 creatureFeetWorld, Vector3 playerFeetWorld)
    {
        Vector3 playerLocal = frame.ToLocal(playerFeetWorld);
        Vector3 creatureLocal = frame.ToLocal(creatureFeetWorld);
        NavigationReplaceReceipt published = engine.Spatial.ReplaceCollisionNavigation(new CollisionNavigationReplaceRequest(
            terrain.Session,
            playerLocal - new Vector3(HalfExtent, DepthBelow, HalfExtent),
            playerLocal + new Vector3(HalfExtent, HeightAbove, HalfExtent),
            new CollisionNavigationConfig(
                GridId,
                TerrainConstants.VoxelSize,
                ChunkSize,
                MaximumStepCells,
                AgentRadius,
                AgentHeight,
                MaximumSlopeDegrees,
                MaximumCells)));
        NavigationStepResult step = engine.Spatial.EvaluateNavigationStep(new NavigationStepRequest(
            terrain.Session,
            creatureLocal,
            playerLocal,
            MaximumStepMetres,
            MaximumVisitedCells));
        Vector3 waypoint = frame.ToWorld(step.NextWaypoint);
        return string.Create(CultureInfo.InvariantCulture,
            $"walkable={published.WalkableCellCount} revision={published.NavigationRevision} outcome={step.Outcome} "
            + $"path={step.Path.Length} visited={step.Visited} next={waypoint.X:F1},{waypoint.Y:F1},{waypoint.Z:F1} "
            + $"from={creatureFeetWorld.X:F1},{creatureFeetWorld.Y:F1},{creatureFeetWorld.Z:F1} "
            + $"to={playerFeetWorld.X:F1},{playerFeetWorld.Y:F1},{playerFeetWorld.Z:F1}");
    }
}
