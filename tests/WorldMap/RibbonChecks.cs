using System.Numerics;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// Narrow streams as ribbons (#10191), on the default continent: a narrow reach's tile draws a ribbon over its water,
/// the lattice leaves only cells a drawn ribbon lies over, no ribbon is drawn where its stations meet other water, and
/// the ribbon follows the stage - risen and wider in spate, gone when dry, ice when frozen.
/// </summary>
internal static class RibbonChecks
{
    private const int TileMetres = NearWaterSquare.TileMetres, Step = WaterWaveSettings.MeshSpacingMetres, Side = (TileMetres / Step) + 1;

    /// <summary>How many narrow reaches are tried for one whose tile draws a ribbon.</summary>
    private const int Candidates = 60;

    /// <summary>A lattice vertex lying on a quad's edge counts as under it within this (metres).</summary>
    private const float EdgeTolerance = 1e-3f;

    internal static void Run(WorldMap continent, TerrainRecipe recipe)
    {
        WaterNetwork network = MapRegions.For(continent).Drainage.Network;
        WaterModel water = new(recipe);
        StreamRibbons ribbons = new(water);
        (long X, long Z)? found = null;
        int reach = -1;
        foreach (Reach candidate in network.Reaches.Where(r => r.Width < StreamRibbons.WidestMetres && r.Nodes.Length > 2).Take(Candidates))
        {
            int node = candidate.Nodes[candidate.Nodes.Length / 2];
            long cx = (long)network.Grid.X(node), cz = (long)network.Grid.Z(node);
            for (int d = 0; d < 1600 && found is null; d++)
            {
                long x = cx + (d % 40) - 20, z = cz + (d / 40) - 20;
                if (water.Sample(x, z) is { Wet: true, Body: WaterBody.River } sample && sample.BodyId == candidate.Id) found = (x, z);
            }

            if (found is not null) { reach = candidate.Id; break; }
        }

        Check.That(found is not null, "a narrow reach's water is found");
        if (found is not (long X, long Z) at) return;
        (long X, long Z) key = ((long)Math.Floor(at.X / (double)TileMetres), (long)Math.Floor(at.Z / (double)TileMetres));
        Check.That(water.StreamsWithin(key.X * TileMetres, key.Z * TileMetres, (key.X + 1) * TileMetres, (key.Z + 1) * TileMetres, StreamRibbons.WidestMetres).Contains(reach),
            "the reach is among the narrow streams near its tile");

        // The tile and its neighbours, so a covered lattice vertex can be matched to whichever tile draws the ribbon over it.
        Dictionary<(long X, long Z), (WaterSample[] Lattice, StreamRibbons.TileStreams Streams)> tiles = [];
        for (long dz = -1; dz <= 1; dz++)
        for (long dx = -1; dx <= 1; dx++)
            tiles[(key.X + dx, key.Z + dz)] = Sampled(water, ribbons, key.X + dx, key.Z + dz);
        (WaterSample[] lattice, StreamRibbons.TileStreams streams) = tiles[key];
        Check.That(streams.Strips.Any(strip => strip.Reach == reach && strip.Drawn.Any(drawn => drawn)), "the narrow reach's tile draws its ribbon");
        Check.That(streams.Covered.Any(covered => covered), "the ribbon covers some of its tile's lattice");

        List<(Vector3 A, Vector3 B, Vector3 C)> drawnTriangles = [];
        bool foreign = false, ownCorner = true, tops = true;
        foreach (((long X, long Z) tile, (WaterSample[] _, StreamRibbons.TileStreams tileStreams)) in tiles)
        foreach (StreamRibbons.Strip strip in tileStreams.Strips)
        {
            List<(Vector3 Position, WaterSample Sample)> vertices = [];
            List<uint> waterIndices = [], iceIndices = [];
            long x0 = tile.X * TileMetres, z0 = tile.Z * TileMetres;
            int owner = strip.Reach;
            StreamRibbons.Append(new([strip], []), x0, z0, (position, sample) =>
            {
                vertices.Add((position, sample));
                return (uint)(vertices.Count - 1);
            }, waterIndices, iceIndices);
            Check.That(iceIndices.Count == 0, "an unfrozen stream draws no ice");
            for (int t = 0; t < waterIndices.Count; t += 6)
            {
                int[] corners = [(int)waterIndices[t], (int)waterIndices[t + 1], (int)waterIndices[t + 2], (int)waterIndices[t + 4]];
                (Vector3 Position, WaterSample Sample)[] quad = [.. corners.Select(c => vertices[c])];
                foreign |= quad.Any(v => v.Sample.Wet && !(v.Sample.Body == WaterBody.River && v.Sample.BodyId == owner));
                ownCorner &= quad.Any(v => v.Sample is { Wet: true, Body: WaterBody.River } s && s.BodyId == owner);
                tops &= quad.All(v => v.Sample.Wet ? Math.Abs(v.Position.Y - v.Sample.Top) < 1e-3 : v.Position.Y <= v.Sample.Bed - WaterWaveSettings.ShoreSink + 1e-3);
                Vector3 offset = new(x0, 0, z0);
                for (int k = t; k < t + 6; k += 3)
                    drawnTriangles.Add((vertices[(int)waterIndices[k]].Position + offset, vertices[(int)waterIndices[k + 1]].Position + offset, vertices[(int)waterIndices[k + 2]].Position + offset));
            }
        }

        Check.That(drawnTriangles.Count > 0, "the ribbons about the reach draw quads");
        Check.That(ownCorner, "every drawn ribbon quad touches its own stream's water");
        Check.That(!foreign, "no ribbon is drawn where its stations meet other water: the lattice draws the meeting");
        Check.That(tops, "a ribbon stands at its water's top, and on a dry bank no higher than a little under the ground");

        // No gap: every lattice vertex the ribbon covers lies under a drawn ribbon quad, whichever tile draws it.
        int coveredCount = 0, underRibbon = 0;
        for (int v = 0; v < lattice.Length; v++)
        {
            if (!streams.Covered[v]) continue;
            coveredCount++;
            Vector2 p = new((key.X * TileMetres) + (v % Side * Step), (key.Z * TileMetres) + (v / Side * Step));
            if (drawnTriangles.Any(t => Inside(p, t.A, t.B, t.C))) underRibbon++;
        }

        Check.That(underRibbon == coveredCount, $"every lattice vertex left to a ribbon lies under a drawn ribbon ({underRibbon} of {coveredCount})");

        // The stage: in spate the ribbon stands higher and reaches wider; dry, it draws nothing; frozen, it is ice.
        StubStage stub = new(reach);
        water.Stages = stub;
        Reach stream = network.Reaches[reach];
        double still = water.HalfWidthNow(reach);
        stub.Now = new(stream.Flow * 3, 3, 0.9, 0.4, false, false);
        StreamRibbons.TileStreams spate = ribbons.Sample(key.X * TileMetres, key.Z * TileMetres, TileMetres, Side, Step, Lattice(water, key.X, key.Z));
        Check.That(water.HalfWidthNow(reach) >= still && Math.Abs(water.HalfWidthNow(reach) - RiverGeometry.HalfWidth(stream.Flow * 3)) < 1e-12,
            "a stream's ribbon is as wide as its flow now asks");
        Check.That(Own(spate, reach).All(s => Math.Abs(s.Stage - 0.4) < 1e-9) && Own(spate, reach).Any(),
            "a stream's ribbon stands at its stage");
        stub.Now = new(stream.Flow * 0.01, 0.01, 0.002, -0.2, false, true);
        Check.That(Quads(ribbons, water, key).Water == 0, "a dry stream draws no ribbon");
        stub.Now = new(stream.Flow * 0.4, 0.4, 0.15, -0.1, true, false);
        (int frozenWater, int frozenIce) = Quads(ribbons, water, key);
        Check.That(frozenIce > 0 && frozenWater == 0, $"a frozen stream's ribbon is ice ({frozenIce} quads)");
        water.Stages = null;
    }

