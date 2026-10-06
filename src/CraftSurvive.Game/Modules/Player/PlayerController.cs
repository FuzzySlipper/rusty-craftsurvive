using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using Rusty.Engine.Entities;
using TerrainVoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// Product-owned player policy: it reads input, steps the Engine's character controller, aims
/// edits, and keeps the player's pose in world coordinates. It owns the player's vitals and
/// progress and applies the respawn they request. The camera, the water and climb decisions, input
/// diagnostics and world-origin rebasing are its parts; Engine services integrate look,
/// collision, world origin and the camera view.
/// </summary>
internal sealed class PlayerController : IDisposable
{
    internal static readonly ComponentType<PlayerRuntimeComponent> RuntimeComponent =
        ComponentType<PlayerRuntimeComponent>.Create(
            ProductComponentKeys.Create(ProductIds.PlayerRuntimeComponent));

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly WorldFrame frame;
    private readonly ProductUiPublisher ui;
    private readonly Cues cues;
    private readonly PlayerFootfalls footfalls = new();
    private readonly PlayerInputState input = new();
    private readonly PlayerInputDiagnostics diagnostics = new();
    private readonly PlayerWaterProbe water;
    private readonly PlayerClimbProbe climb;
    private readonly PlayerCamera camera;
    private readonly WorldOriginRebaser rebaser;
    private readonly PlayerContinuationStore continuation;
    private readonly EntityStore entityWorld = new([RuntimeComponent]);
    private readonly EntityId playerEntity;
    private readonly CharacterControllerConfig controllerConfig;
    private CharacterMotion motion;
    private LookState look = StartingLook;
    private PlayerWorldPosition playerGlobal;
    private PlayerWorldPosition spawn;
    private Vector3 playerLocal;
    private double controllerStepAccumulator;
    private double cameraSampleTimeSeconds;
    private ulong commandSequence;
    private bool jumpHeld;
    private bool impulseHeld;
    private bool jumpPending;
    private bool impulsePending;

    /// <summary>How much longer a climb action pressed while not holding on takes hold of a face the player reaches.</summary>
    private double takeHoldSeconds;

    /// <summary>A climb action pressed while holding on, not yet acted on by a controller step: let go.</summary>
    private bool letGoPending;
    private bool started;
    private ulong updateCount;
    private ulong lastSimulationStep;
    private uint lastAdmittedStepCount;
    private uint lastControllerStepCount;
    private PlayerInputFrame lastInputFrame;
    private CharacterStepReceipt? lastStepReceipt;

    /// <summary>Where the player last stood with a step accepted or a placement made: a place their body fit.</summary>
    private Vector3 lastClearLocal;
    private long recoveries;
    private string lastRecovery = "none";

    /// <summary>Whether the last controller step left the player holding a climb rail.</summary>
    private bool climbHeld;

    /// <summary>Whether the last controller step left the player's head under water.</summary>
    private bool headSubmerged;

    /// <summary>How deep in water the last controller step left the body, from none to whole.</summary>
    private float immersion;

    /// <summary>The hits the player had taken when last heard, so each new one is heard once.</summary>
    private long hitsHeard;

    /// <summary>The separate space the player is in, and where they left the open world; null in the open world.</summary>
    private SeparateSpace? away;
    private LandscapeSampleSpace? landscapeSpace;
    private bool landscapeEntered;

    /// <summary>The session the player stands in: a separate space's, or the open world's.</summary>
    private SpatialSession Session => away?.Session ?? terrain.Session;
    private Vector3 lastUpdatePositionBefore;
    private Vector3 lastUpdatePositionAfter;
    private TerrainWorldEditResult? lastTerrainEdit;

    /// <summary>Where the view pointed after the latest update's look; zero before the first update.</summary>
    private Vector3 aimForward;

    internal PlayerController(IEngineContext engine, TerrainWorld terrain, WorldFrame frame, ProductStore store, ProductUiPublisher ui, Cues cues)
    {
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        this.cues = cues ?? throw new ArgumentNullException(nameof(cues));
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        ArgumentNullException.ThrowIfNull(frame);
        water = new PlayerWaterProbe(engine);
        climb = new PlayerClimbProbe(engine);
        camera = new PlayerCamera(engine);
        rebaser = new WorldOriginRebaser(engine, frame);
        continuation = new PlayerContinuationStore(engine, store, terrain.SaveIdentity, Vitals.MaximumHealth);
        controllerConfig = PlayerBody.Configure(engine.Spatial.DefaultCharacterControllerConfig());
        playerGlobal = PlayerWorldPosition.FromWorld(PlayerConstants.SpawnColumn.X, 0d, PlayerConstants.SpawnColumn.Y);
        playerEntity = entityWorld.Create();
        PublishRuntimeComponent();
        this.frame = frame;
    }

