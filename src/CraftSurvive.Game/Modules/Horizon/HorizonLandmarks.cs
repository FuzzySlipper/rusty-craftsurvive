using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Places;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Horizon;

/// <summary>
/// Known places on the horizon (#9751, H3 #9781). Each place on the map (home, ruins, vantage points
/// and dungeon entrances) stands where the map has it as a small silhouette of its kind:
/// <list type="bullet">
/// <item>past the far field's reach, in the backdrop on the map's ground;</item>
/// <item>between the near ground's edge and that reach, in the world on the far field's ground;</item>
/// <item>within the near ground, nothing: the place's own structure is built there.</item>
/// </list>
/// The edges are measured square, as those windows are (<see cref="HorizonLandmarkRules.Band"/>).
/// A real site is about ten metres across, a few pixels a kilometre off, so a silhouette never shrinks
/// below <see cref="HorizonLandmarkRules.MinimumDegrees"/> tall: past half a kilometre or so it is drawn larger than life. It is
/// fogged like the land it stands on, so it fades with it. By night home, the one inhabited place, shows
/// its lights: glowing points (Engine particles, which take no fog) dimmed by distance here instead.
/// </summary>
internal sealed class HorizonLandmarks : IDisposable
{
    /// <summary>The nearest places drawn; the rest wait until the player is nearer.</summary>
    private const int MaximumLandmarks = 64;
    /// <summary>The places are listed again every this many updates: they change only when one is found or home moves.</summary>
    private const int ListEveryUpdates = 30;

    /// <summary>A box of a silhouette, in metres from the site's ground at its centre.</summary>
    private readonly record struct Block(Vector3 Centre, Vector3 Half, Color Colour, bool Roof = false);

    /// <summary>A kind's silhouette: its blocks and its true height (what <see cref="HorizonLandmarkRules.MinimumDegrees"/> is measured against).</summary>
    private sealed record Shape(Block[] Blocks, float Height);

    // Home's silhouette: darker than the map's timber, since a silhouette has to stand out of the pale haze it is seen through.
    // Authored as sRGB hex and decoded here, once: the blocks' vertex colours are linear (#9844).
    private static readonly Color DarkStone = Color.FromSrgbHex(0x7c7979);
    private static readonly Color Timber = Color.FromSrgbHex(0x8e7961);
    private static readonly Color Thatch = Color.FromSrgbHex(0x7c6550);
    /// <summary>Every silhouette reaches this far below its ground, so a coarse tier's ground never shows under it.</summary>
    private const float Foundation = 3f;

    private static readonly Shape Home = new(
    [
        new(new(0, 2, 0), new(3.6f, 2 + Foundation, 3.1f), Timber),
        new(new(0, 5.75f, 0), new(4.2f, 1.75f, 3.7f), Thatch, Roof: true),
        new(new(2, 7, 1.4f), new(0.5f, 1.5f, 0.5f), DarkStone),
    ], 8.5f);

    /// <summary>Home's lights, in metres from its ground: a window on each side, so one shows from any bearing.</summary>
    private static readonly Vector3[] HomeLights = [new(3.7f, 2, 0), new(-3.7f, 2, 0), new(0, 2, 3.2f), new(0, 2, -3.2f)];
    private const string LightContentPath = "textures/fire-ember.png";
    private const string LightSignal = "craftsurvive.horizon.light";
    /// <summary>A light is this wide at true size, and at least <see cref="LightMinimumDegrees"/> across as seen.</summary>
    private const float LightMetres = 1.6f;
    private const double LightMinimumDegrees = 0.8;
    /// <summary>A light dims by <c>exp(-distance / LightHazeMetres)</c>: the air between, since particles take no fog.</summary>
    private const double LightHazeMetres = 40_000;
    /// <summary>A lit window's glow, sRGB; decoded for the particle keys, which are linear (#9844).</summary>
    private static readonly Color LightColour = new(1f, 0.7f, 0.36f, 1f);
    /// <summary>
    /// Each light is several long-lived glows at one point, overlapping as they come and go, so it flickers a
    /// little. They are added to the frame, so together they are brighter than one: the Engine takes no
    /// colour above white, and a window a few pixels across still has to read as a light.
    /// </summary>
    private const float LightLifeSeconds = 7f;
    private const uint LightGlows = 5;
    /// <summary>A light is moved only once it has moved this share of its size, or its brightness this much.</summary>
    private const float LightMoveShare = 0.1f, LightBrightnessStep = 0.05f;

