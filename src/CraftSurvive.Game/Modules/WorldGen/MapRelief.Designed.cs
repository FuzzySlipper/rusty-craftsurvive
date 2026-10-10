namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// A designed continent's starting state (#9815). The design says where land is and where the land is
/// lifted; erosion then forms the ridges and drainage from that, as it does for a seeded map.
/// <list type="bullet">
/// <item><b>Coast.</b> Deep inside the drawn outline is land and far outside is sea; within the design's
/// coast band, warped noise decides, so bays, headlands, estuaries and islands form where they will.
/// Fixed zones force land (the neck's core) or sea (moats against other bridges).</item>
/// <item><b>Uplift.</b> Each belt lifts the land toward its crest, scaled by its calibration gain, with
/// ridged noise for spurs; uplands and basins raise or lower the rest. Belts are harder rock.</item>
/// <item><b>Shore.</b> Land rises from the resolved shore over a distance that varies along the coast,
/// so the drawn outline leaves no profile of its own.</item>
/// </list>
/// </summary>
internal sealed partial class MapRelief
{
    /// <summary>Within the coast band, how strongly the noise outweighs the distance to the drawn outline.</summary>
    private const double CoastNoiseGain = 2.5;
    /// <summary>A designed coast's noise: its octaves (down to bays a few kilometres across), warp and salt.</summary>
    private const int CoastOctaves = 6;
    private const double CoastWarp = 0.6;
    private const ulong CoastSalt = 0x2F2B8C3E91D5A7C3UL;
    /// <summary>A designed belt keeps this share of its uplift where the ridge noise is lowest.</summary>
    private const double DesignedRidgeFloor = 0.5;
    /// <summary>The shore ramp varies between these shares of the design's, along the coast.</summary>
    private const double ShoreRampLeast = 0.25, ShoreRampMost = 1.75;
    private const ulong ShoreSalt = 0x45D3_A11B_09C7_6E25UL;

    /// <param name="gains">Each belt's calibration gain (1 at first); see <see cref="MapSimulation"/>.</param>
    /// <param name="areaGains">Each area's calibration gain (1 at first; unused for an area without a height).</param>
    internal static MapRelief Designed(MapGrid grid, ulong seed, ReliefRecipe r, ContinentDesign design, IReadOnlyList<double> gains, IReadOnlyList<double> areaGains)
    {
        double l = MapScale.For(grid).Lengths;
        double warpWavelength = r.WarpWavelength * l, warpDistance = r.WarpDistance * l;
        double hardnessWavelength = r.HardnessWavelength * l, textureWavelength = r.TextureWavelength * l, uplandWavelength = r.UplandWavelength * l;
        // Shares are of the design's own highest crest, so a gain above 1 lifts a belt beyond it.
        double highest = Math.Max(1, design.HighestAsked);
        double band = design.CoastBand / 2;
        MapRelief relief = new(grid);
        for (int i = 0; i < grid.Count; i++)
        {
            double x = grid.X(i), z = grid.Z(i), u = x / grid.Radius, v = z / grid.Radius;
            double wx = x + warpDistance * MapNoise.Fbm(seed ^ WarpXSalt, x / warpWavelength, z / warpWavelength, 3, r.FractalPersistence);
            double wz = z + warpDistance * MapNoise.Fbm(seed ^ WarpZSalt, x / warpWavelength, z / warpWavelength, 3, r.FractalPersistence);
            relief.Sea[i] = !DesignedLand(design, seed, u, v, band);
            relief.Hardness[i] = WorldMap.Smooth(Math.Clamp(0.5 + r.HardnessContrast * MapNoise.Fbm(seed ^ HardnessSalt,
                wx / hardnessWavelength, wz / hardnessWavelength, 3, r.FractalPersistence), 0, 1));
            relief.Height[i] = MapNoise.Fbm(seed ^ TextureSalt, x / textureWavelength, z / textureWavelength, r.TextureOctaves, r.FractalPersistence) * r.InitialTexture;
            if (relief.Sea[i]) continue;

            double share = design.Crest(u, v, gains) / highest;
            double ridges = MapNoise.Ridged(seed ^ BeltSalt, u / design.RidgeWavelength, v / design.RidgeWavelength, r.BeltOctaves, r.BeltPersistence);
            double upland = r.UplandUplift * (0.5 + 0.5 * MapNoise.Fbm(seed ^ UplandSalt, wx / uplandWavelength, wz / uplandWavelength, 3, r.FractalPersistence));
            double belts = share * (DesignedRidgeFloor + ((1 - DesignedRidgeFloor) * ridges));
            double areas = design.AreaUplift(u, v, areaGains, highest, ridges);
            relief.Uplift[i] = Math.Max(0, ((r.PlainUplift + upland) * (1 + design.AreaLift(u, v))) + (r.BeltUplift * Math.Max(belts, areas)));
            relief.Hardness[i] = Math.Clamp(relief.Hardness[i] + (design.BeltHardness * Math.Max(share, design.Ruggedness(u, v))), 0, 1);
        }

        // The land must meet the mainland only through the neck (R9815-1): a stray bridge is drowned, a world that cannot be repaired refused.
        DesignTopology.Enforce(grid, relief.Sea, design);

        // Land rises from the shore it actually has.
        double[] shore = DistanceFromOutlets(grid, relief.Sea);
        for (int i = 0; i < grid.Count; i++)
        {
            if (relief.Sea[i])
            {
                relief.Outlet[i] = true;
                relief.Uplift[i] = 0;
                relief.Height[i] = -r.SeaFloorDepth * 2;
                continue;
            }

            double vary = 0.5 + 0.5 * MapNoise.Fbm(seed ^ ShoreSalt, grid.X(i) / warpWavelength, grid.Z(i) / warpWavelength, 3, r.FractalPersistence);
            double ramp = WorldMap.Smooth(Math.Clamp(shore[i] / (design.ShoreRampMetres * (ShoreRampLeast + ((ShoreRampMost - ShoreRampLeast) * vary))), 0, 1));
            relief.Uplift[i] *= ramp;
            relief.Height[i] += relief.Uplift[i] * r.InitialRelief + 1;
            relief.Height[i] += r.InlandRise * shore[i] / (1000 * l);
        }

        return relief;
    }