    private static LookState StartingLook => new(
        Angles.ToRadians(PlayerConstants.InitialYawDegrees),
        Angles.ToRadians(PlayerConstants.InitialPitchDegrees));

    /// <summary>A press-only attack request for this update, for the systems that act on it.</summary>
    internal bool AttackRequested => lastInputFrame.AttackRequested;

    /// <summary>This update's hotbar requests: a slot picked by number (-1 for none), steps along it, and a use of what it holds.</summary>
    internal (int Pick, int Steps, bool Use) HotbarRequests => (lastInputFrame.HotbarPick, lastInputFrame.HotbarSteps, lastInputFrame.UseRequested);

    internal EntityStore EntityStore => entityWorld;

    /// <summary>The player's health and defeat; creatures strike through it.</summary>
    internal PlayerVitals Vitals { get; } = new(CharacterSheet.Starting.Derived.MaximumHealth);

    /// <summary>What the player has earned.</summary>
    internal PlayerProgress Progress { get; } = new();

    /// <summary>What the player's climbs spend.</summary>
    internal PlayerStamina Stamina { get; } = new(CharacterSheet.Starting.Derived.MaximumStamina);

    /// <summary>The player's character as the rules see it, at the level they have reached.</summary>
    internal CharacterSheet Sheet => CharacterSheet.Starting with { Level = Progress.Level };

    /// <summary>The Engine step the latest update brought the player to.</summary>
    internal long CurrentStep { get; private set; }

    /// <summary>The centre of the player's capsule, in world coordinates.</summary>
    internal Vector3 WorldPosition => playerGlobal.ToWorldVector();

    internal Vector3 WorldEyePosition => WorldPosition + (Vector3.UnitY * PlayerBody.EyeOffset(motion.Stance));

    /// <summary>Where the player's capsule meets the ground, in world coordinates.</summary>
    internal Vector3 WorldFeetPosition => WorldPosition - (Vector3.UnitY * (PlayerBody.Height(motion.Stance) / 2f));

    internal void Start()
    {
        if (started)
        {
            return;
        }

        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(terrain.Session));
        frame.Observe(origin);

        // Home is whatever ground the generator put under the spawn column, so a generator change
        // can never start the player inside the terrain. A saved session continues where it ended
        // when the player still fits there, and from home when they do not.
        spawn = StandingAt(PlayerConstants.SpawnColumn.X,
            terrain.GroundAt(PlayerConstants.SpawnColumn.X, PlayerConstants.SpawnColumn.Y), PlayerConstants.SpawnColumn.Y);
        playerGlobal = spawn;
        if (continuation.Restore() is PlayerContinuation saved)
        {
            Continue(saved);
        }

        playerLocal = playerGlobal.ToLocal(origin);
        if (WorldOriginRebaser.IsNeeded(playerLocal))
        {
            playerLocal = rebaser.Rebase(terrain.Session, playerGlobal, playerLocal);
        }

