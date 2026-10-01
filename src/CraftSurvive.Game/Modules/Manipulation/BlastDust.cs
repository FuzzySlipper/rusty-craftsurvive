using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// The smoke and debris of a charge. Emitted before the edit, with the cloud still billowing when
/// the cleared cells disappear and settling after, so the blast reads as one event. The smoke is a
/// soft puff sprite tinted by its colour curve; the debris are cubes in the world's own atlas.
///
/// Everything here is a pure function of the charge - its centre and the identity mixed from it - so the same blast
/// produces the same dust, as the product's generation draws do.
/// </summary>
internal static class BlastDust
{
    /// <summary>The smoke's sprite: a soft white puff, authored by scripts/generate-dust-puff.mjs.</summary>
    internal const string PuffContentPath = "textures/dust-puff.png";

    /// <summary>Dust puffs: enough to hide a crater, few enough to stay cheap.</summary>
    internal const int SmokeParticles = 160;

    /// <summary>Thrown blocks: the debris that says something solid came apart.</summary>
    internal const int DebrisParticles = 48;

    /// <summary>How long dust lives, in seconds.</summary>
    internal const float SmokeLifetimeSeconds = 2.8f;

    internal const float DebrisLifetimeSeconds = 0.9f;

    /// <summary>Half-extent of the box debris collides inside: roughly the crater a blast opens.</summary>
    private const float BlastCraterRadius = 3.5f;

    /// <summary>The atlas is used as one whole frame, not a flipbook.</summary>
    private const ushort SingleFrame = 1;

    private const string SmokeSignal = "craftsurvive.blast.smoke";

    private const string DebrisSignal = "craftsurvive.blast.debris";

    /// <summary>The widest seed an emission admits: the Engine requires it to fit 53 bits.</summary>
    internal const ulong MaximumParticleSeed = (1UL << 53) - 1;

    private const ulong IdentityOffsetBasis = 0xCBF2_9CE4_8422_2325UL;
    private const ulong IdentityPrime = 0x0000_0100_0000_01B3UL;

    /// <summary>Separates the debris emitter's identity from the smoke's for the same charge.</summary>
    private const ulong DebrisIdentitySalt = 0x9E37_79B9_7F4A_7C15UL;

    /// <summary>
    /// A charge's identity: the same blast in the same place produces the same dust. Every
    /// coordinate is mixed whole, so negative and distant centres stay distinct.
    /// </summary>
    internal static ulong ChargeIdentity(CraftSurvive.Game.Modules.Terrain.VoxelAddress centre)
    {
        ulong hash = IdentityOffsetBasis;
        foreach (long coordinate in (ReadOnlySpan<long>)[centre.X, centre.Y, centre.Z])
        {
            hash = unchecked((hash ^ (ulong)coordinate) * IdentityPrime);
        }

        return hash;
    }

    /// <summary>A charge identity folded into the seed range an emission admits.</summary>
    internal static ulong ParticleSeed(ulong identity) => (identity ^ (identity >> 53)) & MaximumParticleSeed;

    /// <summary>A charge's dust: a seeded burst of billboards that drifts up and fades out.</summary>
    internal static PresentationParticleDescriptor Smoke(Vector3 centre, RenderResourceReference sprite, ulong identity) => new()
    {
        SignalId = SmokeSignal,
        LogicalId = identity,
        Visible = true,
        Anchor = new PresentationAnchor
        {
            Kind = PresentationAnchorKind.World,
            Position = centre,
        },
        Sprite = sprite,
        SpriteFrameCount = SingleFrame,
        Visual = PresentationParticleVisual.Billboard,
        BurstCount = SmokeParticles,
        RatePerSecond = 0f,
        MaxParticles = SmokeParticles,
        LifetimeMinSeconds = SmokeLifetimeSeconds * 0.6f,
        LifetimeMaxSeconds = SmokeLifetimeSeconds,
        VelocityMin = new Vector3(-2.4f, 0.4f, -2.4f),
        VelocityMax = new Vector3(2.4f, 3.2f, 2.4f),
        Acceleration = new Vector3(0f, -0.4f, 0f),
        SizeCurve = new PresentationParticleScalarKey[]
        {
            new() { Age = 0f, Value = 1.5f },
            new() { Age = 0.2f, Value = 4f },
            new() { Age = 1f, Value = 6f },
        },
        ColorCurve = new PresentationParticleColorKey[]
        {
            new() { Age = 0f, Color = new Color(0.72f, 0.66f, 0.58f, 0.9f) },
            new() { Age = 0.5f, Color = new Color(0.68f, 0.64f, 0.58f, 0.55f) },
            new() { Age = 1f, Color = new Color(0.66f, 0.63f, 0.6f, 0f) },
        },
        Seed = ParticleSeed(identity),
        HasCollision = false,
    };

    /// <summary>A charge's debris: seeded cubes, thrown outward, falling, and settling on terrain.</summary>
    internal static PresentationParticleDescriptor Debris(Vector3 centre, RenderResourceReference sprite, ulong identity) => new()
    {
        SignalId = DebrisSignal,
        LogicalId = identity ^ DebrisIdentitySalt,
        Visible = true,
        Anchor = new PresentationAnchor
        {
            Kind = PresentationAnchorKind.World,
            Position = centre,
        },
        Sprite = sprite,
        SpriteFrameCount = SingleFrame,
        Visual = PresentationParticleVisual.Cube,
        BurstCount = DebrisParticles,
        RatePerSecond = 0f,
        MaxParticles = DebrisParticles,
        LifetimeMinSeconds = DebrisLifetimeSeconds * 0.7f,
        LifetimeMaxSeconds = DebrisLifetimeSeconds,
        VelocityMin = new Vector3(-5.5f, 1.5f, -5.5f),
        VelocityMax = new Vector3(5.5f, 6.5f, 5.5f),
        Acceleration = new Vector3(0f, -9.8f, 0f),
        SizeCurve = new PresentationParticleScalarKey[]
        {
            new() { Age = 0f, Value = 0.22f },
            new() { Age = 1f, Value = 0.16f },
        },
        ColorCurve = new PresentationParticleColorKey[]
        {
            new() { Age = 0f, Color = new Color(0.46f, 0.34f, 0.24f, 1f) },
            new() { Age = 1f, Color = new Color(0.4f, 0.3f, 0.22f, 1f) },
        },
        Seed = ParticleSeed(identity),
        HasCollision = true,
        Collision = new PresentationParticleCollision
        {
            Radius = 0.12f,
            Restitution = 0.25f,
            Friction = 0.55f,
            MaximumImpacts = 3,
            SleepSpeed = 0.5f,
            LimitBehavior = PresentationParticleCollisionLimitBehavior.Sleep,
        },
        CollisionVolumes = new PresentationParticleCollisionVolume[]
        {
            new()
            {
                Kind = PresentationParticleCollisionVolumeKind.Aabb,
                Minimum = centre - new Vector3(BlastCraterRadius, BlastCraterRadius, BlastCraterRadius),
                Maximum = centre + new Vector3(BlastCraterRadius, BlastCraterRadius, BlastCraterRadius),
            },
        },
    };
}
