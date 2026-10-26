using System.Numerics;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using RustyChemistry.Simulation;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>
/// A grass fire drawn from the chemistry field (#10296), not as a lamp's flame repeated:
/// <list type="bullet">
/// <item>the ground: every burning cell a projected decal of char under glowing seams, every cell that burnt out a decal of
/// cooling char that settles to cold char (the scene's decal atlas, painted at 32 texels a metre);</item>
/// <item>light: a warm, flickering light over each square of the fire, as strong as the cells burning in it;</item>
/// <item>smoke: over each square a low fog volume glowing with the fire under it, and a dark plume leaning downwind;</item>
/// <item>flames: pixel-art tongues on the hottest burning cells, a few metres apart, kept on a cell while it burns.</item>
/// </list>
/// Everything stands in the scene's frame and is placed again when the frame origin moves. Nothing is saved.
/// </summary>
internal sealed class GrassFireLook : IDisposable
{
    private const int Size = ChemistryField.ChunkSize;
    private const int CellsPerChunk = Size * Size * Size;

    // The atlas (textures/decals/atlas.png, grass-fire/fire_art.py): 256 x 256; the fire's 32 x 32 tiles from y 128.
    private const float AtlasTexels = 256f, Tile = 32f;
    private const int BurningTiles = 4, CharTiles = 4, CoolingTiles = 8;
    private const float BurningRow = 128f, CharColumn = 128f, CoolingRow = 160f;

    /// <summary>
    /// A cell's decal box (metres): a little wider than the cell, so the tiles' ragged rims overlap, and deep enough to reach the
    /// ground in it. Each cell's box is moved and sized a little by its hash, so the tiles do not line up as paving.
    /// </summary>
    private const float DecalLeast = 1.6f, DecalMost = 2f, DecalDepth = 3f, DecalJitter = 0.25f;

    /// <summary>The decal pool: up to this many burning cells (the nearest), the rest of the pool for burnt ones.</summary>
    private const int MostBurningDecals = 768;
    private const int MostBurntDecals = ProductIds.ChemistryFireDecals - MostBurningDecals;

    /// <summary>A burnt cell shows cooling coals for this long, then cold char.</summary>
    private const double CoolingSeconds = 25;

    /// <summary>Flames: at most this many, on burning cells at least this hot and this far apart (metres).</summary>
    private const int MostFlames = ProductIds.ChemistryFlames;
    private const float FlameHeat = 300f, FlameSpacing = 1.8f;

    /// <summary>A flame stands this far over its cell's floor, leans this much a cell a second of drift, and grows with heat.</summary>
    private const float FlameAboveCell = 0.55f, FlameLean = 0.25f, FlameScaleAtHeat = 900f, FlameScaleMost = 2.4f, FlameScaleLeast = 1.4f;

    // A square's light, linear (#9844): its intensity for one burning cell and its most, its range for one and its most,
    // and the flicker: how deep and how fast.
    private static readonly Vector3 FireLightColour = new(1f, 0.5f, 0.16f);
    private const float LightPerCell = 1.6f, LightMost = 28f, LightRangeLeast = 7f, LightRangePerCell = 0.5f, LightRangeMost = 16f;
    private const float LightAbove = 1.2f, LightDecay = 2f, FlickerDepth = 0.25f, FlickerHertz = 7f;

    // The glow over a square: a flat ellipsoid over its burning ground, glowing per unit of density.
    private const float GlowAbove = 0.4f, GlowHalfHeight = 0.5f, GlowReachPerCell = 0.15f, GlowReachLeast = 1.2f, GlowReachMost = 2.6f;
    private const float GlowDensity = 0.08f, GlowEdge = 0.8f, GlowNoiseScale = 0.9f, GlowNoiseStrength = 0.6f, GlowRise = 0.8f;
    private static readonly Vector3 GlowAlbedo = new(0.08f, 0.06f, 0.05f), GlowEmission = new(1.6f, 0.48f, 0.09f);