        motion = PlayerBody.AtRest(playerLocal);
        climbHeld = false;
        camera.Create(EyePosition(), look, updateCount);
        terrain.SynchronizeAround(playerGlobal.FloorVoxel());
        ui.PublishPlayer(ToUiFacts());
        PublishRuntimeComponent();
        started = true;
    }

    internal void Update(ProductUpdate update)
    {
        EnsureStarted();
        updateCount = checked(updateCount + 1UL);
        ProductStep step = ProductStep.From(update.Facts);
        CurrentStep = step.Step;
        if (landscapeSpace is { } studySpace && !landscapeEntered)
        {
            if (away is not null) { studySpace.Dispose(); landscapeSpace = null; }
            else
            {
                studySpace.Advance();
                if (studySpace.Loaded)
                {
                    EnterSeparateSpace(studySpace.Session, studySpace.Arrival);
                    look = new LookState(0f, Modules.WorldGen.LandscapeStudies.ArrivalPitch);
                    landscapeEntered = true;
                }
            }
        }
        if (Vitals.TryRespawn(step.Step))
        {
            // A defeated player comes back where they started, outside the ring creatures spawn
            // at, rather than inside the reach of whatever defeated them.
            MoveHome();
            Stamina.Refill(Sheet.Derived.MaximumStamina);
        }

        // Whatever hurt the player last update - a creature, hunger, drowning - is heard now.
        if (Vitals.HitsTaken > hitsHeard)
        {
            cues.Raise(Cue.Hurt);
        }

        hitsHeard = Vitals.HitsTaken;
        lastSimulationStep = update.Facts.SimulationStep;
        lastAdmittedStepCount = update.Facts.AdmittedStepCount;
        cameraSampleTimeSeconds = step.Step * step.FixedDeltaSeconds;
        diagnostics.Capture(update.Input, updateCount);
        lastUpdatePositionBefore = playerLocal;
        PlayerInputFrame frame = input.Consume(update.Input, checked((float)step.ElapsedSeconds));
        lastInputFrame = frame;
        LookReceipt lookReceipt = Look.IntegrateClamped(new LookRequest(look, frame.LookDelta, PlayerBody.Look));
        look = lookReceipt.After;
        aimForward = lookReceipt.Forward;

        jumpPending |= frame.JumpHeld && !jumpHeld;
        impulsePending |= frame.ImpulseHeld && !impulseHeld;
        if (frame.ClimbRequested)
        {
            if (climbHeld)
            {
                letGoPending = true;
            }
            else
            {
                takeHoldSeconds = PlayerConstants.TakeHoldWindowSeconds;
            }
        }
        jumpHeld = frame.JumpHeld;
        impulseHeld = frame.ImpulseHeld;
        StepCharacter(frame, lookReceipt, step.ElapsedSeconds);
        lastUpdatePositionAfter = playerLocal;
        diagnostics.RecordMovement(updateCount, frame, lastControllerStepCount, lastStepReceipt,
            lastUpdatePositionBefore, lastUpdatePositionAfter);

        if (away is null && WorldOriginRebaser.IsNeeded(playerLocal))
        {
            Vector3 before = playerLocal;
            playerLocal = rebaser.Rebase(terrain.Session, playerGlobal, playerLocal);
            motion = motion.Rebased(playerLocal - before) with
            {
                CollisionWorldHash = PlayerConstants.UninitializedCollisionWorldHash,
            };
            camera.Cut();
        }

        if (away is null)
        {
            terrain.SynchronizeAround(playerGlobal.FloorVoxel());
        }

        if (away is null && frame.Edit is TerrainEditKind edit)
        {
            lastTerrainEdit = terrain.TryEditFromView(
                EyePosition(),
                lookReceipt.Forward,
                edit,
                PlayerConstants.PlaceMaterial,
                frame.BrushRadius,
                OverlapsVoxel);
        }

        camera.Publish(EyePosition(), look, cameraSampleTimeSeconds, updateCount);
        ui.PublishPlayer(ToUiFacts());
        PublishRuntimeComponent();
        continuation.SaveIfDue(step.Step, Continuation());
    }

    /// <summary>The player's facts for playtest observation: pose, vitals and how much input arrived.</summary>
    internal PlayerObservation Observe() => new(
        WorldFeetPosition,
        Angles.ToDegrees(look.YawRadians),
        Angles.ToDegrees(look.PitchRadians),
        motion.Grounded,
        motion.Stance == CharacterStance.Crouched,
        Vitals.State.Health,
        Vitals.MaximumHealth,
        Progress.Level,
        diagnostics.TotalKeys,
        updateCount);

    /// <summary>Turns the view by degrees through the product's own look rules, without stepping anything.</summary>
    internal void LookBy(double yawDegrees, double pitchDegrees)
    {
        EnsureStarted();
        Vector2 units = new(
            (float)(yawDegrees / PlayerConstants.LookDegreesPerPointerUnit),
            (float)(-pitchDegrees / PlayerConstants.LookDegreesPerPointerUnit));
        look = Look.IntegrateClamped(new LookRequest(look, units, PlayerBody.Look)).After;
        camera.Publish(EyePosition(), look, cameraSampleTimeSeconds, updateCount);
        PublishRuntimeComponent();
    }

    /// <summary>Whether the player's head is under water, as the last controller step found it.</summary>
    internal bool HeadSubmerged => headSubmerged;

    /// <summary>Whether the player is sprinting: asking to, and moving.</summary>
    internal bool Sprinting => lastInputFrame.SprintRequested && lastInputFrame.PlanarIntent != Vector2.Zero;

    /// <summary>Where the player last looked, as a view direction in world axes; zero before the first look.</summary>
    internal Vector3 AimForward => aimForward;

    /// <summary>A point some metres in front of the player's eyes, along where they look, in the session's local frame.</summary>
    internal Vector3 LocalAhead(float metres) => EyePosition() + (aimForward * metres);

    /// <summary>What the player is aiming at now, within edit reach. Nothing in a dungeon is aimed at: it is not built on.</summary>
    internal TerrainPick Aim()
    {
        EnsureStarted();
        return aimForward == Vector3.Zero || away is not null
            ? TerrainPick.Missed(TerrainPickOutcome.CastMiss)
            : terrain.PickFromView(EyePosition(), aimForward);
    }

    /// <summary>Whether the player is in a separate space (a dungeon) rather than the open world.</summary>
    internal bool InSeparateSpace => away is not null;

    /// <summary>
    /// Moves the player into a separate, finite space: its own spatial session, never rebased or
    /// streamed, with its origin at zero, so its local and global coordinates agree. Where the player
    /// stood in the open world is kept to come back to, and is what the continuation saves meanwhile.
    /// </summary>
    internal void EnterSeparateSpace(SpatialSession session, Vector3 standingFeet)
    {
        EnsureStarted();
        if (away is not null)
        {
            throw new InvalidOperationException("The player is already in a separate space.");
        }

        away = new SeparateSpace(session, playerGlobal);
        Place(PlayerWorldPosition.FromWorld(standingFeet.X, standingFeet.Y + (PlayerConstants.StandingHeight / 2f) + PlayerConstants.SpawnClearance, standingFeet.Z));
    }

    /// <summary>Moves the player within the separate space they are in, standing at a point.</summary>
    internal void MoveWithinSeparateSpace(Vector3 standingFeet)
    {
        EnsureStarted();
        if (away is null)
        {
            throw new InvalidOperationException("The player is not in a separate space.");
        }

        Place(PlayerWorldPosition.FromWorld(standingFeet.X, standingFeet.Y + (PlayerConstants.StandingHeight / 2f) + PlayerConstants.SpawnClearance, standingFeet.Z));
    }

    /// <summary>Brings the player back to where they left the open world. A player already there stays put.</summary>
    internal void ReturnFromSeparateSpace()
    {
        EnsureStarted();
        if (away is SeparateSpace space)
        {
            away = null;
            Place(space.Return);
        }
        landscapeSpace?.Dispose();
        landscapeSpace = null;
        landscapeEntered = false;
    }

    /// <summary>What was restored at start and how the continuation save is going.</summary>
    internal string ContinuationReadout() => string.Create(CultureInfo.InvariantCulture,
        $"{continuation.Readout()} feet={WorldFeetPosition.X:F2},{WorldFeetPosition.Y:F2},{WorldFeetPosition.Z:F2} health={Vitals.State.Health} experience={Progress.Experience} level={Progress.Level}");

    /// <summary>Returns one bounded product-owned explanation of the latest movement update.</summary>
    internal string DebugReadout()
    {
        EnsureStarted();
        string stepReadout = lastStepReceipt is not CharacterStepReceipt receipt
            ? "none"
            : string.Create(CultureInfo.InvariantCulture,
                $"attempted={receipt.Step.Attempted};accepted={receipt.Step.Accepted};wish={PlayerInputDiagnostics.Format(receipt.WishVelocity)};displacement={PlayerInputDiagnostics.Format(receipt.Displacement)};blocked={receipt.BlockFlags};casts={receipt.CastCount}");
        return string.Create(CultureInfo.InvariantCulture,
            $"updates={updateCount};simulationStep={lastSimulationStep};admittedSteps={lastAdmittedStepCount};controllerSteps={lastControllerStepCount};{diagnostics.Readout()};")
            + string.Create(CultureInfo.InvariantCulture,
            $"intent={PlayerInputDiagnostics.Format(lastInputFrame.PlanarIntent)};lookDelta={PlayerInputDiagnostics.Format(lastInputFrame.LookDelta)};jump={lastInputFrame.JumpHeld};crouch={lastInputFrame.CrouchRequested};sprint={lastInputFrame.SprintRequested};")
            + string.Create(CultureInfo.InvariantCulture,
            $"before={PlayerInputDiagnostics.Format(lastUpdatePositionBefore)};after={PlayerInputDiagnostics.Format(lastUpdatePositionAfter)};yaw={Angles.ToDegrees(look.YawRadians):F2};pitch={Angles.ToDegrees(look.PitchRadians):F2};grounded={motion.Grounded};stance={motion.Stance};")
            + $"{camera.Readout()};cameraPosition={PlayerInputDiagnostics.Format(EyePosition())};step=[{stepReadout}];lastMovement=[{diagnostics.MovementReadout()}];water=[{water.LastCheck}];climb=[{climb.LastRail?.ToString() ?? "none"}];climbHeld={climbHeld};stamina={Stamina.Current.ToString("F1", CultureInfo.InvariantCulture)}/{Stamina.Maximum};recoveries={recoveries};lastRecovery=[{lastRecovery}];landscape=[{landscapeSpace?.Readout ?? "overworld"}]";
    }

    /// <summary>Returns the latest product interaction outcome without retaining Engine gameplay state.</summary>
    internal string TerrainEditReadout()
    {
        EnsureStarted();
        return TerrainWorldEditResult.Format(lastTerrainEdit);
    }

    /// <summary>
    /// Moves the live player through the ordinary product state and Engine publication lane. The
    /// player goes where asked if a standing body fits there, else onto the ground of that column
    /// if it fits there; a body placed inside the world would stop the character controller, so
    /// otherwise nothing moves and the answer is null.
    /// </summary>
    internal PlayerRuntimeComponent? Teleport(double x, double y, double z)
    {
        EnsureStarted();
        if (landscapeSpace is not null) ReturnFromSeparateSpace();
        PlayerWorldPosition asked = PlayerWorldPosition.FromWorld(x, y, z);
        PlayerWorldPosition onGround = StandingAt(x, terrain.GroundAt(x, z), z);
        PlayerWorldPosition? target = Fits(asked) ? asked : Fits(onGround) ? onGround : null;
        if (target is not PlayerWorldPosition place)
        {
            return null;
        }

        // A teleport is in the open world: a player in a separate space is brought out first.
        away = null;
        Place(place);
        return entityWorld.Get(playerEntity, RuntimeComponent);
    }

    /// <summary>
    /// Saves the continuation now rather than at its next due step: map travel settles the
    /// party's position through it whenever the journey stops.
    /// </summary>
    internal void SaveContinuationNow()
    {
        EnsureStarted();
        continuation.Save(Continuation(), CurrentStep);
    }

    /// <summary>Visits an authored terrain study through the ordinary relocation path.</summary>
    internal bool VisitLandscape(string name)
    {
        if (InSeparateSpace && landscapeSpace is null) return false;
        const string FullSuffix = "full";
        bool full = name.EndsWith(FullSuffix, StringComparison.Ordinal);
        string id = full ? name[..^FullSuffix.Length] : name;
        if (Modules.WorldGen.LandscapeStudies.Find(id) is not { } study) return false;
        if (landscapeSpace is not null) ReturnFromSeparateSpace();
        landscapeSpace = terrain.CreateLandscapeStudy(study);
        return true;
    }

    internal void ActivateCamera() => camera.Activate();

    internal void ClearInput() => input.Reset();

    /// <summary>Stands the player at a place in the session they are now in, at rest.</summary>
    private void Place(PlayerWorldPosition place)
    {
        playerGlobal = place;
        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(Session));
        playerLocal = playerGlobal.ToLocal(origin);
        lastClearLocal = playerLocal;
        motion = PlayerBody.AtRest(playerLocal);
        climbHeld = false;
        headSubmerged = false;
        controllerStepAccumulator = 0d;
        jumpPending = false;
        impulsePending = false;
        takeHoldSeconds = 0d;
        letGoPending = false;
        if (away is null)
        {
            terrain.SynchronizeAround(playerGlobal.FloorVoxel());
        }

        camera.Cut();
        camera.Publish(EyePosition(), look, cameraSampleTimeSeconds, updateCount);
        ui.PublishPlayer(ToUiFacts());
        PublishRuntimeComponent();
    }

    /// <summary>Whether the player's body covers a cell, so an edit that would fill it can be refused.</summary>
    internal bool Occupies(TerrainVoxelAddress voxel) => OverlapsVoxel(voxel);

    /// <summary>A fresh session: the player returns to where they started with full vitals and no progress.</summary>
    internal void Restart()
    {
        EnsureStarted();
        Vitals.Reset();
        Progress.Reset();
        Stamina.Refill(Sheet.Derived.MaximumStamina);
        footfalls.Reset();
        hitsHeard = Vitals.HitsTaken;
        look = StartingLook;
        MoveHome();
    }

    public void Dispose()
    {
        if (started)
        {
            continuation.Save(Continuation(), CurrentStep);
        }

        landscapeSpace?.Dispose();
        landscapeSpace = null;
        camera.Dispose();
        started = false;
        entityWorld.Dispose();
    }

    /// <summary>
    /// Runs the character controller for every fixed step the Engine admitted this update. The
    /// controller steps at its own rate, so the accumulator carries any remainder.
    /// </summary>
    private void StepCharacter(PlayerInputFrame frame, LookReceipt lookReceipt, double elapsedSeconds)
    {
        controllerStepAccumulator += elapsedSeconds;
        lastControllerStepCount = 0U;
        lastStepReceipt = null;
        while (controllerStepAccumulator + PlayerConstants.ControllerStepEpsilon >= PlayerConstants.ControllerStepSeconds)
        {
            CharacterControllerConfig stepConfig = frame.SprintRequested && !frame.CrouchRequested
                ? PlayerBody.WithSprintSpeed(controllerConfig)
                : controllerConfig;
            commandSequence = checked(commandSequence + 1UL);
            CharacterStepReceipt receipt;
            try
            {
                receipt = engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(
                    Session,
                    playerLocal,
                    motion,
                    default,
                    ReadOnlyMemory<CharacterObstacle>.Empty,
                    ReadOnlyMemory<CharacterMeshInstance>.Empty,
                    stepConfig,
                    Command(frame, lookReceipt, commandSequence)));
            }
            catch (EngineCallException refusal) when (PlayerRecovery.IsPenetration(refusal))
            {
                // Too deep in collision to step: stand the player clear and take up steps next update.
                Recover();
                controllerStepAccumulator = 0d;
                break;
            }

            lastControllerStepCount = checked(lastControllerStepCount + 1U);
            lastStepReceipt = receipt;
            Hear(receipt);
            climbHeld = receipt.Movement.ClimbAttached;
            headSubmerged = receipt.Movement.HeadSubmerged;
            immersion = receipt.Movement.Immersion;
            jumpPending = false;
            impulsePending = false;
            letGoPending = false;
            takeHoldSeconds = climbHeld ? 0d : Math.Max(0d, takeHoldSeconds - PlayerConstants.ControllerStepSeconds);
            playerLocal = receipt.Transform.Translation;
            motion = receipt.Motion;
            Stamina.Tick(
                climbHeld ? PlayerStamina.OnFace(frame.PlanarIntent.Y) : motion.Grounded ? PlayerExertion.Resting : PlayerExertion.Airborne,
                PlayerConstants.ControllerStepSeconds);
            WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(Session));
            playerGlobal = PlayerWorldPosition.FromLocal(origin, playerLocal);
            lastClearLocal = playerLocal;
            controllerStepAccumulator -= PlayerConstants.ControllerStepSeconds;
        }
    }

    /// <summary>Raises what the body did in one controller step that can be heard, from before the step and its receipt.</summary>
    private void Hear(CharacterStepReceipt receipt)
    {
        BodySound heard = footfalls.Hear(new BodyStep(
            motion.Grounded,
            receipt.Motion.Grounded,
            -motion.ControlledVelocity.Y,
            receipt.Motion.ControlledVelocity.Y,
            new Vector2(receipt.Displacement.X, receipt.Displacement.Z).Length(),
            immersion,
            receipt.Movement.Immersion,
            climbHeld,
            receipt.Movement.ClimbAttached));
        // Body sounds are heard as the player's own, but a landing or a splash is seen at the feet.
        Vector3 feet = receipt.Transform.Translation - (Vector3.UnitY * (PlayerBody.Height(receipt.Motion.Stance) / 2f));
        foreach ((BodySound sound, Cue cue) in BodyCues)
        {
            if (heard.HasFlag(sound))
            {
                cues.RaiseAt(cue, feet);
            }
        }
    }

    private static readonly (BodySound Sound, Cue Cue)[] BodyCues =
    [
        (BodySound.Footstep, Cue.Footstep),
        (BodySound.Jump, Cue.Jump),
        (BodySound.Land, Cue.Land),
        (BodySound.HardLanding, Cue.HardLanding),
        (BodySound.Splash, Cue.Splash),
        (BodySound.Grip, Cue.Grip),
    ];

    /// <summary>
    /// Stands the player at the first nearby place their body fits, at rest. With nowhere clear
    /// nearby, the player stays put and the next update tries again; the game keeps running.
    /// </summary>
    private void Recover()
    {
        Vector3 from = playerLocal;
        Vector3? clear = PlayerRecovery.FirstClear(engine.Spatial, Session, PlayerRecovery.Candidates(playerLocal, lastClearLocal),
            PlayerBody.Height(motion.Stance), controllerConfig.Shape.Radius, controllerConfig.Shape.ContactSkin);
        recoveries++;
        if (clear is not Vector3 to)
        {
            lastRecovery = string.Create(CultureInfo.InvariantCulture, $"stuck at {PlayerInputDiagnostics.Format(from)}: nowhere clear within {PlayerRecovery.SearchHeightMetres:F0} m");
            return;
        }

        playerLocal = to;
        lastClearLocal = to;
        motion = PlayerBody.AtRest(to);
        climbHeld = false;
        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(Session));
        playerGlobal = PlayerWorldPosition.FromLocal(origin, playerLocal);
        camera.Cut();
        lastRecovery = $"moved clear from {PlayerInputDiagnostics.Format(from)} to {PlayerInputDiagnostics.Format(to)}";
    }

    /// <summary>
    /// The controller command for one step: swimming when the player's own cells are water, climbing
    /// when they hold a climbable face, else walking. The climb action takes hold of a face reached
    /// within a moment of pressing it, when there is the stamina for it, and lets go when pressed
    /// again; a jump also lets go, and so does running out.
    /// The probes compose the volume or rail the Engine moves them through.
    /// </summary>
    private CharacterControllerCommand Command(PlayerInputFrame frame, LookReceipt lookReceipt, ulong sequence)
    {
        Vector3 impulse = impulsePending
            ? (lookReceipt.Right * PlayerConstants.ImpulseSpeed) + (Vector3.UnitY * PlayerConstants.ImpulseLift)
            : Vector3.Zero;
        float stepSeconds = (float)PlayerConstants.ControllerStepSeconds;
        ClimbGrip grip = climbHeld
            ? letGoPending || Stamina.Exhausted ? ClimbGrip.None : ClimbGrip.Holding
            : takeHoldSeconds > 0d && Stamina.CanTakeHold ? ClimbGrip.Taking : ClimbGrip.None;
        if (water.TrySwim(Session, playerGlobal, playerLocal, motion.Stance, out CharacterMovementRequest movement)
            || (!jumpPending && climb.TryClimb(Session, playerGlobal, playerLocal, motion.Stance,
                lookReceipt.Forward, frame.PlanarIntent.Y, grip, out movement)))
        {
            return new CharacterControllerCommand(
                movement, frame.PlanarIntent, look.YawRadians, jumpPending, frame.JumpHeld,
                frame.CrouchRequested, Vector3.Zero, impulse, stepSeconds, sequence);
        }

        return new CharacterControllerCommand(
            frame.PlanarIntent, look.YawRadians, jumpPending, frame.JumpHeld, frame.CrouchRequested,
            Vector3.Zero, impulse, stepSeconds, sequence);
    }

    /// <summary>Home, or the ground of the home column if something now stands there; if neither has room the player stays put.</summary>
    private void MoveHome() => Teleport(spawn.WorldX, spawn.WorldY, spawn.WorldZ);

    /// <summary>The capsule centre of a player standing with their feet at a height.</summary>
    private static PlayerWorldPosition StandingAt(double x, double feetY, double z) =>
        PlayerWorldPosition.FromWorld(x, feetY + (PlayerConstants.StandingHeight / 2f) + PlayerConstants.SpawnClearance, z);

    /// <summary>
    /// What a session leaves for the next. A player in a separate space is saved where they left the
    /// open world, so a session that ends inside a dungeon continues at its entrance.
    /// </summary>
    private PlayerContinuation Continuation()
    {
        PlayerWorldPosition at = away?.Return ?? playerGlobal;
        double feetY = away is null ? WorldFeetPosition.Y : at.WorldY - (PlayerBody.Height(CharacterStance.Standing) / 2f);
        PlayerDefeatState vitals = Vitals.State;
        return new PlayerContinuation(
            at.WorldX, feetY, at.WorldZ,
            look.YawRadians, look.PitchRadians,
            vitals.Health, vitals.Defeats, Progress.Experience, Progress.ItemsCollected);
    }

    /// <summary>
    /// Applies a saved session. Progress always carries over. A player saved while down comes back
    /// at home at full health, as a respawn would have brought them; otherwise they stand where
    /// they were, if a standing body still fits there.
    /// </summary>
    private void Continue(PlayerContinuation saved)
    {
        Progress.Restore(saved.Experience, saved.ItemsCollected);
        if (saved.Health <= 0)
        {
            Vitals.Restore(Vitals.MaximumHealth, saved.Defeats);
            continuation.Applied = "progress; down, so home at full health";
            return;
        }

        Vitals.Restore(saved.Health, saved.Defeats);
        PlayerWorldPosition standing = StandingAt(saved.FeetX, saved.FeetY, saved.FeetZ);
        if (!Fits(standing))
        {
            continuation.Applied = "progress and vitals; position blocked, so home";
            return;
        }

        playerGlobal = standing;
        look = new LookState((float)saved.YawRadians, (float)saved.PitchRadians);
        continuation.Applied = "position, look, vitals and progress";
    }

    /// <summary>
    /// Smooth ground stands up to half a voxel off its cubic material: a column whose surface rounds
    /// up holds a solid voxel just above where the feet rest on the dual-contoured surface.
    /// </summary>
    private const double SmoothGroundAllowance = 0.5;

    /// <summary>Whether a standing body at this position is inside the world and clear of every collidable block below its feet's allowance.</summary>
    private bool Fits(PlayerWorldPosition position)
    {
        double radius = controllerConfig.Shape.Radius;
        double halfHeight = PlayerConstants.StandingHeight / 2d;
        long limit = terrain.Recipe.Radius;
        if (Math.Abs(position.WorldX) + radius >= limit || Math.Abs(position.WorldZ) + radius >= limit)
        {
            return false;
        }

        for (long x = (long)Math.Floor(position.WorldX - radius); x <= (long)Math.Floor(position.WorldX + radius); x++)
        {
            for (long y = (long)Math.Floor(position.WorldY - halfHeight + SmoothGroundAllowance); y <= (long)Math.Floor(position.WorldY + halfHeight); y++)
            {
                for (long z = (long)Math.Floor(position.WorldZ - radius); z <= (long)Math.Floor(position.WorldZ + radius); z++)
                {
                    if (BlockRegistry.TryGetBySlot(terrain.MaterialAt(new TerrainVoxelAddress(x, y, z)), out BlockDefinition block)
                        && block.Collidable)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private bool OverlapsVoxel(TerrainVoxelAddress voxel)
    {
        double halfHeight = PlayerBody.Height(motion.Stance) / 2d;
        float radius = controllerConfig.Shape.Radius;
        return (playerGlobal.WorldX - radius) < voxel.X + 1L
            && (playerGlobal.WorldX + radius) > voxel.X
            && (playerGlobal.WorldY - halfHeight) < voxel.Y + 1L
            && (playerGlobal.WorldY + halfHeight) > voxel.Y
            && (playerGlobal.WorldZ - radius) < voxel.Z + 1L
            && (playerGlobal.WorldZ + radius) > voxel.Z;
    }

    private Vector3 EyePosition() => playerLocal + (Vector3.UnitY * PlayerBody.EyeOffset(motion.Stance));

    private PlayerUiFacts ToUiFacts() => new(
        playerGlobal.WorldX,
        playerGlobal.WorldY + PlayerBody.EyeOffset(motion.Stance),
        playerGlobal.WorldZ,
        Angles.ToDegrees(look.YawRadians),
        Angles.ToDegrees(look.PitchRadians),
        motion.Grounded,
        motion.Stance == CharacterStance.Crouched)
    {
        Health = Vitals.State.Health,
        MaximumHealth = Vitals.MaximumHealth,
        Defeats = Vitals.State.Defeats,
        Experience = Progress.Experience,
        Level = Progress.Level,
        ItemsCollected = Progress.ItemsCollected,
        Stamina = Stamina.Current,
        MaximumStamina = Stamina.Maximum,
        Climbing = climbHeld,
        HitsTaken = Vitals.HitsTaken,
        Submerged = headSubmerged,
    };

    private void PublishRuntimeComponent()
    {
        entityWorld.Set(playerEntity, RuntimeComponent, new PlayerRuntimeComponent(
            playerGlobal.WorldX,
            playerGlobal.WorldY,
            playerGlobal.WorldZ,
            Angles.ToDegrees(look.YawRadians),
            Angles.ToDegrees(look.PitchRadians),
            motion.Grounded,
            motion.Stance == CharacterStance.Crouched));
    }

    private void EnsureStarted()
    {
        if (!started)
        {
            throw new InvalidOperationException("CraftSurvive player has not started.");
        }
    }
}

/// <summary>What a playtest observation reads of the player.</summary>
internal readonly record struct PlayerObservation(
    Vector3 Feet,
    double YawDegrees,
    double PitchDegrees,
    bool Grounded,
    bool Crouched,
    int Health,
    int MaximumHealth,
    int Level,
    ulong KeyEvents,
    ulong Updates);

internal readonly record struct PlayerRuntimeComponent(
    double X,
    double Y,
    double Z,
    double YawDegrees,
    double PitchDegrees,
    bool Grounded,
    bool Crouched);

/// <summary>A separate space the player has gone into, and where they left the open world.</summary>
internal sealed record SeparateSpace(SpatialSession Session, PlayerWorldPosition Return);
