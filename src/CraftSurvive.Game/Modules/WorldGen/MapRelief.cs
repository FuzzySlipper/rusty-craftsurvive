namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The tectonic starting state that erosion works on: where land is, how fast each place is
/// lifted, and how resistant its rock is. Nothing here is a fixed template; every landform is
/// placed by seeded noise in metre space, and the finite border is either open sea or a
/// mountain rim chosen per side.
/// </summary>
internal sealed class MapRelief
{
    // Wavelengths in metres: continents, mountain belts, regional activity, rock strata.
    private const double ContinentWavelength = 7000;
    private const double WarpWavelength = 3500;
    private const double WarpDistance = 1400;
    private const double BeltWavelength = 3200;
    private const double ActivityWavelength = 5200;
    private const double HardnessWavelength = 1100;
    private const double TextureWavelength = 260;
    private const int ContinentOctaves = 5;
    private const int BeltOctaves = 4;
    private const int TextureOctaves = 4;
    private const double FractalPersistence = 0.5;
    private const double BeltPersistence = 0.55;

    private const double MinimumSeaFraction = 0.12;
    private const double SeaFractionRange = 0.16;
    private const double SeaSideChance = 0.55;
    private const double MaximumSeaReach = 2600;
    private const double SeaReachFraction = 0.24;
    private const double MaximumRangeReach = 1100;
    private const double RangeReachFraction = 0.1;
    private const double BorderWobble = 0.45;
    private const double SeaBorderStrength = 0.9;
    private const double RangeBorderLand = 0.5;
    private const double RangeBorderUplift = 0.55;
    private const double UplandWavelength = 1700;
    private const double UplandUplift = 0.3;
    private const double CentreReserveRadius = 420;
    private const double CentreLandBias = 0.8;
    private const double CentreCalm = 0.75;
    private const double CoastRamp = 0.12;

    private const double PlainUplift = 0.06;
    private const double BeltUplift = 1.5;
    private const double BeltSharpness = 1.6;
    private const double ActivityFloor = -0.15;
    private const double ActivityCeiling = 0.35;
    private const double HardnessContrast = 1.1;
    private const double InitialRelief = 14;
    private const double InitialTexture = 3;
    private const double SeaFloorDepth = 1;
    /// <summary>Simulation units of texture added when refining, enough to seed finer channels.</summary>
    private const double RefinedTexture = 0.4;
    /// <summary>Simulation units of rise per kilometre inland, so drainage organises toward the sea.</summary>
    private const double InlandRise = 1.6;

    private const ulong ContinentSalt = 0x6A09E667F3BCC908UL;
    private const ulong WarpXSalt = 0xBB67AE8584CAA73BUL;
    private const ulong WarpZSalt = 0x3C6EF372FE94F82BUL;
    private const ulong BeltSalt = 0xA54FF53A5F1D36F1UL;
    private const ulong ActivitySalt = 0x510E527FADE682D1UL;
    private const ulong HardnessSalt = 0x9B05688C2B3E6C1FUL;
    private const ulong TextureSalt = 0x1F83D9ABFB41BD6BUL;
    private const ulong BorderSalt = 0x5BE0CD19137E2179UL;
    private const ulong SeaFractionSalt = 0xCBBB9D5DC1059ED8UL;
    private const ulong WobbleSalt = 0x629A292A367CD507UL;
    private const ulong UplandSalt = 0x9159015A3070DD17UL;

    private MapRelief(MapGrid grid)
    {
        Grid = grid;
        Height = new double[grid.Count];
        Uplift = new double[grid.Count];
        Hardness = new double[grid.Count];
        Sea = new bool[grid.Count];
        Outlet = new bool[grid.Count];
    }

    internal MapGrid Grid { get; }
    /// <summary>Initial surface in simulation units; erosion rescales it to metres afterwards.</summary>
    internal double[] Height { get; }
    internal double[] Uplift { get; }
    /// <summary>0..1 rock resistance: high values erode slowly and stand as ridges and mesas.</summary>
    internal double[] Hardness { get; }
    internal bool[] Sea { get; }
    /// <summary>Base level: open sea, plus the border where a side meets the sea.</summary>
    internal bool[] Outlet { get; }
    /// <summary>West, east, north, south: whether that border is sea rather than a mountain rim.</summary>
    internal bool[] SeaSides { get; } = new bool[4];

