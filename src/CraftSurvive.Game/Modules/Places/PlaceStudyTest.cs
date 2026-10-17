using System.Globalization;
using System.Numerics;
using System.Text.Json;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>
/// The place look study (#9992), a test aid rather than shipped content: candidate textured meshes for an
/// overworld place, stood in the world by live debug so their look can be compared at play distance before
/// the per-voxel place shapes are replaced. The variants are named in the study manifest; each is a prop
/// mesh with its texture sampled nearest up close and mipmapped at distance (the crunch rule). Placing a
/// variant again moves it; each variant's mesh and appearance load on first use and live until disposal.
/// </summary>
internal sealed class PlaceStudyTest : IDisposable
{
    private const string Manifest = "models/place-study/study.json";
    /// <summary>Weathered stone: matt, and nothing about it moves in the wind.</summary>
    private const float Roughness = 0.92f;

    private readonly IEngineContext engine;
    private readonly ProductContent content;
    private readonly WorldFrame frame;
    private readonly Func<double, double, float> groundAt;
    private readonly Func<Vector3> playerWorld;
    private readonly PropMaterials materials;
    private readonly Dictionary<string, Variant> variants = [];
    private readonly Dictionary<string, (MeshResource Mesh, Appearance Appearance)> loaded = [];
    private readonly Dictionary<string, Placement> placed = [];

    internal PlaceStudyTest(IEngineContext engine, ProductContent content, WorldFrame frame, Func<double, double, float> groundAt, Func<Vector3> playerWorld,
        SurfaceMoss? moss = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.content = content ?? throw new ArgumentNullException(nameof(content));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.groundAt = groundAt ?? throw new ArgumentNullException(nameof(groundAt));
        this.playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        materials = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, 0, 0, SurfaceMoss.FieldStoneClumpMetres, SurfaceMoss.SmallPropTexelsPerMetre), moss: moss);
        using JsonDocument manifest = JsonDocument.Parse(content.ReadText(Manifest));
        foreach (JsonElement v in manifest.RootElement.GetProperty("variants").EnumerateArray())
        {
            string name = v.GetProperty("name").GetString()!;
            string? texture = v.TryGetProperty("texture", out JsonElement t) ? t.GetString() : null;
            variants[name] = new(name, v.GetProperty("model").GetString()!, texture, v.GetProperty("sink").GetSingle());
        }
    }

    /// <summary>
    /// A study variant: its prop mesh, an optional texture in place of the mesh's own (texture treatments
    /// of one mesh), and how far its base sinks below the ground it stands on.
    /// </summary>
    private sealed record Variant(string Name, string Model, string? Texture, float Sink);

    private sealed record Placement(int Index, Variant Variant, double X, double Z, double YawDegrees);

    internal IEnumerable<string> Names => variants.Keys;

    /// <summary>Stands a variant at a world column, its broad face toward yawDegrees (0 north, 90 east).</summary>
    internal string Place(string name, double x, double z, double yawDegrees)
    {
        if (!variants.TryGetValue(name, out Variant? variant)) return $"unknown variant '{name}'; one of {string.Join('|', variants.Keys)}";
        if (!loaded.ContainsKey(name))
        {
            MeshResource mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, variant.Model,
                (role, texture) => materials.For(role, variant.Texture ?? texture ?? throw new InvalidOperationException($"Place study '{name}' part '{role}' names no texture."))));
            loaded[name] = (mesh, engine.Graphics.CreateMeshAppearance(mesh));
        }

        int index = placed.TryGetValue(name, out Placement? earlier) ? earlier.Index : variants.Keys.ToList().IndexOf(name);
        placed[name] = new(index, variant, x, z, yawDegrees);
        return Readout();
    }

    /// <summary>Stands a variant metres east and south of the player.</summary>
    internal string PlaceNear(string name, double east, double south, double yawDegrees)
    {
        Vector3 player = playerWorld();
        return Place(name, player.X + east, player.Z + south, yawDegrees);
    }

    /// <summary>Takes every variant out of the world (their meshes stay loaded until disposal).</summary>
    internal string Clear()
    {
        placed.Clear();
        return Readout();
    }

    internal IEnumerable<AppearanceFact> Facts => placed.Values.Select(Fact);

    private AppearanceFact Fact(Placement p)
    {
        float y = groundAt(p.X, p.Z) - p.Variant.Sink;
        // The mesh's broad face looks along +Z; yaw 0 turns it north (-Z) and 90 east (+X).
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)((180 - p.YawDegrees) * Math.PI / 180));
        return new AppearanceFact(ProductIds.PlaceStudyBase + (ulong)p.Index, false, 0,
            new Transform(frame.ToLocal(p.X, y, p.Z), turn, Vector3.One), loaded[p.Variant.Name].Appearance,
            Visible: true, RenderLayer.Scene, ShadowCasting.Cast);
    }

    internal string Readout() => placed.Count == 0
        ? "placed=none"
        : string.Join(';', placed.Values.Select(p => string.Create(CultureInfo.InvariantCulture,
            $"{p.Variant.Name}@{p.X:F1},{p.Z:F1} yaw={p.YawDegrees:F0} ground={groundAt(p.X, p.Z):F2} sink={p.Variant.Sink:F2}")));

    /// <summary>Disposes the meshes; call only after a snapshot that no longer names them.</summary>
    public void Dispose()
    {
        placed.Clear();
        foreach ((MeshResource mesh, Appearance appearance) in loaded.Values)
        {
            appearance.Dispose();
            mesh.Dispose();
        }

        loaded.Clear();
        materials.Dispose();
    }
}
