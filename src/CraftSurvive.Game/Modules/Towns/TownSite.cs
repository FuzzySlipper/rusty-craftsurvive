using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Towns;

/// <summary>
/// Where a town's frame stands in the world: its seat and pad height, the facing toward its water
/// (frame +X), and which side the arrival view takes. A mirrored town places its layout on the other
/// side of the water's line without mirroring any mesh, so pieces keep their handedness.
/// </summary>
internal readonly record struct TownFrame(double X, double Y, double Z, double FacingX, double FacingZ, bool Mirrored)
{
    /// <summary>A point in the town's frame, in world metres.</summary>
    internal (double X, double Y, double Z) World(Vector3 local)
    {
        double across = Mirrored ? -local.Z : local.Z;
        return (X + (local.X * FacingX) - (across * FacingZ), Y + local.Y, Z + (local.X * FacingZ) + (across * FacingX));
    }

    /// <summary>A direction in the town's frame, in the world.</summary>
    internal Vector3 Direction(Vector3 local)
    {
        float across = Mirrored ? -local.Z : local.Z;
        return new((float)((local.X * FacingX) - (across * FacingZ)), local.Y, (float)((local.X * FacingZ) + (across * FacingX)));
    }

    /// <summary>A piece's turn about +Y in the world, from its yaw (degrees) in the town's frame.</summary>
    internal Quaternion Turn(float yawDegrees)
    {
        // The frame's +X is the world's (FacingX, FacingZ): a turn of atan2(-FacingZ, FacingX) about +Y.
        double frameYaw = Math.Atan2(-FacingZ, FacingX);
        double yaw = (Mirrored ? 180 - yawDegrees : yawDegrees) * Math.PI / 180;
        return Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(frameYaw + yaw));
    }
}

