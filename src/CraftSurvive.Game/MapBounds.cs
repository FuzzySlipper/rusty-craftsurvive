using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game;

/// <summary>
/// The world's bounds in play (#9817, owner decisions 2026-10-10). On a designed continent the expedition
/// is turned back past the frontier, just beyond the crest of the passes, and a short way out to sea; one
/// rule (<see cref="FrontierBound"/>) answers for every path. Map routes go no farther, a walk is held at
/// the line, and a teleport or arrival past it is refused.
/// </summary>
public sealed partial class CraftSurviveProduct
{
    /// <summary>A turn-back is told in first person for this long after the last one.</summary>
    private const double TurnedBackPromptSeconds = 6;

    private const string FrontierTurnBack = "Beyond the crest lies the mainland, and the frontier ends here: turn back";
    private const string OpenSeaTurnBack = "The open sea is no place for the expedition: turn back to the shore";
    private const string FrontierRouteNote = "The frontier ends at the passes; the route goes no farther than the crest.";
    private const string OpenSeaRouteNote = "The expedition does not cross the open sea; the route ends at the shore.";
    private const string FrontierRefusal = "That lies beyond the frontier.";
    private const string OpenSeaRefusal = "That lies out on the open sea.";

    private long turnBacksSeen;
    private double turnedBackSeconds = double.PositiveInfinity;

    /// <summary>The current world's bounds; null for a world without them.</summary>
    private FrontierBound? Bounds => FrontierBound.For(worlds.Current.Map);

    /// <summary>Holds the new world's player inside its bounds.</summary>
    private void BindBounds()
    {
        player.Bound = Bounds is FrontierBound bound ? bound.At : null;
        turnBacksSeen = 0;
        turnedBackSeconds = double.PositiveInfinity;
    }

    /// <summary>Times the first-person telling of the latest turn-back.</summary>
    private void NoteTurnBacks(double elapsedSeconds)
    {
        if (player.TurnBacks != turnBacksSeen)
        {
            turnBacksSeen = player.TurnBacks;
            turnedBackSeconds = 0;
        }
        else turnedBackSeconds += elapsedSeconds;
    }

    /// <summary>What first person says of a recent turn-back; null when there is none to tell.</summary>
    private string? TurnedBackPrompt() =>
        turnedBackSeconds > TurnedBackPromptSeconds || player.TurnedBack is not { } turned ? null
        : turned.Side == BoundSide.OpenSea ? OpenSeaTurnBack : FrontierTurnBack;

    /// <summary>What a route toward a destination past the bounds is told: where it stops instead; null within them.</summary>
    private string? RouteBoundNote(Vector2 destination) => Bounds?.At(destination.X, destination.Y) switch
    {
        BoundSide.BeyondFrontier => FrontierRouteNote,
        BoundSide.OpenSea => OpenSeaRouteNote,
        _ => null,
    };

    /// <summary>Why a point cannot be arrived at, teleported to or set out for; null within the bounds.</summary>
    internal string? BoundRefusal(double x, double z) => Bounds?.At(x, z) switch
    {
        BoundSide.BeyondFrontier => FrontierRefusal,
        BoundSide.OpenSea => OpenSeaRefusal,
        _ => null,
    };

    /// <summary>The bound at a world point, for the live check.</summary>
    private string BoundReadout(double x, double z) => Bounds is FrontierBound bound
        ? string.Create(CultureInfo.InvariantCulture, $"side={bound.At(x, z)};seaMetres={bound.SeaMetres(x, z):F0};turnBacks={player.TurnBacks};last={player.TurnedBack?.Side.ToString() ?? "none"}")
        : "side=Open;bounds=none";

    /// <summary>Plans a route to a world point as a map click does, for the live check.</summary>
    private string PlanReadout(double x, double z)
    {
        if (!mapOpen) return "refused: open the map first";
        try { PlanTravel(new((float)x, (float)z), "the waypoint"); }
        catch (FormatException refused) { return $"refused: {refused.Message}"; }
        PublishWorld();
        return party?.Route is TravelRoute route
            ? string.Create(CultureInfo.InvariantCulture, $"end={route.Points[^1].X:F0},{route.Points[^1].Y:F0};endSide={Bounds?.At(route.Points[^1].X, route.Points[^1].Y).ToString() ?? "Open"};message={worldMessage}")
            : $"no route;message={worldMessage}";
    }
}
