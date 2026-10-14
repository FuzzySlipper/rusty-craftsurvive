using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using CraftSurvive.Game.Modules.Towns;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;
using Rusty.Engine;
using Rusty.Engine.Interaction;
using Rusty.Engine.Testing;

const double Step = 1d / 60;
const float Tolerance = .001f;
const double PadHeight = 10;

string directory = Path.Combine(AppContext.BaseDirectory, "Content");
Dictionary<string, ReadOnlyMemory<byte>> files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
    .ToDictionary(file => Path.GetRelativePath(directory, file).Replace('\\', '/'), file => (ReadOnlyMemory<byte>)File.ReadAllBytes(file));
ProductContent content = new(files.Select(file => new ProductContentFile(Encoding.UTF8.GetBytes(file.Key), file.Value)).ToArray());
TownContent towns = TownContent.Read(content);

// Synthetic seated settlements: a port facing +X and a river town facing an arbitrary bearing.
static Settlement Seated(string name, SettlementKind kind, double x, double z, double bearing) =>
    new(name, kind, x, z, 0, SettlementReason.Mouth, 1) { Ground = new(x, z, Math.Cos(bearing), Math.Sin(bearing), PadHeight) };
static double Flat(double x, double z) => PadHeight;
TownSite[] sites = [.. new[] { Seated("Brackwater", SettlementKind.Port, 120, -40, 0), Seated("Two Waters", SettlementKind.Town, 30_000, 8_000, 2.2) }
    .Select(settlement => TownSite.For(settlement, towns, Flat)!)];

Check.Section("town content is complete and every reference resolves", () =>
{
    Check.That(towns.Layouts.Length == 2 && towns.LayoutFor(SettlementKind.Port)?.Key == "port" && towns.LayoutFor(SettlementKind.Town)?.Key == "landing",
        "a port and a river town each have their layout");
    Check.That(towns.LayoutFor(SettlementKind.Hold) is null, "the mountain hold has no town layout yet");
    foreach (KitPiece piece in towns.Kit.Values)
    {
        foreach (string mesh in new[] { piece.Mesh }.Concat(piece.Sign is KitSign sign ? [sign.Mesh] : []))
        {
            Check.That(files.TryGetValue(mesh, out ReadOnlyMemory<byte> glb), $"{piece.Key}: {mesh} is staged content");
            foreach (string uri in ImageUris(glb.Span))
                Check.That(files.ContainsKey(Path.GetDirectoryName(mesh)!.Replace('\\', '/') + "/" + uri), $"{piece.Key}: its texture {uri} is shared content");
        }

        Check.That(piece.Max.X > piece.Min.X && piece.Max.Y > piece.Min.Y && piece.Max.Z > piece.Min.Z, $"{piece.Key} has real bounds");
    }

    foreach ((string palette, IReadOnlyDictionary<string, Color> tints) in towns.Palettes)
        foreach (string material in tints.Keys)
            Check.That(towns.Kit.Values.Any(piece => piece.Materials.Contains(material)), $"palette {palette} tints a material the kit uses ({material})");
    Check.That(towns.Kit.Values.Where(piece => piece.Kind == "building").All(piece => piece.Entrance is not null && piece.Sign is not null),
        "every building has an entrance and a hanging sign");
    Check.Equal(towns.Kit.Count + towns.Kit.Values.Count(piece => piece.Sign is not null), files.Keys.Count(file => file.EndsWith(".glb", StringComparison.Ordinal)),
        "the kit stages exactly its own pieces; nothing from the old composed stage remains");
});

