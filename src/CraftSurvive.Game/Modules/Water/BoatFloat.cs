using System.Numerics;
using CraftSurvive.Game.Modules.Travel;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// What a boat floats on at a point: the water's top and depth there, its current (m/s), and how far the
/// drawn waves stand over that top now (#9902; depth is measured from the rest level, not the waves).
/// </summary>
internal readonly record struct FloatWater(double Top, double Depth, double CurrentX, double CurrentZ, bool Ice, double Lift = 0)
{
    /// <summary>The surface the hull rides: the rest level and the waves on it.</summary>
    internal double Surface => Top + Lift;
}

/// <summary>
/// A boat afloat in first person (#9879), kinematic: it stands at the water owner's surface (lower by a
/// little with someone aboard), eases toward the current's velocity with the water's drag, and the
/// paddler drives it along its bow and turns it. It will not go where the water is shallower than its
/// draft, nor onto ice; moored, it stays put. On waves (#9902) it rides the surface under its ends and
/// sides: heaving with their mean, pitching bow to stern and rolling side to side, so waves shorter than
/// the hull pass under it. Kinematic height and tilt only - buoyancy, settling under a load and tipping
/// are the Engine's Dynamics (rusty-engine #9883). Engine-free.
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

    /// <summary>How quickly the hull leans to the water under it (seconds): a heavy raft slowly, a canoe quicker, neither at once.</summary>
    private const double RaftLeanSeconds = 0.6, CanoeLeanSeconds = 0.3;

    /// <summary>The fastest the waterline under it is taken to rise or fall (m/s): the drawn waves' own stay well within it.</summary>
    private const double MaximumHeave = 1.5;

    /// <summary>Where each hull meets the water: its length and beam (m), the models' waterline bounds.</summary>
    private const double RaftLength = 4.0, RaftBeam = 2.3, CanoeLength = 4.6, CanoeBeam = 0.78;

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

    /// <summary>The bow's rise over the stern (radians; positive bow up), from the waves under its ends.</summary>
    internal double Pitch { get; private set; }

    /// <summary>The roll (radians about the bow line; positive lifts the side to the bow's left, the model's +Z).</summary>
    internal double Roll { get; private set; }

    /// <summary>How fast the waterline under it rose on the last step (m/s), so what stands aboard is carried up and down.</summary>
    internal double Heave { get; private set; }

    /// <summary>Tied up: it holds its place whatever the current.</summary>
    internal bool Moored { get; set; } = true;

    /// <summary>Aground or against ice on the last step: stopped, or sliding along the edge.</summary>
    internal bool Grounded { get; private set; }

    private double Draft => Spec.Kind == BoatKind.Raft ? RaftDraft : CanoeDraft;

    private double DragSeconds => Spec.Kind == BoatKind.Raft ? RaftDragSeconds : CanoeDragSeconds;

    private double MaximumTurn => Spec.Kind == BoatKind.Raft ? RaftTurnRate : CanoeTurnRate;

    private double Length => Spec.Kind == BoatKind.Raft ? RaftLength : CanoeLength;

    private double LeanSeconds => Spec.Kind == BoatKind.Raft ? RaftLeanSeconds : CanoeLeanSeconds;

    private double Beam => Spec.Kind == BoatKind.Raft ? RaftBeam : CanoeBeam;

    /// <summary>The side to the bow's left on the ground plane (the model's +Z turned by the heading).</summary>
    private Vector2 Side => new((float)Math.Sin(Heading), (float)Math.Cos(Heading));

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
        if (Moored || seconds <= 0)
        {
            Velocity = Vector2.Zero;
            TurnRate = 0;
            Grounded = !Floats(here);
            Ride(seconds, water, here, laden);
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
            Ride(seconds, water, there, laden);
            return;
        }

        Velocity = Vector2.Zero;
        Ride(seconds, water, here, laden);
    }

    /// <summary>Stands the hull on the surface under its bow, stern and sides: their mean is its waterline, their differences its pitch and roll.</summary>
    private void Ride(double seconds, Func<double, double, FloatWater> water, FloatWater middle, bool laden)
    {
        Vector2 bow = Bow * (float)(Length / 2), side = Side * (float)(Beam / 2);
        double Surface(Vector2 offset)
        {
            FloatWater at = water(X + offset.X, Z + offset.Y);
            // Off the water's edge the hull end rests on the water under its middle.
            return at.Depth > 0 && !at.Ice ? at.Surface : middle.Surface;
        }

        double fore = Surface(bow), aft = Surface(-bow), left = Surface(side), right = Surface(-side);
        double waterline = (fore + aft + left + right) / 4;
        // The heave is the waves' alone: settling under a load as someone boards is not the water moving.
        Heave = seconds > 0 && double.IsFinite(rode) ? Math.Clamp((waterline - rode) / seconds, -MaximumHeave, MaximumHeave) : 0;
        rode = waterline;
        Y = waterline - (laden ? LadenSink : 0);
        // It leans toward the water's slope under it with a little inertia, rather than snapping to each wave.
        double lean = seconds > 0 ? 1 - Math.Exp(-seconds / LeanSeconds) : 1;
        Pitch += (Math.Atan2(fore - aft, Length) - Pitch) * lean;
        Roll += (Math.Atan2(left - right, Beam) - Roll) * lean;
    }

    /// <summary>The waterline under the hull on the last step, before any load set it lower.</summary>
    private double rode = double.NaN;

    /// <summary>The hull's attitude: turned to its heading, pitched and rolled on the waves.</summary>
    internal Quaternion Attitude =>
        Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)Heading)
        * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)Pitch)
        * Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)-Roll);

    /// <summary>Puts the boat at a place, at rest (the map moved it, or a load put it there).</summary>
    internal void Place(double x, double z, double heading)
    {
        (X, Z, Heading) = (x, z, Normalise(heading));
        Velocity = Vector2.Zero;
        TurnRate = 0;
        rode = double.NaN;
    }

    internal static double Normalise(double angle) => Math.IEEERemainder(angle, 2 * Math.PI);
}
