using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Feedback;

/// <summary>What a burst's particles are drawn with: the soft dust puff, or cubes in the terrain atlas.</summary>
internal enum BurstLook
{
    Puff,
    Chips,
}

/// <summary>
/// One kind of particle burst: how many, how long they live, how they are thrown and pulled, and
/// how they grow and fade. Chips bounce off a box around where they were thrown, the size of
/// <see cref="SettleRadius"/>, and settle; puffs pass through everything.
/// </summary>
internal sealed record BurstStyle(
    string Label,
    BurstLook Look,
    int Count,
    float LifetimeMinSeconds,
    float LifetimeMaxSeconds,
    Vector3 VelocityMin,
    Vector3 VelocityMax,
    Vector3 Acceleration,
    (float Age, float Size)[] Sizes,
    (float Age, Color Colour)[] Colours,
    bool SizedInWorld = true,
    float SettleRadius = 0f);

/// <summary>
/// The particle bursts the game's cues are seen as, and how one becomes an Engine emission. A burst
/// is a pure function of its style, where it happens and its seed, so the same event looks the same.
/// </summary>
internal static class Bursts
{
    /// <summary>The smoke's sprite: a soft white puff, authored by scripts/generate-dust-puff.mjs.</summary>
    internal const string PuffContentPath = "textures/dust-puff.png";

    /// <summary>The widest seed an emission admits: the Engine requires it to fit 53 bits.</summary>
    internal const ulong MaximumSeed = (1UL << 53) - 1;

    /// <summary>A sprite is used whole, as one frame.</summary>
    private const ushort SingleFrame = 1;

    /// <summary>How a settling chip meets the ground.</summary>
    private const float ChipRadius = 0.12f;
    private const float ChipRestitution = 0.25f;
    private const float ChipFriction = 0.55f;
    private const ushort ChipImpacts = 3;
    private const float ChipSleepSpeed = 0.5f;

    /// <summary>Folds an identity into the seed range an emission admits.</summary>
    internal static ulong Seed(ulong identity) => (identity ^ (identity >> 53)) & MaximumSeed;

    private static readonly Vector3 Gravity = new(0f, -9.8f, 0f);

    private static readonly Color Earth = new(0.62f, 0.56f, 0.48f, 0.75f);
    private static readonly Color EarthGone = new(0.6f, 0.56f, 0.5f, 0f);

    /// <summary>A charge's smoke: a big billowing cloud that drifts up and fades, drawn at a fixed size on screen.</summary>
    internal static readonly BurstStyle BlastSmoke = new(
        "craftsurvive.blast.smoke", BurstLook.Puff, 160, 2.8f * 0.6f, 2.8f,
        new(-2.4f, 0.4f, -2.4f), new(2.4f, 3.2f, 2.4f), new(0f, -0.4f, 0f),
        [(0f, 1.5f), (0.2f, 4f), (1f, 6f)],
        [(0f, new(0.72f, 0.66f, 0.58f, 0.9f)), (0.5f, new(0.68f, 0.64f, 0.58f, 0.55f)), (1f, new(0.66f, 0.63f, 0.6f, 0f))],
        SizedInWorld: false);

    /// <summary>A charge's debris: blocks thrown outward that fall and settle in the crater.</summary>
    internal static readonly BurstStyle BlastDebris = new(
        "craftsurvive.blast.debris", BurstLook.Chips, 48, 0.9f * 0.7f, 0.9f,
        new(-5.5f, 1.5f, -5.5f), new(5.5f, 6.5f, 5.5f), Gravity,
        [(0f, 0.22f), (1f, 0.16f)],
        [(0f, new(0.46f, 0.34f, 0.24f, 1f)), (1f, new(0.4f, 0.3f, 0.22f, 1f))],
        SettleRadius: 3.5f);

    /// <summary>Coming down: a low ring of dust kicked out around the feet.</summary>
    internal static readonly BurstStyle LandingDust = new(
        "craftsurvive.land", BurstLook.Puff, 14, 0.45f, 0.8f,
        new(-1.4f, 0.1f, -1.4f), new(1.4f, 0.6f, 1.4f), new(0f, -0.3f, 0f),
        [(0f, 0.25f), (1f, 0.8f)],
        [(0f, Earth), (1f, EarthGone)]);

    /// <summary>Coming down hard: more dust, thrown wider and higher.</summary>
    internal static readonly BurstStyle HardLandingDust = LandingDust with
    {
        Label = "craftsurvive.land.hard",
        Count = 32,
        VelocityMin = new(-2.6f, 0.2f, -2.6f),
        VelocityMax = new(2.6f, 1.4f, 2.6f),
        Sizes = [(0f, 0.35f), (1f, 1.3f)],
    };

    /// <summary>Going into water: drops thrown up that fall back.</summary>
    internal static readonly BurstStyle SplashDrops = new(
        "craftsurvive.splash.drops", BurstLook.Puff, 26, 0.45f, 0.75f,
        new(-1.6f, 2.2f, -1.6f), new(1.6f, 4.6f, 1.6f), Gravity,
        [(0f, 0.18f), (1f, 0.1f)],
        [(0f, new(0.86f, 0.94f, 1f, 0.95f)), (1f, new(0.7f, 0.85f, 1f, 0.2f))]);

    /// <summary>Going into water: a low white spray that spreads on the surface.</summary>
    internal static readonly BurstStyle SplashSpray = new(
        "craftsurvive.splash.spray", BurstLook.Puff, 12, 0.4f, 0.7f,
        new(-1.8f, 0f, -1.8f), new(1.8f, 0.4f, 1.8f), Vector3.Zero,
        [(0f, 0.3f), (1f, 0.9f)],
        [(0f, new(0.9f, 0.96f, 1f, 0.7f)), (1f, new(0.85f, 0.92f, 1f, 0f))]);

