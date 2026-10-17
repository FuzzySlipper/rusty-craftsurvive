# Visual direction

CraftSurvive aims for a beautiful, weathered fantasy world with the texture and
tone of an older 3D game, at a scale and with an environmental richness that
were out of reach in that era. Think of an impossibly expansive landscape from
the late 1990s or 2000s, built from simple, tangible forms and carried by
distance, atmosphere, weather and light.

**The one rule: the references set the mood, Valheim sets the method.** The
approved concept images say what places should feel like: their palette,
composition, architecture and hospitality. Valheim says how the game achieves
that: detail level, texture size, and how much light, fog and weather do the
work. Where a concept image's richness and Valheim's economy disagree on detail,
follow Valheim.

This is art guidance, not a claim about the renderer's implemented features.
Game design lives in the
[Wandering Year Design Atlas](https://github.com/FuzzySlipper/docs/blob/main/wandering-year/readme.md);
Den holds tasks, technical records and accepted tuning values.

## The method: what "like Valheim" means

Valheim combines cheap surfaces with an expensive-looking atmosphere. Take these
six ingredients from it, and not its Norse setting, creatures or building kit.

### 1. Simple forms, small textures

- Models are low-poly with chunky, readable silhouettes and **smooth shading**.
  Flat-shaded faceted models with one colour per facet are not the style.
- Textures are small and carry a few broad marks: colour shifts, planks, cracks,
  moss patches. They do not carry photographic grain, pores or fabric weave.
- A surface shows at most three levels of variation at play distance: the broad
  colour of the mass, a few large marks, and one finer texture. Nothing finer.
- **Texture crunch (owner, 2026-10-14):**
  - **Objects** (props, scatter, buildings, gear and other meshes) use small textures,
    about 32 texels per metre, magnified nearest so texels show crisply up close.
    They are mipmapped and smoothly filtered at distance, so they never sparkle.
  - **The ground and grass stay smoothly filtered.** Crisp, low-res ground breaks into
    blocky patches and shimmers in motion.
  - **Texture size, not filtering, is what keeps surfaces from looking photographic.**
    At 250 to 500 texels per metre, wood and plaster show realistic grain; at about
    32 they read as painted colour.

  The comparison is the Den task "Texture crunch calibration" (#10010).
- **Authored detail, not wrapped texture (owner, 2026-10-17):** a prop or place is a calm surface,
  nearly flat broad colour, carrying a couple of strong authored details (a few bold cracks, one
  ceramic accent) rather than a busy texture tiled over the whole mesh. The approved method paints a
  concept of the object and projects it back onto the clean shape. It then reduces the result as
  pixel art at 32 texels per metre: dark line work stays one crisp texel wide, and the palette is a
  few stone colours plus the accent's own. The comparison is in the Den task for overworld places
  as meshes (#9992); the recipe is in asset-pipeline `legacy/experiments/stele-place-study/`.

### 2. Air and light carry the mood

The mood of a place comes mostly from its atmosphere, not from repainting its
textures. Each region has an **atmosphere preset**: fog colour and density, sky,
sun and ambient light colour, and colour grade. Weather and time of day shift
the preset. The same meadow textures should read golden at noon, amber at
sunset, grey-green in rain and blue at night.

Materials still own their base colour. Check an asset in the neutral daylight
preset first. If it looks wrong there (too lime, grey or flat), fix the texture
or its palette ramp (`content/style/palette.json`), not the atmosphere. Presets
then change mood on purpose; they never cover for a bad asset.

### 3. Far, layered distance

On a clear day you see far. Distant ridges and landmarks stay visible as paler,
bluer, lower-contrast layers. Aerial haze grows steadily with distance, so the
view separates into foreground, middle distance and horizon. Distance must never
read as uniformly sharp and saturated all the way to the skyline.

Fog closes the view in where conditions call for it: weather, forest and wetland
mornings, valleys at dusk. Clear high country keeps the long view.

### 4. Strong light effects

Light is a showpiece, as in Valheim:

- Strong sun shadows with a definite direction; avoid flat, even noon light everywhere.
- Visible light shafts through trees, fog and doorways.
- Lamps, windows, fires and the sun's disc glow and bloom.
- Sunrise and sunset drench the land in colour, not only the sky.
- **Warm firelight against a cool surrounding** is the key contrast. It is what
  makes a town, inn or camp read as shelter.

### 5. Moody but readable night and weather

Night and storms change the mood clearly but never make the world unreadable:

- Night is darker and bluer, and the landform and the route stay legible. Lamps
  and fires matter for warmth and welcome more than for seeing at all.
- Rain greys and cools the palette, darkens wet surfaces and pulls the view in.
  Heavy weather can shrink visibility to a few hundred metres. The path ahead,
  nearby hazards and shelter silhouettes stay readable.
- A gathering storm is a beautiful sight as well as a reason to seek shelter.
  Avoid both a perpetually sunny, safe-looking world and a uniformly bleak one.

### 6. Living things move

Grass, foliage and trees sway, and their motion strengthens with the wind. Rain,
snow, drifting leaves, embers and chimney smoke make conditions visible.
Architecture and most props stay still; motion belongs to living things,
weather and fire.

## Palette

Colour starts from the shared ramps in `content/style/palette.json`. Regional
character comes from which ramps dominate and from the atmosphere preset.

- **An ordinary sunny meadow** is a warm, slightly golden green with deep,
  slightly cool shade under trees and a soft haze towards the horizon. The
  brightest sunlit grass reads as warm olive-gold. It must never read as lime or
  neon green.
- **Shadows are coloured.** They are cool in daylight and warm-rimmed near fire,
  never plain grey.
- **Accents are restrained.** Faded red cloth, softened teal and ceramic insets
  appear in small amounts against the earth, stone and plant colours.

Starting atmospheres for the atlas geography. These are proposals, to be tuned
in the running game:

| Region | Dominant ramps | Atmosphere |
| --- | --- | --- |
| Wet lowlands and coast (the starting port) | meadow, foliage, water, stone | Mild and fresh; soft sea haze; bright but not saturated |
| River forests and upriver towns | foliage, bark, sage, teal | Deep blue-green shade; mist in valleys and mornings; frequent rain |
| High dry country and canyons | sandstone, earth, sage, autumn | Ochre and sun-faded; very long, clear views; strong warm light, violet distance |
| Cold high mountains and tundra | snow, stone, distance | Blue-white, pale sky; severe weather closes in |
| Ancient and buried places | stone, teal, cloth | Worn pale stone; warm work-lights against cool darkness |

## Detail and the concept images

The concept images are richer than the game will be. Take from them:

- mood, light and weather;
- composition, with clear foreground, middle distance and horizon;
- palette families and material choices;
- architectural vocabulary: shapes, openings, rooflines;
- signs of hospitality: lit windows, open thresholds, dry places to sit.

Do not take their density of small objects, ornament, clutter or microtexture.
Translate each image into a few big shapes, a handful of signature details and
small textures, then let atmosphere supply the richness.

**Town buildings** use medium detail: simple masses (walls, heavy roofs, posts)
plus a handful of modelled details that read at town-overview distance, such as
shutters, awnings, signs, balconies, porches, chimneys and lit windows. Plaster,
plank and stone come from small textures, not modelled geometry. The exact amount
is being calibrated.

## Calibration still open

This needs a focused in-game test before it becomes a rule. Judge it in motion
and at normal play distance, not from texture swatches or still close-ups.

- **Building detail.** Calibrate "medium detail" on one town building at the
  overview and entrance distances: how many modelled details, and how large the
  texture marks are.

## Approved references

The [Paperback Sanctum set](style-references/paperback-sanctum/README.md) holds
four approved images with their exact generation prompts and source style
prompt. Both the detailed studies and the broader painted canyon are within
range. Their strengths are monumental landforms, layered distance, worn stone
with restrained coloured accents, distinctive curved ancient structures, and
small practical expedition gear. The ancient openings, tapered ribs and
ceramic-like insets are an approved architectural vocabulary to develop. They do
not fix every ruin's doorway or settle the civilization's lore.

The [town set](style-references/town/README.md) holds eight references for warm,
inhabited sanctuaries: a lowland port, a rainy upriver town and a small
high-country refuge, with establishing views, closer views and interface
studies. Use them for mood, spatial composition and hospitality. Their names,
UI copy and fine details are illustrative.

The [character set](style-references/characters/README.md) holds the approved human character
look: painted surfaces on simple, smooth-shaded forms, with storybook proportions. It includes its
style sheet and exact prompts. Valheim, the method reference above, has no characters facing the
camera, so for people this set governs.

Mood references, not visual styles:

| Reference | What to take from it |
| --- | --- |
| Valheim | The method: see above. |
| Breath of the Wild | Spacious fantasy adventure and ancient mystery, with beauty and danger sharing the landscape. |
| Nausicaä of the Valley of the Wind | A dreamy yet sometimes harsh register; a world larger than its inhabitants. |

Breath of the Wild and Nausicaä guide mood only. Toon rendering, outlines and
anime character design are outside the target.

## Tone

The world is dreamy, spacious and beautiful, and also harsh and dangerous. A
desert can be luminous and inviting to look at while its exposure makes
crossing it hard. Present the world warmly even where play is harsh. Its danger
comes from scale and indifference, not malice, so do not paint deadliness onto
every surface. Survival is about respecting the environment, not spotting
tricks.

Dreaminess comes from colour, light, atmosphere and scale, not from cute or
exaggerated shapes. Harshness does not require photorealism or a grim palette.

Do not default to a conventional medieval castle-and-village world. The living
setting has common gunpowder and early-modern technology alongside magic; the
buried world has its own much older identity. This does not establish a
historical region, an industrial aesthetic or a glowing ancient-machine motif.

## Terrain, scale and construction

- The overworld uses smooth, stylized dual-contoured terrain. Smooth does not
  mean featureless: cliffs, abrupt breaks and harsh rock faces belong.
- Natural terrain uses Engine triplanar projection. Choosing and blending ground
  materials is a separate design decision from projection.
- Props, rocks and vegetation share the ground's textured, softly shaded surface
  language and palette.
- Build the large landform, the route through it and its landmarks before small
  surface decoration.
- Construction may use a grid or discrete pieces for usability. That does not
  make the terrain cubic or require buildings to advertise a voxel grid.
- Dungeons carry the same surface language into enclosed and vertical spaces.
  They can be darker and more threatening while footholds, ledges and openings
  stay readable.

## Scavenging has a physical presence

The abstract building resource must not make exploration read as vacuuming the
scenery into a wallet. Valuable finds are situated in places and recovered from
them, and hauling and extraction stay meaningful. Distinguish finds by form,
placement and restrained feedback. Instant, frictionless conversion of the whole
environment is not the intended fantasy.

## Not this

- Lime or neon greens, or saturation that is high everywhere.
- Clear, crisp, equally saturated air all the way to the horizon.
- Flat, even lighting with weak shadows; weather that changes nothing but particles.
- Photographic microtexture, or realistic PBR surface detail.
- Untextured flat-shaded facets beside the textured ground.
- Toon outlines, anime styling, cute exaggerated shapes.
- Full-screen pixelation, painted-canvas overlays, CRT or deliberate rendering defects.
- A mixed bag of asset styles that do not share the palette and texture scale.
- Concept-art clutter reproduced in geometry.

## Judging visual work

Judge at ordinary play distance, in motion, and in more than one condition: noon,
dusk, night and rain. Compare with the matching kind of Valheim scene (a
meadow, a forest, a storm, a lit building at night) and ask:

- Does the air carry the mood, with haze in the distance, directional light and
  coloured shadow?
- Do foreground, middle distance and horizon separate, so a player can see where
  they might go?
- Is the meadow warm and golden rather than lime?
- Do terrain, props, buildings and inhabitants share one texture scale and
  palette?
- Does a lit building read as warm shelter against a cooler world?
- Do night and storms change the mood strongly while the route stays readable?

Use Engine-owned rendering and presentation services. If a look needs a missing
capability (for example light shafts, per-region fog or bloom), name the
upstream need rather than adding a downstream renderer or a DOM substitute.
