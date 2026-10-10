namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The tectonic starting state that erosion works on: where land is, how fast each place is
/// lifted, and how resistant its rock is. Nothing here is a fixed template; every landform is
/// placed by seeded noise in metre space, and the finite border is either open sea or a
/// mountain rim chosen per side.
/// </summary>
internal sealed class MapRelief
{
    // Tuning is the recipe's (ReliefRecipe); the salts keep each noise field independent.
    /// <summary>A designed belt keeps this share of its crest where the ridge noise is lowest, so the drawn range is never lost.</summary>
    private const double DesignedRidgeFloor = 0.6;
    /// <summary>A designed coast's noise: its octaves (down to bays a few kilometres across) and salt.</summary>
    private const int CoastOctaves = 6;
    private const ulong CoastSalt = 0x2F2B8C3E91D5A7C3UL;
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

    /// <summary>
    /// A relief from given heights in metres, for a region tile's local erosion (#9550): no uplift, the
    /// given rock resistance, and the given sea and base-level nodes.
    /// </summary>
    internal static MapRelief FromHeights(MapGrid grid, double[] height, double[] hardness, bool[] sea, bool[] outlet)
    {
        MapRelief relief = new(grid);
        Array.Copy(height, relief.Height, height.Length);
        Array.Copy(hardness, relief.Hardness, hardness.Length);
        Array.Copy(sea, relief.Sea, sea.Length);
        Array.Copy(outlet, relief.Outlet, outlet.Length);
        return relief;
    }

