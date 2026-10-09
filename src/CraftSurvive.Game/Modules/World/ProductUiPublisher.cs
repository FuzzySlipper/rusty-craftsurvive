using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>The player's pose, vitals and progress, for the UI projection.</summary>
internal readonly record struct PlayerUiFacts(
    double EyeX,
    double EyeY,
    double EyeZ,
    double YawDegrees,
    double PitchDegrees,
    bool Grounded,
    bool Crouched)
{
    internal int Health { get; init; }

    internal int MaximumHealth { get; init; }

    internal int Defeats { get; init; }

    internal int Experience { get; init; }

    internal int Level { get; init; }

    internal int ItemsCollected { get; init; }

    internal double Stamina { get; init; }

    internal int MaximumStamina { get; init; }

    internal bool Climbing { get; init; }

    /// <summary>How many times health has been lost this session; the HUD flashes when it rises.</summary>
    internal long HitsTaken { get; init; }

    /// <summary>Whether the player's head is under water.</summary>
    internal bool Submerged { get; init; }

    /// <summary>What placing builds now and the keys that change it (#9729); empty where nothing can be built.</summary>
    internal string Building { get; init; } = string.Empty;
}

/// <summary>What the player's UI requests came to, and the build palette it may name, for the UI projection.</summary>
internal readonly record struct ActionUiFacts(long Applied, long Refused, string Last, string Palette);

/// <summary>
/// What the journal knows, for the UI projection: its counts, the last place as numbers, and that
/// place named for a player.
/// </summary>
internal readonly record struct DiscoveryUiFacts(
    double Places,
    double Visited,
    double Seen,
    double Refused,
    double NearestMetres,
    double LastX,
    double LastZ,
    double LastKind,
    double LastStage,
    double LastTick,
    string LastFound,
    string Journal);

/// <summary>
/// What the player carries, for the UI projection: the items as one line, each occupied slot as a
/// <c>slot|id|name|count|use</c> entry (use is food, healing, light or material), the recipe book as
/// <c>id|makes|count|name*count+name*count|ready</c> entries, entries separated by <c>;</c>, how
/// much is carried of how much can be, the torches for lights, and what the last inventory action
/// came to.
/// </summary>
internal readonly record struct InventoryUiFacts(string Carried, string Items, string RecipeBook, double Load, double Limit, int Torches, string Last)
{
    /// <summary>How many hotbar slots there are, numbered first, and pack slots after them.</summary>
    internal double HotbarSlots { get; init; }

    internal double PackSlots { get; init; }

    /// <summary>The selected hotbar slot.</summary>
    internal double Selected { get; init; }
}

/// <summary>
/// Where the player stands with dungeons, for the UI projection: outside, loading (with progress) or
/// inside, the prompt to show, whether entering or leaving is possible now, and the last outcome.
/// </summary>
internal readonly record struct DungeonUiFacts(string State, double Progress, string Prompt, bool CanEnter, bool CanLeave, string Last);

/// <summary>The player's survival tracks for the UI projection: food and air, and the last harm they did.</summary>
/// <summary>The survival tracks for the HUD, with how wet and chilled the player is (whole percents) and the weather over them (#9741).</summary>
internal readonly record struct SurvivalUiFacts(double Satiety, double Breath, double MaximumBreath, string LastHarm,
    double Wetness = 0, double Chill = 0, string Weather = "", bool Sheltered = true, bool WeatherWounds = false);

/// <summary>The world's conditions for the UI projection: the time as a player reads it, its daylight, the difficulty and the difficulties to choose from.</summary>
internal readonly record struct ConditionsUiFacts(string Time, double Daylight, bool Night, string Difficulty, string Difficulties);

/// <summary>The world's facts for the UI projection, read when it is published.</summary>
internal readonly record struct WorldUiFacts(VoxelSceneReadout Scene, int OverlayEntries);

/// <summary>The sled for the pack screen (#9473): whether it is within reach, its load, and its cargo as <c>id|name|count;...</c>.</summary>
internal readonly record struct SledUiFacts(bool Near, int Load, int Limit, string Cargo, double DistanceMetres, string ExpeditionPrompt = "");

