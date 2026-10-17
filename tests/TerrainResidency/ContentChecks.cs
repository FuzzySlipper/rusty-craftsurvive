using System.Numerics;
using System.Text.Json.Nodes;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// The authored terrain atlas against the block registry, and the player's global position against
/// the Engine's world-origin facts: both are pure, both guard what the player sees and where they
/// stand, and neither had a check.
/// </summary>
internal static class ContentChecks
{
    private const string AtlasFile = "content/game/textures/terrain-atlas.json";

    internal static void Run()
    {
        PlaceKit();

        // The staged atlas layout must satisfy the product's registry: every material block binds
        // tiles of its own name and face, no two tiles overlap, and every tile names a real block.
        string json = File.ReadAllText(Path.Combine(RepositoryRoot(), AtlasFile));
        TerrainAtlasLayout layout = TerrainAtlasLayout.Parse(json);
        Check.That(layout.Regions.Count >= BlockRegistry.MaterialBlocks.Count(),
            $"the atlas must hold at least one tile per material block, holds {layout.Regions.Count}");
        foreach (BlockDefinition block in BlockRegistry.MaterialBlocks)
        {
            Check.That(layout.RegionFor(block.BaseRegion).IsBaseFace, $"{block.Name} must bind a base tile");
        }

        // A layout that breaks any of those rules is refused, whichever rule it breaks.
        Check.Throws<InvalidOperationException>(() => TerrainAtlasLayout.Parse(Edit(json, root => root["schemaVersion"] = 99)),
            "a layout of another schema must be refused");
        Check.Throws<InvalidOperationException>(() => TerrainAtlasLayout.Parse(Edit(json, root => root["tileExtent"]![0] = 7)),
            "an extent that is not whole tiles must be refused");
        Check.Throws<InvalidOperationException>(() => TerrainAtlasLayout.Parse(Edit(json, root => root["regions"]![1]!["contentMin"] = root["regions"]![0]!["contentMin"]!.DeepClone())),
            "two tiles sharing a position must be refused");
        Check.Throws<InvalidOperationException>(() => TerrainAtlasLayout.Parse(Edit(json, root => root["regions"]![0]!["face"] = "side")),
            "a tile of an unknown face must be refused");
        Check.Throws<InvalidOperationException>(() => TerrainAtlasLayout.Parse(Edit(json, root => root["regions"]![0]!["block"] = "nonesuch")),
            "a tile naming no registered block must be refused");
        Check.Throws<InvalidOperationException>(() => TerrainAtlasLayout.Parse(Edit(json, root => root["contentHash"] = "sha256:not-a-hash")),
            "a content hash that is not SHA-256 hex must be refused");
        Check.Throws<InvalidOperationException>(() => TerrainAtlasLayout.Parse(Edit(json, root => root["regions"]!.AsArray().RemoveAt(0))),
            "a block whose tile is missing must be refused");

        // A global position is a whole cell and a fraction in [0, 1), negative coordinates included.
        PlayerWorldPosition position = PlayerWorldPosition.FromWorld(-0.25, 1.5, 1023.75);
        Check.That(position is { CellX: -1, CellY: 1, CellZ: 1023 }, $"-0.25, 1.5, 1023.75 must floor to cells -1, 1, 1023, was {position}");
        Check.That(position.OffsetX == 0.75 && position.OffsetY == 0.5 && position.OffsetZ == 0.75, "the fractions must be what the floor left");
        Check.That(position.WorldX == -0.25 && position.WorldZ == 1023.75, "the world coordinate must be the cell plus the fraction");
        Check.That(position.FloorVoxel() == new VoxelAddress(-1, 1, 1023), "the voxel under a position is its cell");
        Check.Throws<ArgumentOutOfRangeException>(() => PlayerWorldPosition.FromWorld(double.NaN, 0, 0), "a position that is not a number must be refused");

        // Local and global agree through a rebased origin, far from zero.
        WorldOriginReadout origin = new(1000, 0, -2000, Revision: 1, LocalEnvelope: 1024f, VoxelSourceRevision: 0, StaticMeshRevision: 0);
        Vector3 local = new(3.25f, 5.5f, -0.75f);
        PlayerWorldPosition fromLocal = PlayerWorldPosition.FromLocal(origin, local);
        Check.That(fromLocal is { CellX: 1003, CellY: 5, CellZ: -2001 }, $"a local position must land in the origin's frame, was {fromLocal}");
        Check.That(fromLocal.ToLocal(origin) == local, "a position must come back to the local point it came from");
        Check.That(fromLocal.WorldX == 1003.25 && fromLocal.WorldZ == -2000.75, "the world coordinate must add the origin's cell");
    }

    private static string Edit(string json, Action<JsonNode> change)
    {
        JsonNode root = JsonNode.Parse(json)!;
        change(root);
        return root.ToJsonString();
    }

    /// <summary>
    /// Every piece the place layouts can name is in the place kit (#9992), and each kit piece's model and
    /// the texture it names are installed, so no site can ask for a piece that is not there.
    /// </summary>
    private static void PlaceKit()
    {
        string root = RepositoryRoot();
        JsonNode kit = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "content/game/models/places/kit.json")))!;
        Dictionary<string, string> models = kit["pieces"]!.AsArray().ToDictionary(p => p!["name"]!.GetValue<string>(), p => p!["model"]!.GetValue<string>());
        string[] named =
        [
            PlaceLayouts.PiercedStele, PlaceLayouts.TallStele, PlaceLayouts.BrokenStele, PlaceLayouts.Cairn, PlaceLayouts.RuinWall,
            PlaceLayouts.RuinRib, PlaceLayouts.CaveArch, PlaceLayouts.EntranceArch, PlaceLayouts.EntrancePlatform,
            .. Enumerable.Range((int)PoiConstants.VantageMinimumSteps, (int)PoiConstants.VantageStepRange).Select(h => $"{PlaceLayouts.VantagePrefix}{h}"),
        ];
        foreach (string piece in named)
        {
            Check.That(models.ContainsKey(piece), $"the place kit must hold the {piece} piece the layouts name");
        }

        foreach ((string piece, string model) in models)
        {
            string path = Path.Combine(root, "content/game", model);
            Check.That(File.Exists(path), $"the place kit's {piece} model must be installed at {model}");
            if (!File.Exists(path)) continue;
            JsonNode mesh = JsonNode.Parse(File.ReadAllText(path))!;
            foreach (KeyValuePair<string, JsonNode?> texture in mesh["textures"]!.AsObject())
            {
                Check.That(File.Exists(Path.Combine(root, "content/game", texture.Value!.GetValue<string>())),
                    $"the place kit's {piece} texture {texture.Value} must be installed");
            }
        }
    }

    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, AtlasFile)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the check's binaries.");
    }
}
