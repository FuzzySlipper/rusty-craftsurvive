using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// Placeholder bodies for wildlife (#10567) until S3 brings the meshes: each animal is a torso, a
/// head and a block of legs, proportioned by its kind's body and coloured by it, so behaviour can be
/// reviewed now. Posture shows what an animal is doing: lying at rest, head down grazing, head up
/// looking, swinging its head to display, reared to stand, and down on its belly hunkered.
///
/// It holds three primitive appearances per kind, shared by every animal of the kind, and derives
/// every fact from the awake herds on each sync.
/// </summary>
internal sealed class WildlifePresentation : IDisposable
{
    /// <summary>How many animals may be awake at once: the product id range is four facts for each.</summary>
    internal const int MostMembers = (int)(ProductIds.WildlifeAppearanceLimit / PartsPerMember);

    private const ulong PartsPerMember = 4;

    private enum Part
    {
        Legs,
        Torso,
        Head,
    }

    /// <summary>Head and legs are darker than the coat, so the parts read apart.</summary>
    private const float HeadShade = 0.75f, LegShade = 0.55f;

    // Proportions of the body's height (H), length (L) and width (W) - a placeholder's, not an anatomy.
    private const float LegHeight = 0.45f, LegLength = 0.6f, LegWidth = 0.7f;
    private const float TorsoBottom = 0.4f, TorsoHeight = 0.55f, TorsoLength = 0.8f, TorsoBack = 0.05f;
    private const float HeadHeight = 0.32f, HeadLength = 0.22f, HeadWidth = 0.5f, HeadForward = 0.48f;
    private const float HeadLevel = 0.78f, HeadRaised = 0.92f, HeadGrazing = 0.3f;

    /// <summary>Lying at rest or hunkered: the torso's underside this near the ground.</summary>
    private const float LyingBottom = 0.04f, HunkerHeight = 0.6f, HunkerHead = 0.2f;

    /// <summary>A standing animal rears this far, nose up, about its hind end.</summary>
    private const float RearRadians = 0.6f;

    /// <summary>A displaying animal swings its head this far each way, this many times a second.</summary>
    private const float DisplaySwingRadians = 0.45f, DisplaySwingsPerSecond = 1.5f;

    /// <summary>An animal moving slower than this is grazing in place, head down.</summary>
    private const float GrazingSpeed = 0.6f;

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly Dictionary<string, Appearance[]> appearances = [];
    private AppearanceFact[] facts = [];

    internal WildlifePresentation(IEngineContext engine, WorldFrame frame)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    internal IReadOnlyList<AppearanceFact> Facts => facts;

    /// <summary>Every awake animal's parts for this update's snapshot.</summary>
    internal void Sync(IReadOnlyList<WildlifeHerd> herds, Func<WildlifeMember, float> feet)
    {
        List<AppearanceFact> next = [];
        foreach (WildlifeHerd herd in herds)
        {
            Appearance[] parts = Appearances(herd.Kind);
            foreach (WildlifeMember member in herd.Members)
            {
                Pose(herd, member, feet(member), parts, next);
            }
        }

        facts = [.. next];
    }

