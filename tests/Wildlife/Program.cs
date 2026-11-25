using System.Numerics;
using System.Text.Json;
using CraftSurvive.Game.Modules.Birds;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.Wildlife;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

// Wildlife's rules (#10567), checked without a host: the kinds the product ships, a herd keeping
// together, alarm spreading, every pressed response, deterministic region seeding, and a hunted
// population recovering slowly.

const ulong Seed = 0x5EED_10567UL;
const double Tick = 0.1;
WildlifeCatalog catalog = WildlifeKinds.Parse(ReadKinds());
int nextId = 0;
int NextId() => ++nextId;

// A herd woken at a point, awake from the given time of day.
WildlifeHerd Herd(string kindId, Vector2 at, int count, string key = "test")
{
    WildlifeKind kind = catalog.Kind(kindId);
    return WildlifeRules.Wake(new HerdRecord(key, kind.Id, at, count), kind, catalog, Seed, at, count, NextId);
}

// Runs a herd for some seconds, the player standing where the function says at each moment.
void Run(WildlifeHerd herd, double seconds, WorldTime start, Func<double, Vector2?> player, Action<double>? each = null, Vector2? water = null)
{
    for (double t = 0; t < seconds; t += Tick)
    {
        WorldTime time = start with { DayFraction = start.DayFraction + (t / WorldClock.DaySeconds) };
        WildlifeRules.Decide(herd, new HerdFacts(player(t), time, Seed, water), catalog, Tick);
        WildlifeRules.Integrate(herd, Tick);
        each?.Invoke(t);
    }
}

WorldTime Morning = new(3, 6.5 / 24);
WorldTime Midnight = new(3, 0.5 / 24);

double SpreadRadius(WildlifeHerd herd) => herd.Members.Max(m => Vector2.Distance(m.Position, herd.Leader.Position));

// Between grown animals: the young run about among them.
double ClosestPair(WildlifeHerd herd)
{
    double closest = double.PositiveInfinity;
    WildlifeMember[] grown = [.. herd.Members.Where(member => !member.Young)];
    for (int i = 0; i < grown.Length; i++)
        for (int j = i + 1; j < grown.Length; j++)
            closest = Math.Min(closest, Vector2.Distance(grown[i].Position, grown[j].Position));
    return closest;
}

// How far a herd of n may spread around its leader and still be one herd.
double CohesionBound(WildlifeKind kind, int count) =>
    kind.SpacingMetres * ((0.75 * Math.Sqrt(count - 1)) + catalog.Behaviour.CatchUpSpacings + catalog.Behaviour.SlotJitterShare);

Check.Section("birds", () =>
{
    // Patch, 2026-11-05: birds are drawn, not simulated. They come from beyond sight and cross the sky, never speeding towards the player.
    BirdCatalog birds = BirdCatalog.Parse(ReadResource("Wildlife.birds.json"));
    BirdKind soarer = birds.Kinds[0];
    Check.That(birds.Kinds.All(kind => kind.Mesh.Glide != kind.Mesh.Flap), "a bird's glide and flap are different clips");
    Check.That(soarer.FliesAt(MapBiome.Desert, 12, Season.Summer), "a soaring bird flies over the desert at noon");
    Check.That(!soarer.FliesAt(MapBiome.Desert, 3, Season.Summer), "it does not fly in the night");
    Check.That(!soarer.FliesAt(MapBiome.Grassland, 12, Season.Summer), "it keeps to its own country");
    Check.That(FlightRules.Choose([], MapBiome.Desert, 0.5) is null, "with nothing flying here no flight is sent");
    Check.That(FlightRules.Choose(birds.Kinds, MapBiome.Desert, 0.5) == soarer, "a kind is chosen for the ground");

    Random random = new(10630);
    Vector2 player = new(5000, -3000);
    for (int trial = 0; trial < 200; trial++)
    {
        Flight flight = FlightRules.Plan(soarer, birds, player, random.NextDouble, (x, z) => 900 + (x % 40));
        foreach (FlightSlot slot in flight.Slots)
        {
            BirdPose first = FlightRules.At(flight, slot, 0), last = FlightRules.At(flight, slot, flight.Seconds);
            Check.That(FlightRules.Size(first, player, birds) == 0, "a flight starts with nothing to see of it");
            Check.That(FlightRules.Size(last, player, birds) == 0, "and a player who stayed put sees nothing of it at the end of its line");
            Check.That(first.Position.Y >= 900 + soarer.Flight.HeightMetres.Least, "a flight is high above the ground it crosses");
        }

        Check.That(FlightRules.Passes(flight, player) >= birds.MissMetres.Least - 1, "a flight's line misses the player");
        Check.That(FlightRules.Passes(flight, player) <= birds.MissMetres.Most + 1, "and passes within the sky above them");
        Check.That(flight.Slots.Count >= soarer.Flock.Least && flight.Slots.Count <= soarer.Flock.Most, "a flock is its kind's size");
    }

    // R10630-2: a flight's line is drawn from where the player was, so a player who walks to its far end must not see it go.
    const double Step = 0.5, Walking = 2.6, Horizon = 3000;
    Func<double, double, double> flat = (_, _) => 900;
    Random moving = new(106302);
    for (int trial = 0; trial < 50; trial++)
    {
        Flight flight = FlightRules.Plan(soarer, birds, player, moving.NextDouble, flat);
        Vector2 walker = FlightRules.End(flight) - (flight.Heading * (float)(Walking * flight.Seconds));
        bool left = false;
        for (double age = 0; age < Horizon && !left; age += Step)
        {
            FlightRules.Advance(flight, birds, Step, flat);
            walker += flight.Heading * (float)(Walking * Step);
            left = FlightRules.Gone(flight, walker, birds.ReachMetres);
            float seen = flight.Slots.Max(slot => FlightRules.Size(FlightRules.At(flight, slot, flight.Age), walker, birds));
            Check.That(!left || seen == 0, "a flight goes only when nothing of it can be seen by a player walking after it");
        }

        Check.That(left, "and a walker does not hold a flight in the sky for ever");
        Check.That(flight.Age > flight.Seconds, "it carries on past the end of its line to do so");
    }

    // At the reach a bird is still a speck of a few pixels, so it grows from nothing rather than appearing.
    BirdPose at(double metres) => new(new Vector3(player.X + (float)metres, 1000, player.Y), 0, 0);
    Check.That(FlightRules.Size(at(birds.ReachMetres), player, birds) == 0, "a bird at the reach is not drawn");
    Check.That(FlightRules.Size(at(birds.ReachMetres - (birds.FadeMetres / 2)), player, birds) is > 0 and < 1, "it grows as it comes in");
    Check.That(FlightRules.Size(at(birds.ReachMetres - birds.FadeMetres), player, birds) == 1, "and is its whole size inside the fade");
    Check.That(FlightRules.Size(at(birds.MissMetres.Most), player, birds) == 1, "as it is where a flight passes nearest");

    Flight stayed = FlightRules.Plan(soarer, birds, player, moving.NextDouble, flat);
    stayed.Age = stayed.Seconds;
    Check.That(!FlightRules.Gone(stayed, FlightRules.At(stayed, stayed.Slots[0], stayed.Age).Ground, birds.ReachMetres), "a player under a flight at the end of its line keeps it in the sky");
    stayed.Age = 0;
    Check.That(!FlightRules.Gone(stayed, stayed.Start + (stayed.Heading * (float)(1.5 * birds.ReachMetres)), birds.ReachMetres), "a flight on its way does not go early for a player who is not far from it");
    Check.That(FlightRules.Gone(stayed, player + new Vector2(0, (float)(5 * birds.ReachMetres)), birds.ReachMetres), "a player who has gone far away leaves it behind");

    // A sprinter can stay under a flight past the ground its plan looked at, up a slope, and it climbs to stay clear.
    const double Slope = 0.3;
    Flight chased = FlightRules.Plan(soarer, birds, player, moving.NextDouble, flat);
    Func<double, double, double> rising = (x, z) => 900 + Math.Max(0, Slope * (Vector2.Dot(new Vector2((float)x, (float)z) - chased.Start, chased.Heading) - (soarer.Flight.DriftMetresPerSecond * chased.Seconds)));
    double lowest = double.PositiveInfinity;
    for (double age = 0; age < Horizon; age += Step)
    {
        FlightRules.Advance(chased, birds, Step, rising);
        BirdPose bird = FlightRules.At(chased, chased.Slots[0], chased.Age);
        lowest = Math.Min(lowest, bird.Position.Y - rising(bird.Position.X, bird.Position.Z));
        Check.That(!FlightRules.Gone(chased, bird.Ground, birds.ReachMetres), "a player keeping up with a flight keeps it in the sky");
    }

    Check.That(chased.Climbed > 0, "past its plan a flight climbs with the ground");
    Check.That(lowest > 0, "and is never in it");

    // Patch, 2026-11-05: greener country has flocks. A skein flies straight, in a V with one arm longer than the other.
    BirdKind skein = birds.Kinds.Single(kind => kind.Id == "skein");
    Check.That(skein.FliesAt(MapBiome.Grassland, 12, Season.Spring), "a skein flies over the grass");
    Check.That(!skein.FliesAt(MapBiome.Desert, 12, Season.Spring), "and leaves the desert to the streamer");
    Check.That(FlightRules.Choose(birds.Kinds, MapBiome.Grassland, 0.5) == skein, "away from the sea, the kind that likes the ground is chosen");
    Random flocks = new(10631);
    for (int trial = 0; trial < 100; trial++)
    {
        Flight v = FlightRules.Plan(skein, birds, player, flocks.NextDouble, (_, _) => 900);
        Check.That(v.Slots.Count >= skein.Flock.Least && v.Slots.Count <= skein.Flock.Most, "a skein is its kind's size");
        Check.That(v.Slots.Count <= birds.MostBirds, "and fits in the sky");
        double age = v.Seconds / 2;
        BirdPose[] poses = [.. v.Slots.Select(slot => FlightRules.At(v, slot, age))];
        Vector2 side = new(-v.Heading.Y, v.Heading.X);
        float[] along = [.. poses.Select(pose => Vector2.Dot(pose.Ground, v.Heading))];
        float[] across = [.. poses.Select(pose => Vector2.Dot(pose.Ground - poses[0].Ground, side))];
        Check.That(along[0] >= along.Max() - (2 * skein.Flock.Formation!.StrayMetres), "its first bird leads");
        int left = across.Count(metres => metres < -skein.Flock.Formation.SpacingMetres / 2), right = across.Count(metres => metres > skein.Flock.Formation.SpacingMetres / 2);
        Check.That(left > 0 && right > 0, "it has two arms");
        Check.That(left != right, "and one is longer than the other");
        double closest = poses.SelectMany((a, i) => poses.Skip(i + 1).Select(b => Vector2.Distance(a.Ground, b.Ground))).Min();
        Check.That(closest > skein.Flock.Formation.SpacingMetres / 3, "no two birds fly in each other");
        Vector2 facing2 = WildlifeRules.Facing(poses[1].Yaw);
        foreach (FlightSlot slot in v.Slots)
        {
            Check.That(FlightRules.Size(FlightRules.At(v, slot, 0), player, birds) == 0, "a skein starts with nothing to see of it");
            Check.That(FlightRules.Size(FlightRules.At(v, slot, v.Seconds), player, birds) == 0, "and all of it is out of sight at the end of its line");
        }

        Check.That(Vector2.Dot(facing2, v.Heading) > 0.99f, "it faces along its line");
        Check.That(FlightRules.Passes(v, player) >= birds.MissMetres.Least - 1, "and its line misses the player");
    }

    Check.Throws<InvalidDataException>(() => BirdCatalog.Parse(ReadResource("Wildlife.birds.json").Replace("\"mostBirds\": 30", "\"mostBirds\": 5")), "a flock too big for the sky is refused");

    // Patch, 2026-11-05: a sea bird says the sea is somewhere near, and its way points to it.
    BirdKind shag = birds.Kinds.Single(kind => kind.Id == "line-shag");
    BirdSea tie = shag.Sea!;
    Func<Vector2, bool> seaEast = point => point.X > 20000;
    SeaReading? east = FlightRules.FindSea(birds, new Vector2(0, 0), seaEast);
    Check.That(east is not null, "a sea within the last ring is found");
    Check.That(east!.Value.Metres >= 20000 && east.Value.Metres <= birds.SeaRingsMetres[^1], "at the ring that reaches it");
    Check.That(Vector2.Dot(east.Value.Way, Vector2.UnitX) > 0.95f, "and the way to it is true");
    Check.That(FlightRules.FindSea(birds, new Vector2(0, 0), _ => false) is null, "no sea within reach is no sea");
    Check.That(FlightRules.SeaChance(shag, null) == 0, "a sea bird does not fly where no sea is found");
    Check.That(FlightRules.SeaChance(shag, new SeaReading(tie.WithinMetres, Vector2.UnitX)) == 0, "nor at the edge of its distance");
    Check.That(FlightRules.SeaChance(skein, null) == 1, "and the sea does not matter to other birds");
    Check.That(FlightRules.Choose([shag], MapBiome.Grassland, 0.5, null) is null, "with only sea birds and no sea, no flight is sent");
    Check.That(FlightRules.Choose([shag, skein], MapBiome.Grassland, 0.99, new SeaReading(3000, Vector2.UnitX)) is not null, "by the coast both can be sent");

    // R10642-1: thinning has to mean fewer flights, even where a sea bird is the only kind that flies.
    int Launches(IReadOnlyList<BirdKind> kinds, MapBiome biome, double metres, BirdKind wanted)
    {
        Random tries = new(106421);
        SeaReading reading = new(metres, Vector2.UnitX);
        return Enumerable.Range(0, 10000).Count(_ => FlightRules.Choose(kinds, biome, tries.NextDouble(), reading, tries.NextDouble()) == wanted);
    }

    int coastal = Launches([shag], MapBiome.Tundra, 3000, shag), inland = Launches([shag], MapBiome.Tundra, 25000, shag);
    Check.That(coastal > 8500, "where only shags fly, the coast sends one nearly every time");
    Check.That(inland > 3000 && inland < 4500, "and 25 km in, far fewer");
    Check.That(Launches([shag], MapBiome.Tundra, tie.WithinMetres, shag) == 0, "and at the edge of its distance, none");
    int skeinsFar = Launches([shag, skein], MapBiome.Grassland, tie.WithinMetres, skein);
    Check.That(skeinsFar == 10000, "a shag that does not come leaves the sky to the others");

    // R10642-2: sea on every side is still sea, though its bearings cancel.
    SeaReading? island = FlightRules.FindSea(birds, Vector2.Zero, point => point.Length() > 1000);
    Check.That(island is not null, "a place with sea on every side has found it");
    Check.That(island!.Value.Metres == birds.SeaRingsMetres[0], "on the first ring");
    Check.That(MathF.Abs(island.Value.Way.Length() - 1) < 0.001f, "with a way to go");
    Func<Vector2, bool> strait = point => Math.Abs(point.X) > 2000;
    SeaReading? between = FlightRules.FindSea(birds, Vector2.Zero, strait);
    Check.That(between is not null && strait(between.Value.Way * (float)between.Value.Metres), "between two coasts the way leads to one of them");
    SeaReading coast = new(8000, new Vector2(0.6f, 0.8f));
    Random lines = new(10641);
    for (int trial = 0; trial < 100; trial++)
    {
        Flight outbound = FlightRules.Plan(shag, birds, player, lines.NextDouble, (_, _) => 900, FlightRules.SeaBearing(tie, coast, 8, lines.NextDouble()));
        Flight inbound = FlightRules.Plan(shag, birds, player, lines.NextDouble, (_, _) => 900, FlightRules.SeaBearing(tie, coast, 16, lines.NextDouble()));
        double spread = Math.Cos((tie.HeadingSpreadDegrees + 0.1) * Math.PI / 180);
        Check.That(Vector2.Dot(outbound.Heading, coast.Way) >= spread, "in the morning a flight runs out to sea");
        Check.That(Vector2.Dot(inbound.Heading, -coast.Way) >= spread, "in the afternoon it runs back");
        BirdPose[] file = [.. outbound.Slots.Select(slot => FlightRules.At(outbound, slot, outbound.Seconds / 2))];
        Vector2 sideways = new(-outbound.Heading.Y, outbound.Heading.X);
        Check.That(file.All(pose => Math.Abs(Vector2.Dot(pose.Ground - file[0].Ground, sideways)) <= 2 * shag.Flock.Formation!.StrayMetres + 0.01), "a file keeps to one line");
        Check.That(file.Skip(1).All(pose => Vector2.Dot(pose.Ground - file[0].Ground, outbound.Heading) < 0), "behind its leader");
        Check.That(FlightRules.Passes(outbound, player) >= birds.MissMetres.Least - 1, "and misses the player");
        Check.That(outbound.Slots.All(slot => FlightRules.Size(FlightRules.At(outbound, slot, 0), player, birds) == 0), "and starts out of sight");
    }

    Flight circling = FlightRules.Plan(soarer, birds, player, random.NextDouble, (_, _) => 900);
    FlightSlot only = circling.Slots[0];
    BirdPose a = FlightRules.At(circling, only, 100), b = FlightRules.At(circling, only, 100.1);
    Vector2 facing = WildlifeRules.Facing(a.Yaw);
    Check.That(Vector2.Dot(facing, Vector2.Normalize(b.Ground - a.Ground)) > 0.99f, "a bird faces the way it is going");
    Check.That(Math.Sign(a.Bank) == Math.Sign(circling.TurnRadiansPerSecond), "it leans into its turn");
    int flapping = Enumerable.Range(0, 900).Count(second => FlightRules.Flapping(circling, only, second));
    Check.That(flapping > 0 && flapping < 200, "it soars, and flaps now and then");
    Check.Throws<InvalidDataException>(() => BirdCatalog.Parse(ReadResource("Wildlife.birds.json").Replace("\"Desert\"", "\"Dessert\"")), "an unknown biome is refused");
});

Check.Section("parties", () =>
{
    // Patch, 2026-11-05: small birds are drawn too. Nothing lands in view, and a bird on the ground and a bird in the air need not be the same bird.
    PartyCatalog parties = PartyCatalog.Parse(ReadResource("Wildlife.birds.json"));
    PartyKind parrot = parties.Kinds[0];
    const double Fov = 70, Margin = 5, Step = 0.1;
    Check.That(parrot.FeedsAt(MapBiome.TemperateForest, 12, Season.Summer), "a glade parrot feeds in the forest");
    Check.That(!parrot.FeedsAt(MapBiome.TemperateForest, 2, Season.Summer), "not in the night");
    Check.That(!parrot.FeedsAt(MapBiome.Desert, 12, Season.Summer), "and not in the desert");

    Vector3 eye = new(100, 11.6f, 200), forward = new(0, 0, -1);
    Check.That(PartyRules.InView(eye + new Vector3(0, 0, -30), eye, forward, Fov, Margin), "what is straight ahead is in view");
    Check.That(!PartyRules.InView(eye + new Vector3(0, 0, 30), eye, forward, Fov, Margin), "what is behind is not");
    Check.That(PartyRules.InView(eye + new Vector3(30, 0, -30), eye, forward, Fov, Margin), "what is at the corner of a wide screen counts as seen");

    Random random = new(10639);
    Vector2 player = new(eye.X, eye.Z);
    for (int trial = 0; trial < 300; trial++)
    {
        double bearing = random.NextDouble() * 2 * Math.PI;
        Vector3 look = new((float)Math.Sin(bearing), 0, (float)-Math.Cos(bearing));
        var placed = PartyRules.Place(parties, parrot, player, eye, look, Fov, random.NextDouble, _ => 10, _ => true);
        Check.That(placed is not null, "open ground gives a party a spot");
        Vector2 found = placed!.Value.Spot;
        // R10639-2: a spot out of view can still scatter a bird onto the screen.
        Check.That(placed.Value.Birds.All(bird => !PartyRules.InView(PartyRules.Standing(bird), eye, look, Fov, Margin)), "every bird of a party appears out of view");
        double metres = Vector2.Distance(found, player);
        Check.That(metres >= parties.AppearMetres.Least - 0.01 && metres <= parties.AppearMetres.Most + 0.01, "and at the distance parties appear");
    }

    Check.That(PartyRules.Place(parties, parrot, player, eye, forward, Fov, random.NextDouble, _ => 10, _ => false) is null, "ground that does not suit gives no spot");

    Vector2 spotAt = new(0, 0);
    PartyBird[] birds = PartyRules.Scatter(parrot, spotAt, random.NextDouble, _ => 10);
    Check.That(birds.Length >= parrot.Least && birds.Length <= parrot.Most, "a party is its kind's size");
    Check.That(birds.All(bird => Vector2.Distance(bird.At, spotAt) <= parrot.SpreadMetres + 0.01), "its birds stand about its spot");
    int hops = 0;
    double wandered = 0;
    for (double age = 0; age < 300; age += Step)
    {
        foreach (PartyBird bird in birds)
        {
            bool was = bird.HopLeft > 0;
            PartyRules.Feed(bird, parrot, spotAt, Step, random.NextDouble);
            if (!was && bird.HopLeft > 0) hops++;
            wandered = Math.Max(wandered, Vector2.Distance(bird.At, spotAt));
            Check.That(PartyRules.HopHeight(bird, parrot) <= parrot.Gait.HopHeightMetres + 1e-6, "a hop keeps to its height");
        }
    }

    Check.That(hops > birds.Length, "birds hop about as they feed");
    Check.That(wandered <= parrot.SpreadMetres + (3 * parrot.Gait.HopMetres), "and keep to the party's ground");
    Check.That(!PartyRules.Spooked(birds, parrot, new Vector2(100, 0)), "a far player flushes nothing");
    Vector2 near = birds[0].At + new Vector2((float)parrot.Burst.FlushMetres - 0.5f, 0);
    Check.That(PartyRules.Spooked(birds, parrot, near), "a player who comes near does");
    PartyRules.Flush(birds, parrot, near, random.NextDouble);
    Check.That(birds.All(bird => bird.FlushIn is not null && bird.FlushIn <= parrot.Burst.StaggerSeconds), "the whole party goes within a moment");
    foreach (PartyBird bird in birds)
    {
        bird.Launch = new Vector3(bird.At.X, 10, bird.At.Y);
        bird.Flying = 3;
        BirdPose pose = PartyRules.Burst(bird, parrot);
        Check.That(Vector2.Distance(pose.Ground, near) > Vector2.Distance(bird.At, near), "a flushed bird goes away from the player");
        Check.That(pose.Position.Y > bird.Launch.Y, "and up");
        Vector2 way = WildlifeRules.Facing(pose.Yaw);
        Check.That(Vector2.Dot(way, bird.Away) > 0.99f, "facing where it goes");
    }

    // R10639-1: a party whose time is up must not take a bird out of the sky in front of the player.
    Func<Vector3, bool> noneSeen = _ => false, allSeen = _ => true;
    Check.That(!PartyRules.Leaves(birds, 0, 0, parties, noneSeen), "a party with birds in the air stays until they have flown");
    PartyBird flushed = PartyRules.Scatter(parrot, spotAt, random.NextDouble, _ => 10)[0];
    flushed.FlushIn = 0.1;
    Check.That(!PartyRules.Leaves([flushed], 0, 0, parties, noneSeen), "as does one with a bird about to go");
    PartyBird[] feeding = PartyRules.Scatter(parrot, spotAt, random.NextDouble, _ => 10);
    Check.That(!PartyRules.Leaves(feeding, 30, 0, parties, noneSeen), "a party stays its time");
    Check.That(PartyRules.Leaves(feeding, 0, 0, parties, noneSeen), "then goes when nothing of it can be seen");
    Check.That(!PartyRules.Leaves(feeding, 0, 0, parties, allSeen), "and not while it can");
    Check.That(PartyRules.Leaves(feeding, 30, parties.GoneMetres, parties, noneSeen), "a party left far behind goes early");

    Check.That(PartyRules.Size(parties.GoneMetres - parties.FadeMetres - 1, parties) == 1, "a bird keeps its size until the last of the way");
    Check.That(PartyRules.Size(parties.GoneMetres, parties) == 0, "and is nothing when it goes");
    Check.Throws<InvalidDataException>(() => PartyCatalog.Parse(ReadResource("Wildlife.birds.json").Replace("\"Rainforest\"", "\"Rainforrest\"")), "an unknown biome is refused");
});

