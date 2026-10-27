using System.Numerics;
using System.Reflection;
using CraftSurvive.Game.Modules.Chemistry;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Tests;
using RustyChemistry.Content;
using RustyChemistry.Simulation;

// The chemistry adapter's rules and the product's own pack (#10293), checked without a host: the authored
// content resolves, write-back keeps the terrain overlay's reserve, the weather becomes ambient, and grass fire
// behaves as the slice promises on small meadows of the real pack.

ChemistrySettings settings = ChemistrySettings.Parse(Resource("Chemistry.adapter.json"));
ChemistryPack pack = ChemistryPack.Read(Resource("Chemistry.pack.json"));
BlockChemistry blocks = new(settings, pack);
int heat = pack.Quantity("heat"), moisture = pack.Quantity("moisture"), smoke = pack.Quantity("smoke");
int soil = pack.Material("soil"), stone = pack.Material("stone"), air = pack.Material("air");
int grass = pack.Material("grass"), dryGrass = pack.Material("dry-grass"), burning = pack.Material("burning-grass");
int burnt = pack.Material("burnt-ground");

Check.Section("content", () =>
{
    Check.Equal(settings.PackPath, "chemistry/pack.json", "the adapter names the pack beside it");
    Check.Equal(grass, (int)blocks.MaterialOf((ushort)BlockId.Grass), "grass loads as grass");
    Check.Equal(dryGrass, (int)blocks.MaterialOf((ushort)BlockId.DryGrass), "dry grass loads as dry grass");
    Check.Equal(stone, (int)blocks.MaterialOf((ushort)BlockId.Bedrock), "an unnamed block takes the default");
    Check.Equal(pack.Material("water"), (int)blocks.CellMaterial((ushort)BlockId.Air, 4, 5.5), "air below a water surface is water");
    Check.Equal(air, (int)blocks.CellMaterial((ushort)BlockId.Air, 6, 5.5), "air above it stays air");
    Check.That(blocks.WriteBacks.TryGetValue(burnt, out WriteBackSetting? rule) && rule.Block == BlockId.BurntGround
        && rule.Over.Contains(BlockId.Grass) && rule.Over.Contains(BlockId.DryGrass), "burnt ground writes back as the scar block over grass");
    Check.Equal(burnt, (int)blocks.MaterialOf((ushort)BlockId.BurntGround), "a scar loads as burnt ground, which does not burn again");
    Check.That(blocks.Burning.SetEquals([burning]), "burning grass is what shows as fire");
    Check.Throws<InvalidDataException>(() => ChemistrySettings.Parse(Resource("Chemistry.adapter.json").Replace("\"Grass\": \"grass\"", "\"Grasss\": \"grass\"")),
        "a misspelt block fails at load");
});

Check.Section("step hand-off", () =>
{
    // R10384-2: a burn the worker stepped reaches the write-back exactly once, even when the player left for a separate
    // space (the update only waits there) while it ran, or after it had already finished.
    foreach (bool finishedFirst in new[] { false, true })
    {
        ChemistryField field = Meadow(dryGrass);
        Ignite(field, 24, 24);
        StepHandoff<List<MaterialChange>> handoff = new();
        handoff.Start(() =>
        {
            List<MaterialChange> changes = [];
            for (int step = 0; step < 150; step++)
            {
                field.Step();
                changes.AddRange(field.MaterialChanges);
            }

            return changes;
        });
        if (finishedFirst)
        {
            SpinWait.SpinUntil(() => !handoff.Running);
        }

        handoff.Wait();
        bool held = handoff.Holding && !handoff.Running;
        bool took = handoff.TryTake(out List<MaterialChange> result);
        int wanted = WriteBack.Wanted(result, blocks).Count();
        bool again = handoff.TryTake(out _);
        Check.That(held && took && !again && wanted > 0 && wanted == result.Count(change => change.To == burnt),
            $"a step run kept over a separate space is taken once, its {wanted} burnt cells all wanted for write-back (finished first: {finishedFirst})");
    }

    StepHandoff<int> dropped = new();
    dropped.Start(() => 7);
    dropped.Discard();
    Check.That(!dropped.Holding && !dropped.TryTake(out _), "a restart drops the run of the field it replaced");
});

