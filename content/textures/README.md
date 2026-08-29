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
atlas in the order recorded by `terrain-atlas.json`. The canonical atlas and
metadata are retained here for a future deliberate C# content slice. There is
no browser-served duplicate and no atlas-copy script in the current runtime.
`scripts/check-terrain-atlas.mjs` checks the canonical hash, dimensions, and
sky format without requiring a host or renderer.

The current C# terrain slice uses named flat material colors through Engine's
voxel scene presentation. It does not yet consume this atlas. When a future
product task adopts it, the product should publish the content through a
named Engine resource/material capability rather than reintroducing a browser
asset copy.

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
