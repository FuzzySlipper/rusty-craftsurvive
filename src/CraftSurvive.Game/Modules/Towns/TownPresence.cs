using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Towns;

/// <summary>A service building as placed: its stable id, entrance and selection box in world metres.</summary>
internal sealed record PlacedService(ulong Id, string Service, string Name, (double X, double Y, double Z) Entrance,
    (double X, double Y, double Z) Min, (double X, double Y, double Z) Max, TownView? Close);

/// <summary>
/// A town standing in the world (#9895): the nearest town's kit pieces on its pad, its paving and
/// approach, ferns, hanging signs, lamps and chimney smoke. One town is placed at a time (settlements
/// stand days apart) while the traveller is within reach; it is seen from outside its bubble and is
/// the scene of a visit. The kit's meshes are opened once and shared; each timber palette is one
/// tinted appearance per mesh.
/// </summary>
internal sealed class TownPresence : IDisposable
{
    /// <summary>How near (m) a town must be for it to stand in the world.</summary>
    internal const double ReachMetres = 700;

    private const float LampIntensity = 7, LampRange = 7, LampDecay = 2;
    private const float SignAmplitude = .055f, SignRadiansPerSecond = 1.35f;
    private const float PavingLift = .03f, PathLift = .04f, PavingTileMetres = 3.4f, PavingRoughness = .9f;
    private const float PathStepMetres = 2f;
    private const int CourtyardSteps = 8;
    private const string PavingTexture = TownContent.Root + "kit/textures/masonry.png";
    private const string SmokeSprite = "textures/fire-smoke.png";
    private const ulong SignOffset = 0x200, GroundOffset = 0x300, FernOffset = 0x340;
    private const float SmokeRate = 2, SmokeLifeMin = 3.5f, SmokeLifeMax = 5.5f, SmokeSoftness = .5f;
    private const uint SmokeParticles = 24;
    private const ulong ScatterSalt = 0x9E3779B97F4A7C15UL;
    private static readonly Vector3 LampColour = new(1, .63f, .27f);
    private static readonly Color White = new(1, 1, 1, 1);
    private static readonly Vector3 SmokeVelocityMin = new(.10f, .5f, -.09f), SmokeVelocityMax = new(.26f, .8f, .08f);
    private static readonly Vector3 SmokeAcceleration = new(.04f, .035f, 0);
    private static readonly PresentationParticleScalarKey[] SmokeSize = [new(0, .32f), new(.5f, .85f), new(1, 1.7f)];
    private static readonly PresentationParticleColorKey[] SmokeColour =
        [new(0, new Color(.52f, .55f, .55f, .48f)), new(.45f, new Color(.58f, .61f, .62f, .32f)), new(1, new Color(.58f, .61f, .62f, 0))];

    private sealed record Placement(ulong Id, Appearance Look, (double X, double Y, double Z) At, Quaternion Turn, float Scale = 1);

    private sealed record Sign(ulong Id, Appearance Look, (double X, double Y, double Z) Pivot, Vector3 Axis, Quaternion Turn);

    private sealed class Placed(TownSite site)
    {
        internal TownSite Site { get; } = site;
        internal List<Placement> Pieces { get; } = [];
        internal List<Sign> Signs { get; } = [];
        internal List<(double X, double Y, double Z)> Lamps { get; } = [];
        internal List<(double X, double Y, double Z)> Chimneys { get; } = [];
        internal List<PlacedService> Services { get; } = [];
        internal List<((double X, double Y, double Z) Min, (double X, double Y, double Z) Max)> Blockers { get; } = [];
        internal List<(MeshResource Mesh, Appearance Look, (double X, double Y, double Z) Origin)> Ground { get; } = [];
        internal List<Light> Lights { get; } = [];
        internal List<PresentationEmitter> Smoke { get; } = [];
    }

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly TownContent towns;
    private readonly WorldFrame frame;
    private readonly Func<double, double, double> groundAt;
    private readonly Dictionary<string, RenderResource> sources = [];
    private readonly Dictionary<(string Mesh, string Palette), Appearance> looks = [];
    private readonly Dictionary<string, Appearance> props = [];
    private readonly List<MeshResource> propMeshes = [];
    private readonly PropMaterials propMaterials;
    private Material? paving;
    private RenderResource? pavingTexture, smokeSprite;
    private Placed? placed;
    private double swingSeconds;

