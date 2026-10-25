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

        // The tile and its neighbours, each taken alone: a tile must draw water over every lattice vertex it leaves to a
        // ribbon whether or not its neighbours are loaded (the near water square's edge, R10191-1).
        Dictionary<(long X, long Z), (WaterSample[] Lattice, StreamRibbons.TileStreams Streams)> tiles = [];
        for (long dz = -1; dz <= 1; dz++)
        for (long dx = -1; dx <= 1; dx++)
            tiles[(key.X + dx, key.Z + dz)] = Sampled(water, ribbons, key.X + dx, key.Z + dz);
        StreamRibbons.TileStreams streams = tiles[key].Streams;
        Check.That(streams.Strips.Any(strip => strip.Reach == reach && strip.Drawn.Any(drawn => drawn)), "the narrow reach's tile draws its ribbon");
        Check.That(streams.Covered.Any(covered => covered), "the ribbon covers some of its tile's lattice");
        Check.That(tiles.Values.All(tile => tile.Streams.Strips.All(Clear)),
            "no ribbon is drawn where its stations meet other water: the lattice draws the meeting");

        int covered = 0, under = 0, drawnQuads = 0;
        bool inside = true, noIce = true;
        foreach (((long X, long Z) tile, (WaterSample[] _, StreamRibbons.TileStreams tileStreams)) in tiles)
        {
            (List<(Vector3 A, Vector3 B, Vector3 C)> waterTriangles, List<(Vector3 A, Vector3 B, Vector3 C)> iceTriangles, int quads) = Drawn(tileStreams, tile);
            drawnQuads += quads;
            noIce &= iceTriangles.Count == 0;
            inside &= waterTriangles.SelectMany(t => new[] { t.A, t.B, t.C }).All(p => Within(p, tile));
            (int c, int u) = Under(tileStreams, tile, waterTriangles);
            covered += c;
            under += u;
        }

        Check.That(drawnQuads > 0, "the ribbons about the reach draw quads");
        Check.That(noIce, "an unfrozen stream draws no ice");
        Check.That(inside, "a tile draws its ribbons cut to its own square");
        Check.That(under == covered, $"every lattice vertex a tile leaves to a ribbon lies under that tile's own ribbon ({under} of {covered})");

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
        // Ice is what the walking session stands on: each tile alone keeps ice under every lattice vertex it leaves to a ribbon.
        int frozenCovered = 0, frozenUnder = 0;
        for (long dz = -1; dz <= 1; dz++)
        for (long dx = -1; dx <= 1; dx++)
        {
            (long X, long Z) tile = (key.X + dx, key.Z + dz);
            StreamRibbons.TileStreams frozen = Sampled(water, ribbons, tile.X, tile.Z).Streams;
            (int c, int u) = Under(frozen, tile, Drawn(frozen, tile).Ice);
            frozenCovered += c;
            frozenUnder += u;
        }

        Check.That(frozenCovered > 0 && frozenUnder == frozenCovered, $"a frozen stream's ice lies under every lattice vertex left to it ({frozenUnder} of {frozenCovered})");
        water.Stages = null;
    }

    /// <summary>Whether a strip is drawn only between sections none of whose stations is wet with other water.</summary>
    private static bool Clear(StreamRibbons.Strip strip)
    {
        int width = StreamRibbons.Width;
        for (int s = 0; s + 1 < strip.Sections; s++)
        {
            if (!strip.Drawn[s]) continue;
            for (int v = s * width; v < (s + 2) * width; v++)
                if (strip.Samples[v] is { Wet: true } sample && !(sample.Body == WaterBody.River && sample.BodyId == strip.Reach)) return false;
        }

        return true;
    }

    /// <summary>A tile's ribbon triangles in world metres, water and ice, and how many quads it draws.</summary>
    private static (List<(Vector3 A, Vector3 B, Vector3 C)> Water, List<(Vector3 A, Vector3 B, Vector3 C)> Ice, int Quads) Drawn(StreamRibbons.TileStreams streams, (long X, long Z) tile)
    {
        List<Vector3> vertices = [];
        List<uint> waterIndices = [], iceIndices = [];
        int quads = StreamRibbons.Append(streams, tile.X * TileMetres, tile.Z * TileMetres, TileMetres, _ => default, vertex =>
        {
            vertices.Add(vertex.Position + new Vector3(tile.X * TileMetres, 0, tile.Z * TileMetres));
            return (uint)(vertices.Count - 1);
        }, waterIndices, iceIndices);
        List<(Vector3, Vector3, Vector3)> Triangles(List<uint> indices) =>
            [.. Enumerable.Range(0, indices.Count / 3).Select(t => (vertices[(int)indices[3 * t]], vertices[(int)indices[(3 * t) + 1]], vertices[(int)indices[(3 * t) + 2]]))];
        return (Triangles(waterIndices), Triangles(iceIndices), quads);
    }

    /// <summary>How many of a tile's lattice vertices are left to a ribbon, and how many of those lie under the given triangles.</summary>
    private static (int Covered, int Under) Under(StreamRibbons.TileStreams streams, (long X, long Z) tile, List<(Vector3 A, Vector3 B, Vector3 C)> triangles)
    {
        int covered = 0, under = 0;
        for (int v = 0; v < streams.Covered.Length; v++)
        {
            if (!streams.Covered[v]) continue;
            covered++;
            Vector2 p = new((tile.X * TileMetres) + (v % Side * Step), (tile.Z * TileMetres) + (v / Side * Step));
            if (triangles.Any(t => Inside(p, t.A, t.B, t.C))) under++;
        }

        return (covered, under);
    }

    private static bool Within(Vector3 p, (long X, long Z) tile) =>
        p.X >= (tile.X * TileMetres) - EdgeTolerance && p.X <= ((tile.X + 1) * TileMetres) + EdgeTolerance
        && p.Z >= (tile.Z * TileMetres) - EdgeTolerance && p.Z <= ((tile.Z + 1) * TileMetres) + EdgeTolerance;

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
        StreamRibbons.Append(streams, key.X * TileMetres, key.Z * TileMetres, TileMetres, _ => default, _ => next++, waterIndices, iceIndices);
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
