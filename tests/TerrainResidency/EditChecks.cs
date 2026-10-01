using System.Buffers.Binary;
using System.Security.Cryptography;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>Decided edits, blast charges and their dust, build stamps, and block entities.</summary>
internal static class EditChecks
{
    internal static void Run()
    {
        // A decided cell set is applied exactly as given - that is the whole point of it - and it is
        // bounded, because the bound is the manipulation slice's promise about update latency.
        {
            VoxelAddress[] decided = [new(1, 2, 3), new(4, 5, 6), new(7, 8, 9)];
            TerrainVoxelEdit[] placed = TerrainBrushPolicy.Expand(
                TerrainEditRequest.FromCells(decided, TerrainEditKind.Set, TerrainConstants.StoneMaterial));
            Check.That(placed.Length == decided.Length, $"a decided edit must apply exactly its {decided.Length} cells, applied {placed.Length}");
            Check.That(placed[0].Address == decided[0] && placed[2].Address == decided[2],
                "a decided edit must keep the order and addresses it was given");
            Check.That(placed.All(edit => edit.Material == TerrainConstants.StoneMaterial),
                "a decided set must carry the material it was given");

            TerrainVoxelEdit[] cleared = TerrainBrushPolicy.Expand(
                TerrainEditRequest.FromCells(decided, TerrainEditKind.Clear, TerrainConstants.StoneMaterial));
            Check.That(cleared.All(edit => edit.Material == TerrainConstants.EmptyMaterial),
                "a decided clear must empty every cell it was given, whatever material it was handed");

            Check.Throws<ArgumentOutOfRangeException>(() =>
            {
                _ = TerrainBrushPolicy.Expand(TerrainEditRequest.FromCells(
                    Enumerable.Repeat(new VoxelAddress(0, 0, 0), TerrainBrushPolicy.MaximumTransactionCells + 1).ToArray(),
                    TerrainEditKind.Clear,
                    TerrainConstants.EmptyMaterial));
            }, "a decided edit larger than the transaction bound must be refused");

            Console.WriteLine($"Decided-cell edit request: {placed.Length} cells applied exactly, and {TerrainBrushPolicy.MaximumTransactionCells} is the bound.");

            // The blast policy, exercised at its boundaries rather than only in the middle. A charge
            // resolves as one transaction or not at all.
            Check.That(BlastPolicy.Decide(1).Disposition == BlastDisposition.Single, "a one-cell charge must resolve in one transaction");
            Check.That(BlastPolicy.Decide(BlastPolicy.MaximumCells).Applies, "a charge at the maximum must still fire");
            Check.That(BlastPolicy.Decide(BlastPolicy.MaximumCells + 1).Disposition == BlastDisposition.Refused,
                "a charge past the maximum must be refused rather than truncated");
            Check.That(BlastPolicy.Decide(BlastPolicy.MaximumCells).Cells == BlastPolicy.MaximumCells,
                "an admission must report the charge it decided on");

            // What a charge breaks: every solid block the charge's strength reaches, weighed cell by cell
            // against the block's own blast resistance. Air and water are never edited, bedrock never breaks,
            // and masonry survives the rim that clears dirt.
            ushort dirt = BlockRegistry.Get(BlockId.Dirt).Slot;
            ushort brick = BlockRegistry.Get(BlockId.Brick).Slot;
            ushort bedrock = BlockRegistry.Get(BlockId.Bedrock).Slot;
            ushort water = BlockRegistry.Get(BlockId.Water).Slot;
            VoxelAddress blastCentre = new(100, 8, 100);
            BlastCharge allDirt = BlastCharge.Plan(blastCentre, 2, _ => dirt);
            Check.That(allDirt.Admission.Cells == 33 && allDirt.Cleared.Count == 33,
                $"a radius-2 charge in dirt must break all 33 cells it reaches, broke {allDirt.Cleared.Count} of {allDirt.Admission.Cells}");
            BlastCharge allBrick = BlastCharge.Plan(blastCentre, 2, _ => brick);
            Check.That(allBrick.Cleared.Count > 0 && allBrick.Cleared.Count < allBrick.Admission.Cells,
                $"brick must break near the centre and survive the rim, broke {allBrick.Cleared.Count} of {allBrick.Admission.Cells}");
            Check.That(allBrick.Cleared.Contains(blastCentre), "a charge must break brick at its own centre");
            Check.That(!allBrick.Cleared.Contains(new VoxelAddress(102, 8, 100)), "brick at a radius-2 charge's rim must survive");
            Check.That(BlastCharge.Plan(blastCentre, 3, _ => bedrock).Cleared.Count == 0, "no charge may break bedrock");
            Check.That(BlastCharge.Plan(blastCentre, 3, _ => water).Cleared.Count == 0, "a charge must not drain water");
            Check.That(BlastCharge.Plan(blastCentre, 1, _ => TerrainConstants.EmptyMaterial).Cleared.Count == 0,
                "a charge in air has nothing to break");
            BlastCharge tooBig = BlastCharge.Plan(new VoxelAddress(300, 8, 300), 6, _ => dirt);
            Check.That(tooBig.Admission.Disposition == BlastDisposition.Refused && tooBig.Cleared.Count == 0,
                $"a radius-6 charge is {tooBig.Admission.Cells} cells and must be refused without breaking anything");
            Console.WriteLine($"Blast charge: radius 2 breaks 33/33 dirt and {allBrick.Cleared.Count}/33 brick; bedrock, water and air hold; radius 6 ({tooBig.Admission.Cells} cells) refused.");

            // A charge's dust seed must be admissible wherever the charge goes off: the Engine takes a
            // 53-bit seed, and ordinary centres are negative or past 2047 on some axis.
            VoxelAddress[] centres =
            [
                new(0, 0, 0), new(-1, 8, -1), new(2048, 12, -2048), new(-5120, -9, 5119), new(5119, 40, 5119),
                new(TerrainConstants.MaximumCoordinateMagnitude, 0, -TerrainConstants.MaximumCoordinateMagnitude),
            ];
            ulong[] identities = [.. centres.Select(BlastDust.ChargeIdentity)];
            foreach (ulong identity in identities)
            {
                Check.That(BlastDust.Smoke(default, default, identity).Seed <= BlastDust.MaximumParticleSeed
                    && BlastDust.Debris(default, default, identity).Seed <= BlastDust.MaximumParticleSeed,
                    $"a dust seed must fit 53 bits, identity {identity:x16} does not");
            }

            Check.That(identities.Distinct().Count() == identities.Length, "different charge centres must have different dust identities");
            Check.That(BlastDust.ChargeIdentity(new VoxelAddress(-1, 8, -1)) == identities[1], "the same centre must give the same dust");

            // A stamp is a decided volume too, and the shapes a base is built from have to be exactly the
            // size they claim and no larger than the bound.
            BuildStamp plate = BuildStamp.Plate(new VoxelAddress(0, 4, 0), 3, 4, TerrainConstants.StoneMaterial);
            Check.That(plate.Cells.Count == 12, $"a 3x4 plate is 12 cells, counted {plate.Cells.Count}");
            Check.That(plate.Cells.All(cell => cell.Y == 4), "a plate must stay on its own course");
            Check.That(plate.Cells.Distinct().Count() == plate.Cells.Count, "a plate must not place the same cell twice");
            Check.That(plate.Cells.All(cell => cell.X is >= 0 and < 3 && cell.Z is >= 0 and < 4),
                "a plate must stay inside the footprint it was given");
            Check.That(plate.Placed, "a small plate must be placeable in one transaction");

            BuildStamp wall = BuildStamp.Wall(new VoxelAddress(10, 4, 10), 5, 3, alongX: true, TerrainConstants.StoneMaterial);
            Check.That(wall.Cells.Count == 15, $"a 5x3 wall is 15 cells, counted {wall.Cells.Count}");
            Check.That(wall.Cells.All(cell => cell.Z == 10), "an along-X wall must keep one Z");
            Check.That(wall.Cells.Select(cell => cell.Y).Distinct().Count() == 3, "a 3-tall wall must occupy 3 courses");

            // A stamp from the UI is laid where the player faces: a floor runs away from them, centred
            // across their facing, and a wall stands across it - whichever of the four ways they face.
            Check.That(BuildStamp.Cardinal(0.2f, -0.9f) == (0, -1) && BuildStamp.Cardinal(-0.8f, 0.5f) == (-1, 0),
                "a facing snaps to the nearer axis");
            VoxelAddress aimed = new(10, 4, 10);
            foreach ((int X, int Z) facing in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                BuildStamp ahead = BuildStamp.PlateAhead(aimed, 3, 4, facing, TerrainConstants.StoneMaterial);
                long Ahead(VoxelAddress cell) => ((cell.X - aimed.X) * facing.X) + ((cell.Z - aimed.Z) * facing.Z);
                long Across(VoxelAddress cell) => ((cell.Z - aimed.Z) * facing.X) - ((cell.X - aimed.X) * facing.Z);
                Check.That(ahead.Cells.Count == 12 && ahead.Cells.Distinct().Count() == 12 && ahead.Cells.Contains(aimed)
                    && ahead.Cells.All(cell => cell.Y == 4 && Ahead(cell) is >= 0 and < 4 && Across(cell) is >= -1 and <= 1),
                    $"a 3x4 floor facing {facing} must start at the aimed cell and run 4 away, 3 across");
                BuildStamp across = BuildStamp.WallAcross(aimed, 5, 3, facing, TerrainConstants.StoneMaterial);
                Check.That(across.Cells.Count == 15 && across.Cells.Distinct().Count() == 15 && across.Cells.Contains(aimed)
                    && across.Cells.All(cell => Ahead(cell) == 0 && Across(cell) is >= -2 and <= 2 && cell.Y is >= 4 and < 7),
                    $"a 5x3 wall facing {facing} must stand across the facing, centred on the aimed cell");
            }

            BuildStamp oversizedPlate = BuildStamp.Plate(new VoxelAddress(0, 4, 0), 40, 40, TerrainConstants.StoneMaterial);
            Check.That(!oversizedPlate.Placed, $"a {oversizedPlate.Cells.Count}-cell plate must be refused, not truncated");
            Check.That(BuildStamp.Plate(new VoxelAddress(0, 4, 0), 1, 1, TerrainConstants.StoneMaterial).Cells.Count == 1,
                "a one-cell stamp is one cell");

            // A stamp fills only replaceable cells, so laying a plate across a slope leaves the hill alone
            // and its undo takes back only what it filled.
            BuildStamp onSlope = plate.OnReplaceable(cell => cell.X == 0 ? BlockRegistry.Get(BlockId.Stone).Slot
                : cell.X == 1 ? BlockRegistry.Get(BlockId.Water).Slot : TerrainConstants.EmptyMaterial);
            Check.That(onSlope.Cells.Count == 8 && onSlope.Cells.All(cell => cell.X != 0),
                $"a plate over one stone row must fill the 8 air and water cells and skip the stone, filled {onSlope.Cells.Count}");
            Check.That(!BuildStamp.IsReplaceable(BlockRegistry.Get(BlockId.Bedrock).Slot) && BuildStamp.IsReplaceable(TerrainConstants.EmptyMaterial),
                "air is replaceable and bedrock is not");
            Check.That(!BuildStamp.IsReplaceable(ushort.MaxValue), "an unknown slot must not be replaceable");
            Console.WriteLine($"Build stamp: plate 12 cells, wall 15 cells, {BuildStamp.MaximumStampCells} is the bound; a plate over stone fills only its 8 open cells.");

            // Block entities: one per cell, broken when their cell opens, swept when a volume is cleared.
            BlockEntityIndex entities = new();
            VoxelAddress doorCell = new(4, 5, 4);
            VoxelAddress lightCell = new(6, 7, 6);
            BlockEntity door = entities.Place(BlockEntityKind.Door, doorCell, state: 0);
            entities.Place(BlockEntityKind.Light, lightCell);
            Check.That(entities.Count == 2, $"two entities were placed, counted {entities.Count}");
            Check.That(entities.TryFind(doorCell, out BlockEntity found) && found.Kind == BlockEntityKind.Door,
                "the door must be findable in the cell it was placed in");
            Check.That(found.Id == door.Id, "an entity must keep the identity it was given");
            Check.That(entities.Occupies(lightCell), "the light must occupy its cell");

            // One per cell: placing again replaces rather than accumulating.
            entities.Place(BlockEntityKind.Container, doorCell);
            Check.That(entities.Count == 2, $"replacing a cell must not add an entity, counted {entities.Count}");
            Check.That(entities.TryFind(doorCell, out BlockEntity replaced) && replaced.Kind == BlockEntityKind.Container,
                "placing onto a cell must replace what stood there");

            // The door flag is product meaning, not position.
            entities.Place(BlockEntityKind.Door, doorCell, state: 0);
            Check.That(entities.SetState(doorCell, 1), "an occupied cell must accept a state change");
            Check.That(entities.TryFind(doorCell, out BlockEntity opened) && opened.State == 1, "the door must read as open");
            Check.That(!entities.SetState(new VoxelAddress(0, 0, 0), 1), "an empty cell must not accept a state change");

            // Break removes exactly the cell named, and says whether it did.
            Check.That(entities.Break(doorCell), "breaking an occupied cell must report a removal");
            Check.That(!entities.Break(doorCell), "breaking an empty cell must report nothing removed");
            Check.That(entities.Count == 1, $"one entity must remain, counted {entities.Count}");

            // Blast sweeps a decided volume; entities outside it are untouched.
            BlockEntityIndex swept = new();
            VoxelAddress insideA = new(2, 3, 2);
            VoxelAddress insideB = new(3, 3, 3);
            VoxelAddress outside = new(9, 3, 9);
            swept.Place(BlockEntityKind.Container, insideA);
            swept.Place(BlockEntityKind.Light, insideB);
            swept.Place(BlockEntityKind.Door, outside);
            int destroyed = swept.Sweep([insideA, insideB]);
            Check.That(destroyed == 2, $"a sweep of two occupied cells destroys two, destroyed {destroyed}");
            Check.That(swept.Count == 1 && swept.Occupies(outside), "a sweep must leave entities outside its volume standing");
            Console.WriteLine($"Block entities: {entities.Count} standing after replacement, break and a 2-cell sweep.");

        }
    }
}
