namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// The river stage's stores at an hour (#9878 review), as saved: each reach's storage (m³) and frost
/// (degree-hours), each cell's snow and ground water. With the world's calibration they are the whole
/// stage: the flow is the storage over the reach's storage time.
/// </summary>
internal sealed record RiverStageState(double Hours, double[] Storage, double[] Frost, double[] Snow, double[] Ground);

/// <summary>Where the river stage is kept between sessions (#9878 review): the product's save slot, or nothing.</summary>
internal interface IRiverStageArchive
{
    RiverStageState? Restore();

    void Save(RiverStageState state);
}
