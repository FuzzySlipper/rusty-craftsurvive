using System.Security.Cryptography;
using System.Buffers.Binary;
using CraftSurvive.Game.Modules.Terrain;

// Material snapshots taken before the residency/column optimization. Cover
// authored landmarks, boundaries, negative coordinates, layers, and two seeds.
foreach (ulong seed in new[] { TerrainConstants.DefaultSeed, 12345UL })
{
    TerrainConfiguration config = new(seed, TerrainConstants.DefaultSize);
    var generator = new TerrainChunkGenerator(config.CreateRecipe());
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
        ? "82D9A221205562F17136098BFE21215741B3FE27005CD8D314E38DA715F921B2"
        : "EA2EFDDF7EE64FB0931CDE1AEF10D0EBDA9DB7FBF0A27A98F193EC5BD497718E";
    Require(Convert.ToHexString(hash.GetHashAndReset()) == expected, "authored material snapshot changed");
}

var configuration = TerrainConfiguration.Default;
var recipe = configuration.CreateRecipe();
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
Console.WriteLine("Terrain materials, residency overlap, ordering, payload reuse, edits, restore and eviction passed.");

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