    internal static MapRelief Build(MapGrid grid, ulong seed)
    {
        MapRelief relief = new(grid);
        relief.ChooseBorders(seed);
        double seaReach = Math.Min(MaximumSeaReach, grid.Radius * 2 * SeaReachFraction);
        double rangeReach = Math.Min(MaximumRangeReach, grid.Radius * 2 * RangeReachFraction);
        double[] continent = new double[grid.Count];
        double[] borderUplift = new double[grid.Count];
        for (int i = 0; i < grid.Count; i++)
        {
            double x = grid.X(i), z = grid.Z(i);
            double wx = x + WarpDistance * MapNoise.Fbm(seed ^ WarpXSalt, x / WarpWavelength, z / WarpWavelength, 3, FractalPersistence);
            double wz = z + WarpDistance * MapNoise.Fbm(seed ^ WarpZSalt, x / WarpWavelength, z / WarpWavelength, 3, FractalPersistence);
            double c = MapNoise.Fbm(seed ^ ContinentSalt, wx / ContinentWavelength, wz / ContinentWavelength, ContinentOctaves, FractalPersistence);
            double centre = Math.Exp(-(x * x + z * z) / (CentreReserveRadius * CentreReserveRadius));
            c += CentreLandBias * centre;
            // Distance inside each border, west/east/north/south.
            ReadOnlySpan<double> inside = [x + grid.Radius, grid.Radius - x, z + grid.Radius, grid.Radius - z];
            double wobble = 1 + BorderWobble * MapNoise.Fbm(seed ^ WobbleSalt, x / WarpWavelength, z / WarpWavelength, 3, FractalPersistence);
            for (int side = 0; side < 4; side++)
            {
                double reach = relief.SeaSides[side] ? seaReach : rangeReach;
                double near = 1 - WorldMap.Smooth(Math.Clamp(inside[side] * wobble / reach, 0, 1));
                if (relief.SeaSides[side]) c -= SeaBorderStrength * near;
                else
                {
                    c += RangeBorderLand * near;
                    borderUplift[i] = Math.Max(borderUplift[i], RangeBorderUplift * near);
                }
            }
            continent[i] = c;

            double belt = Math.Pow(MapNoise.Ridged(seed ^ BeltSalt, wx / BeltWavelength, wz / BeltWavelength, BeltOctaves, BeltPersistence), BeltSharpness);
            double activity = WorldMap.Smooth(Math.Clamp((MapNoise.Fbm(seed ^ ActivitySalt, x / ActivityWavelength, z / ActivityWavelength, 3, FractalPersistence)
                - ActivityFloor) / (ActivityCeiling - ActivityFloor), 0, 1));
            double upland = UplandUplift * (0.5 + 0.5 * MapNoise.Fbm(seed ^ UplandSalt, wx / UplandWavelength, wz / UplandWavelength, 3, FractalPersistence));
            relief.Uplift[i] = (PlainUplift + upland + BeltUplift * belt * activity) * (1 - CentreCalm * centre);
            relief.Hardness[i] = WorldMap.Smooth(Math.Clamp(0.5 + HardnessContrast * MapNoise.Fbm(seed ^ HardnessSalt,
                wx / HardnessWavelength, wz / HardnessWavelength, 3, FractalPersistence), 0, 1));
            double texture = MapNoise.Fbm(seed ^ TextureSalt, x / TextureWavelength, z / TextureWavelength, TextureOctaves, FractalPersistence);
            relief.Height[i] = texture * InitialTexture;
        }

        double seaFraction = MinimumSeaFraction + SeaFractionRange * MapNoise.Unit(seed ^ SeaFractionSalt, 0, 0);
        double shoreline = Quantile(continent, seaFraction);
        for (int i = 0; i < grid.Count; i++)
        {
            double land = Math.Clamp((continent[i] - shoreline) / CoastRamp, -1, 1);
            if (land <= 0)
            {
                relief.Sea[i] = relief.Outlet[i] = true;
                relief.Uplift[i] = 0;
                relief.Height[i] = -SeaFloorDepth * (1 - land);
                continue;
            }
            double ramp = WorldMap.Smooth(Math.Max(land, 0));
            relief.Uplift[i] = (relief.Uplift[i] + borderUplift[i]) * ramp;
            relief.Height[i] += relief.Uplift[i] * InitialRelief + 1;
            if (grid.IsEdge(i) && relief.SeaSides[EdgeSide(grid, i)])
            {
                // Where land reaches a sea border, the border itself is the shore.
                relief.Outlet[i] = true;
                relief.Uplift[i] = 0;
                relief.Height[i] = 0;
            }
        }
        double[] inland = DistanceFromOutlets(grid, relief.Outlet);
        for (int i = 0; i < grid.Count; i++)
            if (!relief.Sea[i]) relief.Height[i] += InlandRise * inland[i] / 1000;
        return relief;
    }

