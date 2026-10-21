using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The moss cards on a built dungeon's kit (#10092): the places' painted mats, cushions and curtains (#10044), grown where
/// the kit's damp stain is strongest - the same moss field kit-stone.wgsl reads, times each placement's dampness and its
/// rising damp - so every card sits on a stain that fades out round it. Mats lie on wet floors and rubble, cushions stand
/// at wet walls' feet, curtains hang from wet ledges' lips. Positions are layout metres, as a cave's cards are.
/// </summary>
internal static class KitMoss
{
    // As kit-stone.wgsl: the moss strength's threshold (SurfaceMoss: 0.74 less 0.09 per strength, at strength 2) and the
    // kit's moisture scale; a card grows this far over the threshold, where the stain is full.
    private const float Threshold = 0.74f - (2f * 0.09f), MoistureScale = 1.7f, CardMargin = 0.07f;

    // Where the cards are tried: the spacing along a floor, a wall's foot and a ledge's lip, in metres.
    private const float FloorStep = 0.7f, FootStep = 0.6f, LipStep = 0.55f;

    // The kit's frame: a floor's top, a room's half width less a margin, the wall's panel (its face at the middle curves back
    // this far from the room's edge, over this half width) and its plinth's depth, a ledge's lip.
    private const float FloorTop = 1.03f, RoomHalf = 2.6f, Inner = 3f, PanelBack = 0.5f, PanelHalf = 2.4f, Plinth = 0.12f;
    private const float LipOut = -0.14f, LipHeight = 1f;

    // Ivy where the cave breaks in (the breach): curtains hang from the broken lintel's front every so far along it, in
    // chains of so many links so far apart, the lowest well above the floor; this share of the spots carry a chain.
    private const float IvyStep = 0.7f, IvyLink = 1f, IvyLowest = 2.6f;
    private const int IvyChain = 2;
    private const double IvyShare = 0.35;

    /// <summary>The most cards a dungeon grows (as a cave's).</summary>
    private const int MostCards = 1200;

    internal static IReadOnlyList<MossCard> Cards(IReadOnlyList<KitPlacement> kit, ulong seed)
    {
        List<MossCard> cards = [];
        foreach (KitPlacement p in kit)
        {
            if (cards.Count >= MostCards)
            {
                break;
            }

            switch (p.Piece)
            {
                case KitPiece.Floor or KitPiece.FloorHeaved:
                    for (float x = -RoomHalf; x <= RoomHalf; x += FloorStep)
                        for (float z = -RoomHalf; z <= RoomHalf; z += FloorStep)
                        {
                            Try(cards, p, MossCardKind.Mat, new Vector3(x, FloorTop, z), Vector3.Zero, Vector3.UnitY, Vector3.UnitY, 1f, seed);
                        }

                    break;
                case KitPiece.RubbleHeap:
                    for (float x = 0.2f; x <= 1.8f; x += FloorStep)
                        for (float z = 0.2f; z <= 1.8f; z += FloorStep)
                        {
                            Try(cards, p, MossCardKind.Mat, new Vector3(x, 2.15f, z), Vector3.Zero, Vector3.UnitY, Vector3.UnitY, 1f, seed);
                        }

                    break;
                case KitPiece.Wall or KitPiece.WallDoor or KitPiece.WallAlcove or KitPiece.WallRoundel or KitPiece.WallTall
                    or KitPiece.WallTallDoor or KitPiece.WallTallRoundel:
                    for (float x = -PanelHalf + 0.3f; x <= PanelHalf - 0.3f; x += FootStep)
                    {
                        // A door's foot is its opening.
                        if (p.Piece is KitPiece.WallDoor or KitPiece.WallTallDoor && MathF.Abs(x) < 1.2f)
                        {
                            continue;
                        }

                        // The panel's face at the foot, the plinth proud of it; the cushion stands on the floor before it, the field
                        // read on the wall a little above the floor (its rising damp near full).
                        float face = -Inner - (PanelBack * MathF.Cos(0.5f * MathF.PI * x / PanelHalf)) + Plinth;
                        Try(cards, p, MossCardKind.Cushion, new Vector3(x, FloorTop, face + 0.3f), Vector3.UnitZ, Vector3.UnitY,
                            Vector3.UnitZ, 0.95f, seed, new Vector3(x, 1.3f, face));
                    }

                    break;
                case KitPiece.WallBreach:
                    for (float x = -PanelHalf - 0.2f; x <= PanelHalf + 0.2f; x += IvyStep)
                    {
                        // The lintel's ragged lower edge (kit_authored.py's breach_break) and the panel's face there.
                        float edge = 4.2f + (0.22f * MathF.Sin((2.3f * x) + 0.7f)) + (0.1f * MathF.Sin((5.3f * x) + 2f));
                        float face = MathF.Abs(x) >= PanelHalf ? -Inner + 0.15f : -Inner - (PanelBack * MathF.Cos(0.5f * MathF.PI * x / PanelHalf));
                        Vector3 top = DungeonKit.Local(p, new Vector3(x, edge, face + 0.03f));
                        if (Hash(seed, top) > IvyShare)
                        {
                            continue;
                        }

                        Vector3 outward = DungeonKit.Turned(p, Vector3.UnitZ);
                        // Some chains are a single curtain, so the ivy trails rather than walls the opening off.
                        int links = Hash(seed ^ 0x5EEDUL, top) < 0.5 ? 1 : IvyChain;
                        for (int link = 0; link < links && edge - (IvyLink * link) >= IvyLowest; link++)
                        {
                            Vector3 at = top - (Vector3.UnitY * (IvyLink * link));
                            cards.Add(new MossCard(MossCardKind.Curtain, at, outward, outward, (float)Hash(seed ^ (ulong)link, at)));
                        }
                    }

                    break;
                case KitPiece.Ledge1 or KitPiece.Ledge2 or KitPiece.Ledge4:
                    for (float x = -1.8f; x <= 1.8f; x += LipStep / Math.Max(p.Stretch.X, 0.25f))
                    {
                        Try(cards, p, MossCardKind.Curtain, new Vector3(x, LipHeight, LipOut), -Vector3.UnitZ, Vector3.UnitY, Vector3.UnitY, 1f, seed,
                            new Vector3(x, FloorTop, 0.2f));
                    }

                    break;
            }
        }

        return cards;
    }

    /// <summary>
    /// A card at a point of a piece (its own frame) where the moss is strongest there: the field read at <paramref name="readAt"/>
    /// (else the card's point) on a surface facing <paramref name="facing"/>, times the piece's dampness and the rising damp.
    /// </summary>
    private static void Try(List<MossCard> cards, KitPlacement p, MossCardKind kind, Vector3 local, Vector3 outward, Vector3 normal, Vector3 facing,
        float rising, ulong seed, Vector3? readAt = null)
    {
        Vector3 at = DungeonKit.Local(p, local);
        Vector3 read = DungeonCollision.InSession(DungeonKit.Local(p, readAt ?? local));
        Vector3 n = DungeonKit.Turned(p, facing);
        float moisture = p.Wet * rising * MoistureScale;
        float m = SurfaceMoss.FieldAt((0, 0, 0), read.X, read.Y, read.Z, n, SurfaceMoss.PlaceGrain, moisture);
        if (m < Threshold + CardMargin)
        {
            return;
        }

        float pick = (float)Hash(seed, at);
        cards.Add(new MossCard(kind, at, outward == Vector3.Zero ? Vector3.Zero : DungeonKit.Turned(p, outward), DungeonKit.Turned(p, normal), pick));
    }

    private static double Hash(ulong seed, Vector3 at)
    {
        ulong h = seed ^ 0x4B4D6F7373UL ^ ((ulong)(uint)(int)MathF.Floor(at.X * 10) * 0x9E3779B97F4A7C15UL)
            ^ ((ulong)(uint)(int)MathF.Floor(at.Y * 10) * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)(uint)(int)MathF.Floor(at.Z * 10) * 0x165667B19E3779F9UL);
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return (h >> 11) / (double)(1UL << 53);
    }
}