Check.Section("ragdolls", () =>
{
    // A body has to go while the ground under it is resident: 8 requested chunks of 16 m (TerrainConstants).
    const double ResidentMetres = 128;
    Check.That(catalog.Animation.CorpseMetres > 0 && catalog.Animation.CorpseMetres <= ResidentMetres, "a body goes before the ground under it does");
    Check.That(catalog.Animation.Ragdoll.BlowMetresPerSecond >= 0, "a killing blow drives a body away from the player");

    foreach (WildlifeKind kind in catalog.Kinds.Where(kind => kind.Mesh is not null))
    {
        string path = kind.Mesh!.Ragdoll ?? throw new InvalidDataException($"{kind.Id} has a mesh and no ragdoll");
        WildlifeRagdollRig rig = WildlifeRagdollRig.Parse(ReadResource($"Wildlife.{Path.GetFileName(path)}"));
        string[] joints = [.. rig.Bones.Select(bone => bone.Joint)];

        // A snake (#10883) has no hips or chest: its chain starts at its tail tip, and the blow lands mid-body.
        bool snake = joints.Contains("body1");
        // A gulper (#10987) is one body between its jaws and its tail: it starts and takes the blow there.
        bool gulper = joints.Contains("body") && joints.Contains("tail1");
        Check.That(rig.Bones[0].Joint == (snake ? "body1" : gulper ? "body" : "hips"), $"{kind.Id}'s ragdoll starts from its hips (a snake from its tail, a gulper from its body), which the fall follows");
        Check.That(snake ? joints[rig.HitBone].StartsWith("body", StringComparison.Ordinal) : joints[rig.HitBone] == (gulper ? "body" : "chest"),
            $"{kind.Id}'s killing blow lands on its chest (a snake mid-body, a gulper on its body)");
        Check.That(rig.Bones.All(bone => bone.Radius > 0 && bone.Mass > 0), $"{kind.Id}'s bones have a body");
        Check.That(rig.Bones.All(bone => bone.End is null ? bone.Length > 0 : joints.Contains(bone.End)), $"{kind.Id}'s bones end at a bone or in a length");

        // One link to each bone but the first, so the body hangs together as one tree.
        int[] children = [.. rig.Links.Select(link => link.Child).Order()];
        Check.That(children.SequenceEqual(Enumerable.Range(1, rig.Bones.Count - 1)), $"{kind.Id}'s bones are joined into one body");

        // Limits count from the rest pose, so a limit that leaves it out would throw the body at the first step.
        Check.That(rig.Links.All(link => link.Least <= 0 && link.Most >= 0), $"{kind.Id}'s joints can stand at rest");
        Check.That(rig.Links.All(link => link.Swing is null or > 0), $"{kind.Id}'s cones can swing");
        Check.That(rig.Links.All(link => MathF.Abs(link.Axis.Length() - 1) < 0.01f), $"{kind.Id}'s joint axes are units");
    }

    Check.Throws<InvalidDataException>(() => WildlifeRagdollRig.Parse("""{"hitBone":0,"bones":[],"links":[]}"""), "a ragdoll with no bones is refused");
    Check.Throws<InvalidDataException>(() => WildlifeRagdollRig.Parse("""{"hitBone":3,"bones":[{"joint":"hips","end":null,"length":1,"radius":0.1,"mass":1}],"links":[]}"""), "a hit bone it does not have is refused");
});

Check.Section("hostile animals", () =>
{
    // Patch, 2026-11-05: the desert's second hunter is a large rangy bird, bigger than the gaunt.
    WildlifeKind beak = catalog.Kind("axe-beak");
    Check.That(beak.Pressed == PressedResponse.Hunt, "the axe-beak hunts");
    Check.That(beak.Body.HeightMetres > catalog.Kind("gaunt").Body.HeightMetres, "the axe-beak stands over the gaunt");
    Check.That(catalog.SizeClasses[beak.Body.SizeClass].Height > catalog.SizeClasses[WildlifeSizeClass.Huge].Height, "its body is as tall as it is");
    // Patch, 2026-11-05: the drift-runner hunts as a pack, in a basic form to start.
    WildlifeKind runner = catalog.Kind("drift-runner");
    WildlifePack pack = runner.Hunt!.Pack ?? throw new InvalidDataException("the drift-runner hunts as a pack");
    Check.That(runner.GroupMinimum >= 3, "a pack is more than a pair");
    Check.That(catalog.Kind("gaunt").Hunt!.Pack is null, "the gaunt hunts alone");
    Check.That(Enumerable.Range(0, 5).Select(HunterRules.SideOf).SequenceEqual([0, 1, -1, 2, -2]), "a pack spreads to both sides of its first");
    // #10925 review R10925-1: a creature never settled settles at once, at any step, and then not again until the cooldown is out.
    Check.That(HunterRules.MaySettle(0, null) && HunterRules.MaySettle(600, null) && HunterRules.MaySettle(long.MaxValue, null), "a creature never settled may settle at once");
    Check.That(!HunterRules.MaySettle(1000, 1000) && !HunterRules.MaySettle(1000 + HunterRules.SettleAgainSteps - 1, 1000), "one just settled may not settle again before the cooldown");
    Check.That(HunterRules.MaySettle(1000 + HunterRules.SettleAgainSteps, 1000), "one settled may settle again once the cooldown is out");

    Vector2 player = Vector2.Zero, far = new(0, 40), near = new(0, 8);
    Check.That(HunterRules.Flank(far, player, 0, pack) is null, "the first of a pack comes straight in");
    Check.That(HunterRules.Flank(near, player, 1, pack) is null, "near the player they all go straight in");
    Vector2 left = HunterRules.Flank(far, player, 1, pack)!.Value, right = HunterRules.Flank(far, player, -1, pack)!.Value;
    Check.That(left.X * right.X < 0, "pack animals swing out to opposite sides");
    foreach (int side in new[] { 1, -1, 2, -2, 9 })
    {
        // Followed all the way, an animal spreads from the line it started on and arrives at the edge of the close.
        Vector2 walking = far;
        int walked = 0;
        while (HunterRules.Flank(walking, player, side, pack) is Vector2 aim && walked++ < 500)
        {
            double before = walking.Length();
            walking += Vector2.Normalize(aim - walking) * 0.5f;
            Check.That(walking.Length() < before, $"a flanking animal on side {side} keeps closing");
        }

        Check.That(walked < 500, $"an animal on side {side} gets there");
        Check.That(Math.Abs(walking.X) > 2 && Math.Sign(walking.X) != Math.Sign(far.X) || walking.X != 0, $"an animal on side {side} arrives away from the straight line");
    }

    // #10878: near the player a pack rings it, each at striking distance in its own place, rather than piling onto one spot.
    const double Halt = 2.1;
    Vector2 leaderSpot = new(0, 2.1f);
    Check.That(HunterRules.Ring(near, player, leaderSpot, 0, pack, Halt) is null, "the first of a pack goes straight in");
    Check.That(HunterRules.Ring(far, player, leaderSpot, 1, pack, Halt) is null, "out past the close, a pack animal swings wide instead");
    Vector2[] places = [.. new[] { 1, -1, 2, -2 }.Select(side => HunterRules.Ring(near, player, leaderSpot, side, pack, Halt)!.Value)];
    Check.That(places.All(place => Math.Abs(place.Length() - Halt) < 1e-4), "each place is at striking distance");
    Check.That(places.Append(leaderSpot).SelectMany((a, i) => places.Append(leaderSpot).Skip(i + 1).Select(b => Vector2.Distance(a, b))).Min() > 1.0,
        "no two of a pack of five share a place");
    Check.That(places[0].X * places[1].X < 0, "the first two either side of the first take opposite sides");
    Vector2 pairPlace = HunterRules.Ring(near, player, leaderSpot, 1, null, Halt)!.Value;
    Check.That(Vector2.Distance(pairPlace, leaderSpot) > 1.0 && Math.Abs(pairPlace.Length() - Halt) < 1e-4, "a pair with no pack stands apart too");

    HerdRecord den = new("runners", runner.Id, Vector2.Zero, 3), other = new("others", runner.Id, Vector2.Zero, 1);
    WildlifeMember Runner(int id) => WildlifeRules.Wake(den, runner, catalog, 7, Vector2.Zero, 1, () => id).Leader;
    Dictionary<int, Hunter> packed = new()
    {
        [1] = new Hunter(runner, den, Runner(1), 0),
        [2] = new Hunter(runner, den, Runner(2), 1),
        [3] = new Hunter(runner, other, Runner(3)),
    };
    Dictionary<string, double> alarms = [];
    bool InSight(int _) => true;
    bool OutOfSight(int _) => false;
    Check.That(HunterRules.Alerted([], packed, alarms, 0.1, InSight).Count == 0, "a pack that has seen nothing stays as it was");
    Check.That(HunterRules.Alerted([1], packed, alarms, 0.1, OutOfSight).SetEquals([1]) && alarms.Count == 0, "a clear line beyond an animal's own sight calls no one");
    Check.That(HunterRules.Alerted([1], packed, alarms, 0.1, InSight).SetEquals([1, 2]), "what one of a pack sees they all come for, and no one else does");
    Check.That(HunterRules.Alerted([], packed, alarms, pack.AlarmSeconds / 2, InSight).SetEquals([1, 2]), "a pack keeps coming when the ground hides the player for a moment");
    Check.That(HunterRules.Alerted([1], packed, alarms, pack.AlarmSeconds / 4, OutOfSight).SetEquals([1, 2]), "a call runs on while it lasts");
    Check.That(HunterRules.Alerted([1], packed, alarms, pack.AlarmSeconds / 2, OutOfSight).SetEquals([1]), "a line beyond sight does not renew a call, so it dies away");
    Check.That(alarms.Count == 0, "a dead call leaves nothing behind");

    WildlifeMember second = WildlifeRules.Wake(den, runner, catalog, 7, Vector2.Zero, 1, () => 1, null, 1).Leader;
    WildlifeMember first = WildlifeRules.Wake(den, runner, catalog, 7, Vector2.Zero, 1, () => 2).Leader;
    Check.That(first.Coat != second.Coat || first.Size != second.Size, "animals of a pack differ by their place in it");

    // Each body routes on a grid of its own (#10925): the gaunt and the axe-beak are different capsules, so different grids.
    WildlifeCapsule gauntBody = catalog.SizeClasses[catalog.Kind("gaunt").Body.SizeClass], beakBody = catalog.SizeClasses[beak.Body.SizeClass];
    Check.That(gauntBody != beakBody, "a gaunt and an axe-beak are routed as different bodies");
    // The low, wide cave hunters take the broad class: their true width, a person's headroom.
    WildlifeCapsule human = catalog.SizeClasses[WildlifeSizeClass.Human], broad = catalog.SizeClasses[WildlifeSizeClass.Broad];
    Check.That(broad.Radius > human.Radius && broad.Height == human.Height, "a broad body is wider than a person and no taller");
    foreach (string wide in new[] { "mouldback", "drop-sloth", "hummock" })
    {
        WildlifeKind k = catalog.Kind(wide);
        Check.Equal(WildlifeSizeClass.Broad, k.Body.SizeClass, $"the {wide} is broad");
        // The hummock (1.6 m) is wider still: broad is the nearest class without a large body's 2.4 m of headroom.
        Check.That(wide == "hummock" || 2 * broad.Radius >= k.Body.WidthMetres * 0.9, $"the {wide}'s capsule holds its width");
    }
    (float standing, float tilt) = WildlifeClips.Stance(10, 12, 8, -1.08, -1.08, catalog.Animation);
    Check.That(standing == 10 && tilt == 0, "a body whose feet stand in one place is not pitched by the ground");

    WildlifeKind gaunt = catalog.Kind("gaunt");
    WildlifeHunt hunt = gaunt.Hunt ?? throw new InvalidDataException("the gaunt must hunt");
    Check.That(catalog.Kinds.All(k => (k.Pressed == PressedResponse.Hunt) == (k.Hunt is not null)), "a kind hunts exactly when it has a hunt block");

    // Patch, 2026-11-04: hostile animals keep to empty country, away from the lush ground neutral herds make idyllic.
    MapBiome[] lush = [MapBiome.Grassland, MapBiome.TemperateForest, MapBiome.Rainforest, MapBiome.BorealForest, MapBiome.Shrubland];
    Check.That(lush.All(biome => gaunt.HabitatWeight(biome) == 0), "a hostile animal lives on none of the lush country");
    Check.That(gaunt.HabitatWeight(MapBiome.Alpine) > 0 && gaunt.HabitatWeight(MapBiome.Desert) > 0, "it lives high and dry");

    int Gaunts(MapBiome biome) => Enumerable.Range(0, 2000)
        .SelectMany(i => WildlifeSeeding.Seed(new WildlifeRegion(i, 7), 41, catalog, (_, _) => biome).Herds)
        .Count(herd => herd.KindId == "gaunt");
    Check.Equal(0, Gaunts(MapBiome.Grassland), "no hostile animal is seeded on grassland");
    int alpine = Gaunts(MapBiome.Alpine);
    Check.That(alpine is > 0 and < 60, $"it is rare where it lives: {alpine} homes in 2000 square kilometres of mountain");

    CreatureKind creature = HunterRules.KindOf(gaunt);
    Check.Equal(hunt.ReachMetres, creature.ReachMetres, "the creature rules bite at the content's reach");
    Check.Equal(gaunt.Speeds.Flee, creature.PursueSpeedMetresPerSecond, "it hunts at its running speed");
    Check.Equal(CreatureDisposition.Hostile, creature.Tuning.Disposition, "it is hostile");
    Check.That(creature.HaltDistanceMetres < creature.ReachMetres, "it halts inside its reach");

    string manifest = ReadResource("Wildlife.gaunt.manifest.json");
    WildlifeRig rig = WildlifeRig.For(gaunt.Mesh!, CreatureManifest.Parse(manifest));
    foreach (WildlifeClipRole role in new[] { WildlifeClipRole.Threat, WildlifeClipRole.Bite, WildlifeClipRole.Hurt, WildlifeClipRole.Die, WildlifeClipRole.Run })
        Check.That(rig.Clips.ContainsKey(role), $"the gaunt has a {role} clip");
    Check.That(rig.Clip(WildlifeClipRole.Bite).Seconds > hunt.BiteWindupSeconds, "the jaws close before the bite ends");

    // The capsule holds the legs (R10615-1): the mesh stands ahead of it, so all four feet lie within its radius.
    WildlifeCapsule capsule = catalog.SizeClasses[gaunt.Body.SizeClass];
    CreatureRig stance = CreatureManifest.Parse(manifest);
    Vector2 drawn = HunterRules.Drawn(Vector2.Zero, 0, stance.HindMetres, stance.ForeMetres, 1);
    Vector2 facing = WildlifeRules.Facing(0);
    Check.That(Vector2.Dot(drawn, facing) > 0, "the mesh stands ahead of the capsule");
    double hindFoot = Vector2.Dot(drawn + (facing * (float)stance.HindMetres), facing), foreFoot = Vector2.Dot(drawn + (facing * (float)stance.ForeMetres), facing);
    Check.That(Math.Abs(hindFoot + foreFoot) < 1e-4, $"the capsule stands between the feet: {hindFoot:F2} and {foreFoot:F2}");
    Check.That(Math.Abs(hindFoot) < capsule.Radius && gaunt.Body.WidthMetres / 2 < capsule.Radius, "the feet lie within its radius");
    Check.That(capsule.Height >= gaunt.Body.HeightMetres, "and it is as tall as the animal's back");
    Check.That(hunt.HaltMetres > capsule.Radius, "it halts clear of its own capsule");

    Hunter Fresh() => new(gaunt, new HerdRecord("gaunt@test", "gaunt", Vector2.Zero, 1),
        new WildlifeMember(1, Vector2.Zero, Vector2.Zero, CombatantState.Fresh(gaunt.MaximumHealth, gaunt.Defence)));
    const double Runs = 3.7, Threat = 2.4, Step = 1d / 60;

    Hunter hunter = Fresh();
    Check.Equal(WildlifeClipRole.Idle, HunterRules.Want(hunter, CreatureState.Idle, 0, Runs), "an unaware hunter idles");
    Check.Equal(WildlifeClipRole.Walk, HunterRules.Want(hunter, CreatureState.Pursuing, 2, Runs), "slowly, it walks");
    Check.Equal(WildlifeClipRole.Run, HunterRules.Want(hunter, CreatureState.Pursuing, 7, Runs), "fast, it runs");
    Check.Equal(WildlifeClipRole.Look, HunterRules.Want(hunter, CreatureState.Attacking, 0, Runs), "standing at the player between bites, it watches them");

    HunterRules.Advance(hunter, CreatureState.Pursuing, 30, Threat, Step);
    Check.That(hunter.Holding, "a hunter that has seen the player stands to show its threat");
    Check.Equal(WildlifeClipRole.Threat, HunterRules.Want(hunter, CreatureState.Pursuing, 0, Runs), "the threat shows");
    for (int i = 0; i < 200; i++) HunterRules.Advance(hunter, CreatureState.Pursuing, 30, Threat, Step);
    Check.That(!hunter.Holding, "the threat ends, and the hunt goes on");
    HunterRules.Advance(hunter, CreatureState.Pursuing, 30, Threat, Step);
    Check.That(!hunter.Holding, "it threatens once a hunt");
    HunterRules.Advance(hunter, CreatureState.Idle, 60, Threat, Step);
    HunterRules.Advance(hunter, CreatureState.Pursuing, 30, Threat, Step);
    Check.That(hunter.Holding, "a hunter that lost interest threatens again on the next hunt");

    Hunter close = Fresh();
    HunterRules.Advance(close, CreatureState.Pursuing, hunt.ReachMetres - 0.1, Threat, Step);
    Check.That(!close.Holding, "a player already in reach gets no threat");

    Hunter biting = Fresh();
    AttackProfile blow = new(hunt.Accuracy, hunt.Power, DamageType.Piercing);
    HunterRules.Bite(biting, blow, 1.0);
    Check.That(biting.Holding, "it stands to bite");
    Check.Equal(WildlifeClipRole.Bite, HunterRules.Want(biting, CreatureState.Attacking, 0, Runs), "the bite shows");
    int closesAt = 0;
    for (int i = 1; i <= 60 && closesAt == 0; i++)
    {
        if (HunterRules.Advance(biting, CreatureState.Attacking, 2, Threat, Step) is not null) closesAt = i;
    }

    Check.That(Math.Abs((closesAt * Step) - hunt.BiteWindupSeconds) <= Step, $"the blow lands as the jaws close: step {closesAt}");
    Check.That(HunterRules.Lands(hunt, hunt.ReachMetres + hunt.DodgeMetres - 0.01, seesPlayer: true), "a player still in reach is bitten");
    Check.That(!HunterRules.Lands(hunt, hunt.ReachMetres + hunt.DodgeMetres + 0.01, seesPlayer: true), "a player who stepped away in time is not");
    Check.That(!HunterRules.Lands(hunt, hunt.ReachMetres - 0.5, seesPlayer: false), "a bite begun in sight does not land round a corner, however near the player stands");

    biting.HurtSeconds = 0.3;
    Check.Equal(WildlifeClipRole.Hurt, HunterRules.Want(biting, CreatureState.Attacking, 0, Runs), "a blow shows over the bite");

    Check.That(Math.Abs(HunterRules.Turned(0, new Vector2(1, 0), 90, 10) - (MathF.PI / 2)) < 1e-4, "it comes round to face east");
    Check.That(Math.Abs(HunterRules.Turned(0, new Vector2(1, 0), 90, 0.1) - (MathF.PI / 20)) < 1e-4, "no faster than it can turn");
    Check.Equal(1f, HunterRules.Turned(1, Vector2.Zero, 90, 1), "with nowhere to face it stays as it was");
});

Check.Section("kinds", () =>
{
    string[] firstWave = ["ground-sloth", "hipparion", "giant-deer", "entelodont", "giant-beaver", "cave-goat", "glyptodon", "mammoth"];
    string[] secondWave = ["ancylotherium", "paraceratherium", "diprotodon", "megacerops", "sivatherium"];
    Check.That(firstWave.Concat(secondWave).All(id => catalog.TryKind(id, out _)), "the first- and second-wave kinds must load");
    string[] hunters = ["gaunt", "axe-beak", "drift-runner"];
    string[] cave = ["root-adder", "mouldback", "drowner", "hummock", "ribbon-stalker", "stalk-bill", "drop-sloth", "draft-hound", "salt-monitor", "glass-courser", "seep-rat", "plume-neck"];
    Check.That(cave.All(id => catalog.TryKind(id, out WildlifeKind k) && k.Cave is not null), "the cave's hunters (#10876) load, each with its cave");
    string[] ruin = ["collector", "skitterer", "carrier", "reaper", "threshold-lurker", "shard-dweller", "wall-runner", "channel-sifter", "stilt-pack", "brood-bearer", "mender", "brood-skitterer", "chime-grazer"];
    Check.That(ruin.All(id => catalog.TryKind(id, out WildlifeKind k) && k.Hunt is not null && k.Cave is null), "the ruins' hunters (#10956) load, in no cave's communities");
    string[] fog = ["needle-mouth", "fade-ribbon", "gulper"];
    Check.That(fog.All(id => catalog.TryKind(id, out WildlifeKind k) && k.Hunt?.Fog is not null && k.Cave is null), "the fog foes (#10985) load, each with its fog ways");
    Check.Equal(firstWave.Length + secondWave.Length + hunters.Length + cave.Length + ruin.Length + fog.Length, catalog.Kinds.Count,
        "the content holds the two waves, the hunters, the cave's hunters, the ruins' hunters, the fog foes and nothing else");
    Check.That(Enum.GetValues<PressedResponse>().All(r => catalog.Kinds.Any(k => k.Pressed == r)), "every pressed response must have a kind that shows it");
    Check.That(Enum.GetValues<WildlifeSizeClass>().All(c => catalog.SizeClasses.ContainsKey(c)), "every size class must have a capsule");
    WildlifeKind sloth = catalog.Kind("ground-sloth");
    Check.Equal(1.0, sloth.HabitatWeight(MapBiome.Grassland), "sloths live on grassland");
    Check.Equal(0.0, sloth.HabitatWeight(MapBiome.Desert), "a biome a kind does not list carries none of it");
    Check.Equal(PressedResponse.Stand, sloth.Pressed, "a crowded sloth stands");
    Check.Equal(WildlifeSizeClass.Huge, sloth.Body.SizeClass, "a sloth is huge");
    Check.Equal(0x8a6a48u, sloth.Body.SrgbColour, "a body colour is read as sRGB hex");
    Check.That(catalog.Kinds.All(k => k.Senses.ActMetres < k.Senses.NoticeMetres), "every kind notices before it acts");
    Check.That(catalog.Kinds.All(k => k.Loot.Entries.All(e => e.ItemId is "meat" or "hide")), "wildlife drops only items the inventory knows");
    Check.Equal(WildlifeActivity.Rest, sloth.Schedule.Steps[sloth.Schedule.StepAt(0.5 / 24)].Activity, "a day grazer rests after midnight");
    Check.Equal(WildlifeActivity.Graze, sloth.Schedule.Steps[sloth.Schedule.StepAt(6.0 / 24)].Activity, "a day grazer grazes at dawn");

    string text = ReadKinds();
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(text.Replace("\"noticeMetres\": 25", "\"noticeMetres\": 5")),
        "a kind that acts before it notices must be refused");
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(text.Replace("\"Desert\": 1.0", "\"Dessert\": 1.0")),
        "an unknown biome must be refused");
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(text.Replace("\"pressed\": \"hunker\"", "\"pressed\": \"burrow\"")),
        "an unknown pressed response must be refused");
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(text.Replace("\"sleepMetres\": 360", "\"sleepMetres\": 100")),
        "herds must sleep further out than they wake");
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(text.Replace("\"huge\": { \"radius\": 1.0, \"height\": 3.2 }", "\"huge\": { \"radius\": 1.0, \"height\": 1.5 }")),
        "a size class must be a capsule taller than its end caps");
});

