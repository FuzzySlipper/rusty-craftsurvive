using System.Text;
using CraftSurvive.Game.Modules.World;

namespace CraftSurvive.Game.Modules.Towns;

/// <summary>The conditions a service's lines and choices answer to, read from the world at the visit.</summary>
internal readonly record struct TownConditions(bool Wet, bool Night, bool Port)
{
    internal bool Hold(IReadOnlyCollection<string> when)
    {
        foreach (string tag in when)
        {
            bool holds = tag switch
            {
                "wet" => Wet,
                "dry" => !Wet,
                "night" => Night,
                "day" => !Night,
                "port" => Port,
                "town" => !Port,
                _ => false,
            };
            if (!holds) return false;
        }

        return true;
    }
}

/// <summary>What a service choice asks the expedition's owners to do; talk asks nothing.</summary>
internal readonly record struct TownServiceRequest(string Service, ServiceChoice Choice);

/// <summary>What the expedition's owners did: whether it happened, and its outcome in real game terms.</summary>
internal readonly record struct TownServiceOutcome(bool Done, string Text);

/// <summary>
/// A visit's conversations and services (#9895), from the town content: the welcome, the choices on
/// offer under the visit's conditions, and what each one said and did. A choice that acts (a meal, a
/// rest, supplies) is applied by the product through the existing survival and inventory owners and
/// is then spent for the visit. Talking and browsing do nothing to the expedition.
/// </summary>
internal sealed class TownServices(TownContent content, TownSite site)
{
    internal const string Talk = "talk";
    private readonly HashSet<(string Service, string Choice)> spent = [];
    private string reply = "", outcome = "";

    internal void Reset()
    {
        spent.Clear();
        Greet();
    }

    internal void Greet() => (reply, outcome) = ("", "");

    /// <summary>A choice the service offers now, or none.</summary>
    internal ServiceChoice? Offered(string service, string key, TownConditions conditions) =>
        content.Services.TryGetValue(service, out ServiceDefinition? definition)
            ? definition.Choices.FirstOrDefault(choice => choice.Key == key && conditions.Hold(choice.When))
            : null;

    /// <summary>Why a choice cannot be taken now (spent this visit, or the product's own reason), or none.</summary>
    internal string Unavailable(string service, ServiceChoice choice, Func<ServiceChoice, string?> blocked) =>
        spent.Contains((service, choice.Key)) ? choice.Spent : blocked(choice) ?? "";

    /// <summary>Records a choice's reply and, when it acted, its outcome; an action that happened is spent.</summary>
    internal void Record(string service, ServiceChoice choice, TownConditions conditions, TownServiceOutcome? acted)
    {
        reply = Fill(Pick(choice.Reply, conditions), service, choice);
        outcome = acted?.Text ?? "";
        if (acted is { Done: true }) spent.Add((service, choice.Key));
    }

    internal TownServiceUiFacts Describe(string service, TownConditions conditions, string portrait, Func<ServiceChoice, string?> blocked)
    {
        ServiceDefinition definition = content.Services[service];
        TownPerson keeper = site.Keepers[service];
        StringBuilder choices = new();
        foreach (ServiceChoice choice in definition.Choices.Where(choice => conditions.Hold(choice.When)))
        {
            string reason = Unavailable(service, choice, blocked);
            if (choices.Length > 0) choices.Append(';');
            choices.Append(choice.Key).Append('|').Append(Clean(Fill(Pick(choice.Label, conditions), service, choice))).Append('|')
                .Append(Clean(Fill(choice.Detail, service, choice))).Append('|').Append(reason.Length == 0 ? 1 : 0).Append('|')
                .Append(Clean(reason)).Append('|').Append(choice.Action == Talk ? Talk : "service");
        }

        string speech = reply.Length > 0 ? reply : Fill(Pick(definition.Welcome, conditions), service, null);
        return new(keeper.Name, definition.Role, speech, portrait, choices.ToString(), outcome, content.Hint);
    }

    /// <summary>The first line whose conditions hold; the last line is the fallback.</summary>
    internal static string Pick(IReadOnlyList<TownLine> lines, TownConditions conditions) =>
        (lines.FirstOrDefault(line => conditions.Hold(line.When)) ?? lines[^1]).Text;

    private string Fill(string text, string service, ServiceChoice? choice) => text
        .Replace("{town}", site.Name, StringComparison.Ordinal)
        .Replace("{host}", site.Keepers[service].Name, StringComparison.Ordinal)
        .Replace("{service}", site.ServiceNames[service], StringComparison.Ordinal)
        .Replace("{allotment}", choice?.Allotment.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal);

    /// <summary>The UI rows use '|' and ';' as separators; authored text never carries them through.</summary>
    private static string Clean(string text) => text.Replace('|', '/').Replace(';', ',');
}