    internal TownPresence(IEngineContext engine, ProductContent content, TownContent towns, WorldFrame frame, Func<double, double, double> groundAt)
    {
        this.engine = engine;
        this.content = content;
        this.towns = towns;
        this.frame = frame;
        this.groundAt = groundAt;
        propMaterials = new PropMaterials(engine, _ => new PropMaterials.Finish(PavingRoughness, 0, 0));
        frame.Rebased += _ => Relight();
    }

    /// <summary>The town standing in the world, if any.</summary>
    internal TownSite? Site => placed?.Site;

    /// <summary>The visible ground at the placed town's seat (m): the pad its pieces stand on.</summary>
    internal double Pad { get; private set; }

    /// <summary>The placed town's service buildings, for a visit's selection.</summary>
    internal IReadOnlyList<PlacedService> Services => placed?.Services ?? [];

    /// <summary>The placed town's solid shapes a selection ray must not see through (world metres).</summary>
    internal IReadOnlyList<((double X, double Y, double Z) Min, (double X, double Y, double Z) Max)> Blockers =>
        placed?.Blockers ?? [];

    internal string Readout => $"placed={placed?.Site.Key ?? "none"};pieces={Pieces};lights={Lights};emitters={Emitters}";

    internal int Lights => placed?.Lights.Count ?? 0;
    internal int Emitters => placed?.Smoke.Count ?? 0;
    internal int Pieces => placed?.Pieces.Count ?? 0;

    /// <summary>Stands the nearest town within reach, releasing one the traveller has left behind.</summary>
    internal void Follow(IReadOnlyList<TownSite> sites, double x, double z)
    {
        TownSite? nearest = null;
        double best = ReachMetres * ReachMetres;
        foreach (TownSite site in sites)
        {
            double squared = ((site.Frame.X - x) * (site.Frame.X - x)) + ((site.Frame.Z - z) * (site.Frame.Z - z));
            if (squared >= best) continue;
            best = squared;
            nearest = site;
        }

        if (nearest?.Key == placed?.Site.Key) return;
        Release();
        if (nearest is not null) Place(nearest);
    }

    /// <summary>Moves the hanging signs on host time, which keeps passing while a town is visited.</summary>
    internal void Advance(double seconds) => swingSeconds += Math.Max(0, seconds);

    internal AppearanceFact[] Facts
    {
        get
        {
            if (placed is null) return [];
            float swing = MathF.Sin((float)swingSeconds * SignRadiansPerSecond) * SignAmplitude;
            List<AppearanceFact> facts = new(placed.Pieces.Count + placed.Signs.Count + placed.Ground.Count);
            foreach (Placement piece in placed.Pieces)
                facts.Add(new(piece.Id, false, 0, new(Local(piece.At), piece.Turn, new Vector3(piece.Scale)), piece.Look, true, RenderLayer.Scene));
            foreach (Sign sign in placed.Signs)
                facts.Add(new(sign.Id, false, 0, new(Local(sign.Pivot), Quaternion.CreateFromAxisAngle(sign.Axis, swing) * sign.Turn, Vector3.One),
                    sign.Look, true, RenderLayer.Scene));
            for (int i = 0; i < placed.Ground.Count; i++)
                facts.Add(new(ProductIds.TownAppearanceBase + GroundOffset + (ulong)i, false, 0,
                    new(Local(placed.Ground[i].Origin), Quaternion.Identity, Vector3.One), placed.Ground[i].Look, true, RenderLayer.Scene, ShadowCasting.None));
            return [.. facts];
        }
    }

