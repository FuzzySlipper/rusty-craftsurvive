using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A built dungeon's moisture (#10092; the design recorded on #10044 and #9833): how damp the air is through the dungeon,
/// 0 dry to 1 wet, on a grid of <see cref="Step"/>-metre cells. Water comes from the caves the building opens onto (their
/// air is damp), from a few seeps where a ceiling lets water through, and rises from the deep: lower storeys are damper.
/// It spreads through air only (walls hold it back), more readily down than up, and fades with distance. The moss stain,
/// cards, ivy and drips are grown from it.
/// </summary>
internal sealed class DungeonMoisture
{
    /// <summary>The grid's cell, in metres (voxels): coarse enough to relax quickly, fine enough for a room's corners.</summary>
    internal const int Step = 2;

    // Sources: a cave module's air, a seep (water through a ceiling), and the building's own air at the top and bottom of
    // the dungeon's height.
    private const float CaveDamp = 1f, Seep = 1f, BuildingDryest = 0.3f, BuildingDeepest = 0.6f;

    /// <summary>The share of ceiling cells that seep, by the seed.</summary>
    private const double SeepShare = 0.03;

    // Spreading: how much a cell keeps of its neighbours' mean per step, the weights of the cell above (water falls), beside
    // and below, and how many relaxation passes are made.
    private const float Keep = 0.95f, FromAbove = 1.6f, FromBeside = 1f, FromBelow = 0.5f;
    private const int Passes = 48;

    private readonly int sizeX, sizeY, sizeZ;
    private readonly float[] wet;
    private readonly bool[] air;

    private DungeonMoisture(int sizeX, int sizeY, int sizeZ)
    {
        this.sizeX = sizeX;
        this.sizeY = sizeY;
        this.sizeZ = sizeZ;
        wet = new float[sizeX * sizeY * sizeZ];
        air = new bool[wet.Length];
    }

    /// <summary>How many cells seep (water through a ceiling).</summary>
    internal int Seeps => seeps.Count;

    /// <summary>Where water seeps through a ceiling, in layout metres: the middle of its cell's top.</summary>
    internal IReadOnlyList<Vector3> SeepPoints => seeps;

    private readonly List<Vector3> seeps = [];

    /// <summary>The moisture at a point of the layout (metres), 0 where the grid has no air (inside rock or a wall).</summary>
    internal float At(Vector3 p)
    {
        int x = (int)MathF.Floor(p.X / Step), y = (int)MathF.Floor(p.Y / Step), z = (int)MathF.Floor(p.Z / Step);
        return Inside(x, y, z) ? wet[Index(x, y, z)] : 0f;
    }

    /// <summary>The moisture of a built dungeon's volume, its modules telling cave from building.</summary>
    internal static DungeonMoisture Compute(DungeonVolume volume, IReadOnlyList<PlacedModule> modules, ulong seed)
    {
        DungeonMoisture m = new((volume.SizeX + Step - 1) / Step, (volume.SizeY + Step - 1) / Step, (volume.SizeZ + Step - 1) / Step);
        float[] source = new float[m.wet.Length];
        // Which cells lie in a cave module (their air is damp), by the modules' boxes.
        bool[] cave = new bool[m.wet.Length];
        foreach (PlacedModule module in modules.Where(module => module.Shape.Kind == ModuleKind.Cave))
        {
            int x0 = module.X * ModuleCanvas.CellSize, y0 = (module.Y * ModuleCanvas.StoreyHeight) + ModularDungeon.BaseY, z0 = module.Z * ModuleCanvas.CellSize;
            for (int x = x0 / Step; x < (x0 + (module.Width * ModuleCanvas.CellSize)) / Step; x++)
                for (int y = y0 / Step; y < (y0 + (module.Shape.Height * ModuleCanvas.StoreyHeight)) / Step; y++)
                    for (int z = z0 / Step; z < (z0 + (module.Depth * ModuleCanvas.CellSize)) / Step; z++)
                    {
                        if (m.Inside(x, y, z))
                        {
                            cave[m.Index(x, y, z)] = true;
                        }
                    }
        }

        // The air's lowest and highest cells, for the building's rising damp.
        int lowest = int.MaxValue, highest = int.MinValue;
        for (int x = 0; x < m.sizeX; x++)
            for (int y = 0; y < m.sizeY; y++)
                for (int z = 0; z < m.sizeZ; z++)
                {
                    if (!AnyAir(volume, x, y, z))
                    {
                        continue;
                    }

                    m.air[m.Index(x, y, z)] = true;
                    lowest = Math.Min(lowest, y);
                    highest = Math.Max(highest, y);
                }

        for (int x = 0; x < m.sizeX; x++)
            for (int y = 0; y < m.sizeY; y++)
                for (int z = 0; z < m.sizeZ; z++)
                {
                    int i = m.Index(x, y, z);
                    if (!m.air[i])
                    {
                        continue;
                    }

                    float height = highest > lowest ? (y - lowest) / (float)(highest - lowest) : 0f;
                    source[i] = cave[i] ? CaveDamp : BuildingDeepest + ((BuildingDryest - BuildingDeepest) * height);
                    // A seep: a ceiling over this air (no air above), by the seed.
                    if (!cave[i] && (y + 1 >= m.sizeY || !m.air[m.Index(x, y + 1, z)]) && Hash(seed, x, y, z) < SeepShare)
                    {
                        source[i] = Seep;
                        m.seeps.Add(new Vector3((x + 0.5f) * Step, (y + 1) * Step, (z + 0.5f) * Step));
                    }
                }

        Array.Copy(source, m.wet, source.Length);
        for (int pass = 0; pass < Passes; pass++)
        {
            for (int x = 0; x < m.sizeX; x++)
                for (int y = 0; y < m.sizeY; y++)
                    for (int z = 0; z < m.sizeZ; z++)
                    {
                        int i = m.Index(x, y, z);
                        if (!m.air[i])
                        {
                            continue;
                        }

                        float sum = 0f, weight = 0f;
                        void From(int nx, int ny, int nz, float w)
                        {
                            if (m.Inside(nx, ny, nz) && m.air[m.Index(nx, ny, nz)])
                            {
                                sum += m.wet[m.Index(nx, ny, nz)] * w;
                                weight += w;
                            }
                        }

                        From(x, y + 1, z, FromAbove);
                        From(x, y - 1, z, FromBelow);
                        From(x + 1, y, z, FromBeside);
                        From(x - 1, y, z, FromBeside);
                        From(x, y, z + 1, FromBeside);
                        From(x, y, z - 1, FromBeside);
                        float spread = weight > 0f ? Keep * sum / weight : 0f;
                        m.wet[i] = Math.Max(source[i], spread);
                    }
        }

        return m;
    }

    private static bool AnyAir(DungeonVolume volume, int cx, int cy, int cz)
    {
        for (int x = cx * Step; x < (cx + 1) * Step; x++)
            for (int y = cy * Step; y < (cy + 1) * Step; y++)
                for (int z = cz * Step; z < (cz + 1) * Step; z++)
                {
                    if (volume.At(x, y, z) == BlockId.Air)
                    {
                        return true;
                    }
                }

        return false;
    }

    private bool Inside(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < sizeX && y < sizeY && z < sizeZ;

    private int Index(int x, int y, int z) => (((y * sizeZ) + z) * sizeX) + x;

    private static double Hash(ulong seed, int x, int y, int z)
    {
        ulong h = seed ^ ((ulong)(uint)x * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)(uint)z * 0x165667B19E3779F9UL) ^ 0x6D6F6973UL;
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return (h >> 11) / (double)(1UL << 53);
    }
}
