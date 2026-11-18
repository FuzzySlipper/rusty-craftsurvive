using System.Globalization;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The terrain's side of the caves under the world (#10889): which chunks a cave or a tendril holds, kept resident round
/// the player like the surface; how deep under the overworld's ground the player stands; and from that how much of the
/// overworld is kept. Near the surface everything is; going down a tendril into a cave the surface kept round the player
/// shrinks until, deep in, the overworld is let go entirely and only cave chunks are resident; climbing out reverses it.
/// </summary>
internal sealed class TerrainCaves
{
    /// <summary>Under this much ground the overworld is all kept.</summary>
    internal const double SurfaceDepthMetres = 8d;

    /// <summary>Under this much ground none of the overworld is kept: deep in a cave.</summary>
    internal const double DeepDepthMetres = 32d;

    /// <summary>How far up and down from the player's chunk a cave's chunks are kept.</summary>
    internal const int ShellChunksUpDown = 3;

    /// <summary>Under a share of this much of the way down the player's own storey is not kept (it is buried rock).</summary>
    private const double StoreyBelow = 0.25d;

    private readonly TerrainRecipe recipe;
    private readonly Dictionary<(long X, long Z), (CavePlacement Cave, HashSet<TerrainChunkAddress> Shell)> admitted = [];
    private readonly Dictionary<(long X, long Z), HashSet<TerrainChunkAddress>> tendrils = [];
    private long revision = 1;
    private int surfaceRadius = int.MaxValue;
    private bool storey = true;
    private HashSet<TerrainChunkAddress> shell = [];
    private TerrainChunkAddress shellCentre;
    private long shellRevision = -1;

