using System.Text.Json;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// One clip of a creature kind, as the build manifest (art/creatures/build_creature.py) describes it:
/// its length, whether it loops, whether all four feet stay on the ground (so IK may move them onto
/// uneven ground), and, for a gait, the ground speed at which its planted feet do not slide.
/// </summary>
internal sealed record CreatureClip(string Name, double Seconds, bool Loops, bool PlantsFeet, double Speed);

/// <summary>What a creature's build says of its rig: its clips, its legs as two-bone IK chains, and where its hind and fore feet stand along its body.</summary>
internal sealed record CreatureRig(
    IReadOnlyDictionary<string, CreatureClip> Clips,
    IReadOnlyList<(string Root, string Mid, string End)> Legs,
    double HindMetres,
    double ForeMetres);

/// <summary>Reads a creature's build manifest. Pure: the product hands it the file's text, and the rule lane reads the same file.</summary>
internal static class CreatureManifest
{
    internal static CreatureRig Parse(string json)
    {
        using JsonDocument manifest = JsonDocument.Parse(json);
        JsonElement root = manifest.RootElement;
        Dictionary<string, CreatureClip> clips = [];
        foreach (JsonProperty c in root.GetProperty("clips").EnumerateObject())
        {
            clips[c.Name] = new CreatureClip(c.Name, c.Value.GetProperty("seconds").GetDouble(), c.Value.GetProperty("loops").GetBoolean(),
                c.Value.GetProperty("plantsFeet").GetBoolean(), c.Value.TryGetProperty("speed", out JsonElement speed) ? speed.GetDouble() : 0);
        }

        List<(string, string, string)> legs = [.. root.GetProperty("legs").EnumerateArray().Select(l =>
            (l[0].GetString()!, l[1].GetString()!, l[2].GetString()!))];
        JsonElement body = root.GetProperty("body");
        return new CreatureRig(clips, legs, body.GetProperty("hindMetres").GetDouble(), body.GetProperty("foreMetres").GetDouble());
    }
}
