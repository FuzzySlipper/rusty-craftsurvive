using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// Footprints in the snow (#10068): a pool of small painted alpha-cut quads stamped on the ground at footfalls, by the
/// player (boots) and the creatures near them (paws), each lying along the ground's slope. A print softens and fades
/// as fresh snow falls into it (the cover's snow fallen since it was made) and with age, and goes once the snow under
/// it has melted. They are not saved: tracks are a few hours of the party's passing. The oldest print is reused when
/// the pool is full.
/// </summary>
internal sealed class SnowTracks : IProductModule
{
    private const string AtlasPath = "textures/snow/footprints.png";
    /// <summary>Prints are made once this much snow lies (metres).</summary>
    internal const double TracksFrom = 0.03;
    /// <summary>The pool's size: about a few hundred metres of the party's trail and the creatures' near them.</summary>
    internal const int PoolSize = 320;
    /// <summary>Creatures this near the player leave tracks (metres).</summary>
    internal const double CreatureMetres = 60;
    /// <summary>Strides, metres: one foot falls each this far (the player's), and a creature's.</summary>
    private const float PlayerStride = 0.75f, CreatureStride = 0.9f;
    /// <summary>Each foot falls this far either side of the way walked.</summary>
    private const float PlayerFootSpread = 0.12f, CreatureFootSpread = 0.14f;
    /// <summary>A quad's side (metres): the atlas cell, 32 texels at 64 a metre.</summary>
    private const float PrintMetres = 0.5f;
    /// <summary>A print lies this far above the ground it was laid on, so it never fights the ground for depth.</summary>
    private const float Lift = 0.012f;
    /// <summary>The ground's slope is read this far around a print.</summary>
    private const float SlopeProbe = 0.3f;
    /// <summary>A body that jumps farther than this between updates (a teleport, a respawn) starts a new trail.</summary>
    private const float GapMetres = 6f;
    /// <summary>A print settles once this much fresh snow has fallen into it, fades at this much, and is gone at this much (metres).</summary>
    private const double SettledFallen = 0.01, FaintFallen = 0.03, GoneFallen = 0.06;
    /// <summary>With no fresh snow, a print settles, fades and goes over these many game hours.</summary>
    private const double SettledHours = 8, FaintHours = 20, GoneHours = 36;
    /// <summary>A print goes once the snow under it is shallower than this (metres): it has melted out.</summary>
    private const double MeltedBelow = 0.01;
    private const float Roughness = 0.9f;
    /// <summary>The atlas's columns and rows: three stages a row, boots above paws.</summary>
    private const int Columns = 4, Rows = 2, Stages = 3;

    private enum Maker { Boot, Paw }

    private sealed class Print
    {
        internal Vector3 At;
        internal Quaternion Turn;
        internal bool Left;
        internal Maker Maker;
        internal double Hours, Fallen;
        internal int Stage = -1;
        internal bool Live;
    }

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly SnowCover cover;
    private readonly Func<double> hours;
    private readonly Func<(Vector3 Feet, bool Grounded, bool Open)> player;
    private readonly Func<IEnumerable<(int Id, Vector3 Feet)>> creatures;
    private readonly Func<double, double, float> groundAt;
    private readonly Print[] pool = new Print[PoolSize];
    private readonly Dictionary<int, (Vector3 Last, float Walked, bool Left)> walkers = [];
    private PropMaterials? materials;
    private (MeshResource Mesh, Appearance Look)[] looks = [];
    private (Vector3 Last, float Walked, bool Left)? walking;
    private int next;
    private long stamped;

