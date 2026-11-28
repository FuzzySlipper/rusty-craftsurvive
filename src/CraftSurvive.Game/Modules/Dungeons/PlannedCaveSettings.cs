using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// What a planned system's box is generated with (#11219), from the plan alone so the walking session and the authoring tools
/// generate the same box: its seed, the setting (the system's climate, the box's depth), and the karst generator's switches -
/// a port on each face a link or a side mouth crosses, the way in under a sinkhole, the water table, and the spring led toward
/// the system's outlet (its resurgence's box, else its lowest), so the water runs the way the plan drains.
/// </summary>
internal static class PlannedCaveSettings
{
    /// <summary>A side mouth's port: as wide as a tendril's section reaches.</summary>
    internal const float MouthPortRadius = 2.4f;

    /// <summary>The label a link's port goes by on a box (KarstCave reports its route under it).</summary>
    internal static string LinkLabel(int link, int from, int to) => $"L{link}:{from}->{to}";

    /// <summary>The label a mouth's port goes by.</summary>
    internal static string MouthLabel(int mouth) => $"M{mouth}";

    /// <summary>The seed, setting and switches box <paramref name="boxIndex"/> of a system is generated with.</summary>
    internal static (ulong Seed, CaveSetting Setting, KarstCave.Settings Settings) For(UndergroundSystem system, int boxIndex, KarstCave.Settings basis)
    {
        UndergroundBox box = system.Boxes[boxIndex];
        Dictionary<int, List<KarstCave.BoundaryPort>> ports = PortsByBox(system);
        Vector2? wayIn = null;
        foreach (UndergroundMouth m in system.Mouths)
        {
            if (m.BoxIndex == boxIndex && m.Face == UndergroundFace.PosY)
            {
                wayIn ??= new Vector2(m.PortU, m.PortV);
            }
        }

        KarstCave.Settings settings = basis with
        {
            ChunksX = box.ChunksX,
            ChunksY = box.ChunksY,
            ChunksZ = box.ChunksZ,
            Underground = true,
            WayIn = wayIn,
            BoundaryPorts = ports[boxIndex],
            WaterTableTarget = box.WaterTableLocalY,
            SpringAt = SpringAt(system, boxIndex, ports),
        };
        return (box.BoxSeed, new CaveSetting(system.Temperature, system.Moisture, box.DepthBelowSurface), settings);
    }

    /// <summary>Every box's ports: each link's on both its boxes, each side mouth's on its own.</summary>
    internal static Dictionary<int, List<KarstCave.BoundaryPort>> PortsByBox(UndergroundSystem system)
    {
        Dictionary<int, List<KarstCave.BoundaryPort>> ports = system.Boxes.ToDictionary(b => b.Index, _ => new List<KarstCave.BoundaryPort>());
        for (int i = 0; i < system.Links.Count; i++)
        {
            UndergroundLink l = system.Links[i];
            UndergroundBox a = system.Boxes[l.FromBox], b = system.Boxes[l.ToBox];
            bool vertical = l.Kind == UndergroundLinkKind.Shaft;
            float va = vertical ? l.FaceV : l.WorldY - (float)a.OriginY;
            float vb = vertical ? l.FaceV : l.WorldY - (float)b.OriginY;
            ports[l.FromBox].Add(new KarstCave.BoundaryPort((KarstCave.BoxFace)l.FromFace, l.FaceU, va, l.RadiusMetres, l.WaterLevelWorldY - (float)a.OriginY, l.SeamSeed, LinkLabel(i, l.FromBox, l.ToBox)));
            ports[l.ToBox].Add(new KarstCave.BoundaryPort((KarstCave.BoxFace)l.ToFace, l.FaceU, vb, l.RadiusMetres, l.WaterLevelWorldY - (float)b.OriginY, l.SeamSeed, LinkLabel(i, l.ToBox, l.FromBox)));
        }

        for (int i = 0; i < system.Mouths.Count; i++)
        {
            UndergroundMouth m = system.Mouths[i];
            if (m.Face == UndergroundFace.PosY)
            {
                continue;
            }

            UndergroundBox box = system.Boxes[m.BoxIndex];
            ports[m.BoxIndex].Add(new KarstCave.BoundaryPort((KarstCave.BoxFace)m.Face, m.PortU, m.PortV, MouthPortRadius, box.WaterTableLocalY, box.BoxSeed ^ (ulong)(0xA0 + i), MouthLabel(i)));
        }

        return ports;
    }

    /// <summary>The system's outlet: its resurgence's box, else its lowest box (the first of the lowest).</summary>
    internal static int Outlet(UndergroundSystem system) =>
        system.Mouths.FirstOrDefault(m => m.Kind == UndergroundMouthKind.Resurgence && m.Face != UndergroundFace.PosY)?.BoxIndex
        ?? system.Boxes.OrderBy(b => b.OriginY).ThenBy(b => b.Index).First().Index;

    /// <summary>Where a box's water leaves it: its resurgence port at the outlet, else its port toward the outlet; null for chance's.</summary>
    private static Vector2? SpringAt(UndergroundSystem system, int boxIndex, Dictionary<int, List<KarstCave.BoundaryPort>> ports)
    {
        int outlet = Outlet(system);
        Dictionary<int, int> hops = new() { [outlet] = 0 };
        Queue<int> frontier = new([outlet]);
        while (frontier.TryDequeue(out int at))
        {
            foreach (UndergroundLink l in system.Links)
            {
                int other = l.FromBox == at ? l.ToBox : l.ToBox == at ? l.FromBox : -1;
                if (other >= 0 && hops.TryAdd(other, hops[at] + 1)) frontier.Enqueue(other);
            }
        }

        UndergroundBox box = system.Boxes[boxIndex];
        float sx = box.ChunksX * 16f, sy = box.ChunksY * 16f, sz = box.ChunksZ * 16f;
        if (boxIndex == outlet)
        {
            int resurgence = system.Mouths.ToList().FindIndex(m => m.Kind == UndergroundMouthKind.Resurgence && m.BoxIndex == boxIndex && m.Face != UndergroundFace.PosY);
            if (resurgence < 0) return null;
            Vector3 a = ports[boxIndex].First(p => p.Label == MouthLabel(resurgence)).AnchorPoint(sx, sy, sz);
            return new Vector2(a.X, a.Z);
        }

        if (!hops.TryGetValue(boxIndex, out int mine))
        {
            return null;
        }

        for (int i = 0; i < system.Links.Count; i++)
        {
            UndergroundLink l = system.Links[i];
            int other = l.FromBox == boxIndex ? l.ToBox : l.ToBox == boxIndex ? l.FromBox : -1;
            if (other >= 0 && hops.GetValueOrDefault(other, int.MaxValue) < mine)
            {
                string label = LinkLabel(i, boxIndex, other);
                Vector3 a = ports[boxIndex].First(p => p.Label == label).AnchorPoint(sx, sy, sz);
                return new Vector2(a.X, a.Z);
            }
        }

        return null;
    }
}