    /// <summary>The parts of one animal, posed by what it is doing.</summary>
    private void Pose(WildlifeHerd herd, WildlifeMember member, float feet, Appearance[] parts, List<AppearanceFact> into)
    {
        WildlifeBody body = herd.Kind.Body;
        float h = (float)body.HeightMetres, l = (float)body.LengthMetres, w = (float)body.WidthMetres;
        bool responding = member.Stage == Wariness.Respond;
        bool hunkered = responding && herd.Kind.Pressed == PressedResponse.Hunker;
        bool rearing = responding && herd.Kind.Pressed == PressedResponse.Stand;
        bool displaying = member.Stage == Wariness.Warn && herd.Kind.Pressed == PressedResponse.DisplayCharge;
        bool calm = member.Stage is Wariness.Calm or Wariness.Regroup;
        bool still = member.Velocity.Length() < GrazingSpeed;
        bool lying = hunkered || (calm && herd.Activity == WildlifeActivity.Rest && member.Velocity == Vector2.Zero);
        bool grazing = calm && still && herd.Activity is WildlifeActivity.Graze or WildlifeActivity.Drink;

        float torsoBottom = lying ? LyingBottom * h : TorsoBottom * h;
        float torsoHeight = hunkered ? HunkerHeight * h : TorsoHeight * h;
        float headLevel = hunkered ? HunkerHead * h
            : lying ? torsoBottom + (torsoHeight * 0.8f)
            : grazing ? HeadGrazing * h
            : member.Stage is Wariness.Look or Wariness.Warn ? HeadRaised * h
            : HeadLevel * h;
        Quaternion swing = displaying
            ? Quaternion.CreateFromAxisAngle(Vector3.UnitY, DisplaySwingRadians * MathF.Sin((float)(member.StageSeconds * DisplaySwingsPerSecond * 2 * Math.PI)))
            : Quaternion.Identity;

        Quaternion yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -member.Yaw);
        Quaternion pitch = rearing ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, RearRadians) : Quaternion.Identity;
        // Rearing turns the body about its hind end on the ground; forward is -Z.
        Vector3 pivot = new(0, 0, l / 2);
        Vector3 origin = frame.ToLocal(member.Position.X, feet, member.Position.Y);
        ulong id = ProductIds.WildlifeAppearanceBase + ((ulong)member.Id * PartsPerMember);

        void Add(Part part, Vector3 centre, Vector3 size, Quaternion turn)
        {
            Vector3 placed = Vector3.Transform(Vector3.Transform(centre - pivot, pitch) + pivot, yaw);
            into.Add(new AppearanceFact(id + (ulong)part, false, 0,
                new Transform(origin + placed, yaw * pitch * turn, size),
                parts[(int)part], Visible: true, RenderLayer.Scene, ShadowCasting.Cast));
        }

        if (!lying)
        {
            Add(Part.Legs, new Vector3(0, LegHeight * h / 2, 0), new Vector3(LegWidth * w, LegHeight * h, LegLength * l), Quaternion.Identity);
        }

        Add(Part.Torso, new Vector3(0, torsoBottom + (torsoHeight / 2), TorsoBack * l), new Vector3(w, torsoHeight, TorsoLength * l), Quaternion.Identity);
        Add(Part.Head, new Vector3(0, headLevel, -HeadForward * l), new Vector3(HeadWidth * w, HeadHeight * h, HeadLength * l), swing);
    }

    /// <summary>A kind's three shared appearances, made the first time it is drawn.</summary>
    private Appearance[] Appearances(WildlifeKind kind)
    {
        if (appearances.TryGetValue(kind.Id, out Appearance[]? parts)) return parts;
        Color coat = Color.FromSrgbHex((int)kind.Body.SrgbColour);
        parts = new Appearance[3];
        parts[(int)Part.Legs] = Primitive(Shade(coat, LegShade));
        parts[(int)Part.Torso] = Primitive(coat);
        parts[(int)Part.Head] = Primitive(Shade(coat, HeadShade));
        appearances[kind.Id] = parts;
        return parts;
    }

    private Appearance Primitive(Color color) =>
        engine.Graphics.CreatePrimitive(new PrimitiveAppearanceRequest(PrimitiveGeometry.Cube, false, color));

    /// <summary>A darker colour: primitive colours are linear, so a share of each channel darkens evenly.</summary>
    private static Color Shade(Color color, float share) => new(color.R * share, color.G * share, color.B * share, color.A);

    /// <summary>Releases the shared appearances. The product publishes a snapshot without them first.</summary>
    public void Dispose()
    {
        facts = [];
        foreach (Appearance part in appearances.Values.SelectMany(parts => parts)) part.Dispose();
        appearances.Clear();
    }
}
