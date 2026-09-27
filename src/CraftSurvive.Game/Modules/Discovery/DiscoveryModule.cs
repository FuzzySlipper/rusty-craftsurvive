using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;
using Rusty.Engine.Debugging;

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
public sealed class DiscoveryModule : IDisposable, IDebugCommandModule
{
    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly DiscoveryState journal;
    private readonly List<PoiSite> candidates = [];
    private readonly PersistenceStore store;
    private string restoreOutcome = "not attempted";
    private long tick;
    private bool disposed;

    internal DiscoveryModule(IEngineContext engine, TerrainWorld terrain, PlayerController player)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        journal = new DiscoveryState(terrain.Recipe.Contract.Seed);
        store = engine.Persistence.OpenStore(new PersistenceOpenRequest(TerrainConstants.PersistenceScope));
        Restore();
    }

    internal int Count => journal.Count;

    /// <summary>What happened to the saved journal at startup, for evidence.</summary>
    internal string RestoreOutcome => restoreOutcome;

    /// <summary>
    /// Whether the journal is saved, and how many bytes it holds. It exists for the same
    /// reason the overlay's does: it is the half of the save path an Engine-side write
    /// never touches, so the live lane can show that a fact the player earned reached
    /// the store.
    /// </summary>
    internal (bool Present, int Bytes) JournalSaved()
    {
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(
            store, DiscoveryConstants.PersistenceKey));
        PersistenceBlobInfo info = engine.Persistence.DescribeBlob(blob);
        return info.Present ? (true, engine.Persistence.ReadBlobBytes(blob).Length) : (false, 0);
    }

    /// <summary>
    /// Looks around and records anything newly seen or reached. It runs on an interval
    /// rather than every tick because a player at a walking pace crosses a fraction of a
    /// metre in one, against a notice radius of 128.
    /// </summary>
    internal void Update()
    {
        if (disposed)
        {
            return;
        }

        tick++;
        if (tick % DiscoveryConstants.NoticeIntervalTicks != 0)
        {
            return;
        }

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
            changed |= journal.Notice(site, DiscoveryRules.StageFor(distance, visible), tick);
        }

        if (changed)
        {
            Save();
        }

        // The first look publishes too, so the UI surface carries a journal from the start
        // rather than only after something is found. This runs from Update, not the
        // constructor, because the projection reads the live scene.
        if (changed || tick == DiscoveryConstants.NoticeIntervalTicks)
        {
            Publish(nearest);
        }
    }

    /// <summary>
    /// Hands the journal's numbers to the world, which owns the product's one UI stream.
    /// Nothing here is a name: the projection is a flat numeric map, so kind and stage travel
    /// as their enum values - which is why neither may ever be renumbered.
    /// </summary>
    private void Publish(double nearestMetres)
    {
        DiscoveryEntry? last = journal.Last;
        terrain.PublishDiscoveryUi(new DiscoveryUiFacts(
            journal.Count,
            journal.VisitedCount,
            journal.SeenCount,
            journal.Refused,
            double.IsFinite(nearestMetres) ? nearestMetres : 0d,
            last?.X ?? 0d,
            last?.Z ?? 0d,
            last is DiscoveryEntry kind ? (double)(ushort)kind.Kind : 0d,
            last is DiscoveryEntry stage ? (byte)stage.Stage : 0d,
            last?.LastTick ?? 0d));
    }

    [DebugCommand("craft.discovery.readout", Description = "Reads the journal: what has been seen or reached, and whether it is stored.")]
    public string Readout()
    {
        (bool present, int bytes) = JournalSaved();
        return $"journal {journal.Readout()} stored={present}/{bytes} restore={restoreOutcome}";
    }

    /// <summary>
    /// The places nearest the player, nearest first, with what is known about each. It is
    /// how a live session can be steered towards a landmark without guessing a heading, and
    /// it reads only - looking around is the update pass's job, not a query's.
    /// </summary>
    [DebugCommand("craft.discovery.near", Description = "Lists the places nearest the player, with kind, distance and what is known about each.")]
    public string Near(long radius)
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

    private static double Distance(PoiSite site, Vector3 position)
    {
        double dx = site.X - position.X;
        double dz = site.Z - position.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    private void Save()
    {
        byte[] bytes = DiscoveryCodec.Encode(journal.Snapshot());
        engine.Persistence.Save(new PersistenceSaveRequest(
            store, DiscoveryConstants.PersistenceKey, PersistenceRevisionGuard.Any, 0, bytes));
    }

    private void Restore()
    {
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(
            store, DiscoveryConstants.PersistenceKey));
        PersistenceBlobInfo info = engine.Persistence.DescribeBlob(blob);
        if (!info.Present)
        {
            restoreOutcome = "absent";
            return;
        }

        byte[] bytes = engine.Persistence.ReadBlobBytes(blob).ToArray();
        try
        {
            journal.Restore(DiscoveryCodec.Decode(terrain.Recipe.Contract.Seed, bytes));
            restoreOutcome = "restored";
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // Worlds are disposable under the settled save policy, so a journal that does
            // not match this world is set aside rather than reaching the load call and
            // failing the product. The previous generation is kept as one backup.
            PreserveBackup(bytes);
            restoreOutcome = $"discarded: {exception.Message}";
        }
    }

    private void PreserveBackup(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        engine.Persistence.Save(new PersistenceSaveRequest(
            store, DiscoveryConstants.BackupPersistenceKey, PersistenceRevisionGuard.Any, 0, bytes));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        store.Dispose();
    }
}