    private Vector3 Local((double X, double Y, double Z) world) => frame.ToLocal(world.X, world.Y, world.Z);

    private void Place(TownSite site)
    {
        Placed town = new(site);
        placed = town;
        TownFrame at = site.Frame;
        double pad = groundAt(at.X, at.Z);
        Pad = pad;
        ulong next = ProductIds.TownAppearanceBase;
        foreach (LayoutPiece piece in site.Layout.Pieces)
            Add(town, towns.Kit[piece.Piece], piece.At, piece.Yaw, piece.Service, piece.Close, ref next);
        Add(town, towns.Kit[site.Landmark], site.Layout.Landmark.At, site.Layout.Landmark.Yaw, null, null, ref next);
        DrapeGround(town, pad);
        Scatter(town);
        Relight();

        void Add(Placed town, KitPiece piece, Vector3 local, float yaw, string? service, TownView? close, ref ulong id)
        {
            Quaternion turn = at.Turn(yaw);
            (double X, double Y, double Z) origin = World(local);
            ulong pieceId = id++;
            town.Pieces.Add(new(pieceId, Look(piece.Mesh, piece.Materials, site.Palette), origin, turn));
            foreach (Vector3 lamp in piece.Lamps) town.Lamps.Add(Offset(origin, turn, lamp));
            foreach (Vector3 chimney in piece.Chimneys) town.Chimneys.Add(Offset(origin, turn, chimney));
            // Other pieces hide an entrance behind them; a service building is selected by its own box.
            if (service is null) town.Blockers.Add(Box(origin, turn, (piece.Min + piece.Max) / 2, piece.Max - piece.Min));
            if (piece.Sign is KitSign sign)
                town.Signs.Add(new(ProductIds.TownAppearanceBase + SignOffset + (ulong)town.Signs.Count, Look(sign.Mesh, sign.Materials, site.Palette),
                    Offset(origin, turn, sign.Pivot), Vector3.Transform(sign.Axis, turn), turn));
            if (service is not null && piece.Entrance is Vector3 entrance)
            {
                var box = Box(origin, turn, piece.SelectionCentre, piece.SelectionSize);
                town.Services.Add(new(ProductIds.TownServiceBase + (ulong)town.Services.Count, service, site.ServiceNames[service],
                    Offset(origin, turn, entrance), box.Min, box.Max, close));
            }
        }

        (double X, double Y, double Z) World(Vector3 local)
        {
            (double x, double y, double z) = at.World(local);
            return (x, pad + y - at.Y, z);
        }
    }

    private static (double X, double Y, double Z) Offset((double X, double Y, double Z) origin, Quaternion turn, Vector3 local)
    {
        Vector3 turned = Vector3.Transform(local, turn);
        return (origin.X + turned.X, origin.Y + turned.Y, origin.Z + turned.Z);
    }

