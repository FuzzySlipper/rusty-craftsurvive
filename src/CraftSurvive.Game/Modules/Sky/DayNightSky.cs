using System.Numerics;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Sky;

/// <summary>
/// The sky and the world's light, following the clock. The Engine blends two neighbouring authored
/// panoramas of the day, the twilights (<see cref="Twilight"/>) and the night,
/// lights the world from them (the sky's light), and keeps four retained lights that replace its
/// neutral rig (disabled in the project): the sun, casting through cascades by day; the moon on its
/// own month (<see cref="Moon"/>), casting by night; the sky's occlusion layer (an ambient light
/// whose shadow is the open sky, so caves and ground under a canopy lose the sky); and a hemisphere
/// fill. The same clock drives the distance fog, the air's haze toward the sun, the disc in the sky,
/// the sun's shafts and how far the eyes have adjusted to the dark. This module only decides the
/// blend and each light's colour, strength and direction from the time of day (#9544).
/// </summary>
internal sealed class DayNightSky : IDisposable
{
    internal const string DayPanoramaContentPath = "textures/sky-panorama.png";
    internal const string NightPanoramaContentPath = "textures/sky-night.png";

    /// <summary>
    /// Each base panorama, and its horizon in linear light, which the open fog takes as the sky is
    /// blended. Day and night: the horizon all round (printed by <c>scripts/make-sky-gradient.py</c>).
    /// The twilights: the horizon away from the sun (printed by <c>scripts/make-twilight-sky.py</c>),
    /// because the haze toward the sun warms the fog on its side.
    /// </summary>
    private static readonly IReadOnlyDictionary<SkyPanorama, (string Path, Vector3 Horizon)> Panoramas = new Dictionary<SkyPanorama, (string, Vector3)>
    {
        [SkyPanorama.Day] = (DayPanoramaContentPath, new(0.4172f, 0.5232f, 0.5619f)),
        [SkyPanorama.Night] = (NightPanoramaContentPath, new(0.0094f, 0.0160f, 0.0427f)),
        [SkyPanorama.DawnGlowVivid] = ("textures/sky-dawn-glow-vivid.png", new(0.2854f, 0.2587f, 0.2795f)),
        [SkyPanorama.DawnGlowMuted] = ("textures/sky-dawn-glow-muted.png", new(0.2539f, 0.2907f, 0.3245f)),
        [SkyPanorama.DawnAfterglowVivid] = ("textures/sky-dawn-afterglow-vivid.png", new(0.1194f, 0.1252f, 0.1547f)),
        [SkyPanorama.DawnAfterglowMuted] = ("textures/sky-dawn-afterglow-muted.png", new(0.1223f, 0.1482f, 0.1796f)),
        [SkyPanorama.DuskGlowVivid] = ("textures/sky-dusk-glow-vivid.png", new(0.2854f, 0.2587f, 0.2795f)),
        [SkyPanorama.DuskGlowMuted] = ("textures/sky-dusk-glow-muted.png", new(0.2539f, 0.2907f, 0.3245f)),
        [SkyPanorama.DuskAfterglowVivid] = ("textures/sky-dusk-afterglow-vivid.png", new(0.1194f, 0.1252f, 0.1547f)),
        [SkyPanorama.DuskAfterglowMuted] = ("textures/sky-dusk-afterglow-muted.png", new(0.1223f, 0.1482f, 0.1796f)),
    };

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

    /// <summary>
    /// The moon (#9806): its own light, beside the sun's (<see cref="Moon"/> decides how bright and
    /// what colour). Overcast cuts it. A full moon adds this much to the night's fill and sky light, so
    /// shade under a full moon is not black; a new moon adds nothing.
    /// </summary>
    private const float CloudMoonDim = 0.85f;
    private const float MoonFillIntensity = 0.06f, MoonSkyLight = 0.4f;

    /// <summary>
    /// The moon in the sky (rusty-engine #9866): its lit part glows at this radiance by night (it
    /// blooms) and this by day (a pale disc over the blue); by night its dark part shows this share as
    /// earthshine (by day it is the sky); a full moon has this halo by night, a crescent less.
    /// </summary>
    private const float NightMoonRadiance = 2.5f, DayMoonRadiance = 0.45f, Earthshine = 0.04f, MoonHalo = 0.25f;

    /// <summary>The moon is drawn from this far under the horizon (a sine), so it rises through the land rather than appearing on it.</summary>
    private const double MoonDrawnAbove = -0.05d;

    /// <summary>
    /// Eyes adjust to the dark: by night the exposure may rise to this, closing on its target at this
    /// rate (1/s), so darkness eases in over the first minutes rather than at once. By day, underground
    /// and on the map it stays 1. The background is never exposed, so the range stays small enough for
    /// the world to keep in step with its sky.
    /// </summary>
    private const float NightMaxExposure = 2.5f, EyeAdjustRate = 0.05f;

    /// <summary>By night the grade cools and loses colour by this much (at no daylight), so moonlight reads as night rather than a dim day.</summary>
    private const float NightCooling = -0.12f, NightDesaturation = -0.35f;
    private float gradeNight;

    /// <summary>The sun's and moon's lights follow them across the sky in steps of this cosine (about half a degree).</summary>
    private const float SkyMoveStep = 0.99996f;

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

    /// <summary>
    /// The unshadowed fill: what a shaded place still gets, so shade is not black by day. By night,
    /// with no moon, it is starlight: dark, but the ground's shape still shows.
    /// </summary>
    private const float DayFillIntensity = 0.22f;
    private const float NightFillIntensity = 0.16f;

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

