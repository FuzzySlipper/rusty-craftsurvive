using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.WorldGen;

internal readonly record struct LandscapeStudy(string Id, double CentreX, double CentreZ);

/// <summary>Three authored, walkable terrain studies within the streamed world; not a biome system.</summary>
internal static class LandscapeStudies
{
    internal const double Radius = 96;
    private const double CoreRadius = 64;
    internal const double ArrivalOffsetZ = 20;
    internal const double ArrivalClearance = 4;
    internal const float ArrivalPitch = -0.10f;
    private const double BaseHeight = 4;
    private const double CanyonHeight = 14;
    private const double CanyonHalfWidth = 5;
    private const double CanyonWallWidth = 9;
    private const double CanyonBend = 4;
    private const double CanyonBendLength = 15;
    private const double RollingLength = 13;
    private const double RollingHeight = 3;
    private const double OutcropHeight = 9;
    private const double OutcropWidth = 7;
    private const double OutcropX = -14;
    private const double OutcropZ = -12;
    private const double FrostRidgeHeight = 11;
    private const double FrostRidgeWidth = 8;
    private const double FrostRidgeX = -12;
    private const double FrostRidgeSlant = 0.25;
    private const double FloorRippleHeight = 0.6;
    private const double FloorRippleLength = 6;
    private const double RockThreshold = 0.24;
    private const double FrostBaseHeight = 7;
    // Separate banks keep fine relief independent of the amplitude budget of large hills.
    // Each band's finest lattice is at least three metres on the one-metre voxel grid.
    internal const int MaximumHeight = 32;
    private const double CanyonFloorDetail = 0.12;
    private const double UplandSoilDetail = 0.45;
    private const double SecondaryOutcropWeight = 0.7;
    private const double SecondaryOutcropOffsetX = 35;
    private const double SecondaryOutcropOffsetZ = 16;
    private const double FrostShoulderOffset = 32;
    private const double FrostShoulderWeight = 0.5;
    private const ulong DetailSeed = 0x947A_452C_018D_B3E1UL;
    private const ulong RegionalSeed = 0x37FA_80D6_1879_B2C5UL;
    private const ulong FineSeed = 0x752B_3981_AAF6_120DUL;
    private const ulong WarpXSeed = 0xA397_B810_716C_52D3UL;
    private const ulong WarpZSeed = 0xC10B_5984_235A_9E77UL;
    private static readonly NoiseBand Regional = new(48, 3, 0.6, 7, RegionalSeed);
    private static readonly NoiseBand Structure = new(12, 3, 0.65, 5, DetailSeed);
    private static readonly NoiseBand Surface = new(6, 2, 0.7, 1.2, FineSeed);
    private static readonly NoiseBand WarpX = new(32, 3, 0.5, 8, WarpXSeed);
    private static readonly NoiseBand WarpZ = new(32, 3, 0.5, 8, WarpZSeed);

    // A future generated map can supply this geographic anchor. Local detail consumes it;
    // it does not decide the drainage or move the broad canyon/ridge to another location.
    private readonly record struct Geography(double Elevation, double Rock, double DetailWeight);
    private readonly record struct NoiseBand(int Scale, int Octaves, double Persistence, double Amplitude, ulong Seed)
    {
        internal double At(double x, double z)
        {
            const int Lacunarity = 2;
            double sum = 0, weight = 1, totalWeight = 0;
            int scale = Scale;
            for (int octave = 0; octave < Octaves; octave++)
            {
                ulong salt = unchecked((ulong)octave * GenerationConstants.CoordinateXMultiplier);
                sum += weight * (TerrainRecipe.ValueNoise(Seed ^ salt, x, z, scale) * 2 - 1);
                totalWeight += weight;
                weight *= Persistence;
                scale /= Lacunarity;
            }
            return Amplitude * sum / totalWeight;
        }
    }

    internal static readonly LandscapeStudy[] All =
    [
        new("canyon", 256, 256),
        new("uplands", 512, 256),
        new("tundra", 768, 256),
    ];

