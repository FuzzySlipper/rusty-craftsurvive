using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using RustyChemistry.Content;
using RustyChemistry.Simulation;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>
/// Casts seen in the world (#10529), each in its quantity's overlay colour, so a cast whose field change shows nothing
/// of itself still reads:
/// <list type="bullet">
/// <item>where an operation lands, a burst of motes (thrown out where it adds, falling in where it drains), a light that
/// flares and holds while it acts, and a glowing patch on the ground as wide as its reach;</item>
/// <item>the cells a discharge ran through and a front's leading edge, as glints on the ground that fade at once;</item>
/// <item>around each cast, the ground's marks (<see cref="CastWatch"/>): glowing, wet or bleached cells while the field
/// holds them there.</item>
/// </list>
/// Everything stands in the scene's frame and nothing is saved.
/// </summary>
internal sealed class CastLook : IDisposable
{
    // The decal atlas (textures/decals/atlas.png; authoring/tools/generators/generate-cast-marks.py): white marks 64 x 64 from y 192, two soft
    // blobs and two mottled ones, which a mark's colour tints.
    private const float AtlasTexels = 256f;
    private static readonly Vector4[] Blobs = [Region(0f, 192f, 64f), Region(64f, 192f, 64f)];
    private static readonly Vector4[] Mottled = [Region(128f, 192f, 64f), Region(192f, 192f, 64f)];

    /// <summary>The decal pool: the ground's marks first, the passing marks after them, reused in turn.</summary>
    private const int MostMarks = 640;
    private const int Passing = ProductIds.ChemistryCastDecals - MostMarks;

    /// <summary>A ground mark's box (metres): a little wider than its cell, so neighbours overlap, and deep enough to reach the ground.</summary>
    private const float MarkSize = 1.8f, MarkDepth = 2.2f;

    /// <summary>A mark is placed again only when its share moves to another of this many steps.</summary>
    private const int MarkSteps = 4;

    // A glow mark: its colour's share over the ground at full, and its light (linear, above 1 blooms).
    private const float GlowCover = 0.35f, GlowEmission = 0.9f;

    // A wet mark: dark and glossy, more so as it is wetter; a dry one bleached towards straw.
    private static readonly Vector3 WetColour = new(0.04f, 0.06f, 0.09f), DryColour = new(0.5f, 0.36f, 0.1f);
    private const float WetCoverLeast = 0.25f, WetCoverMost = 0.7f, WetRoughness = 0.12f, DryCoverMost = 0.6f;

    // Where an operation lands: its patch (metres across per cell of reach, least and most), its glow, and how long it shows
    // (at least; longer while the operation acts, up to the most) and fades.
    private const float PatchPerReach = 2f, PatchLeast = 2f, PatchMost = 6f, PatchCover = 0.4f, PatchEmission = 1.2f;
    private const float PatchSecondsLeast = 0.8f, PatchSecondsMost = 5f, PatchFadeSeconds = 0.8f;

    // A passing mark: its size, glow (a discharge's brighter) and how briefly it shows and fades.
    private const float GlintSize = 1.3f, GlintEmission = 2f, DischargeEmission = 6f, GlintSeconds = 0.2f, GlintFadeSeconds = 0.35f;

    // A landing's light: its flare (and how fast it falls, seconds per e-fold), what it holds while the operation acts, its
    // height over the landing (metres), range and fall-off; the lights are reused in turn.
    private const float FlarePeak = 22f, FlareSeconds = 0.3f, HoldIntensity = 3f, LightAbove = 1f, LightRange = 12f, LightDecay = 2f;
    private const double LightOffBelow = 0.05;

    // A landing's burst of motes: how many, how long they live, how fast they are thrown, and their size (metres).
    private const int BurstCount = 48;
    private const float BurstLifeLeast = 0.5f, BurstLifeMost = 1.1f, BurstSpeed = 3.2f, BurstRise = 3.5f, BurstSize = 0.6f;

    /// <summary>Mixed into a running count for each burst's seed.</summary>
    private const ulong BurstMix = 0x9E37_79B9_7F4A_7C15UL;

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly IReadOnlyList<CastMarkSetting> marks;
    private readonly int[] markQuantities;
    private readonly Dictionary<int, Vector3> colours = [];
    private readonly Dictionary<Cell, (int Slot, int Mark, int Step)> placed = [];
    private readonly Stack<int> freeMarks = new(Enumerable.Range(0, MostMarks).Reverse());
    private readonly Light?[] lights = new Light?[ProductIds.ChemistryCastLights];
    private readonly (Vector3 At, Vector3 Colour, double Start, double Hold)?[] flares = new (Vector3, Vector3, double, double)?[ProductIds.ChemistryCastLights];
    private RenderResourceReference mote;
    private bool opened;
    private int nextPassing, nextLight;
    private ulong bursts;
    private long refused;
    private string lastFailure = "none";
    private (long X, long Y, long Z) placedOrigin;
    private double seconds;

