using CraftSurvive.Game.Modules.Discovery;
using System.Security.Cryptography;
using System.Buffers.Binary;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Tests;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Rpg;

PlayerInputChecks.Run();

// Material snapshots taken before the residency/column optimization. Cover
// authored landmarks, boundaries, negative coordinates, layers, and two seeds.
// The hashes moved once, deliberately: campaign #8595's S1 removed the
// hand-placed testbed furniture (traversal route, clearing, gaps, trench,
// bridge, pillars) from the world recipe rather than carrying it into the world
// model. Every overlap, ordering, payload-reuse, edit and eviction check below
// is unchanged and still passes.
foreach (ulong seed in new[] { TerrainConstants.DefaultSeed, 12345UL })
{
    TerrainConfiguration config = new(seed, TerrainConstants.DefaultSize);
    var generator = new TerrainChunkGenerator(config.CreateRecipe(new TestDraws(seed)));
    var overlay = new TerrainOverlayState(seed);
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] bytes = new byte[TerrainConstants.ChunkVolume * sizeof(ushort)];
    foreach (long x in new long[] { -3, -2, -1, 0, 1, 2, 3 })
    foreach (long z in new long[] { -2, 0, 2 })
    foreach (long y in new long[] { -1, 0, 1 })
    {
        TerrainChunk chunk = generator.Generate(new(x, y, z), overlay.Snapshot());
        for (int i = 0; i < chunk.Materials.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort)), chunk.Materials.Span[i]);
        hash.AppendData(bytes);
    }
    // Moved deliberately at each generation change: version 4 placed surface
    // features, version 5 added water, version 6 gave the world an authored bedrock
    // floor and border. All three sit in the ground band these snapshots cover.
    // Moved at version 6, which gave the world an authored bedrock floor and border
    // at the settled ~100 km2 extent: the old 96 m wall no longer stands inside the
    // sampled box, and the floor still does.
    // Moved at version 7, which replaced the hand-placed landmark pillars with drawn
    // points of interest. The sampled box holds no site, so what moved here is the
    // surface features: a version bump changes every draw key by construction, so every
    // tree is redrawn. Confirmed by reverting the version to 6 and watching this hash
    // return to the value below, which is what rules out an accidental terrain change.
    string expected = seed == TerrainConstants.DefaultSeed
        ? "BD110A823FFDD38C7132E98EDF7808864CA345EFCBD9CD8B8B2401A37679BF72"
        : "C6615AFC15E3F3AFECB745D5B4F617FB3A3C34B74176FD72865BFD2ED2FB9F72";
    string actual = Convert.ToHexString(hash.GetHashAndReset());
    Require(actual == expected, $"authored material snapshot changed: {actual}");
}

// Cross-order agreement: two neighbours must produce identical voxels whichever
// one is generated first, including a tree that overhangs the boundary. The two
// passes use separate recipes, so nothing is shared but the contract.
{
    TerrainConfiguration config = TerrainConfiguration.TraversalShowcase;
    TerrainOverlayState snapshot = new(config.Seed);
    TerrainChunkAddress[] pairs = [new(0, 1, 0), new(1, 1, 0), new(0, 1, 1), new(-1, 1, 0)];
    foreach (TerrainChunkAddress left in pairs)
    {
        foreach (TerrainChunkAddress right in pairs)
        {
            var forward = new TerrainChunkGenerator(config.CreateRecipe(new TestDraws(config.Seed)));
            TerrainChunk a = forward.Generate(left, snapshot.Snapshot());
            TerrainChunk b = forward.Generate(right, snapshot.Snapshot());

            var reverse = new TerrainChunkGenerator(config.CreateRecipe(new TestDraws(config.Seed)));
            TerrainChunk bReversed = reverse.Generate(right, snapshot.Snapshot());
            TerrainChunk aReversed = reverse.Generate(left, snapshot.Snapshot());

            Require(a.Materials.Span.SequenceEqual(aReversed.Materials.Span),
                $"chunk {left} depends on generation order");
            Require(b.Materials.Span.SequenceEqual(bReversed.Materials.Span),
                $"chunk {right} depends on generation order");
        }
    }

    Console.WriteLine("Terrain generation agrees across chunk order, including overhanging features.");
}

