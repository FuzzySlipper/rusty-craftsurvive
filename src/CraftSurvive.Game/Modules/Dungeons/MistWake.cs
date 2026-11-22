using System.Numerics;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// Where things moving through a sump's mist shed their eddies (#10989), with no Engine: each mover's last sample, and
/// when it last shed. An update that admitted no simulation time moves nothing on (R10989-2): the dungeon reads its
/// movers before the creatures move, so such an update would see a creature's new place with no time to divide by, and
/// replacing its sample there would hide the movement from the next timed update. Positions are the session's frame.
/// </summary>
internal sealed class MistWake
{
    // A mover sheds while moving at least so fast, at most so often, at its body's middle a little behind it, and the eddy
    // carries on with a share of its speed.
    internal const double ShedEverySeconds = 0.3;
    internal const float MovingMetresPerSecond = 0.35f, BodyMiddleMetres = 0.8f, BehindMetres = 0.4f, CarriedShare = 0.6f;

    private readonly Dictionary<int, (Vector3 At, double Shed)> movers = [];

    /// <summary>The simulation time this wake has seen, in seconds.</summary>
    internal double Now { get; private set; }

    /// <summary>
    /// Moves time on by an update's seconds and gives the eddies due: where each one starts and how fast it carries on.
    /// <paramref name="inMist"/> says whether a point is inside a sump's mist. An update of no time gives none and keeps
    /// every sample as it was.
    /// </summary>
    internal List<(Vector3 At, Vector3 Velocity)> Step(double seconds, IEnumerable<(int Id, Vector3 Feet)> moving, Func<Vector3, bool> inMist)
    {
        List<(Vector3, Vector3)> shed = [];
        if (seconds <= 0) return shed;
        Now += seconds;
        HashSet<int> seen = [];
        foreach ((int id, Vector3 feet) in moving)
        {
            seen.Add(id);
            if (!movers.TryGetValue(id, out (Vector3 At, double Shed) was))
            {
                movers[id] = (feet, double.NegativeInfinity);
                continue;
            }

            Vector3 velocity = (feet - was.At) / (float)seconds;
            movers[id] = (feet, was.Shed);
            Vector3 flat = new(velocity.X, 0f, velocity.Z);
            if (flat.Length() < MovingMetresPerSecond || Now - was.Shed < ShedEverySeconds) continue;
            Vector3 middle = feet + new Vector3(0f, BodyMiddleMetres, 0f) - (Vector3.Normalize(flat) * BehindMetres);
            if (!inMist(middle)) continue;
            movers[id] = (feet, Now);
            shed.Add((middle, flat * CarriedShare));
        }

        foreach (int gone in movers.Keys.Where(id => !seen.Contains(id)).ToArray()) movers.Remove(gone);
        return shed;
    }

    /// <summary>Forgets every mover, as the mist goes.</summary>
    internal void Clear() => movers.Clear();
}
