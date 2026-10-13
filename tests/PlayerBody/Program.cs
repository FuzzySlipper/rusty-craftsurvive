using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Tests;
using Rusty.Engine;
using Rusty.Engine.Testing;

// The player's body against the Engine's own collision, headless: a body sunk deep in stone is
// refused a step by the Engine, and the product's recovery stands it at the first clear place
// instead of letting the refusal stop the game.

const int Edge = TerrainConstants.ChunkEdgeLength;

// One chunk, stone below this height and air above.
const int StoneTop = 8;
const ushort StoneSlot = (ushort)BlockId.Stone;

using EngineTestHost host = EngineTestHost.Create(new EngineTestHostOptions());
host.Call(engine =>
{
    using SpatialSession session = engine.Spatial.CreateSession(new SpatialSessionConfig(
        TerrainConstants.VoxelSize, TerrainConstants.VoxelChunkSize, VoxelSurfaceMode.GreedyCubes));
    engine.Voxel.ConfigureMaterialCollision(new VoxelMaterialCollisionRequest(session, new[] { new VoxelMaterialCollision(StoneSlot, true) }));
    uint[] materials = new uint[Edge * Edge * Edge];
    for (int z = 0; z < Edge; z++)
    {
        for (int y = 0; y < StoneTop; y++)
        {
            for (int x = 0; x < Edge; x++)
            {
                materials[(z * Edge * Edge) + (y * Edge) + x] = StoneSlot;
            }
        }
    }

    engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(session,
        new[] { new VoxelResidencyOperation(VoxelResidencyOperationKind.Admit, new VoxelChunkIdentity(0, 0, 0), 0U, (uint)materials.Length) },
        materials));

    CharacterControllerConfig body = PlayerBody.Configure(engine.Spatial.DefaultCharacterControllerConfig());
    float height = PlayerBody.Height(CharacterStance.Standing);
    Vector3 sunk = new(8.5f, StoneTop - 1f, 8.5f);
    Vector3 standing = new(8.5f, StoneTop + (height / 2f) + PlayerConstants.SpawnClearance, 8.5f);

    Check.Section("the Engine refuses a step deep in collision with the code recovery listens for", () =>
    {
        EngineCallException? refusal = null;
        try
        {
            engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(
                session, sunk, PlayerBody.AtRest(sunk), default,
                ReadOnlyMemory<CharacterObstacle>.Empty, ReadOnlyMemory<CharacterMeshInstance>.Empty,
                body, default(CharacterControllerCommand) with { StepSeconds = (float)PlayerConstants.ControllerStepSeconds, Sequence = 1UL }));
        }
        catch (EngineCallException caught)
        {
            refusal = caught;
        }

        Check.That(refusal is not null && PlayerRecovery.IsPenetration(refusal),
            $"a body {StoneTop - sunk.Y:F0} m into stone must be refused with {PlayerRecovery.PenetrationCode}, was {refusal?.Message ?? "accepted"}");
    });

    Check.Section("recovery stands a sunk body at the first clear place above", () =>
    {
        Vector3? clear = PlayerRecovery.FirstClear(engine.Spatial, session, PlayerRecovery.Candidates(sunk, sunk),
            height, body.Shape.Radius, body.Shape.ContactSkin);
        Check.That(clear is Vector3 found && found.Y >= StoneTop + (height / 2f) && found.Y < StoneTop + (height / 2f) + PlayerRecovery.SearchStepMetres + 0.1f,
            $"the body must be stood just clear of the stone's top, was {clear}");
        if (clear is Vector3 place)
        {
            CharacterStepReceipt receipt = engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(
                session, place, PlayerBody.AtRest(place), default,
                ReadOnlyMemory<CharacterObstacle>.Empty, ReadOnlyMemory<CharacterMeshInstance>.Empty,
                body, default(CharacterControllerCommand) with { StepSeconds = (float)PlayerConstants.ControllerStepSeconds, Sequence = 2UL }));
            Check.That(receipt.Transform.Translation.Y >= StoneTop + (height / 2f) - body.Recovery.NormalNudge,
                $"the next step from the recovered place must go ahead with the body on the stone, was at {receipt.Transform.Translation.Y:F2}");
        }
    });

    Check.Section("where the player last stood clear is tried first", () =>
    {
        Vector3? clear = PlayerRecovery.FirstClear(engine.Spatial, session, PlayerRecovery.Candidates(sunk, standing),
            height, body.Shape.Radius, body.Shape.ContactSkin);
        Check.Equal<Vector3?>(standing, clear, "a body sunk after standing clear must go back to where it stood");
    });

    Check.Section("a body buried deeper than the search reaches stays put rather than being placed in stone", () =>
    {
        // Low enough in the stone that the whole search, straight up, stays inside it.
        Vector3 buried = sunk with { Y = (height / 2f) + PlayerConstants.SpawnClearance };
        Vector3? clear = PlayerRecovery.FirstClear(engine.Spatial, session, PlayerRecovery.Candidates(buried, buried),
            height, body.Shape.Radius, body.Shape.ContactSkin);
        Check.Equal<Vector3?>(null, clear, "no place inside the stone may be called clear");
    });

    // A river's current on a swimmer (#9879): it builds after a grace, carries the swimmer, and weakens
    // their stroke to their own; a starting swimmer is swept back by a current a strong one swims against.
    Check.Section("a current sweeps a weak swimmer back and a strong one holds against it", () =>
    {
        SwimCurrent rules = new();
        rules.Step(0.5, new Vector2(2, 0), CharacterRules.SwimStroke(CharacterAttributes.Starting));
        Check.That(Math.Abs(rules.Share - SwimCurrent.GraceShare) < 1e-9 && Math.Abs(rules.Push.X - (2 * SwimCurrent.GraceShare)) < 1e-6
            && rules.Stroke > PlayerConstants.WaterSpeed * 0.8, "within the grace the current only tugs, and the swimmer keeps most of their stroke");
        for (int i = 0; i < 10; i++) rules.Step(0.5, new Vector2(2, 0), CharacterRules.SwimStroke(CharacterAttributes.Starting));
        Check.That(rules.Share == 1 && Math.Abs(rules.Push.X - 2) < 1e-6 && Math.Abs(rules.Stroke - CharacterRules.SwimStroke(CharacterAttributes.Starting)) < 1e-6,
            "past the grace and the ramp the current bears in full, and the swimmer strokes only as strongly as they are");
        double held = rules.Exposure;
        rules.Step(1, null, CharacterRules.SwimStroke(CharacterAttributes.Starting));
        Check.That(rules.Push == Vector2.Zero && rules.Stroke == PlayerConstants.WaterSpeed && Math.Abs(rules.Exposure - (held - SwimCurrent.RecoverRate)) < 1e-9,
            "out of the current it lets go at once, and its hold drains");
        rules.Step(1, new Vector2(0.01f, 0), CharacterRules.SwimStroke(CharacterAttributes.Starting));
        Check.That(rules.Push == Vector2.Zero && rules.Stroke == PlayerConstants.WaterSpeed, "still water leaves swimming as it was");
        Check.That(CharacterRules.SwimStroke(new CharacterAttributes(10, 5, 10, 5)) > CharacterRules.SwimStroke(CharacterAttributes.Starting)
            && CharacterRules.SwimStroke(new CharacterAttributes(1, 5, 1, 5)) == CharacterRules.SwimStrokeFloor, "might and vitality make the stroke");

        // In the Engine: a body swimming upstream for six seconds in a 2 m/s river, as the controller steps it.
        const float WaterTop = StoneTop + 3f;
        const float Upstream = -MathF.PI / 2;
        (float Total, float Last) Swum(CharacterAttributes swimmer, float riverSpeed)
        {
            float atFour = 0;
            Vector3 at = new(8.5f, StoneTop + 1.5f, 8.5f);
            CharacterMotion motion = PlayerBody.AtRest(at);
            SwimCurrent current = new();
            const float Step = (float)PlayerConstants.ControllerStepSeconds;
            for (int i = 0; i < (int)(6 / Step); i++)
            {
                current.Step(Step, new Vector2(riverSpeed, 0), CharacterRules.SwimStroke(swimmer));
                CharacterMovementRequest swim = PlayerBody.Swim(new Vector3(at.X, StoneTop, at.Z), new Vector3(at.X, WaterTop, at.Z)) with { Speed = current.Stroke };
                // Heading a quarter turn clockwise swims toward -X, against the river.
                CharacterStepReceipt receipt = engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(
                    session, at, motion, default, ReadOnlyMemory<CharacterObstacle>.Empty, ReadOnlyMemory<CharacterMeshInstance>.Empty, body,
                    new CharacterControllerCommand(swim, new Vector2(0, 1), Upstream, false, false, false,
                        new Vector3(current.Push.X, 0, current.Push.Y), Vector3.Zero, Step, (ulong)i + 1)));
                at = receipt.Transform.Translation;
                motion = receipt.Motion;
                if (i == (int)(4 / Step)) atFour = at.X;
            }

            return (at.X - 8.5f, at.X - atFour);
        }

        var still = Swum(CharacterAttributes.Starting, 0);
        var weak = Swum(CharacterAttributes.Starting, 2);
        var strong = Swum(new CharacterAttributes(10, 5, 10, 5), 2);
        Console.WriteLine($"swimming upstream 6 s (the last 2 s at full force): still water {still.Total:F1} m, a 2 m/s river: starting swimmer {weak.Total:F1} m ({weak.Last:+0.0;-0.0} m in the last 2 s), strong swimmer {strong.Total:F1} m ({strong.Last:+0.0;-0.0} m)");
        Check.That(still.Total < -10, $"in still water the swimmer crosses quickly ({still.Total:F1} m)");
        Check.That(weak.Total > still.Total + 10, $"the grace lets a swimmer make some way before the current takes hold ({weak.Total:F1} m)");
        Check.That(weak.Last > 1, $"once a 2 m/s river bears in full a starting swimmer is swept back however they swim ({weak.Last:+0.0} m in 2 s)");
        Check.That(strong.Last < 0, $"a strong swimmer still makes way against it ({strong.Last:+0.0} m in 2 s)");
    });

    // Creatures keep out of deep water (#9879 follow-up): a river across the floor, too deep to wade but
    // for a ford at its far end, laid over the Engine's collision-derived navigation as its overlay.
    Check.Section("creatures route round deep water, through a ford, and not at all with none", () =>
    {
        const long RiverFrom = 6, RiverTo = 10, FordFrom = 12;
        const double Deep = 1.2, Shallow = 0.5;
        CollisionNavigationConfig defaults = engine.Spatial.DefaultCollisionNavigationConfig();
        CollisionNavigationConfig navigation = defaults with
        {
            GridId = 1, CellSize = TerrainConstants.VoxelSize, ChunkSize = (uint)Edge, MaximumCells = 65_536,
            Character = defaults.Character with { Shape = defaults.Character.Shape with { Radius = 0.3f, StandingHeight = 1.8f } },
        };
        void Publish() => engine.Spatial.ReplaceCollisionNavigation(new CollisionNavigationReplaceRequest(
            session, new Vector3(0, StoneTop - 4, 0), new Vector3(Edge, StoneTop + 8, Edge), navigation));
        ColumnWater? River(long x, long z, bool ford) =>
            x >= RiverFrom && x < RiverTo ? new ColumnWater(StoneTop + (ford && z >= FordFrom ? Shallow : Deep), ford && z >= FordFrom ? Shallow : Deep, false) : null;
        NavigationStepResult Route(Vector3 from, Vector3 to) =>
            engine.Spatial.EvaluateNavigationStep(new NavigationStepRequest(session, from, to, 64f, 4096U));
        Vector3 west = new(2.5f, StoneTop, 2.5f), east = new(13.5f, StoneTop, 2.5f), midstream = new(8.5f, StoneTop, 2.5f);
        bool InRiver(PlanarNavCell cell) => cell.X >= RiverFrom && cell.X < RiverTo;

        Publish();
        NavigationStepResult open = Route(west, east);
        Check.That(open.Outcome == NavigationPathOutcome.Reached && open.Path.ToArray().Any(cell => InRiver(cell) && cell.Z < FordFrom),
            $"with no water laid over it the route goes straight across ({open.Outcome})");

        WaterAvoidance avoidance = new();
        Check.That(avoidance.Apply(engine.Spatial, session, (0, 0), (Edge, Edge), (x, z) => River(x, z, ford: true), 1)
            && avoidance.Closed == (RiverTo - RiverFrom) * FordFrom && avoidance.Fords == (RiverTo - RiverFrom) * (Edge - FordFrom),
            $"the river's deep cells are closed and its ford's cells cost more ({avoidance.Readout()})");
        NavigationStepResult forded = Route(west, east);
        Check.That(forded.Outcome == NavigationPathOutcome.Reached && forded.Path.ToArray().Where(InRiver).All(cell => cell.Z >= FordFrom),
            $"with the river laid over it the route crosses at the ford ({forded.Outcome}, {forded.Path.Length} cells)");
        Check.That(Route(midstream, east).Outcome == NavigationPathOutcome.StartBlocked, "a creature standing in deep water has no route by the map");

        Publish();
        Check.That(avoidance.Apply(engine.Spatial, session, (0, 0), (Edge, Edge), (x, z) => River(x, z, ford: false), 2)
            && Route(west, east).Outcome is not NavigationPathOutcome.Reached, "a river with no ford parts a creature from the far bank");
        Check.That(avoidance.Apply(engine.Spatial, session, (0, 0), (Edge, Edge), (x, z) => River(x, z, ford: false) is ColumnWater frozen ? frozen with { Ice = true } : null, 2)
            && avoidance.Closed == 0 && Route(west, east).Outcome == NavigationPathOutcome.Reached, "a frozen river is ground");
    });

    // A boat on waves (#9902): the Engine's platform carry takes a character along on a level collider, and
    // on one that rises and falls under them, so BoatStore heaves the boat's collider with the waves.
    Check.Section("a character rides a platform: level, and heaving", () =>
    {
        const ulong Raft = 9902;
        const float Speed = 1, Seconds = 4, PlatformY = StoneTop + 2f, HeaveAmplitude = 0.3f, HeavePeriod = 2.5f;
        Vector3 hullMin = new(-2.0f, -0.16f, -1.15f), hullMax = new(2.0f, 0.18f, 1.15f);
        (float Carried, float Moved, int Aboard, int Steps) Ride(bool heave, bool reportVertical)
        {
            const float Step = (float)PlayerConstants.ControllerStepSeconds;
            float startX = 4;
            Vector3 at = new(startX, PlatformY + hullMax.Y + (height / 2f) + 0.02f, 8.5f);
            CharacterMotion motion = PlayerBody.AtRest(at);
            bool aboard = false;
            int on = 0, steps = (int)(Seconds / Step);
            float platformX = startX;
            for (int i = 0; i < steps; i++)
            {
                float t = i * Step, omega = 2 * MathF.PI / HeavePeriod;
                platformX = startX + (Speed * t);
                float y = PlatformY + (heave ? HeaveAmplitude * MathF.Sin(omega * t) : 0);
                float vy = heave && reportVertical ? HeaveAmplitude * omega * MathF.Cos(omega * t) : 0;
                Transform pose = new(new Vector3(platformX, y, 8.5f), Quaternion.Identity, Vector3.One);
                CharacterObstacle hull = new(Raft, pose, hullMin, hullMax, CollisionEnabled: true, new Vector3(Speed, vy, 0), Vector3.Zero);
                CharacterSupport support = aboard ? new CharacterSupport(true, CharacterSupportLifecycle.Active, Raft, pose) : default;
                CharacterStepReceipt receipt = engine.Spatial.ProposeCharacterStep(new CharacterStepRequest(
                    session, at, motion, support, new[] { hull }, ReadOnlyMemory<CharacterMeshInstance>.Empty, body,
                    default(CharacterControllerCommand) with { StepSeconds = Step, Sequence = (ulong)i + 1 }));
                aboard = (receipt.Platform.Present && receipt.Platform.Entity == Raft) || (receipt.Ground.Present && receipt.Ground.SourceEntity == Raft);
                if (aboard) on++;
                at = receipt.Transform.Translation;
                motion = receipt.Motion;
            }

            return (at.X - startX, platformX - startX, on, steps);
        }

        var level = Ride(heave: false, reportVertical: false);
        var heaved = Ride(heave: true, reportVertical: false);
        var told = Ride(heave: true, reportVertical: true);
        string Say((float Carried, float Moved, int Aboard, int Steps) r) => $"carried {r.Carried:F2} of {r.Moved:F2} m, aboard {r.Aboard}/{r.Steps} steps";
        Console.WriteLine($"platform carry: level {Say(level)}; heaving {HeaveAmplitude} m, pose only {Say(heaved)}; heaving with its vertical velocity {Say(told)}");
        Check.That(level.Aboard >= level.Steps - 2 && Math.Abs(level.Carried - level.Moved) < 0.2f, $"a level platform carries the character ({Say(level)})");
        Check.That(told.Aboard >= told.Steps - 2 && Math.Abs(told.Carried - told.Moved) < 0.2f,
            $"one heaving with the waves, and saying how fast, carries them up and down as well ({Say(told)})");
    });
});

return Check.Finish("PlayerBody");
