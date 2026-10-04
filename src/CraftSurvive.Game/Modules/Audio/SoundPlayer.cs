using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Feedback;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Audio;

/// <summary>
/// The one owner of the game's audio, driven by the feedback module. It opens the generated clips,
/// plays each cue it is handed, and keeps the four ambience beds looping at the levels the player's
/// surroundings call for, fading between them. Sound is presentation: a clip the Engine refuses is
/// counted and reported, and play goes on without it.
/// </summary>
internal sealed class SoundPlayer : IDisposable
{
    /// <summary>How fast a bed's level moves toward its target, in level per second: about a second's fade.</summary>
    private const float FadePerSecond = 0.6f;

    /// <summary>The smallest change in a bed's level worth sending to the Engine.</summary>
    private const float LevelStep = 0.01f;

    /// <summary>A cue's normal speed, and the range of an unplaced cue, which the Engine requires positive.</summary>
    private const float NormalPitch = 1f;
    private const float UnplacedRange = 1f;

    private readonly IEngineContext engine;
    private readonly Dictionary<string, AudioClip> clips = [];
    private readonly Dictionary<AmbienceBed, (AudioVoice Voice, AudioSourceDescriptor Descriptor, float Level)> beds = [];
    private readonly Dictionary<Cue, long> played = [];
    private long signal;
    private long refused;
    private string lastFailure = "none";

    internal SoundPlayer(IEngineContext engine) =>
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>Opens every clip and starts the beds looping, silent until the surroundings call for them.</summary>
    internal void Start()
    {
        foreach (string clip in SoundCatalog.Cues.Values.SelectMany(sound => sound.Clips).Concat(SoundCatalog.Beds.Values).Distinct())
        {
            Open(clip);
        }

        foreach ((AmbienceBed bed, string clip) in SoundCatalog.Beds)
        {
            if (!clips.TryGetValue(clip, out AudioClip? opened))
            {
                continue;
            }

            AudioSourceDescriptor descriptor = Unplaced(opened, AudioBus.Ambient, 0f) with { Looping = true };
            if (Attempt(() => engine.Audio.CreateVoice(descriptor)) is AudioVoice voice)
            {
                beds[bed] = (voice, descriptor, 0f);
            }
        }
    }

    /// <summary>Moves each bed's level toward what the surroundings call for, over some seconds of play.</summary>
    internal void Fade(Surroundings around, double elapsedSeconds)
    {
        float fade = (float)(FadePerSecond * elapsedSeconds);
        foreach (AmbienceBed bed in beds.Keys.ToArray())
        {
            (AudioVoice voice, AudioSourceDescriptor descriptor, float level) = beds[bed];
            float target = SoundCatalog.Level(bed, around);
            float next = level < target ? Math.Min(target, level + fade) : Math.Max(target, level - fade);
            if (Math.Abs(next - descriptor.Volume) < LevelStep && next != target)
            {
                beds[bed] = (voice, descriptor, next);
                continue;
            }

            AudioSourceDescriptor louder = descriptor with { Volume = next };
            if (next != descriptor.Volume)
            {
                Attempt(() =>
                {
                    engine.Audio.UpdateVoice(new AudioVoiceUpdateRequest(voice, louder));
                    return true;
                });
            }

            beds[bed] = (voice, louder, next);
        }
    }

    public void Dispose()
    {
        foreach ((AudioVoice voice, _, _) in beds.Values)
        {
            voice.Dispose();
        }

        beds.Clear();
        foreach (AudioClip clip in clips.Values)
        {
            clip.Dispose();
        }

        clips.Clear();
    }

    internal string Readout()
    {
        AudioResult audio = engine.Audio.Read();
        AudioRealizationResult realization = engine.Audio.ReadRealization();
        int completed = 0;
        int diagnostics = 0;
        foreach (AudioRealizationFact fact in realization.Facts.Span)
        {
            completed += fact.Kind == AudioRealizationFactKind.NaturalCompletionOneShot ? 1 : 0;
            diagnostics += fact.Kind == AudioRealizationFactKind.Diagnostic ? 1 : 0;
        }

        string levels = string.Join(",", beds.Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Key.ToString().ToLowerInvariant()}={entry.Value.Level:F2}")));
        string counts = string.Join(",", played.OrderBy(entry => entry.Key).Select(entry => $"{entry.Key.ToString().ToLowerInvariant()}={entry.Value}"));
        return $"audio clips={audio.AdmittedClips} voices={audio.ActiveVoices} emitted={audio.EmittedSignals} completed={completed} deviceDiagnostics={diagnostics} " +
            $"refused={refused} beds={levels} played={(counts.Length == 0 ? "none" : counts)} failure={lastFailure}";
    }

    internal void Play(RaisedCue raised)
    {
        CueSound sound = SoundCatalog.Cues[raised.Cue];
        long count = played.GetValueOrDefault(raised.Cue);
        played[raised.Cue] = count + 1;

        // A cue with several clips takes them in turn, so a walk's footfalls do not repeat one sound.
        if (!clips.TryGetValue(sound.Clips[count % sound.Clips.Length], out AudioClip? clip))
        {
            return;
        }

        AudioSourceDescriptor descriptor = sound.Placed && raised.At is Vector3 at
            ? new(clip, sound.Bus, sound.Volume, NormalPitch, false, 1f, sound.RangeMetres, AudioRolloff.LinearDecibels, 0f,
                AudioEmitterKind.World3d, at, 0UL, Vector3.Zero)
            : Unplaced(clip, sound.Bus, sound.Volume);
        string id = string.Create(CultureInfo.InvariantCulture, $"cue-{signal++}");
        Attempt(() =>
        {
            engine.Audio.Emit(new AudioEmitRequest(id, descriptor));
            return true;
        });
    }

    private static AudioSourceDescriptor Unplaced(AudioClip clip, AudioBus bus, float volume) =>
        new(clip, bus, volume, NormalPitch, false, 0f, UnplacedRange, AudioRolloff.Linear, 0f, AudioEmitterKind.Global2d, Vector3.Zero, 0UL, Vector3.Zero);

    private void Open(string clip)
    {
        AudioClip? opened = Attempt(() =>
        {
            using ContentReference content = engine.Content.OpenReference(new(SoundCatalog.ContentPath(clip)));
            return engine.Audio.OpenClipFromContent(new AudioClipFromContentRequest(content));
        });
        if (opened is not null)
        {
            clips[clip] = opened;
        }
    }

    /// <summary>Runs one Engine audio call; a refusal is counted and reported, never thrown into play.</summary>
    private T? Attempt<T>(Func<T> call)
        where T : class
    {
        try
        {
            return call();
        }
        catch (EngineCallException exception)
        {
            refused++;
            lastFailure = exception.Message;
            return null;
        }
    }

    private bool Attempt(Func<bool> call)
    {
        try
        {
            return call();
        }
        catch (EngineCallException exception)
        {
            refused++;
            lastFailure = exception.Message;
            return false;
        }
    }
}
