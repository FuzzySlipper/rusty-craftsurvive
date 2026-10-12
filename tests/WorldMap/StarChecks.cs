using System.Numerics;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Tests;

/// <summary>
/// The night sky (#9807): a sky turning about a pole above the north horizon (north is -Z, east +X),
/// stars that turn with it and drift through a year, and which stars show as the night develops.
/// </summary>
internal static class StarChecks
{
    private const float Close = 1e-3f;

    internal static void Run()
    {
        // The sky's latitude: the pole stands north, the latitude up; the sun rises due east, stands in the south at noon, sets due west.
        float latitude = (float)(WorldClock.LatitudeDegrees * Math.PI / 180d);
        Check.That(Vector3.Distance(WorldClock.CelestialPole, new Vector3(0f, MathF.Sin(latitude), -MathF.Cos(latitude))) < Close, "the pole stands north, the latitude above the horizon");
        Check.That(Vector3.Distance(WorldClock.TowardSun(0.25d), Vector3.UnitX) < Close, "the sun rises due east");
        Check.That(Vector3.Distance(WorldClock.TowardSun(0.75d), -Vector3.UnitX) < Close, "the sun sets due west");
        Vector3 noon = WorldClock.TowardSun(0.5d);
        Check.That(MathF.Abs(noon.X) < Close && noon.Z > 0f && MathF.Abs(MathF.Asin(noon.Y) - ((MathF.PI / 2f) - latitude)) < Close, "the noon sun stands in the south, the latitude short of the zenith");
        Check.That(Math.Abs(WorldClock.SunElevation(0.4d) - WorldClock.TowardSun(0.4d).Y) < Close, "the sun's elevation is its direction's");
        Check.That(MathF.Abs(Vector3.Dot(WorldClock.TowardSun(0.37d), WorldClock.CelestialPole)) < Close, "the sun keeps to the celestial equator");

        // The stars: the pole star stands still; the catalogue's +X is on the meridian at the first midnight and sets in the west six hours later.
        foreach (double fraction in new[] { 0d, 0.3d, 0.77d })
        {
            Vector3 pole = Vector3.Transform(Vector3.UnitZ, WorldClock.StarOrientation(new WorldTime(5, fraction)));
            Check.That(Vector3.Distance(pole, WorldClock.CelestialPole) < Close, $"the pole star stands still ({fraction})");
        }

        Vector3 meridian = Vector3.Transform(Vector3.UnitX, WorldClock.StarOrientation(new WorldTime(0, 0d)));
        Check.That(Vector3.Distance(meridian, WorldClock.TowardSun(0.5d)) < Close, "the catalogue's +X is on the meridian at the first midnight");
        Vector3 setting = Vector3.Transform(Vector3.UnitX, WorldClock.StarOrientation(new WorldTime(0, 0.25d / (1d + (1d / WorldClock.StarYearDays)))));
        Check.That(Vector3.Distance(setting, -Vector3.UnitX) < 2e-3f, "the stars turn westward: six sidereal hours later it sets due west");
        Vector3 a = Vector3.Transform(Vector3.UnitX, WorldClock.StarOrientation(new WorldTime(10, 0d)));
        Vector3 b = Vector3.Transform(Vector3.UnitX, WorldClock.StarOrientation(new WorldTime(11, 0d)));
        float drift = MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * 180f / MathF.PI;
        Check.That(MathF.Abs(drift - (360f / (float)WorldClock.StarYearDays)) < 0.05f, $"the stars at midnight drift a year's share each day ({drift:F2}°)");
        Vector3 yearLater = Vector3.Transform(Vector3.UnitX, WorldClock.StarOrientation(new WorldTime(10 + (long)WorldClock.StarYearDays, 0d)));
        Check.That(Vector3.Distance(a, yearLater) < Close, "a year later the same stars stand at midnight");

        // Which stars show: none by day, the brightest first, the faintest late; the moon and murk wash them out.
        Check.That(Stars.LimitingMagnitude(0.5d, 0d, 0d) < 0d, "no stars by day");
        double previous = double.MinValue;
        bool developing = true;
        for (double elevation = 0d; elevation >= -0.9d; elevation -= 0.01d)
        {
            double limit = Stars.LimitingMagnitude(elevation, 0d, 0d);
            developing &= limit >= previous;
            previous = limit;
        }

        Check.That(developing, "stars only ever come out as the sun sinks");
        Check.That(Stars.LimitingMagnitude(-0.12d, 0d, 0d) is > 1d and < 3d, "at the end of twilight's glow only the brightest show");
        Check.That(Stars.Band(Stars.LimitingMagnitude(-0.2d, 0d, 0d)) == 0d, "no Milky Way in twilight");
        double dark = Stars.LimitingMagnitude(-0.9d, 0d, 0d);
        Check.That(Math.Abs(dark - Stars.DarkSkyMagnitude) < 1e-9 && Stars.Band(dark) == 1d, "a dark moonless night shows the faintest stars and the Milky Way");
        double moonlit = Stars.LimitingMagnitude(-0.9d, 1d, 0d);
        Check.That(moonlit < dark - 1d && Stars.Band(moonlit) == 0d, "a full moon washes out the faint stars and hides the Milky Way");
        Check.That(Stars.LimitingMagnitude(-0.9d, 0d, 1d) < 2d, "murk hides most stars");
    }
}
