using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// Product-authored scalar samples. Engine owns reconstruction and collision. Samples are at
/// voxel centres; the unrounded top cell's upper face defines the natural ground surface.
/// Material edits preserve the original magnitude, matching Engine ApplyEdits across reloads.
/// </summary>
internal static class TerrainDensity
{
    private const double SampleCentre = 0.5;
    private const double TopFace = 1.0;
    private const float FaceMagnitude = 0.5f;
    private const float MinimumMagnitude = 0.001f;

    internal static bool IsGround(ushort material) => (BlockId)material is
        BlockId.Grass or BlockId.Dirt or BlockId.Stone or BlockId.Sand or BlockId.Gravel or BlockId.Snow;

    /// <param name="structure">
    /// The signed distance to a shaped landmark's surface near this voxel (TerrainRecipe.StructureDistanceAt):
    /// the landmark's stone is united with the ground, so the nearer surface sets the density and the
    /// Engine reconstructs the landmark's curves rather than its voxel faces.
    /// </param>
    internal static float At(long y, double height, ushort generatedMaterial, ushort material, double? structure = null)
    {
        float distance = (float)(y + SampleCentre - (height + TopFace));
        if (structure is double shape)
        {
            distance = Math.Min(distance, (float)shape);
        }

        // Cut structures and filled ground must retain the sign of the recipe's material.
        // Constructed materials keep face-centred samples; water uses the ground distance so
        // the Engine's non-occluding reconstruction layer preserves the submerged slope.
        float magnitude = IsGround(generatedMaterial) && distance < 0f
            || generatedMaterial == TerrainConstants.EmptyMaterial && distance > 0f
            || generatedMaterial == (ushort)BlockId.Water && distance > 0f
                ? Math.Max(Math.Abs(distance), MinimumMagnitude)
                : FaceMagnitude;
        return material == TerrainConstants.EmptyMaterial ? magnitude : -magnitude;
    }
}
