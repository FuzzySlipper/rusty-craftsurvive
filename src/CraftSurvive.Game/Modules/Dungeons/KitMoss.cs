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
    private const float FloorStep = 0.9f, FootStep = 0.6f, LipStep = 0.55f;

    // A mat grows only where water gathers - near a wall's foot or in a corner, no farther from the room's edge than this -
    // well inside a patch (this far over the threshold, the field over it this far round it each way), at most so many to
    // a floor: a wet floor's stain is its moss carpet, and mats strewn over it read as dots.
    private const float MatMargin = 0.16f, PatchReach = 0.7f, MatEdge = 1.0f;
    private const int MatsPerFloor = 2;

    // The kit's frame: a floor's top, a room's half width less a margin, the wall's panel (its face at the middle curves back
    // this far from the room's edge, over this half width) and its plinth's depth, a ledge's lip.
    private const float FloorTop = 1.03f, RoomHalf = 2.6f, Inner = 3f, PanelBack = 0.5f, PanelHalf = 2.4f, Plinth = 0.12f;
    private const float LipOut = -0.14f, LipHeight = 1f;

    // Ivy where the cave breaks in (the breach): curtains lie on the broken lintel's face from near its top, so only their
    // ends trail past its edge, every so far along it; this share of the spots carry one.
    private const float IvyStep = 0.6f, LintelTop = 5.0f;
    private const double IvyShare = 0.7;

    // Ivy down the wettest walls (by the caves): curtains hung from the cove's overhang (it hides their top edge, as a lip
    // does), every so far along the wall; a wall takes them from this dampness, more of them the wetter. The cove's lip
    // (kit_authored.py's wall): its height, and how far out from the room's edge it stands.
    private const float WallIvyFrom = 1.2f, WallIvyStep = 0.7f, CoveLipHeight = 4.95f, CoveLipOut = 2.38f;

    /// <summary>
    /// How damp a piece grows for its moisture (0..1): as it is up to <see cref="OvergrownFrom"/>, then rising much faster,
    /// to 2 by the caves, so the wettest rooms are overgrown and the drier stay calm. kit-stone.wgsl reads it from the
    /// placement's tint (DungeonKitLook), and the cards are grown from it.
    /// </summary>
    internal static float Dampness(float wet) => wet < OvergrownFrom ? wet : OvergrownFrom + ((wet - OvergrownFrom) * OvergrownRise);

    /// <summary>The highest dampness <see cref="Dampness"/> gives.</summary>
    internal const float MostDampness = 2f;

    private const float OvergrownFrom = 0.6f, OvergrownRise = 3.5f;

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
                    int mats = cards.Count;
                    for (float x = -RoomHalf; x <= RoomHalf; x += FloorStep)
                        for (float z = -RoomHalf; z <= RoomHalf; z += FloorStep)
                        {
                            bool edge = RoomHalf - Math.Max(MathF.Abs(x), MathF.Abs(z)) <= MatEdge;
                            if (edge && cards.Count - mats < MatsPerFloor && Patch(p, new Vector3(x, FloorTop, z)))
                            {
                                Try(cards, p, MossCardKind.Mat, new Vector3(x, FloorTop, z), Vector3.Zero, Vector3.UnitY, Vector3.UnitY, 1f, seed, margin: MatMargin);
                            }
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
                    // Ivy down the wettest walls, flat on the panel under the cove.
                    float damp = Dampness(p.Wet);
                    for (float x = -PanelHalf + 0.2f; damp >= WallIvyFrom && x <= PanelHalf - 0.2f; x += WallIvyStep)
                    {
                        if (p.Piece is KitPiece.WallDoor or KitPiece.WallTallDoor && MathF.Abs(x) < 1.4f)
                        {
                            continue;
                        }

                        Vector3 top = DungeonKit.Local(p, new Vector3(x, CoveLipHeight, -CoveLipOut));
                        if (Hash(seed, top) > (damp - WallIvyFrom) / (MostDampness - WallIvyFrom))
                        {
                            continue;
                        }

                        Vector3 outward = DungeonKit.Turned(p, Vector3.UnitZ);
                        cards.Add(new MossCard(MossCardKind.Curtain, top, outward, outward, (float)Hash(seed ^ 0x11UL, top)));
                    }

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
                        // On the lintel's face from near its top (above the ragged edge): the curtain lies on the stone and
                        // only its end trails past the break, never hanging alone in the opening.
                        Vector3 top = DungeonKit.Local(p, new Vector3(x, LintelTop - (0.25f * (float)Hash(seed ^ 0x70UL, new Vector3(x, 0f, 0f))), face));
                        if (edge > LintelTop - 0.3f || Hash(seed, top) > IvyShare)
                        {
                            continue;
                        }

                        Vector3 outward = DungeonKit.Turned(p, Vector3.UnitZ);
                        cards.Add(new MossCard(MossCardKind.Curtain, top, outward, outward, (float)Hash(seed ^ 0x1717UL, top)));
                    }

                    // Cushions at the broken pilasters' feet.
                    foreach (float side in new[] { -1f, 1f })
                    {
                        Try(cards, p, MossCardKind.Cushion, new Vector3(side * (Inner - 0.3f), FloorTop, -Inner + 0.45f), Vector3.UnitZ, Vector3.UnitY,
                            Vector3.UnitZ, 1f, seed, new Vector3(side * (Inner - 0.3f), 1.3f, -Inner + 0.15f));
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
    /// <summary>Whether the moss holds all round a floor point: the field over the threshold <see cref="PatchReach"/> away each way.</summary>
    private static bool Patch(KitPlacement p, Vector3 local) =>
        new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ }.All(d => Strength(p, local + (d * PatchReach), Vector3.UnitY, 1f) >= Threshold);

    /// <summary>The moss at a point of a piece (its own frame) on a surface facing <paramref name="facing"/>, as kit-stone.wgsl reads it.</summary>
    private static float Strength(KitPlacement p, Vector3 local, Vector3 facing, float rising)
    {
        Vector3 read = DungeonCollision.InSession(DungeonKit.Local(p, local));
        return SurfaceMoss.FieldAt((0, 0, 0), read.X, read.Y, read.Z, DungeonKit.Turned(p, facing), SurfaceMoss.PlaceGrain, Dampness(p.Wet) * rising * MoistureScale);
    }

    private static void Try(List<MossCard> cards, KitPlacement p, MossCardKind kind, Vector3 local, Vector3 outward, Vector3 normal, Vector3 facing,
        float rising, ulong seed, Vector3? readAt = null, float margin = CardMargin)
    {
        Vector3 at = DungeonKit.Local(p, local);
        Vector3 read = DungeonCollision.InSession(DungeonKit.Local(p, readAt ?? local));
        Vector3 n = DungeonKit.Turned(p, facing);
        float moisture = Dampness(p.Wet) * rising * MoistureScale;
        float m = SurfaceMoss.FieldAt((0, 0, 0), read.X, read.Y, read.Z, n, SurfaceMoss.PlaceGrain, moisture);
        if (m < Threshold + margin)
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
