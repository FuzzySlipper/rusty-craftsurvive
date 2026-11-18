using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// What a cave or dungeon is dressed with beyond its rock (#10889: one owner for a separate space and for a cave under
/// the overworld): its living growth, waterfalls, lakes' surfaces, moss and ivy cards, a ruin's kit and its overgrowth,
/// the forest at its openings, an abyss's black floor and its water marks. Everything is built once from the layout, its
/// meshes and positions in the separate space's frame (<see cref="DungeonCollision.Origin"/> plus layout metres); where it
/// is drawn is one transform from that frame, given with each snapshot: none in a separate space, and in the walking
/// session the cave's place under its mouth, turned to its facing and carried with the world's origin.
/// </summary>
internal sealed class CaveDressing : IDisposable
{
    private readonly IEngineContext engine;
    private readonly DungeonLayout layout;

    /// <summary>The growth meshes, waterfall sheets, water surfaces, moss cards and the abyss floor, drawn as they were made.</summary>
    private readonly List<(MeshResource Mesh, Appearance Look)> meshes = [];
    private readonly Dictionary<EcologyKind, (Material Plain, Material Glowing)> sharedGrowthMaterials = [];
    private readonly List<Material> materials = [];
    private CaveWaterSurfaces? waterSurfaces;
    private CaveMossCards? mossCards;
    private DungeonKitLook? kit;
    private DungeonOvergrowth? overgrowth;

    /// <summary>The forest growing into the space's openings (#10129): the world's flora looks (not this dressing's to dispose), each placed.</summary>
    private readonly List<(Appearance Look, Transform Place)> forest = [];

    // The water marks placed, and where (in the separate space's frame) with their turn, for placing again when the frame moves.
    private readonly List<DungeonDrips.Mark> drips = [];
    private Transform dripsPlaced = new(new Vector3(float.NaN), Quaternion.Identity, Vector3.One);

    /// <summary>How rough a water mark leaves the surface it covers: wet stone a little glossy.</summary>
    private const float DripRoughness = 0.55f;

    // A chasm's depths (#10094): what the eye meets looking down it is black, not the sky drawn behind the world - an unlit
    // black floor this far below the space, far wider than the opening so no slant past it finds the sky, out of any light's reach.
    private const float AbyssDepth = 25f, AbyssWiden = 8f;
    private const int AbyssSides = 24;

    // The object ids of the meshes and the forest, within the dressing's range; the kit and overgrowth keep their own ranges.
    private const ulong ForestObjects = 0xE00, MeshObjects = 0x1000;
    private const int MostMeshes = 0x1000;

