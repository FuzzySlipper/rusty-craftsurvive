using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Feedback;

/// <summary>
/// A fire as the Engine draws it (#9547): a flame flipbook and embers added to the frame, and
/// smoke over it, each sheet fading where it meets the world (the Engine's soft and additive
/// billboards). A lamp and a dungeon torch burn the same fire at their own place; the three
/// emitters of one fire are retained together and moved together.
/// </summary>
internal static class Fires
{
    internal const string FlameContentPath = "textures/fire-flame.png";
    internal const string EmberContentPath = "textures/fire-ember.png";
    internal const string SmokeContentPath = "textures/fire-smoke.png";

    /// <summary>The flame strip's frames and how fast they play.</summary>
    private const ushort FlameFrames = 8;
    private const float FlameFramesPerSecond = 12f;

    /// <summary>How far each sheet fades as it nears the ground or a wall, in metres.</summary>
    private const float FlameSoftness = 0.3f;
    private const float EmberSoftness = 0.1f;
    private const float SmokeSoftness = 0.5f;

    private const float FlameRate = 14f, EmberRate = 6f, SmokeRate = 4f;
    private const uint FlameParticles = 32, EmberParticles = 32, SmokeParticles = 24;

    /// <summary>The emitters of one fire, in the order they are created.</summary>
    internal const int EmittersPerFire = 3;

    /// <summary>The three descriptors of a fire at a place, with their logical ids from a base.</summary>
    internal static PresentationParticleDescriptor[] At(ulong idBase, string signal, Vector3 at, RenderResource flame, RenderResource ember, RenderResource smoke) =>
    [
        Fire(idBase, signal + ".flame", at, flame, FlameFrames, FlameFramesPerSecond, FlameRate, 0.35f, 0.55f,
            new(-0.1f, 0.6f, -0.1f), new(0.1f, 1.1f, 0.1f), Vector3.Zero,
            // Added to the frame: a dozen overlapping sheets must sum to a glow, not a white block.
            [new(0f, 0.5f), new(0.5f, 0.65f), new(1f, 0.18f)],
            [new(0f, new Color(1f, 0.8f, 0.45f, 0.55f)), new(0.6f, new Color(1f, 0.45f, 0.1f, 0.4f)), new(1f, new Color(0.5f, 0.1f, 0f, 0f))],
            PresentationParticleBlendMode.Additive, FlameSoftness, FlameParticles),
        Fire(idBase + 1, signal + ".embers", at, ember, 1, 0f, EmberRate, 1f, 2f,
            new(-0.35f, 0.8f, -0.35f), new(0.35f, 1.6f, 0.35f), new(0f, -0.5f, 0f),
            [new(0f, 0.06f), new(1f, 0.02f)],
            [new(0f, new Color(1f, 0.6f, 0.2f, 0.7f)), new(1f, new Color(1f, 0.2f, 0f, 0f))],
            PresentationParticleBlendMode.Additive, EmberSoftness, EmberParticles),
        Fire(idBase + 2, signal + ".smoke", at, smoke, 1, 0f, SmokeRate, 2f, 3f,
            new(-0.2f, 0.45f, -0.2f), new(0.2f, 0.8f, 0.2f), Vector3.Zero,
            [new(0f, 0.3f), new(1f, 1.1f)],
            [new(0f, new Color(0.3f, 0.27f, 0.25f, 0.4f)), new(1f, new Color(0.25f, 0.25f, 0.25f, 0f))],
            PresentationParticleBlendMode.Alpha, SmokeSoftness, SmokeParticles),
    ];