    // Light colours are linear, tuned by eye as the Engine draws them (#9844): not palette entries, so not decoded.
    private static readonly Vector3 SunColour = new(1f, 0.95f, 0.86f);
    private static readonly Vector3 DuskSunColour = new(1f, 0.5f, 0.24f);
    private static readonly Vector3 DaySkyColour = new(0.55f, 0.7f, 0.95f);
    private static readonly Vector3 NightSkyColour = new(0.25f, 0.32f, 0.6f);
    private static readonly Vector3 DayGroundColour = new(0.38f, 0.33f, 0.24f);
    private static readonly Vector3 NightGroundColour = new(0.08f, 0.08f, 0.12f);

    /// <summary>
    /// How quickly distance fades in the open (exponential squared, so the near ground is clear
    /// and the far closes): half the ground is left at 200 m, where the far field (FarField) has
    /// taken over from the drawn chunks, and a tenth at 400 m, so ridges a kilometre off stand as
    /// pale shapes against the sky.
    /// </summary>
    private const float OpenFogDensity = 0.004f;

    /// <summary>
    /// With the map on the horizon (#9779) the land runs on past the far field, so the haze no longer
    /// hides an edge: the open fog thins with distance (exponential, not squared), keeping a tenth of a
    /// ridge at about <c>ln 10 / density</c> metres (11.5 km), so ranges tens of kilometres off read as
    /// pale shapes, and a weather front's veil and its cloud body stay together farther out (#9800).
    /// </summary>
    private const float HorizonFogDensity = 0.0002f;
    private bool horizonShown;
    private float horizonFogDensity = HorizonFogDensity;

    /// <summary>Whether the map stands on the horizon behind the world: the open fog thins to show it.</summary>
    internal void Horizon(bool shown)
    {
        if (disposed || shown == horizonShown) return;
        horizonShown = shown;
        litDaylight = double.NaN;
    }

    /// <summary>Sets the horizon fog's density live, for tuning; the next world starts from the constant again.</summary>
    internal string SetHorizonFog(float density)
    {
        horizonFogDensity = Math.Max(0f, density);
        litDaylight = double.NaN;
        return FormattableString.Invariant($"horizon fog density={horizonFogDensity} shown={horizonShown}");
    }

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

    /// <summary>
    /// A low sun (#9805): its disc grows toward this radius and its halo by this much as it warms. The
    /// haze toward it strengthens by up to this much and spreads wider (a smaller exponent).
    /// </summary>
    private const float LowSunRadiusDegrees = 2.6f;
    private const float LowSunHalo = 0.5f;
    private const float LowSunHaze = 0.9f;
    private const float LowSunHazeExponent = 2.5f;

    /// <summary>
    /// After sunset the ground's twilight light rises from the horizon to its glow above it over this
    /// span (as a sine), so shadows swing up rather than jump. The sky's sun is placed apart from it
    /// (rusty-engine #9858, #9859): the disc sets behind the land and the clouds take the real sun.
    /// </summary>
    private const double GlowRisesOver = 0.03d;

    /// <summary>
    /// The sky's sun: the disc's own HDR radiance (it blooms), by day and as it warms toward the
    /// horizon; and its light on the clouds and haze while low, whatever the ground's light is.
    /// </summary>
    private const float DaySunRadiance = 4f, LowSunRadiance = 12f;
    private const float LowSkySunIntensity = 1.2f;

    /// <summary>
    /// The clouds keep the sun after sunset until it is this far under for a cloud at the layer's
    /// altitude, reddening as it sinks: through the afterglow, gone before the moon takes the light.
    /// </summary>
    private const float CloudTwilightDegrees = 8f;

    /// <summary>
    /// The cloud layer lit by a low sun: tinted by the twilight toward gold while the sun is up, and
    /// toward pink once it has gone under (the colour turns between these elevations, as sines). A
    /// muted evening keeps a share of it, and heavy cloud mutes it (no light gets under an overcast).
    /// </summary>
    private static readonly Vector3 GoldenCloudColour = new(1.3f, 1f, 0.72f);
    private static readonly Vector3 TwilightCloudColour = new(1.35f, 0.82f, 0.85f);
    private const double CloudsPinkFrom = 0.08d, CloudsPinkTo = -0.02d;
    private const float MutedTwilight = 0.5f;

    private const float OvercastFrom = 0.55f, OvercastTo = 0.95f;

    /// <summary>Light streams past what stands before a low sun; it fades by full day.</summary>
    private const float SunShaftIntensity = 0.7f;
    private const double SunShaftsFadeAbove = 0.5d;

    /// <summary>Only emission and highlights above white glow; lamps and the sun's halo bloom, lit ground does not.</summary>
    private const float BloomThreshold = 1f;
    private const float BloomIntensity = 0.3f;

    /// <summary>
    /// A warm, nearly neutral grade over the repainted content (#9669): the meadow, trees and sky
    /// are already coloured, so the grade adds warmth and a little contrast rather than the
    /// saturation the old grey ground needed (which turned the new greens lime).
    /// </summary>
    private const float GradingTemperature = 0.08f;
    private const float GradingSaturation = 0.04f;
    private const float GradingContrast = 0.08f;

    private readonly IEngineContext engine;
    private readonly Func<Vector3> observer;
    private readonly Dictionary<SkyPanorama, RenderResource> panoramas = [];
    private Light? sun;
    private Light? skyShadow;
    private Light? fill;
    private Light? moon;
    private Vector3 litSunToward, litMoonToward;
    private double litDaylight = double.NaN;
    private SkyBlend litBlend;

    /// <summary>How spectacular this twilight is (1 vivid, less muted), and how much its glow lights the clouds now.</summary>
    private float spectacle = 1f;
    private float cloudGlow;
    private Vector3 cloudColour = TwilightCloudColour;