    /// <param name="design">
    /// A drawn continent (#9815): where given, it decides where land is and where ranges rise, and the
    /// noise only roughens and varies them; null keeps the seeded geography.
    /// </param>
    internal static MapRelief Build(MapGrid grid, ulong seed, ReliefRecipe r, ContinentDesign? design = null)
    {
        // Lengths were tuned on regional worlds; a continent scales them all (#9549).
        double l = MapScale.For(grid).Lengths;
        double WarpWavelength = r.WarpWavelength * l, WarpDistance = r.WarpDistance * l;
        double ContinentWavelength = r.ContinentWavelength * l, BeltWavelength = r.BeltWavelength * l;
        double ActivityWavelength = r.ActivityWavelength * l, HardnessWavelength = r.HardnessWavelength * l;
        double TextureWavelength = r.TextureWavelength * l, UplandWavelength = r.UplandWavelength * l;
        double CentreReserveRadius = r.CentreReserveRadius * l;
        double MaximumSeaReach = r.MaximumSeaReach * l, MaximumRangeReach = r.MaximumRangeReach * l;
        MapRelief relief = new(grid);
        // A design draws its own coasts; a map edge it leaves as land is a mountain rim, not an outlet.
        if (design is null) relief.ChooseBorders(seed, r.SeaSideChance);
        double highestCrest = Math.Max(design?.HighestCrest ?? 0, 1);
        double seaReach = Math.Min(MaximumSeaReach, grid.Radius * 2 * r.SeaReachFraction);
        double rangeReach = Math.Min(MaximumRangeReach, grid.Radius * 2 * r.RangeReachFraction);
        double[] continent = new double[grid.Count];
        double[] borderUplift = new double[grid.Count];

        // A designed node: land from the drawn outline (roughened by the continent noise), uplift from the
        // belts' crests (textured by the belt noise) and the drawn uplands and basins.
        void Designed(MapRelief relief, ContinentDesign design, int i, double u, double v, double noise, double wx, double wz)
        {
            double coastX = u / design.CoastWavelength, coastZ = v / design.CoastWavelength;
            double coast = MapNoise.Fbm(seed ^ CoastSalt, coastX + (0.5 * noise), coastZ - (0.5 * noise), CoastOctaves, r.FractalPersistence);
            continent[i] = (design.LandDistance(u, v) + (design.CoastRoughness * coast)) / design.CoastRamp;
            double ridges = MapNoise.Ridged(seed ^ BeltSalt, (u / design.RidgeWavelength) + (0.3 * noise), (v / design.RidgeWavelength) - (0.3 * noise), r.BeltOctaves, r.BeltPersistence);
            double crest = design.Crest(u, v) / highestCrest;
            double upland = r.UplandUplift * (0.5 + 0.5 * MapNoise.Fbm(seed ^ UplandSalt, wx / UplandWavelength, wz / UplandWavelength, 3, r.FractalPersistence));
            relief.Uplift[i] = Math.Max(0, (r.PlainUplift + upland) * (1 + design.AreaLift(u, v)) + (r.BeltUplift * crest * (DesignedRidgeFloor + ((1 - DesignedRidgeFloor) * ridges))));
            relief.Hardness[i] = WorldMap.Smooth(Math.Clamp(0.5 + r.HardnessContrast * MapNoise.Fbm(seed ^ HardnessSalt,
                wx / HardnessWavelength, wz / HardnessWavelength, 3, r.FractalPersistence), 0, 1));
            relief.Height[i] = MapNoise.Fbm(seed ^ TextureSalt, u * grid.Radius / TextureWavelength, v * grid.Radius / TextureWavelength, r.TextureOctaves, r.FractalPersistence) * r.InitialTexture;
        }

        for (int i = 0; i < grid.Count; i++)
        {
            double x = grid.X(i), z = grid.Z(i);
            double wx = x + WarpDistance * MapNoise.Fbm(seed ^ WarpXSalt, x / WarpWavelength, z / WarpWavelength, 3, r.FractalPersistence);
            double wz = z + WarpDistance * MapNoise.Fbm(seed ^ WarpZSalt, x / WarpWavelength, z / WarpWavelength, 3, r.FractalPersistence);
            double c = MapNoise.Fbm(seed ^ ContinentSalt, wx / ContinentWavelength, wz / ContinentWavelength, r.ContinentOctaves, r.FractalPersistence);
            if (design is not null)
            {
                Designed(relief, design, i, x / grid.Radius, z / grid.Radius, c, wx, wz);
                continue;
            }

            double centre = Math.Exp(-(x * x + z * z) / (CentreReserveRadius * CentreReserveRadius));
            c += r.CentreLandBias * centre;
            // Distance inside each border, west/east/north/south.
            ReadOnlySpan<double> inside = [x + grid.Radius, grid.Radius - x, z + grid.Radius, grid.Radius - z];
            double wobble = 1 + r.BorderWobble * MapNoise.Fbm(seed ^ WobbleSalt, x / WarpWavelength, z / WarpWavelength, 3, r.FractalPersistence);
            for (int side = 0; side < 4; side++)
            {
                double reach = relief.SeaSides[side] ? seaReach : rangeReach;
                double near = 1 - WorldMap.Smooth(Math.Clamp(inside[side] * wobble / reach, 0, 1));
                if (relief.SeaSides[side]) c -= r.SeaBorderStrength * near;
                else
                {
                    c += r.RangeBorderLand * near;
                    borderUplift[i] = Math.Max(borderUplift[i], r.RangeBorderUplift * near);
                }
            }
            continent[i] = c;

            double belt = Math.Pow(MapNoise.Ridged(seed ^ BeltSalt, wx / BeltWavelength, wz / BeltWavelength, r.BeltOctaves, r.BeltPersistence), r.BeltSharpness);
            double activity = WorldMap.Smooth(Math.Clamp((MapNoise.Fbm(seed ^ ActivitySalt, x / ActivityWavelength, z / ActivityWavelength, 3, r.FractalPersistence)
                - r.ActivityFloor) / (r.ActivityCeiling - r.ActivityFloor), 0, 1));
            double upland = r.UplandUplift * (0.5 + 0.5 * MapNoise.Fbm(seed ^ UplandSalt, wx / UplandWavelength, wz / UplandWavelength, 3, r.FractalPersistence));
            relief.Uplift[i] = (r.PlainUplift + upland + r.BeltUplift * belt * activity) * (1 - r.CentreCalm * centre);
            relief.Hardness[i] = WorldMap.Smooth(Math.Clamp(0.5 + r.HardnessContrast * MapNoise.Fbm(seed ^ HardnessSalt,
                wx / HardnessWavelength, wz / HardnessWavelength, 3, r.FractalPersistence), 0, 1));
            double texture = MapNoise.Fbm(seed ^ TextureSalt, x / TextureWavelength, z / TextureWavelength, r.TextureOctaves, r.FractalPersistence);
            relief.Height[i] = texture * r.InitialTexture;
        }

        double seaFraction = r.MinimumSeaFraction + r.SeaFractionRange * MapNoise.Unit(seed ^ SeaFractionSalt, 0, 0);
        // A design's land field is already in coast-ramp units about its drawn shore.
        double shoreline = design is null ? Quantile(continent, seaFraction) : 0;
        double coastRamp = design is null ? r.CoastRamp : 1;
        for (int i = 0; i < grid.Count; i++)
        {
            double land = Math.Clamp((continent[i] - shoreline) / coastRamp, -1, 1);
            if (land <= 0)
            {
                relief.Sea[i] = relief.Outlet[i] = true;
                relief.Uplift[i] = 0;
                relief.Height[i] = -r.SeaFloorDepth * (1 - land);
                continue;
            }
            double ramp = WorldMap.Smooth(Math.Max(land, 0));
            relief.Uplift[i] = (relief.Uplift[i] + borderUplift[i]) * ramp;
            relief.Height[i] += relief.Uplift[i] * r.InitialRelief + 1;
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
            if (!relief.Sea[i]) relief.Height[i] += r.InlandRise * inland[i] / (1000 * l);
        return relief;
    }

    /// <summary>
    /// Carry an evolved coarse relief onto a finer lattice: heights, uplift and hardness are
    /// interpolated, the coast follows the interpolated sea floor, and a little fresh texture
    /// lets the finer drainage organise itself during refinement.
    /// </summary>
    internal static MapRelief Refine(MapRelief coarse, MapGrid fine, ulong seed, ReliefRecipe r)
    {
        double TextureWavelength = r.TextureWavelength * MapScale.For(fine).Lengths;
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
            double texture = MapNoise.Fbm(seed ^ TextureSalt, x / TextureWavelength, z / TextureWavelength, r.TextureOctaves, r.FractalPersistence);
            relief.Height[i] = height + r.RefinedTexture * (1 + texture);
            if (fine.IsEdge(i) && relief.SeaSides[EdgeSide(fine, i)])
            {
                relief.Outlet[i] = true;
                relief.Uplift[i] = 0;
                relief.Height[i] = 0;
            }
        }
        return relief;
    }

    private void ChooseBorders(ulong seed, double seaSideChance)
    {
        for (int side = 0; side < 4; side++) SeaSides[side] = MapNoise.Unit(seed ^ BorderSalt, side, 1) < seaSideChance;
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
