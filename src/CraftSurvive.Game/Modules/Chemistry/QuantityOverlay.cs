using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using RustyChemistry.Simulation;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>How a field cell's material is drawn in the world, which decides how the overlay shows a value in it.</summary>
internal enum OverlayCell : byte
{
    /// <summary>Nothing drawn (air): a cube grows with the value.</summary>
    Open,

    /// <summary>A solid block: a tinted shell around it shows the value inside.</summary>
    Opaque,

    /// <summary>Water: a cube drawn opaque, since two blended surfaces in one cell sort unreliably.</summary>
    Translucent,
}

/// <summary>
/// Shows one quantity of the chemistry field as cubes, one mesh per field chunk (#10529, from rusty-chemistry's
/// sandbox): in an open cell a cube grows with the value, around a solid block a tinted shell shows the value inside.
/// Values are shown in a few levels, and a chunk's mesh is rebuilt only when they change. Meshes are built about their
/// chunk's corner and placed in the session's frame when the product's appearance snapshot is taken, so a rebase only
/// moves them; a replaced mesh is released once a snapshot no longer names it. It reads the field only while no step
/// runs, as its owner decides.
/// </summary>
internal sealed class QuantityOverlay : IDisposable
{
    private const int Levels = 8;
    private const int Size = ChemistryField.ChunkSize;
    private const int CellsPerChunk = Size * Size * Size;

    /// <summary>A shell stands just outside its block; a cube stays off the cell's faces, from this half-size up to the next.</summary>
    private const float ShellHalf = 0.52f, CubeHalfLeast = 0.12f, CubeHalfGrowth = 0.33f;

    /// <summary>A shell's and a cube's opacity at the lowest level, and what the full level adds.</summary>
    private const float ShellAlpha = 0.15f, ShellAlphaGrowth = 0.45f, CubeAlpha = 0.1f, CubeAlphaGrowth = 0.6f;

