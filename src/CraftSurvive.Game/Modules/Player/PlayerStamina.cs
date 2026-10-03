namespace CraftSurvive.Game.Modules.Player;

/// <summary>What the player's body is doing, as far as stamina is concerned.</summary>
internal enum PlayerExertion
{
    /// <summary>Standing on the ground: stamina comes back.</summary>
    Resting,

    /// <summary>In the air, neither held nor standing: stamina neither drains nor comes back.</summary>
    Airborne,

    /// <summary>Holding a face without moving along it.</summary>
    Hanging,

    /// <summary>Moving up or down a face.</summary>
    Climbing,
}

/// <summary>
/// The player's stamina: what a climb spends. Holding a face drains it, faster while moving along
/// the face; it comes back only on the ground, so a climber who runs out lets go and cannot take
/// hold again until they have stood and rested. Taking hold needs a share of the bar, so a nearly
/// spent climber cannot catch a face on the way down. The bar's size is the character's
/// (<see cref="Rpg.DerivedStatistics.MaximumStamina"/>), and with it the tallest face a full bar
/// climbs (<see cref="ClimbReachMetres"/>), which is what a layout uses to put a place out of reach.
/// </summary>
internal sealed class PlayerStamina
{
    /// <summary>Stamina spent per second while moving up or down a face.</summary>
    internal const float ClimbDrainPerSecond = 10f;

    /// <summary>Stamina spent per second while holding a face without moving.</summary>
    internal const float HangDrainPerSecond = 5f;

    /// <summary>Stamina regained per second while standing on the ground.</summary>
    internal const float RecoveryPerSecond = 12f;

    /// <summary>The share of a full bar needed to take hold of a face.</summary>
    internal const float TakeHoldShare = 0.25f;

    /// <summary>How much up-or-down intent counts as climbing rather than hanging.</summary>
    internal const float ClimbingIntent = 0.1f;

    internal PlayerStamina(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        Maximum = maximum;
        Current = maximum;
    }

    internal int Maximum { get; private set; }

    internal float Current { get; private set; }

    /// <summary>Whether there is enough to take hold of a face.</summary>
    internal bool CanTakeHold => Current >= Maximum * TakeHoldShare;

    /// <summary>Whether a climber has nothing left to hold on with.</summary>
    internal bool Exhausted => Current <= 0f;

    internal bool Full => Current >= Maximum;

    /// <summary>The tallest face a full bar of this size climbs, moving up it the whole way.</summary>
    internal static double ClimbReachMetres(int maximum) => maximum / ClimbDrainPerSecond * PlayerConstants.ClimbSpeed;

    /// <summary>What a body on a face is doing: climbing while it means to move along it, else hanging.</summary>
    internal static PlayerExertion OnFace(float verticalIntent) =>
        MathF.Abs(verticalIntent) > ClimbingIntent ? PlayerExertion.Climbing : PlayerExertion.Hanging;

    /// <summary>Spends or regains stamina for a span of time spent exerting the body one way.</summary>
    internal void Tick(PlayerExertion exertion, double seconds)
    {
        float change = exertion switch
        {
            PlayerExertion.Climbing => -ClimbDrainPerSecond,
            PlayerExertion.Hanging => -HangDrainPerSecond,
            PlayerExertion.Resting => RecoveryPerSecond,
            _ => 0f,
        };
        Current = Math.Clamp(Current + (change * (float)seconds), 0f, Maximum);
    }

    /// <summary>A full bar, of a new size when the character's has changed.</summary>
    internal void Refill(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        Maximum = maximum;
        Current = maximum;
    }
}
