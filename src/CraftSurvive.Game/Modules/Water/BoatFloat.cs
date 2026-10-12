using System.Numerics;
using CraftSurvive.Game.Modules.Travel;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>What a boat floats on at a point: the water's top and depth there, and its current (m/s).</summary>
internal readonly record struct FloatWater(double Top, double Depth, double CurrentX, double CurrentZ, bool Ice);

/// <summary>
/// A boat afloat in first person (#9879), kinematic: it stands at the water owner's surface (lower by a
/// little with someone aboard), eases toward the current's velocity with the water's drag, and the
/// paddler drives it along its bow and turns it. It will not go where the water is shallower than its
/// draft, nor onto ice; moored, it stays put. No buoyancy, tilt or tipping: that is the Engine's Dynamics
/// request (rusty-engine #9883) if a kinematic float reads wrong. Engine-free.
/// </summary>
internal sealed class BoatFloat
{
    /// <summary>How quickly the boat takes up the water's velocity (seconds): a heavy raft slowly, a canoe quickly.</summary>
    private const double RaftDragSeconds = 3, CanoeDragSeconds = 1.5;

    /// <summary>How fast the paddler turns it (radians a second).</summary>
    private const double RaftTurnRate = 0.35, CanoeTurnRate = 0.9;

    /// <summary>The water must be this much deeper than the hull reaches for it to float on.</summary>
    private const double KeelClearance = 0.1;

    /// <summary>How deep each hull reaches below the waterline (m), and how much lower someone aboard sets it.</summary>
    private const double RaftDraft = 0.16, CanoeDraft = 0.2, LadenSink = 0.04;

    internal BoatFloat(BoatSpec spec, double x, double z, double heading)
    {
        Spec = spec;
        X = x;
        Z = z;
        Heading = heading;
    }

    internal BoatSpec Spec { get; }

    internal double X { get; private set; }

    internal double Z { get; private set; }

    /// <summary>The waterline's height under the boat: where its origin stands.</summary>
    internal double Y { get; private set; } = double.NaN;

    /// <summary>The bow's bearing (radians about Y; zero along +X, as the model's length).</summary>
    internal double Heading { get; private set; }

    internal Vector2 Velocity { get; private set; }

    internal double TurnRate { get; private set; }

    /// <summary>Tied up: it holds its place whatever the current.</summary>
    internal bool Moored { get; set; } = true;

    /// <summary>Aground or against ice on the last step: stopped, or sliding along the edge.</summary>
    internal bool Grounded { get; private set; }

    private double Draft => Spec.Kind == BoatKind.Raft ? RaftDraft : CanoeDraft;

    private double DragSeconds => Spec.Kind == BoatKind.Raft ? RaftDragSeconds : CanoeDragSeconds;

    private double MaximumTurn => Spec.Kind == BoatKind.Raft ? RaftTurnRate : CanoeTurnRate;

    /// <summary>The bow's direction on the ground plane.</summary>
    internal Vector2 Bow => new((float)Math.Cos(Heading), (float)-Math.Sin(Heading));

    /// <summary>Whether water this deep floats it.</summary>
    internal bool Floats(FloatWater water) => !water.Ice && water.Depth >= Draft + KeelClearance;

    /// <summary>
    /// One step: the water under it now, the paddle (X turns, Y drives along the bow; zero for none) and
    /// whether someone is aboard. A step that would put the hull on the bed or the ice holds it where it is.
    /// </summary>
    internal void Step(double seconds, Func<double, double, FloatWater> water, Vector2 paddle, bool laden)
    {
        FloatWater here = water(X, Z);
        Y = here.Top - (laden ? LadenSink : 0);
        if (Moored || seconds <= 0)
        {
            Velocity = Vector2.Zero;
            TurnRate = 0;
            Grounded = !Floats(here);
            return;
        }

        // The water's drag eases the boat toward the current; the paddle drives it along the bow.
        double ease = Math.Min(1, seconds / DragSeconds);
        Vector2 current = here.Ice ? Vector2.Zero : new((float)here.CurrentX, (float)here.CurrentZ);
        Vector2 thrust = Bow * (float)(Math.Clamp(paddle.Y, -1, 1) * Spec.Paddle);
        Vector2 velocity = Velocity + ((current + thrust - Velocity) * (float)ease);
        TurnRate = Math.Clamp(paddle.X, -1, 1) * MaximumTurn;
        Heading = Normalise(Heading - (TurnRate * seconds));
        // Against the bank or the ice it slides along whichever way still floats it, else it stops there.
        Vector2[] tries = [velocity, new(velocity.X, 0), new(0, velocity.Y)];
        Grounded = true;
        foreach (Vector2 attempt in tries)
        {
            double nextX = X + (attempt.X * seconds), nextZ = Z + (attempt.Y * seconds);
            FloatWater there = water(nextX, nextZ);
            if (!Floats(there)) continue;
            Grounded = attempt != velocity;
            Velocity = attempt;
            (X, Z) = (nextX, nextZ);
            Y = there.Top - (laden ? LadenSink : 0);
            return;
        }

        Velocity = Vector2.Zero;
    }

    /// <summary>Puts the boat at a place, at rest (the map moved it, or a load put it there).</summary>
    internal void Place(double x, double z, double heading)
    {
        (X, Z, Heading) = (x, z, Normalise(heading));
        Velocity = Vector2.Zero;
        TurnRate = 0;
    }

    internal static double Normalise(double angle) => Math.IEEERemainder(angle, 2 * Math.PI);
}
