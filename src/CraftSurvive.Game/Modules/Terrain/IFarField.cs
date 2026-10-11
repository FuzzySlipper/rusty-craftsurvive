namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The land beyond the drawn chunks, as the walking world drives it: a dual-contoured voxel session
/// (<see cref="FarField"/>) or an Engine heightfield (<see cref="FarFieldHeightfield"/>, #9850).
/// </summary>
internal interface IFarField : IDisposable
{
    /// <summary>How many far chunk columns (<see cref="FarField.ChunkMetres"/>) it keeps each way: the view distance (#9759).</summary>
    int RadiusChunks { get; set; }

    /// <summary>The near ground's edge, in metres from the player: the sunk zone keeps short of it.</summary>
    double NearEdgeMetres { set; }

    /// <summary>Keeps it centred on a world column, and the sunk zone under the player.</summary>
    void Follow(long worldX, long worldZ);

    /// <summary>Applies a bounded amount of its streaming work this update.</summary>
    void Advance();

    string Readout();
}
