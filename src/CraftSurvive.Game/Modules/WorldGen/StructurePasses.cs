namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The rules by which structures change the ground, written once so the generator and the
/// chunk-content predicate cannot disagree about them.
/// </summary>
internal static class StructurePasses
{
    /// <summary>
    /// A site's voxel over the material the ground put there. A cut is final but stops one step
    /// below the local ground, the deepest floor a character with no climb reach can step out of.
    /// A fill reaches only air, or the surface course it stands on, so a wall that meets a slope
    /// loses to the slope rather than hollowing the hillside.
    /// </summary>
    internal static ushort ApplySite(ushort before, PoiVoxel site, long y, TerrainColumn column) => site.Kind switch
    {
        PoiVoxelKind.Carve => y >= column.Surface - GenerationConstants.MaximumStructureStepBelowGround
            ? Terrain.TerrainConstants.EmptyMaterial
            : before,
        PoiVoxelKind.Fill => Fills(before, y, column) ? site.Material : before,
        _ => before,
    };

    /// <summary>
    /// A crossing's voxel over what the ground and the sites left. A crossing only fills, so it
    /// is something walked over rather than a dam across the water.
    /// </summary>
    internal static ushort ApplyCrossing(ushort before, PoiVoxel span, long y, TerrainColumn column) =>
        span.Kind == PoiVoxelKind.Fill && Fills(before, y, column) ? span.Material : before;

    private static bool Fills(ushort before, long y, TerrainColumn column) =>
        before == Terrain.TerrainConstants.EmptyMaterial || y == column.Surface;
}
