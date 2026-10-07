using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Debugging;

/// <summary>Thin live-debug adapters over ordinary CraftSurvive and Engine operations.</summary>
public sealed class CraftDebugModule : IDebugCommandModule
{
    private readonly Func<PlayerController> playerSource;
    private PlayerController player => playerSource();
    private readonly Func<CreatureModule> creaturesSource;
    private CreatureModule creatures => creaturesSource();
    private readonly Func<TerrainWorld> terrainSource;
    private TerrainWorld terrain => terrainSource();
    private readonly DebugExecutionContext execution;

    internal CraftDebugModule(
        Func<PlayerController> player,
        Func<CreatureModule> creatures,
        Func<TerrainWorld> terrain,
        DebugExecutionContext execution)
    {
        this.playerSource = player;
        this.creaturesSource = creatures;
        this.terrainSource = terrain;
        this.execution = execution;
    }

    [DebugCommand("craft.player.landscape", Description = "Assisted: visits a landscape study (canyon, uplands or tundra) as the menu's Visit landscape does; the player readout reports its admission.")]
    public string VisitLandscape(string name) => player.VisitLandscape(name) ? $"visiting {name}" : $"refused: no study {name} here";

    [DebugCommand("craft.player.teleport", Description = "Moves the live player where a standing body fits: the point asked for, else the ground of that column.")]
    public string Teleport(double x, double y, double z) =>
        player.Teleport(x, y, z) is PlayerRuntimeComponent state
            ? FormattableString.Invariant($"player={state.X:F3},{state.Y:F3},{state.Z:F3}")
            : FormattableString.Invariant($"refused: a standing body fits neither at {x:F3},{y:F3},{z:F3} nor on the ground below it");

    [DebugCommand("craft.player.look", Description = "Assisted: turns the live view by degrees of yaw and pitch through the player's own look rules, for aimed captures.")]
    public string LookBy(double yawDegrees, double pitchDegrees)
    {
        player.LookBy(yawDegrees, pitchDegrees);
        return player.DebugReadout();
    }

    [DebugCommand("craft.player.readout", Description = "Reads the latest admitted player input, fixed-step, motion, and pose facts.")]
    public string ReadPlayer() => player.DebugReadout();

    [DebugCommand("craft.player.continuation", Description = "Reports what the last session's continuation restored and how its save is going.")]
    public string Continuation() => player.ContinuationReadout();

    [DebugCommand("craft.player.attack", Description = "Swings at the nearest creature, as the attack key does.")]
    public string Attack() => creatures.PlayerAttack();

    [DebugCommand("craft.player.strike", Description = "Deals damage to the player through their vitals, so an encounter can be lost as well as won.")]
    public string StrikePlayer(int damage)
    {
        PlayerDefeatState state = player.Vitals.TakeHit(damage, player.CurrentStep);
        return FormattableString.Invariant(
            $"health={state.Health}/{state.MaximumHealth} defeats={state.Defeats} respawnStep={state.RespawnTick} outcome={player.Vitals.Outcome(player.CurrentStep)}");
    }

    [DebugCommand("craft.terrain.scene", Description = "Reads the current Engine-owned voxel scene facts.")]
    public string ReadTerrainScene()
    {
        VoxelSceneReadout scene = terrain.ReadScene();
        return string.Create(CultureInfo.InvariantCulture,
            $"present={scene.Present};revision={scene.SourceRevision};chunks={scene.ResidentChunkCount};solidVoxels={scene.SolidVoxelCount};{terrain.LevelOfDetailReadout()};{terrain.FarFieldReadout()}");
    }

    [DebugCommand("craft.terrain.vertexocclusion", Description = "Assisted (#9506 exploration): darkens the overworld's surface vertices by the solid voxels around them at the given strength (0 off, 1 full); every resident chunk remeshes.")]
    public string TerrainVertexOcclusion(float strength)
    {
        VoxelSceneReadout scene = terrain.ConfigureVertexOcclusion(strength);
        return string.Create(CultureInfo.InvariantCulture, $"vertexOcclusion={strength:F2};chunks={scene.ResidentChunkCount}");
    }

    [DebugCommand("craft.terrain.generation", Description = "Reads the generator's version, live fingerprint, golden status and chunk cache.")]
    public string ReadGeneration() => terrain.GenerationReadout();

    [DebugCommand("craft.terrain.edit", Description = "Reads the latest terrain target and typed edit outcome.")]
    public string ReadTerrainEdit() => player.TerrainEditReadout();

    [DebugCommand("craft.terrain.timing", Description = "Switches recording of each edit's cost by part (1 on, 0 off); the blast readout reports it.")]
    public string SetEditTiming(long enabled)
    {
        terrain.EditTimingEnabled = enabled != 0;
        return $"editTiming={(terrain.EditTimingEnabled ? "on" : "off")}";
    }

    [DebugCommand("craft.runtime", Description = "Reads the latest committed Rust host lifecycle and binding facts.")]
    public string ReadRuntime()
    {
        DebugExecutionSnapshot snapshot = execution.Snapshot;
        return string.Create(CultureInfo.InvariantCulture,
            $"state={snapshot.LifecycleState};generation={snapshot.Generation?.ToString(CultureInfo.InvariantCulture) ?? "unknown"};controlRevision={snapshot.ControlRevision?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
    }
}
