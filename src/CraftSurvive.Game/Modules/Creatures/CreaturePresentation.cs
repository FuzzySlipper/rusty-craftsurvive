using System.Numerics;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using Rusty.Engine.Entities;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>The creature's Engine-side row: where it is and what it is doing.</summary>
internal readonly record struct CreatureRuntimeComponent(
    float X,
    float Y,
    float Z,
    int State,
    int Health,
    int MaximumHealth);

/// <summary>
/// What the Engine shows of the roster: one appearance and one entity row per creature, created
/// when a creature joins and retired when it leaves. It holds no creature state of its own - each
/// <see cref="Sync"/> derives everything from the roster, so the two cannot disagree about who
/// exists.
/// </summary>
internal sealed class CreaturePresentation : IDisposable
{
    internal static readonly ComponentType<CreatureRuntimeComponent> RuntimeComponent =
        ComponentType<CreatureRuntimeComponent>.Create(ProductComponentKeys.Create(ProductIds.CreatureRuntimeComponent));

    private static readonly Vector3 BodyScale = new(0.8f, 1.6f, 0.8f);

    /// <summary>A creature's centre stands this far above the ground it is on: half its height.</summary>
    private const float BodyCentreHeight = 0.8f;

    private static readonly Color HostileColor = new(0.55f, 0.12f, 0.12f, 1f);
    private static readonly Color NeutralColor = new(0.45f, 0.38f, 0.22f, 1f);

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly Func<Vector2, float> groundAt;
    private readonly Dictionary<int, (Appearance Appearance, EntityId Entity)> shown = [];

    /// <summary>
    /// Appearances of creatures that left. The Engine refuses to dispose an appearance the last
    /// published snapshot still references, so they are released only once the product root has
    /// published a snapshot without them - whether they left during an update or a debug command.
    /// </summary>
    private readonly List<Appearance> retiring = [];

    private AppearanceFact[] facts = [];

    internal CreaturePresentation(IEngineContext engine, WorldFrame frame, Func<Vector2, float> groundAt)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.groundAt = groundAt ?? throw new ArgumentNullException(nameof(groundAt));
    }

    internal EntityStore Entities { get; } = new([RuntimeComponent]);

    /// <summary>The creature appearances for this update's snapshot.</summary>
    internal IReadOnlyList<AppearanceFact> Facts => facts;

    internal int Shown => shown.Count;

    /// <summary>
    /// Releases retired appearances. Call only after a snapshot built from <see cref="Facts"/> has
    /// been published, which is what removes the Engine's last reference to them.
    /// </summary>
    internal void ReleaseRetired()
    {
        foreach (Appearance appearance in retiring)
        {
            appearance.Dispose();
        }

        retiring.Clear();
    }

    /// <summary>Brings the Engine's view in line with the roster.</summary>
    internal void Sync(CreatureRoster roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        foreach (int id in shown.Keys.Where(id => !roster.TryGet(id, out _)).ToArray())
        {
            Retire(id);
        }

        List<AppearanceFact> next = new(roster.Count);
        foreach (Creature creature in roster.All)
        {
            if (!shown.TryGetValue(creature.Id, out (Appearance Appearance, EntityId Entity) entry))
            {
                entry = (engine.Graphics.CreatePrimitive(new PrimitiveAppearanceRequest(
                    PrimitiveGeometry.Cube,
                    false,
                    creature.Kind.Tuning.Disposition == CreatureDisposition.Neutral ? NeutralColor : HostileColor)),
                    Entities.Create());
                shown[creature.Id] = entry;
            }

            float ground = groundAt(creature.Position);
            Entities.Set(entry.Entity, RuntimeComponent, new CreatureRuntimeComponent(
                creature.Position.X,
                ground,
                creature.Position.Y,
                (int)creature.Behavior.State,
                creature.Combat.Health,
                creature.Combat.MaximumHealth));
            next.Add(new AppearanceFact(
                ProductIds.CreatureAppearanceBase + (ulong)creature.Id,
                false,
                0,
                new Transform(
                    frame.ToLocal(creature.Position.X, ground + BodyCentreHeight, creature.Position.Y),
                    Quaternion.Identity,
                    BodyScale),
                entry.Appearance,
                Visible: true,
                RenderLayer.Scene));
        }

        facts = [.. next];
    }

    /// <summary>Retires every creature; their appearances are released after the next snapshot.</summary>
    internal void Clear()
    {
        foreach (int id in shown.Keys.ToArray())
        {
            Retire(id);
        }

        facts = [];
    }

    public void Dispose()
    {
        Clear();
        ReleaseRetired();
        Entities.Dispose();
    }

    private void Retire(int id)
    {
        (Appearance appearance, EntityId entity) = shown[id];
        shown.Remove(id);
        Entities.Destroy(entity);
        retiring.Add(appearance);
    }
}