Check.Section("cohesion", () =>
{
    WildlifeKind sloth = catalog.Kind("ground-sloth");
    WildlifeHerd herd = Herd("ground-sloth", new Vector2(100, 100), 7);
    // Scatter them, as a herd wakes or comes out of a scare.
    for (int i = 0; i < herd.Members.Count; i++) herd.Members[i].Position += new Vector2((i * 7 % 5) - 2, (i * 3 % 7) - 3) * 6;
    Vector2 startCentre = herd.Centre;
    // A game day is twenty minutes: a hundred seconds is two hours of the morning's grazing.
    Run(herd, 100, Morning, _ => null);
    Check.Equal(WildlifeActivity.Graze, herd.Activity, "a sloth herd grazes in the morning");
    Check.That(SpreadRadius(herd) <= CohesionBound(sloth, herd.Members.Count),
        $"a grazing herd keeps together around its leader (spread {SpreadRadius(herd):F1} m)");
    Check.That(ClosestPair(herd) >= sloth.SpacingMetres * 0.4, $"herd-mates keep their spacing (closest {ClosestPair(herd):F1} m)");
    Check.That(Vector2.Distance(herd.Centre, startCentre) > 20, "a grazing herd drifts across its range");
    Check.That(Vector2.Distance(herd.Leader.Position, herd.Home) <= sloth.Population.RangeMetres + 5, "the leader stays in the home range");
    Check.That(herd.Members.All(m => m.Stage == Wariness.Calm), "with no player about, every animal stays calm");

    WildlifeHerd resting = Herd("ground-sloth", new Vector2(0, 0), 5, "resting");
    Vector2 before = resting.Leader.Position;
    Run(resting, 120, Midnight, _ => null);
    Check.Equal(WildlifeActivity.Rest, resting.Activity, "a sloth herd rests after midnight");
    Check.That(Vector2.Distance(resting.Leader.Position, before) < 0.5, "a resting leader stays put");

    // The leader is killed: the next one leads and the herd keeps its shape around it.
    herd.Remove(herd.Leader);
    Check.Equal(Vector2.Zero, herd.Leader.Slot, "the new leader's slot is the herd's centre");
    Run(herd, 300, Morning with { DayFraction = 7.0 / 24 }, _ => null);
    Check.That(SpreadRadius(herd) <= CohesionBound(sloth, herd.Members.Count), "a herd that lost its leader keeps together on the next");
});

Check.Section("alarm spread", () =>
{
    WildlifeKind horse = catalog.Kind("hipparion");
    WildlifeHerd band = Herd("hipparion", new Vector2(0, 0), 12, "band");
    Run(band, 5, Morning, _ => null);
    // The player comes up on the east edge: only the nearest animal is inside its act distance.
    WildlifeMember edge = band.Members.MaxBy(m => m.Position.X)!;
    Vector2 player = edge.Position + new Vector2((float)(horse.Senses.ActMetres - 2), 0);
    Check.That(band.Members.Count(m => Vector2.Distance(m.Position, player) <= horse.Senses.ActMetres) < band.Members.Count,
        "the test's player is inside act distance of some of the band, not all of it");
    Dictionary<int, double> fled = [];
    Run(band, 10, Morning, _ => player, t =>
    {
        foreach (WildlifeMember m in band.Members.Where(m => m.Stage == Wariness.Respond)) fled.TryAdd(m.Id, t);
    });
    Check.That(fled.Count == band.Members.Count, $"the whole band must take flight, not only those the player pressed ({fled.Count}/{band.Members.Count})");
    double first = fled.Values.Min();
    Check.That(fled.Values.Max() - first <= 3, "the alarm must reach the far side of the band within a few seconds");
    Check.That(first >= horse.Senses.LookSeconds + horse.Senses.WarnSeconds - Tick, "an animal looks and warns before it bolts");

    // A herd with heads up waits for its watchers rather than walking away from them.
    WildlifeHerd watched = Herd("ground-sloth", new Vector2(500, 0), 6, "watched");
    WildlifeKind sloth = watched.Kind;
    WildlifeMember edgeSloth = watched.Members.MaxBy(m => m.Position.X)!;
    Vector2 watcher = edgeSloth.Position + new Vector2((float)(sloth.Senses.NoticeMetres - 1), 0);
    Run(watched, 60, Morning, _ => watcher);
    Check.That(watched.Members.Any(m => m.Stage == Wariness.Look), "a herd-mate within notice distance keeps watching");
    Check.That(SpreadRadius(watched) <= CohesionBound(sloth, watched.Members.Count), $"a watched herd keeps together (spread {SpreadRadius(watched):F1} m)");

    // A charging kind's partner displays rather than charges.
    WildlifeHerd pair = Herd("entelodont", new Vector2(0, 0), 2, "pair");
    WildlifeKind hog = pair.Kind;
    Vector2 near = pair.Members[0].Position + new Vector2((float)(hog.Senses.ActMetres - 1), 0);
    Run(pair, 7, Morning, _ => near);
    WildlifeMember partner = pair.Members.MaxBy(m => Vector2.Distance(m.Position, near))!;
    Check.That(partner.Stage >= Wariness.Warn, "an entelodont's partner warns too");
});

Check.Section("pressed responses", () =>
{
    // Flee: the band runs clear of the player, then regroups and calms.
    WildlifeKind horse = catalog.Kind("hipparion");
    WildlifeHerd band = Herd("hipparion", new Vector2(0, 0), 8, "flee");
    Vector2 player = band.Centre + new Vector2(20, 0);
    double startDistance = Vector2.Distance(band.Centre, player);
    bool allFled = false;
    Run(band, 20, Morning, _ => player, _ => allFled |= band.Members.All(m => m.Stage == Wariness.Respond));
    Check.That(allFled, "a pressed band flees as one");
    Check.That(Vector2.Distance(band.Centre, player) > startDistance + 40, "a fleeing band ends far from the player");
    Run(band, 30, Morning, _ => player);
    Check.That(band.Members.All(m => m.Stage is Wariness.Calm or Wariness.Regroup), "clear of the player, a band stops fleeing");
    Check.That(SpreadRadius(band) <= CohesionBound(horse, band.Members.Count), "a band regroups after fleeing");

    // Stand: a crowded sloth holds its ground facing the player, and calms once the player leaves.
    WildlifeHerd sloths = Herd("ground-sloth", new Vector2(0, 0), 3, "stand");
    WildlifeMember sloth = sloths.Members[0];
    Vector2 crowd = sloth.Position + new Vector2(5, 0);
    Run(sloths, 0.5, Midnight, _ => null);
    Vector2 stood = sloth.Position;
    Run(sloths, 10, Midnight, _ => crowd);
    Check.Equal(Wariness.Respond, sloth.Stage, "a crowded sloth stands");
    Check.That(sloths.Members.All(m => m.Stage == Wariness.Respond), "its herd stands with it");
    Check.That(Vector2.Distance(sloth.Position, stood) < 1, "a standing sloth holds its ground");
    Check.That(Math.Abs(WildlifeRules.Yaw(crowd - sloth.Position) - sloth.Yaw) < 0.01, "a standing sloth faces the player");
    Run(sloths, 20, Midnight, _ => crowd + new Vector2(200, 0));
    Check.That(sloths.Members.All(m => m.Stage == Wariness.Calm), "once the player leaves, the sloths calm");

    // Display then charge: the entelodont displays, charges to contact, then displays again.
    WildlifeHerd pair = Herd("entelodont", new Vector2(0, 0), 2, "charge");
    WildlifeKind hog = pair.Kind;
    WildlifeMember boar = pair.Members[0];
    Vector2 standing = boar.Position + new Vector2((float)(hog.Senses.ActMetres - 2), 0);
    List<Wariness> stages = [];
    double closest = double.PositiveInfinity;
    Run(pair, 15, Morning, _ => standing, _ =>
    {
        if (stages.Count == 0 || stages[^1] != boar.Stage) stages.Add(boar.Stage);
        closest = Math.Min(closest, Vector2.Distance(boar.Position, standing));
    });
    int warn = stages.IndexOf(Wariness.Warn);
    int charge = stages.IndexOf(Wariness.Respond);
    Check.That(warn >= 0 && charge > warn, $"an entelodont displays before it charges ({string.Join(">", stages)})");
    Check.That(stages.Skip(charge + 1).Contains(Wariness.Warn), "after a charge it displays again");
    Check.That(closest <= WildlifeRules.ContactMetres(hog, catalog.Behaviour) + 0.5, $"a charge closes to contact (closest {closest:F1} m)");

    // Hunker: the glyptodon stops where it is and stays down until the player has gone.
    WildlifeHerd shell = Herd("glyptodon", new Vector2(0, 0), 1, "hunker");
    WildlifeMember glyptodon = shell.Members[0];
    Vector2 poke = glyptodon.Position + new Vector2(4, 0);
    Run(shell, 6, Morning, _ => poke);
    Vector2 hunkered = glyptodon.Position;
    Run(shell, 10, Morning, _ => poke);
    Check.Equal(Wariness.Respond, glyptodon.Stage, "a pressed glyptodon hunkers");
    Check.That(Vector2.Distance(glyptodon.Position, hunkered) < 0.01, "a hunkered glyptodon does not move");
    Run(shell, 2, Morning, _ => poke + new Vector2(100, 0));
    Check.Equal(Wariness.Regroup, glyptodon.Stage, "a glyptodon comes out once the player has gone");

    // Struck: an animal answers a blow as its kind does when pressed, at once.
    WildlifeHerd struck = Herd("cave-goat", new Vector2(0, 0), 6, "struck");
    Run(struck, 1, Morning, _ => null);
    struck.Members[2].Struck = true;
    Run(struck, Tick, Morning, _ => struck.Members[2].Position + new Vector2(1, 0));
    Check.Equal(Wariness.Respond, struck.Members[2].Stage, "a struck goat flees at once");
});

Check.Section("bodies", () =>
{
    WildlifeKind kind = catalog.Kind("ground-sloth");
    WildlifeAnimation animation = catalog.Animation;
    CreatureRig built = CreatureManifest.Parse(ReadResource("Wildlife.sloth.manifest.json"));
    WildlifeRig rig = WildlifeRig.For(kind.Mesh!, built);
    Check.That(catalog.Kinds.All(k => k.Mesh is not null), "every kind has its real body");
    Check.Equal("alert", rig.Clip(WildlifeClipRole.Look).Name, "a looking sloth plays its alert clip");
    Check.Equal("idle", rig.Clip(WildlifeClipRole.Hunker).Name, "a role with no clip falls back to idling");
    Check.Throws<InvalidDataException>(() => WildlifeRig.For(kind.Mesh! with { Clips = new Dictionary<WildlifeClipRole, string> { [WildlifeClipRole.Idle] = "yawn" } }, built),
        "a clip the build does not have must be refused");

    // What the herd's state calls for.
    // A herd of grown animals, so the checks below are about the clips and not the young.
    WildlifeKind grown = kind with { Young = null };
    WildlifeHerd herd = WildlifeRules.Wake(new HerdRecord("clips", kind.Id, Vector2.Zero, 3), grown, catalog, Seed, Vector2.Zero, 3, NextId);
    WildlifeMember one = herd.Members[1];
    herd.Activity = WildlifeActivity.Graze;
    Check.Equal(WildlifeClipRole.Graze, WildlifeClips.Want(one, herd, rig), "a standing grazer feeds");
    herd.Activity = WildlifeActivity.Rest;
    Check.Equal(WildlifeClipRole.Idle, WildlifeClips.Want(one, herd, rig), "a resting animal idles");
    one.Velocity = new Vector2(0.75f, 0);
    Check.Equal(WildlifeClipRole.Walk, WildlifeClips.Want(one, herd, rig), "at walking pace it walks");
    Check.Equal(1f, WildlifeClips.Rate(WildlifeClipRole.Walk, 0.75f, rig, animation), "at the clip's own ground speed it plays at its own rate");
    one.Velocity = new Vector2(2.1f, 0);
    Check.Equal(WildlifeClipRole.Run, WildlifeClips.Want(one, herd, rig), "at fleeing pace it runs");
    Check.That(WildlifeClips.Rate(WildlifeClipRole.Run, 3, rig, animation) <= animation.MaximumRate, "a clip's rate stays within its limit");
    // #10926: a test aid's held clip plays at once, whatever the state calls for; a frozen animal keeps the clip it has.
    one.Hold = new WildlifeHold { Clip = WildlifeClipRole.Look };
    Check.That(WildlifeClips.Advance(one, herd, rig, animation, 0) && one.Clip == WildlifeClipRole.Look, "a held clip plays at once");
    one.Hold = new WildlifeHold { Frozen = true };
    Check.That(!WildlifeClips.Advance(one, herd, rig, animation, 5) && one.Clip == WildlifeClipRole.Look, "a frozen animal keeps its clip");
    one.Hold = null;
    one.Velocity = Vector2.Zero;
    one.Enter(Wariness.Look);
    Check.Equal(WildlifeClipRole.Look, WildlifeClips.Want(one, herd, rig), "a looking animal is alert");
    one.Enter(Wariness.Warn);
    Check.Equal(WildlifeClipRole.Warn, WildlifeClips.Want(one, herd, rig), "a warning animal warns");
    Check.Equal("alert", rig.Clip(WildlifeClipRole.Warn).Name, "a kind with no warning clip warns by looking");
    one.Enter(Wariness.Respond);
    Check.Equal(WildlifeClipRole.Stand, WildlifeClips.Want(one, herd, rig), "a pressed sloth swipes");

    // A new clip waits out the hold unless the animal is alarmed.
    WildlifeMember settling = herd.Members[2];
    settling.Clip = WildlifeClipRole.Idle;
    settling.Velocity = new Vector2(0.75f, 0);
    Check.That(!WildlifeClips.Advance(settling, herd, rig, animation, 0.1), "a clip just begun holds against a walk");
    Check.That(WildlifeClips.Advance(settling, herd, rig, animation, animation.ClipHoldSeconds), "after the hold the walk starts");
    settling.Velocity = Vector2.Zero;
    settling.Enter(Wariness.Look);
    Check.That(WildlifeClips.Advance(settling, herd, rig, animation, 0.01), "alarm does not wait for the hold");

    // Heading turns at the kind's rate, the short way round.
    WildlifeHerd turning = Herd("ground-sloth", Vector2.Zero, 1, "turning");
    WildlifeMember turner = turning.Members[0];
    turner.Yaw = 3f;
    Vector2 east = turner.Position + new Vector2(100, 0);
    WildlifeRules.Decide(turning, new HerdFacts(null, Morning, Seed), catalog, 0.1);
    turner.Yaw = MathF.PI - 0.1f;
    turner.Stage = Wariness.Look;
    Vector2 behind = turner.Position + (WildlifeRules.Facing(-MathF.PI + 0.1f) * 10);
    WildlifeRules.Decide(turning, new HerdFacts(behind, Morning, Seed), catalog, 0.1);
    float turned = MathF.IEEERemainder(turner.Yaw - (MathF.PI - 0.1f), 2 * MathF.PI);
    Check.That(turned > 0 && turned <= kind.Speeds.TurnDegrees * Math.PI / 180 * 0.1 + 1e-4, $"a body turns the short way, at its rate ({turned:F3} rad)");

    // A grazing herd walks at its walking pace, in turn with feeding, and covers its graze-drift speed.
    WildlifeHerd grazers = Herd("ground-sloth", new Vector2(0, 0), 4, "stop-and-go");
    int walking = 0, standing = 0;
    Vector2 start = grazers.Leader.Position;
    Run(grazers, 60, Morning, _ => null, _ =>
    {
        float speed = grazers.Leader.Velocity.Length();
        if (speed == 0) standing++;
        else if (Math.Abs(speed - kind.Speeds.Walk) < 0.01) walking++;
    });
    Check.That(walking > 0 && standing > 0 && walking + standing >= 590, $"a grazing leader stands or walks, never creeps ({walking} walking, {standing} standing)");
    double drift = Vector2.Distance(grazers.Leader.Position, start) / 60;
    Check.That(drift > kind.Speeds.GrazeDrift * 0.6 && drift < kind.Speeds.GrazeDrift * 1.4, $"over a cycle it keeps its graze-drift speed ({drift:F2} m/s)");

    // How a body sits on the ground.
    // A column's height stands at its centre, so a plane comes back exactly between columns.
    double Plane(long x, long z) => (0.25 * (x + 0.5)) - (0.1 * (z + 0.5));
    Check.That(Math.Abs(WildlifeClips.DrawnGround(12.3, -7.8, Plane) - ((0.25 * 12.3) + (0.1 * 7.8))) < 1e-9, "the drawn ground blends the columns around a point");
    // Blocks rise in whole metres: a gentle 5% slope drawn smooth must not tip a long body as its steps would.
    double Slope(long x, long z) => 0.05 * (x + 0.5);
    double Blocks(long x, long z) => Math.Round(Slope(x, z));
    (_, float onSteps) = WildlifeClips.Stance(0.5f, (float)Blocks(9, 0), (float)Blocks(11, 0), -1.2, 1.1, animation);
    (_, float onDrawn) = WildlifeClips.Stance(0.5f, (float)WildlifeClips.DrawnGround(8.8, 0, Slope), (float)WildlifeClips.DrawnGround(11.1, 0, Slope), -1.2, 1.1, animation);
    Check.That(Math.Abs(onDrawn - (animation.PitchShare * Math.Atan(0.05))) < 1e-3 && Math.Abs(onSteps) > 2 * Math.Abs(onDrawn), $"a body on the drawn ground takes its share of the slope ({onDrawn:F3} rad), far less than the blocks there would give ({onSteps:F3})");
    (float height, float pitch) = WildlifeClips.Stance(10, 10, 10.5f, -0.12, 1.6, animation);
    Check.That(pitch > 0.15 && pitch < 0.2 && height > 10 && height < 10.1, $"rising ground pitches the nose up by a share of the slope, the origin on it ({pitch:F2} rad at {height:F2})");
    (float high, float steep) = WildlifeClips.Stance(10, 10, 14, -0.12, 1.6, animation);
    double limit = animation.MostPitchDegrees * Math.PI / 180;
    Check.That(Math.Abs(steep) < limit && high > 10, $"a cast landing on a stone cannot stand a body on end ({steep * 180 / Math.PI:F0} degrees)");
    (float held, float most) = WildlifeClips.Stance(10, 9, 11, -0.2, 0.3, animation);
    Check.That(Math.Abs(most - limit) < 1e-4 && held == 10, $"a short body between two casts pitches no further than the limit ({most * 180 / Math.PI:F0} degrees)");
    Check.Equal((10f, 0f), WildlifeClips.Stance(10, 10, 10, -0.12, 1.6, animation), "level ground is level");

    // Planting feet is what a crowd costs, so only the nearest few are planted.
    double[] many = [.. Enumerable.Range(1, animation.MostPlanted + 5).Select(i => i * 3.0)];
    Check.Equal(animation.MostPlanted * 3.0, WildlifeClips.PlantWithin(many, animation), "only the nearest few animals are planted");
    Check.Equal(double.PositiveInfinity, WildlifeClips.PlantWithin([4, 9], animation), "a few animals are all planted");

    // Planted feet hold only while the body's placement in the session does.
    Vector3 at = new(5, 10, 5);
    Quaternion facing = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1f);
    Check.That(WildlifeClips.FeetHold(at, facing, at, facing, animation), "a body standing as it stood keeps its feet");
    Check.That(WildlifeClips.FeetHold(at, facing, at, -facing, animation), "a turn and its negative are the same turn");
    Check.That(WildlifeClips.FeetHold(at, facing, at + new Vector3(0.01f, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.005f), animation),
        "a hair's movement keeps them, so a standing body costs nothing");
    Check.That(!WildlifeClips.FeetHold(at, facing, at, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.2f), animation), "a body turning on the spot plants again");
    Check.That(!WildlifeClips.FeetHold(at, facing, at, Quaternion.Concatenate(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.1f), facing), animation), "and one pitching to the ground");
    Check.That(!WildlifeClips.FeetHold(at, facing, at - new Vector3(1024, 0, 0), facing, animation), "and one the world's origin moved out from under");

    // Ids are recycled: a body belongs to the animal it was made for, not to the number.
    List<string> retired = [];
    OwnedBodies<string> owned = new();
    WildlifeMember Animal(int id) => new(id, Vector2.Zero, Vector2.Zero, default);
    void Retire(string body) => retired.Add(body);
    WildlifeMember sloth = Animal(7), goat = Animal(7), later = Animal(7);
    Check.Equal("sloth", owned.Take(sloth, () => "sloth", Retire), "an animal gets a body");
    Check.Equal("sloth", owned.Take(sloth, () => "another", Retire), "and keeps it");
    // The sloth sleeps and a placeholder takes its id: it draws no body, and is not among the owners.
    owned.Keep(new HashSet<WildlifeMember>(), Retire);
    Check.That(retired.SequenceEqual(["sloth"]) && owned.Count == 0, "a sloth whose id went to a placeholder has its body retired");
    Check.Equal("calf", owned.Take(later, () => "calf", Retire), "and the id serves the next animal afresh");
    Check.Equal("adult", owned.Take(goat, () => "adult", Retire), "another animal under the id gets a body of its own");
    Check.That(retired.SequenceEqual(["sloth", "calf"]), "the old owner's is retired first, not handed on");
    owned.Keep(new HashSet<WildlifeMember> { goat }, Retire);
    Check.That(owned.Count == 1 && retired.Count == 2, "a body with its owner awake stays");

    // An object number goes round before it comes back: the last snapshot still holds a retired body's.
    CyclingSlots slots = new(3);
    int first = slots.Take(), second = slots.Take();
    slots.Release(first);
    Check.That(slots.Take() != first, "a number just released is not handed straight back");
    Check.Equal(first, slots.Take(), "it comes round in turn");
    Check.Throws<InvalidOperationException>(() => slots.Take(), "bodies past the range are refused rather than sharing a number");
    slots.Release(second);
    Check.Equal(second, slots.Take(), "and one in use is skipped");

    // How much of its animation an animal runs at each distance.
    Check.Equal(WildlifeTier.Full, WildlifeClips.Tier(10, WildlifeTier.Full, animation), "near, everything runs");
    Check.Equal(WildlifeTier.Play, WildlifeClips.Tier(100, WildlifeTier.Full, animation), "further, the clip plays");
    Check.Equal(WildlifeTier.Held, WildlifeClips.Tier(300, WildlifeTier.Play, animation), "further still, the pose holds");
    Check.Equal(WildlifeTier.Hidden, WildlifeClips.Tier(animation.DrawMetres + 50, WildlifeTier.Held, animation), "past the draw distance it is not drawn");
    double line = animation.FullMetres + 1;
    Check.Equal(WildlifeTier.Full, WildlifeClips.Tier(line, WildlifeTier.Full, animation), "an animal keeps the nearer tier for the margin");
    Check.Equal(WildlifeTier.Play, WildlifeClips.Tier(line, WildlifeTier.Play, animation), "and the further one until it is back inside the line");
});

