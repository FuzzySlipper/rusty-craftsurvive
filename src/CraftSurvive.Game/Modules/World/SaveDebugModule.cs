using Rusty.Engine;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.World;

/// <summary>Live-debug adapter over the save manifest: which keys are stored now, and how large they are.</summary>
public sealed class SaveDebugModule : IDebugCommandModule
{
    private readonly IEngineContext engine;
    private readonly ProductStore store;

    internal SaveDebugModule(IEngineContext engine, ProductStore store)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    [DebugCommand("craft.save.manifest", Description = "Lists every saved key with its owner and schema, and whether it and its backup are stored.")]
    public string Manifest() => string.Join("; ", SaveManifest.All.Select(key =>
        $"{key.Key} owner={key.Owner} schema={key.Schema} stored={Describe(key.Key)} backup={Describe(key.BackupKey)}"));

    private string Describe(string key)
    {
        using PersistenceBlob blob = engine.Persistence.Load(new PersistenceLoadRequest(store.Store, key));
        PersistenceBlobInfo info = engine.Persistence.DescribeBlob(blob);
        return info.Present ? $"{info.PayloadLen}B@r{info.Revision}" : "no";
    }
}
