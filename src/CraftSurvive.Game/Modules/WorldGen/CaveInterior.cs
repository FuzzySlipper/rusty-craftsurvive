namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// What a cave's interior holds once it is generated (#10889): its voxels in the cave's own (canonical) box, each a material
/// and a density magnitude, ready to be read into the overworld's chunks where the box lies. Immutable once made.
/// </summary>
internal sealed class CaveInterior
{
    internal CaveInterior(int sizeX, int sizeY, int sizeZ, ushort[] materials, float[] densities)
    {
        if (materials.Length != sizeX * sizeY * sizeZ || densities.Length != materials.Length)
        {
            throw new ArgumentException("A cave interior needs one material and one density per voxel of its box.");
        }

        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        Materials = materials;
        Densities = densities;
    }

    internal int SizeX { get; }
    internal int SizeY { get; }
    internal int SizeZ { get; }

    /// <summary>The canonical box's materials, x fastest, then y, then z.</summary>
    internal ushort[] Materials { get; }

    /// <summary>Each voxel's density magnitude in voxel units (its sign is the material's).</summary>
    internal float[] Densities { get; }

    internal int Index(int i, int j, int k) => (((k * SizeY) + j) * SizeX) + i;
}
