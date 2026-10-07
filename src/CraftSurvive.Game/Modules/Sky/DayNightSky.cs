using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Sky;

/// <summary>
/// The sky and the world's light, following the clock. The Engine blends two authored panoramas,
/// lights the world from them (the sky's light), and keeps three retained lights that replace its
/// neutral rig (disabled in the project): a sun that becomes the moon after dusk and casts through
/// cascades, the sky's occlusion layer (an ambient light whose shadow is the open sky, so caves and
/// ground under a canopy lose the sky), and a hemisphere fill. The same clock drives the distance
/// fog, the air's haze toward the sun, the sun's disc and its shafts. This module only decides the
/// blend and each light's colour, strength and direction from the time of day (#9544).
/// </summary>
internal sealed class DayNightSky : IDisposable
{
    internal const string DayPanoramaContentPath = "textures/sky-panorama.png";
    internal const string NightPanoramaContentPath = "textures/sky-night.png";

    /// <summary>The operator every light below is tuned under: film-like contrast, highlights rolling to white.</summary>
    private const ToneMappingOperator Operator = ToneMappingOperator.AcesFilmic;
    private const float Exposure = 1f;

    /// <summary>
    /// Full-day sunlight. With the sky's light on a sunlit upward face, the two together sit a
    /// little above white, which the operator rolls off rather than clipping.
    /// </summary>
    private const float DaySunIntensity = 1.5f;

    /// <summary>How far the sun's cascades reach: the fog has closed by then.</summary>
    private const float SunShadowRangeMetres = 120f;
    private const uint SunShadowResolution = 2048;

    /// <summary>Moonlight: enough to see the ground's shape by, not enough to read it.</summary>
    private const float MoonIntensity = 0.18f;

    /// <summary>
    /// After sunset and before sunrise the sky still glows from the sun's side: the sun's light
    /// keeps shining from the horizon, warm and fading, until the sun is this far below it (as a
    /// sine), so the land is lit long and low rather than switched off at the horizon.
    /// </summary>
    private const double TwilightBelow = -0.18d;
    private const float TwilightGlowElevation = 0.2f;
    private const float TwilightSunIntensity = 0.6f;

    /// <summary>How strongly the panoramas light the world: panorama colours are read as linear light.</summary>
    private const float DaySkyLightIntensity = 0.65f;
    private const float NightSkyLightIntensity = 0.6f;

    /// <summary>
    /// The sky's occlusion layer: a faint sky-coloured ambient whose shadow is the open sky. Its
    /// own light hardly matters; the layer it draws also shades the sky's light, which is what
    /// darkens a cave mouth and the ground under a canopy.
    /// </summary>
    private const float DaySkyShadowIntensity = 0.12f;
    private const float NightSkyShadowIntensity = 0.03f;

    /// <summary>Half the side of the square of sky the layer looks down over; it must cover what the fog leaves visible.</summary>
    private const float SkyShadowRangeMetres = 80f;
    private const uint SkyShadowResolution = 2048;

    /// <summary>
    /// How far the player may move before the sky's square is moved onto them. Moving it re-renders
    /// the layer, so it follows in steps rather than every update.
    /// </summary>
    private const float SkyFollowStepMetres = 12f;

    /// <summary>The unshadowed fill: what a shaded place still gets, so shade is not black by day.</summary>
    private const float DayFillIntensity = 0.22f;
    private const float NightFillIntensity = 0.05f;

    /// <summary>The fill's twilight glow, strongest with the sun just under the horizon, from the dusk sky's colour.</summary>
    private const float TwilightFillIntensity = 0.3f;
    private static readonly Vector3 TwilightSkyColour = new(0.8f, 0.55f, 0.45f);

    /// <summary>How much daylight must change before the lights are replaced again.</summary>
    private const double RelightStep = 0.004d;

    /// <summary>The sun's elevation (as a sine) where its colour has warmed fully, and where it is full day.</summary>
    private const double SunWarmBelow = 0.05d;
    private const double SunWhiteAbove = 0.4d;

    /// <summary>The sun's strength ramps from nothing at the horizon to full by this elevation.</summary>
    private const double SunFullAbove = 0.3d;

    private static readonly Vector3 SunColour = new(1f, 0.95f, 0.86f);
    private static readonly Vector3 DuskSunColour = new(1f, 0.5f, 0.24f);
    private static readonly Vector3 MoonColour = new(0.55f, 0.66f, 1f);
    private static readonly Vector3 DaySkyColour = new(0.55f, 0.7f, 0.95f);
    private static readonly Vector3 NightSkyColour = new(0.25f, 0.32f, 0.6f);
    private static readonly Vector3 DayGroundColour = new(0.38f, 0.33f, 0.24f);
    private static readonly Vector3 NightGroundColour = new(0.08f, 0.08f, 0.12f);