    // The plume: a column standing on the fire (its foot a little into the ground) and leaning downwind, dark, drifting up
    // and with the wind.
    private const float PlumeAbove = 4f, PlumeDownwindSeconds = 2f, PlumeHalfHeight = 5f, PlumeReachLeast = 1.6f, PlumeReachPerCell = 0.18f, PlumeReachMost = 3.6f;
    private const float PlumeDensity = 0.3f, PlumeEdge = 0.8f, PlumeNoiseScale = 1.2f, PlumeNoiseStrength = 0.8f, PlumeRise = 1.4f;
    private static readonly Vector3 PlumeAlbedo = new(0.2f, 0.19f, 0.18f), PlumeEmission = Vector3.Zero;

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly Action<bool> fogVolumes;
    private readonly int slots, cluster;
    private readonly Light?[] lights;
    private readonly (Vector3 At, int Cells)?[] squares;
    private readonly Dictionary<Cell, int> burningDecals = [];
    private readonly Stack<int> freeBurning = new(Enumerable.Range(0, MostBurningDecals).Reverse());
    private readonly (Cell Cell, double At, bool Cold)?[] burnt = new (Cell, double, bool)?[MostBurntDecals];
    private readonly Queue<int> cooling = [];
    private readonly (Cell Cell, PresentationEmitter Emitter)?[] flames = new (Cell, PresentationEmitter)?[MostFlames];
    private readonly ushort[] materials = new ushort[CellsPerChunk];
    private readonly float[] heat = new float[CellsPerChunk];
    private int nextBurnt, volumesPlaced;
    private RenderResource? flameSprite;
    private (long X, long Y, long Z) placedOrigin;
    private Vector3 lean;
    private double seconds;
    private bool shown;

