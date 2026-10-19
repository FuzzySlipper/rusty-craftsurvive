using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Debugging;

/// <summary>Thin live-debug adapters over ordinary CraftSurvive and Engine operations.</summary>
public sealed class CraftDebugModule : IDebugCommandModule
{
    private readonly Func<PlayerController> playerSource;
    private PlayerController player => playerSource();
    private readonly Func<CreatureModule> creaturesSource;
    private CreatureModule creatures => creaturesSource();
    private readonly Func<TerrainWorld> terrainSource;
    private TerrainWorld terrain => terrainSource();
    private readonly DebugExecutionContext execution;

    internal CraftDebugModule(
        Func<PlayerController> player,
        Func<CreatureModule> creatures,
        Func<TerrainWorld> terrain,
        DebugExecutionContext execution)
    {
        this.playerSource = player;
        this.creaturesSource = creatures;
        this.terrainSource = terrain;
        this.execution = execution;
    }

    [DebugCommand("craft.player.landscape", Description = "Assisted: visits a landscape study (canyon, uplands or tundra) as the menu's Visit landscape does; the player readout reports its admission.")]
    public string VisitLandscape(string name) => player.VisitLandscape(name) ? $"visiting {name}" : $"refused: no study {name} here";

    [DebugCommand("craft.player.teleport", Description = "Moves the live player where a standing body fits: the point asked for, else the ground of that column.")]
    public string Teleport(double x, double y, double z) =>
        player.Bound?.Invoke(x, z) is { } side && side != BoundSide.Open
            ? FormattableString.Invariant($"refused: {x:F3},{z:F3} lies past the world's bounds ({side})")
        : player.Teleport(x, y, z) is PlayerRuntimeComponent state
            ? FormattableString.Invariant($"player={state.X:F3},{state.Y:F3},{state.Z:F3}")
            : FormattableString.Invariant($"refused: a standing body fits neither at {x:F3},{y:F3},{z:F3} nor on the ground below it");

    [DebugCommand("craft.player.look", Description = "Assisted: turns the live view by degrees of yaw and pitch through the player's own look rules, for aimed captures.")]
    public string LookBy(double yawDegrees, double pitchDegrees)
    {
        player.LookBy(yawDegrees, pitchDegrees);
        return player.DebugReadout();
    }

    [DebugCommand("craft.player.climbprobe", Description = "Assisted (#10126): reads the natural face toward a heading (degrees: 0 faces -z, 90 faces +x) as a climber would - whether it can be held, how high it goes, the grip, and what stops it - without climbing.")]
    public string ClimbProbe(float headingDegrees)
    {
        float r = headingDegrees * MathF.PI / 180f;
        return player.DescribeClimb(new System.Numerics.Vector3(MathF.Sin(r), 0f, -MathF.Cos(r)));
    }

    [DebugCommand("craft.player.readout", Description = "Reads the latest admitted player input, fixed-step, motion, and pose facts.")]
    public string ReadPlayer() => player.DebugReadout();

    [DebugCommand("craft.player.continuation", Description = "Reports what the last session's continuation restored and how its save is going.")]
    public string Continuation() => player.ContinuationReadout();

    [DebugCommand("craft.player.attack", Description = "Swings at the nearest creature, as the attack key does.")]
    public string Attack() => creatures.PlayerAttack();

    [DebugCommand("craft.player.strike", Description = "Deals damage to the player through their vitals, so an encounter can be lost as well as won.")]
    public string StrikePlayer(int damage)
    {
        PlayerDefeatState state = player.Vitals.TakeHit(damage, player.CurrentStep);
        return FormattableString.Invariant(
            $"health={state.Health}/{state.MaximumHealth} defeats={state.Defeats} respawnStep={state.RespawnTick} outcome={player.Vitals.Outcome(player.CurrentStep)}");
    }

    [DebugCommand("craft.terrain.groundcover", Description = "Assisted (#9911): sets how far ground cover grows (metres, at least 128), as the Ground cover option does for the session, and reads the vegetation.")]
    public string TerrainGroundCover(double metres)
    {
        terrain.GroundCoverMetres = metres;
        return terrain.VegetationReadout();
    }

