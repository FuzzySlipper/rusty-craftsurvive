using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.VegetationTiles;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// What grows on the ground beside the trees (#10032, #10034), as Engine vegetation records in two fields:
/// <list type="bullet">
/// <item><b>Plants:</b> ferns, woodland flowers, shrubs, scrub and deadwood, each species on its own in every
/// <see cref="CellMetres"/> cell where its ecology suits (<see cref="Forest.Plants"/>), so they overlap; and
/// saplings of the stand's trees in its gaps and edges (<see cref="Forest.Sapling"/>).</item>
/// <item><b>Forest floor:</b> shade grass, a clump in every <see cref="FloorCellMetres"/> cell of forest floor
/// (the Dirt topsoil under a closed canopy), so the meadow's grass carries on under the trees, deeper and darker.
/// It reaches <see cref="FloorReach"/>: past that, the trunks hide the floor.</item>
/// </list>
/// Placed on the tiles' workers from pure hashes, never in a town's yard; a record over an edited column is
/// dropped before its tile is committed, and an edit re-asks the tiles under it. Meadow grass, meadow flowers and
/// field stones stay the voxel and tile scatter's. Plants are presentation only, but deadwood is solid: as a tile is
/// committed its logs and stumps are kept, and <see cref="DeadwoodNear"/> gives those about the player, with each
/// one's mesh as its collision, for the walking session (TerrainWorld).
/// </summary>
internal sealed class PlantTiles : IDisposable
{
    private const double TileMetres = 64;
    private const uint TileCells = 8;
    private const double FloorTileMetres = 32;
    private const uint FloorTileCells = 8;

    private const int CellMetres = Forest.PlantCellMetres;
    private const double FloorCellMetres = 0.7;

    /// <summary>Shrubs, deadwood and saplings are drawn this far, ground plants to <see cref="GroundReach"/>, each shrinking away over the last <see cref="FadeShare"/>.</summary>
    internal const float Reach = 160, GroundReach = 112;
    /// <summary>Shade grass is drawn this far.</summary>
    internal const float FloorReach = 72;
    /// <summary>Blade shade grass draws its lighter clump from this far, dithering over the band before it.</summary>
    private const float FloorLiteFrom = 24, FloorLodBand = 4;
    private const float FadeShare = 0.2f, FloorFadeShare = 0.3f;

    /// <summary>A plant's root sits this far into the ground, so a slope never shows under it.</summary>
    private const float Sink = 0.1f, FloorSink = 0.05f;
    private const double ScaleMinimum = 0.8, ScaleMaximum = 1.2;
    /// <summary>Deadwood varies less, so a fallen trunk always stands above the player's step and mostly below a jump.</summary>
    private const double DeadwoodScaleMinimum = 0.9, DeadwoodScaleMaximum = 1.1;
    private const double ShadeScaleMinimum = 1.15, ShadeScaleMaximum = 1.8;
    /// <summary>A sapling stands this tall, whatever its species grows to.</summary>
    private const double SaplingHeightMinimum = 1.2, SaplingHeightMaximum = 3.6;

    /// <summary>Layer keys: one per plant species variant, and one per tree species for its saplings.</summary>
    private const uint PlantBase = 0x400, VariantsPerSpecies = 4, SaplingBase = 0x600;
    private const uint MaximumPerLayer = 65_536;
    private const int CommitsPerUpdate = 8;
    private const double FocusStepMetres = 4;
    private const float CellCentre = 0.5f;

    // Plants flutter by their vertex wind weight and lean a little, as the scatter's did.
    private const float WindBend = 0.05f, WindFlutter = 0.05f, Roughness = 0.85f;
    private static readonly Color White = new(1, 1, 1, 1);
    private const string ModelsFolder = "models/";

    /// <summary>Where in its cell a plant stands, and its variant, turn and size, are hashed from the world's seed mixed with these.</summary>
    private const ulong PlacementSeed = 0x5EED_F10A_0000_0001UL, FloorSeed = 0x5EED_F10A_0000_0002UL;

