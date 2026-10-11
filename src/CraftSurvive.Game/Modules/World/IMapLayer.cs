namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// One resolution of the map's ground, streamed as chunk columns of <see cref="MapVoxelLayer.EdgeLength"/>
/// cells from the owner's sampler: as a dual-contoured voxel session (<see cref="MapVoxelLayer"/>) or as
/// heightfield grid meshes (<see cref="MapMeshLayer"/>, #9839).
/// </summary>
internal interface IMapLayer : IDisposable
{
    /// <summary>Make exactly these chunk columns resident.</summary>
    void Want(IEnumerable<(long X, long Z)> chunkColumns);

    /// <summary>Sample these chunk columns again.</summary>
    void Invalidate(IEnumerable<(long X, long Z)> chunkColumns);

    /// <summary>Sample waiting columns within the layer's budget and apply up to <paramref name="budget"/> pieces of work.</summary>
    void Advance(int budget);

    /// <summary>The chunk column sampling works outward from.</summary>
    (long X, long Z) Focus { get; set; }

    bool Settled { get; }
    int ResidentChunks { get; }
    int PendingChunks { get; }
    double WorkMilliseconds { get; }

    /// <summary>A digest of the sampled columns, to compare a layer that followed the player with one built fresh.</summary>
    ulong Digest();
}
