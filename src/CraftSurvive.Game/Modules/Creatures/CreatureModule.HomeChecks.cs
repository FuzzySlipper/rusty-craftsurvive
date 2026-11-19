using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// Whether each cave home the wildlife derived could start a route (#10924 review R10924-1). A home is read once the
/// navigation published around the player covers it, and the reading is kept: <c>ok</c>, or <c>UNROUTABLE</c> with how
/// its column reads. Until then it is pending, with why (outside the published box, or its floor not loaded), so an
/// actually bad floor is never confused with one simply out of reach of the navigation.
/// </summary>
internal sealed partial class CreatureModule
{
    /// <summary>Each cave home's routing reading by home key, and whether it is final.</summary>
    private readonly Dictionary<string, (bool Final, string Reading)> homeRouting = [];

    /// <summary>How many cave homes have read UNROUTABLE since the creatures started.</summary>
    private int unroutableHomes;

    /// <summary>The cave homes are looked at again this many update steps apart.</summary>
    private const long HomeCheckEverySteps = 30;

    /// <summary>A home is read only this far inside the box published about the player, so a republish does not lose it at once.</summary>
    private const float HomeReadMarginMetres = 4f;

    /// <summary>How far a cave home moves at most to floor its animals can route from (#10925).</summary>
    private const double ResiteMetres = 8d;

    /// <summary>How many cells about a home are looked over for floor to move it to: once per home, so wider than a settle.</summary>
    internal const int ResiteRingCells = 7;

    /// <summary>
    /// How many of the floor cells about a home are searched from for one that leads somewhere: a home boxed in by a steep face
    /// and a pocket below it (cave 6,2's glass-courser home) has a dozen dead cells nearer than open floor.
    /// </summary>
    internal const int ResiteTries = 40;

    /// <summary>An animal this near its home's old spot was set down on it, and moves with it.</summary>
    private const double HomeSpotMetres = 1.5d;

    /// <summary>How many cave homes have moved to routable floor.</summary>
    private int resitedHomes;

    /// <summary>A home's column is read for at most this many homes in one update.</summary>
    private const int MostHomeReadsPerUpdate = 2;

    private const string RoutableReading = "its column supports it";

    /// <summary>A cave home's routing as the caves readout shows it: ok, UNROUTABLE (why) or pending (why).</summary>
    internal string HomeRouting(HerdRecord home) => (homeRouting.TryGetValue(home.Key, out (bool Final, string Reading) known)
        ? known.Final
            ? known.Reading.StartsWith(RoutableReading, StringComparison.Ordinal) ? "ok" : $"UNROUTABLE ({known.Reading})"
            : $"pending ({known.Reading})"
        : "pending (not looked at yet)")
        + (home.ResitedMetres > 0 ? string.Create(CultureInfo.InvariantCulture, $" moved={home.ResitedMetres:F1}m") : "");

    /// <summary>
    /// Reads the cave homes the published navigation covers that have no final reading yet, a few at a time; one that
    /// cannot start a route is told in the readout's last event.
    /// </summary>
    private void CheckCaveHomes(CreatureSpace? here)
    {
        if (here != walking || Wildlife is null || step % HomeCheckEverySteps != 0) return;
        int reads = 0;
        foreach (HerdRecord home in Wildlife.CaveHomes)
        {
            // Each home is read on its own kind's grid (#10925), published only while one of its size routes.
            CreatureNavigation navigation = HomeNavigation(home);
            if (homeRouting.TryGetValue(home.Key, out (bool Final, string) known) && known.Final) continue;
            // A home moved is read at the floor it was moved to, where its support stands.
            if ((home.ResitedMetres > 0 ? home.Floor : HomeFloor(home)) is not float floor)
            {
                homeRouting[home.Key] = (false, "its floor is not loaded");
                continue;
            }

            // In the caves, a home near the player has its kind's grid published to be read (#10925), as a pursuit would.
            Vector3 feet = player.WorldFeetPosition;
            if (walking.UnderRock(feet) && Vector2.Distance(home.Home, new Vector2(feet.X, feet.Z)) < CreatureNavigation.ReachMetres - HomeReadMarginMetres)
                Publish(navigation, feet);
            if (navigation.Covers(new Vector3(home.Home.X, floor, home.Home.Y)) is string outside)
            {
                homeRouting[home.Key] = (false, outside);
                continue;
            }

            if (reads++ >= MostHomeReadsPerUpdate) break;
            Vector3 site = new(home.Home.X, floor, home.Home.Y);
            string reading = navigation.Diagnose(site);
            // A home on floor its animals cannot route from moves, once, to the nearest that they can and that leads somewhere,
            // near it and on its level (#10925); it is read again there.
            if (!reading.StartsWith(RoutableReading, StringComparison.Ordinal) && home.ResitedMetres == 0
                && navigation.OpenSupport(site, CreatureNavigation.DiagnoseLevelMetres, ResiteMetres, ResiteRingCells, ResiteTries) is Vector3 support)
            {
                home.Resite(new Vector2(support.X, support.Z), support.Y);
                resitedHomes++;
                // One of its animals resting on the old spot goes with it; any already about are routed from where they are.
                foreach (Creature resting in roster.All.Where(c => c.Behavior.State == Rpg.CreatureState.Resting && hunters.TryGetValue(c.Id, out Hunter? h)
                    && h.Record == home && Vector3.Distance(Feet(c), site) <= HomeSpotMetres))
                {
                    resting.Position = new Vector2(support.X, support.Z);
                    resting.Feet = support.Y;
                    resting.Body = null;
                    break;
                }

                homeRouting[home.Key] = (false, string.Create(CultureInfo.InvariantCulture, $"moved {Vector3.Distance(support, site):F1} m off: {reading}"));
                lastEvent = string.Create(CultureInfo.InvariantCulture, $"cave home {home.Key} moved {Vector3.Distance(support, site):F1} m to {support.X:F1},{support.Y:F1},{support.Z:F1}: {reading}");
                continue;
            }

            homeRouting[home.Key] = (true, reading);
            if (!reading.StartsWith(RoutableReading, StringComparison.Ordinal))
            {
                unroutableHomes++;
                lastEvent = string.Create(CultureInfo.InvariantCulture,
                    $"cave home {home.Key} at {home.Home.X:F1},{floor:F1},{home.Home.Y:F1} is UNROUTABLE: {reading}");
            }
        }
    }

    /// <summary>A cave home's floor under its site, when that ground is loaded.</summary>
    private float? HomeFloor(HerdRecord home) =>
        walking.FloorBelow(home.Home.X, home.Home.Y, home.Floor ?? StandingAtHome(home), HomeFloorSearchMetres);
}
