using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The voxel world: the generator and its fingerprint, the spatial session the Engine keeps the
/// scene in, and the parts that keep that scene true - residency streaming, the player's edits and
/// their save, the edit path, and the presentation. Engine remains the collision, mesh, renderer,
/// handle and revision authority.
/// </summary>
internal sealed class TerrainWorld : IDisposable
{
    /// <summary>How far above the generated surface a structure or a placed block may stand.</summary>
    private const int GroundSearchAbove = 16;

    /// <summary>How far below the generated surface an edit may have dug.</summary>
    private const int GroundSearchBelow = 16;

    private static readonly TerrainChunkAddress FixedResidencyCenter = new(0, 0, 0);

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private Manipulation.BuildStudy? study;
    private WaterStudy? waterStudy;
    private readonly ProductUiPublisher ui;
    private readonly WorldFrame frame;
    private readonly TerrainTrees trees;
    private readonly TerrainResidencyPolicy policy;
    private readonly TerrainRecipe recipe;
    private readonly TerrainChunkCache chunkCache;
    private readonly TerrainOverlayStore overlayStore;
    private readonly TerrainResidencyStreamer streamer;
    private readonly TerrainPresentation presentation;
    private readonly TerrainEditService edits;
    private SpatialSession? session;

    /// <summary>The horizon beyond the drawn chunks (#9548), drawn from the same height field.</summary>
    private FarField? farField;
    private long currentStep;
    private bool started;

    internal TerrainWorld(IEngineContext engine, ProductContent content, TerrainConfiguration configuration,
        WorldFrame frame, ProductStore store, ProductUiPublisher ui, WorldMap? map = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentNullException.ThrowIfNull(content);
        this.content = content;
        ArgumentNullException.ThrowIfNull(frame);
        this.frame = frame;
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        frame.Rebased += OnRebased;
        map ??= WorldMapGenerator.Generate(configuration);
        recipe = configuration.CreateRecipe(new EngineTerrainDraws(engine.Random), map);

        // The fingerprint comes from a fresh recipe, so nothing the live one memoises can hide a
        // change in the generator or in the Engine's keyed draws underneath it.
        GenerationFingerprint = TerrainGenerationFingerprint.Compute(
            configuration.CreateRecipe(new EngineTerrainDraws(engine.Random), map),
            TerrainGenerationFingerprint.Startup);
        CacheIdentity = TerrainGenerationFingerprint.CacheIdentity(GenerationFingerprint, TerrainGeneratorSource.Stamp);
        chunkCache = new TerrainChunkCache(engine, recipe.Contract, CacheIdentity);
        TerrainChunkGenerator generator = new(recipe, chunkCache);
        policy = new TerrainResidencyPolicy(recipe, generator);
        SaveIdentity = new SaveIdentity(recipe.Contract.Version, recipe.Contract.Seed);
        overlayStore = new TerrainOverlayStore(engine, store, SaveIdentity);
        streamer = new TerrainResidencyStreamer(engine, policy, generator, chunkCache, overlayStore.Overlay, configuration.Seed);
        Moss = new SurfaceMoss(engine);
        presentation = new TerrainPresentation(engine, content, Moss);
        trees = new TerrainTrees(engine, content, frame);
        places = new Places.PlaceMeshes(engine, content, frame, recipe.Placement,
            (x, z) => recipe.ContinuousHeightAt((long)Math.Floor(x), (long)Math.Floor(z)) + PlaceGroundTopFace, Moss);
        Water = new Water.WaterModel(recipe);
        waterSurfaces = new Water.WaterSurfaces(engine, frame, Water);
        edits = new TerrainEditService(engine, overlayStore, policy, streamer, presentation, ui.Publish,
            committed => Edited?.Invoke(committed), requested => TreeFelling.Dependents(requested, recipe, MaterialAt));
    }

    /// <summary>Accepted final cell states, after the Engine and overlay agree on the edit.</summary>
    internal event Action<IReadOnlyList<TerrainVoxelEdit>>? Edited;

    /// <summary>The generation recipe, for the modules that reason about the generated world.</summary>
    internal TerrainRecipe Recipe => recipe;

    /// <summary>Where water stands in the walking world (#9877): the one owner every water question asks.</summary>
    internal Water.WaterModel Water { get; }

    private readonly Water.WaterSurfaces waterSurfaces;

    /// <summary>The surface moss (#10044) the places and field stones grow, one style for all.</summary>
    internal SurfaceMoss Moss { get; }

    /// <summary>The overworld places as kit meshes (#9992), stood on the ground's top face.</summary>
    private readonly Places.PlaceMeshes places;
    private const double PlaceGroundTopFace = 1.0;

    /// <summary>The places' pieces standing about the player, for the appearance snapshot.</summary>
    internal IEnumerable<AppearanceFact> PlaceFacts => [.. places.Facts, .. places.BlanketFacts, .. places.CardFacts];

