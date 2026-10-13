using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>The river stage's save slot in the running product (#9878 review).</summary>
internal sealed class RiverStageStore(Rusty.Engine.IEngineContext engine, ProductStore store, SaveIdentity identity) : IRiverStageArchive
{
    private readonly ProductSaveSlot<RiverStageState> slot = new(engine, store, SaveManifest.WaterStage, new RiverStageCodec(identity));

    public RiverStageState? Restore() => slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: RiverStageState saved } ? saved : null;

    public void Save(RiverStageState state) => slot.Save(state);
}
