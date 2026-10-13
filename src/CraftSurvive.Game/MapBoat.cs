using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Places;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game;

/// <summary>
/// The expedition's boat (#9879). Built at the bank of a river a canoe could take - a raft in a day's
/// work, a canoe in two - it lies moored at the water's edge. On the map it goes with the party when the
/// party stands near it, as the sled does: a route is then planned over the water too (<see cref="RiverRouter"/>)
/// and taken if it beats walking, and the boat is left moored at the landing. A canoe leaves the sled
/// at the put-in; a raft carries it. In first person it floats where it lies, and the player boards it.
/// </summary>
public sealed partial class CraftSurviveProduct
{
    private BoatStore boat = null!;

    /// <summary>Whether the boat travels with the party on this map visit.</summary>
    private bool boatWithParty;

    /// <summary>The mooring is searched along the line from the open water to the bank in steps this long (metres).</summary>
    private const float MooringStepMetres = 2;

    /// <summary>A moored boat lies this far off the bank beyond half its beam (metres); the beams are the models'.</summary>
    private const float MooringClearance = 1, RaftHalfBeam = 1.15f, CanoeHalfBeam = 0.39f;

    private WaterNetwork Network => MapRegions.For(worlds.Current.Map).Drainage.Network;

    private ReachStage? StageOf(int reach) => riverStage.At(reach);

    /// <summary>Builds a boat at the river beside the party: the hours pass, and it lies moored at the water's edge.</summary>
    private void BuildBoat(string kind)
    {
        if (!Boats.TryFind(kind, out BoatSpec spec)) throw new FormatException("Build a raft or a canoe.");
        if (player.InSeparateSpace) throw new FormatException("Return to the overworld to build a boat.");
        if (!worlds.Current.Map.Scale.Continental) throw new FormatException("Only a continent's rivers carry boats.");
        if (party is { State: TravelState.Travelling }) throw new FormatException("Stop before building a boat.");
        Vector2 at = party?.Position ?? PlayerGround;
        if (RiverRouter.NearestOpenWater(TravelCost, Network, spec, StageOf, at, Boats.BuildReachMetres) is not Vector2 water)
            throw new FormatException(FormattableString.Invariant($"Build a boat within {Boats.BuildReachMetres:F0} m of a river a canoe could take, open (not frozen, dry or in flood)."));
        LoseHours(spec.BuildHours);
        (Vector2 moored, double heading) = Mooring(water, at, spec.Kind);
        boat.Put(spec, moored, heading);
        boat.Save();
        boatWithParty = true;
        RefreshPlaces();
        worldMessage = string.Create(CultureInfo.InvariantCulture,
            $"Built a {spec.Name} in {spec.BuildHours:F0} hours; it is moored {Vector2.Distance(at, moored):F0} m away. {(spec.CarriesSled ? "It drifts downstream only, and carries the sled." : "It paddles up and down, and is carried round rapids; the sled stays at the landing.")}");
        if (party is { State: TravelState.Planned or TravelState.Paused, Route: TravelRoute route }) PlanTravel(route.Points[^1], route.Destination);
    }

    /// <summary>
    /// Where a boat lies at the water's edge between open water and a bank: the last point, walking from
    /// the water toward the bank, where it still floats; its bow along the current there, or along the bank.
    /// </summary>
    private (Vector2 At, double Heading) Mooring(Vector2 water, Vector2 bank, BoatKind kind)
    {
        // A network node can lie off the walked channel: start from the water nearest it.
        water = FloatingNear(water, Boats.For(kind)) ?? water;
        Vector2 toward = bank - water;
        float length = toward.Length();
        Vector2 step = length > 0 ? toward / length : Vector2.UnitX;
        Vector2 at = water;
        BoatFloat test = new(Boats.Canoe, water.X, water.Y, 0);
        for (float along = MooringStepMetres; along < length; along += MooringStepMetres)
        {
            Vector2 next = water + (step * along);
            if (!test.Floats(boat.WaterAt(next.X, next.Y))) break;
            at = next;
        }

        // Lying along the bank, its side faces it: stand it off by half its beam and a little more.
        float off = Math.Min(Vector2.Distance(water, at), (kind == BoatKind.Raft ? RaftHalfBeam : CanoeHalfBeam) + MooringClearance);
        at -= step * off;
        FloatWater here = boat.WaterAt(at.X, at.Y);
        Vector2 along2 = Math.Abs(here.CurrentX) + Math.Abs(here.CurrentZ) > 0.01 ? new((float)here.CurrentX, (float)here.CurrentZ) : new(step.Y, -step.X);
        return (at, Math.Atan2(-along2.Y, along2.X));
    }

