using System.Numerics;
using CraftSurvive.Game.Modules.Creatures;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>How much of an animal's animation runs, by its distance from the player (#10569).</summary>
internal enum WildlifeTier
{
    /// <summary>The clip plays, the body pitches to the ground every update and the feet are planted.</summary>
    Full,

    /// <summary>The clip plays and the body pitches now and then; the feet keep the clip's flat ground.</summary>
    Play,

    /// <summary>The pose is held, and the animal moves as a still figure too far off to read a gait.</summary>
    Held,

    /// <summary>Not drawn.</summary>
    Hidden,
}

/// <summary>A kind's clips by what they are for, with the rig they belong to. A role the mesh has no clip for falls back to idling.</summary>
internal sealed record WildlifeRig(CreatureRig Rig, IReadOnlyDictionary<WildlifeClipRole, CreatureClip> Clips)
{
    internal static WildlifeRig For(WildlifeMesh mesh, CreatureRig rig)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(rig);
        Dictionary<WildlifeClipRole, CreatureClip> clips = [];
        foreach ((WildlifeClipRole role, string name) in mesh.Clips)
        {
            clips[role] = rig.Clips.TryGetValue(name, out CreatureClip? clip) ? clip : throw new InvalidDataException($"{mesh.File} has no clip '{name}' for {role}");
        }

        if (!clips.ContainsKey(WildlifeClipRole.Idle)) throw new InvalidDataException($"{mesh.File} needs an idle clip");
        return new WildlifeRig(rig, clips);
    }

    /// <summary>The clip for a role: a kind with no warning of its own warns by looking, and any other role left out idles.</summary>
    internal CreatureClip Clip(WildlifeClipRole role)
    {
        if (Clips.TryGetValue(role, out CreatureClip? clip)) return clip;
        return role == WildlifeClipRole.Warn ? Clip(WildlifeClipRole.Look) : Clips[WildlifeClipRole.Idle];
    }
}

/// <summary>
/// What an animal's body shows, as Engine-free rules (#10569): the clip its behaviour calls for, how
/// fast it plays so planted feet do not slide, how its body sits on the ground, and how much of its
/// animation runs at its distance. The presentation applies these and decides nothing.
/// </summary>
internal static class WildlifeClips
{
    /// <summary>Slower than this over the ground is standing.</summary>
    private const float StandingSpeed = 0.05f;

    /// <summary>The clip the animal's state calls for now.</summary>
    internal static WildlifeClipRole Want(WildlifeMember member, WildlifeHerd herd, WildlifeRig rig)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(herd);
        ArgumentNullException.ThrowIfNull(rig);
        PressedResponse response = WildlifeRules.Response(member, herd.Kind);
        if (member.Stage == Wariness.Respond && !member.Young && response == PressedResponse.Stand) return WildlifeClipRole.Stand;
        if (member.Stage == Wariness.Respond && response == PressedResponse.Hunker) return WildlifeClipRole.Hunker;
        float speed = member.Velocity.Length();
        if (speed > StandingSpeed)
        {
            // A gait changes where the two clips' own ground speeds meet, and a smaller body's are slower.
            double between = (rig.Clip(WildlifeClipRole.Walk).Speed + rig.Clip(WildlifeClipRole.Run).Speed) / 2 * Scale(member, herd.Kind);
            return speed > between ? WildlifeClipRole.Run : WildlifeClipRole.Walk;
        }

