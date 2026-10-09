namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// Which fronts have reached a travelling party (#9739), so each arrival stops the journey once:
/// every front that comes to cover the party is offered its own shelter-or-press-on choice, however
/// strong another front over it is, and a front already announced is not announced again while it
/// still covers the party - only after it has passed and returns.
/// </summary>
internal sealed class FrontArrivals
{
    /// <summary>A front arrives when it covers the party at least this strongly.</summary>
    internal const double ArrivalCover = 0.3;
    /// <summary>A front has passed once it covers the party less than this.</summary>
    internal const double PassedCover = 0.1;

    private readonly HashSet<FrontKey> present = [];

    /// <summary>
    /// The front that has newly arrived over the party in this sample, if any. When several arrive at
    /// once, one that harms comes first, then the strongest; the rest are offered on later samples.
    /// </summary>
    internal WeatherFront? Arrived(EnvironmentSample sky)
    {
        ArgumentNullException.ThrowIfNull(sky);
        present.RemoveWhere(key => !sky.Fronts.Any(over => over.Front.Key == key && over.Cover >= PassedCover));
        FrontPresence[] arriving = [.. sky.Fronts
            .Where(over => over.Cover >= ArrivalCover && !present.Contains(over.Front.Key))
            .OrderByDescending(over => over.Front.Kind.Effects.Harm > 0)
            .ThenByDescending(over => over.Cover)];
        if (arriving.Length == 0) return null;
        present.Add(arriving[0].Front.Key);
        return arriving[0].Front;
    }
}
