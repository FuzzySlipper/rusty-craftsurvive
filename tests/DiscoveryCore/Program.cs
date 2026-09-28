using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Discovery;
using CraftSurvive.Game.Modules.Terrain;

// Point-of-interest placement, checked without a runtime: that a site is a pure
// function of the contract and its anchor cell, that the ground gates the kind it can
// carry, and that every structure stays inside the bounds it claims - including the
// one pass that is allowed to remove material.

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

const long Radius = TerrainConstants.DefaultSize / 2;
const ulong Seed = 0x5eed_0000_0000_0001UL;

PoiPlacement Create(ulong seed, uint version, ITerrainColumns columns) =>
    new(new TerrainGeneratorContract(seed, version, TerrainConstants.DefaultSize),
        new TestDraws(), columns, Radius);

var rolling = new RollingColumns(baseHeight: 8);
PoiPlacement placement = Create(Seed, TerrainGeneratorContract.CurrentVersion, rolling);

// --- a site is a pure function of the contract and its cell ----------------------
List<(long X, long Z)> cells = [];
for (long cellX = -20; cellX <= 20; cellX++)
{
    for (long cellZ = -20; cellZ <= 20; cellZ++)
    {
        cells.Add((cellX, cellZ));
    }
}

int sites = 0;
int present = 0;
foreach ((long cellX, long cellZ) in cells)
{
    PoiSite? site = placement.SiteAt(cellX, cellZ);
    if (site is null)
    {
        continue;
    }

    present++;
    sites++;
}
Require(sites > 1000, $"the anchor lattice must place sites across the world, found {sites}");

Dictionary<(long X, long Z), PoiSite?> forward = [];
foreach ((long cellX, long cellZ) in cells)
{
    forward[(cellX, cellZ)] = placement.SiteAt(cellX, cellZ);
}

// A second placement, built from a fresh draw port, must agree cell for cell.
PoiPlacement repeat = Create(Seed, TerrainGeneratorContract.CurrentVersion, rolling);
int mismatches = cells.Count(cell => repeat.SiteAt(cell.X, cell.Z) != forward[cell]);
Require(mismatches == 0, $"the same contract must place the same sites, {mismatches} cells disagreed");

// Order must not matter: query the whole lattice backwards on a fresh placement.
PoiPlacement reversed = Create(Seed, TerrainGeneratorContract.CurrentVersion, rolling);
int orderMismatches = 0;
for (int index = cells.Count - 1; index >= 0; index--)
{
    if (reversed.SiteAt(cells[index].X, cells[index].Z) != forward[cells[index]])
    {
        orderMismatches++;
    }
}

Require(orderMismatches == 0, $"cell query order must not change a site, {orderMismatches} disagreed");

// A cell asked twice answers the same thing, which is what the cache must preserve.
Require(placement.SiteAt(3, -7) == forward[(3, -7)], "a repeated query must return the same site");

// A different seed, and a different generation version, must move the world.
PoiPlacement otherSeed = Create(Seed ^ 0x1234, TerrainGeneratorContract.CurrentVersion, rolling);
PoiPlacement otherVersion = Create(Seed, TerrainGeneratorContract.CurrentVersion + 1, rolling);
int seedDifferences = cells.Count(cell => otherSeed.SiteAt(cell.X, cell.Z) != forward[cell]);
int versionDifferences = cells.Count(cell => otherVersion.SiteAt(cell.X, cell.Z) != forward[cell]);
Require(seedDifferences > 100, $"a different seed must place a different world, only {seedDifferences} cells differed");
Require(versionDifferences > 100,
    $"a version bump must regenerate deliberately, only {versionDifferences} cells differed");

// --- placement obeys the ground it stands on -------------------------------------
foreach (PoiSite site in forward.Values.Where(site => site is not null).Select(site => site!.Value))
{
    Require(Math.Abs(site.X) <= Radius - PoiConstants.WorldMargin
        && Math.Abs(site.Z) <= Radius - PoiConstants.WorldMargin,
        $"{site.Id} must stand inside the world, away from the border wall");
    Require(site.Ground >= PoiConstants.MinimumGroundHeight,
        $"{site.Id} must not stand at or below the water line");
    Require(site.Kind != PoiKind.None, $"{site.Id} must have a kind");
}

