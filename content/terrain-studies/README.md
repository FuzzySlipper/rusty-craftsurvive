# Terrain material sources

These four source PNGs were generated for the content pass (#9666) with Z-Image and
seamless tiling on den-m5 (asset-pipeline `tools/materials/tileable_gen.py --tiling seamless
--size 1024`), from painterly top-down ground prompts. Exact prompts, seeds, source hashes and
the stylise command for each map are retained in
[`prompts.json`](../game/textures/terrain-studies/prompts.json). They replace the earlier GPT
studies (see git history).

`source/` contains the unmodified generator outputs. Runtime copies in
[`content/game/textures/terrain-studies`](../game/textures/terrain-studies/)
are made by `scripts/stylise-texture.py --tile` (512 px, painterly grouping, a pull toward the
palette ramps in `content/style/palette.json`), written as 8-bit RGBA PNG for Engine admission.

`materials.json` records the runtime dimensions, SHA-256 and metres per repeat.
Update its hash after replacing a runtime map. `TerrainGroundMaterials` admits
the maps as authored repeating materials, with Engine triplanar sharpness 4.
There are no painted-in grass-top/soil-side strips and no normal maps.

Bindings: sage ground covers Grass and Dirt; ochre rock covers Stone; dune sand
covers Sand; frost stone covers Snow and Gravel. Construction and vegetation
retain their independent atlas materials. These are replaceable material studies,
not a final palette or an implicit rule for future biome distribution.

Use the ordinary Menu's Landscape study picker and Visit landscape action for
the canyon, uplands and tundra. These are authored areas inside the same streamed
world, with its normal terrain reconstruction, material projection and collision.

## Shape and projection tuning

`materials.json` exposes `metresPerTile` and `triplanarSharpness` per map.
Sharpness blends projections of the same map: 1 is broad, 4 tighter, larger
values approach box projection. It does not blend different ground materials.

`LandscapeStudies` separates an authored geographic anchor from independently
weighted noise banks. These change the unrounded height/density field on the
one-metre voxel grid; they are separate from surface roughness and texture scale.

| Bank | Lattice spacings (metres) | Persistence | Amplitude (metres) |
| --- | --- | --- | --- |
| Regional elevation | 48, 24, 12 | 0.6 | 7 |
| Rock structure | 12, 6, 3 | 0.65 | 5 before signed/ridged shaping |
| Fine relief | 6, 3 | 0.7 | 1.2 |
| Horizontal domain warp, per axis | 32, 16, 8 | 0.5 | 8 |

Each fBm bank normalizes its octave weights before applying its own amplitude.
Fine relief therefore has an independent budget instead of being the negligible
tail of a large hill's noise stack. Rock structure mixes signed and ridged noise;
the canyon floor suppresses detail, and the uplands weight it towards outcrops.
The warped geographic anchor also controls material placement. The smallest
noise lattice is three metres, allowing the one-metre grid to sample its slopes.

These banks are tuning parameters for authored studies. The geographic anchor
is a seam for a future generated world map, not a claim that drainage, erosion,
or a regional map generator is implemented. See the generation direction in
[`survival-direction.md`](../../docs/survival-direction.md).

## Loaded studies

The landscape picker also offers a loaded study for each landscape. These are
finite, walkable Engine spatial sessions covering a 96 × 96 × 48 metre volume
on the one-metre grid. The player enters after all 108 chunks have been admitted.
Select an ordinary landscape entry to return to the overworld.

Loaded studies sample the same metre-space height function and material recipe.
They contain only natural ground, without the world's water or structures, and
do not establish whole-region streaming performance. The player diagnostic's
`landscape` field reports progress and accumulated CPU time in generation,
admission and projection calls; this is not GPU frame time.
