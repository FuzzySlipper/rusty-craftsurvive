using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>Live-debug adapters over wildlife (#10567); it owns no state of its own.</summary>
public sealed class WildlifeDebugModule : IDebugCommandModule
{
    private readonly Func<WildlifeModule> wildlifeSource;
    private readonly Func<string> navigationCost;
    private WildlifeModule wildlife => wildlifeSource();

    internal WildlifeDebugModule(Func<WildlifeModule> wildlife, Func<string> navigationCost)
    {
        wildlifeSource = wildlife ?? throw new ArgumentNullException(nameof(wildlife));
        this.navigationCost = navigationCost ?? throw new ArgumentNullException(nameof(navigationCost));
    }

    [DebugCommand("craft.wildlife.readout", Description = "Wildlife (#10567): awake herds with their activity, alarm and each animal's wariness, and the update cost since the last readout (reset by reading).")]
    public string Readout() => wildlife.Readout();

    [DebugCommand("craft.wildlife.spawn", Description = "Wildlife (#10567): wake a herd of a kind just beyond its notice distance east of the player; it joins no region's population.")]
    public string Spawn(string kind) => wildlife.Spawn(kind);

    [DebugCommand("craft.wildlife.spawnat", Description = "Wildlife (#10567): wake a herd of a kind metres east and south of the player.")]
    public string SpawnAt(string kind, double east, double south) => wildlife.Spawn(kind, east, south);

    [DebugCommand("craft.wildlife.herd", Description = "Wildlife (#10569): wake a herd of a kind and a size metres east and south of the player, for cost runs.")]
    public string Herd(string kind, long size, double east, double south) => wildlife.Spawn(kind, east, south, (int)size);

    [DebugCommand("craft.wildlife.fleeing", Description = "Test aid (#10569): wake a herd that flees when pressed, whatever its kind does, to see a kind's run.")]
    public string Fleeing(string kind, long size, double east, double south) => wildlife.Spawn(kind, east, south, (int)size, PressedResponse.Flee);

    [DebugCommand("craft.wildlife.region", Description = "Wildlife (#10567): the population ledger - each seeded region's biomes and herds, with how many each has left now.")]
    public string Region() => wildlife.RegionReadout();

    [DebugCommand("craft.wildlife.find", Description = "Wildlife (#10567): the nearest seeded herds of a kind within some kilometres, with their homes and biome; it seeds the regions it searches.")]
    public string Find(string kind, double kilometres) => wildlife.Find(kind, kilometres);

    [DebugCommand("craft.wildlife.country", Description = "Wildlife (#10570): the nearest ground of a biome within some kilometres, to see a kind in its own country.")]
    public string Country(string biome, double kilometres) => wildlife.Country(biome, kilometres);

    [DebugCommand("craft.wildlife.sleep", Description = "Wildlife (#10567): put every awake herd to sleep; the ledger keeps them, and they wake again where their range puts them.")]
    public string Sleep() => wildlife.SleepAll();

    [DebugCommand("craft.wildlife.navcost", Description = "Wildlife (#10567): what one collision-navigation projection costs for each size class the kinds use, over the creatures' box; the creatures' own is published again on their next pursuit.")]
    public string NavigationCost() => navigationCost();
}
