using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>What an implement-carrier's drink does this update (#10966).</summary>
internal enum DrinkTurn
{
    /// <summary>Nothing to drink, or it rests from its last drink.</summary>
    None,

    /// <summary>It raises its crescent (or its display is already raised) at the lantern: the lantern starts to gutter.</summary>
    Begins,

    /// <summary>The raise goes on, and the lantern gutters.</summary>
    Holds,

    /// <summary>The lantern left its reach or its sight before the end: the drink fails, and the lantern steadies.</summary>
    Fails,

    /// <summary>The raise ended whole: the lantern goes out.</summary>
    Drinks,
}

/// <summary>
/// When an implement-carrier drinks the player's lantern (#10966), as settled with Patch on the wiki's Ruins wildlife page
/// ("Its crescent drinks light"). It drinks in its display: the display it shows when its hunt starts, or one it raises
/// afresh while it hunts, at most every <see cref="WildlifeDrinking.AgainSeconds"/>, at a lit, steady lantern within its
/// reach and in its sight. The lantern gutters for the rest of the raise, and the drink is whole when the raise ends with
/// the lantern still in reach and in sight. A blow during the raise cuts it short (the creature module's, as with the
/// chime grazer's ring). Engine-free.
/// </summary>
internal static class DrinkRules
{
    /// <summary>A display already raised is drunk from only with this long left, so the lantern's gutter is still slow enough to see before the dark.</summary>
    internal const double LeastGutterSeconds = 2.0;

    /// <param name="raising">Whether a drink is under way.</param>
    /// <param name="hunting">Whether it hunts the player (alert, pursuing or attacking).</param>
    /// <param name="displaySeconds">How long its display has left; 0 when none shows.</param>
    /// <param name="busy">Biting or flinching: no fresh raise now.</param>
    /// <param name="sinceDrank">Seconds since its last drink ended, whole or not; null if it never has.</param>
    /// <param name="drinkable">The lantern is carried lit and burns steady.</param>
    /// <param name="carried">The lantern is carried lit, whatever a drink has done to it.</param>
    internal static DrinkTurn Turn(WildlifeDrinking drinks, bool raising, bool hunting, double displaySeconds, bool busy, double? sinceDrank,
        bool drinkable, bool carried, double playerMetres, bool seesPlayer)
    {
        ArgumentNullException.ThrowIfNull(drinks);
        bool inReach = playerMetres <= drinks.ReachMetres && seesPlayer;
        if (raising)
        {
            if (!carried || !inReach) return DrinkTurn.Fails;
            return displaySeconds > 0 ? DrinkTurn.Holds : DrinkTurn.Drinks;
        }

        if (!hunting || !drinkable || !inReach) return DrinkTurn.None;
        if (sinceDrank is double since && since < drinks.AgainSeconds) return DrinkTurn.None;
        // A display under way is drunk from if enough of it is left; otherwise it raises a fresh one, unless busy.
        if (displaySeconds > 0) return displaySeconds >= LeastGutterSeconds ? DrinkTurn.Begins : DrinkTurn.None;
        return busy ? DrinkTurn.None : DrinkTurn.Begins;
    }
}
