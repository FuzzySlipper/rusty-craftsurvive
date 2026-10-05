using System.Text;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The product's one UI projection, as a flat object of numbers and short texts: the world's scene
/// facts, the player's pose, vitals and progress, the journal, what the last UI action came to,
/// the world's time and difficulty, the player's food and air, what they carry and can make, and
/// where they stand with dungeons. Each owner pushes its facts to the publisher; the projection is
/// how they are laid out for the DOM companion. A list travels as one text of entries separated by
/// <c>;</c>, with fields separated by <c>|</c>.
/// </summary>
internal static class ProductUiProjection
{
    internal static UiValue Create(WorldUiFacts? sceneFacts, PlayerUiFacts? player,
        DiscoveryUiFacts? discovery, ActionUiFacts? actions, ConditionsUiFacts? conditions, SurvivalUiFacts? survival, InventoryUiFacts? inventory, DungeonUiFacts? dungeon, WorldMapUiFacts? map = null)
    {
        NumericObjectBuilder values = new();
        if (sceneFacts is WorldUiFacts scene)
        {
            values.Add("revision", scene.Scene.SourceRevision);
            values.Add("residentChunks", scene.Scene.ResidentChunkCount);
            values.Add("solidVoxels", scene.Scene.SolidVoxelCount);
            values.Add("overlayEntries", scene.OverlayEntries);
        }
        if (player is PlayerUiFacts facts)
        {
            values.Add("playerX", facts.EyeX);
            values.Add("playerY", facts.EyeY);
            values.Add("playerZ", facts.EyeZ);
            values.Add("yawDegrees", facts.YawDegrees);
            values.Add("pitchDegrees", facts.PitchDegrees);
            values.Add("grounded", facts.Grounded ? 1d : 0d);
            values.Add("crouched", facts.Crouched ? 1d : 0d);
            values.Add("health", facts.Health);
            values.Add("maximumHealth", facts.MaximumHealth);
            values.Add("defeats", facts.Defeats);
            values.Add("experience", facts.Experience);
            values.Add("level", facts.Level);
            values.Add("itemsCollected", facts.ItemsCollected);
            values.Add("stamina", facts.Stamina);
            values.Add("maximumStamina", facts.MaximumStamina);
            values.Add("climbing", facts.Climbing ? 1d : 0d);
            values.Add("hitsTaken", facts.HitsTaken);
            values.Add("submerged", facts.Submerged ? 1d : 0d);
        }
        if (discovery is DiscoveryUiFacts journal)
        {
            // Kind and stage are their enum values, which is why neither may ever be renumbered;
            // the journal names the last place itself, so the UI shows it without a copy of either enum.
            values.Add("discoveryPlaces", journal.Places);
            values.Add("discoveryVisited", journal.Visited);
            values.Add("discoverySeen", journal.Seen);
            values.Add("discoveryRefused", journal.Refused);
            values.Add("discoveryNearest", journal.NearestMetres);
            values.Add("discoveryLastX", journal.LastX);
            values.Add("discoveryLastZ", journal.LastZ);
            values.Add("discoveryLastKind", journal.LastKind);
            values.Add("discoveryLastStage", journal.LastStage);
            values.Add("discoveryLastTick", journal.LastTick);
            values.AddText("discoveryLastFound", journal.LastFound);
            values.AddText("journalPlaces", journal.Journal);
        }

        if (actions is ActionUiFacts requests)
        {
            values.Add("actionsApplied", requests.Applied);
            values.Add("actionsRefused", requests.Refused);
            values.AddText("lastAction", requests.Last);
            values.AddText("buildPalette", requests.Palette);
        }

        if (conditions is ConditionsUiFacts world)
        {
            values.AddText("worldTime", world.Time);
            values.Add("daylight", world.Daylight);
            values.Add("night", world.Night ? 1d : 0d);
            values.AddText("difficulty", world.Difficulty);
            values.AddText("difficulties", world.Difficulties);
        }

        if (survival is SurvivalUiFacts tracks)
        {
            values.Add("satiety", tracks.Satiety);
            values.Add("breath", tracks.Breath);
            values.Add("maximumBreath", tracks.MaximumBreath);
            values.AddText("lastHarm", tracks.LastHarm);
        }

        if (inventory is InventoryUiFacts carried)
        {
            values.AddText("carried", carried.Carried);
            values.AddText("packItems", carried.Items);
            values.Add("hotbarSlots", carried.HotbarSlots);
            values.Add("packSlots", carried.PackSlots);
            values.Add("hotbarSelected", carried.Selected);
            values.AddText("recipeBook", carried.RecipeBook);
            values.Add("packLoad", carried.Load);
            values.Add("packLimit", carried.Limit);
            values.Add("torches", carried.Torches);
            values.AddText("lastInventory", carried.Last);
        }

        if (dungeon is DungeonUiFacts below)
        {
            values.AddText("dungeonState", below.State);
            values.Add("dungeonProgress", below.Progress);
            values.AddText("dungeonPrompt", below.Prompt);
            values.Add("dungeonCanEnter", below.CanEnter ? 1d : 0d);
            values.Add("dungeonCanLeave", below.CanLeave ? 1d : 0d);
            values.AddText("dungeonLast", below.Last);
        }

        if (map is WorldMapUiFacts geography)
        {
            values.Add("worldMapOpen", geography.Open ? 1 : 0);
            values.AddText("worldSeed", geography.Seed);
            values.Add("worldSize", geography.Size);
            values.AddText("worldSites", geography.Sites);
            values.AddText("worldMessage", geography.Message);
            values.Add("worldGeneration", geography.Generation);
            values.Add("worldMapFaceted", geography.Faceted ? 1 : 0);
        }

        return values.Build();
    }

    /// <summary>Small encoder for a flat UI object of numbers and text.</summary>
    private sealed class NumericObjectBuilder
    {
        private readonly List<StructuredValueNode> nodes = [];
        private readonly List<uint> edges = [];
        private readonly List<byte> utf8 = [];

        internal void Add(string key, double value)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            uint keyOffset = checked((uint)utf8.Count);
            utf8.AddRange(keyBytes);
            uint nodeIndex = checked((uint)nodes.Count + 1U);
            nodes.Add(new StructuredValueNode(
                StructuredValueKind.Number,
                0,
                value,
                keyOffset,
                checked((uint)keyBytes.Length),
                0,
                0,
                0,
                0));
            edges.Add(nodeIndex);
        }

        internal void AddText(string key, string value)
        {
            ArgumentException.ThrowIfNullOrEmpty(key);
            ArgumentNullException.ThrowIfNull(value);
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            uint keyOffset = checked((uint)utf8.Count);
            utf8.AddRange(keyBytes);
            byte[] textBytes = Encoding.UTF8.GetBytes(value);
            uint textOffset = checked((uint)utf8.Count);
            utf8.AddRange(textBytes);
            uint nodeIndex = checked((uint)nodes.Count + 1U);
            nodes.Add(new StructuredValueNode(
                StructuredValueKind.String,
                0,
                0,
                keyOffset,
                checked((uint)keyBytes.Length),
                textOffset,
                checked((uint)textBytes.Length),
                0,
                0));
            edges.Add(nodeIndex);
        }

        internal UiValue Build()
        {
            StructuredValueNode root = new(
                StructuredValueKind.Object,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                checked((uint)edges.Count));
            StructuredValueNode[] values = new StructuredValueNode[nodes.Count + 1];
            values[0] = root;
            nodes.CopyTo(values, 1);
            return new UiValue(values, edges.ToArray(), 0, utf8.ToArray());
        }
    }
}
