using System.Diagnostics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Tests;

// A bank of dungeon seeds, drawn and route-checked: how many come out walkable on their first
// candidate, how many after redrawing, and why the rest fail. Pass a seed count to draw more, or
// "section <seed>" to print one dungeon's side sections, or "engine <a|b|c> <seeds> [sweep] [quiet]"
// for the Engine's route check in detail.
if (args is ["render", string renderSeed, string output, ..] && args.Length <= 4)
{
    ulong chosen = ulong.Parse(renderSeed, System.Globalization.CultureInfo.InvariantCulture);
    string which = args.Length == 4 ? args[3] : "a";
    bool sculpted = which != "a";
    (DungeonVolume shown, DungeonPlan drawnPlan, DungeonVerdict drawnVerdict) = which switch
    {
        "c" => Sculpted(chosen),
        "b" => Modular(chosen),
        _ => Carved(chosen),
    };
    HashSet<DungeonCell> walkableCells = DungeonWalk.Reachable(shown, drawnPlan.Arrival);
    Cutaway.Write(output, shown, drawnPlan, walkableCells, slices: 6);
    Cutaway.Isometric(output.Replace(".png", "-iso.png", StringComparison.Ordinal), shown, drawnPlan, walkableCells);
    _ = sculpted;
    Console.WriteLine($"seed {renderSeed} ({which.ToUpperInvariant()}): {drawnPlan.Mix}, {drawnPlan.Floors} floors, attempt {drawnPlan.Attempt}, {drawnVerdict.Reason} -> {output}");
    return 0;
}

if (args is ["moduledump", string moduleName])
{
    DungeonModuleShape shape = ((DungeonModuleShape[])[DungeonModules.Arrival, .. DungeonModules.CaveRoute, .. DungeonModules.BuildingRooms, DungeonModules.Breach, DungeonModules.Vault]).Single(m => m.Name == moduleName);
    var (volume, sockets) = ModularDungeon.Isolated(shape);
    foreach (var (socket, stand) in sockets)
    {
        HashSet<DungeonCell> reach = DungeonWalk.Reachable(volume, stand);
        Console.WriteLine($"from {socket.Face} stand {stand}: {reach.Count} cells, y {reach.Min(c => c.Y)}..{reach.Max(c => c.Y)}");
    }

    foreach ((int cx, int cz) in new[] { (21, 14), (21, 13), (21, 12), (21, 11), (20, 10) })
    {
        Console.WriteLine($"column {cx},{cz}: " + string.Join(" ", Enumerable.Range(8, 9).Select(y => $"{y}:{volume.At(cx, y, cz).ToString()[..2]}{(DungeonWalk.Standable(volume, cx, y, cz) ? "*" : "")}")));
    }

    var (_, first) = sockets[0];
    HashSet<DungeonCell> walked = DungeonWalk.Reachable(volume, first);
    HashSet<DungeonCell> walkedBack = DungeonWalk.Reachable(volume, sockets[^1].Stand);
    foreach (int level in new[] { 12, 13, 14 })
    {
        Console.WriteLine($"-- level y={level} from above (x across, z down); T top-reach, B bottom-reach, # solid");
        for (int z = 26; z >= 6; z--)
        {
            char[] row = new char[24];
            for (int x = 6; x < 30; x++)
            {
                DungeonCell cell = new(x, level, z);
                row[x - 6] = walked.Contains(cell) ? 'T' : walkedBack.Contains(cell) ? 'B' : volume.At(x, level, z) == BlockId.Air ? ' ' : '#';
            }

            Console.WriteLine($"{z,3} {new string(row)}");
        }
    }

    for (int y = 30; y >= 31; y--)
    {
        char[] row = new char[40];
        for (int x = 4; x < 44; x++)
        {
            int z = 12;
            row[x - 4] = walked.Contains(new DungeonCell(x, y, z)) ? ':' : volume.At(x, y, z) == BlockId.Air ? ' ' : '#';
        }

        Console.WriteLine($"{y,3} {new string(row)}  (z=12)");
    }

    return 0;
}

