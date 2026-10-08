using System.Numerics;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>
/// What the player builds with (#9729, Den <c>decision-building-hybrid-pieces</c>): chunky pieces,
/// thin where they should be, drawn as meshes while intact. The order is the cycling order.
/// </summary>
internal enum PieceKind : byte
{
    Wall,
    Doorway,
    Window,
    Post,
    Beam,
    Floor,
    Roof,
    Stairs,
}

/// <summary>The construction maps a piece may be made of (content/game/textures/construction/).</summary>
internal enum PieceMaterial : byte
{
    Planks,
    Timber,
    Masonry,
    Shingles,
}

/// <summary>
/// One box of a piece in the piece's own frame: metres from its anchor (the middle of its footing,
/// on the ground), its half extents, and a pitch about X (positive tips its +Z end down).
/// </summary>
internal readonly record struct PieceBox(Vector3 Centre, Vector3 Half, float Pitch = 0f);

/// <summary>
/// The pieces' shapes as boxes, their materials and names. A piece's +Z is the side it faces the
/// player from when placed; stairs and roofs rise away from it (toward -Z).
/// </summary>
internal static class PieceCatalog
{
    internal const float WallWidth = 2f, WallHeight = 2.5f, WallThickness = 0.2f;
    internal const float DoorWidth = 1f, DoorHeight = 2f;
    internal const float WindowWidth = 0.8f, WindowSill = 1f, WindowHeight = 0.8f;
    internal const float PostSide = 0.3f, BeamSide = 0.3f;
    internal const float FloorSide = 2f, FloorThickness = 0.2f;
    internal const float RoofRun = 2f, RoofThickness = 0.15f;
    internal static readonly float RoofPitch = 35f * MathF.PI / 180f;
    internal const int StairSteps = 5;
    internal const float StairRise = 0.25f, StairWidth = 1f, StairRun = 2f;

    private static readonly Dictionary<PieceKind, PieceBox[]> Shapes = new()
    {
        [PieceKind.Wall] = [new(new(0, WallHeight / 2, 0), new(WallWidth / 2, WallHeight / 2, WallThickness / 2))],
        [PieceKind.Doorway] = Doorway(),
        [PieceKind.Window] = Window(),
        [PieceKind.Post] = [new(new(0, WallHeight / 2, 0), new(PostSide / 2, WallHeight / 2, PostSide / 2))],
        [PieceKind.Beam] = [new(new(0, BeamSide / 2, 0), new(WallWidth / 2, BeamSide / 2, BeamSide / 2))],
        [PieceKind.Floor] = [new(new(0, FloorThickness / 2, 0), new(FloorSide / 2, FloorThickness / 2, FloorSide / 2))],
        [PieceKind.Roof] = [Roof()],
        [PieceKind.Stairs] = Stairs(),
    };

    private static readonly Dictionary<PieceKind, PieceMaterial[]> Allowed = new()
    {
        [PieceKind.Wall] = [PieceMaterial.Planks, PieceMaterial.Masonry, PieceMaterial.Timber],
        [PieceKind.Doorway] = [PieceMaterial.Planks, PieceMaterial.Masonry, PieceMaterial.Timber],
        [PieceKind.Window] = [PieceMaterial.Planks, PieceMaterial.Masonry, PieceMaterial.Timber],
        [PieceKind.Post] = [PieceMaterial.Timber, PieceMaterial.Masonry],
        [PieceKind.Beam] = [PieceMaterial.Timber],
        [PieceKind.Floor] = [PieceMaterial.Planks, PieceMaterial.Masonry, PieceMaterial.Timber],
        [PieceKind.Roof] = [PieceMaterial.Shingles, PieceMaterial.Planks],
        [PieceKind.Stairs] = [PieceMaterial.Timber, PieceMaterial.Masonry, PieceMaterial.Planks],
    };

    internal static IReadOnlyList<PieceKind> Kinds { get; } = Enum.GetValues<PieceKind>();

    internal static IReadOnlyList<PieceBox> Boxes(PieceKind kind) => Shapes[kind];

    /// <summary>The materials a kind may be made of; the first is its default.</summary>
    internal static IReadOnlyList<PieceMaterial> Materials(PieceKind kind) => Allowed[kind];

    internal static bool Allows(PieceKind kind, PieceMaterial material) => Array.IndexOf(Allowed[kind], material) >= 0;

    internal static string Name(PieceKind kind) => kind.ToString().ToLowerInvariant();

    internal static string Name(PieceMaterial material) => material.ToString().ToLowerInvariant();

    internal static bool TryParse(string name, out PieceKind kind) => Enum.TryParse(name, ignoreCase: true, out kind) && Enum.IsDefined(kind);

    internal static bool TryParse(string name, out PieceMaterial material) => Enum.TryParse(name, ignoreCase: true, out material) && Enum.IsDefined(material);

    /// <summary>The content path of a material's map.</summary>
    internal static string Texture(PieceMaterial material) => $"textures/construction/{Name(material)}.png";

    private static PieceBox[] Doorway()
    {
        float side = (WallWidth - DoorWidth) / 4;
        return
        [
            new(new(-(DoorWidth / 2) - side, WallHeight / 2, 0), new(side, WallHeight / 2, WallThickness / 2)),
            new(new((DoorWidth / 2) + side, WallHeight / 2, 0), new(side, WallHeight / 2, WallThickness / 2)),
            new(new(0, (DoorHeight + WallHeight) / 2, 0), new(DoorWidth / 2, (WallHeight - DoorHeight) / 2, WallThickness / 2)),
        ];
    }

    private static PieceBox[] Window()
    {
        float side = (WallWidth - WindowWidth) / 4, top = WindowSill + WindowHeight;
        return
        [
            new(new(0, WindowSill / 2, 0), new(WallWidth / 2, WindowSill / 2, WallThickness / 2)),
            new(new(0, (top + WallHeight) / 2, 0), new(WallWidth / 2, (WallHeight - top) / 2, WallThickness / 2)),
            new(new(-(WindowWidth / 2) - side, WindowSill + (WindowHeight / 2), 0), new(side, WindowHeight / 2, WallThickness / 2)),
            new(new((WindowWidth / 2) + side, WindowSill + (WindowHeight / 2), 0), new(side, WindowHeight / 2, WallThickness / 2)),
        ];
    }

    /// <summary>A slab covering a 2 m run, rising toward -Z, its underside's low edge on the footing.</summary>
    private static PieceBox Roof()
    {
        float slope = RoofRun / MathF.Cos(RoofPitch);
        float rise = RoofRun * MathF.Tan(RoofPitch);
        Vector3 lift = new(0, MathF.Cos(RoofPitch) * RoofThickness / 2, MathF.Sin(RoofPitch) * RoofThickness / 2);
        return new(new Vector3(0, rise / 2, 0) + lift, new(WallWidth / 2, RoofThickness / 2, slope / 2), RoofPitch);
    }

    /// <summary>Solid steps rising toward -Z, each a block from the ground to its tread.</summary>
    private static PieceBox[] Stairs()
    {
        float depth = StairRun / StairSteps;
        PieceBox[] steps = new PieceBox[StairSteps];
        for (int step = 0; step < StairSteps; step++)
        {
            float height = (step + 1) * StairRise;
            steps[step] = new(new(0, height / 2, (StairRun / 2) - (depth * (step + 0.5f))), new(StairWidth / 2, height / 2, depth / 2));
        }

        return steps;
    }
}