    /// <param name="kitDrawn">Whether a ruin's kit stands in for its building's voxels (a voxel look; a mesh look keeps them).</param>
    /// <remarks>Nothing is built yet: <see cref="Advance"/> builds it a part at a time, so a cave in the open world is dressed
    /// over a few updates rather than in one (#10889); a separate space behind its loading screen builds it all at once.</remarks>
    /// <param name="stoneShader">The kit stone shader the kit and overgrowth draw through, when one is kept open for every cave (#10889).</param>
    internal CaveDressing(IEngineContext engine, TerrainWorld terrain, DungeonLayout layout, bool kitDrawn, DungeonKitLook.Look kitLook, string? kitPaint,
        RenderResource? stoneShader = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentNullException.ThrowIfNull(terrain);
        stages = new Queue<Action>([
            () =>
            {
                // Each growth's mesh is made on an update of its own.
                foreach (MeshResourceCreateRequest request in CaveEcology.Meshes(engine, layout.Ecology, DungeonCollision.Origin, materials, sharedGrowthMaterials))
                {
                    pendingMeshes.Enqueue(request);
                }
            },
            () =>
            {
                if (layout.Waterfalls.Count > 0)
                {
                    Waterfalls = new CaveWaterfalls(engine, layout.Waterfalls);
                    foreach (MeshResourceCreateRequest request in Waterfalls.SheetRequests())
                    {
                        Add(request);
                    }
                }

                if (layout.WaterZones.Any(z => z.Kind is WaterZoneKind.Lake or WaterZoneKind.Pool))
                {
                    waterSurfaces = new CaveWaterSurfaces(engine);
                    foreach (MeshResourceCreateRequest request in waterSurfaces.Requests(layout.WaterZones, layout.Shape))
                    {
                        Add(request);
                    }
                }
            },
            () =>
            {
                if (layout.Kit.Count > 0 && kitDrawn)
                {
                    // The kit stands where its building's voxels were taken out (DungeonKit): it draws and collides in their place.
                    long kitStarted = Stopwatch.GetTimestamp();
                    kit = new DungeonKitLook(engine, terrain.Content, layout.Kit, kitLook, kitPaint, stoneShader);
                    Readout = string.Create(CultureInfo.InvariantCulture, $"kit={kit.Count} ({kit.Meshes} meshes; built {Stopwatch.GetElapsedTime(kitStarted).TotalMilliseconds:F0} ms)");
                }
            },
            () =>
            {
                if (layout.Overgrowth.Count > 0 && DungeonOvergrowth.Shown)
                {
                    // The wall overgrowth (#10096): drawn only; vent trunks collide where nothing else holds the static collision.
                    long grownStarted = Stopwatch.GetTimestamp();
                    overgrowth = new DungeonOvergrowth(engine, terrain.Content, layout.Overgrowth, stoneShader);
                    Readout = string.Create(CultureInfo.InvariantCulture,
                        $"{Readout} overgrowth={overgrowth.Count} ({overgrowth.Counts}; built {Stopwatch.GetElapsedTime(grownStarted).TotalMilliseconds:F0} ms)");
                }
            },
            () =>
            {
                if (layout.MossCards.Count > 0)
                {
                    mossCards = new CaveMossCards(engine);
                    if (mossCards.Request(layout.MossCards) is MeshResourceCreateRequest request)
                    {
                        Add(request);
                    }
                }

                foreach (ForestPlacement plant in layout.Forest)
                {
                    if (terrain.FloraLook(plant.Species, plant.Variant) is Appearance look)
                    {
                        forest.Add((look, new Transform(DungeonSpace.InSession(plant.At), Quaternion.CreateFromAxisAngle(Vector3.UnitY, plant.YawDegrees * MathF.PI / 180f),
                            new Vector3(plant.Scale))));
                    }
                }

                if (layout.Abyss is (Vector3 abyssAt, float abyssRadius))
                {
                    Add(AbyssFloor(DungeonSpace.InSession(abyssAt) - (Vector3.UnitY * AbyssDepth), abyssRadius * AbyssWiden));
                }

                drips.AddRange(layout.Drips.Take(ProductIds.DungeonDecals));
            },
        ]);
    }

    private readonly Queue<Action> stages;

    /// <summary>Builds the next part of the dressing; true once it is all built.</summary>
    internal bool Advance()
    {
        // The growths, the kit and the overgrowth build a mesh an update once made.
        long building = Stopwatch.GetTimestamp();
        if (pendingMeshes.TryDequeue(out MeshResourceCreateRequest growth))
        {
            Add(growth);
            Spent(building);
            return false;
        }

        if (kit is not null && !kitBuilt)
        {
            kitBuilt = kit.Advance();
            Spent(building);
            return false;
        }

        if (overgrowth is not null && !overgrowthBuilt)
        {
            overgrowthBuilt = overgrowth.Advance();
            Spent(building);
            return false;
        }

        if (stages.TryDequeue(out Action? stage))
        {
            long started = Stopwatch.GetTimestamp();
            stage();
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            BuildMilliseconds += ms;
            SlowestStageMilliseconds = Math.Max(SlowestStageMilliseconds, ms);
            stageTimes.Add(ms);
        }

        return Built;
    }

    private bool kitBuilt, overgrowthBuilt;
    private readonly Queue<MeshResourceCreateRequest> pendingMeshes = new();
    private readonly List<double> stageTimes = [];

    /// <summary>What the dressing came to once built, and each part's time (growth, water, kit made, overgrowth made, moss and forest).</summary>
    internal string BuiltReadout => string.Create(CultureInfo.InvariantCulture,
        $"kit={kit?.Count ?? 0} overgrowth={overgrowth?.Count ?? 0} meshes={meshes.Count} parts=[{string.Join(" ", stageTimes.Select(t => $"{t:F0}"))}]ms");