    /// <summary>The world box about a turned local box (centre and size in the piece's frame).</summary>
    private static ((double X, double Y, double Z) Min, (double X, double Y, double Z) Max) Box(
        (double X, double Y, double Z) origin, Quaternion turn, Vector3 centre, Vector3 size)
    {
        Vector3 low = new(float.MaxValue), high = new(float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            Vector3 point = centre + (size / 2 * new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
            Vector3 turned = Vector3.Transform(point, turn);
            low = Vector3.Min(low, turned);
            high = Vector3.Max(high, turned);
        }

        return ((origin.X + low.X, origin.Y + low.Y, origin.Z + low.Z), (origin.X + high.X, origin.Y + high.Y, origin.Z + high.Z));
    }

    /// <summary>One appearance per kit mesh and palette, tinted once; every placement shares it.</summary>
    private Appearance Look(string mesh, string[] materials, string palette)
    {
        if (looks.TryGetValue((mesh, palette), out Appearance? look)) return look;
        if (!sources.TryGetValue(mesh, out RenderResource? source))
        {
            using ContentReference reference = engine.Content.OpenReference(new(mesh));
            source = engine.Animation.OpenAnimatedMeshFromContent(new(reference));
            sources[mesh] = source;
        }

        look = engine.Animation.CreateAnimatedMeshAppearance(new(source));
        IReadOnlyDictionary<string, Color> tints = towns.Palettes[palette];
        MeshMaterialFactors[] factors = [.. materials.Select((material, slot) => (material, slot))
            .Where(entry => tints.ContainsKey(entry.material))
            .Select(entry => MeshMaterialFactors.Tint((uint)entry.slot, tints[entry.material]))];
        if (factors.Length > 0) engine.Animation.UpdateAnimatedMeshMaterialFactors(new(look, factors));
        looks[(mesh, palette)] = look;
        return look;
    }

    /// <summary>The courtyard paving and the approach path, laid on the ground as it lies.</summary>
    private void DrapeGround(Placed town, double pad)
    {
        TownSite site = town.Site;
        pavingTexture ??= engine.Graphics.OpenResource(new(PavingTexture, TextureFilter.Linear, TextureWrap.Repeat)).Handle;
        paving ??= engine.Graphics.CreateMaterial(new MaterialRequest(White, pavingTexture, PavingRoughness, White, Vector3.Zero, 0, false));
        (double X, double Y, double Z) origin = (site.Frame.X, pad, site.Frame.Z);

        // The courtyard: a fan about its outline's centre, each wedge subdivided so it lies on the ground.
        Vector2[] outline = site.Layout.Paving;
        Vector2 centre = outline.Aggregate(Vector2.Zero, (sum, point) => sum + point) / outline.Length;
        List<Vector3> positions = [];
        List<uint> indices = [];
        for (int i = 0; i < outline.Length; i++)
        {
            Vector2 a = outline[i], b = outline[(i + 1) % outline.Length];
            uint first = (uint)positions.Count;
            // Rows of a barycentric grid from the centre (row 0) to the edge (row n).
            for (int row = 0; row <= CourtyardSteps; row++)
                for (int k = 0; k <= row; k++)
                {
                    Vector2 edge = row == 0 ? centre : Vector2.Lerp(Vector2.Lerp(centre, a, row / (float)CourtyardSteps),
                        Vector2.Lerp(centre, b, row / (float)CourtyardSteps), k / (float)row);
                    positions.Add(Relative(edge, PavingLift));
                }

            uint At(int row, int k) => first + (uint)((row * (row + 1) / 2) + k);
            for (int row = 0; row < CourtyardSteps; row++)
                for (int k = 0; k <= row; k++)
                {
                    indices.AddRange(Facing(At(row, k), At(row + 1, k), At(row + 1, k + 1), positions));
                    if (k < row) indices.AddRange(Facing(At(row, k), At(row + 1, k + 1), At(row, k + 1), positions));
                }
        }

        Lay(positions, indices);

        // The approach: a strip along its centre line, following the ground between the pad and the view.
        Vector2[] line = Resample(site.Layout.Path.Points, PathStepMetres);
        positions = [];
        indices = [];
        float half = site.Layout.Path.Width / 2;
        for (int i = 0; i < line.Length; i++)
        {
            Vector2 along = line[Math.Min(i + 1, line.Length - 1)] - line[Math.Max(i - 1, 0)];
            Vector2 across = Vector2.Normalize(new(-along.Y, along.X)) * half;
            positions.Add(Relative(line[i] - across, PathLift));
            positions.Add(Relative(line[i] + across, PathLift));
            if (i == 0) continue;
            uint a = (uint)((i - 1) * 2);
            indices.AddRange(Facing(a, a + 1, a + 3, positions));
            indices.AddRange(Facing(a, a + 3, a + 2, positions));
        }

        Lay(positions, indices);

        Vector3 Relative(Vector2 local, float lift)
        {
            (double x, _, double z) = site.Frame.World(new(local.X, 0, local.Y));
            double y = groundAt(x, z);
            return new((float)(x - origin.X), (float)(y - origin.Y + lift), (float)(z - origin.Z));
        }

        void Lay(List<Vector3> points, List<uint> triangles)
        {
            Vector2[] uvs = [.. points.Select(p => new Vector2(p.X / PavingTileMetres, p.Z / PavingTileMetres))];
            MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(points.ToArray(),
                Enumerable.Repeat(Vector3.UnitY, points.Count).ToArray(), uvs, Enumerable.Repeat(White, points.Count).ToArray(),
                triangles.ToArray(), new MeshGroup[] { new(0, 0, (uint)triangles.Count) }, new MeshMaterialBinding[] { new(0, paving!) }));
            town.Ground.Add((mesh, engine.Graphics.CreateMeshAppearance(mesh), origin));
        }
    }

