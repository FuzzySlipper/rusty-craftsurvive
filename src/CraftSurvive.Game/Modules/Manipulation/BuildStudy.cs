using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// The building look study (#9684): one small timber-and-stone cabin built side by side at several
/// granularities, to judge how blocky built work can be beside the procedural trees and the
/// dual-contoured terrain. Every variant uses the same construction maps
/// (<see cref="GroundTextureSet.Construction"/>), so only the geometry differs:
/// <list type="bullet">
/// <item>cubes: 1 m voxels drawn as cubes, as construction is today;</item>
/// <item>blocky: 1 m voxels dual contoured with blocky vertices;</item>
/// <item>half and quarter: 0.5 m and 0.25 m voxels dual contoured with sharp features from the
/// pieces' signed distances, so posts, beams, thin walls and a pitched roof can exist;</item>
/// <item>mesh: the same pieces as snap-together textured meshes, crisp at any size.</item>
/// </list>
/// The cabin is described once as signed-distance pieces; a voxel variant samples it at its own
/// voxel size, thickening any piece thinner than a voxel so it survives. Each voxel variant is its
/// own spatial session aligned to the world frame, like the far field. Study only: it does not
/// collide, is not saved and is not part of the player's building.
/// </summary>
internal sealed class BuildStudy : IDisposable
{
    internal const float SpacingMetres = 10f;
    private const float FaceDensity = 0.5f;
    private const float DensityFloor = 0.001f;
    private const float MetresPerTile = 2f;
    private const float FlatCreaseDegrees = 0f;
    private const float SharpCreaseDegrees = 30f;
    private const float StudyRoughness = 0.9f;
    /// <summary>Roof pitch, ridge height and the eaves' reach past the walls.</summary>
    private const float Pitch = 35f * MathF.PI / 180f;
    private const float Eaves = 2.95f, EavesReach = 2.3f;
    private static readonly float Ridge = Eaves + (EavesReach * MathF.Tan(Pitch));

    private enum Piece { Planks, Timber, Masonry, Roof }

    private enum Shape { Box, Gable }

    /// <summary>
    /// One cabin piece: a box turned about X by <paramref name="Pitch"/> (positive tips its +Z end
    /// down, as the front roof slab falls toward the eaves) or a gable end's triangle.
    /// </summary>
    private readonly record struct Part(Piece Piece, Shape Shape, Vector3 Centre, Vector3 Half, float Pitch = 0f);

    private sealed record Variant(string Name, double VoxelSize, VoxelSurfaceMode Mode, SurfaceCharacter Character, bool SignedDistance);

    private static readonly Variant[] Variants =
    [
        new("cubes", 1.0, VoxelSurfaceMode.GreedyCubes, SurfaceCharacter.Default, false),
        new("blocky", 1.0, VoxelSurfaceMode.DualContouring, new SurfaceCharacter(VertexPlacement.Blocky, FlatCreaseDegrees, 0f), false),
        new("half", 0.5, VoxelSurfaceMode.DualContouring, new SurfaceCharacter(VertexPlacement.Sharp, SharpCreaseDegrees, 0f), true),
        new("quarter", 0.25, VoxelSurfaceMode.DualContouring, new SurfaceCharacter(VertexPlacement.Sharp, SharpCreaseDegrees, 0f), true),
    ];

    /// <summary>Slots the study binds each piece to (any bound blocks; the study's own sessions give them its maps).</summary>
    private static readonly Dictionary<Piece, BlockId> Slots = new()
    {
        [Piece.Planks] = BlockId.Planks,
        [Piece.Timber] = BlockId.Log,
        [Piece.Masonry] = BlockId.Cobblestone,
        [Piece.Roof] = BlockId.Brick,
    };

    private static readonly Dictionary<Piece, string> Maps = new()
    {
        [Piece.Planks] = "planks",
        [Piece.Timber] = "timber",
        [Piece.Masonry] = "masonry",
        [Piece.Roof] = "shingles",
    };

    /// <summary>
    /// The cabin, about 5 m by 4 m on a stone plinth: corner posts, plank walls with a door and a
    /// window, top plates, a pitched shingle roof with plank gables, and a step.
    /// </summary>
    private static readonly Part[] Cabin = BuildCabin();

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly TerrainGroundMaterials maps;
    private readonly PropMaterials meshMaterials;
    private readonly List<(SpatialSession Session, VoxelScenePresentation Projection)> sessions = [];
    private MeshResource? cabinMesh;
    private Appearance? cabinLook;
    private Vector3 meshAt;
    private bool meshShown;
    private bool collide;
    private (long X, long Y, long Z) alignedOrigin = (long.MinValue, 0, 0);
    private string built = "none";

