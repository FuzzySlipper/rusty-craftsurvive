using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Towns;

/// <summary>
/// The keeper's bust in the dialogue (#10027): their portrait rig, posed live by the Engine in the window the town UI
/// leaves in its portrait frame and anchors as <see cref="Anchor"/>. It rests, and plays its talking clip while a new
/// line is said. Owns the portrait rigs and their placed appearances; the town module says who is shown and when.
/// </summary>
internal sealed class TownPortraitView(IEngineContext engine) : IDisposable
{
    /// <summary>The UI element the bust follows: the portrait frame's window.</summary>
    internal const string Anchor = "town-portrait";

    private const float AlphaCutoff = .5f, ClipCrossfadeSeconds = .12f;
    private const int RenderOrder = 0;
    // Where the bust stands while no page reports the window (a snapshot or offline render): the dialogue's 3x window
    // on a 1920 x 1080 view, in CSS pixels from its lower left.
    private const float FallbackLeft = 409, FallbackBottom = 87, FallbackSide = 381, FallbackWidth = 1920, FallbackHeight = 1080;
    private static readonly Vector2 Centred = new(.5f);
    private static readonly Color White = new(1, 1, 1, 1);

    private readonly Dictionary<string, PartsRig> rigs = [];
    private readonly Dictionary<TownPortrait, Appearance> looks = [];
    private TownPortrait? shown;
    private string clip = "";
    private bool published, playing;
    private string? played;

    /// <summary>The bust's draw fact while a keeper with a portrait is speaking; none otherwise.</summary>
    internal AppearanceFact[] Facts => shown is null ? [] :
        [new(ProductIds.TownPortraitId, false, 0, new(Vector3.Zero, Quaternion.Identity, Vector3.One), Look(shown), true, RenderLayer.Viewmodel)];

    /// <summary>The clip the bust is playing, or none, for readouts.</summary>
    internal string Readout => shown is null ? "none" : playing ? clip : "pending";

    /// <summary>Shows this keeper's bust (or none), talking or at rest. A change of clip crossfades.</summary>
    internal void Show(TownPortrait? portrait, bool talking)
    {
        if (portrait != shown) playing = false;
        shown = portrait;
        clip = portrait is null ? "" : talking ? portrait.Talk : portrait.Rest;
        if (playing && published && clip != played) Play();
    }

    /// <summary>Starts the clip once the bust is published; a bust withdrawn and shown again starts afresh.</summary>
    internal void AfterAppearanceSnapshot()
    {
        published = shown is not null;
        if (!published) playing = false;
        else if (!playing) Play();
    }

    private void Play()
    {
        engine.PartsRigs.Play(new PartsRigPlayRequest(ProductIds.TownPortraitId, clip, playing ? ClipCrossfadeSeconds : 0));
        played = clip;
        playing = true;
    }

    /// <summary>A keeper's bust: their rig, loaded once, unlit, on its texel grid and placed in the frame's window.</summary>
    private Appearance Look(TownPortrait portrait)
    {
        if (looks.TryGetValue(portrait, out Appearance? look)) return look;
        if (!rigs.TryGetValue(portrait.Rig, out PartsRig? rig))
        {
            rig = engine.PartsRigs.Load(new PartsRigLoadRequest(portrait.Rig, TextureFilter.Nearest)).Rig;
            rigs[portrait.Rig] = rig;
        }

        SpriteMaterialDescriptor unlit = new(SpriteLightingMode.Unlit, default, default, 0, 0, SpriteAlphaMode.Mask, AlphaCutoff, SpriteShadowPolicy.None);
        // Placed in the viewport, its scale, size and billboard modes are not read.
        look = engine.PartsRigs.CreateAppearance(new PartsRigAppearanceRequest(new PartsRigView[] { new(rig, 0, false) },
            1, SpriteSizeMode.World, BillboardMode.None, RenderOrder, SpriteDepthPolicy.Default, White, unlit, portrait.TexelUnits));
        engine.PartsRigs.SetViewport(new PartsRigViewportRequest(look,
            new Vector2(FallbackLeft / FallbackWidth, FallbackBottom / FallbackHeight), new Vector2(FallbackSide / FallbackWidth, FallbackSide / FallbackHeight),
            Centred, SpriteViewportFit.Contain, portrait.FrameMinimum, new Vector2(portrait.FrameUnits)));
        engine.PartsRigs.SetViewportAnchor(new PartsRigViewportAnchorRequest(look, Anchor));
        looks[portrait] = look;
        return look;
    }

    /// <summary>Releases the busts; the caller has already published a snapshot without them.</summary>
    public void Dispose()
    {
        shown = null;
        foreach (Appearance look in looks.Values) look.Dispose();
        looks.Clear();
        foreach (PartsRig rig in rigs.Values) rig.Dispose();
        rigs.Clear();
    }
}
