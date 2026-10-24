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

    // Floors (owner, 2026-10-21: grass clumps on the floor, an implied growth, and mushrooms): grass runs along the floor's
    // edges (a wall's foot or a slab's seam, so far in from the room's edge, at these places along it), tufts tried every so
    // far over it (each moved up to so far), and mushrooms in its corners and along its walls; each where the floor's moss
    // field clears the stain's threshold, from these dampnesses. The water gathers at a wall's foot: a walled edge grows
    // in full, an open edge (a hall's floor runs on into the next cell's) only this share as a seam's grass, and the open
    // floor only tufts where the field is this far over the threshold, at this share (strewn evenly, tufts read as dots).
    private const float RoomHalf = 2.6f, StripIn = 0.3f, TuftStep = 0.9f, TuftJitter = 0.3f, TuftEdge = 1.0f;
    private const float SeamShare = 0.3f, OpenMargin = 0.12f, OpenShare = 0.25f;
    private static readonly float[] StripAt = [-1.5f, 0f, 1.5f];
    private const float GrassFrom = 0.7f, MushroomsFrom = 0.7f, MushroomAlong = 1.15f, MushroomIn = 0.4f, CornerShare = 0.9f, AlongShare = 0.45f;

    // Grass and mushrooms grow over their authored sizes, so a tuft and a cluster read at a room's width.
    private const float GrassScale = 1.5f, MushroomScale = 1.8f;

    // A rubble heap's top (kit_authored.py's mound over the 2 x 2 heap of gravel, its top at 2 m): tufts and mushrooms on it.
    private const float HeapTop = 2.15f;

    // Sizes: each grown at so much of its authored size, give or take.
    private const float LeastScale = 0.85f, ScaleRange = 0.3f;

    internal static IReadOnlyList<OvergrowthPlacement> Placements(IReadOnlyList<KitPlacement> kit, ulong seed)
    {
        List<OvergrowthPlacement> grown = [];
        // Which way each cell's walls face into its room, by the cell (its pieces share its origin).
        Dictionary<(int, int, int), List<Vector3>> walls = [];
        foreach (KitPlacement w in kit.Where(w => w.Piece is KitPiece.Wall or KitPiece.WallDoor or KitPiece.WallAlcove or KitPiece.WallRoundel
            or KitPiece.WallTall or KitPiece.WallTallDoor or KitPiece.WallTallRoundel or KitPiece.WallBreach))
        {
            (int, int, int) cell = Cell(w.Origin);
            if (!walls.TryGetValue(cell, out List<Vector3>? faces))
            {
                walls[cell] = faces = [];
            }

            faces.Add(DungeonKit.Turned(w, Vector3.UnitZ));
        }

        // The cells holding a broken piece (#10305): fallen stonework gathers on the floors in and round them.
        List<Vector3> broken = [.. kit.Where(b => b.Piece is KitPiece.WallBreach or KitPiece.CornerBroken or KitPiece.RubbleHeap or KitPiece.RubbleSlope
            or KitPiece.FloorHeaved).Select(b => b.Origin)];
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
                case KitPiece.Floor:
                    Floor(grown, p, damp, seed, walls.GetValueOrDefault(Cell(p.Origin)) ?? []);
                    Fallen(grown, p, damp, seed, broken.Any(b => Vector3.Distance(b, p.Origin) < BrokenNear));
                    break;
                case KitPiece.Vault:
                    Vault(grown, p, damp, seed);
                    break;
                case KitPiece.RubbleHeap:
                    for (float x = 0.4f; x <= 1.6f; x += TuftStep / 1.5f)
                        for (float z = 0.4f; z <= 1.6f; z += TuftStep / 1.5f)
                        {
                            Vector3 on = new(x, HeapTop, z);
                            if (KitMoss.Strength(p, on, Vector3.UnitY, 1f) >= KitMoss.StainThreshold)
                            {
                                bool mushroom = Hash(seed ^ 0x4D, DungeonKit.Local(p, on)) < 0.3;
                                Grow(grown, p, mushroom ? OvergrowthKind.Mushrooms : OvergrowthKind.GrassTuft, on, Turn(p, seed, on),
                                    Chance(damp, mushroom ? MushroomsFrom : GrassFrom), seed, mushroom ? MushroomScale : GrassScale);
                            }
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

        // Flowstone (#10305; owner, 2026-10-23: drip on the stonework) coating the wettest walls from under the cove, between
        // the drapes.
        foreach (float along in FlowstoneAt)
        {
            if ((door || feature) && MathF.Abs(along) < OpeningHalf)
            {
                continue;
            }

            float y = ceiling - DrapeUnder - FlowstoneUnder;
            Grow(grown, p, OvergrowthKind.Flowstone, new Vector3(along, y, WallFace(along, y, ceiling) - FlowstoneSink), PanelNormal(along), Chance(damp, FlowstoneFrom), seed,
                RuinFlowstoneScale);
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

    private static void Floor(List<OvergrowthPlacement> grown, KitPlacement p, float damp, ulong seed, IReadOnlyList<Vector3> walls)
    {
        bool Mossy(Vector3 local, float margin = 0f) => KitMoss.Strength(p, local, Vector3.UnitY, 1f) >= KitMoss.StainThreshold + margin;
        // Whether the floor's edge on the far side from inward (its own frame) stands under a wall facing into the room.
        bool Walled(Vector3 inward) => walls.Any(w => Vector3.Dot(w, DungeonKit.Turned(p, inward)) > 0.9f);
        Vector3[] sides = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ];
        bool[] walled = [.. sides.Select(Walled)];

        // Grass along each edge, facing into the room (a strip runs along its x): a wall's foot in full, a seam sparsely.
        for (int side = 0; side < sides.Length; side++)
        {
            Vector3 inward = sides[side], along = new(inward.Z, 0f, -inward.X);
            foreach (float t in StripAt)
            {
                Vector3 on = (along * t) - (inward * (RoomHalf - StripIn)) + (Vector3.UnitY * FloorTop);
                if (Mossy(on))
                {
                    Grow(grown, p, OvergrowthKind.GrassStrip, on, inward, (walled[side] ? 1f : SeamShare) * Chance(damp, GrassFrom), seed, GrassScale);
                }
            }
        }

        // Tufts: near a walled edge in full, out on the open floor only in its strongest moss, sparsely.
        for (float x = -RoomHalf + 0.4f; x <= RoomHalf - 0.4f; x += TuftStep)
            for (float z = -RoomHalf + 0.4f; z <= RoomHalf - 0.4f; z += TuftStep)
            {
                Vector3 at = DungeonKit.Local(p, new Vector3(x, 0f, z));
                Vector3 on = new(x + (((float)Hash(seed ^ 0x71, at) - 0.5f) * 2f * TuftJitter), FloorTop, z + (((float)Hash(seed ^ 0x72, at) - 0.5f) * 2f * TuftJitter));
                bool byWall = Enumerable.Range(0, sides.Length).Any(side => walled[side] && RoomHalf + Vector3.Dot(on, sides[side]) <= TuftEdge);
                if (byWall ? Mossy(on) : Mossy(on, OpenMargin))
                {
                    Grow(grown, p, OvergrowthKind.GrassTuft, on, Turn(p, seed, on), (byWall ? 1f : OpenShare) * Chance(damp, GrassFrom), seed, GrassScale);
                }
            }

        // Mushrooms at a wall's foot beside the room's corners (the corner itself holds the rib's mound), more often where
        // the next wall meets it.
        for (int side = 0; side < sides.Length; side++)
        {
            Vector3 inward = sides[side], along = new(inward.Z, 0f, -inward.X);
            foreach (float t in new[] { -MushroomAlong, MushroomAlong })
            {
                int next = Array.IndexOf(sides, -along * MathF.Sign(t));
                float share = !walled[side] ? 0f : walled[next] ? CornerShare : AlongShare;
                Vector3 on = (along * t) - (inward * (RoomHalf - MushroomIn)) + (Vector3.UnitY * FloorTop);
                if (share > 0f && Mossy(on))
                {
                    Grow(grown, p, OvergrowthKind.Mushrooms, on, Turn(p, seed, on), share * Chance(damp, MushroomsFrom), seed, MushroomScale);
                }
            }
        }
    }

    /// <summary>
    /// The drip under a wet vault (#10305; owner, 2026-10-23: straws and small stalactites where water drips on the
    /// stonework): patches of soda straws hung from its underside off its ribs, and on the wettest a few small stalactites.
    /// </summary>
    private static void Vault(List<OvergrowthPlacement> grown, KitPlacement p, float damp, ulong seed)
    {
        foreach ((float x, float z) in DripAt)
        {
            Vector3 under = new(x, VaultUnder(x, z) + DripSink, z);
            Grow(grown, p, OvergrowthKind.Straws, under, Turn(p, seed, under), Chance(damp, StrawsFrom), seed, RuinStrawsScale);
            Vector3 beside = under + new Vector3(StalactiteBeside, 0f, 0f);
            Grow(grown, p, OvergrowthKind.Stalactite, beside with { Y = VaultUnder(beside.X, z) + DripSink }, Turn(p, seed, beside),
                StalactiteShare * Chance(damp, StalactitesFrom), seed, RuinStalactiteScale);
        }
    }

    /// <summary>A storey's vault underside over a point of its cell (kit_authored.py's vault_front, without its ribs and boss).</summary>
    private static float VaultUnder(float x, float z) => Ceiling - VaultDrop + (VaultRise * MathF.Cos(0.5f * MathF.PI * x / Inner) * MathF.Cos(0.5f * MathF.PI * z / Inner));

    /// <summary>
    /// What fell on a floor (#10305): debris of broken stonework in and round a cell with a broken piece, sparsely elsewhere,
    /// and gravel the water laid on a wet floor.
    /// </summary>
    private static void Fallen(List<OvergrowthPlacement> grown, KitPlacement p, float damp, ulong seed, bool nearBroken)
    {
        foreach (Vector3 spot in FallenAt)
        {
            Vector3 on = spot + (Vector3.UnitY * FloorTop);
            Grow(grown, p, OvergrowthKind.Debris, on, Turn(p, seed, on), nearBroken ? DebrisNearBroken : DebrisElsewhere, seed);
            Vector3 laid = new Vector3(-spot.Z, 0f, spot.X) + (Vector3.UnitY * FloorTop);
            Grow(grown, p, OvergrowthKind.Gravel, laid, Turn(p, seed, laid), GravelShare * Chance(damp, GravelFrom), seed);
        }
    }

    // The ruins' drip and debris (#10305; owner, 2026-10-23: read more strongly, bigger and denser): straws hung at these
    // places under a vault (off its diagonal ribs), each sunk so far into it, from this dampness, at this size; a small
    // stalactite so far beside a patch, this share of the time on the wetter vaults, at this size. Flowstone coats a wall at
    // these places along it, so far under the drapes' head and sunk into the panel, from this dampness, at this size. Debris lies at these places on a floor: this likely within BrokenNear of a broken piece, this
    // elsewhere; gravel beside it, this share of the time on wet floors.
    private static readonly (float X, float Z)[] DripAt = [(1.5f, 0.4f), (-1.5f, -0.4f), (0.4f, -1.5f), (-0.4f, 1.5f), (0f, 0.9f), (0f, -0.9f), (0.9f, 0f),
        (-0.9f, 0f), (2.2f, -0.6f), (-2.2f, 0.6f), (0.6f, 2.2f), (-0.6f, -2.2f)];
    private const float DripSink = 0.08f, StrawsFrom = 0.5f, RuinStrawsScale = 2f, StalactiteBeside = 0.45f, StalactiteShare = 1f, StalactitesFrom = 0.65f;
    private const float RuinStalactiteScale = 0.85f, RuinFlowstoneScale = 1.2f;
    private const float VaultDrop = 0.04f, VaultRise = 0.55f;
    private static readonly float[] FlowstoneAt = [-1.8f, -0.6f, 0.6f, 1.8f];
    private const float FlowstoneUnder = 0.15f, FlowstoneSink = 0.03f, FlowstoneFrom = 0.7f;
    private static readonly Vector3[] FallenAt = [new(1.4f, 0f, 1.1f), new(-1.2f, 0f, -1.5f)];
    private const float BrokenNear = 7f, DebrisNearBroken = 0.75f, DebrisElsewhere = 0.12f, GravelShare = 0.5f, GravelFrom = 0.8f;

    /// <summary>A kit cell, by a piece's origin (its pieces share it).</summary>
    private static (int, int, int) Cell(Vector3 origin) => ((int)MathF.Round(origin.X), (int)MathF.Round(origin.Y), (int)MathF.Round(origin.Z));

    /// <summary>A floor growth's turn, by the hash (a floor piece's +z is only a turn).</summary>
    private static Vector3 Turn(KitPlacement p, ulong seed, Vector3 local)
    {
        float a = 2f * MathF.PI * (float)Hash(seed ^ 0x7A, DungeonKit.Local(p, local));
        return new Vector3(MathF.Sin(a), 0f, MathF.Cos(a));
    }

    /// <summary>One overgrowth at a point of a piece (its own frame) facing <paramref name="facing"/> there, with the given chance.</summary>
    private static void Grow(List<OvergrowthPlacement> grown, KitPlacement p, OvergrowthKind kind, Vector3 local, Vector3 facing, float chance, ulong seed,
        float size = 1f)
    {
        Vector3 at = DungeonKit.Local(p, local);
        if (Hash(seed ^ (ulong)kind, at) >= chance || (KarstCave.StudyCarvedFormations && OvergrowthPlacement.Formation(kind)))
        {
            return;
        }

        float pick = (float)Hash(seed ^ 0x5CA1E, at);
        grown.Add(new OvergrowthPlacement(kind, at, DungeonKit.Turned(p, facing), size * (LeastScale + (ScaleRange * pick)), (float)Hash(seed ^ 0x7157, at)));
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