    /// <param name="fogVolumes">Tells the sky fog volumes stand in the open world (the volumetric fog draws them), or no longer do.</param>
    internal GrassFireLook(IEngineContext engine, WorldFrame frame, Action<bool> fogVolumes, int slots, int cluster)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.fogVolumes = fogVolumes ?? throw new ArgumentNullException(nameof(fogVolumes));
        this.slots = slots;
        this.cluster = cluster;
        lights = new Light?[slots];
        squares = new (Vector3, int)?[slots];
        placedOrigin = frame.Origin;
    }

    internal int BurningDecals => burningDecals.Count;

    internal int BurntDecals => burnt.Count(entry => entry is not null);

    internal int Flames => flames.Count(flame => flame is not null);

    internal int Squares => squares.Count(square => square is not null);

    internal string Readout() =>
        $"look burning={BurningDecals} burnt={BurntDecals} cooling={cooling.Count} flames={Flames} squares={Squares} volumes={volumesPlaced}";

    /// <summary>Marks the cells that burnt out this step: their ground cools, then lies as char.</summary>
    internal void Burnt(IReadOnlyList<MaterialChange> changes, int burntMaterial)
    {
        foreach (MaterialChange change in changes)
        {
            if (change.To != burntMaterial)
            {
                continue;
            }

            int slot = nextBurnt;
            nextBurnt = (nextBurnt + 1) % MostBurntDecals;
            burnt[slot] = (change.Cell, seconds, false);
            cooling.Enqueue(slot);
            Place(BurntId(slot), change.Cell, CoolingRegion(change.Cell));
        }
    }

    /// <summary>
    /// Shows the field's fire as it stands after this update's steps: the burning cells' ground, the squares' lights and
    /// smoke, and the flames. Reads only active chunks, since a sleeping one cannot be burning.
    /// </summary>
    internal void Show(ChemistryField field, IReadOnlySet<int> burning, int heatQuantity, Vector3 near)
    {
        if (!shown)
        {
            shown = true;
            Replace();
        }

        Rebase();
        lean = new Vector3(field.Drift.X, 0f, field.Drift.Z) * FlameLean;
        List<(Cell Cell, float Heat)> cells = Burning(field, burning, heatQuantity);
        Ground(cells, near);
        Gather(cells, near, field.Drift);
        Kindle(cells, near);
    }

    /// <summary>Advances the flicker and the cooling; called every update while shown.</summary>
    internal void Tick(double elapsed)
    {
        seconds += elapsed;
        while (cooling.TryPeek(out int slot) && burnt[slot] is { } entry && seconds - entry.At >= CoolingSeconds)
        {
            cooling.Dequeue();
            burnt[slot] = entry with { Cold = true };
            Place(BurntId(slot), entry.Cell, CharRegion(entry.Cell));
        }

        while (cooling.TryPeek(out int stale) && burnt[stale] is null or { Cold: true })
        {
            cooling.Dequeue();
        }

        if (shown)
        {
            Lights();
        }
    }

    /// <summary>Takes the whole fire out of the scene (a dungeon, a restart); the burnt ground is forgotten.</summary>
    internal void Hide()
    {
        if (!shown && burningDecals.Count == 0 && volumesPlaced == 0 && Flames == 0 && BurntDecals == 0)
        {
            return;
        }

        foreach (int slot in burningDecals.Values)
        {
            Remove(BurningId(slot));
            freeBurning.Push(slot);
        }

        burningDecals.Clear();
        for (int slot = 0; slot < burnt.Length; slot++)
        {
            if (burnt[slot] is not null)
            {
                Remove(BurntId(slot));
                burnt[slot] = null;
            }
        }

        cooling.Clear();
        for (int slot = 0; slot < flames.Length; slot++)
        {
            PutOut(slot);
        }

        Array.Clear(squares);
        Lights();
        Volumes();
        shown = false;
    }

    public void Dispose()
    {
        Hide();
        foreach (Light? light in lights)
        {
            light?.Dispose();
        }

        Array.Clear(lights);
        flameSprite?.Dispose();
        flameSprite = null;
    }

    /// <summary>The burning cells of the active chunks and their heat.</summary>
    private List<(Cell Cell, float Heat)> Burning(ChemistryField field, IReadOnlySet<int> burning, int heatQuantity)
    {
        List<(Cell, float)> found = [];
        foreach (Cell origin in field.LoadedChunks())
        {
            if (!field.IsActive(origin))
            {
                continue;
            }

            field.ReadChunkMaterials(origin, materials);
            bool any = false;
            for (int index = 0; index < CellsPerChunk && !any; index++)
            {
                any = burning.Contains(materials[index]);
            }

            if (!any)
            {
                continue;
            }

            field.ReadChunk(origin, heatQuantity, heat);
            for (int index = 0; index < CellsPerChunk; index++)
            {
                if (burning.Contains(materials[index]))
                {
                    found.Add((origin.Offset(index % Size, index / Size % Size, index / (Size * Size)), heat[index]));
                }
            }
        }

        return found;
    }

    /// <summary>The burning cells' decals: placed as cells catch, taken away as they burn out or go out (the nearest kept).</summary>
    private void Ground(List<(Cell Cell, float Heat)> cells, Vector3 near)
    {
        HashSet<Cell> now = [.. cells
            .Select(entry => entry.Cell)
            .OrderBy(cell => Vector3.DistanceSquared(Centre(cell), near))
            .Take(MostBurningDecals)];
        foreach (Cell gone in burningDecals.Keys.Where(cell => !now.Contains(cell)).ToList())
        {
            int slot = burningDecals[gone];
            Remove(BurningId(slot));
            burningDecals.Remove(gone);
            freeBurning.Push(slot);
        }

        foreach (Cell cell in now)
        {
            if (!burningDecals.ContainsKey(cell) && freeBurning.TryPop(out int slot))
            {
                burningDecals[cell] = slot;
                Place(BurningId(slot), cell, BurningRegion(cell));
            }
        }
    }

    /// <summary>The fire's squares, the nearest: each one's middle (on top of its highest burning cell) and how many cells burn in it.</summary>
    private void Gather(List<(Cell Cell, float Heat)> cells, Vector3 near, Vector3 drift)
    {
        Dictionary<(int X, int Z), (Vector3 Sum, int Count, int Top)> found = [];
        foreach ((Cell cell, _) in cells)
        {
            (int, int) key = ((int)Math.Floor(cell.X / (double)cluster), (int)Math.Floor(cell.Z / (double)cluster));
            (Vector3 sum, int count, int top) = found.GetValueOrDefault(key, (Vector3.Zero, 0, int.MinValue));
            found[key] = (sum + new Vector3(cell.X + 0.5f, 0f, cell.Z + 0.5f), count + 1, Math.Max(top, cell.Y));
        }

        (Vector3 At, int Cells)[] nearest = [.. found.Values
            .Select(square => ((square.Sum / square.Count) with { Y = square.Top + 1 }, square.Count))
            .OrderBy(square => Vector3.DistanceSquared(square.Item1, near))
            .Take(slots)];
        for (int slot = 0; slot < slots; slot++)
        {
            squares[slot] = slot < nearest.Length ? nearest[slot] : null;
        }

        Volumes(drift);
    }

    /// <summary>Keeps each flame on its cell while it burns; fills empty flames from the hottest cells not too near another.</summary>
    private void Kindle(List<(Cell Cell, float Heat)> cells, Vector3 near)
    {
        Dictionary<Cell, float> hot = cells.Where(entry => entry.Heat >= FlameHeat).ToDictionary(entry => entry.Cell, entry => entry.Heat);
        List<Vector3> standing = [];
        for (int slot = 0; slot < flames.Length; slot++)
        {
            if (flames[slot] is { } flame)
            {
                if (hot.TryGetValue(flame.Cell, out float h))
                {
                    standing.Add(Centre(flame.Cell));
                    engine.Presentation.UpdateEmitter(flame.Emitter, Flame(slot, flame.Cell, h));
                }
                else
                {
                    PutOut(slot);
                }
            }
        }

        IEnumerator<KeyValuePair<Cell, float>> candidates = hot
            .Where(entry => flames.All(flame => flame?.Cell != entry.Key))
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => Vector3.DistanceSquared(Centre(entry.Key), near))
            .GetEnumerator();
        for (int slot = 0; slot < flames.Length; slot++)
        {
            if (flames[slot] is not null)
            {
                continue;
            }

            while (candidates.MoveNext())
            {
                (Cell cell, float h) = (candidates.Current.Key, candidates.Current.Value);
                Vector3 at = Centre(cell);
                if (standing.Any(other => Vector3.DistanceSquared(other, at) < FlameSpacing * FlameSpacing))
                {
                    continue;
                }

                flameSprite ??= engine.Graphics.OpenResource(new RenderResourceRequest(Fires.FrontFlameContentPath, TextureFilter.NearestMipmapped, TextureWrap.Clamp)).Handle;
                flames[slot] = (cell, engine.Presentation.CreateEmitter(Flame(slot, cell, h)));
                standing.Add(at);
                break;
            }
        }
    }

    private PresentationParticleDescriptor Flame(int slot, Cell cell, float h)
    {
        float scale = Math.Clamp(h / FlameScaleAtHeat, FlameScaleLeast, FlameScaleMost);
        return Fires.FrontFlame(ProductIds.ChemistryFireBase + (ulong)slot, $"craftsurvive.chemistry.flame.{slot}",
            frame.ToLocal(cell.X + 0.5, cell.Y + FlameAboveCell, cell.Z + 0.5), lean, scale, flameSprite!);
    }

    private void PutOut(int slot)
    {
        if (flames[slot] is { } flame)
        {
            flame.Emitter.Dispose();
            flames[slot] = null;
        }
    }

    /// <summary>Each square's light, flickering; the spare lights off.</summary>
    private void Lights()
    {
        for (int slot = 0; slot < slots; slot++)
        {
            bool on = squares[slot] is not null;
            (Vector3 at, int count) = squares[slot] ?? (Vector3.Zero, 0);
            double phase = (seconds * FlickerHertz) + (slot * 1.7);
            float flicker = 1f - (FlickerDepth * (float)(0.5 + (0.3 * Math.Sin(phase)) + (0.2 * Math.Sin(phase * 2.3 + 1.1))));
            LightDescriptor descriptor = new(
                LightKind.Point, FireLightColour, Math.Min(LightPerCell * count, LightMost) * flicker, on,
                on ? frame.ToLocal(at.X, at.Y + LightAbove, at.Z) : Vector3.Zero,
                -Vector3.UnitY, true, Math.Min(LightRangeLeast + (LightRangePerCell * count), LightRangeMost), LightDecay, 0f, 0f,
                LightShadowIntent.Disabled);
            LightRequest request = new(ProductIds.ChemistryFireLightBase + (ulong)slot, false, 0UL, descriptor);
            if (lights[slot] is Light light)
            {
                engine.Graphics.UpdateLight(new LightUpdateRequest(light, request));
            }
            else if (on)
            {
                lights[slot] = engine.Graphics.CreateLight(request);
            }
        }
    }

    /// <summary>Each square's glow and plume as fog volumes; the spare ones removed, and the sky told whether any stand.</summary>
    private void Volumes(Vector3 drift = default)
    {
        int placed = 0;
        for (int slot = 0; slot < slots; slot++)
        {
            if (squares[slot] is not (Vector3 at, int count))
            {
                continue;
            }

            float glow = Math.Clamp(GlowReachLeast + (GlowReachPerCell * count), GlowReachLeast, GlowReachMost);
            engine.CameraView.SetFogVolume(new FogVolumeRequest(ProductIds.ChemistryFireVolumeBase + (uint)placed++, FogVolumeShape.Ellipsoid,
                frame.ToLocal(at.X, at.Y + GlowAbove, at.Z), new Vector3(glow, GlowHalfHeight, glow), 0f, GlowDensity, GlowAlbedo, GlowEmission,
                GlowEdge, GlowNoiseScale, GlowNoiseStrength, new Vector3(drift.X, GlowRise, drift.Z)));
            float plume = Math.Clamp(PlumeReachLeast + (PlumeReachPerCell * count), PlumeReachLeast, PlumeReachMost);
            Vector3 downwind = new Vector3(drift.X, 0f, drift.Z) * PlumeDownwindSeconds;
            engine.CameraView.SetFogVolume(new FogVolumeRequest(ProductIds.ChemistryFireVolumeBase + (uint)placed++, FogVolumeShape.Ellipsoid,
                frame.ToLocal(at.X + downwind.X, at.Y + PlumeAbove, at.Z + downwind.Z), new Vector3(plume, PlumeHalfHeight, plume), 0f, PlumeDensity,
                PlumeAlbedo, PlumeEmission, PlumeEdge, PlumeNoiseScale, PlumeNoiseStrength, new Vector3(drift.X, PlumeRise, drift.Z)));
        }

        for (int id = placed; id < volumesPlaced; id++)
        {
            engine.CameraView.RemoveFogVolume(new FogVolumeRemoval(ProductIds.ChemistryFireVolumeBase + (uint)id));
        }

        volumesPlaced = placed;
        fogVolumes(placed > 0);
    }

    /// <summary>Places everything again when the frame origin has moved.</summary>
    private void Rebase()
    {
        if (frame.Origin != placedOrigin)
        {
            placedOrigin = frame.Origin;
            Replace();
        }
    }

    private void Replace()
    {
        foreach ((Cell cell, int slot) in burningDecals)
        {
            Place(BurningId(slot), cell, BurningRegion(cell));
        }

        for (int slot = 0; slot < burnt.Length; slot++)
        {
            if (burnt[slot] is { } entry)
            {
                Place(BurntId(slot), entry.Cell, entry.Cold ? CharRegion(entry.Cell) : CoolingRegion(entry.Cell));
            }
        }
    }

    /// <summary>Lays a cell's decal over the ground alone: on the grass's blades the projection smears into streaks down them.</summary>
    private void Place(ulong id, Cell cell, Vector4 region)
    {
        float across = DecalLeast + ((DecalMost - DecalLeast) * Unit(cell, 4));
        double x = cell.X + 0.5 + (DecalJitter * ((2 * Unit(cell, 5)) - 1)), z = cell.Z + 0.5 + (DecalJitter * ((2 * Unit(cell, 6)) - 1));
        engine.CameraView.SetDecal(new DecalRequest(id, frame.ToLocal(x, cell.Y + 0.5, z),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2 * (Hash(cell, 7) % 4)), new Vector3(across, DecalDepth, across), region, new Color(1f, 1f, 1f, 1f),
            Roughness: 0f, NormalStrength: 0f, AngleFalloff: true, OnVegetation: false, OnAnimatedMeshes: false, LifeSeconds: 0f, FadeSeconds: 0f));
    }

    private void Remove(ulong id) => engine.CameraView.RemoveDecal(new DecalRemoval(id));

    private static ulong BurningId(int slot) => ProductIds.ChemistryFireDecalBase + (ulong)slot;

    private static ulong BurntId(int slot) => ProductIds.ChemistryFireDecalBase + MostBurningDecals + (ulong)slot;

    private static Vector4 BurningRegion(Cell cell) => Region(Tile * (Hash(cell, 1) % BurningTiles), BurningRow);

    private static Vector4 CharRegion(Cell cell) => Region(CharColumn + (Tile * (Hash(cell, 2) % CharTiles)), BurningRow);

    private static Vector4 CoolingRegion(Cell cell) => Region(Tile * (Hash(cell, 3) % CoolingTiles), CoolingRow);

    private static Vector4 Region(float x, float y) => new(x / AtlasTexels, y / AtlasTexels, (x + Tile) / AtlasTexels, (y + Tile) / AtlasTexels);

    private static Vector3 Centre(Cell cell) => new(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);

    /// <summary>The cell's hash as a number from 0 to 1.</summary>
    private static float Unit(Cell cell, uint salt) => (Hash(cell, salt) & 0xFFFF) / 65535f;

    private static uint Hash(Cell cell, uint salt)
    {
        uint h = ((uint)cell.X * 374761393u) ^ ((uint)cell.Y * 668265263u) ^ ((uint)cell.Z * 2246822519u) ^ (salt * 3266489917u);
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }
}
