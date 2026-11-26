using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Wildlife;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// What the caves under the world offer their animals (#10876): read once from each admitted cave's layout, in world
/// metres, for wildlife to place its cave animals by zone and community.
/// </summary>
internal sealed partial class DungeonModule
{
    /// <summary>The cave mouth's twilight (#10876): the tendril from this far in from the mouth...</summary>
    private const float MouthTwilightFromMetres = 6f;

    /// <summary>...to this far, on the overworld's side of the cave's box.</summary>
    private const float MouthTwilightToMetres = 24f;

    /// <summary>A floor site is named this far over its floor, in the air, so the floor is found looking down from it.</summary>
    private const float SiteOverFloorMetres = 0.5f;

    /// <summary>A growth's site stands this far out from the rock it grows on, into the cave's air.</summary>
    private const float SiteOffGrowthMetres = 1f;

    /// <summary>The deep floors are the deeper part of a cave's main route: below this share of its height span.</summary>
    private const float DeepFloorShare = 0.5f;

    private readonly Dictionary<(long X, long Z), CaveSites> caveSites = [];

    /// <summary>The sites of every admitted cave under the world, for wildlife (#10876). Empty in a dungeon of its own.</summary>
    internal IReadOnlyList<CaveSites> CaveSitesNear()
    {
        if (player.InSeparateSpace) return [];
        List<CaveSites> near = [];
        foreach (UnderCave cave in underCaves.Values)
        {
            if (cave.Ready is not UndergroundCave ready) continue;
            if (!caveSites.TryGetValue(cave.Cave.Key, out CaveSites? sites))
            {
                caveSites[cave.Cave.Key] = sites = SitesOf(cave.Cave, ready.Layout);
            }

            near.Add(sites);
        }

        // Caves let go take their sites with them.
        foreach ((long, long) gone in caveSites.Keys.Where(key => !underCaves.ContainsKey(key)).ToArray()) caveSites.Remove(gone);
        return near;
    }

    /// <summary>Each admitted cave's ruin doorways in world metres (#11097), kept for the layout they were read from.</summary>
    private readonly Dictionary<(long X, long Z), (DungeonLayout Layout, IReadOnlyList<RuinDoorway> Doorways)> caveDoorways = [];

    private IReadOnlyList<RuinDoorway> doorwaysNear = [];

    /// <summary>
    /// The ruin doorways of every admitted cave under the world, in world metres (#11097): what a mender in the walking session
    /// seals. The same list is given until a cave is admitted, regenerated or let go. Empty in a dungeon of its own.
    /// </summary>
    internal IReadOnlyList<RuinDoorway> CaveDoorwaysNear()
    {
        if (player.InSeparateSpace) return [];
        bool changed = false;
        foreach (UnderCave cave in underCaves.Values)
        {
            if (cave.Ready is not UndergroundCave ready) continue;
            if (caveDoorways.TryGetValue(cave.Cave.Key, out var known) && ReferenceEquals(known.Layout, ready.Layout)) continue;
            caveDoorways[cave.Cave.Key] = (ready.Layout, CaveRuinSites.Doorways(cave.Cave.ToWorld, ready.Layout));
            changed = true;
        }

        foreach ((long, long) gone in caveDoorways.Keys.Where(key => !underCaves.TryGetValue(key, out UnderCave? cave) || cave.Ready is null).ToArray())
        {
            caveDoorways.Remove(gone);
            changed = true;
        }

        if (changed) doorwaysNear = [.. caveDoorways.Values.SelectMany(entry => entry.Doorways)];
        return doorwaysNear;
    }

    /// <summary>A cave's sites, from its layout and its tendril.</summary>
    internal static CaveSites SitesOf(CavePlacement cave, DungeonLayout layout)
    {
        string key = CaveKey(cave);
        Vector3 Over(Vector3 canonical) => cave.ToWorld(canonical) + (Vector3.UnitY * SiteOverFloorMetres);

        // The twilight: the tendril's centre line a few metres in from the mouth, which the overworld's generator carves.
        List<Vector3> mouth = [];
        float along = 0f;
        for (int i = 1; i < cave.Path.Count; i++)
        {
            along += Vector3.Distance(cave.Path[i - 1], cave.Path[i]);
            if (along >= MouthTwilightFromMetres && along <= MouthTwilightToMetres) mouth.Add(cave.Path[i]);
        }

        List<CavePool> pools = [.. layout.WaterZones
            .Where(zone => zone.Kind is WaterZoneKind.Lake or WaterZoneKind.Pool && zone.SurfaceColumns.Count > 0)
            .Select(zone => new CavePool(
                cave.ToWorld(new Vector3(0f, zone.Surface, 0f)).Y,
                [.. zone.SurfaceColumns.Select(column => cave.ToWorld(new Vector3(column.X, zone.Surface, column.Y))).Select(world => new Vector2(world.X, world.Z))]))];

        List<Vector3> deep = [];
        if (layout.MainRoute.Count > 0)
        {
            float low = layout.MainRoute.Min(point => point.Y), high = layout.MainRoute.Max(point => point.Y);
            deep = [.. layout.MainRoute.Where(point => point.Y <= low + ((high - low) * DeepFloorShare)).Select(cave.ToWorld)];
        }

        Dictionary<CaveHabitat, IReadOnlyList<Vector3>> communities = [];
        foreach (CaveCommunity community in layout.Communities)
        {
            // A grove's hangers grow down from the ceiling: its animals' homes are on the fallfield under them (#10877).
            bool overhead = community == CaveCommunity.InvertedGrove;
            communities[HabitatOf(community)] = [.. layout.Overgrowth
                .Where(growth => CaveCommunities.Signature(community, growth.Kind))
                .Select(growth => growth.At + (growth.Out * SiteOffGrowthMetres))
                .Select(site => overhead ? Over(layout.FloorUnder(site)) : cave.ToWorld(site))];
        }

        // The veil fans stand square to the draft, facing downwind (#10878): upwind is the other way, in world terms.
        List<(Vector2 At, Vector2 Upwind)> drafts = [];
        foreach (OvergrowthPlacement fan in layout.Overgrowth.Where(growth => CaveCommunities.Signature(CaveCommunity.VeilForest, growth.Kind)))
        {
            Vector3 at = cave.ToWorld(fan.At), downwind = cave.ToWorld(fan.At + fan.Out) - at;
            Vector2 upwind = -new Vector2(downwind.X, downwind.Z);
            if (upwind.LengthSquared() > 1e-6f) drafts.Add((new Vector2(at.X, at.Z), Vector2.Normalize(upwind)));
        }

        return new CaveSites(key, mouth, [.. layout.FungalPockets.Select(Over)], pools, deep, communities) { Drafts = drafts, Ruin = CaveRuinSites.Of(cave.ToWorld, layout) };
    }

    /// <summary>The wildlife's name for a community: the same eight.</summary>
    internal static CaveHabitat HabitatOf(CaveCommunity community) => community switch
    {
        CaveCommunity.VentOrchard => CaveHabitat.VentOrchard,
        CaveCommunity.InvertedGrove => CaveHabitat.InvertedGrove,
        CaveCommunity.FunnelGarden => CaveHabitat.FunnelGarden,
        CaveCommunity.GlassSteppe => CaveHabitat.GlassSteppe,
        CaveCommunity.RibbonThicket => CaveHabitat.RibbonThicket,
        CaveCommunity.VeilForest => CaveHabitat.VeilForest,
        CaveCommunity.CushionBarrens => CaveHabitat.CushionBarrens,
        _ => CaveHabitat.IvoryHeath,
    };
}
