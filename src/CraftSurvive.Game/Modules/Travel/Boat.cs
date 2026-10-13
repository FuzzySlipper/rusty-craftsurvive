using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Travel;

/// <summary>
/// What a craft does on the water (#9879). Speeds are through the water, in metres a second, before
/// the current; the map compresses them as it does walking (<see cref="TravelCostModel.JourneyScale"/>).
/// </summary>
/// <param name="Paddle">Its own speed through the water: a canoe paddled, a raft only poled and steered.</param>
/// <param name="Upstream">Whether it can go against the current at all; a raft only drifts down.</param>
/// <param name="Portages">Whether the party can carry it round rapids and falls.</param>
/// <param name="CarriesSled">Whether the sled rides on it; a canoe leaves the sled at the landing.</param>
/// <param name="BuildHours">Daylight hours of work to make one at a river bank.</param>
/// <param name="ModelPath">Its prop mesh (scripts/generate-boats.py), waterline at its origin.</param>
/// <param name="WaveLimit">The highest waves (significant height, metres) it crosses open water in (#9902); past it the party is windbound.</param>
internal sealed record BoatSpec(BoatKind Kind, string Name, double Paddle, bool Upstream, bool Portages, bool CarriesSled, double BuildHours, string ModelPath,
    double WaveLimit);

/// <summary>The craft and their rules for the river (#9879).</summary>
internal static class Boats
{
    internal static BoatSpec Raft { get; } = new(BoatKind.Raft, "raft", Paddle: 0.3, Upstream: false, Portages: false, CarriesSled: true, BuildHours: 8,
        "models/raft.prop-mesh.json", WaveLimit: 0.5);

    internal static BoatSpec Canoe { get; } = new(BoatKind.Canoe, "canoe", Paddle: 1.3, Upstream: true, Portages: true, CarriesSled: false, BuildHours: 20,
        "models/canoe.prop-mesh.json", WaveLimit: 0.4);

    internal static BoatSpec For(BoatKind kind) => kind == BoatKind.Raft ? Raft : Canoe;

    internal static bool TryFind(string name, out BoatSpec spec)
    {
        spec = name switch { "raft" => Raft, "canoe" => Canoe, _ => null! };
        return spec is not null;
    }

    /// <summary>A boat is built on, and boarded from, the bank of a river at least this large: a canoe river (#9875).</summary>
    internal const double LeastFlow = WaterNetwork.CanoeFlow;

    /// <summary>A boat is built within this distance of such a river's channel.</summary>
    internal const double BuildReachMetres = 400;

    /// <summary>
    /// The party takes a boat up, as it does the sled, from this close: it lies at the water's edge, and the
    /// party walks down to it as the first leg of a journey.
    /// </summary>
    internal const float ReachMetres = 500;

    /// <summary>
    /// Against a current faster than a craft's paddling less this, the party lines it from the bank
    /// instead: hauling on a rope at this pace (m/s), whatever the current.
    /// </summary>
    internal const double LiningSpeed = 0.35;

    /// <summary>Carrying a canoe round rapids, the party walks this many times slower than on open ground.</summary>
    internal const double PortageMultiplier = 2;

    /// <summary>Loading, launching and landing take this many daylight hours at each end of a run on the water.</summary>
    internal const double LaunchHours = 0.5;

    /// <summary>A river's speed follows its flow to this power (Manning's velocity in a wide channel).</summary>
    internal const double VelocityExponent = 0.4;

    /// <summary>A reach's current now (m/s): its mean velocity, faster as it carries more than its mean.</summary>
    internal static double Current(Reach reach, double flowRatio) => reach.Current * Math.Pow(Math.Max(0, flowRatio), VelocityExponent);

    /// <summary>
    /// Going up, a canoe keeps to the slack water by the banks and the eddies behind bends, where the
    /// current runs at about this share of the channel's mean; going down it keeps to the fast middle.
    /// </summary>
    internal const double UpstreamCurrentShare = 0.5;

    /// <summary>A craft's speed over the ground on a reach (m/s), with the current or against it; zero where it cannot go.</summary>
    internal static double Speed(BoatSpec boat, double current, bool downstream)
    {
        if (downstream) return boat.Paddle + current;
        if (!boat.Upstream) return 0;
        return Math.Max(boat.Paddle - (current * UpstreamCurrentShare), LiningSpeed);
    }

    /// <summary>Whether a craft going up against a current is lined from the bank rather than paddled.</summary>
    internal static bool Lined(BoatSpec boat, double current) => boat.Paddle - (current * UpstreamCurrentShare) < LiningSpeed;

    /// <summary>Map hours for a run of water at a speed over the ground: the same time compression as walking.</summary>
    internal static double MapHours(double metres, double metresPerSecond) =>
        metresPerSecond <= 0 ? double.PositiveInfinity : metres / (metresPerSecond * 3600) * TravelCostModel.JourneyScale;
}

/// <summary>The expedition's boat (#9879): its kind and where it lies, moored at a bank or afloat with the party.</summary>
internal sealed class Boat(BoatSpec spec, Vector2 position)
{
    internal BoatSpec Spec { get; } = spec;

    internal Vector2 Position { get; private set; } = position;

    internal bool Within(Vector2 at) => Vector2.Distance(at, Position) <= Boats.ReachMetres;

    internal void MoveTo(Vector2 position) => Position = position;
}
