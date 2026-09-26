using System.Globalization;
using System.Text.Json;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Content;

/// <summary>One authored tile inside the world's atlas image.</summary>
internal readonly record struct TerrainAtlasRegion(string Id, string Block, string Face, uint X, uint Y)
{
    internal bool IsTopFace => string.Equals(Face, TerrainAtlasLayout.TopFace, StringComparison.Ordinal);

    internal bool IsBaseFace => string.Equals(Face, TerrainAtlasLayout.BaseFace, StringComparison.Ordinal);
}

/// <summary>
/// The atlas layout of record, read from checked content rather than duplicated
/// in code: which tiles exist, where they sit, and the hash the image must have.
/// The generator that writes the image writes this metadata in the same step, and
/// the managed texture audit checks the two agree, so the atlas cannot drift from
/// the layout the product binds.
/// </summary>
internal sealed class TerrainAtlasLayout
{
    internal const string ContentPath = "textures/terrain-atlas.json";
    internal const string ImageContentPath = "textures/terrain-atlas.png";
    internal const string AtlasId = "sprite-sheet/terrain";
    internal const string TextureId = "texture/terrain-atlas";
    internal const string BaseFace = "base";
    internal const string TopFace = "top";
    internal const uint SchemaVersion = 2;

    private readonly Dictionary<string, TerrainAtlasRegion> regions;

    private TerrainAtlasLayout(
        uint extentX,
        uint extentY,
        uint tileWidth,
        uint tileHeight,
        string contentHash,
        TerrainAtlasRegion[] regions)
    {
        ExtentX = extentX;
        ExtentY = extentY;
        TileWidth = tileWidth;
        TileHeight = tileHeight;
        ContentHash = contentHash;
        Regions = regions;
        this.regions = regions.ToDictionary(region => region.Id, StringComparer.Ordinal);
    }

    internal uint ExtentX { get; }

    internal uint ExtentY { get; }

    internal uint TileWidth { get; }

    internal uint TileHeight { get; }

    /// <summary>The SHA-256 the atlas image must have, as recorded in its layout.</summary>
    internal string ContentHash { get; }

    internal IReadOnlyList<TerrainAtlasRegion> Regions { get; }

    /// <summary>
    /// Reads and validates the layout. Every check here is a content error the
    /// Engine would otherwise report later and less clearly, or accept silently:
    /// a tile outside the image, two tiles sharing a position, or a block whose
    /// declared tile is missing from the layout.
    /// </summary>
    internal static TerrainAtlasLayout Read(ProductContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string json = content.ReadText(ContentPath);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        uint schema = root.GetProperty("schemaVersion").GetUInt32();
        if (schema != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Terrain atlas layout schema {schema} is not the supported schema {SchemaVersion}.");
        }

        JsonElement extent = root.GetProperty("extent");
        uint extentX = extent[0].GetUInt32();
        uint extentY = extent[1].GetUInt32();
        JsonElement tile = root.GetProperty("tileExtent");
        uint tileWidth = tile[0].GetUInt32();
        uint tileHeight = tile[1].GetUInt32();
        if (tileWidth == 0 || tileHeight == 0 || extentX % tileWidth != 0 || extentY % tileHeight != 0)
        {
            throw new InvalidOperationException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Terrain atlas extent {extentX}x{extentY} is not a whole number of {tileWidth}x{tileHeight} tiles."));
        }

        List<TerrainAtlasRegion> parsed = [];
        HashSet<(uint X, uint Y)> occupied = [];
        foreach (JsonElement region in root.GetProperty("regions").EnumerateArray())
        {
            string id = region.GetProperty("id").GetString() ?? string.Empty;
            string block = region.GetProperty("block").GetString() ?? string.Empty;
            string face = region.GetProperty("face").GetString() ?? string.Empty;
            JsonElement position = region.GetProperty("contentMin");
            uint x = position[0].GetUInt32();
            uint y = position[1].GetUInt32();
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException("Terrain atlas layout contains a region without an id.");
            }

            if (face != BaseFace && face != TopFace)
            {
                throw new InvalidOperationException(
                    $"Terrain atlas region '{id}' declares face '{face}', which is neither '{BaseFace}' nor '{TopFace}'.");
            }

            if (x % tileWidth != 0 || y % tileHeight != 0 || x + tileWidth > extentX || y + tileHeight > extentY)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Terrain atlas region '{id}' at ({x}, {y}) is not a tile-aligned position inside {extentX}x{extentY}."));
            }

            if (!occupied.Add((x, y)))
            {
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"Two terrain atlas regions share position ({x}, {y})."));
            }

            parsed.Add(new TerrainAtlasRegion(id, block, face, x, y));
        }

        TerrainAtlasLayout layout = new(extentX, extentY, tileWidth, tileHeight,
            NormalizeHash(root.GetProperty("contentHash").GetString()), [.. parsed]);
        layout.ValidateAgainstRegistry();
        return layout;
    }

    /// <summary>
    /// The Engine's authored-asset hashes are bare lowercase hex, while the layout
    /// records them the way the texture audit prints them. Normalizing here keeps
    /// one authored form and one Engine form rather than two drifting copies.
    /// </summary>
    private static string NormalizeHash(string? recorded)
    {
        string value = recorded ?? string.Empty;
        const string prefix = "sha256:";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }

        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                $"Terrain atlas layout records content hash '{recorded}', which is not a SHA-256 in hex.");
        }

        return value.ToLowerInvariant();
    }

    /// <summary>The tile for one block face, or a failure naming the block and face.</summary>
    internal TerrainAtlasRegion RegionFor(string regionId) => regions.TryGetValue(regionId, out TerrainAtlasRegion region)
        ? region
        : throw new InvalidOperationException($"The terrain atlas layout has no region '{regionId}'.");

    private void ValidateAgainstRegistry()
    {
        foreach (BlockDefinition block in BlockRegistry.MaterialBlocks)
        {
            TerrainAtlasRegion baseRegion = RegionFor(block.BaseRegion);
            if (!baseRegion.IsBaseFace || !string.Equals(baseRegion.Block, block.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Block '{block.Name}' binds base tile '{block.BaseRegion}', which belongs to '{baseRegion.Block}' as its {baseRegion.Face} face.");
            }

            if (block.TopRegion is null)
            {
                continue;
            }

            TerrainAtlasRegion topRegion = RegionFor(block.TopRegion);
            if (!topRegion.IsTopFace || !string.Equals(topRegion.Block, block.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Block '{block.Name}' binds top tile '{block.TopRegion}', which belongs to '{topRegion.Block}' as its {topRegion.Face} face.");
            }
        }

        foreach (TerrainAtlasRegion region in Regions)
        {
            // Names come from the registry, not from enum reflection: the layout is
            // authored data and must match the product's block names exactly.
            bool registered = false;
            foreach (BlockDefinition candidate in BlockRegistry.All)
            {
                if (!candidate.IsAir && string.Equals(candidate.Name, region.Block, StringComparison.Ordinal))
                {
                    registered = true;
                    break;
                }
            }

            if (!registered)
            {
                throw new InvalidOperationException(
                    $"Terrain atlas region '{region.Id}' names block '{region.Block}', which is not a registered block.");
            }
        }
    }
}
