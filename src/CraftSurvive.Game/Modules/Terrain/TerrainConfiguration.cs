namespace CraftSurvive.Game.Modules.Terrain;

internal readonly record struct TerrainConfiguration(ulong Seed, int Size)
{
    internal static TerrainConfiguration Default => new(TerrainConstants.DefaultSeed, TerrainConstants.DefaultSize);

    internal TerrainConfiguration Validate()
    {
        if (Size < TerrainConstants.MinimumSize || Size > TerrainConstants.MaximumSize)
        {
            throw new ArgumentOutOfRangeException(nameof(Size), Size,
                $"Terrain size must be within {TerrainConstants.MinimumSize}..={TerrainConstants.MaximumSize}.");
        }

        if ((Size & 1) != 0)
        {
            throw new ArgumentException("Terrain size must be even.", nameof(Size));
        }

        return this;
    }

    internal TerrainRecipe CreateRecipe() => new(this.Validate());
}