    /// <summary>Sets a site's overgrowth (#10044): its moss deepens, its walls take the ivy blanket.</summary>
    internal string SetOvergrowth(string siteId, float extra) => places.SetOvergrowth(siteId, extra);

    internal string PlacesReadout() => places.Readout() + string.Create(CultureInfo.InvariantCulture, $" colliders={PlaceColliders}");

    /// <summary>How many place pieces the walking collision holds, for the readout.</summary>
    internal int PlaceColliders { get; private set; }

    /// <summary>The near water surfaces' draw facts (#9877).</summary>
    internal AppearanceFact[] WaterFacts => waterSurfaces.Facts;

    internal string WaterReadout() => string.Create(CultureInfo.InvariantCulture, $"{waterSurfaces.Readout()} icePatches={IcePatches}");

    internal string WaterVisual(string mode) => waterSurfaces.SetVisual(mode);

    /// <summary>The waves over the near water (#9902).</summary>
    internal Water.SeaState Sea => waterSurfaces.Sea;

    /// <summary>
    /// Moves the waves on (#9902) by the simulation seconds this update, toward the weather's wind (its
    /// channel and flow) by the game hours, and hands the drawn water the result - before anything rides it.
    /// </summary>
    internal void AdvanceSea(double seconds, double gameHours, double windChannel, Vector2 flow, Vector3 playerWorld)
    {
        waterSurfaces.Sea.Advance(seconds, gameHours, windChannel, flow, playerWorld.X, playerWorld.Z);
        waterSurfaces.FollowSea();
    }

    /// <summary>How far the drawn waves stand over the water's rest level at a point now (#9902), given its sample.</summary>
    internal double WaveLift(double x, double z, Water.WaterSample sample) => waterSurfaces.Lift(x, z, sample);
    internal string StartWaterFlow(double x, double z) => waterSurfaces.StartFlow(Session, x, z);
    internal string StopWaterFlow() => waterSurfaces.StopFlow();
    internal string WaterFlowAt(double x, double z) => waterSurfaces.FlowAt(x, z);

    /// <summary>Moves on whenever the water's stage does, so what keeps water samples knows to take them again.</summary>
    internal long WaterRevision { get; private set; }

    /// <summary>
    /// The water's stage moved on (#9878): the near water is sampled again as updates allow, and the far
    /// field where water froze, thawed, ran dry or came back.
    /// </summary>
    internal void RefreshWater(IReadOnlyList<Water.WaterBounds> changed)
    {
        WaterRevision++;
        waterSurfaces.Refresh();
        farField?.InvalidateWater(changed);
    }

    /// <summary>After an appearance snapshot is published: retired water tiles may now be disposed.</summary>
    internal void AfterAppearanceSnapshot()
    {
        waterSurfaces.AfterAppearanceSnapshot();
        places.AfterAppearanceSnapshot();
    }

    internal LandscapeSampleSpace CreateLandscapeStudy(LandscapeStudy study) =>
        new(engine, content, study);

    /// <summary>The world every save belongs to; a save written for another is discarded.</summary>
    internal SaveIdentity SaveIdentity { get; }

    /// <summary>What this run's generator produces over the startup probe, with the Engine's draws.</summary>
    internal ulong GenerationFingerprint { get; }

    /// <summary>What cached chunks are keyed on: the output fingerprint and the generator's source stamp.</summary>
    internal ulong CacheIdentity { get; }

    internal SpatialSession Session => session ?? throw new InvalidOperationException("Terrain spatial session is unavailable.");

    /// <summary>Darkens the overworld's surface vertices by the solid voxels around them (#9506 exploration).</summary>
    internal VoxelSceneReadout ConfigureVertexOcclusion(float strength)
    {
        VoxelSceneReadout scene = engine.Voxel.ConfigureVertexOcclusion(new VoxelVertexOcclusionRequest(Session, strength));
        if (presentation.Projected)
        {
            presentation.Refresh();
        }

        return scene;
    }

    /// <summary>Draws a separate space's session with this world's block materials; the caller disposes the projection.</summary>
    internal VoxelScenePresentation ProjectSeparateSpace(SpatialSession separate) => presentation.ProjectAnother(separate);

    /// <summary>How much of the open world's ground is drawn coarse (#9563).</summary>
    internal string LevelOfDetailReadout() => presentation.LevelOfDetailReadout();

    internal string ScatterReadout() => presentation.ScatterReadout();

    /// <summary>Switches grass between blades (the default) and the painted cards, everywhere it grows, to compare (#10038).</summary>
    internal string GrassBlades(bool blades)
    {
        EnsureStarted();
        presentation.GrassBlades = blades;
        vegetationTiles?.Grow(presentation.ScatterKinds, (float)groundCoverMetres);
        plantTiles?.ShadeGrass(presentation.ShadeGrass);
        return GrassLook;
    }

    internal string GrassLook => presentation.GrassBlades ? "grass=blades" : "grass=cards";