    [DebugCommand("craft.vegetation.readout", Description = "Reads the ground cover and trees: reach, the voxel scatter, both vegetation tile fields and the tree tiles (tiles, pending, copies, over budget), the plant tiles (plants, deadwood and saplings) and the forest floor's shade grass.")]
    public string VegetationReadout() => terrain.VegetationReadout();

    [DebugCommand("craft.vegetation.deadwood", Description = "Assisted (#10034): the deadwood of a kind (log, stump, fallen-trunk, or any) nearest a point among those streamed, with mesh, position, turn and size; each is solid within 64 m of the player.")]
    public string Deadwood(string kind, double x, double z) => terrain.DeadwoodNearest(kind == "any" ? "" : kind, x, z, DeadwoodListed);

    private const int DeadwoodListed = 5;

    [DebugCommand("craft.terrain.grass", Description = "Assisted (#10038): draws grass as blades (the game's look) or as the painted cards it was, everywhere it grows, for the session; read reports which.")]
    public string Grass(string look) => look switch
    {
        "blades" => terrain.GrassBlades(true),
        "cards" => terrain.GrassBlades(false),
        "read" => terrain.GrassLook,
        _ => $"refused: expected blades, cards or read, not {look}",
    };

    [DebugCommand("craft.vegetation.solid", Description = "Assisted (#10034): makes fallen logs and stumps solid (on, the game's rule) or not (off) for the session, to compare walking over them; read reports it.")]
    public string DeadwoodSolid(string state)
    {
        if (state is "on" or "off") terrain.DeadwoodSolid = state == "on";
        else if (state != "read") return $"refused: expected on, off or read, not {state}";
        return FormattableString.Invariant($"deadwoodSolid={terrain.DeadwoodSolid} deadwoodColliders={terrain.DeadwoodColliders}");
    }

    [DebugCommand("craft.vegetation.sites", Description = "Lists the forest columns nearest a point from the generated map, with ground height and local tree counts. Does not move the player.")]
    public string VegetationSites(double x, double z)
    {
        const int MaximumSites = 4;
        const long CountRadius = 64;
        TerrainRecipe recipe = terrain.Recipe;
        WorldMap map = recipe.Map;
        return string.Join(" | ", Enumerable.Range(0, map.Grid.Count)
            .Where(i => recipe.Forest.Canopy(map.Grid.X(i), map.Grid.Z(i), map.Sample(map.Grid.X(i), map.Grid.Z(i))) >= Forest.FloorFromCanopy)
            .OrderBy(i => Math.Pow(map.Grid.X(i) - x, 2) + Math.Pow(map.Grid.Z(i) - z, 2))
            .Take(MaximumSites).Select(i =>
            {
                long sx = (long)map.Grid.X(i), sz = (long)map.Grid.Z(i);
                List<TerrainTree> local = [];
                recipe.TreesIn(sx - CountRadius, sz - CountRadius, sx + CountRadius, sz + CountRadius, local);
                return FormattableString.Invariant($"{WorldMap.Biome(recipe.Geography(sx, sz))} at={sx},{recipe.SurfaceAt(sx, sz) + 2},{sz} trees128m={local.Count}");
            }));
    }

    [DebugCommand("craft.terrain.scene", Description = "Reads the current Engine-owned voxel scene facts.")]
    public string ReadTerrainScene()
    {
        VoxelSceneReadout scene = terrain.ReadScene();
        return string.Create(CultureInfo.InvariantCulture,
            $"present={scene.Present};revision={scene.SourceRevision};chunks={scene.ResidentChunkCount};nearRadius={terrain.NearRadii.Requested}/{terrain.NearRadii.Retained};solidVoxels={scene.SolidVoxelCount};multiLoopCells={scene.MultiLoopCells};{terrain.LevelOfDetailReadout()};{terrain.FarFieldReadout()};{terrain.ScatterReadout()};{terrain.TreesReadout()}");
    }

    [DebugCommand("craft.moss.strength", Description = "Tuning (#10044): sets the surface moss's strength on every stone place and field stone (0 none, 1 accents, 2 moss, 3 overgrown) and reads it.")]
    public string MossStrength(double strength) => terrain.SetMossStrength((float)strength);

