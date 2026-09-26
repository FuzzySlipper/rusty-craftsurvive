using System.Security.Cryptography;
using System.Buffers.Binary;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Tests;
using CraftSurvive.Game.Modules.Content;

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
    // Moved deliberately at generation version 4, the first version that places
    // surface features: trees stand in the ground band these snapshots cover.
    string expected = seed == TerrainConstants.DefaultSeed
        ? "2E27EEA73F7655939DF17B0E4C252FFFB8CBC170B7106645F9FBEF2C152AB5C5"
        : "BB9780295CC26830DFA40B59577FA7B3F9084B26BD77A5DE04503EA47EAE06B5";
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
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort)), material);
        }

        hash.AppendData(bytes);
    }

    string featureHash = Convert.ToHexString(hash.GetHashAndReset());
    // Pinned against the managed draw port; the live lane prints the same snapshot
    // through the Engine's keyed RNG. The two agree today because no feature voxel
    // is placed yet, so both hash the field - the contract is what they pin.

    const string ExpectedFeatureHash = "B23DE176C79233DBC5F0F6AB4F85C6C3BF73A69DB4B5E919637DA8C28469E544";
    Console.WriteLine($"Terrain surface features placed and deterministic: {featureVoxels} voxels, {featureHash}");
    Require(featureVoxels > 0, "the surface feature pass placed no feature voxel");
    Require(featureHash == ExpectedFeatureHash, "surface feature snapshot changed");
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
Console.WriteLine("Player input plus terrain materials, residency overlap, ordering, payload reuse, edits, restore and eviction passed.");

void CheckAgainstFullScan(TerrainChunkAddress location)
{
    var snapshot = state.Snapshot();
    var populated = new List<TerrainChunkAddress>();
    for (long x = location.X - 2; x <= location.X + 2; x++)
    for (long z = location.Z - 2; z <= location.Z + 2; z++)
    for (long y = -1; y <= 1; y++)
    {
        TerrainChunkAddress address = new(x, y, z);
        var generated = chunkGenerator.Generate(address, snapshot);
        var planned = policy.PlanFor(location, state).Chunk(address);
        Require(generated.Materials.Span.SequenceEqual(planned.Materials.Span), "planned material payload differs from fresh generation");
        if (generated.SolidVoxelCount > 0) populated.Add(address);
    }
    var ordered = populated.OrderBy(a => ((a.X-location.X)*(a.X-location.X)+(a.Z-location.Z)*(a.Z-location.Z), a.Y, a)).ToArray();
    var expectedRequested = ordered.Where(a => Math.Abs(a.X-location.X) <= 1 && Math.Abs(a.Z-location.Z) <= 1);
    var plan = policy.PlanFor(location, state);
    Require(plan.Requested.SequenceEqual(expectedRequested), "request priority/occupancy differs from full scan");
    Require(plan.Retained.SequenceEqual(ordered.Take(64)), "retained priority/occupancy differs from full scan");
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
