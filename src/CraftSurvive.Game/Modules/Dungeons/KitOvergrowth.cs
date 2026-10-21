using System.Numerics;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The underground forest's wall overgrowth on a built dungeon's kit (#10096; owner, 2026-10-21: wet dark green vines
/// and draped and clumped ivy filling in round the isolated ivy): vines draped from the walls' coves, the breach's lintel
/// and the ledges' lips, ivy clumps on the wall panels round the ivy blanket, mounds at the walls' and ribs' feet, and
/// root tangles off the corner ribs' heads. Each grows from its piece's dampness (KitMoss.Dampness: densest by the caves)
/// and, on the walls, from the same moss field the stain, cards and blanket read, so it gathers where they are thickest.
/// Positions are layout metres, as the cards' are.
/// </summary>
internal static class KitOvergrowth
{
    /// <summary>The most overgrowth a dungeon grows.</summary>
    private const int Most = 2048;

    // The kit's frame (kit_authored.py): the room's edge and the panel's half width (inside the pilasters), how far the
    // panel curves back, the pilaster's proud, the plinth's height and depth, the floor's top, the cove's height and how
    // far it overhangs, and a storey's and a tall wall's ceilings.
    private const float Inner = 3f, PanelHalf = 2.4f, PanelBack = 0.5f, PilasterProud = 0.15f, PlinthTop = 1.3f, Plinth = 0.12f;
    private const float FloorTop = 1.03f, CoveHeight = 0.7f, CoveOver = 0.45f, Ceiling = 5f, TallCeiling = 11f;

    // Drapes: hung this far under a ceiling (inside the cove's foot, which hides their head from below), every so far along
    // a wall (each moved up to so far either way), from this dampness; a long one this share of the time (on a tall wall,
    // always). A wall door's opening and a roundel's mosaic stay clear for this half width.
    private const float DrapeUnder = 0.55f, DrapeStep = 1.2f, DrapeJitter = 0.25f, DrapeFrom = 0.7f, LongShare = 0.35f, OpeningHalf = 1.4f;

    // Clumps on a wall's panel: tried every so far across and up (from and to these heights above the floor block), where the
    // moss field is this strong (the ivy blanket shows from 0.8), from this dampness and at most this share.
    private const float ClumpStep = 0.8f, ClumpLowest = 1.9f, ClumpGap = 0.6f, ClumpField = 0.7f, ClumpFrom = 0.9f, ClumpShare = 0.6f;

    // Mounds at a wall's foot: at these places along it, where the field at the wall's foot clears the stain's threshold.
    private static readonly float[] MoundAt = [-1.5f, 0f, 1.5f];
    private const float MoundJitter = 0.3f, MoundFrom = 0.8f, MoundReadHeight = 1.3f;

    // The breach: drapes over the broken lintel's face from this height, and mounds at the pilaster stumps' feet; it opens on
    // the cave, so its dampness counts from lower.
    private static readonly float[] BreachDrapes = [-1.8f, -0.6f, 0.6f, 1.8f];
    private const float LintelHead = 4.7f, BreachFrom = 0.4f;

    // Ledges: drapes from the lip (its height and how far out it stands, the lip facing -z), every so far along it, the field
    // read on the ledge's top just back from the lip.
    private const float LipHead = 0.95f, LipOut = -0.17f, LedgeStep = 1.2f, LedgeFrom = 0.6f;

    // Corner ribs (kit_authored.py's rib_section and tall_rib): roots hung off the rib's front at its head, and a mound at
    // its foot; each rib's front point on the corner's diagonal at those heights.
    private const float RibHeadY = 4.7f, RibHead = -2.69f, TallRibHeadY = 10.5f, TallRibHead = -2.53f, RibFoot = -2.48f, RootsFrom = 0.8f;

    // Sizes: each grown at so much of its authored size, give or take.
    private const float LeastScale = 0.85f, ScaleRange = 0.3f;

    internal static IReadOnlyList<OvergrowthPlacement> Placements(IReadOnlyList<KitPlacement> kit, ulong seed)
    {
        List<OvergrowthPlacement> grown = [];
        foreach (KitPlacement p in kit)
        {
            if (grown.Count >= Most)
            {
                break;
            }

            float damp = KitMoss.Dampness(p.Wet);
            switch (p.Piece)
            {
                case KitPiece.Wall or KitPiece.WallDoor or KitPiece.WallAlcove or KitPiece.WallRoundel or KitPiece.WallTall
                    or KitPiece.WallTallDoor or KitPiece.WallTallRoundel:
                    Wall(grown, p, damp, seed);
                    break;
                case KitPiece.WallBreach:
                    foreach (float x in BreachDrapes)
                    {
                        Grow(grown, p, OvergrowthKind.VineDrapeShort, new Vector3(x, LintelHead, PanelFace(x)), Vector3.UnitZ, Chance(damp, BreachFrom), seed);
                    }

                    foreach (float side in new[] { -1f, 1f })
                    {
                        float x = side * (Inner - 0.3f);
                        Grow(grown, p, OvergrowthKind.IvyMound, new Vector3(x, FloorTop, -Inner + PilasterProud + Plinth), Vector3.UnitZ, Chance(damp, BreachFrom), seed);
                    }

                    break;
                case KitPiece.Ledge1 or KitPiece.Ledge2 or KitPiece.Ledge4:
                    for (float x = -1.8f; x <= 1.8f; x += LedgeStep / Math.Max(p.Stretch.X, 0.25f))
                    {
                        if (KitMoss.Strength(p, new Vector3(x, FloorTop, 0.2f), Vector3.UnitY, 1f) < KitMoss.StainThreshold)
                        {
                            continue;
                        }

                        Vector3 head = new(x, LipHead, LipOut);
                        OvergrowthKind kind = Hash(seed ^ 0x1E, DungeonKit.Local(p, head)) < LongShare ? OvergrowthKind.VineDrape : OvergrowthKind.VineDrapeShort;
                        Grow(grown, p, kind, head, -Vector3.UnitZ, Chance(damp, LedgeFrom), seed);
                    }

                    break;
                case KitPiece.Corner or KitPiece.CornerTall:
                    bool tall = p.Piece == KitPiece.CornerTall;
                    Vector3 diagonal = Vector3.Normalize(new Vector3(1f, 0f, 1f));
                    float rib = tall ? TallRibHead : RibHead;
                    Grow(grown, p, OvergrowthKind.RootTangle, new Vector3(rib, tall ? TallRibHeadY : RibHeadY, rib), diagonal, Chance(damp, RootsFrom), seed);
                    Grow(grown, p, OvergrowthKind.IvyMound, new Vector3(RibFoot, FloorTop, RibFoot), diagonal, Chance(damp, MoundFrom), seed);
                    break;
            }
        }

        return grown;
    }

    private static void Wall(List<OvergrowthPlacement> grown, KitPlacement p, float damp, ulong seed)
    {
        bool tall = p.Piece is KitPiece.WallTall or KitPiece.WallTallDoor or KitPiece.WallTallRoundel;
        bool door = p.Piece is KitPiece.WallDoor or KitPiece.WallTallDoor;
        bool feature = p.Piece is KitPiece.WallRoundel or KitPiece.WallTallRoundel or KitPiece.WallAlcove;
        float ceiling = tall ? TallCeiling : Ceiling;

        // Vines draped from the cove.
        for (float x = -PanelHalf + 0.4f; x <= PanelHalf - 0.4f; x += DrapeStep)
        {
            float at = x + ((float)(Hash(seed ^ 0xD7, DungeonKit.Local(p, new Vector3(x, 0f, 0f))) - 0.5) * 2f * DrapeJitter);
            if ((door || feature) && MathF.Abs(at) < OpeningHalf)
            {
                continue;
            }

            float y = ceiling - DrapeUnder;
            Vector3 head = new(at, y, WallFace(at, y, ceiling));
            OvergrowthKind kind = tall || Hash(seed ^ 0x1E, DungeonKit.Local(p, head)) < LongShare ? OvergrowthKind.VineDrape : OvergrowthKind.VineDrapeShort;
            Grow(grown, p, kind, head, PanelNormal(at), Chance(damp, DrapeFrom), seed);
        }

        // Ivy clumps on the panel round the blanket, where the field is strong.
        for (float x = -PanelHalf + 0.4f; damp >= ClumpFrom && x <= PanelHalf - 0.4f; x += ClumpStep)
        {
            if ((door || feature) && MathF.Abs(x) < OpeningHalf)
            {
                continue;
            }

            for (float y = ClumpLowest; y <= ceiling - DrapeUnder - ClumpGap; y += ClumpStep)
            {
                float face = WallFace(x, y, ceiling);
                float rising = 1f - (0.65f * Math.Clamp((y - 1f) / 3.6f, 0f, 1f));
                if (KitMoss.Strength(p, new Vector3(x, y, face), Vector3.UnitZ, rising) < ClumpField)
                {
                    continue;
                }

                Grow(grown, p, OvergrowthKind.IvyClump, new Vector3(x, y, face), PanelNormal(x), ClumpShare * Chance(damp, ClumpFrom), seed);
            }
        }

        // Mounds at the wall's foot, on the plinth.
        foreach (float along in MoundAt)
        {
            float x = along + ((float)(Hash(seed ^ 0x3A, DungeonKit.Local(p, new Vector3(along, 0f, 0f))) - 0.5) * 2f * MoundJitter);
            if (door && MathF.Abs(x) < OpeningHalf)
            {
                continue;
            }

            float face = WallFace(x, FloorTop, ceiling);
            if (KitMoss.Strength(p, new Vector3(x, MoundReadHeight, face), Vector3.UnitZ, 0.95f) < KitMoss.StainThreshold)
            {
                continue;
            }

            Grow(grown, p, OvergrowthKind.IvyMound, new Vector3(x, FloorTop, face), PanelNormal(x), Chance(damp, MoundFrom), seed);
        }
    }

    /// <summary>One overgrowth at a point of a piece (its own frame) facing <paramref name="facing"/> there, with the given chance.</summary>
    private static void Grow(List<OvergrowthPlacement> grown, KitPlacement p, OvergrowthKind kind, Vector3 local, Vector3 facing, float chance, ulong seed)
    {
        Vector3 at = DungeonKit.Local(p, local);
        if (Hash(seed ^ (ulong)kind, at) >= chance)
        {
            return;
        }

        float pick = (float)Hash(seed ^ 0x5CA1E, at);
        grown.Add(new OvergrowthPlacement(kind, at, DungeonKit.Turned(p, facing), LeastScale + (ScaleRange * pick), (float)Hash(seed ^ 0x7157, at)));
    }

    /// <summary>How likely growth is at a dampness: none below <paramref name="from"/>, rising to certain at the wettest.</summary>
    private static float Chance(float damp, float from) => Math.Clamp((damp - from) / (KitMoss.MostDampness - from), 0f, 1f);

    /// <summary>The panel's face at x across (its plan curve; kit_authored.py's wall_depth without plinth and cove).</summary>
    private static float PanelFace(float x) => MathF.Abs(x) >= PanelHalf ? -Inner + PilasterProud
        : MathF.Min(-Inner - (PanelBack * MathF.Cos(0.5f * MathF.PI * x / PanelHalf)), -Inner);

    /// <summary>The wall's inner face at x across and y up under a ceiling (kit_authored.py's wall_depth).</summary>
    private static float WallFace(float x, float y, float ceiling)
    {
        float z = PanelFace(x) + (y < PlinthTop ? Plinth : 0f);
        float t = Math.Clamp((y - (ceiling - CoveHeight)) / CoveHeight, 0f, 1f);
        float cove = MathF.Pow(t * t * (3f - (2f * t)), 1.6f);
        return z + ((-Inner + PilasterProud + CoveOver - z) * cove);
    }

    /// <summary>The panel's outward normal at x across, in the piece's frame (its curve turns it a little).</summary>
    private static Vector3 PanelNormal(float x)
    {
        if (MathF.Abs(x) >= PanelHalf)
        {
            return Vector3.UnitZ;
        }

        float slope = PanelBack * 0.5f * MathF.PI / PanelHalf * MathF.Sin(0.5f * MathF.PI * x / PanelHalf);
        return Vector3.Normalize(new Vector3(-slope, 0f, 1f));
    }

    private static double Hash(ulong seed, Vector3 at) => KitMoss.Hash(seed ^ 0x0F6047UL, at);
}
