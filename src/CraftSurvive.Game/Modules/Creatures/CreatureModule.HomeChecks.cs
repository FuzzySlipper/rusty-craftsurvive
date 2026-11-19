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

    /// <summary>A home's column is read for at most this many homes in one update.</summary>
    private const int MostHomeReadsPerUpdate = 2;

    private const string RoutableReading = "its column supports it";

    /// <summary>A cave home's routing as the caves readout shows it: ok, UNROUTABLE (why) or pending (why).</summary>
    internal string HomeRouting(HerdRecord home) => homeRouting.TryGetValue(home.Key, out (bool Final, string Reading) known)
        ? known.Final
            ? known.Reading.StartsWith(RoutableReading, StringComparison.Ordinal) ? "ok" : $"UNROUTABLE ({known.Reading})"
            : $"pending ({known.Reading})"
        : "pending (not looked at yet)";

    /// <summary>
    /// Reads the cave homes the published navigation covers that have no final reading yet, a few at a time; one that
    /// cannot start a route is told in the readout's last event.
    /// </summary>
    private void CheckCaveHomes(CreatureSpace? here)
    {
        if (here != walking || Wildlife is null || step % HomeCheckEverySteps != 0) return;
        CreatureNavigation navigation = NavigationIn(walking);
        int reads = 0;
        foreach (HerdRecord home in Wildlife.CaveHomes)
        {
            if (homeRouting.TryGetValue(home.Key, out (bool Final, string) known) && known.Final) continue;
            if (HomeFloor(home) is not float floor)
            {
                homeRouting[home.Key] = (false, "its floor is not loaded");
                continue;
            }

            if (navigation.Covers(new Vector3(home.Home.X, floor, home.Home.Y)) is string outside)
            {
                homeRouting[home.Key] = (false, outside);
                continue;
            }

            if (reads++ >= MostHomeReadsPerUpdate) break;
            string reading = navigation.Diagnose(new Vector3(home.Home.X, floor, home.Home.Y));
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
