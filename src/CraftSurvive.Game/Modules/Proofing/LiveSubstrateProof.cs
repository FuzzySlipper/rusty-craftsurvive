using System.Numerics;
using Rusty.Engine;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using EngineVoxelAddress = Rusty.Engine.VoxelAddress;

namespace CraftSurvive.Game.Modules.Proofing;

/// <summary>
/// S0 staged proof for campaign #8595, run only under
/// <c>CRAFTSURVIVE_PROOF=substrate</c>. It exercises the Engine mechanisms the
/// campaign is priced on against a live product session rather than against
/// documentation: per-cell state surviving an edit and a read, direct-light
/// sampling over a product-created light, and the swim movement mode reporting
/// immersion and submersion from a product-supplied water volume.
/// <para>
/// This is evidence scaffolding, not gameplay. Each slice that ships one of
/// these behaviours replaces its section with real product policy.
/// </para>
/// </summary>
internal sealed class LiveSubstrateProof
{
    internal const string ActivationVariable = "CRAFTSURVIVE_PROOF";
    internal const string ActivationValue = "substrate";

    private const string EvidencePrefix = "[proof]";
    private const int SiteSearchVoxels = 24;
    private const int SiteLiftVoxels = 3;
    private const uint ProofQuarterTurns = 1;
    private const uint ProofVariant = 3;
    private const uint SecondQuarterTurns = 3;
    private const uint EmptyState = 0;
    private const float SampleCellCenter = 0.5f;
    private const float SwimCenterLiftVoxels = 2.5f;
    private const float DirectionalDistance = 32f;
    private const float TorchRange = 12f;
    private const float TorchDecay = 2f;
    private const float TorchIntensity = 4f;
    private const float SpotOuterAngle = 0f;
    private const float SpotPenumbra = 0f;
    private const float LampRed = 1f;
    private const float LampGreen = 0.72f;
    private const float LampBlue = 0.35f;
    private const float UnlitLuminanceTolerance = 0.0001f;
    private const float WaterExtent = 4f;
    private const float WaterHeight = 3f;
    private const float WaterSpeed = 4f;
    private const float WaterAcceleration = 8f;
    private const float WaterDrag = 2f;
    private const float WaterGravityScale = 1f;
    private const float WaterBuoyancy = 1.5f;
    private const float NoClimbReach = 0f;
    private const float VerticalNeutral = 0f;
    private const float ControllerStepSeconds = 1f / 60f;
    private const ulong FirstCommandSequence = 1UL;
    private const ulong SecondCommandSequence = 2UL;
    private const ulong TorchLogicalId = 9001UL;

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly List<string> failures = [];
    private bool completed;

