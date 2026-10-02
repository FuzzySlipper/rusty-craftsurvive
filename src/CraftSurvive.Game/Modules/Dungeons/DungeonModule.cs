using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Survival;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>Where the player stands with respect to dungeons.</summary>
internal enum DungeonState
{
    Outside,
    Loading,
    Inside,
}

/// <summary>
/// The one owner of going into a dungeon and coming out. A player at a dungeon entrance asks to
/// enter; the dungeon is built in its own finite space a few chunks per update behind the loading
/// screen, then the player is moved in, the sky goes dark and the dungeon's own lights come on.
/// Leaving from its way out - or being carried out by a defeat - returns the player to the entrance
/// and releases the space. Open-world owners (creatures, discovery) wait while the player is inside.
/// </summary>
internal sealed class DungeonModule : IProductModule
{
    /// <summary>How close to a dungeon entrance's centre the player must stand to enter.</summary>
    internal const double EnterReachMetres = 10d;

    /// <summary>How close to the way out the player must stand to leave.</summary>
    internal const double LeaveReachMetres = 3d;

    /// <summary>How many chunks are admitted per update while loading: a bounded slice of each frame.</summary>
    internal const int ChunksPerUpdate = 6;

    /// <summary>How many lights a dungeon may hang, from a pool of retained Engine lights.</summary>
    internal const int MaximumLights = DungeonLayout.MaximumLights;

    /// <summary>How often, in steps, the open world is searched for an entrance near the player.</summary>
    private const long EntranceSearchIntervalSteps = 30;

    private const float LightIntensity = 40f;
    private const float LightRange = 18f;
    private const float LightDecay = 2f;
    private static readonly Vector3 LightColour = new(1f, 0.7f, 0.4f);

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly WorldConditionsModule conditions;
    private readonly DayNightSky sky;
    private readonly ProductUiPublisher ui;
    private readonly List<PoiSite> candidates = [];
    private readonly Light?[] pool = new Light?[MaximumLights];
    private DungeonSpace? space;

    /// <summary>A closed dungeon waiting for one snapshot without its rock before it is released.</summary>
    private DungeonSpace? retiring;
    private DungeonPlan? plan;

    /// <summary>A voxel copy of the whole loaded dungeon, rock included, that the generator walked.</summary>
    private DungeonVolume? walkable;
    private DungeonApproach approach = DungeonApproach.Modules;

    /// <summary>The entrance being entered: its seed, and which of its candidates is loading or loaded.</summary>
    private ulong entranceSeed;
    private int candidateIndex;

    /// <summary>The Engine's verdict on the dungeon last loaded, accepted or not.</summary>
    private DungeonRouteVerdict? routes;
    private long candidatesRefused;
    private Material? rockMaterial;

    /// <summary>
    /// How the next dungeon's voxels are surfaced. Cubes by default; the other looks load the
    /// dungeon as voxels throughout, its sculpted rock as voxel densities the Engine reconstructs.
    /// </summary>
    private DungeonSurface surface = DungeonSurface.Cubes;

    private DungeonState state = DungeonState.Outside;
    private PoiSite? nearbyEntrance;
    private long nextSearchStep;
    private long entered;
    private long refused;
    private string last = "none";
    private DungeonUiFacts? published;

    internal DungeonModule(IEngineContext engine, TerrainWorld terrain, PlayerController player,
        WorldConditionsModule conditions, DayNightSky sky, ProductUiPublisher ui)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
        this.sky = sky ?? throw new ArgumentNullException(nameof(sky));
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    internal DungeonState State => state;

    /// <summary>How many requests to enter or leave were refused, so a caller can tell a refusal from its answer.</summary>
    internal long Refused => refused;

    public void Start() => Publish();

