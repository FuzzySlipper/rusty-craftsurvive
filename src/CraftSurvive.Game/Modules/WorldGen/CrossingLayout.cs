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
/// so unit checks can verify walkability, bounds, orientation, and toe-to-ground continuity directly.
/// </summary>
internal sealed record CrossingBridgeGeometry(
    Vector3 WorldAnchor,
    Quaternion Rotation,
    double SpanMetres,
    double DeckWidth,
    double NearRampRun,
    double FarRampRun,
    float NearRampPitch,
    float FarRampPitch,
    Vector3 NearToeWorld,
    Vector3 FarToeWorld,
    CrossingBox[] Boxes);

/// <summary>
/// Pure parametric bridge geometry (#11186): builds a textured, walkable timber footbridge or
/// stone-and-timber bridge oriented perpendicularly across any river channel, with adaptive sloped
/// approach ramps that always meet both banks' actual terrain at a walkable grade (whether below,
/// level with, or rising above the deck), deep bank abutments, riverbed piers, and side rails/parapets.
/// </summary>
internal static class CrossingLayout
{
    /// <summary>Offset from voxel integer height to its top walking face (matching PlaceMeshes).</summary>
    internal const double GroundTopOffset = 1.0;

    internal const float TimberDeckWidth = 3.0f;
    internal const float StoneDeckWidth = 3.6f;
    internal const float DeckThickness = 0.28f;
    internal const float DeckOverhang = 0.30f;

    internal const float MinimumRampRun = 3.2f;
    internal const float MaximumRampRun = 14.0f;
    internal const float RampSearchStep = 0.5f;
    /// <summary>Maximum target approach slope (rise/run ≈ 24.2°), comfortably walkable in both directions.</summary>
    internal const float MaximumWalkableGrade = 0.45f;
    internal const float RampThickness = 0.32f;
    /// <summary>Embeds the outer ramp toe slightly into the bank so walking onto the ramp has no lip.</summary>
    internal const float RampToeEmbed = 0.15f;

    internal const float AbutmentDepth = 3.5f;
    internal const float AbutmentLength = 1.4f;
    internal const float MidRampSupportThreshold = 5.5f;
    internal const float MidRampSupportLength = 0.9f;

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

    /// <summary>Horizontal run needed to connect <paramref name="deckTopY"/> to <paramref name="bankTopY"/> at a walkable grade.</summary>
    internal static double RampRunFor(double deckTopY, double bankTopY)
    {
        double targetDelta = (bankTopY - RampToeEmbed) - deckTopY;
        return Math.Clamp(Math.Abs(targetDelta) / MaximumWalkableGrade, MinimumRampRun, MaximumRampRun);
    }

    /// <summary>
    /// Builds bridge geometry from fixed near/far bank heights (used when no continuous terrain sampler is supplied).
    /// </summary>
    internal static CrossingBridgeGeometry Build(CrossingSite site, double? nearBankTop = null, double? farBankTop = null)
    {
        double nearTop = nearBankTop ?? (site.EffectiveNearBankY + GroundTopOffset);
        double farTop = farBankTop ?? (site.EffectiveFarBankY + GroundTopOffset);
        double deckTopY = site.DeckY + GroundTopOffset;
        double nearRun = RampRunFor(deckTopY, nearTop);
        double farRun = RampRunFor(deckTopY, farTop);
        return BuildWithRamps(site, nearRun, nearTop - RampToeEmbed - deckTopY, farRun, farTop - RampToeEmbed - deckTopY);
    }

    /// <summary>
    /// Builds bridge geometry by solving each approach ramp's landing distance against the live continuous
    /// <paramref name="groundTop"/> sampler so every ramp toe meets the actual terrain surface at a walkable
    /// grade (handling unequal banks up to 4+ m, rising banks above the deck, and sloping valley walls).
    /// </summary>
    internal static CrossingBridgeGeometry Build(CrossingSite site, Func<double, double, double> groundTop)
    {
        ArgumentNullException.ThrowIfNull(groundTop);
        double fromX = site.FromX + 0.5, fromZ = site.FromZ + 0.5;
        double toX = site.ToX + 0.5, toZ = site.ToZ + 0.5;
        double dx = toX - fromX, dz = toZ - fromZ;
        double span = Math.Max(1.0, double.Hypot(dx, dz));
        double ux = dx / span, uz = dz / span;
        double deckTopY = site.DeckY + GroundTopOffset;

        (double nearRun, double nearTargetY) = SolveApproach(fromX, fromZ, -ux, -uz, deckTopY, groundTop);
        (double farRun, double farTargetY) = SolveApproach(toX, toZ, ux, uz, deckTopY, groundTop);
        return BuildWithRamps(site, nearRun, nearTargetY, farRun, farTargetY);
    }

