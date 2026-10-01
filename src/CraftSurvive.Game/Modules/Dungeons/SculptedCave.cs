using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// Dungeon approach C: approach A's flow and structure, with the rock sculpted smooth by
/// <see cref="SculptedRock"/>. A candidate is accepted only when it is still walkable after
/// sculpting; otherwise the next of A's candidates is tried.
/// </summary>
internal static class SculptedCave
{
    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVerdict Verdict, DungeonVolume Walkable) Generate(ulong seed)
    {
        (DungeonLayout, DungeonPlan, DungeonVerdict, DungeonVolume) last = default;
        for (int attempt = 0; attempt < CarveAndStamp.MaximumAttempts; attempt++)
        {
            (DungeonLayout carved, DungeonPlan plan, DungeonVerdict carvedVerdict) = CarveAndStamp.Candidate(seed, attempt);
            if (!carvedVerdict.Walkable)
            {
                last = (carved, plan, carvedVerdict, carved.Volume);
                continue;
            }

            (RockDensity rock, DungeonVolume building, DungeonVolume walkable) = SculptedRock.Sculpt(carved.Volume, plan.Arrival, seed);
            DungeonVerdict verdict = CarveAndStamp.Check(walkable, plan);
            DungeonLayout layout = carved with { Name = $"sculpted-cave-{seed:x}", Volume = building, Rock = rock };
            last = (layout, plan, verdict with { Reason = verdict.Walkable ? verdict.Reason : $"after sculpting, {verdict.Reason}" }, walkable);
            if (verdict.Walkable)
            {
                return last;
            }
        }

        return last;
    }
}
