using System.Globalization;
using System.Numerics;
using Rusty.Engine;
using Rusty.Engine.Entities;
using CraftSurvive.Game.Modules.Terrain;
using TerrainVoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// Product-owned player policy. It retains only input, pose, and platform facts;
/// Engine services continue to integrate look, collision, world-origin, and the camera view.
/// </summary>
internal sealed class PlayerController : IDisposable
{
    internal static readonly ComponentType<PlayerRuntimeComponent> RuntimeComponent =
        ComponentType<PlayerRuntimeComponent>.Create(
            ProductComponentKeys.Create(PlayerConstants.RuntimeComponentLocalId));

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly PlayerInputState input = new();
    private readonly EntityWorld entityWorld = new([RuntimeComponent]);
    private readonly EntityId playerEntity;
    private readonly CharacterControllerConfig controllerConfig;
    private readonly LookConfig lookConfig;
    private Camera? camera;
    private Appearance? platformAppearance;
    private CharacterMotion motion;
    private LookState look;
    private PlayerWorldPosition playerGlobal;
    private PlayerWorldPosition platformGlobal;
    private Vector3 playerLocal;
    private Vector3 platformLocal;
    private Vector3 platformLinearVelocity;
    private float platformDirection = 1f;
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
    private int lastClearEventCount;
    private string lastInputEvent = "none";
    private ulong totalInputEventCount;
    private ulong lastInputEventUpdate;
    private PlayerInputFrame lastInputFrame;
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
    private ulong cameraPublicationCount;
    private ulong lastCameraPublicationUpdate;

