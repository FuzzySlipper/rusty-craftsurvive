using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Sky;

/// <summary>
/// The sky and the world's light, following the clock. The Engine blends two authored panoramas
/// and keeps the two retained lights - a sun that becomes the moon after dusk, and an ambient fill -
/// which replace the Engine's neutral rig (disabled in the project). This module only decides the
/// blend and the lights' colour, strength and direction from the time of day.
/// </summary>
internal sealed class DayNightSky : IDisposable
{
    internal const string DayPanoramaContentPath = "textures/sky-panorama.png";
    internal const string NightPanoramaContentPath = "textures/sky-night.png";

    /// <summary>Full-day light, matching the strength of the Engine's neutral rig it replaces.</summary>
    private const float DaySunIntensity = 2.2f;
    private const float DayAmbientIntensity = 1.7f;

    /// <summary>Moonlight: enough to see the ground's shape by, not enough to read it.</summary>
    private const float MoonIntensity = 0.45f;
    private const float NightAmbientIntensity = 0.32f;

    /// <summary>How much daylight must change before the lights are replaced again.</summary>
    private const double RelightStep = 0.004d;

    private static readonly Vector3 SunColour = new(1f, 0.96f, 0.88f);
    private static readonly Vector3 DuskSunColour = new(1f, 0.62f, 0.38f);
    private static readonly Vector3 MoonColour = new(0.62f, 0.72f, 1f);
    private static readonly Vector3 DayAmbientColour = new(0.92f, 0.96f, 1f);
    private static readonly Vector3 NightAmbientColour = new(0.45f, 0.52f, 0.8f);

    private readonly IEngineContext engine;
    private readonly RenderResource day;
    private readonly RenderResource night;
    private Light? sun;
    private Light? ambient;
    private double litDaylight = double.NaN;
    private bool underground;
    private bool submerged;
    private bool disposed;

    /// <summary>Underground there is no sky: a near-black background, no sun, and a faint fill.</summary>
    private static readonly Color UndergroundBackground = new(0.02f, 0.02f, 0.03f, 1f);

    /// <summary>
    /// The fill underground: faint, so what the dungeon's own lights do not reach falls toward
    /// black and a drop reads as depth.
    /// </summary>
    private const float UndergroundAmbientIntensity = 0.08f;

    /// <summary>
    /// How quickly distance fades underground into the background colour (exponential squared: clear
    /// near, closing in beyond about twenty metres), so far floors and depths sink into the dark.
    /// </summary>
    private const float UndergroundFogDensity = 0.045f;

    /// <summary>
    /// What distance fades to underground, in linear light: darker than the background's colour
    /// reads, so depth closes into black rather than a grey haze.
    /// </summary>
    private static readonly Color UndergroundFog = new(0.002f, 0.002f, 0.003f, 1f);

    /// <summary>
    /// Under water there is no sky either: distance and the background are both the water's own
    /// murk, a dim blue-green in linear light, so what is far fades exactly into it.
    /// </summary>
    private static readonly Color WaterMurk = new(0.02f, 0.08f, 0.09f, 1f);

    /// <summary>
    /// How quickly distance fades under water (exponential squared): the near bank and the bed
    /// read, and beyond about ten metres everything has gone into the murk.
    /// </summary>
    private const float WaterFogDensity = 0.11f;

    internal DayNightSky(IEngineContext engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        day = Panorama(DayPanoramaContentPath);
        night = Panorama(NightPanoramaContentPath);
    }

    /// <summary>The daylight the lights were last set for.</summary>
    internal double Daylight => litDaylight;

    /// <summary>
    /// Goes underground or comes back up. Underground the sky is a dark background and the sun is
    /// out; coming back up shows the sky for the time of day again.
    /// </summary>
    internal void Underground(bool below, WorldTime time)
    {
        if (disposed)
        {
            return;
        }

        underground = below;
        litDaylight = double.NaN;
        Veil();
        if (!below)
        {
            Show(time);
            return;
        }

        LightDescriptor dark = Directional(MoonColour, 0f, -Vector3.UnitY) with { Enabled = false };
        LightDescriptor fill = Fill(0d) with { Intensity = UndergroundAmbientIntensity };
        if (sun is Light retainedSun && ambient is Light retainedFill)
        {
            engine.Graphics.UpdateLight(new LightUpdateRequest(retainedSun, Request(ProductIds.SunLight, dark)));
            engine.Graphics.UpdateLight(new LightUpdateRequest(retainedFill, Request(ProductIds.AmbientLight, fill)));
        }
    }

    /// <summary>
    /// Puts the player's eyes under water or brings them back out. Under water the view closes into
    /// a blue-green murk, whether in the open or underground; coming out shows what was there before.
    /// </summary>
    internal void Submerged(bool below)
    {
        if (disposed || below == submerged)
        {
            return;
        }

        submerged = below;
        Veil();
    }

