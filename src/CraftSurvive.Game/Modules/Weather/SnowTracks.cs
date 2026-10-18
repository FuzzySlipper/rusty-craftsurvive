using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// Footprints in the snow (#10068): a pool of painted marks laid at footfalls by the player (boots) and the creatures near
/// them (paws), as Engine projected decals (rusty-engine #10070), so each one wraps the ground it falls on - a slope, a step's
/// edge - instead of floating. A print softens and fades as fresh snow falls into it and with age, and goes once the snow
/// under it has melted (<see cref="TrackFading"/>). They are not saved: tracks are a few hours of the party's passing. The
/// oldest print is reused when the pool is full.
/// </summary>
internal sealed class SnowTracks : IProductModule
{
    private const string AtlasPath = "textures/snow/footprints.png";
    /// <summary>Prints are made once this much snow lies (metres).</summary>
    internal const double TracksFrom = 0.03;
    /// <summary>The pool's size: a few hundred metres of the party's trail and the creatures' near them.</summary>
    internal const int PoolSize = 512;
    /// <summary>Creatures this near the player leave tracks (metres).</summary>
    internal const double CreatureMetres = 60;
    /// <summary>Strides, metres: one foot falls each this far (the player's), and a creature's.</summary>
    private const float PlayerStride = 0.75f, CreatureStride = 0.9f;
    /// <summary>Each foot falls this far either side of the way walked.</summary>
    private const float PlayerFootSpread = 0.12f, CreatureFootSpread = 0.14f;
    /// <summary>A print's box (metres): the atlas cell across (32 texels at 64 a metre) and its depth; a paw's is this much larger, the creatures being big beasts.</summary>
    private const float PrintMetres = 0.5f, PrintDepth = 0.8f, PawScale = 1.6f;
    /// <summary>A body that jumps farther than this between updates (a teleport, a respawn) starts a new trail.</summary>
    private const float GapMetres = 6f;
    /// <summary>The atlas's columns and rows: three stages a row, boots above paws.</summary>
    private const int Columns = 4, Rows = 2;

    private enum Maker { Boot, Paw }

    private sealed class Print
    {
        internal Vector3 At;
        internal float Heading;
        internal bool Left;
        internal Maker Maker;
        internal double Hours, Fallen;
        /// <summary>The stage shown, or <see cref="TrackFading.Gone"/> for none; and the stage the Engine was last given.</summary>
        internal int Stage = TrackFading.Gone, Placed = TrackFading.Gone;
    }

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly SnowCover cover;
    private readonly Func<double> hours;
    private readonly Func<(Vector3 Feet, bool Grounded, bool Open)> player;
    private readonly Func<IEnumerable<(int Id, Vector3 Feet)>> creatures;
    private readonly Print[] pool = new Print[PoolSize];
    private readonly Dictionary<int, (Vector3 Last, float Walked, bool Left)> walkers = [];
    private RenderResource? atlas;
    private (Vector3 Last, float Walked, bool Left)? walking;
    private (long X, long Y, long Z) placedOrigin;
    private bool shown = true;
    private int next;
    private long stamped, refused;
    private string lastFailure = "none";

