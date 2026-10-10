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
            ui.PublishMap(new(true, "1", 4096, "", "Generating your first world", 0, false, "", "idle", "", "", 1));
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

    // A continent (#9549): generated, saved and restored through the same catalog, with its own golden.
    ulong continentFingerprint = 0;
    using (EngineTestHost host = EngineTestHost.Create(new() { PersistenceRoot = root }))
    host.Call(engine =>
    {
        using ProductStore store = new(engine);
        WorldCatalog catalog = new(engine, store);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        WorldMapSave continent = catalog.Prepare(ContinentSeed, MapScale.DefaultContinentalSize);
        double generateMs = clock.Elapsed.TotalMilliseconds;
        Check.That(catalog.Commit(continent), "a continent commits like any world");
        continentFingerprint = continent.Map.Fingerprint;
        Console.WriteLine($"continent {continent.Map.Configuration.Size / 1000} km: map {continentFingerprint:x16}; nodes={continent.Map.Grid.Count}; spacing={continent.Map.Spacing:F0} m; bytes={catalog.StoredBytes}; generateMs={generateMs:F0}");
        Check.That(continentFingerprint == ContinentGolden, $"the default continent's geography matches its golden ({continentFingerprint:x16})");
        Check.That(continent.Map.Scale.Continental && continent.Map.Spacing == 1000, "a continent is simulated on a kilometre lattice");
        DesignTopologyReport topology = DesignTopology.Check(continent.Map.Grid, i => continent.Map.Fields.Elevation[i] >= GenerationConstants.WaterLevel, ContinentDesign.For(continent.Map.Configuration.Size)!);
        Check.That(topology.Holds(ContinentDesign.For(continent.Map.Configuration.Size)!), $"the published continent keeps the frontier's rules: {topology}");
        RegionChecks(continent.Map);
        Check.Section("weather on the continent", () => WeatherChecks.Continent(continent.Map));
        ContinentalTerrainChecks(continent.Map.Configuration.CreateRecipe(new EngineTerrainDraws(engine.Random), continent.Map));
    });
    using (EngineTestHost host = EngineTestHost.Create(new() { PersistenceRoot = root }))
    host.Call(engine =>
    {
        using ProductStore store = new(engine);
        WorldCatalog restored = new(engine, store);
        // A restored continent is admitted once its arrival tile is rebuilt off-thread (#9551).
        Check.That(!restored.HasWorld && restored.Preparing, "a restored continent waits for its arrival tile without blocking load");
        Stopwatch waited = Stopwatch.StartNew();
        WorldMapSave? admitted = null;
        while (admitted is null && waited.ElapsedMilliseconds < PrefetchWaitMs) { admitted = restored.TakePrepared(); Thread.Sleep(20); }
        Check.That(admitted is not null && restored.Commit(admitted) && restored.HasWorld && MapRegions.For(restored.Current.Map).IsReady((0, 0)),
            $"the restored continent is admitted with its arrival tile built ({waited.ElapsedMilliseconds} ms)");
        Check.That(restored.RestoreOutcome == "restored" && restored.GenerationMilliseconds == 0
            && restored.Current.Map.Fingerprint == continentFingerprint && restored.Current.Map.Configuration.Size == MapScale.DefaultContinentalSize,
            "a fresh host restores the continent from its save without generating it again");
    });
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
Check.Section("generation recipe", RecipeChecks.DefaultsAreTheGeneratorsOwn);
Check.Section("continent design", RecipeChecks.DesignsDrawTheContinent);
Check.Section("continent design topology", RecipeChecks.DesignTopologyIsEnforced);
Check.Section("map palette", PaletteChecks.Run);
Check.Section("erosion filter", ErosionFilterChecks.Run);
Check.Section("erosion filter shelters the passes", ErosionFilterChecks.PassesAreSheltered);
Check.Section("weather", WeatherChecks.Synthetic);
Check.Section("weather over the player", WeatherChecks.HereFollowsTheClock);
Check.Section("weather on a route", WeatherChecks.RouteForecastKeepsTheMarchPace);
Check.Section("weather arrivals", WeatherChecks.EachFrontArrivesOnce);
Check.Section("horizon sink", HorizonChecks.SinkMovesOnlyUnderTheFarField);
Check.Section("horizon landmarks", HorizonChecks.LandmarksStandInTheirBand);
Check.Section("horizon landmarks on visible ground", HorizonChecks.LandmarksStandOnVisibleGround);
Check.Section("horizon middle tier follows the region window", HorizonChecks.MiddleTierFollowsTheRegionWindow);
return Check.Finish("WorldMap");

