using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Towns;

/// <summary>Live checks of the expedition's towns (#9895); selection uses the shared interaction module.</summary>
public sealed class TownDebugModule(Func<string, string> enter, Func<string> leave, Func<string> read,
    Func<string, string> motion, Func<string> sites, Func<string, string> approach,
    Func<string, string> select, Func<string, string, string> choose) : IDebugCommandModule
{
    [DebugCommand("craft.town.select", Description = "In a visit, open a service (inn|provisioner) as clicking its door does; \"back\" returns to the overview.")]
    public string Select(string service) => select(service);

    [DebugCommand("craft.town.choose", Description = "In a service, take a choice by key as the menu does (real actions apply); then reads the conversation.")]
    public string Choose(string service, string choice) => choose(service, choice);

    [DebugCommand("craft.town.sites", Description = "This world's towns: key, kind, seat, pad height, facing toward the water and recipe.")]
    public string Sites() => sites();

    [DebugCommand("craft.town.approach", Description = "Stand at a town's arrival view (by key or name), outside its bubble, without entering.")]
    public string Approach(string town) => approach(town);

    [DebugCommand("craft.town.enter", Description = "Enter the town offered here (doorstep), as Enter does, or a town by key or name from its arrival view.")]
    public string Enter(string town) => enter(town);

    [DebugCommand("craft.town.leave", Description = "Leave the town view; the traveller stands at its arrival view again.")]
    public string Leave() => leave();

    [DebugCommand("craft.town.read", Description = "Visit identity, town and recipe, selection, camera, conditions, doorstep offer and the standing town's resources.")]
    public string Read() => read();

    [DebugCommand("craft.town.motion", Description = "Compare the still camera (off) and eased pointer response (on).")]
    public string Motion(string state) => motion(state);
}
