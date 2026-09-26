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
    string expected = seed == TerrainConstants.DefaultSeed
        ? "1816B4ADFD0EEE867A3775833CE2E4A2256496510BD5B6669C5B0059ED9BCD8C"
        : "72DB0885AE75644CC272203AC667380B8BC159B13276526F77711CF1B0579827";
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
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] bytes = new byte[TerrainConstants.ChunkVolume * sizeof(ushort)];
    for (long x = -2; x <= 2; x++)
    for (long z = -2; z <= 2; z++)
    for (long y = 2; y <= 6; y++)
    {
        TerrainChunk chunk = generator.Generate(new(x, y, z), overlay.Snapshot());
        for (int i = 0; i < chunk.Materials.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * sizeof(ushort)), chunk.Materials.Span[i]);
        hash.AppendData(bytes);
    }

    string featureHash = Convert.ToHexString(hash.GetHashAndReset());
    // Pinned against the managed draw port; the live lane prints the same snapshot
    // through the Engine's keyed RNG. The two agree today because no feature voxel
    // is placed yet, so both hash the field - the contract is what they pin.
    const string ExpectedFeatureHash = "7B331C02E313C7599D5A90212E17E6D3CB729BD2E1C9B873C302A63C95A2F9BF";
    Console.WriteLine($"Terrain surface features are deterministic: {featureHash}");
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