    /// <summary>A vividness held for captures instead of the day's own roll, or null.</summary>
    private float? heldVividness;
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

    /// <summary>
    /// The wind over the open world: grass, bushes and trees sway in it (their materials carry the
    /// bend and flutter). A steady breeze from the west-south-west, a little over half of it gusting.
    /// </summary>
    private static readonly Vector2 WindDirection = new(1f, 0.35f);
    private const float WindStrength = 1f;
    private const float WindGust = 0.6f;

    /// <summary>
    /// What the weather does to the sky and the light (#9740, Den <c>design/weather-and-environment</c>),
    /// per unit of its channels: cloud dims the sun, the shafts and the sky's light, cools the grade
    /// and greys it; murk and rain thicken the fog toward the fronts' air colour and fill the valleys;
    /// wind strengthens the sway and gusts along the flow; the arcane brightens and tints the colour.
    /// </summary>
    /// <summary>
    /// The Engine shades the sun where cloud lies toward it (rusty-engine #9743); the product adds only
    /// a little overall dimming and lets the sky's light fall with the cloud.
    /// </summary>
    private const float CloudSunDim = 0.2f, CloudSkyLightDim = 0.35f, CloudExposure = -0.2f;
    private const float CloudCooling = -0.1f, ColdCooling = -0.1f, CloudDesaturation = -0.15f, MurkFlattening = -0.06f;
    private const float ArcaneSaturation = 0.2f, ArcaneTint = 0.12f;
    private const float MurkFog = 8f, RainFog = 3f, CloudFog = 0.5f;
    private const float CloudAirShare = 0.6f, MurkAirShare = 0.8f, NightAir = 0.15f;
    private const float MurkFogFalloffMetres = 120f;
    private const float WindStrengthening = 3f, WindGusting = 0.4f;
    /// <summary>The cloud layer: coverage from fair to full with the cloud channel, drifting with the flow faster as the wind rises.</summary>
    private const float CloudCoverageFloor = 0.25f, CloudCoverageSpan = 0.7f, CloudAppearsAbove = 0.02f;
    private const float CloudDriftMetresPerSecond = 6f, WindDriftMetresPerSecond = 18f;
    private const float CloudAltitudeMetres = 1500f, CloudScaleMetres = 600f, CloudDarkening = 0.4f;
    /// <summary>Murk covers the sky like cloud, this much of it, tinted by the air at this brightness.</summary>
    private const float MurkCloud = 1f, MurkCloudBrightness = 1.6f;
    /// <summary>A clear sky: no cloud layer and no clouds pass.</summary>
    private static readonly CloudsRequest NoClouds = new(0f, Vector2.Zero, CloudAltitudeMetres, CloudScaleMetres, Vector3.One);

    /// <summary>The cloud layer's height: the base of every front's cloud, where its curtain on the horizon meets it (#9780).</summary>
    internal const float CloudBaseMetres = CloudAltitudeMetres;

    private IReadOnlyList<CloudRegionRequest> wantedRegions = [];
    private readonly HashSet<uint> placedRegions = [];

    /// <summary>
    /// The fronts' clouds (#9780): each a cloud region where it stands, so a storm on the horizon is
    /// cloud in the sky's own layer, and the one overhead is the same cloud. They are replaced as the
    /// fronts move, and lifted underground.
    /// </summary>
    internal void CloudRegions(IReadOnlyList<CloudRegionRequest> regions)
    {
        if (disposed) return;
        wantedRegions = regions ?? throw new ArgumentNullException(nameof(regions));
        PlaceRegions();
    }

    private void PlaceRegions()
    {
        IReadOnlyList<CloudRegionRequest> wanted = underground || onMap ? [] : wantedRegions;
        HashSet<uint> keep = [];
        foreach (CloudRegionRequest region in wanted)
        {
            engine.CameraView.SetCloudRegion(region);
            keep.Add(region.Id);
        }

        foreach (uint stale in placedRegions.Where(id => !keep.Contains(id)).ToList()) engine.CameraView.RemoveCloudRegion(new(stale));
        placedRegions.Clear();
        placedRegions.UnionWith(keep);
    }
    /// <summary>
    /// What falls (rusty-engine #9742), by kind at full density: how many drops, how they fall (down,
    /// and along the flow by the wind), their size, a streak's seconds of travel, their colour by
    /// day (dimmed toward night), and whether they add light. The volume reaches this far around the eyes.
    /// </summary>
    private const uint RainDrops = 40_000, SnowFlakes = 25_000, DustGrains = 12_000, GlassGlints = 9_000;
    private const float RainFall = 14f, SnowFall = 1.4f, DustFall = 0.3f, GlassFall = 1.8f;
    private const float RainDrift = 3f, SnowDrift = 4f, DustDrift = 14f, GlassDrift = 0.6f;
    private const float RainSize = 0.012f, SnowSize = 0.05f, DustSize = 0.2f, GlassSize = 0.06f, RainStreakSeconds = 0.04f;
    private static readonly Color RainColour = new(1.3f, 1.4f, 1.6f, 0.35f), SnowColour = new(2f, 2f, 2.1f, 0.85f);
    private static readonly Color DustColour = new(0.9f, 0.68f, 0.4f, 0.22f), GlassColour = new(5f, 3.5f, 4.5f, 0.9f);
    private const float PrecipitationRadius = 18f, PrecipitationHeight = 10f, NightPrecipitation = 0.15f;
    private const float DriftCalm = 0.2f;
    private float shownDaylight = 1f;