Check.Section("water and ledges", () =>
{
    WildlifeBehaviour behaviour = catalog.Behaviour;
    Check.Equal(PressedResponse.TakeToWater, catalog.Kind("giant-beaver").Pressed, "a beaver takes to the water");
    Vector2 shore = new(30, 0);

    // Pressed, a beaver runs for its shore, slips in and goes under.
    WildlifeHerd family = Herd("giant-beaver", new Vector2(0, 0), 3, "dive");
    Vector2 hunter = family.Centre - new Vector2(5, 0);
    bool slipped = false;
    Run(family, 30, Morning, _ => hunter, _ => slipped |= family.Members.Any(m => m.DiveSeconds is not null && !m.Under), shore);
    Check.That(slipped, "a beaver is seen slipping in before it goes under");
    Check.That(family.Members.All(m => m.Under), "a pressed beaver with water goes under");
    double slid = behaviour.SlipMetresPerSecond * behaviour.SlipSeconds;
    Check.That(family.Members.All(m => m.Position.X > shore.X - behaviour.ShoreArrivalMetres && Vector2.Distance(m.Position, shore) <= behaviour.ShoreArrivalMetres + slid + 0.5), "it goes in at its shore, sliding on past it into the water");
    Check.That(family.Members.All(m => m.Velocity == Vector2.Zero), "under the water it does not move");

    // It stays down while the player waits on the bank, and comes up when the player has gone.
    Run(family, 60, Morning, _ => shore + new Vector2(0, 4), water: shore);
    Check.That(family.Members.All(m => m.Under && m.Stage == Wariness.Respond), "a player waiting at the shore keeps it under");
    Vector2 where = family.Leader.Position;
    Run(family, 2, Morning, _ => shore + new Vector2(0, 400), water: shore);
    Check.That(family.Members.All(m => !m.Under && m.DiveSeconds is null && m.Stage == Wariness.Regroup), "it comes up once the player has gone");
    Check.That(Vector2.Distance(family.Leader.Position, where) < 3, "it comes up where it went in");

    // Going in at different moments, a family still comes up together and stays up.
    WildlifeHerd scattered = Herd("giant-beaver", new Vector2(0, 0), 4, "scattered");
    Vector2 shock = scattered.Centre - new Vector2(5, 0);
    Run(scattered, 25, Morning, _ => shock, water: new Vector2(30, 0));
    scattered.Members[0].DiveSeconds += 5;
    Vector2 gone = new(900, 0);
    Run(scattered, 40, Morning, _ => gone, water: new Vector2(30, 0));
    Check.That(scattered.Members.All(m => !m.Under && m.DiveSeconds is null && m.Stage is Wariness.Regroup or Wariness.Calm), "a family comes up together, and the alarm does not send it back down");

    // A player leaving at once does not bring it straight back up.
    WildlifeHerd brief = Herd("giant-beaver", new Vector2(0, 0), 1, "brief");
    WildlifeMember diver = brief.Members[0];
    Run(brief, 10, Morning, _ => diver.Position - new Vector2(5, 0), water: new Vector2(10, 0));
    double wentUnder = diver.DiveSeconds ?? double.NaN;
    Check.That(diver.Under && wentUnder < behaviour.SlipSeconds + behaviour.LeastUnderSeconds, $"a beaver near water has just gone under ({wentUnder:F1} s)");
    Run(brief, 1, Morning, _ => new Vector2(500, 0), water: new Vector2(10, 0));
    Check.That(diver.Under, "it stays down its least time even with the player gone");

    // A far shore: it has long outrun the player by then, and goes in all the same.
    WildlifeHerd far = Herd("giant-beaver", new Vector2(0, 0), 2, "far");
    Vector2 startled = far.Centre - new Vector2(5, 0);
    bool wentIn = false;
    Run(far, 90, Morning, _ => startled, _ => wentIn |= far.Members.All(m => m.Under), new Vector2(150, 0));
    Check.That(wentIn, "a beaver that has made for the water goes in, however far it had to run");

    // One held back from the water gives up, and does not keep the rest under for ever.
    WildlifeHerd held = Herd("giant-beaver", new Vector2(0, 0), 3, "held");
    Vector2 near = held.Centre - new Vector2(5, 0);
    Vector2 stuckAt = held.Members[2].Position;
    Run(held, 200, Morning, _ => near, _ => held.Members[2].Position = stuckAt, new Vector2(25, 0));
    Check.That(!held.Members[2].ToWater && held.Members[2].DiveSeconds is null, "a beaver that cannot reach the water gives up");
    Run(held, 60, Morning, _ => new Vector2(900, 0), _ => held.Members[2].Position = stuckAt, new Vector2(25, 0));
    Check.That(held.Members.All(m => !m.Under), "and its family comes up all the same");

    // With no water in its range it flees like any other.
    WildlifeHerd dry = Herd("giant-beaver", new Vector2(0, 0), 3, "dry");
    Vector2 from = dry.Centre - new Vector2(5, 0);
    Run(dry, 12, Morning, _ => from);
    Check.That(dry.Members.All(m => m.DiveSeconds is null && !m.Under), "with no water it never dives");
    Check.That(dry.Centre.X > 10, "with no water it runs from the player");

    // A hurt beaver still takes to the water: it does not turn into a lone runner.
    WildlifeMember hurt = Herd("giant-beaver", new Vector2(0, 0), 1, "hurt").Members[0];
    hurt.Combat = hurt.Combat with { Health = 1 };
    Check.Equal(PressedResponse.TakeToWater, WildlifeRules.Response(hurt, catalog.Kind("giant-beaver")), "a hurt beaver makes for the water");

    // A goat springs at a rise too steep to walk, and lands before it springs again.
    WildlifeMember goat = Herd("cave-goat", new Vector2(0, 0), 1, "hop").Members[0];
    Check.That(WildlifeClips.Hops(goat, 0.6f, behaviour), "a rise past the walk makes a goat spring");
    Check.That(!WildlifeClips.Hops(goat, 0.2f, behaviour), "a walkable rise does not");
    goat.HopSeconds = 0.2;
    Check.That(!WildlifeClips.Hops(goat, 1f, behaviour), "it does not spring in the air");
    goat.HopSeconds = 0;
    goat.SinceHop = behaviour.HopEverySeconds / 2;
    Check.That(!WildlifeClips.Hops(goat, 1f, behaviour), "it lands before it springs again");

    // The rules time what the clips show.
    double ClipSeconds(string kind, string clip) => CreatureManifest.Parse(ReadResource($"Wildlife.{kind}.manifest.json")).Clips[clip].Seconds;

    Check.That(Math.Abs(ClipSeconds("giant-beaver", "slip") - behaviour.SlipSeconds) < 0.05, "the slip lasts as long as its clip");
    Check.That(Math.Abs(ClipSeconds("cave-goat", "ledge-hop") - behaviour.HopSeconds) < 0.05, "the hop lasts as long as its clip");
});

Check.Section("variety", () =>
{
    WildlifeVariety variety = catalog.Variety;
    List<WildlifeMember> all = [];
    for (int i = 0; i < 40; i++) all.AddRange(Herd("ground-sloth", Vector2.Zero, 8, $"look{i}").Members);
    Check.That(all.All(m => Math.Abs(m.Size - 1) <= variety.SizeShare + 1e-4), "sizes stay within their share");
    double most = (1 + variety.ValueShare) * (1 + variety.WarmthShare), least = (1 - variety.ValueShare) * (1 - variety.WarmthShare);
    Check.That(all.All(m => m.Coat.X <= most + 1e-4 && m.Coat.Z >= least - 1e-4 && m.Coat.Y > 0), "coats stay within theirs");
    Check.That(all.Select(m => m.Size).Distinct().Count() > all.Count / 2, "animals differ in size");
    Check.That(all.Select(m => m.Coat).Distinct().Count() > all.Count / 2, "and in coat");
    Check.That(Math.Abs(all.Average(m => m.Size) - 1) < 0.01 && Math.Abs(all.Average(m => m.Coat.Y) - 1) < 0.02, "a herd centres on its kind's own size and paint");
    Check.That(all.Any(m => m.Coat.X > m.Coat.Z) && all.Any(m => m.Coat.X < m.Coat.Z), "some coats run warmer, some cooler");
    Check.That(all.Any(m => m.Young) && all.Where(m => m.Young).Select(m => m.Coat).Distinct().Count() > 3, "the young vary too");

    // An animal looks the same each time its herd wakes.
    WildlifeHerd once = Herd("ground-sloth", Vector2.Zero, 6, "same");
    WildlifeHerd again = Herd("ground-sloth", new Vector2(50, 50), 6, "same");
    Check.That(once.Members.Zip(again.Members).All(pair => pair.First.Size == pair.Second.Size && pair.First.Coat == pair.Second.Coat && pair.First.Young == pair.Second.Young),
        "a herd wakes with the same animals in it");

    // A body's size carries through to its gait and its clips.
    WildlifeKind kind = catalog.Kind("ground-sloth");
    WildlifeMember grown = once.Members.First(m => !m.Young);
    Check.Equal(grown.Size, WildlifeClips.Scale(grown, kind), "a grown animal is drawn at its own size");
    WildlifeMember small = all.First(m => m.Young);
    Check.That(Math.Abs(WildlifeClips.Scale(small, kind) - (small.Size * kind.Young!.Scale)) < 1e-5, "a young one at its size on top of its age");
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(ReadKinds().Replace("\"sizeShare\": 0.07", "\"sizeShare\": 1.5")), "a variety share past the whole is refused");
});

Check.Section("hurt", () =>
{
    // A sloth stands until it is badly hurt, then runs by itself; the herd goes on standing.
    WildlifeKind kind = catalog.Kind("ground-sloth");
    WildlifeHerd herd = Herd("ground-sloth", new Vector2(0, 0), 5, "hurt");
    WildlifeMember sloth = herd.Members.First(member => !member.Young && member != herd.Leader);
    Vector2 crowd = herd.Leader.Position + new Vector2(5, 0);
    Run(herd, 10, Midnight, _ => crowd);
    Check.Equal(PressedResponse.Stand, WildlifeRules.Response(sloth, kind), "a whole sloth stands");
    int bad = (int)(sloth.Combat.MaximumHealth * kind.Senses.FleeBelowHealth) - 1;
    sloth.Combat = sloth.Combat with { Health = bad };
    Check.Equal(PressedResponse.Flee, WildlifeRules.Response(sloth, kind), "a badly hurt sloth runs");
    double before = Vector2.Distance(sloth.Position, crowd);
    Run(herd, 6, Midnight, _ => crowd);
    Check.That(Vector2.Distance(sloth.Position, crowd) > before + 8, "it runs from the player");
    Check.Equal(Wariness.Respond, herd.Leader.Stage, "and the herd goes on standing");
    Check.That(herd.Leader.Velocity == Vector2.Zero, "where it stood");

    // Its flight is its own: once it is clear the herd calms when the player goes, not held in alarm by it.
    Run(herd, 40, Midnight, _ => crowd + new Vector2(300, 0));
    Check.That(herd.Members.All(member => member.Stage is Wariness.Calm or Wariness.Regroup), "the herd calms once the player has gone");

    // A hurt glyptodon stays in its shell.
    WildlifeHerd shell = Herd("glyptodon", Vector2.Zero, 1, "shell");
    shell.Leader.Combat = shell.Leader.Combat with { Health = 1 };
    Check.Equal(PressedResponse.Hunker, WildlifeRules.Response(shell.Leader, shell.Kind), "a shell is no place to run from");
    Check.That(catalog.Kind("mammoth").Senses.FleeBelowHealth > 0 && catalog.Kind("entelodont").Senses.FleeBelowHealth > 0, "the other kinds that hold their ground break too");
});

Check.Section("young", () =>
{
    WildlifeKind kind = catalog.Kind("ground-sloth");
    WildlifeYoung young = kind.Young!;
    CreatureRig built = CreatureManifest.Parse(ReadResource("Wildlife.sloth.manifest.json"));
    WildlifeRig rig = WildlifeRig.For(kind.Mesh!, built);

    // A fair share of animals are young, and no herd is led by one.
    int total = 0, youngCount = 0;
    for (int i = 0; i < 200; i++)
    {
        WildlifeHerd sample = Herd("ground-sloth", Vector2.Zero, 6, $"count{i}");
        Check.That(!sample.Leader.Young, "a herd is led by a grown animal");
        total += sample.Members.Count - 1;
        youngCount += sample.Members.Count(member => member.Young);
    }

    Check.That(Math.Abs((youngCount / (double)total) - young.Share) < 0.08, $"young make their share of a herd ({youngCount} of {total})");
    Check.That(catalog.Kind("hipparion").Young is null, "a kind without young has none");

    WildlifeHerd herd = YoungHerd(7, "playful");
    WildlifeMember calf = herd.Members.First(member => member.Young);
    Check.That(calf.Combat.MaximumHealth < kind.MaximumHealth / 2, "the young are weaker");
    Check.That(young.Loot.Entries.All(entry => entry.Maximum <= kind.Loot.Entries.First(adult => adult.ItemId == entry.ItemId).Maximum / 2 + 1), "and yield less");

    // Among the adults they move faster, and they change direction, and stop.
    int moving = 0, still = 0, turns = 0;
    Vector2 last = Vector2.Zero;
    double farthest = 0, fastest = 0;
    Run(herd, 100, Morning, _ => null, _ =>
    {
        float speed = calf.Velocity.Length();
        if (speed == 0) still++;
        else
        {
            moving++;
            fastest = Math.Max(fastest, speed);
            if (last != Vector2.Zero && Vector2.Dot(Vector2.Normalize(last), Vector2.Normalize(calf.Velocity)) < 0.7f) turns++;
            last = calf.Velocity;
        }

        farthest = Math.Max(farthest, Vector2.Distance(calf.Position, herd.NearestAdult(calf.Position).Position));
    });
    Check.That(fastest <= young.DartSpeed + 0.01, $"a young animal keeps to its dart speed ({fastest:F2} m/s)");
    Check.That(young.DartSpeed > kind.Speeds.Walk, "the young run faster than the adults walk");
    Check.That(moving > 100 && still > 100, $"it darts and stops ({moving} moving, {still} still)");
    Check.That(turns >= 4, $"it changes direction ({turns} turns)");
    Check.That(farthest <= young.DartMetres * 2.5, $"it stays among the adults (at most {farthest:F1} m from one)");

    // Its legs turn over faster for its size, and it runs where an adult at that speed would walk.
    calf.Velocity = new Vector2((float)young.DartSpeed, 0);
    calf.Enter(Wariness.Calm);
    Check.Equal(WildlifeClipRole.Run, WildlifeClips.Want(calf, herd, rig), "at its dart speed it runs");
    Check.That(WildlifeClips.Rate(WildlifeClipRole.Run, (float)young.DartSpeed, rig, catalog.Animation, young.Scale) > 1.2f, "a small body's stride turns over faster");

    // Pressed, it keeps behind an adult from the player, while the adults stand.
    WildlifeHerd pressed = YoungHerd(7, "pressed");
    WildlifeMember small = pressed.Members.First(member => member.Young);
    Vector2 player = pressed.Leader.Position + new Vector2(6, 0);
    Run(pressed, 16, Midnight, _ => player);
    WildlifeMember shield = pressed.NearestAdult(small.Position);
    Check.Equal(Wariness.Respond, small.Stage, "the young are pressed with the herd");
    Check.That(Vector2.Distance(small.Position, player) > Vector2.Distance(shield.Position, player), "it stands behind an adult, from the player");
    Check.That(Vector2.Distance(small.Position, shield.Position) <= young.ShelterMetres * 2, "and close to it");
    Check.That(WildlifeClips.Want(small, pressed, rig) != WildlifeClipRole.Stand, "and it does not swipe");

    // The leader dies: a grown animal takes over.
    pressed.Remove(pressed.Leader);
    Check.That(!pressed.Leader.Young, "a grown animal leads when the leader falls");
    Check.Equal(Vector2.Zero, pressed.Leader.Slot, "from the herd's centre");

    WildlifeHerd YoungHerd(int count, string key)
    {
        for (int i = 0; ; i++)
        {
            WildlifeHerd candidate = Herd("ground-sloth", new Vector2(100, 100), count, $"{key}{i}");
            int small = candidate.Members.Count(member => member.Young);
            if (small >= 1 && small <= 3) return candidate;
        }
    }
});

Check.Section("region seeding", () =>
{
    // A made-up world: grassland west of x = 0, desert east of it, sea south of z = -20 km.
    MapBiome BiomeAt(double x, double z) => z < -20_000 ? MapBiome.Sea : x < 0 ? MapBiome.Grassland : MapBiome.Desert;
    WildlifeRegion region = new(-3, 2);
    RegionPopulation once = WildlifeSeeding.Seed(region, Seed, catalog, BiomeAt);
    RegionPopulation again = WildlifeSeeding.Seed(region, Seed, catalog, BiomeAt);
    Check.Equal(string.Join(";", once.Herds.Select(h => $"{h.Key}:{h.Home}:{h.Founding}")),
        string.Join(";", again.Herds.Select(h => $"{h.Key}:{h.Home}:{h.Founding}")), "the same seed seeds a region the same way");

    double size = catalog.Placement.RegionMetres;
    List<HerdRecord> west = [], east = [], sea = [], otherSeed = [];
    for (int i = 0; i < 20; i++)
    {
        for (int j = 0; j < 20; j++)
        {
            west.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(-1 - i, j), Seed, catalog, BiomeAt).Herds);
            east.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(i, j), Seed, catalog, BiomeAt).Herds);
            sea.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(i, -40 - j), Seed, catalog, BiomeAt).Herds);
            otherSeed.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(-1 - i, j), Seed + 1, catalog, BiomeAt).Herds);
        }
    }

    // A kind that takes to water lives on a river's bank, and not where there is none.
    const double RiverX = 500, RiverHalfWidth = 12;
    IReadOnlyList<RiverBank> RiverIn(double minX, double minZ, double maxX, double maxZ) =>
        minX <= RiverX && RiverX < maxX ? [.. Enumerable.Range(0, 20).Select(i => new RiverBank(RiverX, minZ + (i * 50), RiverHalfWidth, 1, 0))] : [];
    MapBiome Forest(double x, double z) => MapBiome.BorealForest;
    List<HerdRecord> byRiver = [], byNothing = [], noDrainage = [];
    for (int i = 0; i < 144; i++)
    {
        byRiver.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(0, i), Seed, catalog, Forest, null, RiverIn).Herds.Where(h => h.KindId == "giant-beaver"));
        byNothing.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(5, i), Seed, catalog, Forest, null, RiverIn).Herds.Where(h => h.KindId == "giant-beaver"));
        noDrainage.AddRange(WildlifeSeeding.Seed(new WildlifeRegion(5, i), Seed, catalog, Forest).Herds.Where(h => h.KindId == "giant-beaver"));
    }

    double OffBank(HerdRecord herd) => Math.Abs(herd.Home.X - RiverX) - RiverHalfWidth;
    Check.That(byRiver.Count > 0, "beavers settle along a river");
    Check.That(byRiver.All(h => Math.Abs(OffBank(h) - catalog.Placement.BankMetres) < 0.01), "a beaver's home is on the bank, clear of the water");
    Check.That(byRiver.Any(h => h.Home.X > RiverX) && byRiver.Any(h => h.Home.X < RiverX), "beavers settle on either bank");
    Check.Equal(0, byNothing.Count, "no beavers settle where there is no river");
    Check.That(noDrainage.Count > 0, "a world with no drainage places them as any other kind");
    Check.That(sea.Count == 0, "nothing is seeded at sea");
    Check.That(west.All(h => catalog.Kind(h.KindId).HabitatWeight(MapBiome.Grassland) > 0), "only grassland kinds are seeded on grassland");
    Check.That(east.All(h => catalog.Kind(h.KindId).HabitatWeight(MapBiome.Desert) > 0), "only desert kinds are seeded in desert");
    Check.That(east.Any(h => h.KindId == "glyptodon") && west.Any(h => h.KindId == "ground-sloth"), "each country holds its own kinds");
    double squareKilometres = 400 * size * size / 1_000_000;
    int sloths = west.Count(h => h.KindId == "ground-sloth");
    double expected = catalog.Kind("ground-sloth").Population.HerdsPerSquareKilometre * squareKilometres;
    Check.That(Math.Abs(sloths - expected) <= 3 * Math.Sqrt(expected), $"sloth herds come at the kinds' sparse density ({sloths} for {expected:F0} expected)");
    Check.That(west.All(h => h.Founding >= catalog.Kind(h.KindId).GroupMinimum && h.Founding <= catalog.Kind(h.KindId).GroupMaximum),
        "every herd's size is within its kind's group size");
    Check.That(west.Select(h => h.Home).Except(otherSeed.Select(h => h.Home)).Any(), "another world seed seeds other herds");
    Check.That(once.Herds.All(h => WildlifeRegion.At(h.Home.X, h.Home.Y, size) == region), "a herd's home is in its own region");

    // Towns keep their sanctuary: no home within it.
    Vector2 town = new(-1500, 2500);
    const double Sanctuary = 160;
    bool Excluded(double x, double z) => Vector2.Distance(new Vector2((float)x, (float)z), town) < Sanctuary;
    WildlifeLedger ledger = new(catalog, Seed, BiomeAt, Excluded);
    List<HerdRecord> nearTown = [.. ledger.HomesWithin(town.X, town.Y, 3000)];
    Check.That(nearTown.Count > 0 && nearTown.All(h => Vector2.Distance(h.Home, town) >= Sanctuary), "no herd makes its home in a town's sanctuary");
    Check.That(ledger.HomesWithin(town.X, town.Y, 3000).First() == nearTown[0], "the ledger keeps a seeded herd, so what happens to it lasts");

    // Waking: a herd wakes where its range puts it for the time of day, the same each time.
    HerdRecord herd = nearTown[0];
    WildlifeKind kind = catalog.Kind(herd.KindId);
    Vector2 wake = WildlifeRules.WakePoint(herd, kind, Morning, Seed, catalog.Placement.HomeAttempts, null);
    Check.Equal(wake, WildlifeRules.WakePoint(herd, kind, Morning, Seed, catalog.Placement.HomeAttempts, null), "a herd wakes in the same place for the same hour");
    Check.That(Vector2.Distance(wake, herd.Home) <= kind.Population.RangeMetres, "a herd wakes inside its home range");
    Check.That(wake != WildlifeRules.WakePoint(herd, kind, Morning with { Day = Morning.Day + 1 }, Seed, catalog.Placement.HomeAttempts, null),
        "a herd wakes elsewhere on another day");
});

