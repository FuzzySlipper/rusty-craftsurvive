using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Discovery;

/// <summary>
/// Discovery in the running product: it asks what the player is near, decides what that
/// counts as, records it, and saves what changed.
///
/// The journal has its own owner and its own persistence key rather than being folded into
/// the terrain world. That split is not ceremony - it is what keeps the save honest. The
/// world owns voxel overrides and knows nothing about places; the journal owns what has
/// been found and knows nothing about voxels. Each keeps its own identity header, so a
/// world whose generator version moved is discarded once, in one place, instead of
/// poisoning a shared blob that only half applies.
/// </summary>
internal sealed class DiscoveryModule : IProductModule
{
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly DiscoveryState journal;

    private readonly ProductSaveSlot<DiscoverySnapshot> slot;
    private readonly ProductUiPublisher ui;
    private readonly List<PoiSite> candidates = [];

    /// <summary>The last save or publish failure, reported by the readout rather than swallowed.</summary>
    private string? lastFailure;

    /// <summary>Places reached for the first time in this session.</summary>
    private long firstVisits;

    /// <summary>
    /// First reaches not yet taken by their consumer, oldest first. A first reach is an outcome for
    /// another owner (the supply cache it holds), so it is handed over as a read, never a call
    /// from here; a reload makes the place a return, so nothing is handed over twice.
    /// </summary>
    private readonly Queue<PoiSite> firstReached = new();

    /// <summary>The journal tick this session's step zero stands for.</summary>
    private long tickBase;
    private long nextNoticeStep;
    private bool noticedOnce;
    private double publishedNearest;
    private bool started;
    private bool disposed;

    internal DiscoveryModule(IEngineContext engine, TerrainWorld terrain, PlayerController player, ProductStore store, ProductUiPublisher ui)
    {
        ArgumentNullException.ThrowIfNull(engine);
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        journal = new DiscoveryState(terrain.SaveIdentity.Seed);
        slot = new ProductSaveSlot<DiscoverySnapshot>(
            engine, store, SaveManifest.DiscoveryJournal, new DiscoveryCodec(terrain.SaveIdentity));
    }

    /// <summary>Restores what was found before.</summary>
    public void Start()
    {
        if (started)
        {
            return;
        }

        Restore();
        started = true;
    }

    /// <summary>A fresh session over the saved journal: what is stored is read back.</summary>
    public void Restart()
    {
        journal.Restore(new DiscoverySnapshot(terrain.SaveIdentity.Seed, []));
        firstVisits = 0;
        firstReached.Clear();
        nextNoticeStep = 0;
        noticedOnce = false;
        Restore();
    }

    internal int Count => journal.Count;

    /// <summary>
    /// Whether the journal is saved, and how many bytes it holds, so the live lane can show that a
    /// fact the player earned reached the store.
    /// </summary>
    internal (bool Present, int Bytes) JournalSaved() => slot.Probe();

    /// <summary>
    /// Looks around and records anything newly seen or reached. It runs every few Engine steps
    /// rather than every one because a player at a walking pace crosses a fraction of a metre
    /// in a step, against a notice radius of 128.
    /// </summary>
    public void Update(ProductStep time)
    {
        // Places are noticed in the open world; inside a dungeon the journal waits.
        if (disposed || !started || time.Step < nextNoticeStep || player.InSeparateSpace)
        {
            return;
        }

        nextNoticeStep = time.Step + DiscoveryConstants.NoticeIntervalTicks;
        long tick = tickBase + time.Step;

        Vector3 position = player.WorldPosition;
        long columnX = (long)Math.Floor(position.X);
        long columnZ = (long)Math.Floor(position.Z);
        candidates.Clear();
        terrain.Recipe.Placement.CollectSitesNear(
            columnX, columnZ, DiscoveryRules.NoticeRadiusMetres, candidates);

        bool changed = false;
        double nearest = double.MaxValue;
        foreach (PoiSite site in candidates)
        {
            double distance = Distance(site, position);
            if (distance < nearest)
            {
                nearest = distance;
            }

            if (distance > DiscoveryRules.NoticeRadiusMetres)
            {
                continue;
            }

            // A structure's own height is what is looked for, so a ring of stones on a
            // ridge is visible over ground that would hide its base. Arriving counts
            // without a sightline, because standing at a place is not a matter of view.
            bool visible = distance <= DiscoveryRules.VisitRadiusMetres
                || DiscoveryRules.HasSightline(terrain.Recipe.Columns, columnX, columnZ,
                    position.Y + DiscoveryRules.EyeHeightMetres,
                    site.X, site.Z, site.Ground + site.Height);
            DiscoveryStage stage = DiscoveryRules.StageFor(distance, visible);
            bool wasVisited = journal.Find(site.CellX, site.CellZ)?.Stage == DiscoveryStage.Visited;
            bool rose = journal.Notice(site, stage, tick);
            if (rose && stage == DiscoveryStage.Visited && !wasVisited)
            {
                // First reach of this place, not a return to it: the difference between exploring
                // somewhere and farming somewhere already known.
                firstVisits++;
                firstReached.Enqueue(site);
            }

            changed |= rose;
        }

        // The journal refuses rather than throwing, but these two reach the Engine - a store
        // write and a projection publish - and a deterministic failure here would drop every
        // later update in this frame for the rest of the session. So the failure is caught,
        // remembered and reported rather than allowed to leave the module silent.
        try
        {
            if (changed)
            {
                Save();
            }

            // The first look publishes too, so the UI surface carries a journal from the start
            // rather than only after something is found, and so does a change in the nearest
            // place's whole-metre distance, which moves as the player walks. This runs from
            // Update, not the constructor, because the projection reads the live scene.
            double nearestShown = double.IsFinite(nearest) && nearest < double.MaxValue ? Math.Round(nearest) : 0d;
            if (changed || !noticedOnce || nearestShown != publishedNearest)
            {
                Publish(nearestShown);
                publishedNearest = nearestShown;
                noticedOnce = true;
            }
        }
        catch (EngineCallException failure)
        {
            lastFailure = failure.Message;
        }
    }