    internal LiveSubstrateProof(IEngineContext engine, TerrainWorld terrain, PlayerController player)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(player);
        this.engine = engine;
        this.terrain = terrain;
        this.player = player;
    }

    internal static bool Requested => string.Equals(
        Environment.GetEnvironmentVariable(ActivationVariable),
        ActivationValue,
        StringComparison.OrdinalIgnoreCase);

    internal void Update()
    {
        if (completed)
        {
            return;
        }

        completed = true;
        try
        {
            Run();
        }
        catch (Exception exception)
        {
            Report($"FAILED without completing: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void Run()
    {
        SpatialSession session = terrain.Session;
        Report($"live substrate proof beginning (player at {Format(player.WorldPosition)})");

        if (!TryFindAirSite(session, out EngineVoxelAddress site))
        {
            Report($"FAILED: no air site found within {SiteSearchVoxels} voxels above the player");
            return;
        }

        Report($"site {Format(site)}");
        ProvePerCellState(session, site);
        ProveDirectLight(session, site);
        ProveSwimMode(session, site);
        ClearSite(session, site);
        ReportAll();
    }

    /// <summary>
    /// The proof edits real voxels, so it puts the addressed cell back. This runs
    /// after the assertions and never fails the proof: a cleanup problem is
    /// reported as its own line.
    /// </summary>
    private void ClearSite(SpatialSession session, EngineVoxelAddress site)
    {
        try
        {
            VoxelSceneReadout scene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(session));
            VoxelEditReceipt cleared = Apply(
                session,
                scene.SourceRevision,
                new VoxelEdit(VoxelEditKind.Clear, site, 0));
            Report($"cleanup: cleared the proof cell, status {cleared.Status}");
        }
        catch (Exception exception)
        {
            Report($"cleanup: could not clear the proof cell ({exception.GetType().Name}: {exception.Message})");
        }
    }

    // ---------------------------------------------------------- per-cell state
    private void ProvePerCellState(SpatialSession session, EngineVoxelAddress site)
    {
        VoxelSceneReadout before = engine.Voxel.ReadScene(new VoxelSceneReadRequest(session));
        uint encoded = VoxelCellState.Encode(ProofQuarterTurns, ProofVariant);
        VoxelEditReceipt placed = Apply(
            session,
            before.SourceRevision,
            new VoxelEdit(encoded, VoxelEditKind.Set, site, TerrainConstants.StoneMaterial));
        Require(placed.Status == VoxelEditStatus.Accepted, $"placing a stateful cell surfaced as {placed.Status}");
        if (placed.Status != VoxelEditStatus.Accepted)
        {
            return;
        }

        VoxelReadout read = engine.Voxel.Read(new VoxelReadRequest(session, site));
        Require(read.Present, "the stateful cell does not read back as present");
        Require(read.State == encoded, $"the cell reads state {read.State}, expected {encoded}");
        Require(
            VoxelCellState.QuarterTurns(read.State) == ProofQuarterTurns,
            $"the cell decodes {VoxelCellState.QuarterTurns(read.State)} quarter turns, expected {ProofQuarterTurns}");
        Require(
            VoxelCellState.Variant(read.State) == ProofVariant,
            $"the cell decodes variant {VoxelCellState.Variant(read.State)}, expected {ProofVariant}");
        Require(
            placed.MeshRevision > before.MeshRevision,
            "placing a stateful cell did not advance the mesh revision");
        Report($"state cell: wrote {encoded}, read {read.State} as quarterTurns={VoxelCellState.QuarterTurns(read.State)} variant={VoxelCellState.Variant(read.State)}; mesh revision {before.MeshRevision} -> {placed.MeshRevision}");

        // A state-only change on an existing cell must be its own accepted edit:
        // rotation and growth stages update without re-placing the block.
        uint rotated = VoxelCellState.Encode(SecondQuarterTurns, EmptyState);
        VoxelEditReceipt restated = Apply(
            session,
            placed.AcceptedRevision,
            new VoxelEdit(rotated, VoxelEditKind.Set, site, TerrainConstants.StoneMaterial));
        Require(restated.Status == VoxelEditStatus.Accepted, $"a state-only edit surfaced as {restated.Status}");
        VoxelReadout after = engine.Voxel.Read(new VoxelReadRequest(session, site));
        Require(after.MaterialSlot == TerrainConstants.StoneMaterial, "a state-only edit changed the material");
        Require(after.State == rotated, $"a state-only edit left state {after.State}, expected {rotated}");
        Require(
            restated.AcceptedRevision > placed.AcceptedRevision,
            "a state-only edit did not advance the accepted revision");
        Report($"state-only edit: material kept at {after.MaterialSlot}, state {read.State} -> {after.State}, accepted revision {placed.AcceptedRevision} -> {restated.AcceptedRevision}");
    }

    // ------------------------------------------------------- direct-light read
    private void ProveDirectLight(SpatialSession session, EngineVoxelAddress site)
    {
        LightDescriptor torch = new(
            LightKind.Point,
            new Vector3(LampRed, LampGreen, LampBlue),
            TorchIntensity,
            true,
            new Vector3(site.X + SampleCellCenter, site.Y + SampleCellCenter, site.Z + SampleCellCenter),
            -Vector3.UnitY,
            true,
            TorchRange,
            TorchDecay,
            SpotOuterAngle,
            SpotPenumbra,
            LightShadowIntent.Disabled);
        using Light light = engine.Graphics.CreateLight(new LightRequest(
            TorchLogicalId,
            false,
            0UL,
            torch));

        LightDescriptor[] lights = [torch];
        VoxelLightSample lit = engine.Voxel.SampleDirectLighting(new VoxelLightSampleRequest
        {
            Session = session,
            Address = site,
            Offset = new Vector3(SampleCellCenter, SampleCellCenter, SampleCellCenter),
            Normal = Vector3.Zero,
            Lights = lights,
            DirectionalDistance = DirectionalDistance,
        });
        Require(lit.ContributingLights >= 1U, $"a lit address reports {lit.ContributingLights} contributing lights");
        Require(lit.Luminance > 0f, $"a lit address reports luminance {lit.Luminance}");

        // Far outside the light's range: the readout must go dark, which is what
        // makes a sealed room without a torch dark.
        VoxelLightSample unlit = engine.Voxel.SampleDirectLighting(new VoxelLightSampleRequest
        {
            Session = session,
            Address = new EngineVoxelAddress(site.X, site.Y + (long)(TorchRange * 4f), site.Z),
            Offset = new Vector3(SampleCellCenter, SampleCellCenter, SampleCellCenter),
            Normal = Vector3.Zero,
            Lights = lights,
            DirectionalDistance = DirectionalDistance,
        });
        Require(
            unlit.Luminance <= UnlitLuminanceTolerance,
            $"an address outside every light reports luminance {unlit.Luminance}, expected darkness");
        Require(
            unlit.ContributingLights == 0U,
            $"an address outside every light reports {unlit.ContributingLights} contributing lights");
        Report($"direct light: lit luminance={lit.Luminance:F4} from {lit.ContributingLights} light(s); unlit luminance={unlit.Luminance:F4} from {unlit.ContributingLights} light(s)");
    }

    // ------------------------------------------------------------- swim mode
    private void ProveSwimMode(SpatialSession session, EngineVoxelAddress site)
    {
        // Step in the clear air above the placed cell: the solver rejects a
        // character that starts inside solid voxels.
        Vector3 center = new(
            site.X + SampleCellCenter,
            site.Y + SwimCenterLiftVoxels,
            site.Z + SampleCellCenter);
        Vector3 minimum = center - new Vector3(WaterExtent, 0f, WaterExtent);
        Vector3 maximum = center + new Vector3(WaterExtent, WaterHeight, WaterExtent);
        CharacterMovementRequest swimming = new()
        {
            Mode = CharacterMovementMode.Swimming,
            VerticalIntent = VerticalNeutral,
            Speed = WaterSpeed,
            Acceleration = WaterAcceleration,
            Drag = WaterDrag,
            Minimum = minimum,
            Maximum = maximum,
            GravityScale = WaterGravityScale,
            Buoyancy = WaterBuoyancy,
            ClimbReach = NoClimbReach,
        };
        CharacterControllerConfig config = engine.Spatial.DefaultCharacterControllerConfig();
        CharacterMotion motion = new(
            Vector3.Zero,
            Vector3.Zero,
            false,
            CharacterStance.Standing,
            0f,
            0f,
            0f,
            false,
            0UL,
            Vector3.Zero,
            Vector3.Zero,
            Quaternion.Identity,
            Vector3.Zero,
            0f,
            0f,
            0UL,
            0UL);
        CharacterSupport support = new(false, CharacterSupportLifecycle.Active, 0UL, default);

        CharacterStepReceipt submerged = engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(
            session,
            center,
            motion,
            support,
            default,
            ReadOnlyMemory<CharacterMeshInstance>.Empty,
            config,
            new CharacterControllerCommand(
                swimming,
                Vector2.Zero,
                0f,
                false,
                false,
                false,
                Vector3.Zero,
                Vector3.Zero,
                ControllerStepSeconds,
                FirstCommandSequence)));
        Require(
            submerged.Movement.Mode == CharacterMovementMode.Swimming,
            $"a swim command produced mode {submerged.Movement.Mode}");
        Require(
            submerged.Movement.Immersion > 0f,
            $"a character inside the water volume reports immersion {submerged.Movement.Immersion}");
        Report($"swim step inside the volume: mode={submerged.Movement.Mode} immersion={submerged.Movement.Immersion:F3} headSubmerged={submerged.Movement.HeadSubmerged}");

        // Outside the volume the same command must fall back to ordinary walking:
        // water is environmental input, not a session-wide mode.
        CharacterStepReceipt dry = engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(
            session,
            center + new Vector3(0f, WaterHeight + TorchRange, 0f),
            motion,
            support,
            default,
            ReadOnlyMemory<CharacterMeshInstance>.Empty,
            config,
            new CharacterControllerCommand(
                swimming,
                Vector2.Zero,
                0f,
                false,
                false,
                false,
                Vector3.Zero,
                Vector3.Zero,
                ControllerStepSeconds,
                SecondCommandSequence)));
        Require(
            dry.Movement.Immersion <= UnlitLuminanceTolerance,
            $"a character outside the water volume reports immersion {dry.Movement.Immersion}");
        Report($"swim step outside the volume: mode={dry.Movement.Mode} immersion={dry.Movement.Immersion:F3} headSubmerged={dry.Movement.HeadSubmerged}");
    }

    // --------------------------------------------------------------- utilities
    private VoxelEditReceipt Apply(SpatialSession session, ulong expectedRevision, VoxelEdit edit)
        => engine.Voxel.ApplyEdits(new VoxelEditTransaction(session, expectedRevision, new VoxelEdit[] { edit }));

    private bool TryFindAirSite(SpatialSession session, out EngineVoxelAddress site)
    {
        Vector3 position = player.WorldPosition;
        long x = (long)Math.Floor(position.X);
        long z = (long)Math.Floor(position.Z);
        long first = (long)Math.Floor(position.Y) + SiteLiftVoxels;
        for (long y = first; y < first + SiteSearchVoxels; y++)
        {
            VoxelReadout cell = engine.Voxel.Read(new VoxelReadRequest(session, new EngineVoxelAddress(x, y, z)));
            VoxelReadout above = engine.Voxel.Read(new VoxelReadRequest(session, new EngineVoxelAddress(x, y + 1, z)));
            VoxelReadout headroom = engine.Voxel.Read(new VoxelReadRequest(session, new EngineVoxelAddress(x, y + 2, z)));
            if (!cell.Present && !above.Present && !headroom.Present)
            {
                site = new EngineVoxelAddress(x, y, z);
                return true;
            }
        }

        site = default;
        return false;
    }

    private void Require(bool condition, string message)
    {
        if (!condition)
        {
            failures.Add(message);
        }
    }

    private void ReportAll()
    {
        if (failures.Count == 0)
        {
            Report("live substrate proof PASSED");
            return;
        }

        foreach (string failure in failures)
        {
            Report($"FAIL {failure}");
        }

        Report($"live substrate proof FAILED with {failures.Count} problem(s)");
    }

    private static void Report(string message) => Console.WriteLine($"{EvidencePrefix} {message}");

    private static string Format(Vector3 value) => FormattableString.Invariant($"({value.X:F2}, {value.Y:F2}, {value.Z:F2})");

    private static string Format(EngineVoxelAddress value) => FormattableString.Invariant($"({value.X}, {value.Y}, {value.Z})");
}