    private void Spent(long from)
    {
        double ms = Stopwatch.GetElapsedTime(from).TotalMilliseconds;
        BuildMilliseconds += ms;
        SlowestStageMilliseconds = Math.Max(SlowestStageMilliseconds, ms);
    }

    /// <summary>Whether every part is built.</summary>
    internal bool Built => stages.Count == 0 && pendingMeshes.Count == 0 && (kit is null || kitBuilt) && (overgrowth is null || overgrowthBuilt);

    /// <summary>Builds whatever is left at once (behind a loading screen).</summary>
    internal void Finish()
    {
        while (!Advance())
        {
        }
    }

    /// <summary>
    /// Reads, off the update, the models a cave's kit and overgrowth will draw (#10889), so dressing it later only makes
    /// meshes on the update.
    /// </summary>
    internal static void Preload(ProductContent content, DungeonLayout layout)
    {
        CaveEcology.Prepare(layout.Ecology, DungeonCollision.Origin);
        CaveWaterSurfaces.Prepare(layout.WaterZones, layout.Shape);
        DungeonKitLook.Preload(content, layout.Kit);
        if (DungeonOvergrowth.Shown)
        {
            DungeonOvergrowth.Preload(content, layout.Overgrowth);
        }
    }

    /// <summary>A chasm's waterfalls (#10094); null for a cave without them.</summary>
    internal CaveWaterfalls? Waterfalls { get; private set; }

    /// <summary>What the kit and overgrowth came to and cost.</summary>
    internal string Readout { get; private set; } = string.Empty;

    /// <summary>How long building the dressing took so far, in milliseconds, and its slowest part (one update's share).</summary>
    internal double BuildMilliseconds { get; private set; }

    internal double SlowestStageMilliseconds { get; private set; }

    /// <summary>How many water marks are placed.</summary>
    internal int Drips => drips.Count;

    internal int MeshCount => meshes.Count;

    internal int KitCount => kit?.Count ?? 0;

    /// <summary>Retunes the kit's stone shader now (a study).</summary>
    internal void TuneKit(DungeonKitLook.Look look) => kit?.Tune(look);

    /// <summary>Retunes the kit's glowing pieces to the glow intensity now (a study).</summary>
    internal void TuneGlow() => kit?.TuneGlow();