    /// <summary>
    /// Carry an evolved designed relief onto a finer lattice: heights, uplift and hardness interpolated as
    /// for any map, but the coast decided again at the finer spacing, so its bays and headlands are drawn
    /// at that resolution rather than smoothed from the coarser one.
    /// </summary>
    internal static MapRelief RefineDesigned(MapRelief coarse, MapGrid fine, ulong seed, ReliefRecipe r, ContinentDesign design)
    {
        double textureWavelength = r.TextureWavelength * MapScale.For(fine).Lengths;
        double band = design.CoastBand / 2;
        MapRelief relief = new(fine);
        MapGrid from = coarse.Grid;
        for (int i = 0; i < fine.Count; i++)
        {
            double x = fine.X(i), z = fine.Z(i);
            relief.Hardness[i] = from.Bilinear(coarse.Hardness, x, z);
            if (!DesignedLand(design, seed, x / fine.Radius, z / fine.Radius, band))
            {
                relief.Sea[i] = relief.Outlet[i] = true;
                relief.Height[i] = Math.Min(from.Bilinear(coarse.Height, x, z), -r.SeaFloorDepth);
                continue;
            }

            relief.Uplift[i] = from.Bilinear(coarse.Uplift, x, z);
            double texture = MapNoise.Fbm(seed ^ TextureSalt, x / textureWavelength, z / textureWavelength, r.TextureOctaves, r.FractalPersistence);
            relief.Height[i] = Math.Max(from.Bilinear(coarse.Height, x, z), 0) + r.RefinedTexture * (1 + texture);
        }

        // The finer coast is held to the same rules; land a repair drowns becomes sea floor.
        bool[] wasSea = (bool[])relief.Sea.Clone();
        DesignTopology.Enforce(fine, relief.Sea, design);
        for (int i = 0; i < fine.Count; i++)
        {
            if (!relief.Sea[i] || wasSea[i]) continue;
            relief.Outlet[i] = true;
            relief.Uplift[i] = 0;
            relief.Height[i] = -r.SeaFloorDepth;
        }

        return relief;
    }

    /// <summary>
    /// Whether a point is land: forced by a fixed zone; else land deep inside the outline, sea far outside,
    /// and within the coast band whichever the warped noise favours there.
    /// </summary>
    internal static bool DesignedLand(ContinentDesign design, ulong seed, double u, double v, double band)
    {
        if (design.FixedAt(u, v) is bool forced) return forced;
        double distance = design.LandDistance(u, v);
        if (distance >= band) return true;
        if (distance <= -band) return false;
        double fx = u / design.CoastWavelength, fz = v / design.CoastWavelength;
        double warp = CoastWarp * MapNoise.Fbm(seed ^ WarpXSalt, fx * 0.5, fz * 0.5, 3, 0.5);
        double noise = MapNoise.Fbm(seed ^ CoastSalt, fx + warp, fz - warp, CoastOctaves, 0.5);
        return (distance / band) + (CoastNoiseGain * noise) > 0;
    }
}
