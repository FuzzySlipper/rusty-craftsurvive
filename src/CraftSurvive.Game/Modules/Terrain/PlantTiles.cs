using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.VegetationTiles;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The world's shrubs and ferns (#10032) as Engine vegetation records: understory under the canopy
/// (holly, hazel, ferns, bramble) and scrub in the open (broom, gorse, heather, rosemary), each where
/// its ecology suits (<see cref="Forest.Plant"/>). They are presentation only: placed on the tiles'
/// workers from a pure hash of each <see cref="CellMetres"/> cell, on meadow, forest floor or dry sand,
/// never in a town's yard; a plant over an edited column is dropped before its tile is committed, and
/// an edit re-asks the tiles under it. Grass, flowers and field stones stay the voxel and tile scatter's.
/// </summary>
internal sealed class PlantTiles : IDisposable
{
    private const double TileMetres = 64;
    private const uint TileCells = 8;

    private const int CellMetres = Forest.PlantCellMetres;

    /// <summary>Plants are drawn this far, shrinking away over the last <see cref="FadeShare"/> of it.</summary>
    internal const float Reach = 160;
    private const float FadeShare = 0.2f;

    /// <summary>A plant's root sits this far into the ground, so a slope never shows under it.</summary>
    private const float Sink = 0.1f;
    private const double ScaleMinimum = 0.8, ScaleMaximum = 1.2;

    /// <summary>A plant layer's key: one per species variant, above the ground cover's and the trees'.</summary>
    private const uint LayerBase = 0x400, VariantsPerSpecies = 4;
    private const uint MaximumPlantsPerLayer = 65_536;
    private const int CommitsPerUpdate = 8;
    private const double FocusStepMetres = 4;
    private const float CellCentre = 0.5f;

    // Plants flutter by their vertex wind weight and lean a little, as the scatter's did.
    private const float WindBend = 0.05f, WindFlutter = 0.05f, Roughness = 0.85f;
    private static readonly Color White = new(1, 1, 1, 1);
    private const string ModelsFolder = "models/";

    /// <summary>Where in its cell a plant stands, and its variant, turn and size, are hashed from the world's seed mixed with this.</summary>
    private const ulong PlacementSeed = 0x5EED_F10A_0000_0001UL;

    private readonly IEngineContext engine;
    private readonly Func<TerrainOverlayState> overlay;
    private readonly Material plain;
    private readonly PropMaterials textured;
    private readonly List<MeshResource> meshes = [];
    private readonly Dictionary<ushort, Appearance[]> looks = [];
    private readonly VegetationField field;
    private readonly VegetationStreamer streamer;
    private (double X, double Z) focus;

