namespace CraftSurvive.Game.Modules.Sky;

/// <summary>
/// Which stars show (#9807), as rules. The night develops as the sun sinks: the brightest stars appear
/// first, then the rest, then the Milky Way. The moon washes the faint ones out (a full moon hides the
/// Milky Way), and murk dims all of them. Clouds cover them where they lie, which the sky draws.
/// Magnitudes are astronomical: smaller is brighter.
/// </summary>
internal static class Stars
{
    /// <summary>
    /// The faintest star that shows as the sun sinks, by its elevation (a sine): none until it is this
    /// far under, the brightest by the end of twilight's glow, and the faintest only once the sky is
    /// fully dark, well into the night.
    /// </summary>
    private static readonly (double Elevation, double Magnitude)[] BySun =
    [
        (-0.05d, -1.5d),
        (-0.11d, 1.5d),
        (-0.22d, 4d),
        (-0.42d, DarkSkyMagnitude),
    ];

    /// <summary>The faintest star a dark, moonless, clear sky shows.</summary>
    internal const double DarkSkyMagnitude = 6.2d;

    /// <summary>A full moon high (share 1) takes this many magnitudes off the faintest star, so its light hides the Milky Way.</summary>
    private const double MoonWash = 2d;

    /// <summary>Murk (fog, blown sand) at full strength takes this many off.</summary>
    private const double MurkWash = 5d;

    /// <summary>The Milky Way shows only in a sky this dark (limiting magnitudes), fading in between them.</summary>
    private const double BandFrom = 5.2d, BandFull = 6d;

    /// <summary>The faintest star that shows now, from the sun's elevation (a sine), the moon's share of a full moon's light and the murk.</summary>
    internal static double LimitingMagnitude(double sunElevation, double moonShare, double murk)
    {
        double sun = BySun[0].Magnitude;
        if (sunElevation <= BySun[^1].Elevation)
        {
            sun = BySun[^1].Magnitude;
        }
        else
        {
            for (int i = 1; i < BySun.Length; i++)
            {
                if (sunElevation > BySun[i].Elevation)
                {
                    double t = (sunElevation - BySun[i - 1].Elevation) / (BySun[i].Elevation - BySun[i - 1].Elevation);
                    sun = BySun[i - 1].Magnitude + ((BySun[i].Magnitude - BySun[i - 1].Magnitude) * Math.Clamp(t, 0d, 1d));
                    break;
                }
            }
        }

        double moon = DarkSkyMagnitude - (MoonWash * Math.Clamp(moonShare, 0d, 1d));
        return Math.Min(sun, moon) - (MurkWash * Math.Clamp(murk, 0d, 1d));
    }

    /// <summary>How strongly the Milky Way shows at a limiting magnitude, 0 to 1.</summary>
    internal static double Band(double limitingMagnitude)
    {
        double t = Math.Clamp((limitingMagnitude - BandFrom) / (BandFull - BandFrom), 0d, 1d);
        return t * t * (3d - (2d * t));
    }
}
