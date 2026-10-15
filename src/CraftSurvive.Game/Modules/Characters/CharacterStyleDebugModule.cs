using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Characters;

/// <summary>Live-debug adapters over the character style test (#10014); it owns no state of its own.</summary>
public sealed class CharacterStyleDebugModule : IDebugCommandModule
{
    private readonly Func<CharacterStyleTest> testSource;
    private CharacterStyleTest test => testSource();

    internal CharacterStyleDebugModule(Func<CharacterStyleTest> test)
    {
        this.testSource = test ?? throw new ArgumentNullException(nameof(test));
    }

    [DebugCommand("craft.style.place", Description = "Test aid (#10014, #10028): place a character kind (craft.style.kinds) at world x z facing yaw degrees (0 north, 90 east), at the kind's scale.")]
    public string Place(string variant, double x, double z, double yaw) => test.Place(variant, x, z, yaw, 0);

    [DebugCommand("craft.style.kinds", Description = "Test aid (#10014, #10028): the placeable character kinds: the travellers and the framework-check cast.")]
    public string Kinds() => string.Join(';', test.Kinds.Values.Select(k => $"{k.Name}:scale={k.Scale:F2}:stoop={k.StoopDegrees:F0}"));

    [DebugCommand("craft.style.placescaled", Description = "Test aid (#10014): as craft.style.place, at a given uniform scale (1 = the fitted mesh, about 2 m).")]
    public string PlaceScaled(string variant, double x, double z, double yaw, double scale) => test.Place(variant, x, z, yaw, (float)scale);

    [DebugCommand("craft.style.near", Description = "Test aid (#10014, #10028): place a character kind metres east and south of the player, facing yaw degrees, at a uniform scale (0 = the kind's).")]
    public string Near(string variant, double east, double south, double yaw, double scale) => test.PlaceNear(variant, east, south, yaw, (float)scale);

    [DebugCommand("craft.style.mode", Description = "Test aid (#10014): set a traveller's mode (idle|walk|talk) by id, or every traveller's with id -1.")]
    public string Mode(long id, string mode) => test.SetMode((int)id, mode);

    [DebugCommand("craft.style.face", Description = "Test aid (#10014): pin a traveller's face (neutral|blink|talk|smile) or let it move (auto), at a texture size (32|64|128); id -1 for all.")]
    public string Face(long id, string face, long size) => test.SetFace((int)id, face, (int)size);

    [DebugCommand("craft.style.body", Description = "Test aid (#10014): a traveller's body texture density in texels per metre (32, the crunch rule and the GLB's own, or 64 on the same UVs); id -1 for all.")]
    public string Body(long id, long density) => test.SetBody((int)id, (int)density);

    [DebugCommand("craft.style.driver", Description = "Test aid (#10028): new characters play through an Engine graph (graph) or direct playback (direct); only one graph-driven character animates on pair 0a5dfab7b318 (rusty-engine #10019).")]
    public string Driver(string driver)
    {
        if (driver is not ("graph" or "direct")) return "driver must be graph or direct";
        test.DirectPlayback = driver == "direct";
        return $"driver={driver}";
    }

    [DebugCommand("craft.style.clear", Description = "Test aid (#10014): remove every traveller.")]
    public string Clear() => test.Clear();

    [DebugCommand("craft.style.read", Description = "Test aid (#10014): each traveller's variant, mode, face, pose and controller state.")]
    public string Read() => test.Readout();

    [DebugCommand("craft.style.clips", Description = "Test aid (#10014): a variant's mesh facts and the clips the Engine assembled for it.")]
    public string Clips(string variant) => test.Clips(variant);
}