    /// <summary>How wet the ground looks (rusty-engine #9744): 0 dry to 1 soaked, and how much stands in puddles.</summary>
    internal void Wetness(float wetness, float puddles)
    {
        if (disposed) return;
        wetGround = (Math.Clamp(wetness, 0f, 1f), Math.Clamp(puddles, 0f, 1f));
        engine.CameraView.SetWetness(underground ? new WetnessRequest(0f, 0f) : new WetnessRequest(wetGround.Wetness, wetGround.Puddles));
    }

    private (float Wetness, float Puddles) wetGround;

    /// <summary>The precipitation for the weather: its kind and density, blown along the flow, dimmed by night.</summary>
    private PrecipitationRequest Precipitation(WeatherLook w)
    {
        // Nothing falls under ground or under water: the eyes see the murk, not the weather above it.
        if (underground || submerged || w.Fall == WeatherFall.None || w.FallDensity <= 0f) return NoPrecipitation;
        Vector3 along = new(w.Flow.X, 0f, w.Flow.Y);
        float wind = DriftCalm + w.Wind;
        float light = NightPrecipitation + ((1f - NightPrecipitation) * shownDaylight);
        Color Dim(Color colour) => new(colour.R * light, colour.G * light, colour.B * light, colour.A);
        uint Drops(uint full) => (uint)(full * Math.Clamp(w.FallDensity, 0f, 1f) * screenWeather);
        if (screenWeather <= 0f) return NoPrecipitation;
        return w.Fall switch
        {
            WeatherFall.Rain => new(Drops(RainDrops), PrecipitationShape.Streak, (along * RainDrift * wind) - (Vector3.UnitY * RainFall), RainSize, RainStreakSeconds,
                Dim(RainColour), false, PrecipitationRadius, PrecipitationHeight),
            WeatherFall.Snow => new(Drops(SnowFlakes), PrecipitationShape.Flake, (along * SnowDrift * wind) - (Vector3.UnitY * SnowFall), SnowSize, 0f,
                Dim(SnowColour), false, PrecipitationRadius, PrecipitationHeight),
            WeatherFall.Dust => new(Drops(DustGrains), PrecipitationShape.Flake, (along * DustDrift * wind) - (Vector3.UnitY * DustFall), DustSize, 0f,
                Dim(DustColour), false, PrecipitationRadius, PrecipitationHeight),
            _ => new(Drops(GlassGlints), PrecipitationShape.Flake, (along * GlassDrift) - (Vector3.UnitY * GlassFall), GlassSize, 0f,
                Dim(GlassColour), true, PrecipitationRadius, PrecipitationHeight),
        };
    }

    /// <summary>The glass storm's veil over the view (rusty-engine #9745), shown once the arcane channel passes this.</summary>
    internal const string VeilContentPath = "shaders/weather-veil.wgsl";
    private const float VeilAbove = 0.02f;
    private RenderResource? veil;
    private bool veiled;

    /// <summary>The arcane air ripples and splits the light over the view; nothing runs while there is none.</summary>
    private void ArcaneVeil(WeatherLook w)
    {
        bool wanted = !underground && w.Arcane * screenWeather > VeilAbove;
        if (!wanted)
        {
            if (veiled) engine.CameraView.SetImageEffect(default);
            veiled = false;
            return;
        }

        veil ??= engine.Graphics.OpenResource(new RenderResourceRequest(VeilContentPath)).Handle;
        engine.CameraView.SetImageEffect(new ImageEffectRequest(veil, new Vector4(w.Arcane * screenWeather, 0f, 0f, 0f)));
        veiled = true;
    }

    private static readonly PrecipitationRequest NoPrecipitation = new(0, PrecipitationShape.Streak, -Vector3.UnitY, 0.1f, 0f, new Color(1, 1, 1, 1), false, 1f, 1f);

    /// <summary>The lights and fog are replaced once the weather has moved this far.</summary>
    private const float WeatherRelightStep = 0.02f;

    private WeatherLook weather = WeatherLook.Clear(Vector2.Normalize(WindDirection));
    private WeatherLook litWeather = WeatherLook.Clear(Vector2.Normalize(WindDirection));
    private float gradeTemperature = GradingTemperature, gradeTint, gradeContrast = GradingContrast, gradeSaturation = GradingSaturation, gradeExposure = Exposure;
    private float windStrength = WindStrength;

