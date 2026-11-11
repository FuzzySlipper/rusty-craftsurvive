# Authoring

Everything that makes the game's content and is not itself loaded by the game. The product stages only
`content/game/`; nothing here is staged. Generic image, mesh and texture tooling stays in the asset-pipeline
checkout (`../asset-pipeline`); what is specific to CraftSurvive is here. Each tool's usage is in its own header.

```
authoring/
  tools/        tracked
    generators/   procedural content: textures, sky, flora, boats, sounds, decals, the terrain atlas
    characters/   the 3D character studies (MakeHuman cast, Quaternius test, traveller)
    creatures/    the wildlife pipeline: species specs to rigged, animated GLBs and ragdolls
    people/       the town's people (2D parts rigs) and their palettes
    towns/        the town kit: Blender recipes to the kit GLBs
    MapLab/       world maps offline from a seed, size and recipe (dotnet run --project authoring/tools/MapLab)
    assets-sync.sh
  assets/       ignored, backed up to the den NAS; mirrors tools/ by area
    creatures/<species>/   concept sheets, reference views, species.json, prompts
    characters/            traveller concept and faces, cast face sheets, Quaternius cloth textures
    towns/source/          the inn concept
    textures/, terrain-studies/   the generated source images behind content/game/textures
    maplab-out/            MapLab's default output (regenerable; not backed up)
```

## Assets

- Binary files under `authoring/assets/` are not in git. Their text (prompts, specs, provenance, READMEs: `*.txt`,
  `*.json`, `*.md`) is, beside them.
- After adding or changing assets, back them up: `authoring/tools/assets-sync.sh push` (adds and updates on
  `/mnt/den-nas-backup/data/rusty-craftsurvive/assets`, deletes nothing).
- A fresh checkout fills them with `authoring/tools/assets-sync.sh pull`.
- A finished file the game loads goes to `content/game/`, which stays in git, with its provenance in the matching
  `content/*.sources.json`.

## Running tools

- Run tools from the repository root; they find the root from their own place, and take their inputs and outputs as
  arguments.
- Blender tools: `blender -b --factory-startup --python-exit-code 1 --python authoring/tools/<area>/<tool>.py -- ...`.
- Python tools that read downloaded or generated files run with `python3 -I`.
- Older Den tasks name tools by their earlier homes: `art/<area>/` is now `authoring/tools/<area>/` (its images in
  `authoring/assets/<area>/`), `scripts/generate-*`, `make-*`, `stylise-*` and `prepare-*` are in
  `authoring/tools/generators/`, and `tools/MapLab` is `authoring/tools/MapLab`.