    /// <summary>A blow landing on a creature: a small puff and a few flecks.</summary>
    internal static readonly BurstStyle StrikePuff = new(
        "craftsurvive.strike", BurstLook.Puff, 10, 0.25f, 0.45f,
        new(-1.5f, -0.2f, -1.5f), new(1.5f, 1.2f, 1.5f), new(0f, -2f, 0f),
        [(0f, 0.18f), (1f, 0.45f)],
        [(0f, new(0.95f, 0.9f, 0.82f, 0.9f)), (1f, new(0.85f, 0.8f, 0.72f, 0f))]);

    /// <summary>A creature going down: a rising grey cloud.</summary>
    internal static readonly BurstStyle DefeatCloud = new(
        "craftsurvive.defeat", BurstLook.Puff, 28, 0.8f, 1.4f,
        new(-0.8f, 0.4f, -0.8f), new(0.8f, 1.8f, 0.8f), new(0f, 0.2f, 0f),
        [(0f, 0.4f), (0.3f, 0.9f), (1f, 1.4f)],
        [(0f, new(0.7f, 0.7f, 0.72f, 0.85f)), (1f, new(0.6f, 0.6f, 0.64f, 0f))]);

    /// <summary>A block set down: a puff of dust from its seams.</summary>
    internal static readonly BurstStyle PlaceDust = new(
        "craftsurvive.place", BurstLook.Puff, 10, 0.3f, 0.55f,
        new(-1.1f, -0.3f, -1.1f), new(1.1f, 0.5f, 1.1f), new(0f, -0.6f, 0f),
        [(0f, 0.2f), (1f, 0.55f)],
        [(0f, Earth), (1f, EarthGone)]);

    /// <summary>A built piece taken down: splinters thrown out that fall and settle (#9730).</summary>
    internal static readonly BurstStyle BreakSplinters = new(
        "craftsurvive.break.splinters", BurstLook.Chips, 18, 0.7f, 1.0f,
        new(-2.2f, 1.0f, -2.2f), new(2.2f, 3.2f, 2.2f), Gravity,
        [(0f, 0.14f), (1f, 0.1f)],
        [(0f, new(0.55f, 0.4f, 0.26f, 1f)), (1f, new(0.48f, 0.35f, 0.23f, 1f))],
        SettleRadius: 2f);

    /// <summary>What each cue is seen as; a cue not listed has no burst.</summary>
    internal static readonly IReadOnlyDictionary<Cue, BurstStyle[]> ByCue = new Dictionary<Cue, BurstStyle[]>
    {
        [Cue.Blast] = [BlastSmoke, BlastDebris],
        [Cue.Land] = [LandingDust],
        [Cue.HardLanding] = [HardLandingDust],
        [Cue.Splash] = [SplashDrops, SplashSpray],
        [Cue.Strike] = [StrikePuff],
        [Cue.Defeat] = [DefeatCloud],
        [Cue.Place] = [PlaceDust],
        [Cue.Break] = [BreakSplinters, PlaceDust],
    };

    /// <summary>One burst of a style at a place, drawn with the given sprite, from a seed.</summary>
    internal static PresentationParticleDescriptor Describe(BurstStyle style, Vector3 at, RenderResourceReference sprite, ulong seed)
    {
        bool chips = style.Look == BurstLook.Chips;
        PresentationParticleDescriptor descriptor = new()
        {
            SignalId = style.Label,
            Visible = true,
            Anchor = new PresentationAnchor { Kind = PresentationAnchorKind.World, Position = at },
            Sprite = sprite,
            SpriteFrameCount = SingleFrame,
            Visual = chips ? PresentationParticleVisual.Cube : PresentationParticleVisual.Billboard,
            SizeMode = style.SizedInWorld ? PresentationParticleSizeMode.World : PresentationParticleSizeMode.Screen,
            BurstCount = (uint)style.Count,
            RatePerSecond = 0f,
            MaxParticles = (uint)style.Count,
            LifetimeMinSeconds = style.LifetimeMinSeconds,
            LifetimeMaxSeconds = style.LifetimeMaxSeconds,
            VelocityMin = style.VelocityMin,
            VelocityMax = style.VelocityMax,
            Acceleration = style.Acceleration,
            SizeCurve = style.Sizes.Select(key => new PresentationParticleScalarKey { Age = key.Age, Value = key.Size }).ToArray(),
            ColorCurve = style.Colours.Select(key => new PresentationParticleColorKey { Age = key.Age, Color = key.Colour }).ToArray(),
            Seed = Seed(seed),
            HasCollision = chips && style.SettleRadius > 0f,
        };
        if (!descriptor.HasCollision)
        {
            return descriptor;
        }

        Vector3 reach = new(style.SettleRadius);
        return descriptor with
        {
            Collision = new PresentationParticleCollision
            {
                Radius = ChipRadius,
                Restitution = ChipRestitution,
                Friction = ChipFriction,
                MaximumImpacts = ChipImpacts,
                SleepSpeed = ChipSleepSpeed,
                LimitBehavior = PresentationParticleCollisionLimitBehavior.Sleep,
            },
            CollisionVolumes = new PresentationParticleCollisionVolume[]
            {
                new()
                {
                    Kind = PresentationParticleCollisionVolumeKind.Aabb,
                    Minimum = at - reach,
                    Maximum = at + reach,
                },
            },
        };
    }
}
