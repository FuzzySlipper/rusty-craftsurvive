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
/// Presentation only, placed on the tiles' workers from pure hashes, never in a town's yard; a record over an
/// edited column is dropped before its tile is committed, and an edit re-asks the tiles under it. Meadow grass,
/// meadow flowers and field stones stay the voxel and tile scatter's.
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
    private const float FadeShare = 0.2f, FloorFadeShare = 0.3f;

    /// <summary>A plant's root sits this far into the ground, so a slope never shows under it.</summary>
    private const float Sink = 0.1f, FloorSink = 0.05f;
    private const double ScaleMinimum = 0.8, ScaleMaximum = 1.2;
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
    private readonly VegetationField plants, floor;
    private readonly VegetationStreamer plantStreamer, floorStreamer;
    private (double X, double Z) focus;

    internal PlantTiles(IEngineContext engine, ProductContent content, TerrainRecipe recipe, SpatialSession session,
        IReadOnlyDictionary<ushort, Appearance[]> treeMeshes, IReadOnlyList<(Appearance Look, float Share)> shadeGrass,
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
            Appearance[] variants = [.. species.Variants.Take((int)VariantsPerSpecies).Select(name =>
            {
                MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, ModelsFolder + species.Folder + "/" + name + PropMesh.Suffix,
                    (role, texture) => texture is null ? plain : textured.For(role, texture)));
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
        for (int variant = 0; variant < shadeGrass.Count; variant++)
            SetLayer(floor, (uint)variant, shadeGrass[variant].Look, FloorReach, FloorFadeShare, castsShadows: false);

        VegetationGround ground = new(recipe);
        plantStreamer = new VegetationStreamer(engine, plants,
            new PlantSampler(recipe, ground, recipe.Configuration.Seed ^ PlacementSeed, variantCounts), DropEdited, CommitsPerUpdate);
        floorStreamer = new VegetationStreamer(engine, floor,
            new FloorSampler(recipe, ground, recipe.Configuration.Seed ^ FloorSeed, shadeShares), DropEdited, CommitsPerUpdate);
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

    internal string Readout()
    {
        static string Field(string name, VegetationReadout r, VegetationStreamer streamer) => string.Create(CultureInfo.InvariantCulture,
            $"{name} tiles={r.Tiles} pending={r.PendingTiles} patches={r.Patches} copies={r.Instances} overBudget={r.OverBudget} deferred={r.Deferred} selectMs={r.SelectionMicroseconds / 1e3:F2} skipped={r.SettlesSkipped} reselected={r.SettlesReselected} sampleMs={streamer.SampleMilliseconds:F0} settled={r.PendingTiles == 0 && r.Deferred == 0 && streamer.InFlight == 0}");
        return string.Create(CultureInfo.InvariantCulture, $"plantTiles species={variantCounts.Count} ")
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
        top is BlockId.Grass or BlockId.Sand || (top == BlockId.Dirt && !recipe.InTownYard(x, z));

    /// <summary>A tile's cells, in the given cell size, covering the tile.</summary>
    private static (long First, long Last) Cells(double origin, double size, double cell) =>
        ((long)Math.Floor(origin / cell), (long)Math.Ceiling((origin + size) / cell) - 1);

    /// <summary>One plant tile on a worker: heights from the recipe, and every plant and sapling growing in each cell.</summary>
    private sealed class PlantSampler(TerrainRecipe recipe, VegetationGround ground, ulong placement, IReadOnlyDictionary<ushort, int> variantCounts)
        : IVegetationSampler
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
                    double scale = ScaleMinimum + ((ScaleMaximum - ScaleMinimum) * Part(hash, Draw.Scale));
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
            samples.Records.Add(new VegetationRecord(key,
                new Vector3((float)(x - tile.OriginX), (float)(height - sink), (float)(z - tile.OriginZ)), yaw, (float)scale));
        }

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
