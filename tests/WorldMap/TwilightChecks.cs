using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Tests;

/// <summary>
/// Sunrise and sunset (#9805): the sky steps from day through the glow and the afterglow to night,
/// blending only neighbours and never jumping, and each dawn and dusk rolls its own mood.
/// </summary>
internal static class TwilightChecks
{
    /// <summary>Steps through a day: about a twentieth of a game minute.</summary>
    private const int Steps = 24 * 60 * 20;

    /// <summary>The largest change in what is shown between neighbouring steps.</summary>
    private const double LargestStep = 0.02;

    /// <summary>Days rolled for the share of muted twilights.</summary>
    private const int RolledDays = 2000;

    internal static void Run()
    {
        foreach (float vividness in new[] { 0.1f, 0.9f })
        {
            TwilightMood mood = Twilight.Mood(vividness);
            Check.Equal(SkyPanorama.Day, Shown(Twilight.Blend(0.5, vividness)), "noon shows the day panorama alone");
            Check.Equal(SkyPanorama.Night, Shown(Twilight.Blend(0.0, vividness)), "midnight shows the night panorama alone");
            Check.Equal(Twilight.Panorama(dawn: false, afterglow: false, mood), Shown(Twilight.Blend(0.75, vividness)), "sunset shows the dusk's glow alone");
            Check.Equal(Twilight.Panorama(dawn: true, afterglow: false, mood), Shown(Twilight.Blend(0.25, vividness)), "sunrise shows the dawn's glow alone");
            double afterglow = 0.75 + (Math.Asin(-Twilight.AfterglowAt / Math.Cos(WorldClock.LatitudeDegrees * Math.PI / 180d)) / (2 * Math.PI));
            Check.Equal(Twilight.Panorama(dawn: false, afterglow: true, mood), Shown(Twilight.Blend(afterglow, vividness)), "the sun under the horizon shows the dusk's afterglow alone");

            // Continuity: what is shown, as a weight on each panorama, never jumps between steps.
            Dictionary<SkyPanorama, double> previous = Weights(Twilight.Blend(0, vividness));
            double largest = 0;
            for (int step = 1; step <= Steps; step++)
            {
                Dictionary<SkyPanorama, double> now = Weights(Twilight.Blend((double)step / Steps % 1d, vividness));
                foreach (SkyPanorama key in Enum.GetValues<SkyPanorama>())
                {
                    largest = Math.Max(largest, Math.Abs(now.GetValueOrDefault(key) - previous.GetValueOrDefault(key)));
                }

                previous = now;
            }

            Check.That(largest < LargestStep, $"the sky never jumps through a {mood} day (largest step {largest:F4})");
        }

        // Dawn and dusk the same day roll apart; each roll repeats; some are muted and most are vivid.
        int muted = 0, differing = 0;
        for (long day = 0; day < RolledDays; day++)
        {
            float dawn = Twilight.Vividness(day, dawn: true), dusk = Twilight.Vividness(day, dawn: false);
            Check.That(dawn is >= 0f and < 1f && dusk is >= 0f and < 1f, "a vividness lies in [0, 1)");
            if (Twilight.Mood(dusk) == TwilightMood.Muted) muted++;
            if (dawn != dusk) differing++;
        }

        Check.Equal(Twilight.Vividness(41, dawn: false), Twilight.Vividness(41, dawn: false), "a day's dusk always rolls the same");
        double mutedShare = (double)muted / RolledDays;
        Check.That(Math.Abs(mutedShare - Twilight.MutedBelow) < 0.05, $"about {Twilight.MutedBelow:P0} of dusks are muted ({mutedShare:P1})");
        Check.That(differing > RolledDays * 0.99, "a day's dawn and dusk roll independently");
    }

    /// <summary>The panorama a blend shows alone, or none when it shows two.</summary>
    private static SkyPanorama? Shown(SkyBlend blend) =>
        blend.Amount <= 0f ? blend.From : blend.Amount >= 1f ? blend.To : null;

    private static Dictionary<SkyPanorama, double> Weights(SkyBlend blend)
    {
        Dictionary<SkyPanorama, double> weights = [];
        weights[blend.From] = 1d - blend.Amount;
        weights[blend.To] = weights.GetValueOrDefault(blend.To) + blend.Amount;
        return weights;
    }
}
