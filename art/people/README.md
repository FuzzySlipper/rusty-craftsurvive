# People

The town's people are detailed pixel art in the manner of 1990s PC role-playing games (atlas Towns page, "How
people are shown"). Each person is generated once and rigged as 2D parts with rig-c. The Engine poses both rigs at
runtime (its parts rigs, `IEngineContext.PartsRigs`): the figure at their door and the bust in the dialogue. Nothing
is baked. Den #10027 holds the decisions and the evidence.

## The route

1. **Figure.** Krea 2 turbo with the pixel-art card (`provenance.json` holds the exact prompts): the person drawn
   in a stance parts can be cut from (arms clear of the body, feet apart, legs straight), 832x1216. Colour comes
   from the person's description, not the card. Krea's "pixel art" has texels of about 5 to 6 px, not on one
   exact grid.
2. **Portrait.** Krea image-to-image at denoise 0.65 over the figure's head and shoulders, enlarged to 1024, so it is
   the same person; Krea draws it on an 8 px grid (127 texels square).
3. **Parts.** rig-c/parts ("parts rig", as in Rusty Dagger's `authoring/docs/people-animation.md`): BiRefNet matte,
   RTMW keypoints, See-through layers for the body. Pixel faces are cut from the painting itself (See-through repaints
   faces softly and fails on some portraits). Mouth shapes and a closed-eye blink are Qwen-Image edits confined to the
   mouth or eyes, swapped in as part sets.
4. **Motion.** Door figures: an idle clip from Pika H3 FL2VA started and ended on the rig's own frame, read with
   RTMW and retargeted onto the rig. Portraits: breathing and sway, a blink, and the mouth sets switched in a speech
   rhythm.
5. **Pixels.** Busts: the Engine samples the pages nearest on the rig's texel grid (its texel snap), so a bust is
   drawn as whole texels. Krea's 1024 portrait grid is 8 px from the painting's edges, which is the rig's origin, so
   the bust's texels are the painting's. Door figures: a texel grid lives in each part's space and turns and scales
   with its bone, and in the town view a texel is smaller than a screen pixel, so snapped figures read as noise.
   They are drawn smooth instead and the Engine cuts the lit figure into blocks of whole screen pixels on one grid
   anchored at its feet, the same at every distance.

## In the game

Busts are rig-c builds copied into `content/game/towns/people/<name>/portrait/` with a ground slot added first: a
lamp-warm glow a little larger than the frame, so the window never shows the town behind. Each part's matte leaves
a ring of light pixels (the painting's grey ground); ring pixels much lighter than the part just inside take that
inside colour, or they would outline the bust on the dark ground. A keeper's `portrait` in
`people.json` names the model, its resting clip, the clip it plays while a new line is said, the texel (8 rig units)
and the frame: the square of the rig's plane the window shows, 127 texels from whole texels off its origin. The
dialogue leaves a clear window in its portrait frame and anchors it; the Engine draws the bust there, unlit, at the
largest whole number of screen pixels a texel (3 in the 381-pixel window).

Door figures are rig-c builds copied into `content/game/towns/people/<name>/door/`: `skeleton.model.json`
(`rigc-compiled/3`) with each page it names beside it under its own file name. A keeper's `figure` in `people.json`
names the model, the looping clip, metres per rig unit, the pixel grid (screen pixels a block: 2; 3 is too coarse for
a figure about 320 pixels tall in the inn's close view) and the pose rate (8 poses a second, as the baked figures
played, so fewer blocks change a frame); a kit piece's `keeper` spot is where they stand. The Engine draws them
upright, turned to the eye, sampled linearly with mipmaps and lit by the scene on a flat normal, then cuts them into
blocks, each the average of the figure under it, drawn where she covers half of it. A figure may also name a palette
file; `palette.py` designs one from the figure's pages: per material a ramp from near black to near white, each shade a
fixed ratio lighter, and with `--lit` the ramps again under lamp and moon light. Mara and Orrin use 6 materials of
12 shades, lit (`palette.py <door> palette.json 6 12 --lit`): it keeps some stepped light on the cloth with little
flicker. Ten materials of 8 shades was too noisy (the bright blocks flickered, lamp-lit faces at night went to pale
blocks) and 5 of 16 was close to no palette (docs/evidence/human-look/screen-pixel/palette).