// Generation snapshot over the height band where surface features live. While the
// Engine binds only three base materials, feature voxels cannot be placed (see
// BlockRegistry.BoundBlocks), so this currently pins the terrain field and its
// feature draws; it starts covering placed features, and moves deliberately, once
// the material capacity lands.
{
    TerrainConfiguration config = TerrainConfiguration.TraversalShowcase;
    var generator = new TerrainChunkGenerator(config.CreateRecipe(new TestDraws(config.Seed)));
    var overlay = new TerrainOverlayState(config.Seed);
    long featureVoxels = 0;
    long waterVoxels = 0;
    long bedrockVoxels = 0;
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] bytes = new byte[TerrainConstants.ChunkVolume * sizeof(ushort)];
    // A 64x64 column window, so the snapshot contains whole anchor cells and the
    // trees they own: a smaller box can legitimately hold no tree at all.
    // The whole 96x96 world in x/z: 144 anchor cells at one tree in twenty-four
    // is about six trees, so a deterministic zero here means placement broke
    // rather than that the sample was unlucky.
    for (long x = -3; x <= 2; x++)
    for (long z = -3; z <= 2; z++)
    // Chunk coordinates: y -1..1 is world y -16..31, the band the ground and its
    // features actually occupy.
    for (long y = -1; y <= 1; y++)
    {
        TerrainChunk chunk = generator.Generate(new(x, y, z), overlay.Snapshot());
        for (int i = 0; i < chunk.Materials.Length; i++)
        {
            ushort material = chunk.Materials.Span[i];
            if (material == (ushort)BlockId.Log || material == (ushort)BlockId.Leaves)
                featureVoxels++;

            if (material == (ushort)BlockId.Water)
                waterVoxels++;

            if (material == (ushort)BlockId.Bedrock)
                bedrockVoxels++;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort)), material);
        }

        hash.AppendData(bytes);
    }

    string featureHash = Convert.ToHexString(hash.GetHashAndReset());
    // Pinned against the managed draw port; the live lane prints the same snapshot
    // through the Engine's keyed RNG. Both hash real voxels now: the feature pass places
    // trees, and version 7 added structure voxels to this box. Moved at version 7, which
    // changes every draw key and so redraws every feature in it.

    const string ExpectedFeatureHash = "FD75840EEDDFAA9CF118147563F044653A68CC4A9D3EBB24E522C6A64B041C1A";
    Console.WriteLine(
        $"Terrain features, water and world edges placed and deterministic: {featureVoxels} feature, " +
        $"{waterVoxels} water, {bedrockVoxels} bedrock voxels, {featureHash}");
    // The hashed box is a sample, and which trees fall inside it is the version's draw to
    // decide: pinning "the box holds a tree" made the check depend on that accident rather
    // than on the pass working. The invariant is that the world places features at all, so it
    // is answered by looking for one across a region wide enough that a world without trees
    // cannot pass by chance.
    {
        TerrainConfiguration scanConfig = TerrainConfiguration.TraversalShowcase;
        var scanGenerator = new TerrainChunkGenerator(scanConfig.CreateRecipe(new TestDraws(scanConfig.Seed)));
        TerrainOverlayState scanOverlay = new(scanConfig.Seed);
        long scanFeatures = 0;
        for (long chunkX = -8; chunkX <= 8 && scanFeatures == 0; chunkX++)
        {
            for (long chunkZ = -8; chunkZ <= 8 && scanFeatures == 0; chunkZ++)
            {
                for (long chunkY = 0; chunkY <= 2 && scanFeatures == 0; chunkY++)
                {
                    TerrainChunk scan = scanGenerator.Generate(new(chunkX, chunkY, chunkZ), scanOverlay.Snapshot());
                    foreach (ushort material in scan.Materials.Span)
                    {
                        if (material == (ushort)BlockId.Log || material == (ushort)BlockId.Leaves)
                        {
                            scanFeatures++;
                        }
                    }
                }
            }
        }

        Require(scanFeatures > 0, "the surface feature pass placed no feature voxel anywhere in the scanned region");
    }


    Require(waterVoxels > 0, "the water pass placed no water voxel");
    Require(bedrockVoxels > 0, "the world has no authored bedrock floor");
    Require(featureHash == ExpectedFeatureHash, "surface, water and border snapshot changed");
}