    internal CastLook(IEngineContext engine, WorldFrame frame, ChemistrySettings settings, ChemistryPack pack)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pack);
        marks = settings.CastLooks.Marks;
        markQuantities = [.. marks.Select(mark => pack.Quantity(mark.Quantity))];
        foreach (OverlaySetting overlay in settings.Overlays)
        {
            Color linear = Color.FromSrgbHex(overlay.Srgb, 1f);
            colours[pack.Quantity(overlay.Quantity)] = new Vector3(linear.R, linear.G, linear.B);
        }

        placedOrigin = frame.Origin;
    }

    internal string Readout() =>
        string.Create(CultureInfo.InvariantCulture,
            $"castLook marks={placed.Count} flares={flares.Count(flare => flare is not null)} bursts={bursts} refused={refused} failure={lastFailure}");

    /// <summary>Shows what casts did over the worker's last steps.</summary>
    internal void Show(CastSeen seen)
    {
        ArgumentNullException.ThrowIfNull(seen);
        Rebase();
        foreach (CastLanding landing in seen.Landings)
        {
            Vector3 colour = Colour(landing.Quantity);
            Burst(landing, colour);
            Flare(landing, colour);
            float across = Math.Clamp(landing.Reach * PatchPerReach, PatchLeast, PatchMost);
            float shown = Math.Clamp(landing.Seconds, PatchSecondsLeast, PatchSecondsMost);
            Pass(landing.At, across, Blobs[Hash(new Cell((int)landing.At.X, (int)landing.At.Y, (int)landing.At.Z), 1) % Blobs.Length], new Color(colour.X, colour.Y, colour.Z, PatchCover), colour * PatchEmission, shown, PatchFadeSeconds);
        }

        foreach (CastSpark spark in seen.Sparks)
        {
            Vector3 colour = Colour(spark.Quantity);
            Pass(new Vector3(spark.Cell.X + 0.5f, spark.Cell.Y + 0.5f, spark.Cell.Z + 0.5f), GlintSize, Blobs[Hash(spark.Cell, 1) % Blobs.Length],
                new Color(colour.X, colour.Y, colour.Z, 1f), colour * (spark.Discharge ? DischargeEmission : GlintEmission), GlintSeconds, GlintFadeSeconds);
        }

        if (seen.Marks is { } now)
        {
            Ground(now);
        }
    }

    /// <summary>Advances the flares; called every update.</summary>
    internal void Tick(double elapsed)
    {
        seconds += elapsed;
        for (int slot = 0; slot < flares.Length; slot++)
        {
            if (flares[slot] is not { } flare)
            {
                continue;
            }

            double age = seconds - flare.Start;
            double intensity = (FlarePeak * Math.Exp(-age / FlareSeconds)) + (age < flare.Hold ? HoldIntensity : 0);
            if (intensity < LightOffBelow)
            {
                flares[slot] = null;
                Light(slot, flare.At, flare.Colour, 0f, on: false);
                continue;
            }

            Light(slot, flare.At, flare.Colour, (float)intensity, on: true);
        }
    }

    /// <summary>Takes every mark and flare out of the scene (a dungeon, a restart); bursts and passing marks fade on their own.</summary>
    internal void Hide()
    {
        foreach ((int slot, _, _) in placed.Values)
        {
            engine.CameraView.RemoveDecal(new DecalRemoval(MarkId(slot)));
            freeMarks.Push(slot);
        }

        placed.Clear();
        for (int slot = 0; slot < flares.Length; slot++)
        {
            if (flares[slot] is { } flare)
            {
                flares[slot] = null;
                Light(slot, flare.At, flare.Colour, 0f, on: false);
            }
        }
    }

    public void Dispose()
    {
        Hide();
        foreach (Light? light in lights)
        {
            light?.Dispose();
        }

        Array.Clear(lights);
    }

    /// <summary>The ground's marks as the watch last read them: new and changed ones placed, gone ones removed.</summary>
    private void Ground(IReadOnlyList<CastMark> now)
    {
        Dictionary<Cell, CastMark> wanted = now.ToDictionary(mark => mark.Cell);
        foreach (Cell gone in placed.Keys.Where(cell => !wanted.ContainsKey(cell)).ToList())
        {
            int slot = placed[gone].Slot;
            engine.CameraView.RemoveDecal(new DecalRemoval(MarkId(slot)));
            placed.Remove(gone);
            freeMarks.Push(slot);
        }

        foreach (CastMark mark in now)
        {
            int step = Math.Clamp((int)MathF.Ceiling(mark.Share * MarkSteps), 1, MarkSteps);
            if (placed.TryGetValue(mark.Cell, out (int Slot, int Mark, int Step) was))
            {
                if (was.Mark == mark.Mark && was.Step == step)
                {
                    continue;
                }

                placed[mark.Cell] = (was.Slot, mark.Mark, step);
                Place(was.Slot, mark.Cell, mark.Mark, step);
            }
            else if (freeMarks.TryPop(out int slot))
            {
                placed[mark.Cell] = (slot, mark.Mark, step);
                Place(slot, mark.Cell, mark.Mark, step);
            }
        }
    }

    /// <summary>
    /// Lays one ground mark over its cell's ground and the grass standing on it: in a meadow the blades hide the ground, and
    /// the mark is what the grass shows (darkened and glossy where wet, bleached where dry), so it is not faded down them.
    /// </summary>
    private void Place(int slot, Cell cell, int mark, int step)
    {
        float share = step / (float)MarkSteps;
        CastMarkSetting setting = marks[mark];
        Vector3 colour = Colour(markQuantities[mark]);
        (Color tint, float roughness, Vector3 emission, Vector4 region) = setting.Look switch
        {
            CastMarkLook.Wet => (new Color(WetColour.X, WetColour.Y, WetColour.Z, WetCoverLeast + ((WetCoverMost - WetCoverLeast) * share)), WetRoughness,
                Vector3.Zero, Blobs[Hash(cell, 2) % Blobs.Length]),
            CastMarkLook.Dry => (new Color(DryColour.X, DryColour.Y, DryColour.Z, DryCoverMost * share), 0f, Vector3.Zero, Mottled[Hash(cell, 2) % Mottled.Length]),
            _ => (new Color(colour.X, colour.Y, colour.Z, GlowCover * share), 0f, colour * (GlowEmission * share), Blobs[Hash(cell, 2) % Blobs.Length]),
        };
        engine.CameraView.SetDecal(new DecalRequest(MarkId(slot), frame.ToLocal(cell.X + 0.5, cell.Y + 0.5, cell.Z + 0.5),
            Turn(cell), new Vector3(MarkSize, MarkDepth, MarkSize), region, tint, Roughness: roughness, NormalStrength: 0f, Emission: emission,
            AngleFalloff: false, OnVegetation: true, OnAnimatedMeshes: false, LifeSeconds: 0f, FadeSeconds: 0f));
    }

    /// <summary>A mark that shows for a while and fades, from the passing pool in turn.</summary>
    private void Pass(Vector3 at, float across, Vector4 region, Color tint, Vector3 emission, float shown, float fade)
    {
        ulong id = ProductIds.ChemistryCastDecalBase + MostMarks + (ulong)nextPassing;
        nextPassing = (nextPassing + 1) % Passing;
        engine.CameraView.SetDecal(new DecalRequest(id, frame.ToLocal(at.X, at.Y, at.Z), Quaternion.Identity, new Vector3(across, MarkDepth, across),
            region, tint, Roughness: 0f, NormalStrength: 0f, Emission: emission, AngleFalloff: true, OnVegetation: false, OnAnimatedMeshes: false,
            LifeSeconds: shown, FadeSeconds: fade));
    }

    /// <summary>A landing's motes: thrown up and out where it adds, sinking in and shrinking where it drains.</summary>
    private void Burst(CastLanding landing, Vector3 colour)
    {
        if (!opened)
        {
            RenderResourceInfo resource = engine.Graphics.OpenResource(new RenderResourceRequest(Bursts.PuffContentPath));
            if (resource.Kind != RenderResourceKind.Texture || resource.ByteLength == 0)
            {
                throw new InvalidOperationException($"CraftSurvive dust sprite '{Bursts.PuffContentPath}' must be a non-empty Engine texture.");
            }

            mote = resource.Handle;
            opened = true;
        }

        bursts++;
        Vector3 spread = new(BurstSpeed, 0f, BurstSpeed);
        (Vector3 least, Vector3 most, Vector3 pull, float start, float end) = landing.Drains
            ? (-spread * 0.3f - (Vector3.UnitY * 0.5f), (spread * 0.3f) + (Vector3.UnitY * 0.3f), -Vector3.UnitY, BurstSize * 2f, 0.05f)
            : (-spread + (Vector3.UnitY * 0.5f), spread + (Vector3.UnitY * BurstRise), -Vector3.UnitY * 2f, BurstSize, BurstSize * 0.4f);
        Color bright = new(colour.X * 2f, colour.Y * 2f, colour.Z * 2f, 1f), gone = new(colour.X, colour.Y, colour.Z, 0f);
        PresentationParticleDescriptor descriptor = new()
        {
            SignalId = $"craftsurvive.cast.burst.{bursts}",
            Visible = true,
            Anchor = new PresentationAnchor { Kind = PresentationAnchorKind.World, Position = frame.ToLocal(landing.At.X, landing.At.Y, landing.At.Z) },
            Sprite = mote,
            SpriteFrameCount = 1,
            Visual = PresentationParticleVisual.Billboard,
            SizeMode = PresentationParticleSizeMode.World,
            Blend = PresentationParticleBlendMode.Additive,
            BurstCount = BurstCount,
            RatePerSecond = 0f,
            MaxParticles = BurstCount,
            LifetimeMinSeconds = BurstLifeLeast,
            LifetimeMaxSeconds = BurstLifeMost,
            VelocityMin = least,
            VelocityMax = most,
            Acceleration = pull,
            SizeCurve = new[] { new PresentationParticleScalarKey { Age = 0f, Value = start }, new PresentationParticleScalarKey { Age = 1f, Value = end } },
            // Already linear: the overlay colours are decoded once.
            ColorCurve = new[] { new PresentationParticleColorKey { Age = 0f, Color = bright }, new PresentationParticleColorKey { Age = 1f, Color = gone } },
            Seed = Bursts.Seed(unchecked(bursts * BurstMix)),
        };
        try
        {
            engine.Presentation.EmitParticles(descriptor);
        }
        catch (EngineCallException exception)
        {
            refused++;
            lastFailure = exception.Message;
        }
    }

    /// <summary>Starts a landing's light in the next slot: a flare, then what it holds while the operation acts.</summary>
    private void Flare(CastLanding landing, Vector3 colour)
    {
        int slot = nextLight;
        nextLight = (nextLight + 1) % flares.Length;
        flares[slot] = (landing.At + (Vector3.UnitY * LightAbove), colour, seconds, landing.Seconds);
        Light(slot, flares[slot]!.Value.At, colour, FlarePeak, on: true);
    }

    private void Light(int slot, Vector3 at, Vector3 colour, float intensity, bool on)
    {
        LightDescriptor descriptor = new(
            LightKind.Point, colour, intensity, on, on ? frame.ToLocal(at.X, at.Y, at.Z) : Vector3.Zero,
            -Vector3.UnitY, true, LightRange, LightDecay, 0f, 0f, LightShadowIntent.Disabled);
        LightRequest request = new(ProductIds.ChemistryCastLightBase + (ulong)slot, false, 0UL, descriptor);
        if (lights[slot] is Light light)
        {
            engine.Graphics.UpdateLight(new LightUpdateRequest(light, request));
        }
        else if (on)
        {
            lights[slot] = engine.Graphics.CreateLight(request);
        }
    }

    /// <summary>Places the ground's marks again when the frame origin has moved; passing marks are let fade where they were.</summary>
    private void Rebase()
    {
        if (frame.Origin == placedOrigin)
        {
            return;
        }

        placedOrigin = frame.Origin;
        foreach ((Cell cell, (int slot, int mark, int step)) in placed)
        {
            Place(slot, cell, mark, step);
        }
    }

    private Vector3 Colour(int quantity) => colours.GetValueOrDefault(quantity, Vector3.One);

    private static ulong MarkId(int slot) => ProductIds.ChemistryCastDecalBase + (ulong)slot;

    private static Quaternion Turn(Cell cell) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2 * (Hash(cell, 3) % 4));

    private static Vector4 Region(float x, float y, float size) =>
        new(x / AtlasTexels, y / AtlasTexels, (x + size) / AtlasTexels, (y + size) / AtlasTexels);

    private static int Hash(Cell cell, uint salt)
    {
        uint h = ((uint)cell.X * 374761393u) ^ ((uint)cell.Y * 668265263u) ^ ((uint)cell.Z * 2246822519u) ^ (salt * 3266489917u);
        h = (h ^ (h >> 13)) * 1274126177u;
        return (int)((h ^ (h >> 16)) & 0x7FFF_FFFF);
    }
}
