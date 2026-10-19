using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The cave's living light drawn (#10129; owner, 2026-10-19: light always has a source the player can see): each kind's
/// growths merged into one mesh in a glowing material and a plain one, and the small unshadowed lights the strongest
/// growths give. The places come from the cave generator's fields (<see cref="KarstCave"/>); this only builds them.
/// </summary>
internal static class CaveEcology
{
    // How each kind glows (linear emission) and looks unlit, and the light its strongest growths give.
    internal static readonly Vector3 FungiGlow = new(0.15f, 0.9f, 0.6f), WormGlow = new(0.25f, 0.6f, 1.2f), CrystalGlow = new(0.55f, 0.3f, 0.9f);
    private static readonly Color FungiStem = new(0.35f, 0.32f, 0.26f, 1f), WormThread = new(0.6f, 0.62f, 0.6f, 1f), CrystalBody = new(0.3f, 0.22f, 0.4f, 1f);
    private const float FungiGlowStrength = 2.5f, WormGlowStrength = 3f, CrystalGlowStrength = 1.2f, GrowthRoughness = 0.6f;

    // A great hall's lights (#10094): the giant fungi's stalks and caps and the vault blooms' glow.
    internal static readonly Vector3 GiantGlow = new(0.15f, 0.6f, 1f), VaultGlow = new(0.7f, 0.85f, 1f);
    private static readonly Color GiantStalk = new(0.42f, 0.4f, 0.36f, 1f);
    private const float GiantGlowStrength = 1.1f, VaultGlowStrength = 1.2f;

    // Giant fungi: stalk height and girth, cap radius, how much the stalk bends, and the small ones at its foot.
    private const float GiantHeight = 6f, GiantGirth = 0.35f, GiantCap = 2.6f, GiantBend = 0.6f;
    private const int GiantSegments = 6, FewestCompanions = 2, MostCompanions = 5;
    private const float CompanionLeast = 0.25f, CompanionMost = 0.5f, CompanionSpread = 3.5f;

    // Vault blooms: prisms per bloom, their lengths and widths, and how far they splay from hanging straight down.
    private const int FewestVaultPrisms = 12, MostVaultPrisms = 22;
    private const float ShortestVaultPrism = 1.5f, LongestVaultPrism = 5f, VaultPrismWidth = 0.35f, VaultSplay = 1.1f;

    // Each hall light: how bright and how far it reaches.
    private const float GiantIntensity = 220f, GiantRange = 35f, VaultIntensity = 2500f, VaultRange = 90f;

    // Fungi: caps per cluster, their spread and sizes.
    private const int FewestCaps = 5, MostCaps = 14;
    private const float CapSpread = 0.6f, SmallestCap = 0.05f, LargestCap = 0.16f;

    // Glow-worms: threads per curtain, its spread, thread lengths and widths, and the bead.
    private const int FewestThreads = 30, MostThreads = 80;
    private const float CurtainSpread = 1.2f, ShortestThread = 0.15f, LongestThread = 0.9f, ThreadWidth = 0.008f, Bead = 0.02f;

    // Crystals: prisms per bloom, their lengths and widths.
    private const int FewestPrisms = 4, MostPrisms = 9;
    private const float ShortestPrism = 0.25f, LongestPrism = 0.9f, PrismWidth = 0.09f;

    /// <summary>The lights the growths give: their colour, intensity and reach, by kind.</summary>
    internal static (Vector3 Colour, float Intensity, float Range) LightOf(EcologyKind kind) => kind switch
    {
        EcologyKind.GlowFungi => (FungiGlow, 2f, 4f),
        EcologyKind.GlowWorms => (WormGlow, 1.5f, 6f),
        EcologyKind.GiantFungus => (GiantGlow, GiantIntensity, GiantRange),
        EcologyKind.VaultBloom => (VaultGlow, VaultIntensity, VaultRange),
        _ => (CrystalGlow, 1f, 3f),
    };

    /// <summary>Whether a kind's light casts shadows: the vault's sun does, so the hall's pillars and giants throw them across its floor.</summary>
    internal static bool Shadowed(EcologyKind kind) => kind == EcologyKind.VaultBloom;

    /// <summary>Where a growth's light hangs, in layout metres: under a giant's cap, inside a vault bloom, else just off the growth.</summary>
    internal static Vector3 LightAt(EcologyPlacement growth, float offset) => growth.Kind switch
    {
        EcologyKind.GiantFungus => growth.At + (growth.Normal * ((GiantHeight * growth.Size) - (GiantCap * growth.Size * 0.4f))),
        EcologyKind.VaultBloom => growth.At + (growth.Normal * (ShortestVaultPrism * growth.Size)),
        _ => growth.At + (growth.Normal * offset),
    };