Check.Section("sanctuary", () =>
{
    const float Radius = 160;
    Vector2 town = Vector2.Zero;
    bool Inside(Vector2 point) => Vector2.Distance(point, town) < Radius;
    double step = catalog.Behaviour.PathCheckMetres;

    // Runs a herd with the town in force, failing if any animal ever stands in it.
    int Intrusions(WildlifeHerd herd, double seconds, WorldTime start, Func<double, Vector2?> player)
    {
        int found = 0;
        for (double t = 0; t < seconds; t += Tick)
        {
            WorldTime time = start with { DayFraction = (start.DayFraction + (t / WorldClock.DaySeconds)) % 1 };
            WildlifeRules.Decide(herd, new HerdFacts(player(t), time, Seed, null, point => !Inside(point), Inside), catalog, Tick);
            WildlifeRules.Integrate(herd, Tick);
            found += herd.Members.Count(member => Inside(member.Position));
        }

        return found;
    }

    WildlifeHerd Beside(string kindId, Vector2 at, int count, string key)
    {
        WildlifeKind kind = catalog.Kind(kindId);
        return WildlifeRules.Wake(new HerdRecord(key, kind.Id, at, count), kind, catalog, Seed, at, count, NextId, Inside);
    }

    // A herd woken right on the edge: the slots that fall inside are drawn out of it.
    WildlifeHerd edge = Beside("mammoth", new Vector2(161, 0), 10, "edge");
    Check.That(!edge.Members.Any(member => Inside(member.Position)), "no animal wakes in a sanctuary, however near the edge its herd does");
    Check.Equal(0, Intrusions(edge, 30, Morning, _ => null), "an edge herd settling into its spacing stays out");

    // Goals across the town: a straight walk there would cross it.
    Check.That(!WildlifeRules.PathClear(new Vector2(-200, 0), new Vector2(200, 0), step, Inside), "a path across a sanctuary is not clear");
    Check.That(WildlifeRules.PathClear(new Vector2(-200, 170), new Vector2(200, 170), step, Inside), "a path past it is");
    WildlifeHerd drifting = Beside("mammoth", new Vector2(-200, 0), 7, "across");
    Vector2 started = drifting.Leader.Position;
    Check.Equal(0, Intrusions(drifting, 900, Morning, _ => null), "a herd drifting through a day with a town in its range never enters it");
    Check.That(Vector2.Distance(drifting.Leader.Position, started) > 20, "and it is not stuck at the edge");

    // Fleeing straight at the town: the band goes round.
    WildlifeHerd band = Beside("hipparion", new Vector2(190, 0), 8, "flight");
    Vector2 pressing = band.Centre + new Vector2(20, 0);
    Check.Equal(0, Intrusions(band, 30, Morning, _ => pressing), "a band bolting toward a town turns along its edge");
    Check.That(Vector2.Distance(band.Centre, pressing) > 40, "and still gets away");

    // Charging a player who stands inside: it stops at the edge.
    WildlifeHerd pair = Beside("entelodont", new Vector2(170, 0), 2, "charge");
    Vector2 safe = new(Radius - 5, 0);
    Check.Equal(0, Intrusions(pair, 20, Morning, _ => safe), "a charge stops at the sanctuary's edge");

    // A town founded on an animal: it may walk out, and it does not stay caught.
    WildlifeHerd caught = Herd("hipparion", new Vector2(150, 0), 1, "caught");
    Vector2 behind = new(130, 0);
    Intrusions(caught, 30, Morning, _ => behind);
    Check.That(!Inside(caught.Leader.Position), "a pressed animal already inside can flee out");
    WildlifeHerd calm = Herd("hipparion", new Vector2(150, 0), 1, "calm-inside");
    // It keeps its day on the way, resting and all, so it is given one; once out, it stays out.
    for (int hour = 0; hour < 24 && Inside(calm.Leader.Position); hour++)
    {
        Intrusions(calm, 50, Morning with { DayFraction = ((6.5 + hour) % 24) / 24 }, _ => null);
    }
    Check.That(!Inside(calm.Leader.Position), "a calm animal already inside walks out too");
    Check.Equal(0, Intrusions(calm, 120, Morning with { DayFraction = 19.0 / 24 }, _ => null), "and it does not walk back in");
});

Check.Section("population", () =>
{
    const double RecoveryDays = 12;
    HerdRecord herd = new("ledger", "ground-sloth", Vector2.Zero, 6);
    Check.Equal(6, herd.Alive(0, RecoveryDays), "a herd starts at its founding size");
    herd.Kill(10, RecoveryDays);
    Check.Equal(5, herd.Alive(10, RecoveryDays), "a kill takes one from the herd");
    Check.Equal(5, herd.Alive(21.9, RecoveryDays), "nothing comes back before the recovery days are up");
    Check.Equal(6, herd.Alive(22, RecoveryDays), "one comes back after the recovery days");
    herd.Kill(14, RecoveryDays);
    herd.Kill(15, RecoveryDays);
    Check.Equal(3, herd.Alive(15, RecoveryDays), "three kills in a few days leave three");
    Check.Equal(4, herd.Alive(22, RecoveryDays), "recovery counts on from the first loss, one at a time");
    Check.Equal(5, herd.Alive(34, RecoveryDays), "a second comes back a recovery period later");
    Check.Equal(6, herd.Alive(1000, RecoveryDays), "a herd never recovers past its founding size");
    HerdRecord wiped = new("wiped", "glyptodon", Vector2.Zero, 2);
    wiped.Kill(1, RecoveryDays);
    wiped.Kill(1, RecoveryDays);
    wiped.Kill(1, RecoveryDays);
    Check.Equal(0, wiped.Alive(1, RecoveryDays), "a herd killed out has none, and a further kill takes nothing");
    Check.Equal(1, wiped.Alive(13, RecoveryDays), "even a herd killed out comes back, slowly");

    // A session's losses carry to the next: a new ledger over the same world holds the same herds, as hunted.
    MapBiome Grass(double x, double z) => MapBiome.Grassland;
    double size = catalog.Placement.RegionMetres;
    WildlifeLedger first = new(catalog, Seed, Grass);
    List<HerdRecord> near = [.. first.HomesWithin(0, 0, size * 4)];
    HerdRecord hunted = near[0], untouched = near[1];
    double recovery = catalog.Kind(hunted.KindId).Population.RecoveryDays;
    hunted.Kill(100, recovery);
    hunted.Kill(100.5, recovery);
    IReadOnlyList<HerdLoss> saved = first.Losses(101);
    Check.Equal(1, saved.Count, "only a herd short of animals is saved");
    Check.Equal(HerdLoss.IdOf(hunted.Key), saved[0].Herd, "the saved loss names the hunted herd");

    WildlifeLedger second = new(catalog, Seed, Grass);
    second.Restore(saved);
    Check.Equal(saved.Count, second.Losses(101).Count, "a loss in a region not yet seeded is kept for the next save");
    HerdRecord again = second.HomesWithin(0, 0, size * 4).First(herd => herd.Key == hunted.Key);
    Check.Equal(hunted.Founding - 2, again.Alive(101, recovery), "a hunted herd is still hunted next session");
    Check.Equal(hunted.Founding - 1, again.Alive(100 + recovery, recovery), "and its recovery runs on from the same day");
    Check.Equal(hunted.Founding, again.Alive(100 + (2 * recovery), recovery), "until it is whole");
    Check.Equal(untouched.Founding, second.HomesWithin(0, 0, size * 4).First(herd => herd.Key == untouched.Key).Alive(101, recovery), "other herds are untouched");
    Check.Equal(0, second.Losses(100 + (2 * recovery)).Count, "a herd that has recovered drops out of the save");

    WildlifeLedger other = new(catalog, Seed, Grass);
    other.Place(catalog.Kind("ground-sloth"), Vector2.Zero, 4).Kill(1, recovery);
    Check.Equal(0, other.Losses(1).Count, "a herd placed by live debug is in no population and is not saved");
});

