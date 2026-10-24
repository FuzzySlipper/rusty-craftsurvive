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

    [DebugCommand("craft.dungeon.water", Description = "Reads the loaded cave's water zones (#10094): kind, surface height, open columns, cells, the extent of the open surface and its mouths, in layout metres.")]
    public string Water() => dungeons.WaterReadout();

    [DebugCommand("craft.dungeon.cards", Description = "Reads the loaded cave's moss and ivy cards (#10092): how many of each kind, and with a kind (mat, face, cushion, curtain) some of its anchors and outward directions, in layout metres.")]
    public string Cards(string kind = "") => dungeons.CardsReadout(kind);

    [DebugCommand("craft.dungeon.overgrowth", Description = "The loaded built dungeon's wall overgrowth (#10096): how many of each kind, and with a kind (VineDrape, IvyClump, IvyMound, RootTangle ...) some anchors and outward directions in layout metres; on or off draws it or not in the next dungeon entered (a before and after).")]
    public string Overgrowth(string kind = "") => dungeons.OvergrowthReadout(kind);

    [DebugCommand("craft.dungeon.studylight", Description = "Assisted, for studies (#10090): sets the underground fill light and fog density (negative restores the game's own), so shapes can be seen and compared.")]
    public string StudyLight(float fill, float fogDensity) => dungeons.StudyLight(fill, fogDensity);

    [DebugCommand("craft.dungeon.rockdetail", Description = "Assisted, for the cave detail study (#10094): draws the next dungeon's voxel rock through a detail shader - grain strength, grain frequency (per metre), facet size (metres) and facet strength; all 0 draws it plainly.")]
    public string RockDetail(float grain, float frequency, float facet, float facetStrength) => dungeons.StudyRockDetail(grain, frequency, facet, facetStrength);

    [DebugCommand("craft.dungeon.rockpalette", Description = "Assisted, for the cave detail study (#10094): true colours the next dungeon's voxel rock from the cave's own palette (strata, mottling, wet floors) instead of its painted texture.")]
    public string RockPalette(bool palette) => dungeons.StudyRockPalette(palette);

    [DebugCommand("craft.dungeon.rockpixels", Description = "Assisted, for the cave detail study (#10094): the detail shader's colour cell in metres (0 none; each cell one flat colour of the palette), its facets' normal steps (0 the default), what cells do toward a screen pixel (0 nothing, 1 fade, 2 grow) and how much neighbouring facets lean alike (0 to 1).")]
    public string RockPixels(float colourCell, float facetSteps, float distance, float coherence) => dungeons.StudyRockPixels(colourCell, facetSteps, distance, coherence);

    [DebugCommand("craft.dungeon.darkness", Description = "Assisted, for the darkness study (#10091): the next dungeon's darkness treatment - today, lights (torches, no fill), lantern (the player's lantern alone), fakery (low fill, dark fog, lantern, mineral glints) or mix (sparse torches, lantern, glints).")]
    public string Darkness(string name) => dungeons.StudyDarkness(name);

    [DebugCommand("craft.dungeon.voxelsize", Description = "Assisted, for the micro-voxel study (#10094): the natural cavern study's (approach n) voxel side, 1, 0.5 or 0.25 metres, for voxel surfaces.")]
    public string VoxelSize(double metres) => dungeons.StudyVoxelSize(metres);

    [DebugCommand("craft.dungeon.stream", Description = "Assisted, for the streamed underworld prototype (#10304): start <radius chunks> <budget chunks per update> enters an endless cave streamed about the player; readout; stop.")]
    public string Stream(string action, int radius = 6, int budget = 8) => dungeons.StudyStream(action, radius, budget);

    [DebugCommand("craft.dungeon.streamwalk", Description = "Assisted, for the streamed underworld prototype (#10304): moves the player this many metres across and through, onto the nearest standing place.")]
    public string StreamWalk(float dx, float dz) => dungeons.StudyStreamWalk(dx, dz);

    [DebugCommand("craft.dungeon.streamradius", Description = "Assisted, for the stored, streamed cave prototype (#10304): the next dungeons stream within this many chunks of the player (0 loads them whole); caves are generated once a run and kept.")]
    public string StreamRadius(int radius) => dungeons.StudyStreamRadius(radius);

    [DebugCommand("craft.dungeon.streamhandoff", Description = "Assisted, for the stored, streamed cave prototype (#10304): hands streamed chunks to the Engine (one transaction and one presentation refresh) at most every so many seconds, sooner when a chunk round the player is missing; 0 every update.")]
    public string StreamHandOff(double seconds) => DungeonModule.StudyStreamHandOff(seconds);

    [DebugCommand("craft.dungeon.streamprobes", Description = "Assisted, for the stored, streamed cave prototype (#10304): the next streamed dungeons' indirect light - follow (a probe volume about the player within the streamed rock, the default), whole (over the whole space, rebaked as rock streams) or off.")]
    public string StreamProbes(string mode) => dungeons.StudyStreamProbes(mode);

    [DebugCommand("craft.dungeon.streamed", Description = "Reads the stored, streamed cave prototype (#10304): the last entry's generation and store cost, and the stream's residency and update cost.")]
    public string Streamed() => dungeons.StreamedReadout();

    [DebugCommand("craft.dungeon.tour", Description = "Assisted, for the streamed cave prototype (#10304): moves the player this many metres along a route from the arrival to the farthest place reachable.")]
    public string Tour(float metres) => dungeons.StudyTour(metres);

    [DebugCommand("craft.dungeon.streamreset", Description = "Clears the streamed cave prototype's measures (#10304), so a walk is measured on its own.")]
    public string StreamReset() => dungeons.StudyStreamReset();

    [DebugCommand("craft.dungeon.cavesize", Description = "Assisted, for the cave size prototype (#10304): the next karst caves' box in units across, up and through (16 m each; 6 4 6 the default); their feature counts scale with its floor area.")]
    public string CaveSize(int chunksX, int chunksY, int chunksZ)
    {
        KarstCave.ChunksX = Math.Clamp(chunksX, 2, 32);
        KarstCave.ChunksY = Math.Clamp(chunksY, 2, 16);
        KarstCave.ChunksZ = Math.Clamp(chunksZ, 2, 32);
        return $"karst cave box {KarstCave.ChunksX * 16}x{KarstCave.ChunksY * 16}x{KarstCave.ChunksZ * 16} m";
    }

    [DebugCommand("craft.dungeon.centrepiece", Description = "Assisted, for the centrepiece studies (#10094): what the next karst caves (approach k) are built round - hall (a great hall), chasm (a bottomless chasm open to the sky), none, or auto (chance).")]
    public string Centrepiece(string choice) => dungeons.StudyCentrepiece(choice);

    [DebugCommand("craft.dungeon.ventorchard", Description = "Assisted, for the underground ecology study (#10096): how many vent orchards (ivory tubes with red crowns round warm glowing seeps) the karst caves (approach k) entered next grow; 0 none.")]
    public string VentOrchard(int count) => dungeons.StudyVentOrchard(count);

    [DebugCommand("craft.dungeon.invertedgrove", Description = "Assisted, for the underground ecology study (#10096): how many inverted groves (canopy hangers, root web and seed sacs under a ceiling close to the surface, a fallfield beneath) the karst caves (approach k) entered next grow; 0 none.")]
    public string InvertedGrove(int count) => dungeons.StudyInvertedGrove(count);

    [DebugCommand("craft.dungeon.funnelgarden", Description = "Assisted, for the underground ecology study (#10096): how many funnel gardens (living catch-basins under a shaft, unfurled elders, debris gardens, drinkers at their feet) the karst caves (approach k) entered next grow; 0 none.")]
    public string FunnelGarden(int count) => dungeons.StudyFunnelGarden(count);

    [DebugCommand("craft.dungeon.glasssteppe", Description = "Assisted, for the underground ecology study (#10096): how many glass steppes (sparse glowing glass tufts over a buried organism, its backs, grit, sentinel spires) the karst caves (approach k) entered next grow; 0 none.")]
    public string GlassSteppe(int count) => dungeons.StudyGlassSteppe(count);

    [DebugCommand("craft.dungeon.ribbonthicket", Description = "Assisted, for the underground ecology study (#10096): how many ribbon thickets (woody arches spanning tall wet passages, ribbon curtains, knot nurseries, shed beds) the karst caves (approach k) entered next grow; 0 none.")]
    public string RibbonThicket(int count) => dungeons.StudyRibbonThicket(count);

    [DebugCommand("craft.dungeon.veilforest", Description = "Assisted, for the underground ecology study (#10096): how many veil forests (lace fans square to the draft between the cave's openings, comb fans, lee drifts, puffs) the karst caves (approach k) entered next grow; 0 none.")]
    public string VeilForest(int count) => dungeons.StudyVeilForest(count);

    [DebugCommand("craft.dungeon.cushionbarrens", Description = "Assisted, for the underground ecology study (#10096): how many cushion barrens (soft hummocks smothering a fallen-in chamber's rubble, split elders, mats, buds) the karst caves (approach k) entered next grow; 0 none.")]
    public string CushionBarrens(int count) => dungeons.StudyCushionBarrens(count);

    [DebugCommand("craft.dungeon.communities", Description = "The underground's stranger communities (#10304): read names the loaded cave's region, depth and the communities it grew; auto lets the next caves choose theirs by region and depth (the default); none grows none. A community's own switch (craft.dungeon.ventorchard ...) overrides the choice.")]
    public string Communities(string mode) => mode == "read" ? dungeons.CommunitiesReadout() : DungeonModule.StudyCommunities(mode);

    [DebugCommand("craft.dungeon.formations", Description = "Assisted, for the geological kit's before and after (#10305): carved carves the next caves' drip formations into the rock and places none of the kit's pieces, in caves or ruins (the look before the kit); kit places them (the default).")]
    public string Formations(string mode)
    {
        KarstCave.StudyCarvedFormations = mode == "carved";
        return KarstCave.StudyCarvedFormations ? "the next caves carve their drip formations into the rock" : "the next caves place the geological kit's formation pieces";
    }

    [DebugCommand("craft.dungeon.lightgrowth", Description = "Assisted, for growth at the light's before and after (#10305): off grows nothing by the daylight in the next caves - no ferns, no moss or roots beyond the older rules - and no fungi in their damp dead ends; on grows them (the default).")]
    public string LightGrowth(string mode)
    {
        KarstCave.StudyNoLightGrowth = mode == "off";
        return KarstCave.StudyNoLightGrowth ? "the next caves grow nothing by the daylight, nor fungi in their dead ends" : "the next caves grow ferns, moss and roots by the daylight and fungi in their damp dead ends";
    }

    [DebugCommand("craft.dungeon.setting", Description = "Assisted, for the community study (#10304): the next caves lie under this region and depth - temperature and moisture (0 to 1, as the world map's) and the cave's depth in metres; a negative depth goes back to each entrance's own.")]
    public string Setting(double temperature, double moisture, double depth) =>
        dungeons.StudySetting(depth < 0 ? null : new CaveSetting(temperature, moisture, (float)depth));

    [DebugCommand("craft.dungeon.ivoryheath", Description = "Assisted, for the underground ecology study (#10096): how many ivory heaths (bristle-crowned storage bulbs on dead flowstone floors, hollow elders, tufts, salt bloom, pods) the karst caves (approach k) entered next grow; 0 none.")]
    public string IvoryHeath(int count) => dungeons.StudyIvoryHeath(count);

    [DebugCommand("craft.dungeon.kitpaint", Description = "Assisted, for the kit paint study (#10089): qwen (the kit's own: Qwen projected on lit views) or plain (painted from the geometry), for the next dungeon entered.")]
    public string KitPaint(string choice) => dungeons.StudyKitPaint(choice);

    [DebugCommand("craft.dungeon.kit", Description = "Reads the loaded dungeon's kit (#10089): how many of each piece, and with a piece's name (wall-alcove, sphere, relic, stair ...) some of its placements and turns, in layout metres; with modules, the building modules and their boxes.")]
    public string Kit(string piece = "") => dungeons.KitReadout(piece);

    [DebugCommand("craft.dungeon.ruinlight", Description = "Assisted, for the ruin light study (#10089): the relics' point lights as a share of their own (0 puts them out), their glows' emission intensity, whether the lantern burns (true/false), the glow of a powered room's inlays and the glow of the roundels' mosaics (0 none); now and for later dungeons.")]
    public string RuinLight(float relicLight, float glow, bool lantern, float inlay, float mosaic) => dungeons.StudyRuinLight(relicLight, glow, lantern, inlay, mosaic);

    [DebugCommand("craft.dungeon.kitlook", Description = "Assisted, for the kit stone study (#10089): texel facet strength, snap steps, coherence, then calming from and full (texels per pixel), its mip and amount (0..1); now and for later dungeons.")]
    public string KitLook(float strength, float steps, float coherence, float calmFrom, float calmFull, float calmMip, float calm) =>
        dungeons.StudyKitLook(strength, steps, coherence, calmFrom, calmFull, calmMip, calm);

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

    [DebugCommand("craft.dungeon.visit", Description = "Moves the player to a place in the dungeon: arrival, breach, loot, floor0..floorN, a layout cell x,y,z, community:<name>, formations, ferns, roots or pocket:<i> (#10304, #10305: open floor near that community, the formations, the ferns, the root tangles or the cave's i-th fungal pocket, that sees the most of it, naming the yaw that faces it), or route:<i>.")]
    public string Visit(string place) => dungeons.Visit(place);

    [DebugCommand("craft.dungeon.sweeproute", Description = "The route walk (#10304): sweeps the player's capsule along a natural cave's main route against the live collision (voxel rock and the communities' trunks) and counts the segments each blocks; passage, not walking.")]
    public string SweepRoute() => dungeons.SweepRoute();

    [DebugCommand("craft.dungeon.validate", Description = "Publishes the Engine's navigation over the loaded dungeon and routes arrival to breach and loot, and back.")]
    public string Validate() => dungeons.Validate();

    [DebugCommand("craft.dungeon.leave", Description = "Leaves the dungeon from its way out, as the UI's Leave does.")]
    public string Leave() => dungeons.Leave();
}
