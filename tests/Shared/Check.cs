namespace CraftSurvive.Game.Tests;

/// <summary>
/// The one harness every managed lane uses. A failed check is recorded, not thrown, so a lane
/// reports every failure in the run; <see cref="Section"/> keeps an unexpected exception inside
/// one area from ending the others, and <see cref="Finish"/> prints the result and sets the exit
/// code. A lane that crashes anyway still prints what it had collected.
/// </summary>
internal static class Check
{
    private static readonly List<string> Failures = [];
    private static int passed;
    private static string section = "lane";

    static Check() => AppDomain.CurrentDomain.UnhandledException += (_, _) => Report();

    internal static void That(bool condition, string message)
    {
        if (condition)
        {
            passed++;
        }
        else
        {
            Failures.Add($"[{section}] {message}");
        }
    }

    internal static void Equal<T>(T expected, T actual, string message) =>
        That(EqualityComparer<T>.Default.Equals(expected, actual), $"{message} (expected {expected}, was {actual})");

    /// <summary>Passes when the action throws <typeparamref name="TException"/>; any other outcome fails.</summary>
    internal static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
            That(false, $"{message} (nothing was thrown)");
        }
        catch (TException)
        {
            That(true, message);
        }
    }

    /// <summary>Runs one area of checks; an exception it did not expect is that area's failure.</summary>
    internal static void Section(string name, Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        string previous = section;
        section = name;
        try
        {
            body();
        }
        catch (Exception unexpected)
        {
            Failures.Add($"[{name}] threw {unexpected.GetType().Name}: {unexpected.Message}");
        }
        finally
        {
            section = previous;
        }
    }

    /// <summary>Prints the lane's result and returns its exit code.</summary>
    internal static int Finish(string lane)
    {
        if (Report())
        {
            return 1;
        }

        Console.WriteLine($"{lane}: all {passed} checks passed.");
        return 0;
    }

    private static bool Report()
    {
        if (Failures.Count == 0)
        {
            return false;
        }

        Console.Error.WriteLine($"{Failures.Count} check(s) failed ({passed} passed):");
        foreach (string failure in Failures)
        {
            Console.Error.WriteLine($"  - {failure}");
        }

        return true;
    }
}