    public void Update(ProductStep step)
    {
        switch (state)
        {
            case DungeonState.Outside:
                if (step.Step >= nextSearchStep)
                {
                    nearbyEntrance = NearestEntrance();
                    nextSearchStep = step.Step + EntranceSearchIntervalSteps;
                }

                break;
            case DungeonState.Loading:
                DungeonSpace loading = space!;
                try
                {
                    loading.Advance(ChunksPerUpdate);
                }
                catch (EngineCallException refusal)
                {
                    // A load the Engine refuses is abandoned with the player still at the entrance.
                    Close($"the dungeon could not be loaded: {refusal.Message}");
                    break;
                }

                if (loading.Loaded && !Accept(loading))
                {
                    break;
                }

                if (loading.Loaded)
                {
                    player.EnterSeparateSpace(loading.Session, DungeonSpace.InSession(loading.Layout.Arrival));
                    sky.Underground(true, conditions.Time);
                    Light(loading.Layout.Lights);
                    state = DungeonState.Inside;
                    entered++;
                    last = string.Create(CultureInfo.InvariantCulture,
                        $"entered {loading.Layout.Name} (candidate {candidateIndex}): {loading.TotalChunks} chunks in {loading.LoadMilliseconds:F0} ms, routes checked in {routes!.PublishMilliseconds + routes.QueryMilliseconds:F0} ms");
                }

                break;
            case DungeonState.Inside:
                if (!player.InSeparateSpace)
                {
                    // A defeat or a teleport took the player out: the dungeon is let go.
                    Close("carried out of the dungeon");
                }

                break;
        }

        Publish();
    }

    /// <summary>A fresh session starts outside, with any dungeon let go.</summary>
    public void Restart()
    {
        if (state != DungeonState.Outside)
        {
            player.ReturnFromSeparateSpace();
            Close("restarted");
        }
    }

    /// <summary>
    /// Releases everything at shutdown. The product has already published its last snapshot and
    /// cleared the sky, so the dungeon is released at once and the sky is left alone.
    /// </summary>
    public void Dispose()
    {
        if (space is not null)
        {
            player.ReturnFromSeparateSpace();
            Light([]);
            space.Dispose();
            space = null;
        }

        AfterAppearanceSnapshot();
        rockMaterial?.Dispose();
        rockMaterial = null;
    }

    /// <summary>Starts loading the dungeon behind the entrance the player stands at.</summary>
    internal string Enter()
    {
        if (state != DungeonState.Outside || player.InSeparateSpace)
        {
            return Refuse("enter refused: already in a dungeon");
        }

        if (NearestEntrance() is not PoiSite entrance)
        {
            return Refuse(string.Create(CultureInfo.InvariantCulture, $"enter refused: no dungeon entrance within {EnterReachMetres:F0} m"));
        }

        // Every entrance has its own dungeon: the world's seed and the entrance's place decide it.
        entranceSeed = DungeonSeed(terrain.SaveIdentity.Seed, entrance);
        string loading = Load(0);
        if (state != DungeonState.Loading)
        {
            return Refuse(loading);
        }

        last = $"{loading} under the entrance at {entrance.X},{entrance.Z}";
        Publish();
        return last;
    }

    /// <summary>Generates one of the entrance's candidates and starts loading it.</summary>
    private string Load(int index)
    {
        DungeonCandidate candidate = DungeonCandidates.Generate(approach, entranceSeed, index);
        candidateIndex = index;
        plan = candidate.Plan;
        walkable = candidate.Walkable;
        try
        {
            space = surface == DungeonSurface.Cubes
                ? new DungeonSpace(engine, terrain, candidate.Layout, candidate.Layout.Rock is null ? null : RockMaterial())
                : new DungeonSpace(engine, terrain, candidate.AllVoxels, null, surface);
        }
        catch (EngineCallException refusal)
        {
            // The Engine would not make the space: nothing is entered, and the game carries on.
            plan = null;
            walkable = null;
            return $"enter refused: the dungeon could not be made: {refusal.Message}";
        }

        state = DungeonState.Loading;
        return $"loading {approach} {candidate.Plan.Mix} dungeon {candidate.Seed:x} (candidate {index}, attempt {candidate.Plan.Attempt}, {candidate.Verdict.Reason})";
    }

