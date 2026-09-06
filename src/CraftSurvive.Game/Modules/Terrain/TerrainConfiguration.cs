namespace CraftSurvive.Game.Modules.Terrain;

internal enum TerrainSceneMode
{
    TraversalShowcase,
    ExperimentalCourtyard,
}

internal readonly record struct TerrainConfiguration(ulong Seed, int Size, TerrainSceneMode Scene)
{
    private const string SceneEnvironmentVariable = "CRAFTSURVIVE_SCENE";

    /// <summary>
    /// Reads the product-owned startup selection. TerrainWorld captures this
    /// value once, so attached/restarted product state cannot change scenes.
    /// </summary>
    internal static TerrainConfiguration Default => ReadSceneEnvironment() switch
    {
        TerrainSceneMode.ExperimentalCourtyard => ExperimentalCourtyard,
        TerrainSceneMode.TraversalShowcase => TraversalShowcase,
        _ => throw new InvalidOperationException("CraftSurvive selected an unsupported terrain scene."),
    };

    internal static TerrainConfiguration ExperimentalCourtyard => new(
        TerrainConstants.DefaultSeed,
        TerrainConstants.DefaultSize,
        TerrainSceneMode.ExperimentalCourtyard);

    /// <summary>Retained comparison route for existing terrain and renderer exercises.</summary>
    internal static TerrainConfiguration TraversalShowcase => new(
        TerrainConstants.DefaultSeed,
        TerrainConstants.DefaultSize,
        TerrainSceneMode.TraversalShowcase);

    internal TerrainConfiguration(ulong seed, int size)
        : this(seed, size, TerrainSceneMode.TraversalShowcase)
    {
    }

    private static TerrainSceneMode ReadSceneEnvironment()
    {
        string? selected = Environment.GetEnvironmentVariable(SceneEnvironmentVariable);
        return selected?.Trim().ToLowerInvariant() switch
        {
            null or "" or "courtyard" => TerrainSceneMode.ExperimentalCourtyard,
            "traversal" => TerrainSceneMode.TraversalShowcase,
            _ => throw new InvalidOperationException(
                $"{SceneEnvironmentVariable} must be 'courtyard' or 'traversal'; received '{selected}'."),
        };
    }

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

        if (!Enum.IsDefined(Scene))
        {
            throw new ArgumentOutOfRangeException(nameof(Scene), Scene, "Terrain scene mode is not supported.");
        }

        return this;
    }

    internal TerrainRecipe CreateRecipe() => new(this.Validate());
}
