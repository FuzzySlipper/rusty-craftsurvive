# Human characters: style sheet

Owner-chosen direction (2026-10-15, Den #10027): direction 3 with storybook proportions.
Anything marked **Open** still waits on a test.

## The idea in one line

Simple, smoothly shaded forms whose surfaces are **painted**: folds, seams, hair, wrinkles
and faces drawn on with a few confident strokes, under the game's real light.

It is not anime, a toy cartoon, middle realism, or an emulation of old hardware.

## References and what each one is for

| Reference | Take | Don't take |
|---|---|---|
| `blend-lineup.png`, `blend-doorstep.png` | The overall target | The triangle facets on surfaces (a generation artefact) |
| Vagrant Story (Akihiko Yoshida) | Detail drawn into the texture, not modelled; quiet faces; grounded, slightly elongated European figures | PS1 limits: jitter, blur, painted-in hard shadows |
| Arcane (Fortiche) | Visible brushwork on every surface including skin; faces built from a few simplified planes | Its dramatic action posing and high-contrast neon palette |
| The Long Dark story mode | How characters join an environment-led painterly world: simplified forms, restrained expressiveness, a "living storybook"; roughly its degree of caricature | Anything sillier than that |

## Form

- **Low polygon count, smooth-shaded.** Angularity may show only in silhouettes (shoulders,
  elbows, hat brims, coat hems), never as visible triangles on surfaces.
- **Few, large modelled masses.** Body, coat, apron, hat, boots, pack. Folds, seams, buttons,
  straps and stitching are painted, unless they change the silhouette.
- **Proportions: storybook (owner, 2026-10-15; `storybook-lineup.png`).**
  - About 6 heads tall: the head is larger than real.
  - Big, capable hands and boots.
  - Simplified, rounded noses and small, calm eyes.
  - Serious and warm: not a children's cartoon, not cute.
  - Without strongly stated painting, generators slide back to near-photoreal.
  - Body types differ boldly and are exaggerated: very round and solid, very thin and
    stooped, long-legged.
- **Faces.** Simple planes for brow, cheekbones, nose and jaw, no sculpted wrinkles. Eyes,
  brows, mouth and age lines are painted. Expressions change by swapping face textures
  (neutral, blink, talk, smile), tested in #10014.

## Surface

- Brushwork you can see: broad colour patches, painted edges between colours, a few strokes
  per fold.
- **Painted shading stays soft.** Paint the form (fold shadows, the underside of a brim), not
  a light direction. The Engine's sun, lamps and fire must still read on top: dusk and
  firelight are where these characters are judged.
- No photographic fabric, leather grain or skin pores.
- **Texel density: Open.** Characters need more room to draw than props (about 32 texels/m).
  Expect about 64 on clothing and more on faces, which #10014 found sensible. The test is
  that strokes stay legible at 2 m.
- Palette from `content/style/palette.json`: warm ochre and plaster, sun-faded reds,
  softened teal and green, leather browns. Clothes are worn and faded, never saturated.

## Who they are

- An early-modern frontier: 17th to 18th-century clothing, common gunpowder, practical
  expedition gear. No knights, heavy armour or steampunk.
- Warm, tired, capable people. Hospitality reads in posture and face, not in costume clutter.
- Costume carries trade and region: an apron and keys, a waistcoat and ledger, a travel coat
  and pack. Port people dress lighter and brighter; mountain people heavier and duller.

## Variation the system must allow (framework test)

One shared skeleton and body family, with:
- body-shape morphs (build, height, age);
- a face library painted to one layout;
- skin tones;
- swappable hair and headgear;
- costume pieces per trade and region.

The lineup test is 6 to 8 townsfolk who read as one cast of different people.

## Not this

- Anime or chibi proportions.
- Cute, rounded children's cartoon.
- Photoreal skin or PBR fabric.
- Triangle-faceted "polygon art".
- PS1 emulation.
- Heavy painted-in shadows that fight real light.
- Accessory clutter (straps, pouches, buckles everywhere).
