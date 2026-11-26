using System.Numerics;
using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>What a cave's ruin offers its life (#10955), read from the cave's layout. Engine-free.</summary>
internal static class CaveRuinSites
{
    /// <summary>A site is named this far over its floor, in the air, as a cave's sites are.</summary>
    internal const float SiteOverFloorMetres = 0.5f;

    /// <summary>A room's corner site stands this far in from its walls.</summary>
    private const float RuinCornerInsetMetres = 2f;

    /// <summary>A site beside a doorway stands this far along the wall from the door's middle, and this far into a room.</summary>
    private const float BesideDoorAlongMetres = 2.2f, BesideDoorInMetres = 1f;

    /// <summary>A cave's ruin doorways in world metres (#11097), for its menders to seal: each carried by the cave's placement.</summary>
    internal static IReadOnlyList<RuinDoorway> Doorways(Func<Vector3, Vector3> toWorld, DungeonLayout layout) =>
        [.. layout.RuinDoorways.Select(door => door.Placed(toWorld))];

    /// <summary>
    /// What a cave's ruin offers its life (#10955), in world metres: each room's middle and corners over its lowest floor
    /// (passages and stairs apart, the vault marked), the thresholds of the doorways whose frames stand and the walls beside
    /// them, the floors under its relic lights, its mouth, and its modules' boxes. Null for a cave without a ruin.
    /// </summary>
    internal static RuinSites? Of(Func<Vector3, Vector3> toWorld, DungeonLayout layout)
    {
        if (layout.RuinSize is not RuinSize size || layout.RuinMouth is not Vector3 mouth) return null;
        Vector3 Over(Vector3 canonical) => toWorld(canonical) + (Vector3.UnitY * SiteOverFloorMetres);
        const float cell = ModuleCanvas.CellSize, storey = ModuleCanvas.StoreyHeight;
        // A storey's walked floor is the voxel row over its slab.
        static float FloorOf(int lattice) => ModularDungeon.BaseY + (lattice * storey) + 1;

        List<RuinRoom> rooms = [];
        List<(Vector3 Min, Vector3 Max)> boxes = [];
        foreach (PlacedModule module in layout.Modules.Where(module => module.Shape.Kind == ModuleKind.Building))
        {
            Vector3 low = new(module.X * cell, ModularDungeon.BaseY + (module.Y * storey), module.Z * cell);
            Vector3 high = low + new Vector3(module.Width * cell, module.Shape.Height * storey, module.Depth * cell);
            Vector3 a = toWorld(low), b = toWorld(high);
            boxes.Add((Vector3.Min(a, b), Vector3.Max(a, b)));
            if (module.Shape == DungeonModules.Breach) continue;
            float floor = FloorOf(module.Y);
            Vector3 middle = new(low.X + (module.Width * cell / 2), floor, low.Z + (module.Depth * cell / 2));
            float farX = low.X + (module.Width * cell) - RuinCornerInsetMetres, farZ = low.Z + (module.Depth * cell) - RuinCornerInsetMetres;
            Vector3[] corners = [new(low.X + RuinCornerInsetMetres, floor, low.Z + RuinCornerInsetMetres), new(farX, floor, low.Z + RuinCornerInsetMetres),
                new(low.X + RuinCornerInsetMetres, floor, farZ), new(farX, floor, farZ)];
            bool passage = module.Shape == DungeonModules.Corridor || module.Shape == DungeonModules.Stair || module.Shape == DungeonModules.Collapse;
            rooms.Add(new RuinRoom(Over(middle), [.. corners.Select(Over)], module == layout.RuinVault, passage));
        }

        List<Vector3> thresholds = [], beside = [];
        foreach (RuinDoorway door in layout.RuinDoorways)
        {
            thresholds.Add(Over(door.Floor));
            Vector3 through = new(door.Through.X, 0, door.Through.Y), along = new(through.Z, 0, through.X);
            foreach (float side in new[] { 1f, -1f })
                beside.Add(Over(door.Floor + (along * BesideDoorAlongMetres * side) + (through * BesideDoorInMetres * side)));
        }

        // A relic hangs in a room: its site is on the floor of the storey it hangs in.
        // The ruin's lights hang in its rooms (its relic rooms' lights among the cave's, and the kit's hall relics).
        bool InModule(Vector3 at) => layout.Modules.Any(module => module.Shape.Kind == ModuleKind.Building
            && at.X >= module.X * cell && at.X < (module.X + module.Width) * cell && at.Z >= module.Z * cell && at.Z < (module.Z + module.Depth) * cell
            && at.Y >= ModularDungeon.BaseY + (module.Y * storey) && at.Y < ModularDungeon.BaseY + ((module.Y + module.Shape.Height) * storey));
        List<Vector3> lights = [.. layout.Lights.Concat(layout.Relics).Where(InModule).Distinct()
            .Select(light => Over(new Vector3(light.X, FloorOf((int)MathF.Floor((light.Y - ModularDungeon.BaseY) / storey)), light.Z)))];
        RuinExtent extent = size switch { RuinSize.Shell => RuinExtent.Shell, RuinSize.Wing => RuinExtent.Wing, _ => RuinExtent.Large };
        return new RuinSites(extent, Over(mouth), rooms, thresholds, beside, lights, boxes);
    }
}
