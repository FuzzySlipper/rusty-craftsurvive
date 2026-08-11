# Terrain texture provenance

The four checked source PNGs were generated for this repository with OpenAI's built-in image
generation tool on 2026-08-11. They are original, tileable-looking voxel terrain studies rather
than copied game assets. The prompts asked separately for a square orthographic pixel-art texture
of a grassy top, a grass-over-dirt side, earthy dirt, and gray stone; each prompt required no text,
objects, lighting gradient, border, or perspective and a seamless edge treatment suitable for a
voxel tile.

`terrain-atlas.png` is the deterministic runtime derivative. Each source is resized to 64 by 64
with a box filter, converted to RGBA, and placed into the 2 by 2 atlas in the order recorded by
`terrain-atlas.json`. Run `scripts/build-terrain-atlas.sh` from the repository root to reproduce
both the canonical content copy and the browser-served copy. The script prints the SHA-256 hash;
update the checked metadata and Rust admission expectation only when intentionally changing art.

Greedy box faces are projected in stable world cell units, so a merged quad repeats its selected
atlas region rather than stretching it. Vertical faces use horizontal distance for U and inverted
world Y for V, matching the atlas's top-origin rows and keeping the green edge of grass-side tiles
at the physical top on every cardinal face. Grass top and side faces are split into presentation
material slots without changing canonical grass voxels. Marching-cubes and dual-contouring
triangles use the same deterministic world projection; grass-side triangles always choose a
vertical plane even on steep slopes.