    /// <summary>The growths of every kind as meshes (one per kind: a plain group and a glowing group), positioned in the session.</summary>
    internal static IReadOnlyList<MeshResourceCreateRequest> Meshes(IEngineContext engine, IReadOnlyList<EcologyPlacement> growths, Vector3 origin,
        List<Material> materials)
    {
        List<MeshResourceCreateRequest> meshes = [];
        foreach (EcologyKind kind in Enum.GetValues<EcologyKind>())
        {
            EcologyPlacement[] these = [.. growths.Where(g => g.Kind == kind)];
            if (these.Length == 0)
            {
                continue;
            }

            (Color body, Vector3 glow, float strength) = kind switch
            {
                EcologyKind.GlowFungi => (FungiStem, FungiGlow, FungiGlowStrength),
                EcologyKind.GlowWorms => (WormThread, WormGlow, WormGlowStrength),
                EcologyKind.GiantFungus => (GiantStalk, GiantGlow, GiantGlowStrength),
                EcologyKind.VaultBloom => (CrystalBody, VaultGlow, VaultGlowStrength),
                _ => (CrystalBody, CrystalGlow, CrystalGlowStrength),
            };
            Material plain = engine.Graphics.CreateMaterial(new MaterialRequest(body, default, GrowthRoughness, new Color(1f, 1f, 1f, 1f), Vector3.Zero, 0f, false));
            Material glowing = engine.Graphics.CreateMaterial(new MaterialRequest(new Color(glow.X * 0.3f, glow.Y * 0.3f, glow.Z * 0.3f, 1f), default,
                GrowthRoughness, new Color(1f, 1f, 1f, 1f), glow, strength, false));
            materials.Add(plain);
            materials.Add(glowing);
            Builder plainPart = new(), glowPart = new();
            foreach (EcologyPlacement growth in these)
            {
                Random random = new((int)growth.Seed);
                Vector3 at = growth.At + origin;
                switch (kind)
                {
                    case EcologyKind.GlowFungi:
                        Fungi(plainPart, glowPart, at, growth.Normal, growth.Size, random);
                        break;
                    case EcologyKind.GlowWorms:
                        Worms(plainPart, glowPart, at, growth.Size, random);
                        break;
                    case EcologyKind.GiantFungus:
                        Giant(plainPart, glowPart, at, growth.Size, random);
                        break;
                    case EcologyKind.VaultBloom:
                        VaultBloom(glowPart, at, growth.Size, random);
                        break;
                    default:
                        Crystals(glowPart, at, growth.Normal, growth.Size, random);
                        break;
                }
            }

            meshes.Add(Merge(plainPart, glowPart, plain, glowing));
        }

        return meshes;
    }

    /// <summary>A cluster of caps on short stems, leaning out from the surface they grow on.</summary>
    private static void Fungi(Builder stems, Builder caps, Vector3 at, Vector3 normal, float size, Random random)
    {
        int count = FewestCaps + random.Next(MostCaps - FewestCaps + 1);
        (Vector3 u, Vector3 v) = Tangents(normal);
        for (int i = 0; i < count; i++)
        {
            float a = (float)random.NextDouble() * MathF.Tau, r = CapSpread * size * MathF.Sqrt((float)random.NextDouble());
            float cap = (SmallestCap + ((LargestCap - SmallestCap) * (float)random.NextDouble())) * size;
            Vector3 foot = at + (u * (MathF.Cos(a) * r)) + (v * (MathF.Sin(a) * r));
            Vector3 up = Vector3.Normalize(normal + (u * (((float)random.NextDouble() - 0.5f) * 0.6f)) + (v * (((float)random.NextDouble() - 0.5f) * 0.6f)));
            Vector3 top = foot + (up * cap * 1.6f);
            stems.Prism(foot, top, cap * 0.22f, 5);
            caps.Dome(top, up, cap, 6);
        }
    }