    /// <summary>On opening the map, the boat joins the party when the party stands near it.</summary>
    private void TakeBoatIfNear()
    {
        if (party is null || party.State == TravelState.Travelling) return;
        bool near = boat.Boat?.Within(party.Position) == true;
        if (near == boatWithParty) return;
        boatWithParty = near;
        RefreshPlaces();
    }

    /// <summary>
    /// Plans the party's route to a destination as it now travels (#9879 review): on foot, and by water too
    /// when its boat is with it, taking whichever is quicker. Every plan - chosen, resumed, or redone because
    /// the sled joined or left - comes through here. Returns whether a route was planned, and the boat's verdict.
    /// </summary>
    private (bool Planned, string? ByBoat) PlanForParty(Vector2 destination, string name)
    {
        if (party is null) return (false, null);
        bool planned = party.Plan(PartyCost, destination, name);
        string? byBoat = PlanByBoat(destination, name);
        return (planned || party.Route?.ByBoat == true, byBoat);
    }

    /// <summary>
    /// The plan by boat, when the party has one (#9879): taken if the river beats walking. A canoe leaves
    /// the sled where it is; a raft carries it, so walking is weighed with the sled's load.
    /// </summary>
    private string? PlanByBoat(Vector2 destination, string name)
    {
        if (!boatWithParty || party is null || boat.Boat is not Boat craft) return null;
        bool hauling = sledWithParty && craft.Spec.CarriesSled;
        RiverPlan plan = RiverRouter.Plan(hauling ? PartyCost : TravelCost, Network, craft.Spec, craft.Position, StageOf, party.Position, destination, name,
            hauling ? party.LoadMultiplier : 1);
        lastRiverPlan = plan;
        if (plan.Route is not TravelRoute route) return plan.Reason;
        if (sledWithParty && !craft.Spec.CarriesSled)
        {
            // The canoe cannot take the sled: it stays where the party set out from.
            sledWithParty = false;
            ApplySledLoad();
            RefreshPlaces();
        }

        party.Take(route);
        return plan.Reason;
    }

    private RiverPlan? lastRiverPlan;

    /// <summary>
    /// The boat follows the party on the water, and is left moored at the landing once the party walks
    /// on; it is saved whenever the journey stops.
    /// </summary>
    private void FollowBoat()
    {
        if (!boatWithParty || party?.Route is not TravelRoute { ByBoat: true } route) return;
        if (party.Afloat)
        {
            int leg = party.Leg;
            Vector2 along = route.Points[leg + 1] - route.Points[leg];
            boat.MoveTo(party.Position, Math.Atan2(-along.Y, along.X));
            return;
        }

        int lastAfloat = -1;
        for (int leg = 0; leg < route.LegHours.Length; leg++)
            if (route.Afloat(leg)) lastAfloat = leg;
        if (lastAfloat < 0 || party.Leg <= lastAfloat) return;
        Vector2 landing = route.Points[lastAfloat + 1];
        Vector2 ashore = route.Points[Math.Min(lastAfloat + 2, route.Points.Length - 1)];
        (Vector2 moored, double heading) = Mooring(landing, ashore, boat.Boat?.Spec.Kind ?? BoatKind.Canoe);
        boat.MoveTo(moored, heading);
        boat.Save();
        boatWithParty = false;
        RefreshPlaces();
    }

    /// <summary>
    /// A journey stopped on the water puts the player aboard, seated, where the party is (#9879): the boat
    /// floats there and the player paddles on in first person. Returns false when the party is ashore.
    /// </summary>
    private bool SettleAfloat()
    {
        if (!boatWithParty || party is not { Afloat: true } || boat.Boat is not Boat craft) return false;
        // The map's water follows the network's 250 m nodes; the walked channel wanders off them, so the boat
        // takes to the nearest water that floats it.
        if (FloatingNear(party.Position, craft.Spec) is not Vector2 afloat) return false;
        boat.MoveTo(afloat, boat.Float?.Heading ?? 0);
        if (boat.Seat() is not Vector3 deck) return false;
        if (player.Teleport(deck.X, deck.Y, deck.Z) is null) return false;
        boat.Save();
        // A raft carries the sled: it stops where the raft does, so it stays with the expedition (#9879 review).
        if (sledWithParty)
        {
            sled.Sled.MoveTo(afloat);
            sled.Save();
        }

        return true;
    }

