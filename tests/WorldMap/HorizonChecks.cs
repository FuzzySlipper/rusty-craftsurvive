using System.Numerics;
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
        Check.That(HorizonLandmarkRules.Band(Near - 1, 0, Near, Reach) == LandmarkBand.None, "a place within the near ground is its own structure");
        Check.That(HorizonLandmarkRules.Band(Near, 0, Near, Reach) == LandmarkBand.World && HorizonLandmarkRules.Band(Reach - 1, 0, Near, Reach) == LandmarkBand.World,
            "between the near ground and the far field's reach it stands in the world");
        Check.That(HorizonLandmarkRules.Band(Reach, 0, Near, Reach) == LandmarkBand.Backdrop
            && HorizonLandmarkRules.Band(0, HorizonLandmarkRules.RangeMetres, Near, Reach) == LandmarkBand.Backdrop, "past the reach, in the backdrop");
        Check.That(HorizonLandmarkRules.Band(HorizonLandmarkRules.RangeMetres + 1, 0, Near, Reach) == LandmarkBand.None, "and not past its range");
        // The reviewed diagonals (R9781-1): radially past the reach, but still under the square far field.
        Check.That(HorizonLandmarkRules.Band(1088, 1088, Near, 1536) == LandmarkBand.World && HorizonLandmarkRules.Band(1450, 1450, Near, 2048) == LandmarkBand.World,
            "a place on the diagonal stays in the world while the far field covers it");

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

    /// <summary>
    /// R9781-1: wherever a place is drawn in the backdrop, the backdrop's ground under it is not sunk,
    /// so its silhouette stands on visible ground; wherever it is drawn in the world, the far field
    /// (a square of at least its reach about the player) covers it. Every bearing, with the sunk zone's
    /// centre anywhere within a follow step of the player.
    /// </summary>
    internal static void LandmarksStandOnVisibleGround()
    {
        const double Near = 128;
        Random random = new(9781);
        int backdrop = 0, world = 0, sunk = 0, uncovered = 0;
        foreach (double reach in new double[] { 6 * FarChunkMetres, 12 * FarChunkMetres, 16 * FarChunkMetres })
        {
            for (int sample = 0; sample < 20_000; sample++)
            {
                // About the band's edge, on any bearing: the diagonals are where radial and square differ most.
                double angle = random.NextDouble() * Math.Tau;
                double radius = reach * (0.9 + (0.6 * random.NextDouble()));
                double dx = Math.Cos(angle) * radius, dz = Math.Sin(angle) * radius;
                double sinkX = (random.NextDouble() - 0.5) * 2 * (HorizonSink.FollowMetres - 1), sinkZ = (random.NextDouble() - 0.5) * 2 * (HorizonSink.FollowMetres - 1);
                switch (HorizonLandmarkRules.Band(dx, dz, Near, reach))
                {
                    case LandmarkBand.Backdrop:
                        backdrop++;
                        if (HorizonSink.At(Math.Max(Math.Abs(dx - sinkX), Math.Abs(dz - sinkZ)), reach, FarChunkMetres) > 0) sunk++;
                        break;
                    case LandmarkBand.World:
                        world++;
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) >= reach) uncovered++;
                        break;
                }
            }
        }

        Check.That(backdrop > 0 && world > 0, "places fall in both bands about the edge");
        Check.That(sunk == 0, $"no backdrop place stands on sunk ground ({sunk} of {backdrop} did)");
        Check.That(uncovered == 0, $"every world place is under the far field ({uncovered} of {world} were not)");
    }

    /// <summary>
    /// R9822-1: the middle tier sinks beneath the region window, so when the window moves or its tiles
    /// become ready, every middle column whose sinking changes must be sampled again. Any point whose
    /// sinking differs between the old state and the new lies in a column under the old window or the new.
    /// </summary>
    internal static void MiddleTierFollowsTheRegionWindow()
    {
        const double Window = 16 * HorizonTiers.RegionChunkMetres;
        Random random = new(98221);
        int changes = 0, missed = 0;
        for (int move = 0; move < 400; move++)
        {
            (Vector2 Min, Vector2 Max, HashSet<(long, long)> Covered) State(double x, double z, double readiness)
            {
                double minX = (Math.Floor(x / HorizonTiers.RegionChunkMetres) - 8) * HorizonTiers.RegionChunkMetres;
                double minZ = (Math.Floor(z / HorizonTiers.RegionChunkMetres) - 8) * HorizonTiers.RegionChunkMetres;
                HashSet<(long, long)> covered = [];
                for (int cz = 0; cz < 16; cz++)
                for (int cx = 0; cx < 16; cx++)
                    if (random.NextDouble() < readiness) covered.Add(((long)(minX / HorizonTiers.RegionChunkMetres) + cx, (long)(minZ / HorizonTiers.RegionChunkMetres) + cz));
                return (new((float)minX, (float)minZ), new((float)(minX + Window), (float)(minZ + Window)), covered);
            }

            double fromX = (random.NextDouble() - 0.5) * 100_000, fromZ = (random.NextDouble() - 0.5) * 100_000;
            // A walk (a few chunks), or a teleport; tiles partly or wholly ready before and after.
            bool teleport = random.NextDouble() < 0.2;
            double toX = fromX + (teleport ? 30_000 : (random.NextDouble() - 0.5) * 3000), toZ = fromZ + (teleport ? -20_000 : (random.NextDouble() - 0.5) * 3000);
            var before = State(fromX, fromZ, random.NextDouble());
            var after = State(toX, toZ, random.NextDouble());
            HashSet<(long, long)> resampled = [.. HorizonTiers.MidColumnsUnder(before.Min, before.Max).Concat(HorizonTiers.MidColumnsUnder(after.Min, after.Max))];
            for (int sample = 0; sample < 200; sample++)
            {
                double x = fromX + ((random.NextDouble() - 0.5) * 80_000), z = fromZ + ((random.NextDouble() - 0.5) * 80_000);
                if (sample % 2 == 0)
                {
                    x = before.Min.X + (random.NextDouble() * Window);
                    z = before.Min.Y + (random.NextDouble() * Window);
                }

                if (HorizonTiers.UnderRegion(before.Covered, before.Min, before.Max, x, z) == HorizonTiers.UnderRegion(after.Covered, after.Min, after.Max, x, z)) continue;
                changes++;
                if (!resampled.Contains(((long)Math.Floor(x / HorizonTiers.MidChunkMetres), (long)Math.Floor(z / HorizonTiers.MidChunkMetres)))) missed++;
            }
        }

        Check.That(changes > 0, "moves and readiness changes do change where the region sinks the tier beneath");
        Check.That(missed == 0, $"every point whose sinking changes lies in a middle column sampled again ({missed} of {changes} did not)");
    }
}
