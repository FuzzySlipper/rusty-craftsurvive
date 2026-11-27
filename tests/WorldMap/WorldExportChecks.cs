using System.Text.Json;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// The world read out as data (#11125), as the golden-seed export pages it: each page parses, says what the
/// generator made (the raster is the map's own samples, a window's places are the recipe's own sites), and
/// refuses a page it cannot answer.
/// </summary>
internal static class WorldExportChecks
{
    private const int Pixels = 8;
    private const long WindowHalf = 512;
    private const long WindowStep = 64;

    internal static void Regional(WorldMap map, ITerrainDraws draws)
    {
        WorldExport export = new(map, () => [], draws);
        using JsonDocument header = JsonDocument.Parse(export.Header());
        JsonElement world = header.RootElement;
        Check.That(world.GetProperty("seed").GetUInt64() == map.Configuration.Seed && world.GetProperty("size").GetInt32() == map.Configuration.Size,
            "the export's header names the active world");
        Check.That(world.GetProperty("biomes").GetArrayLength() == Enum.GetValues<MapBiome>().Length
            && world.GetProperty("biomeColours").GetArrayLength() == Enum.GetValues<MapBiome>().Length,
            "the header carries every biome and the map's colour for each");

        using JsonDocument raster = JsonDocument.Parse(export.Raster(Pixels, 0, Pixels));
        float[] elevation = Floats(raster.RootElement.GetProperty("elevation").GetBytesFromBase64());
        byte[] biome = raster.RootElement.GetProperty("biome").GetBytesFromBase64();
        Check.That(elevation.Length == Pixels * Pixels && biome.Length == Pixels * Pixels, "a raster page holds a value per pixel in each layer");
        const int Column = 5, Row = 3;
        double cell = map.Radius * 2 / Pixels;
        MapSample sample = map.Sample(-map.Radius + ((Column + 0.5) * cell), -map.Radius + ((Row + 0.5) * cell));
        Check.That(elevation[(Row * Pixels) + Column] == (float)sample.Elevation && biome[(Row * Pixels) + Column] == (byte)WorldMap.Biome(sample),
            "a raster pixel is the map's own sample at its centre, rows from the north edge");
        Check.Throws<ArgumentOutOfRangeException>(() => export.Raster(Pixels, Pixels / 2, Pixels), "a raster page past the last row is refused");

        using JsonDocument window = JsonDocument.Parse(export.Window(0, 0, WindowHalf, WindowStep));
        long side = (WindowHalf * 2 / WindowStep) + 1;
        Check.That(window.RootElement.GetProperty("side").GetInt64() == side
            && window.RootElement.GetProperty("height").GetBytesFromBase64().Length == side * side * sizeof(short),
            "a window's ground is a height per step across it");
        TerrainRecipe recipe = map.Configuration.CreateRecipe(draws, map);
        int places = 0, mismatched = 0;
        foreach (JsonElement place in window.RootElement.GetProperty("places").EnumerateArray())
        {
            places++;
            string[] cellKey = place.GetProperty("id").GetString()!.Split(':');
            PoiSite? site = recipe.Placement.SiteAt(long.Parse(cellKey[1], System.Globalization.CultureInfo.InvariantCulture),
                long.Parse(cellKey[2], System.Globalization.CultureInfo.InvariantCulture));
            if (site is not PoiSite found || found.X != place.GetProperty("x").GetInt64() || found.Z != place.GetProperty("z").GetInt64()
                || found.KindName != place.GetProperty("kind").GetString() || place.TryGetProperty("cave", out _) != (recipe.Caves.For(found) is not null))
                mismatched++;
        }

        Check.That(places > 0 && mismatched == 0, $"a window's places are the recipe's own sites, caves where a mouth has one ({places} places, {mismatched} differ)");
        Check.Throws<ArgumentOutOfRangeException>(() => export.Window(0, 0, WindowHalf, 1), "a window finer than the least step is refused");

        using JsonDocument named = JsonDocument.Parse(export.Places());
        Check.That(named.RootElement.GetProperty("lakes").GetArrayLength() == map.Lakes.Count
            && named.RootElement.GetProperty("sites").GetArrayLength() == map.Sites.Count, "the places page lists every lake and representative site");
    }

    internal static void Continent(WorldMap map, ITerrainDraws draws)
    {
        Settlement[] settlements = [.. Settlements.For(map)];
        ExportedTown[] towns = [.. settlements.Select(s => new ExportedTown(s.Key, s.Name, s.Kind, s.Reason, s.X, s.Z, s.Ground.Height, 1, 0, s.X, s.Z,
            "layout/palette/landmark/plain", "layout", "palette", "landmark", s.Reach, s.Score, s.Shelter, s.Flat,
            new Dictionary<string, string>(), new Dictionary<string, string>()))];
        WorldExport export = new(map, () => towns, draws);
        int reaches = MapRegions.For(map).Drainage.Network.Reaches.Count;
        using JsonDocument first = JsonDocument.Parse(export.Drainage(0));
        int pages = first.RootElement.GetProperty("pages").GetInt32();
        Check.That(first.RootElement.GetProperty("count").GetInt32() == reaches && pages == (reaches + WorldExport.ReachesPerPage - 1) / WorldExport.ReachesPerPage,
            $"the walked drainage pages cover its {reaches} reaches");
        using JsonDocument last = JsonDocument.Parse(export.Drainage(pages - 1));
        int onLast = last.RootElement.GetProperty("reaches").GetArrayLength();
        Check.That(onLast == reaches - ((pages - 1) * WorldExport.ReachesPerPage), "the last drainage page holds the remainder");
        JsonElement reach = first.RootElement.GetProperty("reaches")[0];
        Check.That(reach.GetProperty("points").GetArrayLength() == MapRegions.For(map).Drainage.Network.Reaches[0].Nodes.Length * 2,
            "a reach's path is its lattice nodes as x, z pairs");

        using JsonDocument named = JsonDocument.Parse(export.Places());
        string[] keys = [.. named.RootElement.GetProperty("towns").EnumerateArray().Select(town => town.GetProperty("key").GetString()!)];
        Check.That(keys.Length > 0 && keys.SequenceEqual(settlements.Select(s => s.Key)), $"the places page lists the continent's towns in order ({keys.Length})");
    }

    private static float[] Floats(byte[] bytes)
    {
        float[] values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }
}
