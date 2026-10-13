using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>Whether the player is building, and with what: not at all, with pieces, or working the terrain with the brush.</summary>
internal enum BuildMode { Off, Pieces, Earthworks }

/// <summary>What a place or clear request comes to: the pieces take it, the terrain takes it, or nothing does.</summary>
internal enum BuildEditOutcome { Pieces, Terrain, Refused }

/// <summary>
/// Building is a mode the player enters and leaves (#9811): out of it no ghost stands where they look and
/// nothing is placed, so exploring, travelling and fighting show no building at all. In it they build with
/// pieces, or work the terrain with the brush (earthworks). Digging works in every mode. Engine-free.
/// </summary>
internal static class BuildModes
{
    /// <summary>The mode after the build key: off, pieces, earthworks, and off again.</summary>
    internal static BuildMode Next(BuildMode mode) => mode switch
    {
        BuildMode.Off => BuildMode.Pieces,
        BuildMode.Pieces => BuildMode.Earthworks,
        _ => BuildMode.Off,
    };

    /// <summary>Whether the selected piece's ghost shows where the player looks.</summary>
    internal static bool ShowsGhost(BuildMode mode) => mode == BuildMode.Pieces;

    /// <summary>
    /// Who takes a place or clear request. With pieces, the pieces take both (clearing what is aimed falls
    /// through to digging when no piece is in front of the ground). Working the terrain, it takes both, but a
    /// piece in the way is not dug through. Out of building, nothing is placed, and digging stops at a piece
    /// rather than taking it down.
    /// </summary>
    /// <param name="pieceFirst">Whether a piece (or what is left of one) stands in front of the ground the view meets.</param>
    internal static BuildEditOutcome Edit(BuildMode mode, TerrainEditKind edit, bool pieceFirst) => (mode, edit) switch
    {
        (BuildMode.Pieces, TerrainEditKind.Set) => BuildEditOutcome.Pieces,
        (BuildMode.Pieces, _) => pieceFirst ? BuildEditOutcome.Pieces : BuildEditOutcome.Terrain,
        (_, TerrainEditKind.Set) when mode == BuildMode.Off => BuildEditOutcome.Refused,
        _ => pieceFirst ? BuildEditOutcome.Refused : BuildEditOutcome.Terrain,
    };

    internal static string Name(BuildMode mode) => mode switch
    {
        BuildMode.Pieces => "pieces",
        BuildMode.Earthworks => "terrain",
        _ => "off",
    };
}