    /// <summary>
    /// Carry an evolved coarse relief onto a finer lattice: heights, uplift and hardness are
    /// interpolated, the coast follows the interpolated sea floor, and a little fresh texture
    /// lets the finer drainage organise itself during refinement.
    /// </summary>
    internal static MapRelief Refine(MapRelief coarse, MapGrid fine, ulong seed)
    {
        MapRelief relief = new(fine);
        Array.Copy(coarse.SeaSides, relief.SeaSides, relief.SeaSides.Length);
        MapGrid from = coarse.Grid;
        for (int i = 0; i < fine.Count; i++)
        {
            double x = fine.X(i), z = fine.Z(i);
            double height = from.Bilinear(coarse.Height, x, z);
            relief.Hardness[i] = from.Bilinear(coarse.Hardness, x, z);
            if (height < 0)
            {
                relief.Sea[i] = relief.Outlet[i] = true;
                relief.Height[i] = height;
                continue;
            }
            relief.Uplift[i] = from.Bilinear(coarse.Uplift, x, z);
            double texture = MapNoise.Fbm(seed ^ TextureSalt, x / TextureWavelength, z / TextureWavelength, TextureOctaves, FractalPersistence);
            relief.Height[i] = height + RefinedTexture * (1 + texture);
            if (fine.IsEdge(i) && relief.SeaSides[EdgeSide(fine, i)])
            {
                relief.Outlet[i] = true;
                relief.Uplift[i] = 0;
                relief.Height[i] = 0;
            }
        }
        return relief;
    }

    private void ChooseBorders(ulong seed)
    {
        for (int side = 0; side < 4; side++) SeaSides[side] = MapNoise.Unit(seed ^ BorderSalt, side, 1) < SeaSideChance;
        // A closed rim would have nowhere to drain; open the side the seed favours most.
        if (!SeaSides.Any(open => open))
        {
            int favoured = 0;
            for (int side = 1; side < 4; side++)
                if (MapNoise.Unit(seed ^ BorderSalt, side, 1) < MapNoise.Unit(seed ^ BorderSalt, favoured, 1)) favoured = side;
            SeaSides[favoured] = true;
        }
    }

    private static int EdgeSide(MapGrid grid, int i)
    {
        int x = i % grid.Side, z = i / grid.Side;
        return x == 0 ? 0 : x == grid.Segments ? 1 : z == 0 ? 2 : 3;
    }

    /// <summary>Eight-neighbour path distance in metres from the nearest outlet.</summary>
    private static double[] DistanceFromOutlets(MapGrid grid, bool[] outlet)
    {
        double[] distance = new double[grid.Count];
        Array.Fill(distance, double.PositiveInfinity);
        PriorityQueue<int, double> open = new();
        for (int i = 0; i < grid.Count; i++)
            if (outlet[i]) { distance[i] = 0; open.Enqueue(i, 0); }
        while (open.TryDequeue(out int c, out double d))
        {
            if (d > distance[c]) continue;
            for (int k = 0; k < 8; k++)
            {
                int n = grid.Neighbour(c, k);
                if (n < 0) continue;
                double next = d + grid.Spacing * MapGrid.NeighbourLength(k);
                if (next < distance[n]) { distance[n] = next; open.Enqueue(n, next); }
            }
        }
        return distance;
    }

    internal static double Quantile(double[] values, double fraction)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return sorted[Math.Clamp((int)(fraction * (sorted.Length - 1)), 0, sorted.Length - 1)];
    }
}