    internal static LandscapeStudy? Find(string id)
    {
        foreach (LandscapeStudy study in All)
            if (study.Id == id) return study;
        return null;
    }

    internal static LandscapeStudy? At(double x, double z)
    {
        foreach (LandscapeStudy study in All)
            if (Math.Abs(x - study.CentreX) < Radius && Math.Abs(z - study.CentreZ) < Radius) return study;
        return null;
    }

    internal static double Height(double x, double z, double outside)
    {
        if (At(x, z) is not LandscapeStudy study) return outside;
        double localX = x - study.CentreX, localZ = z - study.CentreZ;
        double blend = Smooth((Radius - Math.Max(Math.Abs(localX), Math.Abs(localZ))) / (Radius - CoreRadius));
        return outside + (Shape(study.Id, localX, localZ) - outside) * blend;
    }

    internal static BlockId Material(LandscapeStudy study, double x, double z)
    {
        Geography geography = Anchor(study.Id, x - study.CentreX, z - study.CentreZ);
        return study.Id switch
        {
            "canyon" => geography.Rock > RockThreshold ? BlockId.Stone : BlockId.Sand,
            "uplands" => geography.Rock > RockThreshold ? BlockId.Stone : BlockId.Grass,
            _ => BlockId.Snow,
        };
    }

    private static double Shape(string id, double x, double z)
    {
        Geography geography = Anchor(id, x, z);
        // Ridged structure has different statistics from the broad height bank. Fine relief
        // gets its own budget rather than being the almost-invisible tail of that bank.
        double structure = Structure.At(x, z);
        double ridge = Structure.Amplitude * (1 - 2 * Math.Abs(structure / Structure.Amplitude));
        double fractured = (structure + ridge) * 0.5;
        return geography.Elevation
            + geography.DetailWeight * fractured
            + Math.Sqrt(geography.DetailWeight) * Surface.At(x, z);
    }

    private static Geography Anchor(string id, double x, double z)
    {
        double warpedX = x + WarpX.At(x, z), warpedZ = z + WarpZ.At(x, z);
        double regional = Regional.At(warpedX, warpedZ);
        if (id == "canyon")
        {
            double wall = CanyonWall(warpedX, warpedZ);
            double detail = CanyonFloorDetail + (1 - CanyonFloorDetail) * wall;
            return new(BaseHeight + CanyonHeight * wall + regional * detail
                + FloorRippleHeight * Math.Sin(z / FloorRippleLength), wall, detail);
        }
        if (id == "uplands")
        {
            double rock = Math.Max(Outcrop(warpedX, warpedZ), SecondaryOutcropWeight
                * Outcrop(warpedX - SecondaryOutcropOffsetX, warpedZ + SecondaryOutcropOffsetZ));
            return new(BaseHeight + RollingHeight * Math.Sin(warpedX / RollingLength) * Math.Cos(warpedZ / RollingLength)
                + OutcropHeight * rock + regional, rock,
                UplandSoilDetail + (1 - UplandSoilDetail) * rock);
        }
        double ridge = Math.Max(FrostRidge(warpedX, warpedZ),
            FrostShoulderWeight * FrostRidge(warpedX - FrostShoulderOffset, warpedZ));
        return new(FrostBaseHeight + RollingHeight * Math.Sin(warpedZ / RollingLength)
            + FrostRidgeHeight * ridge + regional, ridge, 1);
    }

    private static double FrostRidge(double x, double z) =>
        Math.Exp(-Math.Pow((x - FrostRidgeX - z * FrostRidgeSlant) / FrostRidgeWidth, 2));

    private static double CanyonWall(double x, double z) =>
        Smooth((Math.Abs(x - CanyonBend * Math.Sin(z / CanyonBendLength)) - CanyonHalfWidth) / CanyonWallWidth);

    private static double Outcrop(double x, double z) =>
        Math.Exp(-(Math.Pow((x - OutcropX) / OutcropWidth, 2) + Math.Pow((z - OutcropZ) / OutcropWidth, 2)));

    private static double Smooth(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
