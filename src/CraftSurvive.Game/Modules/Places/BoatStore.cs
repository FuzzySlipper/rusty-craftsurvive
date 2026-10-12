using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>
/// Owns the expedition's boat in the running product (#9879): restores and saves it under its own key,
/// draws it where it lies - afloat at the water owner's surface, or drawn up on the bank - and in first
/// person is the carrier the player boards: a kinematic <see cref="BoatFloat"/> passed to the character
/// controller as a borrowed collider each step, so the Engine's platform carry takes the player along.
/// Seated, the movement keys paddle and steer; standing, the player walks on it or steps off, and a boat
/// left alone is moored where it is.
/// </summary>
internal sealed class BoatStore : IPlayerCarrier, IDisposable
{
    /// <summary>Wood: matt.</summary>
    private const float Roughness = 0.9f;

    /// <summary>The player boards from within this distance of its middle (metres, on the ground plane).</summary>
    private const float BoardMetres = 4;

    /// <summary>The collider is stepped, and the boat drawn, within these distances of the player.</summary>
    private const float CarryMetres = 64, DrawMetres = 256;

    /// <summary>Boarding stands the player this far over the deck, so the controller settles them onto it.</summary>
    private const float StandClearance = 1.1f;

    /// <summary>Each hull as the controller collides with it: a box about its waterline origin (the models' bounds, decks flat).</summary>
    private static readonly (Vector3 Min, Vector3 Max) RaftHull = (new(-2.0f, -0.16f, -1.15f), new(2.0f, 0.18f, 1.15f));
    /// <summary>The canoe rides with its floor at the waterline (scripts/generate-boats.py): the player stands on its floor, inside its rims.</summary>
    private static readonly (Vector3 Min, Vector3 Max) CanoeHull = (new(-2.3f, -0.05f, -0.39f), new(2.3f, 0.1f, 0.39f));

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly WorldFrame frame;
    private readonly WaterModel water;
    private readonly ProductSaveSlot<BoatSave> slot;
    private readonly PropMaterials materials;
    private readonly Dictionary<BoatKind, (MeshResource Mesh, Appearance Look)> looks = [];
    private int steps, groundings;

    /// <summary>A paddle held by an assisted capture, and how many more controller steps it holds for.</summary>
    private Vector2 assistedPaddle;
    private int assistedSteps;

    /// <summary>Assisted, captures only: holds a paddle for so many seconds, as the movement keys would while seated.</summary>
    internal void HoldPaddle(Vector2 paddle, double seconds, double stepSeconds)
    {
        assistedPaddle = paddle;
        assistedSteps = (int)Math.Ceiling(seconds / stepSeconds);
    }