Check.Section("cave animals", () =>
{
    // #10876: each zone and community of the cave-wildlife page has its hunter, kept out of the overworld's regions.
    Dictionary<CaveHabitat, string[]> lives = new()
    {
        [CaveHabitat.Mouth] = ["root-adder"],
        [CaveHabitat.DampDeadEnds] = ["mouldback"],
        [CaveHabitat.Pools] = ["drowner"],
        [CaveHabitat.CushionBarrens] = ["hummock"],
        [CaveHabitat.RibbonThicket] = ["ribbon-stalker"],
        [CaveHabitat.FunnelGarden] = ["stalk-bill"],
        [CaveHabitat.InvertedGrove] = ["drop-sloth"],
        [CaveHabitat.VeilForest] = ["draft-hound"],
        [CaveHabitat.IvoryHeath] = ["salt-monitor"],
        [CaveHabitat.GlassSteppe] = ["glass-courser"],
        [CaveHabitat.VentOrchard] = ["seep-rat", "plume-neck"],
    };
    WildlifeKind[] caveKinds = [.. catalog.Kinds.Where(kind => kind.Cave is not null)];
    foreach ((CaveHabitat habitat, string[] ids) in lives)
    {
        Check.That(caveKinds.Where(kind => kind.Cave!.Lives == habitat).Select(kind => kind.Id).Order().SequenceEqual(ids.Order()), $"{habitat} holds {string.Join(" and ", ids)}");
    }

    foreach (WildlifeKind kind in caveKinds)
    {
        Check.That(kind.Pressed == PressedResponse.Hunt && kind.Hunt is not null, $"{kind.Id} hunts");
        Check.That(Enum.GetValues<MapBiome>().All(biome => kind.HabitatWeight(biome) == 0) && kind.Population.HerdsPerSquareKilometre == 0, $"{kind.Id} lives in no overworld region");
        Check.That(kind.Hunt!.Rest is { } rest && rest.WakeMetres > kind.Hunt.ReachMetres && rest.WakeMetres <= kind.Hunt.SightMetres, $"{kind.Id} rests and wakes within its sight");
        Check.That(kind.Hunt.Display is { Seconds: > 0 } && kind.Hunt.Search is { Seconds: > 0 }, $"{kind.Id} displays before it attacks and searches for a player it lost");
        CreatureKind creature = HunterRules.KindOf(kind);
        Check.That(creature.Tuning.WakeRange == kind.Hunt.Rest!.WakeMetres && creature.Tuning.SearchTicks > 0, $"{kind.Id}'s rest and search reach the creature rules");
    }

    // The ruins' hunters (#10956) live under the world as the cave's do, so they rest, display and search too.
    string[] overworldHunters = ["gaunt", "axe-beak", "drift-runner"];
    Check.That(catalog.Kinds.Where(kind => overworldHunters.Contains(kind.Id))
            .All(kind => kind.Hunt!.Rest is null && kind.Hunt.Display is null && kind.Hunt.Search is null),
        "the overworld's hunters keep their own ways: no rest, display or search of a cave's");
    Check.That(catalog.CaveWakeMetres > 0 && catalog.CaveWakeMetres < 96, "a cave home wakes inside the distance the encounter director lets creatures go at");

    // A cave with dead ends, a pool, the veil forest and the vent orchard, and a mouth; no cushion barrens.
    Vector3[] veil = [new(10, -40, 10), new(30, -40, 10), new(50, -40, 12), new(70, -41, 10)];
    Vector3[] vents = [new(10, -80, 60), new(25, -80, 60), new(40, -80, 60), new(55, -80, 60), new(70, -80, 60)];
    Vector3[] deadEnds = [new(5, -30, 40), new(8, -30, 42), new(40, -35, 40)];
    CavePool pool = new(-50f, [new Vector2(20.5f, 80.5f), new Vector2(21.5f, 80.5f), new Vector2(22.5f, 80.5f), new Vector2(21.5f, 81.5f)]);
    Vector3[] mouth = [new(0, 2, -20), new(0, 1, -14), new(0, 0, -8)];
    Vector3[] deep = [new(60, -85, 30), new(80, -88, 30)];
    CaveSites sites = new("cave:1,2", mouth, deadEnds, [pool], deep, new Dictionary<CaveHabitat, IReadOnlyList<Vector3>>
    {
        [CaveHabitat.VeilForest] = veil,
        [CaveHabitat.VentOrchard] = vents,
    });
    IReadOnlyList<HerdRecord> homes = CaveFauna.Homes(sites, catalog, Seed);
    Check.That(homes.All(home => home.Floor is not null), "every cave home carries its floor's height");

    // #10878: the draft-hound rests into the cave's draft, which the veil forest's fans show.
    Check.That(catalog.Kind("draft-hound").Hunt!.Rest!.DownwindShare is > 0 and < 1, "the draft-hound rests into the draft and hears less from downwind");
    Check.That(homes.All(home => home.Upwind is null), "with no draft in a cave, no home faces one");
    Vector2 east = Vector2.UnitX;
    CaveSites drafty = sites with { Drafts = [.. veil.Select(fan => (new Vector2(fan.X, fan.Z - 3), east))] };
    IReadOnlyList<HerdRecord> draftHomes = CaveFauna.Homes(drafty, catalog, Seed);
    Check.That(draftHomes.Where(home => home.KindId == "draft-hound").All(home => home.Upwind == east)
        && draftHomes.Any(home => home.KindId == "draft-hound"), "a draft-hound home in the veil forest faces the way the draft comes from");
    Check.That(draftHomes.Where(home => home.KindId != "draft-hound").All(home => home.Upwind is null), "a kind that ignores the draft faces none");
    Check.Equal((Vector2?)null, drafty.UpwindAt(new Vector3(10, -40, 10 + CaveSites.DraftReachMetres + 5)), "past the fans' reach there is no draft");
    double Share(Vector2 player) => HunterRules.WakeShare(Vector2.Zero, player, east, 0.3);
    Check.That(Math.Abs(Share(new Vector2(10, 0)) - 1) < 1e-9, "a player coming down the draft is heard at the whole wake range");
    Check.That(Math.Abs(Share(new Vector2(-10, 0)) - 0.3) < 1e-9, "one coming from downwind, at the downwind share");
    Check.That(Math.Abs(Share(new Vector2(0, 10)) - 0.65) < 1e-9, "one from the side, between the two");
    Check.Equal(1d, HunterRules.WakeShare(Vector2.Zero, new Vector2(-10, 0), null, 0.3), "with no draft at its home, it hears all round");
    Check.That(Math.Abs(HunterRules.HeadingDegrees(east) - 90) < 1e-9 && Math.Abs(HunterRules.HeadingDegrees(-Vector2.UnitY)) < 1e-9, "headings count as yaw does: 0 faces -z, 90 faces +x");
    Check.That(homes.All(home => catalog.Kind(home.KindId).Cave is not null), "only cave kinds live in a cave");
    Check.That(!homes.Any(home => home.KindId is "hummock" or "ribbon-stalker" or "stalk-bill" or "drop-sloth" or "salt-monitor" or "glass-courser"),
        "a cave without a community holds none of that community's hunters");
    bool At(HerdRecord home, IEnumerable<Vector3> points) => points.Any(p => p.X == home.Home.X && p.Z == home.Home.Y && p.Y == home.Floor);
    Check.That(homes.Where(home => home.KindId == "draft-hound").All(home => At(home, veil)) && homes.Any(home => home.KindId == "draft-hound"), "the draft-hounds live in the veil forest");
    Check.That(homes.Where(home => home.KindId is "seep-rat" or "plume-neck").All(home => At(home, vents)), "the seep-rats and plume-necks live in the vent orchard");
    Check.That(homes.Any(home => home.KindId == "seep-rat") && homes.Any(home => home.KindId == "plume-neck"), "and both of its hunters live there");
    Check.That(homes.Where(home => home.KindId == "mouldback").All(home => At(home, deadEnds)) && homes.Any(home => home.KindId == "mouldback"), "the mouldbacks live in the damp dead ends");
    Check.That(homes.Where(home => home.KindId == "root-adder").All(home => At(home, mouth)) && homes.Count(home => home.KindId == "root-adder") == 1, "one root-adder lives in the mouth's twilight");
    Check.That(homes.Where(home => home.KindId == "drowner").All(home => home.Floor == pool.Surface) && homes.Count(home => home.KindId == "drowner") == 1, "a drowner lives in the pool");
    foreach (IGrouping<string, HerdRecord> kind in homes.GroupBy(home => home.KindId))
    {
        WildlifeKind k = catalog.Kind(kind.Key);
        Check.That(kind.Count() <= k.Cave!.HomesPerCave, $"{kind.Key} keeps to its number of homes in a cave");
        HerdRecord[] its = [.. kind];
        Check.That(its.All(a => its.All(b => a == b || Vector2.Distance(a.Home, b.Home) >= CaveFauna.HomesApartMetres - 0.01f)), $"{kind.Key}'s homes stand apart");
        Check.That(its.All(home => home.Founding >= k.GroupMinimum && home.Founding <= k.GroupMaximum), $"{kind.Key}'s groups are of its size");
    }

    Check.That(homes.Select(home => (home.Key, home.Home, home.Floor, home.Founding)).SequenceEqual(CaveFauna.Homes(sites, catalog, Seed).Select(home => (home.Key, home.Home, home.Floor, home.Founding))),
        "the same cave gives the same homes");
    Check.That(homes.Select(home => home.Key).Distinct().Count() == homes.Count, "each home has its own key");

    // The ledger holds a cave's homes like a region's: found near the player, and a loss kept while the cave is let go.
    WildlifeLedger ledger = new(catalog, Seed, (_, _) => MapBiome.Grassland);
    ledger.AddCave(sites.Key, homes);
    Check.That(ledger.HomesWithin(30, 10, 40).Any(home => home.KindId == "draft-hound"), "a cave home is found near the player");
    // R10876-3: a hunter still holds its home's record while its cave is let go (the player in a dungeon, or gone far off) and
    // taken up again from a fresh seeding. Its death must count against the record the ledger finds and saves.
    HerdRecord hound = homes.First(home => home.KindId == "draft-hound");
    double recovery = catalog.Kind("draft-hound").Population.RecoveryDays;
    ledger.RemoveCave(sites.Key);
    Check.That(!ledger.HomesWithin(30, 10, 40).Any(home => home.KindId == "draft-hound"), "a cave let go holds no homes near the player");
    IReadOnlyList<HerdRecord> again = CaveFauna.Homes(sites, catalog, Seed);
    IReadOnlyList<HerdRecord> held = ledger.AddCave(sites.Key, again);
    Check.That(held.Contains(hound) && !held.Intersect(again).Any(), "a cave taken up again takes back its own records, not the fresh seeding's");
    HerdRecord found = ledger.HomesWithin(30, 10, 40).First(home => home.Key == hound.Key);
    Check.That(ReferenceEquals(found, hound), "the home found near the player is the very record its hunter holds");
    for (int i = 0; i < hound.Founding; i++) hound.Kill(10, recovery);
    Check.Equal(0, found.Alive(10, recovery), "the hunter's deaths count against the ledger's record: none left, so no replacement wakes");
    Check.That(ledger.Losses(10).Any(loss => loss.Herd == HerdLoss.IdOf(hound.Key) && loss.Lost == hound.Founding), "and the saved losses carry them");
    ledger.RemoveCave(sites.Key);
    Check.That(ledger.Losses(10).Any(loss => loss.Herd == HerdLoss.IdOf(hound.Key) && loss.Lost == hound.Founding), "a cave let go still has its losses saved");
    HerdRecord dormantKill = homes.First(home => home.KindId == "mouldback");
    dormantKill.Kill(10, catalog.Kind("mouldback").Population.RecoveryDays);
    Check.That(ledger.Losses(10).Any(loss => loss.Herd == HerdLoss.IdOf(dormantKill.Key)), "a death while its cave is let go is saved too");
    ledger.AddCave(sites.Key, CaveFauna.Homes(sites, catalog, Seed));
    Check.Equal(dormantKill.Founding - 1, ledger.HomesWithin(5, 40, 40).First(home => home.Key == dormantKill.Key).Alive(10, catalog.Kind("mouldback").Population.RecoveryDays),
        "and the cave taken up again counts it");
    // #10924: a cave regenerated afresh for a test forgets its records, held or kept, and takes the fresh seeding's.
    ledger.RemoveCave(sites.Key);
    ledger.ForgetCave(sites.Key);
    IReadOnlyList<HerdRecord> fresh = CaveFauna.Homes(sites, catalog, Seed);
    Check.That(ReferenceEquals(ledger.AddCave(sites.Key, fresh), fresh), "a cave forgotten while let go takes the fresh homes when taken up again");
    ledger.ForgetCave(sites.Key);
    Check.That(!ledger.Caves.Contains(sites.Key) && !ledger.CaveHomes.Any(), "a held cave forgotten holds no homes");
    // A loss saved before this session is dropped by a cave forgotten afresh, and taken up by one seeded as usual.
    WildlifeLedger saved = new(catalog, Seed, (_, _) => MapBiome.Grassland);
    saved.Restore([new HerdLoss(HerdLoss.IdOf(hound.Key), hound.Founding, 10)]);
    saved.ForgetCave(sites.Key);
    saved.AddCave(sites.Key, CaveFauna.Homes(sites, catalog, Seed));
    Check.Equal(hound.Founding, saved.CaveHomes.First(home => home.Key == hound.Key).Alive(10, recovery), "a cave forgotten afresh comes back whole, its saved losses dropped");
    Check.That(!saved.Losses(10).Any(loss => loss.Herd == HerdLoss.IdOf(hound.Key)), "and those losses are no longer saved");
    WildlifeLedger usual = new(catalog, Seed, (_, _) => MapBiome.Grassland);
    usual.Restore([new HerdLoss(HerdLoss.IdOf(hound.Key), hound.Founding, 10)]);
    usual.AddCave(sites.Key, CaveFauna.Homes(sites, catalog, Seed));
    Check.Equal(0, usual.CaveHomes.First(home => home.Key == hound.Key).Alive(10, recovery), "a cave seeded as usual takes up its saved losses");

    // The small creatures (#10876; Patch: no colliders, no combat): none is a wildlife kind, so no body, capsule or swing finds one.
    Check.That(catalog.CaveAmbient.Select(kind => kind.Id).Order().SequenceEqual(new[] { "blindfish", "drift-grazer", "mound-shrew" }), "the three small cave creatures load");
    Check.That(catalog.CaveAmbient.All(kind => !catalog.TryKind(kind.Id, out _)), "a small cave creature is not a herd or a hunter: it has no capsule, collider or combat");
    IReadOnlyList<CaveAmbientHome> ambient = CaveFauna.Ambient(sites, catalog, Seed);
    Check.That(ambient.Where(home => home.Kind.Id == "blindfish").All(home => home.Pool == pool) && ambient.Any(home => home.Kind.Id == "blindfish"), "blindfish school in the pools");
    Check.That(ambient.Where(home => home.Kind.Id == "drift-grazer").All(home => deep.Contains(home.Home)) && ambient.Any(home => home.Kind.Id == "drift-grazer"), "drift-grazers wander the deep floors");
    Check.That(!ambient.Any(home => home.Kind.Id == "mound-shrew"), "a cave without the inverted grove has no mound-shrews");

    // They wander, and slip away from a player who comes near, hiding while the player stays.
    int ambientIds = 5000;
    CaveAmbience ambience = new(Seed, (x, z, from) => -88f);
    Check.Equal(0, ambience.Add(ambient, () => ++ambientIds).Count, "every small creature finds its floor");
    CaveAmbient grazer = ambience.Creatures.First(c => c.Home.Kind.Id == "drift-grazer");
    Vector3 far = new(500, 0, 500);
    Vector2 began = grazer.Member.Position;
    for (int i = 0; i < 600; i++) ambience.Update(far, Tick);
    Check.That(Vector2.Distance(began, grazer.Member.Position) > 0.1f, "a small creature moves about on its own");
    Check.That(ambience.Creatures.All(c => c.Home.Pool is not CavePool p || p.Columns.Any(col => Vector2.Distance(col, c.Member.Position) <= 1.01f)), "a fish keeps to its pool");
    Vector3 near = new(grazer.Member.Position.X + 1, grazer.Feet, grazer.Member.Position.Y);
    Vector2 before = grazer.Member.Position;
    ambience.Update(near, Tick);
    Check.That(Vector2.Distance(grazer.Member.Position, new Vector2(near.X, near.Z)) > Vector2.Distance(before, new Vector2(near.X, near.Z)), "a small creature runs from the player");
    for (int i = 0; i < 100; i++) ambience.Update(near, Tick);
    Check.That(grazer.Member.Under, "having run a while it slips out of sight");
    for (int i = 0; i < 5; i++) ambience.Update(far, Tick);
    Check.That(!grazer.Member.Under, "and comes out once the player has gone");
    Check.Equal(ambience.Creatures.Count, ambience.Remove(c => c.Home.Cave == sites.Key).Count, "a cave let go takes its small creatures with it");

    // Hunter rules for a cave's ways: a display first, however near; then its search and rest shown.
    WildlifeKind hummock = catalog.Kind("hummock");
    Hunter Of(WildlifeKind kind) => new(kind, new HerdRecord("cave@test", kind.Id, Vector2.Zero, 1),
        new WildlifeMember(1, Vector2.Zero, Vector2.Zero, CombatantState.Fresh(kind.MaximumHealth, kind.Defence)));
    Hunter displaying = Of(hummock);
    HunterRules.Advance(displaying, CreatureState.Attacking, 1, 9, 1d / 60);
    Check.That(displaying.Holding && Math.Abs(displaying.ThreatSeconds - hummock.Hunt!.Display!.Seconds) < 1e-9, "a cave hunter shows its display for its own while, even with the player in reach");
    Check.Equal(hummock.Hunt!.Display!.Clip, HunterRules.Want(displaying, CreatureState.Attacking, 0, 3), "showing its display's clip");
    Hunter idler = Of(hummock);
    Check.Equal(hummock.Hunt!.Rest!.Clip, HunterRules.Want(idler, CreatureState.Resting, 0, 3), "at rest it shows its rest");
    Check.Equal(hummock.Hunt!.Search!.Clip, HunterRules.Want(idler, CreatureState.Searching, 0, 3), "searching it shows its search");
    Hunter overworld = Of(catalog.Kind("gaunt"));
    HunterRules.Advance(overworld, CreatureState.Attacking, 1, 9, 1d / 60);
    Check.That(!overworld.Holding && !overworld.Threatened, "an overworld hunter in reach bites without a threat, as before");

    // The drop-sloth (#10877): it hangs from a ceiling high enough, drops under gravity when woken, and only climbs back unseen.
    WildlifeKind dropSloth = catalog.Kind("drop-sloth");
    Check.That(dropSloth.Hunt!.Rest!.Hangs, "the drop-sloth rests hanging");
    Hunter hanger = Of(dropSloth);
    hanger.CeilingLift = 5f;
    HunterRules.Hang(hanger, CreatureState.Resting, playerOutOfSight: true, Tick);
    Check.That(hanger.Hangs && Math.Abs(hanger.Member.Lift - 5f) < 1e-6, "under a high ceiling it hangs from it");
    Check.Equal(WildlifeClipRole.Hang, HunterRules.Want(hanger, CreatureState.Resting, 0, 3), "hanging, it shows its hang");
    HunterRules.Hang(hanger, CreatureState.Alert, playerOutOfSight: false, Tick);
    Check.That(!hanger.Hangs && hanger.Member.Lift < 5f && hanger.Member.Lift > 0, "woken, it lets go");
    Check.Equal(WildlifeClipRole.Drop, HunterRules.Want(hanger, CreatureState.Alert, 0, 3), "falling, it shows its drop");
    Check.That(hanger.Holding, "falling, it does not walk");
    int fallTicks = 1;
    while (hanger.Member.Lift > 0 && fallTicks < 600) { HunterRules.Hang(hanger, CreatureState.Pursuing, false, Tick); fallTicks++; }
    double fallSeconds = fallTicks * Tick, freeFall = Math.Sqrt(2 * 5 / HunterRules.FallAcceleration);
    Check.That(hanger.Member.Lift == 0 && Math.Abs(fallSeconds - freeFall) < 0.05, $"it lands in about the time a fall from 5 m takes ({fallSeconds:F2} s)");
    Check.That(!hanger.Holding && HunterRules.Want(hanger, CreatureState.Pursuing, 0, 3) != WildlifeClipRole.Drop, "landed, it hunts");
    Check.Equal(0f, HunterRules.BodyOffFloor(hanger), "landed, its body is on its floor, where a swing measures from (R10877-2)");
    Hunter overhead = Of(dropSloth);
    overhead.CeilingLift = 5f;
    HunterRules.Hang(overhead, CreatureState.Resting, playerOutOfSight: true, Tick);
    float hangingOff = HunterRules.BodyOffFloor(overhead);
    Check.That(Math.Abs(hangingOff - (5f - (float)dropSloth.Body.HeightMetres)) < 1e-5, $"hanging 5 m up, the lowest of its body is {hangingOff:F2} m over its floor (R10877-2)");
    HunterRules.Hang(overhead, CreatureState.Alert, playerOutOfSight: false, Tick);
    float fallingOff = HunterRules.BodyOffFloor(overhead);
    Check.That(fallingOff > 0 && fallingOff < hangingOff, "falling, its body comes down toward its floor");
    HunterRules.Hang(hanger, CreatureState.Resting, playerOutOfSight: false, Tick);
    Check.That(!hanger.Hangs && hanger.Member.Lift == 0, "at rest again in the player's sight, it stays on the floor");
    HunterRules.Hang(hanger, CreatureState.Resting, playerOutOfSight: true, Tick);
    Check.That(hanger.Hangs, "out of their sight, it is back up");
    Hunter low = Of(dropSloth);
    low.CeilingLift = HunterRules.LowestHangMetres - 0.5f;
    HunterRules.Hang(low, CreatureState.Resting, playerOutOfSight: true, Tick);
    Check.That(!low.Hangs && low.Member.Lift == 0, "under a low ceiling it rests on the floor");
    Hunter mouldback = Of(catalog.Kind("mouldback"));
    Check.Equal(WildlifeClipRole.Lie, HunterRules.Want(mouldback, CreatureState.Resting, 0, 3), "the mouldback lies flat at rest");
    WildlifeKind draftHound = catalog.Kind("draft-hound");
    Check.Equal(WildlifeClipRole.Search, HunterRules.Want(Of(draftHound), CreatureState.Searching, 0, 3), "a searching draft-hound flares its ear-fans (#10878)");
    Check.That(draftHound.Mesh!.Clips.ContainsKey(WildlifeClipRole.Search), "the draft-hound's body has a search clip");
    Check.Equal(WildlifeClipRole.Lie, HunterRules.Want(Of(catalog.Kind("glass-courser")), CreatureState.Resting, 0, 3), "the glass-courser lies flat at rest (#10878)");
    // #10879: the drowner rests low in its pool's shallows and strikes with its lunge; the salt-monitor rests belly-down.
    WildlifeKind drowner = catalog.Kind("drowner");
    Check.Equal(WildlifeClipRole.Swim, HunterRules.Want(Of(drowner), CreatureState.Resting, 0, 3), "the drowner rests lying low in its pool (#10879)");
    Check.That(drowner.Mesh!.Clips.TryGetValue(WildlifeClipRole.Bite, out string? strike) && strike == "lunge", "the drowner's bite is its lunge from the water");
    Check.That(drowner.Cave!.Lives == CaveHabitat.Pools, "the drowner lives in pools, whose homes are their middles");
    WildlifeKind saltMonitor = catalog.Kind("salt-monitor");
    Check.Equal(WildlifeClipRole.Lie, HunterRules.Want(Of(saltMonitor), CreatureState.Resting, 0, 3), "the salt-monitor lies belly-down at rest (#10879)");
    Check.That(drowner.Body.SizeClass == WildlifeSizeClass.Broad && saltMonitor.Body.SizeClass == WildlifeSizeClass.Broad, "both route as broad bodies: wide and low");

    // #10880: the hummock hides under its dome at rest; a seep-rat colony rests packed and hunts as a pack.
    WildlifeKind domed = catalog.Kind("hummock");
    Check.Equal(WildlifeClipRole.Lie, HunterRules.Want(Of(domed), CreatureState.Resting, 0, 3), "the hummock rests lying (#10880)");
    Check.That(domed.Mesh!.Clips.TryGetValue(WildlifeClipRole.Lie, out string? hide) && hide == "hide", "and its rest is hiding under its dome");
    Check.That(domed.Cave!.Lives == CaveHabitat.CushionBarrens && domed.Body.SizeClass == WildlifeSizeClass.Broad, "it lives in the cushion barrens and routes as a broad body");
    WildlifeKind seepRat = catalog.Kind("seep-rat");
    Check.Equal(WildlifeClipRole.Lie, HunterRules.Want(Of(seepRat), CreatureState.Resting, 0, 3), "a seep-rat lies belly-down at rest (#10880)");
    Check.That(seepRat.Hunt!.Pack is not null && seepRat.GroupMinimum >= 4, "seep-rats live as a colony of four or more and hunt as a pack");
    Check.That(seepRat.SpacingMetres < seepRat.Body.LengthMetres, "a colony rests packed closer than a body's length");
    Check.That(seepRat.Cave!.Lives == CaveHabitat.VentOrchard && seepRat.Body.SizeClass == WildlifeSizeClass.Small, "they live in the vent orchard and route as small bodies");

    // #10881: the ribbon-stalker and the stalk-bill rest standing still; the stalk-bill stalks and spears.
    WildlifeKind ribbonStalker = catalog.Kind("ribbon-stalker");
    Check.Equal(WildlifeClipRole.Lie, HunterRules.Want(Of(ribbonStalker), CreatureState.Resting, 0, 3), "the ribbon-stalker rests on its rest clip (#10881)");
    Check.That(ribbonStalker.Mesh!.Clips.TryGetValue(WildlifeClipRole.Lie, out string? hangs) && hangs == "stand", "and its rest is standing still like a hanging ribbon");
    WildlifeKind stalkBill = catalog.Kind("stalk-bill");
    Check.That(stalkBill.Mesh!.Clips.TryGetValue(WildlifeClipRole.Lie, out string? stalkRest) && stalkRest == "stand", "the stalk-bill rests standing still as a stalk (#10881)");
    Check.That(stalkBill.Mesh.Clips.TryGetValue(WildlifeClipRole.Walk, out string? stalks) && stalks == "stalk", "it walks at a slow stalk");
    Check.That(stalkBill.Mesh.Clips.TryGetValue(WildlifeClipRole.Bite, out string? spears) && spears == "spear", "and its bite is the spearing strike");
    Check.That(ribbonStalker.Cave!.Lives == CaveHabitat.RibbonThicket && stalkBill.Cave!.Lives == CaveHabitat.FunnelGarden, "each lives in its own community");

    // #10882: the plume-neck stands upright at rest and shows its flared ruff before it strikes.
    WildlifeKind plumeNeck = catalog.Kind("plume-neck");
    Check.That(plumeNeck.Mesh!.Clips.TryGetValue(WildlifeClipRole.Lie, out string? upright) && upright == "upright", "the plume-neck rests standing upright (#10882)");
    Hunter flaring = Of(plumeNeck);
    HunterRules.Advance(flaring, CreatureState.Attacking, 1, 9, 1d / 60);
    Check.That(flaring.Holding, "it holds its display before it strikes, even with the player in reach");
    Check.Equal(WildlifeClipRole.Threat, HunterRules.Want(flaring, CreatureState.Attacking, 0, 3), "and its display is its threat, the ruff flared");
    Check.That(plumeNeck.Cave!.Lives == CaveHabitat.VentOrchard, "it lives in the vent orchard");
    WildlifeBreath miasma = plumeNeck.Mesh!.Breath ?? throw new InvalidDataException("the plume-neck breathes a miasma");
    Check.That(miasma.Crossed(WildlifeClipRole.Threat, 0f, 1f) >= 2 && miasma.Crossed(WildlifeClipRole.Bite, 0f, 1f) >= 1, "its miasma leaves its mouth in its roar and with its bite");
    Check.Equal(0, miasma.Crossed(WildlifeClipRole.Walk, 0f, 1f), "and never as it walks");
    Check.Equal(miasma.Crossed(WildlifeClipRole.Threat, 0f, 1f), miasma.Crossed(WildlifeClipRole.Threat, 0f, 0.5f) + miasma.Crossed(WildlifeClipRole.Threat, 0.5f, 1f), "a clip's breaths are let out once each, however its updates fall");
    Check.That(catalog.Kind("salt-monitor").Mesh!.Breath is null, "a kind without a breath lets none out");

    // #10883: the root-adder hangs from a ceiling at rest, drops, and on the ground rests coiled.
    WildlifeKind rootAdder = catalog.Kind("root-adder");
    Check.That(rootAdder.Hunt!.Rest is { Hangs: true }, "the root-adder hangs from a ceiling at rest (#10883)");
    Check.That(rootAdder.Mesh!.Clips.ContainsKey(WildlifeClipRole.Hang) && rootAdder.Mesh.Clips.ContainsKey(WildlifeClipRole.Drop), "and has its hang and its drop");
    Check.That(rootAdder.Mesh.Clips.TryGetValue(WildlifeClipRole.Lie, out string? coiled) && coiled == "lie", "on the ground it rests coiled");
    Check.That(rootAdder.Cave!.Lives == CaveHabitat.Mouth, "it lives in a cave's mouth");

    // #10884: the small cave creatures have bodies that stand, wander and run off, and nothing to fight with.
    // How far a small creature's drawn length may stray from its design's, as a share of it.
    const double DrawnLengthTolerance = 0.03;
    foreach (string small in new[] { "blindfish", "drift-grazer", "mound-shrew" })
    {
        CaveAmbientKind smallKind = catalog.CaveAmbient.Single(kind => kind.Id == small);
        Check.That(smallKind.Mesh is { } body && WildlifeKinds.AmbientClips.All(body.Clips.ContainsKey) && body.Ragdoll is null, $"the {small} has a body that idles, walks and runs, and no ragdoll (#10884)");
        Check.That(catalog.Kinds.All(kind => kind.Id != small), $"the {small} is not a hunter or a herd animal: nothing hunts it, nothing it fights");
        // Review R10884-1: the design's size is the length drawn, the mesh's own length at its scale.
        double drawn = GlbLongestExtent($"Wildlife.{small}.glb") * smallKind.Mesh!.Scale;
        Check.That(Math.Abs(drawn - smallKind.Body.LengthMetres) <= DrawnLengthTolerance * smallKind.Body.LengthMetres,
            $"the {small} is drawn {drawn:0.000} m long, its design's {smallKind.Body.LengthMetres} m");
    }

});

// Creature behaviour debugging (#10926): a test aid's hold on a herd animal, read by the rules, and its release.
Check.Section("ruin wildlife", () =>
{
    // The main ruin inhabitant (#10956): a hunter of no overworld region and no cave, wearing a shell as equipment.
    WildlifeKind collector = catalog.Kind("collector");
    Check.That(Enum.GetValues<MapBiome>().All(biome => collector.HabitatWeight(biome) == 0) && collector.Population.HerdsPerSquareKilometre == 0,
        "the collector lives in no overworld region");
    Check.That(collector.Hunt!.Rest is { } rest && rest.WakeMetres > collector.Hunt.ReachMetres && rest.WakeMetres <= collector.Hunt.SightMetres,
        "the collector rests and wakes within its sight");
    Check.That(collector.Hunt.Display is { Seconds: > 0 } && collector.Hunt.Search is { Seconds: > 0 }, "the collector displays before it attacks and searches for a player it lost");
    WildlifeWorn worn = collector.Mesh!.Worn ?? throw new InvalidOperationException("the collector wears nothing");
    Check.Equal("creatures/collector/shells/pieces.json", worn.Pieces, "its pieces come from the shell maker's file");
    Check.Equal("bare", worn.Pick(0.0), "the lowest share wears the first look");
    Check.Equal("full", worn.Pick(0.999), "the highest share wears the last look");
    Check.That(Enumerable.Range(0, 600).Select(i => worn.Pick(i / 600.0)).Distinct().SequenceEqual(["bare", "dome", "split", "full"]), "every look is worn by some share");
    Check.Equal(200, Enumerable.Range(0, 600).Count(i => worn.Pick(i / 600.0) == "dome"), "each look is worn by its weight's share");

    using JsonDocument pieces = JsonDocument.Parse(ReadResource("Wildlife.collector.pieces.json"));
    string[] joints = GlbJoints("Wildlife.collector.glb");
    JsonElement[] held = [.. pieces.RootElement.GetProperty("held").EnumerateObject().Select(piece => piece.Value)];
    foreach (JsonProperty look in pieces.RootElement.GetProperty("looks").EnumerateObject())
    {
        JsonElement[] all = [.. look.Value.EnumerateArray(), .. held];
        Check.That(all.Length <= WildlifeWorn.MostPieces, $"the {look.Name} look and what is held fit a body's ids");
        Check.That(all.All(piece => joints.Contains(piece.GetProperty("joint").GetString())), $"every piece of the {look.Name} look hangs from a joint of the collector's rig");
    }

    Check.That(pieces.RootElement.GetProperty("looks").EnumerateObject().Select(look => look.Name).SequenceEqual(["bare", "dome", "split", "full"]),
        "the pieces file offers Patch's four looks");
    Check.That(worn.Looks.All(look => pieces.RootElement.GetProperty("looks").TryGetProperty(look.Look, out _)), "every look the kind wears is in its pieces file");
    // The implement-carrier's crescent (#10957) carries a light that flares while it threatens or strikes.
    using JsonDocument carrierPieces = JsonDocument.Parse(ReadResource("Wildlife.carrier.pieces.json"));
    WildlifeHeldLight crescent = WildlifeHeldLight.Read(carrierPieces.RootElement.GetProperty("held").GetProperty("implement").GetProperty("light"));
    Check.That(crescent.Range > 0 && crescent.Intensity > 0 && crescent.ThreatShare > 1, "the carrier's crescent lights what is near, brighter in its threat");
    Check.That(catalog.Kind("skitterer").Mesh!.Worn is { } knife && knife.Looks.Count == 1, "the skitterer wears no shell of its own choosing: it holds its knife");
    using JsonDocument noLight = JsonDocument.Parse("""{ "at": [0, 0, 0], "colour": [1, 1, 1], "intensity": 0, "range": 1 }""");
    Check.Throws<InvalidDataException>(() => WildlifeHeldLight.Read(noLight.RootElement), "a held light with no intensity is refused");
    using JsonDocument noWeight = JsonDocument.Parse("""{ "worn": { "pieces": "p.json", "looks": { "bare": 0 } } }""");
    Check.Throws<InvalidDataException>(() => WildlifeWorn.Read(noWeight.RootElement), "looks with no weight between them are refused");
});

