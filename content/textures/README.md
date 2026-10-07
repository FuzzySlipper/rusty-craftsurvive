# Terrain texture provenance

The four checked source PNGs were generated for this repository with OpenAI's
built-in image generation tool on 2026-08-11. They are original,
tileable-looking voxel terrain studies rather than copied game assets. The
prompts asked separately for a square orthographic pixel-art grassy top,
grass-over-dirt side, earthy dirt, and gray stone; each prompt required no
text, objects, lighting gradient, border, or perspective and a seamless edge
treatment suitable for a voxel tile.

`terrain-atlas.png` is the deterministic derivative. Each source is resized
to 64 by 64 with a box filter, converted to RGBA, and placed into the 2 by 2
atlas in the order recorded by `terrain-atlas.json`. The C# terrain catalog
admits this canonical image through Engine AuthoredContent and Appearance:
source slot 1 has grass-side as its base and grass-top as its Engine-directed
`+Y` override, while slots 2 and 3 select dirt and stone. Slot 4 in historical
metadata is retired and is never emitted by the C# terrain recipe. There is no
browser-served duplicate and no atlas-copy script in the current runtime.
`scripts/check-terrain-atlas.mjs` checks the canonical hash, dimensions, and
sky format without requiring a host or renderer.

The typed C# catalog mirrors the canonical metadata's 128 by 128 extent, 64
by 64 regions, nearest filtering, clamp wrapping, and half-texel inset. Engine
validates and resolves the authored catalog; `scripts/check-terrain-atlas.mjs`
remains the focused file audit rather than runtime texture validation.

## Sky panorama provenance

`source/craftsurvive-sky-panorama-klein-s24.png` (sha256 `0ef25a3a918e7b4db31552295fc31b5a3e11e09c92d28131d18fa38d45cb36b3`) was
generated on 2026-10-07 for the content pass (#9667) with FLUX.2 klein on den-nimo
(asset-pipeline `legacy/tools/text-to-3d/layers/text2image/src/klein.py --raw-prompt
--seed 24 --width 2048 --height 1024`; the exact prompt and the other candidates are in
`asset-pipeline/outputs/craftsurvive-content-pass/sky/generate.sh`). It replaces the
2026-08-14 GPT panorama, whose stepped voxel-era clouds no longer fit the world.
`sky-panorama.png` is derived from it by `scripts/prepare-sky-panorama.py` (horizon remapped
to the middle row, the wrap cross-faded seamless, the zenith calmed) as 2048 by 1024 RGBA8;
the script prints the horizon colour `DayNightSky.DayHorizon` carries as the open fog colour.

The panorama is retained as canonical content for future Engine-backed
presentation work. It is presentation-only and does not define environment
light, reflections, collision, picking, or gameplay state.
