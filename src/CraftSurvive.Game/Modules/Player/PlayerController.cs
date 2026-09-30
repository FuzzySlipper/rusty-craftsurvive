using CraftSurvive.Game.Modules.Content;
using System.Globalization;
using System.Numerics;
using Rusty.Engine;
using Rusty.Engine.Entities;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.World;
using TerrainVoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// Product-owned player policy. It retains only input and pose facts;
/// Engine services continue to integrate look, collision, world-origin, and the camera view.
/// It owns the player's vitals and progress and applies the respawn they request, and it owns
/// world-origin rebasing, which it commits through the product's <see cref="WorldFrame"/>.
/// </summary>
internal sealed class PlayerController : IDisposable
{
    internal static readonly ComponentType<PlayerRuntimeComponent> RuntimeComponent =
        ComponentType<PlayerRuntimeComponent>.Create(
            ProductComponentKeys.Create(ProductIds.PlayerRuntimeComponent));

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly WorldFrame frame;
    private readonly PlayerInputState input = new();
    private readonly EntityStore entityWorld = new([RuntimeComponent]);
    private readonly EntityId playerEntity;
    private readonly CharacterControllerConfig controllerConfig;
    private readonly LookConfig lookConfig;
    private Camera? camera;
    private CharacterMotion motion;
    private LookState look;
    private PlayerWaterCheck waterCheck;
    private PlayerWorldPosition playerGlobal;
    private PlayerWorldPosition spawn;
    private Vector3 playerLocal;
    private double controllerStepAccumulator;
    private ulong commandSequence;
    private bool jumpHeld;
    private bool impulseHeld;
    private bool jumpPending;
    private bool impulsePending;
    private bool started;
    private ulong updateCount;
    private ulong lastSimulationStep;
    private uint lastAdmittedStepCount;
    private int lastInputEventCount;
    private int lastKeyEventCount;
    private int lastPointerEventCount;
    private ulong totalPointerDeltaEventCount;
    private int lastControllerButtonEventCount;
    private int lastControllerAxisEventCount;
    private int lastClearEventCount;
    private string lastInputEvent = "none";
    private ulong totalInputEventCount;
    private ulong lastInputEventUpdate;
    private PlayerInputFrame lastInputFrame;

    /// <summary>A press-only attack request for this frame, for the systems that act on it.</summary>
    internal bool AttackRequested => lastInputFrame.AttackRequested;
    private uint lastControllerStepCount;
    private CharacterStepReceipt? lastStepReceipt;
    private Vector3 lastUpdatePositionBefore;
    private Vector3 lastUpdatePositionAfter;
    private ulong lastMovementUpdate;
    private PlayerInputFrame lastMovementInputFrame;
    private uint lastMovementControllerStepCount;
    private CharacterStepReceipt? lastMovementStepReceipt;
    private Vector3 lastMovementPositionBefore;
    private Vector3 lastMovementPositionAfter;
    private CameraInterpolation cameraInterpolation = CameraInterpolation.Position;
    private double cameraSampleTimeSeconds;
    private double cameraDelaySeconds = PlayerConstants.CameraPresentationDelaySeconds;
    private bool cameraCut = true;
    private ulong cameraPublicationCount;
    private ulong lastCameraPublicationUpdate;
    private TerrainWorldEditResult? lastTerrainEdit;