    private readonly IEngineContext engine;
    private readonly Func<IReadOnlyList<KnownPlace>> places;
    private readonly Func<Vector3> playerWorld;
    private readonly Func<double, double, double> worldGround;
    private readonly Func<Vector3, Vector3> toLocal;
    private readonly Func<double> nearEdge;
    private readonly Dictionary<Shape, (MeshResource Mesh, Appearance Look)> built = [];
    private readonly PlaceSilhouettes silhouettes;
    private Material? material;
    private RenderResource? glow;
    private IReadOnlyList<KnownPlace> listed = [];
    private int sinceListed = ListEveryUpdates;
    private double scaleMetres = 1;
    /// <summary>A light was refused since the places were last listed: none is asked for again until they are.</summary>
    private bool refusedSinceListed;
    /// <summary>What was placed this update: the facts publish it, the lights are lit from it.</summary>
    private readonly List<(KnownPlace Place, Placement At)> placed = [];
    private readonly Dictionary<int, (PresentationEmitter Emitter, PresentationParticleDescriptor Descriptor)> lights = [];

    /// <summary>Where a silhouette stands: in the backdrop or the world, its position there, and its scale.</summary>
    private readonly record struct Placement(bool Backdrop, Vector3 Position, float Scale, double Distance, float LightScale);

    /// <summary>The sky's daylight (0 night to 1 day): home's lights show as it falls.</summary>
    internal Func<double> Daylight { get; set; } = () => 1;

    /// <summary>Lights the Engine refused (its particle budget spent), and why the last was.</summary>
    internal int LightsRefused { get; private set; }

    internal string? LastRefusal { get; private set; }