    /// <param name="observer">The eyes' position in the Engine's local frame: the sky's occlusion square follows them.</param>
    internal DayNightSky(IEngineContext engine, Func<Vector3> observer)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.observer = observer ?? throw new ArgumentNullException(nameof(observer));
        foreach ((SkyPanorama key, (string path, _)) in Panoramas) panoramas[key] = Panorama(path);
        engine.CameraView.SetBloom(new BloomRequest(BloomThreshold, BloomIntensity));
        Eyes(0f);
        Weathered();
    }

    /// <summary>Sets the colour grade and exposure live, for tuning; the next world starts from the constants again.</summary>
    internal string SetGrade(float temperature, float tint, float contrast, float saturation, float exposure)
    {
        (gradeTemperature, gradeTint, gradeContrast, gradeSaturation, gradeExposure) = (temperature, tint, contrast, saturation, exposure);
        Weathered();
        return FormattableString.Invariant($"grade temperature={temperature} tint={tint} contrast={contrast} saturation={saturation} exposure={exposure}");
    }

    /// <summary>Sets the wind's strength (0 stills it), for tuning and for measuring its cost.</summary>
    internal string SetWindStrength(float strength)
    {
        windStrength = strength;
        Weathered();
        return FormattableString.Invariant($"wind strength={strength} gust={WindGust} direction={WindDirection.X},{WindDirection.Y}");
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
        PlaceRegions();
        Veil();
        Weathered();
        if (!below)
        {
            Show(time);
            return;
        }

        engine.CameraView.SetSkyLight(new SkyLightRequest(0f));
        engine.CameraView.SetAtmosphere(default);
        engine.CameraView.SetMoon(default);
        engine.CameraView.SetSunShafts(new SunShaftsRequest(0f, 0f));
        Eyes(0f);
        Relight(
            Off,
            SkyShadow(0d, Vector3.Zero) with { Color = UndergroundFillColour, Intensity = UndergroundFillIntensity, ShadowIntent = LightShadowIntent.Disabled },
            LightDescriptor.Hemisphere(NightSkyColour, NightGroundColour, 0f, enabled: false),
            Off);
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
        // Precipitation stops at once under water and resumes on surfacing.
        Weathered();
    }

    private bool onMap;
    /// <summary>The map screen's sun: from the north-west, about 50° up, as a map is conventionally lit.</summary>
    private static readonly Vector3 MapSunTravel = Vector3.Normalize(new Vector3(0.55f, -1.3f, 0.55f));

    /// <summary>
    /// Opens or closes the map screen's light (#9813). A map is read, not lived in: it is lit the same at
    /// any hour, by a sun from the north-west and a clear day's sky, with no fog, haze, clouds or
    /// precipitation, so its relief and colours read whatever the time and weather in the world. Closing
    /// it leaves the world's sky to be shown again (<see cref="Underground"/> or <see cref="Show"/>).
    /// </summary>
    internal void Map(bool open)
    {
        if (disposed || open == onMap) return;
        onMap = open;
        litDaylight = double.NaN;
        skyShadowAround = null;
        PlaceRegions();
        Weathered();
        if (!open) return;
        engine.CameraView.SetSkyBackgroundBlend(new SkyBackgroundBlendRequest(panoramas[SkyPanorama.Day], panoramas[SkyPanorama.Night], 0f));
        engine.CameraView.SetFog(new(FogMode.Off, default, 0f, 0f, 0f));
        engine.CameraView.SetAtmosphere(default);
        engine.CameraView.SetMoon(default);
        engine.CameraView.SetSunShafts(new SunShaftsRequest(0f, 0f));
        engine.CameraView.SetSkyLight(new SkyLightRequest(DaySkyLightIntensity));
        Eyes(0f);
        Relight(
            Directional(SunColour, DaySunIntensity, MapSunTravel),
            SkyShadow(1d, Vector3.Zero) with { ShadowIntent = LightShadowIntent.Disabled },
            Fill(0.5d, 1d, 0f),
            Off);
    }

    /// <summary>Whether the view is under water.</summary>
    internal bool ViewSubmerged => submerged;

    /// <summary>
    /// The weather over the player, as it should look now (#9740): the grade, wind and clouds follow
    /// at once; lights and fog are replaced on the next <see cref="Show"/> once it has moved enough.
    /// </summary>
    internal void Weather(WeatherLook look)
    {
        if (disposed) return;
        weather = look;
        Weathered();
        // Lights and fog follow once the weather has moved far enough from what they were set for.
        if (look.Distance(litWeather) > WeatherRelightStep) litDaylight = double.NaN;
    }

    internal WeatherLook Look => weather;

    private float screenWeather = 1f;
    private float fogReach = 1f;

    /// <summary>
    /// The player's options (#9759): how much of the weather is drawn on screen (0 to 1: what falls and
    /// the glass storm's veil; the weather still acts on the game), and how far the land is drawn as a
    /// share of the default reach. Exponential-squared fog keeps the same share of the land at the far
    /// field's edge when its density scales inversely with the reach.
    /// </summary>
    internal void Options(float weatherOnScreen, float reachShare)
    {
        if (disposed) return;
        screenWeather = Math.Clamp(weatherOnScreen, 0f, 1f);
        fogReach = Math.Max(0.1f, reachShare);
        litDaylight = double.NaN;
        Weathered();
    }

    /// <summary>The precipitation last given the Engine: none under ground or under water.</summary>
    internal PrecipitationRequest Falling { get; private set; } = NoPrecipitation;

    /// <summary>The grade, wind and cloud layer for the weather: the base values in the open, the base alone underground.</summary>
    private void Weathered()
    {
        if (disposed) return;
        WeatherLook w = underground || onMap ? WeatherLook.Clear(weather.Flow) : weather;
        engine.CameraView.SetToneMapping(new ToneMappingRequest(Operator, gradeExposure * (1f + (CloudExposure * w.Cloud))));
        // Colour fades and cools in the dark, as eyes see by night; not underground, where torchlight is warm.
        float night = underground || onMap ? 0f : gradeNight;
        engine.CameraView.SetColorGrading(new ColorGradingRequest(
            gradeTemperature + (CloudCooling * w.Cloud) + (ColdCooling * w.Cold) + (NightCooling * night),
            gradeTint + (ArcaneTint * w.Arcane),
            gradeContrast + (MurkFlattening * w.Murk),
            gradeSaturation + (CloudDesaturation * w.Cloud) + (ArcaneSaturation * w.Arcane) + (NightDesaturation * night)));
        Vector2 flow = w.Flow.LengthSquared() > 0 ? w.Flow : Vector2.Normalize(WindDirection);
        engine.CameraView.SetWind(new(flow, windStrength * (1f + (WindStrengthening * w.Wind)), Math.Min(1f, WindGust + (WindGusting * w.Wind))));
        Clouds(w);
        Falling = Precipitation(w);
        engine.CameraView.SetPrecipitation(Falling);
        ArcaneVeil(w);
    }

    /// <summary>
    /// The cloud layer for the weather: its cover, drift and tint. A low sun warms it, less under a
    /// heavy overcast that no light gets beneath.
    /// </summary>
    private void Clouds(WeatherLook w)
    {
        Vector2 flow = w.Flow.LengthSquared() > 0 ? w.Flow : Vector2.Normalize(WindDirection);
        // The background is never fogged, so murk (fog, blown sand) veils the sky through the cloud layer, tinted by the air.
        float cover = Math.Max(w.Cloud, MurkCloud * w.Murk);
        Vector3 tint = Vector3.One * (1f - (CloudDarkening * w.Cloud));
        float glow = underground || onMap ? 0f : cloudGlow * (1f - (float)Smooth(cover, OvercastFrom, OvercastTo));
        tint *= Vector3.Lerp(Vector3.One, cloudColour, glow);
        if (w.Air != Vector3.Zero) tint = Vector3.Lerp(tint, w.Air * MurkCloudBrightness, w.Murk);
        if (heldCloud is float held) cover = held;
        // A clear layer still carries the tint: the fronts' cloud regions take it.
        engine.CameraView.SetClouds(new CloudsRequest(cover > CloudAppearsAbove ? Math.Min(1f, CloudCoverageFloor + (CloudCoverageSpan * cover)) : 0f,
            flow * (CloudDriftMetresPerSecond + (WindDriftMetresPerSecond * w.Wind)), CloudAltitudeMetres, CloudScaleMetres, tint, 0f, CloudKind.Cumulus, CloudTwilightDegrees));
    }

    /// <summary>A cloud cover held for captures instead of the weather's, or null.</summary>
    private float? heldCloud;

    /// <summary>
    /// Captures only: holds the twilight's vividness (0 to 1; below 0 follows the day's own roll) and
    /// the cloud layer's cover (0 to 1; below 0 follows the weather), so dawn and dusk can be compared
    /// in clear and broken cloud. Only the sky's look is held; the weather's effects on play are not.
    /// </summary>
    internal string HoldTwilight(float vividness, float cloud)
    {
        heldVividness = vividness < 0f ? null : Math.Clamp(vividness, 0f, 1f);
        heldCloud = cloud < 0f ? null : Math.Clamp(cloud, 0f, 1f);
        litDaylight = double.NaN;
        Weathered();
        return FormattableString.Invariant($"twilight vividness={(heldVividness?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "rolled")} cloud={(heldCloud?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "weather")}");
    }

    /// <summary>
    /// Shows the sky and lights for a moment. Lights are replaced only when daylight has moved
    /// or the player has walked out from under the sky's occlusion square.
    /// </summary>
    internal void Show(WorldTime time)
    {
        if (underground || onMap || disposed)
        {
            return;
        }

        double daylight = WorldClock.Daylight(time.DayFraction);
        float vividness = heldVividness ?? Twilight.Vividness(time.Day, Twilight.Rising(time.DayFraction));
        SkyBlend blend = Twilight.Blend(time.DayFraction, vividness);
        if (!submerged)
        {
            engine.CameraView.SetSkyBackgroundBlend(new SkyBackgroundBlendRequest(panoramas[blend.From], panoramas[blend.To], blend.Amount));
        }

        Vector3 eyes = observer();
        bool moved = skyShadowAround is not Vector3 around || Vector3.Distance(around, eyes) > SkyFollowStepMetres;
        Vector3 sunToward = WorldClock.TowardSun(time.DayFraction), moonToward = WorldClock.TowardMoon(time);
        bool skyMoved = Vector3.Dot(sunToward, litSunToward) < SkyMoveStep || Vector3.Dot(moonToward, litMoonToward) < SkyMoveStep;
        if (sun is not null && Math.Abs(daylight - litDaylight) < RelightStep && !moved && !skyMoved && blend.From == litBlend.From)
        {
            return;
        }

        Vector3 skyCentre = moved ? eyes : skyShadowAround!.Value;
        skyShadowAround = skyCentre;
        LightDescriptor sunlight = Sunlight(time.DayFraction);
        float moonShare = Moon.Share(time) * (1f - (CloudMoonDim * weather.Cloud));
        Relight(
            sunlight with { Intensity = sunlight.Intensity * (1f - (CloudSunDim * weather.Cloud)) },
            SkyShadow(daylight, skyCentre),
            Fill(time.DayFraction, daylight, moonShare),
            Moonlight(time, moonShare, sunOff: !sunlight.Enabled));
        Eyes(1f - (float)daylight);
        if (Math.Abs(gradeNight - (1f - (float)daylight)) > RelightStep)
        {
            gradeNight = 1f - (float)daylight;
            Weathered();
        }

        litSunToward = sunToward;
        litMoonToward = moonToward;
        spectacle = Twilight.Mood(vividness) == TwilightMood.Vivid ? 1f : MutedTwilight;
        double sunElevation = WorldClock.SunElevation(time.DayFraction);
        cloudGlow = (float)TwilightGlow(sunElevation) * spectacle;
        cloudColour = Vector3.Lerp(GoldenCloudColour, TwilightCloudColour, (float)Smooth(sunElevation, CloudsPinkFrom, CloudsPinkTo));
        if (!submerged)
        {
            Air(time, daylight, blend, eyes, moonShare);
        }

        Clouds(underground || onMap ? WeatherLook.Clear(weather.Flow) : weather);
        litBlend = blend;
        litDaylight = daylight;
        litWeather = weather;
        shownDaylight = (float)daylight;
    }

    /// <summary>Releases this world's Engine lights and panorama handles before another world takes their place.</summary>
    public void Dispose()
    {
        if (disposed) return;
        engine.CameraView.SetSkyLight(new SkyLightRequest(0f));
        engine.CameraView.SetAtmosphere(default);
        engine.CameraView.SetMoon(default);
        engine.CameraView.SetSunShafts(new SunShaftsRequest(0f, 0f));
        engine.CameraView.SetFog(new(FogMode.Off, default, 0f, 0f, 0f));
        engine.CameraView.SetWind(new(WindDirection, 0f, 0f));
        engine.CameraView.SetClouds(NoClouds);
        foreach (uint region in placedRegions) engine.CameraView.RemoveCloudRegion(new(region));
        placedRegions.Clear();
        Falling = NoPrecipitation;
        engine.CameraView.SetPrecipitation(Falling);
        engine.CameraView.SetWetness(new WetnessRequest(0f, 0f));
        engine.CameraView.SetImageEffect(default);
        veil?.Dispose();
        veil = null;
        engine.CameraView.ClearSkyBackground(new ClearSkyBackgroundRequest(0U));
        sun?.Dispose();
        skyShadow?.Dispose();
        fill?.Dispose();
        moon?.Dispose();
        moon = null;
        foreach (RenderResource panorama in panoramas.Values) panorama.Dispose();
        panoramas.Clear();
        disposed = true;
        sun = null;
        skyShadow = null;
        fill = null;
        litDaylight = double.NaN;
    }

    private void Relight(LightDescriptor sunlight, LightDescriptor sky, LightDescriptor hemisphere, LightDescriptor moonlight)
    {
        if (sun is Light retainedSun && skyShadow is Light retainedSky && fill is Light retainedFill && moon is Light retainedMoon)
        {
            engine.Graphics.UpdateLight(new LightUpdateRequest(retainedSun, Request(ProductIds.SunLight, sunlight)));
            engine.Graphics.UpdateLight(new LightUpdateRequest(retainedSky, Request(ProductIds.AmbientLight, sky)));
            engine.Graphics.UpdateLight(new LightUpdateRequest(retainedFill, Request(ProductIds.FillLight, hemisphere)));
            engine.Graphics.UpdateLight(new LightUpdateRequest(retainedMoon, Request(ProductIds.MoonLight, moonlight)));
        }
        else
        {
            sun = engine.Graphics.CreateLight(Request(ProductIds.SunLight, sunlight));
            skyShadow = engine.Graphics.CreateLight(Request(ProductIds.AmbientLight, sky));
            fill = engine.Graphics.CreateLight(Request(ProductIds.FillLight, hemisphere));
            moon = engine.Graphics.CreateLight(Request(ProductIds.MoonLight, moonlight));
        }
    }

    /// <summary>A directional light switched off: no light, no cascades.</summary>
    private static readonly LightDescriptor Off = Directional(Vector3.One, 0f, -Vector3.UnitY) with { Enabled = false, ShadowIntent = LightShadowIntent.Disabled };

    /// <summary>How far the eyes may open: up to the night's limit as the daylight goes (0 by day, 1 by night).</summary>
    private void Eyes(float night) =>
        engine.CameraView.SetAutoExposure(new AutoExposureRequest(true, EyeAdjustRate, 1f, 1f + ((NightMaxExposure - 1f) * Math.Clamp(night, 0f, 1f))));

    /// <summary>
    /// The moon's own light, at the share of a full moon's it gives now (phase, height, cloud). It
    /// casts shadows only while the sun's light is off, so day and twilight never pay for two sets
    /// of cascades; off while it is down or new.
    /// </summary>
    private static LightDescriptor Moonlight(WorldTime time, float share, bool sunOff)
    {
        float intensity = Moon.FullIntensity * share;
        if (intensity <= 0f) return Off;
        return Directional(Moon.Colour(WorldClock.MoonElevation(time)), intensity, -WorldClock.TowardMoon(time)) with
        {
            ShadowIntent = sunOff ? LightShadowIntent.Requested : LightShadowIntent.Disabled,
        };
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
            engine.CameraView.SetMoon(default);
            engine.CameraView.SetSunShafts(new SunShaftsRequest(0f, 0f));
        }
        else if (underground)
        {
            engine.CameraView.SetBackgroundColor(new SetBackgroundColorRequest(UndergroundBackground));
            engine.CameraView.SetFog(new(FogMode.ExponentialSquared, UndergroundFog, 0f, 0f, UndergroundFogDensity));
        }
    }

    /// <summary>The air in the open: the horizon's fog, haze toward the sun, its disc, and its shafts when it is low.</summary>
    private void Air(WorldTime time, double daylight, SkyBlend blend, Vector3 eyes, float moonShare)
    {
        double dayFraction = time.DayFraction;
        double elevation = WorldClock.SunElevation(dayFraction);
        WeatherLook w = weather;
        // The far ground fades into the horizon of the sky behind it, blended as the sky is.
        Vector3 horizon = Vector3.Lerp(Panoramas[blend.From].Horizon, Panoramas[blend.To].Horizon, blend.Amount);
        // The weather's air greys the horizon toward the fronts' colour, darker by night, and thickens the fog.
        float airShare = Math.Min(1f, (CloudAirShare * w.Cloud) + (MurkAirShare * w.Murk));
        if (w.Air != Vector3.Zero) horizon = Vector3.Lerp(horizon, w.Air * (NightAir + ((1f - NightAir) * (float)daylight)), airShare);
        float weatherThickening = 1f + (MurkFog * w.Murk) + (RainFog * w.Precipitation) + (CloudFog * w.Cloud);
        engine.CameraView.SetFog(horizonShown
            ? new(FogMode.Exponential, new Color(horizon.X, horizon.Y, horizon.Z, 1f), 0f, 0f, horizonFogDensity / fogReach * weatherThickening)
            : new(FogMode.ExponentialSquared, new Color(horizon.X, horizon.Y, horizon.Z, 1f), 0f, 0f, OpenFogDensity / fogReach * weatherThickening));
        float warmth = Warmth(elevation);
        // A low sun hazes the air toward it more strongly and more widely, as much as the evening is spectacular.
        float low = (float)TwilightGlow(elevation) * spectacle;
        Vector3 haze = Vector3.Lerp(DayHazeColour, DuskHazeColour, warmth) * ((float)daylight + (LowSunHaze * low)) * (1f - w.Cloud);
        float nightSkyLight = NightSkyLightIntensity * (1f + (MoonSkyLight * moonShare));
        engine.CameraView.SetSkyLight(new SkyLightRequest((float)(nightSkyLight + ((DaySkyLightIntensity - nightSkyLight) * daylight)) * (1f - (CloudSkyLightDim * w.Cloud))));
        Vector3 hazeColour = new(haze.X, haze.Y, haze.Z);
        float hazeExponent = HazeExponent + ((LowSunHazeExponent - HazeExponent) * low);
        // The sky's sun is always the real sun, so the moon's lit limb faces it; by night it shows no disc.
        bool sunUp = SunInSky(elevation);
        engine.CameraView.SetAtmosphere(new AtmosphereRequest(
            eyes.Y - FogBaseBelowEyesMetres, FogFalloffHeightMetres + (MurkFogFalloffMetres * w.Murk), ToColor(hazeColour), hazeExponent,
            sunUp ? SunDisc(elevation) : 0f,
            sunUp ? (SunHalo + (LowSunHalo * warmth)) * (1f - w.Cloud) : 0f,
            sunUp ? (DaySunRadiance + ((LowSunRadiance - DaySunRadiance) * warmth)) * (1f - w.Cloud) : 0f,
            WorldClock.TowardSun(dayFraction), ToColor(Vector3.Lerp(SunColour, DuskSunColour, warmth)),
            LowSkySunIntensity + ((DaySunIntensity - LowSkySunIntensity) * (float)Smooth(elevation, 0d, SunFullAbove))));
        engine.CameraView.SetMoon(MoonInSky(time, daylight, w.Cloud));
        float shafts = elevation > 0d
            ? SunShaftIntensity * (1f - (float)Smooth(elevation, 0d, SunShaftsFadeAbove)) * (1f - w.Cloud)
            : 0f;
        engine.CameraView.SetSunShafts(new SunShaftsRequest(shafts, 0f));
    }

    /// <summary>
    /// The sun by day, warming and weakening toward the horizon; through twilight its glow from
    /// along the horizon; off by night, when the moon has its own light.
    /// </summary>
    private static LightDescriptor Sunlight(double dayFraction)
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
            // The glow comes from where the sun went down, a little above the horizon so the ground
            // still catches it, rising there from the sun's own direction as it sets.
            float rise = (float)Smooth(elevation, 0d, -GlowRisesOver);
            Vector3 alongHorizon = Vector3.Normalize(new Vector3(toward.X, toward.Y + ((TwilightGlowElevation - toward.Y) * rise), toward.Z));
            float intensity = TwilightSunIntensity * (float)Smooth(elevation, TwilightBelow, 0d);
            return Directional(DuskSunColour, intensity, -alongHorizon);
        }

        return Off;
    }

    /// <summary>Whether the sun shows in the sky: by day and through twilight, while its light is on.</summary>
    private static bool SunInSky(double elevation) => elevation > TwilightBelow;

    /// <summary>The sun's disc in degrees: larger as it warms toward the horizon.</summary>
    private static float SunDisc(double elevation) => SunRadiusDegrees + ((LowSunRadiusDegrees - SunRadiusDegrees) * Warmth(elevation));

    /// <summary>
    /// The moon in the sky, by day and by night: its phase (the Engine turns the lit limb toward the
    /// sun), warm and large low, a pale disc by day and a glowing one by night with a halo as full as
    /// it is; none while it is well under the horizon or new.
    /// </summary>
    private static MoonRequest MoonInSky(WorldTime time, double daylight, float cloud)
    {
        double elevation = WorldClock.MoonElevation(time);
        float lit = (float)WorldClock.MoonLit(time);
        if (elevation < MoonDrawnAbove || lit <= 0f) return default;
        float night = 1f - (float)daylight;
        return new MoonRequest(WorldClock.TowardMoon(time), Moon.Disc(elevation), Moon.Colour(elevation),
            DayMoonRadiance + ((NightMoonRadiance - DayMoonRadiance) * night), lit, Earthshine * night, MoonHalo * lit * night * (1f - cloud));
    }

    private static Color ToColor(Vector3 colour) => new(colour.X, colour.Y, colour.Z, 1f);

    /// <summary>How much of the twilight glow there is: full with the sun just under the horizon, none by day or deep night.</summary>
    private static double TwilightGlow(double elevation) =>
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

    /// <param name="moonShare">How much of a full moon's light there is now: it lifts the night's fill.</param>
    private static LightDescriptor Fill(double dayFraction, double daylight, float moonShare)
    {
        float twilight = (float)TwilightGlow(WorldClock.SunElevation(dayFraction));
        Vector3 sky = Vector3.Lerp(Vector3.Lerp(NightSkyColour, DaySkyColour, (float)daylight), TwilightSkyColour, twilight);
        float night = NightFillIntensity + (MoonFillIntensity * moonShare);
        float intensity = (float)(night + ((DayFillIntensity - night) * daylight)) + (TwilightFillIntensity * twilight);
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