    /// <summary>Whether the view is under water.</summary>
    internal bool ViewSubmerged => submerged;

    /// <summary>Shows the sky and lights for a moment. Lights are replaced only when daylight has moved.</summary>
    internal void Show(WorldTime time)
    {
        if (underground || disposed)
        {
            return;
        }

        double daylight = WorldClock.Daylight(time.DayFraction);
        if (!submerged)
        {
            engine.CameraView.SetSkyBackgroundBlend(new SkyBackgroundBlendRequest(day, night, (float)(1d - daylight)));
        }

        if (sun is not null && Math.Abs(daylight - litDaylight) < RelightStep)
        {
            return;
        }

        LightDescriptor sunlight = Sunlight(time.DayFraction, daylight);
        LightDescriptor fill = Fill(daylight);
        if (sun is Light retainedSun && ambient is Light retainedFill)
        {
            engine.Graphics.UpdateLight(new LightUpdateRequest(retainedSun, Request(ProductIds.SunLight, sunlight)));
            engine.Graphics.UpdateLight(new LightUpdateRequest(retainedFill, Request(ProductIds.AmbientLight, fill)));
        }
        else
        {
            sun = engine.Graphics.CreateLight(Request(ProductIds.SunLight, sunlight));
            ambient = engine.Graphics.CreateLight(Request(ProductIds.AmbientLight, fill));
        }

        litDaylight = daylight;
    }

    /// <summary>Clears the sky before its panoramas are released. Lights are Engine-owned and go with the session.</summary>
    public void Dispose()
    {
        engine.CameraView.ClearSkyBackground(new ClearSkyBackgroundRequest(0U));
        disposed = true;
        sun = null;
        ambient = null;
        litDaylight = double.NaN;
    }

    /// <summary>
    /// The fog and background for where the eyes are: water's murk under water, the dark underground,
    /// and in the open no fog, the sky's background being shown by <see cref="Show"/>.
    /// </summary>
    private void Veil()
    {
        if (submerged)
        {
            engine.CameraView.SetBackgroundColor(new SetBackgroundColorRequest(WaterMurk));
            engine.CameraView.SetFog(new(FogMode.ExponentialSquared, WaterMurk, 0f, 0f, WaterFogDensity));
        }
        else if (underground)
        {
            engine.CameraView.SetBackgroundColor(new SetBackgroundColorRequest(UndergroundBackground));
            engine.CameraView.SetFog(new(FogMode.ExponentialSquared, UndergroundFog, 0f, 0f, UndergroundFogDensity));
        }
        else
        {
            engine.CameraView.SetFog(new(FogMode.Off, default, 0f, 0f, 0f));
        }
    }

    /// <summary>The sun by day, warming toward dusk, and the moon opposite it by night.</summary>
    private static LightDescriptor Sunlight(double dayFraction, double daylight)
    {
        bool byDay = WorldClock.SunElevation(dayFraction) >= 0d;
        Vector3 toward = WorldClock.TowardSun(dayFraction) * (byDay ? 1f : -1f);
        float warmth = (float)Math.Clamp(1d - (WorldClock.SunElevation(dayFraction) * 3d), 0d, 1d);
        Vector3 colour = byDay ? Vector3.Lerp(SunColour, DuskSunColour, warmth) : MoonColour;
        float intensity = byDay
            ? (float)(DaySunIntensity * daylight)
            : (float)(MoonIntensity * (1d - daylight));
        return Directional(colour, intensity, -toward);
    }

    private static LightDescriptor Fill(double daylight) => new(
        LightKind.Ambient,
        Vector3.Lerp(NightAmbientColour, DayAmbientColour, (float)daylight),
        (float)(NightAmbientIntensity + ((DayAmbientIntensity - NightAmbientIntensity) * daylight)),
        true, Vector3.Zero, -Vector3.UnitY, false, 0f, 0f, 0f, 0f, LightShadowIntent.Disabled);

    private static LightDescriptor Directional(Vector3 colour, float intensity, Vector3 travel) => new(
        LightKind.Directional, colour, intensity, true, Vector3.Zero, travel, false, 0f, 0f, 0f, 0f, LightShadowIntent.Disabled);

    private static LightRequest Request(ulong id, LightDescriptor descriptor) => new(id, false, 0UL, descriptor);

    private RenderResource Panorama(string path)
    {
        RenderResourceInfo resource = engine.Graphics.OpenResource(new RenderResourceRequest(path, TextureFilter.Linear, TextureWrap.Clamp));
        if (resource.Kind != RenderResourceKind.Texture || resource.ByteLength == 0)
        {
            throw new InvalidOperationException($"CraftSurvive sky panorama '{path}' must be a non-empty Engine texture.");
        }

        return resource.Handle;
    }
}
