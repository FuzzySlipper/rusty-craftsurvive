using System.Numerics;
using RustyChemistry.Casting;
using RustyChemistry.Content;
using RustyChemistry.Simulation;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>What casts did over a worker's steps, for <see cref="CastLook"/>: where they landed, what passed, and the ground's marks (null: not read again).</summary>
internal sealed record CastSeen(IReadOnlyList<CastLanding> Landings, IReadOnlyList<CastSpark> Sparks, IReadOnlyList<CastMark>? Marks)
{
    internal static readonly CastSeen Nothing = new([], [], null);
}

/// <summary>A cast's operation landing: where (cells), the quantity it acts through, whether it drains, how far it reaches (cells) and how long it acts (seconds).</summary>
internal readonly record struct CastLanding(Vector3 At, int Quantity, bool Drains, float Reach, float Seconds);

/// <summary>A passing mark: a cell a discharge ran through, or one a front's leading edge reached.</summary>
internal readonly record struct CastSpark(Cell Cell, int Quantity, bool Discharge);

/// <summary>A cell of ground marked around a cast: by which of the settings' marks, and how far towards full (0 to 1).</summary>
internal readonly record struct CastMark(Cell Cell, int Mark, float Share);

/// <summary>
/// The worker's side of seeing casts (#10529): it notes each operation the caster lands, the cells each discharge runs
/// through and each cast front's leading edge, and around every cast reads the ground's top cells for the settings'
/// marks. It touches nothing but the field, and only on the worker (or with the worker idle).
/// </summary>
internal sealed class CastWatch
{
    private const int Size = ChemistryField.ChunkSize;
    private const int CellsPerChunk = Size * Size * Size;

    /// <summary>At most this many casts' surroundings are watched at once; a new one replaces the one ending soonest.</summary>
    private const int MostRegions = 16;

    /// <summary>Marks are read above and below a cast's centre at most this many cells: the ground near it, not the whole sphere.</summary>
    private const float MostDepth = 6f;

    /// <summary>The ground is read again at most this often (field seconds), and at most this many marks are kept, the strongest.</summary>
    private const double ScanSeconds = 0.25;
    private const int MostMarks = 640;

    /// <summary>Of each discharge at most this many cells are marked; of each front this many of its leading edge each step; all told at most this many a hand-off.</summary>
    private const int SparksPerDischarge = 40, SparksPerFrontStep = 12, MostSparks = 384;

    /// <summary>A front's leading edge: the cells it reached this step within this many cells of its farthest.</summary>
    private const float EdgeCells = 1f;

    private readonly CastLookSetting setting;
    private readonly int[] quantities;
    private readonly HashSet<int>?[] only;
    private readonly HashSet<int> ground;
    private readonly List<Region> regions = [];
    private readonly Dictionary<int, int> fronts = [];
    private readonly List<(Cell Origin, int Quantity)> starting = [];
    private readonly List<CastLanding> landings = [];
    private readonly List<CastSpark> sparks = [];
    private readonly Dictionary<int, float[]> amounts = [];
    private readonly ushort[] materials = new ushort[CellsPerChunk];
    private double scanned = double.NegativeInfinity;
    private bool marked;

    internal CastWatch(CastLookSetting setting, ChemistryPack pack)
    {
        this.setting = setting ?? throw new ArgumentNullException(nameof(setting));
        ArgumentNullException.ThrowIfNull(pack);
        ground = [.. setting.Ground.Select(pack.Material)];
        quantities = [.. setting.Marks.Select(mark => pack.Quantity(mark.Quantity))];
        only = [.. setting.Marks.Select(mark => mark.Only is null ? null : new HashSet<int>(mark.Only.Select(pack.Material)))];
        foreach (int quantity in quantities.Distinct())
        {
            amounts[quantity] = new float[CellsPerChunk];
        }
    }

    /// <summary>The caster adds each operation it lands here, before the step (<see cref="Caster.Advance"/>).</summary>
    internal List<CastOperation> Landed { get; } = [];