    internal PlayerController(IEngineContext engine, TerrainWorld terrain, WorldFrame frame)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        controllerConfig = CreateControllerConfig(engine.Spatial.DefaultCharacterControllerConfig());
        lookConfig = new LookConfig(
            PlayerConstants.LookRadiansPerInputUnit,
            PlayerConstants.LookRadiansPerInputUnit,
            PlayerConstants.MinimumPitchRadians,
            PlayerConstants.MaximumPitchRadians,
            PlayerConstants.LookMaximumDeltaRadians,
            false,
            true,
            true);
        look = new LookState(
            DegreesToRadians(PlayerConstants.InitialYawDegrees),
            DegreesToRadians(PlayerConstants.InitialPitchDegrees));
        playerGlobal = PlayerWorldPosition.FromWorld(PlayerConstants.SpawnColumn.X, 0d, PlayerConstants.SpawnColumn.Y);
        playerEntity = entityWorld.Create();
        PublishRuntimeComponent();
    }

    /// <summary>
    /// The controller command for this step. Walking is unchanged; when the player's
    /// own column is water the product composes the water volume from the terrain and
    /// selects the Engine's swim mode, which is where submersion and buoyancy come
    /// from. The product owns the volume and the decision, the Engine owns the solver.
    /// </summary>
    private CharacterControllerCommand Command(
        PlayerInputFrame frame,
        LookState look,
        bool jumpPending,
        bool impulsePending,
        LookReceipt lookReceipt,
        ulong sequence)
    {
        Vector3 impulse = impulsePending
            ? (lookReceipt.Right * PlayerConstants.ImpulseSpeed) + (Vector3.UnitY * PlayerConstants.ImpulseLift)
            : Vector3.Zero;
        float stepSeconds = (float)PlayerConstants.ControllerStepSeconds;
        if (TryWaterMovement(playerLocal, out CharacterMovementRequest swim))
        {
            return new CharacterControllerCommand(
                swim, frame.PlanarIntent, look.YawRadians, jumpPending, frame.JumpHeld,
                frame.CrouchRequested, Vector3.Zero, impulse, stepSeconds, sequence);
        }

        return new CharacterControllerCommand(
            frame.PlanarIntent, look.YawRadians, jumpPending, frame.JumpHeld, frame.CrouchRequested,
            Vector3.Zero, impulse, stepSeconds, sequence);
    }

    /// <summary>
    /// Reads the player's own column and, when it is water, hands back the movement
    /// request with the water volume around the player. A single voxel read per step
    /// is the cost of the product owning this decision.
    /// </summary>
    private bool TryWaterMovement(Vector3 playerLocal, out CharacterMovementRequest movement)
    {
        movement = default;
        // The cell the player *stands* in, not the cell their eye is in. Checking the
        // eye cell meant waist-deep water never triggered a swim: the product asked
        // about the cell its head was in, which is air above the surface. The eye cell
        // is still checked as a fallback, so a player whose head is under water swims
        // as well as one standing in it.
        // Voxel addresses are global cells even after a rebase, so the cells are taken from the
        // player's world position; the swim volume below is a position and so stays local.
        Rusty.Engine.VoxelAddress feet = new(
            (long)Math.Floor(playerGlobal.WorldX),
            (long)Math.Floor(playerGlobal.WorldY - EyeOffset(motion.Stance)),
            (long)Math.Floor(playerGlobal.WorldZ));
        Rusty.Engine.VoxelAddress eyes = new(
            (long)Math.Floor(playerGlobal.WorldX),
            (long)Math.Floor(playerGlobal.WorldY),
            (long)Math.Floor(playerGlobal.WorldZ));
        VoxelReadout feetRead = engine.Voxel.Read(new VoxelReadRequest(terrain.Session, feet));
        bool feetWater = feetRead.Present && feetRead.MaterialSlot == (ushort)Content.BlockId.Water;
        bool eyesWater = false;
        if (!feetWater)
        {
            VoxelReadout eyesRead = engine.Voxel.Read(new VoxelReadRequest(terrain.Session, eyes));
            eyesWater = eyesRead.Present && eyesRead.MaterialSlot == (ushort)Content.BlockId.Water;
        }

        waterCheck = new PlayerWaterCheck(feet, feetRead.Present, feetRead.MaterialSlot, eyes, feetWater, eyesWater);
        if (!feetWater && !eyesWater)
        {
            return false;
        }

        Vector3 center = new(playerLocal.X, MathF.Floor(playerLocal.Y), playerLocal.Z);
        movement = new CharacterMovementRequest
        {
            Mode = CharacterMovementMode.Swimming,
            VerticalIntent = PlayerConstants.WaterVerticalNeutral,
            Speed = PlayerConstants.WaterSpeed,
            Acceleration = PlayerConstants.WaterAcceleration,
            Drag = PlayerConstants.WaterDrag,
            Minimum = center - new Vector3(PlayerConstants.WaterExtent, 0f, PlayerConstants.WaterExtent),
            Maximum = center + new Vector3(PlayerConstants.WaterExtent, PlayerConstants.WaterHeight, PlayerConstants.WaterExtent),
            GravityScale = PlayerConstants.WaterGravityScale,
            Buoyancy = PlayerConstants.WaterBuoyancy,
            ClimbReach = PlayerConstants.NoClimbReach,
        };
        return true;
    }

    internal void Start()
    {
        if (started)
        {
            return;
        }

        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(terrain.Session));
        frame.Observe(origin);
        // The player stands on whatever ground the generator put under the spawn column, so a
        // generator change can never start them inside the terrain.
        float ground = terrain.GroundAt(playerGlobal.CellX, playerGlobal.CellZ);
        playerGlobal = PlayerWorldPosition.FromWorld(
            playerGlobal.WorldX,
            ground + (PlayerConstants.StandingHeight / 2f) + PlayerConstants.SpawnClearance,
            playerGlobal.WorldZ);

        spawn = playerGlobal;
        playerLocal = playerGlobal.ToLocal(origin);
        motion = CreateInitialMotion(playerLocal);
        camera = engine.CameraView.CreateCamera(CreateCameraDescriptor());
        engine.CameraView.SetActiveCamera(camera);
        cameraPublicationCount = 1UL;
        lastCameraPublicationUpdate = updateCount;
        terrain.SynchronizeAround(playerGlobal.FloorVoxel());
        terrain.PublishPlayerUi(ToUiFacts());
        PublishRuntimeComponent();
        started = true;
    }

    internal void Update(ProductUpdate update)
    {
        EnsureStarted();
        updateCount = checked(updateCount + 1UL);
        ProductStep step = ProductStep.From(update.Facts);
        CurrentStep = step.Step;
        if (Vitals.TryRespawn(step.Step))
        {
            // A defeated player comes back where they started, outside the ring creatures spawn
            // at, rather than inside the reach of whatever defeated them.
            MoveHome();
        }

        lastSimulationStep = update.Facts.SimulationStep;
        lastAdmittedStepCount = update.Facts.AdmittedStepCount;
        cameraSampleTimeSeconds = (update.Facts.SimulationStep + update.Facts.AdmittedStepCount)
            * update.Facts.FixedDeltaSeconds;
        CaptureInputEvents(update.Input);
        lastUpdatePositionBefore = playerLocal;
        float simulationDeltaSeconds = checked((float)(update.Facts.AdmittedStepCount * update.Facts.FixedDeltaSeconds));
        PlayerInputFrame frame = input.Consume(update.Input, simulationDeltaSeconds);
        lastInputFrame = frame;
        LookReceipt lookReceipt = Look.IntegrateClamped(new LookRequest(look, frame.LookDelta, lookConfig));
        look = lookReceipt.After;

        jumpPending |= frame.JumpHeld && !jumpHeld;
        impulsePending |= frame.ImpulseHeld && !impulseHeld;
        jumpHeld = frame.JumpHeld;
        impulseHeld = frame.ImpulseHeld;
        controllerStepAccumulator += update.Facts.AdmittedStepCount * update.Facts.FixedDeltaSeconds;
        lastControllerStepCount = 0U;
        lastStepReceipt = null;
        while (controllerStepAccumulator + PlayerConstants.ControllerStepEpsilon >= PlayerConstants.ControllerStepSeconds)
        {
            CharacterControllerConfig stepConfig = frame.SprintRequested && !frame.CrouchRequested
                ? WithSprintSpeed(controllerConfig)
                : controllerConfig;
            commandSequence = checked(commandSequence + 1UL);
            CharacterStepReceipt receipt = engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(
                terrain.Session,
                playerLocal,
                motion,
                default,
                ReadOnlyMemory<CharacterObstacle>.Empty,
                ReadOnlyMemory<CharacterMeshInstance>.Empty,
                stepConfig,
                Command(frame, look, jumpPending, impulsePending, lookReceipt, commandSequence)));
            lastControllerStepCount = checked(lastControllerStepCount + 1U);
            lastStepReceipt = receipt;
            jumpPending = false;
            impulsePending = false;
            playerLocal = receipt.Transform.Translation;
            motion = receipt.Motion;
            WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(terrain.Session));
            playerGlobal = PlayerWorldPosition.FromLocal(origin, playerLocal);
            controllerStepAccumulator -= PlayerConstants.ControllerStepSeconds;
        }
        lastUpdatePositionAfter = playerLocal;
        if (frame.PlanarIntent != Vector2.Zero || frame.JumpHeld || frame.CrouchRequested
            || frame.SprintRequested || frame.ImpulseHeld)
        {
            lastMovementUpdate = updateCount;
            lastMovementInputFrame = frame;
            lastMovementControllerStepCount = lastControllerStepCount;
            lastMovementStepReceipt = lastStepReceipt;
            lastMovementPositionBefore = lastUpdatePositionBefore;
            lastMovementPositionAfter = lastUpdatePositionAfter;
        }

        RebaseIfNeeded();
        terrain.SynchronizeAround(playerGlobal.FloorVoxel());
        if (frame.Edit is TerrainEditKind edit)
        {
            lastTerrainEdit = terrain.TryEditFromView(
                EyePosition(),
                lookReceipt.Forward,
                edit,
                PlayerConstants.PlaceMaterial,
                frame.BrushRadius,
                OverlapsVoxel);
        }

        PublishCamera();
        terrain.PublishPlayerUi(ToUiFacts());
        PublishRuntimeComponent();
    }

    /// <summary>Returns one bounded product-owned explanation of the latest movement update.</summary>
    internal string DebugReadout()
    {
        EnsureStarted();
        CharacterStepReceipt? step = lastStepReceipt;
        string stepReadout = step is not CharacterStepReceipt receipt
            ? "none"
            : string.Create(CultureInfo.InvariantCulture,
                $"attempted={receipt.Step.Attempted};accepted={receipt.Step.Accepted};wish={Format(receipt.WishVelocity)};displacement={Format(receipt.Displacement)};blocked={receipt.BlockFlags};casts={receipt.CastCount}");
        string movementReadout = lastMovementUpdate == 0UL
            ? "none"
            : string.Create(CultureInfo.InvariantCulture,
                $"update={lastMovementUpdate};intent={Format(lastMovementInputFrame.PlanarIntent)};controllerSteps={lastMovementControllerStepCount};before={Format(lastMovementPositionBefore)};after={Format(lastMovementPositionAfter)};step=[{FormatStep(lastMovementStepReceipt)}]");
        return string.Create(CultureInfo.InvariantCulture,
            $"updates={updateCount};simulationStep={lastSimulationStep};admittedSteps={lastAdmittedStepCount};controllerSteps={lastControllerStepCount};events={lastInputEventCount};totalEvents={totalInputEventCount};keys={lastKeyEventCount};pointer={lastPointerEventCount};totalPointerDeltas={totalPointerDeltaEventCount};controllerButtons={lastControllerButtonEventCount};controllerAxes={lastControllerAxisEventCount};clears={lastClearEventCount};lastEventUpdate={lastInputEventUpdate};lastEvent={lastInputEvent};intent={Format(lastInputFrame.PlanarIntent)};lookDelta={Format(lastInputFrame.LookDelta)};jump={lastInputFrame.JumpHeld};crouch={lastInputFrame.CrouchRequested};sprint={lastInputFrame.SprintRequested};before={Format(lastUpdatePositionBefore)};after={Format(lastUpdatePositionAfter)};yaw={RadiansToDegrees(look.YawRadians):F2};pitch={RadiansToDegrees(look.PitchRadians):F2};grounded={motion.Grounded};stance={motion.Stance};cameraPresentation={cameraInterpolation};cameraDelaySeconds={cameraDelaySeconds};cameraPublications={cameraPublicationCount};cameraPublishedUpdate={lastCameraPublicationUpdate};cameraPosition={Format(EyePosition())};step=[{stepReadout}];lastMovement=[{movementReadout}];water=[{waterCheck}]");
    }

    /// <summary>Returns the latest product interaction outcome without retaining Engine gameplay state.</summary>
    internal string TerrainEditReadout()
    {
        EnsureStarted();
        return TerrainWorld.FormatEditReadout(lastTerrainEdit);
    }

    /// <summary>Moves the live player through the ordinary product state and Engine publication lane.</summary>
    internal PlayerRuntimeComponent Teleport(double x, double y, double z)
    {
        EnsureStarted();
        cameraCut = true;
        playerGlobal = PlayerWorldPosition.FromWorld(x, y, z);
        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(terrain.Session));
        playerLocal = playerGlobal.ToLocal(origin);
        motion = CreateInitialMotion(playerLocal);
        controllerStepAccumulator = 0d;
        jumpPending = false;
        impulsePending = false;
        terrain.SynchronizeAround(playerGlobal.FloorVoxel());
        PublishCamera();
        terrain.PublishPlayerUi(ToUiFacts());
        PublishRuntimeComponent();
        return entityWorld.Get(playerEntity, RuntimeComponent);
    }

    internal EntityStore EntityStore => entityWorld;

    /// <summary>The player's health and defeat; creatures strike through it.</summary>
    internal PlayerVitals Vitals { get; } = new(CharacterSheet.Starting.Derived.MaximumHealth);

    /// <summary>The player's character as the rules see it, at the level they have reached.</summary>
    internal CharacterSheet Sheet => CharacterSheet.Starting with { Level = Progress.Level };

    /// <summary>The Engine step the latest update brought the player to.</summary>
    internal long CurrentStep { get; private set; }

    /// <summary>What the player has earned.</summary>
    internal PlayerProgress Progress { get; } = new();

    /// <summary>The centre of the player's capsule, in world coordinates.</summary>
    internal Vector3 WorldPosition => playerGlobal.ToWorldVector();

    internal Vector3 WorldEyePosition => WorldPosition + Vector3.UnitY * EyeOffset(motion.Stance);

    /// <summary>Where the player's capsule meets the ground, in world coordinates.</summary>
    internal Vector3 WorldFeetPosition => WorldPosition - Vector3.UnitY * (StanceHeight(motion.Stance) / 2f);

    /// <summary>
    /// A fresh session: the player returns to where they started with full vitals and no progress.
    /// </summary>
    internal void Restart()
    {
        EnsureStarted();
        Vitals.Reset();
        Progress.Reset();
        look = new LookState(DegreesToRadians(PlayerConstants.InitialYawDegrees), DegreesToRadians(PlayerConstants.InitialPitchDegrees));
        MoveHome();
    }

    private void MoveHome() => Teleport(spawn.WorldX, spawn.WorldY, spawn.WorldZ);

    public void Dispose()
    {
        if (camera is not null)
        {
            engine.CameraView.ClearActiveCamera(new ClearActiveCameraRequest(0U));
            camera.Dispose();
            camera = null;
        }

        started = false;
        entityWorld.Dispose();
    }

    private Camera Camera => camera ?? throw new InvalidOperationException("CraftSurvive camera is unavailable.");

    private void CaptureInputEvents(ReadOnlySpan<ProductInputEvent> events)
    {
        lastInputEventCount = events.Length;
        lastKeyEventCount = 0;
        lastPointerEventCount = 0;
        lastControllerButtonEventCount = 0;
        lastControllerAxisEventCount = 0;
        lastClearEventCount = 0;
        foreach (ProductInputEvent inputEvent in events)
        {
            totalInputEventCount = checked(totalInputEventCount + 1UL);
            lastInputEventUpdate = updateCount;
            switch (inputEvent.Kind)
            {
                case InputEventKind.Key:
                    lastKeyEventCount++;
                    lastInputEvent = $"key:{inputEvent.Keyboard}:{inputEvent.Edge}";
                    break;
                case InputEventKind.PointerDelta:
                    lastPointerEventCount++;
                    totalPointerDeltaEventCount = checked(totalPointerDeltaEventCount + 1UL);
                    lastInputEvent = string.Create(CultureInfo.InvariantCulture,
                        $"pointer-delta:{inputEvent.X:F3},{inputEvent.Y:F3}");
                    break;
                case InputEventKind.PointerButton:
                    lastPointerEventCount++;
                    lastInputEvent = $"pointer-button:{inputEvent.PointerButton}:{inputEvent.Edge}";
                    break;
                case InputEventKind.ControllerButton:
                    lastControllerButtonEventCount++;
                    lastInputEvent = $"controller-button:{inputEvent.ControllerButton}:{inputEvent.Edge}";
                    break;
                case InputEventKind.ControllerAxis:
                    lastControllerAxisEventCount++;
                    lastInputEvent = string.Create(CultureInfo.InvariantCulture,
                        $"controller-axis:{inputEvent.ControllerAxis}:{inputEvent.X:F3}");
                    break;
                case InputEventKind.Clear:
                    lastClearEventCount++;
                    lastInputEvent = $"clear:{inputEvent.ClearReason}";
                    break;
                default:
                    lastInputEvent = inputEvent.Kind.ToString();
                    break;
            }
        }
    }

    private static string Format(Vector2 value) => string.Create(CultureInfo.InvariantCulture,
        $"{value.X:F3},{value.Y:F3}");

    private static string Format(Vector3 value) => string.Create(CultureInfo.InvariantCulture,
        $"{value.X:F3},{value.Y:F3},{value.Z:F3}");

    /// <summary>
    /// The movement facts of the most recent character step: the Engine's answer about
    /// which mode the player is in and how submerged they are. S7's breath reads
    /// head submersion from here, and it is what makes the swim policy observable at
    /// all rather than only inferable from the command the product sent.
    /// </summary>
    internal CharacterMovementFact? LastMovementFact => lastStepReceipt?.Movement;

    /// <summary>
    /// The last water decision this controller made: which cell it read, what the
    /// Engine reported there, and which slot it was compared against. It exists so a
    /// disagreement between "the world has water here" and "the Engine says the
    /// player is walking" resolves to a cell and a slot rather than to a guess.
    /// </summary>

    private static string FormatStep(CharacterStepReceipt? step) => step is not CharacterStepReceipt receipt
        ? "none"
        : string.Create(CultureInfo.InvariantCulture,
            $"attempted={receipt.Step.Attempted};accepted={receipt.Step.Accepted};wish={Format(receipt.WishVelocity)};displacement={Format(receipt.Displacement)};blocked={receipt.BlockFlags};casts={receipt.CastCount};movement={receipt.Movement.Mode};immersion={receipt.Movement.Immersion:F3};headSubmerged={receipt.Movement.HeadSubmerged}");

    private void RebaseIfNeeded()
    {
        if (MathF.Abs(playerLocal.X) < PlayerConstants.RebaseThreshold
            && MathF.Abs(playerLocal.Z) < PlayerConstants.RebaseThreshold)
        {
            return;
        }

        Vector3 playerBeforeRebase = playerLocal;
        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(terrain.Session));
        WorldOriginEntityRow[] roots =
        [
            new WorldOriginEntityRow(PlayerConstants.PlayerEntityId, PlayerTransform(), playerGlobal.ToEngine()),
        ];
        using WorldOriginPrepared prepared = engine.WorldOrigin.Prepare(new WorldOriginPrepareRequest(
            terrain.Session,
            playerGlobal.CellX,
            origin.CellY,
            playerGlobal.CellZ,
            roots));
        // Prepare returns one affected transform per root, in request order.
        ReadOnlySpan<WorldOriginAffectedTransform> affected = engine.WorldOrigin.ReadPrepared(
            new WorldOriginPreparedReadRequest(prepared)).Affected.Span;
        WorldOriginCommitReceipt committed = engine.WorldOrigin.Commit(new WorldOriginCommitRequest(prepared));
        cameraCut = true;
        playerLocal = affected[0].LocalTransform.Translation;
        Vector3 localTranslation = playerLocal - playerBeforeRebase;
        motion = motion.Rebased(localTranslation) with
        {
            CollisionWorldHash = PlayerConstants.UninitializedCollisionWorldHash,
        };
        frame.Commit(playerGlobal.CellX, origin.CellY, playerGlobal.CellZ);
    }

    private bool OverlapsVoxel(TerrainVoxelAddress voxel)
    {
        float height = motion.Stance == CharacterStance.Crouched
            ? controllerConfig.Shape.CrouchedHeight
            : controllerConfig.Shape.StandingHeight;
        double halfHeight = height / 2d;
        return (playerGlobal.WorldX - controllerConfig.Shape.Radius) < voxel.X + 1L
            && (playerGlobal.WorldX + controllerConfig.Shape.Radius) > voxel.X
            && (playerGlobal.WorldY - halfHeight) < voxel.Y + 1L
            && (playerGlobal.WorldY + halfHeight) > voxel.Y
            && (playerGlobal.WorldZ - controllerConfig.Shape.Radius) < voxel.Z + 1L
            && (playerGlobal.WorldZ + controllerConfig.Shape.Radius) > voxel.Z;
    }

    private Vector3 EyePosition() => playerLocal + Vector3.UnitY * EyeOffset(motion.Stance);

    private Transform PlayerTransform() => new(playerLocal, Quaternion.Identity, Vector3.One);


    private CameraDescriptor CreateCameraDescriptor() => new(
        new CameraPose(EyePosition(), RadiansToDegrees(look.PitchRadians), RadiansToDegrees(look.YawRadians)),
        CameraBasisMode.Derived,
        default,
        new CameraProjection(CameraProjectionKind.Perspective, PlayerConstants.CameraFieldOfViewDegrees, 0d,
            PlayerConstants.CameraNearDistance, PlayerConstants.CameraFarDistance),
        new CameraViewport(PlayerConstants.CameraViewportOrigin, PlayerConstants.CameraViewportOrigin,
            PlayerConstants.CameraViewportExtent, PlayerConstants.CameraViewportExtent));

    internal string SetCameraPresentation(string mode, double delaySeconds)
    {
        CameraInterpolation selected = mode.ToLowerInvariant() switch
        {
            "latest" => CameraInterpolation.Latest,
            "position" => CameraInterpolation.Position,
            "pose" => CameraInterpolation.Pose,
            _ => throw new ArgumentException("Camera mode must be latest, position, or pose.", nameof(mode)),
        };
        if (!double.IsFinite(delaySeconds) || delaySeconds <= 0d)
            throw new ArgumentOutOfRangeException(nameof(delaySeconds), "Delay must be finite and positive.");
        cameraInterpolation = selected;
        cameraDelaySeconds = delaySeconds;
        cameraCut = true;
        PublishCamera();
        return FormattableString.Invariant($"cameraPresentation={selected};delaySeconds={delaySeconds}");
    }

    private void PublishCamera()
    {
        engine.CameraView.UpdateCameraSample(new CameraSampleRequest(Camera, CreateCameraDescriptor(),
            cameraSampleTimeSeconds, cameraDelaySeconds, cameraInterpolation, cameraCut ? (byte)1 : (byte)0));
        cameraCut = false;
        cameraPublicationCount = checked(cameraPublicationCount + 1UL);
        lastCameraPublicationUpdate = updateCount;
    }

    private TerrainPlayerUiFacts ToUiFacts() => new(
        playerGlobal.WorldX,
        playerGlobal.WorldY + EyeOffset(motion.Stance),
        playerGlobal.WorldZ,
        RadiansToDegrees(look.YawRadians),
        RadiansToDegrees(look.PitchRadians),
        motion.Grounded,
        motion.Stance == CharacterStance.Crouched);

    private void PublishRuntimeComponent()
    {
        entityWorld.Set(playerEntity, RuntimeComponent, new PlayerRuntimeComponent(
            playerGlobal.WorldX,
            playerGlobal.WorldY,
            playerGlobal.WorldZ,
            RadiansToDegrees(look.YawRadians),
            RadiansToDegrees(look.PitchRadians),
            motion.Grounded,
            motion.Stance == CharacterStance.Crouched));
    }

    private static CharacterMotion CreateInitialMotion(Vector3 localPosition) => new(
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
        localPosition.Y,
        localPosition.Y,
        0UL,
        0UL);

    private void EnsureStarted()
    {
        if (!started)
        {
            throw new InvalidOperationException("CraftSurvive player has not started.");
        }
    }

    private static CharacterControllerConfig CreateControllerConfig(CharacterControllerConfig baseline) => baseline with
    {
        Shape = baseline.Shape with
        {
            StandingHeight = PlayerConstants.StandingHeight,
            CrouchedHeight = PlayerConstants.CrouchedHeight,
            Radius = PlayerConstants.CapsuleRadius,
            ContactSkin = PlayerConstants.ContactSkin,
        },
        Ground = baseline.Ground with
        {
            ForwardSpeed = PlayerConstants.GroundSpeed,
            BackwardSpeed = PlayerConstants.GroundSpeed,
            StrafeSpeed = PlayerConstants.GroundSpeed,
            Acceleration = PlayerConstants.GroundAcceleration,
            Braking = PlayerConstants.GroundBraking,
            Friction = PlayerConstants.GroundFriction,
        },
        Air = baseline.Air with
        {
            MaximumSpeed = PlayerConstants.GroundSpeed,
            Acceleration = PlayerConstants.AirAcceleration,
            WishSpeedCap = PlayerConstants.GroundSpeed,
        },
        Vertical = baseline.Vertical with
        {
            Gravity = PlayerConstants.Gravity,
            JumpSpeed = PlayerConstants.JumpSpeed,
            TerminalFallSpeed = PlayerConstants.TerminalFallSpeed,
        },
        Surface = baseline.Surface with
        {
            MaximumSlopeRadians = DegreesToRadians(PlayerConstants.MaximumSlopeDegrees),
            MaximumStepHeight = PlayerConstants.MaximumStepHeight,
            FloorSnapDistance = PlayerConstants.FloorSnapDistance,
            FloorSnapSpeedLimit = PlayerConstants.FloorSnapSpeedLimit,
        },
        ExternalMotion = baseline.ExternalMotion with { ExternalDecayPerSecond = PlayerConstants.ExternalDecayPerSecond },
    };

    private static CharacterControllerConfig WithSprintSpeed(CharacterControllerConfig baseline) => baseline with
    {
        Ground = baseline.Ground with
        {
            ForwardSpeed = PlayerConstants.SprintSpeed,
            BackwardSpeed = PlayerConstants.SprintSpeed,
            StrafeSpeed = PlayerConstants.SprintSpeed,
        },
    };

    private static float StanceHeight(CharacterStance stance) => stance == CharacterStance.Crouched
        ? PlayerConstants.CrouchedHeight
        : PlayerConstants.StandingHeight;

    private static float EyeOffset(CharacterStance stance)
    {
        float eyeHeight = stance == CharacterStance.Crouched
            ? PlayerConstants.CrouchedEyeHeight
            : PlayerConstants.StandingEyeHeight;
        float capsuleHeight = stance == CharacterStance.Crouched
            ? PlayerConstants.CrouchedHeight
            : PlayerConstants.StandingHeight;
        return eyeHeight - capsuleHeight / 2f;
    }

    private static float DegreesToRadians(double value) => checked((float)(value * Math.PI / 180d));

    private static double RadiansToDegrees(float value) => value * 180d / Math.PI;
}

internal readonly record struct PlayerRuntimeComponent(
    double X,
    double Y,
    double Z,
    double YawDegrees,
    double PitchDegrees,
    bool Grounded,
    bool Crouched);

/// <summary>The last water decision: the global cells read and what the Engine reported there.</summary>
internal readonly record struct PlayerWaterCheck(
    Rusty.Engine.VoxelAddress Feet,
    bool FeetPresent,
    uint FeetSlot,
    Rusty.Engine.VoxelAddress Eyes,
    bool FeetWater,
    bool EyesWater)
{
    internal bool InWater => FeetWater || EyesWater;

    public override string ToString() => FormattableString.Invariant(
        $"feet=({Feet.X},{Feet.Y},{Feet.Z});feetPresent={FeetPresent};feetSlot={FeetSlot};eyes=({Eyes.X},{Eyes.Y},{Eyes.Z});feetWater={FeetWater};eyesWater={EyesWater}");
}
