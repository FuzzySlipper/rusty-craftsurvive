namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>The underground's stranger communities (#10096), each grown where its own feature of the cave is.</summary>
internal enum CaveCommunity
{
    VentOrchard,
    InvertedGrove,
    FunnelGarden,
    GlassSteppe,
    RibbonThicket,
    VeilForest,
    CushionBarrens,
    IvoryHeath,
}

/// <summary>
/// What a cave knows of where it lies (#10304): the climate of the region above its entrance and how deep the cave lies
/// under the ground there, in metres (its top's depth; a place in it lies deeper by how far it is under the cave's surface).
/// </summary>
internal readonly record struct CaveSetting(double Temperature, double Moisture, float DepthMetres)
{
    /// <summary>The deepest a cave lies under its entrance, under high, rocky ground.</summary>
    private const float MostDepthMetres = 90f;

    // How the depth follows the land: mostly its height (the water had further to fall to its spring), some its rock.
    private const double DepthFromHeight = 0.75, DepthFromRock = 0.25;

    /// <summary>A temperate, middling place: the cave the bank and studies generate without an entrance.</summary>
    internal static CaveSetting Temperate { get; } = new(0.5, 0.5, 60f);

    /// <summary>
    /// The setting under a place of the world map: its temperature and moisture (0 to 1), its height as a share of the map's
    /// peaks and its rock (0 to 1).
    /// </summary>
    internal static CaveSetting Under(double temperature, double moisture, double heightShare, double rock)
    {
        double deep = Math.Clamp((DepthFromHeight * Math.Clamp(heightShare, 0d, 1d)) + (DepthFromRock * Math.Clamp(rock, 0d, 1d)), 0d, 1d);
        return new CaveSetting(temperature, moisture, (float)(MostDepthMetres * deep));
    }

    /// <summary>A short name for readouts and the generation cache.</summary>
    internal string Key => FormattableString.Invariant($"t{Temperature:0.00}m{Moisture:0.00}d{DepthMetres:0}");
}

/// <summary>
/// How a cave chooses its communities (owner, 2026-10-23; atlas, Combat &amp; Delving): the cave's own features say where
/// each can grow (each placement finds its sites), the region above weights which ones it grows (a community far from its
/// climate is ruled out), and depth says how strange (each needs its sites to lie so deep). A cave grows one to three.
/// </summary>
internal static class CaveCommunities
{
    /// <summary>
    /// A community's needs: how deep its sites must lie (metres under the entrance's ground), and the climate it favours
    /// above (the world map's temperature and moisture, 0 to 1) with how far from it it still grows.
    /// </summary>
    internal readonly record struct Traits(float DepthMetres, double Temperature, double TemperatureReach, double Moisture, double MoistureReach);

    // The order of strangeness (atlas, a proposal): the nearer plant- and fungus-like communities from a shallow depth, the
    // veil forest and ivory heath deeper, the glass steppe and vent orchard deepest. Metres under the entrance's ground.
    private const float Near = 15f, Deeper = 45f, Deepest = 75f;

    internal static Traits Of(CaveCommunity community) => community switch
    {
        // Rubble slopes under cool, middling land.
        CaveCommunity.CushionBarrens => new(Near, 0.35, 0.3, 0.45, 0.3),
        // Tall wet passages under warm, wet forest.
        CaveCommunity.RibbonThicket => new(Near, 0.6, 0.3, 0.7, 0.2),
        // Under shafts fed by the dust and drip of a living surface.
        CaveCommunity.FunnelGarden => new(Near, 0.5, 0.35, 0.6, 0.25),
        // Rooted in a ceiling close under living ground: never under bare desert or ice.
        CaveCommunity.InvertedGrove => new(Near, 0.5, 0.3, 0.6, 0.2),
        // On the draft's line under cold, draughty uplands.
        CaveCommunity.VeilForest => new(Deeper, 0.25, 0.2, 0.4, 0.3),
        // Dried flowstone under dry, warm land: never under wet forest.
        CaveCommunity.IvoryHeath => new(Deeper, 0.6, 0.3, 0.15, 0.15),
        // Wide dry floors under dry, open land.
        CaveCommunity.GlassSteppe => new(Deepest, 0.45, 0.35, 0.3, 0.2),
        // Warm seeps under warm land (owner, 2026-10-23: rarer, not under every region); ruled out under cold land.
        _ => new(Deepest, 0.7, 0.2, 0.5, 0.35),
    };

