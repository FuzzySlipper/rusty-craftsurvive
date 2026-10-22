using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The overgrowth drawn (#10096): each kind's prop mesh (content/game/models/dungeon-overgrowth, painted by the texturing
/// pillar) loaded once, one appearance per kind, an appearance fact for every placement - instanced, as the kit is - under
/// the kit stone shader. The vent orchard's pieces have a far level of detail each, drawn from so far from the eye. The
/// vent trunks collide as simple prisms. The space owns this for as long as it is loaded.
/// </summary>
internal sealed class DungeonOvergrowth : IDisposable
{
    private const string ShaderPath = "shaders/kit-stone.wgsl";

    /// <summary>The kit stone shader's keyword for the swaying overgrowth (its displace stage).</summary>
    private const string SwayKeyword = "SWAY";

    /// <summary>
    /// How a swaying kind moves (kit-stone.wgsl's SWAY): the draft's reach at its freest vertex, how near the traveller's
    /// body a vertex is pushed back to, and how much a pushed vertex rises (above 0) or presses down (below 0) per metre
    /// it is pushed, all metres. The ribbons swing slowly through a hand's breadth and part round the traveller, rising a
    /// little as they swing aside; the grass stirs and bows under the feet.
    /// </summary>
    private readonly record struct Sway(float Draft, float Reach, float Lift);

    private static readonly Sway Ribbons = new(0.16f, 0.55f, 0.3f);
    private static readonly Sway Grass = new(0.03f, 0.4f, -0.4f);

    // The veil fans breathe in the draft (their lace weighted up from the hub), high over the traveller's reach; the comb
    // fans stir and bow as the grass does.
    private static readonly Sway Fans = new(0.12f, 0f, 0f);
    private static readonly Sway Combs = new(0.04f, 0.4f, -0.4f);

    private static Sway? Sways(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.RibbonArch or OvergrowthKind.RibbonArchWide or OvergrowthKind.RibbonCurtain => Ribbons,
        OvergrowthKind.GrassTuft or OvergrowthKind.GrassStrip => Grass,
        OvergrowthKind.VeilFan or OvergrowthKind.VeilFanGreat => Fans,
        OvergrowthKind.VeilComb => Combs,
        _ => null,
    };

    /// <summary>Wet leaves are a little less matte than the stone.</summary>
    private const float Roughness = 0.75f;

    /// <summary>As the kit's textures: 512 texels across (32 texels/m over the 16 m an atlas spans); the giant vents' are 1024 (overgrowth_paint.sh).</summary>
    private const float TextureTexels = 512f, GiantTextureTexels = 1024f;

    // kit-stone.wgsl's stain: none on the leaves (their tint is white, so no dampness either), as the kit's facing limits.
    private const float MossStrength = 0f, StainDepth = 1f, SideShare = 0.85f, LowestFacing = -0.25f;

    /// <summary>How far an overgrowth is turned about its surface's normal, at most, either way (radians), so repeats differ.</summary>
    private const float MostTwist = 0.12f;

    private readonly IEngineContext engine;
    private readonly RenderResource shader;
    private RenderResource? swayShader;
    private readonly List<RenderResource> textures = [];
    private readonly List<Material> materials = [];
    private readonly Dictionary<OvergrowthKind, (MeshResource Mesh, Appearance Look)> kinds = [];
    private readonly Dictionary<OvergrowthKind, (MeshResource Mesh, Appearance Look, float From)> far = [];
    private readonly List<(OvergrowthKind Kind, Transform Place)> placed = [];

    internal DungeonOvergrowth(IEngineContext engine, ProductContent content, IReadOnlyList<OvergrowthPlacement> placements)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentNullException.ThrowIfNull(content);
        shader = engine.Graphics.OpenResource(new RenderResourceRequest(ShaderPath)).Handle;
        foreach (OvergrowthKind kind in placements.Select(p => p.Kind).Distinct())
        {
            (MeshResourceCreateRequest model, string[] parts) = Model(content, ModelPath(kind));
            float texels = kind is OvergrowthKind.VentGiant or OvergrowthKind.VentGiantCluster or OvergrowthKind.GroveHanger or OvergrowthKind.FunnelGiant or OvergrowthKind.RibbonArch or OvergrowthKind.RibbonArchWide or OvergrowthKind.VeilFan or OvergrowthKind.VeilFanGreat or OvergrowthKind.CushionGreat ? GiantTextureTexels : TextureTexels;
            bool glows = kind is OvergrowthKind.SteppeTuft or OvergrowthKind.SteppeTuftLarge or OvergrowthKind.SteppeSpire or OvergrowthKind.GroveHanger or OvergrowthKind.GroveHangerSmall;
            MeshMaterialBinding[] bindings = [.. parts.Select((texture, slot) => new MeshMaterialBinding((uint)slot, Material(texture, texels, glows ? Emission(texture) : null, Sways(kind))))];
            MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(model.Positions, model.Normals, model.Uvs, model.Colors,
                model.Indices, model.Groups, bindings));
            kinds[kind] = (mesh, engine.Graphics.CreateMeshAppearance(mesh));
            if (FarLevel(kind) is (string farPath, float from))
            {
                // The far level is cut from the near piece's painted mesh: its own uvs into the same texture, so the same materials.
                MeshResourceCreateRequest farModel = Model(content, farPath).Mesh;
                MeshResource farMesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(farModel.Positions, farModel.Normals, farModel.Uvs,
                    farModel.Colors, farModel.Indices, farModel.Groups, bindings));
                far[kind] = (farMesh, engine.Graphics.CreateMeshAppearance(farMesh), from);
            }
        }

        foreach (OvergrowthPlacement p in placements.Take(ProductIds.DungeonOvergrowthObjects))
        {
            placed.Add((p.Kind, new Transform(DungeonCollision.InSession(p.At), Turn(p), new Vector3(p.Scale))));
        }
    }

    /// <summary>
    /// How a placement is turned: its frame's +z to its outward and +y to its up - a floor piece square to its up and turned
    /// about it, a wall piece facing its outward exactly - and twisted a little about its up by its pick.
    /// </summary>
    private static Quaternion Turn(OvergrowthPlacement p)
    {
        Vector3 up = Vector3.Normalize(p.Up ?? Vector3.UnitY), facing = Vector3.Normalize(p.Out);
        Vector3 y, z;
        if (OvergrowthPlacement.OnFloor(p.Kind) || MathF.Abs(Vector3.Dot(up, facing)) > 0.98f)
        {
            y = up;
            Vector3 flat = facing - (y * Vector3.Dot(facing, y));
            z = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.Normalize(Vector3.Cross(Vector3.UnitX, y));
        }
        else
        {
            z = facing;
            y = Vector3.Normalize(up - (z * Vector3.Dot(up, z)));
        }

        // System.Numerics transforms row vectors: the matrix's rows are where the frame's axes go.
        Vector3 x = Vector3.Cross(y, z);
        Quaternion frame = Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f));
        // Twisted about its up: a drape keeps hanging and a mound keeps its foot on the floor.
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(y, (p.Pick - 0.5f) * 2f * MostTwist) * frame);
    }

    /// <summary>A kind's prop mesh.</summary>
    internal static string ModelPath(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.VineDrape => "models/dungeon-overgrowth/vine-drape.prop-mesh.json",
        OvergrowthKind.VineDrapeShort => "models/dungeon-overgrowth/vine-drape-short.prop-mesh.json",
        OvergrowthKind.IvyMound => "models/dungeon-overgrowth/ivy-mound.prop-mesh.json",
        OvergrowthKind.IvyClump => "models/dungeon-overgrowth/ivy-clump.prop-mesh.json",
        OvergrowthKind.RootTangle => "models/dungeon-overgrowth/root-tangle.prop-mesh.json",
        OvergrowthKind.GrassTuft => "models/dungeon-overgrowth/grass-tuft.prop-mesh.json",
        OvergrowthKind.GrassStrip => "models/dungeon-overgrowth/grass-strip.prop-mesh.json",
        OvergrowthKind.VentTall => "models/dungeon-overgrowth/vent-tall.prop-mesh.json",
        OvergrowthKind.VentCluster => "models/dungeon-overgrowth/vent-cluster.prop-mesh.json",
        OvergrowthKind.VentShort => "models/dungeon-overgrowth/vent-short.prop-mesh.json",
        OvergrowthKind.VentBed => "models/dungeon-overgrowth/vent-bed.prop-mesh.json",
        OvergrowthKind.VentGiant => "models/dungeon-overgrowth/vent-giant.prop-mesh.json",
        OvergrowthKind.VentGiantCluster => "models/dungeon-overgrowth/vent-giant-cluster.prop-mesh.json",
        OvergrowthKind.SeepChimney => "models/dungeon-overgrowth/seep-chimney.prop-mesh.json",
        OvergrowthKind.SeepShells => "models/dungeon-overgrowth/seep-shells.prop-mesh.json",
        OvergrowthKind.SeepMat => "models/dungeon-overgrowth/seep-mat.prop-mesh.json",
        OvergrowthKind.GroveHanger => "models/dungeon-overgrowth/grove-hanger.prop-mesh.json",
        OvergrowthKind.GroveHangerSmall => "models/dungeon-overgrowth/grove-hanger-small.prop-mesh.json",
        OvergrowthKind.GroveRootweb => "models/dungeon-overgrowth/grove-rootweb.prop-mesh.json",
        OvergrowthKind.GroveSacs => "models/dungeon-overgrowth/grove-sacs.prop-mesh.json",
        OvergrowthKind.GroveLitter => "models/dungeon-overgrowth/grove-litter.prop-mesh.json",
        OvergrowthKind.GroveCatchers => "models/dungeon-overgrowth/grove-catchers.prop-mesh.json",
        OvergrowthKind.GroveMound => "models/dungeon-overgrowth/grove-mound.prop-mesh.json",
        OvergrowthKind.FunnelGiant => "models/dungeon-overgrowth/funnel-giant.prop-mesh.json",
        OvergrowthKind.FunnelTall => "models/dungeon-overgrowth/funnel-tall.prop-mesh.json",
        OvergrowthKind.FunnelSmall => "models/dungeon-overgrowth/funnel-small.prop-mesh.json",
        OvergrowthKind.FunnelUnfurled => "models/dungeon-overgrowth/funnel-unfurled.prop-mesh.json",
        OvergrowthKind.FunnelDrinkers => "models/dungeon-overgrowth/funnel-drinkers.prop-mesh.json",
        OvergrowthKind.FunnelDebris => "models/dungeon-overgrowth/funnel-debris.prop-mesh.json",
        OvergrowthKind.SteppeTuft => "models/dungeon-overgrowth/steppe-tuft.prop-mesh.json",
        OvergrowthKind.SteppeTuftLarge => "models/dungeon-overgrowth/steppe-tuft-large.prop-mesh.json",
        OvergrowthKind.SteppeBack => "models/dungeon-overgrowth/steppe-back.prop-mesh.json",
        OvergrowthKind.SteppeGrit => "models/dungeon-overgrowth/steppe-grit.prop-mesh.json",
        OvergrowthKind.SteppeSpire => "models/dungeon-overgrowth/steppe-spire.prop-mesh.json",
        OvergrowthKind.RibbonArch => "models/dungeon-overgrowth/ribbon-arch.prop-mesh.json",
        OvergrowthKind.RibbonArchWide => "models/dungeon-overgrowth/ribbon-arch-wide.prop-mesh.json",
        OvergrowthKind.RibbonCurtain => "models/dungeon-overgrowth/ribbon-curtain.prop-mesh.json",
        OvergrowthKind.RibbonShed => "models/dungeon-overgrowth/ribbon-shed.prop-mesh.json",
        OvergrowthKind.VeilFan => "models/dungeon-overgrowth/veil-fan.prop-mesh.json",
        OvergrowthKind.VeilFanGreat => "models/dungeon-overgrowth/veil-fan-great.prop-mesh.json",
        OvergrowthKind.VeilComb => "models/dungeon-overgrowth/veil-comb.prop-mesh.json",
        OvergrowthKind.VeilDrift => "models/dungeon-overgrowth/veil-drift.prop-mesh.json",
        OvergrowthKind.VeilPuffs => "models/dungeon-overgrowth/veil-puffs.prop-mesh.json",
        OvergrowthKind.CushionHummock => "models/dungeon-overgrowth/cushion-hummock.prop-mesh.json",
        OvergrowthKind.CushionHummockRust => "models/dungeon-overgrowth/cushion-hummock-rust.prop-mesh.json",
        OvergrowthKind.CushionGreat => "models/dungeon-overgrowth/cushion-great.prop-mesh.json",
        OvergrowthKind.CushionSplit => "models/dungeon-overgrowth/cushion-split.prop-mesh.json",
        OvergrowthKind.CushionMat => "models/dungeon-overgrowth/cushion-mat.prop-mesh.json",
        OvergrowthKind.CushionBuds => "models/dungeon-overgrowth/cushion-buds.prop-mesh.json",
        _ => "models/dungeon-overgrowth/mushrooms.prop-mesh.json",
    };

    // The vent orchard's far levels (#10096; overgrowth_lod.py): each plume thinned to every other frond, its barbs gone and
    // the fronds kept broadened to hold its mass, with the near piece's paint; the tubes and crust kept. Drawn from so many
    // metres from the eye, by kind: from where the broader fronds no longer show as blades (nearer for the small pieces,
    // further for the giants).
    private const float VentFarFrom = 10f, SmallVentFarFrom = 6f, GiantVentFarFrom = 20f;

    // The inverted grove's hangers' far levels: every third hanging filament kept, the bell, trunk and roots whole (the bell
    // holds the crown's mass), drawn from so far: where single filaments no longer read.
    private const float HangerFarFrom = 14f, SmallHangerFarFrom = 9f;

    /// <summary>A kind's far level of detail and the distance it is drawn from, metres; none for most kinds.</summary>
    internal static (string Path, float From)? FarLevel(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.VentTall => ("models/dungeon-overgrowth/vent-tall-far.prop-mesh.json", VentFarFrom),
        OvergrowthKind.GroveHanger => ("models/dungeon-overgrowth/grove-hanger-far.prop-mesh.json", HangerFarFrom),
        OvergrowthKind.GroveHangerSmall => ("models/dungeon-overgrowth/grove-hanger-small-far.prop-mesh.json", SmallHangerFarFrom),
        OvergrowthKind.VentCluster => ("models/dungeon-overgrowth/vent-cluster-far.prop-mesh.json", VentFarFrom),
        OvergrowthKind.VentShort => ("models/dungeon-overgrowth/vent-short-far.prop-mesh.json", SmallVentFarFrom),
        OvergrowthKind.VentBed => ("models/dungeon-overgrowth/vent-bed-far.prop-mesh.json", SmallVentFarFrom),
        OvergrowthKind.VentGiant => ("models/dungeon-overgrowth/vent-giant-far.prop-mesh.json", GiantVentFarFrom),
        OvergrowthKind.VentGiantCluster => ("models/dungeon-overgrowth/vent-giant-cluster-far.prop-mesh.json", GiantVentFarFrom),
        _ => null,
    };

    // Each kind's model parsed once for the process (as the kit's): every dungeon reuses its geometry with its own materials.
    private static readonly Dictionary<string, (MeshResourceCreateRequest Mesh, string[] Textures)> Models = [];

    private static (MeshResourceCreateRequest Mesh, string[] Textures) Model(ProductContent content, string path)
    {
        if (!Models.TryGetValue(path, out (MeshResourceCreateRequest Mesh, string[] Textures) model))
        {
            List<string> parts = [];
            MeshResourceCreateRequest mesh = PropMesh.Read(content, path, (role, texture) =>
            {
                parts.Add(texture ?? throw new InvalidOperationException($"Overgrowth model {path} has no texture for {role}."));
                return null!;
            });
            model = (mesh, [.. parts]);
            Models[path] = model;
        }

        return model;
    }

    /// <summary>A material for an overgrowth texture: nearest with mipmaps, under the kit stone shader at the kit's settings.</summary>
    /// <summary>A glowing piece's emission map beside its texture (overgrowth_emission.py: its tips cut from it).</summary>
    private static string Emission(string texture) => $"{Path.GetDirectoryName(texture)!.Replace('\\', '/')}/emission/{Path.GetFileName(texture)}";

    /// <summary>
    /// How brightly a glowing piece's tips shine through their emission map (the glass steppe's beads, the grove's amber
    /// tips): enough to read as the cave's own scattered light, and to lend it through the probe volume, as the kit's inlays do.
    /// </summary>
    private const float TipGlow = 1.2f;

    private Material Material(string path, float texels, string? emissionPath = null, Sway? sway = null)
    {
        RenderResource texture = engine.Graphics.OpenResource(new RenderResourceRequest(path, TextureFilter.NearestMipmapped, TextureWrap.Repeat)).Handle;
        textures.Add(texture);
        RenderResource? emission = null;
        if (emissionPath is not null)
        {
            emission = engine.Graphics.OpenResource(new RenderResourceRequest(emissionPath, TextureFilter.NearestMipmapped, TextureWrap.Repeat)).Handle;
            textures.Add(emission);
        }

        DungeonKitLook.Look look = DungeonKitLook.Look.Default;
        Color white = new(1f, 1f, 1f, 1f);
        Material material = engine.Graphics.CreateMaterial(new MaterialRequest(white, texture, Roughness, white, emission is null ? Vector3.Zero : Vector3.One,
            emission is null ? 0f : TipGlow, false)
        {
            EmissionMap = emission is null ? default(RenderResourceReference) : emission,
            Shader = sway is Sway moves
                ? new MaterialShader(SwayShader, new Vector4(texels, look.FacetStrength, look.FacetSteps, look.FacetCoherence),
                    new Vector4(look.CalmFrom, look.CalmFull, look.CalmMip, look.Calm), new Vector4(moves.Draft, moves.Reach, moves.Lift, MossStrength),
                    new Vector4(StainDepth, SideShare, LowestFacing, PlayerConstants.StandingEyeHeight), texture, default(RenderResourceReference),
                    moves.Draft + (moves.Reach * (1f + MathF.Abs(moves.Lift))))
                : new MaterialShader(shader, new Vector4(texels, look.FacetStrength, look.FacetSteps, look.FacetCoherence),
                    new Vector4(look.CalmFrom, look.CalmFull, look.CalmMip, look.Calm), new Vector4(0f, 0f, 0f, MossStrength),
                    new Vector4(StainDepth, SideShare, LowestFacing, 0f), texture, default(RenderResourceReference)),
        });
        materials.Add(material);
        return material;
    }

    /// <summary>The kit stone shader under its SWAY keyword, opened when a swaying kind is first drawn.</summary>
    private RenderResource SwayShader => swayShader ??= engine.Graphics.OpenResource(new RenderResourceRequest(ShaderPath) { ShaderKeywords = SwayKeyword }).Handle;

    /// <summary>Whether a dungeon loaded from now draws its overgrowth (a study's before and after; on by default).</summary>
    internal static bool Shown { get; set; } = true;

    internal int Count => placed.Count;

    /// <summary>How many of each kind are drawn, for readouts.</summary>
    internal string Counts => string.Join(" ", placed.GroupBy(p => p.Kind).Select(g => $"{g.Key}={g.Count()}"));

    /// <summary>Every placement drawn as seen from an eye (session coordinates): a piece with a far level, at least its distance away, at that level.</summary>
    internal IEnumerable<AppearanceFact> Facts(Vector3 eye) => placed.Select((p, index) => new AppearanceFact(ProductIds.DungeonOvergrowth + (ulong)index, false, 0UL,
        p.Place, Look(p.Kind, Vector3.DistanceSquared(p.Place.Translation, eye)), true, RenderLayer.Scene, ShadowCasting.Cast));

    private Appearance Look(OvergrowthKind kind, float distanceSquared) =>
        far.TryGetValue(kind, out (MeshResource Mesh, Appearance Look, float From) level) && distanceSquared >= level.From * level.From ? level.Look : kinds[kind].Look;

    // The vent trunks' colliders (#10096): each trunk a closed prism of so many sides, as overgrowth_authored.py builds the
    // tubes (their feet and radii, metres, in the piece's frame); the plumes, beds and the rest do not collide. The asset and
    // instance ids, clear of the kit's and the rock's.
    private const int TrunkSides = 8;
    private const ulong TrunkAssetBase = 0x5000UL, TrunkInstanceBase = 0x30_0000UL;

    private static (float X, float Z, float Radius, float Height)[] Trunks(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.VentTall => [(0f, 0f, 0.32f, 2.6f)],
        OvergrowthKind.VentCluster => [(0f, 0f, 0.33f, 3f), (0.42f, 0.2f, 0.27f, 2.1f), (-0.3f, 0.38f, 0.22f, 1.6f), (0.1f, -0.42f, 0.2f, 1.4f)],
        OvergrowthKind.VentGiant => [(0f, 0f, 1.1f, 10f)],
        OvergrowthKind.VentGiantCluster => [(0f, 0f, 1.15f, 11f), (1.6f, 0.8f, 0.95f, 8f), (-1.1f, 1.4f, 0.78f, 6f)],
        OvergrowthKind.SeepChimney => [(0f, 0f, 0.45f, 2.6f), (0.55f, 0.25f, 0.32f, 1.6f), (-0.35f, 0.5f, 0.27f, 1f)],
        // A funnel collides as its stem, up to where its cup flares out over the player's head.
        OvergrowthKind.FunnelGiant => [(0f, 0f, 0.85f, 3f)],
        OvergrowthKind.FunnelTall => [(0f, 0f, 0.5f, 2f)],
        OvergrowthKind.SteppeSpire => [(0f, 0f, 0.4f, 4f)],
        // An arch collides at its feet against the walls (its ribbons the player pushes through).
        OvergrowthKind.RibbonArch => [(-2.9f, 0f, 0.45f, 2f), (2.9f, 0f, 0.45f, 2f)],
        OvergrowthKind.RibbonArchWide => [(-4.35f, 0f, 0.6f, 2.5f), (4.35f, 0f, 0.6f, 2.5f)],
        OvergrowthKind.VeilFan => [(0f, 0f, 0.26f, 3f)],
        OvergrowthKind.VeilFanGreat => [(0f, 0f, 0.34f, 4.6f)],
        // The barrens' domes collide as low prisms inside their swell: the small ones a step the traveller climbs over.
        OvergrowthKind.CushionHummock or OvergrowthKind.CushionHummockRust => [(0f, 0f, 0.95f, 0.85f)],
        OvergrowthKind.CushionSplit => [(0f, 0f, 1f, 0.95f)],
        OvergrowthKind.CushionGreat => [(0f, 0f, 2f, 2.3f)],
        _ => [],
    };

    /// <summary>Whether any placement has a trunk that collides.</summary>
    internal bool Collides => placed.Any(p => Trunks(p.Kind).Length > 0);

    /// <summary>
    /// The vent trunks as the space's static collision, one asset per kind and an instance per placement, as the kit
    /// collides; only where nothing else holds the space's static collision (a natural cave's rock is voxels).
    /// </summary>
    internal void Collide(SpatialSession session)
    {
        List<OvergrowthKind> kinds = [.. placed.Select(p => p.Kind).Where(k => Trunks(k).Length > 0).Distinct()];
        List<Vector3> vertices = [];
        List<Triangle> triangles = [];
        List<StaticMeshAsset> assets = [];
        foreach (OvergrowthKind kind in kinds)
        {
            uint firstVertex = (uint)vertices.Count, firstTriangle = (uint)triangles.Count;
            foreach ((float x, float z, float radius, float height) in Trunks(kind))
            {
                uint b = (uint)vertices.Count - firstVertex;
                for (int k = 0; k < TrunkSides; k++)
                {
                    float a = MathF.Tau * k / TrunkSides;
                    Vector3 rim = new(x + (radius * MathF.Cos(a)), 0f, z + (radius * MathF.Sin(a)));
                    vertices.Add(rim);
                    vertices.Add(rim + new Vector3(0f, height, 0f));
                }

                for (int k = 0; k < TrunkSides; k++)
                {
                    uint low = b + (uint)(2 * k), high = low + 1, nextLow = b + (uint)(2 * ((k + 1) % TrunkSides)), nextHigh = nextLow + 1;
                    triangles.Add(new Triangle(low, nextLow, nextHigh));
                    triangles.Add(new Triangle(low, nextHigh, high));
                    if (k > 0 && k < TrunkSides - 1)
                    {
                        // The caps, fans from the first rim vertex.
                        triangles.Add(new Triangle(b, nextLow, low));
                        triangles.Add(new Triangle(b + 1, high, nextHigh));
                    }
                }
            }

            assets.Add(new StaticMeshAsset(TrunkAssetBase + (ulong)assets.Count, firstVertex, (uint)vertices.Count - firstVertex, firstTriangle, (uint)triangles.Count - firstTriangle));
        }

        engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(session, assets.ToArray(), vertices.ToArray(), triangles.ToArray(),
            placed.Where(p => Trunks(p.Kind).Length > 0)
                .Select((p, i) => new StaticMeshInstance(TrunkInstanceBase + (ulong)i, TrunkAssetBase + (ulong)kinds.IndexOf(p.Kind), p.Place)).ToArray()));
    }

    /// <summary>Disposes the meshes after the snapshot that last named them, then their materials.</summary>
    public void Dispose()
    {
        foreach ((MeshResource mesh, Appearance look) in kinds.Values.Concat(far.Values.Select(level => (level.Mesh, level.Look))))
        {
            look.Dispose();
            mesh.Dispose();
        }

        kinds.Clear();
        far.Clear();
        foreach (Material material in materials)
        {
            material.Dispose();
        }

        materials.Clear();
        foreach (RenderResource texture in textures)
        {
            texture.Dispose();
        }

        textures.Clear();
        shader.Dispose();
        swayShader?.Dispose();
    }
}
