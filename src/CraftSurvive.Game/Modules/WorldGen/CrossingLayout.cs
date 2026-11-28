using System.Numerics;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>Construction material role for one part of a parametric bridge mesh (#11186).</summary>
internal enum CrossingMaterial : byte
{
    Planks = 0,
    Timber = 1,
    Masonry = 2,
}

/// <summary>
/// One oriented box part of a bridge in bridge-local coordinates: origin at bridge midpoint on the
/// deck's top walking surface, +Z from <see cref="CrossingSite.FromX"/>/<see cref="CrossingSite.FromZ"/>
/// toward <see cref="CrossingSite.ToX"/>/<see cref="CrossingSite.ToZ"/>, +X across width, +Y up.
/// Positive <see cref="Pitch"/> rotates around +X, tipping the +Z end downward (matching <c>BoxMesh</c>).
/// </summary>
internal readonly record struct CrossingBox(
    CrossingMaterial Material,
    Vector3 Centre,
    Vector3 Half,
    float Pitch = 0f,
    bool Collidable = true);

/// <summary>
/// Complete pure-geometry description of one river bridge (#11186), independent of Engine resources
/// so unit checks can verify walkability, bounds, orientation, and ramp grades directly.
/// </summary>
internal sealed record CrossingBridgeGeometry(
    Vector3 WorldAnchor,
    Quaternion Rotation,
    double SpanMetres,
    double DeckWidth,
    double RampRun,
    float NearRampPitch,
    float FarRampPitch,
    CrossingBox[] Boxes);

/// <summary>
/// Pure parametric bridge geometry (#11186): builds a textured, walkable timber footbridge or
/// stone-and-timber bridge oriented perpendicularly across any river channel, with seamless sloped
/// approach ramps to both banks, deep bank abutments, riverbed piers, and side rails/parapets.
/// </summary>
internal static class CrossingLayout
{
    /// <summary>Offset from voxel integer height to its top walking face (matching PlaceMeshes).</summary>
    internal const double GroundTopOffset = 1.0;

    internal const float TimberDeckWidth = 3.0f;
    internal const float StoneDeckWidth = 3.6f;
    internal const float DeckThickness = 0.28f;
    internal const float DeckOverhang = 0.30f;

    internal const float RampRun = 3.2f;
    internal const float RampThickness = 0.32f;
    internal const float RampMinimumDrop = 0.20f;
    internal const float RampMaximumDrop = 1.50f;
    internal const float RampToeEmbed = 0.15f;

    internal const float AbutmentDepth = 3.5f;
    internal const float AbutmentLength = 1.4f;

    internal const float StringerWidth = 0.24f;
    internal const float StringerHeight = 0.30f;

    internal const float PierSpacing = 4.5f;
    internal const float PierDepth = 5.5f;
    internal const float TimberPierSide = 0.34f;
    internal const float StonePierLength = 0.85f;

    internal const float TimberPostSpacing = 2.6f;
    internal const float TimberPostSide = 0.16f;
    internal const float TimberPostHeight = 0.90f;
    internal const float TimberRailHeight = 0.12f;
    internal const float TimberRailWidth = 0.14f;
    internal const float TimberRailElevation = 0.80f;

    internal const float StoneParapetWidth = 0.30f;
    internal const float StoneParapetHeight = 0.56f;
    internal const float StoneBollardWidth = 0.42f;
    internal const float StoneBollardHeight = 0.86f;

    internal static CrossingBridgeGeometry Build(CrossingSite site, double? nearBankTop = null, double? farBankTop = null)
    {
        double fromX = site.FromX + 0.5;
        double fromZ = site.FromZ + 0.5;
        double toX = site.ToX + 0.5;
        double toZ = site.ToZ + 0.5;
        double dx = toX - fromX;
        double dz = toZ - fromZ;
        double span = Math.Max(1.0, double.Hypot(dx, dz));
        float halfSpan = (float)(span * 0.5);

        double deckTopY = site.DeckY + GroundTopOffset;
        double nearTop = nearBankTop ?? (site.EffectiveNearBankY + GroundTopOffset);
        double farTop = farBankTop ?? (site.EffectiveFarBankY + GroundTopOffset);

        Vector3 anchor = new((float)((fromX + toX) * 0.5), (float)deckTopY, (float)((fromZ + toZ) * 0.5));
        float yaw = MathF.Atan2((float)dx, (float)dz);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);

        bool stone = site.Style == CrossingStyle.StoneBridge;
        float deckWidth = stone ? StoneDeckWidth : TimberDeckWidth;
        float halfWidth = deckWidth * 0.5f;
        CrossingMaterial deckMaterial = stone ? CrossingMaterial.Masonry : CrossingMaterial.Planks;
        CrossingMaterial pierMaterial = stone ? CrossingMaterial.Masonry : CrossingMaterial.Timber;

        List<CrossingBox> boxes = new(24);

        // 1. Main level deck: top surface at local y = 0.
        float deckLength = (float)span + (DeckOverhang * 2f);
        boxes.Add(new CrossingBox(
            deckMaterial,
            new Vector3(0f, -DeckThickness * 0.5f, 0f),
            new Vector3(halfWidth, DeckThickness * 0.5f, deckLength * 0.5f)));

        // 2. Seamless sloped approach ramps at both banks.
        float nearDrop = (float)Math.Clamp(deckTopY - nearTop + RampToeEmbed, RampMinimumDrop, RampMaximumDrop);
        float farDrop = (float)Math.Clamp(deckTopY - farTop + RampToeEmbed, RampMinimumDrop, RampMaximumDrop);
        float nearAngle = MathF.Atan2(nearDrop, RampRun);
        float farAngle = MathF.Atan2(farDrop, RampRun);
        float nearLength = MathF.Sqrt((RampRun * RampRun) + (nearDrop * nearDrop));
        float farLength = MathF.Sqrt((RampRun * RampRun) + (farDrop * farDrop));

        // Near ramp (-Z): negative pitch tips the -Z outer toe downward while meeting y = 0 at z = -halfSpan.
        Vector3 nearTopMid = new(0f, -nearDrop * 0.5f, -(halfSpan + (RampRun * 0.5f)));
        Vector3 nearDown = new(0f, -MathF.Cos(nearAngle), MathF.Sin(nearAngle));
        boxes.Add(new CrossingBox(
            deckMaterial,
            nearTopMid + (nearDown * (RampThickness * 0.5f)),
            new Vector3(halfWidth, RampThickness * 0.5f, nearLength * 0.5f),
            Pitch: -nearAngle));

        // Far ramp (+Z): positive pitch tips the +Z outer toe downward while meeting y = 0 at z = +halfSpan.
        Vector3 farTopMid = new(0f, -farDrop * 0.5f, halfSpan + (RampRun * 0.5f));
        Vector3 farDown = new(0f, -MathF.Cos(farAngle), -MathF.Sin(farAngle));
        boxes.Add(new CrossingBox(
            deckMaterial,
            farTopMid + (farDown * (RampThickness * 0.5f)),
            new Vector3(halfWidth, RampThickness * 0.5f, farLength * 0.5f),
            Pitch: farAngle));

        // 3. Solid abutments seated deep into both riverbanks.
        float abutmentY = -DeckThickness - (AbutmentDepth * 0.5f);
        float abutmentZ = halfSpan + (AbutmentLength * 0.4f);
        boxes.Add(new CrossingBox(
            CrossingMaterial.Masonry,
            new Vector3(0f, abutmentY, -abutmentZ),
            new Vector3(halfWidth + 0.12f, AbutmentDepth * 0.5f, AbutmentLength * 0.5f)));
        boxes.Add(new CrossingBox(
            CrossingMaterial.Masonry,
            new Vector3(0f, abutmentY, abutmentZ),
            new Vector3(halfWidth + 0.12f, AbutmentDepth * 0.5f, AbutmentLength * 0.5f)));

        // 4. Longitudinal under-deck stringers.
        float stringerX = halfWidth - 0.36f;
        float stringerY = -DeckThickness - (StringerHeight * 0.5f);
        boxes.Add(new CrossingBox(
            CrossingMaterial.Timber,
            new Vector3(-stringerX, stringerY, 0f),
            new Vector3(StringerWidth * 0.5f, StringerHeight * 0.5f, deckLength * 0.5f)));
        boxes.Add(new CrossingBox(
            CrossingMaterial.Timber,
            new Vector3(stringerX, stringerY, 0f),
            new Vector3(StringerWidth * 0.5f, StringerHeight * 0.5f, deckLength * 0.5f)));

        // 5. Riverbed piers along the span.
        int pierCount = span >= 5.0 ? Math.Max(1, (int)Math.Floor(span / PierSpacing)) : 0;
        float pierTop = -DeckThickness - StringerHeight;
        float pierCentreY = pierTop - (PierDepth * 0.5f);
        for (int i = 0; i < pierCount; i++)
        {
            double frac = (i + 1.0) / (pierCount + 1.0);
            float pz = (float)((frac - 0.5) * span);
            if (stone)
            {
                boxes.Add(new CrossingBox(
                    pierMaterial,
                    new Vector3(0f, pierCentreY, pz),
                    new Vector3(halfWidth - 0.18f, PierDepth * 0.5f, StonePierLength * 0.5f)));
            }
            else
            {
                float postX = halfWidth - 0.36f;
                boxes.Add(new CrossingBox(
                    pierMaterial,
                    new Vector3(-postX, pierCentreY, pz),
                    new Vector3(TimberPierSide * 0.5f, PierDepth * 0.5f, TimberPierSide * 0.5f)));
                boxes.Add(new CrossingBox(
                    pierMaterial,
                    new Vector3(postX, pierCentreY, pz),
                    new Vector3(TimberPierSide * 0.5f, PierDepth * 0.5f, TimberPierSide * 0.5f)));
            }
        }

        // 6. Side railings (timber footbridge) or low masonry parapets + corner bollards (stone bridge).
        if (stone)
        {
            float parapetX = halfWidth - (StoneParapetWidth * 0.5f);
            foreach (float side in new[] { -1f, 1f })
            {
                boxes.Add(new CrossingBox(
                    CrossingMaterial.Masonry,
                    new Vector3(side * parapetX, StoneParapetHeight * 0.5f, 0f),
                    new Vector3(StoneParapetWidth * 0.5f, StoneParapetHeight * 0.5f, halfSpan)));
                foreach (float end in new[] { -1f, 1f })
                {
                    boxes.Add(new CrossingBox(
                        CrossingMaterial.Masonry,
                        new Vector3(side * parapetX, StoneBollardHeight * 0.5f, end * halfSpan),
                        new Vector3(StoneBollardWidth * 0.5f, StoneBollardHeight * 0.5f, StoneBollardWidth * 0.5f)));
                }
            }
        }
        else
        {
            float railX = halfWidth - (TimberPostSide * 0.5f) - 0.04f;
            int postSegments = Math.Max(2, (int)Math.Ceiling(span / TimberPostSpacing));
            foreach (float side in new[] { -1f, 1f })
            {
                boxes.Add(new CrossingBox(
                    CrossingMaterial.Timber,
                    new Vector3(side * railX, TimberRailElevation, 0f),
                    new Vector3(TimberRailWidth * 0.5f, TimberRailHeight * 0.5f, halfSpan + 0.1f)));
                for (int p = 0; p <= postSegments; p++)
                {
                    float pz = -halfSpan + ((float)p / postSegments * (float)span);
                    boxes.Add(new CrossingBox(
                        CrossingMaterial.Timber,
                        new Vector3(side * railX, TimberPostHeight * 0.5f, pz),
                        new Vector3(TimberPostSide * 0.5f, TimberPostHeight * 0.5f, TimberPostSide * 0.5f)));
                }
            }
        }

        return new CrossingBridgeGeometry(anchor, rotation, span, deckWidth, RampRun, -nearAngle, farAngle, [.. boxes]);
    }
}
