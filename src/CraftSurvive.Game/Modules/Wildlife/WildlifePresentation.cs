using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// What the Engine shows of wildlife. A kind with a mesh is drawn by <see cref="WildlifeMeshBodies"/>
/// (#10569). Until a kind has one it stays a placeholder (#10567): a torso, a head and a block of
/// legs, proportioned by its kind's body and coloured by it, whose posture shows what the animal is
/// doing: lying at rest, head down grazing, head up looking, swinging its head to display, reared
/// to stand, and down on its belly hunkered.
///
/// It holds three primitive appearances per placeholder kind, shared by every animal of the kind,
/// and derives every fact from the awake herds and the fallen on each sync.
/// </summary>
internal sealed class WildlifePresentation : IDisposable
{
    /// <summary>How many animals may be awake at once: the product id range is four facts for each.</summary>
    internal const int MostMembers = (int)(ProductIds.WildlifeAppearanceLimit / PartsPerMember);

    internal const ulong PartsPerMember = 4;

    /// <summary>The number of the four a mesh body's object takes; a placeholder's parts have the first three.</summary>
    internal const ulong MeshPart = 3;

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
    private readonly WildlifeMeshBodies meshes;
    private readonly Dictionary<string, Appearance[]> appearances = [];
    private AppearanceFact[] facts = [];

    internal WildlifePresentation(IEngineContext engine, WorldFrame frame, WildlifeMeshBodies meshes)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.meshes = meshes ?? throw new ArgumentNullException(nameof(meshes));
    }

    internal WildlifeMeshBodies Meshes => meshes;

    internal IReadOnlyList<AppearanceFact> Facts => facts;

    /// <summary>Every awake animal's parts for this update's snapshot.</summary>
    /// <param name="here">Where the player stands (world X, Z), which sets how much of each animal's animation runs.</param>
    /// <param name="seconds">How long since the last sync; zero when it only reflects a change made outside an update.</param>
    internal void Sync(IReadOnlyList<WildlifeHerd> herds, IReadOnlyList<WildlifeFallen> fallen, Func<WildlifeMember, float> feet, Vector2 here, double seconds)
    {
        List<AppearanceFact> next = [];
        HashSet<WildlifeMember> kept = [];
        double plantWithin = WildlifeClips.PlantWithin(
            herds.Where(herd => herd.Kind.Mesh is not null).SelectMany(herd => herd.Members).Where(member => !member.Under).Select(member => (double)Vector2.Distance(member.Position, here)),
            meshes.Animation);
        foreach (WildlifeHerd herd in herds)
        {
            foreach (WildlifeMember member in herd.Members)
            {
                if (member.Under) continue;
                if (herd.Kind.Mesh is null)
                {
                    Pose(herd.Kind, herd.Activity, member, feet(member), false, Appearances(herd.Kind), next);
                    continue;
                }

                kept.Add(member);
                next.Add(meshes.Pose(herd, herd.Kind, member, feet(member), Vector2.Distance(member.Position, here), seconds, plantWithin, fallen: false));
            }
        }

        foreach (WildlifeFallen body in fallen)
        {
            if (body.Kind.Mesh is null)
            {
                Pose(body.Kind, WildlifeActivity.Rest, body.Member, feet(body.Member), true, Appearances(body.Kind), next);
                continue;
            }

            kept.Add(body.Member);
            next.Add(meshes.Pose(null, body.Kind, body.Member, feet(body.Member), Vector2.Distance(body.Member.Position, here), seconds, plantWithin, fallen: true));
        }

        meshes.Keep(kept);
        facts = [.. next];
    }

    /// <summary>The parts of one animal, posed by what it is doing.</summary>
    private void Pose(WildlifeKind kind, WildlifeActivity activity, WildlifeMember member, float feet, bool fallen, Appearance[] parts, List<AppearanceFact> into)
    {
        WildlifeBody body = kind.Body;
        float h = (float)body.HeightMetres * member.Size, l = (float)body.LengthMetres * member.Size, w = (float)body.WidthMetres * member.Size;
        bool responding = !fallen && member.Stage == Wariness.Respond;
        bool hunkered = responding && kind.Pressed == PressedResponse.Hunker;
        bool rearing = responding && kind.Pressed == PressedResponse.Stand;
        bool displaying = !fallen && member.Stage == Wariness.Warn && kind.Pressed == PressedResponse.DisplayCharge;
        bool calm = fallen || member.Stage is Wariness.Calm or Wariness.Regroup;
        bool still = member.Velocity.Length() < GrazingSpeed;
        bool lying = fallen || hunkered || (calm && activity == WildlifeActivity.Rest && member.Velocity == Vector2.Zero);
        bool grazing = calm && still && activity is WildlifeActivity.Graze or WildlifeActivity.Drink;

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
        meshes.Dispose();
        foreach (Appearance part in appearances.Values.SelectMany(parts => parts)) part.Dispose();
        appearances.Clear();
    }
}
