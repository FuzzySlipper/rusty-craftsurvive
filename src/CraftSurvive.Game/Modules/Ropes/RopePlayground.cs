using System.Globalization;
using System.Numerics;
using Rusty.Engine;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Ropes;

/// <summary>Provisional attachment/reel policy and presentation; all constraint motion belongs to Engine.</summary>
internal sealed class RopePlayground(IEngineContext engine, TerrainWorld terrain) : IDisposable
{
    internal const float CharacterMass = 80f, MaximumReactionImpulse = 120f;
    private const ulong FirstObject = 80_000;
    private const ulong HangingTether = 1, PairTether = 2, PairSupport = 3, ChainId = 4;
    private const uint ChainBeads = 6, SolverSubsteps = 4, SolverIterations = 8;
    private const float Gravity = 24f, HangingMass = 120f, PairFirstMass = 30f, PairSecondMass = 20f;
    private const float HangingLength = 4f, PairLength = 2.5f, PairSupportLength = 3f;
    private const float ChainLinkLength = 1f, ChainRadius = 0.12f, ChainMass = 1f;
    private const float Damping = 0.03f, Friction = 0.6f, AttachmentSlack = 0.3f;
    private const float MinimumSegmentLength = 0.0001f, OppositeDirectionThreshold = -0.9999f;
    private static readonly Vector3 HangingBodyStart = new(4, 8, -4);
    private static readonly Vector3 PairFirstStart = new(-3, 9, 5);
    private static readonly Vector3 PairSecondStart = new(-1, 8, 5);
    private static readonly Vector3 ChainEnd = new(-5, 3.5f, 3);
    private const float ReelSpeed = 0.25f, MinimumLength = 2f, MaximumLength = 10f;
    private const float BodyHalfSize = 0.35f, MarkerSize = 0.22f;
    private static readonly Vector3[] Anchors = [new(0, 11, -2), new(3, 11, 7)];
    private static readonly Vector3 HangingAnchor = new(4, 12, -4);
    private static readonly Vector3 PairAnchor = new(-3, 12, 5);
    private static readonly Vector3 ChainAnchor = new(-5, 8, 3);
    private readonly List<DynamicsBody> bodies = [];
    private readonly List<AppearanceFact> facts = [];
    private DynamicsWorld? world;
    private Appearance? line, marker, objectAppearance;
    private Vector3 originOffset;
    private bool attached, shorten, lengthen, attachPending, releasePending;
    private int station;
    private ulong attachmentId;
    private float initialLength, targetLength;
    private DynamicsAnchorObservation dynamicAnchor;
    private CharacterTetherFact last;
    private DynamicsStepReceipt solver;
    private uint casts;
    private ulong catches, releases, blockedSteps;
    private float maximumSpeed, maximumTangent, maximumReaction, releaseSpeed;
    private string outcome = "ready";
    internal bool Active => terrain.IsCourtyard && !terrain.IsGeneratedLevel;
    internal IReadOnlyList<AppearanceFact> Facts => facts;