    /// <summary>
    /// Sets the surface moss's strength (#10044) for the places and the field stones: the places' materials follow at
    /// once, and the scatter and its tiles are grown again, since their copies keep the shader values they were set with.
    /// </summary>
    internal string SetMossStrength(float strength)
    {
        string readout = Moss.SetStrength(strength);
        places.RelayCards();
        presentation.RegrowScatter();
        vegetationTiles?.Grow(presentation.ScatterKinds, (float)groundCoverMetres);
        return readout;
    }

    internal string TuneScatter(float grassPerSquareMetre)
    {
        string readout = presentation.TuneScatter(grassPerSquareMetre);
        vegetationTiles?.Grow(presentation.ScatterKinds, (float)groundCoverMetres);
        return readout;
    }

    /// <summary>The atlas image every block material is built from.</summary>
    internal RenderResourceReference AtlasSprite => presentation.AtlasSprite;

    /// <summary>The last accepted edit's cost by part; diagnostics, not game state.</summary>
    internal string LastEditTiming => edits.LastTiming;

    /// <summary>Switches recording of where each edit's time goes; diagnostics only, off by default.</summary>
    internal bool EditTimingEnabled
    {
        get => edits.TimingEnabled;
        set => edits.TimingEnabled = value;
    }

    internal void Start()
    {
        if (started)
        {
            return;
        }

        try
        {
            session = engine.Spatial.CreateSession(new SpatialSessionConfig(
                TerrainConstants.VoxelSize,
                TerrainConstants.VoxelChunkSize,
                VoxelSurfaceMode.DualContouring));
            TerrainSurfaces.Apply(engine, session);
            VoxelMaterialRules.Apply(engine, session);
            presentation.ConfigureGround(session);
            ui.Open(ProductUiPublisher.StreamName, ProductUiPublisher.StreamContract, WorldFacts);
            overlayStore.Restore();
            streamer.Synchronize(session, FixedResidencyCenter);
            presentation.Project(session);
            farField = new FarField(engine, content, recipe, () => Session, Water) { RadiusChunks = viewDistanceChunks, NearEdgeMetres = NearEdgeMetres };
            vegetationTiles = new VegetationTiles(engine, recipe, session, () => overlayStore.Overlay, lastCentre);
            vegetationTiles.Grow(presentation.ScatterKinds, (float)groundCoverMetres);
            treeTiles = new TreeTiles(engine, recipe, session, trees, MaterialAt, IsEdited, lastCentre);
            treeTiles.Grow(treeReachMetres);
            plantTiles = new PlantTiles(engine, content, recipe, session, trees.NearMeshes, presentation.ShadeGrass, () => overlayStore.Overlay, lastCentre);
            ui.Publish();
            started = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Saves the player's edits once they pause. Called once per product update.</summary>
    internal void Update(ProductStep step)
    {
        currentStep = step.Step;
        waterSurfaces.UpdateFlow(step.Step);
        overlayStore.SaveIfDue(step.Step);
    }

    internal void Restart()
    {
        EnsureStarted();
        if (streamer.Synchronize(Session, FixedResidencyCenter))
        {
            presentation.Refresh();
            ui.Publish();
        }
    }

    public void Dispose()
    {
        if (started)
        {
            overlayStore.Flush();
        }

        streamer.Clear();
        vegetationTiles?.Dispose();
        vegetationTiles = null;
        treeTiles?.Dispose();
        treeTiles = null;
        plantTiles?.Dispose();
        plantTiles = null;
        farField?.Dispose();
        farField = null;
        trees.Dispose();
        places.Dispose();
        waterSurfaces.Dispose();
        study?.Dispose();
        waterStudy?.Dispose();
        presentation.Dispose();
        Moss.Dispose();
        chunkCache.Dispose();
        session?.Dispose();
        session = null;
        started = false;
    }

    /// <summary>Advances the bounded voxel residency plan around a product global voxel fact.</summary>
    internal void SynchronizeAround(VoxelAddress centerVoxel)
    {
        EnsureStarted();
        // On a continent the region tiles around the walker are refined before the residency reaches them (#9551).
        recipe.Regions?.Prefetch(centerVoxel.X, centerVoxel.Z, MapRegions.PrefetchRadius);
        if (streamer.Synchronize(Session, centerVoxel.Chunk))
        {
            presentation.Refresh();
            ui.Publish();
        }

        trees.Follow(centerVoxel, recipe, MaterialAt, IsResident, EditRevision, streamer.ResidentCount);
        places.Follow(new Vector3(centerVoxel.X + 0.5f, centerVoxel.Y, centerVoxel.Z + 0.5f), NearEdgeMetres);
        Moss.Follow(frame.Origin);
        lastCentre = centerVoxel;
        // Ground cover and trees re-ask the tiles under the columns edited since the last follow.
        var changedColumns = overlayStore.Overlay.TakeChangedColumns();
        vegetationTiles?.Follow(centerVoxel, changedColumns);
        treeTiles?.Follow(centerVoxel, changedColumns);
        plantTiles?.Follow(centerVoxel, changedColumns);
        if (farField is FarField far)
        {
            far.Follow(centerVoxel.X, centerVoxel.Z);
            far.Advance();
            // The near water squares to the far field's sunk zone, which leaves its water out there.
            waterSurfaces.Follow(far.SinkCentre, far.SinkOuter);
        }
        else
        {
            waterSurfaces.Follow((centerVoxel.X + 0.5, centerVoxel.Z + 0.5), NearEdgeMetres);
        }

        // The ice is part of the walking collision (#9878): compose again when it changes, or when the origin moved under it.
        if (waterSurfaces.IceRevision != composedIce || (composedIceAny && composedOrigin != frame.Origin)) ComposeStaticCollision();
        // So are fallen logs and stumps (#10034): compose again when deadwood near the player changed, or they moved on.
        // So are the places' pieces (#9992): compose again when they were laid out again or the player walked on.
        else if (places.CollisionStale(new Vector3(centerVoxel.X + 0.5f, 0, centerVoxel.Z + 0.5f))) ComposeStaticCollision();
        else if (plantTiles is PlantTiles flora && (flora.DeadwoodRevision != composedDeadwood
            || Math.Max(Math.Abs(centerVoxel.X - composedDeadwoodAt.X), Math.Abs(centerVoxel.Z - composedDeadwoodAt.Z)) >= DeadwoodStepMetres
            || (DeadwoodColliders > 0 && composedOrigin != frame.Origin)))
            ComposeStaticCollision();
    }

    internal string TreesReadout() => trees.Readout();

    /// <summary>The kept logs and stumps nearest a point (#10034), for aimed checks; "none" before the plants have streamed.</summary>
    internal string DeadwoodNearest(string kind, double x, double z, int count) => plantTiles?.Nearest(kind, x, z, count) ?? "none";

    /// <summary>
    /// How many times the walking session's static collision (built pieces, #9734) has been replaced:
    /// navigation republishes when it moves, as it does for edits.
    /// </summary>
    internal long StaticCollisionRevision { get; private set; }

    /// <summary>
    /// Replaces the built work's part of the walking session's static collision and moves the revision. The
    /// session's collision is that part with the river and lake ice drawn about the player (#9878).
    /// </summary>
    internal void ReplaceStaticCollision(CollisionReplaceRequest request)
    {
        builtCollision = request;
        ComposeStaticCollision();
    }

    /// <summary>The ice's collision ids, clear of the built work's.</summary>
    private const ulong IceAssetBase = 0x4000_0000UL, IceInstanceBase = 0x4000_0000UL;

    /// <summary>Deadwood's collision ids (#10034): an asset per log or stump mesh, an instance per one standing.</summary>
    private const ulong DeadwoodAssetBase = 0x5000_0000UL, DeadwoodInstanceBase = 0x5000_0000UL;

    /// <summary>
    /// Deadwood is solid this far about the player (a square's half side), recomposed once they have walked
    /// <see cref="DeadwoodStepMetres"/>: wide enough to cover the creatures' navigation box (32 m about where it was
    /// published) from wherever the player stands before the next composition.
    /// </summary>
    private const double DeadwoodCollisionMetres = 64, DeadwoodStepMetres = 16;

    private long composedDeadwood = -1;
    private (long X, long Z) composedDeadwoodAt = (long.MinValue / 2, long.MinValue / 2);

    /// <summary>How many logs and stumps the walking collision holds, for the readout.</summary>
    internal int DeadwoodColliders { get; private set; }

    /// <summary>Whether deadwood is solid (the game's rule); a session may turn it off to compare (#10034).</summary>
    internal bool DeadwoodSolid
    {
        get => deadwoodSolid;
        set
        {
            if (deadwoodSolid == value) return;
            deadwoodSolid = value;
            ComposeStaticCollision();
        }
    }

    private bool deadwoodSolid = true;

    private CollisionReplaceRequest? builtCollision;
    private long composedIce;
    private bool composedIceAny;
    private (long X, long Y, long Z) composedOrigin;

    /// <summary>How many ice patches the walking collision holds, for the readout.</summary>
    internal int IcePatches { get; private set; }

    private void ComposeStaticCollision()
    {
        EnsureStarted();
        List<StaticMeshAsset> assets = [.. (builtCollision?.Assets ?? default).ToArray()];
        List<Vector3> vertices = [.. (builtCollision?.Vertices ?? default).ToArray()];
        List<Triangle> triangles = [.. (builtCollision?.Triangles ?? default).ToArray()];
        List<StaticMeshInstance> instances = [.. (builtCollision?.Instances ?? default).ToArray()];
        ulong patch = 0;
        foreach ((Vector3 corner, Vector3[] iceVertices, Triangle[] iceTriangles) in waterSurfaces.Ice)
        {
            ulong asset = IceAssetBase + patch;
            assets.Add(new StaticMeshAsset(asset, (uint)vertices.Count, (uint)iceVertices.Length, (uint)triangles.Count, (uint)iceTriangles.Length));
            vertices.AddRange(iceVertices);
            triangles.AddRange(iceTriangles);
            instances.Add(new StaticMeshInstance(IceInstanceBase + patch, asset,
                new Transform(frame.ToLocal(corner.X, corner.Y, corner.Z), Quaternion.Identity, Vector3.One)));
            patch++;
        }

        int deadwood = 0;
        if (plantTiles is PlantTiles flora)
        {
            // Composed as of now whether or not deadwood is solid, so turning it off does not recompose every update.
            composedDeadwood = flora.DeadwoodRevision;
            composedDeadwoodAt = (lastCentre.X, lastCentre.Z);
            List<PlantTiles.Deadwood> standing = [];
            if (deadwoodSolid) flora.DeadwoodNear(lastCentre.X + 0.5, lastCentre.Z + 0.5, DeadwoodCollisionMetres, standing);
            HashSet<uint> shaped = [];
            foreach (PlantTiles.Deadwood wood in standing)
            {
                ulong asset = DeadwoodAssetBase + wood.Key;
                if (shaped.Add(wood.Key))
                {
                    (Vector3[] positions, uint[] indices) = flora.DeadwoodShape(wood.Key);
                    assets.Add(new StaticMeshAsset(asset, (uint)vertices.Count, (uint)positions.Length, (uint)triangles.Count, (uint)(indices.Length / 3)));
                    vertices.AddRange(positions);
                    for (int i = 0; i + 2 < indices.Length; i += 3) triangles.Add(new Triangle(indices[i], indices[i + 1], indices[i + 2]));
                }

                instances.Add(new StaticMeshInstance(DeadwoodInstanceBase + (ulong)deadwood, asset,
                    new Transform(frame.ToLocal(wood.World.X, wood.World.Y, wood.World.Z), Quaternion.CreateFromAxisAngle(Vector3.UnitY, wood.Yaw),
                        new Vector3(wood.Scale))));
                deadwood++;
            }
        }

        PlaceColliders = places.AddCollision(new Vector3(lastCentre.X + 0.5f, 0, lastCentre.Z + 0.5f), assets, vertices, triangles, instances);
        engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(Session, assets.ToArray(), vertices.ToArray(), triangles.ToArray(), instances.ToArray()));
        DeadwoodColliders = deadwood;
        StaticCollisionRevision++;
        IcePatches = (int)patch;
        composedIce = waterSurfaces.IceRevision;
        composedIceAny = patch > 0;
        composedOrigin = frame.Origin;
    }

    /// <summary>The building look study's draw facts (#9684); empty unless one is built.</summary>
    internal AppearanceFact[] StudyFacts => [.. study?.Facts ?? [], .. waterStudy?.Facts ?? []];

    /// <summary>How many hollows the water study's search lists.</summary>
    private const int WaterStudyHollows = 8;

    /// <summary>
    /// The water look study (#9819): floods the hollow at (x, z) to a level within a radius, as voxel
    /// water cells ("voxel") or as one water surface mesh at the level ("surface"); draws a river reach
    /// as a skin at its own surface ("river"); lists nearby closed hollows ("find"); or clears ("clear").
    /// </summary>
    internal string WaterStudy(string mode, double x, double z, double level, long radius)
    {
        waterStudy ??= new WaterStudy(engine, frame);
        if (mode == "clear")
        {
            waterStudy.HideSurface();
            IReadOnlyList<VoxelAddress> cells = waterStudy.PlacedCells;
            for (int i = 0; i < cells.Count; i += TerrainBrushPolicy.MaximumTransactionCells)
            {
                TryEditCells([.. cells.Skip(i).Take(TerrainBrushPolicy.MaximumTransactionCells)], TerrainEditKind.Clear, TerrainConstants.EmptyMaterial);
            }

            waterStudy.ForgetPlaced();
            waterStudy.Placed([], "cleared");
            return waterStudy.Readout();
        }

        long cx = (long)Math.Floor(x), cz = (long)Math.Floor(z);
        if (mode == "find")
        {
            // Here the level is how far to look (metres) and the radius bounds each hollow.
            IReadOnlyList<Terrain.WaterStudy.Hollow> hollows = Terrain.WaterStudy.FindHollows(cx, cz, (long)level, radius, WaterStudyHollows, recipe.ContinuousHeightAt);
            return string.Join(' ', hollows.Select(h => string.Create(CultureInfo.InvariantCulture,
                $"[{h.X},{h.Z} floor={h.Floor:F1} spill={h.Spill:F1} depth={h.Spill - h.Floor:F1} columns={h.Columns}]")));
        }

        if (mode == "river")
        {
            // The river's own surface over its channel within the radius, its voxel water cleared there.
            Dictionary<(long X, long Z), double> surfaces = [];
            List<VoxelAddress> water = [];
            for (long sz = cz - radius; sz <= cz + radius; sz++)
            {
                for (long sx = cx - radius; sx <= cx + radius; sx++)
                {
                    TerrainColumn column = recipe.ColumnAt(sx, sz);
                    if (!column.Geography.InRiver || ((sx - cx) * (sx - cx)) + ((sz - cz) * (sz - cz)) > radius * radius)
                    {
                        continue;
                    }

                    surfaces[(sx, sz)] = column.Geography.RiverSurface;
                    for (long y = column.Surface + 1; y <= column.WaterTop; y++)
                    {
                        water.Add(new VoxelAddress(sx, y, sz));
                    }
                }
            }

            for (int i = 0; i < water.Count; i += TerrainBrushPolicy.MaximumTransactionCells)
            {
                TryEditCells([.. water.Skip(i).Take(TerrainBrushPolicy.MaximumTransactionCells)], TerrainEditKind.Clear, TerrainConstants.EmptyMaterial);
            }

            waterStudy.ShowSkin(cx, cz, surfaces);
            return string.Create(CultureInfo.InvariantCulture, $"{waterStudy.Readout()} clearedVoxelWater={water.Count}");
        }

        Terrain.WaterStudy.Flood flood = Terrain.WaterStudy.FloodFrom(cx, cz, level, radius, recipe.ContinuousHeightAt);
        if (mode == "surface")
        {
            waterStudy.ShowSurface(cx, cz, level, flood);
            return waterStudy.Readout();
        }

        if (mode != "voxel")
        {
            return $"refused: mode {mode} is not voxel, surface, river, find or clear";
        }

        List<VoxelAddress> fill = Terrain.WaterStudy.VoxelCells(level, flood, recipe.SurfaceAt);
        int applied = 0;
        string outcome = "none";
        for (int i = 0; i < fill.Count; i += TerrainBrushPolicy.MaximumTransactionCells)
        {
            VoxelAddress[] batch = [.. fill.Skip(i).Take(TerrainBrushPolicy.MaximumTransactionCells)];
            TerrainWorldEditResult result = TryEditCells(batch, TerrainEditKind.Set, (ushort)BlockId.Water);
            outcome = TerrainWorldEditResult.Format(result);
            if (result is TerrainWorldEditApplied or TerrainWorldEditNoChanges)
            {
                applied += batch.Length;
                waterStudy.Placed(batch, "");
            }
        }

        waterStudy.Placed([], string.Create(CultureInfo.InvariantCulture,
            $"voxel to {Math.Floor(level):F0} m: {applied}/{fill.Count} cells over {flood.Columns.Count} columns{(flood.Spilled ? ", spills past the radius" : "")} last={outcome}"));
        return waterStudy.Readout();
    }

    /// <summary>Builds the building look study's cabins in a row from (x, z) (#9684), or clears it.</summary>
    internal string BuildStudy(double x, double z, bool clear, bool collide = false)
    {
        if (clear)
        {
            study?.Clear();
            return study?.Readout() ?? "buildStudy none";
        }

        study ??= new Manipulation.BuildStudy(engine, content, frame);
        return study.Build(x, z, (cx, cz) => recipe.ContinuousHeightAt((long)Math.Round(cx), (long)Math.Round(cz)) + 1, collide);
    }

    private int viewDistanceChunks = FarField.DefaultRadiusChunks;

    /// <summary>The near ground's requested radius for a view distance, in chunks: in proportion to the default, within these.</summary>
    private const int NearestRequestedRadius = 5, FarthestRequestedRadius = 10;

    /// <summary>The near ground's edge, in metres from the player.</summary>
    internal double NearEdgeMetres => policy.RequestedRadius * TerrainConstants.ChunkEdgeLength;

    /// <summary>
    /// How far the land is drawn (#9759), in the far field's chunk columns. It sets the far field's
    /// reach; the near ground's request window in proportion (8 chunks at the default 12, within
    /// 5 to 10), which streams in or out within the residency's per-update budget with no reload;
    /// the far field's sunk zone, which stays short of the near edge; the near trees' shelter, which
    /// follows the retained ring; and how far trees are drawn (<see cref="TreeReachFor"/>).
    /// </summary>
    internal int ViewDistanceChunks
    {
        get => viewDistanceChunks;
        set
        {
            viewDistanceChunks = value;
            int near = Math.Clamp((int)Math.Round((double)value * TerrainConstants.RequestedChunkRadius / FarField.DefaultRadiusChunks),
                NearestRequestedRadius, FarthestRequestedRadius);
            policy.SetRequestedRadius(near);
            trees.NearMetres = policy.RetainedRadius * TerrainConstants.ChunkEdgeLength;
            if (farField is not null)
            {
                farField.RadiusChunks = value;
                farField.NearEdgeMetres = NearEdgeMetres;
            }

            treeReachMetres = TreeReachFor(value);
            treeTiles?.Grow(treeReachMetres);
        }
    }

    /// <summary>Trees are drawn this many metres out per far-field chunk column of view distance: 1,200 m at the default.</summary>
    private const float TreeReachPerViewChunk = 100;

    /// <summary>How far trees are drawn for a view distance: in proportion, never past the far field's ground.</summary>
    internal static float TreeReachFor(int viewDistanceChunks) =>
        Math.Min(TreeReachPerViewChunk * viewDistanceChunks, (viewDistanceChunks + 1) * FarField.ChunkMetres);

    private float treeReachMetres = TreeReachFor(FarField.DefaultRadiusChunks);

    private double groundCoverMetres = VegetationTiles.DefaultReachMetres;

    /// <summary>How far grass, flowers, bushes and stones grow (the player's option), thinning with distance (#10016).</summary>
    internal double GroundCoverMetres
    {
        get => groundCoverMetres;
        set
        {
            groundCoverMetres = Math.Max(value, VegetationTiles.MinimumReachMetres);
            vegetationTiles?.Grow(presentation.ScatterKinds, (float)groundCoverMetres);
        }
    }

    /// <summary>The near ground's request and retained radii, in chunks.</summary>
    internal (int Requested, int Retained) NearRadii => (policy.RequestedRadius, policy.RetainedRadius);

    /// <summary>Ground cover beyond the voxel scatter (#10016) and the trees (#10017), as Engine vegetation fields; null before the world starts.</summary>
    private VegetationTiles? vegetationTiles;
    private TreeTiles? treeTiles;

    /// <summary>The shrubs and ferns the forest places (#10032), as Engine vegetation records; null before the world starts.</summary>
    private PlantTiles? plantTiles;

    /// <summary>Where the player's voxel was at the last follow: where the vegetation fields centre.</summary>
    private VoxelAddress lastCentre;


    internal string VegetationReadout() => FormattableString.Invariant(
        $"treeCell={recipe.TreeCellMetres}m treeCache={recipe.TreeCacheCells}/{GenerationConstants.FeatureCacheLimit} treeCacheClears={recipe.TreeCacheClears} cover={groundCoverMetres}m treeReach={treeReachMetres}m resident={streamer.ResidentCount} pendingAdmissions={streamer.PendingAdmissions} {presentation.ScatterReadout()} {trees.Readout()}{(vegetationTiles is null ? "" : " " + vegetationTiles.Readout())}{(treeTiles is null ? "" : " " + treeTiles.Readout())}{(plantTiles is null ? "" : " " + plantTiles.Readout() + $" deadwoodColliders={DeadwoodColliders}")}");


    /// <summary>Sets how far trees are drawn for the session, for measuring (the view distance sets it again).</summary>
    internal string SetFarTrees(long metres)
    {
        treeReachMetres = Math.Clamp(metres, TreeTiles.NearReach, TreeTiles.MaximumReach);
        treeTiles?.Grow(treeReachMetres);
        return VegetationReadout();
    }

    /// <summary>How much of the horizon is drawn, for the scene readout.</summary>
    internal string FarFieldReadout() => farField?.Readout() ?? "farField=none";


    /// <summary>
    /// The Engine's standing surface in resident terrain, including its reconstructed shape.
    /// Outside residency, the recipe and edit overlay give a coarse placement estimate; collision
    /// must be available before a body actually moves there.
    /// </summary>
    internal float GroundAt(double x, double z)
    {
        long cellX = (long)Math.Floor(x), cellZ = (long)Math.Floor(z);
        long surface = recipe.SurfaceAt(cellX, cellZ);
        if (session is not null && IsResident(new VoxelAddress(cellX, surface, cellZ)))
        {
            double top = surface + GroundSearchAbove + 1;
            SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(
                session, frame.ToLocal(x, top, z), -Vector3.UnitY, GroundSearchAbove + GroundSearchBelow + 1,
                new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
                ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty,
                ReadOnlyMemory<SpatialEntityCollider>.Empty));
            if (hit.Present)
            {
                return (float)(top - hit.Distance);
            }
        }

        for (long y = surface + GroundSearchAbove; y >= surface - GroundSearchBelow; y--)
        {
            if (BlockRegistry.TryGetBySlot(MaterialAt(new VoxelAddress(cellX, y, cellZ)), out BlockDefinition block) && block.Collidable)
            {
                return y + 1;
            }
        }

        return surface + 1;
    }