Check.Section("layouts stand on their pad, land side, with the arrival view outside the bubble", () =>
{
    foreach (TownLayout layout in towns.Layouts)
    {
        foreach (LayoutPiece placed in layout.Pieces)
        {
            KitPiece piece = towns.Kit[placed.Piece];
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, placed.Yaw * MathF.PI / 180);
            Vector3[] corners = [.. Enumerable.Range(0, 8).Select(i => placed.At + Vector3.Transform(new Vector3(
                (i & 1) == 0 ? piece.Min.X : piece.Max.X, 0, (i & 4) == 0 ? piece.Min.Z : piece.Max.Z), turn))];
            Check.That(corners.All(c => new Vector2(c.X, c.Z).Length() <= TownGround.FlatMetres), $"{layout.Key}: {placed.Piece} stands on the flat pad");
            if (piece.Kind == "building")
            {
                Check.That(corners.All(c => c.X < TownGround.Waterfront), $"{layout.Key}: {placed.Piece} stands on the land side of the waterfront");
                Check.That(corners.All(c => new Vector2(c.X, c.Z).Length() < TownSite.BubbleMetres), $"{layout.Key}: {placed.Piece} is inside the bubble, never walked to");
            }
        }

        Check.That(layout.Paving.All(p => p.Length() <= TownGround.FlatMetres && p.X <= TownGround.Waterfront), $"{layout.Key}: the courtyard lies on the flat pad");
        Check.That(layout.Arrival.Length() > TownSite.BubbleMetres && layout.Arrival.X < TownGround.Waterfront - TownGround.ShoreFade,
            $"{layout.Key}: the traveller arrives outside the bubble, on land");
        Check.That(Vector2.Distance(layout.Arrival, new(layout.View.Eye.X, layout.View.Eye.Z)) < TownSite.DoorstepMetres * 2,
            $"{layout.Key}: the composed view stands beside where the traveller arrives");
        Check.Equal(2, layout.Pieces.Count(p => p.Service is not null), $"{layout.Key}: an inn and a provisioner can be entered");
    }
});

Check.Section("a town's identity, frame and bubble", () =>
{
    foreach (TownSite site in sites)
    {
        TownSite again = TownSite.For(site.Settlement, towns, Flat)!;
        Check.Equal(site.Recipe, again.Recipe, $"{site.Name}: the same settlement draws the same town");
        Check.That(site.ServiceNames.SequenceEqual(again.ServiceNames) && site.Keepers.SequenceEqual(again.Keepers), $"{site.Name}: names and keepers are stable");
        Check.That(towns.Services["inn"].Names[site.Settlement.Kind].Contains(site.ServiceNames["inn"]), $"{site.Name}: the inn's name suits a {site.Settlement.Kind}");
        Check.That(site.Holds(site.Frame.X, site.Frame.Z), $"{site.Name}: its seat is inside the bubble");
        (double ax, double az) = site.Arrival;
        Check.That(!site.Holds(ax, az), $"{site.Name}: the arrival point is outside the bubble");
        Check.That(site.Welcomes(ax, az), $"{site.Name}: a journey, a new game or leaving stands the traveller where entry is offered");
        (double ox, _, double oz) = site.Frame.World(new(-(float)(TownSite.BubbleMetres + TownSite.DoorstepMetres + 20), 0, 0));
        Check.That(!site.Welcomes(ox, oz), $"{site.Name}: well beyond the doorstep, nothing is offered");
        (double ex, _, double ez) = site.Frame.World(new(-(float)(TownSite.BubbleMetres + (TownSite.DoorstepMetres / 2)), 0, 0));
        Check.That(site.Welcomes(ex, ez), $"{site.Name}: walking up to the edge from inland offers entry");
        Check.That(!site.Welcomes(site.Frame.X, site.Frame.Z), $"{site.Name}: no offer is made from inside the bubble");
        (double wx, _, double wz) = site.Frame.World(new((float)TownGround.Waterfront + 6, 0, 0));
        Check.That(!site.Holds(wx, wz), $"{site.Name}: the water beside the quay is open to a swimmer or a boat");
        (double fx, _, double fz) = site.Frame.World(Vector3.UnitX);
        Check.That(Math.Abs(fx - site.Frame.X - site.Frame.FacingX) < 1e-6 && Math.Abs(fz - site.Frame.Z - site.Frame.FacingZ) < 1e-6,
            $"{site.Name}: the frame's +X faces the water");
        Vector3 front = Vector3.Transform(Vector3.UnitZ, site.Frame.Turn(0));
        Vector3 across = site.Frame.Direction(Vector3.UnitZ);
        Check.That(Vector3.Distance(front, across) < Tolerance, $"{site.Name}: a piece at yaw 0 faces the arrival view");
    }

    TownSite port = sites[0];
    TownSite mirrored = port with { Frame = port.Frame with { Mirrored = true } };
    (double px, _, double pz) = port.Frame.World(new(3, 0, 5));
    (double mx, _, double mz) = mirrored.Frame.World(new(3, 0, -5));
    Check.That(Math.Abs(px - mx) < 1e-9 && Math.Abs(pz - mz) < 1e-9, "a mirrored town places its layout across the water's line, not its meshes");
    Vector3 mirroredFront = Vector3.Transform(Vector3.UnitZ, mirrored.Frame.Turn(0));
    Check.That(Vector3.Distance(mirroredFront, mirrored.Frame.Direction(Vector3.UnitZ)) < Tolerance, "a mirrored town's pieces still face its arrival view");

    TownGround ground = port.Settlement.Ground;
    Check.Equal(1d, ground.Weight(ground.X, ground.Z), "the pad is flat at its seat");
    Check.Equal(0d, ground.Weight(ground.X + TownGround.ReachMetres + 1, ground.Z), "the pad leaves the land beyond its reach alone");
    Check.Equal(0d, ground.Weight(ground.X + TownGround.Waterfront + TownGround.ShoreFade + .5, ground.Z), "the pad leaves the water beside the quay alone");
    Check.Equal(PadHeight, ground.Level(55, ground.X - 3, ground.Z + 4), "ground on the pad is held to its level");
});

