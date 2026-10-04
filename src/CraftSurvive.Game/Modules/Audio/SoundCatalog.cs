using CraftSurvive.Game.Modules.Feedback;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Audio;

/// <summary>
/// How one cue sounds: the clips it takes turns between, its bus and level, and whether it is heard
/// from where it happened (<paramref name="Placed"/>) and then how far it carries. An unplaced cue
/// plays at the listener, wherever it happened: the player's own body is heard as their own.
/// </summary>
internal sealed record CueSound(string[] Clips, AudioBus Bus, float Volume, bool Placed = false, float RangeMetres = CueSound.DefaultRangeMetres)
{
    /// <summary>How far a placed cue carries before it falls silent.</summary>
    internal const float DefaultRangeMetres = 48f;
}

/// <summary>One ambience bed: a looping clip on the ambient bus, at a level the place sets.</summary>
internal enum AmbienceBed
{
    Wind,
    Night,
    Cave,
    Water,
}

/// <summary>Where the player is listening from, as far as the ambience is concerned.</summary>
internal readonly record struct Surroundings(bool Underground, bool Submerged, double Daylight);

/// <summary>
/// The game's sounds by name: which generated clip (<c>scripts/generate-sounds.mjs</c>) each cue
/// plays, on which bus and how loud, and how loud each ambience bed is for where the player is.
/// </summary>
internal static class SoundCatalog
{
    internal const string Directory = "audio";

    /// <summary>The levels of the beds: open air by day and by night, underground, and under water.</summary>
    private const float WindNight = 0.12f;
    private const float WindDay = 0.3f;
    private const float NightChorus = 0.28f;
    private const float CaveLevel = 0.45f;
    private const float WaterLevel = 0.55f;

    /// <summary>What is left of the other beds while the player's head is under water.</summary>
    private const float Muffled = 0.15f;

    internal static readonly IReadOnlyDictionary<Cue, CueSound> Cues = new Dictionary<Cue, CueSound>
    {
        [Cue.Footstep] = new(["step-1", "step-2", "step-3"], AudioBus.Sfx, 0.32f),
        [Cue.Jump] = new(["jump"], AudioBus.Sfx, 0.22f),
        [Cue.Land] = new(["land"], AudioBus.Sfx, 0.45f),
        [Cue.HardLanding] = new(["land-hard"], AudioBus.Sfx, 0.7f),
        [Cue.Splash] = new(["splash"], AudioBus.Sfx, 0.55f),
        [Cue.Grip] = new(["grip"], AudioBus.Sfx, 0.4f),
        [Cue.Hurt] = new(["hurt"], AudioBus.Sfx, 0.6f),
        [Cue.Swing] = new(["swing"], AudioBus.Sfx, 0.4f),
        [Cue.Strike] = new(["strike"], AudioBus.Sfx, 0.55f),
        [Cue.Defeat] = new(["defeat"], AudioBus.Sfx, 0.5f),
        [Cue.Blast] = new(["blast"], AudioBus.Sfx, 0.9f, Placed: true, RangeMetres: 96f),
        [Cue.Place] = new(["place"], AudioBus.Sfx, 0.5f),
        [Cue.Craft] = new(["craft"], AudioBus.Ui, 0.35f),
        [Cue.Pickup] = new(["pickup"], AudioBus.Ui, 0.35f),
        [Cue.Discovery] = new(["discovery"], AudioBus.Ui, 0.4f),
        [Cue.Refused] = new(["refused"], AudioBus.Ui, 0.3f),
        [Cue.Portal] = new(["portal"], AudioBus.Sfx, 0.6f),
    };

    internal static readonly IReadOnlyDictionary<AmbienceBed, string> Beds = new Dictionary<AmbienceBed, string>
    {
        [AmbienceBed.Wind] = "amb-wind",
        [AmbienceBed.Night] = "amb-night",
        [AmbienceBed.Cave] = "amb-cave",
        [AmbienceBed.Water] = "amb-water",
    };

    internal static string ContentPath(string clip) => $"{Directory}/{clip}.wav";

    /// <summary>
    /// How loud a bed is for where the player is: wind in the open air, stronger by day; the night
    /// chorus after dark; the cave's drone underground; and under water the water's rumble, with
    /// everything else muffled.
    /// </summary>
    internal static float Level(AmbienceBed bed, Surroundings around)
    {
        float daylight = (float)Math.Clamp(around.Daylight, 0d, 1d);
        float level = bed switch
        {
            AmbienceBed.Wind => around.Underground ? 0f : WindNight + ((WindDay - WindNight) * daylight),
            AmbienceBed.Night => around.Underground ? 0f : NightChorus * (1f - daylight),
            AmbienceBed.Cave => around.Underground ? CaveLevel : 0f,
            AmbienceBed.Water => around.Submerged ? WaterLevel : 0f,
            _ => 0f,
        };
        return around.Submerged && bed != AmbienceBed.Water ? level * Muffled : level;
    }
}
