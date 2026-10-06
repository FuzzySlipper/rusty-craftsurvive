using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The world this run generates: its seed, its extent and the generator version. The product has
/// one scene - the generated world - and this is its identity.
/// </summary>
internal readonly record struct TerrainConfiguration(ulong Seed, int Size, uint GeneratorVersion)
{
    /// <summary>The starting world when no compatible map has been saved.</summary>
    internal static TerrainConfiguration Default => new(
        TerrainConstants.DefaultSeed,
        TerrainConstants.DefaultSize,
        TerrainGeneratorContract.CurrentVersion);

    internal TerrainConfiguration(ulong seed, int size)
        : this(seed, size, TerrainGeneratorContract.CurrentVersion)
    {
    }

    internal TerrainConfiguration Validate()
    {
        if (Size < TerrainConstants.MinimumSize || Size > TerrainConstants.MaximumSize || !WorldGen.MapScale.IsValidSize(Size))
        {
            throw new ArgumentOutOfRangeException(nameof(Size), Size,
                $"Terrain size must be a regional {TerrainConstants.MinimumSize}..={WorldGen.MapScale.RegionalMaximumSize} or a continental {WorldGen.MapScale.ContinentalMinimumSize}..={WorldGen.MapScale.ContinentalMaximumSize}.");
        }

        if ((Size & 1) != 0)
        {
            throw new ArgumentException("Terrain size must be even.", nameof(Size));
        }

        return this;
    }

    internal TerrainRecipe CreateRecipe(ITerrainDraws draws) => new(this.Validate(), draws);

    internal TerrainRecipe CreateRecipe(ITerrainDraws draws, WorldMap map) => new(this.Validate(), draws, map);

    /// <summary>The versioned identity generation draws from.</summary>
    internal TerrainGeneratorContract Contract => new(Seed, GeneratorVersion, Size);
}
