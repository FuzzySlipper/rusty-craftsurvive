using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// What falls past the player's eyes (#9740): rain streaks, snow, blown dust or the glass storm's
/// glints, from a grid of retained emitters kept around the eyes and blown along the flow. The Engine
/// spawns particles from points, so the grid spreads them; its velocity ranges fill the gaps. This is
/// a stand-in until rusty-engine #9742 gives a camera-relative precipitation volume, and it falls
/// through roofs: nothing masks it under cover.
/// </summary>
internal sealed class WeatherParticles : IDisposable
{
    internal const string RainContentPath = "textures/weather-rain.png";
    internal const string FlakeContentPath = "textures/weather-flake.png";
    internal const string GlintContentPath = "textures/weather-glint.png";
    internal const string GrainContentPath = "textures/weather-grain.png";

    /// <summary>The grid: this many emitters a side, this far apart, re-centred once the eyes move this far.</summary>
    private const int Side = 3;
    private const float SpacingMetres = 8f, FollowMetres = 3f;
    /// <summary>Emitters are re-described once the density has moved this far.</summary>
    private const float DensityStep = 0.05f;
    private const float Quiet = 0.01f;

    private readonly IEngineContext engine;
    private readonly PresentationEmitter?[] emitters = new PresentationEmitter?[Side * Side];
    private readonly Dictionary<WeatherFall, RenderResource> sprites = [];
    private WeatherFall shownFall = WeatherFall.None;
    private float shownDensity;
    private Vector3 shownAround;
    private Vector2 shownFlow;
    private float shownWind;

