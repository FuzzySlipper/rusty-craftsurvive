using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
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
/// progress and applies the respawn they request. The camera, the water decision, input
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
    private readonly PlayerInputState input = new();
    private readonly PlayerInputDiagnostics diagnostics = new();
    private readonly PlayerWaterProbe water;
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
    private bool started;
    private ulong updateCount;
    private ulong lastSimulationStep;
    private uint lastAdmittedStepCount;
    private uint lastControllerStepCount;
    private PlayerInputFrame lastInputFrame;
    private CharacterStepReceipt? lastStepReceipt;
    private Vector3 lastUpdatePositionBefore;
    private Vector3 lastUpdatePositionAfter;
    private TerrainWorldEditResult? lastTerrainEdit;

    internal PlayerController(IEngineContext engine, TerrainWorld terrain, WorldFrame frame, ProductStore store, ProductUiPublisher ui)
    {
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        ArgumentNullException.ThrowIfNull(frame);
        water = new PlayerWaterProbe(engine);
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

    internal EntityStore EntityStore => entityWorld;

    /// <summary>The player's health and defeat; creatures strike through it.</summary>
    internal PlayerVitals Vitals { get; } = new(CharacterSheet.Starting.Derived.MaximumHealth);

    /// <summary>What the player has earned.</summary>
    internal PlayerProgress Progress { get; } = new();

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
        spawn = StandingAt(PlayerConstants.SpawnColumn.X, terrain.GroundAt(playerGlobal.CellX, playerGlobal.CellZ), PlayerConstants.SpawnColumn.Y);
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
        if (Vitals.TryRespawn(step.Step))
        {
            // A defeated player comes back where they started, outside the ring creatures spawn
            // at, rather than inside the reach of whatever defeated them.
            MoveHome();
        }

        lastSimulationStep = update.Facts.SimulationStep;
        lastAdmittedStepCount = update.Facts.AdmittedStepCount;
        cameraSampleTimeSeconds = step.Step * step.FixedDeltaSeconds;
        diagnostics.Capture(update.Input, updateCount);
        lastUpdatePositionBefore = playerLocal;
        PlayerInputFrame frame = input.Consume(update.Input, checked((float)step.ElapsedSeconds));
        lastInputFrame = frame;
        LookReceipt lookReceipt = Look.IntegrateClamped(new LookRequest(look, frame.LookDelta, PlayerBody.Look));
        look = lookReceipt.After;

        jumpPending |= frame.JumpHeld && !jumpHeld;
        impulsePending |= frame.ImpulseHeld && !impulseHeld;
        jumpHeld = frame.JumpHeld;
        impulseHeld = frame.ImpulseHeld;
        StepCharacter(frame, lookReceipt, step.ElapsedSeconds);
        lastUpdatePositionAfter = playerLocal;
        diagnostics.RecordMovement(updateCount, frame, lastControllerStepCount, lastStepReceipt,
            lastUpdatePositionBefore, lastUpdatePositionAfter);

        if (WorldOriginRebaser.IsNeeded(playerLocal))
        {
            Vector3 before = playerLocal;
            playerLocal = rebaser.Rebase(terrain.Session, playerGlobal, playerLocal);
            motion = motion.Rebased(playerLocal - before) with
            {
                CollisionWorldHash = PlayerConstants.UninitializedCollisionWorldHash,
            };
            camera.Cut();
        }

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

        camera.Publish(EyePosition(), look, cameraSampleTimeSeconds, updateCount);
        ui.PublishPlayer(ToUiFacts());
        PublishRuntimeComponent();
        continuation.SaveIfDue(step.Step, Continuation());
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
            + $"{camera.Readout()};cameraPosition={PlayerInputDiagnostics.Format(EyePosition())};step=[{stepReadout}];lastMovement=[{diagnostics.MovementReadout()}];water=[{water.LastCheck}]";
    }

    /// <summary>Returns the latest product interaction outcome without retaining Engine gameplay state.</summary>
    internal string TerrainEditReadout()
    {
        EnsureStarted();
        return TerrainWorldEditResult.Format(lastTerrainEdit);
    }

    internal string SetCameraPresentation(CameraInterpolation mode, double delaySeconds)
    {
        camera.SetPresentation(mode, delaySeconds);
        camera.Publish(EyePosition(), look, cameraSampleTimeSeconds, updateCount);
        return FormattableString.Invariant($"cameraPresentation={mode};delaySeconds={delaySeconds}");
    }

    /// <summary>Moves the live player through the ordinary product state and Engine publication lane.</summary>
    internal PlayerRuntimeComponent Teleport(double x, double y, double z)
    {
        EnsureStarted();
        playerGlobal = PlayerWorldPosition.FromWorld(x, y, z);
        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(terrain.Session));
        playerLocal = playerGlobal.ToLocal(origin);
        motion = PlayerBody.AtRest(playerLocal);
        controllerStepAccumulator = 0d;
        jumpPending = false;
        impulsePending = false;
        terrain.SynchronizeAround(playerGlobal.FloorVoxel());
        camera.Cut();
        camera.Publish(EyePosition(), look, cameraSampleTimeSeconds, updateCount);
        ui.PublishPlayer(ToUiFacts());
        PublishRuntimeComponent();
        return entityWorld.Get(playerEntity, RuntimeComponent);
    }

    /// <summary>A fresh session: the player returns to where they started with full vitals and no progress.</summary>
    internal void Restart()
    {
        EnsureStarted();
        Vitals.Reset();
        Progress.Reset();
        look = StartingLook;
        MoveHome();
    }

    public void Dispose()
    {
        if (started)
        {
            continuation.Save(Continuation(), CurrentStep);
        }

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
            CharacterStepReceipt receipt = engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(
                terrain.Session,
                playerLocal,
                motion,
                default,
                ReadOnlyMemory<CharacterObstacle>.Empty,
                ReadOnlyMemory<CharacterMeshInstance>.Empty,
                stepConfig,
                Command(frame, lookReceipt, commandSequence)));
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
    }

    /// <summary>
    /// The controller command for one step: walking, or swimming when the player's own cells are
    /// water, in which case the water probe composes the volume the Engine swims them through.
    /// </summary>
    private CharacterControllerCommand Command(PlayerInputFrame frame, LookReceipt lookReceipt, ulong sequence)
    {
        Vector3 impulse = impulsePending
            ? (lookReceipt.Right * PlayerConstants.ImpulseSpeed) + (Vector3.UnitY * PlayerConstants.ImpulseLift)
            : Vector3.Zero;
        float stepSeconds = (float)PlayerConstants.ControllerStepSeconds;
        return water.TrySwim(terrain.Session, playerGlobal, playerLocal, motion.Stance, out CharacterMovementRequest swim)
            ? new CharacterControllerCommand(
                swim, frame.PlanarIntent, look.YawRadians, jumpPending, frame.JumpHeld,
                frame.CrouchRequested, Vector3.Zero, impulse, stepSeconds, sequence)
            : new CharacterControllerCommand(
                frame.PlanarIntent, look.YawRadians, jumpPending, frame.JumpHeld, frame.CrouchRequested,
                Vector3.Zero, impulse, stepSeconds, sequence);
    }

    private void MoveHome() => Teleport(spawn.WorldX, spawn.WorldY, spawn.WorldZ);

    /// <summary>The capsule centre of a player standing with their feet at a height.</summary>
    private static PlayerWorldPosition StandingAt(double x, double feetY, double z) =>
        PlayerWorldPosition.FromWorld(x, feetY + (PlayerConstants.StandingHeight / 2f) + PlayerConstants.SpawnClearance, z);

    private PlayerContinuation Continuation()
    {
        Vector3 feet = WorldFeetPosition;
        PlayerDefeatState vitals = Vitals.State;
        return new PlayerContinuation(
            playerGlobal.WorldX, feet.Y, playerGlobal.WorldZ,
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

    /// <summary>Whether a standing body at this position is inside the world and clear of every collidable block.</summary>
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
            for (long y = (long)Math.Floor(position.WorldY - halfHeight); y <= (long)Math.Floor(position.WorldY + halfHeight); y++)
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
        motion.Stance == CharacterStance.Crouched);

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

internal readonly record struct PlayerRuntimeComponent(
    double X,
    double Y,
    double Z,
    double YawDegrees,
    double PitchDegrees,
    bool Grounded,
    bool Crouched);
