using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>The player's pose, for the UI projection.</summary>
internal readonly record struct PlayerUiFacts(
    double EyeX,
    double EyeY,
    double EyeZ,
    double YawDegrees,
    double PitchDegrees,
    bool Grounded,
    bool Crouched);

/// <summary>
/// What the journal knows, as numbers, for the UI projection. It is a flat snapshot with no
/// identity strings: the projection's encoder is numeric by design, and a place's name belongs to
/// the journal's own readout.
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
    double LastTick);

/// <summary>The world's facts for the UI projection, read when it is published.</summary>
internal readonly record struct WorldUiFacts(VoxelSceneReadout Scene, int OverlayEntries);

/// <summary>
/// The product's one UI stream. Owners push their facts - the player its pose, discovery its
/// counts, the world its scene - and each push republishes the whole projection.
/// </summary>
internal sealed class ProductUiPublisher : IDisposable
{
    private readonly IEngineContext engine;
    private UiStream? stream;
    private Func<WorldUiFacts>? world;
    private PlayerUiFacts? player;
    private DiscoveryUiFacts? discovery;
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

    internal void Publish()
    {
        if (stream is null || world is null)
        {
            return;
        }

        WorldUiFacts facts = world();
        engine.Ui.PublishProjection(new UiProjection(stream, ++sequence,
            ProductUiProjection.Create(facts.Scene, facts.OverlayEntries, player, discovery)));
    }

    public void Dispose()
    {
        stream?.Dispose();
        stream = null;
    }
}
