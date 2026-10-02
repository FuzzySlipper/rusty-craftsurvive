using System.Diagnostics;
using System.Globalization;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.Player;
using Rusty.Engine;
using CraftSurvive.Game.Modules.Content;
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

    /// <summary>A profile made for one dungeon's volume, from the Engine's defaults.</summary>
    internal delegate NavigationProfile ProfileFor(ISpatialService spatial, DungeonVolume volume);

    internal static readonly ProfileFor Player = NavigationProfile.Player;

    /// <summary>The player's body and variations on it, one option at a time, to see what a refusal hangs on.</summary>
    internal static readonly ProfileFor[] Sweep =
    [
        Player,
        (spatial, volume) => Vary(spatial, volume, "no-drop", config => config with { MaximumDrop = config.Character.Surface.MaximumStepHeight }),
        (spatial, volume) => Vary(spatial, volume, "tight-snap", config => config with { SnapAbove = DefaultSnap(spatial), SnapBelow = DefaultSnap(spatial) }),
        (spatial, volume) => Vary(spatial, volume, "slim", config => config with
        {
            Character = config.Character with { Shape = config.Character.Shape with { Radius = SlimRadius } },
        }),
        (spatial, volume) => Vary(spatial, volume, "diagonal", config => config with { DiagonalNeighbors = true }),
        (spatial, volume) => Vary(spatial, volume, "deep-columns", config => config with { SupportsPerColumn = DeepColumnSupports }),
        (spatial, volume) => Vary(spatial, volume, "walk-only", config => config with
        {
            Character = config.Character with { Surface = config.Character.Surface with { MaximumStepHeight = PlayerConstants.MaximumStepHeight } },
        }),
    ];

    private const float SlimRadius = 0.25f;
    private const uint DeepColumnSupports = 16U;


    private static NavigationProfile Vary(ISpatialService spatial, DungeonVolume volume, string name, Func<CollisionNavigationConfig, CollisionNavigationConfig> change)
    {
        NavigationProfile player = NavigationProfile.Player(spatial, volume);
        return new NavigationProfile(name, change(player.Config));
    }

    private static double DefaultSnap(ISpatialService spatial) => spatial.DefaultCollisionNavigationConfig().SnapBelow;

    internal sealed record Outcome(string Approach, ulong Seed, DungeonRouteVerdict Verdict, IReadOnlyList<RouteHangUp> HangUps, double BuildMilliseconds);

    /// <summary>Checks seeds of one approach for each profile, printing a line per dungeon and a summary per profile.</summary>
    internal static IReadOnlyList<Outcome> Run(string approach, ulong firstSeed, int seeds, IReadOnlyList<ProfileFor> profiles, bool verbose,
        DungeonSurface surface = DungeonSurface.Cubes)
    {
        using EngineTestHost host = CreateHost();
        List<Outcome> outcomes = [];
        for (ulong seed = firstSeed; seed < firstSeed + (ulong)seeds; seed++)
        {
            DungeonCandidate candidate = DungeonCandidates.Generate(Approach(approach), seed, 0);
            outcomes.AddRange(Check(host, approach, candidate, profiles, verbose, surface));
        }

        Summarise(approach, outcomes);
        return outcomes;
    }

    /// <summary>
    /// Enters each entrance seed as the game does: candidates in order until the Engine walks one.
    /// Returns, per seed, the index of the candidate accepted, or -1 when none was.
    /// </summary>
    internal static IReadOnlyList<int> Accept(string approach, ulong firstSeed, int seeds, DungeonSurface surface = DungeonSurface.Cubes)
    {
        using EngineTestHost host = CreateHost();
        List<int> accepted = [];
        List<double> buildMs = [];
        Stopwatch clock = Stopwatch.StartNew();
        for (ulong seed = firstSeed; seed < firstSeed + (ulong)seeds; seed++)
        {
            int found = -1;
            for (int index = 0; index < DungeonCandidates.MaximumCandidates && found < 0; index++)
            {
                DungeonCandidate candidate = DungeonCandidates.Generate(Approach(approach), seed, index);
                Outcome checkedOne = Check(host, approach, candidate, [Player], verbose: false, surface)[0];
                buildMs.Add(checkedOne.BuildMilliseconds);
                found = checkedOne.Verdict.Walkable ? index : -1;
            }

            accepted.Add(found);
        }

        var tries = accepted.GroupBy(index => index).OrderBy(group => group.Key)
            .Select(group => group.Key < 0 ? $"none x{group.Count()}" : $"candidate {group.Key} x{group.Count()}");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"engine-accepted {approach} {surface}: {accepted.Count(index => index >= 0)}/{seeds} entrances have a dungeon ({string.Join(", ", tries)}); build {buildMs.Average():F0} ms per candidate; {clock.Elapsed.TotalSeconds:F1} s"));
        return accepted;
    }

    private static DungeonApproach Approach(string name) => name switch
    {
        "b" => DungeonApproach.Modules,
        "c" => DungeonApproach.SculptedCave,
        _ => DungeonApproach.CarveAndStamp,
    };

    private static EngineTestHost CreateHost() => EngineTestHost.Create(new EngineTestHostOptions
    {
        Content = new Dictionary<string, ReadOnlyMemory<byte>>
        {
            [DungeonCollision.RockTextureContentPath] = File.ReadAllBytes(RockTexturePath()),
        },
    });

    /// <summary>Builds one candidate in the Engine as the game loads it and checks its routes for each profile.</summary>
    private static List<Outcome> Check(EngineTestHost host, string approach, DungeonCandidate candidate, IReadOnlyList<ProfileFor> profiles, bool verbose,
        DungeonSurface surface)
    {
        List<Outcome> outcomes = [];
        DungeonLayout layout = surface == DungeonSurface.Cubes ? candidate.Layout : candidate.AllVoxels;
        DungeonPlan plan = candidate.Plan;
        DungeonVolume walkable = candidate.Walkable;
        ulong seed = candidate.Seed;
        host.Call(engine =>
        {
            Stopwatch clock = Stopwatch.StartNew();
            using SpatialSession session = DungeonCollision.CreateSession(engine, surface);
            DungeonCollision.Admit(engine, session, layout.Volume, DungeonCollision.Chunks(layout.Volume), layout.Densities);
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
                foreach (ProfileFor profileFor in profiles)
                {
                    NavigationProfile profile = profileFor(engine.Spatial, walkable);
                    DungeonRouteVerdict verdict = DungeonRoutes.Check(engine, session, walkable, plan, profile);
                    IReadOnlyList<RouteHangUp> hangUps = verdict.Walkable ? [] : DungeonRoutes.HangUps(engine, session, walkable, verdict);
                    outcomes.Add(new Outcome(approach, seed, verdict, hangUps, buildMs));
                    if (verbose)
                    {
                        Console.WriteLine($"{approach} seed {seed} {plan.Mix} floors={plan.Floors}: {verdict}");
                        foreach (RouteHangUp hangUp in hangUps)
                        {
                            Console.WriteLine($"    hangs up: {hangUp}");
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
        return outcomes;
    }

    private static void Summarise(string approach, List<Outcome> outcomes)
    {
        foreach (string name in outcomes.Select(outcome => outcome.Verdict.Profile.Name).Distinct())
        {
            List<Outcome> mine = outcomes.Where(outcome => outcome.Verdict.Profile.Name == name).ToList();
            NavigationProfile profile = mine[0].Verdict.Profile;
            int walkableCount = mine.Count(outcome => outcome.Verdict.Walkable);
            var refused = mine.SelectMany(outcome => outcome.Verdict.Routes).Where(route => !route.Reached)
                .GroupBy(route => $"{route.Name.Split("->")[0]}->{(route.Name.Contains("floor", StringComparison.Ordinal) ? "floorN" : route.Name.Split("->")[1])} {route.Outcome}")
                .OrderByDescending(group => group.Count()).Select(group => $"{group.Key} x{group.Count()}");
            var shapes = mine.SelectMany(outcome => outcome.HangUps)
                .GroupBy(hangUp => $"rise {hangUp.To.Y - hangUp.From.Y}: {hangUp.Why.Split(" m")[0].Split(" at ")[0]}")
                .OrderByDescending(group => group.Count()).Select(group => $"{group.Key} x{group.Count()}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"engine routes {approach} {profile}: {walkableCount}/{mine.Count} walkable; build {mine.Average(o => o.BuildMilliseconds):F0} ms, publish {mine.Average(o => o.Verdict.PublishMilliseconds):F0} ms, queries {mine.Average(o => o.Verdict.QueryMilliseconds):F0} ms per dungeon"));
            Console.WriteLine($"    refused: {(walkableCount == mine.Count ? "none" : string.Join("; ", refused))}");
            Console.WriteLine($"    first hang-ups by step: {(walkableCount == mine.Count ? "none" : string.Join("; ", shapes))}");
        }

    }

    internal static (DungeonLayout Layout, DungeonPlan Plan, DungeonVolume Walkable) Generate(string approach, ulong seed)
    {
        DungeonCandidate candidate = DungeonCandidates.Generate(Approach(approach), seed, 0);
        return (candidate.Layout, candidate.Plan, candidate.Walkable);
    }

    internal static string RockTexturePath() => Path.Combine(FindRepository(), RockTexture);

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

/// <summary>
/// The smallest case of a smooth slope: a dual-contoured ramp rising along +X at an angle in an
/// otherwise empty space, and the Engine asked to walk up it and down it for the player's body.
/// </summary>
internal static class RampProbe
{
    private const int Width = 16;
    private const int Height = 16;
    private const int Depth = 6;
    private const float BaseHeight = 3f;

    internal static void Run(double degrees)
    {
        using EngineTestHost host = EngineTestHost.Create(new EngineTestHostOptions
        {
            Content = new Dictionary<string, ReadOnlyMemory<byte>>
            {
                [DungeonCollision.RockTextureContentPath] = File.ReadAllBytes(EngineRouteBank.RockTexturePath()),
            },
        });
        float slope = (float)Math.Tan(degrees * Math.PI / 180d);
        float[] values = new float[Width * Height * Depth];
        for (int z = 0; z < Depth; z++)
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    // Sample centres sit at cell middles; negative below the ramp's surface.
                    float surface = BaseHeight + (slope * (x + 0.5f));
                    values[(((z * Height) + y) * Width) + x] = (y + 0.5f) - surface;
                }
            }
        }

        RockDensity rock = new(Width, Height, Depth, values);
        DungeonVolume empty = new(1, 1, 1, BlockId.Air);
        host.Call(engine =>
        {
            using SpatialSession session = DungeonCollision.CreateSession(engine);
            Material material = DungeonCollision.CreateRockMaterial(engine);
            MeshResource mesh = DungeonCollision.MeshRock(engine, rock, material, out _);
            DungeonCollision.AdmitRock(engine, session, mesh);
            CollisionNavigationConfig config = NavigationProfile.Player(engine.Spatial, empty).Config with { MaximumCells = Width * Depth };
            engine.Spatial.ReplaceCollisionNavigation(new CollisionNavigationReplaceRequest(
                session, DungeonCollision.Origin, DungeonCollision.Origin + new System.Numerics.Vector3(Width, Height, Depth), config));
            System.Numerics.Vector3 At(float x) => DungeonCollision.InSession(new System.Numerics.Vector3(x + 0.5f, BaseHeight + (slope * (x + 0.5f)), 2.5f));
            NavigationStepResult up = engine.Spatial.EvaluateNavigationStep(new NavigationStepRequest(session, At(2), At(Width - 4), 4096f, 100_000U));
            NavigationStepResult down = engine.Spatial.EvaluateNavigationStep(new NavigationStepRequest(session, At(Width - 4), At(2), 4096f, 100_000U));
            string edges = string.Join(", ", Enumerable.Range(2, 4).Select(x =>
            {
                CollisionNavigationColumnResult a = engine.Spatial.ExplainCollisionNavigationColumn(new CollisionNavigationColumnRequest(session, x, 2));
                PlanarNavCell from = a.Samples.Span[0].Cell;
                CollisionNavigationColumnResult b = engine.Spatial.ExplainCollisionNavigationColumn(new CollisionNavigationColumnRequest(session, x + 1, 2));
                PlanarNavCell to = b.Samples.Span[0].Cell;
                CollisionNavigationEdgeReadout edge = engine.Spatial.ExplainCollisionNavigationEdge(new CollisionNavigationEdgeRequest(session, from, to));
                CollisionNavigationEdgeReadout back = engine.Spatial.ExplainCollisionNavigationEdge(new CollisionNavigationEdgeRequest(session, to, from));
                return $"x{x}->{x + 1} up {edge.Outcome} down {back.Outcome}";
            }));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ramp {degrees:0} deg: up {up.Outcome}, down {down.Outcome}; {edges}"));
            session.Dispose();
            mesh.Dispose();
            material.Dispose();
        });
    }
}