    private readonly IEngineContext engine;
    private readonly Func<TerrainOverlayState> overlay;
    private readonly Material plain;
    private readonly PropMaterials textured;
    private readonly List<MeshResource> meshes = [];
    private readonly List<Appearance> looks = [];
    private readonly Dictionary<ushort, int> variantCounts = [];

    /// <summary>A fallen log or stump standing in the world: its layer key, world position, turn and size.</summary>
    internal readonly record struct Deadwood(uint Key, Vector3 World, float Yaw, float Scale);

    /// <summary>Each deadwood layer's mesh as collision: its positions and triangles (each three indices).</summary>
    private readonly Dictionary<uint, (Vector3[] Positions, uint[] Indices)> deadwoodShapes = [];
    private readonly Dictionary<(double X, double Z), Deadwood[]> deadwoodByTile = [];
    private readonly Dictionary<uint, string> deadwoodNames = [];

    /// <summary>
    /// How far each deadwood mesh reaches along its length from its origin, and whether it stands above the player's
    /// step: a log lies only where the ground is level enough under it.
    /// </summary>
    private readonly Dictionary<uint, (double Half, bool Blocking)> deadwoodLying = [];

    /// <summary>A log or trunk lies only where the ground under both ends is within this of its middle's: on a slope one end would float.</summary>
    private const double DeadwoodEndTolerance = 0.3;

    /// <summary>
    /// Deadwood taller than the player's step also needs the ground either side of its middle within this: from
    /// the uphill side it would otherwise be low enough to step onto.
    /// </summary>
    private const double DeadwoodSideTolerance = 0.2, DeadwoodSideMetres = 1.5;

    /// <summary>Kept tiles' deadwood is forgotten past this many metres from the focus.</summary>
    private const double DeadwoodKeptMetres = Reach * 1.5;
    private double deadwoodReach;
    private readonly VegetationField plants, floor;
    private readonly VegetationStreamer plantStreamer, floorStreamer;
    private (double X, double Z) focus;

