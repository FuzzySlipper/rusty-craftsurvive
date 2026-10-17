using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>Live-debug adapters over the place look study (#9992); it owns no state of its own.</summary>
public sealed class PlaceStudyDebugModule : IDebugCommandModule
{
    private readonly Func<PlaceStudyTest> testSource;
    private PlaceStudyTest test => testSource();

    internal PlaceStudyDebugModule(Func<PlaceStudyTest> test)
    {
        this.testSource = test ?? throw new ArgumentNullException(nameof(test));
    }

    [DebugCommand("craft.placestudy.variants", Description = "Test aid (#9992): the place study's variants (base a|b|c, paint proj|stamps).")]
    public string Variants() => string.Join('|', test.Names);

    [DebugCommand("craft.placestudy.place", Description = "Test aid (#9992): stand a place study variant at world x z, its broad face toward yaw degrees (0 north, 90 east); placing it again moves it.")]
    public string Place(string variant, double x, double z, double yaw) => test.Place(variant, x, z, yaw);

    [DebugCommand("craft.placestudy.near", Description = "Test aid (#9992): stand a place study variant metres east and south of the player, its broad face toward yaw degrees.")]
    public string Near(string variant, double east, double south, double yaw) => test.PlaceNear(variant, east, south, yaw);

    [DebugCommand("craft.placestudy.clear", Description = "Test aid (#9992): take every place study variant out of the world.")]
    public string Clear() => test.Clear();

    [DebugCommand("craft.placestudy.readout", Description = "Test aid (#9992): where each place study variant stands.")]
    public string Readout() => test.Readout();
}
