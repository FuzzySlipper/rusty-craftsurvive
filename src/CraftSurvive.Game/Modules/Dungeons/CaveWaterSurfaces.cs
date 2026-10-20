using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A cave's still water drawn (#10094; owner, 2026-10-20: lakes and pools for mood and variety): each lake's or pool's
/// surface as one flat mesh at its water's level, on the cave field's half-metre columns. The space publishes the meshes
/// with its growths; this owns the material and its textures. Mist sumps are not water and are not drawn here.
/// <para>
/// Where the water meets the rock (#10178), the surface runs on into it (<see cref="CaveWaterShore"/>), so the rock's
/// own face draws the shore. The surface has an underside, seen from under the water.
/// </para>
/// <para>
/// Its look is still cave water, not the open world's (#10178): clear over a shallow bed and darkening with depth, so
/// the bed shows through it near the shore; dark and glossy, so it reads by what it reflects - the lantern, the light
/// of a glow - rather than by a colour of its own; no shore foam or rolling bands, which nothing stirs here; and only a
/// faint, slow ripple.
/// </para>
/// </summary>
internal sealed class CaveWaterSurfaces : IDisposable
{
    /// <summary>The side of each surface square: the cave field's step.</summary>
    private const float Column = CaveWaterShore.Column;

    // The look (#10178). Linear colours: the water's own colour is faint and dark - most of what shows is the bed through
    // it and the light it reflects; how clear it is over a shallow bed (the alpha), and how many metres of water along
    // the view darken it to the deep colour and make it opaque.
    private static readonly Color Clear = new(1f, 1f, 1f, 0.35f);
    private static readonly Color ShallowColour = new(0.12f, 0.16f, 0.17f, 1f);
    private static readonly Color DeepColour = new(0.008f, 0.02f, 0.025f, 1f);
    private const float DepthScale = 1.5f;

    /// <summary>Glossy still water (the Engine keeps a floor of about 0.05).</summary>
    private const float Roughness = 0.08f;

    /// <summary>Still water has no shore foam: no shoreline band, and the highest foam threshold.</summary>
    private const float NoShoreline = 0f, NoFoam = 1f;

    /// <summary>
    /// The water fades in over its first this many metres of depth (the corner's alpha), so where the bed or a lip rises to
    /// the surface the water thins to nothing rather than ending in an edge; the cave's shape is read down this finely.
    /// </summary>
    private const float FullDepth = 0.6f, DepthProbe = 0.05f;

    /// <summary>The ripple map's strength, its slow drift (repeats a second) and how many metres one repeat covers.</summary>
    private const float RippleStrength = 0.06f, RippleScale = 4f;
    private static readonly Vector2 RippleScroll = new(-0.003f, 0.005f);

    private readonly IEngineContext engine;
    private RenderResource? foam, ripples, flatNormal;
    private Material? material, luminous;

    /// <summary>How strongly a luminous pool's water glows (#10129), in its glow's colour.</summary>
    private const float LuminousGlow = 0.35f;

    internal CaveWaterSurfaces(IEngineContext engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>One surface mesh for each lake and pool, in the session; <paramref name="shape"/> is the cave's (open above 0).</summary>
    internal IEnumerable<MeshResourceCreateRequest> Requests(IReadOnlyList<WaterZone> zones, Func<Vector3, float>? shape)
    {
        foreach (WaterZone zone in zones.Where(z => z.Kind is WaterZoneKind.Lake or WaterZoneKind.Pool && z.SurfaceColumns.Count > 0))
        {
            yield return Surface(zone, CaveWaterShore.Columns(zone, shape), shape, zone.Luminous ? Luminous() : Material());
        }
    }

    private Material Material() => material ??= engine.Graphics.CreateMaterial(Request(Vector3.Zero, 0f));

    /// <summary>The water look glowing from within, for a luminous pool (#10129): the water is the source the eye traces its light to.</summary>
    private Material Luminous() => luminous ??= engine.Graphics.CreateMaterial(Request(CaveEcology.PoolGlow, LuminousGlow));

    private MaterialRequest Request(Vector3 glow, float glowIntensity)
    {
        foam ??= TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterFoamContentPath);
        ripples ??= TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterRipplesContentPath);
        flatNormal ??= TerrainAtlasCatalog.OpenTexture(engine, TerrainAtlasCatalog.WaterFlatNormalContentPath);
        Color white = new(1f, 1f, 1f, 1f);
        return new MaterialRequest(Clear, default(RenderResourceReference), Roughness, white, glow, glowIntensity, false)
            with
            {
                AlphaMode = MaterialAlphaMode.Blend,
                // A flat normal map of its own, so the Engine scales the ripples by RippleStrength.
                NormalMap = flatNormal,
                NormalScale = RippleStrength,
                Water = new MaterialWater(ShallowColour, DeepColour, DepthScale, NoShoreline, NoFoam, Vector2.Zero, Vector2.Zero, RippleScroll, RippleScale, foam, ripples),
            };
    }

    /// <summary>The surface as one grid over the columns, its corners shared and faded by the water's depth under them, with an underside facing down.</summary>
    private static MeshResourceCreateRequest Surface(WaterZone zone, HashSet<(int X, int Z)> columns, Func<Vector3, float>? shape, Material material)
    {
        List<Vector3> positions = [];
        List<Vector3> normals = [];
        List<Color> colours = [];
        List<uint> indices = [];
        Dictionary<(int X, int Z, bool Down), uint> corners = [];
        uint Corner(int x, int z, bool down)
        {
            if (corners.TryGetValue((x, z, down), out uint at))
            {
                return at;
            }

            at = (uint)positions.Count;
            corners[(x, z, down)] = at;
            positions.Add(DungeonSpace.InSession(new Vector3(x * Column, zone.Surface, z * Column)));
            normals.Add(down ? -Vector3.UnitY : Vector3.UnitY);
            colours.Add(new Color(1f, 1f, 1f, Coverage(new Vector3(x * Column, zone.Surface, z * Column), shape)));
            return at;
        }

        foreach ((int x, int z) in columns.OrderBy(c => c.Z).ThenBy(c => c.X))
        {
            uint a = Corner(x, z, false), b = Corner(x + 1, z, false), c = Corner(x + 1, z + 1, false), d = Corner(x, z + 1, false);
            indices.AddRange([a, c, b, a, d, c]);
            a = Corner(x, z, true);
            b = Corner(x + 1, z, true);
            c = Corner(x + 1, z + 1, true);
            d = Corner(x, z + 1, true);
            indices.AddRange([a, b, c, a, c, d]);
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(),
            positions.Select(p => new Vector2(p.X, p.Z)).ToArray(), colours.ToArray(), indices.ToArray(),
            new[] { new MeshGroup(0, 0, (uint)indices.Count) }, new[] { new MeshMaterialBinding(0, material) });
    }

    /// <summary>How much of the water shows at a point of its surface (layout metres): eased in over its depth there.</summary>
    private static float Coverage(Vector3 surface, Func<Vector3, float>? shape)
    {
        if (shape is null)
        {
            return 1f;
        }

        float depth = 0f;
        while (depth < FullDepth && shape(surface - (Vector3.UnitY * (depth + DepthProbe))) > 0f)
        {
            depth += DepthProbe;
        }

        float t = depth / FullDepth;
        return t * t * (3f - (2f * t));
    }

    public void Dispose()
    {
        material?.Dispose();
        luminous?.Dispose();
        material = luminous = null;
        foam?.Dispose();
        ripples?.Dispose();
        flatNormal?.Dispose();
        foam = ripples = flatNormal = null;
    }
}