// The chunk cache's payload and key, proven lossless before anything is wired to a
// store: a cache that silently corrupts a chunk is worse than no cache at all.
{
    TerrainConfiguration config = new(TerrainConstants.DefaultSeed, TerrainConstants.DefaultSize);
    var generator = new TerrainChunkGenerator(config.CreateRecipe(new TestDraws(config.Seed)));
    TerrainOverlaySnapshot snapshot = new TerrainOverlayState(config.Seed).Snapshot();
    TerrainChunkAddress address = new(1, 0, -2);
    TerrainChunk chunk = generator.Generate(address, snapshot);
    byte[] encoded = TerrainChunkCachePayload.Encode(chunk.Materials.Span);
    Require(encoded.Length == TerrainChunkCachePayload.HeaderLength + (chunk.Materials.Length * sizeof(ushort)),
        "the cached payload is not the size of the chunk it carries");
    Require(TerrainChunkCachePayload.TryDecode(encoded, out ushort[] decoded), "a freshly encoded payload did not decode");
    Require(decoded.AsSpan().SequenceEqual(chunk.Materials.Span), "a chunk did not survive the cache payload round trip");

    // A payload that is truncated, extended or foreign must be refused rather than
    // reinterpreted: a partial chunk would be a silently wrong world.
    Require(!TerrainChunkCachePayload.TryDecode(encoded.AsSpan(0, encoded.Length - 2), out _), "a truncated payload was accepted");
    Require(!TerrainChunkCachePayload.TryDecode([1, 2, 3, 4, 5, 6, 7, 8], out _), "a foreign payload was accepted");
    Require(!TerrainChunkCachePayload.TryDecode(ReadOnlySpan<byte>.Empty, out _), "an empty payload was accepted");

    string key = TerrainChunkCacheKey.For(config.Contract, address);
    Require(key == TerrainChunkCacheKey.For(config.Contract, address), "a cache key is not stable for the same chunk");
    Require(key != TerrainChunkCacheKey.For(config.Contract, new TerrainChunkAddress(1, 0, -1)), "two chunks share a cache key");
    Require(key != TerrainChunkCacheKey.For(config.Contract with { Version = config.Contract.Version + 1 }, address),
        "a generation version bump did not change the cache key");
    Require(key != TerrainChunkCacheKey.For(config.Contract with { Seed = config.Contract.Seed + 1 }, address),
        "a different world seed did not change the cache key");
    Console.WriteLine($"Chunk cache payload and key verified: {encoded.Length} bytes, key {key}");
}