    /// <summary>
    /// Asks the Engine whether a loaded candidate can be walked as its flow promises. If it cannot,
    /// the candidate is let go and the entrance's next one starts loading, still behind the loading
    /// screen; when none is left, the entrance is given up and the player stays outside.
    /// </summary>
    private bool Accept(DungeonSpace loaded)
    {
        try
        {
            routes = DungeonRoutes.Check(engine, loaded.Session, walkable!, plan!, NavigationProfile.Player(engine.Spatial, walkable!));
        }
        catch (EngineCallException refusal)
        {
            Close($"the dungeon's routes could not be checked: {refusal.Message}");
            return false;
        }

        if (routes.Walkable)
        {
            return true;
        }

        candidatesRefused++;
        string refusedRoute = routes.FirstRefused?.ToString() ?? "a route";
        if (candidateIndex + 1 >= DungeonCandidates.MaximumCandidates)
        {
            Close($"no dungeon under this entrance can be walked: candidate {candidateIndex} refused {refusedRoute}");
            return false;
        }

        Retire();
        string loading = Load(candidateIndex + 1);
        if (state != DungeonState.Loading)
        {
            Close(loading);
            return false;
        }

        last = $"{loading}; the last refused {refusedRoute}";
        return false;
    }

    /// <summary>What the loaded dungeon adds to the appearance snapshot: a sculpted dungeon's rock.</summary>
    internal IEnumerable<AppearanceFact> AppearanceFacts => space?.AppearanceFacts ?? [];

    /// <summary>
    /// Called by the product root once it has published a snapshot: a closed dungeon's appearance is
    /// no longer in use, so the dungeon can be released.
    /// </summary>
    internal void AfterAppearanceSnapshot()
    {
        retiring?.Dispose();
        retiring = null;
    }

    /// <summary>Chooses how the next dungeon's voxels are surfaced: cubes, dc (worn rock), faceted (chiselled rock), ruined (weathered building) or mc (marched rock).</summary>
    internal string ChooseSurface(string name)
    {
        if (DungeonSurfaces.Parse(name) is not DungeonSurface next)
        {
            return $"surface refused: \"{name}\" is not cubes, dc, faceted, ruined or mc";
        }

        surface = next;
        return $"the next dungeon's voxels are surfaced {surface}";
    }

    /// <summary>Chooses how the next dungeon entered is generated: a (carve and stamp), b (modules) or c (sculpted cave).</summary>
    internal string Choose(string name)
    {
        DungeonApproach? chosen = name switch
        {
            "a" => DungeonApproach.CarveAndStamp,
            "b" => DungeonApproach.Modules,
            "c" => DungeonApproach.SculptedCave,
            _ => null,
        };
        if (chosen is not DungeonApproach next)
        {
            return $"approach refused: \"{name}\" is not a (carve and stamp), b (modules) or c (sculpted cave)";
        }

        approach = next;
        return $"the next dungeon is {approach}";
    }

    /// <summary>The sculpted rock's material, made once and kept for every sculpted dungeon.</summary>
    private Material RockMaterial()
    {
        if (rockMaterial is null)
        {
            rockMaterial = DungeonCollision.CreateRockMaterial(engine);
        }

        return rockMaterial;
    }

    /// <summary>A dungeon's seed: the world's seed mixed with its entrance's cell, so each entrance is its own dungeon.</summary>
    internal static ulong DungeonSeed(ulong worldSeed, PoiSite entrance)
    {
        ulong hash = worldSeed ^ 0x6A09_E667_F3BC_C908UL;
        foreach (long coordinate in (ReadOnlySpan<long>)[entrance.CellX, entrance.CellZ])
        {
            hash = unchecked((hash ^ (ulong)coordinate) * 0x0000_0100_0000_01B3UL);
        }

        return hash;
    }

    /// <summary>Moves the player to a named place in the dungeon (arrival, breach, loot, or floor0..floorN), for a live look.</summary>
    internal string Visit(string place)
    {
        if (state != DungeonState.Inside || plan is null || !player.InSeparateSpace)
        {
            return "visit refused: not in a dungeon";
        }

        DungeonCell? cell = place switch
        {
            "arrival" => plan.Arrival,
            "breach" => plan.Breach,
            "loot" => plan.Loot,
            _ when place.StartsWith("floor", StringComparison.Ordinal)
                && int.TryParse(place.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int floor)
                && floor >= 0 && floor < plan.FloorAnchors.Count => plan.FloorAnchors[floor],
            _ => null,
        };
        if (cell is not DungeonCell at)
        {
            return $"visit refused: \"{place}\" is not arrival, breach, loot or floor0..floor{plan.FloorAnchors.Count - 1}";
        }

        // A plan names its places loosely; the player is put on the standing cell that serves for one,
        // never inside the blocks around it.
        if (walkable is null || DungeonWalk.Nearest(DungeonWalk.Reachable(walkable, plan.Arrival), at) is not DungeonCell stand)
        {
            return $"visit refused: nowhere to stand at the {place}";
        }

        player.MoveWithinSeparateSpace(DungeonSpace.InSession(new Vector3(stand.X + 0.5f, stand.Y, stand.Z + 0.5f)));
        return $"at the {place}: {stand}";
    }

