using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A chasm's waterfalls drawn and heard (#10094; owner, 2026-10-20): each stream a sheet of water strands arcing from its
/// lip, water streaming down it and spreading into spray as it falls, a cloud of spray where it lands, and its roar at the
/// fall. Deep in a chasm almost no direct light reaches the water, yet falling water shows by the light of the opening it
/// catches; so the sheet and the spray are shown at the light that reaches them - the day's light, less the deeper the
/// fall lies below the surface (the Engine does not light particles) - and fade to nothing at night rather than glowing. The places come from the cave generator (<see cref="KarstCave"/>).
/// </summary>
internal sealed class CaveWaterfalls : IDisposable
{
    private const string SprayContentPath = "textures/dust-puff.png";
    private const string StrandsContentPath = "textures/waterfall-strands.png";
    private const string RoarContentPath = "towns/audio/river.wav";

    // The water's fall: gravity, how fast it leaves the lip (more water, faster), how wide the sheet is (more water, wider)
    // and how much it spreads as it falls, its colour and how clear it is, and how far its sheet reaches into an abyss.
    private const float Gravity = 9.8f, LeastPour = 1.2f, PourPerFlow = 2.5f, NarrowestSheet = 0.6f, SheetPerFlow = 2.4f, SheetSpread = 1.8f;
    private static readonly Color SheetColour = new(0.62f, 0.7f, 0.74f, 1f);
    private const float SheetRoughness = 0.12f, AbyssSheet = 25f;

    /// <summary>
    /// The sheet is cut from a strand texture (masked, not blended: the Engine does not draw blended product meshes): how
    /// much of a strand's alpha shows, and how many metres across and down one repeat of the texture covers.
    /// </summary>
    private const float StrandCutoff = 0.5f, StrandsAcross = 1.5f, StrandsDown = 4f;
    private const int SheetSegments = 18;

    // The streaming water and the spray: rates, lifetimes, sizes and how clear they are.
    private const float StreamRate = 220f, StreamRatePerFlow = 300f, StreamJitter = 0.5f, LongestStream = 4.5f;
    private const float SprayRate = 18f, SprayRatePerFlow = 30f, SprayLife = 2.5f;
    private const float StreamAlpha = 0.35f, SprayAlpha = 0.3f;
    private const uint MostStreamParticles = 1600, MostSprayParticles = 160;

    /// <summary>How brightly the sheet shows the light it catches from the opening, at full daylight.</summary>
    private const float SheetCatch = 0.5f;
    private static readonly Vector3 WaterTint = new(0.85f, 0.9f, 0.95f);

    // The roar: how loud (more water, louder), its pitch (a deep river's sound brought down to a fall's), and how far it carries.
    private const float RoarVolume = 0.35f, RoarPerFlow = 0.5f, RoarPitch = 0.7f, RoarReach = 70f;

    /// <summary>Spray brightness changes smaller than this are not sent again.</summary>
    private const float BrightnessStep = 0.03f;

    private readonly IEngineContext engine;
    private readonly IReadOnlyList<Waterfall> falls;
    private readonly List<PresentationEmitter> emitters = [];
    private readonly List<AudioVoice> voices = [];
    private readonly List<Material> sheetMaterials = [];
    private RenderResource? spray, strands;
    private AudioClip? roar;
    private float brightness = -1f;

    internal CaveWaterfalls(IEngineContext engine, IReadOnlyList<Waterfall> falls)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.falls = falls;
        if (falls.Count == 0)
        {
            return;
        }

        strands = engine.Graphics.OpenResource(new RenderResourceRequest(StrandsContentPath, TextureFilter.Linear, TextureWrap.Repeat)).Handle;
        foreach (Waterfall fall in falls)
        {
            sheetMaterials.Add(engine.Graphics.CreateMaterial(SheetLook(0f)));
        }