Check.Section("write-back budget", () =>
{
    WriteBackBudgetSetting budget = new(KeepFreeEntries: 32768, CellsPerWrite: 256, WriteSeconds: 1);
    Check.Equal(256, WriteBack.Allowed(0, 65536, budget), "a write is capped at its batch");
    Check.Equal(100, WriteBack.Allowed(32768 - 100, 65536, budget), "it shrinks as the overlay nears the reserve");
    Check.Equal(0, WriteBack.Allowed(40000, 65536, budget), "nothing is written past the reserve");

    WriteBackSetting rule = blocks.WriteBacks[burnt];
    (Cell, WriteBackSetting)[] waiting = [(new Cell(1, 1, 1), rule), (new Cell(2, 1, 1), rule), (new Cell(3, 1, 1), rule)];
    Dictionary<Cell, BlockId> world = new() { [new(1, 1, 1)] = BlockId.Grass, [new(2, 1, 1)] = BlockId.Cobblestone, [new(3, 1, 1)] = BlockId.DryGrass,
        [new(1, 0, 1)] = BlockId.Dirt, [new(2, 0, 1)] = BlockId.Dirt, [new(3, 0, 1)] = BlockId.Stone };
    Dictionary<BlockId, List<Cell>> chosen = WriteBack.Choose(waiting, cell => (ushort)world[cell], allowed: 8, out int took);
    Check.That(chosen.Count == 1 && chosen[BlockId.BurntGround].SequenceEqual([new Cell(1, 1, 1), new Cell(1, 0, 1), new Cell(3, 1, 1)]),
        "only cells the world still holds as grass are written, as the scar, with the dirt beneath; a player's edit since wins");
    Check.Equal(3, took, "every waiting cell was dealt with");
    Check.Equal(1, WriteBack.Choose(waiting, cell => (ushort)world[cell], allowed: 1, out _)[BlockId.BurntGround].Count, "a write takes no more than allowed");

    // R10296-1: batches drain a long burn whole. Each grass cell over dirt takes two of the allowance; none is lost.
    Dictionary<Cell, BlockId> meadow = [];
    Queue<(Cell, WriteBackSetting)> queue = new();
    for (int x = 0; x < 300; x++)
    {
        meadow[new(x, 1, 0)] = BlockId.Grass;
        meadow[new(x, 0, 0)] = BlockId.Dirt;
        queue.Enqueue((new Cell(x, 1, 0), rule));
    }

    HashSet<Cell> writtenCells = [];
    int writes = 0;
    bool withinAllowance = true;
    while (queue.Count > 0 && writes < 100)
    {
        Dictionary<BlockId, List<Cell>> batch = WriteBack.Choose(queue.Take(256), cell => (ushort)meadow[cell], allowed: 256, out int dealt);
        for (int index = 0; index < dealt; index++) queue.Dequeue();
        int cellsWritten = batch.Values.Sum(cells => cells.Count);
        withinAllowance &= cellsWritten <= 256;
        foreach (Cell cell in batch.Values.SelectMany(cells => cells))
        {
            writtenCells.Add(cell);
            meadow[cell] = BlockId.BurntGround;
        }

        writes++;
    }

    Check.That(queue.Count == 0 && writtenCells.Count == 600 && withinAllowance && writes == 3,
        $"300 burnt cells over dirt drain in batches of 256 cells, each surface with its ground beneath ({writtenCells.Count} cells in {writes} writes)");
});

