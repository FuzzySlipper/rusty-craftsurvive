# Human character style references

The owner chose these on 2026-10-15 (Den #10027) as the target look for human characters:
**direction 3**, painted surfaces on simple, smooth-shaded forms, with **storybook proportions**.

| Image | What it shows |
| --- | --- |
| `blend-lineup.png` | Direction 3: the innkeeper, provisioner and fellow explorer |
| `blend-doorstep.png` | Direction 3: the innkeeper greeting from her doorstep at dusk |
| `storybook-lineup.png` | The chosen proportions: about 6 heads tall, big hands and boots, bold body types |

- [`style-sheet.md`](style-sheet.md) has the rules.
- [`prompts.json`](prompts.json) has:
  - the exact GPT image prompts for these images;
  - the lessons from rejected passes;
  - the Qwen-Image prompts that repainted the in-game test characters (Den #10029): bodies, faces, expressions and cloth tiles.

These are generated concept images, not Engine captures. The proportions and painting are the
target. The individual costumes, faces and props are illustrative.

The in-game test characters built toward this look are in `content/game/characters/quaternius/`
(sources in `content/characters-quaternius.sources.json`; scripts in
`art/characters/quaternius/`). They keep the Quaternius pack's own proportions. The storybook
proportions are not yet applied in 3D.