// The chunk content predicate against generation itself: the predicate must never claim
// a chunk is empty when generating it produces voxels, because the residency policy will
// use it to decide what to evict. The reverse - claiming content for an empty chunk - only
// costs a retained empty chunk, so it is reported rather than failed.
{
    long falseEmpties = 0;
    long falseContents = 0;
    long compared = 0;
    foreach (ulong seed in new[] { TerrainConstants.DefaultSeed, 12345UL, 777UL })
    {
        TerrainConfiguration config = new(seed, TerrainConstants.DefaultSize);
        var generator = new TerrainChunkGenerator(config.CreateRecipe(new TestDraws(seed)));
        TerrainOverlaySnapshot snapshot = new TerrainOverlayState(seed).Snapshot();
        for (long x = -4; x <= 4; x += 2)
        for (long z = -4; z <= 4; z += 2)
        for (long y = -1; y <= 1; y++)
        {
            TerrainChunkAddress address = new(x, y, z);
            bool generated = generator.Generate(address, snapshot).SolidVoxelCount > 0;
            bool predicted = config.CreateRecipe(new TestDraws(seed)).ChunkHasContent(address);
            compared++;
            if (generated && !predicted)
            {
                falseEmpties++;
            }

            if (!generated && predicted)
            {
                falseContents++;
            }
        }
    }

    Require(falseEmpties == 0, $"{falseEmpties} chunks were called empty but generate voxels");
    Require(falseContents == 0, $"{falseContents} chunks were called content but generate nothing");
    Console.WriteLine($"Chunk content predicate matched generation exactly on all {compared} chunks");

// Every kind the placement contract draws must actually stand somewhere in the real world.
//
// This exists because two of the five did not. The relief a cave mouth is cut into was gated on
// a single-block slope, which this terrain's gentle noise almost never produces, so every cave
// mouth and dungeon entrance was reconciled into standing stones - a rule that looked reasonable
// and placed nothing. A census over the fakes could not see it: their hillsides are steep. Only
// the real recipe can answer it, which is why the check lives here.
{
    TerrainConfiguration censusConfig = TerrainConfiguration.TraversalShowcase;
    var censusRecipe = censusConfig.CreateRecipe(new TestDraws(censusConfig.Seed));
    Dictionary<PoiKind, int> census = [];
    int censusSites = 0;
    for (long cellX = -12; cellX <= 12; cellX++)
    {
        for (long cellZ = -12; cellZ <= 12; cellZ++)
        {
            if (censusRecipe.Placement.SiteAt(cellX, cellZ) is not PoiSite site)
            {
                continue;
            }

            censusSites++;
            census[site.Kind] = census.GetValueOrDefault(site.Kind) + 1;
        }
    }

    string counts = string.Join(", ", census.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));
    Console.WriteLine($"Point-of-interest census over {censusSites} real sites: {counts}");

// A decided cell set is applied exactly as given - that is the whole point of it - and it is
// bounded, because the bound is the manipulation slice's promise about update latency.
{
    VoxelAddress[] decided = [new(1, 2, 3), new(4, 5, 6), new(7, 8, 9)];
    TerrainVoxelEdit[] placed = TerrainBrushPolicy.Expand(
        TerrainEditRequest.FromCells(decided, TerrainEditKind.Set, TerrainConstants.StoneMaterial));
    Require(placed.Length == decided.Length, $"a decided edit must apply exactly its {decided.Length} cells, applied {placed.Length}");
    Require(placed[0].Address == decided[0] && placed[2].Address == decided[2],
        "a decided edit must keep the order and addresses it was given");
    Require(placed.All(edit => edit.Material == TerrainConstants.StoneMaterial),
        "a decided set must carry the material it was given");

    TerrainVoxelEdit[] cleared = TerrainBrushPolicy.Expand(
        TerrainEditRequest.FromCells(decided, TerrainEditKind.Clear, TerrainConstants.StoneMaterial));
    Require(cleared.All(edit => edit.Material == TerrainConstants.EmptyMaterial),
        "a decided clear must empty every cell it was given, whatever material it was handed");

    try
    {
        _ = TerrainBrushPolicy.Expand(TerrainEditRequest.FromCells(
            Enumerable.Repeat(new VoxelAddress(0, 0, 0), TerrainBrushPolicy.MaximumTransactionCells + 1).ToArray(),
            TerrainEditKind.Clear,
            TerrainConstants.EmptyMaterial));
        Require(false, "a decided edit larger than the transaction bound must be refused");
    }
    catch (ArgumentOutOfRangeException)
    {
    }

    Console.WriteLine($"Decided-cell edit request: {placed.Length} cells applied exactly, and {TerrainBrushPolicy.MaximumTransactionCells} is the bound.");

    // The blast policy, exercised at its boundaries rather than only in the middle. A charge
    // resolves as one transaction or not at all - staging was removed after measurement showed it
    // pays the Engine's per-transaction cost once per stage.
    Require(BlastPolicy.Decide(1).Disposition == BlastDisposition.Single, "a one-cell charge must resolve in one transaction");
    Require(BlastPolicy.Decide(BlastPolicy.MaximumCells).Applies, "a charge at the maximum must still fire");
    Require(BlastPolicy.Decide(BlastPolicy.MaximumCells + 1).Disposition == BlastDisposition.Refused,
        "a charge past the maximum must be refused rather than truncated");
    Require(!BlastPolicy.Decide(BlastPolicy.MaximumCells + 1).Applies, "a refused charge must not apply");
    Require(BlastPolicy.Decide(BlastPolicy.MaximumCells).Cells == BlastPolicy.MaximumCells,
        "an admission must report the charge it decided on");
    Console.WriteLine($"Blast policy: one transaction up to {BlastPolicy.MaximumCells} cells, costing a measured {BlastPolicy.MeasuredTransactionMilliseconds} ms stall the presentation must cover; beyond that refused.");

    // A charge resolves through the policy in exactly one transaction, and every cell is accounted
    // for whether or not the world had anything to remove there.
    BlastSequence small = BlastSequence.Plan(new VoxelAddress(100, 8, 100), 2);
    Require(small.Admission.Disposition == BlastDisposition.Single,
        $"a radius-2 charge is {small.Admission.Cells} cells and must resolve in one transaction");
    int deliveredSmall = 0;
    int smallStages = 0;
    while (small.Pending && small.Advance(cells => { deliveredSmall += cells.Count; return true; }))
    {
        smallStages++;
        Require(smallStages <= 1, "a charge must not take more than one transaction");
    }
    Require(smallStages == 1 && deliveredSmall == small.Admission.Cells,
        $"a charge must deliver all {small.Admission.Cells} cells in one transaction, delivered {deliveredSmall} in {smallStages}");

    BlastSequence large = BlastSequence.Plan(new VoxelAddress(200, 8, 200), 3);
    Require(large.Admission.Disposition == BlastDisposition.Single,
        $"a radius-3 charge is {large.Admission.Cells} cells and still resolves in one transaction");
    int deliveredLarge = 0;
    int largeStages = 0;
    while (large.Pending && large.Advance(cells => { deliveredLarge += cells.Count; return true; }))
    {
        largeStages++;
    }
    Require(largeStages == 1 && deliveredLarge == large.Admission.Cells,
        $"a large charge must deliver all {large.Admission.Cells} cells in one transaction, delivered {deliveredLarge} in {largeStages}");

    // A refused charge changes nothing, and says so rather than shrinking itself.
    BlastSequence tooBig = BlastSequence.Plan(new VoxelAddress(300, 8, 300), 6);
    Require(tooBig.Admission.Disposition == BlastDisposition.Refused,
        $"a radius-6 charge is {tooBig.Admission.Cells} cells and must be refused");
    Require(!tooBig.Pending, "a refused charge must not be pending");
    bool touched = false;
    Require(!tooBig.Advance(_ => { touched = true; return true; }), "a refused charge must not apply");
    Require(!touched, "a refused charge must not touch the world at all");
    Console.WriteLine($"Blast sequence: radius 2 ({small.Admission.Cells} cells) and radius 3 ({large.Admission.Cells} cells) each in one transaction, radius 6 refused ({tooBig.Admission.Cells} cells).");
}
    Require(censusSites > 400, $"the world must place sites across a wide region, found {censusSites}");
    foreach (PoiKind kind in new[]
        { PoiKind.StandingStones, PoiKind.Ruin, PoiKind.CaveMouth, PoiKind.DungeonEntrance, PoiKind.VantagePoint })
    {
        Require(census.GetValueOrDefault(kind) > 0,
            $"the real world must carry at least one {kind}, counts were {counts}");
    }
}

}

