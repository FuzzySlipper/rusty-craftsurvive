using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Towns;

/// <summary>One town soundscape, with per-voice fades and an outdoor crossfade share.</summary>
internal sealed class TownAudio : IDisposable
{
    private const double ArrivalSeconds = 1.2, DepartureSeconds = .65, MixSeconds = .15;
    private const float MusicLevel = .12f, RiverLevel = .17f, HearthLevel = .075f, ActivityLevel = .20f;
    private const float OverlayAmbience = .55f, OverlayMusic = .42f, OverlayHearth = 1.5f;
    private const float LevelEpsilon = .001f;
    /// <summary>A port's open water is quieter than a landing's river; its quays are busier.</summary>
    private const float PortWater = .85f, RiverWater = 1, PortActivity = 1, RiverActivity = .6f;
    private sealed record Layer(AudioClip Clip, AudioVoice Voice, AudioSourceDescriptor Descriptor, string Name)
    {
        internal float Level;
    }
    private readonly IEngineContext engine;
    private readonly List<Layer> layers = [];
    private int refusals;
    private string failure = "none";
    internal float Presence { get; private set; }
    internal int Voices => layers.Count;
    internal bool Silent => Presence == 0 && layers.All(layer => layer.Level == 0);

    internal TownAudio(IEngineContext engine)
    {
        this.engine = engine;
        foreach ((string name, AudioBus bus, float pan) in new[]
        {
            ("river", AudioBus.Ambient, .25f), ("hearth", AudioBus.Ambient, -.1f),
            ("activity", AudioBus.Ambient, -.4f), ("shelter-music", AudioBus.Music, 0f),
        })
        {
            AudioClip? clip = null;
            try
            {
                using ContentReference content = engine.Content.OpenReference(new($"towns/audio/{name}.wav"));
                clip = engine.Audio.OpenClipFromContent(new(content));
                AudioSourceDescriptor descriptor = new(clip, bus, 0, 1, true, 0, 1, AudioRolloff.Linear,
                    pan, AudioEmitterKind.Global2d, Vector3.Zero, 0, Vector3.Zero);
                AudioVoice voice = engine.Audio.CreateVoice(descriptor);
                layers.Add(new(clip, voice, descriptor, name));
            }
            catch (EngineCallException refused)
            {
                clip?.Dispose();
                refusals++;
                failure = refused.Message;
            }
        }
    }

    internal void Advance(double seconds, bool open, bool overlay, TownSite? site)
    {
        Presence = Approach(Presence, open ? 1 : 0, (float)(seconds / (open ? ArrivalSeconds : DepartureSeconds)));
        bool port = site?.Port ?? false;
        foreach (Layer layer in layers)
        {
            float desired = layer.Name switch
            {
                "river" => RiverLevel * (port ? PortWater : RiverWater) * (overlay ? OverlayAmbience : 1),
                "hearth" => HearthLevel * (overlay ? OverlayHearth : 1),
                "activity" => ActivityLevel * (port ? PortActivity : RiverActivity) * (overlay ? OverlayAmbience : 1),
                _ => MusicLevel * (overlay ? OverlayMusic : 1),
            };
            float target = desired * Presence;
            float blend = (float)(1 - Math.Exp(-seconds / MixSeconds));
            float level = layer.Level + (target - layer.Level) * blend;
            if (target == 0 && level < LevelEpsilon) level = 0;
            if (level == layer.Level) continue;
            try { engine.Audio.UpdateVoice(new(layer.Voice, layer.Descriptor with { Volume = level })); }
            catch (EngineCallException refused) { refusals++; failure = refused.Message; }
            layer.Level = level;
        }
    }

    private static float Approach(float value, float target, float amount) => value < target ? Math.Min(target, value + amount) : Math.Max(target, value - amount);
    internal string Readout => $"voices={Voices};presence={Presence:F3};mix={string.Join(',', layers.Select(layer => $"{layer.Name}:{layer.Level:F3}"))};audioRefused={refusals};audioFailure={failure}";

    public void Dispose()
    {
        foreach (Layer layer in layers) layer.Voice.Dispose();
        foreach (Layer layer in layers) layer.Clip.Dispose();
        layers.Clear();
    }
}
