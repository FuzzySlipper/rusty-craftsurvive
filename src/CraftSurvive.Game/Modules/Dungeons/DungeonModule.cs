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
    internal const int MaximumLights = 16;

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
                loading.Advance(ChunksPerUpdate);
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

    public void Dispose()
    {
        if (space is not null)
        {
            player.ReturnFromSeparateSpace();
            Close("closed");
        }
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

        space = new DungeonSpace(engine, terrain, TestChamber.Build());
        state = DungeonState.Loading;
        last = $"loading the dungeon under the entrance at {entrance.X},{entrance.Z}";
        Publish();
        return last;
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
        return $"dungeon state={state}{loading} nearbyEntrance={entrance} entered={entered} refused={refused} last={last}";
    }

    private void Close(string outcome)
    {
        Light([]);
        space?.Dispose();
        space = null;
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