    /// <summary>
    /// The Engine's word on the loaded dungeon: navigation published over the whole space for the
    /// player's body and every route the flow promises asked of it, with where each refused route
    /// first hangs up along the generator's own walk.
    /// </summary>
    internal string Validate()
    {
        if (state != DungeonState.Inside || space is null || plan is null || walkable is null)
        {
            return "validate refused: not in a dungeon";
        }

        DungeonRouteVerdict verdict = DungeonRoutes.Check(engine, space.Session, walkable, plan, NavigationProfile.Player(engine.Spatial, walkable));
        IReadOnlyList<RouteHangUp> hangUps = DungeonRoutes.HangUps(engine, space.Session, walkable, verdict);
        return hangUps.Count == 0 ? verdict.ToString() : $"{verdict} hang-ups: {string.Join("; ", hangUps)}";
    }

    /// <summary>How far a dungeon blast reaches along the player's aim.</summary>
    private const float BlastReachMetres = 24f;

    private const BlockId BlastBrushMaterial = BlockId.Stone;

    /// <summary>The largest dungeon blast, in metres of radius.</summary>
    private const float MaximumBlastRadiusMetres = 8f;

    /// <summary>
    /// Carves a sphere of rock and building out of the loaded dungeon where the player aims, through
    /// the Engine's density brush, and reports what it changed and what the rebuild cost. A look and
    /// cost test for runtime destruction; the dungeon's navigation is not republished after it.
    /// </summary>
    internal string Blast(float radius)
    {
        if (state != DungeonState.Inside || space is null)
        {
            return "blast refused: not in a dungeon";
        }

        if (radius <= 0f || radius > MaximumBlastRadiusMetres)
        {
            return string.Create(CultureInfo.InvariantCulture, $"blast refused: the radius must be above 0 and at most {MaximumBlastRadiusMetres:F0} m");
        }

        Vector3 eye = player.WorldEyePosition;
        SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(
            space.Session, eye, player.AimForward, BlastReachMetres,
            new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));
        if (!hit.Present)
        {
            return string.Create(CultureInfo.InvariantCulture, $"blast refused: nothing within {BlastReachMetres:F0} m along the aim");
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        // A carve fills nothing, but the brush still names a real material slot.
        VoxelDensityReceipt receipt = engine.Voxel.ApplyDensityEdits(new VoxelDensityTransaction(
            space.Session, new[] { VoxelDensityEdit.Sphere(hit.Point, radius, VoxelDensityOperation.Subtract, (uint)BlastBrushMaterial) }));
        double editMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        space.Refresh();
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return string.Create(CultureInfo.InvariantCulture,
            $"blast {radius:0.#} m at {hit.Point.X:F1},{hit.Point.Y:F1},{hit.Point.Z:F1}: {receipt.Status}, {receipt.ChangedVoxels} voxels changed ({receipt.SolidityChanges} cleared), {receipt.RebuiltMeshChunks} chunks rebuilt, meshing {receipt.MeshMicroseconds / 1000d:F1} ms, edit {editMs:F1} ms, {elapsedMs:F1} ms with the redraw");
    }

    /// <summary>Leaves the dungeon from its way out, back to the entrance.</summary>
    internal string Leave()
    {
        if (state != DungeonState.Inside || space is null)
        {
            return Refuse("leave refused: not in a dungeon");
        }

        double distance = Vector3.Distance(player.WorldFeetPosition, DungeonSpace.InSession(space.Layout.Exit));
        if (distance > LeaveReachMetres)
        {
            return Refuse(string.Create(CultureInfo.InvariantCulture, $"leave refused: the way out is {distance:F0} m away"));
        }

        player.ReturnFromSeparateSpace();
        Close("left the dungeon");
        return last;
    }