var configuration = TerrainConfiguration.TraversalShowcase;
var recipe = configuration.CreateRecipe(new TestDraws(configuration.Seed));
var chunkGenerator = new TerrainChunkGenerator(recipe);
var state = new TerrainOverlayState(configuration.Seed);
var policy = new TerrainResidencyPolicy(recipe, chunkGenerator);
TerrainChunkAddress center = new(0, 0, 0);
TerrainChunkAddress unchanged = new(0, 0, 0);
TerrainChunkAddress edited = new(0, 1, 0);
var first = policy.PlanFor(center, state);
var neighbor = policy.PlanFor(new(1, 0, 0), state);
Require(ReferenceEquals(first.Chunk(unchanged), neighbor.Chunk(unchanged)), "boundary crossing regenerated overlap");
Require(ReferenceEquals(neighbor, policy.PlanFor(new(1, 0, 0), state)), "stationary plan was rebuilt");
foreach (long x in new long[] { -3, -1, 0, 1, 2, 5, 0 })
    CheckAgainstFullScan(new(x, 0, 0));

var beforeEdit = policy.PlanFor(center, state);
Require(!beforeEdit.Requested.Contains(edited), "test column must begin empty");
VoxelAddress voxel = new(1, 20, 1);
var receipt = state.Apply(new TerrainEditAccepted([new(voxel, TerrainConstants.StoneMaterial)]));
policy.RefreshAfterOverlayChange(state, receipt);
var afterEdit = policy.PlanFor(center, state);
Require(afterEdit.Requested.Contains(edited), "newly occupied chunk was not requested");
Require(afterEdit.Chunk(edited).Materials.Span[4 * TerrainConstants.ChunkEdgeLength + 1 + TerrainConstants.ChunkPlaneLength] == TerrainConstants.StoneMaterial,
    "prepared payload did not contain the edit");