    /// <param name="places">The places the expedition knows (home first).</param>
    /// <param name="worldGround">The terrain's ground height at a world point (x, z), which the far field draws.</param>
    /// <param name="toLocal">A world point in the renderer's frame.</param>
    /// <param name="nearEdge">The near ground's edge from the player in metres: places within it are their own structures.</param>
    internal HorizonLandmarks(IEngineContext engine, ProductContent content, Func<IReadOnlyList<KnownPlace>> places, Func<Vector3> playerWorld,
        Func<double, double, double> worldGround, Func<Vector3, Vector3> toLocal, Func<double> nearEdge)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        silhouettes = new PlaceSilhouettes(engine, content ?? throw new ArgumentNullException(nameof(content)));
        this.places = places ?? throw new ArgumentNullException(nameof(places));
        this.playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        this.worldGround = worldGround ?? throw new ArgumentNullException(nameof(worldGround));
        this.toLocal = toLocal ?? throw new ArgumentNullException(nameof(toLocal));
        this.nearEdge = nearEdge ?? throw new ArgumentNullException(nameof(nearEdge));
    }

    /// <summary>
    /// Places the known places about the player and lights home by night; none while <paramref name="shown"/> is false.
    /// </summary>
    /// <param name="scale">Metres per backdrop unit.</param>
    /// <param name="reach">The far field's reach in metres: past it a place stands in the backdrop.</param>
    /// <param name="backdropGround">The backdrop's drawn ground height at a world point (x, z).</param>
    internal void Update(double scale, bool shown, double reach, Func<double, double, double> backdropGround)
    {
        scaleMetres = scale;
        placed.Clear();
        if (shown)
        {
            if (++sinceListed >= ListEveryUpdates)
            {
                listed = places();
                sinceListed = 0;
                refusedSinceListed = false;
            }

            Vector3 at = playerWorld();
            Vector2 here = new(at.X, at.Z);
            double near = nearEdge();
            foreach ((KnownPlace place, double distance, LandmarkBand band) in listed.Where(place => Of(place) is not null)
                .Select(place => (Place: place, Distance: (double)Vector2.Distance(place.Position, here),
                    Band: HorizonLandmarkRules.Band(place.Position.X - here.X, place.Position.Y - here.Y, near, reach)))
                .Where(entry => entry.Band != LandmarkBand.None)
                .OrderBy(entry => entry.Distance).Take(MaximumLandmarks))
            {
                placed.Add((place, Place(place, distance, band == LandmarkBand.Backdrop, scale, backdropGround)));
            }
        }

        UpdateLights(shown);
    }

    /// <summary>Where a place's silhouette stands at its distance, in the backdrop or the world.</summary>
    private Placement Place(KnownPlace place, double distance, bool backdrop, double scale, Func<double, double, double> backdropGround)
    {
        float grow = (float)HorizonLandmarkRules.Grow(distance, Of(place)!.Value.Height);
        float lightGrow = (float)Math.Max(grow, HorizonLandmarkRules.Grow(distance, LightMetres, LightMinimumDegrees));
        double ground = backdrop ? backdropGround(place.Position.X, place.Position.Y) : worldGround(place.Position.X, place.Position.Y);
        return backdrop
            ? new(true, new Vector3((float)(place.Position.X / scale), (float)(ground / scale), (float)(place.Position.Y / scale)), (float)(grow / scale), distance, (float)(lightGrow / scale))
            : new(false, toLocal(new Vector3(place.Position.X, (float)ground, place.Position.Y)), grow, distance, lightGrow);
    }

    /// <summary>The silhouettes placed this update, for the appearance snapshot.</summary>
    internal IEnumerable<AppearanceFact> Facts()
    {
        int index = 0;
        foreach ((KnownPlace place, Placement at) in placed)
        {
            if (index >= ProductIds.HorizonLandmarkLimit) yield break;
            yield return new AppearanceFact(ProductIds.HorizonLandmarkBase + (ulong)index++, false, 0,
                new Transform(at.Position, Quaternion.Identity, new Vector3(at.Scale)), Of(place)!.Value.Look, true,
                at.Backdrop ? RenderLayer.Backdrop : RenderLayer.Scene, at.Backdrop ? ShadowCasting.None : ShadowCasting.Cast);
        }
    }

    /// <summary>
    /// Home's silhouette, and a map site's: the place's own kit pieces in its kind's layout (PlaceSilhouettes,
    /// #9992), so the horizon shows what stands there. The sled is not a landmark.
    /// </summary>
    private (Appearance Look, float Height)? Of(KnownPlace place) =>
        place.Kind == KnownPlaceKind.Home ? (Silhouette(Home), Home.Height)
        : place.Site == PoiKind.None ? null : silhouettes.For(place.Site);

    /// <summary>
    /// The lights of home while the daylight is low: one emitter per window, placed and sized with its
    /// silhouette, created, moved and lifted as that changes.
    /// </summary>
    private void UpdateLights(bool shown)
    {
        float night = (float)HorizonLandmarkRules.Lit(Daylight());
        HashSet<int> keep = [];
        if (shown && night > 0)
        {
            foreach ((KnownPlace place, Placement at) in placed)
            {
                if (place.Kind != KnownPlaceKind.Home) continue;
                float brightness = night * (float)Math.Exp(-at.Distance / LightHazeMetres);
                for (int window = 0; window < HomeLights.Length; window++)
                {
                    keep.Add(window);
                    PresentationParticleDescriptor wanted = Light(window, at, brightness);
                    if (lights.TryGetValue(window, out var lit))
                    {
                        if (lit.Descriptor.Backdrop != wanted.Backdrop)
                        {
                            lit.Emitter.Dispose();
                            lights.Remove(window);
                        }
                        else
                        {
                            if (Moved(lit.Descriptor, wanted))
                            {
                                engine.Presentation.UpdateEmitter(lit.Emitter, wanted);
                                lights[window] = (lit.Emitter, wanted);
                            }

                            continue;
                        }
                    }

                    if (refusedSinceListed)
                    {
                        keep.Remove(window);
                        continue;
                    }

                    try
                    {
                        lights[window] = (engine.Presentation.CreateEmitter(wanted), wanted);
                    }
                    catch (EngineCallException refused)
                    {
                        // The particle budget is shared: when it is spent, home stands unlit until the places are listed again.
                        keep.Remove(window);
                        refusedSinceListed = true;
                        LightsRefused++;
                        LastRefusal = refused.Message;
                    }
                }

                break;
            }
        }

        foreach (int gone in lights.Keys.Where(window => !keep.Contains(window)).ToList())
        {
            lights[gone].Emitter.Dispose();
            lights.Remove(gone);
        }
    }

    private static bool Moved(PresentationParticleDescriptor was, PresentationParticleDescriptor now)
    {
        float size = now.SizeCurve.Span[0].Value;
        return Vector3.Distance(was.Anchor.Position, now.Anchor.Position) > size * LightMoveShare
            || Math.Abs(was.SizeCurve.Span[0].Value - size) > size * LightMoveShare
            || Math.Abs(was.ColorCurve.Span[1].Color.A - now.ColorCurve.Span[1].Color.A) > LightBrightnessStep;
    }

    private PresentationParticleDescriptor Light(int window, Placement at, float brightness)
    {
        glow ??= engine.Graphics.OpenResource(new RenderResourceRequest(LightContentPath, TextureFilter.Linear, TextureWrap.Clamp)).Handle;
        Vector3 offset = HomeLights[window] * at.Scale;
        float size = LightMetres * at.LightScale;
        Color lit = LightColour.Linear() with { A = brightness };
        return new PresentationParticleDescriptor
        {
            LogicalId = ProductIds.HorizonLandmarkBase + ProductIds.HorizonLandmarkLimit + (ulong)window,
            SignalId = LightSignal,
            Visible = true,
            Seed = (ulong)(window + 1),
            Anchor = new PresentationAnchor { Kind = PresentationAnchorKind.World, Position = at.Position + offset },
            Visual = PresentationParticleVisual.Billboard,
            Sprite = glow,
            SpriteFrameCount = 1,
            SizeMode = PresentationParticleSizeMode.World,
            Blend = PresentationParticleBlendMode.Additive,
            RatePerSecond = LightGlows / LightLifeSeconds,
            BurstCount = LightGlows,
            MaxParticles = LightGlows + 1,
            LifetimeMinSeconds = LightLifeSeconds * 0.7f,
            LifetimeMaxSeconds = LightLifeSeconds,
            VelocityMin = Vector3.Zero,
            VelocityMax = Vector3.Zero,
            SizeCurve = new PresentationParticleScalarKey[] { new(0f, size), new(1f, size) },
            ColorCurve = new PresentationParticleColorKey[] { new(0f, lit with { A = 0f }), new(0.2f, lit), new(0.8f, lit), new(1f, lit with { A = 0f }) },
            Backdrop = at.Backdrop,
        };
    }

    /// <summary>A shape's mesh, built once: its blocks as boxes, a roof block as a pyramid, coloured by vertex.</summary>
    private Appearance Silhouette(Shape shape)
    {
        if (built.TryGetValue(shape, out var made)) return made.Look;
        List<Vector3> positions = [];
        List<Vector3> normals = [];
        List<Color> colours = [];
        List<uint> indices = [];
        void Face(Color colour, params Vector3[] corners)
        {
            Vector3 normal = Vector3.Normalize(Vector3.Cross(corners[1] - corners[0], corners[2] - corners[0]));
            uint first = (uint)positions.Count;
            foreach (Vector3 corner in corners)
            {
                positions.Add(corner);
                normals.Add(normal);
                colours.Add(colour);
            }

            for (uint i = 1; i + 1 < corners.Length; i++) indices.AddRange([first, first + i, first + i + 1]);
        }

        foreach (Block block in shape.Blocks)
        {
            Vector3 lo = block.Centre - block.Half, hi = block.Centre + block.Half;
            Vector3 p(float x, float y, float z) => new(x, y, z);
            if (block.Roof)
            {
                // Eaves at the block's base, rising to a point at its top.
                Vector3 apex = p(block.Centre.X, hi.Y, block.Centre.Z);
                Face(block.Colour, p(lo.X, lo.Y, lo.Z), apex, p(hi.X, lo.Y, lo.Z));
                Face(block.Colour, p(hi.X, lo.Y, lo.Z), apex, p(hi.X, lo.Y, hi.Z));
                Face(block.Colour, p(hi.X, lo.Y, hi.Z), apex, p(lo.X, lo.Y, hi.Z));
                Face(block.Colour, p(lo.X, lo.Y, hi.Z), apex, p(lo.X, lo.Y, lo.Z));
                Face(block.Colour, p(lo.X, lo.Y, lo.Z), p(hi.X, lo.Y, lo.Z), p(hi.X, lo.Y, hi.Z), p(lo.X, lo.Y, hi.Z));
                continue;
            }

            Face(block.Colour, p(lo.X, lo.Y, lo.Z), p(lo.X, hi.Y, lo.Z), p(hi.X, hi.Y, lo.Z), p(hi.X, lo.Y, lo.Z));
            Face(block.Colour, p(hi.X, lo.Y, hi.Z), p(hi.X, hi.Y, hi.Z), p(lo.X, hi.Y, hi.Z), p(lo.X, lo.Y, hi.Z));
            Face(block.Colour, p(lo.X, lo.Y, hi.Z), p(lo.X, hi.Y, hi.Z), p(lo.X, hi.Y, lo.Z), p(lo.X, lo.Y, lo.Z));
            Face(block.Colour, p(hi.X, lo.Y, lo.Z), p(hi.X, hi.Y, lo.Z), p(hi.X, hi.Y, hi.Z), p(hi.X, lo.Y, hi.Z));
            Face(block.Colour, p(lo.X, hi.Y, lo.Z), p(lo.X, hi.Y, hi.Z), p(hi.X, hi.Y, hi.Z), p(hi.X, hi.Y, lo.Z));
        }

        material ??= engine.Graphics.CreateMaterial(new MaterialRequest(new Color(1, 1, 1, 1), default(RenderResourceReference), Roughness,
            new Color(1, 1, 1, 1), Vector3.Zero, 0f, false));
        MeshResource mesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(),
            Enumerable.Repeat(Vector2.Zero, positions.Count).ToArray(), colours.ToArray(), indices.ToArray(),
            new MeshGroup[] { new(0, 0, (uint)indices.Count) }, new MeshMaterialBinding[] { new(0, material) }));
        Appearance appearance = engine.Graphics.CreateMeshAppearance(mesh);
        built[shape] = (mesh, appearance);
        return appearance;
    }

    private const float Roughness = 0.9f;

    internal string Readout()
    {
        string nearest = placed.Count == 0 ? "" : string.Create(CultureInfo.InvariantCulture,
            $" nearest={placed[0].Place.Key}@{placed[0].At.Distance / 1000:F1}km{(placed[0].At.Backdrop ? "/backdrop" : "/world")}x{placed[0].At.Scale * (placed[0].At.Backdrop ? scaleMetres : 1):F1}");
        return string.Create(CultureInfo.InvariantCulture, $"landmarks={placed.Count} lights={lights.Count} lightsRefused={LightsRefused}") + nearest + (LastRefusal is null ? "" : $" lastRefusal=\"{LastRefusal}\"");
    }

    public void Dispose()
    {
        foreach ((PresentationEmitter emitter, _) in lights.Values) emitter.Dispose();
        lights.Clear();
        glow?.Dispose();
        glow = null;
        foreach ((MeshResource mesh, Appearance look) in built.Values)
        {
            look.Dispose();
            mesh.Dispose();
        }

        built.Clear();
        silhouettes.Dispose();
        material?.Dispose();
        material = null;
    }
}
