using System.Diagnostics;
using System.Numerics;
using Rusty.Engine;
using Rusty.Engine.Implicit;

namespace CraftSurvive.Game.Modules.Terrain;

// The original comparison recipe remains available, independent of retained
// graphics, collision replacement, commands and product lifecycle.
internal sealed class CourtyardRecipe(IEngineContext engine, CourtyardMaterials materials)
{
    private const float CourtyardFloor = 3f;
    private const float RaisedFloor = 5f;
    private const float PassageStart = 10f;
    private const float PassageEnd = 22f;
    private const float ChamberEnd = 32f;
    private const float WallThickness = 0.6f;
    private const float WallSectionLength = 4f;
    private const float CourseHeight = 0.6f;
    private const float StoneLength = 0.95f;
    private const float JointHalfWidth = 0.045f;
    private const float StoneRelief = 0.09f;
    private const float UvRepeatsPerMeter = 0.6f;
    private const float DomainPadding = 0.25f;
    private const string TestWall = "west";
    private const float TestSectionStart = -2f;
    private const string TestPartPrefix = "masonry test";
    private const float BrickRegionTolerance = 0.015f;
    private const float CapDepth = 0.14f;
    private const float CapMaterialMargin = 0.03f;
    private const float WallMossHeight = 0.38f;
    private const float MaxMaterialCutoff = 0.15f;
    internal double TestGenerationSeconds { get; private set; }
    internal void Compose(CourtyardSettings next, Action<RecipeSurface> output)
    {
            float halfWidth = next.Width * 0.5f;
            // Floors and the raised route are authored solids; texture density
            // stays constant when dimensions or extraction sampling change.
            BoxPart("courtyard floor", new(-halfWidth, CourtyardFloor - 0.5f, -10f), new(halfWidth, CourtyardFloor, PassageStart), materials.Ground, next, output);
            BoxPart("passage floor", new(-2.6f, RaisedFloor - 0.5f, PassageStart), new(2.6f, RaisedFloor, PassageEnd), materials.Stone, next, output);
            BoxPart("chamber floor", new(-6f, RaisedFloor - 0.5f, PassageEnd), new(6f, RaisedFloor, ChamberEnd), materials.Stone, next, output);
            // Union before extraction so hidden overlapping risers never enter
            // the collision world as separate surfaces.
            using (ImplicitRecipe stairs = new(engine.ImplicitSurfaces))
            {
                const int stairCount = 6;
                const float stairRun = 4f;
                ImplicitNode solid = default;
                for (int step = 0; step < stairCount; step++)
                {
                    float top = CourtyardFloor + (RaisedFloor - CourtyardFloor) * (step + 1) / stairCount;
                    float front = PassageStart - stairRun + stairRun * step / stairCount;
                    ImplicitNode riser = stairs.Box(new(-2f, CourtyardFloor - 0.1f, front), new(2f, top, PassageStart + 0.08f));
                    // Each visible nose is a 45-degree cut, followed by a flat
                    // tread. Collision copies this exact surface.
                    Vector3 noseNormal = Vector3.Normalize(new Vector3(0f, 1f, -1f));
                    float noseRun = (RaisedFloor - CourtyardFloor) / stairCount;
                    ImplicitNode nose = stairs.Plane(noseNormal, Vector3.Dot(noseNormal, new Vector3(0f, top, front + noseRun)));
                    riser = stairs.Intersect(riser, nose);
                    solid = step == 0 ? riser : stairs.Union(solid, riser);
                }
                AddPart("stair flight", stairs, solid, new(-2f, CourtyardFloor - 0.1f, PassageStart - stairRun),
                    new(2f, RaisedFloor, PassageStart + 0.08f), materials.Plaster, [], next, output);
            }

            WallRun("south", new(-halfWidth, CourtyardFloor, -10f), new(halfWidth, 7.2f, -10f + WallThickness), false, next, output);
            WallRun("west", new(-halfWidth, CourtyardFloor, -10f), new(-halfWidth + WallThickness, 7.6f, PassageStart), false, next, output);
            WallRun("east", new(halfWidth - WallThickness, CourtyardFloor, -10f), new(halfWidth, 6.4f, PassageStart), false, next, output);
            WallRun("gateway", new(-halfWidth, CourtyardFloor, PassageStart - WallThickness), new(halfWidth, 8.1f, PassageStart), true, next, output);
            WallRun("passage west", new(-2.6f, RaisedFloor, PassageStart), new(-2f, 8.6f, PassageEnd), false, next, output);
            WallRun("passage east", new(2f, RaisedFloor, PassageStart), new(2.6f, 8.6f, PassageEnd), false, next, output);
            BoxPart("covered passage", new(-2.7f, 8.6f, PassageStart), new(2.7f, 9f, PassageEnd), materials.DarkNeutral, next, output);
            WallRun("chamber entrance", new(-6f, RaisedFloor, PassageEnd), new(6f, 9f, PassageEnd + WallThickness), true, next, output);
            WallRun("chamber west", new(-6f, RaisedFloor, PassageEnd), new(-5.4f, 9.5f, ChamberEnd), false, next, output);
            WallRun("chamber east", new(5.4f, RaisedFloor, PassageEnd), new(6f, 8.8f, ChamberEnd), false, next, output);
            WallRun("chamber end", new(-6f, RaisedFloor, ChamberEnd - WallThickness), new(6f, 10f, ChamberEnd), false, next, output);

            Frame("gateway frame", PassageStart - WallThickness - 0.12f, next, output);
            Frame("chamber frame", PassageEnd - 0.15f, next, output);
            for (int beam = 0; beam < 4; beam++)
            {
                float z = PassageStart + 1f + beam * 3f;
                BoxPart($"roof beam {beam}", new(-2f, 8.25f, z), new(2f, 8.62f, z + 0.28f), materials.Wood, next, output);
            }
            // A low altar and clipped columns anchor the chamber's long view.
            BoxPart("altar base", new(-2.1f, RaisedFloor, 29.4f), new(2.1f, 5.4f, 31f), materials.DarkNeutral, next, output);
            BoxPart("altar cap", new(-2.3f, 5.4f, 29.2f), new(2.3f, 5.65f, 31.2f), materials.Plaster, next, output);
            foreach (float x in new[] { -4.3f, 4.3f })
            {
                BoxPart("column foot", new(x - 0.55f, 5f, 28.3f), new(x + 0.55f, 5.5f, 29.4f), materials.Plaster, next, output);
                using ImplicitRecipe column = new(engine.ImplicitSurfaces);
                ImplicitNode shaft = column.Box(new(x - 0.36f, 5.4f, 28.5f), new(x + 0.36f, 8.7f, 29.2f));
                Vector3 cutNormal = Vector3.Normalize(new Vector3(0.35f, 1f, 0.2f));
                ImplicitNode cut = column.Plane(cutNormal, Vector3.Dot(cutNormal, new Vector3(x, 8.35f, 28.85f)));
                shaft = column.Intersect(shaft, cut);
                AddPart("clipped column", column, shaft, new(x - 0.8f, 5.1f, 28.2f), new(x + 0.8f, 9f, 29.6f), materials.Plaster, [], next, output);
            }
            OrganicDetails(halfWidth, next, output);

    }
    private void WallRun(string name, Vector3 min, Vector3 max, bool doorway, CourtyardSettings next, Action<RecipeSurface> output)
    {
        bool alongX = max.X - min.X > max.Z - min.Z;
        float start = alongX ? min.X : min.Z;
        float end = alongX ? max.X : max.Z;
        for (float position = start; position < end - 0.01f; position += WallSectionLength)
        {
            Vector3 a = min;
            Vector3 b = max;
            if (alongX) { a.X = position; b.X = MathF.Min(end, position + WallSectionLength); }
            else { a.Z = position; b.Z = MathF.Min(end, position + WallSectionLength); }
            bool test = name == TestWall && position == TestSectionStart;
            Stopwatch? watch = test ? Stopwatch.StartNew() : null;
            (Vector3 Min, Vector3 Max)[] stones = CourseStones(a, b, alongX, min.Y).ToArray();
            Vector3 capMin = new(a.X - CapDepth, b.Y - 0.22f, a.Z - CapDepth);
            Vector3 capMax = new(b.X + CapDepth, b.Y + CapDepth, b.Z + CapDepth);
            float chipPosition = position + WallSectionLength * 0.65f;
            long chipVariation = engine.Random.DrawKeyed(new KeyedRngRequest(next.Seed, "courtyard.chips", FormattableString.Invariant($"{name}:{position:R}"), 0, 3)).Value;
            float chipRadius = 0.22f + chipVariation * 0.11f;
            Vector3 chip = alongX ? new(chipPosition, b.Y + 0.08f, (a.Z + b.Z) * 0.5f) : new((a.X + b.X) * 0.5f, b.Y + 0.08f, chipPosition);

            if (test && next.Masonry == "layered")
            {
                // Separate closed solids with real relief. Brick backs overlap
                // the backing volume; exposed faces never rely on draw order.
                LayeredBox($"{TestPartPrefix} mortar", a, b, chip, chipRadius, materials.Mortar, next, output);
                foreach ((Vector3 stoneMin, Vector3 stoneMax) in stones)
                    LayeredBox($"{TestPartPrefix} brick", stoneMin, stoneMax, chip, chipRadius, materials.Stone, next, output);
                LayeredBox($"{TestPartPrefix} cap", capMin, capMax, chip, chipRadius, materials.Plaster, next, output);
            }
            else
            {
                using ImplicitRecipe recipe = new(engine.ImplicitSurfaces);
                ImplicitNode wall = recipe.Box(a, b);
                ImplicitNode bricks = default;
                bool hasBricks = false;
                foreach ((Vector3 stoneMin, Vector3 stoneMax) in stones)
                {
                    ImplicitNode brick = recipe.Box(stoneMin, stoneMax);
                    wall = recipe.Union(wall, brick);
                    if (test && next.Masonry == "regions")
                    {
                        bricks = hasBricks ? recipe.Union(bricks, brick) : brick;
                        hasBricks = true;
                    }
                }
                ImplicitNode cap = recipe.Box(capMin, capMax);
                wall = recipe.Subtract(recipe.Union(wall, cap), recipe.Sphere(chip, chipRadius));
                if (doorway)
                {
                    float doorHalf = next.DoorWidth * 0.5f;
                    ImplicitNode opening = recipe.Box(new(next.DoorOffset - doorHalf, min.Y - 1f, min.Z - 1f), new(next.DoorOffset + doorHalf, 7.75f, max.Z + 1f));
                    wall = recipe.Subtract(wall, opening);
                }
                // Cutoff moves only region classification. The cap and wall root
                // remain fixed, so extracted geometry and collision do not move.
                // These are horizontal bands on this wall part, so use affine
                // half-spaces. A box field also measures distance to its other
                // sides, which would distort vertex-interpolated band cutoffs.
                float capCutoff = capMin.Y - CapMaterialMargin + next.MaterialCutoff;
                ImplicitNode capRegion = recipe.Plane(-Vector3.UnitY, -capCutoff);
                List<ImplicitMaterialRegion> regions = [new(capRegion, materials.Plaster)];
                if (test && next.Masonry == "regions" && hasBricks)
                    regions.Add(new(recipe.Offset(bricks, BrickRegionTolerance), materials.Stone));
                // Exclude the unrelated moss classification in all three test
                // modes so the comparison isolates brick/mortar ownership.
                if (!test)
                {
                    ImplicitNode moss = recipe.Plane(Vector3.UnitY, min.Y + WallMossHeight + next.MaterialCutoff);
                    regions.Add(new(moss, materials.Moss));
                }
                AddPart(test ? $"{TestPartPrefix} union" : name, recipe, wall,
                    a - new Vector3(0.3f), b + new Vector3(0.35f),
                    test && next.Masonry == "regions" ? materials.Mortar : materials.Stone,
                    regions.ToArray(), next, output);
            }
            if (watch is not null) TestGenerationSeconds = watch.Elapsed.TotalSeconds;
        }
    }

