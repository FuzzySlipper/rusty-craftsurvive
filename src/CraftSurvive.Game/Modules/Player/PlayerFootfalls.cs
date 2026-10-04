namespace CraftSurvive.Game.Modules.Player;

/// <summary>What the body did in one controller step that can be heard.</summary>
[Flags]
internal enum BodySound
{
    None = 0,
    Footstep = 1,
    Jump = 2,
    Land = 4,
    HardLanding = 8,
    Splash = 16,
    Grip = 32,
}

/// <summary>One controller step's body facts, before and after, as far as sound is concerned.</summary>
internal readonly record struct BodyStep(
    bool GroundedBefore,
    bool Grounded,
    float FallSpeedBefore,
    float RiseSpeed,
    float PlanarMetres,
    float ImmersionBefore,
    float Immersion,
    bool HeldBefore,
    bool Held);

/// <summary>
/// Hears the player's body: a footfall every stride walked on the ground, a jump when the body
/// starts rising from the ground, a landing (hard after a long fall) when it comes back down, a
/// splash on going into water, and a grip on taking hold of a face. It keeps only the distance
/// walked since the last footfall.
/// </summary>
internal sealed class PlayerFootfalls
{
    /// <summary>Ground covered between footfalls.</summary>
    internal const float StrideMetres = 1.7f;

    /// <summary>The upward speed at which leaving the ground counts as a jump rather than stepping off.</summary>
    internal const float JumpRiseSpeed = 2f;

    /// <summary>The falling speeds at which coming down is heard, and heard as a hard landing.</summary>
    internal const float LandFallSpeed = 3f;
    internal const float HardLandingFallSpeed = 9f;

    /// <summary>How deep in water the body must go for a splash, and back out before another.</summary>
    internal const float SplashImmersion = 0.2f;

    private float walked;

    internal BodySound Hear(BodyStep step)
    {
        BodySound heard = BodySound.None;
        if (step.Grounded && step.GroundedBefore && step.Immersion < SplashImmersion)
        {
            walked += step.PlanarMetres;
            if (walked >= StrideMetres)
            {
                walked -= StrideMetres;
                heard |= BodySound.Footstep;
            }
        }

        // The step that launches a jump may still find the body on the ground, so a jump is the
        // body starting to rise from the ground, whatever the step says after.
        if (step.GroundedBefore && step.RiseSpeed >= JumpRiseSpeed && -step.FallSpeedBefore < JumpRiseSpeed)
        {
            heard |= BodySound.Jump;
        }

        if (!step.GroundedBefore && step.Grounded && step.FallSpeedBefore >= LandFallSpeed)
        {
            // A landing is a footfall too: the next stride starts here.
            walked = 0f;
            heard |= step.FallSpeedBefore >= HardLandingFallSpeed ? BodySound.HardLanding : BodySound.Land;
        }

        if (step.ImmersionBefore < SplashImmersion && step.Immersion >= SplashImmersion)
        {
            heard |= BodySound.Splash;
        }

        if (!step.HeldBefore && step.Held)
        {
            heard |= BodySound.Grip;
        }

        return heard;
    }

    /// <summary>A body set down somewhere new starts its stride afresh.</summary>
    internal void Reset() => walked = 0f;
}