        if (member.Stage == Wariness.Warn) return WildlifeClipRole.Warn;
        if (member.Stage is Wariness.Look or Wariness.Respond) return WildlifeClipRole.Look;
        return herd.Activity is WildlifeActivity.Graze or WildlifeActivity.Drink ? WildlifeClipRole.Graze : WildlifeClipRole.Idle;
    }

    /// <summary>How large an animal is against its kind's mesh.</summary>
    internal static float Scale(WildlifeMember member, WildlifeKind kind) => member.Size * (member.Young && kind.Young is { } young ? young.Scale : 1);

    /// <summary>A gait's rate for its ground speed, so its planted feet do not slide; anything else plays at its own.</summary>
    /// <param name="scale">The body's scale: a smaller one covers less ground with each stride.</param>
    internal static float Rate(WildlifeClipRole role, float speed, WildlifeRig rig, WildlifeAnimation animation, float scale = 1)
    {
        ArgumentNullException.ThrowIfNull(rig);
        double clipSpeed = rig.Clip(role).Speed * scale;
        if (role is not (WildlifeClipRole.Walk or WildlifeClipRole.Run) || clipSpeed <= 0) return 1;
        return (float)Math.Clamp(speed / clipSpeed, animation.MinimumRate, animation.MaximumRate);
    }

    /// <summary>
    /// Moves an animal's clip on by some seconds. A new clip waits out the hold, so an animal settling
    /// into its slot does not flicker between standing and walking; running, alarm and pressed clips
    /// do not wait. Returns whether the clip changed.
    /// </summary>
    internal static bool Advance(WildlifeMember member, WildlifeHerd herd, WildlifeRig rig, WildlifeAnimation animation, double seconds)
    {
        ArgumentNullException.ThrowIfNull(member);
        member.ClipSeconds += seconds;
        WildlifeClipRole want = Want(member, herd, rig);
        if (want == member.Clip) return false;
        // The young change gait at will: that is how they move.
        bool urgent = member.Young || want is WildlifeClipRole.Run or WildlifeClipRole.Look or WildlifeClipRole.Warn or WildlifeClipRole.Stand or WildlifeClipRole.Hunker;
        if (!urgent && member.ClipSeconds < animation.ClipHoldSeconds) return false;
        member.Clip = want;
        member.ClipSeconds = 0;
        return true;
    }

    /// <summary>
    /// How a body sits between the ground under its hind and fore feet: its height and pitch. A cast
    /// that landed on a prop must not stand it on end (59° beside standing stones, #10568), so each
    /// ground is taken within the foot limit of where the animal stands, and the pitch within its limit.
    /// An animal keeps its back flatter than the slope and lets its legs take the rest, so it pitches by a
    /// share of it: a long body facing up a hill otherwise looks to be rearing.
    /// </summary>
    /// <returns>The height of the body's origin, and its pitch in radians, nose up positive.</returns>
    internal static (float Height, float Pitch) Stance(float feet, float hindGround, float foreGround, double hindMetres, double foreMetres, WildlifeAnimation animation)
    {
        float most = (float)animation.MostFootMetres;
        float hind = Math.Clamp(hindGround, feet - most, feet + most), fore = Math.Clamp(foreGround, feet - most, feet + most);
        double span = foreMetres - hindMetres;
        if (span <= 0) return (feet, 0);
        double limit = animation.MostPitchDegrees * Math.PI / 180;
        double pitch = Math.Atan2(fore - hind, span) * animation.PitchShare;
        if (Math.Abs(pitch) > limit) return (feet, (float)Math.CopySign(limit, pitch));
        return (hind + ((fore - hind) * (float)(-hindMetres / span)), (float)pitch);
    }

    /// <summary>
    /// The ground as it is drawn at a point: columns' heights, each taken at its centre, blended between
    /// the four around the point. The blocks underneath go up in whole metres, and a body sat on those
    /// tips to its pitch limit wherever its feet straddle one step, on ground that is hardly sloping.
    /// </summary>
    internal static double DrawnGround(double x, double z, Func<long, long, double> columnHeight)
    {
        ArgumentNullException.ThrowIfNull(columnHeight);
        double fromX = x - ColumnCentre, fromZ = z - ColumnCentre;
        long west = (long)Math.Floor(fromX), north = (long)Math.Floor(fromZ);
        double east = fromX - west, south = fromZ - north;
        double near = double.Lerp(columnHeight(west, north), columnHeight(west + 1, north), east);
        double far = double.Lerp(columnHeight(west, north + 1), columnHeight(west + 1, north + 1), east);
        return double.Lerp(near, far, south);
    }

    private const double ColumnCentre = 0.5;

    /// <summary>
    /// The tier at a distance. An animal keeps the nearer tier for the margin past each limit, so one
    /// grazing on the line does not flicker between them.
    /// </summary>
    internal static WildlifeTier Tier(double distance, WildlifeTier previous, WildlifeAnimation animation)
    {
        double Limit(double metres, WildlifeTier within) => previous <= within ? metres + animation.TierMarginMetres : metres;
        if (distance <= Limit(animation.FullMetres, WildlifeTier.Full)) return WildlifeTier.Full;
        if (distance <= Limit(animation.PlayMetres, WildlifeTier.Play)) return WildlifeTier.Play;
        return distance <= Limit(animation.DrawMetres, WildlifeTier.Held) ? WildlifeTier.Held : WildlifeTier.Hidden;
    }

    /// <summary>
    /// How near an animal must be for its feet to be planted: the distance of the last of the nearest
    /// few. Foot planting is what a crowd costs (#10569); the rest keep the clip's flat-ground feet.
    /// </summary>
    internal static double PlantWithin(IEnumerable<double> distances, WildlifeAnimation animation)
    {
        double[] nearest = [.. distances.Order().Take(animation.MostPlanted)];
        return nearest.Length < animation.MostPlanted ? double.PositiveInfinity : nearest[^1];
    }

    /// <summary>
    /// Whether planted feet still hold: IK targets are set in the session's frame, so they stand only
    /// while the body's placement in that frame does. A body that turned on the spot needs them again,
    /// and so does one the world's origin moved out from under: its session position changed though it
    /// stands where it stood. Both points are as drawn, in the session.
    /// </summary>
    internal static bool FeetHold(Vector3 plantedAt, Quaternion plantedTurn, Vector3 at, Quaternion turn, WildlifeAnimation animation)
    {
        if (Vector3.Distance(plantedAt, at) >= animation.ReplantMetres) return false;
        // The angle between two turns, from their dot product; the sign says nothing, as a turn and its negative are one.
        double between = 2 * Math.Acos(Math.Min(1, Math.Abs(Quaternion.Dot(plantedTurn, turn))));
        return between * 180 / Math.PI < animation.ReplantDegrees;
    }

    /// <summary>The offset a member's clips start at, so a herd does not move in step.</summary>
    internal static float Phase(WildlifeMember member, ulong seed) =>
        (float)WildlifeRules.Unit(seed, $"wildlife:clip-phase:{member.Id}");
}
