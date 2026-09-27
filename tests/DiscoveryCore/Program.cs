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
DiscoveryState journal = new();

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
Require(journal.Find(sample.Id) is { Stage: DiscoveryStage.Visited, FirstSeenTick: 10, LastTick: 20 },
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
DiscoveryState full = new();
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
DiscoveryState reloaded = new();
reloaded.Restore(saved);
Require(reloaded.Count == journal.Count, "a restored journal must hold what was saved");
Require(reloaded.Snapshot().Entries.SequenceEqual(journal.Snapshot().Entries),
    "a restored journal must hold the same facts");
Require(reloaded.Find(sample.Id) is { Stage: DiscoveryStage.Visited },
    "a restored entry must keep its stage");

// Canonical order: the same facts learned in a different order save identically.
DiscoveryState a = new();
DiscoveryState b = new();
PoiSite first = discovered[10];
PoiSite second = discovered[20];
a.Notice(first, DiscoveryStage.Seen, tick: 1);
a.Notice(second, DiscoveryStage.Visited, tick: 2);
b.Notice(second, DiscoveryStage.Visited, tick: 2);
b.Notice(first, DiscoveryStage.Seen, tick: 1);
Require(a.Snapshot().Entries.SequenceEqual(b.Snapshot().Entries),
    "the saved form must depend on the facts, not the order they were learned");

Console.WriteLine(
    $"Discovery rules: site determinism across {cells.Count} anchor cells, order independence, seed and "
    + $"version sensitivity, ground gating, all {kindCounts.Count} kinds reachable, structure bounds, the carve "
    + $"floor, site identity, the noticing policy, the sightline rule, a full journal that refuses instead of "
    + $"throwing, and a canonical round trip passed ({sites} sites, {carved} carved voxels).");

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
