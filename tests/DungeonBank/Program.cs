using System.Diagnostics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Tests;

// A bank of dungeon seeds, drawn and route-checked: how many come out walkable on their first
// candidate, how many after redrawing, and why the rest fail. Pass a seed count to draw more, or
// "section <seed>" to print one dungeon's side sections.
if (args is ["render", string renderSeed, string output])
{
    (DungeonLayout drawn, DungeonPlan drawnPlan, DungeonVerdict drawnVerdict) = CarveAndStamp.Generate(ulong.Parse(renderSeed, System.Globalization.CultureInfo.InvariantCulture));
    Cutaway.Write(output, drawn.Volume, drawnPlan, DungeonWalk.Reachable(drawn.Volume, drawnPlan.Arrival), slices: 6);
    Console.WriteLine($"seed {renderSeed}: {drawnPlan.Mix}, {drawnPlan.Floors} floors, attempt {drawnPlan.Attempt}, {drawnVerdict.Reason} -> {output}");
    return 0;
}

if (args is ["section", string seedText])
{
    Section(ulong.Parse(seedText, System.Globalization.CultureInfo.InvariantCulture));
    return 0;
}

int seeds = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 120;
Dictionary<string, int> firstFailures = [];
Dictionary<DungeonMix, int> mixes = [];
int firstTry = 0;
int walkable = 0;
long attempts = 0;
Stopwatch clock = Stopwatch.StartNew();
for (ulong seed = 1; seed <= (ulong)seeds; seed++)
{
    (DungeonLayout layout, DungeonPlan plan, DungeonVerdict verdict) = CarveAndStamp.Generate(seed);
    attempts += plan.Attempt + 1;
    mixes[plan.Mix] = mixes.GetValueOrDefault(plan.Mix) + 1;
    if (verdict.Walkable)
    {
        walkable++;
        firstTry += plan.Attempt == 0 ? 1 : 0;
    }

    if (plan.Attempt > 0 || !verdict.Walkable)
    {
        string reason = CarveAndStamp.Candidate(seed, 0).Verdict.Reason;
        firstFailures[reason] = firstFailures.GetValueOrDefault(reason) + 1;
    }

    // Determinism: the same seed draws the same dungeon.
    if (seed <= 5)
    {
        (DungeonLayout again, _, _) = CarveAndStamp.Generate(seed);
        Check.That(again.Volume.Count(BlockId.Air) == layout.Volume.Count(BlockId.Air) && again.Arrival == layout.Arrival,
            $"seed {seed} must draw the same dungeon twice");
    }
}

double rate = (double)walkable / seeds;
Console.WriteLine($"carve-and-stamp: {walkable}/{seeds} walkable ({rate:P0}); {firstTry} on the first candidate; {attempts} candidates drawn in {clock.Elapsed.TotalSeconds:F1} s");
Console.WriteLine($"mix: {string.Join(", ", mixes.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} {pair.Value}"))}");
Console.WriteLine($"first-candidate failures: {(firstFailures.Count == 0 ? "none" : string.Join("; ", firstFailures.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} x{pair.Value}")))}");
Check.That(rate >= 0.95, $"at least 95% of seeds must yield a walkable dungeon, {rate:P0} did");
return Check.Finish("DungeonBank");

static void Section(ulong seed)
{
    (DungeonLayout layout, DungeonPlan plan, DungeonVerdict verdict) = CarveAndStamp.Generate(seed);
    DungeonVolume volume = layout.Volume;
    Console.WriteLine($"seed {seed}: {plan.Mix}, {plan.Floors} floors, attempt {plan.Attempt}, {verdict.Reason}; arrival {plan.Arrival}, breach {plan.Breach}, loot {plan.Loot}");
    Console.WriteLine("arrival column: " + string.Join(" ", Enumerable.Range(plan.Arrival.Y - 3, 8).Select(y => $"{y}:{volume.At(plan.Arrival.X, y, plan.Arrival.Z)}")));
    HashSet<DungeonCell> reach = DungeonWalk.Reachable(volume, plan.Arrival);
    foreach (int z in new[] { plan.Breach.Z, plan.Loot.Z, plan.Arrival.Z })
    {
        Console.WriteLine($"-- section at z={z} (x across, y up; # rock, B building, : walkable, space air)");
        for (int y = volume.SizeY - 1; y >= 0; y--)
        {
            char[] row = new char[volume.SizeX];
            for (int x = 0; x < volume.SizeX; x++)
            {
                BlockId block = volume.At(x, y, z);
                row[x] = reach.Contains(new DungeonCell(x, y, z)) ? ':'
                    : block == BlockId.Air ? ' '
                    : block is BlockId.Brick or BlockId.Planks or BlockId.Cobblestone ? 'B'
                    : '#';
            }

            Console.WriteLine($"{y,3} {new string(row)}");
        }
    }
}

