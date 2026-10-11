using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using Rusty.Engine.Persistence;

namespace CraftSurvive.Game.Modules.Places;

/// <summary>
/// Owns the expedition's sled in the running product (#9473): restores and saves it under its own
/// key, and stands it on the ground in first person where the party left it. Absent a save, the sled
/// waits at home. Its model is a textured prop mesh (#9831), in metres.
/// </summary>
internal sealed class SledStore : IDisposable
{
    internal const string ModelPath = "models/sled" + PropMesh.Suffix;
    /// <summary>Wood and canvas: matt, and nothing about a sled moves in the wind.</summary>
    private const float Roughness = 0.9f;

    private readonly ProductSaveSlot<SledSave> slot;
    private readonly PropMaterials materials;
    private readonly MeshResource mesh;
    private readonly Appearance appearance;

    internal SledStore(IEngineContext engine, ProductContent content, ProductStore store, SaveIdentity identity, Vector2 home)
    {
        slot = new ProductSaveSlot<SledSave>(engine, store, SaveManifest.TravelSled, new SledCodec(identity));
        Sled = new Sled(home);
        materials = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, 0, 0));
        mesh = engine.Graphics.CreateMeshResource(PropMesh.Read(content, ModelPath,
            (role, texture) => materials.For(role, texture ?? throw new InvalidOperationException($"CraftSurvive sled part '{role}' names no texture."))));
        appearance = engine.Graphics.CreateMeshAppearance(mesh);
    }

    internal Sled Sled { get; }

    internal void Start()
    {
        if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: SledSave saved })
            Sled.Restore(new((float)saved.X, (float)saved.Z), saved.Cargo.Select(carried => (carried.Item, carried.Count)));
    }

    internal void Save() => slot.Save(new SledSave(Sled.Position.X, Sled.Position.Y,
        [.. ItemCatalog.All.Where(item => Sled.Count(item) > 0).Select(item => new ItemCount(item, Sled.Count(item)))]));

    /// <summary>The sled standing on the ground where it was left, in the player's local frame.</summary>
    internal AppearanceFact Fact(WorldFrame frame, float ground) => new(
        ProductIds.SledObject, false, 0,
        new Transform(frame.ToLocal(Sled.Position.X, ground, Sled.Position.Y), Quaternion.Identity, Vector3.One),
        appearance, Visible: true, RenderLayer.Scene);

    internal string Readout => string.Create(CultureInfo.InvariantCulture,
        $"sled at={Sled.Position.X:F0},{Sled.Position.Y:F0} load={Sled.Load}/{Sled.Capacity} cargo=[{Sled.Describe()}] restore={slot.RestoreOutcome} saves={slot.Saves} failure={slot.LastFailure ?? "none"}");

    public void Dispose()
    {
        appearance.Dispose();
        mesh.Dispose();
        materials.Dispose();
    }
}
