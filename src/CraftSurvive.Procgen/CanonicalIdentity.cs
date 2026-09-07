using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CraftSurvive.Procgen;

/// <summary>Stable identity deliberately avoids serializer implementation and collection insertion order.</summary>
public static class CanonicalIdentity
{
    public static string Hash(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var writer = new StringBuilder(1024);
        Add(writer, "id", candidate.Id);
        Add(writer, "seed", candidate.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(writer, "intent", candidate.IntentId);
        foreach (var node in candidate.Graph.Nodes
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ThenBy(node => node.Kind)
            .ThenBy(node => node.Label, StringComparer.Ordinal)
            .ThenBy(node => node.GrantsItem, StringComparer.Ordinal)
            .ThenBy(node => string.Join("\u001f", node.Tags.OrderBy(tag => tag, StringComparer.Ordinal)), StringComparer.Ordinal))
        {
            Add(writer, "node", node.Id, node.Kind.ToString(), node.Label, node.GrantsItem ?? string.Empty);
            AddMany(writer, "node-tag", node.Tags);
        }
        foreach (var edge in candidate.Graph.Edges
            .OrderBy(edge => edge.Id, StringComparer.Ordinal)
            .ThenBy(edge => edge.From, StringComparer.Ordinal)
            .ThenBy(edge => edge.To, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind)
            .ThenBy(edge => edge.Traversal)
            .ThenBy(edge => edge.RequiredItem, StringComparer.Ordinal)
            .ThenBy(edge => string.Join("\u001f", edge.Tags.OrderBy(tag => tag, StringComparer.Ordinal)), StringComparer.Ordinal))
        {
            Add(writer, "edge", edge.Id, edge.From, edge.To, edge.Kind.ToString(), edge.Traversal.ToString(), edge.RequiredItem ?? string.Empty);
            AddMany(writer, "edge-tag", edge.Tags);
        }
        foreach (var step in candidate.Provenance.OrderBy(step => step.Step).ThenBy(step => step.Operation, StringComparer.Ordinal).ThenBy(step => step.Seed).ThenBy(step => step.Summary, StringComparer.Ordinal))
            Add(writer, "provenance", step.Step.ToString(System.Globalization.CultureInfo.InvariantCulture), step.Operation, step.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture), step.Summary);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(writer.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void AddMany(StringBuilder writer, string name, IEnumerable<string> values)
    {
        foreach (var value in values.OrderBy(value => value, StringComparer.Ordinal)) Add(writer, name, value);
    }

    private static void Add(StringBuilder writer, params string[] parts)
    {
        foreach (var part in parts)
        {
            writer.Append('"');
            writer.Append(JsonEncodedText.Encode(part).ToString());
            writer.Append('"');
            writer.Append('|');
        }
        writer.Append('\n');
    }
}