    internal void Start()
    {
        world = engine.Dynamics.CreateWorld(new(new Vector3(0, -Gravity, 0)));
        engine.Dynamics.BindWorldCollision(new(world, terrain.Session));
        engine.Dynamics.ConfigureRopes(new(world, SolverSubsteps, SolverIterations));
        line = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Line, false, new Color(1, 0.82f, 0.2f, 1)));
        marker = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Sphere, false, new Color(0.2f, 0.9f, 1, 1)));
        objectAppearance = engine.Graphics.CreatePrimitive(new(PrimitiveGeometry.Cube, false, new Color(0.9f, 0.35f, 0.15f, 1)));
        bodies.Add(CreateBody(HangingBodyStart, HangingMass));
        bodies.Add(CreateBody(PairFirstStart, PairFirstMass));
        bodies.Add(CreateBody(PairSecondStart, PairSecondMass));
        engine.Dynamics.SetFixedTether(new(world, bodies[0], Vector3.Zero, HangingAnchor, new(HangingTether, HangingLength, HangingLength, 0, true)));
        engine.Dynamics.SetFixedTether(new(world, bodies[1], Vector3.Zero, PairAnchor, new(PairSupport, PairSupportLength, PairSupportLength, 0, true)));
        engine.Dynamics.SetBodyTether(new(world, bodies[1], bodies[2], Vector3.Zero, Vector3.Zero, new(PairTether, PairLength, PairLength, 0, true)));
        // Six one-metre links start compressed above the floor and settle into terrain contact.
        engine.Dynamics.CreateFixedChain(new(world, ChainAnchor, ChainEnd, new(ChainId, ChainBeads, ChainLinkLength, ChainRadius, Properties(ChainMass))));
        Publish();
    }

    private DynamicsBody CreateBody(Vector3 position, float mass) => engine.Dynamics.CreateBody(new(world!,
        new DynamicsBodyConfig(new(position, Quaternion.Identity, Vector3.One), new Vector3(BodyHalfSize), Properties(mass))));
    private static DynamicsBodyProperties Properties(float mass) => new(mass,
        new(DynamicsMassPolicyKind.DeriveFromShapeAndMass, default), Vector3.Zero, Vector3.Zero, default,
        Damping, Damping, 1, Friction, 0, uint.MaxValue, uint.MaxValue, true, false, true);

    internal void BeginUpdate()
    {
        // Adopt the Engine's current immutable projection after terrain edits/residency changes.
        if (Active) engine.Dynamics.BindWorldCollision(new(world!, terrain.Session));
    }

    internal void Input(ReadOnlySpan<ProductInputEvent> events)
    {
        foreach (ProductInputEvent input in events)
        {
            if (input.Kind == InputEventKind.Clear) { shorten = lengthen = false; continue; }
            if (input.Kind != InputEventKind.Key) continue;
            bool held = input.Edge is InputEdge.Pressed or InputEdge.Held;
            if (input.Keyboard == KeyboardControl.KeyQ) shorten = held;
            if (input.Keyboard == KeyboardControl.KeyZ) lengthen = held;
            if (input.Edge != InputEdge.Pressed) continue;
            if (input.Keyboard == KeyboardControl.KeyR) Attach();
            if (input.Keyboard == KeyboardControl.KeyT) Release();
            if (input.Keyboard == KeyboardControl.KeyV) Select((station + 1) % 3);
        }
    }

    internal string Select(int selected)
    {
        if (selected is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(selected));
        station = selected;
        releasePending = true;
        return outcome = $"selected station {station}: {(station == 2 ? "dynamic" : "fixed")}";
    }
    internal string Attach() { attachPending = true; return outcome = "attach queued"; }
    internal string Release() { releasePending = true; return outcome = "release queued"; }
    internal void ResetAttachment()
    {
        Select(0);
        attached = attachPending = shorten = lengthen = false;
        last = default;
        Publish();
    }
    internal string SetLength(float length)
    {
        if (!float.IsFinite(length) || length < MinimumLength || length > MaximumLength)
            throw new ArgumentOutOfRangeException(nameof(length));
        targetLength = length;
        return outcome = "target length updated";
    }

    internal CharacterTetherRequest BeforeStep(Vector3 player, float seconds)
    {
        if (!Active) { attached = false; attachPending = false; return default; }
        if (releasePending) { attached = false; releasePending = false; }
        Vector3 anchor = station < 2 ? Anchors[station] + originOffset : engine.Dynamics.Read(new(bodies[0])).Transform.Translation;
        if (attachPending)
        {
            attachPending = false;
            float distance = Vector3.Distance(player, anchor);
            if (distance > MaximumLength) outcome = "anchor out of reach; move within 10m";
            else
            {
                initialLength = Math.Clamp(distance + AttachmentSlack, MinimumLength, MaximumLength);
                targetLength = initialLength;
                attachmentId++;
                attached = true;
                outcome = "attached";
            }
        }
        if (!attached) return default;
        targetLength = Math.Clamp(targetLength + ((lengthen ? 1 : 0) - (shorten ? 1 : 0)) * ReelSpeed * seconds,
            MinimumLength, MaximumLength);
        CharacterTetherRequest request;
        if (station == 2)
        {
            dynamicAnchor = engine.Dynamics.ObserveAnchor(new(world!, bodies[0], Vector3.Zero));
            request = CharacterTetherRequest.AtDynamicAnchor(attachmentId, dynamicAnchor, initialLength);
        }
        else request = CharacterTetherRequest.AtFixedAnchor(attachmentId, anchor, initialLength);
        return request with { TargetLength = targetLength, ReelSpeed = ReelSpeed };
    }

    internal void AfterStep(CharacterStepReceipt character, float seconds)
    {
        last = character.Tether;
        casts = character.CastCount;
        if (last.Caught) catches++;
        float speed = (character.Motion.ControlledVelocity + character.Motion.ExternalVelocity).Length();
        if (last.Released) { releases++; releaseSpeed = speed; }
        if (character.BlockFlags != CharacterBlockFlags.None) blockedSteps++;
        maximumTangent = Math.Max(maximumTangent, last.TangentialVelocity.Length());
        maximumReaction = Math.Max(maximumReaction, last.Reaction.Impulse.Length());
        if (last.Invalidated) { attached = false; outcome = "anchor invalidated"; }
        maximumSpeed = Math.Max(maximumSpeed, speed);
        if (!Active) { Publish(); return; }
        ReadOnlyMemory<DynamicsAnchorReaction> reactions = last.Reaction.Present ? new[] { last.Reaction } : ReadOnlyMemory<DynamicsAnchorReaction>.Empty;
        solver = engine.Dynamics.StepWithReactions(new(world!, seconds, 1, ReadOnlyMemory<DynamicsAction>.Empty, reactions));
        Publish();
    }

    internal void Rebase(SpatialSession session, WorldOriginCommitReceipt receipt, Vector3 translation)
    {
        DynamicsWorldReadout state = engine.Dynamics.ReadWorld(new(world!));
        engine.Dynamics.RebaseWorldOrigin(new(world!, session, receipt, state.EntityRevision, state.Generation));
        originOffset += translation;
        last = last with { CharacterPoint = last.CharacterPoint + translation, AnchorPoint = last.AnchorPoint + translation };
        Publish();
    }

    private void Publish()
    {
        facts.Clear();
        if (!Active) return;
        ulong id = FirstObject;
        foreach (Vector3 anchor in Anchors) Mark(ref id, anchor + originOffset);
        Mark(ref id, HangingAnchor + originOffset);
        Mark(ref id, PairAnchor + originOffset);
        Mark(ref id, ChainAnchor + originOffset);
        foreach (DynamicsBody body in bodies)
        {
            Transform pose = engine.Dynamics.Read(new(body)).Transform;
            facts.Add(new(id++, false, 0, pose with { Scale = new Vector3(BodyHalfSize * 2) }, objectAppearance!, true, RenderLayer.Scene));
        }
        foreach (ulong rope in new[] { HangingTether, PairTether, PairSupport })
        {
            DynamicsTetherReadout tether = engine.Dynamics.ReadTether(new(world!, rope));
            Segment(ref id, tether.First, tether.Second);
        }
        DynamicsChainReadout chain = engine.Dynamics.ReadChain(new(world!, ChainId));
        for (uint point = 1; point < chain.PointCount; point++)
        {
            Vector3 a = engine.Dynamics.ReadChainPoint(new(world!, ChainId, point - 1)).Position;
            Vector3 b = engine.Dynamics.ReadChainPoint(new(world!, ChainId, point)).Position;
            Segment(ref id, a, b);
        }
        if (last.Attached) Segment(ref id, last.CharacterPoint, last.AnchorPoint);
    }
    private void Mark(ref ulong id, Vector3 point) => facts.Add(new(id++, false, 0,
        new(point, Quaternion.Identity, new Vector3(MarkerSize)), marker!, true, RenderLayer.Scene));
    private void Segment(ref ulong id, Vector3 a, Vector3 b)
    {
        Vector3 delta = b - a;
        float distance = delta.Length();
        if (distance < MinimumSegmentLength) return;
        Vector3 direction = delta / distance;
        Quaternion rotation = direction.Y < OppositeDirectionThreshold ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI)
            : Quaternion.Normalize(new Quaternion(Vector3.Cross(Vector3.UnitY, direction), 1 + direction.Y));
        facts.Add(new(id++, false, 0, new(a, rotation, new Vector3(1, distance, 1)), line!, true, RenderLayer.Scene));
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"active={Active};station={station};anchor={(station == 2 ? "dynamic-orange" : $"fixed-{station}")};state={(last.Invalidated ? "invalidated" : last.Released ? "released" : last.Attached ? last.Taut ? "taut" : "slack" : "detached")};caught={last.Caught};catches={catches};releases={releases};distance={last.Distance:F3};maximum={last.MaximumLength:F3};target={targetLength:F3};radial={last.RadialVelocity:F3};tangent={last.TangentialVelocity.Length():F3};correction={last.Correction.Length():F3};reaction={last.Reaction.Impulse.Length():F3};saturated={last.Saturated};unresolved={last.Unresolved};casts={casts};links={solver.RopeLinkCount};solverWork={solver.RopeSolverLinkSteps};substeps={solver.RopeSubsteps};iterations={solver.RopeIterations};maxSpeed={maximumSpeed:F3};maxTangent={maximumTangent:F3};maxReaction={maximumReaction:F3};releaseSpeed={releaseSpeed:F3};blockedSteps={blockedSteps};hangingTension={engine.Dynamics.ReadTether(new(world!, HangingTether)).ForceProxy:F2};chainTension={engine.Dynamics.ReadChain(new(world!, ChainId)).ForceProxy:F2};chainEndY={engine.Dynamics.ReadChainPoint(new(world!, ChainId, ChainBeads)).Position.Y:F3};outcome={outcome}");

    public void Dispose()
    {
        foreach (DynamicsBody body in bodies) body.Dispose();
        bodies.Clear();
        world?.Dispose();
        line?.Dispose(); marker?.Dispose(); objectAppearance?.Dispose();
    }
}
