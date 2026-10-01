using System.Diagnostics;
using System.Globalization;
using CraftSurvive.Game.Modules.Dungeons;
using Rusty.Engine;
using Rusty.Engine.Testing;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// The route check as the Engine makes it: each dungeon of a bank built in a headless Engine as the
/// game builds it - its voxels admitted, its sculpted rock meshed and admitted as collision - and
/// every route its flow promises asked of the Engine's collision navigation for a body. Where a
/// route is refused, the first step of the generator's own walk the Engine will not make is reported,
/// so a disagreement names a place and a shape, not just a seed.
/// </summary>
internal static class EngineRouteBank
{
    /// <summary>Where the rock texture sits in the repository's content.</summary>
    private const string RockTexture = "content/game/textures/cave-rock.png";

    /// <summary>Bodies to compare when looking for what a refusal hangs on.</summary>
    internal static readonly NavigationProfile[] Sweep =
    [
        NavigationProfile.Player,
        NavigationProfile.Player with { Name = "slim", AgentRadius = 0.2d },
        NavigationProfile.Player with { Name = "short", AgentHeight = 1.5d },
        NavigationProfile.Player with { Name = "steep", SlopeDegrees = 70d },
        NavigationProfile.Player with { Name = "step2", StepCells = 2U },
        new NavigationProfile("loose", 0.2d, 1.5d, 70d, 3U),
    ];

    /// <summary>Above every dungeon's volume, for counting what a column passes through.</summary>
    private const float TopOfSpace = 200f;

    internal sealed record Outcome(string Approach, ulong Seed, DungeonRouteVerdict Verdict, IReadOnlyList<RouteHangUp> HangUps, double BuildMilliseconds);

