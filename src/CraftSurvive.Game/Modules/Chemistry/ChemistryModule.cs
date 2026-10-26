using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using RustyChemistry.Content;
using RustyChemistry.Simulation;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>
/// The fast, local layer of the world's elements around the player (#10293): heat, moisture, smoke and fuel in
/// 1 m cells, stepped by the rusty-chemistry kernel. It owns only its field. It reads the terrain, the water and
/// the weather (as ambient), writes burnt ground back through the terrain's own edits within a budget, and shows
/// burning cells as fires. Nothing of the field is saved: what lasts is what it wrote into the terrain.
/// </summary>
internal sealed class ChemistryModule : IProductModule
{
    private const int CellsPerChunk = ChemistryField.ChunkSize * ChemistryField.ChunkSize * ChemistryField.ChunkSize;

    /// <summary>A lit torch's heat: deposited around a cell for a moment, enough to ignite dry grass.</summary>
    private const float IgniteRadius = 1.2f, IgniteHeatPerSecond = 4000f, IgniteSeconds = 0.6f;

    /// <summary>Burnt cells waiting for a write; beyond this the oldest are dropped, and their scar is not kept.</summary>
    private const int MostWaiting = 4096;

    /// <summary>Chunk columns whose surface band is kept; past this the kept bands are forgotten and read again.</summary>
    private const int MostBands = 1024;

    private readonly TerrainWorld terrain;
    private readonly WeatherModule weather;
    private readonly WorldFrame frame;
    private readonly Func<Vector3> playerWorld;
    private readonly Func<bool> away;
    private readonly Func<double> snowCover;
    private readonly ChemistrySettings settings;
    private readonly ChemistryPack pack;
    private readonly BlockChemistry blocks;
    private readonly GrassFireLook look;
    private readonly BurnScars scars;
    private readonly int burntMaterial, heatQuantity, smokeQuantity;
    private readonly ushort[] chunkMaterials = new ushort[CellsPerChunk];
    private readonly Queue<(Cell Cell, WriteBackSetting Rule)> waiting = [];
    private readonly Dictionary<(int X, int Z), SurfaceBand> bands = [];
    private ChemistryField field;
    private ChemistryAmbient? applied;
    private HashSet<(int X, int Z)> burningColumns = [];
    private double owed, sinceAmbient, sinceWrite, stepMilliseconds;
    private long written, dropped, refused;
    private string lastRefusal = "none";
    private (long Step, Dictionary<Cell, float[]> Kept)? changesFrom;