    internal BoatStore(IEngineContext engine, ProductContent content, ProductStore store, SaveIdentity identity, WorldFrame frame, WaterModel water)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.water = water ?? throw new ArgumentNullException(nameof(water));
        slot = new ProductSaveSlot<BoatSave>(engine, store, SaveManifest.TravelBoat, new BoatCodec(identity));
        materials = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, 0, 0));
    }

    /// <summary>The expedition's boat, or null when it has none.</summary>
    internal Boat? Boat { get; private set; }

    /// <summary>The boat afloat in first person, or null when there is none.</summary>
    internal BoatFloat? Float { get; private set; }

    public ulong Entity => ProductIds.BoatCarrier;

    public bool Seated { get; private set; }

    internal void Start()
    {
        if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: BoatSave saved })
            Put(Boats.For(saved.Kind), new((float)saved.X, (float)saved.Z), saved.Heading);
    }

    /// <summary>A new boat lies at a place, its bow along a heading; it replaces any other.</summary>
    internal void Put(BoatSpec spec, Vector2 at, double heading)
    {
        Boat = new Boat(spec, at);
        Float = new BoatFloat(spec, at.X, at.Y, heading);
        Seated = false;
    }

    /// <summary>The boat moves with the party on the map: it lies where the party left it, moored.</summary>
    internal void MoveTo(Vector2 at, double heading)
    {
        if (Boat is null || Float is null) return;
        Boat.MoveTo(at);
        Float.Place(at.X, at.Y, heading);
        Float.Moored = true;
        Seated = false;
    }

    internal void Save()
    {
        if (Boat is null || Float is null) return;
        slot.Save(new BoatSave(Boat.Spec.Kind, Float.X, Float.Z, BoatFloat.Normalise(Float.Heading)));
    }

    /// <summary>The water under a point as the float reads it.</summary>
    internal FloatWater WaterAt(double x, double z)
    {
        WaterSample sample = water.Sample(x, z);
        return new(sample.Top, sample.Depth, sample.CurrentX, sample.CurrentZ, sample.Ice);
    }

    public bool TryToggle(Vector3 playerLocal, out Vector3? boardAt)
    {
        boardAt = null;
        if (Float is null) return false;
        if (Seated)
        {
            Seated = false;
            return true;
        }

        Vector3 player = frame.ToWorld(playerLocal);
        if (Vector2.Distance(new(player.X, player.Z), new((float)Float.X, (float)Float.Z)) > BoardMetres) return false;
        FloatWater here = WaterAt(Float.X, Float.Z);
        if (!Float.Floats(here)) return false;
        Seated = true;
        Float.Moored = false;
        float deck = Hull(Float.Spec.Kind).Max.Y;
        boardAt = new Vector3((float)Float.X, (float)(here.Top + deck + StandClearance), (float)Float.Z);
        return true;
    }

    /// <summary>Seats the player aboard where the boat lies (the map left the party afloat): the world point to stand them at.</summary>
    internal Vector3? Seat()
    {
        if (Float is null) return null;
        FloatWater here = WaterAt(Float.X, Float.Z);
        Seated = true;
        Float.Moored = false;
        return new Vector3((float)Float.X, (float)(here.Top + Hull(Float.Spec.Kind).Max.Y + StandClearance), (float)Float.Z);
    }

    public CharacterObstacle? Step(float seconds, Vector2 paddle, Vector3 playerLocal, bool aboard)
    {
        if (Float is null || Boat is null) return null;
        Vector3 player = frame.ToWorld(playerLocal);
        if (Vector2.Distance(new(player.X, player.Z), new((float)Float.X, (float)Float.Z)) > CarryMetres)
        {
            Seated = false;
            if (!Float.Moored) Moor();
            return null;
        }

        // Left alone - nobody seated or standing on it - it is tied up where it is.
        if (!Seated && !aboard && !Float.Moored) Moor();
        if (assistedSteps > 0)
        {
            assistedSteps--;
            paddle = assistedPaddle;
        }

        Float.Step(seconds, WaterAt, Seated ? paddle : Vector2.Zero, laden: Seated || aboard);
        steps++;
        if (Float.Grounded && !Float.Moored) groundings++;
        Boat.MoveTo(new((float)Float.X, (float)Float.Z));
        (Vector3 min, Vector3 max) = Hull(Float.Spec.Kind);
        Transform pose = new(frame.ToLocal(Float.X, Float.Y, Float.Z), Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Float.Heading), Vector3.One);
        return new CharacterObstacle(Entity, pose, min, max, CollisionEnabled: true,
            new Vector3(Float.Velocity.X, 0, Float.Velocity.Y), new Vector3(0, (float)-Float.TurnRate, 0));
    }

    private void Moor()
    {
        if (Float is null) return;
        Float.Moored = true;
        Save();
    }

    private static (Vector3 Min, Vector3 Max) Hull(BoatKind kind) => kind == BoatKind.Raft ? RaftHull : CanoeHull;

    /// <summary>The boat in the first-person snapshot, near enough to be seen: afloat on the water, or on the ground where none is.</summary>
    internal AppearanceFact[] Facts(Vector2 player, Func<double, double, float> ground)
    {
        if (Boat is null || Float is null || Vector2.Distance(player, new((float)Float.X, (float)Float.Z)) > DrawMetres) return [];
        FloatWater here = WaterAt(Float.X, Float.Z);
        double y = here.Depth > 0 ? (double.IsNaN(Float.Y) ? here.Top : Float.Y) : ground(Float.X, Float.Z) - Hull(Boat.Spec.Kind).Min.Y;
        return [new(ProductIds.BoatObject, false, 0,
            new Transform(frame.ToLocal(Float.X, y, Float.Z), Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Float.Heading), Vector3.One),
            Look(Boat.Spec), Visible: true, RenderLayer.Scene)];
    }

    private Appearance Look(BoatSpec spec)
    {
        if (looks.TryGetValue(spec.Kind, out (MeshResource Mesh, Appearance Look) made)) return made.Look;
        MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, spec.ModelPath,
            (role, texture) => materials.For(role, texture ?? throw new InvalidOperationException($"CraftSurvive {spec.Name} part '{role}' names no texture."))));
        Appearance look = engine.Graphics.CreateMeshAppearance(mesh);
        looks[spec.Kind] = (mesh, look);
        return look;
    }

    internal string Readout => Boat is null || Float is null
        ? $"boat=none restore={slot.RestoreOutcome}"
        : string.Create(CultureInfo.InvariantCulture,
            $"boat={Boat.Spec.Name} at={Float.X:F1},{Float.Z:F1} y={Float.Y:F2} heading={Float.Heading:F2} velocity={Float.Velocity.X:F2},{Float.Velocity.Y:F2} turn={Float.TurnRate:F2} moored={Float.Moored} seated={Seated} grounded={Float.Grounded} water=[{Describe(WaterAt(Float.X, Float.Z))}] steps={steps} groundings={groundings} restore={slot.RestoreOutcome} saves={slot.Saves}");

    private static string Describe(FloatWater w) => string.Create(CultureInfo.InvariantCulture,
        $"top={w.Top:F2} depth={w.Depth:F2} current={w.CurrentX:F2},{w.CurrentZ:F2} ice={w.Ice}");

    public void Dispose()
    {
        foreach ((MeshResource mesh, Appearance look) in looks.Values)
        {
            look.Dispose();
            mesh.Dispose();
        }

        looks.Clear();
        materials.Dispose();
    }
}