    /// <summary>Checks seeds of one approach for each profile, printing a line per dungeon and a summary per profile.</summary>
    internal static IReadOnlyList<Outcome> Run(string approach, ulong firstSeed, int seeds, IReadOnlyList<NavigationProfile> profiles, bool verbose)
    {
        using EngineTestHost host = EngineTestHost.Create(new EngineTestHostOptions
        {
            Content = new Dictionary<string, ReadOnlyMemory<byte>>
            {
                [DungeonCollision.RockTextureContentPath] = File.ReadAllBytes(Path.Combine(FindRepository(), RockTexture)),
            },
        });

        List<Outcome> outcomes = [];
        for (ulong seed = firstSeed; seed < firstSeed + (ulong)seeds; seed++)
        {
            (DungeonLayout layout, DungeonPlan plan, DungeonVolume walkable) = Generate(approach, seed);
            host.Call(engine =>
            {
                Stopwatch clock = Stopwatch.StartNew();
                using SpatialSession session = DungeonCollision.CreateSession(engine);
                DungeonCollision.Admit(engine, session, layout.Volume, DungeonCollision.Chunks(layout.Volume));
                Material? material = null;
                MeshResource? mesh = null;
                try
                {
                    if (layout.Rock is RockDensity rock)
                    {
                        material = DungeonCollision.CreateRockMaterial(engine);
                        mesh = DungeonCollision.MeshRock(engine, rock, material, out _);
                        DungeonCollision.AdmitRock(engine, session, mesh);
                    }

                    double buildMs = clock.Elapsed.TotalMilliseconds;
                    foreach (NavigationProfile profile in profiles)
                    {
                        DungeonRouteVerdict verdict = DungeonRoutes.Check(engine, session, walkable, plan, profile);
                        IReadOnlyList<RouteHangUp> hangUps = verdict.Walkable ? [] : DungeonRoutes.HangUps(engine, session, walkable, verdict);
                        outcomes.Add(new Outcome(approach, seed, verdict, hangUps, buildMs));
                        if (verbose)
                        {
                            Console.WriteLine($"{approach} seed {seed} {plan.Mix} floors={plan.Floors}: {verdict}");
                            foreach (RouteHangUp hangUp in hangUps)
                            {
                                Console.WriteLine($"    hangs up: {hangUp}");
                                Console.WriteLine($"      ground under from: {Ground(engine, session, hangUp.From)}; under to: {Ground(engine, session, hangUp.To)}");
                            }
                        }
                    }
                }
                finally
                {
                    // The session holds the mesh as collision until it goes.
                    session.Dispose();
                    mesh?.Dispose();
                    material?.Dispose();
                }
            });
        }

        foreach (NavigationProfile profile in profiles)
        {
            List<Outcome> mine = outcomes.Where(outcome => outcome.Verdict.Profile == profile).ToList();
            int walkableCount = mine.Count(outcome => outcome.Verdict.Walkable);
            var refused = mine.SelectMany(outcome => outcome.Verdict.Routes).Where(route => !route.Reached)
                .GroupBy(route => $"{route.Name.Split("->")[0]}->{(route.Name.Contains("floor", StringComparison.Ordinal) ? "floorN" : route.Name.Split("->")[1])} {route.Outcome}")
                .OrderByDescending(group => group.Count()).Select(group => $"{group.Key} x{group.Count()}");
            var shapes = mine.SelectMany(outcome => outcome.HangUps)
                .GroupBy(hangUp => $"rise {hangUp.To.Y - hangUp.From.Y} {hangUp.Outcome}")
                .OrderByDescending(group => group.Count()).Select(group => $"{group.Key} x{group.Count()}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"engine routes {approach} {profile}: {walkableCount}/{mine.Count} walkable; build {mine.Average(o => o.BuildMilliseconds):F0} ms, publish {mine.Average(o => o.Verdict.PublishMilliseconds):F0} ms, queries {mine.Average(o => o.Verdict.QueryMilliseconds):F0} ms per dungeon"));
            Console.WriteLine($"    refused: {(walkableCount == mine.Count ? "none" : string.Join("; ", refused))}");
            Console.WriteLine($"    first hang-ups by step: {(walkableCount == mine.Count ? "none" : string.Join("; ", shapes))}");
        }

        return outcomes;
    }

    /// <summary>What a ray straight down the middle of a cell's column meets, from its headroom: the height above the cell's floor and the surface's upward normal.</summary>
    private static string Ground(IEngineContext engine, SpatialSession session, DungeonCell cell)
    {
        System.Numerics.Vector3 top = DungeonCollision.InSession(new System.Numerics.Vector3(cell.X + 0.5f, cell.Y + DungeonWalk.Headroom - 0.05f, cell.Z + 0.5f));
        SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(session, top, -System.Numerics.Vector3.UnitY, DungeonWalk.Headroom + 2d,
            new SpatialQueryFilter(CraftSurvive.Game.Modules.Terrain.TerrainConstants.CollisionGroupAll, CraftSurvive.Game.Modules.Terrain.TerrainConstants.CollisionMaskAll),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));
        float floorY = DungeonCollision.InSession(new System.Numerics.Vector3(0f, cell.Y, 0f)).Y;
        System.Numerics.Vector3 corner = DungeonCollision.InSession(new System.Numerics.Vector3(cell.X, 0f, cell.Z));
        SpatialMapCell column = engine.Spatial.ReadMap(new SpatialMapRequest(session, corner, 1d, 1U, 1U,
            floorY - 3d, floorY + 3d, floorY - 3d, floorY + 3d, ReadOnlyMemory<SpatialEntityCollider>.Empty)).Cells.Span[0];
        string supports = column.NavigationSamples == 0
            ? "no navigation supports within 3 m"
            : string.Create(CultureInfo.InvariantCulture, $"{column.NavigationSamples} supports {column.MinimumSupportY - floorY:+0.00;-0.00}..{column.MaximumSupportY - floorY:+0.00;-0.00} m");
        // Every surface a ray straight down the column meets from the top of the space to this floor.
        int crossings = 0;
        System.Numerics.Vector3 from = DungeonCollision.InSession(new System.Numerics.Vector3(cell.X + 0.5f, TopOfSpace, cell.Z + 0.5f));
        while (from.Y > floorY - 0.5f)
        {
            SpatialHit next = engine.Spatial.CastRay(new SpatialRaycastRequest(session, from, -System.Numerics.Vector3.UnitY, from.Y - (floorY - 0.5f),
                new SpatialQueryFilter(CraftSurvive.Game.Modules.Terrain.TerrainConstants.CollisionGroupAll, CraftSurvive.Game.Modules.Terrain.TerrainConstants.CollisionMaskAll),
                ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));
            if (!next.Present)
            {
                break;
            }

            crossings++;
            from = next.Point - new System.Numerics.Vector3(0f, 0.001f, 0f);
        }

        supports += $", {crossings} surfaces from the top";
        return hit.Present
            ? string.Create(CultureInfo.InvariantCulture, $"{hit.Kind} at {hit.Point.Y - floorY:+0.00;-0.00} m, normal.y {hit.Normal.Y:F2}{(hit.StartSolid ? " (started solid)" : string.Empty)}, {supports}")
            : $"nothing, {supports}";
    }

    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVolume Walkable) Generate(string approach, ulong seed)
    {
        switch (approach)
        {
            case "b":
                var modular = ModularDungeon.Generate(seed);
                return (modular.Layout, modular.Plan, modular.Walkable);
            case "c":
                var sculpted = SculptedCave.Generate(seed);
                return (sculpted.Layout, sculpted.Plan, sculpted.Walkable);
            default:
                var carved = CarveAndStamp.Generate(seed);
                return (carved.Layout, carved.Plan, carved.Layout.Volume);
        }
    }

    private static string FindRepository()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, RockTexture)))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("the repository root was not found above the bank");
    }
}
