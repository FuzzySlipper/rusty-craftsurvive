using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// The physics world the dead fall in (#10621). It exists only while one lies in it: created with
/// the first, over the terrain's collision, and gone with the last. The Engine binds a changed
/// scene and passes over an unchanged one, so binding every update costs nothing until terrain
/// streams. The world moves with the origin, as Dynamics does not hear of a rebase by itself.
/// </summary>
internal sealed class WildlifeRagdolls : IDisposable
{
    /// <summary>Ragdolls touch the ground and nothing else: not each other, nor their own unlinked bones, whose shapes overlap at rest.</summary>
    private const uint FallGroup = 2;
    private const uint FallMask = ~FallGroup;
    private const float Gravity = -9.81f;
    private const float FullBlend = 1;

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly Func<SpatialSession> session;
    private readonly WildlifeRagdollFeel feel;
    private DynamicsWorld? world;
    private int lying;

    /// <summary>What has been spent since the last readout: updates, and the time binding and stepping, with the worst of each.</summary>
    private (long Updates, double Bind, double WorstBind, double Step, double WorstStep) spent;

    internal WildlifeRagdolls(IEngineContext engine, WorldFrame frame, Func<SpatialSession> session, WildlifeRagdollFeel feel)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.feel = feel;
        frame.Committed += Rebase;
    }

    /// <summary>Lets the body fall from the pose it was last drawn in, moving as it moved.</summary>
    /// <param name="scale">How large this animal is against the mesh.</param>
    /// <param name="blow">The way the killing blow drove it, as a unit along the ground; zero for none.</param>
    internal DynamicsRagdoll Fall(AnimationInstance instance, WildlifeRagdollRig rig, Func<string, uint> joint, float scale, Vector2 blow)
    {
        world ??= Open();
        DynamicsRagdollBone[] bones = [.. rig.Bones.Select(bone => Bone(bone, joint, scale))];
        DynamicsRagdollLink[] links = [.. rig.Links.Select(link => Link(link, scale))];
        DynamicsRagdoll ragdoll = engine.Dynamics.CreateRagdoll(new(world, instance, bones, links, FallGroup, FallMask,
            (float)feel.Friction, (float)feel.Restitution, (float)feel.LinearDamping, (float)feel.AngularDamping, FullBlend));
        lying++;
        if (blow != Vector2.Zero)
        {
            Vector3 at = engine.Dynamics.ReadRagdoll(ragdoll).Bones.Span[rig.HitBone].Translation;
            Vector3 drive = new Vector3(blow.X, (float)feel.BlowLift, blow.Y) * (float)feel.BlowMetresPerSecond * rig.Mass * Cube(scale);
            engine.Dynamics.ApplyRagdollImpulse(new(ragdoll, (uint)rig.HitBone, at, drive));
        }

        return ragdoll;
    }

    /// <summary>A body has gone. The world goes with the last of them, on the next update.</summary>
    internal void Release(DynamicsRagdoll ragdoll)
    {
        ragdoll.Dispose();
        lying--;
    }

    internal void Update(ProductStep time)
    {
        if (world is null) return;
        if (lying == 0)
        {
            Close();
            return;
        }

        if (time.AdmittedSteps == 0) return;
        long began = Stopwatch.GetTimestamp();
        engine.Dynamics.BindWorldCollision(new(world, session()));
        long bound = Stopwatch.GetTimestamp();
        engine.Dynamics.Step(new(world, (float)time.FixedDeltaSeconds, time.AdmittedSteps, ReadOnlyMemory<DynamicsAction>.Empty));
        double binding = Stopwatch.GetElapsedTime(began, bound).TotalMilliseconds, stepping = Stopwatch.GetElapsedTime(bound).TotalMilliseconds;
        spent = (spent.Updates + 1, spent.Bind + binding, Math.Max(spent.WorstBind, binding), spent.Step + stepping, Math.Max(spent.WorstStep, stepping));
    }

    /// <summary>What the world has cost since the last readout (which it resets).</summary>
    internal string Readout()
    {
        double updates = Math.Max(spent.Updates, 1);
        string text = string.Create(CultureInfo.InvariantCulture,
            $"ragdolls={lying} world={world is not null} bindMs={spent.Bind / updates:F3}/{spent.WorstBind:F3} stepMs={spent.Step / updates:F3}/{spent.WorstStep:F3}");
        spent = default;
        return text;
    }

    public void Dispose()
    {
        frame.Committed -= Rebase;
        Close();
    }

    private DynamicsWorld Open()
    {
        long began = Stopwatch.GetTimestamp();
        DynamicsWorld opened = engine.Dynamics.CreateWorld(new DynamicsWorldConfig(new Vector3(0, Gravity, 0)));
        engine.Dynamics.BindWorldCollision(new(opened, session()));
        double binding = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        spent = spent with { WorstBind = Math.Max(spent.WorstBind, binding) };
        return opened;
    }

    private void Close()
    {
        world?.Dispose();
        world = null;
    }

    private void Rebase(WorldOriginCommitReceipt receipt)
    {
        if (world is not null) engine.Dynamics.RebaseWorldOrigin(new(world, session(), receipt));
    }

    private static DynamicsRagdollBone Bone(WildlifeRagdollBone bone, Func<string, uint> joint, float scale)
    {
        float mass = bone.Mass * Cube(scale), radius = bone.Radius * scale;
        return bone.End is { } end
            ? DynamicsRagdollBone.Capsule(joint(bone.Joint), joint(end), radius, mass)
            : DynamicsRagdollBone.CapsuleAlong(joint(bone.Joint), bone.Length * scale, radius, mass);
    }

    private static DynamicsRagdollLink Link(WildlifeRagdollLink link, float scale)
    {
        // Damping is a torque per turn: mass times length squared.
        float damping = link.Damping * Cube(scale) * scale * scale;
        DynamicsJointLimits limits = link.Swing is float swing
            ? DynamicsJointLimits.Cone(swing, link.Least, link.Most, damping)
            : DynamicsJointLimits.Hinge(link.Least, link.Most, damping);
        return new DynamicsRagdollLink((uint)link.Parent, (uint)link.Child, link.Axis, limits);
    }

    private static float Cube(float value) => value * value * value;
}