Check.Section("entry follows the expedition: walking, where the traveller stands; on the map, where the party stands still", () =>
{
    TownSite port = sites[0];
    (double ax, double az) = port.Arrival;
    (double fx, _, double fz) = port.Frame.World(new(-300, 0, 300));
    Check.Equal(port.Key, TownEntry.Offered(sites, false, (ax, az), null, false)?.Key, "walking up to the arrival point offers the town");
    Check.Equal(port.Key, TownEntry.Offered(sites, true, (ax, az), (ax, az), false)?.Key, "opening the map at the town offers it");
    Check.Equal(port.Key, TownEntry.Offered(sites, true, (fx, fz), (ax, az), false)?.Key, "a journey that stopped at the town offers it");
    Check.That(TownEntry.Offered(sites, true, (ax, az), (ax, az), true) is null, "setting out withdraws the offer at once");
    Check.That(TownEntry.Offered(sites, true, (ax, az), (fx, fz), true) is null, "a party travelling on is offered nothing");
    Check.That(TownEntry.Offered(sites, true, (ax, az), (fx, fz), false) is null,
        "a party stopped beyond the town is offered nothing, though the walking player's last position is still at it");
    Check.Equal(port.Key, TownEntry.Offered(sites, false, (ax, az), (fx, fz), false)?.Key, "walking again, the traveller's own position decides");
});

Check.Section("services come from data and answer to the visit's conditions", () =>
{
    TownSite port = sites[0], landing = sites[1];
    TownServices portInn = new(towns, port);
    TownConditions wetNight = new(true, true, true), dryDay = new(false, false, false), portDay = new(false, false, true);
    portInn.Record("inn", portInn.Offered("inn", "news", wetNight)!, wetNight, null);
    Check.That(portInn.Describe("inn", wetNight, "", _ => null).Text.Contains("quay", StringComparison.Ordinal), "a wet night at a port hears the harbour's news, not the ferry's");
    TownServices landingInn = new(towns, landing);
    landingInn.Record("inn", landingInn.Offered("inn", "news", dryDay)!, dryDay, null);
    Check.That(landingInn.Describe("inn", dryDay, "", _ => null).Text.Contains("ferry", StringComparison.Ordinal), "a river town talks of its ferry");
    Check.That(portInn.Offered("inn", "room", wetNight) is not null && portInn.Offered("inn", "room", portDay) is null, "a room for the night is offered at night");
    Check.That(portInn.Offered("inn", "fireside", portDay) is not null && portInn.Offered("inn", "fireside", wetNight) is null, "by day the inn offers a rest by the fire");
    ServiceChoice meal = portInn.Offered("inn", "meal", portDay)!;
    Check.Equal("", portInn.Unavailable("inn", meal, _ => null), "a meal is available");
    portInn.Record("inn", meal, portDay, new(true, "Food 20% → 60%"));
    Check.Equal(meal.Spent, portInn.Unavailable("inn", meal, _ => null), "a meal taken is spent for the visit");
    TownServiceUiFacts facts = portInn.Describe("inn", portDay, "", _ => null);
    Check.That(facts.Outcome == "Food 20% → 60%" && facts.Speaker == port.Keepers["inn"].Name, "the outcome and the keeper are published");
    Check.That(facts.Choices.Split(';').All(row => row.Split('|').Length == 6), "every choice row carries its six fields");
    Check.That(!facts.Text.Contains('{') && !facts.Choices.Contains('{'), "every placeholder is filled");
    TownServices provisioner = new(towns, port);
    ServiceChoice rations = provisioner.Offered("provisioner", "rations", portDay)!;
    Check.Equal(rations.Full, provisioner.Unavailable("provisioner", rations, choice => choice.Full), "the product's own reason (a full pack) is shown");
    portInn.Reset();
    Check.Equal("", portInn.Unavailable("inn", meal, _ => null), "a new visit offers the meal again");
});

