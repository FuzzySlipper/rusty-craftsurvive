using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

internal static class DensityChecks
{
    internal static void Run()
    {
        TerrainConfiguration config = TerrainConfiguration.Default;
        TerrainRecipe recipe = config.CreateRecipe(new TestDraws(config.Seed));
        TerrainChunkGenerator generator = new(recipe);
        TerrainOverlaySnapshot pristine = new(config.Seed, []);
        TerrainChunkAddress[] addresses = [new(-1, 0, 0), new(0, 0, 0), new(1, 0, 0), new(0, -1, 0)];
        Dictionary<TerrainChunkAddress, TerrainChunk> first = addresses.ToDictionary(a => a, a => generator.Generate(a, pristine));
        foreach (TerrainChunkAddress address in addresses.Reverse())
        {
            TerrainChunk chunk = generator.Generate(address, pristine);
            Check.That(chunk.Densities.Span.SequenceEqual(first[address].Densities.Span), "density is independent of chunk generation order");
            for (int i = 0; i < chunk.Materials.Length; i++)
            {
                Check.That(float.IsFinite(chunk.Densities.Span[i])
                    && (chunk.Densities.Span[i] < 0f) == (chunk.Materials.Span[i] != 0), "density sign agrees with occupancy");
            }
        }

        // A clear and a replacement retain the original magnitude, so material-only saved edits
        // can rebuild the same field as the Engine's live edit path.
        TerrainChunk original = first[addresses[1]];
        int editedIndex = Enumerable.Range(0, original.Materials.Length)
            .First(i => TerrainDensity.IsGround(original.Materials.Span[i]));
        int edge = TerrainConstants.ChunkEdgeLength;
        VoxelAddress cell = new(editedIndex % edge, editedIndex / edge % edge, editedIndex / (edge * edge));
        TerrainChunk cleared = generator.Generate(addresses[1], new(config.Seed, [new(cell, (ushort)BlockId.Air)]));
        TerrainChunk built = generator.Generate(addresses[1], new(config.Seed, [new(cell, (ushort)BlockId.Brick)]));
        Check.Equal(-original.Densities.Span[editedIndex], cleared.Densities.Span[editedIndex], "clear preserves the generated density magnitude");
        Check.Equal(original.Densities.Span[editedIndex], built.Densities.Span[editedIndex], "replacement preserves generated density magnitude");
        Check.That(original.Densities.Span.ToArray().Any(d => Math.Abs(d) != 0.5f), "terrain carries a scalar field, not binary cube densities");

        // Authored study terrain must not escape the streamer's vertical bounds or disappear
        // when its occupancy predicate suppresses the ordinary structures and trees.
        foreach (LandscapeStudy study in LandscapeStudies.All)
        {
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            for (int z = -(int)LandscapeStudies.Radius; z <= LandscapeStudies.Radius; z++)
            for (int x = -(int)LandscapeStudies.Radius; x <= LandscapeStudies.Radius; x++)
            {
                double height = recipe.ContinuousHeightAt((long)study.CentreX + x, (long)study.CentreZ + z);
                minimum = Math.Min(minimum, height);
                maximum = Math.Max(maximum, height);
            }
            Check.That(double.IsFinite(minimum) && double.IsFinite(maximum)
                && minimum > recipe.MinimumMaterialY + GenerationConstants.WorldFloorThickness
                && maximum <= recipe.MaximumMaterialY,
                "all study columns fit the terrain residency bounds without clipping against the world floor");
            Console.WriteLine($"{study.Id} height range: {minimum:F2} to {maximum:F2} metres.");
            foreach (int offset in new[] { -6, -1, 0, 5, 6 })
            {
                for (int y = 0; y <= 2; y++)
                {
                    TerrainChunkAddress address = new((long)study.CentreX / edge + offset, y, (long)study.CentreZ / edge);
                    TerrainChunk chunk = generator.Generate(address, pristine);
                    Check.That(recipe.ChunkHasContent(address) || chunk.SolidVoxelCount == 0,
                        "study residency never rejects a populated chunk");
                    Check.That(address.Origin.Y <= recipe.MaximumMaterialY || chunk.SolidVoxelCount == 0,
                        "study content fits the generation height bound");
                }
            }
        }
    }
}