    /// <param name="player">The player's feet (world metres), whether they stand on the ground, and whether they are in the open world.</param>
    internal SnowTracks(IEngineContext engine, WorldFrame frame, SnowCover cover, Func<double> hours, Func<(Vector3 Feet, bool Grounded, bool Open)> player,
        Func<IEnumerable<(int Id, Vector3 Feet)>> creatures)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.cover = cover ?? throw new ArgumentNullException(nameof(cover));
        this.hours = hours ?? throw new ArgumentNullException(nameof(hours));
        this.player = player ?? throw new ArgumentNullException(nameof(player));
        this.creatures = creatures ?? throw new ArgumentNullException(nameof(creatures));
        for (int i = 0; i < pool.Length; i++) pool[i] = new Print();
    }

    internal int Live => pool.Count(print => print.Stage < TrackFading.Gone);

    public void Start()
    {
        // Crisp texels up close, as every product texture (the crunch rule).
        atlas = engine.Graphics.OpenResource(new RenderResourceRequest(AtlasPath, TextureFilter.NearestMipmapped, TextureWrap.Clamp)).Handle;
        engine.CameraView.SetDecalAtlas(new DecalAtlasRequest(atlas, default));
        placedOrigin = frame.Origin;
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
        Present(open);
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
        Vector3 side = new(-way.Y, 0, way.X);
        Print print = pool[next];
        next = (next + 1) % pool.Length;
        print.At = feet + (side * (left ? -spread : spread));
        // The box turns so its -z (the atlas's toe, v 0) points the way walked.
        print.Heading = MathF.Atan2(-way.X, -way.Y);
        print.Left = left;
        print.Maker = maker;
        print.Hours = now;
        print.Fallen = cover.FallenAt(print.At.X, print.At.Z, now);
        print.Stage = 0;
        stamped++;
    }

    private void Fade(double now)
    {
        foreach (Print print in pool)
        {
            if (print.Stage >= TrackFading.Gone) continue;
            print.Stage = TrackFading.StageOf(cover.FallenAt(print.At.X, print.At.Z, now) - print.Fallen, now - print.Hours,
                cover.DepthAt(print.At.X, print.At.Z, now));
        }
    }

    /// <summary>
    /// Tells the Engine the prints that changed: placed or replaced at their stage, or removed. Decals stand in the scene's
    /// frame, so every print is placed again when the frame origin moves; outside the open world (a dungeon) none are shown.
    /// </summary>
    private void Present(bool open)
    {
        bool rebase = frame.Origin != placedOrigin;
        placedOrigin = frame.Origin;
        if (open != shown)
        {
            shown = open;
            rebase = true;
        }

        for (int i = 0; i < pool.Length; i++)
        {
            Print print = pool[i];
            int want = shown ? print.Stage : TrackFading.Gone;
            if (want == print.Placed && !(rebase && want < TrackFading.Gone)) continue;
            try
            {
                if (want >= TrackFading.Gone) engine.CameraView.RemoveDecal(new DecalRemoval(Id(i)));
                else engine.CameraView.SetDecal(Request(i, print, want));
                print.Placed = want;
            }
            catch (EngineCallException exception)
            {
                refused++;
                lastFailure = exception.Message;
                print.Placed = TrackFading.Gone;
            }
        }
    }

    private static ulong Id(int index) => ProductIds.SnowTrackBase + (ulong)index;

    private DecalRequest Request(int index, Print print, int stage)
    {
        float size = PrintMetres * (print.Maker == Maker.Paw ? PawScale : 1);
        float row = (int)print.Maker, u0 = stage / (float)Columns, u1 = (stage + 1) / (float)Columns;
        // A left foot is the right one mirrored across the atlas cell.
        if (print.Left) (u0, u1) = (u1, u0);
        Vector4 region = new(u0, row / Rows, u1, (row + 1) / Rows);
        return new DecalRequest(Id(index), frame.ToLocal(print.At.X, print.At.Y, print.At.Z), Quaternion.CreateFromAxisAngle(Vector3.UnitY, print.Heading),
            new Vector3(size, PrintDepth, size), region, new Color(1, 1, 1, 1), Roughness: 0, NormalStrength: 0, AngleFalloff: true,
            OnVegetation: false, OnAnimatedMeshes: false, LifeSeconds: 0, FadeSeconds: 0);
    }

    /// <summary>For measuring (#10068): fills the whole pool with fresh prints in rows around the player, as a long trail would.</summary>
    internal string Fill()
    {
        (Vector3 feet, _, _) = player();
        double now = hours();
        int side = (int)Math.Ceiling(Math.Sqrt(PoolSize));
        for (int i = 0; i < PoolSize; i++)
        {
            Vector3 at = feet + new Vector3(((i % side) - (side / 2)) * FillSpacing, 0, ((i / side) - (side / 2)) * FillSpacing);
            Stamp(at, -Vector2.UnitY, i % 2 == 0, 0, i % 3 == 0 ? Maker.Paw : Maker.Boot, now);
        }

        return Readout();
    }

    /// <summary>Filled prints stand this far apart (metres).</summary>
    private const float FillSpacing = 0.6f;

    public void Restart()
    {
        foreach (Print print in pool) print.Stage = TrackFading.Gone;
        walking = null;
        walkers.Clear();
    }

    public void Dispose()
    {
        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i].Placed < TrackFading.Gone) engine.CameraView.RemoveDecal(new DecalRemoval(Id(i)));
        }

        engine.CameraView.SetDecalAtlas(default);
        atlas?.Dispose();
        atlas = null;
    }

    internal string Readout()
    {
        DecalReadout engineDecals = engine.CameraView.ReadDecals();
        return string.Create(CultureInfo.InvariantCulture,
            $"tracks live={Live}/{PoolSize} stamped={stamped} walkers={walkers.Count} stages=[{string.Join(",", Enumerable.Range(0, TrackFading.Stages).Select(s => pool.Count(p => p.Stage == s)))}] decals={engineDecals.Decals}/{engineDecals.Budget} refused={refused} failure={lastFailure}");
    }
}
