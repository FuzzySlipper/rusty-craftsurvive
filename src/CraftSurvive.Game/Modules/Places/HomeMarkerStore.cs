using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>
/// Owns the expedition's home marker. Absent a save, home is where the world's spawn stands; the
/// player moves it from the map, and it is saved at once.
/// </summary>
internal sealed class HomeMarkerStore
{
    private readonly ProductSaveSlot<HomeMarker> slot;

    internal HomeMarkerStore(IEngineContext engine, ProductStore store, SaveIdentity identity, Vector2 spawn)
    {
        slot = new ProductSaveSlot<HomeMarker>(engine, store, SaveManifest.TravelHome, new HomeMarkerCodec(identity));
        Home = new(spawn.X, spawn.Y);
    }

    internal HomeMarker Home { get; private set; }

    internal void Start()
    {
        if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: HomeMarker saved }) Home = saved;
    }

    internal void Set(Vector2 position)
    {
        Home = new(position.X, position.Y);
        slot.Save(Home);
    }

    internal string Readout => string.Create(CultureInfo.InvariantCulture,
        $"home={Home.X:F0},{Home.Z:F0} restore={slot.RestoreOutcome} saves={slot.Saves} failure={slot.LastFailure ?? "none"}");
}