    internal BuildStudy(IEngineContext engine, ProductContent content, WorldFrame frame)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        maps = new TerrainGroundMaterials(engine, content, TerrainConstants.VoxelSize, GroundTextureSet.Construction);
        meshMaterials = new PropMaterials(engine, _ => new PropMaterials.Finish(StudyRoughness, 0f, 0f));
        frame.Rebased += _ => Align();
    }

    /// <summary>The mesh variant's draw fact, for the snapshot; empty until built.</summary>
    internal AppearanceFact[] Facts => meshShown && cabinLook is Appearance look
        ? [new(ProductIds.BuildStudyObject, false, 0, new(frame.ToLocal(meshAt.X, meshAt.Y, meshAt.Z), Quaternion.Identity, Vector3.One), look, true, RenderLayer.Scene)]
        : [];

    /// <summary>
    /// Builds every variant in a row along +X from (x, z), each on the recipe's ground, replacing
    /// any earlier study. Returns where each stands.
    /// </summary>
    internal string Build(double x, double z, Func<double, double, double> groundAt, bool collide = false)
    {
        this.collide = collide;
        Clear();
        List<string> placed = [];
        for (int index = 0; index <= Variants.Length; index++)
        {
            double cx = x + (index * SpacingMetres);
            double ground = groundAt(cx, z);
            Vector3 at = new((float)cx, (float)ground, (float)z);
            if (index < Variants.Length)
            {
                BuildVoxels(Variants[index], at);
                placed.Add(FormattableString.Invariant($"{Variants[index].Name}@{cx:F0},{ground:F1},{z:F0}"));
            }
            else
            {
                BuildMesh(at);
                placed.Add(FormattableString.Invariant($"mesh@{cx:F0},{ground:F1},{z:F0}"));
            }
        }

        Align();
        built = string.Join(' ', placed);
        return Readout();
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture, $"buildStudy sessions={sessions.Count} mesh={meshShown} collide={collide} {built}");

    internal void Clear()
    {
        foreach ((SpatialSession session, VoxelScenePresentation projection) in sessions)
        {
            projection.Dispose();
            session.Dispose();
        }

        sessions.Clear();
        // The mesh cabin is only hidden: an appearance still in the published snapshot cannot be
        // destroyed, and the same cabin is shown again by the next build.
        meshShown = false;
        alignedOrigin = (long.MinValue, 0, 0);
        built = "none";
    }

    public void Dispose()
    {
        Clear();
        cabinLook?.Dispose();
        cabinMesh?.Dispose();
        meshMaterials.Dispose();
        maps.Dispose();
    }

    private void BuildVoxels(Variant variant, Vector3 at)
    {
        double v = variant.VoxelSize;
        SpatialSession session = engine.Spatial.CreateSession(new SpatialSessionConfig(v, TerrainConstants.VoxelChunkSize, variant.Mode));
        try
        {
            engine.Voxel.ConfigureMaterialSurfaces(new VoxelMaterialSurfaceRequest(session, variant.Mode,
                Slots.Values.Select(block => new VoxelMaterialSurface(BlockRegistry.Get(block).Slot, variant.Mode, variant.Character)).ToArray()));
            // Visual unless asked: with its pieces colliding, teleports stopped taking while a study
            // stood, so whether a second colliding session can share the player's space is what the
            // collide switch tests.
            uint[] slots = [.. Slots.Values.Select(block => (uint)BlockRegistry.Get(block).Slot)];
            engine.Voxel.ConfigureMaterialCollision(new VoxelMaterialCollisionRequest(session,
                slots.Select(slot => new VoxelMaterialCollision(slot, collide)).ToArray()));
            engine.Voxel.ConfigureMaterialOcclusion(new VoxelMaterialOcclusionRequest(session,
                slots.Select(slot => new VoxelMaterialOcclusion(slot, true)).ToArray()));
            Part[] parts = [.. Cabin.Select(part => Thickened(part, (float)v))];
            Admit(session, parts, at, v, variant.SignedDistance);
            VoxelScenePresentation projection = engine.VoxelScenePresentation.ProjectSceneDirectional(new ProjectVoxelSceneDirectionalRequest(
                session,
                Slots.Select(pair => new VoxelSceneMaterialBinding(BlockRegistry.Get(pair.Value).Slot, maps.Plain(Maps[pair.Key]))).ToArray(),
                ReadOnlyMemory<VoxelSceneFaceMaterialBinding>.Empty));
            sessions.Add((session, projection));
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Samples the pieces at this voxel size into the session's chunks around the cabin.</summary>
    private void Admit(SpatialSession session, Part[] parts, Vector3 at, double v, bool signedDistance)
    {
        int edge = TerrainConstants.ChunkEdgeLength;
        Vector3 low = at + new Vector3(-3.5f, -1.2f, -3.2f), high = at + new Vector3(3.5f, Ridge + 0.6f, 3.2f);
        (long X, long Y, long Z) first = ChunkOf(low, v), last = ChunkOf(high, v);
        List<VoxelResidencyOperation> operations = [];
        List<uint> materials = [];
        List<float> densities = [];
        for (long cz = first.Z; cz <= last.Z; cz++)
        {
            for (long cy = first.Y; cy <= last.Y; cy++)
            {
                for (long cx = first.X; cx <= last.X; cx++)
                {
                    uint offset = (uint)materials.Count;
                    for (int index = 0; index < TerrainConstants.ChunkVolume; index++)
                    {
                        long ix = (cx * edge) + (index % edge), iy = (cy * edge) + (index / edge % edge), iz = (cz * edge) + (index / (edge * edge));
                        Vector3 centre = new((float)((ix + 0.5) * v), (float)((iy + 0.5) * v), (float)((iz + 0.5) * v));
                        (float distance, Piece piece) = Nearest(parts, centre - at);
                        bool solid = distance < 0;
                        materials.Add(solid ? BlockRegistry.Get(Slots[piece]).Slot : TerrainConstants.EmptyMaterial);
                        float density = signedDistance ? Math.Clamp(distance / (float)v, -FaceDensity, FaceDensity) : (solid ? -FaceDensity : FaceDensity);
                        densities.Add(solid ? Math.Min(density, -DensityFloor) : Math.Max(density, DensityFloor));
                    }

                    operations.Add(new VoxelResidencyOperation(VoxelResidencyOperationKind.Admit, new VoxelChunkIdentity(cx, cy, cz),
                        offset, (uint)TerrainConstants.ChunkVolume, offset, (uint)TerrainConstants.ChunkVolume));
                }
            }
        }

        engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(ReadOnlyMemory<uint>.Empty, session,
            operations.ToArray(), materials.ToArray(), densities.ToArray()));
    }

    private static (long X, long Y, long Z) ChunkOf(Vector3 metres, double v)
    {
        long edge = TerrainConstants.ChunkEdgeLength;
        return (GridMath.FloorDivide((long)Math.Floor(metres.X / v), edge), GridMath.FloorDivide((long)Math.Floor(metres.Y / v), edge),
            GridMath.FloorDivide((long)Math.Floor(metres.Z / v), edge));
    }

    private static (float Distance, Piece Piece) Nearest(Part[] parts, Vector3 p)
    {
        float best = float.MaxValue;
        Piece piece = Piece.Planks;
        foreach (Part part in parts)
        {
            float d = Distance(part, p);
            if (d < best)
            {
                best = d;
                piece = part.Piece;
            }
        }

        return (best, piece);
    }

    private static float Distance(Part part, Vector3 p)
    {
        Vector3 local = p - part.Centre;
        if (part.Shape == Shape.Gable)
        {
            // A triangle in the YZ plane under the roof's pitch, a plank thick.
            float slope = ((local.Y + part.Centre.Y - Ridge) + (MathF.Abs(local.Z) * MathF.Tan(Pitch))) * MathF.Cos(Pitch);
            return MathF.Max(MathF.Max(MathF.Abs(local.X) - part.Half.X, Eaves - (local.Y + part.Centre.Y)), slope);
        }

        if (part.Pitch != 0)
        {
            float c = MathF.Cos(-part.Pitch), s = MathF.Sin(-part.Pitch);
            local = new Vector3(local.X, (c * local.Y) - (s * local.Z), (s * local.Y) + (c * local.Z));
        }

        Vector3 q = Vector3.Abs(local) - part.Half;
        return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f);
    }

    /// <summary>A piece thinner than a voxel is thickened to one, so it survives sampling.</summary>
    private static Part Thickened(Part part, float voxel) =>
        part with { Half = Vector3.Max(part.Half, new Vector3(voxel / 2f)) };

    private static Part[] BuildCabin()
    {
        const float Floor = 0.3f, WallTop = 2.75f, Wall = 0.1f, PostX = 2.35f, PostZ = 1.85f;
        List<Part> parts =
        [
            new(Piece.Masonry, Shape.Box, new(0, -0.35f, 0), new(2.7f, 0.65f, 2.2f)),
            new(Piece.Masonry, Shape.Box, new(0, 0.15f, 2.5f), new(0.6f, 0.15f, 0.3f)),
        ];
        foreach (float x in new[] { -PostX, PostX })
        {
            foreach (float z in new[] { -PostZ, PostZ })
            {
                parts.Add(new(Piece.Timber, Shape.Box, new(x, 1.6f, z), new(0.15f, 1.3f, 0.15f)));
            }
        }

        float wallMid = (Floor + WallTop) / 2, wallHalf = (WallTop - Floor) / 2;
        // Front (+Z): either side of a 1 m door, and a lintel panel over it.
        parts.Add(new(Piece.Planks, Shape.Box, new(-1.35f, wallMid, PostZ), new(0.85f, wallHalf, Wall)));
        parts.Add(new(Piece.Planks, Shape.Box, new(1.35f, wallMid, PostZ), new(0.85f, wallHalf, Wall)));
        parts.Add(new(Piece.Planks, Shape.Box, new(0, 2.525f, PostZ), new(0.5f, 0.225f, Wall)));
        // Back and the plain side.
        parts.Add(new(Piece.Planks, Shape.Box, new(0, wallMid, -PostZ), new(2.2f, wallHalf, Wall)));
        parts.Add(new(Piece.Planks, Shape.Box, new(-PostX, wallMid, 0), new(Wall, wallHalf, 1.7f)));
        // The window side: below, above, and either side of a 0.8 m window.
        parts.Add(new(Piece.Planks, Shape.Box, new(PostX, 0.75f, 0), new(Wall, 0.45f, 1.7f)));
        parts.Add(new(Piece.Planks, Shape.Box, new(PostX, 2.375f, 0), new(Wall, 0.375f, 1.7f)));
        parts.Add(new(Piece.Planks, Shape.Box, new(PostX, 1.6f, -1.05f), new(Wall, 0.4f, 0.65f)));
        parts.Add(new(Piece.Planks, Shape.Box, new(PostX, 1.6f, 1.05f), new(Wall, 0.4f, 0.65f)));
        // Top plates.
        parts.Add(new(Piece.Timber, Shape.Box, new(0, 2.9f, PostZ), new(2.5f, 0.15f, 0.15f)));
        parts.Add(new(Piece.Timber, Shape.Box, new(0, 2.9f, -PostZ), new(2.5f, 0.15f, 0.15f)));
        parts.Add(new(Piece.Timber, Shape.Box, new(PostX, 2.9f, 0), new(0.15f, 0.15f, 2.0f)));
        parts.Add(new(Piece.Timber, Shape.Box, new(-PostX, 2.9f, 0), new(0.15f, 0.15f, 2.0f)));
        // The roof's two pitched slabs, meeting over the ridge, and the gables under them.
        float slopeHalf = (EavesReach / MathF.Cos(Pitch) / 2f) + 0.08f;
        float rise = EavesReach * MathF.Tan(Pitch) / 2f;
        Vector3 offset = new(0, MathF.Cos(Pitch) * 0.1f, 0);
        parts.Add(new(Piece.Roof, Shape.Box, new Vector3(0, Eaves + rise, EavesReach / 2f) + offset, new(3.0f, 0.1f, slopeHalf), Pitch));
        parts.Add(new(Piece.Roof, Shape.Box, new Vector3(0, Eaves + rise, -EavesReach / 2f) + offset, new(3.0f, 0.1f, slopeHalf), -Pitch));
        parts.Add(new(Piece.Planks, Shape.Gable, new(PostX, Eaves, 0), new(Wall, 0, 0)));
        parts.Add(new(Piece.Planks, Shape.Gable, new(-PostX, Eaves, 0), new(Wall, 0, 0)));
        return [.. parts];
    }

    /// <summary>The cabin as snap-together textured pieces: boxes and gable prisms, one group per map.</summary>
    private void BuildMesh(Vector3 at)
    {
        meshAt = at;
        meshShown = true;
        if (cabinLook is not null) return;
        Dictionary<Piece, (List<Vector3> P, List<Vector3> N, List<Vector2> Uv, List<uint> I)> groups = [];
        foreach (Part part in Cabin)
        {
            if (!groups.TryGetValue(part.Piece, out var g))
            {
                g = ([], [], [], []);
                groups[part.Piece] = g;
            }

            if (part.Shape == Shape.Gable) AddGable(part, g.P, g.N, g.Uv, g.I);
            else AddBox(part, g.P, g.N, g.Uv, g.I);
        }

        List<Vector3> positions = [], normals = [];
        List<Vector2> uvs = [];
        List<uint> indices = [];
        List<MeshGroup> meshGroups = [];
        List<MeshMaterialBinding> bindings = [];
        foreach ((Piece piece, var g) in groups)
        {
            uint first = (uint)positions.Count, start = (uint)indices.Count, slot = (uint)bindings.Count;
            positions.AddRange(g.P);
            normals.AddRange(g.N);
            uvs.AddRange(g.Uv);
            indices.AddRange(g.I.Select(index => index + first));
            meshGroups.Add(new(slot, start, (uint)indices.Count - start));
            bindings.Add(new(slot, meshMaterials.For("solid", $"textures/construction/{Maps[piece]}.png")));
        }

        Color white = new(1, 1, 1, 0);
        cabinMesh = engine.Graphics.CreateMeshResource(new MeshResourceCreateRequest(positions.ToArray(), normals.ToArray(), uvs.ToArray(),
            Enumerable.Repeat(white, positions.Count).ToArray(), indices.ToArray(), meshGroups.ToArray(), bindings.ToArray()));
        cabinLook = engine.Graphics.CreateMeshAppearance(cabinMesh);
    }

    private static void AddBox(Part part, List<Vector3> p, List<Vector3> n, List<Vector2> uv, List<uint> indices)
    {
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitX, part.Pitch);
        Vector3 h = part.Half;
        Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        for (int axis = 0; axis < 3; axis++)
        {
            foreach (float sign in new[] { -1f, 1f })
            {
                Vector3 normal = axes[axis] * sign;
                Vector3 u = axes[(axis + 1) % 3], v = axes[(axis + 2) % 3];
                float hu = Component(h, (axis + 1) % 3), hv = Component(h, (axis + 2) % 3), hn = Component(h, axis);
                uint first = (uint)p.Count;
                foreach ((float a, float b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                {
                    Vector3 local = (normal * hn) + (u * a * hu) + (v * b * hv);
                    p.Add(part.Centre + Vector3.Transform(local, turn));
                    n.Add(Vector3.Transform(normal, turn));
                    // Planar in the piece's own frame, in metres, so grain and courses run along it.
                    uv.Add(new Vector2(Component(local, axis == 1 ? 0 : axis == 0 ? 2 : 0), -Component(local, axis == 1 ? 2 : 1)) / MetresPerTile);
                }

                // u x v is the face's axis, so the corners run anticlockwise seen from the axis's positive side.
                indices.AddRange(sign > 0
                    ? [first, first + 1, first + 2, first, first + 2, first + 3]
                    : [first, first + 2, first + 1, first, first + 3, first + 2]);
            }
        }
    }

    private static void AddGable(Part part, List<Vector3> p, List<Vector3> n, List<Vector2> uv, List<uint> indices)
    {
        float halfBase = (Ridge - Eaves) / MathF.Tan(Pitch);
        foreach (float sign in new[] { -1f, 1f })
        {
            float x = part.Centre.X + (sign * part.Half.X);
            Vector3 normal = new(sign, 0, 0);
            uint first = (uint)p.Count;
            foreach (Vector3 corner in new[] { new Vector3(x, Eaves, -halfBase), new Vector3(x, Eaves, halfBase), new Vector3(x, Ridge, 0) })
            {
                p.Add(corner);
                n.Add(normal);
                uv.Add(new Vector2(corner.Z, -corner.Y) / MetresPerTile);
            }

            indices.AddRange(sign > 0 ? [first, first + 2, first + 1] : [first, first + 1, first + 2]);
        }
    }

    private static float Component(Vector3 vector, int axis) => axis switch { 0 => vector.X, 1 => vector.Y, _ => vector.Z };

    /// <summary>Keeps every study session's origin with the world frame's, as the far field does.</summary>
    private void Align()
    {
        (long X, long Y, long Z) origin = frame.Origin;
        if (origin == alignedOrigin) return;
        foreach ((SpatialSession session, _) in sessions)
        {
            using WorldOriginPrepared prepared = engine.WorldOrigin.Prepare(new WorldOriginPrepareRequest(
                session, origin.X, origin.Y, origin.Z, Array.Empty<WorldOriginEntityRow>()));
            engine.WorldOrigin.Commit(new WorldOriginCommitRequest(prepared));
        }

        alignedOrigin = origin;
    }
}
