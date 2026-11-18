using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A cave under the world, generated (or read from the store) and ready to be read into the walking session's chunks
/// (#10889): what dresses it (its layout, without the voxels it was generated in), its voxels in its own frame, and the
/// walking session's chunks that hold its air. A freshly generated one carries its encoding for the store.
/// </summary>
internal sealed record UndergroundCave(DungeonLayout Layout, CaveInterior Interior, HashSet<TerrainChunkAddress> Shell)
{
    /// <summary>The cave encoded for the store, made off the update when it was generated; null once stored, or when read from it.</summary>
    internal byte[]? Encoded { get; init; }
}

/// <summary>
/// A cave under the world as the store keeps it (#10888 R10888-2): what admitting and dressing it reads, and nothing it
/// does not. That is its voxels in its own box, the walking chunks holding its air, the dressing's placements and its lakes'
/// finished surfaces (the cave's shape, a function, is not kept). Pure: encoded and decoded off the update.
/// </summary>
internal static class CaveCodec
{
    private const uint Magic = 0x4556_4143; // "CAVE"

    /// <summary>The layout of the bytes; a change to it (or to what a cave holds) changes this, and older records are misses.</summary>
    internal const int Format = 1;

    internal static byte[] Encode(UndergroundCave cave)
    {
        using MemoryStream raw = new();
        using (ZLibStream zip = new(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (BinaryWriter w = new(zip))
        {
            w.Write(Magic);
            w.Write(Format);
            CaveInterior interior = cave.Interior;
            w.Write(interior.SizeX);
            w.Write(interior.SizeY);
            w.Write(interior.SizeZ);
            foreach (ushort m in interior.Materials) w.Write(m);
            foreach (float d in interior.Densities) w.Write(d);
            w.Write(cave.Shell.Count);
            foreach (TerrainChunkAddress c in cave.Shell)
            {
                w.Write(c.X);
                w.Write(c.Y);
                w.Write(c.Z);
            }

            WriteLayout(w, cave.Layout);
        }

        return raw.ToArray();
    }

    /// <summary>The cave in a record, or null for one in another format or that does not decode (a miss, never a wrong cave).</summary>
    internal static UndergroundCave? Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using MemoryStream raw = new(bytes.ToArray());
            using ZLibStream zip = new(raw, CompressionMode.Decompress);
            using BinaryReader r = new(zip);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Format)
            {
                return null;
            }

            int sx = r.ReadInt32(), sy = r.ReadInt32(), sz = r.ReadInt32();
            int n = checked(sx * sy * sz);
            ushort[] materials = new ushort[n];
            float[] densities = new float[n];
            for (int i = 0; i < n; i++) materials[i] = r.ReadUInt16();
            for (int i = 0; i < n; i++) densities[i] = r.ReadSingle();
            int shellCount = r.ReadInt32();
            HashSet<TerrainChunkAddress> shell = new(shellCount);
            for (int i = 0; i < shellCount; i++) shell.Add(new TerrainChunkAddress(r.ReadInt64(), r.ReadInt64(), r.ReadInt64()));
            DungeonLayout layout = ReadLayout(r);
            return new UndergroundCave(layout, new CaveInterior(sx, sy, sz, materials, densities), shell);
        }
        catch (Exception failure) when (failure is InvalidDataException or EndOfStreamException or OverflowException or ArgumentException or IOException)
        {
            return null;
        }
    }

    private static void WriteLayout(BinaryWriter w, DungeonLayout layout)
    {
        w.Write(layout.Name);
        V(w, layout.Arrival);
        V(w, layout.Exit);
        List(w, layout.Lights, V);
        List(w, layout.Ecology, (w, e) => { w.Write((int)e.Kind); V(w, e.At); V(w, e.Normal); w.Write(e.Size); w.Write(e.Seed); });
        List(w, layout.Kit, (w, k) => { w.Write((int)k.Piece); V(w, k.Origin); w.Write(k.Turn); V2(w, k.Stretch); V2(w, k.Shift); w.Write(k.Lit); w.Write(k.Wet); });
        List(w, layout.Overgrowth, (w, g) =>
        {
            w.Write((int)g.Kind);
            V(w, g.At);
            V(w, g.Out);
            w.Write(g.Scale);
            w.Write(g.Pick);
            w.Write(g.Up.HasValue);
            if (g.Up is Vector3 up) V(w, up);
        });
        List(w, layout.MossCards, (w, c) => { w.Write((int)c.Kind); V(w, c.At); V(w, c.Out); V(w, c.Normal); w.Write(c.Pick); });
        List(w, layout.Forest, (w, f) => { w.Write(f.Species); w.Write(f.Variant); V(w, f.At); w.Write(f.YawDegrees); w.Write(f.Scale); });
        List(w, layout.Drips, (w, d) => { V(w, d.Centre); Q(w, d.Turn); V(w, d.Size); V4(w, d.Region); });
        List(w, layout.Relics, V);
        List(w, layout.MainRoute, V);
        List(w, layout.Waterfalls, (w, f) => { V(w, f.Lip); V(w, f.Out); w.Write(f.Drop); w.Write(f.Lands); w.Write(f.Flow); w.Write(f.SkyShare); });
        w.Write(layout.Abyss.HasValue);
        if (layout.Abyss is (Vector3 centre, float radius))
        {
            V(w, centre);
            w.Write(radius);
        }

        List(w, layout.Communities, (w, c) => w.Write((int)c));
        List(w, layout.WaterZones, (w, z) =>
        {
            w.Write((int)z.Kind);
            w.Write(z.Surface);
            List(w, [.. z.Cells.Order()], (w, c) => w.Write(c));
            List(w, z.SurfaceColumns, V2);
            List(w, z.Mouths, V);
            List(w, z.Path, V);
            w.Write(z.Luminous);
            // The lake's finished surface: the cave's shape that made it is not kept.
            CaveWaterGeometry? surface = CaveWaterGeometry.Of(z);
            w.Write(surface is not null);
            if (surface is not null)
            {
                List(w, surface.Positions, V);
                List(w, surface.Normals, V);
                List(w, surface.Uvs, V2);
                List(w, surface.Colours, (w, c) => { w.Write(c.R); w.Write(c.G); w.Write(c.B); w.Write(c.A); });
                List(w, surface.Indices, (w, i) => w.Write(i));
            }
        });
    }

    private static DungeonLayout ReadLayout(BinaryReader r)
    {
        string name = r.ReadString();
        Vector3 arrival = V(r), exit = V(r);
        List<Vector3> lights = List(r, V);
        List<EcologyPlacement> ecology = List(r, r => new EcologyPlacement((EcologyKind)r.ReadInt32(), V(r), V(r), r.ReadSingle(), r.ReadUInt32()));
        List<KitPlacement> kit = List(r, r => new KitPlacement((KitPiece)r.ReadInt32(), V(r), r.ReadSingle(), V2(r), V2(r), r.ReadBoolean(), r.ReadSingle()));
        List<OvergrowthPlacement> overgrowth = List(r, r =>
        {
            OvergrowthKind kind = (OvergrowthKind)r.ReadInt32();
            Vector3 at = V(r), outward = V(r);
            float scale = r.ReadSingle(), pick = r.ReadSingle();
            Vector3? up = r.ReadBoolean() ? V(r) : null;
            return new OvergrowthPlacement(kind, at, outward, scale, pick, up);
        });
        List<MossCard> cards = List(r, r => new MossCard((MossCardKind)r.ReadInt32(), V(r), V(r), V(r), r.ReadSingle()));
        List<ForestPlacement> forest = List(r, r => new ForestPlacement(r.ReadString(), r.ReadInt32(), V(r), r.ReadSingle(), r.ReadSingle()));
        List<DungeonDrips.Mark> drips = List(r, r => new DungeonDrips.Mark(V(r), Q(r), V(r), V4(r)));
        List<Vector3> relics = List(r, V);
        List<Vector3> route = List(r, V);
        List<Waterfall> falls = List(r, r => new Waterfall(V(r), V(r), r.ReadSingle(), r.ReadBoolean(), r.ReadSingle(), r.ReadSingle()));
        (Vector3 Centre, float Radius)? abyss = r.ReadBoolean() ? (V(r), r.ReadSingle()) : null;
        List<CaveCommunity> communities = List(r, r => (CaveCommunity)r.ReadInt32());
        List<WaterZone> zones = List(r, r =>
        {
            WaterZoneKind kind = (WaterZoneKind)r.ReadInt32();
            float surface = r.ReadSingle();
            HashSet<int> cells = [.. List(r, r => r.ReadInt32())];
            WaterZone zone = new(kind, surface, cells, List(r, V2), List(r, V), List(r, V), r.ReadBoolean());
            if (r.ReadBoolean())
            {
                CaveWaterGeometry.Keep(zone, new CaveWaterGeometry([.. List(r, V)], [.. List(r, V)], [.. List(r, V2)],
                    [.. List(r, r => new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()))], [.. List(r, r => r.ReadUInt32())]));
            }

            return zone;
        });
        return new DungeonLayout(name, new DungeonVolume(1, 1, 1, Content.BlockId.Air), arrival, exit, lights)
        {
            Natural = true,
            Ecology = ecology,
            Kit = kit,
            Overgrowth = overgrowth,
            MossCards = cards,
            Forest = forest,
            Drips = drips,
            Relics = relics,
            MainRoute = route,
            Waterfalls = falls,
            Abyss = abyss,
            Communities = communities,
            WaterZones = zones,
        };
    }

    private static void List<T>(BinaryWriter w, IReadOnlyList<T> items, Action<BinaryWriter, T> write)
    {
        w.Write(items.Count);
        foreach (T item in items) write(w, item);
    }

    private static List<T> List<T>(BinaryReader r, Func<BinaryReader, T> read)
    {
        int count = r.ReadInt32();
        if (count < 0) throw new InvalidDataException("A negative count.");
        List<T> items = new(Math.Min(count, 1 << 20));
        for (int i = 0; i < count; i++) items.Add(read(r));
        return items;
    }

    private static void V(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    private static Vector3 V(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    private static void V2(BinaryWriter w, Vector2 v) { w.Write(v.X); w.Write(v.Y); }
    private static Vector2 V2(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());
    private static void V4(BinaryWriter w, Vector4 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); w.Write(v.W); }
    private static Vector4 V4(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    private static void Q(BinaryWriter w, Quaternion q) { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
    private static Quaternion Q(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}

/// <summary>
/// The caves generated for this world, kept through the Engine's persistence (#10888 R10888-2): a cave is generated once per
/// world and build, and coming back to it - after the generator let it go, or after the game was closed - reads it instead.
/// A record names the world (seed, generator version), this build (the product module's version id, so a changed generator
/// never serves an old cave) and the cave's own key. At most <see cref="MaximumCaves"/> are kept, the oldest let go first. Like
/// the terrain's chunk cache, it is an optimisation: a store that refuses stops writing for the session, and a record that
/// does not decode is a miss.
/// </summary>
internal sealed class CaveStore : IDisposable
{
    internal const string Scope = "craftsurvive.caves";
    private const string IndexKey = "index";

    /// <summary>At one to three megabytes a cave, the store holds at most about 60 MB.</summary>
    internal const int MaximumCaves = 24;

    private readonly IEngineContext engine;
    private readonly PersistenceStore store;
    private readonly string prefix;
    private readonly List<string> index;

    internal CaveStore(IEngineContext engine, ulong worldSeed, uint generatorVersion)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        prefix = string.Create(CultureInfo.InvariantCulture,
            $"cave/{worldSeed:x16}/{generatorVersion}/{CaveCodec.Format}/{typeof(CaveStore).Module.ModuleVersionId:N}/");
        store = engine.Persistence.OpenStore(new PersistenceOpenRequest(Scope));
        using (PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(store, IndexKey)))
        {
            index = engine.Persistence.DescribeBlob(blob).Present
                ? [.. System.Text.Encoding.UTF8.GetString(engine.Persistence.ReadBlobBytes(blob).Span).Split('\n', StringSplitOptions.RemoveEmptyEntries)]
                : [];
        }

        // Records another world or build wrote are let go: the store holds this world's caves.
        string[] stale = [.. index.Where(key => !key.StartsWith(prefix, StringComparison.Ordinal))];
        if (stale.Length > 0)
        {
            Change(() =>
            {
                foreach (string key in stale) Delete(key);
                index.RemoveAll(stale.Contains);
                SaveIndex();
            });
        }
    }

    /// <summary>How many caves the store holds for this world, and how many it read and wrote this session.</summary>
    internal int Count => index.Count;
    internal long Reads { get; private set; }
    internal long Writes { get; private set; }

    /// <summary>Why the store refused this session, after which it writes nothing; null while it writes.</summary>
    internal string? Failure { get; private set; }

    internal bool Has(string caveKey) => index.Contains(prefix + caveKey);

    /// <summary>A stored cave's bytes (to decode off the update), or null where none is kept.</summary>
    internal byte[]? Read(string caveKey)
    {
        if (!Has(caveKey))
        {
            return null;
        }

        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(store, prefix + caveKey));
        if (!engine.Persistence.DescribeBlob(blob).Present)
        {
            return null;
        }

        Reads++;
        return engine.Persistence.ReadBlobBytes(blob).ToArray();
    }

    /// <summary>Keeps a generated cave's bytes, letting the oldest go past <see cref="MaximumCaves"/>.</summary>
    internal void Write(string caveKey, byte[] bytes)
    {
        if (Failure is not null || Has(caveKey))
        {
            return;
        }

        Change(() =>
        {
            engine.Persistence.Save(new PersistenceSaveRequest(store, prefix + caveKey, PersistenceRevisionGuard.Any, 0, bytes));
            index.Add(prefix + caveKey);
            while (index.Count > MaximumCaves)
            {
                Delete(index[0]);
                index.RemoveAt(0);
            }

            SaveIndex();
            Writes++;
        });
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"store caves={Count} reads={Reads} writes={Writes}{(Failure is null ? "" : $" failure=\"{Failure}\"")}");

    public void Dispose() => store.Dispose();

    private void Change(Action change)
    {
        try
        {
            change();
        }
        catch (EngineCallException refused)
        {
            Failure = refused.Message;
        }
    }

    private void Delete(string key) => engine.Persistence.Delete(new PersistenceDeleteRequest(store, key, PersistenceRevisionGuard.Any, 0));

    private void SaveIndex() => engine.Persistence.Save(new PersistenceSaveRequest(store, IndexKey, PersistenceRevisionGuard.Any, 0,
        System.Text.Encoding.UTF8.GetBytes(string.Join('\n', index))));
}