    private static PresentationParticleDescriptor Fire(ulong id, string signal, Vector3 at, RenderResource sprite, ushort frames, float framesPerSecond,
        float rate, float lifeMin, float lifeMax, Vector3 velocityMin, Vector3 velocityMax, Vector3 acceleration,
        PresentationParticleScalarKey[] size, PresentationParticleColorKey[] colour, PresentationParticleBlendMode blend, float softness, uint maximum) => new()
    {
        LogicalId = id,
        SignalId = signal,
        Visible = true,
        Seed = id,
        Anchor = new PresentationAnchor { Kind = PresentationAnchorKind.World, Position = at },
        Visual = PresentationParticleVisual.Billboard,
        Sprite = sprite,
        SpriteFrameCount = frames,
        FlipbookFramesPerSecond = framesPerSecond,
        SizeMode = PresentationParticleSizeMode.World,
        Blend = blend,
        SoftnessMetres = softness,
        RatePerSecond = rate,
        MaxParticles = maximum,
        LifetimeMinSeconds = lifeMin,
        LifetimeMaxSeconds = lifeMax,
        VelocityMin = velocityMin,
        VelocityMax = velocityMax,
        Acceleration = acceleration,
        SizeCurve = size,
        ColorCurve = colour,
    };
}

/// <summary>
/// A pool of burning fires: one per slot, each three retained emitters, created when a slot
/// first burns, moved when its place changes and put out when its slot empties. The sprites
/// are opened once and held while the pool lives.
/// </summary>
internal sealed class FireEmitters : IDisposable
{
    private readonly IEngineContext engine;
    private readonly ulong idBase;
    private readonly string signal;
    private readonly PresentationEmitter?[]?[] pool;
    private readonly Vector3?[] burningAt;
    private RenderResource? flame, ember, smoke;
    private bool opened;

    internal FireEmitters(IEngineContext engine, ulong idBase, string signal, int slots)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.idBase = idBase;
        this.signal = signal;
        pool = new PresentationEmitter?[]?[slots];
        burningAt = new Vector3?[slots];
    }

    /// <summary>How many fires burn now.</summary>
    internal int Burning => burningAt.Count(at => at is not null);

    /// <summary>Burns a fire at each place, slot by slot, and puts out the slots beyond them.</summary>
    internal void Show(IReadOnlyList<Vector3> places)
    {
        for (int slot = 0; slot < pool.Length; slot++)
        {
            if (slot >= places.Count)
            {
                PutOut(slot);
                continue;
            }

            Vector3 at = places[slot];
            if (burningAt[slot] == at) continue;
            Open();
            PresentationParticleDescriptor[] fire = Fires.At(idBase + ((ulong)slot * Fires.EmittersPerFire), $"{signal}.{slot}", at, flame!, ember!, smoke!);
            if (pool[slot] is PresentationEmitter?[] burning)
            {
                for (int index = 0; index < fire.Length; index++)
                {
                    if (burning[index] is PresentationEmitter emitter) engine.Presentation.UpdateEmitter(emitter, fire[index]);
                }
            }
            else
            {
                pool[slot] = [.. fire.Select(descriptor => (PresentationEmitter?)engine.Presentation.CreateEmitter(descriptor))];
            }

            burningAt[slot] = at;
        }
    }

    public void Dispose()
    {
        for (int slot = 0; slot < pool.Length; slot++) PutOut(slot);
        if (opened)
        {
            flame?.Dispose();
            ember?.Dispose();
            smoke?.Dispose();
            flame = ember = smoke = null;
            opened = false;
        }
    }

    private void PutOut(int slot)
    {
        if (pool[slot] is PresentationEmitter?[] burning)
        {
            foreach (PresentationEmitter? emitter in burning) emitter?.Dispose();
            pool[slot] = null;
        }

        burningAt[slot] = null;
    }

    private void Open()
    {
        if (opened) return;
        flame = Sprite(Fires.FlameContentPath);
        ember = Sprite(Fires.EmberContentPath);
        smoke = Sprite(Fires.SmokeContentPath);
        opened = true;
    }

    private RenderResource Sprite(string path)
    {
        RenderResourceInfo resource = engine.Graphics.OpenResource(new RenderResourceRequest(path, TextureFilter.Linear, TextureWrap.Clamp));
        if (resource.Kind != RenderResourceKind.Texture || resource.ByteLength == 0)
        {
            throw new InvalidOperationException($"CraftSurvive fire sprite '{path}' must be a non-empty Engine texture.");
        }

        return resource.Handle;
    }
}
