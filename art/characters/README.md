# Character sources

The editable source of the character style test (Den #10014): one human traveller fitted onto
the generic humanoid rig of Quaternius's CC0 Universal Animation Library
(`content/animations/UAL1_Standard.glb`, user-owned and read only), with a texture-swap face.
Provenance is in [`content/characters.sources.json`](../../content/characters.sources.json). The
visual rule is [`docs/visual-direction.md`](../../docs/visual-direction.md).

## Why the rig is UAL's own

The Engine attaches a clip pack (`AnimationClipPackAssociationRequest`) only to a mesh whose rig
matches the pack exactly: the same joints, local rest matrices and inverse binds. A generated
character therefore cannot keep its own skeleton. Its mesh is moved onto UAL's rest pose and its
weights onto UAL's joints, so bone lengths come from UAL. A proportion variant changes girth, head
and hands; the product scales the whole instance for height.

## Pipeline

`traveller/build.sh <tripo rig-model.glb> <work dir> <python with xatlas>` rebuilds everything
under `content/game/characters/traveller` byte for byte:

1. `make_clip_pack.py` trims UAL1 to the clips the product plays, renamed to the Engine's
   lowercase clip ids. Each character gets its own pack copy until rusty-engine #10018 is fixed.
2. `face_set.py` cuts the painted expression sheet (`traveller/face/faces-01.png`) into
   neutral, blink, talk and smile faces at 32, 64 and 128 px.
3. `fit_to_ual.py` fits the Tripo-rigged model to UAL's rig. It poses Tripo's own rig to UAL's
   T-pose, warps joints onto UAL's, remaps weights and splits the face slot, a front projection
   anchored at the nose tip.
   - A variant file (`traveller/chunky.json`, `traveller/stylised.json`, `traveller/bold.json`)
     gives the head and hand scale and the face box.
   - Hair-coloured triangles beside the cheeks stay on the body texture (a hanging lock); the
     eyes, nostrils and lip line always take face paint, so the generated eyes and lips never
     double the painted ones.
4. `reuv.py` (needs `pip install xatlas` in a scratch environment) re-charts the body at
   32 texels/m and bakes the generated texture into it. It also writes a 64 texels/m texture on
   the same UVs, for comparison.
5. `assemble_character.py` writes the GLB:
   - UAL's nodes and inverse binds, as stored;
   - one primitive per slot (body, face), with nearest/nearest samplers;
   - a still `rest` clip. The Engine refuses a skinned GLB without clips.

`preview.py` renders Blender turnarounds for art review; `--with-clips` GLBs from step 5 carry the
clips for it. These previews are art studies, not in-game evidence.

## The MPFB cast (#10028)

`mpfb/` builds characters from MakeHuman's fixed base mesh (through MPFB2) instead of
per-character generated meshes. Every body shares one topology. Therefore it shares:
- one UV layout, so costume textures fit every body unchanged;
- one face layout;
- the base mesh's own skin weights.

| Script | Does |
| --- | --- |
| `mpfb/dump_body.py` | Evaluates a recipe (MPFB macros and targets) in Blender |
| `mpfb/fit.py` | Poses and warps the body onto UAL's rig or a frame, with girth scaling |
| `mpfb/face_layout.py` | Defines the face slot once |
| `mpfb/paint.py` | Lays procedural brushwork from body-region costume definitions |
| `mpfb/build_cast.py` | Assembles the cast in `mpfb/cast.json` into `content/game/characters/cast` |

`frame.py` derives skeleton frames (other leg lengths) from UAL, each with its own clips. The
verdicts are in Den #10028.
