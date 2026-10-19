using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Feedback;
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
    /// <summary>A dungeon lamp's light, linear as the Engine draws it (#9844), tuned by eye.</summary>
    private static readonly Vector3 LightColour = new(1f, 0.7f, 0.4f);

    /// <summary>
    /// Every torch asks to cast and the Engine's shadow budget keeps the nearest; a torch lights
    /// a room, so it gets more texels than a placed lamp, and a soft edge.
    /// </summary>
    private const uint LightShadowResolution = 512;
    private const int LightShadowPriority = 1;

    /// <summary>
    /// The dungeon is lit from what is there (#9544): an irradiance probe volume over its whole
    /// space, baked by the Engine from the torches' bounce over the sky's faint underground fill,
    /// so a torch warms the walls around it and a chamber away from every torch falls toward the
    /// fill. Two metres between probes suits rooms a few metres across and walls a voxel thick or more.
    /// </summary>
    private const float ProbeSpacingMetres = 2f;
    private const uint ProbeBounces = 2;
    private const float ProbeMarginMetres = 4f;

    private readonly IEngineContext engine;
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly WorldConditionsModule conditions;
    private readonly DayNightSky sky;
    private readonly ProductUiPublisher ui;
    private readonly List<PoiSite> candidates = [];
    private readonly Light?[] pool = new Light?[MaximumLights];

    /// <summary>Each torch burns a fire a little below its light (#9547).</summary>
    private readonly FireEmitters fires;
    private const float FireBelowLight = 0.35f;
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
    /// How the next dungeon's voxels are surfaced. Faceted by default: worn rock reconstructed with
    /// flat faces, the building kept on the grid. The other looks are for comparing; every look but
    /// cubes loads the sculpted rock as voxel densities the Engine reconstructs.
    /// </summary>
    private DungeonSurface surface = DungeonSurface.Faceted;

    /// <summary>A seed the next dungeon entered uses in place of its entrance's own, or null: for looking at a chosen dungeon.</summary>
    private ulong? seedOverride;

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
        fires = new FireEmitters(engine, ProductIds.DungeonFireBase, "craftsurvive.torch", MaximumLights);
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
                    // A finer study grid (#10094) has many more chunks of the same metres; it loads them as fast per metre.
                    loading.Advance((int)(ChunksPerUpdate / Math.Pow(loading.Layout.VoxelSize, 3)));
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
                    IndirectLight(loading.Layout.Volume, loading.Layout.VoxelSize);
                    state = DungeonState.Inside;
                    entered++;
                    last = string.Create(CultureInfo.InvariantCulture,
                        $"entered {loading.Layout.Name} (candidate {candidateIndex}): {loading.TotalChunks} chunks in {loading.LoadMilliseconds:F0} ms, routes checked in {(routes is null ? 0 : routes.PublishMilliseconds + routes.QueryMilliseconds):F0} ms");
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
        fires.Dispose();
        foreach (Light? light in pool) light?.Dispose();
        Array.Clear(pool);
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
        entranceSeed = seedOverride ?? DungeonSeed(terrain.SaveIdentity.Seed, entrance);
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
        DungeonCandidate candidate = DungeonCandidates.Generate(approach, entranceSeed, index, DungeonSurfaces.RockAsMesh(surface) ? 1d : studyVoxelSize);
        candidateIndex = index;
        plan = candidate.Plan;
        walkable = candidate.Walkable;
        try
        {
            // The rock study (#10090): cubes keep the old rock texture; the mesh routes take the cave material, as the dc voxels do.
            space = DungeonSurfaces.RockAsMesh(surface)
                ? new DungeonSpace(engine, terrain, candidate.Layout,
                    candidate.Layout.Rock is null ? null : surface == DungeonSurface.Cubes ? RockMaterial() : terrain.CaveRockMaterial, surface)
                : new DungeonSpace(engine, terrain, candidate.AllVoxels, StudyRockDetail(), surface, RockByAge());
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
        // A natural study space (#10090, #10094) is entered unchecked: its passages are for looking, and are checked by other means.
        if (loaded.Layout.Natural)
        {
            routes = null;
            return true;
        }

        try
        {
            routes = DungeonRoutes.Check(engine, loaded.Session, walkable!, plan!, NavigationProfile.Player(engine.Spatial, walkable!));
        }
        catch (EngineCallException refusal)
        {
            Close($"the dungeon's routes could not be checked: {refusal.Message}");
            return false;
        }

        if (routes.Walkable || keepUnwalkable)
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

    /// <summary>Makes every dungeon entered use a chosen seed (the bank's seeds), or clears that with "entrance".</summary>
    internal string ChooseSeed(string text)
    {
        if (text == "entrance")
        {
            seedOverride = null;
            return "dungeons use their entrance's own seed";
        }

        if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong chosen))
        {
            return $"seed refused: \"{text}\" is not a number or \"entrance\"";
        }

        seedOverride = chosen;
        return string.Create(CultureInfo.InvariantCulture, $"dungeons entered use seed {chosen}");
    }

    /// <summary>The cave detail study (#10094): the shader the voxel rock is drawn through, its parameters, and the materials made for it.</summary>
    private const string RockDetailShaderPath = "shaders/cave-rock-detail.wgsl";
    /// <summary>
    /// Dungeon rock's default look (owner, 2026-10-19): 3D pixels - facets 6 cm across leaning half a unit in two steps,
    /// each one flat colour of the cave palette, growing with distance so they stay a few screen pixels across, leaning
    /// partly with their neighbours.
    /// </summary>
    private static readonly Vector4 DefaultRockDetail = new(0f, 3f, 0.06f, 0.5f);
    private static readonly Vector4 DefaultRockPixels = new(0.06f, 2f, 2f, 0.6f);
    private Vector4? rockDetail = DefaultRockDetail;
    private RenderResource? rockDetailShader;
    private readonly Dictionary<(Vector4 Detail, bool Palette, Vector4 Pixels, Vector4 Age), Material> rockDetailMaterials = [];
    /// <summary>The detail shader's colour cell (metres) and facet step count; zero for none and the default.</summary>
    private Vector4 rockPixels = DefaultRockPixels;
    /// <summary>Whether the detail shader also replaces the painted texture's colour with the cave's own palette.</summary>
    private bool rockPalette = true;

    /// <summary>
    /// Draws the next dungeon's voxel rock through the detail shader: grain strength and frequency (per metre), facet
    /// size (metres, 0 for none) and facet strength; all zero draws it plainly again.
    /// </summary>
    internal string StudyRockDetail(float grain, float frequency, float facet, float facetStrength)
    {
        rockDetail = grain == 0f && facet == 0f ? null : new Vector4(grain, frequency, facet, facetStrength);
        return rockDetail is Vector4 v
            ? string.Create(CultureInfo.InvariantCulture, $"the next dungeon's voxel rock has grain {v.X} at {v.Y}/m and facets {v.Z} m at {v.W}")
            : "the next dungeon's voxel rock is drawn plainly";
    }

    /// <summary>The detail shader's colour cell (metres, 0 none: each cell one flat colour) and facet step count (0 the default).</summary>
    /// <param name="distance">What cells do as they shrink toward a screen pixel: 0 nothing, 1 fade, 2 grow.</param>
    /// <param name="coherence">How much neighbouring facets lean the same way, 0 to 1.</param>
    internal string StudyRockPixels(float colourCell, float facetSteps, float distance, float coherence)
    {
        rockPixels = new Vector4(colourCell, facetSteps, distance, coherence);
        return string.Create(CultureInfo.InvariantCulture,
            $"the next dungeon's voxel rock has colour cells of {colourCell} m, facets in {facetSteps} steps, distance mode {distance}, coherence {coherence}");
    }

    /// <summary>Colours the next dungeon's voxel rock from the cave's own palette (strata, mottling, wet floors) instead of the painted texture.</summary>
    internal string StudyRockPalette(bool palette)
    {
        rockPalette = palette;
        return palette ? "the next dungeon's voxel rock takes the cave palette" : "the next dungeon's voxel rock takes its painted texture's colour";
    }

    private Material? StudyRockDetail() => RockLook(Vector4.Zero);

    /// <summary>
    /// The cave rock's look for each age class (#10094): what a voxel's rock block says of the space it faces - plain,
    /// washed by the active water, patinated by long dry ages, or sediment - as the shader's age parameters.
    /// </summary>
    private static readonly (BlockId Block, Vector4 Age)[] AgeLooks =
    [
        (KarstCave.PlainRock, Vector4.Zero),
        (KarstCave.WashedRock, new Vector4(0f, 1f, 0f, 0f)),
        (KarstCave.PatinatedRock, new Vector4(1f, 0f, 0f, 0f)),
        (KarstCave.SedimentRock, new Vector4(0f, 0f, 1f, 0f)),
    ];

    private Dictionary<BlockId, Material>? RockByAge()
    {
        Dictionary<BlockId, Material> looks = [];
        foreach ((BlockId block, Vector4 age) in AgeLooks)
        {
            if (RockLook(age) is Material look)
            {
                looks[block] = look;
            }
        }

        return looks.Count > 0 ? looks : null;
    }

    private Material? RockLook(Vector4 age)
    {
        if (rockDetail is null && !rockPalette)
        {
            return null;
        }

        Vector4 detail = rockDetail ?? Vector4.Zero;
        if (!rockDetailMaterials.TryGetValue((detail, rockPalette, rockPixels, age), out Material? material))
        {
            rockDetailShader ??= engine.Graphics.OpenResource(new RenderResourceRequest(RockDetailShaderPath)).Handle;
            // The palette needs the layout's height (the session lies below the open world) and the beds' dip.
            Vector4 palette = rockPalette ? new Vector4(1f, -DungeonCollision.Origin.Y, KarstCave.BedDip.X, KarstCave.BedDip.Y) : Vector4.Zero;
            material = terrain.CaveRockWithShader(new MaterialShader(rockDetailShader, detail, palette, rockPixels, age));
            rockDetailMaterials[(detail, rockPalette, rockPixels, age)] = material;
        }

        return material;
    }

    /// <summary>The natural cavern study's voxel side in metres (#10094): 1, or finer for micro-voxels.</summary>
    private double studyVoxelSize = 1d;

    internal string StudyVoxelSize(double metres)
    {
        if (metres is not (1d or 0.5d or 0.25d))
        {
            return "voxel size refused: 1, 0.5 or 0.25 metres";
        }

        studyVoxelSize = metres;
        return string.Create(CultureInfo.InvariantCulture, $"the natural cavern study is built in {metres} m voxels");
    }

    /// <summary>For studies (#10090): enter a candidate even when its routes fail, so looks compare on the same layout.</summary>
    private bool keepUnwalkable;

    internal string KeepUnwalkable(bool keep)
    {
        keepUnwalkable = keep;
        return keep ? "candidates are kept even when their routes fail (study only)" : "candidates whose routes fail are refused again";
    }

    /// <summary>For studies (#10090): a flat diagnostic fill and fog density underground, so shapes can be compared; negative restores the game's.</summary>
    internal string StudyLight(float fill, float fogDensity) => sky.StudyLight(fill, fogDensity);

    /// <summary>Chooses how the next dungeon's voxels are surfaced: cubes, dc (worn rock), faceted (chiselled rock), ruined (weathered building) or mc (marched rock).</summary>
    internal string ChooseSurface(string name)
    {
        if (DungeonSurfaces.Parse(name) is not DungeonSurface next)
        {
            return $"surface refused: \"{name}\" is not cubes, dc, faceted, ruined, mc, mesh, fine, pieced or micro";
        }

        surface = next;
        return $"the next dungeon's voxels are surfaced {surface}";
    }

    /// <summary>Chooses how the next dungeon entered is generated: a (carve and stamp), b (modules), c (sculpted cave), v (chasm sketch) , s (shaft sketch) or n (natural cavern study).</summary>
    internal string Choose(string name)
    {
        DungeonApproach? chosen = name switch
        {
            "a" => DungeonApproach.CarveAndStamp,
            "b" => DungeonApproach.Modules,
            "c" => DungeonApproach.SculptedCave,
            "v" => DungeonApproach.Vertical,
            "s" => DungeonApproach.Shaft,
            "n" => DungeonApproach.Natural,
            "k" => DungeonApproach.Karst,
            _ => null,
        };
        if (chosen is not DungeonApproach next)
        {
            return $"approach refused: \"{name}\" is not a (carve and stamp), b (modules), c (sculpted cave), v (chasm sketch), s (shaft sketch), n (natural cavern study) or k (karst cave study)";
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
            _ => Cell(place),
        };
        if (cell is not DungeonCell at)
        {
            return $"visit refused: \"{place}\" is not arrival, breach, loot, floor0..floor{plan.FloorAnchors.Count - 1} or a cell x,y,z";
        }

        // A plan names its places loosely; the player is put on the standing cell that serves for one,
        // never inside the blocks around it.
        // A natural study space is put exactly where asked: its floors are too uneven for the walk's standing places.
        DungeonCell? standing = space?.Layout.Natural == true ? at
            : walkable is null ? null : DungeonWalk.Nearest(DungeonWalk.Reachable(walkable, plan.Arrival), at);
        if (standing is not DungeonCell stand)
        {
            return $"visit refused: nowhere to stand at the {place}";
        }

        player.MoveWithinSeparateSpace(DungeonSpace.InSession(new Vector3(stand.X + 0.5f, stand.Y, stand.Z + 0.5f)));
        return $"at the {place}: {stand}";
    }

    /// <summary>A layout cell written x,y,z, or null.</summary>
    private static DungeonCell? Cell(string text)
    {
        string[] parts = text.Split(',');
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z)
            ? new DungeonCell(x, y, z)
            : null;
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

        if (space.Layout.Natural)
        {
            // A natural space's places are its own cells in metres; its navigation spans its metres, whatever its voxels.
            Vector3 extent = new Vector3(space.Layout.Volume.SizeX, space.Layout.Volume.SizeY, space.Layout.Volume.SizeZ) * (float)space.Layout.VoxelSize;
            DungeonRouteVerdict natural = DungeonRoutes.CheckNatural(engine, space.Session, extent, plan,
                NavigationProfile.Player(engine.Spatial, (int)extent.X, (int)extent.Z));
            return natural.ToString();
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

    /// <summary>The largest trial chamber, in metres of half width.</summary>
    private const float MaximumChamberHalfWidthMetres = 8f;

    /// <summary>A trial chamber is this much as tall as it is wide, its edges rounded by this much.</summary>
    private const float ChamberHeightShare = 0.6f, ChamberRoundingMetres = 1.5f;

    /// <summary>How far past its surface a region write lowers or raises densities, as the Engine's brushes do.</summary>
    private const int ChamberMarginVoxels = 2;

    /// <summary>
    /// Engine #9505 trial: carves a rounded chamber into the loaded dungeon where the player aims,
    /// either as an implicit field stamp (the Engine samples a rounded-box field over the chamber's
    /// voxels) or as per-voxel region writes (the product reads the densities, computes the same
    /// rounded box and writes them back), and reports what each cost.
    /// </summary>
    internal string Chamber(float halfWidth, bool stamp)
    {
        if (state != DungeonState.Inside || space is null)
        {
            return "chamber refused: not in a dungeon";
        }

        if (halfWidth <= ChamberRoundingMetres || halfWidth > MaximumChamberHalfWidthMetres)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"chamber refused: the half width must be above {ChamberRoundingMetres:F1} m and at most {MaximumChamberHalfWidthMetres:F0} m");
        }

        Vector3 eye = player.WorldEyePosition;
        SpatialHit hit = engine.Spatial.CastRay(new SpatialRaycastRequest(
            space.Session, eye, player.AimForward, BlastReachMetres,
            new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
            ReadOnlyMemory<SpatialEntityCollider>.Empty, ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));
        if (!hit.Present)
        {
            return string.Create(CultureInfo.InvariantCulture, $"chamber refused: nothing within {BlastReachMetres:F0} m along the aim");
        }

        Vector3 half = new(halfWidth, halfWidth * ChamberHeightShare, halfWidth);
        // The chamber opens off the wall the player aims at: its centre lies behind the hit.
        Vector3 centre = hit.Point + Vector3.Normalize(player.AimForward) * halfWidth;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        VoxelDensityReceipt receipt = stamp ? StampChamber(space.Session, centre, half) : WriteChamber(space.Session, centre, half);
        double editMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        space.Refresh();
        double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return string.Create(CultureInfo.InvariantCulture,
            $"chamber {(stamp ? "stamp" : "region")} {halfWidth:0.#} m at {centre.X:F1},{centre.Y:F1},{centre.Z:F1}: {receipt.Status}, {receipt.ChangedVoxels} voxels changed ({receipt.SolidityChanges} cleared), {receipt.RebuiltMeshChunks} chunks rebuilt, meshing {receipt.MeshMicroseconds / 1000d:F1} ms, edit {editMs:F1} ms, {elapsedMs:F1} ms with the redraw");
    }

    private VoxelDensityReceipt StampChamber(SpatialSession session, Vector3 centre, Vector3 half)
    {
        using Rusty.Engine.Implicit.ImplicitRecipe recipe = new(engine.ImplicitSurfaces);
        Vector3 rounding = new(ChamberRoundingMetres);
        ImplicitNode chamber = recipe.Offset(recipe.Box(centre - half + rounding, centre + half - rounding), ChamberRoundingMetres);
        return engine.Voxel.StampImplicit(new VoxelImplicitStampRequest(session, recipe.Field, chamber,
            centre - half, centre + half, VoxelDensityOperation.Subtract, 0f, (uint)BlastBrushMaterial));
    }

    private VoxelDensityReceipt WriteChamber(SpatialSession session, Vector3 centre, Vector3 half)
    {
        float voxel = (float)TerrainConstants.VoxelSize;
        Vector3 low = (centre - half) / voxel - new Vector3(ChamberMarginVoxels), high = (centre + half) / voxel + new Vector3(ChamberMarginVoxels);
        Rusty.Engine.VoxelAddress min = new((long)MathF.Floor(low.X), (long)MathF.Floor(low.Y), (long)MathF.Floor(low.Z));
        (uint X, uint Y, uint Z) size = ((uint)(MathF.Floor(high.X) - min.X + 1), (uint)(MathF.Floor(high.Y) - min.Y + 1), (uint)(MathF.Floor(high.Z) - min.Z + 1));
        ReadOnlyMemory<VoxelDensitySample> before = engine.Voxel.ReadDensities(new VoxelDensityReadRequest(session, min, size.X, size.Y, size.Z));
        float[] densities = new float[before.Length];
        ReadOnlySpan<VoxelDensitySample> samples = before.Span;
        int index = 0;
        for (uint z = 0; z < size.Z; z++)
        for (uint y = 0; y < size.Y; y++)
        for (uint x = 0; x < size.X; x++)
        {
            Vector3 at = new((min.X + x + 0.5f) * voxel, (min.Y + y + 0.5f) * voxel, (min.Z + z + 0.5f) * voxel);
            densities[index] = MathF.Max(samples[index].Density, -RoundedBoxDistance(at - centre, half) / voxel);
            index++;
        }

        return engine.Voxel.ApplyDensityEdits(new VoxelDensityTransaction(session,
            new[] { VoxelDensityEdit.Region(min, size, 0, (uint)densities.Length) }, densities, ReadOnlyMemory<uint>.Empty));
    }

    /// <summary>Signed distance from a point (relative to the centre) to a box of half extents rounded by the chamber rounding.</summary>
    private static float RoundedBoxDistance(Vector3 point, Vector3 half)
    {
        Vector3 q = Vector3.Abs(point) - (half - new Vector3(ChamberRoundingMetres));
        return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f) - ChamberRoundingMetres;
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

    /// <summary>The loaded dungeon's voxel scene facts; null without a dungeon.</summary>
    internal VoxelSceneReadout? ReadScene() =>
        space is DungeonSpace current ? engine.Voxel.ReadScene(new VoxelSceneReadRequest(current.Session)) : null;

    /// <summary>Darkens the loaded dungeon's surface vertices by the solid voxels around them (#9506 exploration); null without a dungeon.</summary>
    internal VoxelSceneReadout? ConfigureVertexOcclusion(float strength)
    {
        if (space is not DungeonSpace current)
        {
            return null;
        }

        VoxelSceneReadout scene = engine.Voxel.ConfigureVertexOcclusion(new VoxelVertexOcclusionRequest(current.Session, strength));
        current.Refresh();
        return scene;
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
        engine.CameraView.SetIndirectLight(new IndirectLightRequest(Vector3.Zero, Vector3.Zero, ProbeSpacingMetres, ProbeBounces, IndirectAmbient.Floor));
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
        fires.Show([.. lights.Take(MaximumLights).Select(light => DungeonSpace.InSession(light) - (Vector3.UnitY * FireBelowLight))]);
        for (int slot = 0; slot < MaximumLights; slot++)
        {
            bool on = slot < lights.Count;
            LightRequest request = new(
                ProductIds.DungeonLightBase + (ulong)slot,
                false,
                0UL,
                new LightDescriptor(LightKind.Point, LightColour, LightIntensity, on,
                    on ? DungeonSpace.InSession(lights[slot]) : Vector3.Zero,
                    -Vector3.UnitY, true, LightRange, LightDecay, 0f, 0f, LightShadowIntent.Requested,
                    LightShadowResolution, LightShadowPriority, true));
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

    /// <summary>Asks for the probe volume over the dungeon's whole volume, in its session's frame.</summary>
    private void IndirectLight(DungeonVolume volume, double voxelSize)
    {
        Vector3 size = new Vector3(volume.SizeX, volume.SizeY, volume.SizeZ) * (float)voxelSize;
        Vector3 centre = DungeonSpace.InSession(size / 2f);
        Vector3 extent = (size / 2f) + new Vector3(ProbeMarginMetres);
        engine.CameraView.SetIndirectLight(new IndirectLightRequest(centre, extent, ProbeSpacingMetres, ProbeBounces, IndirectAmbient.Floor));
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