    internal PlantTiles(IEngineContext engine, ProductContent content, TerrainRecipe recipe, SpatialSession session,
        IReadOnlyDictionary<ushort, Appearance[]> treeMeshes, IReadOnlyList<(Appearance Look, Appearance? Lite, float Share)> shadeGrass,
        Func<TerrainOverlayState> overlay, VoxelAddress centre)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        plain = engine.Graphics.CreateMaterial(new MaterialRequest(White, default(RenderResourceReference), Roughness, White, Vector3.Zero, 0, true) with
        {
            FlatShading = true,
            WindBend = WindBend,
            WindFlutter = WindFlutter,
        });
        textured = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, WindBend, WindFlutter));
        focus = (centre.X + CellCentre, centre.Z + CellCentre);
        VegetationFocus at = VegetationFocus.At(focus.X, centre.Y, focus.Z);
        plants = engine.Vegetation.Create(new VegetationFieldCreateRequest(session, TileMetres, TileCells, at));
        floor = engine.Vegetation.Create(new VegetationFieldCreateRequest(session, FloorTileMetres, FloorTileCells, at));

        foreach (FloraSpecies species in Plants(recipe.Forest.Catalog))
        {
            Appearance[] variants = [.. species.Variants.Take((int)VariantsPerSpecies).Select((name, variant) =>
            {
                MeshResourceCreateRequest request = PropMesh.Read(content, ModelsFolder + species.Folder + "/" + name + PropMesh.Suffix,
                    (role, texture) => texture is null ? plain : textured.For(role, texture));
                if (species.Role == FloraRole.Deadwood)
                {
                    // Deadwood tall enough to block collides as its upright-sided boxes, when the generator wrote them; the rest as its mesh.
                    IReadOnlyList<(Vector3 Centre, Vector3 Half, float Yaw)> boxes = PropMesh.Colliders(content, ModelsFolder + species.Folder + "/" + name + PropMesh.Suffix);
                    deadwoodShapes[PlantKey(species.Id, variant)] = boxes.Count > 0 ? Boxes(boxes) : (request.Positions.ToArray(), request.Indices.ToArray());
                    deadwoodLying[PlantKey(species.Id, variant)] = (request.Positions.ToArray().Max(position => Math.Abs(position.X)),
                        species.Height > Player.PlayerConstants.MaximumStepHeight);
                    deadwoodNames[PlantKey(species.Id, variant)] = name;
                }
                MeshResource mesh = engine.Graphics.CreateMeshResource(request);
                meshes.Add(mesh);
                Appearance look = engine.Graphics.CreateMeshAppearance(mesh);
                looks.Add(look);
                return look;
            })];
            variantCounts[species.Id] = variants.Length;
            // Ground plants (ferns, flowers) are small and many: nearer, and their shadows are not worth a caster pass.
            bool small = species.Role == FloraRole.Ground;
            for (int variant = 0; variant < variants.Length; variant++)
                SetLayer(plants, PlantKey(species.Id, variant), variants[variant], small ? GroundReach : Reach, FadeShare, castsShadows: !small);
        }

        // Saplings are the stand's own trees, small: each tree species' first variant.
        foreach ((ushort species, Appearance[] near) in treeMeshes)
            SetLayer(plants, SaplingKey(species), near[0], Reach, FadeShare, castsShadows: true);

        double shares = shadeGrass.Sum(grass => grass.Share);
        double[] shadeShares = [.. shadeGrass.Select(grass => grass.Share / shares)];
        ShadeGrass(shadeGrass);

        VegetationGround ground = new(recipe);
        plantStreamer = new VegetationStreamer(engine, plants,
            new PlantSampler(recipe, ground, recipe.Configuration.Seed ^ PlacementSeed, variantCounts, deadwoodLying), CommitPlants, CommitsPerUpdate);
        floorStreamer = new VegetationStreamer(engine, floor,
            new FloorSampler(recipe, ground, recipe.Configuration.Seed ^ FloorSeed, shadeShares), DropEdited, CommitsPerUpdate);
    }

    /// <summary>
    /// Sets the forest floor's shade-grass layers: each variant near, and its lighter clump from <see cref="FloorLiteFrom"/>
    /// when it has one (blades, #10038). Called again when the grass's look is switched; the records stay.
    /// </summary>
    internal void ShadeGrass(IReadOnlyList<(Appearance Look, Appearance? Lite, float Share)> shadeGrass)
    {
        for (int variant = 0; variant < shadeGrass.Count; variant++)
        {
            SetLayer(floor, (uint)variant, shadeGrass[variant].Look, FloorReach, FloorFadeShare, castsShadows: false);
            VegetationLod[] levels = shadeGrass[variant].Lite is Appearance lite ? [VegetationLod.Mesh(lite, FloorLiteFrom)] : [];
            engine.Vegetation.SetLayerDetail(new VegetationLayerDetailRequest(floor, (uint)variant, levels, FloorLodBand));
        }
    }

    /// <summary>Keeps both fields centred on the player, re-asks the tiles under new edits, and streams tiles.</summary>
    internal void Follow(VoxelAddress centre, (long MinX, long MinZ, long MaxX, long MaxZ)? changed)
    {
        (double X, double Z) at = (centre.X + CellCentre, centre.Z + CellCentre);
        if (Math.Max(Math.Abs(at.X - focus.X), Math.Abs(at.Z - focus.Z)) >= FocusStepMetres)
        {
            focus = at;
            VegetationFocus moved = VegetationFocus.At(at.X, centre.Y, at.Z);
            engine.Vegetation.SetFocus(new VegetationFocusRequest(plants, moved));
            engine.Vegetation.SetFocus(new VegetationFocusRequest(floor, moved));
        }

        if (changed is var (minX, minZ, maxX, maxZ))
        {
            engine.Vegetation.Invalidate(new VegetationInvalidateRequest(plants, minX - CellMetres, minZ - CellMetres, maxX + CellMetres, maxZ + CellMetres));
            engine.Vegetation.Invalidate(new VegetationInvalidateRequest(floor, minX - 1, minZ - 1, maxX + 1, maxZ + 1));
        }

        plantStreamer.Update();
        floorStreamer.Update();
    }

    /// <summary>Moves when a committed tile's deadwood within <see cref="DeadwoodNear"/>'s last reach changes: the collision is due again.</summary>
    internal long DeadwoodRevision { get; private set; }

    /// <summary>
    /// The kept logs and stumps within <paramref name="reach"/> metres (square) of a point; later commits within it
    /// move <see cref="DeadwoodRevision"/>.
    /// </summary>
    internal void DeadwoodNear(double x, double z, double reach, List<Deadwood> into)
    {
        deadwoodReach = reach;
        foreach (Deadwood[] tile in deadwoodByTile.Values)
            foreach (Deadwood wood in tile)
                if (Math.Abs(wood.World.X - x) <= reach && Math.Abs(wood.World.Z - z) <= reach) into.Add(wood);
    }

    /// <summary>The kept logs and stumps nearest a point: mesh, position, turn in degrees and size.</summary>
    internal string Nearest(string kind, double x, double z, int count) => string.Join(" | ", deadwoodByTile.Values.SelectMany(tile => tile)
        .Where(wood => deadwoodNames[wood.Key].StartsWith(kind + (kind.Length > 0 ? "-" : ""), StringComparison.Ordinal))
        .OrderBy(wood => Math.Pow(wood.World.X - x, 2) + Math.Pow(wood.World.Z - z, 2)).Take(count)
        .Select(wood => FormattableString.Invariant(
            $"{deadwoodNames[wood.Key]} at={wood.World.X:F2},{wood.World.Y:F2},{wood.World.Z:F2} yaw={wood.Yaw * 180 / Math.PI:F0} scale={wood.Scale:F2}")));

    /// <summary>A deadwood layer's mesh as collision: positions and triangle indices.</summary>
    internal (Vector3[] Positions, uint[] Indices) DeadwoodShape(uint key) => deadwoodShapes[key];

    internal string Readout()
    {
        static string Field(string name, VegetationReadout r, VegetationStreamer streamer) => string.Create(CultureInfo.InvariantCulture,
            $"{name} tiles={r.Tiles} pending={r.PendingTiles} patches={r.Patches} copies={r.Instances} overBudget={r.OverBudget} deferred={r.Deferred} selectMs={r.SelectionMicroseconds / 1e3:F2} skipped={r.SettlesSkipped} reselected={r.SettlesReselected} sampleMs={streamer.SampleMilliseconds:F0} settled={r.PendingTiles == 0 && r.Deferred == 0 && streamer.InFlight == 0}");
        return string.Create(CultureInfo.InvariantCulture, $"plantTiles species={variantCounts.Count} deadwoodTiles={deadwoodByTile.Count} ")
            + Field("plants", engine.Vegetation.Read(plants), plantStreamer) + " floorTiles "
            + Field("floor", engine.Vegetation.Read(floor), floorStreamer);
    }

    public void Dispose()
    {
        plantStreamer.Dispose();
        floorStreamer.Dispose();
        plants.Dispose();
        floor.Dispose();
        foreach (Appearance look in looks) look.Dispose();
        looks.Clear();
        foreach (MeshResource mesh in meshes) mesh.Dispose();
        meshes.Clear();
        textured.Dispose();
        plain.Dispose();
    }

    /// <summary>The independent choices hashed from a record's own hash: where in its cell, which variant, its turn and size.</summary>
    private enum Draw
    {
        X = 1,
        Z,
        Variant,
        Yaw,
        Scale,
    }

    /// <summary>One choice in [0, 1) from a record's hash, independent of the others (shifting one hash would correlate them).</summary>
    private static double Part(ulong hash, Draw part) => TerrainRecipe.HashUnit(TerrainRecipe.CoordinateHash(hash, (long)part, 0));

    /// <summary>The catalog's shrubs, ground plants and deadwood that grow by density: those the forest places.</summary>
    private static IEnumerable<FloraSpecies> Plants(FloraCatalog catalog) =>
        catalog.Species.Where(species => species.Role != FloraRole.Tree && species.Ecology is { Density: > 0 });

    /// <summary>Boxes as one collision mesh: each box's eight corners and twelve outward triangles.</summary>
    private static (Vector3[] Positions, uint[] Indices) Boxes(IReadOnlyList<(Vector3 Centre, Vector3 Half, float Yaw)> boxes)
    {
        // Corner index bits: 1 = +X, 2 = +Y, 4 = +Z; each face wound anticlockwise seen from outside.
        ReadOnlySpan<int> faces = [0, 4, 6, 2, 1, 3, 7, 5, 0, 1, 5, 4, 2, 6, 7, 3, 0, 2, 3, 1, 4, 5, 7, 6];
        List<Vector3> positions = [];
        List<uint> indices = [];
        foreach ((Vector3 centre, Vector3 half, float yaw) in boxes)
        {
            uint first = (uint)positions.Count;
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = new((corner & 1) == 0 ? -half.X : half.X, (corner & 2) == 0 ? -half.Y : half.Y, (corner & 4) == 0 ? -half.Z : half.Z);
                positions.Add(centre + Vector3.Transform(local, turn));
            }

            for (int face = 0; face < faces.Length; face += 4)
            {
                indices.AddRange([first + (uint)faces[face], first + (uint)faces[face + 1], first + (uint)faces[face + 2]]);
                indices.AddRange([first + (uint)faces[face], first + (uint)faces[face + 2], first + (uint)faces[face + 3]]);
            }
        }

        return ([.. positions], [.. indices]);
    }

    private static uint PlantKey(ushort species, int variant) => PlantBase + ((uint)species * VariantsPerSpecies) + (uint)variant;

    private static uint SaplingKey(ushort species) => SaplingBase + species;

    private void SetLayer(VegetationField field, uint key, Appearance look, float reach, float fadeShare, bool castsShadows) =>
        engine.Vegetation.SetLayer(new VegetationLayerRequest(field, key, look, default, 0, reach) with
        {
            Grounds = ulong.MaxValue,
            Fade = reach * fadeShare,
            MaximumInstances = MaximumPerLayer,
            CastsShadows = castsShadows,
        });

    /// <summary>A plant tile as it is committed: edited columns' records dropped, and its deadwood kept for the collision.</summary>
    private void CommitPlants(VegetationTileSamples samples)
    {
        DropEdited(samples);
        VegetationTileRequest tile = samples.Tile;
        Deadwood[] wood = [.. samples.Records.Where(record => deadwoodShapes.ContainsKey(record.Layer)).Select(record => new Deadwood(record.Layer,
            new Vector3((float)(tile.OriginX + record.Position.X), record.Position.Y, (float)(tile.OriginZ + record.Position.Z)), record.Yaw, record.Scale))];
        (double X, double Z) key = (tile.OriginX, tile.OriginZ);
        bool had = deadwoodByTile.TryGetValue(key, out Deadwood[]? before);
        if (wood.Length > 0) deadwoodByTile[key] = wood;
        else deadwoodByTile.Remove(key);
        double size = tile.TileCells * tile.CellSize;
        bool near = Math.Abs(tile.OriginX + (size / 2) - focus.X) <= deadwoodReach + size
            && Math.Abs(tile.OriginZ + (size / 2) - focus.Z) <= deadwoodReach + size;
        if (near && (wood.Length > 0 || (had && before!.Length > 0))) DeadwoodRevision++;
        foreach ((double X, double Z) far in deadwoodByTile.Keys.Where(origin =>
            Math.Max(Math.Abs(origin.X - focus.X), Math.Abs(origin.Z - focus.Z)) > DeadwoodKeptMetres).ToList())
            deadwoodByTile.Remove(far);
    }

    /// <summary>Drops the records standing on edited columns, on the update's thread where the edits may be read.</summary>
    private void DropEdited(VegetationTileSamples samples)
    {
        VegetationTileRequest tile = samples.Tile;
        TerrainOverlayState edits = overlay();
        double size = tile.TileCells * tile.CellSize;
        if (!edits.TouchesColumns((long)Math.Floor(tile.OriginX), (long)Math.Floor(tile.OriginZ),
            (long)Math.Ceiling(tile.OriginX + size), (long)Math.Ceiling(tile.OriginZ + size))) return;
        samples.Records.RemoveAll(record => edits.TouchesColumn((long)Math.Floor(tile.OriginX + record.Position.X),
            (long)Math.Floor(tile.OriginZ + record.Position.Z)));
    }

    /// <summary>Whether a plant may root in a column's top: meadow, forest floor or dry sand, never a town's yard.</summary>
    private static bool Soil(TerrainRecipe recipe, BlockId top, long x, long z) =>
        top is BlockId.Grass or BlockId.DryGrass or BlockId.Sand || (top == BlockId.Dirt && !recipe.InTownYard(x, z));

    /// <summary>A tile's cells, in the given cell size, covering the tile.</summary>
    private static (long First, long Last) Cells(double origin, double size, double cell) =>
        ((long)Math.Floor(origin / cell), (long)Math.Ceiling((origin + size) / cell) - 1);

    /// <summary>One plant tile on a worker: heights from the recipe, and every plant and sapling growing in each cell.</summary>
    private sealed class PlantSampler(TerrainRecipe recipe, VegetationGround ground, ulong placement, IReadOnlyDictionary<ushort, int> variantCounts,
        IReadOnlyDictionary<uint, (double Half, bool Blocking)> lying) : IVegetationSampler
    {
        private readonly VegetationTiles.Sampler heights = new(ground);

        public void Sample(VegetationTileRequest tile, VegetationTileSamples samples)
        {
            heights.Sample(tile, samples);
            double size = tile.TileCells * tile.CellSize;
            (long firstX, long lastX) = Cells(tile.OriginX, size, CellMetres);
            (long firstZ, long lastZ) = Cells(tile.OriginZ, size, CellMetres);
            List<(FloraSpecies Species, ulong Hash)> growing = [];
            for (long cellX = firstX; cellX <= lastX; cellX++)
            for (long cellZ = firstZ; cellZ <= lastZ; cellZ++)
            {
                long centreX = (cellX * CellMetres) + (CellMetres / 2), centreZ = (cellZ * CellMetres) + (CellMetres / 2);
                if (ground.At(centreX, centreZ) is not var (_, centreTop, geography) || !Soil(recipe, centreTop, centreX, centreZ)) continue;
                double canopy = recipe.Forest.Canopy(centreX, centreZ, geography);
                growing.Clear();
                recipe.Forest.Plants(cellX, cellZ, canopy, geography, growing);
                foreach ((FloraSpecies species, ulong hash) in growing)
                {
                    int variant = (int)(Part(hash, Draw.Variant) * variantCounts[species.Id]);
                    (double low, double high) = species.Role == FloraRole.Deadwood
                        ? (DeadwoodScaleMinimum, DeadwoodScaleMaximum)
                        : (ScaleMinimum, ScaleMaximum);
                    double scale = low + ((high - low) * Part(hash, Draw.Scale));
                    Place(tile, samples, cellX, cellZ, CellMetres, CombinedHash(placement, hash), PlantKey(species.Id, variant), scale, Sink);
                }

                if (recipe.Forest.Sapling(cellX, cellZ, centreX, centreZ, canopy, geography, out ulong saplingHash) is FloraSpecies sapling)
                {
                    double tall = SaplingHeightMinimum + ((SaplingHeightMaximum - SaplingHeightMinimum) * Part(saplingHash, Draw.Scale));
                    Place(tile, samples, cellX, cellZ, CellMetres, CombinedHash(placement, saplingHash), SaplingKey(sapling.Id), tall / sapling.Height, Sink);
                }
            }
        }

        private void Place(VegetationTileRequest tile, VegetationTileSamples samples, long cellX, long cellZ, double cell, ulong hash,
            uint key, double scale, float sink)
        {
            double x = (cellX + Part(hash, Draw.X)) * cell, z = (cellZ + Part(hash, Draw.Z)) * cell;
            long column = (long)Math.Floor(x), row = (long)Math.Floor(z);
            if (ground.At(column, row) is not var (height, top, _) || !Soil(recipe, top, column, row)) return;
            float yaw = (float)(Part(hash, Draw.Yaw) * Math.Tau);
            if (lying.TryGetValue(key, out (double Half, bool Blocking) shape))
            {
                // The mesh's +X turned by the yaw about +Y, as the Engine turns a record.
                double reach = shape.Half * scale, alongX = Math.Cos(yaw) * reach, alongZ = -Math.Sin(yaw) * reach;
                double acrossX = Math.Sin(yaw) * DeadwoodSideMetres, acrossZ = Math.Cos(yaw) * DeadwoodSideMetres;
                if (!Level(x - alongX, z - alongZ, height, DeadwoodEndTolerance) || !Level(x + alongX, z + alongZ, height, DeadwoodEndTolerance)) return;
                if (shape.Blocking && (!Level(x - acrossX, z - acrossZ, height, DeadwoodSideTolerance) || !Level(x + acrossX, z + acrossZ, height, DeadwoodSideTolerance)))
                    return;
            }

            samples.Records.Add(new VegetationRecord(key,
                new Vector3((float)(x - tile.OriginX), (float)(height - sink), (float)(z - tile.OriginZ)), yaw, (float)scale));
        }

        /// <summary>Whether the ground at a point is within a tolerance of a height.</summary>
        private bool Level(double x, double z, double height, double tolerance) =>
            ground.At((long)Math.Floor(x), (long)Math.Floor(z)) is var (other, _, _) && Math.Abs(other - height) <= tolerance;

        private static ulong CombinedHash(ulong placement, ulong hash) => TerrainRecipe.CoordinateHash(placement, (long)hash, (long)(hash >> 32));
    }

    /// <summary>One forest-floor tile on a worker: a shade-grass clump in every cell of forest floor.</summary>
    private sealed class FloorSampler(TerrainRecipe recipe, VegetationGround ground, ulong placement, double[] shares) : IVegetationSampler
    {
        private readonly VegetationTiles.Sampler heights = new(ground);

        public void Sample(VegetationTileRequest tile, VegetationTileSamples samples)
        {
            heights.Sample(tile, samples);
            double size = tile.TileCells * tile.CellSize;
            (long firstX, long lastX) = Cells(tile.OriginX, size, FloorCellMetres);
            (long firstZ, long lastZ) = Cells(tile.OriginZ, size, FloorCellMetres);
            for (long cellX = firstX; cellX <= lastX; cellX++)
            for (long cellZ = firstZ; cellZ <= lastZ; cellZ++)
            {
                ulong hash = TerrainRecipe.CoordinateHash(placement, cellX, cellZ);
                double x = (cellX + Part(hash, Draw.X)) * FloorCellMetres, z = (cellZ + Part(hash, Draw.Z)) * FloorCellMetres;
                if (x < tile.OriginX || z < tile.OriginZ || x >= tile.OriginX + size || z >= tile.OriginZ + size) continue;
                long column = (long)Math.Floor(x), row = (long)Math.Floor(z);
                if (ground.At(column, row) is not var (height, top, geography) || top != BlockId.Dirt || recipe.InTownYard(column, row)) continue;
                // Forest floor only: a steep bank of bare earth in the open is not the canopy's.
                if (recipe.Forest.Canopy(column, row, geography) < Forest.FloorFromCanopy) continue;
                double pick = Part(hash, Draw.Variant);
                int variant = 0;
                while (variant < shares.Length - 1 && pick >= shares[variant]) pick -= shares[variant++];
                double scale = ShadeScaleMinimum + ((ShadeScaleMaximum - ShadeScaleMinimum) * Part(hash, Draw.Scale));
                float yaw = (float)(Part(hash, Draw.Yaw) * Math.Tau);
                samples.Records.Add(new VegetationRecord((uint)variant,
                    new Vector3((float)(x - tile.OriginX), (float)(height - FloorSink), (float)(z - tile.OriginZ)), yaw, (float)scale));
            }
        }
    }
}
