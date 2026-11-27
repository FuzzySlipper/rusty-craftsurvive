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
    cms/          the content CMS: the catalog, provenance, review notes and the golden-seed atlas
    golden-seed/  the golden seed (golden-seed.json) and its export from the running game (export.py)
    assets-sync.sh
  assets/       ignored, backed up to the den NAS; mirrors tools/ by area
    creatures/<species>/   concept sheets, reference views, species.json, prompts
    characters/            traveller concept and faces, cast face sheets, Quaternius cloth textures
    towns/source/          the inn concept
    textures/, terrain-studies/   the generated source images behind content/game/textures
    maplab-out/            MapLab's default output (regenerable; not backed up)
    golden-seed/           the golden-seed export (regenerable; not backed up)
    cms/                   the CMS's marks, ratings and notes (reviews.sqlite3; backed up)
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

## The CMS

A local web app over this repository's content. From the repository root:

```bash
python3 authoring/tools/cms/server.py
```

It serves `http://127.0.0.1:8790` (`--host` and `--port` change that; `den-serve up rusty-wandering-authoring -repo
"$PWD/authoring/tools/cms"` serves it on the LAN). It reads files in place and writes nothing in git:

- **Catalog**: every file under `content/game/` sorted into records by what it is: creatures (joined to their wildlife
  kind and their working files under `authoring/assets/creatures/`), flora species, town layouts, kit pieces, palettes
  and people, characters, models, textures, audio and data. A record shows its images, its model in a 3D view (GLB
  or prop mesh), its sound, its game data and the provenance entries that name its files.
- **Provenance**: what `content/*.sources.json` records, the paths it names that are gone, and the generated or made
  files no entry names.
- **Marks, ratings and notes** on any record or atlas element, kept in `authoring/assets/cms/reviews.sqlite3`. A
  rating goes stale when the record's files change.
- **Golden-seed atlas**: the export below, in layers.

The web app is built into `authoring/tools/cms/web/dist/`, which is tracked, so the CMS runs without a build. After
changing `web/src/`, rebuild it (`pnpm --dir authoring/tools/cms/web install` once). See `authoring/tools/cms/README.md`.

## The golden seed

The world is generated per seed. The golden seed is one seed and size the authoring tools return to, so the
generator's output can be watched as it changes, and its parts named in a task by a stable current reference. It is
set in `authoring/tools/golden-seed/golden-seed.json`, with the walking-detail windows to export (one about each town,
plus any named there).

```bash
python3 authoring/tools/golden-seed/export.py
```

The export starts a headless `rusty dev` host of the current product (its own instance, `golden-seed`, beside any
other session; `--origin URL` uses a running `--live-debug` host instead). It has the host generate the golden world
afresh, then reads every layer through the product's `craft.export.*` debug commands, and stops the host. It takes
a few minutes, most of it building the walking detail about each town. The output replaces
`authoring/assets/golden-seed/` once complete:

- `continent/`: the map raster at `pixels` a side (elevation, temperature, moisture and rock as float32; biome and
  water codes as bytes), row-major from the north-west corner.
- `map-rivers.json` (the 1 km map rivers), `drainage.json` (the 250 m walked drainage, with flow, navigability and
  channel form) and `places.json` (towns as the game builds them, lakes and representative sites).
- `windows/`: about each town, the walking ground's height and biome, every place (standing stones, ruins, cave
  mouths with their ways underground, dungeon entrances and vantage points), every crossing and the shaped walked
  rivers.
- `manifest.json`: the world's identity (seed, size, generator version, fingerprint), the commit, whether the tree
  was dirty, and the Engine pair.

Everything comes from the running game, not from a copy of the generator: places, crossings and caves are decided by
the Engine's keyed draws, which only a host has. The export uses the host's dev save (dev saves are disposable).
The atlas names each element as the game does (`town:Kelpmouth`, `poi:-459:128`, `crossing:…`, `lake:3`,
`reach:812`) and offers a full reference to copy, with the export's date and fingerprint. It warns when commits since
the export touch the product, its content or the Engine pair, or when `golden-seed.json` has changed.