    private static WaterSample[] Lattice(WaterModel water, long tx, long tz)
    {
        WaterSample[] lattice = new WaterSample[Side * Side];
        for (int j = 0; j < Side; j++)
        for (int i = 0; i < Side; i++)
            lattice[(j * Side) + i] = water.Sample((tx * TileMetres) + (i * Step), (tz * TileMetres) + (j * Step));
        return lattice;
    }

    private static (WaterSample[] Lattice, StreamRibbons.TileStreams Streams) Sampled(WaterModel water, StreamRibbons ribbons, long tx, long tz)
    {
        WaterSample[] lattice = Lattice(water, tx, tz);
        return (lattice, ribbons.Sample(tx * TileMetres, tz * TileMetres, TileMetres, Side, Step, lattice));
    }

    private static IEnumerable<WaterSample> Own(StreamRibbons.TileStreams streams, int reach) =>
        streams.Strips.Where(strip => strip.Reach == reach).SelectMany(strip => strip.Samples).Where(s => s is { Wet: true, Body: WaterBody.River } && s.BodyId == reach);

    /// <summary>A tile's ribbon quads, water and ice.</summary>
    private static (int Water, int Ice) Quads(StreamRibbons ribbons, WaterModel water, (long X, long Z) key)
    {
        StreamRibbons.TileStreams streams = ribbons.Sample(key.X * TileMetres, key.Z * TileMetres, TileMetres, Side, Step, Lattice(water, key.X, key.Z));
        List<uint> waterIndices = [], iceIndices = [];
        uint next = 0;
        StreamRibbons.Append(streams, key.X * TileMetres, key.Z * TileMetres, (_, _) => next++, waterIndices, iceIndices);
        return (waterIndices.Count / 6, iceIndices.Count / 6);
    }

    /// <summary>Whether a point lies in a triangle seen from above.</summary>
    private static bool Inside(Vector2 p, Vector3 a, Vector3 b, Vector3 c)
    {
        static float Cross(Vector2 o, Vector2 u, Vector2 v) => ((u.X - o.X) * (v.Y - o.Y)) - ((u.Y - o.Y) * (v.X - o.X));
        Vector2 a2 = new(a.X, a.Z), b2 = new(b.X, b.Z), c2 = new(c.X, c.Z);
        float area = Cross(a2, b2, c2);
        if (Math.Abs(area) < 1e-9f) return false;
        float scale = EdgeTolerance * MathF.Sqrt(Math.Abs(area));
        float d1 = Cross(a2, b2, p) * Math.Sign(area), d2 = Cross(b2, c2, p) * Math.Sign(area), d3 = Cross(c2, a2, p) * Math.Sign(area);
        return d1 >= -scale && d2 >= -scale && d3 >= -scale;
    }

    private sealed class StubStage(int reach) : IWaterStage
    {
        internal ReachStage? Now { get; set; }

        public ReachStage? At(int asked) => asked == reach ? Now : null;

        public bool LakeIce(Lake lake) => false;
    }
}
