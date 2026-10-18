using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// What the cold looks like around the party (#10068), read from the snow cover's air: breath puffing from the player
/// and the creatures near them in freezing air, and frost creeping in from the view's edges in bitter cold out in the
/// open. Presentation only: nothing here is saved or changes play.
/// </summary>
internal sealed class ColdModule : IProductModule
{
    /// <summary>Breath shows once the air is this far below freezing, fullest this far below (product temperature units).</summary>
    private const double BreathBelow = 0.0, BreathFullBelow = 0.08;
    /// <summary>Seconds between breaths at rest and sprinting, and a creature's (each its own phase).</summary>
    private const double BreathSeconds = 3.2, SprintBreathSeconds = 1.4, CreatureBreathSeconds = 2.6;
    /// <summary>The player's breath is shown this far ahead of their eyes and this far below them.</summary>
    private const float BreathAhead = 0.4f, BreathBelowEyes = 0.14f;
    /// <summary>A creature breathes this high above its feet; only creatures this near are watched.</summary>
    private const float CreatureMouthHeight = 1.55f;
    internal const double CreatureBreathMetres = 30;
    /// <summary>The view's frost begins this far below freezing and is full this far below; wind and falling snow drive it in.</summary>
    private const double ScreenFrostBelow = 0.05, ScreenFrostFullBelow = 0.2, StillAirShare = 0.4;

    private const ulong BreathSalt = 0xB4EA_7400_1006_8000UL;

    /// <summary>A breath: a few soft pale puffs that drift and swell as they fade.</summary>
    private static readonly BurstStyle Breath = new(
        "craftsurvive.breath", BurstLook.Puff, 6, 0.9f, 1.5f,
        new(-0.08f, 0.02f, -0.08f), new(0.08f, 0.14f, 0.08f), new(0f, 0.05f, 0f),
        [(0f, 0.08f), (0.4f, 0.3f), (1f, 0.55f)],
        [(0f, new(0.84f, 0.87f, 0.92f, 0.0f)), (0.15f, new(0.84f, 0.87f, 0.92f, 0.5f)), (1f, new(0.86f, 0.88f, 0.92f, 0f))]);

    private readonly IEngineContext engine;
    private readonly SnowModule snow;
    private readonly Func<Vector3> eyeLocal;
    private readonly Func<Vector3> aheadLocal;
    private readonly Func<bool> sprinting;
    private readonly Func<bool> exposed;
    private readonly Func<IEnumerable<(int Id, Vector3 Feet)>> creatures;
    private readonly WorldFrame frame;
    private readonly Action<float> screenFrost;
    private readonly Dictionary<int, double> creatureBreath = [];
    private RenderResourceReference puff;
    private double seconds, playerBreath;
    private long breaths, refused;
    private float shownFrost;
    private string lastFailure = "none";

    /// <param name="aheadLocal">A point a metre ahead of the player's eyes along their aim, in the local frame.</param>
    /// <param name="exposed">Whether the player is out in the open air (not sheltered, not underground or under water).</param>
    /// <param name="creatures">The awake creatures near the player, with their feet in world metres.</param>
    internal ColdModule(IEngineContext engine, SnowModule snow, WorldFrame frame, Func<Vector3> eyeLocal, Func<Vector3> aheadLocal, Func<bool> sprinting,
        Func<bool> exposed, Func<IEnumerable<(int Id, Vector3 Feet)>> creatures, Action<float> screenFrost)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.snow = snow ?? throw new ArgumentNullException(nameof(snow));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.eyeLocal = eyeLocal ?? throw new ArgumentNullException(nameof(eyeLocal));
        this.aheadLocal = aheadLocal ?? throw new ArgumentNullException(nameof(aheadLocal));
        this.sprinting = sprinting ?? throw new ArgumentNullException(nameof(sprinting));
        this.exposed = exposed ?? throw new ArgumentNullException(nameof(exposed));
        this.creatures = creatures ?? throw new ArgumentNullException(nameof(creatures));
        this.screenFrost = screenFrost ?? throw new ArgumentNullException(nameof(screenFrost));
    }

    /// <summary>How cold the air is for breath, 0 (none) to 1.</summary>
    internal double BreathCold { get; private set; }

    public void Start() => puff = engine.Graphics.OpenResource(new RenderResourceRequest(Bursts.PuffContentPath)).Handle;

    public void Update(ProductStep step)
    {
        seconds += step.ElapsedSeconds;
        double below = Seasons.Freezing - snow.Air;
        BreathCold = double.IsNaN(below) ? 0 : SmoothStep(BreathBelow, BreathFullBelow, below);
        Breathe();
        double wind = Math.Max(snow.Falling, 0);
        float frost = exposed() && !double.IsNaN(below)
            ? (float)(SmoothStep(ScreenFrostBelow, ScreenFrostFullBelow, below) * (StillAirShare + ((1 - StillAirShare) * Math.Min(1, wind))))
            : 0f;
        shownFrost = frost;
        screenFrost(frost);
    }

    private void Breathe()
    {
        if (BreathCold <= 0)
        {
            creatureBreath.Clear();
            return;
        }

        double every = sprinting() ? SprintBreathSeconds : BreathSeconds;
        if (seconds - playerBreath >= every)
        {
            playerBreath = seconds;
            Vector3 eye = eyeLocal(), forward = aheadLocal() - eye;
            forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward with { Y = 0 }) : Vector3.UnitZ;
            Emit(eye + (forward * BreathAhead) - (Vector3.UnitY * BreathBelowEyes), forward, 0);
        }

        foreach ((int id, Vector3 feet) in creatures())
        {
            if (!creatureBreath.TryGetValue(id, out double last))
            {
                // A creature newly near takes its own phase, so a pack does not breathe as one.
                creatureBreath[id] = seconds - (id * 0.618034 % 1 * CreatureBreathSeconds);
                continue;
            }

            if (seconds - last < CreatureBreathSeconds) continue;
            creatureBreath[id] = seconds;
            Emit(frame.ToLocal(feet.X, feet.Y + CreatureMouthHeight, feet.Z), Vector3.Zero, (ulong)id + 1);
        }
    }

    /// <summary>One breath, drifting a little along <paramref name="along"/>, fainter in milder cold.</summary>
    private void Emit(Vector3 at, Vector3 along, ulong who)
    {
        float cold = (float)BreathCold;
        BurstStyle style = Breath with
        {
            // Each axis's least and most stay ordered whichever way the breath drifts.
            VelocityMin = Vector3.Min(Breath.VelocityMin + (along * 0.25f), Breath.VelocityMax + (along * 0.5f)),
            VelocityMax = Vector3.Max(Breath.VelocityMin + (along * 0.25f), Breath.VelocityMax + (along * 0.5f)),
            Colours = [.. Breath.Colours.Select(key => (key.Age, key.Colour with { A = key.Colour.A * cold }))],
        };
        try
        {
            engine.Presentation.EmitParticles(Bursts.Describe(style, at, puff, unchecked((ulong)breaths * BreathSalt) ^ who));
            breaths++;
        }
        catch (EngineCallException exception)
        {
            refused++;
            lastFailure = exception.Message;
        }
    }

    public void Restart()
    {
        creatureBreath.Clear();
    }

    public void Dispose()
    {
    }

    private static double SmoothStep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - (2 * t));
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"cold breath={BreathCold:F2} breaths={breaths} refused={refused} creaturesBreathing={creatureBreath.Count} screenFrost={shownFrost:F2} failure={lastFailure}");
}