using EngineTestHost host = EngineTestHost.Create(new EngineTestHostOptions { Content = files });
host.Call(engine =>
{
    WorldFrame frame = new();
    TownSite port = sites[0];

    Check.Section("a town stands in the world near the traveller and retires cleanly", () =>
    {
        PresentationReadout baseline = engine.Graphics.ReadPresentation();
        uint emitters = engine.Presentation.Read().ActiveEmitters;
        TownPresence presence = new(engine, content, towns, frame, Flat);
        presence.Follow(sites, port.Frame.X + 5_000, port.Frame.Z);
        Check.That(presence.Site is null, "a town far away is not drawn");
        for (int pass = 0; pass < 2; pass++)
        {
            presence.Follow(sites, port.Frame.X + 60, port.Frame.Z);
            Check.Equal(port.Key, presence.Site?.Key, "the nearest town within reach stands");
            int lamps = port.Layout.Pieces.Sum(piece => towns.Kit[piece.Piece].Lamps.Length) + towns.Kit[port.Landmark].Lamps.Length;
            int chimneys = port.Layout.Pieces.Sum(piece => towns.Kit[piece.Piece].Chimneys.Length);
            Check.Equal(lamps, presence.Lights, "every lamp is lit");
            Check.Equal(emitters + (uint)chimneys, engine.Presentation.Read().ActiveEmitters, "every chimney smokes, once");
            Check.Equal(2, presence.Services.Count, "the inn and the provisioner can be selected");
            AppearanceFact[] facts = presence.Facts;
            Check.That(facts.Select(fact => fact.ObjectId).Distinct().Count() == facts.Length, "each drawn piece has its own id");
            Check.That(facts.Length >= port.Layout.Pieces.Length + 1 + 2, "pieces, landmark, signs and the paving are drawn");
            engine.Graphics.PublishSnapshot(facts);
            presence.Advance(.5);
            Check.That(!presence.Facts.SequenceEqual(facts), "the hanging signs swing");
            presence.Follow(sites, port.Frame.X + 5_000, port.Frame.Z);
            engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
            presence.AfterAppearanceSnapshot();
            Check.That(presence.Site is null && presence.Lights == 0, "leaving the town's reach withdraws it");
            Check.Equal(emitters, engine.Presentation.Read().ActiveEmitters, "its smoke stops");
        }

        presence.Dispose();
        Check.Equal(baseline, engine.Graphics.ReadPresentation(), "standing a town twice and disposing leaves nothing behind");
    });

    Check.Section("a visit selects, converses, applies and leaves", () =>
    {
        TownUiFacts latest = default;
        TownPresence presence = new(engine, content, towns, frame, Flat);
        TownModule town = new(engine, towns, facts => latest = facts);
        presence.Follow(sites, port.Frame.X + 60, port.Frame.Z);
        Check.That(!town.Enter(sites[1], presence, new(false, false, false), frame, 1000), "a town that does not stand in the world cannot be entered");
        uint voices = engine.Audio.Read().ActiveVoices;
        InteractionTarget? earlier = null;
        for (int visit = 0; visit < 2; visit++)
        {
            Check.That(town.Enter(port, presence, new(false, false, true), frame, 1000), "the standing town opens");
            engine.Graphics.PublishSnapshot(presence.Facts);
            for (int i = 0; i < 120; i++) town.Present(Step);
            Check.That(town.OutdoorGain < Tolerance, "arrival completes the outdoor crossfade");
            Check.Equal(voices + 4, engine.Audio.Read().ActiveVoices, "one soundscape owns its four voices");
            Check.That(latest.Open && latest.Title == port.Name && latest.Anchors.Contains("inn|") && latest.Anchors.Contains("provisioner|"),
                "the town and both entrances are published");
            if (earlier is InteractionTarget old)
                Check.That(!town.Interactions.UseTarget(old).Performed, "a target from an earlier visit cannot select in a new one");
            earlier = town.ReadInteraction().Candidates.Span[0].Target;
            ulong id = ulong.Parse(latest.Visit, CultureInfo.InvariantCulture);
            foreach (string anchor in latest.Anchors.Split(';'))
            {
                string[] fields = anchor.Split('|');
                Check.Equal("1", fields[5], $"{fields[0]}'s entrance is in view");
                Vector2 label = new(float.Parse(fields[3], CultureInfo.InvariantCulture), float.Parse(fields[4], CultureInfo.InvariantCulture));
                town.Click(label - new Vector2(0, .03f));
                Check.Equal(fields[0], town.Selected?.Service, $"clicking just under {fields[0]}'s label selects it");
                for (int i = 0; i < 240; i++) town.Advance(Step);
                Check.Equal(Vector2.Zero, town.CameraOffset, "reading settles the camera");
                town.Back();
            }

            town.Select("inn");
            Check.That(latest.Service is TownServiceUiFacts inn && inn.Speaker == port.Keepers["inn"].Name, "the inn's keeper speaks");
            Check.That(town.Choose("inn", "news", id) is null && latest.Service!.Value.Text.Length > 0, "talk only replies");
            Check.That(town.Choose("provisioner", "rations", id) is null, "a different counter cannot accept a hidden action");
            Check.That(town.Choose("inn", "meal", id + 1) is null, "a delayed choice from another visit is refused");
            TownServiceRequest? meal = town.Choose("inn", "meal", id);
            Check.That(meal is { Choice.Action: "meal" }, "a meal is handed to the product to apply");
            town.Record(meal!.Value, new(true, "Food 20% → 60%"), new(false, false, true));
            Check.Equal("Food 20% → 60%", latest.Service!.Value.Outcome, "the product's outcome is shown");
            Check.That(town.Choose("inn", "meal", id) is null, "a meal taken is not offered twice");
            town.Back();
            Check.That(town.Open && town.Selected is null, "Back never departs");
            town.Leave();
            Check.That(!latest.Open, "leaving publishes a closed view");
            for (int i = 0; i < 180; i++) town.Present(Step);
            Check.Equal(voices, engine.Audio.Read().ActiveVoices, "departure releases the soundscape after fading");
            Check.Equal(1f, town.OutdoorGain, "the outdoors regains its sound");
        }

        town.Offer(port.Name);
        Check.Equal(port.Name, latest.Prompt, "the doorstep offers entry by name");
        presence.Follow(sites, port.Frame.X + 5_000, port.Frame.Z);
        engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
        presence.AfterAppearanceSnapshot();
        town.Dispose();
        presence.Dispose();
    });
});

return Check.Finish("towns");

static string[] ImageUris(ReadOnlySpan<byte> glb)
{
    int length = BitConverter.ToInt32(glb[12..16]);
    using JsonDocument document = JsonDocument.Parse(glb.Slice(20, length).ToArray());
    return document.RootElement.TryGetProperty("images", out JsonElement images)
        ? [.. images.EnumerateArray().Select(image => image.GetProperty("uri").GetString()!)]
        : [];
}
