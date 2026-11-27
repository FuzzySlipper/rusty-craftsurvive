namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>A set of chunks grown by their neighbours: what a cave's shell keeps resident around the chunks holding its air.</summary>
internal static class ChunkNeighbours
{
    /// <summary>Chunks and every chunk touching one.</summary>
    internal static HashSet<TerrainChunkAddress> Beside(IEnumerable<TerrainChunkAddress> chunks)
    {
        HashSet<TerrainChunkAddress> beside = [];
        foreach (TerrainChunkAddress chunk in chunks)
        {
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            for (long dz = -1; dz <= 1; dz++)
            {
                beside.Add(new TerrainChunkAddress(chunk.X + dx, chunk.Y + dy, chunk.Z + dz));
            }
        }

        return beside;
    }
}