        RenderResourceInfo sprite = engine.Graphics.OpenResource(new RenderResourceRequest(SprayContentPath, TextureFilter.Linear, TextureWrap.Clamp));
        spray = sprite.Handle;
        try
        {
            using ContentReference content = engine.Content.OpenReference(new(RoarContentPath));
            roar = engine.Audio.OpenClipFromContent(new(content));
            foreach (Waterfall fall in falls)
            {
                voices.Add(engine.Audio.CreateVoice(Roar(fall, roar)));
            }
        }
        catch (EngineCallException refused)
        {
            AudioFailure = refused.Message;
        }
    }

    /// <summary>
    /// The sheets' meshes, for the space to draw with its growths: an appearance draws only while a published appearance
    /// fact names it, and the space publishes its own (rusty-engine #10172). These materials stay here.
    /// </summary>
    internal IEnumerable<MeshResourceCreateRequest> SheetRequests() => falls.Select((fall, i) => Sheet(fall, sheetMaterials[i]));

    /// <summary>Why the roar could not be played, if it could not.</summary>
    internal string AudioFailure { get; private set; } = "none";

    internal string Readout => $"waterfalls={falls.Count} sheets={sheetMaterials.Count} emitters={emitters.Count} voices={voices.Count} spray={brightness:F2} audio={AudioFailure}";

    /// <summary>Shows the streams and spray at the day's light (0 to 1) as it reaches each fall; sent again only when it changes.</summary>
    internal void Light(float daylight)
    {
        if (spray is null || MathF.Abs(daylight - brightness) < BrightnessStep)
        {
            return;
        }

        brightness = daylight;
        for (int i = 0; i < falls.Count; i++)
        {
            engine.Graphics.UpdateMaterial(new MaterialUpdateRequest(sheetMaterials[i], SheetLook(daylight * falls[i].SkyShare)));
        }

        PresentationParticleDescriptor[] wanted = [.. falls.SelectMany((fall, i) => Emitters(fall, i, daylight * fall.SkyShare))];
        if (emitters.Count == 0)
        {
            emitters.AddRange(wanted.Select(engine.Presentation.CreateEmitter));
            return;
        }

        for (int i = 0; i < emitters.Count; i++)
        {
            engine.Presentation.UpdateEmitter(emitters[i], wanted[i]);
        }
    }

    /// <summary>The sheet's look: strands of water showing the light they catch from the opening.</summary>
    private MaterialRequest SheetLook(float light) =>
        new MaterialRequest(SheetColour, strands!, SheetRoughness, new Color(1f, 1f, 1f, 1f), WaterTint, SheetCatch * light, false)
            with { AlphaMode = MaterialAlphaMode.Mask, AlphaCutoff = StrandCutoff };

    /// <summary>The sheet of falling water: a ribbon along the stream's arc, widening as it falls, facing out of the wall.</summary>
    private static MeshResourceCreateRequest Sheet(Waterfall fall, Material material)
    {
        float pour = LeastPour + (PourPerFlow * fall.Flow);
        float width = NarrowestSheet + (SheetPerFlow * fall.Flow);
        float drop = fall.Lands ? fall.Drop : fall.Drop + AbyssSheet;
        float time = MathF.Sqrt(2f * drop / Gravity);
        Vector3 across = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, fall.Out));
        List<Vector3> positions = [];
        List<Vector3> normals = [];
        List<Vector2> uvs = [];
        float along = 0f;
        Vector3 previous = DungeonSpace.InSession(fall.Lip);
        for (int i = 0; i <= SheetSegments; i++)
        {
            float t = time * i / SheetSegments;
            Vector3 at = DungeonSpace.InSession(fall.Lip) + (fall.Out * pour * t) - new Vector3(0f, 0.5f * Gravity * t * t, 0f);
            float half = width * 0.5f * (1f + (SheetSpread - 1f) * (i / (float)SheetSegments));
            Vector3 tangent = Vector3.Normalize((fall.Out * pour) - new Vector3(0f, Gravity * t, 0f));
            Vector3 normal = Vector3.Normalize(Vector3.Cross(tangent, across));
            along += Vector3.Distance(previous, at);
            previous = at;
            positions.Add(at - (across * half));
            positions.Add(at + (across * half));
            uvs.Add(new Vector2(-half / StrandsAcross, along / StrandsDown));
            uvs.Add(new Vector2(half / StrandsAcross, along / StrandsDown));
            normals.Add(normal);
            normals.Add(normal);
        }

        // Both faces, as the cave's growths are built: the sheet is seen from the chasm and from behind its lip.
        int front = positions.Count;
        for (int i = 0; i < front; i++)
        {
            positions.Add(positions[i]);
            normals.Add(-normals[i]);
            uvs.Add(uvs[i]);
        }

        List<uint> indices = [];
        for (uint i = 0; i < SheetSegments; i++)
        {
            uint a = i * 2, b = a + 1, c = a + 2, d = a + 3;
            indices.AddRange([a, c, b, b, c, d]);
            uint back = (uint)front;
            indices.AddRange([back + a, back + b, back + c, back + b, back + d, back + c]);
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(), ReadOnlyMemory<Color>.Empty, indices.ToArray(),
            new[] { new MeshGroup(0, 0, (uint)indices.Count) }, new[] { new MeshMaterialBinding(0, material) });
    }

    /// <summary>A fall's two emitters: water streaming down from its lip, spreading as it falls; and spray where it lands, or none.</summary>
    private IEnumerable<PresentationParticleDescriptor> Emitters(Waterfall fall, int index, float light)
    {
        float pour = LeastPour + (PourPerFlow * fall.Flow);
        float drop = fall.Lands ? fall.Drop : fall.Drop + AbyssSheet;
        float life = MathF.Min(LongestStream, MathF.Sqrt(2f * drop / Gravity));
        Vector3 lip = DungeonSpace.InSession(fall.Lip);
        Vector3 velocity = fall.Out * pour;
        Vector3 jitter = new(StreamJitter);
        Vector3 tint = WaterTint * light;
        ulong id = ProductIds.DungeonWaterfallBase + ((ulong)index * 2);
        yield return Emitter(id, $"craftsurvive.waterfall.{index}.stream", lip, velocity - jitter, velocity + jitter, new Vector3(0f, -Gravity, 0f),
            StreamRate + (StreamRatePerFlow * fall.Flow), life * 0.85f, life, [new(0f, 0.2f), new(0.5f, 0.6f), new(1f, 1.6f)],
            [new(0f, Tint(tint, StreamAlpha)), new(0.7f, Tint(tint * 0.8f, StreamAlpha * 0.8f)), new(1f, Tint(tint * 0.6f, 0f))], MostStreamParticles);
        Vector3 foot = lip + (fall.Out * pour * life) - new Vector3(0f, fall.Lands ? fall.Drop - 0.3f : drop, 0f);
        yield return Emitter(id + 1, $"craftsurvive.waterfall.{index}.spray", foot, new Vector3(-2f, 0.4f, -2f), new Vector3(2f, 2.5f, 2f), new Vector3(0f, -1.5f, 0f),
            fall.Lands ? SprayRate + (SprayRatePerFlow * fall.Flow) : 0f, SprayLife * 0.6f, SprayLife, [new(0f, 0.6f), new(1f, 3f)],
            [new(0f, Tint(tint, SprayAlpha)), new(1f, Tint(tint, 0f))], MostSprayParticles);
    }

    private static Color Tint(Vector3 rgb, float alpha) => new(rgb.X, rgb.Y, rgb.Z, alpha);

    private PresentationParticleDescriptor Emitter(ulong id, string signal, Vector3 at, Vector3 velocityMin, Vector3 velocityMax, Vector3 acceleration,
        float rate, float lifeMin, float lifeMax, PresentationParticleScalarKey[] size, PresentationParticleColorKey[] colour, uint most) => new()
    {
        LogicalId = id,
        SignalId = signal,
        Visible = true,
        Seed = id,
        Anchor = new PresentationAnchor { Kind = PresentationAnchorKind.World, Position = at },
        Visual = PresentationParticleVisual.Billboard,
        Sprite = spray!,
        SpriteFrameCount = 1,
        SizeMode = PresentationParticleSizeMode.World,
        Blend = PresentationParticleBlendMode.Alpha,
        SoftnessMetres = 0.5f,
        RatePerSecond = rate,
        MaxParticles = most,
        LifetimeMinSeconds = lifeMin,
        LifetimeMaxSeconds = lifeMax,
        VelocityMin = velocityMin,
        VelocityMax = velocityMax,
        Acceleration = acceleration,
        SizeCurve = size,
        // Particle colour keys are linear (#9844); these are chosen linear.
        ColorCurve = colour,
    };

    /// <summary>A fall's roar: heard from the middle of its drop, louder the more water it carries.</summary>
    private static AudioSourceDescriptor Roar(Waterfall fall, AudioClip clip)
    {
        Vector3 middle = DungeonSpace.InSession(fall.Lip) - new Vector3(0f, MathF.Min(fall.Drop, AbyssSheet) * 0.5f, 0f);
        return new AudioSourceDescriptor(clip, AudioBus.Ambient, RoarVolume + (RoarPerFlow * fall.Flow), RoarPitch, true, 1f, RoarReach,
            AudioRolloff.LinearDecibels, 0f, AudioEmitterKind.World3d, middle, 0, Vector3.Zero);
    }

    public void Dispose()
    {
        foreach (AudioVoice voice in voices)
        {
            voice.Dispose();
        }

        voices.Clear();
        roar?.Dispose();
        roar = null;
        foreach (PresentationEmitter emitter in emitters)
        {
            emitter.Dispose();
        }

        emitters.Clear();
        foreach (Material material in sheetMaterials)
        {
            material.Dispose();
        }

        sheetMaterials.Clear();
        spray?.Dispose();
        spray = null;
        strands?.Dispose();
        strands = null;
    }
}