// Submerged ground is refused outright, so a site is never placed in a lake.
PoiPlacement drowned = Create(Seed, TerrainGeneratorContract.CurrentVersion, new FlatColumns(surface: 0));
foreach ((long cellX, long cellZ) in cells)
{
    Require(drowned.SiteAt(cellX, cellZ) is null,
        $"({cellX},{cellZ}) is under water and must hold no site");
}

// Flat ground carries no hillside and no height, so relief kinds and lookouts are
// reconciled away rather than placed where they would make no sense.
PoiPlacement flat = Create(Seed, TerrainGeneratorContract.CurrentVersion, new FlatColumns(surface: 6));
int flatSites = 0;
foreach ((long cellX, long cellZ) in cells)
{
    if (flat.SiteAt(cellX, cellZ) is not PoiSite site)
    {
        continue;
    }

    flatSites++;
    Require(site.Kind is PoiKind.Ruin or PoiKind.StandingStones,
        $"{site.Id} is on flat, low ground and cannot be a {site.KindName}");
}

Require(flatSites > 1000, $"flat ground must still carry sites, found {flatSites}");

// --- every kind is reachable ------------------------------------------------------
Dictionary<PoiKind, int> kindCounts = [];
foreach (PoiSite site in forward.Values.Where(site => site is not null).Select(site => site!.Value))
{
    kindCounts[site.Kind] = kindCounts.GetValueOrDefault(site.Kind) + 1;
}

