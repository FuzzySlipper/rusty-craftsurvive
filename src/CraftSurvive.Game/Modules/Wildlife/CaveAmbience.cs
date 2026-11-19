using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Rpg;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>One small cave creature (#10876): an animal wildlife draws, with its height and where it is going.</summary>
internal sealed class CaveAmbient(CaveAmbientHome home, WildlifeMember member, float feet)
{
    internal CaveAmbientHome Home { get; } = home;

    internal WildlifeMember Member { get; } = member;

    /// <summary>The height it stands or swims at, in world metres.</summary>
    internal float Feet { get; set; } = feet;

    /// <summary>Where it is wandering to, and how long it stands before it picks again.</summary>
    internal Vector2? Goal { get; set; }

    internal double PauseSeconds { get; set; }

    /// <summary>How long it has been running from the player; past its kind's hiding time it slips out of sight.</summary>
    internal double FleeingSeconds { get; set; }

    /// <summary>How many wanders it has picked: each is drawn afresh.</summary>
    internal int Serial { get; set; }
}

/// <summary>
/// The small cave creatures (#10876; Patch: non-interactive, furtive ambience, no colliders and no combat). Each wanders
/// near its home over the cave's floor, or under its pool's surface, and runs from a player who comes near; having run
/// its kind's while it slips out of sight, until the player has gone. Nothing collides with them and no blow finds them:
/// they are drawn only. Engine-free: the floor under a point comes from the product.
/// </summary>
internal sealed class CaveAmbience
{
    /// <summary>A wanderer stands this long, at most, before it picks its next goal.</summary>
    private const double MostPauseSeconds = 4d;

    /// <summary>A wanderer this near its goal has arrived.</summary>
    private const float ArrivedMetres = 0.2f;

    /// <summary>A floor further than this above or below where it stands is not one it can step to: it stops there.</summary>
    private const double StepMetres = 0.6d;

    /// <summary>A hidden creature comes out again once the player is this many times its flee distance away.</summary>
    private const double ComeOutShare = 3d;

    /// <summary>A creature this far over the floor under it is not standing on it, and settles.</summary>
    private const float SettledMetres = 0.05f;

    /// <summary>A swimmer keeps this far under its pool's surface.</summary>
    private const float SwimDepthMetres = 0.25f;

    private readonly List<CaveAmbient> creatures = [];
    private readonly ulong seed;
    private readonly Func<double, double, double, float?> floorBelow;
    private int hidden, fled;

    /// <param name="floorBelow">The floor under a point (x, z) from a height, within a step either way, or null where there is none resident.</param>
    internal CaveAmbience(ulong seed, Func<double, double, double, float?> floorBelow)
    {
        this.seed = seed;
        this.floorBelow = floorBelow ?? throw new ArgumentNullException(nameof(floorBelow));
    }

    internal IReadOnlyList<CaveAmbient> Creatures => creatures;

    /// <summary>A cave's small creatures come, each on its home's floor; a home with no floor resident yet waits for the next call.</summary>
    /// <returns>The homes that found no floor, to be asked again.</returns>
    internal IReadOnlyList<CaveAmbientHome> Add(IReadOnlyList<CaveAmbientHome> homes, Func<int> takeId)
    {
        ArgumentNullException.ThrowIfNull(homes);
        ArgumentNullException.ThrowIfNull(takeId);
        List<CaveAmbientHome> waiting = [];
        foreach (CaveAmbientHome home in homes)
        {
            if (creatures.Any(creature => creature.Home.Key == home.Key)) continue;
            float? ground = home.Pool is CavePool pool ? pool.Surface - SwimDepthMetres : floorBelow(home.Home.X, home.Home.Z, home.Home.Y);
            if (ground is not float feet)
            {
                waiting.Add(home);
                continue;
            }

            for (int i = 0; i < home.Count; i++)
            {
                // A group starts spread about its home, each a little apart.
                Vector2 at = new Vector2(home.Home.X, home.Home.Z) + (Draw(home.Key, i, 0) * (float)(home.Kind.WanderMetres / 2));
                if (home.Pool is CavePool water) at = Nearest(water, at);
                WildlifeMember member = new(takeId(), at, Vector2.Zero, CombatantState.Fresh(1, new DefenceProfile(0, new ArmourProfile(0))))
                {
                    Yaw = (float)(Draw(home.Key, i, 1).X * Math.PI),
                };
                creatures.Add(new CaveAmbient(home, member, feet));
            }
        }

        return waiting;
    }

    /// <summary>A cave's small creatures go; returns their members, whose ids wildlife frees.</summary>
    internal IReadOnlyList<WildlifeMember> Remove(Func<CaveAmbient, bool> going)
    {
        CaveAmbient[] leaving = [.. creatures.Where(going)];
        foreach (CaveAmbient creature in leaving) creatures.Remove(creature);
        return [.. leaving.Select(creature => creature.Member)];
    }

    /// <summary>Moves every creature on by some seconds, from where the player stands (world metres).</summary>
    internal void Update(Vector3 player, double seconds)
    {
        if (seconds <= 0) return;
        (hidden, fled) = (0, 0);
        Vector2 here = new(player.X, player.Z);
        foreach (CaveAmbient creature in creatures)
        {
            CaveAmbientKind kind = creature.Home.Kind;
            WildlifeMember member = creature.Member;
            double away = Vector3.Distance(new Vector3(member.Position.X, creature.Feet, member.Position.Y), player);
            if (member.Under)
            {
                // Out of sight until the player has gone well away.
                if (away > kind.FleeMetres * ComeOutShare) (member.Under, creature.FleeingSeconds) = (false, 0);
                else hidden++;
                member.Velocity = Vector2.Zero;
                continue;
            }

            Vector2 velocity;
            if (away <= kind.FleeMetres)
            {
                fled++;
                creature.FleeingSeconds += seconds;
                creature.Goal = null;
                if (creature.FleeingSeconds >= kind.HideSeconds)
                {
                    member.Under = true;
                    member.Velocity = Vector2.Zero;
                    continue;
                }

                Vector2 from = member.Position - here;
                velocity = (from == Vector2.Zero ? Draw(creature.Home.Key, creature.Serial, 2) : Vector2.Normalize(from)) * (float)kind.FleeSpeed;
                member.Enter(Wariness.Respond);
            }
            else
            {
                creature.FleeingSeconds = 0;
                if (member.Stage != Wariness.Calm) member.Enter(Wariness.Calm);
                velocity = Wander(creature, seconds);
            }

            Step(creature, velocity, seconds);
            member.StageSeconds += seconds;
        }
    }

    /// <summary>Wandering: stand a while, then walk to a point near home, and stand again.</summary>
    private Vector2 Wander(CaveAmbient creature, double seconds)
    {
        if (creature.PauseSeconds > 0)
        {
            creature.PauseSeconds -= seconds;
            return Vector2.Zero;
        }

        CaveAmbientHome home = creature.Home;
        if (creature.Goal is not Vector2 goal)
        {
            creature.Serial++;
            goal = new Vector2(home.Home.X, home.Home.Z) + (Draw(home.Key, creature.Serial, creature.Member.Id) * (float)home.Kind.WanderMetres);
            if (home.Pool is CavePool pool) goal = Nearest(pool, goal);
            creature.Goal = goal;
        }

        Vector2 to = goal - creature.Member.Position;
        if (to.Length() <= ArrivedMetres)
        {
            creature.Goal = null;
            creature.PauseSeconds = MostPauseSeconds * ((Draw(home.Key, creature.Serial, 3).X + 1) / 2);
            return Vector2.Zero;
        }

        return Vector2.Normalize(to) * (float)home.Kind.WalkSpeed;
    }

    /// <summary>Moves one creature: over its floor, never off a step it cannot take, or within its pool.</summary>
    private void Step(CaveAmbient creature, Vector2 velocity, double seconds)
    {
        WildlifeMember member = creature.Member;
        member.Velocity = velocity;
        if (velocity == Vector2.Zero) return;
        member.Yaw = MathF.Atan2(velocity.X, -velocity.Y);
        Vector2 next = member.Position + (velocity * (float)seconds);
        if (creature.Home.Pool is CavePool pool)
        {
            // A swimmer turns back at its pool's edge.
            Vector2 inside = Nearest(pool, next);
            if (Vector2.Distance(inside, next) > 1f)
            {
                member.Velocity = Vector2.Zero;
                creature.Goal = null;
                return;
            }

            member.Position = next;
            return;
        }

        if (floorBelow(next.X, next.Y, creature.Feet + StepMetres) is not float floor || Math.Abs(floor - creature.Feet) > StepMetres)
        {
            // A wall or a drop: it stops and picks again; standing over its floor (set down before the floor was all there),
            // it settles onto it.
            member.Velocity = Vector2.Zero;
            creature.Goal = null;
            if (floorBelow(member.Position.X, member.Position.Y, creature.Feet + StepMetres) is float under && under < creature.Feet - SettledMetres) creature.Feet = under;
            return;
        }

        member.Position = next;
        creature.Feet = floor;
    }

    /// <summary>The pool column nearest a point (each column's centre).</summary>
    private static Vector2 Nearest(CavePool pool, Vector2 point) =>
        pool.Columns.Count == 0 ? point : pool.Columns.MinBy(column => Vector2.DistanceSquared(column, point));

    /// <summary>A keyed unit-ish offset, the same every session for one creature's nth draw.</summary>
    private Vector2 Draw(string key, int serial, int salt)
    {
        ulong hash = seed ^ ((ulong)(uint)serial << 20) ^ (ulong)(uint)salt;
        foreach (char c in key) hash = (hash ^ c) * 0x100_0000_01B3UL;
        double angle = (hash & 0xFFFF) / 65536d * 2 * Math.PI;
        double reach = Math.Sqrt(((hash >> 16) & 0xFFFF) / 65536d);
        return new Vector2((float)(Math.Sin(angle) * reach), (float)(-Math.Cos(angle) * reach));
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"ambient={creatures.Count} fleeing={fled} hidden={hidden} kinds=[{string.Join(",", creatures.GroupBy(c => c.Home.Kind.Id).Select(g => $"{g.Key}:{g.Count()}"))}]");
}
