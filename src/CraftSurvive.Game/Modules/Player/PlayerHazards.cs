using System.Numerics;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// What the ruin's fixtures do to the player's body (#10963), as settled with Patch on the wiki's Ruins wildlife page
/// ("Fixtures as hazards"): a niche unfolder seizes them, holding them in place and drawing them toward its recess, until
/// a sprint against it, spending stamina, breaks them free; a vault filter numbs them, emptying their stamina and keeping
/// it from coming back, so they cannot sprint, climb or take hold. A channel sifter's cloud marks them (#10971,
/// <see cref="Spores"/>). The creatures decide when; this owns the player's side, and the controller steps and reads it.
/// Engine-free.
/// </summary>
internal sealed class PlayerHazards
{
    /// <summary>Within this far of where it draws them, a seized player is drawn no further.</summary>
    internal const float DrawnCloseMetres = 0.4f;

    private Vector3 drawTo;
    private float drawSpeed;
    private double struggled;
    private double struggleNeeded;
    private float struggleStamina;

    /// <summary>The channel sifter's spore mark (#10971), which draws ruin hunters to the player until it wears or washes off.</summary>
    internal SporeMark Spores { get; } = new();

    /// <summary>The fixture holding the player, by creature id; null while nothing does.</summary>
    internal int? SeizedBy { get; private set; }

    /// <summary>The player broke free by sprinting against the hold since it was last read.</summary>
    internal bool BrokeFree { get; private set; }

    /// <summary>How long the player's numbing has left, in seconds.</summary>
    internal double NumbSeconds { get; private set; }

    internal bool Numb => NumbSeconds > 0;

    /// <summary>How far the player has struggled toward breaking free, 0 to 1.</summary>
    internal double Struggle => struggleNeeded > 0 ? Math.Min(1, struggled / struggleNeeded) : 0;

    /// <summary>
    /// A fixture seizes the player: held in place, drawn toward <paramref name="toward"/> (world metres) at
    /// <paramref name="drawMetresPerSecond"/>, until released or until they have sprinted against it for
    /// <paramref name="struggleSeconds"/>, spending <paramref name="staminaPerSecond"/> while they do.
    /// </summary>
    internal void Seize(int by, Vector3 toward, float drawMetresPerSecond, double struggleSeconds, float staminaPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(drawMetresPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(struggleSeconds);
        SeizedBy = by;
        drawTo = toward;
        drawSpeed = drawMetresPerSecond;
        struggled = 0;
        struggleNeeded = struggleSeconds;
        struggleStamina = staminaPerSecond;
        BrokeFree = false;
    }

    /// <summary>The hold ends: the fixture let go, or was struck.</summary>
    internal void Release() => SeizedBy = null;

    /// <summary>Whether the player broke free since this was last asked; asking clears it.</summary>
    internal bool TakeBrokeFree()
    {
        bool broke = BrokeFree;
        BrokeFree = false;
        return broke;
    }

    /// <summary>A fixture numbs the player: their stamina empties and stays empty for <paramref name="seconds"/>.</summary>
    internal void Numbs(double seconds, PlayerStamina stamina)
    {
        ArgumentNullException.ThrowIfNull(stamina);
        NumbSeconds = Math.Max(NumbSeconds, seconds);
        stamina.Empty();
        stamina.Numbed = true;
    }

    /// <summary>
    /// One controller step: the numbing and the spore mark wear off, and water washes the mark off; a seized player
    /// sprinting against the hold spends stamina and struggles, breaking free once they have struggled long enough. A spent
    /// player cannot struggle.
    /// </summary>
    internal void Step(double seconds, bool sprinting, bool inWater, PlayerStamina stamina)
    {
        ArgumentNullException.ThrowIfNull(stamina);
        Spores.Step(seconds, inWater);
        if (NumbSeconds > 0)
        {
            NumbSeconds = Math.Max(0, NumbSeconds - seconds);
            stamina.Numbed = NumbSeconds > 0;
        }

        if (SeizedBy is null || !sprinting || stamina.Exhausted) return;
        stamina.Spend(struggleStamina * (float)seconds);
        struggled += seconds;
        if (struggled < struggleNeeded) return;
        SeizedBy = null;
        BrokeFree = true;
    }

    /// <summary>How a seized player's body moves this step: no way of their own, drawn toward the hold's point; null while free.</summary>
    internal Vector3? Draw(Vector3 playerWorld)
    {
        if (SeizedBy is null) return null;
        Vector3 offset = drawTo - playerWorld;
        offset.Y = 0;
        float distance = offset.Length();
        return distance <= DrawnCloseMetres ? Vector3.Zero : offset / distance * drawSpeed;
    }

    /// <summary>For the readout.</summary>
    internal string Readout() => SeizedBy is int by
        ? $"seized by {by} struggle={Struggle:F2} numb={NumbSeconds:F1}s spores=[{Spores.Readout()}]"
        : $"free numb={NumbSeconds:F1}s spores=[{Spores.Readout()}]";

    /// <summary>Forgets every hazard (a restart, or the player going down).</summary>
    internal void Clear(PlayerStamina stamina)
    {
        ArgumentNullException.ThrowIfNull(stamina);
        SeizedBy = null;
        BrokeFree = false;
        NumbSeconds = 0;
        stamina.Numbed = false;
        Spores.Clear();
    }
}