    private static (double Run, double TargetY) SolveApproach(
        double bankX, double bankZ, double dirX, double dirZ, double deckTopY, Func<double, double, double> groundTop)
    {
        // Walk outward from MinimumRampRun to MaximumRampRun and take the shortest run where the sampled
        // ground meets the deck within MaximumWalkableGrade. Unlike fixed-point iteration, a monotonic
        // scan never oscillates on rising valley walls and guarantees exact toe-to-ground continuity.
        double bestRun = MinimumRampRun;
        double bestTargetY = (groundTop(bankX + (dirX * MinimumRampRun), bankZ + (dirZ * MinimumRampRun)) - RampToeEmbed) - deckTopY;
        double bestGrade = Math.Abs(bestTargetY) / MinimumRampRun;
        if (bestGrade <= MaximumWalkableGrade)
        {
            return (bestRun, bestTargetY);
        }

        for (double run = MinimumRampRun + RampSearchStep; run <= MaximumRampRun + 1e-6; run += RampSearchStep)
        {
            double clampedRun = Math.Min(run, MaximumRampRun);
            double targetY = (groundTop(bankX + (dirX * clampedRun), bankZ + (dirZ * clampedRun)) - RampToeEmbed) - deckTopY;
            double grade = Math.Abs(targetY) / clampedRun;
            if (grade <= MaximumWalkableGrade)
            {
                // Tighten slightly toward exact MaximumWalkableGrade if level ground allows a shorter run.
                double refinedRun = Math.Clamp(Math.Abs(targetY) / MaximumWalkableGrade, MinimumRampRun, clampedRun);
                double refinedTargetY = (groundTop(bankX + (dirX * refinedRun), bankZ + (dirZ * refinedRun)) - RampToeEmbed) - deckTopY;
                if (Math.Abs(refinedTargetY) / refinedRun <= MaximumWalkableGrade + 1e-3)
                {
                    return (refinedRun, refinedTargetY);
                }

                return (clampedRun, targetY);
            }

            if (grade < bestGrade)
            {
                bestGrade = grade;
                bestRun = clampedRun;
                bestTargetY = targetY;
            }
        }

        // Extreme dropoff steeper than MaximumWalkableGrade over MaximumRampRun: still connect directly
        // to the sampled ground rather than leaving a mid-air gap.
        return (bestRun, bestTargetY);
    }

    private static CrossingBridgeGeometry BuildWithRamps(
        CrossingSite site, double nearRunDouble, double nearTargetYDouble, double farRunDouble, double farTargetYDouble)
    {
        double fromX = site.FromX + 0.5;
        double fromZ = site.FromZ + 0.5;
        double toX = site.ToX + 0.5;
        double toZ = site.ToZ + 0.5;
        double dx = toX - fromX;
        double dz = toZ - fromZ;
        double span = Math.Max(1.0, double.Hypot(dx, dz));
        float halfSpan = (float)(span * 0.5);
        double ux = dx / span, uz = dz / span;

        double deckTopY = site.DeckY + GroundTopOffset;
        Vector3 anchor = new((float)((fromX + toX) * 0.5), (float)deckTopY, (float)((fromZ + toZ) * 0.5));
        float yaw = MathF.Atan2((float)dx, (float)dz);
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);

        bool stone = site.Style == CrossingStyle.StoneBridge;
        float deckWidth = stone ? StoneDeckWidth : TimberDeckWidth;
        float halfWidth = deckWidth * 0.5f;
        CrossingMaterial deckMaterial = stone ? CrossingMaterial.Masonry : CrossingMaterial.Planks;
        CrossingMaterial pierMaterial = stone ? CrossingMaterial.Masonry : CrossingMaterial.Timber;

        List<CrossingBox> boxes = new(28);

