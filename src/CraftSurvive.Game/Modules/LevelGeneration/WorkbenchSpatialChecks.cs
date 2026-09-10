using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Procgen.Workbench;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.LevelGeneration;

internal sealed record WorkbenchProbeResult(string Id, string Kind, WorkbenchPoint From, WorkbenchPoint To,
    string Expected, string Observed, bool? Passed);

internal static class WorkbenchSpatialChecks
{
    internal const string Coverage = "Actual Engine mesh queries: legacy nine-ray checks plus bidirectional standing-capsule casts (height 1.75, radius 0.30), three lanes per route, five lanes across each gate and selected forbidden room pairs. Endpoints must be clear. Sampled horizontal clearance is not exhaustive navigation or a controller trajectory. Jump, climb, crouch-only routes, destruction and movable-prop bypasses are not covered. Model replay does not change the world.";

    internal static WorkbenchProbeResult[] Run(IEngineContext engine, SpatialSession session, WorkbenchCandidate candidate, bool switchOpen)
    {
        WorldOriginReadout origin = engine.WorldOrigin.Read(new(session));
        return WorkbenchProbePlan.Create(candidate, switchOpen, PlayerConstants.StandingHeight, PlayerConstants.CapsuleRadius)
            .Select(Check).ToArray();

        SpatialCapsuleQueryRequest Request(WorkbenchPoint from, Vector3 delta, float skin) => new(session,
            PlayerWorldPosition.FromWorld(WorkbenchRecipe.Point(from)).ToLocal(origin),
            PlayerConstants.StandingHeight / 2 - PlayerConstants.CapsuleRadius, PlayerConstants.CapsuleRadius,
            delta, skin, new SpatialQueryFilter(uint.MaxValue, uint.MaxValue),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty);

        WorkbenchProbeResult Check(WorkbenchProbe probe)
        {
            string expected = probe.ExpectedBlocked ? "blocked" : "clear";
            try
            {
                if (engine.Spatial.OverlapCapsule(Request(probe.From, Vector3.Zero, 0)).Present
                    || engine.Spatial.OverlapCapsule(Request(probe.To, Vector3.Zero, 0)).Present)
                    return new(probe.Id, probe.Kind, probe.From, probe.To, expected, "endpoint overlaps geometry", null);
                SpatialHit hit = engine.Spatial.CastCapsule(Request(probe.From,
                    WorkbenchRecipe.Point(probe.To) - WorkbenchRecipe.Point(probe.From), PlayerConstants.ContactSkin));
                if (hit.Present && (hit.StartSolid || !hit.Converged))
                    return new(probe.Id, probe.Kind, probe.From, probe.To, expected, hit.StartSolid ? "start contact" : "cast did not converge", null);
                return new(probe.Id, probe.Kind, probe.From, probe.To, expected,
                    hit.Present ? "blocked" : "clear", hit.Present == probe.ExpectedBlocked);
            }
            catch (Exception failure)
            {
                return new(probe.Id, probe.Kind, probe.From, probe.To, expected, "unavailable: " + failure.Message, null);
            }
        }
    }
}
