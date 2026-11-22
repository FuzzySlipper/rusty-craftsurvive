using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The mist stirred by what passes through it (#10989; Patch, 2026-11-22: the mist should respond to enemies passing
/// with local shifting, like a water current). Something moving in a sump's mist sheds small eddies of thicker mist behind
/// it that ride its wake, spread and thin away: fog volumes of their own beside the sump's laid ones, within the Engine's
/// cap. Fog volumes only add mist, so a wake shows as a moving swirl, never a clear pocket (rusty-engine #10988 asks for
/// more). Positions are the dungeon session's frame.
/// </summary>
internal sealed class MistEddies(IEngineContext engine)
{
    /// <summary>How many eddies may stir at once (<see cref="MistBudget"/>).</summary>
    private const int Slots = MistBudget.EddySlots;

    /// <summary>How near the player a creature stirs the mist: eddies further off would be lost in the mist anyway.</summary>
    internal const double StirWithinMetres = 30;

    // An eddy (shed where MistWake says): it lives so long, grows from its first size to its last, carries on with a share
    // of the mover's speed that falls away, and is thickest just after it is shed.
    private const double LifeSeconds = 2.4, FadeInSeconds = 0.25;
    private const float FirstHalfMetres = 0.45f, LastHalfMetres = 1.3f, CarriedHalfLifeSeconds = 0.8f;
    private const float Edge = 0.8f, NoiseScaleMetres = 0.7f, NoiseStrength = 0.85f, SwirlShare = 1.6f;

    /// <summary>How thick a fresh eddy is, as a share of the sump's mist.</summary>
    internal float DensityShare { get; set; } = 1.4f;

    /// <summary>
    /// The glint an eddy gives of itself, per unit of its density (linear; Fog wildlife's "a pale violet glint" where one
    /// passes): thicker mist alone hardly shows in a thick sump, light does.
    /// </summary>
    internal Vector3 Glint { get; set; } = new(0.35f, 0.2f, 0.55f);

    private readonly Eddy?[] eddies = new Eddy?[Slots];
    private readonly MistWake wake = new();

    private sealed class Eddy(Vector3 at, Vector3 velocity, double born)
    {
        internal Vector3 At { get; set; } = at;
        internal Vector3 Velocity { get; set; } = velocity;
        internal double Born { get; } = born;
    }

    /// <summary>How many eddies stir now, and how many were shed in all.</summary>
    internal int Live => eddies.Count(eddy => eddy is not null);

    internal int Shed { get; private set; }

    /// <summary>
    /// Moves time on: each mover inside the mist sheds an eddy when it is due, and every eddy rides on, grows and thins,
    /// its fog volume set again. <paramref name="inMist"/> says whether a point is inside a sump's mist and how thick it is
    /// there; <paramref name="albedo"/> and <paramref name="glow"/> are the sump mist's own.
    /// </summary>
    internal void Step(double seconds, IEnumerable<(int Id, Vector3 Feet)> moving, Func<Vector3, float?> inMist, Vector3 albedo, Vector3 glow, uint firstId)
    {
        // An update of no time moves no eddy on and sheds none (R10989-2): each keeps its volume as last set.
        if (seconds <= 0) return;
        foreach ((Vector3 at, Vector3 velocity) in wake.Step(seconds, moving, point => inMist(point) is not null)) ShedAt(at, velocity);
        double now = wake.Now;

        for (int slot = 0; slot < Slots; slot++)
        {
            if (eddies[slot] is not Eddy eddy) continue;
            double age = now - eddy.Born;
            if (age >= LifeSeconds)
            {
                eddies[slot] = null;
                engine.CameraView.RemoveFogVolume(new FogVolumeRemoval(firstId + (uint)slot));
                continue;
            }

            eddy.At += eddy.Velocity * (float)seconds;
            eddy.Velocity *= MathF.Pow(0.5f, (float)(seconds / CarriedHalfLifeSeconds));
            float share = (float)(age / LifeSeconds);
            float sump = inMist(eddy.At) ?? 0f;
            float thickness = sump * DensityShare * (float)Math.Min(1, age / FadeInSeconds) * (1f - share);
            float half = FirstHalfMetres + ((LastHalfMetres - FirstHalfMetres) * share);
            float yaw = MathF.Atan2(eddy.Velocity.X, eddy.Velocity.Z) * 180f / MathF.PI;
            engine.CameraView.SetFogVolume(new FogVolumeRequest(firstId + (uint)slot, FogVolumeShape.Ellipsoid, eddy.At, new Vector3(half * 0.8f, half * 0.7f, half),
                yaw, thickness, albedo, glow + Glint, Edge, NoiseScaleMetres, NoiseStrength, eddy.Velocity * SwirlShare));
        }
    }

    /// <summary>Clears every eddy, as the mist goes.</summary>
    internal void Clear(uint firstId)
    {
        for (int slot = 0; slot < Slots; slot++)
        {
            if (eddies[slot] is null) continue;
            eddies[slot] = null;
            engine.CameraView.RemoveFogVolume(new FogVolumeRemoval(firstId + (uint)slot));
        }

        wake.Clear();
    }

    /// <summary>A new eddy in a free slot, or in place of the oldest.</summary>
    private void ShedAt(Vector3 at, Vector3 velocity)
    {
        int slot = Array.FindIndex(eddies, eddy => eddy is null);
        if (slot < 0)
        {
            slot = 0;
            for (int i = 1; i < Slots; i++)
            {
                if (eddies[i]!.Born < eddies[slot]!.Born) slot = i;
            }
        }

        eddies[slot] = new Eddy(at, velocity, wake.Now);
        Shed++;
    }
}