        // 1. Main level deck: top surface at local y = 0.
        float deckLength = (float)span + (DeckOverhang * 2f);
        boxes.Add(new CrossingBox(
            deckMaterial,
            new Vector3(0f, -DeckThickness * 0.5f, 0f),
            new Vector3(halfWidth, DeckThickness * 0.5f, deckLength * 0.5f)));

        // 2. Adaptive sloped approach ramps at both banks (exact deck-to-toe continuity for drops and rises).
        float nearRun = (float)nearRunDouble;
        float farRun = (float)farRunDouble;
        float nearTargetY = (float)nearTargetYDouble;
        float farTargetY = (float)farTargetYDouble;

        // Near ramp (-Z): inner top edge meets (0, 0, -halfSpan); outer toe meets (0, nearTargetY, -(halfSpan + nearRun)).
        // Positive pitch tips +Z down and -Z up, so pitch = atan2(nearTargetY, nearRun) handles both drops (< 0) and rises (> 0).
        float nearPitch = MathF.Atan2(nearTargetY, nearRun);
        float nearLength = MathF.Sqrt((nearRun * nearRun) + (nearTargetY * nearTargetY));
        Vector3 nearTopMid = new(0f, nearTargetY * 0.5f, -(halfSpan + (nearRun * 0.5f)));
        Vector3 nearDown = new(0f, -MathF.Cos(nearPitch), -MathF.Sin(nearPitch));
        boxes.Add(new CrossingBox(
            deckMaterial,
            nearTopMid + (nearDown * (RampThickness * 0.5f)),
            new Vector3(halfWidth, RampThickness * 0.5f, nearLength * 0.5f),
            Pitch: nearPitch));

        // Far ramp (+Z): inner top edge meets (0, 0, +halfSpan); outer toe meets (0, farTargetY, +(halfSpan + farRun)).
        // Positive pitch tips +Z down, so pitch = atan2(-farTargetY, farRun) handles both drops (< 0) and rises (> 0).
        float farPitch = MathF.Atan2(-farTargetY, farRun);
        float farLength = MathF.Sqrt((farRun * farRun) + (farTargetY * farTargetY));
        Vector3 farTopMid = new(0f, farTargetY * 0.5f, halfSpan + (farRun * 0.5f));
        Vector3 farDown = new(0f, -MathF.Cos(farPitch), -MathF.Sin(farPitch));
        boxes.Add(new CrossingBox(
            deckMaterial,
            farTopMid + (farDown * (RampThickness * 0.5f)),
            new Vector3(halfWidth, RampThickness * 0.5f, farLength * 0.5f),
            Pitch: farPitch));

        // 3. Solid abutments seated deep into both riverbanks, plus mid-ramp support under long approaches.
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

        if (nearRun >= MidRampSupportThreshold)
        {
            float midZ = -(halfSpan + (nearRun * 0.55f));
            float topY = (nearTargetY * 0.55f) - RampThickness;
            boxes.Add(new CrossingBox(
                CrossingMaterial.Masonry,
                new Vector3(0f, topY - (AbutmentDepth * 0.5f), midZ),
                new Vector3(halfWidth, AbutmentDepth * 0.5f, MidRampSupportLength * 0.5f)));
        }

        if (farRun >= MidRampSupportThreshold)
        {
            float midZ = halfSpan + (farRun * 0.55f);
            float topY = (farTargetY * 0.55f) - RampThickness;
            boxes.Add(new CrossingBox(
                CrossingMaterial.Masonry,
                new Vector3(0f, topY - (AbutmentDepth * 0.5f), midZ),
                new Vector3(halfWidth, AbutmentDepth * 0.5f, MidRampSupportLength * 0.5f)));
        }

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

        Vector3 nearToeWorld = new((float)(fromX - (ux * nearRunDouble)), (float)(deckTopY + nearTargetYDouble), (float)(fromZ - (uz * nearRunDouble)));
        Vector3 farToeWorld = new((float)(toX + (ux * farRunDouble)), (float)(deckTopY + farTargetYDouble), (float)(toZ + (uz * farRunDouble)));
        return new CrossingBridgeGeometry(anchor, rotation, span, deckWidth, nearRunDouble, farRunDouble, nearPitch, farPitch, nearToeWorld, farToeWorld, [.. boxes]);
    }
}
