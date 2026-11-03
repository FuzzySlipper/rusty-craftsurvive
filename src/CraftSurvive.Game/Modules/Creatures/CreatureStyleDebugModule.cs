using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>Live-debug adapters over the creature style test (#10568); it owns no state of its own.</summary>
public sealed class CreatureStyleDebugModule : IDebugCommandModule
{
    private readonly Func<CreatureStyleTest> testSource;
    private CreatureStyleTest test => testSource();

    internal CreatureStyleDebugModule(Func<CreatureStyleTest> test)
    {
        this.testSource = test ?? throw new ArgumentNullException(nameof(test));
    }

    [DebugCommand("craft.creature.kinds", Description = "Test aid (#10568): the placeable creature kinds and their clips.")]
    public string Kinds() => string.Join(';', test.Kinds.Values.Select(k => $"{k.Name}:scale={k.Scale:F2}:clips={string.Join('|', k.Clips.Keys)}"));

    [DebugCommand("craft.creature.near", Description = "Test aid (#10568): place a creature kind metres east and south of the player, facing yaw degrees (0 north, 90 east), at a uniform scale (0 = the kind's).")]
    public string Near(string kind, double east, double south, double yaw, double scale) => test.PlaceNear(kind, east, south, yaw, (float)scale);

    [DebugCommand("craft.creature.clip", Description = "Test aid (#10568): play a clip on a creature by id, or on every creature with id -1; walk and run go round a circle at the clip's own speed.")]
    public string Clip(long id, string clip) => test.SetClip((int)id, clip);

    [DebugCommand("craft.creature.driver", Description = "Test aid (#10568): new creatures play through an Engine graph (graph) or direct sampled playback (direct).")]
    public string Driver(string driver)
    {
        if (driver is not ("graph" or "direct")) return "driver must be graph or direct";
        test.DirectPlayback = driver == "direct";
        return test.Readout();
    }

    [DebugCommand("craft.creature.step", Description = "Test aid (#10568): advance poses in whole steps of 1/fps seconds, for a retro rate; 0 is smooth.")]
    public string Step(long fps)
    {
        if (fps < 0) return "fps must be 0 or more";
        test.StepsPerSecond = (int)fps;
        return test.Readout();
    }

    [DebugCommand("craft.creature.plant", Description = "Test aid (#10568): plant feet on uneven ground with two-bone IK (on) or leave the clips' flat-ground feet (off).")]
    public string Plant(string mode)
    {
        if (mode is not ("on" or "off")) return "mode must be on or off";
        test.PlantFeet = mode == "on";
        return test.Readout();
    }

    [DebugCommand("craft.creature.clear", Description = "Test aid (#10568): remove every placed creature.")]
    public string Clear() => test.Clear();

    [DebugCommand("craft.creature.read", Description = "Test aid (#10568): each creature's kind, clip, place, pitch, feet and driver state.")]
    public string Read() => test.Readout();

    [DebugCommand("craft.creature.clips", Description = "Test aid (#10568): a kind's mesh facts and the clips the Engine assembled for it.")]
    public string Clips(string kind) => test.Clips(kind);
}
