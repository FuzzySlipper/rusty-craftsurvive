# Town kit sources

The editable source of the town kit: Blender scripts that build the buildings, landmarks
and quayside pieces from recipes, and the exporter that writes them as runtime content.
The design source is the
[Towns page of the Wandering Year atlas](https://github.com/FuzzySlipper/docs/blob/main/wandering-year/pages/towns-and-explorers.md);
the [approved mood references](../../docs/style-references/town/README.md) guide the art.

## Contents

- `recipes.json`: building dimensions (storeys, projection, roof, porch, woodshed, sign),
  the shared material vocabulary (colour, roughness, texture and metres per tile) and the
  offline preview scenes' placements and cameras.
- `geometry.py` (mesh, UV and material helpers), `buildings.py` (walls, windows, roofs,
  porches, lanterns, signs and whole buildings), `landmarks.py` (beacon, landing bell),
  `scenery.py` (pier, quay wall, crane, ferry shelter, market canopy and the offline
  preview stage).
- `export_kit.py` writes the runtime kit; `export_runtime.py` holds its glTF helpers.
- `build.py` renders offline previews of a single building or a preview scene. Previews
  are art studies; they are not product UI and do not establish in-game presentation.
- `source/inn-concept.png` is the reference image behind the material prompts.
  [provenance.json](provenance.json) records every generated image's prompt and references.

## The runtime kit

```sh
blender -b --factory-startup --python-exit-code 1 \
  --python art/towns/export_kit.py -- --output content/game/towns
```

This writes, under `content/game/towns`:

- `kit/<piece>.glb`: each building, landmark and quayside piece at its own origin, glTF
  Y-up, front facing +Z. A building's hanging sign is a separate `<piece>-sign.glb`
  pivoted at its suspension point.
- `kit/textures/<material>.png`: one 1024 px RGBA copy of each material image, shared
  by every piece through relative image URIs (the Engine packs them when it opens a GLB).
- `kit.json`: every piece's mesh, material slots, bounds, entrance, selection box,
  lamps, chimneys and sign pivot, in the piece's own frame.

Re-running the exporter reproduces `kit.json` and the textures exactly. Timber palettes
are not baked into separate files: `content/game/towns/palettes.json` tints a material
slot at runtime.

## The rest of a town is product data

Layouts (`content/game/towns/layouts/`), services and their lines
(`services.json`), the people who keep them (`people.json`) and palettes are authored
content, read by `Modules/Towns/TownContent.cs`. A layout places kit pieces in a town's
frame: +X toward the water, the waterfront at 12 m, the arrival view on +Z. The
terrain recipe flattens each town's pad and yard (`Modules/WorldGen/TownGround.cs`);
pieces must stand on it, and `tests/Towns` checks that they do.

## Audio

The four loops (river wash, hearth, off-frame activity and a plucked-string piece) are
original synthesis from `scripts/generate-town-audio.mjs`; their seed and method are in
`content/game/towns/audio-provenance.json`.

```sh
node scripts/generate-town-audio.mjs
```

## Portraits

`content/game/towns/portraits/host.png` is a provisional placeholder. How portraits are
made is decided by its own task; `people.json` names which keeper uses which portrait.