Check.Section("ruin tenders", () =>
{
    // The brood bearer (#10961): its threat splits its pods and releases a brood of small skitterers that guard it.
    WildlifeKind bearer = catalog.Kind("brood-bearer");
    WildlifeBrood brood = bearer.Hunt!.Brood ?? throw new InvalidOperationException("the bearer carries no brood");
    Check.That(bearer.Hunt.Tends && bearer.Hunt.Seals is null, "the bearer is a tender that seals nothing");
    Check.Equal("brood-skitterer", brood.Kind, "the bearer's brood is the small skitterers Patch picked");
    Check.Equal(6, brood.Count, "one small skitterer a pod");
    Check.Equal(1200.0, brood.RegrowSeconds, "the pods regrow over a game day");
    WildlifeKind hatchling = catalog.Kind("brood-skitterer");
    Check.That(hatchling.Hunt is { Pack: not null, Rest: null, Tends: false }, "the brood hunts as a pack, awake from the moment it spills out, and fights");
    Check.That(hatchling.MaximumHealth < catalog.Kind("skitterer").MaximumHealth && hatchling.Body.LengthMetres < catalog.Kind("skitterer").Body.LengthMetres,
        "a small skitterer is smaller and weaker than the skitterer");
    Check.Equal(0.0, hatchling.Population.HerdsPerSquareKilometre, "small skitterers come only from a bearer's pods");
    WildlifeWorn pods = bearer.Mesh!.Worn ?? throw new InvalidOperationException("the bearer wears no pods");
    Check.That(Enumerable.Range(0, 100).All(i => pods.Pick(i / 100.0) == "sealed"), "every bearer wakes with its pods sealed");
    using JsonDocument podPieces = JsonDocument.Parse(ReadResource("Wildlife.brood-bearer.pieces.json"));
    string[] bearerJoints = GlbJoints("Wildlife.brood-bearer.glb");
    Check.That(podPieces.RootElement.GetProperty("looks").TryGetProperty(brood.SplitLook, out JsonElement split) && split.GetArrayLength() > 0,
        "the pieces file has the split look the brood turns the pods to");
    Check.That(podPieces.RootElement.GetProperty("looks").EnumerateObject().SelectMany(look => look.Value.EnumerateArray())
        .All(piece => bearerJoints.Contains(piece.GetProperty("joint").GetString())), "the pods hang from a joint of the bearer's rig");

    // The mender (#10961): it seals the doorway nearest the player; a seal hardens, and takes more blows once hard.
    WildlifeKind mender = catalog.Kind("mender");
    WildlifeSealing sealing = mender.Hunt!.Seals ?? throw new InvalidOperationException("the mender seals nothing");
    Check.That(mender.Hunt.Tends && sealing.HardBlows >= 3 * sealing.FreshBlows - 1 && sealing.MostSeals == 2, "a hard seal takes about three times a fresh one's blows; two at most");
    Check.That(sealing.Seconds is >= 3 and <= 5 && sealing.HardenSeconds == 60 && sealing.RefillSeconds == 90, "about 4 s to seal, a minute to harden, 90 s to refill");

    // A doorway through x at (10, 2, 20): its opening runs 2 m along z.
    RuinDoorway door = new(new Vector3(10, 2, 20), FacesX: true);
    Check.That(TenderRules.Crosses(new Vector2(8, 20.5f), new Vector2(12, 20.5f), door), "a line through the opening crosses the doorway");
    Check.That(!TenderRules.Crosses(new Vector2(8, 23), new Vector2(12, 23), door), "a line past the opening's side does not");
    Check.That(!TenderRules.Crosses(new Vector2(8, 20), new Vector2(9.5f, 20), door), "a line that stops short of it does not");
    Check.That(TenderRules.Distance(new Vector2(10, 20.6f), door) < 1e-5, "a point in the opening is in it");
    Check.That(Math.Abs(TenderRules.Distance(new Vector2(10, 22), door) - 1) < 1e-5, "past the opening's side, the distance is to its edge");
    (Vector3 min, Vector3 max) = TenderRules.SealBox(door);
    Check.That(max.X - min.X < 0.5f && Math.Abs(max.Z - min.Z - RuinDoorway.Width) < 1e-5 && Math.Abs(max.Y - RuinDoorway.Height) < 1e-5,
        "a seal is thin through its doorway and fills its width and height");
    Vector2 stand = TenderRules.SealingPlace(door, new Vector2(4, 20));
    Check.That(stand.X < 10 && Math.Abs(stand.Y - 20) < 1e-5, "a mender seals from its own side of the doorway");
    RuinDoorway far = new(new Vector3(30, 2, 20), FacesX: false), upstairs = new(new Vector3(12, 8, 20), FacesX: true);
    Check.Equal(door, TenderRules.DoorToSeal([far, upstairs, door], new Vector3(12, 2, 20), [], 16), "it seals the doorway nearest the player on the player's storey");
    Check.That(TenderRules.DoorToSeal([door], new Vector3(12, 2, 20), [door], 16) is null, "not one already sealed");
    Check.That(TenderRules.DoorToSeal([far], new Vector3(0, 2, 20), [], 16) is null, "nor one out of reach of the player");

    Check.That(!TenderRules.Hard(sealing, 0) && TenderRules.BlowsToCut(sealing, 0) == sealing.FreshBlows, "a whole seal is fresh at first");
    Check.That(TenderRules.Hard(sealing, sealing.HardenSeconds) && TenderRules.BlowsToCut(sealing, sealing.HardenSeconds) == sealing.HardBlows, "after a minute it has hardened");

    // The chime grazer (#10962): disturbed, it rings for about 3 s and calls the ruin hunters within about 40 m; it can ring
    // again about 60 s after the last.
    WildlifeKind grazer = catalog.Kind("chime-grazer");
    WildlifeCalling calling = grazer.Hunt!.Calls ?? throw new InvalidOperationException("the grazer calls nothing");
    Check.That(grazer.Hunt.Tends && grazer.Hunt.Brood is null && grazer.Hunt.Seals is null, "the grazer is a tender that only calls");
    Check.That(grazer.Hunt.Display is { Seconds: >= 2.5 and <= 3.5 } && calling.ReachMetres == 40 && calling.AgainSeconds == 60,
        "its ring lasts about 3 s, reaches about 40 m, and comes again after about 60 s");
    Check.That(grazer.Hunt.Rest is { Clip: WildlifeClipRole.Lie } && grazer.Mesh!.Clips[WildlifeClipRole.Lie] == "graze", "it rests grazing");
    Check.That(TenderRules.Answers(catalog.Kind("collector")) && TenderRules.Answers(catalog.Kind("skitterer")) && TenderRules.Answers(catalog.Kind("brood-skitterer")),
        "ruin hunters answer its call");
    Check.That(!TenderRules.Answers(grazer) && !TenderRules.Answers(mender) && !TenderRules.Answers(bearer), "tenders do not");
    Check.That(catalog.Kinds.Where(kind => kind.Cave is not null).All(kind => !TenderRules.Answers(kind)), "nor does cave life, which keeps away from ruin life");
    Check.That(TenderRules.RingsAgain(calling, null) && !TenderRules.RingsAgain(calling, 30) && TenderRules.RingsAgain(calling, 60), "it rings at once, then not again within a minute");
    // A blow (R10962-1): one before it rang disturbs a rested grazer into its ring, one mid-ring cuts it, and one while it
    // rests from its last ring sends it away.
    Check.Equal(GrazerBlow.StartsRing, TenderRules.Struck(calling, ringing: false, sinceLastSeconds: null), "a first blow starts its ring");
    Check.Equal(GrazerBlow.StartsRing, TenderRules.Struck(calling, ringing: false, sinceLastSeconds: 90), "so does a blow once it has rested");
    Check.Equal(GrazerBlow.CutsRing, TenderRules.Struck(calling, ringing: true, sinceLastSeconds: null), "a blow mid-ring cuts it");
    Check.Equal(GrazerBlow.KeepsAway, TenderRules.Struck(calling, ringing: false, sinceLastSeconds: 30), "a blow while it rests sends it away");

    // A body faded into the mist (#10984): masked at its cutoff, its paint's alpha rising from just over the cutoff at the
    // tail to 1 at the head, each animal's own alpha choosing how much shows from the head back.
    using (JsonDocument faded = JsonDocument.Parse("""{ "fade": { "paint": "creatures/x/fade.png", "cutoff": 0.25, "rest": 0.3, "sweepSeconds": 0.6 } }"""))
    {
        WildlifeFade fade = WildlifeFade.Read(faded.RootElement) ?? throw new InvalidOperationException("no fade read");
        float Paint(float rank) => fade.Cutoff + WildlifeFade.TailMargin + ((1 - fade.Cutoff - WildlifeFade.TailMargin) * rank);
        bool Shows(float rank, float share) => Paint(rank) * fade.AlphaFor(share) >= fade.Cutoff - 1e-6f;
        Check.That(Shows(0, 1) && Shows(1, 1), "a whole body draws whole, tail and head");
        Check.That(fade.AlphaFor(0) == 0 && !Shows(1, 0), "none of it shows at nothing");
        Check.That(Shows(1, 0.3f) && Shows(0.75f, 0.3f) && !Shows(0.6f, 0.3f) && !Shows(0, 0.3f), "at a share, only the texels that near the head show");
        Check.That(Enumerable.Range(1, 9).Select(n => fade.AlphaFor(n / 10f)).Zip(Enumerable.Range(2, 9).Select(n => fade.AlphaFor(n / 10f))).All(p => p.First < p.Second), "more shows as its alpha rises");
        Check.That(fade.Toward(0.3f, 1, 0.3) is > 0.79f and < 0.81f && fade.Toward(0.3f, 1, 2) == 1 && fade.Toward(1, 0.3f, 2) == 0.3f, "it sweeps over its time and stops where it should");
        Check.That(WildlifeFade.Solid(WildlifeClipRole.Bite) && WildlifeFade.Solid(WildlifeClipRole.Threat) && !WildlifeFade.Solid(WildlifeClipRole.Idle) && !WildlifeFade.Solid(WildlifeClipRole.Run),
            "it shows whole while it attacks, not while it moves about");
    }

    foreach (string bad in new[] { """{ "fade": { "paint": "p", "cutoff": 0.99, "rest": 0.3, "sweepSeconds": 0.6 } }""", """{ "fade": { "paint": "p", "cutoff": 0.25, "rest": 1.5, "sweepSeconds": 0.6 } }""", """{ "fade": { "paint": "p", "cutoff": 0.25, "rest": 0.3, "sweepSeconds": 0 } }""" })
    {
        using JsonDocument wrong = JsonDocument.Parse(bad);
        bool refused = false;
        try
        {
            WildlifeFade.Read(wrong.RootElement);
        }
        catch (InvalidDataException)
        {
            refused = true;
        }

        Check.That(refused, $"a fade is refused: {bad}");
    }
    Check.That(TenderRules.AnswerSeconds(40, 1.0, 5) > 40 + 5 && TenderRules.AnswerSeconds(40, 0, 5) <= 40 / 0.5 + 5 + 10,
        "an answer lasts long enough to walk there and search, however slow the walker");
    using JsonDocument crestPieces = JsonDocument.Parse(ReadResource("Wildlife.chime-grazer.pieces.json"));
    string[] grazerJoints = GlbJoints("Wildlife.chime-grazer.glb");
    Check.That(crestPieces.RootElement.GetProperty("held").TryGetProperty("crest", out JsonElement crest) && grazerJoints.Contains(crest.GetProperty("joint").GetString()),
        "its crest hangs from a joint of its rig");
});

// The fog foes (#10985): they float in a mist sump's air, hear rather than see, never leave the mist, and circle off
// between bites.
Check.Section("fog foes", () =>
{
    WildlifeKind needle = catalog.Kind("needle-mouth");
    WildlifeFog fog = needle.Hunt!.Fog ?? throw new InvalidOperationException("the needle-mouth has no fog block");
    Check.That(needle.Cave is null && needle.Population.HerdsPerSquareKilometre == 0, "the needle-mouth is placed by test aid only: placement through the sumps is #10174's");
    Check.That(needle.Mesh!.Clips.TryGetValue(WildlifeClipRole.Circle, out string? circleClip) && circleClip == "circle" && needle.Mesh.Fade is not null && needle.Mesh.Ragdoll is not null,
        "it circles on its own clip, fades into the mist and falls as a ragdoll");
    CreatureRig needleRig = CreatureManifest.Parse(ReadResource("Wildlife.needle-mouth.manifest.json"));
    WildlifeRig needleClips = WildlifeRig.For(needle.Mesh, needleRig);
    Check.That(needle.Mesh.Clips.Keys.All(role => needleClips.Clips.ContainsKey(role)), "its built body plays every clip its kind names");

    // How loud the player is: running is heard furthest, walking hardly; the loudest thing done counts.
    Check.That(FogRules.Loudness(BodySound.Footstep, sprinting: true, swung: false) == FogRules.RunningStep
        && FogRules.Loudness(BodySound.Footstep, sprinting: false, swung: false) == FogRules.WalkingStep, "a running step is loud, a walking one quiet");
    Check.That(FogRules.Loudness(BodySound.None, false, false) == 0 && FogRules.Loudness(BodySound.Footstep | BodySound.HardLanding, false, true) == FogRules.HardLanding,
        "standing still is silent, and the loudest thing done counts");
    double hear = needle.Hunt.SightMetres;
    Check.That(FogRules.Hears(FogRules.RunningStep, hear, hear - 1) && !FogRules.Hears(FogRules.WalkingStep, hear, hear - 1) && FogRules.Hears(FogRules.WalkingStep, hear, 2),
        "a run is heard across its hearing, a walk only close by");
    Check.That(!FogRules.Hears(0, hear, 0.5), "silence is never heard");

    // Sensing: heard lately or within its feel, and only while the player is in the mist with it.
    Check.That(FogRules.Senses(fog, true, 0, 15) && FogRules.Senses(fog, true, 99, fog.FeelMetres - 0.1) && !FogRules.Senses(fog, true, fog.HeardSeconds + 0.1, 15),
        "in the mist it senses a player it heard lately or one within its feel, and loses a quiet one further off");
    Check.That(!FogRules.Senses(fog, false, 0, 1), "out of the mist it senses nothing: the mist's edge is safety");

    // A bite needs a clear line (R10985-1): hearing passes walls, teeth do not; any other hunter's sight already decides.
    Check.That(FogRules.ClearToBite(floats: true, clearLine: true) && !FogRules.ClearToBite(floats: true, clearLine: false),
        "a fog foe that heard the player through a wall hunts them but bites only along a clear line");
    Check.That(FogRules.ClearToBite(floats: false, clearLine: false), "a hunter that sees bites as its sight allows");

    // Circling off: beside the way it came, at its distance, and only in the mist.
    Vector2 foe = new(3, 0), prey = Vector2.Zero;
    Vector2 circled = FogRules.CircleTo(foe, prey, fog.CircleMetres, 1, _ => true)!.Value;
    Check.That(Math.Abs(Vector2.Distance(circled, prey) - fog.CircleMetres) < 1e-4, "it circles off at its circling distance from the player");
    double off = Math.Acos(Vector2.Dot(Vector2.Normalize(circled - prey), Vector2.Normalize(foe - prey))) * 180 / Math.PI;
    Check.That(off is > 60 and < 80, $"swung {off:F0} degrees off the way it came, so it curves off along the passage");
    Vector2 other = FogRules.CircleTo(foe, prey, fog.CircleMetres, -1, _ => true)!.Value;
    Check.That(circled.Y * other.Y < 0, "the two sides swing opposite ways");
    Vector2 inSump = FogRules.CircleTo(foe, prey, fog.CircleMetres, 1, point => point.Y < 0)!.Value;
    Check.That(inSump.Y < -4, "where its first choice is out of the mist, it takes the first that is in it: the other side");
    Vector2 passage = FogRules.CircleTo(foe, prey, fog.CircleMetres, 1, point => Math.Abs(point.Y) < 1)!.Value;
    Check.That(Vector2.Distance(passage, new Vector2((float)fog.CircleMetres, 0)) < 1e-4, "in a passage too narrow to swing aside, it goes straight back the way it came");
    Check.That(FogRules.CircleTo(foe, prey, fog.CircleMetres, 1, _ => false) is null, "with nowhere in the mist it stays");

    // Floating: about its float, never touching the floor, and a dead one sinks.
    float[] lifts = [.. Enumerable.Range(0, 40).Select(k => FogRules.Float(fog, k * fog.BobSeconds / 40, 0.3, false, 0, 0.1))];
    Check.That(lifts.Min() >= fog.FloatMetres - fog.BobMetres - 1e-4 && lifts.Max() <= fog.FloatMetres + fog.BobMetres + 1e-4 && lifts.Max() - lifts.Min() > fog.BobMetres,
        "it swims at its float, rising and falling by its bob");
    Check.That(FogRules.Float(fog, 0, 0, true, 1f, 0.5) < 1f && FogRules.Float(fog, 0, 0, true, 0.1f, 1) == 0, "a dead one sinks to the floor and stops there");

    // Clicks quicken as it comes.
    Check.That(Math.Abs(FogRules.ClickEvery(fog, hear, hear) - fog.ClickFarSeconds) < 1e-9 && Math.Abs(FogRules.ClickEvery(fog, hear, 0) - fog.ClickNearSeconds) < 1e-9
        && FogRules.ClickEvery(fog, hear, hear / 2) < fog.ClickFarSeconds && FogRules.ClickEvery(fog, hear, hear * 3) == fog.ClickFarSeconds,
        "its teeth click slowly at the edge of its hearing, faster as it nears");

    // A floating hunter is not falling or hanging: it swims while lifted, and a swing measures from its lowest point.
    HerdRecord sump = new("sump", needle.Id, Vector2.Zero, 1);
    WildlifeMember eel = WildlifeRules.Wake(sump, needle, catalog, 7, Vector2.Zero, 1, () => 1).Leader;
    Hunter swimmer = new(needle, sump, eel);
    eel.Lift = (float)fog.FloatMetres;
    Check.That(swimmer.Floats && !swimmer.Falling && !swimmer.Holding, "a fog foe swims while lifted, as no falling animal does");
    Check.That(HunterRules.Want(swimmer, CreatureState.Pursuing, 5f, 2) == WildlifeClipRole.Run, "lifted, it swims its run, not a drop");
    Check.That(Math.Abs(HunterRules.BodyOffFloor(swimmer) - eel.Lift) < 1e-6, "its body's lowest point is its lift");
    swimmer.Circling = true;
    Check.That(HunterRules.Want(swimmer, CreatureState.Searching, 3f, 2) == WildlifeClipRole.Circle, "circling off, it swims bowed into its turn");
    swimmer.BiteSeconds = 1;
    Check.That(HunterRules.Want(swimmer, CreatureState.Attacking, 0f, 2) == WildlifeClipRole.Bite, "a bite shows before a circle");
    Check.That(new Hunter(needle, sump, eel, 1).CircleSide == -new Hunter(needle, sump, eel, 0).CircleSide, "foes of one sump circle off to different sides");

    string kindsText = ReadKinds();
    foreach ((string from, string to) in new[] { ("\"floatMetres\": 1.0", "\"floatMetres\": 0"), ("\"feelMetres\": 3.0", "\"feelMetres\": 1.0"), ("\"clickFarSeconds\": 0.9", "\"clickFarSeconds\": 0.05") })
    {
        Check.That(kindsText.Contains(from, StringComparison.Ordinal), $"the kinds name {from}");
        Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(kindsText.Replace(from, to, StringComparison.Ordinal)), $"a fog block with {to} is refused");
    }
});

// The fade-ribbon (#10986): a darter until the line (#10173) exists. It loops round the player in the mist and darts in
// only when they come at it; it flutters whenever it swims; its rear always fades; it twists as it swims.
Check.Section("darters", () =>
{
    WildlifeKind ribbon = catalog.Kind("fade-ribbon");
    WildlifeFog fog = ribbon.Hunt!.Fog ?? throw new InvalidOperationException("the fade-ribbon has no fog block");
    WildlifeFog needle = catalog.Kind("needle-mouth").Hunt!.Fog!;
    Check.That(ribbon.Cave is null && ribbon.Population.HerdsPerSquareKilometre == 0, "the fade-ribbon is placed by test aid only: placement through the sumps is #10174's");
    CreatureRig rig = CreatureManifest.Parse(ReadResource("Wildlife.fade-ribbon.manifest.json"));
    WildlifeRig clips = WildlifeRig.For(ribbon.Mesh!, rig);
    Check.That(ribbon.Mesh!.Clips.Keys.All(role => clips.Clips.ContainsKey(role)) && rig.Clips.ContainsKey("gnaw"),
        "its built body plays every clip its kind names, and has its gnaw for the line");
    Check.That(fog.Sound == FogSound.Flutter && needle.Sound == FogSound.Click, "the ribbon flutters, the needle-mouth clicks");
    Check.That(fog.DartMetres is double dart && dart >= ribbon.Hunt.ReachMetres && dart < fog.CircleMetres && needle.DartMetres is null,
        "the ribbon darts from within its loop and from no further than it bites; the needle-mouth is no darter");

    // Its sound: a flutter whenever it swims, so it is heard before it is seen; the needle-mouth's clicks only while it hunts.
    Check.That(FogRules.Sounds(FogSound.Flutter, CreatureState.Idle) && FogRules.Sounds(FogSound.Flutter, CreatureState.Searching)
        && FogRules.Sounds(FogSound.Flutter, CreatureState.Pursuing), "the ribbon flutters wandering, looping and hunting");
    Check.That(!FogRules.Sounds(FogSound.Flutter, CreatureState.Resting) && !FogRules.Sounds(FogSound.Flutter, CreatureState.Dead), "but not at rest or dead");
    Check.That(FogRules.Sounds(FogSound.Click, CreatureState.Pursuing) && !FogRules.Sounds(FogSound.Click, CreatureState.Searching), "the needle-mouth clicks only while it hunts");

    // Darting: only when the player comes within its dart, as one driving it off does.
    Check.That(FogRules.Darts(fog, fog.DartMetres!.Value - 0.1, ready: true) && !FogRules.Darts(fog, fog.DartMetres!.Value + 0.1, ready: true),
        "it darts in when the player comes within its dart, and not before");
    Check.That(!FogRules.Darts(fog, 1, ready: false), "within its dart but unable to bite yet, it loops away rather than waiting in the player's face");
    Check.That(!FogRules.Darts(needle, 0.5, ready: true), "a needle-mouth never holds off to dart: it lunges");

    // What it does, from its loop (R10986-1): its loop holds it searching, and that must not keep it from darting.
    double inDart = fog.DartMetres!.Value - 0.2, outDart = fog.DartMetres!.Value + 1;
    BehaviorTuning tuning = BehaviorTuning.Hostile with { AttackCooldownTicks = ribbon.Hunt.CooldownTicks };
    CreatureBehaviorState looper = new(CreatureState.Searching, LastAttackTick: -1);
    Check.That(looper.CooledDown(500, tuning) && !looper.CanAttack(500, tuning),
        "a looping darter that never bit has cooled down, though it cannot attack until it is attacking");
    CreatureBehaviorState bitLately = looper with { LastAttackTick = 450 };
    Check.That(!bitLately.CooledDown(450 + tuning.AttackCooldownTicks - 1, tuning) && bitLately.CooledDown(450 + tuning.AttackCooldownTicks, tuning),
        "one that bit lately cools down over its cooldown, looping or not");
    DarterMove Move(double distance, CreatureState state = CreatureState.Searching, bool looping = true, bool other = false, bool biting = false, bool cooled = true, bool clear = true) =>
        FogRules.Darter(fog, distance, state, looping, other, biting, cooled, clear);
    Check.Equal(DarterMove.Dart, Move(inDart), "looping, with its bite cooled and a clear line, the player coming within its dart sends it in");
    Check.Equal(DarterMove.Keep, Move(outDart), "the player further off, it keeps to its loop");
    Check.Equal(DarterMove.Keep, Move(inDart, cooled: false), "still cooling down, it keeps to its loop");
    Check.Equal(DarterMove.Keep, Move(inDart, clear: false), "with no clear line, it keeps to its loop");
    Check.Equal(DarterMove.Keep, Move(inDart, CreatureState.Pursuing, looping: false), "already coming in, ready, it keeps coming");
    Check.Equal(DarterMove.Loop, Move(inDart, CreatureState.Attacking, looping: false, cooled: false), "in the player's face and cooling down, it loops away");
    Check.Equal(DarterMove.Loop, Move(outDart, CreatureState.Pursuing, looping: false), "hunting out of its dart, it sets off on its loop");
    Check.Equal(DarterMove.Loop, Move(outDart, CreatureState.Searching, looping: false), "searching where it heard the player, it loops there rather than coming in");
    Check.Equal(DarterMove.Keep, Move(outDart, CreatureState.Searching, looping: false, other: true), "circling off after a bite, it finishes that first");
    Check.Equal(DarterMove.Keep, Move(inDart, CreatureState.Attacking, looping: false, biting: true, cooled: false), "mid-bite it keeps biting");
    Check.Equal(DarterMove.Keep, Move(outDart, CreatureState.Resting, looping: false), "at rest it stays at rest");

    // Looping: round the player at its loop's distance, a step at a time on its own side, so it goes all the way round.
    Vector2 prey = Vector2.Zero, at = new((float)fog.CircleMetres, 0);
    double turned = 0;
    for (int k = 0; k < 8; k++)
    {
        Vector2 next = FogRules.LoopTo(at, prey, fog.CircleMetres, fog.LoopDegrees, 1, _ => true)!.Value;
        Check.That(Math.Abs(Vector2.Distance(next, prey) - fog.CircleMetres) < 1e-4, "it loops at its loop's distance from the player");
        double step = Math.Atan2((at.X * next.Y) - (at.Y * next.X), Vector2.Dot(at, next)) * 180 / Math.PI;
        Check.That(Math.Abs(step - fog.LoopDegrees) < 1e-3, $"each step of its loop goes {fog.LoopDegrees:F0} degrees round, the same way ({step:F1})");
        turned += step;
        at = next;
    }

    Check.That(turned >= 360, "looping, it swims all the way round the player");
    Vector2 start = new((float)fog.CircleMetres, 0);
    Vector2 other = FogRules.LoopTo(start, prey, fog.CircleMetres, fog.LoopDegrees, -1, _ => true)!.Value;
    Check.That(other.Y < 0, "a foe on the other side loops the other way round");
    Vector2 blocked = FogRules.LoopTo(start, prey, fog.CircleMetres, fog.LoopDegrees, 1, point => point.Y <= 0)!.Value;
    Check.That(blocked.Y < 0, "where its way round leaves the mist, it turns back the other way");
    Vector2 narrow = FogRules.LoopTo(start, prey, fog.CircleMetres, fog.LoopDegrees, 1, point => point.Y < 1.8)!.Value;
    double shortStep = Math.Atan2(narrow.Y, narrow.X) * 180 / Math.PI;
    Check.That(narrow.Y > 0 && shortStep < fog.LoopDegrees, $"where the mist leaves less room it goes less far round ({shortStep:F0} degrees)");
    Check.That(FogRules.LoopTo(start, prey, fog.CircleMetres, fog.LoopDegrees, 1, _ => false) is null, "with nowhere in the mist it stays");

    // Its rear always fades: attacking, it shows only part of itself.
    WildlifeFade fade = ribbon.Mesh.Fade!;
    Check.That(fade.Strike < 1 && fade.Strike > fade.Rest && fade.AlphaFor(fade.Strike) < 1, "attacking, it shows more of itself than at rest, but never its rear");
    Check.That(catalog.Kind("needle-mouth").Mesh!.Fade!.Strike == 1, "a needle-mouth attacking shows all of itself");

    // It twists as it swims: none at the head, wider fast than hovering.
    WildlifeSwim swim = ribbon.Mesh.Swim!;
    Check.That(swim.TwistDegrees > swim.IdleTwistDegrees && swim.IdleTwistDegrees > 0 && catalog.Kind("needle-mouth").Mesh!.Swim!.TwistDegrees == 0,
        "the ribbon twists, more fast than hovering; the needle-mouth does not");
    Vector3[] laid = [.. Enumerable.Range(0, 16).Select(k => new Vector3(0f, 1f, k * 0.33f))];
    float Widest(float speed)
    {
        SwimChain chain = new(laid);
        chain.Lay(new Vector3(0f, 1f, 0f), Vector3.UnitZ);
        float widest = 0f;
        for (int k = 0; k < 120; k++)
        {
            chain.Step(swim, new Vector3(0f, 1f, 0f), Vector3.UnitZ, speed, 1.0 / 30);
            widest = MathF.Max(widest, chain.Twists.Max(MathF.Abs));
            Check.That(chain.Twists[^1] == 0f || MathF.Abs(chain.Twists[^1]) < 1e-6f, "its head end does not twist");
        }

        return widest;
    }

    float lashing = Widest(swim.WaveSpeed), hovering = Widest(0f);
    Check.That(lashing > hovering * 1.5f && lashing <= (swim.TwistDegrees * MathF.PI / 180f) + 1e-4f,
        $"its twist is wider swimming fast ({lashing * 180 / MathF.PI:F0} degrees) than hovering ({hovering * 180 / MathF.PI:F0}), and no wider than its kind's");

    string kindsText = ReadKinds();
    foreach ((string from, string to) in new[] { ("\"dartMetres\": 3.2", "\"dartMetres\": 6.0"), ("\"dartMetres\": 3.2", "\"dartMetres\": 1.0"), ("\"sound\": \"flutter\"", "\"sound\": \"hum\""),
        ("\"strike\": 0.7", "\"strike\": 0.05"), ("\"twistDegrees\": 50", "\"twistDegrees\": -5") })
    {
        Check.That(kindsText.Contains(from, StringComparison.Ordinal), $"the kinds name {from}");
        Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(kindsText.Replace(from, to, StringComparison.Ordinal)), $"a darter with {to} is refused");
    }
});