if (args is ["intrusion", string intrusionSeed])
{
    ulong chosenSeed = ulong.Parse(intrusionSeed, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
    var generated = ModularDungeon.Generate(chosenSeed);
    DungeonVolume building = generated.Layout.Volume;
    RockDensity rock = generated.Layout.Rock!;
    Dictionary<string, int> where = [];
    foreach (PlacedModule module in generated.Modules.Where(m => m.Shape.Kind == ModuleKind.Building))
    {
        foreach ((int cx, int cy, int cz) in module.Cells())
        {
            for (int x = cx * 8 + 1; x < cx * 8 + 7; x++)
            {
                for (int z = cz * 8 + 1; z < cz * 8 + 7; z++)
                {
                    for (int dy = 1; dy < 6; dy++)
                    {
                        int y = (cy * 6) + ModularDungeon.BaseY + dy;
                        if (building.At(x, y, z) == BlockId.Air && rock.At(x, y, z) < 0f)
                        {
                            bool edge = x == cx * 8 + 1 || x == cx * 8 + 6 || z == cz * 8 + 1 || z == cz * 8 + 6;
                            string key = $"{module.Shape.Name} storey-height {dy} {(edge ? "by a wall" : "mid-room")}";
                            where[key] = where.GetValueOrDefault(key) + 1;
                        }
                    }
                }
            }
        }
    }

    Console.WriteLine(string.Join("\n", where.OrderByDescending(pair => pair.Value).Take(14).Select(pair => $"{pair.Value,5}  {pair.Key}")));
    return 0;
}

if (args is ["modulecheck"])
{
    foreach (DungeonModuleShape shape in (DungeonModuleShape[])[DungeonModules.Arrival, .. DungeonModules.CaveRoute, .. DungeonModules.BuildingRooms, DungeonModules.Breach, DungeonModules.Vault])
    {
        var (volume, sockets) = ModularDungeon.Isolated(shape);
        List<string> broken = [];
        foreach (var (from, fromStand) in sockets)
        {
            HashSet<DungeonCell> reach = DungeonWalk.Reachable(volume, fromStand);
            foreach (var (to, toStand) in sockets.Where(other => other.Socket != from))
            {
                if (!reach.Contains(toStand))
                {
                    broken.Add($"{from.X},{from.Y},{from.Z}{from.Face.ToString()[0]} -> {to.X},{to.Y},{to.Z}{to.Face.ToString()[0]}");
                }
            }
        }

        Console.WriteLine($"{shape.Name}: {(broken.Count == 0 ? "every socket reaches every other" : string.Join("; ", broken))}");
    }

    return 0;
}

if (args is ["modules", string moduleSeed])
{
    var result = ModularDungeon.Candidate(ulong.Parse(moduleSeed, System.Globalization.CultureInfo.InvariantCulture), 0);
    Console.WriteLine(result.Verdict.Reason);
    foreach (PlacedModule module in result.Modules)
    {
        Console.WriteLine($"{module.Shape.Name} turns={module.Turns} at {module.X},{module.Y},{module.Z} sockets: {string.Join(" ", module.Sockets().Select(s => $"{s.X},{s.Y},{s.Z}{s.Face.ToString()[0]}{s.Kind.ToString()[0]}"))}");
    }

    return 0;
}

if (args is ["modular", string modularCount])
{
    int count = int.Parse(modularCount, System.Globalization.CultureInfo.InvariantCulture);
    Dictionary<string, int> firstReasons = [];
    Dictionary<string, int> pieces = [];
    int good = 0;
    long candidates = 0;
    Stopwatch modularClock = Stopwatch.StartNew();
    for (ulong seed = 1; seed <= (ulong)count; seed++)
    {
        var result = ModularDungeon.Generate(seed);
        good += result.Verdict.Walkable ? 1 : 0;
        candidates += result.Plan.Attempt + 1;
        foreach (PlacedModule module in result.Modules)
        {
            pieces[module.Shape.Name] = pieces.GetValueOrDefault(module.Shape.Name) + 1;
        }

        string first = ModularDungeon.Candidate(seed, 0).Verdict.Reason;
        firstReasons[first] = firstReasons.GetValueOrDefault(first) + 1;
    }

    Console.WriteLine($"modular: {good}/{count} walkable, {candidates} candidates in {modularClock.Elapsed.TotalSeconds:F1} s");
    Console.WriteLine($"first candidates: {string.Join("; ", firstReasons.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} x{pair.Value}"))}");
    Console.WriteLine($"pieces: {string.Join(", ", pieces.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value}"))}");
    return 0;
}

