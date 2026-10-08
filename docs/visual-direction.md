# Visual direction

CraftSurvive aims for a beautiful, weathered fantasy world with the texture and
tone of an older 3D game, realized at a scale and with environmental richness
that would have been out of reach for that era. Think of an impossibly expansive
1990s or 2000s landscape: simple, tangible surfaces and forms, supported by
distance, atmosphere, weather, and light.

This is art guidance, not a claim about the renderer's implemented features.
The gameplay relationship is in [survival-direction.md](survival-direction.md).
Reference-image discussion and unresolved art choices belong in Den; an
individual reference image does not establish every detail of the style.

## Approved visual references

The [Paperback Sanctum reference set](style-references/paperback-sanctum/README.md)
contains four approved images with their exact generation prompts and the source
style prompt. Use the images as concrete positive references alongside this guide.
Both the detailed canyon, tundra, and dungeon studies and the broader painted
canyon are within the intended range. Do not reject the detailed studies merely
because they contain more naturalistic lighting or surface detail than the
painted variant.

Their shared strengths are monumental landforms, layered distance, tangible
volumes, worn stone with restrained colored accents, distinctive curved ancient
structures, and small practical expedition equipment. Warm ochres, faded reds,
and softened green or teal accents against cooler distance form a useful palette
family, adapted to the climate and lighting rather than imposed on every scene.

The ancient openings, tapered ribs, and ceramic-like insets provide an approved
architectural vocabulary to develop. They do not require every ruin to repeat
the same doorway or settle the civilization's lore. These are concept references;
retro texture density and filtering still need to be judged in a moving game.

## Tone and references

The world can be dreamy, spacious, and beautiful, and also harsh and dangerous.
These qualities should coexist. A desert can be luminous and inviting to look
at while its exposure makes crossing it difficult. A frozen landscape can feel
quiet and immense before weather closes around the traveler.

Present the world warmly even where play is harsh. Its danger comes from scale
and indifference, not malice: a pleasant meadow is hazardous because it is a
large system that does not account for one small traveler, not because it is
conspiring against them. Do not paint deadliness onto every surface as dramatic
foreshadowing. The warmth carries a quiet awe at the environment, and survival
is a matter of respecting it rather than spotting tricks and traps.

Use the references for the qualities named here:

| Reference | What to take from it |
| --- | --- |
| Valheim | The intended balance of retro character, restrained texture resolution, and tactile rendering with rich atmosphere; also a useful level of terrain stylization. |
| Breath of the Wild | A sense of spacious fantasy adventure and ancient mystery, with beauty and danger sharing the landscape. |
| Nausicaä of the Valley of the Wind | A dreamy yet sometimes harsh emotional register and a sense of a world larger than its inhabitants. |

The latter references guide mood; they do not establish an anime or JRPG visual
style. Overt toon rendering and anime character design are outside the target.
Their palettes, architecture, costumes, and technology motifs are not a kit to
copy wholesale. The approved set begins an ancient architectural vocabulary;
its wider technology, objects, and cultural identity remain to be developed.

Do not default to a conventional medieval castle-and-village world. The living
setting includes common gunpowder and early-modern technology alongside magic;
the buried world has its own much older identity. Those layers need room in the
architecture and objects. This does not establish a particular historical region,
an industrial aesthetic, or a standard glowing ancient-machine motif.

Painterly treatment is compatible with the target. Keep it grounded in tangible
forms and material differences rather than pushing everything toward a soft,
storybook cartoon. Dreaminess can come from color, light, atmosphere, and scale;
it need not depend on exaggerated or cute shapes. Harshness does not require
photorealism or a uniformly grim palette.

## Retro surface character

Existing game textures and other art assets are provisional, not a visual baseline
to preserve. Author replacement materials against the approved references; legacy
grass-top/dirt-side tiles and their hard bands do not constrain the new terrain.

Use some low-resolution texture character and a degree of crunchy filtering.
The result should register first as a coherent world and only then as having
older-game texture. Large visible pixels must not dominate every material or
become the subject of the image.

Favor readable material colors, broad variation, and economical detail. Texture
marks should belong to the surface and remain understandable at normal play
distance. More detailed geometry and lighting do not require photorealistic
surface maps. Conversely, retro character does not require stripping away
atmosphere, scale, or environmental complexity.

