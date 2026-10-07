using System.Numerics;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// The light placed lamps give. Each lit Light block entity near the player gets one retained point
/// light at its cell, from a fixed pool of Engine lights: the nearest lamps are lit and the pool's
/// spare lights are disabled. The lights are refreshed when the lamps change, the player has moved
/// far, or the world origin has moved.
/// </summary>
internal sealed class LampLights : IProductModule
{
    /// <summary>How many lamps are lit at once: the nearest ones.</summary>
    internal const int MaximumLitLamps = 16;

    /// <summary>How far the player may move before the nearest lamps are chosen again.</summary>
    private const float RechooseDistanceMetres = 8f;

    private const float LampIntensity = 10f;
    private const float LampRange = 12f;
    private const float LampDecay = 2f;

    /// <summary>
    /// Every lit lamp asks to cast; the Engine's shadow budget (the project's
    /// RustyEngineProductShadowBudget) picks the nearest that fit, so the product keeps no
    /// nearest-N shadow policy of its own. A lamp a few metres from its walls needs few texels.
    /// </summary>
    private const uint LampShadowResolution = 256;
    private const int LampShadowPriority = 0;
    private const float CellCentre = 0.5f;

    /// <summary>
    /// The light hangs this far above the lamp block's top face, outside its cell: a light inside
    /// an opaque block is shadowed by the block's own faces, and the faces a hand's breadth from
    /// it bloom to white.
    /// </summary>
    private const float LightAboveBlock = 1.2f;
    private static readonly Vector3 LampColour = new(1f, 0.72f, 0.38f);

    private readonly IEngineContext engine;
    private readonly BlockEntityIndex entities;
    private readonly PlayerController player;
    private readonly WorldFrame frame;
    private readonly Light?[] pool = new Light?[MaximumLitLamps];

    /// <summary>Each lit lamp burns a fire on its top face (#9547).</summary>
    private readonly FireEmitters fires;
    private const float FireAboveBlock = 0.1f;
    private long shownRevision = -1;
    private Vector3? shownAround;
    private int lit;

    internal LampLights(IEngineContext engine, BlockEntityIndex entities, PlayerController player, WorldFrame frame)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.entities = entities ?? throw new ArgumentNullException(nameof(entities));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        frame.Rebased += _ => shownAround = null;
        fires = new FireEmitters(engine, ProductIds.LampFireBase, "craftsurvive.lamp", MaximumLitLamps);
    }

    /// <summary>How many lamps are lit now.</summary>
    internal int Lit => lit;

    /// <summary>How many of them burn a fire.</summary>
    internal int FiresBurning => fires.Burning;

    public void Start() => Show();

    public void Update(ProductStep step)
    {
        Vector3 feet = player.WorldFeetPosition;
        if (entities.Revision != shownRevision || shownAround is not Vector3 around || Vector3.Distance(around, feet) > RechooseDistanceMetres)
        {
            Show();
        }
    }

    public void Restart() => Show();

    public void Dispose()
    {
        fires.Dispose();
        foreach (Light? light in pool) light?.Dispose();
        Array.Clear(pool);
    }

    private void Show()
    {
        Vector3 feet = player.WorldFeetPosition;
        VoxelAddress[] lamps = [.. entities.All
            .Where(entity => entity.Kind == BlockEntityKind.Light && entity.State != 0)
            .Select(entity => entity.Cell)
            .OrderBy(cell => Vector3.DistanceSquared(Centre(cell), feet))
            .Take(MaximumLitLamps)];
        for (int slot = 0; slot < MaximumLitLamps; slot++)
        {
            bool on = slot < lamps.Length;
            LightDescriptor descriptor = new(
                LightKind.Point, LampColour, LampIntensity, on,
                on ? frame.ToLocal(Centre(lamps[slot])) : Vector3.Zero,
                -Vector3.UnitY, true, LampRange, LampDecay, 0f, 0f, LightShadowIntent.Requested,
                LampShadowResolution, LampShadowPriority, false);
            LightRequest request = new(ProductIds.LampLightBase + (ulong)slot, false, 0UL, descriptor);
            if (pool[slot] is Light light)
            {
                engine.Graphics.UpdateLight(new LightUpdateRequest(light, request));
            }
            else if (on)
            {
                pool[slot] = engine.Graphics.CreateLight(request);
            }
        }

        fires.Show([.. lamps.Select(cell => frame.ToLocal(new Vector3(cell.X + CellCentre, cell.Y + 1f + FireAboveBlock, cell.Z + CellCentre)))]);
        lit = lamps.Length;
        shownRevision = entities.Revision;
        shownAround = feet;
    }

    private static Vector3 Centre(VoxelAddress cell) => new(cell.X + CellCentre, cell.Y + 1f + LightAboveBlock, cell.Z + CellCentre);
}
