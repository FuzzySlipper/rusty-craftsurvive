using System.Numerics;
using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>What a channel sifter does about a player it hunts (#10971).</summary>
internal enum PuffTurn
{
    /// <summary>Nothing yet: the player is beyond its puff's reach.</summary>
    None,

    /// <summary>It rears and puffs, in its display.</summary>
    Puffs,

    /// <summary>It rests from its last puff, and keeps away.</summary>
    KeepsAway,
}

/// <summary>
/// The channel sifter's spore mark (#10971), as settled with Patch on the wiki's Ruins wildlife page ("Channel sifter: its
/// spore mark"): seeing the player within its reach, a rested sifter rears and puffs in its display; the cloud bursts and
/// hangs where it burst; a player in it is marked, and the ruin hunters near a marked player come for them unseen.
/// Engine-free.
/// </summary>
internal static class MarkRules
{
    /// <summary>A cloud marks a player whose feet are within this height of where it burst, above or below.</summary>
    internal const float CloudDepthMetres = 2f;

    /// <param name="sincePuffedSeconds">Seconds since its last puff ended; null if it never has.</param>
    internal static PuffTurn Turn(WildlifeMarking marking, double? sincePuffedSeconds, double playerMetres)
    {
        ArgumentNullException.ThrowIfNull(marking);
        if (sincePuffedSeconds is double since && since < marking.AgainSeconds) return PuffTurn.KeepsAway;
        return playerMetres <= marking.ReachMetres ? PuffTurn.Puffs : PuffTurn.None;
    }

    /// <summary>Whether a player standing at <paramref name="playerFeet"/> is in a cloud that burst at <paramref name="cloudFeet"/> (both world metres, at feet height).</summary>
    internal static bool InCloud(WildlifeMarking marking, Vector3 cloudFeet, Vector3 playerFeet)
    {
        ArgumentNullException.ThrowIfNull(marking);
        Vector2 across = new(playerFeet.X - cloudFeet.X, playerFeet.Z - cloudFeet.Z);
        return across.Length() <= marking.CloudMetres / 2 && MathF.Abs(playerFeet.Y - cloudFeet.Y) <= CloudDepthMetres;
    }

    /// <summary>
    /// Whether a creature of this kind, this far from a marked player, is drawn to them: a ruin hunter, as answers a chime
    /// grazer (no tender, fixture or cave life), within the mark's reach.
    /// </summary>
    internal static bool Draws(WildlifeKind kind, double metres, double drawMetres) => TenderRules.Answers(kind) && metres <= drawMetres;
}