    internal WeatherParticles(IEngineContext engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    internal string Readout => FormattableString.Invariant($"fall={shownFall} density={shownDensity:F2} emitters={emitters.Count(e => e is not null)}");

    /// <summary>Shows the look's fall around the eyes (in the Engine's local frame), or none when <paramref name="open"/> is false.</summary>
    internal void Show(WeatherLook look, Vector3 eyes, bool open)
    {
        WeatherFall fall = open && look.FallDensity > Quiet ? look.Fall : WeatherFall.None;
        float density = fall == WeatherFall.None ? 0 : look.FallDensity;
        if (fall == WeatherFall.None)
        {
            Clear();
            return;
        }

        bool moved = Vector3.Distance(eyes, shownAround) > FollowMetres;
        bool changed = fall != shownFall || Math.Abs(density - shownDensity) > DensityStep
            || Vector2.Distance(look.Flow, shownFlow) > DensityStep || Math.Abs(look.Wind - shownWind) > DensityStep;
        if (!moved && !changed && emitters[0] is not null) return;
        if (fall != shownFall) Clear();
        RenderResource sprite = Sprite(fall);
        for (int index = 0; index < emitters.Length; index++)
        {
            Vector3 at = eyes + Offset(fall, index, look);
            PresentationParticleDescriptor descriptor = Describe(fall, sprite, at, density, look, ProductIds.WeatherFallBase + (ulong)index);
            if (emitters[index] is PresentationEmitter emitter) engine.Presentation.UpdateEmitter(emitter, descriptor);
            else emitters[index] = engine.Presentation.CreateEmitter(descriptor);
        }

        shownFall = fall;
        shownDensity = density;
        shownAround = eyes;
        shownFlow = look.Flow;
        shownWind = look.Wind;
    }

    public void Dispose()
    {
        Clear();
        foreach (RenderResource sprite in sprites.Values) sprite.Dispose();
        sprites.Clear();
    }

    private void Clear()
    {
        for (int index = 0; index < emitters.Length; index++)
        {
            emitters[index]?.Dispose();
            emitters[index] = null;
        }

        shownFall = WeatherFall.None;
        shownDensity = 0;
    }

    /// <summary>Where one emitter of the grid stands from the eyes: above them for what falls, upwind at eye height for dust.</summary>
    private static Vector3 Offset(WeatherFall fall, int index, WeatherLook look)
    {
        float across = ((index % Side) - ((Side - 1) / 2f)) * SpacingMetres;
        float down = ((index / Side) - ((Side - 1) / 2f)) * SpacingMetres;
        Vector3 grid = new(across, 0, down);
        Vector3 upwind = new Vector3(look.Flow.X, 0, look.Flow.Y) * -SpacingMetres;
        return fall switch
        {
            WeatherFall.Rain => grid + new Vector3(0, RainHeight, 0) + (upwind * look.Wind * 0.5f),
            WeatherFall.Snow => grid + new Vector3(0, SnowHeight, 0) + (upwind * look.Wind),
            WeatherFall.Glitter => grid + new Vector3(0, GlitterHeight, 0),
            _ => grid + new Vector3(0, DustHeight, 0) + upwind,
        };
    }

    private const float RainHeight = 14f, SnowHeight = 7f, GlitterHeight = 8f, DustHeight = -0.5f;

    private static PresentationParticleDescriptor Describe(WeatherFall fall, RenderResource sprite, Vector3 at, float density, WeatherLook look, ulong id)
    {
        Vector3 flow = new(look.Flow.X, 0, look.Flow.Y);
        (float rate, uint maximum, float lifeMin, float lifeMax, Vector3 min, Vector3 max, float size, Color colour, PresentationParticleBlendMode blend) = fall switch
        {
            // Wide horizontal spread from high up, so each point's drops fill the grid's square rather than a column.
            WeatherFall.Rain => (360f, 300u, 0.8f, 1f, new Vector3(-6f, -19f, -6f) + (flow * 3f * look.Wind), new Vector3(6f, -16f, 6f) + (flow * 3f * look.Wind),
                0.45f, new Color(0.78f, 0.82f, 0.9f, 0.5f), PresentationParticleBlendMode.Alpha),
            WeatherFall.Snow => (80f, 300u, 3.5f, 5f, new Vector3(-2f, -2f, -2f) + (flow * (0.5f + (3f * look.Wind))), new Vector3(2f, -1.2f, 2f) + (flow * (0.5f + (3f * look.Wind))),
                0.1f, new Color(1f, 1f, 1f, 0.9f), PresentationParticleBlendMode.Alpha),
            WeatherFall.Glitter => (110f, 300u, 2.5f, 3.5f, new Vector3(-2f, -2.5f, -2f), new Vector3(2f, -1.5f, 2f),
                0.15f, new Color(1f, 0.75f, 0.95f, 0.9f), PresentationParticleBlendMode.Additive),
            _ => (220f, 300u, 1f, 1.6f, (flow * 10f) + new Vector3(-2f, -0.5f, -2f), (flow * 16f) + new Vector3(2f, 0.5f, 2f),
                0.45f, new Color(0.65f, 0.5f, 0.3f, 0.35f), PresentationParticleBlendMode.Alpha),
        };
        PresentationParticleColorKey[] colours = fall == WeatherFall.Glitter
            // The glass storm's glints turn through rose, sky and gold as they fall.
            ? [new() { Age = 0f, Color = colour }, new() { Age = 0.5f, Color = new Color(0.6f, 0.9f, 1f, 0.8f) }, new() { Age = 1f, Color = new Color(1f, 0.85f, 0.5f, 0f) }]
            : [new() { Age = 0f, Color = colour with { A = 0f } }, new() { Age = 0.15f, Color = colour }, new() { Age = 1f, Color = colour with { A = colour.A * 0.6f } }];
        return new PresentationParticleDescriptor
        {
            LogicalId = id,
            SignalId = "craftsurvive.weather." + fall.ToString().ToLowerInvariant(),
            Visible = true,
            Seed = id,
            Anchor = new PresentationAnchor { Kind = PresentationAnchorKind.World, Position = at },
            Visual = PresentationParticleVisual.Billboard,
            Sprite = sprite,
            SpriteFrameCount = 1,
            SizeMode = PresentationParticleSizeMode.World,
            Blend = blend,
            RatePerSecond = rate * density,
            MaxParticles = maximum,
            LifetimeMinSeconds = lifeMin,
            LifetimeMaxSeconds = lifeMax,
            VelocityMin = min,
            VelocityMax = max,
            Acceleration = Vector3.Zero,
            SizeCurve = new PresentationParticleScalarKey[] { new() { Age = 0f, Value = size }, new() { Age = 1f, Value = size } },
            ColorCurve = colours,
        };
    }

    private RenderResource Sprite(WeatherFall fall)
    {
        if (sprites.TryGetValue(fall, out RenderResource? sprite)) return sprite;
        string path = fall switch
        {
            WeatherFall.Rain => RainContentPath,
            WeatherFall.Snow => FlakeContentPath,
            WeatherFall.Glitter => GlintContentPath,
            _ => GrainContentPath,
        };
        RenderResourceInfo resource = engine.Graphics.OpenResource(new RenderResourceRequest(path));
        if (resource.Kind != RenderResourceKind.Texture || resource.ByteLength == 0)
        {
            throw new InvalidOperationException($"CraftSurvive weather sprite '{path}' must be a non-empty Engine texture.");
        }

        sprites[fall] = resource.Handle;
        return resource.Handle;
    }
}
