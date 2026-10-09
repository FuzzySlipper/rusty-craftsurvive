using CraftSurvive.Game.Modules.Horizon;
using CraftSurvive.Game.Tests;

/// <summary>
/// The horizon backdrop's sunk zone (#9779, R9779-2): moving it never changes ground the player can
/// see. Wherever the sink differs between the old and new centre, the point is under the far field,
/// which covers at least its reach about the player (Chebyshev).
/// </summary>
internal static class HorizonChecks
{
    private const double FarChunkMetres = 128;
    private const int Moves = 4000;

    internal static void SinkMovesOnlyUnderTheFarField()
    {
        Random random = new(9779);
        int changedPoints = 0, exposedChanges = 0;
        foreach (double reach in new double[] { 6 * FarChunkMetres, 12 * FarChunkMetres, 16 * FarChunkMetres })
        {
            for (int move = 0; move < Moves; move++)
            {
                // The zone was centred where the player stood; they walk to just past the follow step and it moves onto them.
                double angle = random.NextDouble() * Math.Tau;
                double step = HorizonSink.FollowMetres * (1 + (0.05 * random.NextDouble()));
                (double X, double Z) old = (0, 0), player = (Math.Cos(angle) * step, Math.Sin(angle) * step);
                for (int sample = 0; sample < 64; sample++)
                {
                    double x = (random.NextDouble() - 0.5) * 2 * (reach + 1000), z = (random.NextDouble() - 0.5) * 2 * (reach + 1000);
                    double before = HorizonSink.At(Math.Max(Math.Abs(x - old.X), Math.Abs(z - old.Z)), reach, FarChunkMetres);
                    double after = HorizonSink.At(Math.Max(Math.Abs(x - player.X), Math.Abs(z - player.Z)), reach, FarChunkMetres);
                    if (Math.Abs(after - before) < 1e-9) continue;
                    changedPoints++;
                    if (Math.Max(Math.Abs(x - player.X), Math.Abs(z - player.Z)) > reach) exposedChanges++;
                }
            }
        }

        Check.That(changedPoints > 0, "moving the sunk zone changes the backdrop somewhere");
        Check.That(exposedChanges == 0, $"every change lies under the far field about the player ({exposedChanges} of {changedPoints} did not)");
        Check.That(HorizonSink.At(0, 12 * FarChunkMetres, FarChunkMetres) == HorizonSink.DepthMetres, "the backdrop is fully sunk about the player");
        Check.That(HorizonSink.At(HorizonSink.CoveredMetres(12 * FarChunkMetres, FarChunkMetres), 12 * FarChunkMetres, FarChunkMetres) == 0,
            "and at true height from the covered radius out");
        // The reviewer's case: reach 1,536 m, centre (0,0) to (256,0), point (1952,0).
        double reviewed = Math.Abs(HorizonSink.At(1952 - 256, 1536, FarChunkMetres) - HorizonSink.At(1952, 1536, FarChunkMetres));
        Check.That(reviewed == 0, $"the reviewed point 1,952 m out does not move ({reviewed:F3} m)");
    }

    /// <summary>
    /// Known places on the horizon (#9781): a place within the near ground is its own structure, between
    /// it and the far field's reach it stands in the world, past that in the backdrop; its silhouette
    /// is life size up close and never smaller than the floor angle far off; home is lit only by night.
    /// </summary>
    internal static void LandmarksStandInTheirBand()
    {
        const double Near = 128, Reach = 12 * FarChunkMetres, Height = 8;
        Check.That(HorizonLandmarkRules.Band(Near - 1, Near, Reach) == LandmarkBand.None, "a place within the near ground is its own structure");
        Check.That(HorizonLandmarkRules.Band(Near, Near, Reach) == LandmarkBand.World && HorizonLandmarkRules.Band(Reach - 1, Near, Reach) == LandmarkBand.World,
            "between the near ground and the far field's reach it stands in the world");
        Check.That(HorizonLandmarkRules.Band(Reach, Near, Reach) == LandmarkBand.Backdrop
            && HorizonLandmarkRules.Band(HorizonLandmarkRules.RangeMetres, Near, Reach) == LandmarkBand.Backdrop, "past the reach, in the backdrop");
        Check.That(HorizonLandmarkRules.Band(HorizonLandmarkRules.RangeMetres + 1, Near, Reach) == LandmarkBand.None, "and not past its range");

        Check.That(HorizonLandmarkRules.Grow(Near, Height) == 1, "up close a silhouette is life size");
        double worst = double.MaxValue, previous = 0;
        bool growing = true;
        for (double distance = Near; distance <= HorizonLandmarkRules.RangeMetres; distance += 50)
        {
            double grow = HorizonLandmarkRules.Grow(distance, Height);
            worst = Math.Min(worst, Math.Atan(grow * Height / distance) * 180 / Math.PI);
            growing &= grow >= previous;
            previous = grow;
        }

        Check.That(worst >= HorizonLandmarkRules.MinimumDegrees - 1e-9, $"far off it is never under {HorizonLandmarkRules.MinimumDegrees} degrees tall (least {worst:F3})");
        Check.That(growing, "and it grows steadily with distance, so it never jumps");
        Check.That(HorizonLandmarkRules.Lit(1) == 0 && HorizonLandmarkRules.Lit(HorizonLandmarkRules.LightsFrom) == 0, "home is unlit by day");
        Check.That(HorizonLandmarkRules.Lit(HorizonLandmarkRules.LightsFull) == 1 && HorizonLandmarkRules.Lit(0) == 1, "and fully lit by night");
    }
}
