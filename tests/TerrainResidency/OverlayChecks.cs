using System.Buffers.Binary;
using System.Security.Cryptography;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>The overlay's capacity, the edit transaction's order, and the per-chunk index and snapshot cache.</summary>
internal static class OverlayChecks
{
    internal static void Run()
    {
        // The overlay's capacity is decided at admission, before any Engine call: an edit that would
        // exceed it is refused with a typed reason, and an edit that overwrites cells already held costs
        // no new entries. Per-chunk counts answer "does the player touch this chunk" and agree with the
        // sorted snapshot, which is rebuilt only when the overlay changes.
        {
            ulong seed = TerrainConstants.DefaultSeed;
            TerrainOverlayState full = new(seed);
            int held = TerrainConstants.MaximumOverlayEntries - 2;
            TerrainOverlayEntry[] entries = Enumerable.Range(0, held)
                .Select(i => new TerrainOverlayEntry(new VoxelAddress(i % 256, 20 + (i / 65536), i / 256), TerrainConstants.StoneMaterial))
                .ToArray();
            full.Restore(new TerrainOverlaySnapshot(seed, entries));
            TerrainEditRequest three = TerrainEditRequest.FromCells(
                [new VoxelAddress(-1, 40, 0), new VoxelAddress(-2, 40, 0), new VoxelAddress(-3, 40, 0)], TerrainEditKind.Set, TerrainConstants.StoneMaterial);
            Check.That(TerrainEditAdmission.Admit(three, null, full) is TerrainEditRejected { Reason: TerrainEditRejectionReason.OverlayFull },
                "an edit past the overlay's capacity must be refused at admission, before the Engine");
            TerrainEditRequest two = TerrainEditRequest.FromCells(
                [new VoxelAddress(-1, 40, 0), new VoxelAddress(-2, 40, 0)], TerrainEditKind.Set, TerrainConstants.StoneMaterial);
            Check.That(TerrainEditAdmission.Admit(two, null, full) is TerrainEditAccepted, "an edit that exactly fills the overlay must be admitted");
            TerrainEditRequest rewrite = TerrainEditRequest.FromCells(
                [entries[0].Address, entries[1].Address, entries[2].Address], TerrainEditKind.Clear, TerrainConstants.EmptyMaterial);
            Check.That(TerrainEditAdmission.Admit(rewrite, null, full) is TerrainEditAccepted, "rewriting held cells must not count against capacity");

            // The transaction the edit service runs: an edit past capacity never reaches the Engine step,
            // and a cell named twice is one cell to admission, the Engine and the overlay alike, so an
            // edit admitted one entry short of the cap is recorded rather than thrown after the Engine.
            int engineCalls = 0;
            int engineCells = 0;
            bool EngineAccepts(TerrainEditAccepted accepted)
            {
                engineCalls++;
                engineCells += accepted.Edits.Count;
                return true;
            }

            Check.That(TerrainEditTransaction.Run(three, null, full, EngineAccepts) is TerrainEditRefused { Rejection.Reason: TerrainEditRejectionReason.OverlayFull }
                && engineCalls == 0 && full.Count == held, "an edit past capacity must be refused with OverlayFull and make no Engine call");
            VoxelAddress twice = new(-5, 40, 0);
            TerrainEditRequest duplicated = TerrainEditRequest.FromCells(
                [new VoxelAddress(-4, 40, 0), twice, twice], TerrainEditKind.Set, TerrainConstants.StoneMaterial);
            Check.That(TerrainEditTransaction.Run(duplicated, null, full, EngineAccepts) is TerrainEditRecorded { Receipt.AppliedEdits.Count: 2 }
                && engineCalls == 1 && engineCells == 2 && full.Count == TerrainConstants.MaximumOverlayEntries,
                "a cell named twice must be one edit, admitted and recorded without exceeding the cap");
            Check.That(TerrainEditTransaction.Run(TerrainEditRequest.FromCells([new VoxelAddress(-6, 40, 0)], TerrainEditKind.Set, TerrainConstants.StoneMaterial),
                    null, full, EngineAccepts) is TerrainEditRefused && engineCalls == 1,
                "a full overlay must refuse the next new cell without an Engine call");
            Check.That(TerrainEditTransaction.Run(rewrite, null, full, _ => false) is TerrainEditUnchanged && full.TryGetMaterial(entries[0].Address, out ushort kept) && kept == TerrainConstants.StoneMaterial,
                "an edit the Engine found nothing to change in must leave the overlay alone");

            TerrainOverlayState small = new(seed);
            TerrainOverlaySnapshot empty = small.Snapshot();
            Check.That(ReferenceEquals(empty, small.Snapshot()), "an unchanged overlay must hand back the same snapshot");
            small.Apply((TerrainEditAccepted)TerrainEditAdmission.Admit(TerrainEditRequest.FromCells(
                [new VoxelAddress(5, 3, 5), new VoxelAddress(40, 3, 5)], TerrainEditKind.Clear, TerrainConstants.EmptyMaterial)));
            TerrainOverlaySnapshot after = small.Snapshot();
            Check.That(!ReferenceEquals(empty, after) && after.Entries.Length == 2, "an edit must rebuild the snapshot");
            foreach (TerrainChunkAddress chunk in new[] { new VoxelAddress(5, 3, 5).Chunk, new VoxelAddress(40, 3, 5).Chunk, new TerrainChunkAddress(9, 0, 9) })
            {
                Check.That(small.TouchesChunk(chunk) == after.TouchesChunk(chunk), $"per-chunk counts disagree with the snapshot at {chunk}");
            }

            Console.WriteLine($"Overlay: capacity refused at admission at {TerrainConstants.MaximumOverlayEntries} entries; chunk index and cached snapshot agree.");
        }
    }
}
