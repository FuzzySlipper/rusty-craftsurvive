namespace CraftSurvive.Game.Modules.Towns;

/// <summary>
/// Which town the traveller may enter now (#9895 review R9895-2). Walking, it is the town whose
/// doorstep or arrival point they stand at. With the map open the expedition is where its party is,
/// not where the walking player last stood: a town is offered only while the party stands still
/// within its welcome. A party on the move is never offered one, so a departure withdraws the offer
/// and a stale one cannot open a visit behind a journey.
/// </summary>
internal static class TownEntry
{
    internal static TownSite? Offered(IReadOnlyList<TownSite> sites, bool mapOpen, (double X, double Z) feet,
        (double X, double Z)? party, bool partyTravelling)
    {
        if (mapOpen && partyTravelling) return null;
        (double x, double z) = mapOpen && party is { } standing ? standing : feet;
        return sites.Where(site => site.Welcomes(x, z)).MinBy(site => site.FromEdge(x, z));
    }
}
