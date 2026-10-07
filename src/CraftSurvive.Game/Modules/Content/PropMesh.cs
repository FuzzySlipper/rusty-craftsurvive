using System.Numerics;
using System.Text.Json;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Content;

/// <summary>
/// A generated prop's mesh as scripts/stylise-mesh.py writes it: flat-coloured triangles in metres,
/// Y up, grouped into parts by role ("bark", "leaves" or "solid"), each vertex's colour alpha its
/// wind weight. The caller chooses the material for each role.
/// </summary>
internal static class PropMesh
{
    internal const string Suffix = ".prop-mesh.json";

    internal static MeshResourceCreateRequest Read(ProductContent content, string path, Func<string, Material> materialFor)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(materialFor);
        using JsonDocument document = JsonDocument.Parse(content.ReadText(path));
        List<Vector3> positions = [], normals = [];
        List<Color> colors = [];
        List<uint> indices = [];
        List<MeshGroup> groups = [];
        List<MeshMaterialBinding> bindings = [];
        foreach (JsonElement part in document.RootElement.GetProperty("parts").EnumerateArray())
        {
            uint first = (uint)positions.Count;
            uint start = (uint)indices.Count;
            float[] p = Floats(part, "positions"), n = Floats(part, "normals"), c = Floats(part, "colors");
            for (int i = 0; i < p.Length / 3; i++)
            {
                positions.Add(new(p[3 * i], p[(3 * i) + 1], p[(3 * i) + 2]));
                normals.Add(new(n[3 * i], n[(3 * i) + 1], n[(3 * i) + 2]));
                colors.Add(new(c[4 * i], c[(4 * i) + 1], c[(4 * i) + 2], c[(4 * i) + 3]));
            }

            foreach (JsonElement index in part.GetProperty("indices").EnumerateArray())
            {
                indices.Add(first + index.GetUInt32());
            }

            uint slot = (uint)bindings.Count;
            groups.Add(new(slot, start, (uint)indices.Count - start));
            bindings.Add(new(slot, materialFor(part.GetProperty("role").GetString()!)));
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), new Vector2[positions.Count],
            colors.ToArray(), indices.ToArray(), groups.ToArray(), bindings.ToArray());
    }

    private static float[] Floats(JsonElement part, string name) =>
        [.. part.GetProperty(name).EnumerateArray().Select(value => value.GetSingle())];
}