    [DebugCommand("craft.moss.overgrow", Description = "Assisted (#10044): sets one place's overgrowth by site id (craft.places.readout lists them, e.g. poi:-1:0) over its climate's: extra moss strength 0 (ordinary) to 6, deepening its stain and covering its walls with the ivy blanket; a negative value returns it to its climate's.")]
    public string MossOvergrow(string siteId, double extra) => terrain.SetOvergrowth(siteId, (float)extra);

    [DebugCommand("craft.moss.readout", Description = "Reads the surface moss (#10044): its strength, the materials growing it and the origin it was last given.")]
    public string MossReadout() => terrain.Moss.Readout();

    [DebugCommand("craft.places.readout", Description = "Reads the overworld places drawn as kit meshes (#9992): sites and pieces in the near ground, kit pieces loaded, and pieces in the walking collision.")]
    public string ReadPlaces() => terrain.PlacesReadout();

    [DebugCommand("craft.terrain.vertexocclusion", Description = "Assisted (#9506 exploration): darkens the overworld's surface vertices by the solid voxels around them at the given strength (0 off, 1 full); every resident chunk remeshes.")]
    public string TerrainVertexOcclusion(float strength)
    {
        VoxelSceneReadout scene = terrain.ConfigureVertexOcclusion(strength);
        return string.Create(CultureInfo.InvariantCulture, $"vertexOcclusion={strength:F2};chunks={scene.ResidentChunkCount}");
    }

    [DebugCommand("craft.terrain.scatter", Description = "Assisted (#9546): grows grass clumps on the overworld's grass at this density per square metre (0 removes them). Shrubs and ferns are the forest's (#10032).")]
    public string TerrainScatter(float grassPerSquareMetre) => terrain.TuneScatter(grassPerSquareMetre);

    [DebugCommand("craft.terrain.trees", Description = "Assisted (#9665): the nearest trees of a species (a flora catalog name: oak, beech, stone-pine, ...) to a point, searching outward up to a radius in metres, for aimed captures.")]
    public string FindTrees(string kind, double x, double z, double radiusMetres)
    {
        WorldGen.FloraSpecies? wanted = WorldGen.FloraCatalog.Shared.Species.FirstOrDefault(s => s.Role == WorldGen.FloraRole.Tree
            && string.Equals(s.Name, kind, StringComparison.OrdinalIgnoreCase));
        if (wanted is null) return $"refused: no tree species {kind}";
        const double Ring = 64;
        List<WorldGen.TerrainTree> found = [];
        for (double reach = Ring; reach <= radiusMetres && found.Count == 0; reach += Ring)
        {
            List<WorldGen.TerrainTree> trees = [];
            terrain.Recipe.TreesIn((long)(x - reach), (long)(z - reach), (long)(x + reach), (long)(z + reach), trees);
            found = [.. trees.Where(tree => tree.Species == wanted.Id).OrderBy(tree => Math.Pow(tree.X - x, 2) + Math.Pow(tree.Z - z, 2)).Take(5)];
        }

        return found.Count == 0
            ? FormattableString.Invariant($"no {wanted.Name} within {radiusMetres} m of {x},{z}")
            : string.Join(';', found.Select(tree => FormattableString.Invariant($"{wanted.Name} {tree.X},{tree.GroundY},{tree.Z} scale={tree.Scale:F2}")));
    }

    [DebugCommand("craft.build.piece", Description = "Assisted (#9729): places a build piece (wall, doorway, window, post, beam, floor, roof, stairs) of a material (planks, timber, masonry, shingles) with its anchor at world x,y,z (snapped to 0.25 m), turned this many quarter turns.")]
    public string PlacePiece(string kind, string material, double x, double y, double z, long turn) =>
        player.Pieces is not { } pieces ? "no pieces"
        : !Building.PieceCatalog.TryParse(kind, out Building.PieceKind pieceKind) ? $"refused: no piece {kind}"
        : !Building.PieceCatalog.TryParse(material, out Building.PieceMaterial pieceMaterial) ? $"refused: no material {material}"
        : pieces.PlaceAt(pieceKind, pieceMaterial, x, y, z, (int)turn);

