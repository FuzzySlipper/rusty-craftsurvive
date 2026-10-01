using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Survival;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>How dungeons are generated: one of the approaches #8604 is comparing.</summary>
internal enum DungeonApproach
{
    /// <summary>A: carve and stamp, all cubic voxels.</summary>
    CarveAndStamp,

    /// <summary>C: A's structure with its rock sculpted into a smooth mesh around the cubic building.</summary>
    SculptedCave,
}

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
    private DungeonApproach approach = DungeonApproach.SculptedCave;
    private Material? rockMaterial;

    /// <summary>The sculpted rock's texture, authored by scripts/generate-cave-rock.mjs.</summary>
    internal const string RockTextureContentPath = "textures/cave-rock.png";

    private const float RockRoughness = 0.92f;
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

                if (loading.Loaded)
                {
                    player.EnterSeparateSpace(loading.Session, DungeonSpace.InSession(loading.Layout.Arrival));
                    sky.Underground(true, conditions.Time);
                    Light(loading.Layout.Lights);
                    state = DungeonState.Inside;
                    entered++;
                    last = string.Create(CultureInfo.InvariantCulture,
                        $"entered {loading.Layout.Name}: {loading.TotalChunks} chunks in {loading.LoadMilliseconds:F0} ms");
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
        ulong seed = DungeonSeed(terrain.SaveIdentity.Seed, entrance);
        (DungeonLayout layout, DungeonPlan generated, DungeonVerdict verdict) = approach == DungeonApproach.SculptedCave
            ? Sculpted(seed)
            : CarveAndStamp.Generate(seed);
        plan = generated;
        try
        {
            space = new DungeonSpace(engine, terrain, layout, layout.Rock is null ? null : RockMaterial());
        }
        catch (EngineCallException refusal)
        {
            // The Engine would not make the space: nothing is entered, and the game carries on.
            return Refuse($"enter refused: the dungeon could not be made: {refusal.Message}");
        }

        state = DungeonState.Loading;
        last = $"loading {approach} {generated.Mix} dungeon {seed:x} (attempt {generated.Attempt}, {verdict.Reason}) under the entrance at {entrance.X},{entrance.Z}";
        Publish();
        return last;
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

    /// <summary>Chooses how the next dungeon entered is generated: a (carve and stamp) or c (sculpted cave).</summary>
    internal string Choose(string name)
    {
        DungeonApproach? chosen = name switch
        {
            "a" => DungeonApproach.CarveAndStamp,
            "c" => DungeonApproach.SculptedCave,
            _ => null,
        };
        if (chosen is not DungeonApproach next)
        {
            return $"approach refused: \"{name}\" is not a (carve and stamp) or c (sculpted cave)";
        }

        approach = next;
        return $"the next dungeon is {approach}";
    }

    private static (DungeonLayout, DungeonPlan, DungeonVerdict) Sculpted(ulong seed)
    {
        (DungeonLayout layout, DungeonPlan plan, DungeonVerdict verdict, _) = SculptedCave.Generate(seed);
        return (layout, plan, verdict);
    }

    /// <summary>The sculpted rock's material, made once and kept for every sculpted dungeon.</summary>
    private Material RockMaterial()
    {
        if (rockMaterial is null)
        {
            RenderResourceInfo texture = engine.Graphics.OpenResource(new RenderResourceRequest(RockTextureContentPath, TextureFilter.Linear, TextureWrap.Repeat));
            rockMaterial = engine.Graphics.CreateMaterial(new MaterialRequest(
                new Color(1f, 1f, 1f, 1f), texture.Handle, RockRoughness, new Color(1f, 1f, 1f, 1f), Vector3.Zero, 0f, false,
                MaterialAlphaMode.Opaque, 0f));
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

        player.MoveWithinSeparateSpace(DungeonSpace.InSession(new Vector3(at.X + 0.5f, at.Y, at.Z + 0.5f)));
        return $"at the {place}: {at}";
    }

    /// <summary>
    /// The Engine's word on the loaded dungeon, beside the generator's own walk check: navigation
    /// published over the whole space, then routes from the arrival to the breach and the loot, and
    /// from the loot back. Reported with what the publication and queries cost.
    /// </summary>
    internal string Validate()
    {
        if (state != DungeonState.Inside || space is null || plan is null)
        {
            return "validate refused: not in a dungeon";
        }

        DungeonVolume volume = space.Layout.Volume;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        CollisionNavigationReplaceReceipt published = engine.Spatial.ReplaceCollisionNavigation(new CollisionNavigationReplaceRequest(
            space.Session,
            DungeonSpace.Origin,
            DungeonSpace.Origin + new Vector3(volume.SizeX, volume.SizeY, volume.SizeZ),
            new CollisionNavigationConfig(
                ValidationGridId, TerrainConstants.VoxelSize, (uint)TerrainConstants.ChunkEdgeLength, ValidationStepCells,
                ValidationAgentRadius, ValidationAgentHeight, ValidationSlopeDegrees,
                (uint)(volume.SizeX * volume.SizeY * volume.SizeZ))));
        double publishMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        string Route(string name, DungeonCell from, DungeonCell to)
        {
            NavigationStepResult step = engine.Spatial.EvaluateNavigationStep(new NavigationStepRequest(
                space.Session, Feet(from), Feet(to), ValidationStepMetres, ValidationMaxVisited));
            return $"{name}={step.Outcome}/{step.Path.Length}";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"navigation walkable={published.WalkableCellCount} publishMs={publishMs:F0} ")
            + $"{Route("arrival->breach", plan.Arrival, plan.Breach)} {Route("arrival->loot", plan.Arrival, plan.Loot)} {Route("loot->arrival", plan.Loot, plan.Arrival)}";
    }

    private const ulong ValidationGridId = 2UL;
    private const uint ValidationStepCells = 1U;
    private const double ValidationAgentRadius = 0.3d;
    private const double ValidationAgentHeight = 1.75d;
    private const double ValidationSlopeDegrees = 45d;
    private const float ValidationStepMetres = 4096f;
    private const uint ValidationMaxVisited = 1_000_000U;

    private static Vector3 Feet(DungeonCell cell) => DungeonSpace.InSession(new Vector3(cell.X + 0.5f, cell.Y, cell.Z + 0.5f));

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
        return $"dungeon approach={approach} state={state}{loading}{layout} nearbyEntrance={entrance} entered={entered} refused={refused} last={last}";
    }

    private void Close(string outcome)
    {
        Light([]);

        // The rock may be in the snapshot already published: it leaves the next one, then goes.
        retiring?.Dispose();
        retiring = space;
        space = null;
        plan = null;
        state = DungeonState.Outside;
        sky.Underground(false, conditions.Time);
        nextSearchStep = 0;
        last = outcome;
        Publish();
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
