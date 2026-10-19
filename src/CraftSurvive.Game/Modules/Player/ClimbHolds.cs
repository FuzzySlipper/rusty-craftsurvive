using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>One look at the face in front of a climber: at a height, what the body would touch there, if anything.</summary>
/// <param name="Height">Above the feet, in metres.</param>
/// <param name="Distance">From the body's surface to the face, or null where nothing is within reach (open air: a ledge).</param>
internal readonly record struct ClimbSample(double Height, double? Distance, Vector3 Normal, BlockId Block);

/// <summary>Why a natural face stops a climb, or none.</summary>
internal enum ClimbStop
{
    None,

    /// <summary>Nothing steep enough within reach to hold.</summary>
    NoFace,

    /// <summary>The rock leans out over the climber.</summary>
    Overhang,

    /// <summary>Loose ground that will not take a hold.</summary>
    Loose,
}

/// <summary>A natural face's verdict: whether it can be held, how high the climb goes, how good the holds are, and what stops it.</summary>
/// <param name="TopHeight">Above the feet: where the climb ends - the ledge when it tops out, else just below what stops it.</param>
/// <param name="Grip">The poorest hold on the way, 0 to 1: stamina drains faster the poorer it is.</param>
internal readonly record struct ClimbFace(bool Holdable, double TopHeight, bool TopsOut, float Grip, ClimbStop Stop);

/// <summary>
/// The climbing limits of natural rock (#10126; owner, 2026-10-19): rather than marking where climbing is allowed, the rock
/// itself limits it, readably. A climber may hold any face steep enough to be a face, but the rock leaning out over them
/// stops them below it, washed and wet rock holds poorly and drains stamina faster, and loose ground (sediment, mud) gives
/// no hold at all; a climb tops out where the face gives way to a ledge. With the stamina bar's reach, these let a cave be
/// closed or opened by what it is made of and how it is shaped.
/// </summary>
internal static class ClimbHolds
{
    /// <summary>A face steeper than walking ground: its normal may lean up at most this much (about 45 degrees from vertical).</summary>
    internal const float SteepestHoldUp = 0.7f;

    /// <summary>Rock whose normal leans down past this (about 15 degrees past vertical) overhangs, and stops a climb below it.</summary>
    internal const float OverhangDown = -0.26f;

    /// <summary>
    /// Rock that comes closer to the climber by more than this per metre of height leans out over them (about 15 degrees):
    /// read from the face's distance where its collision is boxes, whose normals do not say how the rock leans.
    /// </summary>
    internal const double OverhangLeanPerMetre = 0.27d;

    /// <summary>The lean is read over this much height, so the steps of a voxel face's boxes do not read as leaning.</summary>
    internal const double LeanOverMetres = 1.5d;

    /// <summary>A hold must be found within this much above the feet for a climber to take hold.</summary>
    internal const double TakeHoldBelow = 1.5d;

    /// <summary>How good each rock is to hold, 0 to 1: dry rock and old patinated rock hold well, turf less, washed wet rock poorly, loose sediment not at all.</summary>
    internal static float Grip(BlockId block) => block switch
    {
        BlockId.Gravel => WashedGrip,
        BlockId.Dirt => 0f,
        BlockId.Grass => TurfGrip,
        _ => BlockRegistry.Get(block).Climbable ? 1f : 0f,
    };

    private const float WashedGrip = 0.4f, TurfGrip = 0.6f;

    /// <summary>
    /// Reads the samples up the face (lowest first) into a verdict. The climb runs up while the face is a face, holds and
    /// does not lean out; it tops out at the first sample with open air in front once the face has been held.
    /// </summary>
    internal static ClimbFace Read(IReadOnlyList<ClimbSample> samples)
    {
        float grip = 1f;
        bool held = false;
        double top = 0d;
        List<ClimbSample> heldBelow = [];
        foreach (ClimbSample sample in samples)
        {
            ClimbSample? below = heldBelow.LastOrDefault(b => sample.Height - b.Height >= LeanOverMetres - 1e-9);
            bool leansOut = held && below is ClimbSample under && under.Distance is double was && sample.Distance is double now
                && (was - now) / (sample.Height - under.Height) > OverhangLeanPerMetre;
            if (sample.Distance is null)
            {
                // Open air in front: past a held face, this is the ledge to step onto.
                return held ? new ClimbFace(true, sample.Height, true, grip, ClimbStop.None) : new ClimbFace(false, 0d, false, 0f, ClimbStop.NoFace);
            }

            if (sample.Normal.Y < OverhangDown || leansOut)
            {
                return new ClimbFace(held, top, false, grip, ClimbStop.Overhang);
            }

            if (sample.Normal.Y > SteepestHoldUp)
            {
                // Ground in front, not a face: below the first hold it is still the floor; above, the face lies back into a slope to walk.
                if (!held)
                {
                    continue;
                }

                return new ClimbFace(true, sample.Height, true, grip, ClimbStop.None);
            }

            float hold = Grip(sample.Block);
            if (hold <= 0f)
            {
                return new ClimbFace(held, top, false, grip, ClimbStop.Loose);
            }

            if (!held && sample.Height > TakeHoldBelow)
            {
                return new ClimbFace(false, 0d, false, 0f, ClimbStop.NoFace);
            }

            held = true;
            grip = MathF.Min(grip, hold);
            top = sample.Height;
            heldBelow.Add(sample);
        }

        return new ClimbFace(held, top, false, held ? grip : 0f, held ? ClimbStop.None : ClimbStop.NoFace);
    }
}