Require(ReferenceEquals(beforeEdit.Chunk(unchanged), afterEdit.Chunk(unchanged)), "edit regenerated untouched chunk");
Require(beforeEdit.Chunk(edited).SolidVoxelCount == 0, "edit mutated an earlier plan payload");
CheckAgainstFullScan(center);
receipt = state.Apply(new TerrainEditAccepted([new(voxel, TerrainConstants.EmptyMaterial)]));
policy.RefreshAfterOverlayChange(state, receipt);
Require(!policy.PlanFor(center, state).Requested.Contains(edited), "cleared chunk remained requested");
// An unreported revision change/restore must not reuse stale cached payloads.
state.Apply(new TerrainEditAccepted([new(voxel, TerrainConstants.StoneMaterial)]));
Require(policy.PlanFor(center, state).Requested.Contains(edited), "unreported edit reused stale payload");
state.Restore(new TerrainOverlaySnapshot(configuration.Seed, []));
Require(!policy.PlanFor(center, state).Requested.Contains(edited), "restore reused stale payload");
CheckAgainstFullScan(new(-2, 1, -1));
var distant = policy.PlanFor(new(12, 0, 0), state);
try { distant.Chunk(unchanged); throw new Exception("out-of-window payload was retained"); }
catch (KeyNotFoundException) { }

// Encounters against the real world: the water invariant must hold on generated
// terrain, not only on hypothetical sites.
{
    TerrainRecipe encounterRecipe = configuration.CreateRecipe(new TestDraws(configuration.Seed));
    TerrainEncounterFacts facts = new(encounterRecipe);
    long waterX = 0, waterZ = 0, landX = 0, landZ = 0;
    bool foundWater = false, foundLand = false;
    for (long x = -96; x <= 96 && !(foundWater && foundLand); x += 2)
    {
        for (long z = -96; z <= 96 && !(foundWater && foundLand); z += 2)
        {
            long surface = encounterRecipe.SurfaceAt(x, z);
            if (!foundWater && surface < facts.WaterLevel) { waterX = x; waterZ = z; foundWater = true; }
            if (!foundLand && surface > facts.WaterLevel) { landX = x; landZ = z; foundLand = true; }
        }
    }

    Require(foundWater, "the sample window contained no water column to place against");
    Require(foundLand, "the sample window contained no land column to place against");

    Require(facts.TryDescribe(RegionKind.Wilderness, 1, landX, landZ, out EncounterSite landSite),
        "a land column inside the world must be describable");
    Require(landSite.SurfaceY > landSite.WaterLevel, "a land column must describe ground above the water line");
    Require(SpawnRules.Evaluate(landSite.ToSpawnSite(), CreatureTraits.Walker).Allowed,
        "a walker must be allowed on real land");

    Require(facts.TryDescribe(RegionKind.Wilderness, 1, waterX, waterZ, out EncounterSite waterSite),
        "a water column inside the world must be describable");
    SpawnVerdict walkerInWater = SpawnRules.Evaluate(waterSite.ToSpawnSite(), CreatureTraits.Walker);
    Require(!walkerInWater.Allowed && walkerInWater.Refusal == SpawnRefusal.SubmergedWithoutSwimming,
        $"a walker was allowed into real water at ({waterX}, {waterZ}): {walkerInWater.Reason}");

    // The provider's shore claim must agree with an independent scan of the same radius.
    bool shoreWithinReach = false;
    for (long offsetX = -TerrainEncounterFacts.ShoreReachRadius; offsetX <= TerrainEncounterFacts.ShoreReachRadius && !shoreWithinReach; offsetX += 4)
    {
        for (long offsetZ = -TerrainEncounterFacts.ShoreReachRadius; offsetZ <= TerrainEncounterFacts.ShoreReachRadius && !shoreWithinReach; offsetZ += 4)
        {
            long candidateX = waterX + offsetX;
            long candidateZ = waterZ + offsetZ;
            if (facts.IsInside(candidateX, candidateZ) && encounterRecipe.SurfaceAt(candidateX, candidateZ) >= facts.WaterLevel)
            {
                shoreWithinReach = true;
            }
        }
    }

    Require(waterSite.ShoreIsReachable == shoreWithinReach,
        "the shore reachability the site reports must match the terrain around it");
    SpawnVerdict swimmer = SpawnRules.Evaluate(waterSite.ToSpawnSite(), CreatureTraits.Amphibious);
    Require(swimmer.Allowed == waterSite.ShoreIsReachable,
        "a swimmer may only be placed in water it can leave");

    Require(!facts.TryDescribe(RegionKind.Wilderness, 1, TerrainConstants.DefaultSize, 0, out _),
        "a column outside the finite world must be refused");
    Console.WriteLine(
        $"Encounters on generated terrain: land at ({landX}, {landZ}) surface {landSite.SurfaceY}, water at ({waterX}, {waterZ}) surface {waterSite.SurfaceY}, " +
        $"water level {facts.WaterLevel}, shore reachable {waterSite.ShoreIsReachable}; walkers refused in water, swimmers allowed only with a shore");
}

