using System.Numerics;
using System.Text.Json;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// How a long swimmer's body follows its head (#10995; Patch, 2026-11-22: "procedurally animated so their motion
/// direction could impact their animation more ... in that Rain World kinda way"), from its kind's mesh `swim` block:
/// <list type="bullet">
/// <item>a travelling sideways wave as wide as <paramref name="WaveMetres"/> at its fastest (<paramref name="WaveSpeed"/>
/// metres a second) and <paramref name="IdleWaveMetres"/> while it hovers, <paramref name="WaveLengthMetres"/> from crest to
/// crest, quiet over the first <paramref name="HeadStillShare"/> of the body;</item>
/// <item>each point carrying on with the motion it had, losing it at <paramref name="LagHead"/> a second by the head and
/// only <paramref name="LagTail"/> at the tail, so a sharp turn whips through the tail;</item>
/// <item>easing straight behind the head at <paramref name="Straighten"/> a second as it slows;</item>
/// <item>giving way to its clip by <paramref name="StrikeWeight"/> while it threatens or bites, so a strike's arch shows.</item>
/// </list>
/// </summary>
internal sealed record WildlifeSwim(float WaveMetres, float IdleWaveMetres, float WaveSpeed, float WaveLengthMetres, float HeadStillShare,
    float LagHead, float LagTail, float Straighten, float StrikeWeight)
{
    internal static WildlifeSwim? Read(JsonElement mesh)
    {
        if (!mesh.TryGetProperty("swim", out JsonElement swim)) return null;
        WildlifeSwim read = new(swim.GetProperty("waveMetres").GetSingle(), swim.GetProperty("idleWaveMetres").GetSingle(), swim.GetProperty("waveSpeed").GetSingle(),
            swim.GetProperty("waveLengthMetres").GetSingle(), swim.GetProperty("headStillShare").GetSingle(), swim.GetProperty("lagHead").GetSingle(),
            swim.GetProperty("lagTail").GetSingle(), swim.GetProperty("straighten").GetSingle(), swim.GetProperty("strikeWeight").GetSingle());
        if (read.WaveMetres < 0 || read.IdleWaveMetres < 0 || read.WaveSpeed <= 0 || read.WaveLengthMetres <= 0 || read.HeadStillShare is < 0 or >= 1
            || read.LagTail <= 0 || read.LagHead < read.LagTail || read.Straighten < 0 || read.StrikeWeight is < 0 or > 1)
            throw new InvalidDataException("swim: a wave, a lag slowing from head to tail, a straightening and a strike weight from 0 to 1");
        return read;
    }
}

/// <summary>
/// A swimmer's body as a chain of points from its tail tip to its head (#10995), Engine-free: the head point goes where the
/// creature swims, and each point behind carries on with its own motion and is dragged after the one ahead at its rest
/// spacing (a Verlet chain, as Rain World's creatures move), so the body takes the line its head swam and its tail whips
/// through a sharp turn. A sideways wave travels down it as it swims. Points are in one frame (the body's space's local
/// metres), tail first.
/// </summary>
internal sealed class SwimChain
{
    private readonly float[] lengths;
    private readonly Vector3[] points;
    private readonly Vector3[] before;
    private readonly float total;

    /// <summary>Slower than this share of its wave's speed it hovers, and eases straight; the wave keeps a slow drift there too.</summary>
    internal const float HoverShare = 0.15f;

    /// <param name="rest">The chain's points at rest, tail first and the head point last, in any frame: only their spacing is kept.</param>
    internal SwimChain(IReadOnlyList<Vector3> rest)
    {
        ArgumentNullException.ThrowIfNull(rest);
        if (rest.Count < 3) throw new ArgumentException("a chain has a tail, a head and a point between", nameof(rest));
        lengths = new float[rest.Count - 1];
        for (int i = 0; i < lengths.Length; i++) lengths[i] = Vector3.Distance(rest[i], rest[i + 1]);
        total = lengths.Sum();
        points = [.. rest];
        before = [.. rest];
    }

    /// <summary>The chain's points now, tail first, before the wave.</summary>
    internal IReadOnlyList<Vector3> Points => points;

    /// <summary>The body's length along the chain.</summary>
    internal float Length => total;

    /// <summary>Lays the chain out straight behind its head, as it rests.</summary>
    internal void Lay(Vector3 head, Vector3 heading)
    {
        Vector3 back = -Flat(heading);
        points[^1] = head;
        for (int i = points.Length - 2; i >= 0; i--) points[i] = points[i + 1] + (back * lengths[i]);
        Array.Copy(points, before, points.Length);
    }

