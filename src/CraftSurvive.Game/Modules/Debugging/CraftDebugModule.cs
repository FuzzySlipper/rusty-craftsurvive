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
    private readonly PlayerController player;
    private readonly CreatureModule creatures;
    private readonly TerrainWorld terrain;
    private readonly DebugExecutionContext execution;

    internal CraftDebugModule(
        PlayerController player,
        CreatureModule creatures,
        TerrainWorld terrain,
        DebugExecutionContext execution)
    {
        this.player = player;
        this.creatures = creatures;
        this.terrain = terrain;
        this.execution = execution;
    }

    [DebugCommand("craft.player.teleport", Description = "Moves the live player where a standing body fits: the point asked for, else the ground of that column.")]
    public string Teleport(double x, double y, double z) =>
        player.Teleport(x, y, z) is PlayerRuntimeComponent state
            ? FormattableString.Invariant($"player={state.X:F3},{state.Y:F3},{state.Z:F3}")
            : FormattableString.Invariant($"refused: a standing body fits neither at {x:F3},{y:F3},{z:F3} nor on the ground below it");

    [DebugCommand("craft.player.camera", Description = "Selects latest, position, or pose camera presentation and delay in seconds.")]
    public string SetCameraPresentation(string mode, double delaySeconds) =>
        Enum.TryParse(mode, ignoreCase: true, out CameraInterpolation selected) && Enum.IsDefined(selected)
            ? player.SetCameraPresentation(selected, delaySeconds)
            : $"camera mode must be one of {string.Join(", ", Enum.GetNames<CameraInterpolation>())}";

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
            $"present={scene.Present};revision={scene.SourceRevision};chunks={scene.ResidentChunkCount};solidVoxels={scene.SolidVoxelCount}");
    }

    [DebugCommand("craft.terrain.generation", Description = "Reads the generator's version, live fingerprint, golden status and chunk cache.")]
    public string ReadGeneration() => terrain.GenerationReadout();

    [DebugCommand("craft.terrain.layout", Description = "Reads the selected product terrain layout and its stable dimensions.")]
    public string ReadTerrainLayout() => terrain.ReadLayout();

    [DebugCommand("craft.terrain.edit", Description = "Reads the latest terrain target and typed edit outcome.")]
    public string ReadTerrainEdit() => player.TerrainEditReadout();

    [DebugCommand("craft.terrain.timing", Description = "Switches recording of each edit's cost by part (1 on, 0 off); the blast readout reports it.")]
    public string SetEditTiming(long enabled)
    {
        terrain.EditTimingEnabled = enabled != 0;
        return $"editTiming={(terrain.EditTimingEnabled ? "on" : "off")}";
    }

    [DebugCommand("craft.terrain.materials", Description = "Reads the copied Engine directional terrain material mapping.")]
    public string ReadTerrainMaterials()
    {
        VoxelSceneMaterialMappingResult mapping = terrain.ReadMaterialMapping();
        uint grassRows = 0;
        uint dirtRows = 0;
        uint stoneRows = 0;
        bool grassTopOverride = false;
        foreach (VoxelSceneMaterialMappingRow row in mapping.Mappings.Span)
        {
            switch (row.SourceSlot)
            {
                case TerrainConstants.GrassMaterial:
                    grassRows++;
                    grassTopOverride |= row.Face == SpatialFace.PosY && row.Overridden;
                    break;
                case TerrainConstants.DirtMaterial:
                    dirtRows++;
                    break;
                case TerrainConstants.StoneMaterial:
                    stoneRows++;
                    break;
            }
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"rows={mapping.Mappings.Length};source1={grassRows};source2={dirtRows};source3={stoneRows};grassTop+Y={grassTopOverride};sourceRevision={mapping.SourceRevision};meshRevision={mapping.MeshRevision}");
    }

    [DebugCommand("craft.runtime", Description = "Reads the latest committed Rust host lifecycle and binding facts.")]
    public string ReadRuntime()
    {
        DebugExecutionSnapshot snapshot = execution.Snapshot;
        return string.Create(CultureInfo.InvariantCulture,
            $"state={snapshot.LifecycleState};generation={snapshot.Generation?.ToString(CultureInfo.InvariantCulture) ?? "unknown"};controlRevision={snapshot.ControlRevision?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
    }
}
