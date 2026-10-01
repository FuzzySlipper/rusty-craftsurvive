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
    string LastFound);

/// <summary>The world's facts for the UI projection, read when it is published.</summary>
internal readonly record struct WorldUiFacts(VoxelSceneReadout Scene, int OverlayEntries);

/// <summary>
/// The product's one UI stream. Owners push their facts - the player its pose and vitals, discovery
/// its counts, the UI actions their outcome, the world its scene - and each push republishes the
/// whole projection.
/// </summary>
internal sealed class ProductUiPublisher : IDisposable
{
    private readonly IEngineContext engine;
    private UiStream? stream;
    private Func<WorldUiFacts>? world;
    private PlayerUiFacts? player;
    private DiscoveryUiFacts? discovery;
    private ActionUiFacts? actions;
    private ulong sequence;

    internal ProductUiPublisher(IEngineContext engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    internal void Open(string name, string contract, Func<WorldUiFacts> worldFacts)
    {
        stream ??= engine.Ui.OpenStream(new UiStreamRequest(name, contract));
        world = worldFacts ?? throw new ArgumentNullException(nameof(worldFacts));
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

    internal void Publish()
    {
        if (stream is null || world is null)
        {
            return;
        }

        WorldUiFacts facts = world();
        engine.Ui.PublishProjection(new UiProjection(stream, ++sequence,
            ProductUiProjection.Create(facts.Scene, facts.OverlayEntries, player, discovery, actions)));
    }

    public void Dispose()
    {
        stream?.Dispose();
        stream = null;
    }
}