    /// <summary>A triangle wound to face up, whichever way the frame turns.</summary>
    private static uint[] Facing(uint a, uint b, uint c, List<Vector3> points)
    {
        Vector3 normal = Vector3.Cross(points[(int)b] - points[(int)a], points[(int)c] - points[(int)a]);
        return normal.Y >= 0 ? [a, b, c] : [a, c, b];
    }

    private static Vector2[] Resample(Vector2[] points, float step)
    {
        List<Vector2> even = [points[0]];
        for (int i = 1; i < points.Length; i++)
        {
            float length = Vector2.Distance(points[i - 1], points[i]);
            int count = Math.Max(1, (int)MathF.Ceiling(length / step));
            for (int k = 1; k <= count; k++) even.Add(Vector2.Lerp(points[i - 1], points[i], k / (float)count));
        }

        return [.. even];
    }

    /// <summary>Props scattered about the layout's anchors, drawn from the town's key so a revisit finds them in place.</summary>
    private void Scatter(Placed town)
    {
        TownSite site = town.Site;
        ulong seed = ScatterSalt;
        foreach (char c in site.Key) seed = unchecked((seed ^ c) * 0x100000001B3UL);
        ulong n = 0;
        foreach (ScatterRule rule in site.Layout.Scatter)
        {
            Appearance look = Prop(rule.Prop);
            for (int i = 0; i < rule.Count; i++)
            {
                Vector2 anchor = rule.Around[(int)(Unit(seed, n++) * rule.Around.Length) % rule.Around.Length];
                Vector2 local = anchor + new Vector2((float)((Unit(seed, n++) * 2) - 1) * rule.Spread.X, (float)((Unit(seed, n++) * 2) - 1) * rule.Spread.Y);
                (double x, _, double z) = site.Frame.World(new(local.X, 0, local.Y));
                float scale = rule.Scale.X + ((rule.Scale.Y - rule.Scale.X) * (float)Unit(seed, n++));
                Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(Unit(seed, n++) * Math.Tau));
                town.Pieces.Add(new(ProductIds.TownAppearanceBase + FernOffset + (ulong)i, look, (x, groundAt(x, z), z), turn, scale));
            }
        }
    }

    private static double Unit(ulong seed, ulong index)
    {
        ulong mixed = unchecked((seed + (index * ScatterSalt)) * 0xBF58476D1CE4E5B9UL);
        mixed ^= mixed >> 31;
        return (mixed >> 11) / (double)(1UL << 53);
    }

    private Appearance Prop(string path)
    {
        if (props.TryGetValue(path, out Appearance? look)) return look;
        MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, path,
            (role, texture) => propMaterials.For(role, texture ?? throw new InvalidOperationException($"Town prop '{path}' part '{role}' names no texture."))));
        propMeshes.Add(mesh);
        look = engine.Graphics.CreateMeshAppearance(mesh);
        props[path] = look;
        return look;
    }

    /// <summary>The lamps and chimney smoke, placed afresh in the current drawing frame.</summary>
    private void Relight()
    {
        if (placed is null) return;
        Unlight(placed);
        for (int i = 0; i < placed.Lamps.Count; i++)
        {
            LightDescriptor lamp = new(LightKind.Point, LampColour, LampIntensity, true, Local(placed.Lamps[i]), -Vector3.UnitY,
                true, LampRange, LampDecay, 0, 0, LightShadowIntent.Disabled, 0, 1, true);
            placed.Lights.Add(engine.Graphics.CreateLight(new(ProductIds.TownLightBase + (ulong)i, false, 0, lamp)));
        }

        smokeSprite ??= engine.Graphics.OpenResource(new(SmokeSprite, TextureFilter.Linear, TextureWrap.Clamp)).Handle;
        for (int i = 0; i < placed.Chimneys.Count; i++)
            placed.Smoke.Add(engine.Presentation.CreateEmitter(Smoke(ProductIds.TownEmitterBase + (ulong)i, Local(placed.Chimneys[i]))));
    }

    private PresentationParticleDescriptor Smoke(ulong id, Vector3 at) => new()
    {
        LogicalId = id,
        SignalId = "craftsurvive.town.chimney",
        Visible = true,
        Seed = id,
        Anchor = new() { Kind = PresentationAnchorKind.World, Position = at },
        Visual = PresentationParticleVisual.Billboard,
        Sprite = smokeSprite!,
        SpriteFrameCount = 1,
        SizeMode = PresentationParticleSizeMode.World,
        Blend = PresentationParticleBlendMode.Alpha,
        SoftnessMetres = SmokeSoftness,
        RatePerSecond = SmokeRate,
        MaxParticles = SmokeParticles,
        LifetimeMinSeconds = SmokeLifeMin,
        LifetimeMaxSeconds = SmokeLifeMax,
        VelocityMin = SmokeVelocityMin,
        VelocityMax = SmokeVelocityMax,
        Acceleration = SmokeAcceleration,
        SizeCurve = SmokeSize,
        ColorCurve = SmokeColour,
    };

    private static void Unlight(Placed town)
    {
        foreach (Light light in town.Lights) light.Dispose();
        town.Lights.Clear();
        foreach (PresentationEmitter emitter in town.Smoke) emitter.Dispose();
        town.Smoke.Clear();
    }

    /// <summary>
    /// Withdraws the placed town. Its shared meshes stay open for the next; its ground meshes retire
    /// after the next snapshot no longer draws them (an appearance still published cannot be disposed).
    /// </summary>
    internal void Release()
    {
        if (placed is null) return;
        Unlight(placed);
        retiring.AddRange(placed.Ground.Select(ground => (ground.Mesh, ground.Look)));
        placed = null;
    }

    /// <summary>Disposes what a released town drew, once a snapshot without it has been published.</summary>
    internal void AfterAppearanceSnapshot()
    {
        foreach ((MeshResource mesh, Appearance look) in retiring)
        {
            look.Dispose();
            mesh.Dispose();
        }

        retiring.Clear();
    }

    private readonly List<(MeshResource Mesh, Appearance Look)> retiring = [];

    /// <summary>Releases everything; the caller has already published a snapshot without the town.</summary>
    public void Dispose()
    {
        Release();
        AfterAppearanceSnapshot();
        foreach (Appearance look in looks.Values) look.Dispose();
        looks.Clear();
        foreach (Appearance look in props.Values) look.Dispose();
        props.Clear();
        foreach (MeshResource mesh in propMeshes) mesh.Dispose();
        propMeshes.Clear();
        foreach (RenderResource source in sources.Values) source.Dispose();
        sources.Clear();
        paving?.Dispose();
        paving = null;
        pavingTexture?.Dispose();
        pavingTexture = null;
        smokeSprite?.Dispose();
        smokeSprite = null;
        propMaterials.Dispose();
    }
}