Console.WriteLine("Player input plus terrain materials, residency overlap, ordering, payload reuse, edits, restore and eviction passed.");

void CheckAgainstFullScan(TerrainChunkAddress location)
{
    var snapshot = state.Snapshot();
    var populated = new List<TerrainChunkAddress>();
    // Scanned to the policy's retained radius, so the full scan covers every
    // neighbourhood the plan can retain.
    for (long x = location.X - TerrainConstants.RetainedChunkRadius; x <= location.X + TerrainConstants.RetainedChunkRadius; x++)
    for (long z = location.Z - TerrainConstants.RetainedChunkRadius; z <= location.Z + TerrainConstants.RetainedChunkRadius; z++)
    for (long y = -1; y <= 1; y++)
    {
        TerrainChunkAddress address = new(x, y, z);
        var generated = chunkGenerator.Generate(address, snapshot);
        var planned = policy.PlanFor(location, state).Chunk(address);
        Require(generated.Materials.Span.SequenceEqual(planned.Materials.Span), "planned material payload differs from fresh generation");
        if (generated.SolidVoxelCount > 0) populated.Add(address);
    }
    var ordered = populated.OrderBy(a => ((a.X-location.X)*(a.X-location.X)+(a.Z-location.Z)*(a.Z-location.Z), a.Y, a)).ToArray();
    // Derived from the policy's own radius, so widening the stream window does not
    // silently invalidate this check.
    var expectedRequested = ordered.Where(a =>
        Math.Abs(a.X - location.X) <= TerrainConstants.RequestedChunkRadius
        && Math.Abs(a.Z - location.Z) <= TerrainConstants.RequestedChunkRadius);
    var plan = policy.PlanFor(location, state);
    Require(plan.Requested.SequenceEqual(expectedRequested), "request priority/occupancy differs from full scan");
    Require(plan.Retained.SequenceEqual(ordered.Take(TerrainConstants.MaximumResidentChunks)), "retained priority/occupancy differs from full scan");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}


/// <summary>
/// A deterministic draw port for the managed lanes. The Engine's keyed RNG is the
/// production source and the live golden test pins it; this one exists so the
/// generation contract's properties - order independence, neighbour agreement,
/// feature determinism - can be checked without an Engine context.
/// </summary>
internal sealed class TestDraws(ulong seed) : ITerrainDraws
{
    private readonly ulong seed = seed;

    public long Draw(string scope, string key, ulong drawSeed, long minimum, long maximum)
    {
        ulong value = seed ^ drawSeed;
        foreach (char character in scope)
        {
            value = Mix(value, character);
        }

        foreach (char character in key)
        {
            value = Mix(value, character);
        }

        ulong span = (ulong)(maximum - minimum + 1);
        return minimum + (long)(value % span);
    }

    private static ulong Mix(ulong value, int next)
    {
        unchecked
        {
            value ^= (ulong)next;
            value *= 0x100000001b3UL;
            value ^= value >> 29;
            return value;
        }
    }
}
