using System.Security.Cryptography;
using System.Buffers.Binary;
using CraftSurvive.Game.Modules.Terrain;
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
    string expected = seed == TerrainConstants.DefaultSeed
        ? "FC644BCCC386AD20487A721577A0F25F1521372FF1BA71FD7D3F787892A6595C"
        : "E6D06618050AF228DE2FF28604C84DA68F34E8B405037A3588BFA755B8D998B1";
    Require(Convert.ToHexString(hash.GetHashAndReset()) == expected, "authored material snapshot changed");
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
    // through the Engine's keyed RNG. The two agree today because no feature voxel
    // is placed yet, so both hash the field - the contract is what they pin.

    const string ExpectedFeatureHash = "D86620502DF9D66B49379700B72DF568DA0774ED8EE41D854D27A87D83CF839E";
    Console.WriteLine(
        $"Terrain features, water and world edges placed and deterministic: {featureVoxels} feature, " +
        $"{waterVoxels} water, {bedrockVoxels} bedrock voxels, {featureHash}");
    Require(featureVoxels > 0, "the surface feature pass placed no feature voxel");
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