    /// <summary>A giant fungus: a bending stalk under a broad glowing cap, with a few small ones at its foot.</summary>
    private static void Giant(Builder stalks, Builder caps, Vector3 at, float size, Random random)
    {
        float height = GiantHeight * size, girth = GiantGirth * size, cap = GiantCap * size;
        Vector3 lean = new(((float)random.NextDouble() - 0.5f) * GiantBend, 0f, ((float)random.NextDouble() - 0.5f) * GiantBend);
        Vector3 Along(float t) => at + new Vector3(0f, height * t, 0f) + (lean * height * t * t * 0.3f);
        for (int i = 0; i < GiantSegments; i++)
        {
            float t0 = i / (float)GiantSegments, t1 = (i + 1) / (float)GiantSegments;
            // Thicker at the foot, as a stalk that carries a great cap.
            stalks.Prism(Along(t0), Along(t1) + new Vector3(0f, 0.05f, 0f), girth * (1.6f - (0.7f * t0)), 8);
        }

        Vector3 top = Along(1f);
        Vector3 up = Vector3.Normalize(Vector3.UnitY + (lean * 0.4f));
        caps.Dome(top - (up * cap * 0.15f), up, cap, 12);
        int companions = FewestCompanions + random.Next(MostCompanions - FewestCompanions + 1);
        for (int i = 0; i < companions; i++)
        {
            float a = (float)random.NextDouble() * MathF.Tau, r = (0.5f + (float)random.NextDouble()) * CompanionSpread * size * 0.5f;
            float small = size * (CompanionLeast + ((CompanionMost - CompanionLeast) * (float)random.NextDouble()));
            Vector3 foot = at + new Vector3(MathF.Cos(a) * r, -0.1f, MathF.Sin(a) * r);
            Vector3 head = foot + new Vector3(0f, GiantHeight * small, 0f);
            stalks.Prism(foot, head, GiantGirth * small * 1.3f, 6);
            caps.Dome(head, Vector3.UnitY, GiantCap * small, 8);
        }
    }

    /// <summary>A great crystal bloom hanging from a vault: long prisms splaying down and out, all aglow.</summary>
    private static void VaultBloom(Builder prisms, Vector3 at, float size, Random random)
    {
        int count = FewestVaultPrisms + random.Next(MostVaultPrisms - FewestVaultPrisms + 1);
        for (int i = 0; i < count; i++)
        {
            float a = (float)random.NextDouble() * MathF.Tau, splay = VaultSplay * MathF.Sqrt((float)random.NextDouble());
            Vector3 lean = Vector3.Normalize(new Vector3(MathF.Cos(a) * splay, -1f, MathF.Sin(a) * splay));
            float length = (ShortestVaultPrism + ((LongestVaultPrism - ShortestVaultPrism) * (float)random.NextDouble())) * size;
            float width = VaultPrismWidth * size * (0.6f + (0.6f * (float)random.NextDouble()));
            Vector3 foot = at + new Vector3(0f, 0.3f, 0f);
            Vector3 shoulder = foot + (lean * length * 0.8f);
            prisms.Prism(foot, shoulder, width, 6);
            prisms.Cone(shoulder, shoulder + (lean * length * 0.2f), width, 6);
        }
    }

    /// <summary>A curtain of threads hanging straight down from a ceiling, each ending in a glowing bead.</summary>
    private static void Worms(Builder threads, Builder beads, Vector3 at, float size, Random random)
    {
        int count = FewestThreads + random.Next(MostThreads - FewestThreads + 1);
        for (int i = 0; i < count; i++)
        {
            float a = (float)random.NextDouble() * MathF.Tau, r = CurtainSpread * size * MathF.Sqrt((float)random.NextDouble());
            Vector3 top = at + new Vector3(MathF.Cos(a) * r, 0f, MathF.Sin(a) * r);
            float length = ShortestThread + ((LongestThread - ShortestThread) * (float)Math.Pow(random.NextDouble(), 0.7));
            Vector3 tip = top - new Vector3(0f, length, 0f);
            threads.Prism(top, tip, ThreadWidth, 3);
            beads.Dome(tip - new Vector3(0f, Bead, 0f), -Vector3.UnitY, Bead * 1.4f, 4);
            beads.Dome(tip - new Vector3(0f, Bead, 0f), Vector3.UnitY, Bead * 1.4f, 4);
        }
    }

    /// <summary>A bloom of pointed prisms splaying out from the rock.</summary>
    private static void Crystals(Builder prisms, Vector3 at, Vector3 normal, float size, Random random)
    {
        int count = FewestPrisms + random.Next(MostPrisms - FewestPrisms + 1);
        (Vector3 u, Vector3 v) = Tangents(normal);
        for (int i = 0; i < count; i++)
        {
            Vector3 lean = Vector3.Normalize(normal + (u * (((float)random.NextDouble() - 0.5f) * 1.4f)) + (v * (((float)random.NextDouble() - 0.5f) * 1.4f)));
            float length = (ShortestPrism + ((LongestPrism - ShortestPrism) * (float)random.NextDouble())) * size;
            Vector3 foot = at - (normal * 0.05f);
            Vector3 shoulder = foot + (lean * length * 0.8f);
            prisms.Prism(foot, shoulder, PrismWidth * size, 6);
            prisms.Cone(shoulder, shoulder + (lean * length * 0.2f), PrismWidth * size, 6);
        }
    }