Check.Section("scars heal", () =>
{
    ScarSetting scars = settings.Scars;
    Check.That(scars.Block == BlockId.BurntGround && scars.RegrowDays > 0 && scars.Earliest is > 0 and < 1, "a scar is the burnt-ground block and grows back over days");
    double regrow = scars.RegrowDays * 24;
    int cells = 0, early = 0, byEnd = 0, late = 0;
    for (long x = 0; x < 32; x++)
    {
        for (long z = 0; z < 32; z++)
        {
            cells++;
            if (ScarHealing.Due(x, z, 100, 100 + (regrow * scars.Earliest * 0.99), regrow, scars.Earliest)) early++;
            if (ScarHealing.Due(x, z, 100, 100 + (regrow * 0.6), regrow, scars.Earliest)) late++;
            if (ScarHealing.Due(x, z, 100, 100 + regrow, regrow, scars.Earliest)) byEnd++;
        }
    }

    Check.That(early == 0 && byEnd == cells, $"no cell heals before the earliest share and every cell by the whole time ({early}, {byEnd} of {cells})");
    Check.That(late > cells / 10 && late < cells * 9 / 10, $"part way, a scar has healed in patches ({late} of {cells})");
    Check.That(ScarHealing.Due(3, 5, 100, 100 + (regrow * 0.6), regrow, scars.Earliest) == ScarHealing.Due(3, 5, 100, 100 + (regrow * 0.6), regrow, scars.Earliest),
        "a column's turn is its own, the same every time");
    Check.Throws<InvalidDataException>(() => ChemistrySettings.Parse(Resource("Chemistry.adapter.json").Replace("\"earliest\": 0.4", "\"earliest\": 1.5")),
        "an earliest share past the whole fails at load");
});

Check.Section("weather as ambient", () =>
{
    AmbientSetting ambient = settings.Ambient;
    ChemistryAmbient mild = ChemistryAmbient.From(cold: 0, wind: 0, precipitation: 0, Vector2.UnitX, snowCover: 0, ambient);
    ChemistryAmbient bitter = ChemistryAmbient.From(cold: 1, wind: 1, precipitation: 0.7, Vector2.UnitY, snowCover: 1.4, ambient);
    Check.That(mild.Heat == ambient.WarmHeat && bitter.Heat == ambient.ColdHeat, "the Cold channel moves air heat from warm to bitter");
    Check.That(mild.Drift == Vector3.Zero && Vector3.Distance(bitter.Drift, new Vector3(0, 0, ambient.WindCellsPerSecond)) < 1e-4f,
        "full Wind drifts along the flow at the authored speed");
    Check.That(Math.Abs(bitter.Rain - 0.7f) < 1e-6f, "Precipitation becomes the rain modulator");
    Check.That(mild.Snow == 0f && bitter.Snow == 1f, "the snow cover becomes the snow modulator, no more than whole");
    Check.That((mild with { Snow = 0.5f }).Differs(mild), "snow laid since is applied, even in still weather");
    Check.That(mild.Differs(mild with { Rain = 0.01f, Snow = 0f }) && mild.Differs(mild with { Snow = 0.04f }),
        "rain or snow stopping is applied, however little was left");
    Check.That(!(mild with { Heat = mild.Heat + 0.2f }).Differs(mild) && bitter.Differs(mild), "weather that barely moved is not applied again");
});

Check.Section("field window", () =>
{
    List<Cell> chunks = FieldWindow.Around(0, 0, 1, (_, _) => new SurfaceBand(20, 20));
    Check.Equal(18, chunks.Count, "nine columns, each its surface chunk and the one above");
    Check.That(chunks[0] == new Cell(0, 16, 0) && chunks[1] == new Cell(0, 32, 0), "the player's own column comes first");
    Check.Equal((-1, 2), FieldWindow.ColumnOf(new Vector3(-0.5f, 3, 40)), "columns floor below zero");
    Check.Equal(2, FieldWindow.ColumnsApart(new Cell(-32, 0, 16), 0, 0), "columns apart is the larger axis");
    List<Cell> withFire = FieldWindow.Around(0, 0, 1, 2, [(1, 0), (5, 5)], (_, _) => new SurfaceBand(20, 20));
    Check.Equal(24, withFire.Count, "a burning column at the window's edge brings in its outer neighbours out to the burning reach");
    Check.That(withFire.All(chunk => FieldWindow.ColumnsApart(chunk, 0, 0) <= 2), "but never past it");
    Check.That(withFire.Take(18).All(chunk => FieldWindow.ColumnsApart(chunk, 0, 0) <= 1), "and the player's own window still loads first");

    // A slope rising one cell in eight across a chunk-height boundary: its midpoint stands at 16, its foot at 15.
    static long Slope(long x, long z) => 15 + (x / 8);
    SurfaceBand slope = FieldWindow.BandOf(0, 0, Slope);
    Check.Equal(new SurfaceBand(15, 16), slope, "a column's band runs from its lowest surface cell to its highest");
    List<Cell> sloped = FieldWindow.Around(0, 0, 0, (x, z) => FieldWindow.BandOf(x, z, Slope));
    Check.That(sloped.SequenceEqual([new Cell(0, 0, 0), new Cell(0, 16, 0), new Cell(0, 32, 0)]),
        "a slope across a chunk boundary loads both chunks holding its surface, and the one above");
    Check.That(sloped.Any(chunk => chunk.Y <= 15 && chunk.Y + ChemistryField.ChunkSize > 15), "so the foot of the slope, (0, 15, 0), is in the field");
});

