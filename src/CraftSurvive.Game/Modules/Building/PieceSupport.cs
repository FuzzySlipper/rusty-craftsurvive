using System.Numerics;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>
/// What holds built work up (#9733): a piece or remnant stands while it is connected, through pieces
/// that touch, to one that rests on the ground. Each node is its boxes (world-axis bounds); two
/// nodes touch when any of their boxes meet within <see cref="Contact"/>. Pure: the ground test is
/// the caller's.
/// </summary>
internal static class PieceSupport
{
    /// <summary>How near two boxes may come and still bear on each other (a seam, a rounding of the grid).</summary>
    internal const float Contact = 0.06f;

    /// <summary>Which nodes stand: every node reachable through touching nodes from one that is grounded.</summary>
    internal static bool[] Standing(IReadOnlyList<IReadOnlyList<(Vector3 Centre, Vector3 Half)>> nodes, Func<int, bool> grounded)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(grounded);
        (Vector3 Low, Vector3 High)[] bounds = [.. nodes.Select(Bounds)];
        bool[] standing = new bool[nodes.Count];
        Queue<int> open = new();
        for (int index = 0; index < nodes.Count; index++)
        {
            if (nodes[index].Count > 0 && grounded(index))
            {
                standing[index] = true;
                open.Enqueue(index);
            }
        }

        while (open.Count > 0)
        {
            int at = open.Dequeue();
            for (int other = 0; other < nodes.Count; other++)
            {
                if (standing[other] || !Near(bounds[at], bounds[other]) || !Touch(nodes[at], nodes[other])) continue;
                standing[other] = true;
                open.Enqueue(other);
            }
        }

        return standing;
    }

    /// <summary>Whether any box of one node meets any box of the other within <see cref="Contact"/>.</summary>
    internal static bool Touch(IReadOnlyList<(Vector3 Centre, Vector3 Half)> a, IReadOnlyList<(Vector3 Centre, Vector3 Half)> b)
    {
        foreach ((Vector3 ac, Vector3 ah) in a)
        {
            foreach ((Vector3 bc, Vector3 bh) in b)
            {
                Vector3 gap = Vector3.Abs(ac - bc) - ah - bh;
                if (gap.X <= Contact && gap.Y <= Contact && gap.Z <= Contact) return true;
            }
        }

        return false;
    }

    private static (Vector3 Low, Vector3 High) Bounds(IReadOnlyList<(Vector3 Centre, Vector3 Half)> boxes)
    {
        Vector3 low = new(float.MaxValue), high = new(float.MinValue);
        foreach ((Vector3 centre, Vector3 half) in boxes)
        {
            low = Vector3.Min(low, centre - half);
            high = Vector3.Max(high, centre + half);
        }

        return (low, high);
    }

    private static bool Near((Vector3 Low, Vector3 High) a, (Vector3 Low, Vector3 High) b) =>
        a.Low.X <= b.High.X + Contact && b.Low.X <= a.High.X + Contact
        && a.Low.Y <= b.High.Y + Contact && b.Low.Y <= a.High.Y + Contact
        && a.Low.Z <= b.High.Z + Contact && b.Low.Z <= a.High.Z + Contact;
}
