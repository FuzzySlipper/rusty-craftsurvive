using System.Numerics;
using System.Text.Json;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// A miasma a kind breathes out of its open mouth (#10882; Patch: looking straight down the plume-neck's gaping
/// mouth reads oddly, and a weird miasma that comes with a bite or a roar suits it, though not fire). At each share of
/// a clip listed in <see cref="At"/>, a burst of haze leaves the mouth: the point midway between the upper jaw's tip
/// (<see cref="UpperJoint"/>, <see cref="UpperReachMetres"/> along it) and the lower's, moving the way the jaws point.
/// </summary>
/// <param name="At">For each clip that breathes, the shares of its length at which a burst leaves the mouth.</param>
/// <param name="UpperReachMetres">How far along its joint the upper jaw's tip is, in the mesh's own metres.</param>
/// <param name="Count">Puffs in one burst.</param>
/// <param name="SpeedMin">The slowest a puff leaves the mouth, metres a second.</param>
/// <param name="SpeedMax">The fastest.</param>
/// <param name="SpreadMetresPerSecond">How far off the jaws' line a puff may drift, each way.</param>
/// <param name="RiseMetresPerSecondSquared">How the haze rises (or sinks) as it goes.</param>
/// <param name="Sizes">Puff size in metres by share of its life.</param>
/// <param name="Colours">Puff colour, sRGB and alpha, by share of its life.</param>
internal sealed record WildlifeBreath(
    IReadOnlyDictionary<WildlifeClipRole, float[]> At,
    string UpperJoint,
    float UpperReachMetres,
    string LowerJoint,
    float LowerReachMetres,
    int Count,
    float LifetimeMinSeconds,
    float LifetimeMaxSeconds,
    float SpeedMin,
    float SpeedMax,
    float SpreadMetresPerSecond,
    float RiseMetresPerSecondSquared,
    (float Age, float Size)[] Sizes,
    (float Age, Vector4 Colour)[] Colours)
{
    /// <summary>The signal a breath's emissions carry, for the Engine's readouts.</summary>
    internal const string Signal = "craftsurvive.creature.breath";

    /// <summary>Reads a mesh's `breath` block, if it has one.</summary>
    internal static WildlifeBreath? Read(JsonElement mesh, Func<string, WildlifeClipRole> role)
    {
        if (!mesh.TryGetProperty("breath", out JsonElement breath)) return null;
        Dictionary<WildlifeClipRole, float[]> at = [];
        foreach (JsonProperty clip in breath.GetProperty("at").EnumerateObject())
        {
            float[] shares = [.. clip.Value.EnumerateArray().Select(share => share.GetSingle())];
            if (shares.Any(share => share is < 0f or > 1f)) throw new InvalidDataException($"breath: {clip.Name}'s shares must lie in 0..1");
            at[role(clip.Name)] = shares;
        }

        JsonElement upper = breath.GetProperty("upper"), lower = breath.GetProperty("lower");
        WildlifeBreath read = new(at,
            upper.GetProperty("joint").GetString()!, upper.GetProperty("reachMetres").GetSingle(),
            lower.GetProperty("joint").GetString()!, lower.GetProperty("reachMetres").GetSingle(),
            breath.GetProperty("count").GetInt32(),
            breath.GetProperty("lifetimeSeconds")[0].GetSingle(), breath.GetProperty("lifetimeSeconds")[1].GetSingle(),
            breath.GetProperty("speed")[0].GetSingle(), breath.GetProperty("speed")[1].GetSingle(),
            breath.GetProperty("spreadMetresPerSecond").GetSingle(),
            breath.GetProperty("riseMetresPerSecondSquared").GetSingle(),
            [.. breath.GetProperty("sizes").EnumerateArray().Select(key => (key[0].GetSingle(), key[1].GetSingle()))],
            [.. breath.GetProperty("colours").EnumerateArray().Select(key => (key[0].GetSingle(), Colour(key[1])))]);
        if (read.Count < 1 || read.LifetimeMinSeconds <= 0 || read.LifetimeMaxSeconds < read.LifetimeMinSeconds || read.SpeedMax < read.SpeedMin)
            throw new InvalidDataException("breath: count, lifetime and speed must be positive and in order");
        return read;
    }

    /// <summary>The shares of a clip passed between two moments in it, as a running clip crosses them.</summary>
    internal int Crossed(WildlifeClipRole clip, float from, float to)
    {
        if (!At.TryGetValue(clip, out float[]? shares)) return 0;
        return shares.Count(share => share > from && share <= to);
    }

    private static Vector4 Colour(JsonElement rgba) => new(rgba[0].GetSingle(), rgba[1].GetSingle(), rgba[2].GetSingle(), rgba[3].GetSingle());
}