    /// <summary>Notes what landed before this step and what its discharges and fronts reached.</summary>
    internal void Stepped(ChemistryField field, double now)
    {
        foreach (CastOperation operation in Landed)
        {
            CastLanding landing = operation switch
            {
                EmitOperation emit => new CastLanding(emit.Spec.Shape.Center, emit.Spec.Quantity, emit.Spec.Rate < 0f, emit.Spec.Shape.Radius,
                    float.IsFinite(emit.Spec.Lifetime) ? emit.Spec.Lifetime : 0f),
                DischargeOperation discharge => new CastLanding(Centre(discharge.Origin), discharge.Quantity, false, 1f, 0f),
                FrontOperation front => new CastLanding(Centre(front.Origin), front.Quantity, front.Amplitude < 0f, front.MaxDistance,
                    front.Speed > 0f ? front.MaxDistance / front.Speed : 0f),
                _ => new CastLanding(Vector3.Zero, -1, false, 0f, 0f),
            };
            if (landing.Quantity < 0)
            {
                continue;
            }

            if (operation is FrontOperation started)
            {
                starting.Add((started.Origin, started.Quantity));
            }

            landings.Add(landing);
            Watch(landing.At, landing.Reach + setting.WatchMargin, now + landing.Seconds + setting.WatchSeconds);
        }

        Landed.Clear();
        foreach (Discharge discharge in field.Discharges)
        {
            int stride = Math.Max(1, discharge.Cells.Count / SparksPerDischarge);
            for (int index = 0; index < discharge.Cells.Count && sparks.Count < MostSparks; index += stride)
            {
                sparks.Add(new CastSpark(discharge.Cells[index], discharge.Quantity, true));
            }

            Watch(Centre(discharge.Origin), setting.WatchMargin, now + setting.WatchSeconds);
        }

        Edges(field.FrontArrivals);
    }

    /// <summary>What the steps since the last hand-off saw, and the ground's marks if it was time to read them.</summary>
    internal CastSeen Take(ChemistryField field, double now)
    {
        IReadOnlyList<CastMark>? marks = null;
        if (now - scanned >= ScanSeconds)
        {
            scanned = now;
            marks = Scan(field, now);
        }

        if (landings.Count == 0 && sparks.Count == 0 && marks is null)
        {
            return CastSeen.Nothing;
        }

        CastSeen seen = new([.. landings], [.. sparks], marks);
        landings.Clear();
        sparks.Clear();
        return seen;
    }

    /// <summary>Forgets every cast, as when the field is rebuilt.</summary>
    internal void Clear()
    {
        Landed.Clear();
        regions.Clear();
        fronts.Clear();
        starting.Clear();
        landings.Clear();
        sparks.Clear();
        scanned = double.NegativeInfinity;
        marked = false;
    }

    /// <summary>Each cast front's leading edge this step. A front is known as a cast's by its origin arriving the step it starts.</summary>
    private void Edges(IReadOnlyList<FrontArrival> arrivals)
    {
        HashSet<int> seen = [];
        foreach (IGrouping<int, FrontArrival> front in arrivals.GroupBy(arrival => arrival.Front))
        {
            seen.Add(front.Key);
            if (!fronts.TryGetValue(front.Key, out int quantity))
            {
                int start = starting.FindIndex(entry => front.Any(arrival => arrival.Distance == 0f && arrival.Cell == entry.Origin));
                if (start < 0)
                {
                    continue;
                }

                quantity = starting[start].Quantity;
                fronts[front.Key] = quantity;
                starting.RemoveAt(start);
            }

            float farthest = front.Max(arrival => arrival.Distance);
            List<FrontArrival> edge = [.. front.Where(arrival => arrival.Distance >= farthest - EdgeCells)];
            int stride = Math.Max(1, edge.Count / SparksPerFrontStep);
            for (int index = 0; index < edge.Count && sparks.Count < MostSparks; index += stride)
            {
                sparks.Add(new CastSpark(edge[index].Cell, quantity, false));
            }
        }

        starting.Clear();
        foreach (int done in fronts.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            fronts.Remove(done);
        }
    }

