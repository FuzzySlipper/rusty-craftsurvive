using System.Text;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

internal static class TerrainUiProjection
{
    internal static UiValue Create(VoxelSceneReadout scene, int overlayEntries, TerrainPlayerUiFacts? player,
        DiscoveryUiFacts? discovery)
    {
        TerrainNumericObjectBuilder values = new();
        values.Add("revision", scene.SourceRevision);
        values.Add("residentChunks", scene.ResidentChunkCount);
        values.Add("solidVoxels", scene.SolidVoxelCount);
        values.Add("overlayEntries", overlayEntries);
        if (player is TerrainPlayerUiFacts facts)
        {
            values.Add("playerX", facts.EyeX);
            values.Add("playerY", facts.EyeY);
            values.Add("playerZ", facts.EyeZ);
            values.Add("yawDegrees", facts.YawDegrees);
            values.Add("pitchDegrees", facts.PitchDegrees);
            values.Add("grounded", facts.Grounded ? 1d : 0d);
            values.Add("crouched", facts.Crouched ? 1d : 0d);
            values.Add("platformX", facts.PlatformX);
            values.Add("platformY", facts.PlatformY);
            values.Add("platformZ", facts.PlatformZ);
        }
        if (discovery is DiscoveryUiFacts journal)
        {
            // A journal is numbers only, deliberately: this object is a flat numeric map, and
            // naming a place is the journal's own readout's job. Kind and stage are their
            // enum values, which is why neither may ever be renumbered.
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
        }

        return values.Build();
    }

    /// <summary>Small terrain-local encoder for a flat numeric UI object.</summary>
    private sealed class TerrainNumericObjectBuilder
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
