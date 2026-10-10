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

`sky-panorama.png` (2048 by 1024) and `sky-night.png` (1024 by 512) are plain gradients by
elevation (#9804), written by `scripts/make-sky-gradient.py`: no landforms, clouds, stars, sun or
moon. The dynamic sky is drawn over them: the cloud layer, the sun's disc, the horizon terrain and
weather (#9751), and later the moon and stars (#9803). The script's colour stops are the clear sky of
the painted panoramas they replace, and it prints the horizon colours `DayNightSky.DayHorizon` and
`NightHorizon` carry as the open fog colour.

The painted day panorama they replace was derived from
`source/craftsurvive-sky-panorama-klein-s24.png` (FLUX.2 klein, 2026-10-07, #9667) by
`scripts/prepare-sky-panorama.py`. Both stay for reference: its painted mountains stood behind the
map's real horizon and did not match it.

The panoramas are presentation only. Through the Engine's sky light they light and reflect in
the world (`DayNightSky`), but they define no collision, picking or gameplay state.
