using Rusty.Engine;
using System.Globalization;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>Live-debug adapter over dungeons: read the state, enter at an entrance, leave by the way out.</summary>
public sealed class DungeonDebugModule : IDebugCommandModule
{
    private readonly Func<DungeonModule> dungeonsSource;
    private DungeonModule dungeons => dungeonsSource();

    internal DungeonDebugModule(Func<DungeonModule> dungeons) =>
        this.dungeonsSource = dungeons ?? throw new ArgumentNullException(nameof(dungeons));

    [DebugCommand("craft.dungeon.readout", Description = "Reads where the player stands with dungeons: state, loading progress and cost, the nearby entrance.")]
    public string Readout() => dungeons.Readout();

    [DebugCommand("craft.dungeon.studylight", Description = "Assisted, for studies (#10090): sets the underground fill light and fog density (negative restores the game's own), so shapes can be seen and compared.")]
    public string StudyLight(float fill, float fogDensity) => dungeons.StudyLight(fill, fogDensity);

    [DebugCommand("craft.dungeon.rockdetail", Description = "Assisted, for the cave detail study (#10094): draws the next dungeon's voxel rock through a detail shader - grain strength, grain frequency (per metre), facet size (metres) and facet strength; all 0 draws it plainly.")]
    public string RockDetail(float grain, float frequency, float facet, float facetStrength) => dungeons.StudyRockDetail(grain, frequency, facet, facetStrength);

    [DebugCommand("craft.dungeon.rockpalette", Description = "Assisted, for the cave detail study (#10094): true colours the next dungeon's voxel rock from the cave's own palette (strata, mottling, wet floors) instead of its painted texture.")]
    public string RockPalette(bool palette) => dungeons.StudyRockPalette(palette);

    [DebugCommand("craft.dungeon.rockpixels", Description = "Assisted, for the cave detail study (#10094): the detail shader's colour cell in metres (0 none; each cell one flat colour of the palette), its facets' normal steps (0 the default), what cells do toward a screen pixel (0 nothing, 1 fade, 2 grow) and how much neighbouring facets lean alike (0 to 1).")]
    public string RockPixels(float colourCell, float facetSteps, float distance, float coherence) => dungeons.StudyRockPixels(colourCell, facetSteps, distance, coherence);

    [DebugCommand("craft.dungeon.voxelsize", Description = "Assisted, for the micro-voxel study (#10094): the natural cavern study's (approach n) voxel side, 1, 0.5 or 0.25 metres, for voxel surfaces.")]
    public string VoxelSize(double metres) => dungeons.StudyVoxelSize(metres);

    [DebugCommand("craft.dungeon.keepunwalkable", Description = "Assisted, for studies (#10090): true enters a dungeon candidate even when its routes fail (to compare looks on one layout); false restores refusal.")]
    public string KeepUnwalkable(bool keep) => dungeons.KeepUnwalkable(keep);

    [DebugCommand("craft.dungeon.enter", Description = "Enters the dungeon at the entrance the player stands at, as the UI's Enter does.")]
    public string Enter() => dungeons.Enter();

    [DebugCommand("craft.dungeon.approach", Description = "Chooses how the next dungeon entered is generated: a (carve and stamp), b (modules), c (sculpted cave), v (the chasm sketch), s (the shaft sketch); for studies, n (natural cavern, #10090) or k (karst cave generator, #10094).")]
    public string Approach(string name) => dungeons.Choose(name);

    [DebugCommand("craft.dungeon.seed", Description = "Makes every dungeon entered use a chosen seed (as the dungeon bank numbers them), or \"entrance\" to go back to each entrance's own.")]
    public string Seed(string seed) => dungeons.ChooseSeed(seed);

    [DebugCommand("craft.dungeon.surface", Description = "Chooses how the next dungeon's voxels are surfaced: cubes, dc (dual-contoured worn rock, building on the grid), faceted (flat-faceted rock), ruined (faceted rock, weathered building), mc (marched rock, cube building); for the rock study (#10090): mesh (rock as the Engine's mesh of the field), fine (that mesh at half a metre) or pieced (rock pieces over it); for the micro-voxel study (#10094): micro (every voxel a cube).")]
    public string Surface(string name) => dungeons.ChooseSurface(name);

    [DebugCommand("craft.dungeon.blast", Description = "Carves a sphere of the given radius (metres) out of the loaded dungeon where the player aims, and reports the rebuild cost.")]
    public string Blast(float radius) => dungeons.Blast(radius);

    [DebugCommand("craft.dungeon.chamber", Description = "Assisted (#9505 trial): carves a rounded chamber of this half width (metres) where the player aims, as an implicit field stamp (stamp true) or per-voxel region writes (stamp false), and reports the cost.")]
    public string Chamber(float halfWidth, bool stamp) => dungeons.Chamber(halfWidth, stamp);

    [DebugCommand("craft.dungeon.topology", Description = "Assisted (#9507 exploration): reads the loaded dungeon's chunks and the dual-contouring cells its surface passes through more than once.")]
    public string Topology() => dungeonsSource().ReadScene() is VoxelSceneReadout scene
        ? string.Create(CultureInfo.InvariantCulture, $"chunks={scene.ResidentChunkCount};solidVoxels={scene.SolidVoxelCount};multiLoopCells={scene.MultiLoopCells}")
        : "no dungeon is loaded";

    [DebugCommand("craft.dungeon.vertexocclusion", Description = "Assisted (#9506 exploration): darkens the loaded dungeon's surface vertices by the solid voxels around them at the given strength (0 off, 1 full); every chunk remeshes.")]
    public string VertexOcclusion(float strength)
    {
        return dungeonsSource().ConfigureVertexOcclusion(strength) is VoxelSceneReadout scene
            ? string.Create(CultureInfo.InvariantCulture, $"vertexOcclusion={strength:F2};chunks={scene.ResidentChunkCount}")
            : "no dungeon is loaded";
    }

    [DebugCommand("craft.dungeon.visit", Description = "Moves the player to a place in the dungeon: arrival, breach, loot, floor0..floorN, or a layout cell x,y,z.")]
    public string Visit(string place) => dungeons.Visit(place);

    [DebugCommand("craft.dungeon.validate", Description = "Publishes the Engine's navigation over the loaded dungeon and routes arrival to breach and loot, and back.")]
    public string Validate() => dungeons.Validate();

    [DebugCommand("craft.dungeon.leave", Description = "Leaves the dungeon from its way out, as the UI's Leave does.")]
    public string Leave() => dungeons.Leave();
}
