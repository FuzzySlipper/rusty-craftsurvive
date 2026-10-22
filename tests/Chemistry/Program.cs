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
    Check.That(blocks.WriteBacks.TryGetValue(burnt, out WriteBackSetting? rule) && rule.Block == BlockId.Dirt
        && rule.Over.Contains(BlockId.Grass) && rule.Over.Contains(BlockId.DryGrass), "burnt ground writes back as dirt over grass");
    Check.That(blocks.Burning.SetEquals([burning]), "burning grass is what shows as fire");
    Check.Throws<InvalidDataException>(() => ChemistrySettings.Parse(Resource("Chemistry.adapter.json").Replace("\"Grass\": \"grass\"", "\"Grasss\": \"grass\"")),
        "a misspelt block fails at load");
});

Check.Section("write-back budget", () =>
{
    WriteBackBudgetSetting budget = new(KeepFreeEntries: 32768, CellsPerWrite: 256, WriteSeconds: 1);
    Check.Equal(256, WriteBack.Allowed(0, 65536, budget), "a write is capped at its batch");
    Check.Equal(100, WriteBack.Allowed(32768 - 100, 65536, budget), "it shrinks as the overlay nears the reserve");
    Check.Equal(0, WriteBack.Allowed(40000, 65536, budget), "nothing is written past the reserve");

    WriteBackSetting rule = blocks.WriteBacks[burnt];
    (Cell, WriteBackSetting)[] waiting = [(new Cell(1, 1, 1), rule), (new Cell(2, 1, 1), rule), (new Cell(3, 1, 1), rule)];
    Dictionary<Cell, BlockId> world = new() { [new(1, 1, 1)] = BlockId.Grass, [new(2, 1, 1)] = BlockId.Cobblestone, [new(3, 1, 1)] = BlockId.DryGrass };
    Dictionary<BlockId, List<Cell>> chosen = WriteBack.Choose(waiting, cell => (ushort)world[cell], allowed: 8);
    Check.That(chosen.Count == 1 && chosen[BlockId.Dirt].SequenceEqual([new Cell(1, 1, 1), new Cell(3, 1, 1)]),
        "only cells the world still holds as grass are written, as dirt; a player's edit since wins");
    Check.Equal(1, WriteBack.Choose(waiting, cell => (ushort)world[cell], allowed: 1)[BlockId.Dirt].Count, "a write takes no more than allowed");
});

Check.Section("weather as ambient", () =>
{
    AmbientSetting ambient = settings.Ambient;
    ChemistryAmbient mild = ChemistryAmbient.From(cold: 0, wind: 0, precipitation: 0, Vector2.UnitX, ambient);
    ChemistryAmbient bitter = ChemistryAmbient.From(cold: 1, wind: 1, precipitation: 0.7, Vector2.UnitY, ambient);
    Check.That(mild.Heat == ambient.WarmHeat && bitter.Heat == ambient.ColdHeat, "the Cold channel moves air heat from warm to bitter");
    Check.That(mild.Drift == Vector3.Zero && Vector3.Distance(bitter.Drift, new Vector3(0, 0, ambient.WindCellsPerSecond)) < 1e-4f,
        "full Wind drifts along the flow at the authored speed");
    Check.That(Math.Abs(bitter.Rain - 0.7f) < 1e-6f, "Precipitation becomes the rain modulator");
    Check.That(!(mild with { Heat = mild.Heat + 0.2f }).Differs(mild) && bitter.Differs(mild), "weather that barely moved is not applied again");
});

Check.Section("field window", () =>
{
    List<Cell> chunks = FieldWindow.Around(0, 0, 1, (_, _) => 20);
    Check.Equal(18, chunks.Count, "nine columns, each its surface chunk and the one above");
    Check.That(chunks[0] == new Cell(0, 16, 0) && chunks[1] == new Cell(0, 32, 0), "the player's own column comes first");
    Check.Equal((-1, 2), FieldWindow.ColumnOf(new Vector3(-0.5f, 3, 40)), "columns floor below zero");
    Check.Equal(2, FieldWindow.ColumnsApart(new Cell(-32, 0, 16), 0, 0), "columns apart is the larger axis");
    List<Cell> withFire = FieldWindow.Around(0, 0, 1, 2, [(1, 0), (5, 5)], (_, _) => 20);
    Check.Equal(24, withFire.Count, "a burning column at the window's edge brings in its outer neighbours out to the burning reach");
    Check.That(withFire.All(chunk => FieldWindow.ColumnsApart(chunk, 0, 0) <= 2), "but never past it");
    Check.That(withFire.Take(18).All(chunk => FieldWindow.ColumnsApart(chunk, 0, 0) <= 1), "and the player's own window still loads first");
});

Check.Section("grass fire", () =>
{
    ChemistryField dry = Meadow(dryGrass);
    Ignite(dry, 24, 24);
    Run(dry, 200);
    int dryBurnt = Count(dry, burning) + Count(dry, burnt);
    Check.That(dryBurnt > 60, $"dry grass catches and spreads ({dryBurnt} cells in 20 s)");
    Check.That(BurningPlaces.Pick(dry, blocks.Burning, new Vector3(24, 2, 24), 12, 4) is { Count: > 0 and <= 12 } places
        && places.All(place => place.Y == 2), "burning cells show as at most a slot's worth of fires, on top of the ground");

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

    Check.That(holding.Count > 0 && FieldWindow.BurningColumns(dry, blocks.Burning).SetEquals(holding),
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