    private static IEnumerable<(Vector3 Min, Vector3 Max)> CourseStones(Vector3 a, Vector3 b, bool alongX, float baseY)
    {
        // Both constructions consume the same globally phased brick bounds.
        float position = a[alongX ? 0 : 2];
        for (int course = 0; baseY + course * CourseHeight < b.Y; course++)
        {
            float bottom = baseY + course * CourseHeight;
            float phase = (course % 2) * StoneLength * 0.5f;
            for (float p = MathF.Floor((position - phase) / StoneLength) * StoneLength + phase; p < b[alongX ? 0 : 2]; p += StoneLength)
            {
                float left = MathF.Max(position, p + JointHalfWidth);
                float right = MathF.Min(b[alongX ? 0 : 2], p + StoneLength - JointHalfWidth);
                if (right <= left) continue;
                Vector3 stoneMin = a - new Vector3(StoneRelief, 0, StoneRelief);
                Vector3 stoneMax = b + new Vector3(StoneRelief, 0, StoneRelief);
                stoneMin.Y = bottom + JointHalfWidth;
                stoneMax.Y = MathF.Min(b.Y, bottom + CourseHeight - JointHalfWidth);
                if (alongX) { stoneMin.X = left; stoneMax.X = right; }
                else { stoneMin.Z = left; stoneMax.Z = right; }
                if (stoneMax.Y > stoneMin.Y) yield return (stoneMin, stoneMax);
            }
        }
    }