    internal string Readout()
    {
        string loading = space is DungeonSpace current
            ? string.Create(CultureInfo.InvariantCulture, $" progress={current.Progress:F2} chunks={current.TotalChunks} loadMs={current.LoadMilliseconds:F1}")
            : string.Empty;
        string entrance = nearbyEntrance is PoiSite site ? $"{site.X},{site.Z}" : "none";
        string layout = plan is DungeonPlan current2
            ? $" plan=[{current2.Mix} floors={current2.Floors} attempt={current2.Attempt} arrival={current2.Arrival} breach={current2.Breach} loot={current2.Loot}] {space?.RockReadout}"
            : string.Empty;
        return $"dungeon approach={approach} surface={surface} state={state} candidate={candidateIndex}{loading}{layout} routes=[{routes}] candidatesRefused={candidatesRefused} nearbyEntrance={entrance} entered={entered} refused={refused} last={last}";
    }

    private void Close(string outcome)
    {
        Light([]);
        Retire();
        plan = null;
        walkable = null;
        state = DungeonState.Outside;
        sky.Underground(false, conditions.Time);
        nextSearchStep = 0;
        last = outcome;
        Publish();
    }

    /// <summary>Lets the current space go. Its rock may be in the snapshot already published: it leaves the next one, then goes.</summary>
    private void Retire()
    {
        retiring?.Dispose();
        retiring = space;
        space = null;
    }

    private string Refuse(string outcome)
    {
        refused++;
        last = outcome;
        Publish();
        return outcome;
    }

    /// <summary>The dungeon entrance nearest the player in the open world, if one is within reach.</summary>
    private PoiSite? NearestEntrance()
    {
        if (player.InSeparateSpace)
        {
            return null;
        }

        Vector3 feet = player.WorldFeetPosition;
        candidates.Clear();
        terrain.Recipe.Placement.CollectSitesNear((long)Math.Floor(feet.X), (long)Math.Floor(feet.Z), (long)Math.Ceiling(EnterReachMetres) + 1, candidates);
        return candidates
            .Where(site => site.Kind == PoiKind.DungeonEntrance)
            .Select(site => (Site: site, Distance: Math.Sqrt(Math.Pow(site.X + 0.5 - feet.X, 2) + Math.Pow(site.Z + 0.5 - feet.Z, 2))))
            .Where(found => found.Distance <= EnterReachMetres)
            .OrderBy(found => found.Distance)
            .Select(found => (PoiSite?)found.Site)
            .FirstOrDefault();
    }

    /// <summary>Hangs the dungeon's lights from the pool; the pool's spare lights are put out.</summary>
    private void Light(IReadOnlyList<Vector3> lights)
    {
        for (int slot = 0; slot < MaximumLights; slot++)
        {
            bool on = slot < lights.Count;
            LightRequest request = new(
                ProductIds.DungeonLightBase + (ulong)slot,
                false,
                0UL,
                new LightDescriptor(LightKind.Point, LightColour, LightIntensity, on,
                    on ? DungeonSpace.InSession(lights[slot]) : Vector3.Zero,
                    -Vector3.UnitY, true, LightRange, LightDecay, 0f, 0f, LightShadowIntent.Disabled));
            if (pool[slot] is Light light)
            {
                engine.Graphics.UpdateLight(new LightUpdateRequest(light, request));
            }
            else if (on)
            {
                pool[slot] = engine.Graphics.CreateLight(request);
            }
        }
    }

    private void Publish()
    {
        double progress = space?.Progress ?? 0d;
        string prompt = state switch
        {
            DungeonState.Loading => "Descending...",
            DungeonState.Inside when space is not null => Vector3.Distance(player.WorldFeetPosition, DungeonSpace.InSession(space.Layout.Exit)) <= LeaveReachMetres
                ? "The way out is here"
                : string.Empty,
            _ => nearbyEntrance is null ? string.Empty : "A dungeon entrance is here",
        };
        DungeonUiFacts facts = new(
            state.ToString().ToLowerInvariant(),
            Math.Round(progress, 2),
            prompt,
            state == DungeonState.Outside && nearbyEntrance is not null,
            state == DungeonState.Inside && prompt.Length > 0,
            last);
        if (published != facts)
        {
            published = facts;
            ui.PublishDungeon(facts);
        }
    }
}