Check.Section("grass fire", () =>
{
    ChemistryField dry = Meadow(dryGrass);
    Ignite(dry, 24, 24);
    Run(dry, 200);
    int dryBurnt = Count(dry, burning) + Count(dry, burnt);
    Check.That(dryBurnt > 60, $"dry grass catches and spreads ({dryBurnt} cells in 20 s)");

    ChemistryField green = Meadow(grass);
    Ignite(green, 24, 24);
    Run(green, 200);
    int greenBurnt = Count(green, burning) + Count(green, burnt);
    Check.That(greenBurnt > 0 && greenBurnt * 4 < dryBurnt,
        $"green grass must dry before it burns and catches unevenly, so a fire creeps through it ({greenBurnt} against {dryBurnt})");

    ChemistryField gap = Meadow(dryGrass);
    gap.Fill(new(30, 1, 0), new(32, 1, 47), stone);
    gap.MarkOpenColumns();
    Ignite(gap, 24, 24);
    Run(gap, 600);
    Check.Equal(0, Count(gap, burning, fromX: 33) + Count(gap, burnt, fromX: 33), "a stone band three cells wide stops it");

    ChemistryField rained = Meadow(dryGrass);
    rained.SetModulator("rain", 1f);
    Run(rained, 100);
    Ignite(rained, 24, 24);
    Run(rained, 200);
    Check.Equal(0, Count(rained, burning) + Count(rained, burnt), "ten seconds of rain on open grass keeps a torch from lighting it");

    rained.SetModulator("rain", 0f);
    Run(rained, 3000);
    Check.That(rained.Quantity(new(5, 1, 5), moisture) < 0.2f, "five minutes after the rain, open dry grass has dried out");
    ChemistryField drying = Meadow(grass);
    Run(drying, 3000);
    Check.That(drying.Quantity(new(5, 1, 5), moisture) > 0.3f, "living grass keeps its water while dry grass loses it");

    // Snow lying after the snowfall has stopped: no precipitation, only the cover.
    ChemistryField snowed = Meadow(dryGrass);
    snowed.SetModulator("snow", 1f);
    Run(snowed, 100);
    Check.That(snowed.Quantity(new(5, 1, 5), moisture) >= 0.5f, "dry grass under lying snow is wet with no precipitation falling");
    Ignite(snowed, 24, 24);
    Run(snowed, 200);
    Check.Equal(0, Count(snowed, burning) + Count(snowed, burnt), "and a torch cannot light it");

    ChemistryField snowfall = Meadow(dryGrass);
    Ignite(snowfall, 24, 24);
    Run(snowfall, 30);
    int before = Count(snowfall, burning);
    snowfall.SetModulator("snow", 1f);
    Run(snowfall, 30);
    Check.That(before > 0 && Count(snowfall, burning) == 0, $"snow laid over a burning meadow puts the fire out ({before} cells were burning)");

    ChemistryField windy = Meadow(dryGrass);
    windy.Drift = new Vector3(settings.Ambient.WindCellsPerSecond, 0, 0);
    Ignite(windy, 24, 24);
    Run(windy, 200);
    int downwind = Count(windy, burning, fromX: 25) + Count(windy, burnt, fromX: 25);
    int upwind = Count(windy, burning, toX: 23) + Count(windy, burnt, toX: 23);
    Check.That(downwind > upwind, $"wind carries the fire downwind ({downwind} downwind, {upwind} upwind)");
    float smokeDownwind = Smoke(windy, fromX: 25, toX: 47), smokeUpwind = Smoke(windy, fromX: 0, toX: 23);
    Check.That(smokeDownwind > 1.5f * smokeUpwind, $"and its smoke ({smokeDownwind:0.0} downwind, {smokeUpwind:0.0} upwind)");

    HashSet<(int X, int Z)> holding = [];
    for (int columnZ = 0; columnZ < 3; columnZ++)
    {
        for (int columnX = 0; columnX < 3; columnX++)
        {
            if (Count(dry, burning, fromX: columnX * 16, toX: (columnX * 16) + 15, fromZ: columnZ * 16, toZ: (columnZ * 16) + 15) > 0)
            {
                holding.Add((columnX, columnZ));
            }
        }
    }

    Check.That(holding.Count > 0 && FieldWindow.BurningColumns(FireScan.Read(dry, blocks.Burning, pack.Quantity("heat"), pack.Quantity("smoke"))).SetEquals(holding),
        "the burning columns are the chunk columns holding burning cells");

    ChemistryField burns = Meadow(dryGrass);
    Ignite(burns, 24, 24);
    int wanted = 0;
    for (int step = 0; step < 400; step++)
    {
        burns.Step();
        wanted += WriteBack.Wanted(burns.MaterialChanges, blocks).Count();
    }

    Check.That(Count(burns, burnt) > 0 && wanted == Count(burns, burnt), $"every cell that burns out asks to be written back ({wanted})");
    Check.That(burns.Quantity(new(24, 1, 24), moisture) < 0.2f, "burnt ground is left dry");
});