    private static (Vector3 U, Vector3 V) Tangents(Vector3 normal)
    {
        Vector3 u = Vector3.Normalize(Vector3.Cross(normal, MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        return (u, Vector3.Cross(normal, u));
    }

    private static MeshResourceCreateRequest Merge(Builder plain, Builder glowing, Material plainMaterial, Material glowingMaterial)
    {
        uint offset = (uint)plain.Positions.Count;
        Vector3[] positions = [.. plain.Positions, .. glowing.Positions];
        Vector3[] normals = [.. plain.Normals, .. glowing.Normals];
        uint[] indices = [.. plain.Indices, .. glowing.Indices.Select(i => i + offset)];
        // A kind with no plain part (crystals glow throughout) has only its glowing group: the Engine refuses empty groups.
        List<MeshGroup> groups = [];
        List<MeshMaterialBinding> bindings = [];
        if (plain.Indices.Count > 0)
        {
            groups.Add(new MeshGroup(0, 0, (uint)plain.Indices.Count));
            bindings.Add(new MeshMaterialBinding(0, plainMaterial));
        }

        if (glowing.Indices.Count > 0)
        {
            groups.Add(new MeshGroup(1, (uint)plain.Indices.Count, (uint)glowing.Indices.Count));
            bindings.Add(new MeshMaterialBinding(1, glowingMaterial));
        }

        return new MeshResourceCreateRequest(positions, normals, new Vector2[positions.Length], ReadOnlyMemory<Color>.Empty, indices,
            groups.ToArray(), bindings.ToArray());
    }

    /// <summary>Flat-shaded low-poly pieces gathered into one vertex list.</summary>
    private sealed class Builder
    {
        internal List<Vector3> Positions { get; } = [];
        internal List<Vector3> Normals { get; } = [];
        internal List<uint> Indices { get; } = [];

        private void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
            uint first = (uint)Positions.Count;
            Positions.Add(a);
            Positions.Add(b);
            Positions.Add(c);
            Normals.Add(n);
            Normals.Add(n);
            Normals.Add(n);
            Indices.Add(first);
            Indices.Add(first + 1);
            Indices.Add(first + 2);

            // Both faces: growths are thin and seen from every side.
            uint back = (uint)Positions.Count;
            Positions.Add(a);
            Positions.Add(c);
            Positions.Add(b);
            Normals.Add(-n);
            Normals.Add(-n);
            Normals.Add(-n);
            Indices.Add(back);
            Indices.Add(back + 1);
            Indices.Add(back + 2);
        }

        private static (Vector3 U, Vector3 V) Ring(Vector3 axis) => Tangents(Vector3.Normalize(axis));

        /// <summary>An open-ended prism of some sides from one point to another.</summary>
        internal void Prism(Vector3 from, Vector3 to, float radius, int sides)
        {
            (Vector3 u, Vector3 v) = Ring(to - from);
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * MathF.Tau / sides, a1 = (i + 1) * MathF.Tau / sides;
                Vector3 o0 = ((u * MathF.Cos(a0)) + (v * MathF.Sin(a0))) * radius, o1 = ((u * MathF.Cos(a1)) + (v * MathF.Sin(a1))) * radius;
                Triangle(from + o0, to + o0, to + o1);
                Triangle(from + o0, to + o1, from + o1);
            }
        }

        /// <summary>A cone of some sides from a base ring to a point.</summary>
        internal void Cone(Vector3 from, Vector3 tip, float radius, int sides)
        {
            (Vector3 u, Vector3 v) = Ring(tip - from);
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * MathF.Tau / sides, a1 = (i + 1) * MathF.Tau / sides;
                Triangle(from + (((u * MathF.Cos(a0)) + (v * MathF.Sin(a0))) * radius), tip, from + (((u * MathF.Cos(a1)) + (v * MathF.Sin(a1))) * radius));
            }
        }

        /// <summary>A low dome: a ring and a crown over a flat underside, facing along an axis.</summary>
        internal void Dome(Vector3 centre, Vector3 up, float radius, int sides)
        {
            (Vector3 u, Vector3 v) = Ring(up);
            Vector3 crown = centre + (up * radius * 0.7f);
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * MathF.Tau / sides, a1 = (i + 1) * MathF.Tau / sides;
                Vector3 r0 = centre + (((u * MathF.Cos(a0)) + (v * MathF.Sin(a0))) * radius), r1 = centre + (((u * MathF.Cos(a1)) + (v * MathF.Sin(a1))) * radius);
                Vector3 m0 = centre + (up * radius * 0.45f) + (((u * MathF.Cos(a0)) + (v * MathF.Sin(a0))) * radius * 0.7f);
                Vector3 m1 = centre + (up * radius * 0.45f) + (((u * MathF.Cos(a1)) + (v * MathF.Sin(a1))) * radius * 0.7f);
                Triangle(r0, m0, m1);
                Triangle(r0, m1, r1);
                Triangle(m0, crown, m1);
                Triangle(r1, centre, r0);
            }
        }
    }
}