    /// <param name="player">The player's feet (world metres), whether they stand on the ground, and whether they are in the open world.</param>
    /// <param name="groundAt">The ground's height at a world point, where it is resident.</param>
    internal SnowTracks(IEngineContext engine, WorldFrame frame, SnowCover cover, Func<double> hours, Func<(Vector3 Feet, bool Grounded, bool Open)> player,
        Func<IEnumerable<(int Id, Vector3 Feet)>> creatures, Func<double, double, float> groundAt)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.cover = cover ?? throw new ArgumentNullException(nameof(cover));
        this.hours = hours ?? throw new ArgumentNullException(nameof(hours));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.creatures = creatures ?? throw new ArgumentNullException(nameof(creatures));
        this.groundAt = groundAt ?? throw new ArgumentNullException(nameof(groundAt));
        for (int i = 0; i < pool.Length; i++) pool[i] = new Print();
    }

    internal int Live => pool.Count(print => print.Live);

    public void Start()
    {
        materials = new PropMaterials(engine, _ => new PropMaterials.Finish(Roughness, 0, 0));
        Material material = materials.For(PropMaterials.LeavesRole, AtlasPath);
        // One quad a maker and stage, its cell's texels lying flat with the toe toward -Z (the print's forward).
        looks = new (MeshResource, Appearance)[Rows * Stages];
        for (int row = 0; row < Rows; row++)
            for (int stage = 0; stage < Stages; stage++)
            {
                MeshResource mesh = engine.Graphics.CreateMeshResource(Quad(row, stage, material));
                looks[(row * Stages) + stage] = (mesh, engine.Graphics.CreateMeshAppearance(mesh));
            }
    }

    public void Update(ProductStep step)
    {
        double now = hours();
        (Vector3 feet, bool grounded, bool open) = player();
        if (open) Walk(ref walking, feet, grounded, PlayerStride, PlayerFootSpread, Maker.Boot, now);
        else walking = null;

        HashSet<int> seen = [];
        foreach ((int id, Vector3 at) in creatures())
        {
            seen.Add(id);
            (Vector3 Last, float Walked, bool Left)? state = walkers.TryGetValue(id, out var known) ? known : null;
            Walk(ref state, at, true, CreatureStride, CreatureFootSpread, Maker.Paw, now);
            if (state is { } kept) walkers[id] = kept;
        }

        foreach (int gone in walkers.Keys.Where(id => !seen.Contains(id)).ToList()) walkers.Remove(gone);
        Fade(now);
    }

    /// <summary>A body's walk since the last update: a print each stride on snow deep enough, feet alternating.</summary>
    private void Walk(ref (Vector3 Last, float Walked, bool Left)? state, Vector3 feet, bool grounded, float stride, float spread, Maker maker, double now)
    {
        if (state is not { } s || Vector3.Distance(s.Last, feet) > GapMetres)
        {
            state = (feet, 0, false);
            return;
        }

        Vector2 moved = new(feet.X - s.Last.X, feet.Z - s.Last.Z);
        float walked = s.Walked + moved.Length();
        bool left = s.Left;
        if (grounded && walked >= stride && moved.LengthSquared() > 1e-8f)
        {
            walked %= stride;
            left = !left;
            if (cover.DepthAt(feet.X, feet.Z, now) >= TracksFrom) Stamp(feet, Vector2.Normalize(moved), left, spread, maker, now);
        }

        state = (feet, grounded ? walked : s.Walked, left);
    }

    private void Stamp(Vector3 feet, Vector2 way, bool left, float spread, Maker maker, double now)
    {
        Vector3 forward = new(way.X, 0, way.Y), side = new(-way.Y, 0, way.X);
        Vector3 at = feet + (side * (left ? -spread : spread));
        float y = groundAt(at.X, at.Z);
        if (!float.IsFinite(y)) return;
        // The ground's slope from two probes, ahead and aside.
        float ahead = groundAt(at.X + (forward.X * SlopeProbe), at.Z + (forward.Z * SlopeProbe));
        float aside = groundAt(at.X + (side.X * SlopeProbe), at.Z + (side.Z * SlopeProbe));
        Vector3 f = Vector3.Normalize(forward * SlopeProbe + (Vector3.UnitY * ((float.IsFinite(ahead) ? ahead : y) - y)));
        Vector3 s = Vector3.Normalize(side * SlopeProbe + (Vector3.UnitY * ((float.IsFinite(aside) ? aside : y) - y)));
        Vector3 up = Vector3.Normalize(Vector3.Cross(f, s));
        if (up.Y < 0) up = -up;
        Vector3 x = Vector3.Normalize(Vector3.Cross(up, -f));
        Matrix4x4 basis = new(x.X, x.Y, x.Z, 0, up.X, up.Y, up.Z, 0, -f.X, -f.Y, -f.Z, 0, 0, 0, 0, 1);
        Print print = pool[next];
        next = (next + 1) % pool.Length;
        print.At = new Vector3(at.X, y + Lift, at.Z);
        print.Turn = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
        print.Left = left;
        print.Maker = maker;
        print.Hours = now;
        print.Fallen = cover.FallenAt(at.X, at.Z, now);
        print.Stage = 0;
        print.Live = true;
        stamped++;
    }

    /// <summary>Each print's stage now: by the snow fallen into it and its age; gone once either says so or the snow under it melts.</summary>
    private void Fade(double now)
    {
        foreach (Print print in pool)
        {
            if (!print.Live) continue;
            double fallen = cover.FallenAt(print.At.X, print.At.Z, now) - print.Fallen, age = now - print.Hours;
            int byFall = fallen >= GoneFallen ? 3 : fallen >= FaintFallen ? 2 : fallen >= SettledFallen ? 1 : 0;
            int byAge = age >= GoneHours ? 3 : age >= FaintHours ? 2 : age >= SettledHours ? 1 : 0;
            print.Stage = Math.Max(byFall, byAge);
            if (print.Stage >= Stages || cover.DepthAt(print.At.X, print.At.Z, now) < MeltedBelow || age < 0) print.Live = false;
        }
    }

    /// <summary>The prints lying now, in the player's local frame.</summary>
    internal IEnumerable<AppearanceFact> Facts
    {
        get
        {
            if (looks.Length == 0) yield break;
            for (int i = 0; i < pool.Length; i++)
            {
                Print print = pool[i];
                if (!print.Live) continue;
                Appearance look = looks[((int)print.Maker * Stages) + print.Stage].Look;
                Vector3 scale = new(print.Left ? -1 : 1, 1, 1);
                yield return new AppearanceFact(ProductIds.SnowTrackBase + (ulong)i, false, 0,
                    new Transform(frame.ToLocal(print.At.X, print.At.Y, print.At.Z), print.Turn, scale), look, true, RenderLayer.Scene, ShadowCasting.None);
            }
        }
    }

    /// <summary>A flat quad of one atlas cell, both windings (a left print is the right one mirrored), the toe toward -Z.</summary>
    private static MeshResourceCreateRequest Quad(int row, int stage, Material material)
    {
        float h = PrintMetres / 2, u0 = stage / (float)Columns, u1 = (stage + 1) / (float)Columns, v0 = row / (float)Rows, v1 = (row + 1) / (float)Rows;
        Vector3[] corners = [new(-h, 0, h), new(h, 0, h), new(h, 0, -h), new(-h, 0, -h)];
        Vector2[] uv = [new(u0, v1), new(u1, v1), new(u1, v0), new(u0, v0)];
        Vector3[] positions = [.. corners, .. corners];
        Vector3[] normals = [.. Enumerable.Repeat(Vector3.UnitY, 4), .. Enumerable.Repeat(-Vector3.UnitY, 4)];
        Vector2[] uvs = [.. uv, .. uv];
        // Counter-clockwise seen from above for the upper face; the lower face the other way.
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6];
        return new MeshResourceCreateRequest(positions, normals, uvs, ReadOnlyMemory<Color>.Empty, indices,
            new MeshGroup[] { new(0, 0, (uint)indices.Length) }, new MeshMaterialBinding[] { new(0, material) });
    }

    public void Restart()
    {
        foreach (Print print in pool) print.Live = false;
        walking = null;
        walkers.Clear();
    }

    public void Dispose()
    {
        foreach ((MeshResource mesh, Appearance look) in looks)
        {
            look.Dispose();
            mesh.Dispose();
        }

        looks = [];
        materials?.Dispose();
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"tracks live={Live}/{PoolSize} stamped={stamped} walkers={walkers.Count} stages=[{string.Join(",", Enumerable.Range(0, Stages).Select(s => pool.Count(p => p.Live && p.Stage == s)))}]");
}