    /// <summary>
    /// Hands the journal's facts to the world, which owns the product's one UI stream. Kind and
    /// stage travel as their enum values - which is why neither may ever be renumbered - and the
    /// last place found is also named here, so the UI shows it without knowing either enum.
    /// </summary>
    private void Publish(double nearestMetres)
    {
        DiscoveryEntry? last = journal.Last;
        ui.PublishDiscovery(new DiscoveryUiFacts(
            journal.Count,
            journal.VisitedCount,
            journal.SeenCount,
            journal.Refused,
            double.IsFinite(nearestMetres) ? nearestMetres : 0d,
            last?.X ?? 0d,
            last?.Z ?? 0d,
            last is DiscoveryEntry kind ? (double)(ushort)kind.Kind : 0d,
            last is DiscoveryEntry stage ? (byte)stage.Stage : 0d,
            last?.LastTick ?? 0d,
            last is DiscoveryEntry found ? Describe(found) : string.Empty));
    }

    /// <summary>What a player calls a place they have found, and how well they know it.</summary>
    private static string Describe(DiscoveryEntry entry)
    {
        string place = entry.Kind switch
        {
            PoiKind.StandingStones => "Standing stones",
            PoiKind.Ruin => "Ruin",
            PoiKind.CaveMouth => "Cave mouth",
            PoiKind.DungeonEntrance => "Dungeon entrance",
            PoiKind.VantagePoint => "Vantage point",
            _ => "A place",
        };
        string known = entry.Stage == DiscoveryStage.Visited ? "visited" : "seen";
        return $"{place}, {known}";
    }

    /// <summary>Takes the oldest place reached for the first time and not yet taken, if any.</summary>
    internal bool TryTakeFirstVisit(out PoiSite site) => firstReached.TryDequeue(out site);

    internal string Readout()
    {
        (bool present, int bytes) = JournalSaved();
        return $"journal {journal.Readout()} firstVisits={firstVisits} stored={present}/{bytes} restore={slot.RestoreOutcome} failure={lastFailure ?? "none"}";
    }

    /// <summary>
    /// The places nearest the player, nearest first, with what is known about each. It is
    /// how a live session can be steered towards a landmark without guessing a heading, and
    /// it reads only - looking around is the update pass's job, not a query's.
    /// </summary>
    internal string Near(long radius)
    {
        long limit = Math.Clamp(radius, 0, (long)DiscoveryRules.NoticeRadiusMetres * 16);
        Vector3 position = player.WorldPosition;
        long columnX = (long)Math.Floor(position.X);
        long columnZ = (long)Math.Floor(position.Z);
        candidates.Clear();
        terrain.Recipe.Placement.CollectSitesNear(columnX, columnZ, limit, candidates);
        List<string> rows = [];
        foreach (PoiSite site in candidates.OrderBy(site => Distance(site, position)))
        {
            double distance = Distance(site, position);
            if (distance > limit)
            {
                continue;
            }

            DiscoveryEntry? known = journal.Find(site.CellX, site.CellZ);
            rows.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{site.KindName}@{site.X},{site.Z} d={distance:F1} ground={site.Ground} known={known?.StageName ?? "none"}"));
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"near radius={limit} count={rows.Count}: {(rows.Count == 0 ? "none" : string.Join("; ", rows))}");
    }

