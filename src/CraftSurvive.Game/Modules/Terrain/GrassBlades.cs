using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Grass as blades (#10035): a clump is geometry, many thin tapered blades from one root, rather than crossed
/// cards showing painted blades. Each clump is seeded: how many blades, how tightly they gather, how tall and
/// how far they lean and curve, each blade's own width, twist and colour, a few dry tips. Blades are solid, so
/// nothing is alpha-cut, and their vertex colour carries the shade (darker at the root) and the wind weight in
/// its alpha, rising from root to tip. A lighter level of detail has a third of the blades, wider, in fewer
/// segments, for the middle distance; beyond that the painted cards stay (<see cref="TerrainScatter"/>).
/// </summary>
internal static class GrassBlades
{
    /// <summary>A clump's shape: the range its blades are drawn from.</summary>
    internal sealed record Style(int Blades, float Spread, float HeightMin, float HeightMax, float LeanMin, float LeanMax, float Curve,
        float Width, float DryShare);

    /// <summary>A clump's colours, linear: root, body and tip, a dry tip, and how much a blade's hue may wander.</summary>
    internal sealed record Palette(Vector3 Root, Vector3 Body, Vector3 Tip, Vector3 Dry, float Wander);

    /// <summary>
    /// Meadow clumps: tight tufts, loose spreads, a few tall seed stems among them. A clump covers its ground with blades
    /// rather than more clumps covering it: copies are bounded by the renderer's buffers, blades only by triangles.
    /// </summary>
    internal static readonly Style[] Meadow =
    [
        new(Blades: 56, Spread: 0.26f, HeightMin: 0.3f, HeightMax: 0.55f, LeanMin: 0.15f, LeanMax: 0.5f, Curve: 0.4f, Width: 0.018f, DryShare: 0.06f),
        new(Blades: 60, Spread: 0.42f, HeightMin: 0.22f, HeightMax: 0.45f, LeanMin: 0.3f, LeanMax: 0.75f, Curve: 0.55f, Width: 0.02f, DryShare: 0.04f),
        new(Blades: 42, Spread: 0.3f, HeightMin: 0.45f, HeightMax: 0.8f, LeanMin: 0.1f, LeanMax: 0.35f, Curve: 0.3f, Width: 0.016f, DryShare: 0.2f),
        new(Blades: 70, Spread: 0.46f, HeightMin: 0.15f, HeightMax: 0.32f, LeanMin: 0.35f, LeanMax: 0.85f, Curve: 0.6f, Width: 0.02f, DryShare: 0.03f),
    ];

    // The meadow grass's colours, as the atlas's middle and light blades read lit. Vertex colours are squared on the way
    // to the screen, so each is the square root of the linear colour wanted.
    internal static readonly Palette MeadowPalette = new(new(0.173f, 0.245f, 0.110f), new(0.283f, 0.424f, 0.173f), new(0.469f, 0.583f, 0.224f),
        new(0.648f, 0.616f, 0.316f), 0.12f);

    // Shade grass under the canopy: cooler and darker, and its tips never dry.
    internal static readonly Palette ShadePalette = new(new(0.134f, 0.200f, 0.122f), new(0.224f, 0.346f, 0.173f), new(0.346f, 0.490f, 0.224f),
        new(0.346f, 0.490f, 0.224f), 0.1f);

    /// <summary>Near blades bend in this many segments; the middle distance's in fewer, a third as many and wider.</summary>
    private const int NearSegments = 3, LiteSegments = 2, LiteShare = 3;
    private const float LiteWidening = 1.7f;

    /// <summary>How far a blade's lighting normal leans toward the sky: blades then light as a soft mass, not as slivers.</summary>
    private const float NormalUp = 0.35f;

    /// <summary>A clump's mesh: its style's blades, seeded, drawn with one material from their vertex colours.</summary>
    internal static MeshResourceCreateRequest Clump(Material material, Style style, Palette palette, int seed, bool lite)
    {
        Random random = new(seed);
        int blades = lite ? Math.Max(1, style.Blades / LiteShare) : style.Blades;
        int segments = lite ? LiteSegments : NearSegments;
        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<Color> colors = [];
        List<uint> indices = [];
        for (int blade = 0; blade < blades; blade++)
        {
            // Roots gather toward the clump's middle; a blade leans out the way it stands from it.
            float azimuth = (float)(random.NextDouble() * Math.Tau);
            float radius = style.Spread * MathF.Pow((float)random.NextDouble(), 0.7f);
            Vector3 root = new(MathF.Cos(azimuth) * radius, 0, MathF.Sin(azimuth) * radius);
            float leanAzimuth = azimuth + (float)((random.NextDouble() - 0.5) * 1.2);
            Vector3 outward = new(MathF.Cos(leanAzimuth), 0, MathF.Sin(leanAzimuth));
            float lean = Lerp(style.LeanMin, style.LeanMax, (float)random.NextDouble());
            float height = Lerp(style.HeightMin, style.HeightMax, (float)random.NextDouble());
            float width = style.Width * Lerp(0.7f, 1.3f, (float)random.NextDouble()) * (lite ? LiteWidening : 1);
            // The blade's face turns a little off square to its lean, so a clump never reads as one plane.
            float twist = (float)((random.NextDouble() - 0.5) * 1.4);
            Vector3 across = Vector3.Normalize(Vector3.Transform(Vector3.Cross(Vector3.UnitY, outward), Quaternion.CreateFromAxisAngle(Vector3.UnitY, twist)));
            bool dry = random.NextDouble() < style.DryShare;
            Vector3 hue = new(1 + Wander(random, palette.Wander), 1 + Wander(random, palette.Wander), 1 + Wander(random, palette.Wander * 0.6f));

            uint first = (uint)positions.Count;
            for (int step = 0; step <= segments; step++)
            {
                float t = step / (float)segments;
                // Rising, then bending over: the lean grows with height, more so as the curve deepens.
                float bend = lean * (t + (style.Curve * t * t));
                Vector3 centre = root + (outward * bend * height) + (Vector3.UnitY * height * t * (1 - (0.25f * bend * bend)));
                float half = width * 0.5f * MathF.Pow(1 - t, 0.8f);
                Vector3 face = Vector3.Normalize(Vector3.Cross(across, (outward * lean) + Vector3.UnitY));
                Vector3 normal = Vector3.Normalize(Vector3.Lerp(face, Vector3.UnitY, NormalUp));
                Vector3 shade = Shade(palette, t, dry) * hue;
                Color color = new(shade.X, shade.Y, shade.Z, t);
                if (step < segments)
                {
                    positions.AddRange([centre - (across * half), centre + (across * half)]);
                    normals.AddRange([normal, normal]);
                    uvs.AddRange([new(0, t), new(1, t)]);
                    colors.AddRange([color, color]);
                }
                else
                {
                    positions.Add(centre);
                    normals.Add(normal);
                    uvs.Add(new(0.5f, 1));
                    colors.Add(color);
                }
            }

            for (int step = 0; step < segments - 1; step++)
            {
                uint a = first + (uint)(step * 2);
                indices.AddRange([a, a + 1, a + 3, a, a + 3, a + 2]);
            }

            uint last = first + (uint)((segments - 1) * 2);
            indices.AddRange([last, last + 1, last + 2]);
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(), colors.ToArray(), indices.ToArray(),
            new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, material) });
    }

    /// <summary>A blade's colour a share <paramref name="t"/> of the way up: root to body to tip, or to a dry tip.</summary>
    private static Vector3 Shade(Palette palette, float t, bool dry) => t < 0.5f
        ? Vector3.Lerp(palette.Root, palette.Body, t * 2)
        : Vector3.Lerp(palette.Body, dry ? palette.Dry : palette.Tip, (t - 0.5f) * 2);

    private static float Wander(Random random, float amount) => (float)((random.NextDouble() - 0.5) * 2 * amount);

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);
}