    /// <summary>
    /// Distance fog in the open: the horizon colour of each panorama in linear light (sampled from
    /// the images), blended as the sky is, so the far ground fades exactly into the sky behind it.
    /// </summary>
    private static readonly Vector3 DayHorizon = new(0.479f, 0.662f, 0.760f);
    private static readonly Vector3 NightHorizon = new(0.0103f, 0.0176f, 0.0467f);

    /// <summary>
    /// How quickly distance fades in the open (exponential squared, so the near ground is clear
    /// and the far closes): half the ground is left at 200 m, where the far field (FarField) has
    /// taken over from the drawn chunks, and a tenth at 400 m, so ridges a kilometre off stand as
    /// pale shapes against the sky.
    /// </summary>
    private const float OpenFogDensity = 0.004f;

    /// <summary>The fog thins by e every this many metres above the fog's base, so valleys fill and ridges stand clear.</summary>
    private const float FogFalloffHeightMetres = 45f;

    /// <summary>The fog's base sits this far below the eyes, so the ground the player stands on has the full density.</summary>
    private const float FogBaseBelowEyesMetres = 12f;

    /// <summary>The air brightens toward the sun by this colour; a larger exponent gathers it nearer the sun.</summary>
    private static readonly Vector3 DayHazeColour = new(0.75f, 0.72f, 0.6f);
    private static readonly Vector3 DuskHazeColour = new(0.9f, 0.45f, 0.18f);
    private const float HazeExponent = 6f;
    private const float SunRadiusDegrees = 1.6f;
    private const float SunHalo = 0.35f;

    /// <summary>Light streams past what stands before a low sun; it fades by full day.</summary>
    private const float SunShaftIntensity = 0.7f;
    private const double SunShaftsFadeAbove = 0.5d;

    /// <summary>Only emission and highlights above white glow; lamps and the sun's halo bloom, lit ground does not.</summary>
    private const float BloomThreshold = 1f;
    private const float BloomIntensity = 0.3f;

    /// <summary>
    /// A little more colour and contrast than the operator leaves: the painted ground reads as
    /// ochre and sage rather than grey under the sky's blue light and the haze.
    /// </summary>
    private const float GradingSaturation = 0.18f;
    private const float GradingContrast = 0.06f;

    private readonly IEngineContext engine;
    private readonly Func<Vector3> observer;
    private readonly RenderResource day;
    private readonly RenderResource night;
    private Light? sun;
    private Light? skyShadow;
    private Light? fill;
    private double litDaylight = double.NaN;
    private Vector3? skyShadowAround;
    private bool underground;
    private bool submerged;
    private bool disposed;

    /// <summary>Underground there is no sky: a near-black background, no sun, and a faint fill.</summary>
    private static readonly Color UndergroundBackground = new(0.02f, 0.02f, 0.03f, 1f);

    /// <summary>
    /// The fill underground: faint, so what the dungeon's own lights do not reach falls toward
    /// black and a drop reads as depth. It is an ambient light, not the hemisphere, because a
    /// dungeon's probe volume (DungeonModule) keeps the ambient row everywhere and adds the
    /// torches' bounce over it; a hemisphere light reaches an enclosed room only through the probes,
    /// which see none of it there.
    /// </summary>
    private const float UndergroundFillIntensity = 0.18f;
    private static readonly Vector3 UndergroundFillColour = new(0.62f, 0.6f, 0.58f);

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

    /// <param name="observer">The eyes' position in the Engine's local frame: the sky's occlusion square follows them.</param>
    internal DayNightSky(IEngineContext engine, Func<Vector3> observer)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        day = Panorama(DayPanoramaContentPath);
        night = Panorama(NightPanoramaContentPath);
        engine.CameraView.SetToneMapping(new ToneMappingRequest(Operator, Exposure));
        engine.CameraView.SetBloom(new BloomRequest(BloomThreshold, BloomIntensity));
        engine.CameraView.SetColorGrading(new ColorGradingRequest(0f, 0f, GradingContrast, GradingSaturation));
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
        skyShadowAround = null;
        Veil();
        if (!below)
        {
            Show(time);
            return;
        }

