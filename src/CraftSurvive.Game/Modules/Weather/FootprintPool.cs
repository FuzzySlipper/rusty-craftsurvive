using System.Numerics;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>Who left a footprint: the player's boots or a creature's paws.</summary>
internal enum FootprintMaker { Boot, Paw }

/// <summary>One footprint as the pool keeps it: where (world metres), which way, which foot and maker, and when it was made.</summary>
internal sealed class Footprint
{
    internal Vector3 At;
    internal float Heading;
    internal bool Left;
    internal FootprintMaker Maker;
    internal double Hours, Fallen;
    /// <summary>The stage shown, or <see cref="TrackFading.Gone"/> for none.</summary>
    internal int Stage = TrackFading.Gone;
    /// <summary>Whether the Engine holds a decal in this slot, and at which stage it was given.</summary>
    internal bool OnEngine;
    internal int PlacedStage = TrackFading.Gone;
    /// <summary>The slot holds a print the Engine has not been given (it may still show the slot's previous one).</summary>
    internal bool Replaced;
}

/// <summary>What the Engine must be told about one pool slot: place (or replace) its print, or remove it.</summary>
internal readonly record struct FootprintChange(int Slot, Footprint? Print);

/// <summary>
/// The footprints in the snow as a pool (#10068): the oldest slot is reused for each new print, each print fades by
/// <see cref="TrackFading"/>, and <see cref="Changes"/> says which slots the Engine's decals must be told about. A slot
/// that takes a new print is always told again, whatever its stage, so a reused slot never keeps showing the print it held.
/// </summary>
internal sealed class FootprintPool
{
    private readonly SnowCover cover;
    private readonly Footprint[] slots;
    private int next;

    internal FootprintPool(SnowCover cover, int size)
    {
        this.cover = cover ?? throw new ArgumentNullException(nameof(cover));
        slots = new Footprint[size];
        for (int i = 0; i < size; i++) slots[i] = new Footprint();
    }

    internal int Size => slots.Length;

    internal long Stamped { get; private set; }

    internal int Live => slots.Count(print => print.Stage < TrackFading.Gone);

    internal int AtStage(int stage) => slots.Count(print => print.Stage == stage);

    internal Footprint this[int slot] => slots[slot];

    /// <summary>A new print at a world point, pointing <paramref name="heading"/> (radians about up), in the oldest slot; returns that slot.</summary>
    internal int Stamp(Vector3 at, float heading, bool left, FootprintMaker maker, double hours)
    {
        int slot = next;
        next = (next + 1) % slots.Length;
        Footprint print = slots[slot];
        print.At = at;
        print.Heading = heading;
        print.Left = left;
        print.Maker = maker;
        print.Hours = hours;
        print.Fallen = cover.FallenAt(at.X, at.Z, hours);
        print.Stage = 0;
        print.Replaced = true;
        Stamped++;
        return slot;
    }

    /// <summary>Each print's stage now, by the snow fallen into it, its age and the snow under it.</summary>
    internal void Fade(double hours)
    {
        foreach (Footprint print in slots)
        {
            if (print.Stage >= TrackFading.Gone) continue;
            print.Stage = TrackFading.StageOf(cover.FallenAt(print.At.X, print.At.Z, hours) - print.Fallen, hours - print.Hours,
                cover.DepthAt(print.At.X, print.At.Z, hours));
        }
    }

    /// <summary>
    /// The slots to tell the Engine about now: a new print, a changed stage, or every print shown when <paramref name="all"/>
    /// (the frame origin moved); with <paramref name="shown"/> false, every placed print is removed. Each change is taken as
    /// told: call <see cref="Told"/> with whether the Engine took it.
    /// </summary>
    internal IReadOnlyList<FootprintChange> Changes(bool shown, bool all)
    {
        List<FootprintChange> changes = [];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            Footprint print = slots[slot];
            int want = shown ? print.Stage : TrackFading.Gone;
            if (want >= TrackFading.Gone)
            {
                if (print.OnEngine) changes.Add(new FootprintChange(slot, null));
                else print.Replaced = false;
                continue;
            }

            if (!print.OnEngine || print.Replaced || print.PlacedStage != want || all) changes.Add(new FootprintChange(slot, print));
        }

        return changes;
    }

    /// <summary>Records that the Engine took a change (a refused one is asked again on the next update).</summary>
    internal void Told(FootprintChange change, bool taken)
    {
        if (!taken) return;
        Footprint print = slots[change.Slot];
        if (change.Print is null)
        {
            print.OnEngine = false;
            print.PlacedStage = TrackFading.Gone;
            return;
        }

        print.OnEngine = true;
        print.PlacedStage = print.Stage;
        print.Replaced = false;
    }

    /// <summary>Every print gone (a new world).</summary>
    internal void Clear()
    {
        foreach (Footprint print in slots) print.Stage = TrackFading.Gone;
    }

    /// <summary>The slots the Engine still shows a print in.</summary>
    internal IEnumerable<int> Placed() => Enumerable.Range(0, slots.Length).Where(slot => slots[slot].OnEngine);
}
