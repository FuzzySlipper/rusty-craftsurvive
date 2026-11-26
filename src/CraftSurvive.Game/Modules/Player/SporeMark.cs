using System.Globalization;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// The channel sifter's spore mark on the player (#10971), as settled with Patch on the wiki's Ruins wildlife page
/// ("Channel sifter: its spore mark"): a player in a sifter's cloud is marked for a while, and the creature rules draw ruin
/// hunters to a marked player; water washes it off at once. The sifters decide when; this owns the player's side, and the
/// player's hazards step it. Engine-free.
/// </summary>
internal sealed class SporeMark
{
    /// <summary>How long the mark has left, in seconds; 0 while the player is unmarked.</summary>
    internal double Left { get; private set; }

    /// <summary>How long the mark lasted when it was last given, for the share of it left.</summary>
    internal double Full { get; private set; }

    /// <summary>The sifter whose cloud marked the player last, by creature id.</summary>
    internal int? By { get; private set; }

    internal bool Marked => Left > 0;

    /// <summary>How much of the mark is left, 0 to 1.</summary>
    internal double Share => Full > 0 ? Math.Clamp(Left / Full, 0, 1) : 0;

    /// <summary>How many times the player has been marked, and how many times water washed the mark off.</summary>
    internal long Marks { get; private set; }

    internal long Washes { get; private set; }

    /// <summary>A sifter's cloud marks the player for <paramref name="seconds"/>; marked again, it runs from the start.</summary>
    internal void Mark(int by, double seconds)
    {
        if (seconds <= 0) return;
        if (!Marked) Marks++;
        Left = seconds;
        Full = seconds;
        By = by;
    }

    /// <summary>One step: in water the mark washes off at once; otherwise it wears off.</summary>
    internal void Step(double seconds, bool inWater)
    {
        if (!Marked) return;
        if (inWater)
        {
            Left = 0;
            Washes++;
            return;
        }

        Left = Math.Max(0, Left - seconds);
    }

    /// <summary>Forgets the mark (a restart, or the player going down).</summary>
    internal void Clear()
    {
        Left = 0;
        By = null;
    }

    internal string Readout() => Marked
        ? string.Create(CultureInfo.InvariantCulture, $"marked by {By} {Left:F1}s left (marks={Marks} washes={Washes})")
        : string.Create(CultureInfo.InvariantCulture, $"unmarked (marks={Marks} washes={Washes})");
}
