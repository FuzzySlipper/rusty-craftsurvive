using System.Text.Json;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Options;

/// <summary>
/// Keeps the game's own options for the install (#9759), in a scope of their own beside the worlds'
/// saves, as the Engine keeps the player's renderer settings: a world can be discarded or replaced
/// and the options stay. A damaged file is reported and the defaults used.
/// </summary>
internal sealed class GameOptionsStore : IDisposable
{
    internal const string Scope = "craftsurvive.options";
    internal const string Key = "options";

    private readonly IEngineContext engine;
    private PersistenceStore? store;

    internal GameOptionsStore(IEngineContext engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>What the last load found, for readouts.</summary>
    internal string LoadOutcome { get; private set; } = "not loaded";

    /// <summary>The last write that failed, or null.</summary>
    internal string? LastFailure { get; private set; }

    private PersistenceStore Store => store ??= engine.Persistence.OpenStore(new PersistenceOpenRequest(Scope));

    internal GameOptions Load()
    {
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(Store, Key));
        PersistenceBlobInfo info = engine.Persistence.DescribeBlob(blob);
        if (!info.Present)
        {
            LoadOutcome = "defaults: none saved";
            return GameOptions.Defaults;
        }

        try
        {
            GameOptions options = GameOptions.Decode(engine.Persistence.ReadBlobBytes(blob).Span);
            LoadOutcome = "restored";
            return options;
        }
        catch (JsonException damaged)
        {
            LoadOutcome = $"defaults: the saved options are damaged ({damaged.Message})";
            return GameOptions.Defaults;
        }
    }

    internal void Save(GameOptions options)
    {
        try
        {
            engine.Persistence.Save(new PersistenceSaveRequest(Store, Key, PersistenceRevisionGuard.Any, 0, options.Encode()));
            LastFailure = null;
        }
        catch (EngineCallException failure)
        {
            // An option the store refuses still applies for the session.
            LastFailure = failure.Message;
        }
    }

    public void Dispose()
    {
        store?.Dispose();
        store = null;
    }
}