if (args is ["sculpted", string sculptedCount])
{
    int count = int.Parse(sculptedCount, System.Globalization.CultureInfo.InvariantCulture);
    Dictionary<string, int> reasons = [];
    int good = 0;
    Stopwatch sculptClock = Stopwatch.StartNew();
    for (ulong seed = 1; seed <= (ulong)count; seed++)
    {
        (_, DungeonPlan sculptedPlan, DungeonVerdict sculptedVerdict) = Sculpted(seed);
        good += sculptedVerdict.Walkable ? 1 : 0;
        if (!sculptedVerdict.Walkable || sculptedPlan.Attempt > 0)
        {
            reasons[sculptedVerdict.Reason] = reasons.GetValueOrDefault(sculptedVerdict.Reason) + 1;
        }
    }

    Console.WriteLine($"sculpted-cave: {good}/{count} walkable in {sculptClock.Elapsed.TotalSeconds:F1} s; {string.Join("; ", reasons.Select(pair => $"{pair.Key} x{pair.Value}"))}");
    return 0;
}

if (args is ["engine", string engineApproach, string engineSeeds, ..])
{
    // engine <a|b|c> <seeds> [sweep] [quiet]: the Engine's route check over a bank, for the player or a sweep of bodies.
    IReadOnlyList<NavigationProfile> profiles = args.Contains("sweep") ? EngineRouteBank.Sweep : [NavigationProfile.Player];
    EngineRouteBank.Run(engineApproach, 1UL, int.Parse(engineSeeds, System.Globalization.CultureInfo.InvariantCulture), profiles, verbose: !args.Contains("quiet"));
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

// Every authored module, on its own with every socket opened, connects each socket to every other.
foreach (DungeonModuleShape shape in (DungeonModuleShape[])[DungeonModules.Arrival, .. DungeonModules.CaveRoute, .. DungeonModules.BuildingRooms, DungeonModules.Breach, DungeonModules.Vault])
{
    var (isolated, sockets) = ModularDungeon.Isolated(shape);
    bool connected = sockets.All(from => DungeonWalk.Reachable(isolated, from.Stand) is var reach && sockets.All(to => reach.Contains(to.Stand)));
    Check.That(connected, $"module {shape.Name} must connect every socket to every other");
}

// Approach B: assembled from those modules, sculpted, still walkable.
const int ModularSeeds = 40;
int modularWalkable = 0;
Stopwatch modularBank = Stopwatch.StartNew();
for (ulong seed = 1; seed <= ModularSeeds; seed++)
{
    modularWalkable += ModularDungeon.Generate(seed).Verdict.Walkable ? 1 : 0;
}

Console.WriteLine($"modular: {modularWalkable}/{ModularSeeds} walkable in {modularBank.Elapsed.TotalSeconds:F1} s");
Check.That(modularWalkable >= ModularSeeds * 0.95, $"at least 95% of modular dungeons must be walkable, {modularWalkable}/{ModularSeeds} were");

// Approach C must stay walkable after its rock is sculpted, on a smaller bank (sculpting costs more).
const int SculptedSeeds = 40;
int sculptedWalkable = 0;
Stopwatch sculptedClock = Stopwatch.StartNew();
for (ulong seed = 1; seed <= SculptedSeeds; seed++)
{
    sculptedWalkable += SculptedCave.Generate(seed).Verdict.Walkable ? 1 : 0;
}

Console.WriteLine($"sculpted-cave: {sculptedWalkable}/{SculptedSeeds} walkable in {sculptedClock.Elapsed.TotalSeconds:F1} s");
Check.That(sculptedWalkable >= SculptedSeeds * 0.95, $"at least 95% of sculpted dungeons must stay walkable, {sculptedWalkable}/{SculptedSeeds} did");

// The Engine's own route check, headless, over a few dungeons of each approach for the player's
// body. It is reported, not yet required: the Engine's collision navigation refuses sloped rock
// floors and full-height stair risers (rusty-engine #9032). Every dungeon must still build and
// publish navigation the Engine can query.
const int EngineSeeds = 8;
foreach (string reported in (string[])["a", "b", "c"])
{
    IReadOnlyList<EngineRouteBank.Outcome> outcomes = EngineRouteBank.Run(reported, 1UL, EngineSeeds, [NavigationProfile.Player], verbose: false);
    Check.That(outcomes.Count == EngineSeeds && outcomes.All(outcome => outcome.Verdict.WalkableCells > 0),
        $"every {reported} dungeon must build in the Engine and publish navigation with supports");
}
return Check.Finish("DungeonBank");

static (DungeonVolume, DungeonPlan, DungeonVerdict) Carved(ulong seed)
{
    (DungeonLayout layout, DungeonPlan plan, DungeonVerdict verdict) = CarveAndStamp.Generate(seed);
    return (layout.Volume, plan, verdict);
}

static (DungeonVolume, DungeonPlan, DungeonVerdict) Sculpted(ulong seed)
{
    (_, DungeonPlan plan, DungeonVerdict verdict, DungeonVolume walkable) = SculptedCave.Generate(seed);
    return (walkable, plan, verdict);
}

static (DungeonVolume, DungeonPlan, DungeonVerdict) Modular(ulong seed)
{
    var result = ModularDungeon.Generate(seed);
    return (result.Walkable, result.Plan, result.Verdict);
}

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

    /// <summary>
    /// Every walkable floor cell seen from above and to the side, drawn back to front: building
    /// floors warm, cave floors cool, brighter the higher they are, key places in yellow. It shows
    /// what slices cannot - which floors lie over which.
    /// </summary>
    internal static void Isometric(string path, DungeonVolume volume, DungeonPlan plan, HashSet<DungeonCell> reach)
    {
        const int Tile = 5;
        const int Rise = 7;
        int width = (volume.SizeX + volume.SizeZ) * Tile + 20;
        int height = ((volume.SizeX + volume.SizeZ) * Tile / 2) + (volume.SizeY * Rise) + 20;
        byte[] rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i += 3)
        {
            rgb[i] = 14;
            rgb[i + 1] = 14;
            rgb[i + 2] = 18;
        }

        int top = reach.Count == 0 ? 1 : reach.Max(cell => cell.Y);
        int bottom = reach.Count == 0 ? 0 : reach.Min(cell => cell.Y);
        DungeonCell[] marks = [plan.Arrival, plan.Breach, plan.Loot];
        foreach (DungeonCell cell in reach.OrderBy(cell => cell.X + (volume.SizeZ - cell.Z)).ThenBy(cell => cell.Y))
        {
            double t = (cell.Y - bottom) / (double)Math.Max(1, top - bottom);
            bool built = volume.At(cell.X, cell.Y - 1, cell.Z) is BlockId.Planks or BlockId.Cobblestone or BlockId.Brick;
            byte[] colour = built
                ? [(byte)(110 + (130 * t)), (byte)(80 + (100 * t)), (byte)(60 + (60 * t))]
                : [(byte)(50 + (90 * t)), (byte)(90 + (110 * t)), (byte)(120 + (120 * t))];
            if (marks.Any(mark => Math.Abs(mark.X - cell.X) <= 1 && Math.Abs(mark.Z - cell.Z) <= 1 && Math.Abs(mark.Y - cell.Y) <= 1))
            {
                colour = Mark;
            }

            int sx = 10 + ((cell.X + (volume.SizeZ - cell.Z)) * Tile);
            int sy = 10 + ((cell.X + cell.Z) * Tile / 2) + ((volume.SizeY - cell.Y) * Rise);
            for (int dy = 0; dy < Tile; dy++)
            {
                for (int dx = -Tile + (2 * dy); dx <= Tile - (2 * dy); dx++)
                {
                    foreach (int row in (ReadOnlySpan<int>)[sy - dy, sy + dy])
                    {
                        int px = sx + dx;
                        if (px >= 0 && px < width && row >= 0 && row < height)
                        {
                            int index = ((row * width) + px) * 3;
                            rgb[index] = colour[0];
                            rgb[index + 1] = colour[1];
                            rgb[index + 2] = colour[2];
                        }
                    }
                }
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