    internal TerrainCaves(TerrainRecipe recipe) => this.recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));

    /// <summary>How far under the overworld's ground the player stands, in metres (0 out of every cave's reach).</summary>
    internal double Depth { get; private set; }

    /// <summary>How far down the player is, from 0 (the overworld all kept) to 1 (deep in a cave, none of it kept).</summary>
    internal double Below { get; private set; }

    /// <summary>The cave whose box or tendril the player is in, if any.</summary>
    internal CavePlacement? Within { get; private set; }

    /// <summary>The caves admitted, by their mouths' keys.</summary>
    internal IEnumerable<CavePlacement> Admitted => admitted.Values.Select(a => a.Cave);

    /// <summary>
    /// Reads a generated cave into the world: its chunks generate with it from now on. Returns the chunks it changes, for
    /// the residency to generate again.
    /// </summary>
    internal Func<TerrainChunkAddress, bool> Admit(CavePlacement cave, CaveInterior interior, HashSet<TerrainChunkAddress> caveShell)
    {
        recipe.Caves.Admit(cave, interior);
        admitted[cave.Key] = (cave, caveShell);
        revision++;
        return address => InBoxChunks(cave, address);
    }

    /// <summary>Lets a cave go: its box is rock again. Returns the chunks it changes.</summary>
    internal Func<TerrainChunkAddress, bool> Release(CavePlacement cave)
    {
        recipe.Caves.Release(cave);
        admitted.Remove(cave.Key);
        revision++;
        return address => InBoxChunks(cave, address);
    }

    internal bool IsAdmitted(CavePlacement cave) => admitted.ContainsKey(cave.Key);

    /// <summary>
    /// Follows the player: how deep they stand and in which cave, and what is kept underground round them now. The
    /// revision changes only when what is kept does.
    /// </summary>
    internal TerrainResidencyPolicy.UndergroundResidency Follow(VoxelAddress feet, int retainedRadius)
    {
        long edge = TerrainConstants.ChunkEdgeLength;
        Within = null;
        foreach (CavePlacement cave in recipe.Caves.NearChunkColumn(GridMath.FloorDivide(feet.X, edge), GridMath.FloorDivide(feet.Z, edge)))
        {
            bool inBox = feet.X >= cave.BoxMinimum.X && feet.X <= cave.BoxMaximum.X && feet.Z >= cave.BoxMinimum.Z && feet.Z <= cave.BoxMaximum.Z
                && feet.Y >= cave.BoxMinimum.Y - 2 && feet.Y <= cave.BoxMaximum.Y + 2;
            bool inTendril = feet.X >= cave.TendrilMinimum.X && feet.X <= cave.TendrilMaximum.X && feet.Z >= cave.TendrilMinimum.Z
                && feet.Z <= cave.TendrilMaximum.Z && feet.Y >= cave.TendrilMinimum.Y - 2 && feet.Y <= cave.TendrilMaximum.Y + 2;
            if (inBox || inTendril)
            {
                Within = cave;
                break;
            }
        }

        // Only inside a cave's reach does depth count: a hollow dug in the open world is still the open world.
        Depth = Within is null ? 0d : Math.Max(0d, recipe.SurfaceAt(feet.X, feet.Z) - feet.Y);
        Below = Math.Clamp((Depth - SurfaceDepthMetres) / (DeepDepthMetres - SurfaceDepthMetres), 0d, 1d);
        int radius = Below >= 1d ? -1 : Below <= 0d ? int.MaxValue : (int)Math.Ceiling(retainedRadius * (1d - Below));
        bool keepStorey = Below < StoreyBelow;
        TerrainChunkAddress centre = feet.Chunk;
        bool shellMoved = centre != shellCentre || shellRevision != revision;
        if (shellMoved)
        {
            shell = Shell(centre, retainedRadius);
            shellCentre = centre;
            shellRevision = revision;
        }

        if (shellMoved || radius != surfaceRadius || keepStorey != storey)
        {
            surfaceRadius = radius;
            storey = keepStorey;
            revision++;
            shellRevision = revision;
        }

        return new TerrainResidencyPolicy.UndergroundResidency(revision, surfaceRadius, storey, shell);
    }

    /// <summary>Whether a chunk is a cave's or a tendril's, not the overworld's surface (for the residency readout's split).</summary>
    internal bool IsUnderground(TerrainChunkAddress address) => shell.Contains(address);

    /// <summary>The underground readout: depth, how much of the overworld is kept, the caves admitted, and the resident split.</summary>
    internal string Readout(IEnumerable<TerrainChunkAddress> resident)
    {
        int cave = 0, overworld = 0;
        foreach (TerrainChunkAddress address in resident)
        {
            if (IsUnderground(address)) cave++;
            else overworld++;
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"underground depth={Depth:F1}m below={Below:F2} surfaceRadius={(surfaceRadius == int.MaxValue ? "all" : surfaceRadius.ToString(CultureInfo.InvariantCulture))} storey={storey} within={(Within is CavePlacement w ? $"{w.Site.X},{w.Site.Z}" : "none")} caves={admitted.Count} shell={shell.Count} resident: cave={cave} overworld={overworld}");
    }

    /// <summary>The cave and tendril chunks round a chunk, as far as the surface is kept across and a few chunks up and down.</summary>
    private HashSet<TerrainChunkAddress> Shell(TerrainChunkAddress centre, int radius)
    {
        HashSet<TerrainChunkAddress> chunks = [];
        long edge = TerrainConstants.ChunkEdgeLength;
        long reach = (radius + 1) * edge;
        foreach (CavePlacement cave in recipe.Caves.Near((centre.X * edge) - reach, (centre.X * edge) + reach, (centre.Z * edge) - reach, (centre.Z * edge) + reach))
        {
            IEnumerable<TerrainChunkAddress> held = TendrilChunks(cave);
            if (admitted.TryGetValue(cave.Key, out (CavePlacement Cave, HashSet<TerrainChunkAddress> Shell) box))
            {
                held = held.Concat(box.Shell);
            }

            foreach (TerrainChunkAddress chunk in held)
            {
                if (Math.Abs(chunk.X - centre.X) <= radius && Math.Abs(chunk.Z - centre.Z) <= radius && Math.Abs(chunk.Y - centre.Y) <= ShellChunksUpDown)
                {
                    chunks.Add(chunk);
                }
            }
        }

        return chunks;
    }

    /// <summary>The chunks a tendril's air reaches and every chunk beside them (their rock carries its surface).</summary>
    private HashSet<TerrainChunkAddress> TendrilChunks(CavePlacement cave)
    {
        if (tendrils.TryGetValue(cave.Key, out HashSet<TerrainChunkAddress>? known))
        {
            return known;
        }

        HashSet<TerrainChunkAddress> open = [];
        (long X, long Y, long Z) min = cave.TendrilMinimum, max = cave.TendrilMaximum;
        for (long x = min.X; x <= max.X; x++)
        for (long y = min.Y; y <= max.Y; y++)
        for (long z = min.Z; z <= max.Z; z++)
        {
            if (cave.TendrilOpen(new System.Numerics.Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) > 0f)
            {
                open.Add(new VoxelAddress(x, y, z).Chunk);
            }
        }

        HashSet<TerrainChunkAddress> chunks = Beside(open);
        tendrils[cave.Key] = chunks;
        return chunks;
    }

    /// <summary>Chunks and every chunk touching one.</summary>
    internal static HashSet<TerrainChunkAddress> Beside(IEnumerable<TerrainChunkAddress> chunks)
    {
        HashSet<TerrainChunkAddress> beside = [];
        foreach (TerrainChunkAddress chunk in chunks)
        {
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
            for (long dz = -1; dz <= 1; dz++)
            {
                beside.Add(new TerrainChunkAddress(chunk.X + dx, chunk.Y + dy, chunk.Z + dz));
            }
        }

        return beside;
    }

    private static bool InBoxChunks(CavePlacement cave, TerrainChunkAddress address)
    {
        long edge = TerrainConstants.ChunkEdgeLength;
        long x0 = address.X * edge, y0 = address.Y * edge, z0 = address.Z * edge;
        return x0 + edge - 1 >= cave.BoxMinimum.X && x0 <= cave.BoxMaximum.X && y0 + edge - 1 >= cave.BoxMinimum.Y && y0 <= cave.BoxMaximum.Y
            && z0 + edge - 1 >= cave.BoxMinimum.Z && z0 <= cave.BoxMaximum.Z;
    }
}
