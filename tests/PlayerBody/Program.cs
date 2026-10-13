using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Player;
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
});

return Check.Finish("PlayerBody");
