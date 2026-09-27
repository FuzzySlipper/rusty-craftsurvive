namespace CraftSurvive.Game.Modules.Studies;

/// <summary>
/// The optional study scenes - the level-generation workbench, the microvoxel
/// shrine and the ghost plate - are development instruments from the slices that
/// built them, not part of the survival world. They are constructed only when
/// asked for, so the ordinary runtime scene carries the game and nothing else,
/// and a playtest capture is not full of laboratory props.
///
/// Set <c>CRAFTSURVIVE_STUDIES=1</c> to bring them back for the task that is
/// working on one of them.
/// </summary>
internal static class ProductStudies
{
    internal const string EnvironmentVariable = "CRAFTSURVIVE_STUDIES";

    private const string EnabledDigit = "1";
    private const string EnabledWord = "true";

    internal static bool Enabled { get; } = IsEnabled(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>What a debug command answers when it needs a study that was not constructed.</summary>
    internal static string DisabledMessage =>
        $"This command belongs to an optional study scene, which is not constructed in this run. " +
        $"Set {EnvironmentVariable}=1 to enable the workbench, microvoxel and ghost-plate studies.";

    private static bool IsEnabled(string? value) =>
        string.Equals(value, EnabledDigit, StringComparison.Ordinal)
        || string.Equals(value, EnabledWord, StringComparison.OrdinalIgnoreCase);
}