internal readonly record struct WorldMapUiFacts(bool Open, string Seed, int Size, string Sites, string Message, long Generation, bool Faceted,
    string Travel, string TravelPhase, string Supplies, string Event, int TravelSpeed);

/// <summary>
/// The product's one UI stream. Owners push their facts - the player its pose and vitals, discovery
/// its counts, the UI actions their outcome, the world its scene - and each push republishes the
/// whole projection.
/// </summary>
internal sealed class ProductUiPublisher : IDisposable
{
    /// <summary>
    /// The product's one UI stream and its contract, which the project file and the UI
    /// (<c>src/ui/hud.ts</c>) name too.
    /// </summary>
    internal const string StreamName = "craftsurvive.game";
    internal const string StreamContract = "craftsurvive.game.v1";

    private readonly IEngineContext engine;
    private UiStream? stream;
    private Func<WorldUiFacts>? world;
    private PlayerUiFacts? player;
    private DiscoveryUiFacts? discovery;
    private ActionUiFacts? actions;
    private ConditionsUiFacts? conditions;
    private SurvivalUiFacts? survival;
    private InventoryUiFacts? inventory;
    private DungeonUiFacts? dungeon;
    private ulong sequence;
    private WorldMapUiFacts? map;
    private SledUiFacts? sled;

    /// <summary>How many projections this publisher has sent.</summary>
    internal ulong Published => sequence;

    internal ProductUiPublisher(IEngineContext engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>
    /// Open the product's stream. The product does this at start, before any world exists, so a
    /// fresh store's generating map is published; the terrain later attaches its scene facts.
    /// </summary>
    internal void OpenStream() => stream ??= engine.Ui.OpenStream(new UiStreamRequest(StreamName, StreamContract));

    internal void Open(string name, string contract, Func<WorldUiFacts> worldFacts)
    {
        stream ??= engine.Ui.OpenStream(new UiStreamRequest(name, contract));
        world = worldFacts ?? throw new ArgumentNullException(nameof(worldFacts));
    }

    internal void ResetWorld()
    {
        world = null; player = null; discovery = null; actions = null; conditions = null; survival = null; inventory = null; dungeon = null; sled = null;
    }

    internal void PublishMap(WorldMapUiFacts facts) { map = facts; Publish(); }

    /// <summary>The game's own options (#9759): the install's, so a world's reset keeps them.</summary>
    internal void PublishOptions(string described)
    {
        if (gameOptions == described) return;
        gameOptions = described;
        Publish();
    }

    private string? gameOptions;

    internal void PublishSled(SledUiFacts facts)
    {
        if (sled == facts) return;
        sled = facts;
        Publish();
    }

    internal void PublishPlayer(PlayerUiFacts facts)
    {
        player = facts;
        Publish();
    }

    internal void PublishDiscovery(DiscoveryUiFacts facts)
    {
        discovery = facts;
        Publish();
    }

    internal void PublishActions(ActionUiFacts facts)
    {
        actions = facts;
        Publish();
    }

    internal void PublishDungeon(DungeonUiFacts facts)
    {
        dungeon = facts;
        Publish();
    }

    internal void PublishInventory(InventoryUiFacts facts)
    {
        inventory = facts;
        Publish();
    }

    internal void PublishSurvival(SurvivalUiFacts facts)
    {
        survival = facts;
        Publish();
    }

    internal void PublishConditions(ConditionsUiFacts facts)
    {
        conditions = facts;
        Publish();
    }

    internal void Publish()
    {
        if (stream is null)
        {
            return;
        }

        // Without a world there is no scene; the map and its generating message still publish.
        WorldUiFacts? facts = world?.Invoke();
        engine.Ui.PublishProjection(new UiProjection(stream, ++sequence,
            ProductUiProjection.Create(facts, player, discovery, actions, conditions, survival, inventory, dungeon, map, sled, gameOptions)));
    }

    public void Dispose()
    {
        stream?.Dispose();
        stream = null;
    }
}
