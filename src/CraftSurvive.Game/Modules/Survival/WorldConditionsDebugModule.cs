using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Survival;

/// <summary>Live-debug adapter over the world's conditions: read them, set the hour or the difficulty.</summary>
public sealed class WorldConditionsDebugModule : IDebugCommandModule
{
    private readonly Func<WorldConditionsModule> conditionsSource;
    private WorldConditionsModule conditions => conditionsSource();

    internal WorldConditionsDebugModule(Func<WorldConditionsModule> conditions) =>
        this.conditionsSource = conditions ?? throw new ArgumentNullException(nameof(conditions));

    [DebugCommand("craft.world.readout", Description = "Reads the world's time of day, daylight, difficulty and how its save is going.")]
    public string Readout() => conditions.Readout();

    [DebugCommand("craft.world.hour", Description = "Sets the hour of the current day (0 to below 24), to check night and day live.")]
    public string Hour(double hour) => conditions.SetHour(hour);

    [DebugCommand("craft.world.wind", Description = "Sets the wind's strength over the open world (0 stills grass and trees; 1 is the product's breeze).")]
    public string Wind(float strength) => conditions.SetWindStrength(strength);

    [DebugCommand("craft.world.grade", Description = "Sets the colour grade (temperature, tint, contrast, saturation, each about -1 to 1) and the exposure live, for tuning.")]
    public string Grade(float temperature, float tint, float contrast, float saturation, float exposure) =>
        conditions.SetGrade(temperature, tint, contrast, saturation, exposure);

    [DebugCommand("craft.world.difficulty", Description = "Sets the difficulty: gentle, normal or harsh.")]
    public string SetDifficulty(string name) => conditions.SetDifficulty(name);
}
