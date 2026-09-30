using CraftSurvive.Game.Modules.Content;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Rusty.Engine;
using Rusty.Engine.Entities;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using EngineVoxelAddress = Rusty.Engine.VoxelAddress;
using TerrainVoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;
using CraftSurvive.Game.Modules.WorldGen;

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
    private const long NavGoalCells = 4L;
    private const long NavLevelSweep = 12L;
    private const uint MaxVisitedCells = 4096U;
    private const ulong NavigationGridId = 1UL;
    private const uint NavigationChunkSize = 16U;
    private const uint NavigationMaxStepCells = 1U;
    private const uint NavigationMaximumCells = 65_536U;
    private const double NavigationAgentRadius = 0.3d;
    private const double NavigationAgentHeight = 1.8d;
    private const double NavigationMaximumSlopeDegrees = 45d;
    private const float NavigationHalfExtent = 16f;
    private const float NavigationDepthBelow = 4f;
    private const float NavigationHeightAbove = 8f;
    private const int MaximumProjectedEntities = 8;
    private const float MarkerRed = 0.85f;
    private const float MarkerGreen = 0.25f;
    private const float MarkerBlue = 0.35f;
    private const float MarkerAlpha = 1f;
    private const string PersistenceProofScope = "craftsurvive.proof";
    private const string PersistenceProofKey = "proof/roundtrip";
    private const float EditAimLiftVoxels = 4f;
    private static readonly bool PlantStaleFromEnvironment = string.Equals(
        Environment.GetEnvironmentVariable("CRAFTSURVIVE_PLANT_STALE"), "1", StringComparison.Ordinal);
    private const int ResidencyDiagnosticFrames = 60;
    private const int MaximumResidencyAttempts = 4;
    private const uint DungeonChunkSize = 8U;
    private const int DungeonProbeVoxel = 3;
    private const long ResidencyTargetChunkOffset = 5L;

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly List<string> failures = [];
    private int stage;
    private bool completed;
    private int swimStage;
    private int swimAttempts;
    private int swimUpdates;
    private int swimPaceUpdates;
    private int tickCount;
    private double tickTotalMs;
    private double tickMaximumMs;
    private const int TickReportInterval = 5;
    private bool finishRequested;
    private bool swimDone;
    private const int MaximumSwimAttempts = 8;
    private const int WaterSearchRadius = 60;
    private const int WaterDepth = 3;
    private const float RaycastLift = 6f;
    private const float RaycastReach = 16f;
    private const float SwimDropHeight = 1.7f;
    private const int ReportedSwimAttempts = 20;
    private const int MaximumSwimUpdates = 900;
    private const int MaximumSwimPaceUpdates = 120;

    internal LiveSubstrateProof(IEngineContext engine, TerrainWorld terrain, PlayerController player)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(player);
        this.engine = engine;
        this.terrain = terrain;
        this.player = player;
    }

    private static bool VoxelEditsRequested => string.Equals(
        Environment.GetEnvironmentVariable(ActivationVariable), "voxel-edits", StringComparison.OrdinalIgnoreCase);
    internal static bool Requested => VoxelEditsRequested || string.Equals(
        Environment.GetEnvironmentVariable(ActivationVariable),
        ActivationValue,
        StringComparison.OrdinalIgnoreCase);

    internal void Update()
    {
        if (completed)
        {
            return;
        }

        if (VoxelEditsRequested)
        {
            AdvanceVoxelEditsProof();
            return;
        }

        // The product publishes its own appearance snapshot earlier in the frame
        // and keeps admitting residency, so later proofs run on later frames.
        switch (stage++)
        {
            case 0:
                try
                {
                    ReportOverlayOutcome();
        if (PlantStaleFromEnvironment) PlantStaleOverlay();
        RunWorldProofs();
                }
                catch (Exception exception)
                {
                    failures.Add($"the world proofs threw {exception.GetType().Name}: {exception.Message}");
                }

                break;

            case 1:
            case 2:
            case 3:
            case 4:
            case 5:
            case 6:
            case 7:
            case 8:
            case 9:
            case 10:
            case 11:
            case 12:
            case 13:
            case 14:
            case 15:
            case 16:
                // Consecutive updates, before the residency flow claims this branch and
                // slows each one down: the swim sequence needs a handful of quick frames
                // to teleport, land and be read.
                try
                {
                    Tick(AdvanceSwimProof);
                }
                catch (Exception exception)
                {
                    failures.Add($"the swim proof threw {exception.GetType().Name}: {exception.Message}");
                    swimDone = true;
                }

                break;

            default:
                try
                {
                    AdvanceSwimProof();
                    if (finishRequested && (swimDone || swimPaceUpdates >= MaximumSwimPaceUpdates))
                    {
                        RequestFinish();
                        return;
                    }
                }
                catch (Exception exception)
                {
                    failures.Add($"the swim proof threw {exception.GetType().Name}: {exception.Message}");
                    swimDone = true;
                }

                try
                {
                    Tick(AdvanceResidencyPreparation);
                }
                catch (Exception exception)
                {
                    failures.Add($"residency preparation threw {exception.GetType().Name}: {exception.Message}");
                    RequestFinish();
                }

                break;
        }
    }

    /// <summary>
    /// Asks the proof to finish. The residency flow reaches its end after about two
    /// updates, which is far too soon for a sequence that has to wait for the
    /// character to settle, so the request is held until the swim proof completes or
    /// its own pace cap expires. Everything else about finishing is unchanged.
    /// </summary>
    private void RequestFinish()
    {
        if (swimDone || swimPaceUpdates >= MaximumSwimPaceUpdates)
        {
            Finish();
            return;
        }

        finishRequested = true;
    }

    private void Finish()
    {
        // A run that ends before the swim sequence completes is not a passing run,
        // however green its other lines are: this is the guard against a gate that
        // never fires reading as success.
        if (!swimDone)
        {
            failures.Add(
                $"the run finished before the swim proof completed ({swimUpdates} updates, stage {swimStage})");
        }

        ReportTickCost();
        completed = true;
        ReportAll();
    }

    /// <summary>
    /// Proves the product can persist and read back through the Engine store at all,
    /// on its own key so the world's overlay is untouched. It exists because the
    /// overlay was reported absent on every run: this separates "the store does not
    /// work" from "the world's save path does not reach it".
    /// </summary>
    private void ProvePersistenceRoundTrip()
    {
        PersistenceStore store = engine.Persistence.OpenStore(new PersistenceOpenRequest(PersistenceProofScope));
        // A full chunk payload, not a token: S2 asks whether the Engine's persistence
        // primitive can carry generated chunks, so the measurement is the cache's real
        // unit of work rather than a convenient small blob.
        byte[] written = new byte[TerrainConstants.ChunkVolume * sizeof(ushort)];
        for (int index = 0; index < written.Length; index++)
        {
            written[index] = (byte)(index * 31);
        }
        Stopwatch saveWatch = Stopwatch.StartNew();
        engine.Persistence.Save(new PersistenceSaveRequest(
            store, PersistenceProofKey, PersistenceRevisionGuard.Any, 0, written));
        saveWatch.Stop();
        Stopwatch loadWatch = Stopwatch.StartNew();
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(store, PersistenceProofKey));
        loadWatch.Stop();
        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"save/load latency: {saveWatch.Elapsed.TotalMilliseconds:F2} ms save, {loadWatch.Elapsed.TotalMilliseconds:F2} ms load for one {written.Length / 1024} KiB chunk payload"));
        PersistenceBlobInfo info = engine.Persistence.DescribeBlob(blob);
        if (!info.Present)
        {
            failures.Add("the persistence store did not retain a blob the product just wrote");
            return;
        }

        ReadOnlySpan<byte> read = engine.Persistence.ReadBlobBytes(blob).Span;
        Require(read.SequenceEqual(written), $"persistence returned {read.Length} bytes that differ from what was written");
        Report($"persistence round trip: wrote and read back {read.Length} bytes through the Engine store");
    }

    /// <summary>
    /// Plants a stale world overlay so the *next* start has to deal with one. This
    /// is the failure that used to kill the product: a saved world whose identity no
    /// longer matches. The next run must report it as discarded and keep going.
    /// </summary>
    private void PlantStaleOverlay()
    {
        PersistenceStore store = engine.Persistence.OpenStore(new PersistenceOpenRequest(TerrainConstants.PersistenceScope));
        byte[] stale = [0x53, 0x54, 0x41, 0x4c, 0x45, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        engine.Persistence.Save(new PersistenceSaveRequest(
            store, TerrainConstants.OverlayPersistenceKey, PersistenceRevisionGuard.Any, 0, stale));
        Report($"planted a {stale.Length}-byte stale world overlay for the next start to discard");
    }

    /// <summary>
    /// Apply one edit through the world's own edit path rather than through the
    /// Engine, then report what the world has saved. This is the caller of
    /// `SaveOverlay`, so it is what makes the saved world real: the next start must
    /// report a restored overlay rather than none.
    /// <summary>
    /// The radius of the decided volume this proof edits. Three is a 123-cell sphere - past the
    /// nine-edit transaction that used to stall the update loop, and past the brush's own maximum
    /// of two - so it separates "a brush is fine" from "a real bounded volume is fine".
    /// </summary>
    private const int MultiCellRadius = 3;

    /// <summary>
    /// Where that volume sits relative to the proof site: above the surface so its cells are air
    /// and the edit changes something, and offset sideways so filling it cannot bury the character
    /// the later stages move.
    /// </summary>
    private const int MultiCellOffsetX = 4;

    private const int MultiCellOffsetY = 3;

    /// <summary>The cells of a solid sphere: a volume a charge would remove, or a stamp would fill.</summary>
    private static TerrainVoxelAddress[] SphereCells(TerrainVoxelAddress centre, int radius)
    {
        List<TerrainVoxelAddress> cells = [];
        long squared = (long)radius * radius;
        for (long x = -radius; x <= radius; x++)
        {
            for (long y = -radius; y <= radius; y++)
            {
                for (long z = -radius; z <= radius; z++)
                {
                    if ((x * x) + (y * y) + (z * z) <= squared)
                    {
                        cells.Add(new TerrainVoxelAddress(centre.X + x, centre.Y + y, centre.Z + z));
                    }
                }
            }
        }

        return cells.ToArray();
    }

    private void ProveWorldEditSave(EngineVoxelAddress site)
    {
        Vector3 origin = new(site.X + SampleCellCenter, site.Y + EditAimLiftVoxels, site.Z + SampleCellCenter);
        TerrainWorldEditResult result = terrain.TryEditFromView(
            origin,
            -Vector3.UnitY,
            TerrainEditKind.Set,
            TerrainConstants.StoneMaterial,
            0,
            _ => false);
        Report($"world edit through the product path: {TerrainWorld.FormatEditReadout(result)}");
        Require(result is TerrainWorldEditApplied, "a product edit did not apply");

        (bool present, int bytes) = terrain.OverlaySaved();
        Require(present, "a product edit did not save the world overlay");
        Report($"world overlay saved by that edit: {bytes} bytes");

        // A multi-cell transaction used to stall the product's update loop silently at nine
        // edits, with the accepted edit as the last line ever printed (upstream #8684). A
        // radius-two brush is a sphere well past that threshold, so this stage is the
        // behavioural check that the fix is present in the pinned pair: if the stall were back,
        // every line after this one would simply never appear.
        //
        // The elapsed time is not decoration either. A blast is many dirty chunks at once, so
        // this number - volume against edit, remesh and presentation latency - is what tells
        // the manipulation slice how large a charge can be.
        TerrainVoxelAddress centre = new(site.X + MultiCellOffsetX, site.Y + MultiCellOffsetY, site.Z);
        TerrainVoxelAddress[] volume = SphereCells(centre, MultiCellRadius);
        long started = Stopwatch.GetTimestamp();
        TerrainWorldEditResult multiCell = terrain.TryEditCells(
            volume,
            TerrainEditKind.Set,
            TerrainConstants.StoneMaterial,
            _ => false);
        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Report($"decided-volume edit through the product path: {TerrainWorld.FormatEditReadout(multiCell)}"
            + $" in {elapsedMs:F2} ms for {volume.Length} cells at sphere radius {MultiCellRadius}");
        Require(multiCell is TerrainWorldEditApplied, "a decided-volume product edit did not apply");

        // And put it back exactly. A brush cannot: it is aimed, and re-aiming at a volume just
        // filled picks a different centre, which is how an "undo" leaves a rim. This is also the
        // shape every blast's undo will take.
        long undoStarted = Stopwatch.GetTimestamp();
        TerrainWorldEditResult undo = terrain.TryEditCells(
            volume,
            TerrainEditKind.Clear,
            TerrainConstants.EmptyMaterial,
            _ => false);
        Report($"decided-volume undo through the product path: {TerrainWorld.FormatEditReadout(undo)}"
            + $" in {Stopwatch.GetElapsedTime(undoStarted).TotalMilliseconds:F2} ms");
        Require(undo is TerrainWorldEditApplied, "a decided-volume undo did not apply");
    }

    /// <summary>
    /// Measures what generation and residency actually cost in the live product, so
    /// S2's budgets are numbers rather than intentions. Throughput is measured over
    /// product-owned chunks on copied values; the residency figures come from the
    /// Engine's own scene readout.
    /// </summary>
    private void ProveGenerationBudgets(SpatialSession session)
    {
        var generator = new TerrainChunkGenerator(terrain.Recipe);
        TerrainOverlayState overlay = new(terrain.Recipe.Contract.Seed);
        TerrainOverlaySnapshot snapshot = overlay.Snapshot();
        const int SampleCount = 64;
        long checksum = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int index = 0; index < SampleCount; index++)
        {
            TerrainChunk chunk = generator.Generate(new TerrainChunkAddress(index % 8, 0, index / 8), snapshot);
            checksum += chunk.SolidVoxelCount;
        }

        stopwatch.Stop();
        double millisecondsPerChunk = stopwatch.Elapsed.TotalMilliseconds / SampleCount;
        VoxelSceneReadout scene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(session));
        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"generation budget: {SampleCount} chunks in {stopwatch.Elapsed.TotalMilliseconds:F1} ms ({millisecondsPerChunk:F3} ms/chunk, {checksum} solid voxels); resident chunks {scene.ResidentChunkCount}, mesh revision {scene.MeshRevision}"));
        long residentPayloadBytes = (long)scene.ResidentChunkCount * TerrainConstants.ChunkVolume * sizeof(ushort);
        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"resident payload: {residentPayloadBytes / 1024} KiB of product chunk payload for {scene.ResidentChunkCount} chunks at {TerrainConstants.ChunkVolume * sizeof(ushort) / 1024} KiB each (product-side payload only; Engine mesh, collision and renderer memory is not included)"));
    }

    /// <summary>
    /// Reports the Engine's own movement verdict for the live player: which mode it
    /// put them in and how submerged they are. On dry ground this is the walking
    /// control that shows the swim policy did not disturb the normal case.
    /// </summary>
    private void ReportPlayerMovement()
    {
        CharacterMovementFact? fact = player.LastMovementFact;
        if (fact is not CharacterMovementFact movement)
        {
            Report("player movement: no character step has been reported yet");
            return;
        }

        Report($"player movement: mode={movement.Mode} immersion={movement.Immersion:F3} headSubmerged={movement.HeadSubmerged} climbAttached={movement.ClimbAttached}");
    }

    /// <summary>
    /// The swim integration proof, run against **generated** water rather than water
    /// this proof places. That distinction matters twice over: it is what S2 asks for,
    /// while the separate voxel-edits probe exercises direct multi-cell transactions.
    ///
    /// The product decides: it reads one cell and chooses a movement mode. The Engine
    /// guarantees: given a swim command with a volume the character is inside, it
    /// reports Swimming. This asserts the first and observes the second.
    /// </summary>
    private static readonly int[] VoxelProbeCounts = [1, 2, 3, 4, 9, 27, 64];
    private int voxelProbeUpdate;
    private bool voxelProbeReported;

    // An opt-in probe exercises the ordinary product update callback, including
    // movement and rendering after a direct multi-cell transaction.
    private void AdvanceVoxelEditsProof()
    {
        voxelProbeUpdate++;
        const int SettleUpdates = 60;
        const int UpdatesPerCase = 10;
        if (voxelProbeUpdate < SettleUpdates) return;
        int elapsed = voxelProbeUpdate - SettleUpdates;
        int test = elapsed / UpdatesPerCase;
        if (test >= VoxelProbeCounts.Length * 2)
        {
            if (!voxelProbeReported)
            {
                Report($"voxel batch proof PASSED: updates={voxelProbeUpdate}; all stone/water transactions continued");
                voxelProbeReported = true;
            }
            AdvanceSwimProof();
            return;
        }
        if (elapsed % UpdatesPerCase == 1)
            Report($"voxel batch continued: case={test}; updates={voxelProbeUpdate}; {player.LastWaterCheck}");
        if (elapsed % UpdatesPerCase != 0) return;
        int count = VoxelProbeCounts[test % VoxelProbeCounts.Length];
        uint material = (uint)(test < VoxelProbeCounts.Length ? Content.BlockId.Water : Content.BlockId.Stone);
        EngineVoxelAddress cell = player.LastWaterCheck.Feet;
        // Solid fills belong beside the actor. Deliberately enclosing an actor
        // is separately covered by the Engine penetration-rejection fixture.
        if (material == (uint)Content.BlockId.Stone)
            cell = new EngineVoxelAddress(cell.X + 8, cell.Y, cell.Z);
        VoxelEdit[] edits = Enumerable.Range(0, count).Select(i => new VoxelEdit(
            material, VoxelEditKind.Set,
            new EngineVoxelAddress(cell.X - 1 + i % 4, cell.Y + (i / 4) % 4, cell.Z - 1 + i / 16), material)).ToArray();
        VoxelSceneReadout before = engine.Voxel.ReadScene(new(terrain.Session));
        VoxelEditReceipt receipt = engine.Voxel.ApplyEdits(new(terrain.Session, edits));
        Report($"voxel batch accepted: case={test}; count={count}; material={material}; status={receipt.Status}; changed={receipt.ChangedVoxels}; updates={voxelProbeUpdate}");
        if (receipt.Status != VoxelEditStatus.Accepted)
            throw new InvalidOperationException($"Voxel probe transaction was not accepted: {receipt}");
    }

    private void AdvanceSwimProof()
    {
        if (swimDone)
        {
            return;
        }

        swimPaceUpdates++;
        if (++swimUpdates > MaximumSwimUpdates)
        {
            failures.Add(
                $"the swim proof did not complete within {MaximumSwimUpdates} updates (check: {player.LastWaterCheck})");
            swimDone = true;
            return;
        }

        if (swimStage == 0)
        {
            if (!TryFindWaterColumn(out long surfaceX, out long surfaceZ, out long surfaceY))
            {
                failures.Add($"no generated water column found within {WaterSearchRadius} voxels of the spawn");
                swimDone = true;
                return;
            }

            // Dropped so the *feet* cell is the water layer: the product reads the voxel
            // at the controller's own Y, and a body standing on the bottom of a
            // one-layer lake would have its feet in the ground, not in the water.
            ReportWaterRaycast(surfaceX, surfaceZ, surfaceY);
            player.Teleport(surfaceX + 0.5, GenerationConstants.WaterLevel + SwimDropHeight, surfaceZ + 0.5);
            Report(
                $"swim setup: teleported the player into generated water at ({surfaceX}, {surfaceZ}) " +
                $"where the ground is at y={surfaceY} and the water level is {GenerationConstants.WaterLevel}");
            swimStage = 1;
            return;
        }

        if (!player.LastWaterCheck.InWater)
        {
            // Report every attempt, not just the verdict: a recorded failure that the
            // proof never gets to print tells the next reader nothing about what the
            // product actually saw after the teleport.
            swimAttempts++;
            if (swimAttempts <= ReportedSwimAttempts)
            {
                Report($"swim attempt {swimAttempts}: {player.LastWaterCheck}");
            }

            if (swimAttempts >= MaximumSwimAttempts)
            {
                failures.Add($"swim proof did not observe water: {player.LastWaterCheck}");
                Report($"swim proof FAILED: {player.LastWaterCheck}");
                swimDone = true;
            }

            return;
        }

        CharacterMovementFact? fact = player.LastMovementFact;
        Require(
            fact is CharacterMovementFact movement && movement.Mode == CharacterMovementMode.Swimming,
            $"a player standing in generated water reports {fact?.Mode} (check: {player.LastWaterCheck})");
        if (fact is CharacterMovementFact swimming)
        {
            Report(
                $"swim in generated water: mode={swimming.Mode} immersion={swimming.Immersion:F3} " +
                $"headSubmerged={swimming.HeadSubmerged} while the product read {player.LastWaterCheck}");
        }

        swimDone = true;
    }

    /// <summary>
    /// Casts straight down through a water column and reports where the ray stops.
    /// This is the measurement that separates the two possible reasons a character
    /// rests on the surface of a lake: a hit at the water line means collision treats
    /// water voxels as solid, a hit at the lake bed means collision passes through
    /// them and something else is holding the character up.
    /// </summary>
    private void ReportWaterRaycast(long x, long z, long groundY)
    {
        Vector3 origin = new(x + 0.5f, GenerationConstants.WaterLevel + RaycastLift, z + 0.5f);
        SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(
            terrain.Session,
            origin,
            -Vector3.UnitY,
            RaycastReach,
            new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
            ReadOnlyMemory<SpatialEntityCollider>.Empty,
            ReadOnlyMemory<ulong>.Empty,
            ReadOnlyMemory<SpatialEntityCollider>.Empty));
        Report(
            $"water raycast at ({x}, {z}) where the ground is y={groundY} and water fills up to " +
            $"y={GenerationConstants.WaterLevel}: {hit}");
    }

    /// <summary>
    /// Finds a column the generator filled with water: one whose ground is below the
    /// water level, so the surface voxel at the water line is water. Product-side
    /// generation only - no Engine session is involved, which is what the threading
    /// rule requires.
    /// </summary>
    private bool TryFindWaterColumn(out long waterX, out long waterZ, out long groundY)
    {
        for (long radius = 2; radius <= WaterSearchRadius; radius += 2)
        {
            for (long x = -radius; x <= radius; x += 2)
            {
                for (long z = -radius; z <= radius; z += 2)
                {
                    TerrainColumn column = terrain.Recipe.ColumnAt(x, z);
                    // Depth matters: a one-layer lake leaves a resting player's feet
                    // in the air above it, which cannot answer a question about
                    // swimming. This asks for ground at least `WaterDepth` below the
                    // water line, so the body is in water rather than on top of it.
                    if (column.Surface > GenerationConstants.WaterLevel - WaterDepth)
                    {
                        continue;
                    }

                    ushort material = terrain.Recipe.MaterialAt(
                        new CraftSurvive.Game.Modules.Terrain.VoxelAddress(x, GenerationConstants.WaterLevel, z), column);
                    if (material == (ushort)Content.BlockId.Water)
                    {
                        waterX = x;
                        waterZ = z;
                        groundY = column.Surface;
                        return true;
                    }
                }
            }
        }

        waterX = 0;
        waterZ = 0;
        groundY = 0;
        return false;
    }

    /// <summary>
    /// Proves the chunk cache against the real store with a real generated chunk: write
    /// one, read it back, and compare it to what the generator produces. The managed lane
    /// proves the payload format is lossless; this proves the store carries it.
    /// </summary>
    private void ProveChunkCacheRoundTrip()
    {
        using var cache = new TerrainChunkCache(engine, terrain.Recipe.Contract, terrain.GenerationFingerprint);
        var generator = new TerrainChunkGenerator(terrain.Recipe);
        TerrainOverlaySnapshot snapshot = new TerrainOverlayState(terrain.Recipe.Contract.Seed).Snapshot();
        TerrainChunkAddress address = new(1, 0, -2);
        TerrainChunk chunk = generator.Generate(address, snapshot);
        Stopwatch write = Stopwatch.StartNew();
        cache.Write(address, chunk.Materials.Span);
        write.Stop();
        Stopwatch read = Stopwatch.StartNew();
        bool hit = cache.TryRead(address, out ushort[] cached);
        read.Stop();
        Require(hit, "the chunk cache did not return a chunk it had just written");
        if (!hit)
        {
            return;
        }

        Require(cached.AsSpan().SequenceEqual(chunk.Materials.Span), "a cached chunk differs from fresh generation");

        // Read-through: a generator given the cache must serve that chunk from the store
        // rather than compute it, and must produce the same voxels either way.
        var cachedGenerator = new TerrainChunkGenerator(terrain.Recipe, cache);
        TerrainChunk throughCache = cachedGenerator.Generate(address, snapshot);
        Require(cachedGenerator.CacheHits == 1, $"a cache-backed generator reported {cachedGenerator.CacheHits} hits");
        Require(throughCache.Materials.Span.SequenceEqual(chunk.Materials.Span),
            "generation through the cache differs from generation without it");
        Report(string.Create(
            CultureInfo.InvariantCulture,
            $"chunk cache: {cached.Length} voxels stored and read back in {write.Elapsed.TotalMilliseconds:F2} ms write, {read.Elapsed.TotalMilliseconds:F2} ms read, identical to fresh generation; read-through served a later generation in {read.Elapsed.TotalMilliseconds:F2} ms"));
    }

    /// <summary>
    /// Times one update of the product's own streaming work. Both the residency
    /// synchronisation and the earlier proof stages run through here, because this proof
    /// spends most of its updates in those stages: a figure measured only where the flow
    /// rarely goes is a figure that never prints. Engine render and frame time are not in
    /// it and are not claimed to be.
    /// </summary>
    private void Tick(Action work)
    {
        long before = Stopwatch.GetTimestamp();
        work();
        double elapsed = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
        tickCount++;
        tickTotalMs += elapsed;
        tickMaximumMs = Math.Max(tickMaximumMs, elapsed);
        if (tickCount % TickReportInterval == 0)
        {
            ReportTickCost();
        }
    }

    /// <summary>
    /// The product's own per-update streaming cost, reported as the run goes.
    /// </summary>
    private void ReportTickCost()
    {
        if (tickCount == 0)
        {
            return;
        }

        Report(string.Format(
            CultureInfo.InvariantCulture,
            "product tick cost over {0} updates with {1} resident chunks: mean {2:F3} ms, worst {3:F3} ms in the "
            + "product's residency synchronisation (Engine render and frame time are not included)",
            tickCount,
            terrain.ResidentChunkCount,
            tickTotalMs / tickCount,
            tickMaximumMs));
    }

    private void ReportOverlayOutcome() =>
        Report($"saved world overlay: {terrain.OverlayRestoreOutcome}");

    private void RunWorldProofs()
    {
        SpatialSession session = terrain.Session;
        Report($"live substrate proof beginning (player at {Format(player.WorldPosition)})");

        if (!TryFindAirSite(session, out EngineVoxelAddress site))
        {
            Report($"FAILED: no air site found within {SiteSearchVoxels} voxels above the player");
            completed = true;
            return;
        }

        Report($"site {Format(site)}");
        ProvePerCellState(session, site);
        ProveDirectLight(session, site);
        try
        {
            ProveWorldEditSave(site);
        }
        catch (Exception exception)
        {
            failures.Add($"the world edit save proof threw {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            ProveChunkCacheRoundTrip();
        }
        catch (Exception exception)
        {
            failures.Add($"the chunk cache proof threw {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            ProveGenerationBudgets(session);
        }
        catch (Exception exception)
        {
            failures.Add($"the generation budget measurement threw {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            ReportPlayerMovement();
        }
        catch (Exception exception)
        {
            failures.Add($"the player movement report threw {exception.GetType().Name}: {exception.Message}");
        }

        ProveSwimMode(session, site);
        ProveNavigation(session);
        ClearSite(session, site);
    }

    // ------------------------------------------------------------- navigation
    private void ProveNavigation(SpatialSession session)
    {
        Vector3 position = player.WorldPosition;

        // Navigation is not derived automatically: the product must publish a
        // walkable projection. Collision-derived navigation is the path that
        // keeps one authority for terrain and navigation.
        CollisionNavigationConfig config = new(
            NavigationGridId,
            TerrainConstants.VoxelSize,
            NavigationChunkSize,
            NavigationMaxStepCells,
            NavigationAgentRadius,
            NavigationAgentHeight,
            NavigationMaximumSlopeDegrees,
            NavigationMaximumCells);
        Vector3 worldMin = position - new Vector3(NavigationHalfExtent, NavigationDepthBelow, NavigationHalfExtent);
        Vector3 worldMax = position + new Vector3(NavigationHalfExtent, NavigationHeightAbove, NavigationHalfExtent);
        NavigationReplaceReceipt replaced = engine.Spatial.ReplaceCollisionNavigation(
            new CollisionNavigationReplaceRequest(session, worldMin, worldMax, config));
        Require(
            replaced.WalkableCellCount > 0UL,
            $"collision-derived navigation found no walkable cells in the published box (hash {replaced.ProjectionHash})");
        Report($"navigation replace: walkable cells={replaced.WalkableCellCount} revision={replaced.NavigationRevision} hash={replaced.ProjectionHash} over world box {Format(worldMin)}..{Format(worldMax)}");

        // Query cells are relative to the published box, not world voxels, and the
        // vertical index origin is not assumed: sweep candidate levels around the
        // player until the query answers about the walk rather than the start cell.
        PlanarNavCell start = new(
            (long)Math.Floor((position.X - worldMin.X) / config.CellSize),
            (long)Math.Floor((position.Y - worldMin.Y) / config.CellSize),
            (long)Math.Floor((position.Z - worldMin.Z) / config.CellSize));
        NavigationPathResult path = QueryPath(session, start);
        long initialY = start.Y;
        for (long delta = 1L; delta <= NavLevelSweep && IsStartFailure(path.Outcome); delta++)
        {
            foreach (long y in new[] { initialY - delta, initialY + delta })
            {
                PlanarNavCell candidate = new(start.X, y, start.Z);
                NavigationPathResult attempt = QueryPath(session, candidate);
                if (!IsStartFailure(attempt.Outcome))
                {
                    start = candidate;
                    path = attempt;
                    break;
                }
            }
        }

        // A short query is terrain-dependent: the mechanism question is whether
        // this session answers with a derived projection and a typed outcome, not
        // whether a particular four-cell walk happens to connect.
        Require(
            path.Kind != NavigationProjectionKind.None,
            "the session derived no navigation projection for the query");
        Require(path.NavigationRevision > 0UL, "the navigation projection reports revision zero");
        Require(path.Visited <= MaxVisitedCells, $"the search visited {path.Visited} cells, above its own budget");
        if (path.Outcome == NavigationPathOutcome.Reached)
        {
            Require(path.Path.Length > 0U, "a reached path reports zero cells");
        }

        Report($"navigation query: outcome={path.Outcome} kind={path.Kind} cells={path.Path.Length} visited={path.Visited} revision={path.NavigationRevision} from cell ({start.X}, {start.Y}, {start.Z})");
    }

    // ------------------------------------------------------- entity projection
    private void ProveEntityProjection()
    {
        // Contract check first: an entry whose entity carries no Transform is
        // rejected in managed code, before any Engine call.
        EntityStore bare = new([EngineComponentTypes.Transform]);
        EntityId untransformed = bare.Create(EntityLifecycle.Active);
        using (Appearance probe = engine.Graphics.CreatePrimitive(new PrimitiveAppearanceRequest(
            PrimitiveGeometry.Cube,
            false,
            new Color(MarkerRed, MarkerGreen, MarkerBlue, MarkerAlpha))))
        {
            EntityGraphicsProjection validator = new(bare, engine.Graphics);
            string surface = "accepted an entity without a Transform";
            try
            {
                validator.Publish(
                    new EntityGraphicsProjectionEntry[] { new(untransformed, probe, true, RenderLayer.Scene, null) });
            }
            catch (InvalidOperationException exception)
            {
                surface = exception.Message;
            }

            Require(
                surface != "accepted an entity without a Transform",
                "the projection accepted an entity that carries no Transform");
            Report($"entity projection validation: an entity without {nameof(EngineComponentTypes.Transform)} was refused: {surface}");
        }

        bare.Destroy(untransformed);

        // The publish itself replaces the whole appearance snapshot, and the
        // Engine refuses a snapshot that drops a projected animation target or a
        // ghost plate's source object. This product publishes its own snapshot
        // every frame, so a projection cannot coexist with it: adopting
        // projections means moving all appearance publication onto that path.
        EntityStore store = new([EngineComponentTypes.Transform]);
        EntityId entity = store.Create(EntityLifecycle.Active);
        try
        {
            store.Add(entity, new Transform(player.WorldPosition, Quaternion.Identity, Vector3.One));
            using Appearance marker = engine.Graphics.CreatePrimitive(new PrimitiveAppearanceRequest(
                PrimitiveGeometry.Cube,
                false,
                new Color(MarkerRed, MarkerGreen, MarkerBlue, MarkerAlpha)));
            EntityGraphicsProjection projection = new(store, engine.Graphics);
            try
            {
                EntityGraphicsProjectionReceipt receipt = projection.Publish(
                    new EntityGraphicsProjectionEntry[] { new(entity, marker, true, RenderLayer.Scene, null) });
                Report($"entity projection: published {receipt.Facts.Length} fact(s); the product's own snapshot publication would be replaced");
                // The published snapshot now holds the marker, and the Engine refuses to
                // dispose an appearance a snapshot still uses. Publish an empty snapshot
                // first; the product republishes its own on the next update.
                projection.Publish(ReadOnlyMemory<EntityGraphicsProjectionEntry>.Empty);
            }
            catch (EngineCallException exception)
            {
                Report($"entity projection: publishing a standalone snapshot is refused while the product retains a ghost plate ({exception.Message}); projections require whole-snapshot ownership");
            }
        }
        finally
        {
            store.Destroy(entity);
        }
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
            VoxelEditReceipt cleared = Apply(
                session,
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

    // ------------------------------------------------- residency admission
    /// <summary>
    /// Proves the residency path S2's streaming depends on: one
    /// <c>ApplyResidency</c> call admits a distant generated chunk in place and
    /// it becomes resident. The Engine applies residency directly since #8739;
    /// there is no background preparation to poll or cancel.
    /// </summary>
    private void AdvanceResidencyPreparation()
    {
        SpatialSession session = terrain.Session;
        TerrainChunkAddress target = DistantChunk(ResidencyTargetChunkOffset);
        VoxelResidencyTransaction? transaction = terrain.PlanChunkAdmission(session, target);
        if (transaction is null)
        {
            Report("residency admission: skipped, this scene does not generate residency");
            FinishProof(session);
            return;
        }

        VoxelSceneReadout before = engine.Voxel.ReadScene(new VoxelSceneReadRequest(session));
        VoxelResidencyReceipt admitted = engine.Voxel.ApplyResidency(transaction.Value);
        VoxelSceneReadout after = engine.Voxel.ReadScene(new VoxelSceneReadRequest(session));
        Require(
            after.ResidentChunkCount > before.ResidentChunkCount,
            $"admitting chunk {target} left {after.ResidentChunkCount} resident chunks, was {before.ResidentChunkCount}");
        Report($"residency admission: chunk {target} admitted in place ({admitted.AdmittedCount} admitted), resident chunks {before.ResidentChunkCount} -> {after.ResidentChunkCount}");
        FinishProof(session);
    }

    private void FinishProof(SpatialSession session)
    {
        try
        {
            ProvePersistenceRoundTrip();
        }
        catch (Exception exception)
        {
            failures.Add($"the persistence round trip threw {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            ProveGenerationDeterminism();
        }
        catch (Exception exception)
        {
            failures.Add($"the generation determinism proof threw {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            ProveDimensionLoad(session);
        }
        catch (Exception exception)
        {
            failures.Add($"the dimension load threw {exception.GetType().Name}: {exception.Message}");
        }

        RunEntityProjectionStage();
        RequestFinish();
    }

    private void RunEntityProjectionStage()
    {
        try
        {
            ProveEntityProjection();
        }
        catch (Exception exception)
        {
            failures.Add($"the entity projection threw {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Proves generation determinism through the Engine's own keyed RNG in a live
    /// session: the same chunks, generated in the live process, must hash to a
    /// stable value, and a second pass must agree. The managed lane pins the same
    /// snapshot through a test draw port, so this is the half that exercises the
    /// real draw path.
    /// </summary>
    private void ProveGenerationDeterminism()
    {
        string first = TerrainGenerationSnapshot.Hash(terrain.Recipe);
        string second = TerrainGenerationSnapshot.Hash(terrain.Recipe);
        Require(first == second, "live generation produced two different snapshots in one session");
        Report($"generation determinism: snapshot {first} across the live Engine keyed RNG");
    }

    private TerrainChunkAddress DistantChunk(long offset)
    {
        Vector3 position = player.WorldPosition;
        long edge = TerrainConstants.ChunkEdgeLength;
        return new TerrainChunkAddress(
            (long)Math.Floor(position.X / edge) + offset,
            (long)Math.Floor(position.Y / edge),
            (long)Math.Floor(position.Z / edge));
    }

    // --------------------------------------------------------- dimension load
    /// <summary>
    /// Proves the mechanism a "dimension" needs: a second world can be built and
    /// torn down inside the same runtime, with its own session and residency,
    /// without restarting the product or disturbing the world already loaded.
    /// S8's authored dungeons depend on this being product-owned.
    /// </summary>
    private void ProveDimensionLoad(SpatialSession primary)
    {
        SpatialSession? dungeon = null;
        try
        {
            Stopwatch dimensionWatch = Stopwatch.StartNew();
            dungeon = engine.Spatial.CreateSession(new SpatialSessionConfig(
                TerrainConstants.VoxelSize,
                DungeonChunkSize,
                VoxelSurfaceMode.GreedyCubes));
            uint[] slots = new uint[DungeonChunkSize * DungeonChunkSize * DungeonChunkSize];
            Array.Fill(slots, (uint)TerrainConstants.StoneMaterial);

            VoxelResidencyReceipt admitted = engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(
                dungeon,
                new VoxelResidencyOperation[]
                {
                    new(
                        VoxelResidencyOperationKind.Admit,
                        new VoxelChunkIdentity(0L, 0L, 0L),
                        0U,
                        (uint)slots.Length),
                },
                slots));
            Require(
                admitted.AdmittedCount == 1U,
                $"a second session admitted {admitted.AdmittedCount} chunk(s), expected 1");

            VoxelReadout cell = engine.Voxel.Read(new VoxelReadRequest(
                dungeon,
                new EngineVoxelAddress(DungeonProbeVoxel, DungeonProbeVoxel, DungeonProbeVoxel)));
            Require(cell.Present, "the second session does not read its own admitted voxel");
            Require(
                cell.MaterialSlot == TerrainConstants.StoneMaterial,
                $"the second session reads material {cell.MaterialSlot}, expected {TerrainConstants.StoneMaterial}");

            dimensionWatch.Stop();
            VoxelSceneReadout dungeonScene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(dungeon));
            Report(string.Create(
                CultureInfo.InvariantCulture,
                $"dimension load: session created and one chunk admitted in {dimensionWatch.Elapsed.TotalMilliseconds:F2} ms"));
            Report($"dimension: second session built with its own residency, resident chunks {dungeonScene.ResidentChunkCount}, solid voxels {admitted.ResidentSolidVoxelCount}, authority {admitted.AuthorityHash}");
        }
        finally
        {
            dungeon?.Dispose();
        }

        VoxelSceneReadout after = engine.Voxel.ReadScene(new VoxelSceneReadRequest(primary));
        Require(
            after.ResidentChunkCount > 0UL,
            "the first world lost its residency while a second session was built and disposed");
        Report($"dimension: the first world still reports {after.ResidentChunkCount} resident chunks after the second session was disposed");
    }

    // --------------------------------------------------------------- utilities
    private static bool IsStartFailure(NavigationPathOutcome outcome) => outcome is
        NavigationPathOutcome.StartNotWalkable or
        NavigationPathOutcome.StartNotTraversable or
        NavigationPathOutcome.StartBlocked;

    private NavigationPathResult QueryPath(SpatialSession session, PlanarNavCell start)
        => engine.Spatial.RequestNavigationPath(new NavigationPathRequest(
            session,
            start,
            new PlanarNavCell(start.X + NavGoalCells, start.Y, start.Z),
            MaxVisitedCells));

    private VoxelEditReceipt Apply(SpatialSession session, VoxelEdit edit)
        => engine.Voxel.ApplyEdits(new VoxelEditTransaction(session, new VoxelEdit[] { edit }));

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
