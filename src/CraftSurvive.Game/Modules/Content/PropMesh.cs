using System.Numerics;
using System.Text.Json;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Content;

/// <summary>
/// A generated prop's mesh as scripts/stylise-mesh.py or scripts/generate-tree.py writes it:
/// triangles in metres, Y up, grouped into parts by role ("bark", "leaves" or "solid"), each
/// vertex's colour alpha its wind weight. A part may carry UVs, and the prop may name a texture per
/// role ("textures": {role: content path}); untextured parts are coloured by their vertices alone.
/// The caller chooses the material for each role and its texture (null when it has none).
/// </summary>
internal static class PropMesh
{
    internal const string Suffix = ".prop-mesh.json";

    internal static MeshResourceCreateRequest Read(ProductContent content, string path, Func<string, string?, Material> materialFor)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(materialFor);
        using JsonDocument document = JsonDocument.Parse(content.ReadText(path));
        Dictionary<string, string> textures = [];
        if (document.RootElement.TryGetProperty("textures", out JsonElement named))
        {
            foreach (JsonProperty texture in named.EnumerateObject())
            {
                textures[texture.Name] = texture.Value.GetString()!;
            }
        }

        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<Color> colors = [];
        List<uint> indices = [];
        List<MeshGroup> groups = [];
        List<MeshMaterialBinding> bindings = [];
        foreach (JsonElement part in document.RootElement.GetProperty("parts").EnumerateArray())
        {
            uint first = (uint)positions.Count;
            uint start = (uint)indices.Count;
            float[] p = Floats(part, "positions"), n = Floats(part, "normals"), c = Floats(part, "colors");
            float[] uv = part.TryGetProperty("uvs", out _) ? Floats(part, "uvs") : [];
            for (int i = 0; i < p.Length / 3; i++)
            {
                positions.Add(new(p[3 * i], p[(3 * i) + 1], p[(3 * i) + 2]));
                normals.Add(new(n[3 * i], n[(3 * i) + 1], n[(3 * i) + 2]));
                colors.Add(new(c[4 * i], c[(4 * i) + 1], c[(4 * i) + 2], c[(4 * i) + 3]));
                uvs.Add(uv.Length == 0 ? Vector2.Zero : new(uv[2 * i], uv[(2 * i) + 1]));
            }

            foreach (JsonElement index in part.GetProperty("indices").EnumerateArray())
            {
                indices.Add(first + index.GetUInt32());
            }

            uint slot = (uint)bindings.Count;
            groups.Add(new(slot, start, (uint)indices.Count - start));
            string role = part.GetProperty("role").GetString()!;
            bindings.Add(new(slot, materialFor(role, textures.GetValueOrDefault(role))));
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(),
            colors.ToArray(), indices.ToArray(), groups.ToArray(), bindings.ToArray());
    }

    /// <summary>The bounds of a prop mesh's parts of one role, in its own metres, or null when it has none.</summary>
    internal static (Vector3 Min, Vector3 Max)? RoleBounds(ProductContent content, string path, string role)
    {
        ArgumentNullException.ThrowIfNull(content);
        using JsonDocument document = JsonDocument.Parse(content.ReadText(path));
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (JsonElement part in document.RootElement.GetProperty("parts").EnumerateArray())
        {
            if (part.GetProperty("role").GetString() != role) continue;
            float[] p = Floats(part, "positions");
            for (int i = 0; i + 2 < p.Length; i += 3)
            {
                Vector3 at = new(p[i], p[i + 1], p[i + 2]);
                min = Vector3.Min(min, at);
                max = Vector3.Max(max, at);
            }
        }

        return min.X <= max.X ? (min, max) : null;
    }

    private static float[] Floats(JsonElement part, string name) =>
        [.. part.GetProperty(name).EnumerateArray().Select(value => value.GetSingle())];
}
