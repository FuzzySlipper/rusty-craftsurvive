using System.Globalization;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Debugging;

/// <summary>Thin live-debug adapters over ordinary CraftSurvive and Engine operations.</summary>
public sealed class CraftDebugModule : IDebugCommandModule
{
    private readonly PlayerController player;
    private readonly TerrainWorld terrain;
    private readonly DebugExecutionContext execution;

    internal CraftDebugModule(
        PlayerController player,
        TerrainWorld terrain,
        DebugExecutionContext execution)
    {
        this.player = player;
        this.terrain = terrain;
        this.execution = execution;
    }

    [DebugCommand("craft.player.teleport", Description = "Moves the live player through CraftSurvive's ordinary player owner.")]
    public string Teleport(double x, double y, double z)
    {
        PlayerRuntimeComponent state = player.Teleport(x, y, z);
        return FormattableString.Invariant($"player={state.X:F3},{state.Y:F3},{state.Z:F3}");
    }

    [DebugCommand("craft.terrain.scene", Description = "Reads the current Engine-owned voxel scene facts.")]
    public string ReadTerrainScene()
    {
        VoxelSceneReadout scene = terrain.ReadScene();
        return string.Create(CultureInfo.InvariantCulture,
            $"present={scene.Present};revision={scene.SourceRevision};chunks={scene.ResidentChunkCount};solidVoxels={scene.SolidVoxelCount}");
    }

    [DebugCommand("craft.runtime", Description = "Reads the latest committed Rust host lifecycle and binding facts.")]
    public string ReadRuntime()
    {
        DebugExecutionSnapshot snapshot = execution.Snapshot;
        return string.Create(CultureInfo.InvariantCulture,
            $"state={snapshot.LifecycleState};generation={snapshot.Generation?.ToString(CultureInfo.InvariantCulture) ?? "unknown"};controlRevision={snapshot.ControlRevision?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
    }
}