    private void Watch(Vector3 centre, float radius, double until)
    {
        int same = regions.FindIndex(region => Vector3.DistanceSquared(region.Centre, centre) < 1f);
        if (same >= 0)
        {
            Region region = regions[same];
            regions[same] = region with { Radius = Math.Max(region.Radius, radius), Until = Math.Max(region.Until, until) };
            return;
        }

        if (regions.Count >= MostRegions)
        {
            regions.RemoveAt(regions.IndexOf(regions.MinBy(region => region.Until)!));
        }

        regions.Add(new Region(centre, radius, until, Marked: true));
    }

    /// <summary>
    /// The ground's marks around every watched cast: of each column, the top ground cell any mark holds. A cast is watched
    /// until its time is up and nothing around it is marked any more.
    /// </summary>
    private List<CastMark>? Scan(ChemistryField field, double now)
    {
        regions.RemoveAll(region => region.Until < now && !region.Marked);
        if (regions.Count == 0)
        {
            if (!marked)
            {
                return null;
            }

            marked = false;
            return [];
        }

        HashSet<Cell> chunks = [];
        foreach (Region region in regions)
        {
            float depth = Math.Min(region.Radius, MostDepth);
            Cell low = Chunk(region.Centre - new Vector3(region.Radius, depth, region.Radius));
            Cell high = Chunk(region.Centre + new Vector3(region.Radius, depth, region.Radius));
            for (int x = low.X; x <= high.X; x += Size)
            {
                for (int y = low.Y; y <= high.Y; y += Size)
                {
                    for (int z = low.Z; z <= high.Z; z += Size)
                    {
                        Cell origin = new(x, y, z);
                        if (field.IsLoaded(origin))
                        {
                            chunks.Add(origin);
                        }
                    }
                }
            }
        }

        bool[] holds = new bool[regions.Count];
        Dictionary<(int X, int Z), CastMark> top = [];
        foreach (Cell origin in chunks)
        {
            field.ReadChunkMaterials(origin, materials);
            foreach ((int quantity, float[] values) in amounts)
            {
                field.ReadChunk(origin, quantity, values);
            }

            for (int index = 0; index < CellsPerChunk; index++)
            {
                if (!ground.Contains(materials[index]))
                {
                    continue;
                }

                Cell cell = origin.Offset(index % Size, index / Size % Size, index / (Size * Size));
                int within = Within(Centre(cell));
                if (within < 0)
                {
                    continue;
                }

                for (int mark = 0; mark < quantities.Length; mark++)
                {
                    float share = setting.Marks[mark].Share(amounts[quantities[mark]][index]);
                    if (share <= 0f || (only[mark] is { } allowed && !allowed.Contains(materials[index])))
                    {
                        continue;
                    }

                    holds[within] = true;
                    if (!top.TryGetValue((cell.X, cell.Z), out CastMark above) || above.Cell.Y < cell.Y)
                    {
                        top[(cell.X, cell.Z)] = new CastMark(cell, mark, Math.Min(share, 1f));
                    }

                    break;
                }
            }
        }

        for (int index = 0; index < regions.Count; index++)
        {
            regions[index] = regions[index] with { Marked = holds[index] };
        }

        marked = top.Count > 0;
        return [.. top.Values.OrderByDescending(mark => mark.Share).Take(MostMarks)];
    }

    /// <summary>The first watched cast whose surroundings hold a point, or -1.</summary>
    private int Within(Vector3 point)
    {
        for (int index = 0; index < regions.Count; index++)
        {
            Region region = regions[index];
            Vector3 offset = point - region.Centre;
            if (Math.Abs(offset.Y) <= Math.Min(region.Radius, MostDepth)
                && (offset.X * offset.X) + (offset.Z * offset.Z) <= region.Radius * region.Radius)
            {
                return index;
            }
        }

        return -1;
    }

    private static Cell Chunk(Vector3 point) => new(
        (int)Math.Floor(point.X) & ~(Size - 1), (int)Math.Floor(point.Y) & ~(Size - 1), (int)Math.Floor(point.Z) & ~(Size - 1));

    private static Vector3 Centre(Cell cell) => new(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);

    /// <summary>A cast's surroundings: a column of ground around its centre, watched until a time and while anything there is marked.</summary>
    private readonly record struct Region(Vector3 Centre, float Radius, double Until, bool Marked);
}
