using System.Diagnostics;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// The caves under the world (#10889), checked on the default world without an Engine: every cave mouth's tendril is a
/// pure function of its site; a cave's box lies under its mouth, sealed under the ground; generating a chunk a tendril or
/// a cave reaches costs about what any chunk does; and the residency keeps a cave's chunks round a player deep in it and
/// lets the overworld go.
/// </summary>
internal static class CaveChecks
{
    internal static CavePlacement? FirstCave(TerrainRecipe recipe, out PoiSite mouth)
    {
        mouth = default;
        List<PoiSite> sites = [];
        recipe.Placement.CollectSitesNear(0, 0, 4000, sites);
        foreach (PoiSite site in sites.Where(s => s.Kind == PoiKind.CaveMouth).OrderBy(s => Math.Abs(s.X) + Math.Abs(s.Z)))
        {
            if (recipe.Caves.For(site) is CavePlacement cave)
            {
                mouth = site;
                return cave;
            }
        }

        return null;
    }

    internal static void Run()
    {
        TerrainConfiguration configuration = TerrainConfiguration.Default;
        TerrainRecipe recipe = configuration.CreateRecipe(new TestDraws(configuration.Seed));
        CavePlacement? found = FirstCave(recipe, out PoiSite mouth);
        Check.That(found is not null, "the default world must have a cave mouth with a cave under it within 4 km of the origin");
        if (found is not CavePlacement cave)
        {
            return;
        }

        // The same mouth gives the same way underground from a fresh recipe (a pure function of its site).
        TerrainRecipe again = configuration.CreateRecipe(new TestDraws(configuration.Seed));
        CavePlacement? twice = again.Caves.For(mouth);
        Check.That(twice is not null && twice.Corner == cave.Corner && twice.Path.SequenceEqual(cave.Path),
            "a cave mouth's tendril and box must be the same from every recipe");

        // The tendril opens at the mouth and ends at the way in; the box's top lies under the mouth.
        Vector3 start = cave.Path[0], end = cave.Path[^1];
        Check.That(cave.TendrilOpen(new Vector3(mouth.X + 0.5f, mouth.Ground + 1.5f, mouth.Z + 0.5f)) > 0f, "the tendril must open at the mouth");
        Check.That(Vector3.Distance(cave.WayInWorld, end) < 3f, "the tendril must end at the cave's way in");
        Check.That(cave.BoxMaximum.Y < mouth.Ground, "the cave's box must lie under its mouth's floor");
        Check.That(end.Y < start.Y - 15f, "the tendril must go down into the ground");

        // A chunk the tendril reaches: its air is the tendril's, generated smooth (densities below the half-voxel face).
        TerrainChunkGenerator generator = new(recipe);
        TerrainOverlaySnapshot overlay = new(configuration.Seed, []);
        Vector3 middle = cave.Path[cave.Path.Count / 2];
        VoxelAddress inside = new((long)MathF.Floor(middle.X), (long)MathF.Floor(middle.Y), (long)MathF.Floor(middle.Z));
        TerrainChunk tendrilChunk = generator.Generate(inside.Chunk, overlay);
        int index = (int)(((inside.Z - inside.Chunk.Origin.Z) * TerrainConstants.ChunkEdgeLength * TerrainConstants.ChunkEdgeLength)
            + ((inside.Y - inside.Chunk.Origin.Y) * TerrainConstants.ChunkEdgeLength) + (inside.X - inside.Chunk.Origin.X));
        Check.That(tendrilChunk.Materials.Span[index] == TerrainConstants.EmptyMaterial, "the tendril's centre must be air");
        Check.That(!generator.Cacheable(inside.Chunk), "a chunk a tendril reaches must never be cached");
        int smooth = 0, solid = 0;
        for (int i = 0; i < TerrainConstants.ChunkVolume; i++)
        {
            if (tendrilChunk.Materials.Span[i] == TerrainConstants.EmptyMaterial) continue;
            solid++;
            float magnitude = -tendrilChunk.Densities.Span[i];
            if (magnitude > 0f && magnitude < 0.49f) smooth++;
        }

        Check.That(smooth > 0, "a tendril's wall must take smooth densities, not faces");

        // Generation cost: chunks round the tendril against chunks of plain ground far from any cave.
        double Time(IEnumerable<TerrainChunkAddress> chunks)
        {
            TerrainChunkGenerator fresh = new(recipe);
            Stopwatch clock = Stopwatch.StartNew();
            int n = 0;
            foreach (TerrainChunkAddress chunk in chunks)
            {
                fresh.Generate(chunk, overlay);
                n++;
            }

            return clock.Elapsed.TotalMilliseconds / Math.Max(1, n);
        }

        TerrainChunkAddress[] round = [.. Enumerable.Range(-1, 3).SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => new TerrainChunkAddress(inside.Chunk.X + dx, inside.Chunk.Y + dy, inside.Chunk.Z)))];
        long plainX = cave.BoxMaximum.X + 900, plainZ = cave.BoxMaximum.Z + 900;
        VoxelAddress plain = new(plainX, recipe.SurfaceAt(plainX, plainZ), plainZ);
        TerrainChunkAddress[] ground = [.. Enumerable.Range(-1, 3).SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => new TerrainChunkAddress(plain.Chunk.X + dx, plain.Chunk.Y + dy, plain.Chunk.Z)))];
        Time(round);
        Time(ground);
        double caveMs = Time(round), groundMs = Time(ground);
        Console.WriteLine(System.FormattableString.Invariant(
            $"cave under the mouth at {mouth.X},{mouth.Z}: tendril {start}..{end}, box {cave.BoxMinimum}..{cave.BoxMaximum}; tendril wall {smooth}/{solid} smooth; a chunk round the tendril {caveMs:F2} ms, of plain ground {groundMs:F2} ms"));
        Check.That(caveMs < Math.Max(4d, groundMs * 3d), $"a chunk a tendril reaches must cost about what any chunk does ({caveMs:F2} ms against {groundMs:F2} ms)");

        // Residency deep in a cave: the overworld's surface is let go, the cave's chunks kept.
        TerrainResidencyPolicy policy = new(recipe, new TerrainChunkGenerator(recipe));
        TerrainOverlayState state = new(configuration.Seed);
        HashSet<TerrainChunkAddress> shell = [new TerrainChunkAddress(inside.Chunk.X, inside.Chunk.Y, inside.Chunk.Z), new TerrainChunkAddress(inside.Chunk.X + 1, inside.Chunk.Y, inside.Chunk.Z)];
        policy.SetUnderground(new TerrainResidencyPolicy.UndergroundResidency(1, -1, false, shell));
        TerrainResidencyPlan deep = policy.PlanFor(inside.Chunk, state);
        Check.That(deep.Retained.All(shell.Contains) && deep.Retained.Count == shell.Count, "deep in a cave only its chunks must be kept, none of the overworld");
        policy.SetUnderground(new TerrainResidencyPolicy.UndergroundResidency(2, int.MaxValue, true, shell));
        TerrainResidencyPlan above = policy.PlanFor(inside.Chunk, state);
        Check.That(above.Retained.Count > shell.Count * 10 && shell.All(above.Retained.Contains), "near the surface the overworld must be kept again, and the cave's chunks with it");
    }
}
