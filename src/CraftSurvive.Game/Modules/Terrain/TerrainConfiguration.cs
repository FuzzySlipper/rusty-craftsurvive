namespace CraftSurvive.Game.Modules.Terrain;

internal enum TerrainSceneMode
{
    /// <summary>A generated cubic world: the shape the campaign ships.</summary>
    TraversalShowcase,

    /// <summary>An authored study scene, kept as an authoring and comparison lane.</summary>
    ExperimentalCourtyard,
}

/// <summary>
/// The one place the product's boot scene is chosen. S2 (#8598) flips
/// <see cref="Default"/> to the adventurer world and nothing else changes; every
/// other scene is reached explicitly through the environment selection below,
/// which is the authoring lane.
/// </summary>
internal static class TerrainSceneSelection
{
    internal const string EnvironmentVariable = "CRAFTSURVIVE_SCENE";

    /// <summary>
    /// The boot scene. The courtyard remains the default until S2 lands the world
    /// it switches to, because the traversal showcase is no longer the developer
    /// target and the adventurer world does not exist yet.
    /// </summary>
    internal const TerrainSceneMode Default = TerrainSceneMode.ExperimentalCourtyard;

    /// <summary>
    /// Explicit selection, used by the authoring lane and by proofs. Values are
    /// the scene names; an unknown value is a product configuration error rather
    /// than a silent fallback.
    /// </summary>
    internal static TerrainSceneMode FromEnvironment()
    {
        string? selected = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return selected?.Trim().ToLowerInvariant() switch
        {
            null or "" => Default,
            "courtyard" => TerrainSceneMode.ExperimentalCourtyard,
            "traversal" => TerrainSceneMode.TraversalShowcase,
            _ => throw new InvalidOperationException(
                $"{EnvironmentVariable} must be 'courtyard' or 'traversal'; received '{selected}'."),
        };
    }
}

internal readonly record struct TerrainConfiguration(ulong Seed, int Size, TerrainSceneMode Scene)
{
    /// <summary>
    /// Reads the product-owned startup selection. TerrainWorld captures this
    /// value once, so attached/restarted product state cannot change scenes.
    /// </summary>
    internal static TerrainConfiguration Default => TerrainSceneSelection.FromEnvironment() switch
    {
        TerrainSceneMode.ExperimentalCourtyard => ExperimentalCourtyard,
        TerrainSceneMode.TraversalShowcase => TraversalShowcase,
        _ => throw new InvalidOperationException("CraftSurvive selected an unsupported terrain scene."),
    };

    internal static TerrainConfiguration ExperimentalCourtyard => new(
        TerrainConstants.DefaultSeed,
        TerrainConstants.DefaultSize,
        TerrainSceneMode.ExperimentalCourtyard);

    /// <summary>A generated cubic world, selected explicitly for development and proofs.</summary>
    internal static TerrainConfiguration TraversalShowcase => new(
        TerrainConstants.DefaultSeed,
        TerrainConstants.DefaultSize,
        TerrainSceneMode.TraversalShowcase);

    internal TerrainConfiguration(ulong seed, int size)
        : this(seed, size, TerrainSceneMode.TraversalShowcase)
    {
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
