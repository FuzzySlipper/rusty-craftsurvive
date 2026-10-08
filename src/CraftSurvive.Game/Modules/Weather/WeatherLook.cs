using System.Numerics;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// How the weather over the player looks (#9740, Den <c>design/weather-and-environment</c>): the
/// channels presentation reads, the colour the air takes, what falls and how thickly, and which way
/// the flow carries it. Derived from a sample, then eased toward each new one so conditions blend in
/// over game minutes rather than popping.
/// </summary>
internal readonly record struct WeatherLook(
    float Cloud, float Precipitation, float Murk, float Wind, float Cold, float Arcane,
    Vector3 Air, WeatherFall Fall, float FallDensity, Vector2 Flow)
{
    internal static WeatherLook Clear(Vector2 flow) => new(0, 0, 0, 0, 0, 0, Vector3.Zero, WeatherFall.None, 0, flow);

    /// <summary>The look of a sample: its channels, the cover-weighted colour of its fronts' air, and the strongest front's fall.</summary>
    internal static WeatherLook From(EnvironmentSample sample, Vector2 flow)
    {
        ArgumentNullException.ThrowIfNull(sample);
        EnvironmentChannels c = sample.Channels;
        Vector3 air = Vector3.Zero;
        float weight = 0;
        foreach (FrontPresence over in sample.Fronts)
        {
            air += over.Front.Kind.Air * (float)over.Cover;
            weight += (float)over.Cover;
        }

        WeatherFall fall = WeatherFall.None;
        float density = 0;
        foreach (FrontPresence over in sample.Fronts)
        {
            if (over.Front.Kind.Fall == WeatherFall.None) continue;
            fall = over.Front.Kind.Fall;
            // Rain, snow and glitter fall as thickly as the precipitation; dust blows as thickly as the murk.
            density = (float)(fall == WeatherFall.Dust ? c.Murk : c.Precipitation);
            break;
        }

        return new((float)c.Cloud, (float)c.Precipitation, (float)c.Murk, (float)c.Wind, (float)c.Cold, (float)c.Arcane,
            weight > 0 ? air / weight : Vector3.Zero, fall, density, flow);
    }

    /// <summary>
    /// A step of the way toward another look, by <paramref name="share"/> (0..1). What falls changes
    /// only once the old fall has thinned away, so rain fades out before snow fades in.
    /// </summary>
    internal WeatherLook Toward(WeatherLook target, float share)
    {
        share = Math.Clamp(share, 0, 1);
        bool sameFall = Fall == target.Fall || Fall == WeatherFall.None || FallDensity < FallSwitchBelow;
        WeatherFall fall = sameFall ? target.Fall : Fall;
        // What falls thins away twice as fast as the sky clears, so one fall gives way to the next promptly.
        float density = Lerp(FallDensity, sameFall ? target.FallDensity : 0, sameFall ? share : Math.Min(1, share * 2));
        Vector3 air = Air == Vector3.Zero ? target.Air : target.Air == Vector3.Zero ? Air : Vector3.Lerp(Air, target.Air, share);
        Vector2 flow = Vector2.Lerp(Flow, target.Flow, share);
        return new(Lerp(Cloud, target.Cloud, share), Lerp(Precipitation, target.Precipitation, share), Lerp(Murk, target.Murk, share),
            Lerp(Wind, target.Wind, share), Lerp(Cold, target.Cold, share), Lerp(Arcane, target.Arcane, share),
            air, fall, density, flow.LengthSquared() > 0 ? Vector2.Normalize(flow) : target.Flow);
    }

    /// <summary>How far two looks differ, for deciding whether lights and fog are worth replacing.</summary>
    internal float Distance(WeatherLook other) =>
        Math.Max(Math.Max(Math.Abs(Cloud - other.Cloud), Math.Abs(Murk - other.Murk)),
            Math.Max(Math.Abs(Precipitation - other.Precipitation), Math.Abs(Arcane - other.Arcane)));

    /// <summary>A fall thinner than this may give way to another.</summary>
    private const float FallSwitchBelow = 0.1f;

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
