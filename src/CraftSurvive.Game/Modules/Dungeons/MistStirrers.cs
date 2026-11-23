using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// What moves through a sump's mist stirs it (#10993, on rusty-engine #10988's fog stirrers; Patch, 2026-11-22: the mist
/// should shift where enemies pass, like a water current): each point of a body inside the mist (a creature's head,
/// middle and tail; the player's chest) is a sphere moved every step at its own speed. The Engine parts the mist round it,
/// clears it inside it and lets its wake refill. Positions are the dungeon session's frame.
/// </summary>
internal sealed class MistStirrers(IEngineContext engine)
{
    /// <summary>The most stirrers the Engine moves at once.</summary>
    internal const int EngineMost = 32;

    /// <summary>
    /// How big a creature is to the mist, how hard it drags the mist about it and how much it empties inside itself: a
    /// fog foe's long body clears a wide lane behind it, each of its points emptying the mist wholly (Patch, 2026-11-22:
    /// "strengthen it", after a lighter parting read too subtly in motion).
    /// </summary>
    internal float Radius { get; set; } = 1.1f;

    internal float Push { get; set; } = 1f;

    internal float ClearShare { get; set; } = 1f;

    /// <summary>
    /// The player's own stirrer is smaller and clears less: the lantern rides at its middle, and clearing all round it took
    /// the mist's glow off the lantern and thinned the near mist the player is meant to fear. Their wake still shows behind them.
    /// </summary>
    internal float PlayerRadius { get; set; } = 0.6f;

    internal float PlayerClear { get; set; } = 0.4f;

    /// <summary>The player's key among the stirring points.</summary>
    internal const int PlayerId = -1;

    private readonly Dictionary<int, (Vector3 At, uint Slot)> movers = [];

    /// <summary>How many stir the mist now.</summary>
    internal int Live => movers.Count;

    /// <summary>
    /// Moves every point's stirrer on by an update's seconds: one inside the mist stirs where it is with the speed it moved
    /// at, one outside it or gone stops. An update of no time moves nothing (as R10989-2 found for the eddies).
    /// </summary>
    /// <param name="points">Each stirring point, by a key that stays its own from update to update, and where it is.</param>
    internal void Step(double seconds, IEnumerable<(int Key, Vector3 At)> points, Func<Vector3, bool> inMist, uint firstId)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(inMist);
        if (seconds <= 0) return;
        HashSet<int> seen = [];
        foreach ((int id, Vector3 middle) in points)
        {
            if (!inMist(middle)) continue;
            seen.Add(id);
            Vector3 velocity = Vector3.Zero;
            uint slot;
            if (movers.TryGetValue(id, out (Vector3 At, uint Slot) was))
            {
                velocity = (middle - was.At) / (float)seconds;
                slot = was.Slot;
            }
            else if (FreeSlot() is uint free)
            {
                slot = free;
            }
            else
            {
                continue;
            }

            movers[id] = (middle, slot);
            bool you = id == PlayerId;
            engine.CameraView.SetFogStirrer(new FogStirrerRequest(firstId + slot, middle, velocity, you ? PlayerRadius : Radius, Push, you ? PlayerClear : ClearShare));
        }

        foreach (int gone in movers.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            engine.CameraView.RemoveFogStirrer(new FogStirrerRemoval(firstId + movers[gone].Slot));
            movers.Remove(gone);
        }
    }

    /// <summary>Stops every stirrer, as the mist goes.</summary>
    internal void Clear(uint firstId)
    {
        foreach ((Vector3 _, uint slot) in movers.Values) engine.CameraView.RemoveFogStirrer(new FogStirrerRemoval(firstId + slot));
        movers.Clear();
    }

    private uint? FreeSlot()
    {
        HashSet<uint> used = [.. movers.Values.Select(mover => mover.Slot)];
        for (uint slot = 0; slot < EngineMost; slot++)
        {
            if (!used.Contains(slot)) return slot;
        }

        return null;
    }
}
