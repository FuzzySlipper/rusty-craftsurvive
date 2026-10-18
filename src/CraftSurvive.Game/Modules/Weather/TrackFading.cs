namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// How a footprint fades (#10068): it settles, grows faint and goes as fresh snow falls into it (all the snow that falls
/// on its cell since it was made, including what the depth's cap blew off), or with age, and goes at once once the snow
/// under it has melted out.
/// </summary>
internal static class TrackFading
{
    /// <summary>The stages a print shows: fresh, settled, faint; <see cref="Gone"/> is none.</summary>
    internal const int Stages = 3, Gone = Stages;
    /// <summary>A print settles once this much fresh snow has fallen into it, fades at this much, and is gone at this much (metres).</summary>
    internal const double SettledFallen = 0.01, FaintFallen = 0.03, GoneFallen = 0.06;
    /// <summary>With no fresh snow, a print settles, fades and goes over these many game hours.</summary>
    internal const double SettledHours = 8, FaintHours = 20, GoneHours = 36;
    /// <summary>A print goes once the snow under it is shallower than this (metres): it has melted out.</summary>
    internal const double MeltedBelow = 0.01;

    /// <summary>The stage of a print with <paramref name="fallenSince"/> metres fallen into it, <paramref name="ageHours"/> old, on snow <paramref name="depth"/> deep.</summary>
    internal static int StageOf(double fallenSince, double ageHours, double depth)
    {
        if (depth < MeltedBelow || ageHours < 0) return Gone;
        int byFall = fallenSince >= GoneFallen ? 3 : fallenSince >= FaintFallen ? 2 : fallenSince >= SettledFallen ? 1 : 0;
        int byAge = ageHours >= GoneHours ? 3 : ageHours >= FaintHours ? 2 : ageHours >= SettledHours ? 1 : 0;
        return Math.Max(byFall, byAge);
    }
}
