using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>Live-debug adapter over the weather (#9738): read it here, list the fronts, forecast, and summon a front for diagnosis.</summary>
public sealed class WeatherDebugModule : IDebugCommandModule
{
    private readonly Func<WeatherModule> weatherSource;
    private readonly Func<SnowModule> snowSource;
    private WeatherModule weather => weatherSource();

    internal WeatherDebugModule(Func<WeatherModule> weather, Func<SnowModule> snow)
    {
        weatherSource = weather ?? throw new ArgumentNullException(nameof(weather));
        snowSource = snow ?? throw new ArgumentNullException(nameof(snow));
    }

    [DebugCommand("craft.weather.here", Description = "Reads the weather over the player: the strongest front, its channels and its effects on play.")]
    public string Here() => weather.Readout();

    [DebugCommand("craft.weather.fronts", Description = "Lists the fronts within the given kilometres of the player: where, heading, strength, and when each would arrive.")]
    public string Fronts(double kilometres) => weather.FrontsReadout(kilometres);

    [DebugCommand("craft.weather.forecast", Description = "Forecasts the weather over the player for the given hours ahead, every three hours.")]
    public string Forecast(double hours) => weather.ForecastReadout(hours);

    [DebugCommand("craft.weather.summon", Description = "Assisted: places a front of a kind (rain, snow, sand, fog, glass) the given kilometres upwind of the player, at full strength.")]
    public string Summon(string kind, double upwindKilometres) => weather.Summon(kind, upwindKilometres);

    [DebugCommand("craft.weather.clear", Description = "Lifts every summoned front; the world's own weather stays.")]
    public string Clear() => weather.ClearSummoned();

    [DebugCommand("craft.snow.readout", Description = "Reads the snow where the player stands (#10068): its depth, frost, the air against freezing, what falls, and the kept cells.")]
    public string Snow() => snowSource().Readout();

    [DebugCommand("craft.snow.lay", Description = "Assisted: lays the given metres of snow around the player at once (negative takes it away).")]
    public string Lay(double metres) => snowSource().Lay(metres);
}