    private void LayeredBox(string name, Vector3 min, Vector3 max, Vector3 chip, float chipRadius,
        Material material, CourtyardSettings next, Action<RecipeSurface> output)
    {
        using ImplicitRecipe recipe = new(engine.ImplicitSurfaces);
        ImplicitNode solid = recipe.Subtract(recipe.Box(min, max), recipe.Sphere(chip, chipRadius));
        AddPart(name, recipe, solid, min, max, material, [], next, output);
    }

    private void Frame(string name, float z, CourtyardSettings next, Action<RecipeSurface> output)
    {
        float half = next.DoorWidth * 0.5f;
        float center = next.DoorOffset;
        const float trimWidth = 0.25f;
        foreach (float x in new[] { center - half - trimWidth, center + half })
            BoxPart(name + " jamb", new(x, RaisedFloor, z), new(x + trimWidth, 7.85f, z + 0.28f), materials.Plaster, next, output);
        BoxPart(name + " lintel", new(center - half - trimWidth, 7.75f, z), new(center + half + trimWidth, 8.1f, z + 0.28f), materials.Plaster, next, output);
    }

    private void OrganicDetails(float halfWidth, CourtyardSettings next, Action<RecipeSurface> output)
    {
        foreach ((Vector3 center, Vector3 radii) in new[] {
            (new Vector3(-8.5f, 3.25f, -2f), new Vector3(1.8f, 1.1f, 1.4f)),
            (new Vector3(8.5f, 3.2f, 3f), new Vector3(1.5f, 0.8f, 2f)),
            (new Vector3(-7f, 3.15f, 6f), new Vector3(1.1f, 0.65f, 1.2f)) })
        {
            using ImplicitRecipe recipe = new(engine.ImplicitSurfaces);
            ImplicitNode rock = recipe.Ellipsoid(center, radii);
            Vector3 clipNormal = Vector3.Normalize(new Vector3(0.3f, 1f, 0.15f));
            ImplicitNode clip = recipe.Plane(clipNormal, Vector3.Dot(clipNormal, center + new Vector3(0f, radii.Y * 0.6f, 0f)));
            rock = recipe.Intersect(rock, clip);
            ImplicitNode moss = recipe.Box(center - radii, center + new Vector3(radii.X, 0.15f, radii.Z));
            AddPart("broken rock", recipe, rock, center - radii, center + radii, materials.Stone, [new ImplicitMaterialRegion(moss, materials.Moss)], next, output);
        }
        using ImplicitRecipe roots = new(engine.ImplicitSurfaces);
        Vector3 origin = new(-halfWidth + 0.9f, 3.2f, 2f);
        ImplicitNode root = roots.Capsule(origin, origin + new Vector3(2f, -0.08f, 1f), 0.23f);
        for (int branch = 0; branch < 4; branch++)
        {
            Vector3 bend = origin + new Vector3(1.2f + branch * 0.45f, 0.15f, branch - 1f);
            Vector3 tip = bend + new Vector3(1.4f, -0.17f, 0.7f);
            root = roots.Blend(root, roots.Capsule(origin, bend, 0.18f), 0.12f);
            root = roots.Blend(root, roots.Capsule(bend, tip, 0.12f), 0.1f);
        }
        AddPart("root cluster", roots, root, origin - new Vector3(0.4f, 0.4f, 2f), origin + new Vector3(5f, 1f, 4f), materials.Wood, [], next, output);
    }

    private void BoxPart(string name, Vector3 min, Vector3 max, Material material, CourtyardSettings next, Action<RecipeSurface> output)
    {
        using ImplicitRecipe recipe = new(engine.ImplicitSurfaces);
        Vector3 extent = max - min;
        float narrowest = MathF.Min(extent.X, MathF.Min(extent.Y, extent.Z));
        CourtyardSettings planar = next with { CellSize = MathF.Min(0.5f, narrowest * 0.75f) };
        AddPart(name, recipe, recipe.Box(min, max), min, max, material, [], planar, output);
    }

    private static void AddPart(string name, ImplicitRecipe recipe, ImplicitNode root, Vector3 min, Vector3 max, Material material,
        ImplicitMaterialRegion[] regions, CourtyardSettings next, Action<RecipeSurface> output)
        => output(new(name, recipe.Field, root, min, max, material, regions,
            new(next.CellSize, next.CreaseDegrees, UvRepeatsPerMeter, next.MaterialBoundaryMode),
            new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One)));
}
