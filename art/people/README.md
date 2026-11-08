# People

The town's people are detailed pixel art in the manner of 1990s PC role-playing games (atlas Towns page, "How
people are shown"). Each person is generated once and rigged as 2D parts; the rig plays a few loops, which are
baked to images the game shows. Den #10027 holds the decisions and the evidence.

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
5. **Pixels.** Rendered on the painting's grid at 8 frames a second, then snapped back onto its texel grid with one
   palette, holding a texel while its colour barely changes so the face does not shimmer.

## In the game

Portraits are `content/game/towns/portraits/<name>-rest.png` and `<name>-talk.png`: animated PNGs, one texel per
pixel, 4 s loops at 8 fps. `people.json` names them; the dialogue shows the talking loop while a new line is said
and the resting loop otherwise, scaled by whole screen pixels per texel.