    internal PlantTiles(IEngineContext engine, ProductContent content, TerrainRecipe recipe, SpatialSession session,
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
        foreach (FloraSpecies species in Plants(recipe.Forest.Catalog))
        {
            looks[species.Id] = [.. species.Variants.Take((int)VariantsPerSpecies).Select(name =>
            {
                MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, ModelsFolder + species.Folder + "/" + name + PropMesh.Suffix,
                    (role, texture) => texture is null ? plain : textured.For(role, texture)));
                meshes.Add(mesh);
                return engine.Graphics.CreateMeshAppearance(mesh);
            })];
        }

        focus = (centre.X + CellCentre, centre.Z + CellCentre);
        field = engine.Vegetation.Create(new VegetationFieldCreateRequest(session, TileMetres, TileCells,
            VegetationFocus.At(focus.X, centre.Y, focus.Z)));
        streamer = new VegetationStreamer(engine, field, new Sampler(recipe, new VegetationGround(recipe), recipe.Configuration.Seed ^ PlacementSeed),
            DropEdited, CommitsPerUpdate);
        foreach ((ushort species, Appearance[] variants) in looks)
        {
            for (int variant = 0; variant < variants.Length; variant++)
            {
                engine.Vegetation.SetLayer(new VegetationLayerRequest(field, Key(species, variant), variants[variant], default, 0, Reach) with
                {
                    Grounds = ulong.MaxValue,
                    Fade = Reach * FadeShare,
                    MaximumInstances = MaximumPlantsPerLayer,
                    CastsShadows = true,
                });
            }
        }
    }

    /// <summary>Keeps the field centred on the player, re-asks the tiles under new edits, and streams tiles.</summary>
    internal void Follow(VoxelAddress centre, (long MinX, long MinZ, long MaxX, long MaxZ)? changed)
    {
        (double X, double Z) at = (centre.X + CellCentre, centre.Z + CellCentre);
        if (Math.Max(Math.Abs(at.X - focus.X), Math.Abs(at.Z - focus.Z)) >= FocusStepMetres)
        {
            focus = at;
            engine.Vegetation.SetFocus(new VegetationFocusRequest(field, VegetationFocus.At(at.X, centre.Y, at.Z)));
        }

        if (changed is var (minX, minZ, maxX, maxZ))
        {
            engine.Vegetation.Invalidate(new VegetationInvalidateRequest(field, minX - CellMetres, minZ - CellMetres, maxX + CellMetres, maxZ + CellMetres));
        }

        streamer.Update();
    }

    internal string Readout()
    {
        VegetationReadout r = engine.Vegetation.Read(field);
        return string.Create(CultureInfo.InvariantCulture,
            $"plantTiles species={looks.Count} tiles={r.Tiles} pending={r.PendingTiles} patches={r.Patches} copies={r.Instances} overBudget={r.OverBudget} deferred={r.Deferred} selectMs={r.SelectionMicroseconds / 1e3:F2} skipped={r.SettlesSkipped} reselected={r.SettlesReselected} sampleMs={streamer.SampleMilliseconds:F0} settled={r.PendingTiles == 0 && r.Deferred == 0 && streamer.InFlight == 0}");
    }

    public void Dispose()
    {
        streamer.Dispose();
        field.Dispose();
        foreach (Appearance look in looks.Values.SelectMany(variants => variants)) look.Dispose();
        looks.Clear();
        foreach (MeshResource mesh in meshes) mesh.Dispose();
        meshes.Clear();
        textured.Dispose();
        plain.Dispose();
    }

    /// <summary>The catalog's shrubs and ground plants with an ecology: those the forest places.</summary>
    private static IEnumerable<FloraSpecies> Plants(FloraCatalog catalog) =>
        catalog.Species.Where(species => species.Role != FloraRole.Tree && species.Ecology is not null);

    private static uint Key(ushort species, int variant) => LayerBase + ((uint)species * VariantsPerSpecies) + (uint)variant;

    /// <summary>Drops the plants standing on edited columns, on the update's thread where the edits may be read.</summary>
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

    /// <summary>One tile on a worker: heights from the recipe, and a plant in each cell where one grows.</summary>
    private sealed class Sampler(TerrainRecipe recipe, VegetationGround ground, ulong placement) : IVegetationSampler
    {
        private readonly VegetationTiles.Sampler heights = new(ground);

        public void Sample(VegetationTileRequest tile, VegetationTileSamples samples)
        {
            heights.Sample(tile, samples);
            double size = tile.TileCells * tile.CellSize;
            long firstX = GridMath.FloorDivide((long)Math.Floor(tile.OriginX), CellMetres);
            long firstZ = GridMath.FloorDivide((long)Math.Floor(tile.OriginZ), CellMetres);
            long lastX = GridMath.FloorDivide((long)Math.Ceiling(tile.OriginX + size) - 1, CellMetres);
            long lastZ = GridMath.FloorDivide((long)Math.Ceiling(tile.OriginZ + size) - 1, CellMetres);
            for (long cellX = firstX; cellX <= lastX; cellX++)
            for (long cellZ = firstZ; cellZ <= lastZ; cellZ++)
            {
                ulong hash = TerrainRecipe.CoordinateHash(placement, cellX, cellZ);
                long x = (cellX * CellMetres) + (long)(TerrainRecipe.HashUnit(hash) * CellMetres);
                long z = (cellZ * CellMetres) + (long)(TerrainRecipe.HashUnit(hash >> 7) * CellMetres);
                if (ground.At(x, z) is not var (height, top, geography)) continue;
                bool soil = top is BlockId.Grass or BlockId.Sand || (top == BlockId.Dirt && !recipe.InTownYard(x, z));
                if (!soil) continue;
                if (recipe.Forest.Plant(cellX, cellZ, recipe.Forest.Canopy(x, z, geography), geography, Forest.PlantsPerCell) is not FloraSpecies species)
                    continue;
                int variants = Math.Min(species.Variants.Count, (int)VariantsPerSpecies);
                int variant = (int)(TerrainRecipe.HashUnit(hash >> 13) * variants);
                float yaw = (float)(TerrainRecipe.HashUnit(hash >> 19) * Math.Tau);
                float scale = (float)(ScaleMinimum + ((ScaleMaximum - ScaleMinimum) * TerrainRecipe.HashUnit(hash >> 23)));
                samples.Records.Add(new VegetationRecord(Key(species.Id, variant),
                    new Vector3((float)(x + CellCentre - tile.OriginX), (float)(height - Sink), (float)(z + CellCentre - tile.OriginZ)), yaw, scale));
            }
        }
    }
}
