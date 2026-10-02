using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// What happens when the player's body is found deep inside collision - put there by a teleport, a
/// block placed into them, or a space changing around them. The character controller pushes a body
/// out of shallow overlaps a little each step; deeper than it allows, the Engine refuses the step
/// with <see cref="PenetrationCode"/>. The game does not stop for that: the player is stood at the
/// first nearby place their body fits - where they last stood clear, or straight above - and play
/// carries on.
/// </summary>
internal static class PlayerRecovery
{
    /// <summary>The Engine's diagnostic for a step refused because the body is too deep in collision.</summary>
    internal const string PenetrationCode = "unresolved-character-controller-penetration";

    /// <summary>How far apart the places tried straight above are.</summary>
    internal const float SearchStepMetres = 0.25f;

    /// <summary>How far above the player, or above where they last stood clear, places are tried.</summary>
    internal const float SearchHeightMetres = 3f;

    /// <summary>Whether a refusal is the controller giving up on a body too deep in collision.</summary>
    internal static bool IsPenetration(EngineCallException refusal)
    {
        foreach (EngineDiagnostic diagnostic in refusal.Diagnostics.Span)
        {
            if (diagnostic.Code == PenetrationCode)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The places tried, in order, as capsule centres: where the player last stood clear, then
    /// straight above where they are, then straight above where they last stood clear.
    /// </summary>
    internal static IEnumerable<Vector3> Candidates(Vector3 current, Vector3 lastClear)
    {
        yield return lastClear;
        int steps = (int)MathF.Round(SearchHeightMetres / SearchStepMetres);
        for (int step = 1; step <= steps; step++)
        {
            yield return current + (Vector3.UnitY * (step * SearchStepMetres));
        }

        for (int step = 1; step <= steps; step++)
        {
            yield return lastClear + (Vector3.UnitY * (step * SearchStepMetres));
        }
    }

    /// <summary>The first candidate where a capsule of the body's size overlaps nothing, if any.</summary>
    internal static Vector3? FirstClear(ISpatialService spatial, SpatialSession session, IEnumerable<Vector3> candidates,
        float height, float radius, float contactSkin)
    {
        double halfSegment = Math.Max(0d, (height / 2d) - radius);
        foreach (Vector3 centre in candidates)
        {
            SpatialHit hit = spatial.OverlapCapsule(new SpatialCapsuleQueryRequest(
                session, centre, halfSegment, radius, Vector3.Zero, contactSkin,
                new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
                ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty));
            if (!hit.Present)
            {
                return centre;
            }
        }

        return null;
    }
}