    internal PlayerController(IEngineContext engine, TerrainWorld terrain)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
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
        playerGlobal = PlayerWorldPosition.FromWorld(PlayerConstants.InitialEyePosition - new Vector3(
            0f,
            EyeOffset(CharacterStance.Standing),
            0f));
        platformGlobal = PlayerWorldPosition.FromWorld(PlayerConstants.PlatformInitialCenter);
        playerEntity = entityWorld.Create();
        PublishRuntimeComponent();
    }

    internal void Start()
    {
        if (started)
        {
            return;
        }

        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(terrain.Session));
        playerLocal = playerGlobal.ToLocal(origin);
        platformLocal = platformGlobal.ToLocal(origin);
        motion = CreateInitialMotion(playerLocal);
        platformAppearance = engine.Appearance.CreatePrimitive(new PrimitiveAppearanceRequest(
            PrimitiveGeometry.Cube,
            Wireframe: false,
            PlayerConstants.PlatformColor));
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
        lastSimulationStep = update.Facts.SimulationStep;
        lastAdmittedStepCount = update.Facts.AdmittedStepCount;
        CaptureInputEvents(update.Input);
        lastUpdatePositionBefore = playerLocal;
        PlayerInputFrame frame = input.Consume(update.Input);
        lastInputFrame = frame;
        LookReceipt lookReceipt = engine.Look.Integrate(new LookRequest(look, frame.LookDelta, lookConfig));
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
            AdvancePlatform((float)PlayerConstants.ControllerStepSeconds);
            CharacterControllerConfig stepConfig = frame.SprintRequested && !frame.CrouchRequested
                ? WithSprintSpeed(controllerConfig)
                : controllerConfig;
            commandSequence = checked(commandSequence + 1UL);
            CharacterStepReceipt receipt = engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(
                terrain.Session,
                playerLocal,
                motion,
                CurrentSupport(),
                CurrentPlatformObstacle(),
                stepConfig,
                new CharacterControllerCommand(
                    frame.PlanarIntent,
                    look.YawRadians,
                    jumpPending,
                    frame.JumpHeld,
                    frame.CrouchRequested,
                    Vector3.Zero,
                    impulsePending ? lookReceipt.Right * PlayerConstants.ImpulseSpeed
                        + Vector3.UnitY * PlayerConstants.ImpulseLift : Vector3.Zero,
                    (float)PlayerConstants.ControllerStepSeconds,
                    commandSequence)));
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

        if (RebaseIfNeeded())
        {
            terrain.RefreshAfterWorldOriginCommit(ToUiFacts());
        }
        terrain.SynchronizeAround(playerGlobal.FloorVoxel());
        if (frame.Edit is TerrainEditKind edit)
        {
            terrain.TryEditFromView(
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
            $"updates={updateCount};simulationStep={lastSimulationStep};admittedSteps={lastAdmittedStepCount};controllerSteps={lastControllerStepCount};events={lastInputEventCount};totalEvents={totalInputEventCount};keys={lastKeyEventCount};pointer={lastPointerEventCount};clears={lastClearEventCount};lastEventUpdate={lastInputEventUpdate};lastEvent={lastInputEvent};intent={Format(lastInputFrame.PlanarIntent)};lookDelta={Format(lastInputFrame.LookDelta)};jump={lastInputFrame.JumpHeld};crouch={lastInputFrame.CrouchRequested};sprint={lastInputFrame.SprintRequested};before={Format(lastUpdatePositionBefore)};after={Format(lastUpdatePositionAfter)};yaw={RadiansToDegrees(look.YawRadians):F2};pitch={RadiansToDegrees(look.PitchRadians):F2};grounded={motion.Grounded};stance={motion.Stance};cameraPublications={cameraPublicationCount};cameraPublishedUpdate={lastCameraPublicationUpdate};cameraPosition={Format(EyePosition())};step=[{stepReadout}];lastMovement=[{movementReadout}]");
    }

    /// <summary>Republishes player-owned presentation without advancing simulation state.</summary>
    internal void Attach()
    {
        EnsureStarted();
        PublishCamera();
        terrain.PublishPlayerUi(ToUiFacts());
    }

    /// <summary>Moves the live player through the same product-owned state and Engine publication lane used by gameplay.</summary>
    internal PlayerRuntimeComponent Teleport(double x, double y, double z)
    {
        EnsureStarted();
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

    internal EntityWorld EntityWorld => entityWorld;

    /// <summary>
    /// Returns the current platform fact for the product's single complete
    /// Appearance snapshot. The root composes this with other product-owned
    /// source facts so modules never replace each other's retained visuals.
    /// </summary>
    internal AppearanceFact PlatformAppearanceFact
    {
        get
        {
            Appearance appearance = platformAppearance
                ?? throw new InvalidOperationException("CraftSurvive platform appearance is unavailable.");
            return new AppearanceFact(
                PlayerConstants.PlatformEntityId,
                new Transform(platformLocal, Quaternion.Identity, PlayerConstants.PlatformScale),
                appearance,
                Visible: true,
                RenderLayer.Scene);
        }
    }

    public void Dispose()
    {
        if (camera is not null)
        {
            engine.CameraView.ClearActiveCamera(new ClearActiveCameraRequest(0U));
            camera.Dispose();
            camera = null;
        }

        if (platformAppearance is not null)
        {
            platformAppearance.Dispose();
            platformAppearance = null;
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
                    lastInputEvent = string.Create(CultureInfo.InvariantCulture,
                        $"pointer-delta:{inputEvent.X:F3},{inputEvent.Y:F3}");
                    break;
                case InputEventKind.PointerButton:
                    lastPointerEventCount++;
                    lastInputEvent = $"pointer-button:{inputEvent.PointerButton}:{inputEvent.Edge}";
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

    private static string FormatStep(CharacterStepReceipt? step) => step is not CharacterStepReceipt receipt
        ? "none"
        : string.Create(CultureInfo.InvariantCulture,
            $"attempted={receipt.Step.Attempted};accepted={receipt.Step.Accepted};wish={Format(receipt.WishVelocity)};displacement={Format(receipt.Displacement)};blocked={receipt.BlockFlags};casts={receipt.CastCount}");

    private void AdvancePlatform(float stepSeconds)
    {
        platformLinearVelocity = Vector3.Zero;
        double deltaX = playerGlobal.WorldX - platformGlobal.WorldX;
        double deltaY = playerGlobal.WorldY - platformGlobal.WorldY;
        double deltaZ = playerGlobal.WorldZ - platformGlobal.WorldZ;
        if ((deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ)
            > PlayerConstants.PlatformActivityRadius * PlayerConstants.PlatformActivityRadius)
        {
            return;
        }

        if (platformGlobal.WorldX >= PlayerConstants.PlatformTravelMaximumX)
        {
            platformDirection = -1f;
        }
        else if (platformGlobal.WorldX <= PlayerConstants.PlatformTravelMinimumX)
        {
            platformDirection = 1f;
        }

        platformGlobal = PlayerWorldPosition.FromWorld(
            platformGlobal.WorldX + (platformDirection * PlayerConstants.PlatformSpeed * stepSeconds),
            platformGlobal.WorldY,
            platformGlobal.WorldZ);
        platformLinearVelocity = Vector3.UnitX * (platformDirection * PlayerConstants.PlatformSpeed);
        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(terrain.Session));
        platformLocal = platformGlobal.ToLocal(origin);
    }

    private bool RebaseIfNeeded()
    {
        if (MathF.Abs(playerLocal.X) < PlayerConstants.RebaseThreshold
            && MathF.Abs(playerLocal.Z) < PlayerConstants.RebaseThreshold)
        {
            return false;
        }

        Vector3 playerBeforeRebase = playerLocal;
        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(terrain.Session));
        WorldOriginEntityRow[] roots =
        [
            new WorldOriginEntityRow(PlayerConstants.PlayerEntityId, PlayerTransform(), playerGlobal.ToEngine()),
            new WorldOriginEntityRow(PlayerConstants.PlatformEntityId, PlatformTransform(), platformGlobal.ToEngine()),
        ];
        using WorldOriginPrepared prepared = engine.WorldOrigin.Prepare(new WorldOriginPrepareRequest(
            terrain.Session,
            origin.Revision,
            origin.VoxelSourceRevision,
            origin.StaticMeshRevision,
            playerGlobal.CellX,
            origin.CellY,
            playerGlobal.CellZ,
            roots));
        WorldOriginAffectedAtReceipt player = engine.WorldOrigin.ReadAffectedAt(
            new WorldOriginAffectedAtRequest(prepared, 0U));
        WorldOriginAffectedAtReceipt platform = engine.WorldOrigin.ReadAffectedAt(
            new WorldOriginAffectedAtRequest(prepared, 1U));
        if (!player.Present || player.EntityId != PlayerConstants.PlayerEntityId
            || !platform.Present || platform.EntityId != PlayerConstants.PlatformEntityId)
        {
            throw new InvalidOperationException("Engine world-origin preparation did not retain CraftSurvive roots.");
        }

        engine.WorldOrigin.Commit(new WorldOriginCommitRequest(prepared));
        playerLocal = player.LocalTransform.Translation;
        platformLocal = platform.LocalTransform.Translation;
        Vector3 localTranslation = playerLocal - playerBeforeRebase;
        motion = motion with
        {
            SupportPreviousTranslation = motion.SupportPreviousTranslation + localTranslation,
            FallOriginY = motion.FallOriginY + localTranslation.Y,
            PeakY = motion.PeakY + localTranslation.Y,
            CollisionWorldHash = PlayerConstants.UninitializedCollisionWorldHash,
        };
        return true;
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

    private Transform PlatformTransform() => new(platformLocal, Quaternion.Identity, Vector3.One);

    private ReadOnlyMemory<CharacterObstacle> CurrentPlatformObstacle() => new CharacterObstacle[]
    {
        new(
            PlayerConstants.PlatformEntityId,
            PlatformTransform(),
            -PlayerConstants.PlatformHalfExtents,
            PlayerConstants.PlatformHalfExtents,
            CollisionEnabled: true,
            platformLinearVelocity,
            Vector3.Zero),
    };

    private CameraDescriptor CreateCameraDescriptor() => new(
        new CameraPose(EyePosition(), RadiansToDegrees(look.PitchRadians), RadiansToDegrees(look.YawRadians)),
        CameraBasisMode.Derived,
        default,
        new CameraProjection(CameraProjectionKind.Perspective, PlayerConstants.CameraFieldOfViewDegrees, 0d,
            PlayerConstants.CameraNearDistance, PlayerConstants.CameraFarDistance),
        new CameraViewport(PlayerConstants.CameraViewportOrigin, PlayerConstants.CameraViewportOrigin,
            PlayerConstants.CameraViewportExtent, PlayerConstants.CameraViewportExtent));

    private void PublishCamera()
    {
        engine.CameraView.UpdateCamera(new CameraUpdateRequest(Camera, CreateCameraDescriptor()));
        cameraPublicationCount = checked(cameraPublicationCount + 1UL);
        lastCameraPublicationUpdate = updateCount;
    }

    private CharacterSupport CurrentSupport()
    {
        if (!motion.SupportEntityPresent)
        {
            return default;
        }

        if (motion.SupportEntity != PlayerConstants.PlatformEntityId)
        {
            throw new InvalidOperationException("CraftSurvive only resumes the product-owned moving platform support.");
        }

        return new CharacterSupport(true, CharacterSupportLifecycle.Active,
            PlayerConstants.PlatformEntityId, PlatformTransform());
    }

    private TerrainPlayerUiFacts ToUiFacts() => new(
        playerGlobal.WorldX,
        playerGlobal.WorldY + EyeOffset(motion.Stance),
        playerGlobal.WorldZ,
        RadiansToDegrees(look.YawRadians),
        RadiansToDegrees(look.PitchRadians),
        motion.Grounded,
        motion.Stance == CharacterStance.Crouched,
        platformGlobal.WorldX,
        platformGlobal.WorldY,
        platformGlobal.WorldZ);

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