partial class Program
{
    /// <summary>Region tile (0, 0) of the <see cref="ContinentSeed"/> continent under generator 23 (#9550); a deliberate generation change updates it.</summary>
    private const ulong RegionTileGolden = 0xb9d08462be8629f4UL;
    /// <summary>
    /// A tile builds in about 1.5 s in Release on an idle machine; this wall-clock bound leaves room for a
    /// heavily shared runner (5.6 s was seen at a load average of 40) while still catching a regression.
    /// </summary>
    private const double RegionTileBudgetMs = 8000;
    /// <summary>The steepest metre-to-metre rise across a seam may not exceed the steepest within a tile by more than this.</summary>
    private const double SeamStepAllowance = 1.25;
    private const double SeamProbeMetres = 3000;
    private const int PrefetchWaitMs = 60_000;

    /// <summary>Region tiles (#9550): deterministic, seamless in height and rivers, quick, and built ahead of a party.</summary>
    private static void RegionChecks(WorldMap continent)
    {
        MapRegions regions = MapRegions.For(continent);
        Stopwatch clock = Stopwatch.StartNew();
        MapDrainage drainage = regions.Drainage;
        double drainageMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        RegionTile origin = regions.Tile((0, 0));
        double tileMs = clock.Elapsed.TotalMilliseconds;
        RegionTile east = regions.Tile((1, 0));
        Console.WriteLine($"regions: drainage reaches={drainage.ReachCount} buildMs={drainageMs:F0}; tile (0,0) {origin.Map.Fingerprint:x16} buildMs={tileMs:F0} rivers={origin.Rivers.Reaches.Count}");
        Check.That(drainage.ReachCount > 0 && origin.Rivers.Reaches.Count > 0, "the continent's drainage network reaches the arrival tile");
        Check.That(tileMs < RegionTileBudgetMs, $"a region tile builds within its budget ({tileMs:F0} ms)");
        Check.That(new MapRegions(continent).Tile((0, 0)).Map.Fingerprint == origin.Map.Fingerprint, "a region tile rebuilds identically");
        MapRegions.PrepareArrival(continent);
        Check.That(MapRegions.For(continent) == regions && regions.IsReady((0, 0)), "world preparation leaves the arrival tile built in the continent's one region cache");
        Check.That(origin.Map.Fingerprint == RegionTileGolden, $"the arrival tile matches its golden ({origin.Map.Fingerprint:x16})");

        double seamX = MapRegions.TileSpacing / 2, seam = 0, inside = 0;
        int agreed = 0, disagreed = 0;
        for (double z = -SeamProbeMetres; z < SeamProbeMetres; z += 1)
        {
            seam = Math.Max(seam, Math.Abs(regions.Sample(seamX + 1, z).Elevation - regions.Sample(seamX, z).Elevation));
            inside = Math.Max(inside, Math.Abs(regions.Sample(1, z).Elevation - regions.Sample(0, z).Elevation));
            RiverInfluence? a = origin.Rivers.Nearest(seamX, z), b = east.Rivers.Nearest(seamX, z);
            if (a is null && b is null) continue;
            if (a == b) agreed++; else disagreed++;
        }
        for (double x = seamX - MapRegions.BlendHalfWidth; x <= seamX + MapRegions.BlendHalfWidth; x += 1)
            seam = Math.Max(seam, Math.Abs(regions.Sample(x + 1, 0).Elevation - regions.Sample(x, 0).Elevation));
        Console.WriteLine($"regions: seam max step {seam:F2} m/m, inside {inside:F2} m/m; seam river samples agreed={agreed} disagreed={disagreed}");
        Check.That(seam <= Math.Max(inside, 1) * SeamStepAllowance, "neighbouring tiles meet without a step");
        Check.That(disagreed == 0, "both tiles draw the same rivers where they meet");

        // A party's route is refined ahead of it, off the calling thread.
        // Within the lookahead (MapRegions.RouteAheadMetres), so the whole route is refined ahead.
        System.Numerics.Vector2[] route = [new(0, 0), new(6_000, 0), new(10_000, 4_000)];
        regions.PrefetchAhead(route[0], route);
        clock.Restart();
        while (regions.Pending > 0 && clock.ElapsedMilliseconds < PrefetchWaitMs) Thread.Sleep(50);
        Check.That(regions.Pending == 0 && regions.IsReady(MapRegions.TileAt(10_000, 4_000)), $"tiles along a route are built ahead of the party ({clock.ElapsedMilliseconds} ms)");
    }