return Check.Finish("Chemistry");

// A meadow of 48 x 48 cells: soil, a layer of grass, and air above, open to the sky, on a mild still day.
ChemistryField Meadow(int cover)
{
    ChemistryField field = new(pack, new ChemistryFieldSettings("air", settings.Field.StepSeconds, Seed: 10293));
    field.Fill(new(0, 0, 0), new(47, 0, 47), soil);
    field.Fill(new(0, 1, 0), new(47, 1, 47), cover);
    field.Fill(new(0, 2, 0), new(47, 15, 47), air);
    field.MarkOpenColumns();
    field.SetAmbient(heat, settings.Ambient.WarmHeat);
    field.SetModulator("rain", 0f);
    field.SetModulator("snow", 0f);
    return field;
}

// Heats the grass at a column as the debug command's torch does.
static void Ignite(ChemistryField field, int x, int z) =>
    field.AddEmitter(new EmitterSpec(EmitterShape.Sphere(new Cell(x, 1, z).Center, 1.2f), field.Pack.Quantity("heat"), 4000f, Lifetime: 0.6f));

static void Run(ChemistryField field, int steps)
{
    for (int step = 0; step < steps; step++)
    {
        field.Step();
    }
}

static int Count(ChemistryField field, int material, int fromX = 0, int toX = 47, int fromZ = 0, int toZ = 47)
{
    int count = 0;
    for (int z = fromZ; z <= toZ; z++)
    {
        for (int x = fromX; x <= toX; x++)
        {
            count += field.MaterialAt(new Cell(x, 1, z)) == material ? 1 : 0;
        }
    }

    return count;
}

// The smoke in the air over a band of columns.
float Smoke(ChemistryField field, int fromX, int toX)
{
    float total = 0f;
    for (int z = 0; z < 48; z++)
    {
        for (int y = 2; y <= 15; y++)
        {
            for (int x = fromX; x <= toX; x++)
            {
                total += field.Quantity(new Cell(x, y, z), smoke);
            }
        }
    }

    return total;
}

static string Resource(string name)
{
    using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"missing embedded resource {name}");
    return new StreamReader(stream).ReadToEnd();
}