    /// <param name="snowCover">The snow cover's share of the ground where the player stands, 0..1.</param>
    /// <param name="fogVolumes">Tells the sky the fire's smoke stands in the open world as fog volumes, or no longer does.</param>
    /// <param name="store">Where the scars' ledger is saved.</param>
    internal ChemistryModule(IEngineContext engine, ProductContent content, ProductStore store, TerrainWorld terrain, WeatherModule weather,
        WorldFrame frame, Func<Vector3> playerWorld, Func<bool> away, Func<double> snowCover, Action<bool> fogVolumes)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(content);
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.weather = weather ?? throw new ArgumentNullException(nameof(weather));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        this.away = away ?? throw new ArgumentNullException(nameof(away));
        this.snowCover = snowCover ?? throw new ArgumentNullException(nameof(snowCover));
        settings = ChemistrySettings.Parse(content.ReadText(ChemistrySettings.ContentPath));
        pack = ChemistryPack.Read(content.ReadText(settings.PackPath));
        blocks = new BlockChemistry(settings, pack);
        scars = new BurnScars(engine, store, terrain.SaveIdentity, terrain, settings.Scars);
        // Once a burnt cell is written as the scar block, the block shows its char; the look keeps a decal only where it is not.
        look = new GrassFireLook(engine, frame, fogVolumes, settings.Fires.Slots, settings.Fires.ClusterCells,
            cell => terrain.MaterialAt(Address(cell)) == (ushort)scars.Block);
        burntMaterial = pack.Material(settings.Fires.Burnt);
        heatQuantity = pack.Quantity("heat");
        smokeQuantity = pack.Quantity("smoke");
        field = NewField();
        terrain.Edited += Reload;
    }

    public void Start() => scars.Start();

    public void Update(ProductStep step)
    {
        if (away())
        {
            // A dungeon or study is its own space: the field waits, and its fire is not drawn there.
            look.Hide();
            return;
        }

        look.Tick(step.ElapsedSeconds);

        Vector3 player = playerWorld();
        scars.Update(step.ElapsedSeconds, weather.Hours, player);
        Follow(player);
        sinceAmbient += step.ElapsedSeconds;
        if (applied is null || sinceAmbient >= settings.Ambient.RefreshSeconds)
        {
            sinceAmbient = 0;
            Ambient(player);
        }

        owed += step.ElapsedSeconds;
        int steps = 0;
        while (owed >= settings.Field.StepSeconds && steps < settings.Field.StepsPerUpdate)
        {
            owed -= settings.Field.StepSeconds;
            Step();
            steps++;
        }

        // Time beyond the steps one update may run is dropped, not owed.
        owed = Math.Min(owed, settings.Field.StepSeconds);
        sinceWrite += step.ElapsedSeconds;
        if (sinceWrite >= settings.Budget.WriteSeconds)
        {
            sinceWrite = 0;
            Write();
        }

        if (steps > 0)
        {
            burningColumns = FieldWindow.BurningColumns(field, blocks.Burning);
            look.Show(field, blocks.Burning, heatQuantity, smokeQuantity, player);
        }
    }

    public void Restart()
    {
        field = NewField();
        applied = null;
        owed = 0;
        waiting.Clear();
        bands.Clear();
        burningColumns = [];
        look.Hide();
    }

    public void Dispose()
    {
        terrain.Edited -= Reload;
        look.Dispose();
        scars.Flush();
    }

    /// <summary>Heats the ground at a world column's surface, as a torch would; the fire, if any, is the field's.</summary>
    internal string IgniteAt(double x, double z)
    {
        long cellX = (long)Math.Floor(x), cellZ = (long)Math.Floor(z);
        Cell top = new((int)cellX, (int)terrain.Recipe.SurfaceAt(cellX, cellZ), (int)cellZ);
        if (!field.IsLoaded(top))
        {
            return string.Create(CultureInfo.InvariantCulture, $"refused: {top.X},{top.Y},{top.Z} is outside the field around the player");
        }

        field.AddEmitter(new EmitterSpec(EmitterShape.Sphere(top.Center, IgniteRadius), pack.Quantity("heat"), IgniteHeatPerSecond,
            Lifetime: IgniteSeconds));
        return $"heating {pack.Materials[field.MaterialAt(top)].Id} at {top.X},{top.Y},{top.Z}";
    }

    /// <summary>A cell's material, sky exposure and quantities.</summary>
    internal string CellReadout(long x, long y, long z)
    {
        Cell cell = new((int)x, (int)y, (int)z);
        if (!field.IsLoaded(cell))
        {
            return "outside the field";
        }

        IEnumerable<string> quantities = pack.Quantities.Select((quantity, index) =>
            string.Create(CultureInfo.InvariantCulture, $"{quantity.Id}={field.Quantity(cell, index):0.###}"));
        return $"{pack.Materials[field.MaterialAt(cell)].Id} open={field.IsOpen(cell)} {string.Join(" ", quantities)}";
    }

    /// <summary>Each loaded chunk: its first cell, whether it is awake, and its commonest materials.</summary>
    internal string ChunksReadout() => string.Join(" | ", field.LoadedChunks().Select(origin =>
    {
        field.ReadChunkMaterials(origin, chunkMaterials);
        IEnumerable<string> commonest = chunkMaterials.GroupBy(material => material).OrderByDescending(group => group.Count()).Take(3)
            .Select(group => $"{pack.Materials[group.Key].Id}:{group.Count()}");
        return string.Create(CultureInfo.InvariantCulture,
            $"{origin.X},{origin.Y},{origin.Z} {(field.IsActive(origin) ? "awake" : "asleep")} {string.Join(",", commonest)}");
    }));

    /// <summary>
    /// For diagnosing sleep: the first call keeps every loaded chunk's quantities; the next reports, for each chunk
    /// awake now, the largest change of each quantity since then, the cell it was in, and the steps between.
    /// </summary>
    internal string ChangesReadout()
    {
        if (changesFrom is not (long fromStep, Dictionary<Cell, float[]> kept))
        {
            changesFrom = (field.StepIndex, field.LoadedChunks().ToDictionary(origin => origin, Snapshot));
            return $"kept {field.ChunkCount} chunks at step {field.StepIndex}; call again to compare";
        }

        changesFrom = null;
        List<string> lines = [string.Create(CultureInfo.InvariantCulture, $"steps={field.StepIndex - fromStep}")];
        foreach (Cell origin in field.LoadedChunks().Where(field.IsActive))
        {
            if (!kept.TryGetValue(origin, out float[]? before))
            {
                continue;
            }

            float[] after = Snapshot(origin);
            IEnumerable<string> largest = pack.Quantities.Select((quantity, index) =>
            {
                int at = 0;
                float most = 0f;
                for (int cell = 0; cell < CellsPerChunk; cell++)
                {
                    float change = Math.Abs(after[(index * CellsPerChunk) + cell] - before[(index * CellsPerChunk) + cell]);
                    if (change > most)
                    {
                        (most, at) = (change, cell);
                    }
                }

                Cell where = origin.Offset(at % ChemistryField.ChunkSize, at / ChemistryField.ChunkSize % ChemistryField.ChunkSize,
                    at / (ChemistryField.ChunkSize * ChemistryField.ChunkSize));
                return string.Create(CultureInfo.InvariantCulture,
                    $"{quantity.Id}={most:g3}@{where.X},{where.Y},{where.Z}:{pack.Materials[field.MaterialAt(where)].Id}");
            });
            lines.Add($"{origin.X},{origin.Y},{origin.Z} {string.Join(" ", largest)}");
        }

        return string.Join(" | ", lines);
    }

    private float[] Snapshot(Cell origin)
    {
        float[] values = new float[pack.Quantities.Length * CellsPerChunk];
        for (int quantity = 0; quantity < pack.Quantities.Length; quantity++)
        {
            field.ReadChunk(origin, quantity, values.AsSpan(quantity * CellsPerChunk, CellsPerChunk));
        }

        return values;
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"chemistry chunks={field.ChunkCount} active={field.ActiveChunkCount} step={field.StepIndex} stepMs={stepMilliseconds:0.00} fires={look.Squares} ") +
        look.Readout() + " " +
        string.Create(CultureInfo.InvariantCulture,
        $"written={written} waiting={waiting.Count} dropped={dropped} refused={refused} overlay={terrain.OverlayEntries}/{TerrainConstants.MaximumOverlayEntries} ") +
        string.Create(CultureInfo.InvariantCulture,
        $"keepFree={settings.Budget.KeepFreeEntries} writable={WriteBack.Allowed(terrain.OverlayEntries, TerrainConstants.MaximumOverlayEntries, settings.Budget)} lastRefusal={lastRefusal} ") +
        scars.Readout();

    internal string AmbientReadout() => applied is ChemistryAmbient ambient
        ? string.Create(CultureInfo.InvariantCulture,
            $"heat={ambient.Heat:0.0} drift=({ambient.Drift.X:0.00},{ambient.Drift.Z:0.00}) rain={ambient.Rain:0.00} rainModulator={field.Modulator("rain"):0.00} snow={ambient.Snow:0.00} snowModulator={field.Modulator("snow"):0.00}")
        : "not read yet";

    private ChemistryField NewField() => new(pack, new ChemistryFieldSettings(pack.Materials[blocks.Air].Id, settings.Field.StepSeconds,
        terrain.Recipe.Configuration.Seed));

    /// <summary>
    /// Loads the chunks around the player, and beside a fire out to the burning reach, a few at a time; lets go of
    /// those left behind.
    /// </summary>
    private void Follow(Vector3 player)
    {
        (int columnX, int columnZ) = FieldWindow.ColumnOf(player);
        List<Cell> wanted = FieldWindow.Around(columnX, columnZ, settings.Field.ReachChunks, settings.Field.BurningReachChunks, burningColumns,
            Band);
        HashSet<Cell> keep = [.. wanted];
        foreach (Cell chunk in field.LoadedChunks().ToList())
        {
            if (!keep.Contains(chunk))
            {
                field.UnloadChunk(chunk);
            }
        }

        int loads = 0;
        foreach (Cell chunk in wanted)
        {
            if (loads >= settings.Field.LoadsPerUpdate)
            {
                break;
            }

            if (!field.IsLoaded(chunk))
            {
                Load(chunk);
                loads++;
            }
        }
    }

    /// <summary>
    /// A chunk column's surface band, from the recipe's surface (edits do not move it): read once per column and
    /// kept, since every update asks for the window's.
    /// </summary>
    private SurfaceBand Band(int columnX, int columnZ)
    {
        if (bands.TryGetValue((columnX, columnZ), out SurfaceBand band))
        {
            return band;
        }

        if (bands.Count >= MostBands)
        {
            bands.Clear();
        }

        return bands[(columnX, columnZ)] = FieldWindow.BandOf(columnX, columnZ, terrain.Recipe.SurfaceAt);
    }

    /// <summary>Loads one chunk from the world: each cell's block as its material, open air under water as water.</summary>
    private void Load(Cell origin)
    {
        const int Size = ChemistryField.ChunkSize;
        for (int z = 0; z < Size; z++)
        {
            for (int x = 0; x < Size; x++)
            {
                long worldX = origin.X + x, worldZ = origin.Z + z;
                Water.WaterSample water = terrain.Water.Sample(worldX, worldZ);
                double? top = water.Wet && !water.Ice ? water.Top : null;
                for (int y = 0; y < Size; y++)
                {
                    long worldY = origin.Y + y;
                    ushort block = terrain.MaterialAt(new VoxelAddress(worldX, worldY, worldZ));
                    chunkMaterials[x + (y * Size) + (z * Size * Size)] = blocks.CellMaterial(block, worldY, top);
                }
            }
        }

        field.LoadChunk(origin, chunkMaterials);
        for (int z = 0; z < Size; z++)
        {
            for (int x = 0; x < Size; x++)
            {
                field.MarkOpenColumn(origin.X + x, origin.Z + z);
            }
        }
    }

    /// <summary>
    /// The weather and the lying snow over the player as the field's ambient, applied only when it has moved enough
    /// to matter. The snow's modulator wets open ground, already loaded or not, for as long as the cover lies.
    /// </summary>
    private void Ambient(Vector3 player)
    {
        EnvironmentChannels channels = weather.Field.Sample(player.X, player.Z, weather.Hours).Channels;
        ChemistryAmbient ambient = ChemistryAmbient.From(channels.Cold, channels.Wind, channels.Precipitation, weather.Field.Flow(player.X, player.Z),
            snowCover(), settings.Ambient);
        if (applied is ChemistryAmbient last && !ambient.Differs(last))
        {
            return;
        }

        field.SetAmbient(pack.Quantity("heat"), ambient.Heat);
        field.Drift = ambient.Drift;
        field.SetModulator("rain", ambient.Rain);
        field.SetModulator("snow", ambient.Snow);
        applied = ambient;
    }

    private void Step()
    {
        long began = Stopwatch.GetTimestamp();
        field.Step();
        stepMilliseconds = (stepMilliseconds * 0.9) + (Stopwatch.GetElapsedTime(began).TotalMilliseconds * 0.1);
        look.Burnt(field.MaterialChanges, burntMaterial, playerWorld());
        foreach ((Cell Cell, WriteBackSetting Rule) entry in WriteBack.Wanted(field.MaterialChanges, blocks))
        {
            waiting.Enqueue(entry);
            if (waiting.Count > MostWaiting)
            {
                waiting.Dequeue();
                dropped++;
            }
        }
    }

    /// <summary>Writes waiting burnt cells into the terrain, a batch at a time, within the overlay reserve.</summary>
    private void Write()
    {
        int allowed = WriteBack.Allowed(terrain.OverlayEntries, TerrainConstants.MaximumOverlayEntries, settings.Budget);
        if (waiting.Count == 0 || allowed == 0)
        {
            return;
        }

        List<(Cell Cell, WriteBackSetting Rule)> batch = [];
        while (batch.Count < allowed && waiting.Count > 0)
        {
            batch.Add(waiting.Dequeue());
        }

        foreach ((BlockId block, List<Cell> cells) in WriteBack.Choose(batch, cell => terrain.MaterialAt(Address(cell)), allowed))
        {
            TerrainWorldEditResult result = terrain.TryEditCells([.. cells.Select(Address)], TerrainEditKind.Set, (ushort)block);
            if (result is TerrainWorldEditApplied)
            {
                written += cells.Count;
                if (block == scars.Block)
                {
                    scars.Burnt(cells.Select(Address), weather.Hours);
                }
            }
            else
            {
                refused += cells.Count;
                lastRefusal = TerrainWorldEditResult.Format(result);
            }
        }
    }

    /// <summary>Reloads edited cells the field holds, so a cell the player or a fire changed reads as its new block.</summary>
    private void Reload(IReadOnlyList<TerrainVoxelEdit> edits)
    {
        foreach (TerrainVoxelEdit edit in edits)
        {
            Cell cell = new((int)edit.Address.X, (int)edit.Address.Y, (int)edit.Address.Z);
            if (!field.IsLoaded(cell))
            {
                continue;
            }

            Water.WaterSample water = terrain.Water.Sample(edit.Address.X, edit.Address.Z);
            field.Load(cell, blocks.CellMaterial(edit.Material, edit.Address.Y, water.Wet && !water.Ice ? water.Top : null));
            field.MarkOpenColumn(cell.X, cell.Z);
        }
    }

    private static VoxelAddress Address(Cell cell) => new(cell.X, cell.Y, cell.Z);
}