Economical detail is a hierarchy, not a blanket ban on weathering or convincing
materials. The approved images allow detailed stone and cloth where they support
form and place. Preserve quiet areas and readable large shapes rather than
forcing every surface to be equally sparse or equally intricate.

Painterly color grouping or authored texture marks can provide that economical
detail. Judge them as part of the material in a moving 3D scene; neither dense
illustration detail nor a uniform paint effect across the entire view is needed.

Do not assume a universal texture resolution or a mandatory nearest-neighbor
filter. Filtering, texture density, and contrast must be judged together in
motion and at the distance where a material is normally seen. An enlarged
texture swatch alone cannot establish the right amount of crunch.

A full-screen pixelation effect, painted-canvas overlay, simulated CRT, or
deliberate rendering defects are not requirements of this direction. The
older-game feeling should arise from authored forms and surfaces rather than
depend on such effects.

## Terrain, scale, and construction

The wider world uses smooth, stylized dual-contoured terrain as its target.
Landforms should support broad slopes, cliffs, canyon walls, caves, and worn
transitions. Smooth terrain does not mean featureless rounded hills everywhere:
strong silhouettes, abrupt breaks, and harsh rock faces still belong.

Use Engine triplanar texture projection for reconstructed natural terrain. Keep
texture scale and blending readable across slopes and cliffs. Projection blending
and the choice or transition between ground materials are separate decisions:
triplanar mapping alone does not blend grass into rock or replace directional
material selection. Design those transitions with the new textures rather than
reproducing cube-face decoration.

Prioritize the large landform, the route through it, and its landmarks before
small surface decoration. A convincing sense of distance can come from layered
terrain, changing visibility, and intervening landforms; it does not require a
fixed world-area quota or continuous walking between every region.

Construction may use blocks, a grid, or discrete pieces to simplify assembly.
That interaction choice does not make the terrain cubic or require every
building surface to advertise a voxel grid. Keep structures legible against
the landscape and compatible with its material treatment. Exact construction
shapes and joining rules remain open.

## Atmosphere and readability

Light, sky, distance, and weather should make restrained assets feel part of a
large living environment. Preserve enough separation between foreground,
middle distance, and horizon for a player to understand where they might go.
Atmosphere can soften far forms while nearby routes and hazards remain legible.

Weather must have a presence in the landscape and communicate changes in
conditions. Beauty is not confined to clear skies: a gathering storm can be a
striking sight as well as a reason to seek shelter. Avoid making every location
perpetually sunny and safe-looking, or uniformly bleak and colorless.

Color belongs to the authored materials. Shared palette ramps (`content/style/palette.json`)
and the stylisation scripts set it. The final color grade is a light, near-neutral trim: a
little warmth and contrast. It must not compensate for an asset's color. If a surface reads too
saturated, grey or flat, correct the texture or its ramp rather than the grade. Weather and
time of day may shift the grade per condition. The accepted values and their history are in
Den (`decision-colour-grade`).

Dungeons carry the same surface language into enclosed and vertical spaces.
Their mood can become darker and more threatening while footholds, ledges,
openings, and relevant objects remain readable. At home, construction and
inhabitants should communicate occupancy and the value of returning.

## Scavenging has a physical presence

The abstract building resource simplifies what construction consumes. It must
not make exploration read as vacuuming the scenery into a wallet. Valuable
finds should feel situated in places and recovered from them, with hauling and
extraction remaining meaningful.

Distinguish useful finds through form, placement, and restrained feedback that
fit the world. Exact pickup presentation, cargo representation, and conversion
effects are open; this does not require every carried unit to have a visible
mesh. It does rule out treating instant, frictionless conversion of the whole
environment as the intended fantasy.

## Using this guidance

Judge visual work at ordinary play distance and in motion. Look for:

- A readable landscape with a strong sense of place, scale, and possible routes.
- Restrained retro texture and filtering, without overwhelming pixel patterns.
- Smooth stylized terrain with deliberate large forms and useful local detail.
- Beauty that can accommodate exposure, severe weather, and threatening places.
- Consistent treatment across terrain, structures, props, and inhabitants.
- Clear gameplay information without relying on debug views or intrusive effects.

These are criteria for visual direction, not a fixed renderer recipe or a demand
that every scene show every quality. Use Engine-owned rendering and presentation
services; if an effect requires a missing capability, identify the upstream need
rather than adding a downstream renderer or a DOM substitute.