/// <summary>
/// One settlement's town (#9895): its layout and stable identity choices (palette, landmark, names,
/// the people who keep its services), drawn from the settlement's key, so a revisit, a restored save
/// and another session all find the same place. Weather and visit order never reroll it.
/// </summary>
internal sealed record TownSite(Settlement Settlement, TownLayout Layout, TownFrame Frame, string Palette, string Landmark,
    IReadOnlyDictionary<string, string> ServiceNames, IReadOnlyDictionary<string, TownPerson> Keepers)
{
    /// <summary>The bubble's radius about the seat on the land side (m): the traveller is held at its edge.</summary>
    internal const double BubbleMetres = 28;

    /// <summary>How near the bubble's edge (m) the traveller is offered entry.</summary>
    internal const double DoorstepMetres = 6;

    /// <summary>How near the arrival point (m) the traveller is offered entry: a journey or a new game stands them there.</summary>
    internal const double ArrivalReachMetres = 4;

    private const ulong HashBasis = 0xCBF29CE484222325UL, HashPrime = 0x100000001B3UL;
    private const ulong MixGolden = 0x9E3779B97F4A7C15UL, MixFirst = 0xBF58476D1CE4E5B9UL, MixSecond = 0x94D049BB133111EBUL;

    internal string Key => Settlement.Key;
    internal string Name => Settlement.Name;
    internal bool Port => Settlement.Kind == SettlementKind.Port;

    /// <summary>Where the traveller stands to enter, and stands on leaving: on land, just outside the bubble.</summary>
    internal (double X, double Z) Arrival
    {
        get
        {
            (double x, _, double z) = Frame.World(new(Layout.Arrival.X, 0, Layout.Arrival.Y));
            return (x, z);
        }
    }

    /// <summary>The reproducible choices, for readouts and tests.</summary>
    internal string Recipe => $"{Layout.Key}/{Palette}/{Landmark}/{(Frame.Mirrored ? "mirrored" : "plain")}";

    /// <summary>Whether a point lies inside the town's bubble: within its radius on the land side of the waterfront.</summary>
    internal bool Holds(double x, double z)
    {
        double dx = x - Frame.X, dz = z - Frame.Z;
        if ((dx * dx) + (dz * dz) >= BubbleMetres * BubbleMetres) return false;
        return Settlement.Ground.Local(x, z).Along < TownGround.Waterfront;
    }

    /// <summary>
    /// Whether the traveller at a point is offered entry: at the doorstep, close to the bubble's edge,
    /// or at the arrival point, where a journey, a new game or leaving the town stands them.
    /// </summary>
    internal bool Welcomes(double x, double z)
    {
        (double ax, double az) = Arrival;
        return !Holds(x, z) && (FromEdge(x, z) <= DoorstepMetres
            || ((x - ax) * (x - ax)) + ((z - az) * (z - az)) <= ArrivalReachMetres * ArrivalReachMetres);
    }

    /// <summary>How far a point is from the bubble's edge (m); 0 inside.</summary>
    internal double FromEdge(double x, double z) =>
        Holds(x, z) ? 0 : Math.Max(0, Math.Sqrt(((x - Frame.X) * (x - Frame.X)) + ((z - Frame.Z) * (z - Frame.Z))) - BubbleMetres);

    internal static TownSite? For(Settlement settlement, TownContent content, Func<double, double, double> groundAt)
    {
        if (content.LayoutFor(settlement.Kind) is not TownLayout layout) return null;
        ulong identity = Hash(settlement.Key);
        string[] palettes = [.. content.Palettes.Keys.Order(StringComparer.Ordinal)];
        TownGround ground = settlement.Ground;
        // The arrival view stands on whichever side of the water's line gives the higher ground to look from.
        TownFrame plain = new(ground.X, ground.Height, ground.Z, ground.FacingX, ground.FacingZ, false);
        TownFrame mirrored = plain with { Mirrored = true };
        TownFrame frame = Eye(mirrored) > Eye(plain) ? mirrored : plain;
        Dictionary<string, string> names = [];
        Dictionary<string, TownPerson> keepers = [];
        foreach (string service in layout.Pieces.Select(piece => piece.Service).OfType<string>().Distinct())
        {
            ServiceDefinition definition = content.Services[service];
            string[] choices = definition.Names.TryGetValue(settlement.Kind, out string[]? named) ? named : definition.Names.Values.First();
            names[service] = Draw(choices, identity, service + ".name");
            keepers[service] = Draw(content.People[service], identity, service + ".keeper");
        }

        return new(settlement, layout, frame, Draw(palettes, identity, "palette"), Draw(layout.Landmark.Choices, identity, "landmark"), names, keepers);

        double Eye(TownFrame candidate)
        {
            (double x, _, double z) = candidate.World(layout.View.Eye);
            return groundAt(x, z);
        }
    }

    internal string Describe => string.Create(CultureInfo.InvariantCulture,
        $"{Key}|{Settlement.Kind}|{Frame.X:F0},{Frame.Z:F0}|ground={Frame.Y:F1}|facing={Frame.FacingX:F2},{Frame.FacingZ:F2}|{Recipe}");

    /// <summary>
    /// A stable cosmetic draw, not System.HashCode (which varies by process): FNV-1a keys mixed by
    /// SplitMix64's finaliser, so each purpose's draw is independent of the others for the same town.
    /// </summary>
    private static T Draw<T>(IReadOnlyList<T> choices, ulong identity, string purpose)
    {
        ulong mixed = unchecked(identity + (Hash(purpose) * MixGolden));
        mixed = unchecked((mixed ^ (mixed >> 30)) * MixFirst);
        mixed = unchecked((mixed ^ (mixed >> 27)) * MixSecond);
        mixed ^= mixed >> 31;
        return choices[(int)(mixed % (ulong)choices.Count)];
    }

    private static ulong Hash(string text)
    {
        ulong hash = HashBasis;
        foreach (char c in text) hash = unchecked((hash ^ c) * HashPrime);
        return hash;
    }
}
