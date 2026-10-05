using System.Diagnostics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;
using Rusty.Engine.Testing;

string root = Path.Combine(Path.GetTempPath(), "craft-world-map-" + Guid.NewGuid().ToString("N"));
ulong expectedFingerprint = 0;
long expectedGeneration = 0;
ulong expectedTerrain = 0;
try
{
    using (EngineTestHost host = EngineTestHost.Create(new() { PersistenceRoot = root }))
    host.Call(engine =>
    {
        using ProductStore store = new(engine);
        // Before any world exists the product's UI stream still carries the generating map, and
        // diagnostics that need a world owner answer with the named pending state.
        using (ProductUiPublisher ui = new(engine))
        {
            ui.OpenStream();
            ui.PublishMap(new(true, "1", 4096, "", "Generating your first world", 0, false));
            Check.That(ui.Published == 1, "a world-less publisher sends the generating map projection");
        }
        object probe = new();
        Check.Throws<WorldPendingException>(() => WorldOwners.Require<object>(false, probe), "an owner is refused before its world is built");
        Check.Throws<WorldPendingException>(() => WorldOwners.Require<object>(true, null), "a missing owner is refused");
        Check.That(WorldOwners.Require<object>(true, probe) == probe, "a built owner is returned");

        Stopwatch construction = Stopwatch.StartNew();
        WorldCatalog catalog = new(engine, store);
        double constructionMs = construction.Elapsed.TotalMilliseconds;
        // A fresh store never generates during construction: startup must not wait on the simulation.
        Check.That(catalog.RestoreOutcome == "absent" && !catalog.HasWorld && catalog.Preparing,
            "a fresh store starts its first world in the background instead of generating it at startup");
        Check.Throws<InvalidOperationException>(() => _ = catalog.Current, "no world is readable before it is committed");
        WorldMapSave? first = null;
        for (int poll = 0; poll < 600 && first is null; poll++)
        {
            first = catalog.TakePrepared();
            if (first is null) Thread.Sleep(50);
        }
        Check.That(first is not null && first.Generation == 1 && first.Map.Configuration == TerrainConfiguration.Default,
            "the first world is the default configuration in the first generation namespace");
        Check.That(catalog.Commit(first!) && catalog.HasWorld, "an update commits the first world");
        Check.That(catalog.StoredBytes > 0, "map samples are written through Engine persistence");
        Console.WriteLine($"catalog construction on an empty store: {constructionMs:F1} ms");
        WorldMap initial = catalog.Current.Map;
        var draws = new EngineTerrainDraws(engine.Random);
        ulong live = TerrainGenerationFingerprint.Compute(initial.Configuration.CreateRecipe(draws, initial), TerrainGenerationFingerprint.Startup);
        Console.WriteLine($"Engine generator {initial.Configuration.GeneratorVersion}: {live:x16}; map {initial.Fingerprint:x16}; nodes={initial.Grid.Count}; bytes={catalog.StoredBytes}; generateMs={catalog.GenerationMilliseconds:F3}");

        SaveIdentity identity = new(initial.Configuration.GeneratorVersion, initial.Configuration.Seed);
        ProductSaveSlot<TerrainOverlaySnapshot> oldSlot = new(engine, store, SaveManifest.TerrainOverlay, new TerrainOverlayCodec(identity));
        oldSlot.Restore();
        TerrainOverlaySnapshot edited = new(identity.Seed, [new(new(0, 2, 0), TerrainConstants.StoneMaterial)]);
        Check.That(oldSlot.Save(edited), "old world saves edits");
        WorldMapSave next = catalog.Prepare(identity.Seed, initial.Configuration.Size);
        Check.That(catalog.Commit(next), "same-seed new world atomically selects fresh gameplay keys");
        Check.That(next.Map.Fingerprint == initial.Fingerprint, "same seed and configuration reproduce map geography");
        Check.That(oldSlot.Save(edited), "a retiring world can flush its original key after the selection changes");
        ProductSaveSlot<TerrainOverlaySnapshot> newSlot = new(engine, store, SaveManifest.TerrainOverlay, new TerrainOverlayCodec(identity));
        Check.That(newSlot.Restore().Outcome == SaveRestoreOutcome.Absent, "same-seed new world does not inherit retiring-world edits");

        // A world created from the map view is simulated off the update thread and admitted later.
        Check.That(!catalog.Preparing && catalog.TakePrepared() is null, "nothing is prepared before a request");
        catalog.BeginPrepare(777, 4096);
        Check.Throws<InvalidOperationException>(() => catalog.BeginPrepare(778, 4096), "only one world is prepared at a time");
        WorldMapSave? background = null;
        for (int poll = 0; poll < 600 && background is null; poll++)
        {
            background = catalog.TakePrepared();
            if (background is null) Thread.Sleep(50);
        }
        Check.That(background is not null && !catalog.Preparing, "a background preparation completes and is taken once");
        Check.That(background!.Map.Fingerprint == catalog.Prepare(777, 4096).Map.Fingerprint
            && background.Generation == catalog.Current.Generation + 1, "background and in-update preparation produce the same world");
        next = catalog.Prepare(12345, 4096);
        Check.That(catalog.Commit(next), "a new seed and extent are committed together with samples");
        expectedFingerprint = next.Map.Fingerprint;
        expectedTerrain = TerrainGenerationFingerprint.Compute(next.Map.Configuration.CreateRecipe(draws, next.Map), TerrainGenerationFingerprint.Startup);
        expectedGeneration = next.Generation;
    });
    using (EngineTestHost host = EngineTestHost.Create(new() { PersistenceRoot = root }))
    host.Call(engine =>
    {
        using ProductStore store = new(engine);
        WorldCatalog restored = new(engine, store);
        Check.That(restored.RestoreOutcome == "restored", "a fresh host reads the stored map");
        Check.That(restored.GenerationMilliseconds == 0, "restoring does not rerun map generation");
        Check.That(restored.Current.Map.Fingerprint == expectedFingerprint && restored.Current.Generation == expectedGeneration,
            "host restart preserves the same map and gameplay namespace");
        WorldMap map = restored.Current.Map;
        Check.That(TerrainGenerationFingerprint.Compute(map.Configuration.CreateRecipe(new EngineTerrainDraws(engine.Random), map),
            TerrainGenerationFingerprint.Startup) == expectedTerrain, "restored map and Engine draws reproduce local density and materials across hosts");
        Check.That(restored.Current.Map.Configuration.Seed == 12345 && restored.Current.Map.Configuration.Size == 4096,
            "host restart restores selected seed and extent rather than defaults");
    });
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
return Check.Finish("WorldMap");