    /// <summary>
    /// The standing surface under a body that stood at <paramref name="feet"/> and can climb
    /// <paramref name="climb"/> metres (#9734): the session's first surface below that reach,
    /// so a body indoors stays on the floor beneath a roof. Null when nothing resident is met.
    /// </summary>
    internal float? GroundBelow(double x, double z, double feet, double climb)
    {
        if (session is null || !IsResident(new VoxelAddress((long)Math.Floor(x), (long)Math.Floor(feet), (long)Math.Floor(z)))) return null;
        double top = feet + climb;
        SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(
            session, frame.ToLocal(x, top, z), -Vector3.UnitY, climb + GroundSearchBelow,
            new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty,
            ReadOnlyMemory<SpatialEntityCollider>.Empty));
        return hit.Present ? (float)(top - hit.Distance) : null;
    }

    /// <summary>
    /// Whether anything stands over a point within <paramref name="reach"/> metres (#9741): a built
    /// roof, an overhang, a cave's ceiling, or a standing tree's crown. The Engine casts straight up
    /// through the session and the crowns over the point. The point is in the Engine's local frame.
    /// </summary>
    internal bool CoveredAbove(Vector3 local, double reach)
    {
        if (session is null) return false;
        SpatialEntityCollider[] crowns = [.. trees.Canopies.ToArray().Where(crown =>
            local.X >= crown.Min.X && local.X <= crown.Max.X && local.Z >= crown.Min.Z && local.Z <= crown.Max.Z)];
        SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(
            session, local, Vector3.UnitY, reach,
            new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
            crowns, ReadOnlyMemory<ulong>.Empty,
            ReadOnlyMemory<SpatialEntityCollider>.Empty));
        return hit.Present;
    }

    /// <summary>Whether the chunk holding a cell is resident: drawn, and solid to collision and sight.</summary>
    internal bool IsResident(VoxelAddress cell) => streamer.IsResident(cell.Chunk);

    /// <summary>Moves whenever the player's edits change, so anything derived from collision can tell it is stale.</summary>
    internal ulong EditRevision => overlayStore.Overlay.Revision;

    /// <summary>The material standing at a cell now: the player's override, else the recipe's.</summary>
    /// <summary>Whether an edit has changed this cell from what the recipe generated.</summary>
    internal bool IsEdited(VoxelAddress address) => overlayStore.Overlay.TryGetMaterial(address, out _);

    internal ushort MaterialAt(VoxelAddress address) =>
        overlayStore.Overlay.TryGetMaterial(address, out ushort material) ? material : recipe.MaterialAt(address);

    /// <summary>
    /// Applies an edit whose cells the caller has already decided on - the volume of a charge, the
    /// shape of a stamp, or the exact undo of something just built.
    /// </summary>
    internal TerrainWorldEditResult TryEditCells(IReadOnlyList<VoxelAddress> cells, TerrainEditKind kind,
        ushort material, Func<VoxelAddress, bool>? playerOverlaps = null)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Count == 0)
        {
            return TerrainWorldEditResult.CastMiss;
        }

        TerrainEditRequest request = TerrainEditRequest.FromCells(cells, kind, material);
        return edits.Apply(Session, request, request.Center, request.Center, SpatialFace.PosY, playerOverlaps, currentStep);
    }

    /// <summary>Applies an aimed brush edit along the player's view.</summary>
    internal TerrainWorldEditResult TryEditFromView(Vector3 origin, Vector3 direction,
        TerrainEditKind kind, ushort material, int radius, Func<VoxelAddress, bool>? playerOverlaps)
    {
        EnsureStarted();
        return edits.ApplyFromView(Session, origin, direction, kind, material, radius, playerOverlaps, currentStep);
    }

    /// <summary>Where the view from this origin first meets the ground, within edit reach: point and normal in the walking frame.</summary>
    internal SpatialHit CastView(Vector3 origin, Vector3 direction)
    {
        EnsureStarted();
        return edits.Cast(Session, origin, direction);
    }

    /// <summary>What the view from this origin is aimed at, within edit reach.</summary>
    internal TerrainPick PickFromView(Vector3 origin, Vector3 direction)
    {
        EnsureStarted();
        return edits.Pick(Session, origin, direction);
    }

    /// <summary>
    /// The generator's identity as a readout: its version, its live fingerprint and whether that
    /// fingerprint is the one recorded for the version, plus residency and the chunk cache.
    /// </summary>
    internal string GenerationReadout()
    {
        uint version = recipe.Contract.Version;
        string golden = recipe.Configuration != TerrainConfiguration.Default ? "custom-world" : TerrainGenerationGoldens.Live.TryGetValue(version, out ulong expected)
            ? expected == GenerationFingerprint ? "match" : string.Create(CultureInfo.InvariantCulture, $"mismatch expected={expected:x16}")
            : "unrecorded";
        return string.Create(CultureInfo.InvariantCulture,
            $"version={version} fingerprint={GenerationFingerprint:x16} golden={golden} source={TerrainGeneratorSource.Stamp:x16} cacheIdentity={CacheIdentity:x16} {streamer.Readout()}{(recipe.Regions is MapRegions regions ? " " + regions.Readout() : "")}");
    }

    /// <summary>Reads the live Engine-owned voxel scene for product diagnostics.</summary>
    internal VoxelSceneReadout ReadScene()
    {
        EnsureStarted();
        return engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session));
    }

    /// <summary>Moves what the world holds in local space after the player commits a rebase.</summary>
    private void OnRebased(Vector3 translation)
    {
        trees.Invalidate();
        if (presentation.Projected)
        {
            presentation.Refresh();
        }
    }

    private WorldUiFacts WorldFacts() => new(
        engine.Voxel.ReadScene(new VoxelSceneReadRequest(Session)),
        overlayStore.Overlay.Count);

    private void EnsureStarted()
    {
        if (!started)
        {
            throw new InvalidOperationException("Terrain world has not started.");
        }
    }
}