    /// <summary>
    /// What the dressing adds to the snapshot, placed by a transform from the separate space's frame (identity there), as
    /// seen from an eye in that same frame (the overgrowth thins with distance).
    /// </summary>
    internal IEnumerable<AppearanceFact> Facts(Vector3 eye, Transform place)
    {
        IEnumerable<AppearanceFact> made = [
            .. kit?.Facts ?? [],
            .. overgrowth?.Facts(eye) ?? [],
            .. meshes.Take(MostMeshes).Select((mesh, index) => new AppearanceFact(ProductIds.DungeonRockObject + MeshObjects + (ulong)index, false, 0UL,
                new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One), mesh.Look, true, RenderLayer.Scene)),
            .. forest.Select((plant, index) => new AppearanceFact(ProductIds.DungeonRockObject + ForestObjects + (ulong)index, false, 0UL,
                plant.Place, plant.Look, true, RenderLayer.Scene))];
        return IsIdentity(place) ? made : made.Select(fact => fact with { Transform = Compose(place, fact.Transform) });
    }

    /// <summary>
    /// Places the water marks (#10092), projected decals from the scene's shared atlas, by a transform from the separate
    /// space's frame; again only when it moved.
    /// </summary>
    internal void PlaceDrips(Transform place)
    {
        if (place.Equals(dripsPlaced))
        {
            return;
        }

        dripsPlaced = place;
        for (int i = 0; i < drips.Count; i++)
        {
            DungeonDrips.Mark mark = drips[i];
            Transform at = Compose(place, new Transform(DungeonSpace.InSession(mark.Centre), mark.Turn, Vector3.One));
            engine.CameraView.SetDecal(new DecalRequest(ProductIds.DungeonDecalBase + (ulong)i, at.Translation, at.Rotation, mark.Size, mark.Region,
                new Color(1f, 1f, 1f, 1f), Roughness: DripRoughness, NormalStrength: 0f, Emission: Vector3.Zero, AngleFalloff: true, OnVegetation: false,
                OnAnimatedMeshes: false, LifeSeconds: 0f, FadeSeconds: 0f));
        }
    }

    /// <summary>The kit's and the vent trunks' collision as the space's own static collision (a separate space holds nothing else).</summary>
    internal void Collide(SpatialSession session)
    {
        if (kit is not null)
        {
            kit.Collide(session);
        }
        else if (overgrowth is { Collides: true })
        {
            overgrowth.Collide(session);
        }
    }

    /// <summary>
    /// The kit's collision added to a shared composition (the walking session's, #10889), each instance placed by a
    /// transform from the separate space's frame into world metres.
    /// </summary>
    internal void AddCollision(List<StaticMeshAsset> assets, List<Vector3> vertices, List<Triangle> triangles, List<StaticMeshInstance> instances, Transform place) =>
        kit?.AddCollision(assets, vertices, triangles, instances, transform => Compose(place, transform));

    /// <summary>A transform applied after another: the dressing's place after a piece's own.</summary>
    internal static Transform Compose(Transform outer, Transform inner) => new(
        outer.Translation + Vector3.Transform(inner.Translation * outer.Scale, outer.Rotation),
        Quaternion.Normalize(outer.Rotation * inner.Rotation),
        outer.Scale * inner.Scale);

    private static bool IsIdentity(Transform place) =>
        place.Translation == Vector3.Zero && place.Rotation == Quaternion.Identity && place.Scale == Vector3.One;

    private void Add(MeshResourceCreateRequest request)
    {
        MeshResource mesh = engine.Graphics.CreateMeshResource(request);
        meshes.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh)));
    }

    private MeshResourceCreateRequest AbyssFloor(Vector3 centre, float radius)
    {
        Material black = engine.Graphics.CreateMaterial(new MaterialRequest(new Color(0f, 0f, 0f, 1f), default, 1f, new Color(1f, 1f, 1f, 1f), Vector3.Zero, 0f, false));
        materials.Add(black);
        Vector3[] positions = new Vector3[AbyssSides + 1];
        positions[0] = centre;
        for (int i = 0; i < AbyssSides; i++)
        {
            float a = i * MathF.Tau / AbyssSides;
            positions[i + 1] = centre + new Vector3(MathF.Cos(a) * radius, 0f, MathF.Sin(a) * radius);
        }

        uint[] indices = new uint[AbyssSides * 3];
        for (int i = 0; i < AbyssSides; i++)
        {
            // Facing up, toward an eye looking down the chasm.
            indices[(i * 3) + 0] = 0;
            indices[(i * 3) + 1] = (uint)(((i + 1) % AbyssSides) + 1);
            indices[(i * 3) + 2] = (uint)(i + 1);
        }

        return new MeshResourceCreateRequest(positions, Enumerable.Repeat(Vector3.UnitY, positions.Length).ToArray(), new Vector2[positions.Length], ReadOnlyMemory<Color>.Empty, indices,
            new[] { new MeshGroup(0, 0, (uint)indices.Length) }, new[] { new MeshMaterialBinding(0, black) });
    }

    public void Dispose()
    {
        if (!float.IsNaN(dripsPlaced.Translation.X))
        {
            for (int i = 0; i < drips.Count; i++)
            {
                engine.CameraView.RemoveDecal(new DecalRemoval(ProductIds.DungeonDecalBase + (ulong)i));
            }
        }

        foreach ((MeshResource mesh, Appearance look) in meshes)
        {
            look.Dispose();
            mesh.Dispose();
        }

        meshes.Clear();
        // After the meshes drawn in their materials.
        Waterfalls?.Dispose();
        Waterfalls = null;
        waterSurfaces?.Dispose();
        waterSurfaces = null;
        mossCards?.Dispose();
        mossCards = null;
        kit?.Dispose();
        kit = null;
        overgrowth?.Dispose();
        overgrowth = null;
        foreach (Material material in materials)
        {
            material.Dispose();
        }

        materials.Clear();
    }
}