    [DebugCommand("craft.build.mode", Description = "Assisted (#9811): sets building to off, pieces or terrain (the brush), as T steps it; out of building no ghost shows and nothing is placed.")]
    public string BuildMode(string mode)
    {
        if (player.Pieces is not Building.BuildPieceModule pieces) return "refused: no building here";
        Building.BuildMode? chosen = mode.ToLowerInvariant() switch
        {
            "off" => Building.BuildMode.Off,
            "pieces" => Building.BuildMode.Pieces,
            "terrain" or "earthworks" => Building.BuildMode.Earthworks,
            _ => null,
        };
        if (chosen is not Building.BuildMode set) return "refused: choose off, pieces or terrain";
        pieces.SetMode(set);
        return pieces.Readout();
    }

    [DebugCommand("craft.build.pieces", Description = "Reads the build pieces: how many stand, the selection, the ghost's anchor and the save.")]
    public string ReadPieces() => player.Pieces?.Readout() ?? "no pieces";

    [DebugCommand("craft.build.aim", Description = "Reads what the player's view meets: the nearest build piece and the ground, with distances (#9729).")]
    public string ReadPieceAim() => player.Pieces?.AimReadout(player.LocalAhead(0f), player.AimForward) ?? "no pieces";

    [DebugCommand("craft.build.remnants", Description = "Lists the build remnants (#9731): each bitten piece, its craters and its collision boxes.")]
    public string ReadRemnants() => player.Pieces?.RemnantsReadout() ?? "no pieces";

    [DebugCommand("craft.build.piecesclear", Description = "Assisted (#9729): removes every build piece.")]
    public string ClearPieces()
    {
        player.Pieces?.Clear();
        return player.Pieces?.Readout() ?? "no pieces";
    }

    [DebugCommand("craft.build.study", Description = "Assisted (#9684): builds the building look study, one cabin as 1 m cubes, 1 m blocky, 0.5 m and 0.25 m dual contouring, and snap meshes, in a row along +X from x,z.")]
    public string BuildStudy(double x, double z) => terrain.BuildStudy(x, z, clear: false);

    [DebugCommand("craft.build.studycollide", Description = "Assisted (#9684): as craft.build.study, with the voxel cabins colliding, to test a second colliding session beside the walking world.")]
    public string BuildCollidingStudy(double x, double z) => terrain.BuildStudy(x, z, clear: false, collide: true);

    [DebugCommand("craft.build.studyclear", Description = "Assisted (#9684): removes the building look study.")]
    public string ClearBuildStudy() => terrain.BuildStudy(0, 0, clear: true);

    [DebugCommand("craft.water.readout", Description = "Reads the near water surfaces (#9877): tiles held and drawn, quads, built and retired, and their reach.")]
    public string WaterReadout() => terrain.WaterReadout();

    [DebugCommand("craft.water.at", Description = "Reads the water owner's sample at a world column (#9877): surface, bed, depth, body, stage, ice and current (#9879).")]
    public string WaterAt(double x, double z)
    {
        Water.WaterSample s = terrain.Water.Sample(x, z);
        return FormattableString.Invariant($"surface={s.Surface:F2};bed={s.Bed:F2};depth={s.Depth:F2};body={s.Body};stage={s.Stage:F2};ice={s.Ice};current={s.CurrentX:F2},{s.CurrentZ:F2}");
    }

    [DebugCommand("craft.water.study", Description = "Assisted (#9819): floods the hollow at x,z to a level (metres) within a radius, as voxel water cells (mode voxel) or one water surface mesh at the level (mode surface); mode river clears a river's voxel water within the radius (for good) and draws it as a skin at its unfloored surface (level unused); mode clear removes both; mode find lists the deepest closed hollows within level metres of x,z, each bounded by the radius.")]
    public string WaterStudy(string mode, double x, double z, double level, long radius) => terrain.WaterStudy(mode, x, z, level, radius);

    [DebugCommand("craft.water.visual", Description = "Water appearance comparison: waves (the sea state's, #9902), flow (flat flowing water), velocity (red +X, green +Z, blue slow) or baseline (the Engine's water alone).")]
    public string WaterVisual(string mode) => terrain.WaterVisual(mode);

    [DebugCommand("craft.water.wind", Description = "Assisted (#9902): holds the waves' wind at a speed in m/s (2 calm, 20 a gale) whatever the weather; a negative speed follows the weather again. Boats and swimmers ride what is drawn.")]
    public string WaterWind(double speed)
    {
        terrain.Sea.HeldWind = speed < 0 ? null : speed;
        return terrain.Sea.Readout();
    }

