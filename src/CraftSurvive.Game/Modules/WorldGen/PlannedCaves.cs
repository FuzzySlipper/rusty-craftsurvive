using System.Numerics;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// A planned cave system's boxes as the overworld holds them (#11219): one <see cref="CavePlacement"/> per box, with a tendril
/// for each mouth that has one. A hillside or resurgence mouth's tendril runs from just outside where it comes out, level with
/// its port, into the box past the face (where the box's own port passage meets it); a sinkhole's falls straight down from the
/// ground to the box's way in under its roof; a cliff mouth has none, its face open on the air.
/// </summary>
internal static class PlannedCaves
{
    /// <summary>How far past the box's face a side mouth's tendril runs, so it overlaps the box's own port passage.</summary>
    private const float IntoBoxMetres = 4f;

    /// <summary>How far outside where it comes out a side mouth's tendril starts.</summary>
    private const float OutsideMetres = 1f;

    /// <summary>How high over the ground a tendril's centre starts (as a landmark mouth's does).</summary>
    private const float StartAboveGround = 1.4f;

    /// <summary>How far under the box's roof a sinkhole ends: the karst generator's sinks lie there (<see cref="CavePlacement.SinkHeight"/>).</summary>
    private const float SinkBelowRoof = CavePlacement.SizeY - CavePlacement.SinkHeight;

    /// <summary>How far a tendril bends aside at most, in metres.</summary>
    private const float Wander = 4f;

    private const float Step = 2f;

    /// <summary>The system's boxes as placements, in box order.</summary>
    internal static IReadOnlyList<CavePlacement> Placements(UndergroundSystem system)
    {
        List<CavePlacement> placements = new(system.Boxes.Count);
        foreach (UndergroundBox box in system.Boxes)
        {
            if (box.ChunksX != CavePlacement.BoxChunksX || box.ChunksY != CavePlacement.BoxChunksY || box.ChunksZ != CavePlacement.BoxChunksZ)
            {
                throw new InvalidOperationException($"{system.Id}: a planned box must be the walking session's cave box ({CavePlacement.SizeX}x{CavePlacement.SizeY}x{CavePlacement.SizeZ} m).");
            }

            PlannedBox planned = new(system, box);
            ulong salt = CavePlacement.Mix(system.Seed ^ ((ulong)box.Index * 0x9E37_79B9_7F4A_7C15UL));
            List<CaveTendril> tendrils = [];
            foreach (UndergroundMouth mouth in system.Mouths.Where(m => m.BoxIndex == box.Index && m.Kind != UndergroundMouthKind.Cliff))
            {
                tendrils.Add(mouth.Face == UndergroundFace.PosY ? Sinkhole(box, mouth, salt) : Hillside(box, mouth, salt ^ (ulong)(mouth.Level + 1)));
            }

            placements.Add(CavePlacement.ForPlanned(planned, tendrils));
        }

        return placements;
    }

    /// <summary>Where a side mouth's port lies on its box's face, in world metres.</summary>
    internal static Vector3 PortWorld(UndergroundBox box, UndergroundMouth mouth)
    {
        float ox = (float)box.OriginX, oz = (float)box.OriginZ;
        return mouth.Face switch
        {
            UndergroundFace.PosX => new Vector3(ox + CavePlacement.SizeX, mouth.PortWorldY, oz + mouth.PortU),
            UndergroundFace.NegX => new Vector3(ox, mouth.PortWorldY, oz + mouth.PortU),
            UndergroundFace.PosZ => new Vector3(ox + mouth.PortU, mouth.PortWorldY, oz + CavePlacement.SizeZ),
            UndergroundFace.NegZ => new Vector3(ox + mouth.PortU, mouth.PortWorldY, oz),
            _ => new Vector3(ox + mouth.PortU, (float)box.TopY, oz + mouth.PortV),
        };
    }

    /// <summary>The outward heading of a side face.</summary>
    internal static (int X, int Z) Outward(UndergroundFace face) => face switch
    {
        UndergroundFace.PosX => (1, 0),
        UndergroundFace.NegX => (-1, 0),
        UndergroundFace.PosZ => (0, 1),
        _ => (0, -1),
    };

    private static CaveTendril Hillside(UndergroundBox box, UndergroundMouth mouth, ulong salt)
    {
        (int nx, int nz) = Outward(mouth.Face);
        Vector3 port = PortWorld(box, mouth);
        // From just outside where it comes out, at the ground, to past the face at the port's height: in along -outward.
        Vector3 start = new((float)mouth.WorldX + (nx * OutsideMetres), (float)mouth.WorldY + StartAboveGround, (float)mouth.WorldZ + (nz * OutsideMetres));
        Vector3 end = port - new Vector3(nx * IntoBoxMetres, 0f, nz * IntoBoxMetres);
        float run = MathF.Abs(((end.X - start.X) * -nx) + ((end.Z - start.Z) * -nz));
        float wander = (((salt & 0xFFFF) / 65535f * 2f) - 1f) * Wander;
        int steps = Math.Max(1, (int)MathF.Ceiling(run / Step));
        Vector3[] path = new Vector3[steps + 1];
        for (int n = 0; n <= steps; n++)
        {
            float t = (float)n / steps;
            float aside = wander * MathF.Sin(MathF.PI * t);
            Vector3 straight = Vector3.Lerp(start, end, t);
            // Aside is across the heading: along z for a run in x, along x for a run in z.
            path[n] = straight + new Vector3(nz * aside, 0f, nx * aside);
        }

        return new CaveTendril(path, -nx, -nz, salt);
    }

    private static CaveTendril Sinkhole(UndergroundBox box, UndergroundMouth mouth, ulong salt)
    {
        float x = (float)box.OriginX + mouth.PortU, z = (float)box.OriginZ + mouth.PortV;
        float top = (float)mouth.WorldY + StartAboveGround, bottom = (float)box.TopY - SinkBelowRoof;
        int steps = Math.Max(1, (int)MathF.Ceiling((top - bottom) / Step));
        Vector3[] path = new Vector3[steps + 1];
        for (int n = 0; n <= steps; n++)
        {
            path[n] = new Vector3(x, top + ((bottom - top) * n / steps), z);
        }

        return new CaveTendril(path, 0, 0, salt);
    }
}