    /// <summary>A stopped party's boat looks for water this far from where the map put it (metres), in steps this long.</summary>
    private const float FloatingSearchMetres = 160, FloatingSearchStep = 4;

    /// <summary>The nearest point within the search distance where the craft floats, or null.</summary>
    private Vector2? FloatingNear(Vector2 at, BoatSpec spec)
    {
        BoatFloat test = new(spec, at.X, at.Y, 0);
        for (float radius = 0; radius <= FloatingSearchMetres; radius += FloatingSearchStep)
        {
            int around = Math.Max(1, (int)(2 * Math.PI * radius / FloatingSearchStep));
            for (int k = 0; k < around; k++)
            {
                double angle = 2 * Math.PI * k / around;
                Vector2 point = at + new Vector2((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)));
                if (test.Floats(boat.WaterAt(point.X, point.Y))) return point;
            }
        }

        return null;
    }

    /// <summary>The boat in the first-person snapshot, near enough to be seen.</summary>
    private AppearanceFact[] BoatFacts() => player.InSeparateSpace ? [] : boat.Facts(PlayerGround, (x, z) => (float)terrain.GroundAt(x, z));

    /// <summary>Where the boat lies, for the map's places, when it is not with the party.</summary>
    private (string Name, Vector2 At)? BoatPlace() =>
        boatWithParty || boat.Boat is not Boat craft ? null : ($"The {craft.Spec.Name}", craft.Position);

    internal string BoatReadout() => string.Create(CultureInfo.InvariantCulture,
        $"{boat.Readout} withParty={boatWithParty} aboard={player.Aboard} lastPlan=[{lastRiverPlan?.Reason ?? "none"}] route=[{RouteModes(party?.Route)}]");

    /// <summary>A route's legs by how they are made: metres and daylight hours of each mode.</summary>
    private static string RouteModes(TravelRoute? route)
    {
        if (route is null) return "none";
        Dictionary<LegMode, (double Metres, double Hours)> modes = [];
        for (int leg = 0; leg < route.LegHours.Length; leg++)
        {
            (double metres, double hours) = modes.GetValueOrDefault(route.Mode(leg));
            modes[route.Mode(leg)] = (metres + Vector2.Distance(route.Points[leg], route.Points[leg + 1]), hours + route.LegHours[leg]);
        }

        return string.Join(' ', modes.Select(pair => string.Create(CultureInfo.InvariantCulture,
            $"{pair.Key}:{pair.Value.Metres / 1000:F1}km/{pair.Value.Hours:F1}h")));
    }

    private const string AnswerPrefix = "answer:";

    /// <summary>Assisted: the journey's controls, as the map's buttons do.</summary>
    internal string DebugTravel(string action, long speed)
    {
        if (!mapOpen) return "refused: open the map first";
        if (speed != 0) SetTravelSpeed((int)speed);
        // An event's answer, as its buttons give it: "answer:<choice>".
        if (action.StartsWith(AnswerPrefix, StringComparison.Ordinal)) ResolveTravelEvent(action[AnswerPrefix.Length..]);
        else TravelAction(action);
        return $"{worldMessage} | {TravelReadout()}";
    }

    /// <summary>Assisted: holds a paddle while seated, for captures.</summary>
    internal string DebugPaddle(double forward, double turn, double seconds)
    {
        if (!boat.Seated) return $"refused: board the boat first. {BoatReadout()}";
        boat.HoldPaddle(new((float)turn, (float)forward), seconds, Modules.Player.PlayerConstants.ControllerStepSeconds);
        return BoatReadout();
    }

    /// <summary>Assisted: the board/stand action, as the climb key does.</summary>
    internal string DebugBoard()
    {
        if (!boat.TryToggle(player.LocalPosition, out Vector3? at)) return $"refused: stand within reach of the boat. {BoatReadout()}";
        if (at is Vector3 deck && player.Teleport(deck.X, deck.Y, deck.Z) is null)
        {
            boat.TryToggle(player.LocalPosition, out _);
            return $"refused: no room to stand aboard. {BoatReadout()}";
        }

        return BoatReadout();
    }

    /// <summary>Assisted: builds a boat at the river beside the player, as the map's action does.</summary>
    internal string DebugBuildBoat(string kind)
    {
        party ??= new PartyTravel(PlayerGround);
        if (party.State != TravelState.Travelling) party.Relocate(PlayerGround);
        BuildBoat(kind);
        return $"{worldMessage} {BoatReadout()}";
    }
}
