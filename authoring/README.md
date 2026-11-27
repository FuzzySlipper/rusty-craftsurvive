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
    golden-seed/  the golden seed (golden-seed.json) and its exporter (an Engine tool host running the product's generator)
    assets-sync.sh
  assets/       ignored, backed up to the den NAS; mirrors tools/ by area
    creatures/<species>/   concept sheets, reference views, species.json, prompts
    characters/            traveller concept and faces, cast face sheets, Quaternius cloth textures
    towns/source/          the inn concept
    textures/, terrain-studies/   the generated source images behind content/game/textures
    maplab-out/            MapLab's default output (regenerable; not backed up)
    golden-seed/           the golden-seed export (regenerable; not backed up)
    golden-seed-cache/     the exporter's tiles and cave views, by generator identity (regenerable; not backed up)
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

It listens on every interface at port 8790, so it opens from any machine on the LAN at `http://<this host>:8790`
(`--host` and `--port` change that; `den-serve up rusty-wandering-authoring -repo "$PWD/authoring/tools/cms"`
runs it as a den-serve service). It reads files in place and writes nothing in git:

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
dotnet run --project authoring/tools/golden-seed -c Release
```

The exporter is an Engine tool host (`RustyEngineToolHost`): it compiles the product's own generator, as the test lanes
do, and draws with the Engine's keyed draws, so its places, crossings and caves are the game's without running the game.
It writes `authoring/assets/golden-seed/`, replacing the old export once the new one is complete:

- `continent/`: the map raster at `pixels` a side (elevation, temperature, moisture and rock as float32; biome and
  water codes as bytes), row-major from the north-west corner.
- `map-rivers.json` (the 1 km map rivers), `drainage.json` (the 250 m walked drainage, with flow, navigability and
  channel form) and `places.json` (towns as the game builds them, lakes and representative sites).
- `tiles/`: every place (standing stones, ruins, cave mouths, dungeon entrances, vantage points), cave way and crossing
  of the continent, a 16 km region tile a file, and `tiles/index.json` with each tile's counts; `caves.json` lists
  every cave mouth that has a way underground.
- `windows/`: the walking ground's height and biome about each town.
- `manifest.json`: the world's identity (seed, size, generator version, fingerprint), the generator identity the tiles
  were made under, the commit, whether the tree was dirty, and the Engine pair.

A whole continent takes about an hour the first time, most of it deciding crossings. Each tile is cached under the
generator's identity (`authoring/assets/golden-seed-cache/<identity>/`: the map and terrain fingerprints, a build-time
hash of the product sources the exporter compiles, and the Engine pair), so a rerun with the generator unchanged
reuses every tile and takes a minute or two. `--workers N` sets the threads deciding crossings; `--limit N` builds at
most N new tiles. `--carry-from IDENTITY` copies the tiles of an earlier identity of the same generator version that
stand well above the sea (a change reaching only ground near the water leaves them as they were), and makes every 25th
again to check; a generator version bump redraws every place, cave and crossing, so nothing carries across one. A tile the generator cannot make is recorded in the index with its error and the export goes on.
`dotnet run --project authoring/tools/golden-seed -c Release -- tile X Z` prints one tile's page.

The atlas names each element as the game does (`town:Kelpmouth`, `poi:-459:128`, `crossing:-459:128`,
`lake:-54000:-25000` at a lake's deepest node, `reach:-142750:27500` at the node a walked reach starts from) and
offers a full reference to copy, with the export's date and fingerprint. It warns when commits since the export touch
the product, its content or the Engine pair, or when `golden-seed.json` has changed.

### Caves

The CMS's Caves page lists every cave mouth with a way underground; a cave's page (or Open the cave view from a cave
mouth on the atlas) generates its cave as the walking session admits it - the same seed, setting and preparation
(`Dungeons/UnderCaves.cs`), the way down from the tendril included - through the exporter
(`... -- cave poi:CX:CZ`, cached beside the tiles), and simplifies it:

- a plan and two sections of its open space, coloured by whether a body fits through from where the way down joins
  the cave (standing or crawling, by the karst generator's own radii, measured to the rock's surface), lies beyond a
  pinch narrower than a crawl, or is cut off by rock;
- the cave in three dimensions (three.js, orbit with the mouse): each metre of open space beside rock as a block in its
  reach colour, the route, the way in and the features over it, with layer toggles and a cut-away and a slice to see
  inside;
- the main route's profile, its climbs against a full stamina bar;
- its chambers, sinks, spring, water, ruin and the generator's own dead ends;
- what stands out: cut-off spaces, a bottom out of reach, climbs too tall for one bar, a mostly vertical route.

The first view of a cave takes about ten seconds; the exporter must be built (`dotnet build authoring/tools/golden-seed -c Release`).

When a cave is mostly out of reach, `... -- pinch poi:CX:CZ` says which stage of the generator closed it: the same reach
fill on the cave's field as the passages were checked, with its fine forms, and with its ruin, against the generator's
own clearance along the main route, and what of the ruin closed the cave's air near the route. With `--summary` it
prints one line per cave (each stage's reached share of the room a body fits in, and of the route), for sampling many. The
exporter's `check` (run by CI) generates three of the golden seed's caves the generator once left mostly out of reach
(a ruin across the main route; passages one sample wide read as a crawl) and requires each reached through most of its
room and along its main route.