    /// <summary>
    /// Moves the chain on by an update's seconds: the head to where it now is, each point behind dragged after it, the
    /// tail lagging. Returns the points as drawn, the wave added: wider and faster the faster it swims.
    /// </summary>
    /// <param name="heading">Which way the creature faces, along the ground.</param>
    /// <param name="speed">How fast it swims, metres a second.</param>
    /// <param name="clock">The simulation time, which the wave travels by.</param>
    internal Vector3[] Step(WildlifeSwim swim, Vector3 head, Vector3 heading, float speed, double clock, double seconds)
    {
        ArgumentNullException.ThrowIfNull(swim);
        Vector3 back = -Flat(heading);
        float dt = (float)Math.Max(0, seconds);
        if (dt <= 0) return Waved(swim, speed, clock);
        // Only near a hover does it ease straight: swimming, it follows its head's line.
        float slow = 1f - Math.Clamp(speed / (swim.WaveSpeed * HoverShare), 0f, 1f);
        float straighten = 1f - MathF.Exp(-swim.Straighten * slow * dt);
        before[^1] = points[^1];
        points[^1] = head;
        float along = 0f;
        for (int i = points.Length - 2; i >= 0; i--)
        {
            along += lengths[i];
            float share = along / total;
            // It carries on with the motion it had, losing it faster the nearer the head.
            float lag = swim.LagHead + ((swim.LagTail - swim.LagHead) * share);
            Vector3 carried = points[i] + ((points[i] - before[i]) * MathF.Exp(-lag * dt));
            before[i] = points[i];
            // As it slows it eases straight out behind the head.
            Vector3 eased = Vector3.Lerp(carried, head + (back * along), straighten);
            // It never stretches or bunches: dragged after the point ahead at its spacing.
            points[i] = Toward(points[i + 1], eased, lengths[i], back);
        }

        return Waved(swim, speed, clock);
    }

    /// <summary>The points with the wave across them: nothing at the head, growing to its whole width past the head's still share.</summary>
    private Vector3[] Waved(WildlifeSwim swim, float speed, double clock)
    {
        float fast = Math.Clamp(speed / swim.WaveSpeed, 0f, 1f);
        float width = swim.IdleWaveMetres + ((swim.WaveMetres - swim.IdleWaveMetres) * fast);
        // The wave runs down the body as fast as the body swims, and at a slow drift while it hovers.
        double travel = Math.Max(speed, swim.WaveSpeed * HoverShare) * clock;
        Vector3[] drawn = new Vector3[points.Length];
        float along = 0f;
        drawn[^1] = points[^1];
        for (int i = points.Length - 2; i >= 0; i--)
        {
            along += lengths[i];
            float share = along / total;
            float grow = Math.Clamp((share - swim.HeadStillShare) / (1f - swim.HeadStillShare), 0f, 1f);
            Vector3 tangent = Flat(points[i + 1] - points[i]);
            Vector3 side = tangent == Vector3.Zero ? Vector3.Zero : new Vector3(-tangent.Z, 0f, tangent.X);
            float phase = (float)((travel + along) / swim.WaveLengthMetres * 2 * Math.PI);
            drawn[i] = points[i] + (side * width * grow * MathF.Sin(phase));
        }

        return drawn;
    }

    /// <summary>A point at this distance from <paramref name="from"/>, the way <paramref name="toward"/> lies, or along <paramref name="otherwise"/>.</summary>
    private static Vector3 Toward(Vector3 from, Vector3 toward, float distance, Vector3 otherwise)
    {
        Vector3 way = toward - from;
        return from + ((way.LengthSquared() > 1e-10f ? Vector3.Normalize(way) : otherwise) * distance);
    }

    private static Vector3 Flat(Vector3 v)
    {
        Vector3 flat = new(v.X, 0f, v.Z);
        return flat.LengthSquared() < 1e-10f ? Vector3.UnitZ : Vector3.Normalize(flat);
    }

    /// <summary>The turn that takes one direction to another, the shortest way.</summary>
    internal static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        Vector3 a = Vector3.Normalize(from), b = Vector3.Normalize(to);
        float dot = Math.Clamp(Vector3.Dot(a, b), -1f, 1f);
        if (dot > 0.99999f) return Quaternion.Identity;
        if (dot < -0.99999f)
        {
            Vector3 axis = Vector3.Cross(a, Vector3.UnitY);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(a, Vector3.UnitX);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }

        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(a, b)), MathF.Acos(dot)));
    }
}