    /// <summary>
    /// The nearest places of one kind. `Near` answers "what is around me", which is the right
    /// question for a player and the wrong one for finding a target: its response carries every
    /// site in range, so a kind that is not near the top is lost to the response limit. Asking
    /// for one kind makes the answer small enough to always arrive whole.
    /// </summary>
    internal string Find(string kind, long radius)
    {
        if (!Enum.TryParse(kind, ignoreCase: true, out PoiKind wanted) || wanted == PoiKind.None)
        {
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"unknown kind '{kind}'; known kinds are {string.Join(", ", Enum.GetNames<PoiKind>().Where(name => name != nameof(PoiKind.None)))}");
        }

        long limit = Math.Clamp(radius, 0, (long)DiscoveryRules.NoticeRadiusMetres * 256);
        Vector3 position = player.WorldPosition;
        long columnX = (long)Math.Floor(position.X);
        long columnZ = (long)Math.Floor(position.Z);
        candidates.Clear();
        terrain.Recipe.Placement.CollectSitesNear(columnX, columnZ, limit, candidates);
        List<(double Distance, string Row)> rows = [];
        int known = 0;
        foreach (PoiSite site in candidates)
        {
            if (site.Kind != wanted)
            {
                continue;
            }

            double distance = Distance(site, position);
            if (distance > limit)
            {
                continue;
            }

            known++;
            DiscoveryEntry? entry = journal.Find(site.CellX, site.CellZ);
            rows.Add((distance, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{site.KindName}@{site.X},{site.Z} d={distance:F1} ground={site.Ground} known={entry?.StageName ?? "none"}")));

            // The answer is capped, so the work should be too: keep the nearest rows and trim as
            // we go rather than building a row for every site in the radius and discarding most.
            if (rows.Count > DiscoveryConstants.MaximumFindRows * 2)
            {
                rows.Sort((left, right) => left.Distance.CompareTo(right.Distance));
                rows.RemoveRange(DiscoveryConstants.MaximumFindRows, rows.Count - DiscoveryConstants.MaximumFindRows);
            }
        }

        rows.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        int shown = Math.Min(rows.Count, DiscoveryConstants.MaximumFindRows);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"find kind={wanted} radius={limit} count={known} showing={shown}: "
            + $"{(shown == 0 ? "none" : string.Join("; ", rows.Take(shown).Select(row => row.Row)))}");
    }

    /// <summary>
    /// The crossings nearest the player, with the span they cover and the height of their
    /// deck. It is how a live session can be aimed at one instead of hoping to stumble over
    /// it, and like the site query it only reads.
    /// </summary>
    internal string Crossings(long radius)
    {
        long limit = Math.Clamp(radius, 0, (long)DiscoveryRules.NoticeRadiusMetres * 64);
        Vector3 position = player.WorldPosition;
        long cell = PoiConstants.CellSize;
        long columnX = (long)Math.Floor(position.X);
        long columnZ = (long)Math.Floor(position.Z);
        long firstX = GridMath.FloorDivide(columnX - limit, cell);
        long lastX = GridMath.FloorDivide(columnX + limit, cell);
        long firstZ = GridMath.FloorDivide(columnZ - limit, cell);
        long lastZ = GridMath.FloorDivide(columnZ + limit, cell);
        List<(double Distance, string Row)> rows = [];
        for (long cellX = firstX; cellX <= lastX; cellX++)
        {
            for (long cellZ = firstZ; cellZ <= lastZ; cellZ++)
            {
                if (terrain.Recipe.Crossings.SiteAt(cellX, cellZ) is not CrossingSite site)
                {
                    continue;
                }

                double dx = ((site.FromX + site.ToX) / 2.0) - position.X;
                double dz = ((site.FromZ + site.ToZ) / 2.0) - position.Z;
                double distance = Math.Sqrt((dx * dx) + (dz * dz));
                if (distance > limit)
                {
                    continue;
                }

                long span = site.AlongX ? site.ToX - site.FromX : site.ToZ - site.FromZ;
                rows.Add((distance, string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{site.Id} span={span + 1} deck={site.DeckY} from={site.FromX},{site.FromZ} to={site.ToX},{site.ToZ} d={distance:F1}")));
            }
        }

        rows.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"crossings radius={limit} count={rows.Count}: {(rows.Count == 0 ? "none" : string.Join("; ", rows.Select(row => row.Row)))}");
    }

    private static double Distance(PoiSite site, Vector3 position)
    {
        double dx = site.X - position.X;
        double dz = site.Z - position.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    private void Save()
    {
        if (!slot.Save(journal.Snapshot()))
        {
            lastFailure = slot.LastFailure;
        }
    }

    /// <summary>
    /// Reads the stored journal. One that does not match this world is discarded and kept as the
    /// key's backup; the journal then starts empty. Ticks resume after the latest one restored.
    /// </summary>
    private void Restore()
    {
        if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: DiscoverySnapshot saved })
        {
            journal.Restore(saved);
        }

        lastFailure = slot.LastFailure;
        tickBase = journal.ResumeTick;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
    }
}
