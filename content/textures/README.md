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

`source/craftsurvive-sky-panorama-gpt.png` was generated for this repository
with OpenAI's built-in image generation tool on 2026-08-14. The prompt
requested a seamless 360-degree equirectangular daytime wilderness sky with
broad painterly voxel-game clouds, a centered horizon, and no text, sun disk,
buildings, characters, or nearby objects. `sky-panorama.png` is the RGBA8
runtime copy; it retains the generated image's exact 1774 by 887 2:1
dimensions and sRGB color space.

The panorama is retained as canonical content for future Engine-backed
presentation work. It is presentation-only and does not define environment
light, reflections, collision, picking, or gameplay state.