    [DebugCommand("craft.water.lakes", Description = "Lists the continent's lakes (#9876) nearest a world point, up to a count: id, name, kind, area, where the deepest node lies, level, and whether closed - with the waves a gale raises on each (#9902).")]
    public string WaterLakes(double x, double z, int count)
    {
        WorldGen.WorldMap map = terrain.Recipe.Map;
        return string.Join(" | ", map.Lakes
            .Select(lake => (lake, node: lake.Nodes.MinBy(n => map.Fields.Elevation[n])))
            .OrderBy(l => Math.Pow(map.Grid.X(l.node) - x, 2) + Math.Pow(map.Grid.Z(l.node) - z, 2))
            .Take(Math.Max(1, count))
            .Select(l => string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{l.lake.Id} {(l.lake.Name.Length > 0 ? l.lake.Name : "-")} {l.lake.Kind} {l.lake.AreaSquareKilometres:F0}km2 at={map.Grid.X(l.node):F0},{map.Grid.Z(l.node):F0} level={l.lake.Level:F1}{(l.lake.Closed ? " closed" : "")} gale={Water.SeaWaves.SignificantHeight(Water.SeaState.WindFor(1), Water.SeaWaves.LakeFetchMetres(l.lake.AreaSquareKilometres)):F2}m")));
    }

    [DebugCommand("craft.water.waves.at", Description = "Reads the waves at a world column (#9902): the water's exposure (fetch, swell, coverage), its significant height in the wind now, and how far the drawn surface stands over the rest level.")]
    public string WaterWavesAt(double x, double z)
    {
        Water.WaterSample s = terrain.Water.Sample(x, z);
        Water.WaveExposure e = terrain.Water.Exposure(s);
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"body={s.Body} fetch={e.FetchMetres:F0}m swell={e.Swell:F2} coverage={e.Coverage:F2} height={terrain.Sea.Height(e.FetchMetres):F2}m lift={terrain.WaveLift(x, z, s):F3}m {terrain.Sea.Readout()}");
    }

    [DebugCommand("craft.water.flow", Description = "Starts a 128 m Engine WaterField around world x,z, using the terrain bed and boundary stages. Its current drives appearance only; gameplay water is unchanged.")]
    public string WaterFlow(double x, double z) => terrain.StartWaterFlow(x, z);

    [DebugCommand("craft.water.flow.stop", Description = "Stops the local visual flow study and restores regional current inputs.")]
    public string StopWaterFlow() => terrain.StopWaterFlow();

    [DebugCommand("craft.water.flow.at", Description = "Compares the Engine field's current and depth with the regional water at world x,z.")]
    public string WaterFlowAt(double x, double z) => terrain.WaterFlowAt(x, z);

    [DebugCommand("craft.terrain.fartrees", Description = "Assisted: how far trees are drawn, in metres (144 to 2000), for the session; the view distance sets it again.")]
    public string SetFarTrees(long metres) => terrain.SetFarTrees(metres);

    [DebugCommand("craft.terrain.generation", Description = "Reads the generator's version, live fingerprint, golden status and chunk cache.")]
    public string ReadGeneration() => terrain.GenerationReadout();

    [DebugCommand("craft.terrain.edit", Description = "Reads the latest terrain target and typed edit outcome.")]
    public string ReadTerrainEdit() => player.TerrainEditReadout();

    [DebugCommand("craft.terrain.timing", Description = "Switches recording of each edit's cost by part (1 on, 0 off); the blast readout reports it.")]
    public string SetEditTiming(long enabled)
    {
        terrain.EditTimingEnabled = enabled != 0;
        return $"editTiming={(terrain.EditTimingEnabled ? "on" : "off")}";
    }

    [DebugCommand("craft.runtime", Description = "Reads the latest committed Rust host lifecycle and binding facts.")]
    public string ReadRuntime()
    {
        DebugExecutionSnapshot snapshot = execution.Snapshot;
        return string.Create(CultureInfo.InvariantCulture,
            $"state={snapshot.LifecycleState};generation={snapshot.Generation?.ToString(CultureInfo.InvariantCulture) ?? "unknown"};controlRevision={snapshot.ControlRevision?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
    }
}