    /// <summary>Points 5 km inside each corner of the 390 km continent, all land for <see cref="ContinentSeed"/>: where #9551 audits precision.</summary>
    private const double FarCorner = 190_000;
    private const int SeamWalkMetres = 2000;

    /// <summary>
    /// Walking terrain on a continent (#9551): the recipe samples the region tiles, the far corners
    /// generate the same well-formed ground as the origin, and the recipe's own surface meets at a seam.
    /// </summary>
    private static void ContinentalTerrainChecks(TerrainRecipe recipe)
    {
        Check.That(recipe.Regions == MapRegions.For(recipe.Map), "a continent's walking terrain samples its region tiles");
        (double X, double Z)[] points = [(0, 0), (FarCorner, FarCorner), (FarCorner, -FarCorner), (-FarCorner, FarCorner), (-FarCorner, -FarCorner)];
        foreach ((double x, double z) in points)
        {
            Stopwatch clock = Stopwatch.StartNew();
            MapSample geography = recipe.Geography(x, z);
            double ms = clock.Elapsed.TotalMilliseconds;
            long surface = recipe.SurfaceAt((long)x, (long)z);
            Console.WriteLine($"continent terrain at ({x / 1000:F0} km, {z / 1000:F0} km): surface={surface} {WorldMap.Region(geography)} firstSampleMs={ms:F0}");
            Check.That(geography == recipe.Regions!.Sample(x, z) && surface >= recipe.MinimumMaterialY && surface <= recipe.MaximumMaterialY
                && Math.Abs(surface - geography.Elevation) <= WorldMap.LocalReliefLimit + 1,
                $"terrain at ({x:F0}, {z:F0}) is well-formed ground over its region tile");
        }
        long seamX = (long)(MapRegions.TileSpacing / 2), seam = 0, inside = 0;
        for (long z = -SeamWalkMetres; z < SeamWalkMetres; z++)
        {
            seam = Math.Max(seam, Math.Abs(recipe.SurfaceAt(seamX + 1, z) - recipe.SurfaceAt(seamX, z)));
            inside = Math.Max(inside, Math.Abs(recipe.SurfaceAt(1, z) - recipe.SurfaceAt(0, z)));
        }
        Console.WriteLine($"continent terrain seam: max voxel step {seam} across, {inside} inside");
        Check.That(seam <= Math.Max(inside, 1) + 1, "walking terrain meets across a tile seam like anywhere else");
    }

    private const ulong ContinentSeed = 12345;
    /// <summary>Map fingerprint of the 390 km continent for <see cref="ContinentSeed"/> under generator 23 (#9549); a deliberate generation change updates it.</summary>
    private const ulong ContinentGolden = 0x590be63938a0ae89UL;
}