    /// <summary>Whether a growth kind is one of a community's pieces.</summary>
    internal static bool Grows(CaveCommunity community, OvergrowthKind kind) => community switch
    {
        CaveCommunity.VentOrchard => OvergrowthPlacement.Vent(kind),
        CaveCommunity.InvertedGrove => OvergrowthPlacement.Grove(kind),
        CaveCommunity.FunnelGarden => OvergrowthPlacement.Funnel(kind),
        CaveCommunity.GlassSteppe => OvergrowthPlacement.Steppe(kind),
        CaveCommunity.RibbonThicket => OvergrowthPlacement.Ribbon(kind),
        CaveCommunity.VeilForest => OvergrowthPlacement.Veil(kind),
        CaveCommunity.CushionBarrens => OvergrowthPlacement.Cushion(kind),
        _ => OvergrowthPlacement.Ivory(kind),
    };

    /// <summary>Below this weight a community is ruled out under a region.</summary>
    private const double RuledOut = 0.05;

    /// <summary>A community's weight under a region: its closeness to the climate it favours, 0 when ruled out.</summary>
    internal static double Weight(CaveCommunity community, CaveSetting setting)
    {
        Traits traits = Of(community);
        double t = (setting.Temperature - traits.Temperature) / traits.TemperatureReach, m = (setting.Moisture - traits.Moisture) / traits.MoistureReach;
        double weight = Math.Exp(-((t * t) + (m * m)));
        return weight < RuledOut ? 0d : weight;
    }

    // How many a cave tries to grow: one, two or three, two the likeliest; and a cave shallower than ShallowMetres is the
    // likelier to grow none, the shallower it is (none at all at the surface).
    private const double OneShare = 0.35, TwoShare = 0.45;
    private const float ShallowMetres = 30f;

    /// <summary>How many communities a cave grows, if it has the sites and depth for them.</summary>
    internal static int Target(ulong seed, float depthMetres)
    {
        DungeonRandom random = new(seed ^ 0xC077UL);
        if (random.Unit() >= depthMetres / ShallowMetres)
        {
            return 0;
        }

        double roll = random.Unit();
        return roll < OneShare ? 1 : roll < OneShare + TwoShare ? 2 : 3;
    }

    /// <summary>
    /// The communities a cave may grow, in the order to try them: those deep enough for its deepest place and not ruled out
    /// by the region, drawn by weight without replacement. A cave grows the first ones that find sites, up to its target.
    /// </summary>
    internal static List<CaveCommunity> Candidates(CaveSetting setting, float deepestMetres, ulong seed)
    {
        List<(CaveCommunity Community, double Weight)> pool = [.. Enum.GetValues<CaveCommunity>()
            .Where(c => Of(c).DepthMetres <= deepestMetres)
            .Select(c => (c, Weight(c, setting)))
            .Where(c => c.Item2 > 0d)];
        DungeonRandom random = new(seed ^ 0xC0DEUL);
        List<CaveCommunity> order = [];
        while (pool.Count > 0)
        {
            double roll = random.Unit() * pool.Sum(c => c.Weight);
            int pick = 0;
            while (pick < pool.Count - 1 && roll >= pool[pick].Weight)
            {
                roll -= pool[pick].Weight;
                pick++;
            }

            order.Add(pool[pick].Community);
            pool.RemoveAt(pick);
        }

        return order;
    }
}
