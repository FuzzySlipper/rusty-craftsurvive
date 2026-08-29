namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Product policy for which deterministic chunks should be requested or kept.
/// It creates no Engine residency operation; a later adapter owns that bridge.
/// </summary>
internal sealed class TerrainResidencyPolicy
{
    private readonly TerrainRecipe recipe;
    private readonly TerrainChunkGenerator generator;

    internal TerrainResidencyPolicy(TerrainRecipe recipe, TerrainChunkGenerator generator)
    {
        this.recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }

    internal TerrainResidencyPlan PlanFor(TerrainChunkAddress center, TerrainOverlaySnapshot overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        TerrainChunkAddress[] requested = FindPopulatedChunks(center, TerrainConstants.RequestedChunkRadius, overlay);
        TerrainChunkAddress[] retained = FindPopulatedChunks(center, TerrainConstants.RetainedChunkRadius, overlay)
            .OrderBy(address => DistancePriority(address, center))
            .Take(TerrainConstants.MaximumResidentChunks)
            .ToArray();
        return new TerrainResidencyPlan(center, requested, retained, TerrainConstants.MaximumResidencyOperationsPerTick);
    }

    private TerrainChunkAddress[] FindPopulatedChunks(TerrainChunkAddress center, int radius, TerrainOverlaySnapshot overlay)
    {
        long minimumY = FloorDivide(recipe.MinimumMaterialY, TerrainConstants.ChunkEdgeLength);
        long maximumY = FloorDivide(recipe.MaximumMaterialY, TerrainConstants.ChunkEdgeLength);
        List<TerrainChunkAddress> chunks = [];
        for (long x = center.X - radius; x <= center.X + radius; x++)
        {
            for (long z = center.Z - radius; z <= center.Z + radius; z++)
            {
                for (long y = minimumY; y <= maximumY; y++)
                {
                    TerrainChunkAddress address = new(x, y, z);
                    if (generator.Generate(address, overlay).SolidVoxelCount > 0)
                    {
                        chunks.Add(address);
                    }
                }
            }
        }

        return chunks.OrderBy(address => DistancePriority(address, center)).ToArray();
    }

    private static (long HorizontalDistance, long Y, TerrainChunkAddress Address) DistancePriority(
        TerrainChunkAddress address, TerrainChunkAddress center)
    {
        long x = address.X - center.X;
        long z = address.Z - center.Z;
        return ((x * x) + (z * z), address.Y, address);
    }

    private static long FloorDivide(long value, int divisor)
    {
        long quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }
}

internal sealed class TerrainResidencyPlan
{
    internal TerrainResidencyPlan(TerrainChunkAddress center, TerrainChunkAddress[] requested,
        TerrainChunkAddress[] retained, int maximumOperationsPerTick)
    {
        Center = center;
        Requested = requested ?? throw new ArgumentNullException(nameof(requested));
        Retained = retained ?? throw new ArgumentNullException(nameof(retained));
        MaximumOperationsPerTick = maximumOperationsPerTick;
    }

    internal TerrainChunkAddress Center { get; }

    internal IReadOnlyList<TerrainChunkAddress> Requested { get; }

    internal IReadOnlyList<TerrainChunkAddress> Retained { get; }

    internal int MaximumOperationsPerTick { get; }
}
