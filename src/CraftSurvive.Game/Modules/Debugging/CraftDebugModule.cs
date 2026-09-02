using System.Globalization;
using CraftSurvive.Game.Modules.GhostPlate;
using CraftSurvive.Game.Modules.Microvoxels;
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
    private readonly GhostPlateActor ghost;
    private readonly MicrovoxelPresentation microvoxels;
    private readonly DebugExecutionContext execution;

    internal CraftDebugModule(
        PlayerController player,
        TerrainWorld terrain,
        GhostPlateActor ghost,
        MicrovoxelPresentation microvoxels,
        DebugExecutionContext execution)
    {
        this.player = player;
        this.terrain = terrain;
        this.ghost = ghost;
        this.microvoxels = microvoxels;
        this.execution = execution;
    }

    [DebugCommand("craft.ghost.preset", Description = "Queues accepted, wide, strict, or scene-lighting ghost settings for the next product update.")]
    public string SetGhostPreset(string preset) => ghost.QueuePreset(preset);

    [DebugCommand("craft.ghost.capture", Description = "Queues ghost capture resolution, framing, clip range, and lighting mode.")]
    public string SetGhostCapture(
        ushort resolution,
        float azimuthDegrees,
        float elevationDegrees,
        float near,
        float far,
        float fieldOfViewDegrees,
        GhostPlateCaptureLightingMode lightingMode)
        => ghost.QueueCapture(
            resolution,
            azimuthDegrees,
            elevationDegrees,
            near,
            far,
            fieldOfViewDegrees,
            lightingMode);

    [DebugCommand("craft.ghost.lighting", Description = "Queues ghost capture lighting mode and ambient, key, and fill intensities.")]
    public string SetGhostLighting(
        GhostPlateCaptureLightingMode lightingMode,
        float ambientIntensity,
        float keyIntensity,
        float fillIntensity)
        => ghost.QueueLighting(lightingMode, ambientIntensity, keyIntensity, fillIntensity);

    [DebugCommand("craft.ghost.relief", Description = "Queues ghost depth, anchor, mapping, shell, and shell-tolerance values.")]
    public string SetGhostRelief(
        float depthRetention,
        GhostPlateAnchorPolicy anchorPolicy,
        float anchorValue,
        GhostPlateMapping plateMapping,
        GhostPlateShellMode shellMode,
        float shellDepthEpsilon)
        => ghost.QueueRelief(
            depthRetention,
            anchorPolicy,
            anchorValue,
            plateMapping,
            shellMode,
            shellDepthEpsilon);

    [DebugCommand("craft.ghost.direction", Description = "Queues a 1, 4, 8, or 16-sector hard-snap bank and hysteresis in degrees.")]
    public string SetGhostDirection(byte sectorCount, float hysteresisDegrees)
        => ghost.QueueDirection(sectorCount, hysteresisDegrees);

    [DebugCommand("craft.ghost.place", Description = "Queues ghost world placement and plate size.")]
    public string PlaceGhost(float x, float y, float z, float width, float height)
        => ghost.QueuePlacement(x, y, z, width, height);

    [DebugCommand("craft.ghost.visible", Description = "Queues ghost presentation visibility through its ordinary Engine lifecycle.")]
    public string SetGhostVisible(bool visible) => ghost.QueueVisibility(visible);

    [DebugCommand("craft.ghost.recapture", Description = "Queues an explicit ghost capture-bank rebuild.")]
    public string RecaptureGhost() => ghost.QueueRecapture();

    [DebugCommand("craft.ghost.readout", Description = "Reads selected ghost source, tuning state, and latest Engine presentation facts.")]
    public string ReadGhost() => ghost.DebugReadout();

    [DebugCommand("craft.micro.preset", Description = "Queues accepted, close, or compact microvoxel settings for the next product update.")]
    public string SetMicrovoxelPreset(string preset) => microvoxels.QueuePreset(preset);

    [DebugCommand("craft.micro.place", Description = "Queues microvoxel world placement.")]
    public string PlaceMicrovoxel(float x, float y, float z)
        => microvoxels.QueuePlacement(x, y, z);

    [DebugCommand("craft.micro.scale", Description = "Queues microvoxel scale on each axis.")]
    public string ScaleMicrovoxel(float x, float y, float z)
        => microvoxels.QueueScale(x, y, z);

    [DebugCommand("craft.micro.material", Description = "Queues the common matte roughness used by the microvoxel palette.")]
    public string SetMicrovoxelMaterial(float roughness)
        => microvoxels.QueueMaterial(roughness);

    [DebugCommand("craft.micro.visible", Description = "Queues microvoxel retained-presentation visibility.")]
    public string SetMicrovoxelVisible(bool visible)
        => microvoxels.QueueVisibility(visible);

    [DebugCommand("craft.micro.readout", Description = "Reads selected microvoxel source, tuning state, and Engine presentation totals.")]
    public string ReadMicrovoxel() => microvoxels.DebugReadout();

    [DebugCommand("craft.player.teleport", Description = "Moves the live player through CraftSurvive's ordinary player owner.")]
    public string Teleport(double x, double y, double z)
    {
        PlayerRuntimeComponent state = player.Teleport(x, y, z);
        return FormattableString.Invariant($"player={state.X:F3},{state.Y:F3},{state.Z:F3}");
    }

    [DebugCommand("craft.player.readout", Description = "Reads the latest admitted player input, fixed-step, motion, and pose facts.")]
    public string ReadPlayer() => player.DebugReadout();

    [DebugCommand("craft.terrain.scene", Description = "Reads the current Engine-owned voxel scene facts.")]
    public string ReadTerrainScene()
    {
        VoxelSceneReadout scene = terrain.ReadScene();
        return string.Create(CultureInfo.InvariantCulture,
            $"present={scene.Present};revision={scene.SourceRevision};chunks={scene.ResidentChunkCount};solidVoxels={scene.SolidVoxelCount}");
    }

    [DebugCommand("craft.terrain.materials", Description = "Reads the copied Engine directional terrain material mapping.")]
    public string ReadTerrainMaterials()
    {
        VoxelSceneMaterialMappingLeaseReceipt mapping = terrain.ReadMaterialMapping();
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
