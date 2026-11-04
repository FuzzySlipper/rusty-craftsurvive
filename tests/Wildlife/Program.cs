using System.Numerics;
using CraftSurvive.Game.Modules.Sky;
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
void Run(WildlifeHerd herd, double seconds, WorldTime start, Func<double, Vector2?> player, Action<double>? each = null)
{
    for (double t = 0; t < seconds; t += Tick)
    {
        WorldTime time = start with { DayFraction = start.DayFraction + (t / WorldClock.DaySeconds) };
        WildlifeRules.Decide(herd, new HerdFacts(player(t), time, Seed), catalog, Tick);
        WildlifeRules.Integrate(herd, Tick);
        each?.Invoke(t);
    }
}

WorldTime Morning = new(3, 6.5 / 24);
WorldTime Midnight = new(3, 0.5 / 24);

double SpreadRadius(WildlifeHerd herd) => herd.Members.Max(m => Vector2.Distance(m.Position, herd.Leader.Position));

double ClosestPair(WildlifeHerd herd)
{
    double closest = double.PositiveInfinity;
    for (int i = 0; i < herd.Members.Count; i++)
        for (int j = i + 1; j < herd.Members.Count; j++)
            closest = Math.Min(closest, Vector2.Distance(herd.Members[i].Position, herd.Members[j].Position));
    return closest;
}

// How far a herd of n may spread around its leader and still be one herd.
double CohesionBound(WildlifeKind kind, int count) =>
    kind.SpacingMetres * ((0.75 * Math.Sqrt(count - 1)) + catalog.Behaviour.CatchUpSpacings + catalog.Behaviour.SlotJitterShare);

Check.Section("kinds", () =>
{
    string[] firstWave = ["ground-sloth", "hipparion", "giant-deer", "entelodont", "giant-beaver", "cave-goat", "glyptodon", "mammoth"];
    Check.That(firstWave.All(id => catalog.TryKind(id, out _)), "the eight first-wave kinds must load");
    Check.Equal(firstWave.Length, catalog.Kinds.Count, "the content holds the first wave and nothing else");
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
    Check.Throws<InvalidDataException>(() => WildlifeKinds.Parse(text.Replace("\"sleepMetres\": 260", "\"sleepMetres\": 100")),
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
});

return Check.Finish("wildlife");

static string ReadKinds()
{
    using Stream stream = typeof(Check).Assembly.GetManifestResourceStream("Wildlife.kinds.json")
        ?? throw new InvalidOperationException("the kinds are not embedded");
    using StreamReader reader = new(stream);
    return reader.ReadToEnd();
}