    private static readonly Vector3[] Corners =
    [
        new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
        new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1),
    ];

    // Each face as four corners, counter-clockwise from outside, with its normal.
    private static readonly (int A, int B, int C, int D, Vector3 Normal)[] Faces =
    [
        (1, 2, 6, 5, Vector3.UnitX), (4, 7, 3, 0, -Vector3.UnitX),
        (3, 7, 6, 2, Vector3.UnitY), (4, 0, 1, 5, -Vector3.UnitY),
        (5, 6, 7, 4, Vector3.UnitZ), (0, 3, 2, 1, -Vector3.UnitZ),
    ];

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly Func<int, OverlayCell> draw;
    private readonly Material blended;
    private readonly Material solid;
    private readonly Dictionary<Cell, ChunkOverlay> chunks = [];
    private readonly Stack<int> freeSlots = new(Enumerable.Range(0, ProductIds.ChemistryOverlayObjects).Reverse());
    private readonly float[] values = new float[CellsPerChunk];
    private readonly ushort[] materials = new ushort[CellsPerChunk];
    private readonly List<IDisposable> retired = [];
    private int quantity;
    private Color color;

    /// <param name="draw">How a field material is drawn in the world, by its index in the pack.</param>
    internal QuantityOverlay(IEngineContext engine, WorldFrame frame, Func<int, OverlayCell> draw)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.draw = draw ?? throw new ArgumentNullException(nameof(draw));
        Color white = new(1, 1, 1, 1);
        blended = engine.Graphics.CreateMaterial(new MaterialRequest(white, default, 1f, white, Vector3.Zero, 0f, true, MaterialAlphaMode.Blend, 0f)
            with { Unlit = true });
        solid = engine.Graphics.CreateMaterial(new MaterialRequest(white, default, 1f, white, Vector3.Zero, 0f, false) with { Unlit = true });
    }

    /// <summary>What is shown, or null for nothing.</summary>
    internal OverlaySetting? Shown { get; private set; }

    /// <summary>The overlay's objects for the product's appearance snapshot, placed in the session's frame now.</summary>
    internal IEnumerable<AppearanceFact> Facts => chunks
        .Where(pair => pair.Value.Appearance is not null)
        .Select(pair => new AppearanceFact(pair.Value.ObjectId, false, 0,
            new Transform(frame.ToLocal(pair.Key.X, pair.Key.Y, pair.Key.Z), Quaternion.Identity, Vector3.One), pair.Value.Appearance!,
            true, RenderLayer.Scene, ShadowCasting.None));

    /// <summary>Releases the meshes replaced or taken down, now that a snapshot without them is published.</summary>
    internal void AfterAppearanceSnapshot()
    {
        foreach (IDisposable resource in retired)
        {
            resource.Dispose();
        }

        retired.Clear();
    }

    /// <summary>The meshes drawn now, and the cubes in them.</summary>
    internal (int Meshes, int Cubes) Drawn => (chunks.Values.Count(chunk => chunk.Mesh is not null), chunks.Values.Sum(chunk => chunk.Cubes));

    /// <summary>Switches to another quantity, or to nothing.</summary>
    internal void Show(OverlaySetting? overlay, int quantityIndex)
    {
        Shown = overlay;
        quantity = quantityIndex;
        color = overlay is null ? default : Color.FromSrgbHex(overlay.Srgb, 1f);
        // The old quantity's meshes go; the next refresh draws every chunk in the new one.
        Hide();
    }

    /// <summary>Takes every mesh down, as when the player is in another space; the next refresh draws them again.</summary>
    internal void Hide()
    {
        Remove([.. chunks.Keys]);
    }

    /// <summary>
    /// Draws the chunks whose shown levels changed. Only awake chunks are read, since a sleeping chunk's values hold
    /// still; chunks the field let go are taken down.
    /// </summary>
    internal void Refresh(ChemistryField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (Shown is null)
        {
            return;
        }

        HashSet<Cell> loaded = [.. field.LoadedChunks()];
        Remove([.. chunks.Keys.Where(origin => !loaded.Contains(origin))]);
        foreach (Cell origin in loaded)
        {
            if (!chunks.TryGetValue(origin, out ChunkOverlay? chunk))
            {
                if (!freeSlots.TryPop(out int slot))
                {
                    continue;
                }

                chunk = new ChunkOverlay(ProductIds.ChemistryOverlayBase + (ulong)slot, slot);
                chunks.Add(origin, chunk);
            }

            if (!chunk.Stale && !field.IsActive(origin))
            {
                continue;
            }

            chunk.Stale = false;
            byte[] levels = Quantize(field, origin);
            if (chunk.Mesh is not null && levels.AsSpan().SequenceEqual(chunk.Levels))
            {
                continue;
            }

            levels.CopyTo(chunk.Levels, 0);
            Retire(chunk);
            field.ReadChunkMaterials(origin, materials);
            MeshResourceCreateRequest? request = Build(levels, out int cubes);
            chunk.Cubes = cubes;
            if (request is MeshResourceCreateRequest mesh)
            {
                chunk.Mesh = engine.Graphics.CreateMeshResource(mesh);
                chunk.Appearance = engine.Graphics.CreateMeshAppearance(chunk.Mesh);
            }
        }
    }

    public void Dispose()
    {
        Hide();
        AfterAppearanceSnapshot();
        blended.Dispose();
        solid.Dispose();
    }

    /// <summary>Takes these chunks' meshes down and forgets them, freeing their object ids.</summary>
    private void Remove(IReadOnlyList<Cell> origins)
    {
        foreach (Cell origin in origins)
        {
            ChunkOverlay chunk = chunks[origin];
            Retire(chunk);
            chunks.Remove(origin);
            freeSlots.Push(chunk.Slot);
        }
    }

    /// <summary>Drops a chunk's mesh from the snapshot; it is released after the next one.</summary>
    private void Retire(ChunkOverlay chunk)
    {
        if (chunk.Mesh is MeshResource mesh && chunk.Appearance is Appearance appearance)
        {
            retired.Add(appearance);
            retired.Add(mesh);
        }

        chunk.Mesh = null;
        chunk.Appearance = null;
        chunk.Cubes = 0;
    }

    private byte[] Quantize(ChemistryField field, Cell origin)
    {
        byte[] levels = new byte[CellsPerChunk];
        OverlaySetting shown = Shown!;
        field.ReadChunk(origin, quantity, values);
        float span = shown.High - shown.Low;
        for (int index = 0; index < CellsPerChunk; index++)
        {
            float share = (values[index] - shown.Low) / span;
            levels[index] = share < 0f ? (byte)0 : (byte)(1 + Math.Min(Levels - 1, (int)(share * Levels)));
        }

        return levels;
    }

    /// <summary>The chunk's cubes about its corner, or null when nothing in it shows.</summary>
    private MeshResourceCreateRequest? Build(byte[] levels, out int cubes)
    {
        List<Vector3> positions = [];
        List<Vector3> normals = [];
        List<Color> colors = [];
        List<uint> blendedIndices = [];
        List<uint> solidIndices = [];
        cubes = 0;
        for (int index = 0; index < CellsPerChunk; index++)
        {
            if (levels[index] == 0)
            {
                continue;
            }

            float strength = levels[index] / (float)Levels;
            Vector3 center = new Vector3(index % Size, index / Size % Size, index / (Size * Size)) + new Vector3(0.5f);
            OverlayCell cell = draw(materials[index]);
            bool shell = cell == OverlayCell.Opaque;
            float half = shell ? ShellHalf : CubeHalfLeast + (CubeHalfGrowth * strength);
            Color tint = color with { A = shell ? ShellAlpha + (ShellAlphaGrowth * strength) : CubeAlpha + (CubeAlphaGrowth * strength) };
            List<uint> indices = cell == OverlayCell.Translucent ? solidIndices : blendedIndices;
            foreach ((int a, int b, int c, int d, Vector3 normal) in Faces)
            {
                uint first = (uint)positions.Count;
                foreach (int corner in (ReadOnlySpan<int>)[a, b, c, d])
                {
                    positions.Add(center + (Corners[corner] * half));
                    normals.Add(normal);
                    colors.Add(tint);
                }

                indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            }

            cubes++;
        }

        if (cubes == 0)
        {
            return null;
        }

        List<MeshGroup> groups = [];
        List<MeshMaterialBinding> bindings = [];
        if (blendedIndices.Count > 0)
        {
            groups.Add(new(0, 0, (uint)blendedIndices.Count));
            bindings.Add(new(0, blended));
        }

        if (solidIndices.Count > 0)
        {
            groups.Add(new(1, (uint)blendedIndices.Count, (uint)solidIndices.Count));
            bindings.Add(new(1, solid));
        }

        uint[] indexed = [.. blendedIndices, .. solidIndices];
        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), new Vector2[positions.Count], colors.ToArray(), indexed,
            groups.ToArray(), bindings.ToArray());
    }

    private sealed class ChunkOverlay(ulong objectId, int slot)
    {
        internal ulong ObjectId { get; } = objectId;

        internal int Slot { get; } = slot;

        internal byte[] Levels { get; } = new byte[CellsPerChunk];

        internal bool Stale { get; set; } = true;

        internal MeshResource? Mesh { get; set; }

        internal Appearance? Appearance { get; set; }

        internal int Cubes { get; set; }
    }
}