/// <summary>
/// A picture of one dungeon for a person to judge: vertical slices through it, front to back, and a
/// map from above of every walkable floor coloured by height, with the arrival, breach and loot
/// marked. Written as a PNG.
/// </summary>
internal static class Cutaway
{
    private const int Scale = 3;
    private static readonly byte[] Rock = [52, 50, 48];
    private static readonly byte[] Strata = [74, 62, 50];
    private static readonly byte[] Building = [150, 74, 60];
    private static readonly byte[] Slab = [176, 148, 102];
    private static readonly byte[] Air = [14, 14, 18];
    private static readonly byte[] Walk = [96, 190, 120];
    private static readonly byte[] Mark = [250, 220, 90];

    internal static void Write(string path, DungeonVolume volume, DungeonPlan plan, HashSet<DungeonCell> reach, int slices)
    {
        int sliceWidth = volume.SizeX * Scale;
        int sliceHeight = volume.SizeY * Scale;
        int columns = 3;
        int rows = (slices + columns - 1) / columns;
        int mapSize = volume.SizeX * Scale;
        int width = (columns * (sliceWidth + 6)) + mapSize + 6;
        int height = Math.Max(rows * (sliceHeight + 6), volume.SizeZ * Scale);
        byte[] rgb = new byte[width * height * 3];
        DungeonCell[] marks = [plan.Arrival, plan.Breach, plan.Loot];
        for (int slice = 0; slice < slices; slice++)
        {
            int z = (int)((slice + 0.5) * volume.SizeZ / slices);
            int ox = (slice % columns) * (sliceWidth + 6);
            int oy = (slice / columns) * (sliceHeight + 6);
            for (int x = 0; x < volume.SizeX; x++)
            {
                for (int y = 0; y < volume.SizeY; y++)
                {
                    byte[] colour = Colour(volume.At(x, y, z), reach.Contains(new DungeonCell(x, y, z)));
                    if (marks.Any(mark => Math.Abs(mark.X - x) <= 1 && Math.Abs(mark.Y - y) <= 1 && Math.Abs(mark.Z - z) <= 3))
                    {
                        colour = Mark;
                    }

                    Fill(rgb, width, ox + (x * Scale), oy + ((volume.SizeY - 1 - y) * Scale), colour);
                }
            }
        }

        // From above: the highest walkable floor in each column, brighter the higher it is.
        int mx = columns * (sliceWidth + 6);
        int top = reach.Count == 0 ? 1 : reach.Max(cell => cell.Y);
        int bottom = reach.Count == 0 ? 0 : reach.Min(cell => cell.Y);
        Dictionary<(int, int), int> highest = [];
        foreach (DungeonCell cell in reach)
        {
            highest[(cell.X, cell.Z)] = Math.Max(highest.GetValueOrDefault((cell.X, cell.Z), int.MinValue), cell.Y);
        }

        for (int x = 0; x < volume.SizeX; x++)
        {
            for (int z = 0; z < volume.SizeZ; z++)
            {
                byte[] colour = Air;
                if (highest.TryGetValue((x, z), out int y))
                {
                    double t = (y - bottom) / (double)Math.Max(1, top - bottom);
                    colour = [(byte)(40 + (200 * t)), (byte)(90 + (120 * t)), (byte)(200 - (150 * t))];
                }

                if (marks.Any(mark => Math.Abs(mark.X - x) <= 1 && Math.Abs(mark.Z - z) <= 1))
                {
                    colour = Mark;
                }

                Fill(rgb, width, mx + (x * Scale), z * Scale, colour);
            }
        }

        File.WriteAllBytes(path, Png(width, height, rgb));
    }

    private static byte[] Colour(BlockId block, bool walkable) => walkable ? Walk : block switch
    {
        BlockId.Air => Air,
        BlockId.Brick => Building,
        BlockId.Planks or BlockId.Cobblestone => Slab,
        BlockId.Dirt or BlockId.Gravel => Strata,
        _ => Rock,
    };

    private static void Fill(byte[] rgb, int width, int px, int py, byte[] colour)
    {
        for (int dy = 0; dy < Scale; dy++)
        {
            for (int dx = 0; dx < Scale; dx++)
            {
                int index = (((py + dy) * width) + px + dx) * 3;
                rgb[index] = colour[0];
                rgb[index + 1] = colour[1];
                rgb[index + 2] = colour[2];
            }
        }
    }

    private static byte[] Png(int width, int height, byte[] rgb)
    {
        using MemoryStream raw = new();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgb, y * width * 3, width * 3);
        }

        using MemoryStream packed = new();
        using (System.IO.Compression.ZLibStream zlib = new(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            raw.Position = 0;
            raw.CopyTo(zlib);
        }

        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(typed);
        byte[] crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, System.IO.Hashing.Crc32.HashToUInt32(typed));
        stream.Write(crc);
    }
}
