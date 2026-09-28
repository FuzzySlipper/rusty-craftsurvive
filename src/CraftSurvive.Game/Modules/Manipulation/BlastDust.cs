using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// The smoke and debris that cover a charge's remesh.
///
/// This is what makes a blast read as instantaneous rather than as a hitch: the edit costs a
/// measured ~240 ms inside the Engine, and the presentation is the reason the player reads that
/// as an event instead of a stall. Lifetimes are deliberately longer than the edit so the cloud
/// is still moving when the world reappears.
///
/// Everything here is a pure function of the charge - a centre and a seed - so the same blast
/// produces the same dust. The product's generation contract rests on draws being pure functions
/// of a seed, and an effect that broke that habit would be the first thing in the product to do
/// so.
/// </summary>
internal static class BlastDust
{
    /// <summary>Dust puffs: enough to hide a crater, few enough to stay cheap.</summary>
    internal const int SmokeParticles = 240;

    /// <summary>Thrown blocks: the debris that says something solid came apart.</summary>
    internal const int DebrisParticles = 48;

    /// <summary>How long dust lives, in seconds. Longer than the measured edit, on purpose.</summary>
    internal const float SmokeLifetimeSeconds = 1.2f;

    internal const float DebrisLifetimeSeconds = 0.9f;

    /// <summary>Half-extent of the box debris collides inside: roughly the crater a blast opens.</summary>
    private const float BlastCraterRadius = 3.5f;

    private const string SmokeSignal = "craftsurvive.blast.smoke";

    private const string DebrisSignal = "craftsurvive.blast.debris";

    /// <summary>A charge's dust: a seeded burst of billboards that drifts up and fades out.</summary>
    internal static PresentationParticleDescriptor Smoke(Vector3 centre, RenderResourceReference sprite, ulong seed) => new()
    {
        SignalId = SmokeSignal,
        LogicalId = seed,
        Visible = true,
        Anchor = new PresentationAnchor
        {
            Kind = PresentationAnchorKind.World,
            Position = centre,
        },
        Sprite = sprite,
        Visual = PresentationParticleVisual.Billboard,
        BurstCount = SmokeParticles,
        RatePerSecond = 0f,
        MaxParticles = SmokeParticles,
        LifetimeMinSeconds = SmokeLifetimeSeconds * 0.6f,
        LifetimeMaxSeconds = SmokeLifetimeSeconds,
        VelocityMin = new Vector3(-3.2f, 0.6f, -3.2f),
        VelocityMax = new Vector3(3.2f, 4.4f, 3.2f),
        Acceleration = new Vector3(0f, -1.2f, 0f),
        SizeCurve = new PresentationParticleScalarKey[]
        {
            new() { Age = 0f, Value = 0.35f },
            new() { Age = 0.25f, Value = 1f },
            new() { Age = 1f, Value = 1.6f },
        },
        ColorCurve = new PresentationParticleColorKey[]
        {
            new() { Age = 0f, Color = new Color(0.62f, 0.58f, 0.52f, 0.95f) },
            new() { Age = 0.6f, Color = new Color(0.55f, 0.52f, 0.48f, 0.55f) },
            new() { Age = 1f, Color = new Color(0.5f, 0.48f, 0.45f, 0f) },
        },
        Seed = seed,
        HasCollision = false,
    };

    /// <summary>A charge's debris: seeded cubes, thrown outward, falling, and settling on terrain.</summary>
    internal static PresentationParticleDescriptor Debris(Vector3 centre, RenderResourceReference sprite, ulong seed) => new()
    {
        SignalId = DebrisSignal,
        LogicalId = seed ^ 0x9E37_79B9_7F4A_7C15UL,
        Visible = true,
        Anchor = new PresentationAnchor
        {
            Kind = PresentationAnchorKind.World,
            Position = centre,
        },
        Sprite = sprite,
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
        Seed = seed,
        HasCollision = true,
        Collision = new PresentationParticleCollision
        {
            Radius = 0.12f,
            Restitution = 0.25f,
            Friction = 0.55f,
            MaximumImpacts = 3,
            SleepSpeed = 0.5f,
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
