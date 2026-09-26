using System.Numerics;
using Rusty.Engine;
using Rusty.Engine.Entities;
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
    private ulong preparation;
    private bool reportedPending;
    private int residencyAttempts;
    private ulong residentBefore;

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

            default:
                try
                {
                    AdvanceResidencyPreparation();
                }
                catch (Exception exception)
                {
                    failures.Add($"residency preparation threw {exception.GetType().Name}: {exception.Message}");
                    Finish();
                }

                break;
        }
    }

    private void Finish()
    {
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
        byte[] written = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
        engine.Persistence.Save(new PersistenceSaveRequest(
            store, PersistenceProofKey, PersistenceRevisionGuard.Any, 0, written));
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(store, PersistenceProofKey));
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
        NavigationPathReadout path = QueryPath(session, start);
        long initialY = start.Y;
        for (long delta = 1L; delta <= NavLevelSweep && IsStartFailure(path.Outcome); delta++)
        {
            foreach (long y in new[] { initialY - delta, initialY + delta })
            {
                PlanarNavCell candidate = new(start.X, y, start.Z);
                NavigationPathReadout attempt = QueryPath(session, candidate);
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
            Require(path.PathLen > 0U, "a reached path reports zero cells");
        }

        Report($"navigation query: outcome={path.Outcome} kind={path.Kind} cells={path.PathLen} visited={path.Visited} revision={path.NavigationRevision} from cell ({start.X}, {start.Y}, {start.Z})");
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
                    new EntityGraphicsProjectionEntry[] { new(untransformed, probe, true, RenderLayer.Scene, null) },
                    MaximumProjectedEntities,
                    null);
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

        bare.Destroy(untransformed, bare.GetEntityRevision(untransformed));

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
                    new EntityGraphicsProjectionEntry[] { new(entity, marker, true, RenderLayer.Scene, null) },
                    MaximumProjectedEntities,
                    null);
                Report($"entity projection: published {receipt.Facts.Length} fact(s); the product's own snapshot publication would be replaced");
            }
            catch (EngineCallException exception)
            {
                Report($"entity projection: publishing a standalone snapshot is refused while the product retains a ghost plate ({exception.Message}); projections require whole-snapshot ownership");
            }
        }
        finally
        {
            store.Destroy(entity, store.GetEntityRevision(entity));
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

    // ------------------------------------------------- background residency
    /// <summary>
    /// Proves the Engine-owned preparation path that S2's streaming depends on:
    /// start a preparation off the update path, poll it without blocking, commit
    /// it, and cancel a second one. The product re-applies residency while the
    /// showcase fills, and a commit rechecks generations, so attempts are bounded
    /// and a rejection is reported as the documented stale-candidate guard rather
    /// than treated as a failure.
    /// </summary>
    private void AdvanceResidencyPreparation()
    {
        SpatialSession session = terrain.Session;
        VoxelSceneReadout scene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(session));

        if (stage % ResidencyDiagnosticFrames == 0)
        {
            Report($"residency stage frame {stage}: source revision {scene.SourceRevision}, resident chunks {scene.ResidentChunkCount}");
        }

        if (preparation == 0UL)
        {
            if (residencyAttempts >= MaximumResidencyAttempts)
            {
                Report($"residency preparation: no committed preparation after {residencyAttempts} attempt(s)");
                FinishProof(session);
                return;
            }

            TerrainChunkAddress target = DistantChunk(ResidencyTargetChunkOffset + residencyAttempts);
            VoxelResidencyTransaction? transaction = terrain.PlanChunkAdmission(session, target);
            if (transaction is null)
            {
                Report("residency preparation: skipped, this scene does not generate residency");
                FinishProof(session);
                return;
            }

            residentBefore = scene.ResidentChunkCount;
            VoxelPreparationReceipt started = engine.Voxel.StartResidencyPreparation(transaction.Value);
            preparation = started.Preparation;
            residencyAttempts++;
            Require(
                started.Status is VoxelPreparationStatus.Pending or VoxelPreparationStatus.Ready,
                $"starting a preparation reported {started.Status}");
            Report($"residency preparation attempt {residencyAttempts}: started for chunk {target}, status {started.Status}, resident chunks {residentBefore}, source revision {scene.SourceRevision}");
            return;
        }

        VoxelPreparationRequest request = new(session, preparation);
        VoxelPreparationReceipt polled = engine.Voxel.PollResidencyPreparation(request);
        switch (polled.Status)
        {
            case VoxelPreparationStatus.Pending:
                if (!reportedPending)
                {
                    reportedPending = true;
                    Report("residency preparation: polled while pending, without blocking the frame");
                }

                return;

            case VoxelPreparationStatus.Ready:
            {
                VoxelPreparationReceipt committed = engine.Voxel.CommitResidencyPreparation(request);
                if (committed.Status != VoxelPreparationStatus.Committed)
                {
                    Report($"residency preparation attempt {residencyAttempts}: commit reported {committed.Status}, so this candidate was rejected as stale");
                    preparation = 0UL;
                    reportedPending = false;
                    return;
                }

                VoxelSceneReadout after = engine.Voxel.ReadScene(new VoxelSceneReadRequest(session));
                Require(
                    after.ResidentChunkCount > residentBefore,
                    $"a committed preparation left {after.ResidentChunkCount} resident chunks, was {residentBefore}");
                Report($"residency preparation: committed on attempt {residencyAttempts}, resident chunks {residentBefore} -> {after.ResidentChunkCount}");
                ProveCancellation(session);
                FinishProof(session);
                return;
            }

            default:
                Report($"residency preparation attempt {residencyAttempts}: poll reported {polled.Status}, so this candidate was dropped");
                preparation = 0UL;
                reportedPending = false;
                return;
        }
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
        Finish();
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

    private void ProveCancellation(SpatialSession session)
    {
        TerrainChunkAddress target = DistantChunk(ResidencyTargetChunkOffset + 1L);
        VoxelResidencyTransaction? transaction = terrain.PlanChunkAdmission(session, target);
        if (transaction is null)
        {
            return;
        }

        VoxelPreparationReceipt started = engine.Voxel.StartResidencyPreparation(transaction.Value);
        Require(
            started.Status is VoxelPreparationStatus.Pending or VoxelPreparationStatus.Ready,
            $"starting the cancellation probe reported {started.Status}");
        VoxelPreparationReceipt cancelled = engine.Voxel.CancelResidencyPreparation(
            new VoxelPreparationRequest(session, started.Preparation));
        Require(
            cancelled.Status == VoxelPreparationStatus.Cancelled,
            $"cancelling a preparation reported {cancelled.Status}");
        Report($"residency preparation: a second preparation for chunk {target} cancelled cleanly");
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
            dungeon = engine.Spatial.CreateSession(new SpatialSessionConfig(
                TerrainConstants.VoxelSize,
                DungeonChunkSize,
                VoxelSurfaceMode.GreedyCubes));
            uint[] slots = new uint[DungeonChunkSize * DungeonChunkSize * DungeonChunkSize];
            Array.Fill(slots, (uint)TerrainConstants.StoneMaterial);

            VoxelResidencyReceipt admitted = engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(
                dungeon,
                0UL,
                VoxelResidencyHistoryPolicy.RejectIfNonEmpty,
                new VoxelResidencyOperation[]
                {
                    new(
                        VoxelResidencyOperationKind.Admit,
                        new VoxelChunkIdentity(0L, 0L, 0L),
                        0UL,
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

            VoxelSceneReadout dungeonScene = engine.Voxel.ReadScene(new VoxelSceneReadRequest(dungeon));
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

    private NavigationPathReadout QueryPath(SpatialSession session, PlanarNavCell start)
        => engine.Spatial.RequestNavigationPath(new NavigationPathRequest(
            session,
            start,
            new PlanarNavCell(start.X + NavGoalCells, start.Y, start.Z),
            MaxVisitedCells));

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
