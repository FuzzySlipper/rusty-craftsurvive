using System.Numerics;
using CraftSurvive.Game.Modules.Weather;
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
    /// Distance fog in the open: the horizon colour of each panorama in linear light (printed by
    /// <c>scripts/make-sky-gradient.py</c>), blended as the sky is, so the far ground fades exactly
    /// into the sky behind it.
    /// </summary>
    private static readonly Vector3 DayHorizon = new(0.4172f, 0.5232f, 0.5619f);
    private static readonly Vector3 NightHorizon = new(0.0094f, 0.0160f, 0.0427f);

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
        day = Panorama(DayPanoramaContentPath);
        night = Panorama(NightPanoramaContentPath);
        engine.CameraView.SetBloom(new BloomRequest(BloomThreshold, BloomIntensity));
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
        engine.CameraView.SetSkyBackgroundBlend(new SkyBackgroundBlendRequest(day, night, 0f));
        engine.CameraView.SetFog(new(FogMode.Off, default, 0f, 0f, 0f));
        engine.CameraView.SetAtmosphere(default);
        engine.CameraView.SetSunShafts(new SunShaftsRequest(0f, 0f));
        engine.CameraView.SetSkyLight(new SkyLightRequest(DaySkyLightIntensity));
        Relight(
            Directional(SunColour, DaySunIntensity, MapSunTravel),
            SkyShadow(1d, Vector3.Zero) with { ShadowIntent = LightShadowIntent.Disabled },
            Fill(0.5d, 1d));
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
        engine.CameraView.SetColorGrading(new ColorGradingRequest(
            gradeTemperature + (CloudCooling * w.Cloud) + (ColdCooling * w.Cold),
            gradeTint + (ArcaneTint * w.Arcane),
            gradeContrast + (MurkFlattening * w.Murk),
            gradeSaturation + (CloudDesaturation * w.Cloud) + (ArcaneSaturation * w.Arcane)));
        Vector2 flow = w.Flow.LengthSquared() > 0 ? w.Flow : Vector2.Normalize(WindDirection);
        engine.CameraView.SetWind(new(flow, windStrength * (1f + (WindStrengthening * w.Wind)), Math.Min(1f, WindGust + (WindGusting * w.Wind))));
        // The background is never fogged, so murk (fog, blown sand) veils the sky through the cloud layer, tinted by the air.
        float cover = Math.Max(w.Cloud, MurkCloud * w.Murk);
        Vector3 tint = Vector3.One * (1f - (CloudDarkening * w.Cloud));
        if (w.Air != Vector3.Zero) tint = Vector3.Lerp(tint, w.Air * MurkCloudBrightness, w.Murk);
        engine.CameraView.SetClouds(cover > CloudAppearsAbove
            ? new CloudsRequest(Math.Min(1f, CloudCoverageFloor + (CloudCoverageSpan * cover)), flow * (CloudDriftMetresPerSecond + (WindDriftMetresPerSecond * w.Wind)),
                CloudAltitudeMetres, CloudScaleMetres, tint)
            : NoClouds);
        Falling = Precipitation(w);
        engine.CameraView.SetPrecipitation(Falling);
        ArcaneVeil(w);
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
        LightDescriptor sunlight = Sunlight(time.DayFraction, daylight);
        Relight(sunlight with { Intensity = sunlight.Intensity * (1f - (CloudSunDim * weather.Cloud)) }, SkyShadow(daylight, skyCentre), Fill(time.DayFraction, daylight));
        if (!submerged)
        {
            Air(time.DayFraction, daylight, eyes);
        }

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
        WeatherLook w = weather;
        // The weather's air greys the horizon toward the fronts' colour, darker by night, and thickens the fog.
        Vector3 horizon = Vector3.Lerp(NightHorizon, DayHorizon, (float)daylight);
        float airShare = Math.Min(1f, (CloudAirShare * w.Cloud) + (MurkAirShare * w.Murk));
        if (w.Air != Vector3.Zero) horizon = Vector3.Lerp(horizon, w.Air * (NightAir + ((1f - NightAir) * (float)daylight)), airShare);
        float weatherThickening = 1f + (MurkFog * w.Murk) + (RainFog * w.Precipitation) + (CloudFog * w.Cloud);
        engine.CameraView.SetFog(horizonShown
            ? new(FogMode.Exponential, new Color(horizon.X, horizon.Y, horizon.Z, 1f), 0f, 0f, horizonFogDensity / fogReach * weatherThickening)
            : new(FogMode.ExponentialSquared, new Color(horizon.X, horizon.Y, horizon.Z, 1f), 0f, 0f, OpenFogDensity / fogReach * weatherThickening));
        float warmth = Warmth(elevation);
        Vector3 haze = Vector3.Lerp(DayHazeColour, DuskHazeColour, warmth) * (float)daylight * (1f - w.Cloud);
        engine.CameraView.SetSkyLight(new SkyLightRequest((float)(NightSkyLightIntensity + ((DaySkyLightIntensity - NightSkyLightIntensity) * daylight)) * (1f - (CloudSkyLightDim * w.Cloud))));
        engine.CameraView.SetAtmosphere(new AtmosphereRequest(
            eyes.Y - FogBaseBelowEyesMetres, FogFalloffHeightMetres + (MurkFogFalloffMetres * w.Murk),
            new Color(haze.X, haze.Y, haze.Z, 1f), HazeExponent, SunRadiusDegrees, SunHalo * (1f - w.Cloud)));
        float shafts = elevation > 0d
            ? SunShaftIntensity * (1f - (float)Smooth(elevation, 0d, SunShaftsFadeAbove)) * (1f - w.Cloud)
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
