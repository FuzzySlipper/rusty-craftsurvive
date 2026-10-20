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
    internal static readonly Vector3 GiantGlow = new(0.15f, 0.6f, 1f), VaultGlow = new(0.5f, 0.72f, 1f);
    private static readonly Color GiantStalk = new(0.42f, 0.4f, 0.36f, 1f);

    /// <summary>
    /// A giant's cap is dark on top and glows only in its gills beneath (owner, 2026-10-19), and the gills take almost no light,
    /// so the giant's own light - hung inside its cap on the stalk's axis, where every face seen turns away from it or meets it edge-on - lights
    /// the floor round it without burning the fungus out.
    /// </summary>
    private static readonly Color GiantCapTop = new(0.07f, 0.08f, 0.11f, 1f), GiantGillAlbedo = new(0.015f, 0.02f, 0.03f, 1f);
    private const int GiantGills = 28, CompanionGills = 14;
    private const float GillDepth = 0.35f, GillInner = 1.2f, LightInsideCap = 0.08f;
    private const float GiantGlowStrength = 2f, VaultGlowStrength = 3f;

    /// <summary>
    /// A vault bloom's crystals take almost no light (their colour is their own glow), so the hall's sun hanging under them
    /// does not burn them to white; and how far below the longest crystal's tip that light hangs, so it lies outside them.
    /// </summary>
    private static readonly Color VaultCrystalAlbedo = new(0.015f, 0.02f, 0.03f, 1f);
    private const float VaultLightBelowTips = 4f;

    // Giant fungi: stalk height and girth, cap radius, how much the stalk bends, and the small ones at its foot.
    private const float GiantHeight = 6f, GiantGirth = 0.35f, GiantCap = 2.6f, GiantBend = 0.6f;
    private const int GiantSegments = 6, FewestCompanions = 2, MostCompanions = 5;
    private const float CompanionLeast = 0.25f, CompanionMost = 0.5f, CompanionSpread = 3.5f;

    // Vault blooms: prisms per bloom, their lengths and widths, and how far they splay from hanging straight down.
    private const int FewestVaultPrisms = 12, MostVaultPrisms = 22;
    private const float ShortestVaultPrism = 1.5f, LongestVaultPrism = 5f, VaultPrismWidth = 0.35f, VaultSplay = 1.1f;

    // A luminous pool (#10129): its glow (linear; the water's emission is this, the light's colour too), the light's
    // brightness and reach (scaled by the pool's size), and its height over the water.
    internal static readonly Vector3 PoolGlow = new(0.1f, 0.55f, 0.75f);
    private const float PoolIntensity = 4f, PoolRange = 8f, PoolLightAbove = 0.4f;

    // Root curtains (#10129): the roots' colour (linear, a dark bark brown), how many strands a curtain has, how far
    // they spread, how long they hang, how thick they start, how they wander and fork as they hang.
    private static readonly Color RootColour = new(0.07f, 0.05f, 0.035f, 1f);
    private const int FewestRoots = 25, MostRootStrands = 60, RootSegments = 5;
    private const float RootSpread = 1.4f, ShortestRoot = 0.4f, LongestRoot = 2.6f, RootWidth = 0.03f, RootWander = 0.22f, RootForkChance = 0.35f;

    // Each hall light: how bright and how far it reaches.
    private const float GiantIntensity = 220f, GiantRange = 35f, VaultIntensity = 1500f, VaultRange = 90f;

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
        EcologyKind.LuminousPool => (PoolGlow, PoolIntensity, PoolRange),
        _ => (CrystalGlow, 1f, 3f),
    };

    /// <summary>Whether a kind of growth gives light at all (roots do not).</summary>
    internal static bool GivesLight(EcologyKind kind) => kind != EcologyKind.RootCurtain;

    /// <summary>Whether a kind's light casts shadows: the vault's sun does, so the hall's pillars and giants throw them across its floor.</summary>
    internal static bool Shadowed(EcologyKind kind) => kind == EcologyKind.VaultBloom;

    /// <summary>Where a growth's light hangs, in layout metres: inside a giant's cap, below a vault bloom's tips, else just off the growth.</summary>
    internal static Vector3 LightAt(EcologyPlacement growth, float offset) => growth.Kind switch
    {
        EcologyKind.GiantFungus => GiantStalkTop(growth.At, growth.Size, new Random((int)growth.Seed)).Top - (Vector3.UnitY * GiantCap * growth.Size * LightInsideCap),
        EcologyKind.VaultBloom => growth.At + (growth.Normal * ((LongestVaultPrism * growth.Size) + VaultLightBelowTips)),
        EcologyKind.LuminousPool => growth.At + (growth.Normal * PoolLightAbove),
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
            // A luminous pool's glowing thing is its water (CaveWaterSurfaces): it has a light here, no mesh.
            if (these.Length == 0 || kind == EcologyKind.LuminousPool)
            {
                continue;
            }

            (Color body, Vector3 glow, float strength) = kind switch
            {
                EcologyKind.GlowFungi => (FungiStem, FungiGlow, FungiGlowStrength),
                EcologyKind.GlowWorms => (WormThread, WormGlow, WormGlowStrength),
                EcologyKind.GiantFungus => (GiantStalk, GiantGlow, GiantGlowStrength),
                EcologyKind.VaultBloom => (CrystalBody, VaultGlow, VaultGlowStrength),
                EcologyKind.RootCurtain => (RootColour, Vector3.Zero, 0f),
                _ => (CrystalBody, CrystalGlow, CrystalGlowStrength),
            };
            Material plain = engine.Graphics.CreateMaterial(new MaterialRequest(body, default, GrowthRoughness, new Color(1f, 1f, 1f, 1f), Vector3.Zero, 0f, false));
            Color lit = kind switch
            {
                EcologyKind.VaultBloom => VaultCrystalAlbedo,
                EcologyKind.GiantFungus => GiantGillAlbedo,
                _ => new Color(glow.X * 0.3f, glow.Y * 0.3f, glow.Z * 0.3f, 1f),
            };
            Material glowing = engine.Graphics.CreateMaterial(new MaterialRequest(lit, default,
                GrowthRoughness, new Color(1f, 1f, 1f, 1f), glow, strength, false));
            materials.Add(plain);
            materials.Add(glowing);
            Builder plainPart = new(), glowPart = new(), topPart = new();
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
                        Giant(plainPart, topPart, glowPart, at, growth.Size, random);
                        break;
                    case EcologyKind.VaultBloom:
                        VaultBloom(glowPart, at, growth.Size, random);
                        break;
                    case EcologyKind.RootCurtain:
                        Roots(plainPart, at, growth.Size, random);
                        break;
                    default:
                        Crystals(glowPart, at, growth.Normal, growth.Size, random);
                        break;
                }
            }

            List<(Builder, Material)> parts = [(plainPart, plain), (glowPart, glowing)];
            if (topPart.Indices.Count > 0)
            {
                Material top = engine.Graphics.CreateMaterial(new MaterialRequest(GiantCapTop, default, GrowthRoughness, new Color(1f, 1f, 1f, 1f), Vector3.Zero, 0f, false));
                materials.Add(top);
                parts.Add((topPart, top));
            }

            meshes.Add(Merge(parts));
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

    /// <summary>A giant's stalk: its lean and the top it carries the cap on (read from the giant's seed first, so its light can find it).</summary>
    private static (Vector3 Lean, Vector3 Top) GiantStalkTop(Vector3 at, float size, Random random)
    {
        Vector3 lean = new(((float)random.NextDouble() - 0.5f) * GiantBend, 0f, ((float)random.NextDouble() - 0.5f) * GiantBend);
        return (lean, at + new Vector3(0f, GiantHeight * size, 0f) + (lean * GiantHeight * size * 0.3f));
    }

    /// <summary>A giant fungus: a bending stalk under a broad dark cap with glowing gills beneath, with a few small ones at its foot.</summary>
    private static void Giant(Builder stalks, Builder tops, Builder gills, Vector3 at, float size, Random random)
    {
        float height = GiantHeight * size, girth = GiantGirth * size, cap = GiantCap * size;
        (Vector3 lean, Vector3 top) = GiantStalkTop(at, size, random);
        Vector3 Along(float t) => at + new Vector3(0f, height * t, 0f) + (lean * height * t * t * 0.3f);
        for (int i = 0; i < GiantSegments; i++)
        {
            float t0 = i / (float)GiantSegments, t1 = (i + 1) / (float)GiantSegments;
            // Thicker at the foot, as a stalk that carries a great cap.
            stalks.Prism(Along(t0), Along(t1) + new Vector3(0f, 0.05f, 0f), girth * (1.6f - (0.7f * t0)), 8);
        }

        Vector3 up = Vector3.Normalize(Vector3.UnitY + (lean * 0.4f));
        Cap(tops, gills, top - (up * cap * 0.15f), up, cap, girth, 12, GiantGills);
        int companions = FewestCompanions + random.Next(MostCompanions - FewestCompanions + 1);
        for (int i = 0; i < companions; i++)
        {
            float a = (float)random.NextDouble() * MathF.Tau, r = (0.5f + (float)random.NextDouble()) * CompanionSpread * size * 0.5f;
            float small = size * (CompanionLeast + ((CompanionMost - CompanionLeast) * (float)random.NextDouble()));
            Vector3 foot = at + new Vector3(MathF.Cos(a) * r, -0.1f, MathF.Sin(a) * r);
            Vector3 head = foot + new Vector3(0f, GiantHeight * small, 0f);
            stalks.Prism(foot, head, GiantGirth * small * 1.3f, 6);
            Cap(tops, gills, head, Vector3.UnitY, GiantCap * small, GiantGirth * small * 1.3f, 8, CompanionGills);
        }
    }

    /// <summary>A cap: a dark dome over radial gill fins hanging from its underside, from the stalk out to near the rim.</summary>
    private static void Cap(Builder tops, Builder gills, Vector3 centre, Vector3 up, float radius, float stalk, int sides, int fins)
    {
        tops.Dome(centre, up, radius, sides);
        (Vector3 u, Vector3 v) = Tangents(up);
        float depth = radius * GillDepth;
        for (int i = 0; i < fins; i++)
        {
            float a = i * MathF.Tau / fins;
            Vector3 out_ = (u * MathF.Cos(a)) + (v * MathF.Sin(a));
            Vector3 inner = centre + (out_ * stalk * GillInner), outer = centre + (out_ * radius * 0.95f);
            // Deepest a little in from the rim, closing up to the cap at both ends.
            Vector3 low = centre + (out_ * radius * 0.45f) - (up * depth);
            gills.Triangle(inner - (up * 0.02f), low, outer - (up * 0.02f));
        }
    }

    /// <summary>A curtain of roots hanging from a ceiling: strands tapering as they hang, wandering a little, some forking.</summary>
    private static void Roots(Builder strands, Vector3 at, float size, Random random)
    {
        int count = FewestRoots + random.Next(MostRootStrands - FewestRoots + 1);
        for (int i = 0; i < count; i++)
        {
            float a = (float)random.NextDouble() * MathF.Tau, r = RootSpread * size * MathF.Sqrt((float)random.NextDouble());
            Vector3 top = at + new Vector3(MathF.Cos(a) * r, 0.05f, MathF.Sin(a) * r);
            float length = (ShortestRoot + ((LongestRoot - ShortestRoot) * (float)Math.Pow(random.NextDouble(), 1.5))) * size;
            Strand(strands, top, length, RootWidth * (0.6f + (0.8f * (float)random.NextDouble())), random, fork: true);
        }
    }

    /// <summary>One root strand hanging down from a point, tapering, wandering a little; it may fork once.</summary>
    private static void Strand(Builder strands, Vector3 from, float length, float width, Random random, bool fork)
    {
        Vector3 at = from;
        Vector3 drift = Vector3.Zero;
        for (int s = 0; s < RootSegments; s++)
        {
            float t = s / (float)RootSegments;
            drift += new Vector3(((float)random.NextDouble() - 0.5f) * RootWander, 0f, ((float)random.NextDouble() - 0.5f) * RootWander);
            Vector3 next = at + new Vector3(drift.X, -length / RootSegments, drift.Z);
            strands.Prism(at, next, width * (1f - (0.75f * t)), 4);
            if (fork && s == 1 && random.NextDouble() < RootForkChance)
            {
                Strand(strands, next, length * 0.5f, width * 0.6f, random, fork: false);
            }

            at = next;
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

    /// <summary>The parts of a kind's mesh, each drawn in its own material; empty parts are left out (the Engine refuses empty groups).</summary>
    private static MeshResourceCreateRequest Merge(IReadOnlyList<(Builder Part, Material Material)> parts)
    {
        List<Vector3> positions = [], normals = [];
        List<uint> indices = [];
        List<MeshGroup> groups = [];
        List<MeshMaterialBinding> bindings = [];
        foreach ((Builder part, Material material) in parts)
        {
            if (part.Indices.Count == 0)
            {
                continue;
            }

            uint offset = (uint)positions.Count;
            uint slot = (uint)groups.Count;
            groups.Add(new MeshGroup(slot, (uint)indices.Count, (uint)part.Indices.Count));
            bindings.Add(new MeshMaterialBinding(slot, material));
            positions.AddRange(part.Positions);
            normals.AddRange(part.Normals);
            indices.AddRange(part.Indices.Select(i => i + offset));
        }

        return new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), new Vector2[positions.Count], ReadOnlyMemory<Color>.Empty, indices.ToArray(),
            groups.ToArray(), bindings.ToArray());
    }

    /// <summary>Flat-shaded low-poly pieces gathered into one vertex list.</summary>
    private sealed class Builder
    {
        internal List<Vector3> Positions { get; } = [];
        internal List<Vector3> Normals { get; } = [];
        internal List<uint> Indices { get; } = [];

        internal void Triangle(Vector3 a, Vector3 b, Vector3 c)
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