foreach (PoiKind kind in new[]
    { PoiKind.StandingStones, PoiKind.Ruin, PoiKind.CaveMouth, PoiKind.DungeonEntrance, PoiKind.VantagePoint })
{
    Require(kindCounts.GetValueOrDefault(kind) > 0,
        $"the world must contain at least one {kind}; counts were " +
        string.Join(", ", kindCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")));
}

// --- structures stay inside their bounds ------------------------------------------
long structureTop = PoiConstants.MaximumStructureHeight;
int examined = 0;
int carved = 0;
foreach (PoiSite site in forward.Values.Where(site => site is not null).Select(site => site!.Value))
{
    examined++;
    int built = 0;
    int removed = 0;
    for (long y = site.Ground - PoiConstants.MaximumCarveDepth; y <= site.Ground + structureTop; y++)
    {
        for (long x = site.X - PoiConstants.MaximumStructureReach; x <= site.X + PoiConstants.MaximumStructureReach; x++)
        {
            for (long z = site.Z - PoiConstants.MaximumStructureReach; z <= site.Z + PoiConstants.MaximumStructureReach; z++)
            {
                // The voxel pass finds a structure by scanning the nine cells around a position, which is
                // only sufficient while every builder stays inside the reach it declares. Nothing else
                // asserts it, and a structure one block wider would let two chunks disagree about a shared
                // voxel - the one failure this contract cannot tolerate.
                long outside = PoiConstants.MaximumStructureReach + 2;
                Require(PoiStructures.MaterialAt(site, site.X + outside, site.Ground, site.Z).IsNone
                    && PoiStructures.MaterialAt(site, site.X - outside, site.Ground, site.Z).IsNone
                    && PoiStructures.MaterialAt(site, site.X, site.Ground, site.Z + outside).IsNone
                    && PoiStructures.MaterialAt(site, site.X, site.Ground, site.Z - outside).IsNone,
                    $"{site.Id} must build nothing beyond its declared reach");
                Require(PoiStructures.MaterialAt(site, site.X, site.Ground + PoiConstants.MaximumStructureHeight + 1, site.Z).IsNone,
                    $"{site.Id} must build nothing above its declared height");
                Require(PoiStructures.MaterialAt(site, site.X, site.Ground - PoiConstants.MaximumCarveDepth - 2, site.Z).IsNone,
                    $"{site.Id} must cut nothing below its declared depth");

                PoiVoxel voxel = PoiStructures.MaterialAt(site, x, y, z);
                if (voxel.IsNone)
                {
                    continue;
                }

                if (voxel.Kind == PoiVoxelKind.Carve)
                {
                    removed++;
                    // The only bound that matters is the floor: a mouth also clears the
                    // opening above its own ground line, which is how a hole in the
                    // hillside stays open where the slope rises past the site.
                    Require(y >= site.Ground - PoiConstants.MaximumCarveDepth,
                        $"{site.Id} must not cut below its own floor bound at y={y}");
                    Require(y <= site.Ground + PoiConstants.CaveArchHeight,
                        $"{site.Id} must not cut above its arch at y={y}");
                }
                else
                {
                    built++;
                    Require(voxel.Material != TerrainConstants.EmptyMaterial,
                        $"{site.Id} must only place a bound block, found slot 0 at {x},{y},{z}");
                }
            }
        }
    }

    Require(built > 0, $"{site.Id} is a {site.KindName} that builds nothing");
    carved += removed;

    // A way in is a hole: the kinds that are one must actually cut.
    if (site.Kind is PoiKind.CaveMouth or PoiKind.DungeonEntrance)
    {
        Require(removed > 0, $"{site.Id} is a {site.KindName} and must cut a way in");
    }
}

Require(examined > 1000, $"the structure sweep must see the whole world, saw {examined}");
Require(carved > 0, "some structure must remove material, or nothing makes a way in");

// The deepest cut any site may make stays well above the bedrock floor.
long deepest = PoiConstants.MinimumGroundHeight - PoiConstants.MaximumCarveDepth;
Require(deepest > -TerrainConstants.TerrainDepth,
    $"the deepest cut ({deepest}) must stay above the world floor (-{TerrainConstants.TerrainDepth})");

// --- identity ---------------------------------------------------------------------
HashSet<string> ids = [];
foreach (PoiSite site in forward.Values.Where(site => site is not null).Select(site => site!.Value))
{
    Require(ids.Add(site.Id), $"{site.Id} must identify exactly one site");
}

Require(ids.Count == sites, "every site in the world must have its own id");

// --- discovery: the journal that records what was found ---------------------------
DiscoveryState journal = new(Seed);

// Real sites, taken in a stable order, so the journal checks do not depend on which
// cells the lattice happened to fill.
List<PoiSite> discovered = forward.Values
    .Where(site => site is not null)
    .Select(site => site!.Value)
    .OrderBy(site => site.Id, StringComparer.Ordinal)
    .ToList();
PoiSite sample = discovered[0];
Require(journal.Count == 0, "a new journal knows nothing");
Require(journal.Notice(sample, DiscoveryStage.Seen, tick: 10), "a first sighting must be recorded");
Require(!journal.Notice(sample, DiscoveryStage.Seen, tick: 11), "a repeat sighting must change nothing");
Require(journal.Notice(sample, DiscoveryStage.Visited, tick: 20), "arriving must promote the entry");
Require(!journal.Notice(sample, DiscoveryStage.Seen, tick: 21), "a later glance must not demote a visit");
Require(journal.Find(sample.CellX, sample.CellZ) is { Stage: DiscoveryStage.Visited, FirstSeenTick: 10, LastTick: 20 },
    "an entry must keep the first sighting and raise the stage");
Require(journal.VisitedCount == 1 && journal.SeenCount == 0, "the counts must follow the stages");
Require(!journal.Notice(sample, DiscoveryStage.None, tick: 22), "nothing learned must record nothing");

// Distance and visibility decide the stage, and arriving counts even where the far side
// of the site is hidden.
Require(DiscoveryRules.StageFor(DiscoveryRules.VisitRadiusMetres, visible: false) == DiscoveryStage.Visited,
    "standing at a place must count as visiting it even when its far side is hidden");
Require(DiscoveryRules.StageFor(DiscoveryRules.NoticeRadiusMetres + 1, visible: true) == DiscoveryStage.None,
    "a site beyond the notice radius must not be seen");
Require(DiscoveryRules.StageFor(60, visible: true) == DiscoveryStage.Seen,
    "a visible site inside the notice radius must be seen");
Require(DiscoveryRules.StageFor(60, visible: false) == DiscoveryStage.None,
    "a site behind a ridge must not be seen from the near side of it");

// Sightlines: open ground does not block, a ridge does, and a tall target is visible
// over a rise that would hide a low one.
var open = new FlatColumns(surface: 5);
Require(DiscoveryRules.HasSightline(open, 0, 0, 6.6, 100, 0, 10),
    "open ground must not block a sightline");
Require(!DiscoveryRules.HasSightline(new RidgeColumns(height: 40, halfWidth: 4), 0, 0, 6.6, 100, 0, 10),
    "a ridge must block a sightline to a low site behind it");
Require(DiscoveryRules.HasSightline(new RidgeColumns(height: 8, halfWidth: 2), 0, 0, 6.6, 100, 0, 20),
    "a tall site must be visible over a rise that hides a low one");

// A full journal refuses rather than throwing: an exception here would be raised inside
// a product update, which costs the runtime and not just the fact.
DiscoveryState full = new(Seed);
for (int index = 0; index < PoiConstants.MaximumDiscoveryEntries; index++)
{
    PoiSite synthetic = new(index, 0, PoiKind.Ruin, index * 256, 0, 5, 4, 0, 0);
    Require(full.Notice(synthetic, DiscoveryStage.Seen, tick: index), "the journal must take entries until it is full");
}

Require(full.Count == PoiConstants.MaximumDiscoveryEntries, "the journal must hold exactly its cap");
PoiSite overflow = new(PoiConstants.MaximumDiscoveryEntries, 0, PoiKind.Ruin, 0, 0, 5, 4, 0, 0);
Require(!full.Notice(overflow, DiscoveryStage.Seen, tick: 1), "a full journal must refuse a new place");
Require(full.Refused == 1, "a refusal must be counted so the state is visible rather than silent");

// The saved form is canonical and round-trips.
DiscoverySnapshot saved = journal.Snapshot();
DiscoveryState reloaded = new(Seed);
reloaded.Restore(saved);
Require(reloaded.Count == journal.Count, "a restored journal must hold what was saved");
Require(reloaded.Snapshot().Entries.SequenceEqual(journal.Snapshot().Entries),
    "a restored journal must hold the same facts");
Require(reloaded.Find(sample.CellX, sample.CellZ) is { Stage: DiscoveryStage.Visited },
    "a restored entry must keep its stage");

// Canonical order: the same facts learned in a different order save identically.
DiscoveryState a = new(Seed);
DiscoveryState b = new(Seed);
PoiSite first = discovered[10];
PoiSite second = discovered[20];
a.Notice(first, DiscoveryStage.Seen, tick: 1);
a.Notice(second, DiscoveryStage.Visited, tick: 2);
b.Notice(second, DiscoveryStage.Visited, tick: 2);
b.Notice(first, DiscoveryStage.Seen, tick: 1);
Require(a.Snapshot().Entries.SequenceEqual(b.Snapshot().Entries),
    "the saved form must depend on the facts, not the order they were learned");

// --- the stored form -----------------------------------------------------------------
byte[] encoded = DiscoveryCodec.Encode(journal.Snapshot());
Require(encoded.Length == DiscoveryConstants.HeaderBytes + (journal.Count * DiscoveryConstants.EntryBytes),
    "a journal's stored form must be its header plus one fixed record per place");
DiscoveryState decoded = new(Seed);
decoded.Restore(DiscoveryCodec.Decode(Seed, encoded));
Require(decoded.Snapshot().Entries.SequenceEqual(journal.Snapshot().Entries),
    "the codec must round-trip every fact in a journal");

// An empty journal is a valid journal, and survives the same path.
DiscoveryState blank = new(Seed);
DiscoveryState blankBack = new(Seed);
blankBack.Restore(DiscoveryCodec.Decode(Seed, DiscoveryCodec.Encode(blank.Snapshot())));
Require(blankBack.Count == 0, "an empty journal must round-trip as empty");

// A save from another world is refused rather than read as this one's history.
bool refusedOtherWorld = false;
try
{
    DiscoveryCodec.Decode(Seed ^ 0x9e37, encoded);
}
catch (InvalidOperationException)
{
    refusedOtherWorld = true;
}

Require(refusedOtherWorld, "a journal belonging to another world must be refused");

// Truncation and a corrupted byte are both caught, so a half-written save cannot become
// a history the player never earned.
bool refusedTruncated = false;
try
{
    DiscoveryCodec.Decode(Seed, encoded.AsSpan(0, encoded.Length - 1));
}
catch (InvalidOperationException)
{
    refusedTruncated = true;
}

Require(refusedTruncated, "a truncated journal must be refused");

byte[] corrupted = (byte[])encoded.Clone();
corrupted[^1] ^= 0xFF;
bool refusedCorrupted = false;
try
{
    DiscoveryCodec.Decode(Seed, corrupted);
}
catch (InvalidOperationException)
{
    refusedCorrupted = true;
}

Require(refusedCorrupted, "a journal altered after it was written must fail its fingerprint");

// --- crossings: a dry way over narrow water, and none where there is nothing to cross ---
RiverColumns narrowRiver = new(channelWidth: 8);
RiverColumns broadRiver = new(channelWidth: 40);
CrossingPlacement rivers = new(
    new TerrainGeneratorContract(Seed, TerrainGeneratorContract.CurrentVersion, TerrainConstants.DefaultSize),
    new TestDraws(), narrowRiver, Radius);
CrossingPlacement spans = new(
    new TerrainGeneratorContract(Seed, TerrainGeneratorContract.CurrentVersion, TerrainConstants.DefaultSize),
    new TestDraws(), narrowRiver, Radius);
CrossingPlacement wide = new(
    new TerrainGeneratorContract(Seed, TerrainGeneratorContract.CurrentVersion, TerrainConstants.DefaultSize),
    new TestDraws(), broadRiver, Radius);
CrossingPlacement dryGround = new(
    new TerrainGeneratorContract(Seed, TerrainGeneratorContract.CurrentVersion, TerrainConstants.DefaultSize),
    new TestDraws(), new FlatColumns(surface: 6), Radius);

int bridges = 0;
int wideBridges = 0;
int dryBridges = 0;
int deckVoxels = 0;
int pierVoxels = 0;
int carriage = 0;
foreach ((long cellX, long cellZ) in cells)
{
    if (wide.SiteAt(cellX, cellZ) is not null)
    {
        wideBridges++;
    }

    if (dryGround.SiteAt(cellX, cellZ) is not null)
    {
        dryBridges++;
    }

    if (rivers.SiteAt(cellX, cellZ) is not CrossingSite site)
    {
        continue;
    }

    bridges++;
    Require(spans.SiteAt(cellX, cellZ) == site, $"the crossing in ({cellX},{cellZ}) must not depend on query order");
    Require(CrossingStructure.MaterialAt(site, site.FromX - PoiConstants.CrossingRampLength - 2, site.DeckY, site.FromZ).IsNone
        && CrossingStructure.MaterialAt(site, site.ToX + PoiConstants.CrossingRampLength + 2, site.DeckY, site.ToZ).IsNone
        && CrossingStructure.MaterialAt(site, site.FromX, site.DeckY + 2, site.FromZ).IsNone,
        $"{site.Id} must build nothing beyond its deck and its two ramps");
    Require(site.DeckY >= TerrainConstants.WaterLevel + 1,
        $"{site.Id} must lay its deck above the water line, found {site.DeckY}");
    Require(site.DeckY == TerrainConstants.WaterLevel + 1 || site.DeckY == 6,
        $"{site.Id} must meet a bank, found deck {site.DeckY}");

    long span = site.AlongX ? site.ToX - site.FromX : site.ToZ - site.FromZ;
    Require(span >= 0 && span < PoiConstants.CrossingMaximumSpan,
        $"{site.Id} must span less than {PoiConstants.CrossingMaximumSpan} blocks, found {span}");
    for (long step = 0; step <= span; step++)
    {
        long x = site.AlongX ? site.FromX + step : site.FromX;
        long z = site.AlongX ? site.FromZ : site.FromZ + step;
        Require(narrowRiver.ColumnAt(x, z).Surface < TerrainConstants.WaterLevel,
            $"{site.Id} must only span water, but ({x},{z}) is dry");
    }

    // The defect this asserts against: the deck is laid at the higher bank, so a full-height
    // abutment is climbable from one side and not the other. Every step down to a bank must be
    // one course, which is the character's whole step budget.
    for (long side = 0; side < 2; side++)
    {
        long previous = site.DeckY;
        for (long offset = 1; offset <= PoiConstants.CrossingRampLength; offset++)
        {
            long along = side == 0 ? -offset : span + offset;
            long x = site.AlongX ? site.FromX + along : site.FromX;
            long z = site.AlongX ? site.FromZ : site.FromZ + along;
            long top = long.MinValue;
            for (long y = site.DeckY - PoiConstants.CrossingPierDepth - PoiConstants.CrossingRampLength; y <= site.DeckY; y++)
            {
                PoiVoxel step = CrossingStructure.MaterialAt(site, x, y, z);
                if (step.Kind == PoiVoxelKind.Fill)
                {
                    top = y;
                }
            }

            Require(top != long.MinValue, $"{site.Id} must build a ramp on both sides of its deck");
            Require(previous - top <= 1,
                $"{site.Id} ramps {previous - top} courses in one block at offset {offset}: the low bank would be unclimbable");
            previous = top;
        }
    }

    long nearX = site.AlongX ? site.FromX - 1 : site.FromX;
    long nearZ = site.AlongX ? site.FromZ : site.FromZ - 1;
    long farX = site.AlongX ? site.ToX + 1 : site.ToX;
    long farZ = site.AlongX ? site.ToZ : site.ToZ + 1;
    Require(narrowRiver.ColumnAt(nearX, nearZ).Surface >= TerrainConstants.WaterLevel
        && narrowRiver.ColumnAt(farX, farZ).Surface >= TerrainConstants.WaterLevel,
        $"{site.Id} must land on a bank at both ends");

    for (long y = site.DeckY - PoiConstants.CrossingPierDepth; y <= site.DeckY; y++)
    {
        for (long x = Math.Min(site.FromX, site.ToX) - 2; x <= Math.Max(site.FromX, site.ToX) + 2; x++)
        {
            for (long z = Math.Min(site.FromZ, site.ToZ) - 2; z <= Math.Max(site.FromZ, site.ToZ) + 2; z++)
            {
                PoiVoxel voxel = CrossingStructure.MaterialAt(site, x, y, z);
                if (voxel.IsNone)
                {
                    continue;
                }

                carriage++;
                Require(voxel.Kind == PoiVoxelKind.Fill,
                    $"{site.Id} must only fill: a crossing is walked over, never a dam");
                if (voxel.Material == (ushort)BlockId.Planks)
                {
                    deckVoxels++;
                    Require(y == site.DeckY, $"{site.Id} must lay its planks at the deck height");
                }
                else if (voxel.Material == (ushort)BlockId.Cobblestone)
                {
                    pierVoxels++;
                    Require(y <= site.DeckY, $"{site.Id} must build nothing above its deck");
                }
            }
        }
    }
}

Require(bridges > 0, "narrow water must carry at least one dry crossing");
Require(wideBridges == 0, $"water 40 blocks wide must be walked around, found {wideBridges} crossings");
Require(dryBridges == 0, $"dry ground must hold no crossing, found {dryBridges}");
Require(deckVoxels > 0 && pierVoxels > 0, "a crossing must have both a deck and piers");
Require(carriage > 0, "a crossing must build something");

// --- the codec refuses every blob it cannot interpret, and the journal keeps working full ---
//
// The reviewers named these as unasserted: the dangerous one is a kind flipped to another *valid*
// kind, because nothing but the fingerprint catches it, while the others are caught by an explicit
// check that nothing exercised.
bool probeRefused(byte[] blob)
{
    try
    {
        _ = DiscoveryCodec.Decode(Seed, blob);
        return false;
    }
    catch (Exception failure) when (failure is InvalidOperationException or ArgumentException or OverflowException)
    {
        return true;
    }
}

DiscoverySnapshot probeSnapshot = new(Seed, [new DiscoveryEntry(1, -2, PoiKind.Ruin, 256, -512, DiscoveryStage.Seen, 10, 20)]);
byte[] honest = DiscoveryCodec.Encode(probeSnapshot);
Require(!probeRefused(honest), "the codec must accept a blob it wrote itself");

byte[] wrongMagic = (byte[])honest.Clone(); wrongMagic[0] ^= 0xFF;
Require(probeRefused(wrongMagic), "a blob with the wrong magic must be refused");
byte[] wrongSchema = (byte[])honest.Clone(); wrongSchema[4] = 0x7F;
Require(probeRefused(wrongSchema), "a blob written for another schema must be refused");
byte[] wrongGeneration = (byte[])honest.Clone(); wrongGeneration[8] ^= 0x01;
Require(probeRefused(wrongGeneration), "a blob written for another generation must be refused");
byte[] trailing = [.. honest, 0x00];
Require(probeRefused(trailing), "a blob with a byte appended must be refused, not read short");
byte[] hugeCount = (byte[])honest.Clone(); hugeCount[20] = 0x7F; hugeCount[21] = 0xFF;
Require(probeRefused(hugeCount), "a blob claiming more entries than it holds must be refused");
byte[] badStage = (byte[])honest.Clone(); badStage[32 + 34] = 200;
Require(probeRefused(badStage), "a blob carrying a stage that is not one must be refused");
byte[] badKind = (byte[])honest.Clone(); badKind[32 + 32] = 99;
Require(probeRefused(badKind), "a blob carrying a kind that is not one must be refused");
byte[] flippedKind = (byte[])honest.Clone(); flippedKind[32 + 32] = (byte)PoiKind.VantagePoint;
Require(probeRefused(flippedKind), "a kind altered to another valid kind must trip the fingerprint");
Require(probeRefused([.. honest, .. honest]), "a blob of twice the length must be refused");
Require(probeRefused(new byte[DiscoveryConstants.MaximumJournalBytes + 64]), "a blob larger than the journal may ever be must be refused");
try
{
    _ = new DiscoverySnapshot(Seed, [new DiscoveryEntry(3, 4, PoiKind.Ruin, 0, 0, DiscoveryStage.Seen, 500, 400)]);
    Require(false, "a snapshot whose last tick precedes its first must be refused");
}
catch (InvalidOperationException)
{
}

try
{
    _ = new DiscoverySnapshot(Seed, [new DiscoveryEntry(3, 4, PoiKind.Ruin, 0, 0, DiscoveryStage.Seen, -1, 5)]);
    Require(false, "a snapshot with a negative first-seen tick must be refused");
}
catch (InvalidOperationException)
{
}

DiscoveryState probeRestored = new(Seed);
probeRestored.Restore(probeSnapshot);
Require(probeRestored.Count == 1, "restore must accept a snapshot for its own seed");
DiscoveryState probeOtherSeed = new(Seed + 1);
try
{
    probeOtherSeed.Restore(probeSnapshot);
    Require(false, "restoring a snapshot of another seed must throw");
}
catch (InvalidOperationException)
{
}

// A full journal still promotes what it already knows: refusing new places must not freeze the
// ones it holds, or a player at the cap would stop being told they reached somewhere new.
DiscoveryState capped = new(Seed);

for (long cell = 0; cell < PoiConstants.MaximumDiscoveryEntries; cell++)
{
    PoiSite site = new(cell, 0, PoiKind.Ruin, cell * PoiConstants.CellSize, 0, 4, 6, 0, 0);
    capped.Notice(site, DiscoveryStage.Seen, 1);

}

Require(capped.Count == PoiConstants.MaximumDiscoveryEntries, $"a journal must hold {PoiConstants.MaximumDiscoveryEntries} places, held {capped.Count}");
PoiSite heldFirst = new(0, 0, PoiKind.Ruin, 0, 0, 4, 6, 0, 0);
Require(capped.Notice(heldFirst, DiscoveryStage.Visited, 2), "a full journal must still promote a place it already holds");
Require(capped.Find(0, 0)?.Stage == DiscoveryStage.Visited, "the promotion must be visible");
PoiSite extra = new(PoiConstants.MaximumDiscoveryEntries, 0, PoiKind.Ruin, 999_999, 0, 4, 6, 0, 0);
Require(!capped.Notice(extra, DiscoveryStage.Seen, 3), "a full journal must refuse a place it does not hold");
Require(capped.Refused > 0, "a refusal must be counted, not silent");

// The notice radius is a boundary, and exactly on it is inside.
Require(DiscoveryRules.StageFor(DiscoveryRules.NoticeRadiusMetres, visible: true) == DiscoveryStage.Seen,
    "a place exactly at the notice radius, with a sightline, must be seen");
Require(DiscoveryRules.StageFor(DiscoveryRules.NoticeRadiusMetres + 0.01, visible: true) == DiscoveryStage.None,
    "a place past the notice radius must not be seen, however visible");
Require(DiscoveryRules.StageFor(double.NaN, visible: true) == DiscoveryStage.None,
    "a position that is not a number must notice nothing rather than throw");

Console.WriteLine(
    $"Discovery rules: site determinism across {cells.Count} anchor cells, order independence, seed and "
    + $"version sensitivity, ground gating, all {kindCounts.Count} kinds reachable, structure bounds, the carve "
    + $"floor, site identity, the noticing policy, the sightline rule, a full journal that refuses instead of "
    + $"throwing, a canonical round trip, and the stored form with its identity, truncation and "
    + $"fingerprint refusals, and crossings with their span, bank and deck rules plus the two "
    + $"negative cases passed ({sites} sites, {carved} carved voxels, {bridges} crossings).");

// --- test doubles ---------------------------------------------------------------
// The draw port is Engine-backed in the product; here it only has to be a pure
// function of (scope, key, seed), which is the property the contract promises.
sealed class TestDraws : ITerrainDraws
{
    public long Draw(string scope, string key, ulong seed, long minimum, long maximum)
    {
        ulong hash = 0xcbf2_9ce4_8422_2325UL;
        foreach (char character in scope)
        {
            hash = unchecked((hash ^ character) * 0x100_0000_01b3UL);
        }

        foreach (char character in key)
        {
            hash = unchecked((hash ^ character) * 0x100_0000_01b3UL);
        }

        hash = unchecked(hash ^ seed);
        hash = unchecked(hash + 0x9e37_79b9_7f4a_7c15UL);
        hash = unchecked((hash ^ (hash >> 30)) * 0xbf58_476d_1ce4_e5b9UL);
        hash = unchecked((hash ^ (hash >> 27)) * 0x94d0_49bb_1331_11ebUL);
        hash ^= hash >> 31;
        return minimum + (long)(hash % (ulong)(maximum - minimum + 1));
    }
}

// Ground shaped like the recipe's: broad noise with relief steep enough to hold a
// way in, and flat stretches that cannot.
sealed class RollingColumns : ITerrainColumns
{
    private readonly long baseHeight;

    public RollingColumns(long baseHeight) => this.baseHeight = baseHeight;

    public TerrainColumn ColumnAt(long x, long z) => new(Surface(x, z), Slope(x, z));

    private long Surface(long x, long z) => baseHeight
        + (long)Math.Round(6.0 * Math.Sin(x / 9.0), MidpointRounding.AwayFromZero)
        + (long)Math.Round(6.0 * Math.Cos(z / 11.0), MidpointRounding.AwayFromZero)
        + (long)Math.Round(3.0 * Math.Sin((x + z) / 5.0), MidpointRounding.AwayFromZero);

    private long Slope(long x, long z)
    {
        long top = Surface(x, z);
        return Math.Max(
            Math.Max(Math.Abs(top - Surface(x - 1, z)), Math.Abs(top - Surface(x + 1, z))),
            Math.Max(Math.Abs(top - Surface(x, z - 1)), Math.Abs(top - Surface(x, z + 1))));
    }
}

// Perfectly flat ground: no hillside, so nothing that needs relief may stand there.
sealed class FlatColumns : ITerrainColumns
{
    private readonly long surface;

    public FlatColumns(long surface) => this.surface = surface;

    public TerrainColumn ColumnAt(long x, long z) => new(surface, 0);
}

// A ridge across the path, for the sightline cases: every column inside the half-width
// stands at the given height, and the rest of the world is low ground.
sealed class RidgeColumns : ITerrainColumns
{
    private readonly long height;
    private readonly long halfWidth;

    public RidgeColumns(long height, long halfWidth)
    {
        this.height = height;
        this.halfWidth = halfWidth;
    }

    public TerrainColumn ColumnAt(long x, long z) =>
        new(Math.Abs(x) <= halfWidth ? height : 5, 0);
}

// Land with a north-south river: a channel of the given width every sixty-four blocks. A
// narrow one is spanned, a wide one is not, and the same double proves both.
sealed class RiverColumns : ITerrainColumns
{
    private readonly long channelWidth;

    public RiverColumns(long channelWidth) => this.channelWidth = channelWidth;

    public TerrainColumn ColumnAt(long x, long z)
    {
        long channel = ((x % 64) + 64) % 64;
        return new TerrainColumn(channel < channelWidth ? 0 : 6, 0);
    }
}