        engine.CameraView.SetSkyLight(new SkyLightRequest(0f));
        engine.CameraView.SetAtmosphere(default);
        engine.CameraView.SetSunShafts(new SunShaftsRequest(0f, 0f));
        Relight(
            Directional(MoonColour, 0f, -Vector3.UnitY) with { Enabled = false },
            SkyShadow(0d, Vector3.Zero) with { Color = UndergroundFillColour, Intensity = UndergroundFillIntensity, ShadowIntent = LightShadowIntent.Disabled },
            LightDescriptor.Hemisphere(NightSkyColour, NightGroundColour, 0f, enabled: false));
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
        litDaylight = double.NaN;
        Veil();
    }

    /// <summary>Whether the view is under water.</summary>
    internal bool ViewSubmerged => submerged;

    /// <summary>
    /// Shows the sky and lights for a moment. Lights are replaced only when daylight has moved
    /// or the player has walked out from under the sky's occlusion square.
    /// </summary>
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

        Vector3 eyes = observer();
        bool moved = skyShadowAround is not Vector3 around || Vector3.Distance(around, eyes) > SkyFollowStepMetres;
        if (sun is not null && Math.Abs(daylight - litDaylight) < RelightStep && !moved)
        {
            return;
        }

        Vector3 skyCentre = moved ? eyes : skyShadowAround!.Value;
        skyShadowAround = skyCentre;
        Relight(Sunlight(time.DayFraction, daylight), SkyShadow(daylight, skyCentre), Fill(time.DayFraction, daylight));
        if (!submerged)
        {
            Air(time.DayFraction, daylight, eyes);
        }

        litDaylight = daylight;
    }

    /// <summary>Releases this world's Engine lights and panorama handles before another world takes their place.</summary>
    public void Dispose()
    {
        if (disposed) return;
        engine.CameraView.SetSkyLight(new SkyLightRequest(0f));
        engine.CameraView.SetAtmosphere(default);
        engine.CameraView.SetSunShafts(new SunShaftsRequest(0f, 0f));
        engine.CameraView.SetFog(new(FogMode.Off, default, 0f, 0f, 0f));
        engine.CameraView.ClearSkyBackground(new ClearSkyBackgroundRequest(0U));
        sun?.Dispose();
        skyShadow?.Dispose();
        fill?.Dispose();
        day.Dispose();
        night.Dispose();
        disposed = true;
        sun = null;
        skyShadow = null;
        fill = null;
        litDaylight = double.NaN;
    }

    private void Relight(LightDescriptor sunlight, LightDescriptor sky, LightDescriptor hemisphere)
    {
        if (sun is Light retainedSun && skyShadow is Light retainedSky && fill is Light retainedFill)
        {
            engine.Graphics.UpdateLight(new LightUpdateRequest(retainedSun, Request(ProductIds.SunLight, sunlight)));
            engine.Graphics.UpdateLight(new LightUpdateRequest(retainedSky, Request(ProductIds.AmbientLight, sky)));
            engine.Graphics.UpdateLight(new LightUpdateRequest(retainedFill, Request(ProductIds.FillLight, hemisphere)));
        }
        else
        {
            sun = engine.Graphics.CreateLight(Request(ProductIds.SunLight, sunlight));
            skyShadow = engine.Graphics.CreateLight(Request(ProductIds.AmbientLight, sky));
            fill = engine.Graphics.CreateLight(Request(ProductIds.FillLight, hemisphere));
        }
    }

    /// <summary>
    /// The fog and background for where the eyes are: water's murk under water, the dark underground,
    /// and in the open the sky's horizon, set with the lights by <see cref="Show"/>.
    /// </summary>
    private void Veil()
    {
        if (submerged)
        {
            engine.CameraView.SetBackgroundColor(new SetBackgroundColorRequest(WaterMurk));
            engine.CameraView.SetFog(new(FogMode.ExponentialSquared, WaterMurk, 0f, 0f, WaterFogDensity));
            engine.CameraView.SetSkyLight(new SkyLightRequest(0f));
            engine.CameraView.SetAtmosphere(default);
            engine.CameraView.SetSunShafts(new SunShaftsRequest(0f, 0f));
        }
        else if (underground)
        {
            engine.CameraView.SetBackgroundColor(new SetBackgroundColorRequest(UndergroundBackground));
            engine.CameraView.SetFog(new(FogMode.ExponentialSquared, UndergroundFog, 0f, 0f, UndergroundFogDensity));
        }
    }

    /// <summary>The air in the open: the horizon's fog, haze toward the sun, its disc, and its shafts when it is low.</summary>
    private void Air(double dayFraction, double daylight, Vector3 eyes)
    {
        double elevation = WorldClock.SunElevation(dayFraction);
        Vector3 horizon = Vector3.Lerp(NightHorizon, DayHorizon, (float)daylight);
        engine.CameraView.SetFog(new(FogMode.ExponentialSquared, new Color(horizon.X, horizon.Y, horizon.Z, 1f), 0f, 0f, OpenFogDensity));
        float warmth = Warmth(elevation);
        Vector3 haze = Vector3.Lerp(DayHazeColour, DuskHazeColour, warmth) * (float)daylight;
        engine.CameraView.SetSkyLight(new SkyLightRequest((float)(NightSkyLightIntensity + ((DaySkyLightIntensity - NightSkyLightIntensity) * daylight))));
        engine.CameraView.SetAtmosphere(new AtmosphereRequest(
            eyes.Y - FogBaseBelowEyesMetres, FogFalloffHeightMetres,
            new Color(haze.X, haze.Y, haze.Z, 1f), HazeExponent, SunRadiusDegrees, SunHalo));
        float shafts = elevation > 0d
            ? SunShaftIntensity * (1f - (float)Smooth(elevation, 0d, SunShaftsFadeAbove))
            : 0f;
        engine.CameraView.SetSunShafts(new SunShaftsRequest(shafts, 0f));
    }

    /// <summary>
    /// The sun by day, warming and weakening toward the horizon; through twilight its glow from
    /// along the horizon; and the moon opposite it by night.
    /// </summary>
    private static LightDescriptor Sunlight(double dayFraction, double daylight)
    {
        double elevation = WorldClock.SunElevation(dayFraction);
        Vector3 toward = WorldClock.TowardSun(dayFraction);
        if (elevation >= 0d)
        {
            float intensity = TwilightSunIntensity + ((DaySunIntensity - TwilightSunIntensity) * (float)Smooth(elevation, 0d, SunFullAbove));
            return Directional(Vector3.Lerp(SunColour, DuskSunColour, Warmth(elevation)), intensity, -toward);
        }

        if (elevation > TwilightBelow)
        {
            // The glow comes from where the sun went down, a little above the horizon so the
            // ground still catches it.
            Vector3 alongHorizon = Vector3.Normalize(new Vector3(toward.X, TwilightGlowElevation, toward.Z));
            float intensity = TwilightSunIntensity * (float)Smooth(elevation, TwilightBelow, 0d);
            return Directional(DuskSunColour, intensity, -alongHorizon);
        }

        return Directional(MoonColour, MoonIntensity * (float)(1d - daylight), toward);
    }

    /// <summary>How much of the twilight glow there is: full with the sun just under the horizon, none by day or deep night.</summary>
    private static double Twilight(double elevation) =>
        elevation >= 0d ? 1d - Smooth(elevation, 0d, SunFullAbove) : Smooth(elevation, TwilightBelow, 0d);

    /// <summary>How far the sun has warmed toward its dusk colour: fully at the horizon, not at all high in the sky.</summary>
    private static float Warmth(double elevation) => 1f - (float)Smooth(elevation, SunWarmBelow, SunWhiteAbove);

    private static double Smooth(double value, double from, double to)
    {
        double t = Math.Clamp((value - from) / (to - from), 0d, 1d);
        return t * t * (3d - (2d * t));
    }

    /// <summary>The sky's occlusion layer: a faint ambient light whose shadow is the open sky over a square around the player.</summary>
    private static LightDescriptor SkyShadow(double daylight, Vector3 centre) => new(
        LightKind.Ambient,
        Vector3.Lerp(NightSkyColour, DaySkyColour, (float)daylight),
        (float)(NightSkyShadowIntensity + ((DaySkyShadowIntensity - NightSkyShadowIntensity) * daylight)),
        true, centre, -Vector3.UnitY, true, SkyShadowRangeMetres, 0f, 0f, 0f,
        LightShadowIntent.Requested, SkyShadowResolution, 0, false);

    private static LightDescriptor Fill(double dayFraction, double daylight)
    {
        float twilight = (float)Twilight(WorldClock.SunElevation(dayFraction));
        Vector3 sky = Vector3.Lerp(Vector3.Lerp(NightSkyColour, DaySkyColour, (float)daylight), TwilightSkyColour, twilight);
        float intensity = (float)(NightFillIntensity + ((DayFillIntensity - NightFillIntensity) * daylight)) + (TwilightFillIntensity * twilight);
        return LightDescriptor.Hemisphere(sky, Vector3.Lerp(NightGroundColour, DayGroundColour, (float)daylight), intensity);
    }

    private static LightDescriptor Directional(Vector3 colour, float intensity, Vector3 travel) => new(
        LightKind.Directional, colour, intensity, true, Vector3.Zero, travel, true, SunShadowRangeMetres, 0f, 0f, 0f,
        LightShadowIntent.Requested, SunShadowResolution, 0, false);

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
