using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The generated world read out as data (#11125), for the authoring tools' golden-seed export: what the
/// running generator made of the active world, page by page, as JSON. Nothing here generates anything of
/// its own; every value is read from the active world's map, its walked drainage, its towns, and a recipe
/// with the Engine's keyed draws for the places, so the export is the game's own answer. Rasters are
/// little-endian arrays in base64, row-major from the world's north-west corner (-radius, -radius), +X east
/// and +Z south.
/// </summary>
internal sealed class WorldExport
{
    /// <summary>The largest continent raster a page plan accepts, in pixels a side.</summary>
    internal const int MaximumPixels = 4096;

    /// <summary>The most raster rows, reaches or window cells one command returns, so an answer stays a few megabytes.</summary>
    internal const int MaximumRowsPerPage = 256;
    internal const int ReachesPerPage = 2000;
    internal const long MaximumWindowHalf = 32_768;
    internal const long MinimumWindowStep = 4;
    private const long MaximumWindowSamples = 1 << 20;

    /// <summary>Water codes in the continent raster's water layer.</summary>
    private const byte Land = 0, Sea = 1, LakeWater = 2, RiverWater = 3;

    /// <summary>A walked reach's points are written to the metre; a raster's float values as they are.</summary>
    private const int PointDecimals = 0;

    private readonly WorldMap map;
    private readonly Func<IReadOnlyList<ExportedTown>> towns;
    private readonly Lazy<TerrainRecipe> recipe;

    /// <param name="draws">The game's own draw port (the Engine's keyed draws, <see cref="EngineTerrainDraws"/>): the places
    /// are then the walking world's, since placement is a pure function of the contract and its draws.</param>
    internal WorldExport(WorldMap map, Func<IReadOnlyList<ExportedTown>> towns, ITerrainDraws draws)
    {
        this.map = map ?? throw new ArgumentNullException(nameof(map));
        this.towns = towns ?? throw new ArgumentNullException(nameof(towns));
        ArgumentNullException.ThrowIfNull(draws);
        // A recipe of its own, so the export does not churn the walking recipe's caches.
        recipe = new(() => map.Configuration.CreateRecipe(draws, map));
    }

    internal WorldMap Map => map;

    private MapDrainage? Walked => map.Scale.Continental ? MapRegions.For(map).Drainage : null;

    /// <summary>The world's identity and the vocabularies the other pages use.</summary>
    internal string Header()
    {
        MapDrainage? drainage = Walked;
        return Json(writer =>
        {
            writer.WriteNumber("seed", map.Configuration.Seed);
            writer.WriteNumber("size", map.Configuration.Size);
            writer.WriteNumber("radius", map.Radius);
            writer.WriteNumber("generatorVersion", map.Configuration.GeneratorVersion);
            writer.WriteString("fingerprint", map.Fingerprint.ToString("x16", CultureInfo.InvariantCulture));
            writer.WriteBoolean("continental", map.Scale.Continental);
            writer.WriteString("design", ContinentDesign.For(map.Configuration.Size)?.Name);
            writer.WriteNumber("waterLevel", GenerationConstants.WaterLevel);
            writer.WriteNumber("mapSpacing", map.Spacing);
            writer.WriteNumber("drainageSpacing", MapDrainage.Spacing);
            writer.WriteNumber("placeCell", PoiConstants.CellSize);
            writer.WriteNumber("mapRivers", map.Rivers.Reaches.Count);
            writer.WriteNumber("drainageReaches", drainage?.Network.Reaches.Count ?? 0);
            Names(writer, "biomes", Enum.GetNames<MapBiome>());
            Names(writer, "placeKinds", Enum.GetNames<PoiKind>());
            Names(writer, "navigability", Enum.GetNames<Navigability>());
            Names(writer, "channels", Enum.GetNames<ChannelType>());
            Names(writer, "water", ["land", "sea", "lake", "river"]);
            // The world map's own colours, so the atlas draws biomes and water as the game's map does.
            Names(writer, "biomeColours", Enum.GetValues<MapBiome>().Select(biome => Css(MapPalette.For(biome))));
            writer.WriteStartObject("mapColours");
            writer.WriteString("stone", Css(MapPalette.Stone));
            writer.WriteString("river", Css(MapPalette.River));
            writer.WriteString("lake", Css(MapPalette.Lake));
            writer.WriteString("saltLake", Css(MapPalette.SaltLake));
            writer.WriteString("playa", Css(MapPalette.Playa));
            writer.WriteString("town", Css(MapPalette.Town));
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Rows of the continent raster at <paramref name="pixels"/> a side: elevation (m), temperature, moisture
    /// and exposed rock as float32, and biome and water codes as bytes, each sampled at the pixel's centre.
    /// </summary>
    internal string Raster(int pixels, int firstRow, int rows)
    {
        if (pixels < 1 || pixels > MaximumPixels) throw new ArgumentOutOfRangeException(nameof(pixels));
        if (rows < 1 || rows > MaximumRowsPerPage || firstRow < 0 || firstRow + rows > pixels)
            throw new ArgumentOutOfRangeException(nameof(rows));
        int count = pixels * rows;
        float[] elevation = new float[count], temperature = new float[count], moisture = new float[count], rock = new float[count];
        byte[] biome = new byte[count], water = new byte[count];
        double cell = map.Radius * 2 / pixels;
        for (int row = 0; row < rows; row++)
        for (int column = 0; column < pixels; column++)
        {
            int at = (row * pixels) + column;
            double x = -map.Radius + ((column + 0.5) * cell), z = -map.Radius + ((firstRow + row + 0.5) * cell);
            MapSample sample = map.Sample(x, z);
            elevation[at] = (float)sample.Elevation;
            temperature[at] = (float)sample.Temperature;
            moisture[at] = (float)sample.Moisture;
            rock[at] = (float)sample.Rock;
            biome[at] = (byte)WorldMap.Biome(sample);
            water[at] = sample.InLake ? LakeWater : sample.InRiver ? RiverWater : sample.Elevation < GenerationConstants.WaterLevel ? Sea : Land;
        }

        return Json(writer =>
        {
            writer.WriteNumber("pixels", pixels);
            writer.WriteNumber("firstRow", firstRow);
            writer.WriteNumber("rows", rows);
            writer.WriteBase64String("elevation", MemoryMarshal.AsBytes(elevation.AsSpan()));
            writer.WriteBase64String("temperature", MemoryMarshal.AsBytes(temperature.AsSpan()));
            writer.WriteBase64String("moisture", MemoryMarshal.AsBytes(moisture.AsSpan()));
            writer.WriteBase64String("rock", MemoryMarshal.AsBytes(rock.AsSpan()));
            writer.WriteBase64String("biome", biome);
            writer.WriteBase64String("water", water);
        });
    }

    /// <summary>A page of the map's own rivers (the 1 km lattice the map view and the horizon draw): x, z, half width per point.</summary>
    internal string MapRivers(int page)
    {
        IReadOnlyList<RiverPoint[]> reaches = map.Rivers.Reaches;
        return Json(writer =>
        {
            Paging(writer, page, reaches.Count);
            writer.WriteStartArray("reaches");
            foreach (RiverPoint[] points in Page(reaches, page))
            {
                writer.WriteStartArray();
                foreach (RiverPoint point in points)
                {
                    writer.WriteNumberValue(Math.Round(point.X, PointDecimals));
                    writer.WriteNumberValue(Math.Round(point.Z, PointDecimals));
                    writer.WriteNumberValue(Math.Round(point.HalfWidth, 1));
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        });
    }

    /// <summary>
    /// A page of the walked drainage's reaches (<see cref="MapDrainage"/>, 250 m: what walking terrain, the water
    /// network and the towns use), each with its lattice path and its flow, navigability and channel form.
    /// </summary>
    internal string Drainage(int page)
    {
        MapDrainage? drainage = Walked;
        IReadOnlyList<Reach> reaches = drainage?.Network.Reaches ?? [];
        return Json(writer =>
        {
            Paging(writer, page, reaches.Count);
            writer.WriteStartArray("reaches");
            foreach (Reach reach in Page(reaches, page))
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", reach.Id);
                writer.WriteNumber("downstream", reach.Downstream);
                writer.WriteBoolean("mouth", reach.Mouth);
                writer.WriteNumber("flow", Math.Round(reach.Flow, 2));
                writer.WriteNumber("catchmentKm2", Math.Round(reach.CatchmentSquareKilometres, 1));
                writer.WriteNumber("lengthM", Math.Round(reach.LengthMetres));
                writer.WriteNumber("width", Math.Round(reach.Width, 1));
                writer.WriteNumber("depth", Math.Round(reach.Depth, 2));
                writer.WriteNumber("navigability", (int)reach.Class);
                writer.WriteNumber("channel", (int)reach.Channel);
                writer.WriteStartArray("points");
                foreach (int node in reach.Nodes)
                {
                    writer.WriteNumberValue(Math.Round(drainage!.Grid.X(node), PointDecimals));
                    writer.WriteNumberValue(Math.Round(drainage.Grid.Z(node), PointDecimals));
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    /// <summary>The world's named things: its towns as the game builds them, the settlements chosen for them, its lakes and its representative sites.</summary>
    internal string Places()
    {
        MapDrainage? drainage = Walked;
        return Json(writer =>
        {
            writer.WriteStartArray("towns");
            foreach (ExportedTown town in towns())
            {
                writer.WriteStartObject();
                writer.WriteString("key", town.Key);
                writer.WriteString("name", town.Name);
                writer.WriteString("kind", town.Kind.ToString());
                writer.WriteString("reason", town.Reason.ToString());
                writer.WriteNumber("x", Math.Round(town.X, 1));
                writer.WriteNumber("z", Math.Round(town.Z, 1));
                writer.WriteNumber("ground", Math.Round(town.Ground, 2));
                writer.WriteNumber("facingX", Math.Round(town.FacingX, 3));
                writer.WriteNumber("facingZ", Math.Round(town.FacingZ, 3));
                writer.WriteNumber("arrivalX", Math.Round(town.ArrivalX, 1));
                writer.WriteNumber("arrivalZ", Math.Round(town.ArrivalZ, 1));
                writer.WriteString("recipe", town.Recipe);
                writer.WriteString("layout", town.Layout);
                writer.WriteString("palette", town.Palette);
                writer.WriteString("landmark", town.Landmark);
                writer.WriteNumber("reach", town.Reach);
                writer.WriteNumber("reachFlow", drainage is not null && town.Reach >= 0 && town.Reach < drainage.Network.Reaches.Count
                    ? Math.Round(drainage.Network.Reaches[town.Reach].Flow, 1) : 0);
                writer.WriteNumber("score", Math.Round(town.Score, 3));
                writer.WriteNumber("shelter", Math.Round(town.Shelter, 3));
                writer.WriteNumber("flat", Math.Round(town.Flat, 3));
                Strings(writer, "services", town.Services);
                Strings(writer, "keepers", town.Keepers);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("lakes");
            foreach (Lake lake in map.Lakes)
            {
                double sumX = 0, sumZ = 0, minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
                foreach (int node in lake.Nodes)
                {
                    double x = map.Grid.X(node), z = map.Grid.Z(node);
                    sumX += x;
                    sumZ += z;
                    minX = Math.Min(minX, x);
                    minZ = Math.Min(minZ, z);
                    maxX = Math.Max(maxX, x);
                    maxZ = Math.Max(maxZ, z);
                }

                int nodes = Math.Max(1, lake.Nodes.Length);
                writer.WriteStartObject();
                writer.WriteNumber("id", lake.Id);
                writer.WriteString("name", lake.Name);
                writer.WriteString("kind", lake.Kind.ToString());
                writer.WriteNumber("level", Math.Round(lake.Level, 1));
                writer.WriteNumber("areaKm2", Math.Round(lake.AreaSquareKilometres, 1));
                writer.WriteNumber("depth", Math.Round(lake.MaximumDepth, 1));
                writer.WriteBoolean("closed", lake.Closed);
                writer.WriteNumber("inflow", Math.Round(lake.InflowFlow, 1));
                writer.WriteNumber("x", Math.Round(sumX / nodes));
                writer.WriteNumber("z", Math.Round(sumZ / nodes));
                writer.WriteStartArray("bounds");
                if (lake.Nodes.Length > 0)
                {
                    writer.WriteNumberValue(Math.Round(minX));
                    writer.WriteNumberValue(Math.Round(minZ));
                    writer.WriteNumberValue(Math.Round(maxX));
                    writer.WriteNumberValue(Math.Round(maxZ));
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("sites");
            foreach (MapSite site in map.Sites)
            {
                writer.WriteStartObject();
                writer.WriteString("name", site.Name);
                writer.WriteNumber("x", Math.Round(site.X));
                writer.WriteNumber("z", Math.Round(site.Z));
                writer.WriteNumber("elevation", Math.Round(site.Geography.Elevation, 1));
                writer.WriteString("biome", WorldMap.Region(site.Geography));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    /// <summary>
    /// A square window at walking detail about (x, z): the walking ground's height and biome every
    /// <paramref name="step"/> metres (int16 metres and bytes), every place and crossing whose anchor cell
    /// lies in it, each cave mouth's way underground, and the walked rivers shaped as walking terrain cuts them.
    /// </summary>
    internal string Window(long centreX, long centreZ, long half, long step)
    {
        if (half < PoiConstants.CellSize || half > MaximumWindowHalf) throw new ArgumentOutOfRangeException(nameof(half));
        if (step < MinimumWindowStep) throw new ArgumentOutOfRangeException(nameof(step));
        long side = (half * 2 / step) + 1;
        if (side * side > MaximumWindowSamples) throw new ArgumentOutOfRangeException(nameof(step), "the window has too many samples for its step");
        TerrainRecipe terrain = recipe.Value;
        long minX = centreX - half, minZ = centreZ - half, maxX = centreX + half, maxZ = centreZ + half;
        terrain.Regions?.Prefetch(minX, minZ, maxX, maxZ);

        short[] height = new short[side * side];
        byte[] biome = new byte[side * side];
        for (long row = 0; row < side; row++)
        for (long column = 0; column < side; column++)
        {
            long x = minX + (column * step), z = minZ + (row * step);
            long at = (row * side) + column;
            if (!map.Contains(x, z))
            {
                height[at] = short.MinValue;
                continue;
            }

            TerrainColumn ground = terrain.ColumnAt(x, z);
            height[at] = (short)Math.Clamp(ground.Surface, short.MinValue + 1, short.MaxValue);
            biome[at] = (byte)WorldMap.Biome(ground.Geography);
        }

        long cell = PoiConstants.CellSize;
        long firstCellX = GridMath.FloorDivide(minX, cell), lastCellX = GridMath.FloorDivide(maxX, cell);
        long firstCellZ = GridMath.FloorDivide(minZ, cell), lastCellZ = GridMath.FloorDivide(maxZ, cell);
        List<PoiSite> sites = [];
        List<CrossingSite> crossings = [];
        for (long cellZ = firstCellZ; cellZ <= lastCellZ; cellZ++)
        for (long cellX = firstCellX; cellX <= lastCellX; cellX++)
        {
            if (terrain.Placement.SiteAt(cellX, cellZ) is PoiSite site) sites.Add(site);
            if (terrain.Crossings.SiteAt(cellX, cellZ) is CrossingSite crossing) crossings.Add(crossing);
        }

        MapDrainage? drainage = Walked;
        return Json(writer =>
        {
            writer.WriteNumber("x", centreX);
            writer.WriteNumber("z", centreZ);
            writer.WriteNumber("half", half);
            writer.WriteNumber("step", step);
            writer.WriteNumber("side", side);
            writer.WriteBase64String("height", MemoryMarshal.AsBytes(height.AsSpan()));
            writer.WriteBase64String("biome", biome);
            writer.WriteStartArray("places");
            foreach (PoiSite site in sites)
            {
                writer.WriteStartObject();
                writer.WriteString("id", site.Id);
                writer.WriteString("kind", site.KindName);
                writer.WriteNumber("x", site.X);
                writer.WriteNumber("z", site.Z);
                writer.WriteNumber("ground", site.Ground);
                writer.WriteNumber("height", site.Height);
                writer.WriteNumber("variant", site.Variant);
                writer.WriteNumber("aspect", site.Aspect);
                if (terrain.Caves.For(site) is CavePlacement cave)
                {
                    writer.WriteStartObject("cave");
                    writer.WriteStartArray("path");
                    foreach (System.Numerics.Vector3 point in cave.Path)
                    {
                        writer.WriteNumberValue(Math.Round(point.X, 1));
                        writer.WriteNumberValue(Math.Round(point.Y, 1));
                        writer.WriteNumberValue(Math.Round(point.Z, 1));
                    }

                    writer.WriteEndArray();
                    writer.WriteStartArray("box");
                    writer.WriteNumberValue(cave.BoxMinimum.X);
                    writer.WriteNumberValue(cave.BoxMinimum.Y);
                    writer.WriteNumberValue(cave.BoxMinimum.Z);
                    writer.WriteNumberValue(cave.BoxMaximum.X);
                    writer.WriteNumberValue(cave.BoxMaximum.Y);
                    writer.WriteNumberValue(cave.BoxMaximum.Z);
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("crossings");
            foreach (CrossingSite crossing in crossings)
            {
                writer.WriteStartObject();
                writer.WriteString("id", crossing.Id);
                writer.WriteNumber("fromX", crossing.FromX);
                writer.WriteNumber("fromZ", crossing.FromZ);
                writer.WriteNumber("toX", crossing.ToX);
                writer.WriteNumber("toZ", crossing.ToZ);
                writer.WriteNumber("deck", crossing.DeckY);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("rivers");
            if (drainage is not null)
            {
                foreach (int reach in drainage.ReachesWithin(minX, minZ, maxX, maxZ))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", reach);
                    writer.WriteStartArray("points");
                    foreach (RiverPoint point in drainage.Shaped(reach))
                    {
                        writer.WriteNumberValue(Math.Round(point.X, 1));
                        writer.WriteNumberValue(Math.Round(point.Z, 1));
                        writer.WriteNumberValue(Math.Round(point.HalfWidth, 1));
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        });
    }

    private static string Css(Color colour) => string.Create(CultureInfo.InvariantCulture,
        $"#{Channel(colour.R):x2}{Channel(colour.G):x2}{Channel(colour.B):x2}");

    private static int Channel(float value) => (int)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue);

    private static IEnumerable<T> Page<T>(IReadOnlyList<T> items, int page)
    {
        for (int at = page * ReachesPerPage; at < Math.Min(items.Count, (page + 1) * ReachesPerPage); at++)
        {
            yield return items[at];
        }
    }

    private static void Paging(Utf8JsonWriter writer, int page, int count)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        writer.WriteNumber("page", page);
        writer.WriteNumber("pages", (count + ReachesPerPage - 1) / ReachesPerPage);
        writer.WriteNumber("count", count);
    }

    private static void Strings(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, string> values)
    {
        writer.WriteStartObject(name);
        foreach ((string key, string value) in values)
        {
            writer.WriteString(key, value);
        }

        writer.WriteEndObject();
    }

    private static void Names(Utf8JsonWriter writer, string name, IEnumerable<string> names)
    {
        writer.WriteStartArray(name);
        foreach (string value in names)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static string Json(Action<Utf8JsonWriter> body)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

/// <summary>A town as the game builds it, for the export: its settlement's choice and its town's reproducible identity.</summary>
internal sealed record ExportedTown(string Key, string Name, SettlementKind Kind, SettlementReason Reason, double X, double Z, double Ground,
    double FacingX, double FacingZ, double ArrivalX, double ArrivalZ, string Recipe, string Layout, string Palette, string Landmark,
    int Reach, double Score, double Shelter, double Flat, IReadOnlyDictionary<string, string> Services, IReadOnlyDictionary<string, string> Keepers);