// The gulper (#10987): a waiter. It never comes at the player, but holds still while it hears them, strikes once they are
// within its reach, withdraws while it cools down, and rests when it hears nothing; its throat pulses even at rest.
Check.Section("waiters", () =>
{
    WildlifeKind gulper = catalog.Kind("gulper");
    WildlifeFog fog = gulper.Hunt!.Fog ?? throw new InvalidOperationException("the gulper has no fog block");
    Check.That(gulper.Cave is null && gulper.Population.HerdsPerSquareKilometre == 0 && gulper.GroupMaximum == 1,
        "the gulper is placed by test aid only, one at a time: placement through the sumps is #10174's");
    CreatureRig rig = CreatureManifest.Parse(ReadResource("Wildlife.gulper.manifest.json"));
    WildlifeRig clips = WildlifeRig.For(gulper.Mesh!, rig);
    Check.That(gulper.Mesh!.Clips.Keys.All(role => clips.Clips.ContainsKey(role)) && gulper.Mesh.Fade is not null && gulper.Mesh.Ragdoll is not null,
        "its built body plays every clip its kind names, fades into the mist and falls as a ragdoll");
    Check.That(fog.Waits && fog.DartMetres is null && fog.Sound == FogSound.Pulse && !catalog.Kind("needle-mouth").Hunt!.Fog!.Waits,
        "the gulper waits and pulses; the needle-mouth does not wait");
    Check.That(FogRules.Sounds(FogSound.Pulse, CreatureState.Resting) && FogRules.Sounds(FogSound.Pulse, CreatureState.Searching) && !FogRules.Sounds(FogSound.Pulse, CreatureState.Dead),
        "its throat pulses even at rest, so it is heard before it is reached, and stops when it dies");
    Check.That(gulper.Hunt.CooldownTicks >= 8 * HunterRules.StepsPerSecond, "a long while passes before it strikes again");

    double reach = gulper.Hunt.ReachMetres, near = reach - 0.3, far = reach + 1.5;
    WaiterMove Move(double distance, CreatureState state = CreatureState.Searching, bool senses = true, bool holding = true, bool other = false, bool biting = false, bool cooled = true, bool clear = true) =>
        FogRules.Waiter(distance, reach, state, senses, holding, other, biting, cooled, clear);
    Check.Equal(WaiterMove.Hold, Move(far, CreatureState.Pursuing, holding: false), "hearing the player out of its reach, it holds still where it hangs rather than coming at them");
    Check.Equal(WaiterMove.Hold, Move(far, CreatureState.Alert, holding: false), "woken, it holds still");
    Check.Equal(WaiterMove.Keep, Move(far), "holding, it keeps holding while the player stays out of its reach");
    Check.Equal(WaiterMove.Strike, Move(near), "the player within its reach, cooled and along a clear line, it strikes");
    Check.Equal(WaiterMove.Keep, Move(near, CreatureState.Attacking, holding: false), "striking, it keeps on with its bite");
    Check.Equal(WaiterMove.Keep, Move(near, clear: false), "with no clear line, it keeps holding: never a bite through rock");
    Check.Equal(WaiterMove.Withdraw, Move(near, cooled: false), "within its reach while it is still cooling down, it withdraws");
    Check.Equal(WaiterMove.Withdraw, Move(near, CreatureState.Attacking, holding: false, cooled: false), "and so does one left facing the player after its bite");
    Check.Equal(WaiterMove.Rest, Move(far, senses: false), "hearing nothing, it rests where it holds");
    Check.Equal(WaiterMove.Keep, Move(near, other: true, cooled: false), "withdrawing, it finishes withdrawing");
    Check.Equal(WaiterMove.Keep, Move(near, CreatureState.Attacking, holding: false, biting: true), "mid-bite it keeps biting");
    Check.Equal(WaiterMove.Keep, Move(near, CreatureState.Resting, holding: false), "at rest it stays at rest until a noise or its feel wakes it");
    Check.That(fog.FeelMetres >= reach && gulper.Hunt.Rest!.WakeMetres > reach, "it feels and wakes to a quiet player before they are within its reach");
    // Withdrawing: at its distance where the mist allows, nearer where it does not, but never within its reach.
    Vector2 foe = new(2.5f, 0), prey = Vector2.Zero;
    Vector2 open = FogRules.WithdrawTo(foe, prey, fog.CircleMetres, reach + 0.5, 1, _ => true)!.Value;
    Check.That(Math.Abs(Vector2.Distance(open, prey) - fog.CircleMetres) < 1e-4, "with room, it withdraws to its full distance");
    Vector2 cramped = FogRules.WithdrawTo(foe, prey, fog.CircleMetres, reach + 0.5, 1, point => point.Length() < reach + 1.2)!.Value;
    Check.That(cramped.Length() is var got && got > reach && got < reach + 1.2, $"where the mist leaves little room, it withdraws as far as it can ({got:F1} m), beyond its reach");
    Check.That(FogRules.WithdrawTo(foe, prey, fog.CircleMetres, reach + 0.5, 1, point => point.Length() < reach) is null, "with no room beyond its reach, it has nowhere to withdraw");

    string kindsText = ReadKinds();
    foreach ((string from, string to) in new[] { ("\"sound\": \"pulse\", \"waits\": true", "\"sound\": \"pulse\", \"waits\": true, \"dartMetres\": 3.4"), ("\"feelMetres\": 3.4", "\"feelMetres\": 2.0") })
    {
        Check.That(kindsText.Contains(from, StringComparison.Ordinal), $"the kinds name {from}");
        Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(kindsText.Replace(from, to, StringComparison.Ordinal)), $"a waiter with {to} is refused");
    }
});

// A swimmer's body follows its head (#10995): dragged after it on a chain at its rest spacing, the tail lagging through a
// turn, a wave down it that grows with speed and leaves the head still.
Check.Section("swim chain", () =>
{
    WildlifeSwim swim = catalog.Kind("needle-mouth").Mesh!.Swim ?? throw new InvalidOperationException("the needle-mouth does not swim");
    WildlifeSwim still = swim with { WaveMetres = 0, IdleWaveMetres = 0 };
    Vector3[] rest = [.. Enumerable.Range(0, 16).Select(k => new Vector3(0f, 1f, k * 0.25f))];
    float Spacing(IReadOnlyList<Vector3> p, int i) => Vector3.Distance(p[i], p[i + 1]);
    bool Kept(IReadOnlyList<Vector3> p) => Enumerable.Range(0, p.Count - 1).All(i => MathF.Abs(Spacing(p, i) - 0.25f) < 1e-3f);

    SwimChain chain = new(rest);
    chain.Lay(new Vector3(0f, 1f, 0f), Vector3.UnitX);
    Check.That(Kept(chain.Points) && MathF.Abs(chain.Points[0].X + 3.75f) < 1e-3f, "laid out, it lies straight behind its head at its rest spacing");
    const double Step = 1.0 / 60;
    Vector3 head = Vector3.Zero with { Y = 1f };
    for (int k = 0; k < 120; k++)
    {
        head += new Vector3(3f * (float)Step, 0f, 0f);
        chain.Step(still, head, Vector3.UnitX, 3f, Step);
    }

    Check.That(Kept(chain.Points) && chain.Points.All(p => MathF.Abs(p.Z) < 1e-3f), "swimming straight, it stays straight and keeps its spacing");
    // A sharp turn: the head swings round to swim along +z; the tail still lies along the old line a moment after.
    for (int k = 0; k < 12; k++)
    {
        head += new Vector3(0f, 0f, 3f * (float)Step);
        chain.Step(still, head, Vector3.UnitZ, 3f, Step);
    }

    Check.That(Kept(chain.Points), "through a turn it keeps its spacing");
    Check.That(chain.Points[^3].Z > 0.1f && MathF.Abs(chain.Points[0].Z) < 0.05f, "through a turn the head end swings round while the tail still lies along the way it came");
    for (int k = 0; k < 240; k++) chain.Step(still, head, Vector3.UnitZ, 0f, Step);
    Check.That(chain.Points.Take(15).All(p => p.Z < head.Z + 1e-3f) && MathF.Abs(chain.Points[0].X - head.X) < 0.3f,
        "stopped, it eases straight out behind its head");

    float Width(SwimChain c, float speed) => Enumerable.Range(0, 60).Max(_ => c.Step(swim, head, Vector3.UnitZ, speed, 1.0 / 30)[0].X - c.Points[0].X);
    float hovering = Width(chain, 0f), lashing = Width(chain, swim.WaveSpeed);
    Check.That(lashing > hovering * 2f, "its wave lashes wider swimming fast than hovering");
    Vector3[] waved = chain.Step(swim, head, Vector3.UnitZ, swim.WaveSpeed, 0.37);
    Check.That(waved[^1] == chain.Points[^1] && Vector3.Distance(waved[^2], chain.Points[^2]) < 1e-4f, "its head and neck stay out of the wave");
    // A change of speed long into a swim changes how fast the wave runs from then on, not where it has got to (R10995-1),
    // and its width eases after it: the tail never jumps sideways in a frame as the creature speeds up or slows.
    SwimChain cruising = new(rest);
    Vector3 lead = new(0f, 1f, 0f);
    cruising.Lay(lead, Vector3.UnitZ);
    float Tail(float speed)
    {
        lead += new Vector3(0f, 0f, speed * (float)Step);
        Vector3[] drawn = cruising.Step(swim, lead, Vector3.UnitZ, speed, Step);
        return drawn[0].X - cruising.Points[0].X;
    }

    float last = 0f, steadiest = 0f;
    for (int k = 0; k < 3600; k++)
    {
        float now = Tail(3f);
        if (k > 0) steadiest = MathF.Max(steadiest, MathF.Abs(now - last));
        last = now;
    }

    float faster = Tail(3.01f), slower = Tail(1f);
    Check.That(MathF.Abs(faster - last) <= steadiest + 1e-3f && MathF.Abs(slower - faster) <= steadiest + 1e-3f,
        "a minute into a swim, speeding up or slowing moves its tail no further in a frame than swimming steadily does");
    // Its head eases after its facing (Patch: it "snaps less"): in and out of a turn, the short way round, never past it.
    float angle = 0f, rate = 0f, widest = 0f;
    float[] path = new float[90];
    for (int k = 0; k < path.Length; k++)
    {
        SwimChain.Ease(ref angle, ref rate, MathF.PI / 2, swim.TurnEaseSeconds, (float)Step);
        path[k] = angle;
        widest = MathF.Max(widest, angle);
    }

    Check.That(path[0] < 0.05f && MathF.Abs(path[^1] - (MathF.PI / 2)) < 0.02f && widest <= (MathF.PI / 2) + 1e-4f,
        "a quarter turn starts gently, settles within its ease and never swings past");
    Check.That(path[1] - path[0] < path[10] - path[9], "it speeds into a turn rather than jumping");
    float across = 3f, across2 = 0f;
    SwimChain.Ease(ref across, ref across2, -3f, swim.TurnEaseSeconds, (float)Step);
    Check.That(across > 3f || across < -3f, "from 3 to -3 radians it turns the short way, across the back");
    Quaternion turn = SwimChain.FromTo(Vector3.UnitZ, Vector3.UnitX);
    Check.That(Vector3.Distance(Vector3.Transform(Vector3.UnitZ, turn), Vector3.UnitX) < 1e-4f && SwimChain.FromTo(Vector3.UnitY, Vector3.UnitY) == Quaternion.Identity,
        "a bone is turned from its rest direction to its new one the shortest way");
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(ReadKinds().Replace("\"strikeWeight\": 0.35", "\"strikeWeight\": 1.5", StringComparison.Ordinal)), "a strike weight past 1 is refused");
});

Check.Section("debug holds", () =>
{
    Vector2 near = new(12, 0);
    WildlifeHerd herd = Herd("hipparion", Vector2.Zero, 4, "holds");
    WildlifeMember frozen = herd.Members[1];
    frozen.Hold = new WildlifeHold { Frozen = true };
    (Vector2 at, float yaw, Wariness stage) = (frozen.Position, frozen.Yaw, frozen.Stage);
    Run(herd, 6, Morning, _ => near);
    Check.That(frozen.Position == at && frozen.Yaw == yaw && frozen.Stage == stage && frozen.Velocity == Vector2.Zero, "a frozen animal neither moves, turns nor changes its wariness");
    Check.That(herd.Members.Any(member => member != frozen && member.Stage != Wariness.Calm), "while the rest of its herd takes notice of the player");
    frozen.Hold = null;
    Run(herd, 2, Morning, _ => near);
    Check.That(frozen.Stage != stage || frozen.Position != at, "released, it answers to its senses again");

    WildlifeHerd looking = Herd("hipparion", Vector2.Zero, 3, "look");
    looking.Members[0].Hold = new WildlifeHold { Stage = Wariness.Look };
    Run(looking, 5, Morning, _ => new Vector2(500, 0));
    Check.Equal(Wariness.Look, looking.Members[0].Stage, "a held wariness stays, with no player near");
    Check.Equal(Wariness.Calm, looking.Members[1].Stage, "an animal not held is calm");

    WildlifeHerd standing = Herd("mammoth", Vector2.Zero, 2, "flee");
    WildlifeMember runner = standing.Members[0];
    runner.Hold = new WildlifeHold { Stage = Wariness.Respond, Flee = true };
    Check.Equal(PressedResponse.Flee, WildlifeRules.Response(runner, standing.Kind), "a held flee answers by fleeing, whatever its kind does");
    float before = Vector2.Distance(runner.Position, near);
    Run(standing, 3, Morning, _ => near);
    Check.That(Vector2.Distance(runner.Position, near) > before + 1, "and it runs from the player");

    WildlifeHerd walker = Herd("hipparion", Vector2.Zero, 2, "goto");
    Vector2 there = new(0, -10);
    walker.Members[1].Hold = new WildlifeHold { GoTo = there };
    Run(walker, 30, Morning, _ => null);
    Check.That(Vector2.Distance(walker.Members[1].Position, there) <= catalog.Behaviour.GoalArrivalMetres + 0.5, "an animal sent somewhere walks there");

    // R10926-5: one animal held at rest rests alone; its herd keeps to its day, and release leaves nothing behind.
    WildlifeHerd grazing = Herd("hipparion", Vector2.Zero, 3, "graze");
    WildlifeRules.Begin(grazing, WildlifeActivity.Drift, Morning);
    grazing.Goal = new Vector2(0, -60);
    WildlifeMember sitter = grazing.Members[1];
    sitter.Hold = new WildlifeHold { Stage = Wariness.Calm, Rest = true };
    Vector2 sat = sitter.Position;
    Run(grazing, 4, Morning, _ => null);
    Check.Equal(WildlifeActivity.Drift, grazing.Activity, "holding one animal at rest leaves its herd's activity alone");
    Check.That(sitter.Position == sat && sitter.Velocity == Vector2.Zero, "the held animal stands where it was");
    Check.That(grazing.Members.Where(member => member != sitter).Any(member => member.Velocity != Vector2.Zero), "while the rest of its herd moves on");
    long period = grazing.Period;
    sitter.Hold = null;
    Run(grazing, 2, Morning, _ => null);
    Check.That(grazing.Period == period && grazing.Activity == WildlifeActivity.Drift, "release changes nothing of the herd's, in the same period");
    Check.That(sitter.Position != sat, "and the animal released goes with its herd again");

    // R10926-4: a pick is the animal itself, not its id: an animal woken later under a freed id is not it.
    WildlifeHerd first = WildlifeRules.Wake(new HerdRecord("pick-a", "hipparion", Vector2.Zero, 1), catalog.Kind("hipparion"), catalog, Seed, Vector2.Zero, 1, () => 77);
    HerdPick pick = new(77, "hipparion", first.Leader);
    Check.That(HerdPick.Find([first], pick) is not null, "a picked animal is found while it is awake");
    WildlifeHerd second = WildlifeRules.Wake(new HerdRecord("pick-b", "hipparion", new Vector2(50, 0), 1), catalog.Kind("hipparion"), catalog, Seed, new Vector2(50, 0), 1, () => 77);
    Check.That(HerdPick.Find([second], pick) is null, "once its herd has gone, another hipparion given its id is not it");

    WildlifeHerd resting = Herd("hipparion", Vector2.Zero, 3, "rest");
    WildlifeRules.Begin(resting, WildlifeActivity.Rest, Morning);
    Run(resting, 10, Morning, _ => null);
    Check.Equal(WildlifeActivity.Rest, resting.Activity, "a herd placed to start at rest rests out its schedule's period");
    resting.Period = -1;
    Run(resting, 1, Morning, _ => null);
    Check.Equal(Morning is var m ? catalog.Kind("hipparion").Schedule.Steps[catalog.Kind("hipparion").Schedule.StepAt(m.DayFraction)].Activity : default, resting.Activity,
        "and the next period goes back to its schedule");
});

return Check.Finish("wildlife");

static string ReadKinds() => ReadResource("Wildlife.kinds.json");

// The longest side of a GLB's first mesh's position bounds, in the mesh's own metres.
static double GlbLongestExtent(string name)
{
    using Stream stream = typeof(Check).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"{name} is not embedded");
    using BinaryReader reader = new(stream);
    reader.ReadBytes(12);
    int length = reader.ReadInt32();
    reader.ReadInt32();
    using JsonDocument gltf = JsonDocument.Parse(reader.ReadBytes(length));
    int accessor = gltf.RootElement.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").GetProperty("POSITION").GetInt32();
    JsonElement bounds = gltf.RootElement.GetProperty("accessors")[accessor];
    return Enumerable.Range(0, 3).Max(axis => bounds.GetProperty("max")[axis].GetDouble() - bounds.GetProperty("min")[axis].GetDouble());
}

// The names of a GLB's first skin's joints.
static string[] GlbJoints(string name)
{
    using Stream stream = typeof(Check).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"{name} is not embedded");
    using BinaryReader reader = new(stream);
    reader.ReadBytes(12);
    int length = reader.ReadInt32();
    reader.ReadInt32();
    using JsonDocument gltf = JsonDocument.Parse(reader.ReadBytes(length));
    JsonElement nodes = gltf.RootElement.GetProperty("nodes");
    return [.. gltf.RootElement.GetProperty("skins")[0].GetProperty("joints").EnumerateArray().Select(joint => nodes[joint.GetInt32()].GetProperty("name").GetString()!)];
}

static string ReadResource(string name)
{
    using Stream stream = typeof(Check).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException($"{name} is not embedded");
    using StreamReader reader = new(stream);
    return reader.ReadToEnd();
}
