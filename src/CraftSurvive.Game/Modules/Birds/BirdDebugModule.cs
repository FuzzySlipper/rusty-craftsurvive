using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Birds;

/// <summary>Live-debug adapters over the birds (#10630); it owns no state of its own.</summary>
public sealed class BirdDebugModule : IDebugCommandModule
{
    private readonly Func<BirdModule> birdSource;
    private BirdModule birds => birdSource();

    private readonly Func<PartyModule> partySource;
    private PartyModule parties => partySource();

    internal BirdDebugModule(Func<BirdModule> birds, Func<PartyModule> parties)
    {
        birdSource = birds ?? throw new ArgumentNullException(nameof(birds));
        partySource = parties ?? throw new ArgumentNullException(nameof(parties));
    }

    [DebugCommand("craft.birds.readout", Description = "Birds (#10630): the flights in the air, and what updates have cost since the last readout.")]
    public string Readout() => birds.Readout();

    [DebugCommand("craft.birds.flight", Description = "Test aid (#10630): sends a flight of a kind from the edge of reach, whatever the ground and hour.")]
    public string Flight(string kind) => birds.SendNow(kind, overhead: false);

    [DebugCommand("craft.birds.overhead", Description = "Test aid (#10630): sends a flight of a kind and starts it at its nearest to the player, to look at.")]
    public string Overhead(string kind) => birds.SendNow(kind, overhead: true);

    [DebugCommand("craft.birds.clear", Description = "Test aid (#10630): every flight goes now.")]
    public string Clear() => birds.ClearNow();

    [DebugCommand("craft.birds.parties", Description = "Birds on the ground (#10639): the parties, and what updates have cost since the last readout.")]
    public string Parties() => parties.Readout();

    [DebugCommand("craft.birds.party", Description = "Test aid (#10639): puts a party of a kind ahead of the player at this distance, whatever the ground and hour: craft.birds.party <kind> <metres>.")]
    public string Party(string kind, double metres) => parties.SendNow(kind, metres);

    [DebugCommand("craft.birds.glade", Description = "Test aid (#10639): the nearest ground among trees a party would take: craft.birds.glade <radiusMetres> <stepMetres>.")]
    public string Glade(double radiusMetres, double stepMetres) => parties.FindGlade(radiusMetres, stepMetres);

    [DebugCommand("craft.birds.parties.clear", Description = "Test aid (#10639): every party goes now.")]
    public string ClearParties() => parties.ClearNow();
}
