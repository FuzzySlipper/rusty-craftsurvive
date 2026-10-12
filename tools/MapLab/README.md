# MapLab

Generates a world map with the product's own generator, without the Engine host, and writes what it
looks like and whether its ranges will read on the first-person horizon (#9814).

```bash
dotnet run --project tools/MapLab -c Release -- --seed 4242 --size 390000 --out /tmp/maplab --view 0,0
```

- `--seed`, `--size`: the world, as a new game would make it (size up to 65,536 m, or 320,000–450,000 m
  for a continent).
- `--recipe FILE.json`: generation tuning (`MapRecipe`). A file names only the fields it changes:

  ```json
  { "Relief": { "BeltUplift": 3, "CentreCalm": 0.3 }, "Simulation": { "PeakElevation": 2200 } }
  ```

  `--dump-recipe FILE.json` writes every field with its default. The defaults make the world the
  seed has always made; the game itself uses only the defaults.
- `--design FILE.json`, `--design builtin:NAME` or `--design none`: a continent design (#9815) the
  generator follows. By default, the one the game uses for that size: `builtin:frontier-peninsula` for a
  continent, none for a regional world (`src/CraftSurvive.Game/Modules/WorldGen/Designs/`). It draws the
  land as outlines whose coast noise resolves within a band, forced land and sea zones, the neck where
  the peninsula may meet the mainland, range belts with crest heights (calibrated by re-running
  erosion), passes, areas, reserved sites, and the climate's cold side and wind. An area with a
  `Height` (metres) and `Ruggedness` (0 a plateau, 1 broken peaks) is calibrated like a belt, e.g.
  `{ "Name": "NW massif", "At": [-0.55, -0.35], "Radius": 0.22, "Height": 5000, "Ruggedness": 0.85 }`;
  one with only a `Lift` raises or lowers the plain. Belts take a `Ruggedness` too (default 0.5).
  Belts and height areas are calibrated by their higher ground (90th percentile), passes to the
  saddle their depth asks for. The lowland's scale is measured outside every belt
  and area, so editing one region does not rescale the rest.
- `--erosion-filter`: read every height through the horizon's erosion filter (#9822), as the horizon's
  middle tier draws it. `--bench-filter` times a map sample, a river query, the filter and its crest.
- `--view X,Z`: a viewpoint in world metres for a skyline (repeatable; default `0,0`, where a world
  starts). `--reach-km`: how far a skyline looks (default 150). `--pixels`: map image size.

Output, in `--out` (default `maplab-out/`):

- `relief.png`: biome colours with rock, sea and rivers under a north-west hillshade; viewpoints in red.
- `height.png`: elevation in grey, sea to the highest point.
- `skyline-N.png`: the land's elevation angle by bearing from viewpoint N, 4 pixels a degree across and
  40 up. Red ticks mark north, north-east and so on from the left; faint lines mark whole degrees; the
  land is dark where its ridge is near and pale where it is far. At the default 70° field of view on a
  720-pixel screen, one degree is about 10 pixels.
- `design.png`: with a design, the relief with its outlines (forbidden land red), belts, passes and sites.
- `stats.txt`: land share, elevation percentiles, the highest ground within 10, 30 and 60 km of each
  viewpoint, the skyline's height by compass sector, the ten most prominent peaks and their key saddles,
  local relief within 5 km and the share of gentle land. With a design: the topology (does the
  peninsula meet the mainland, only through the neck; islands), each belt's crest against the height
  asked, each pass's saddle and each site's ground.
- `recipe.json`: the recipe the run used, in full.

## River census (#9874)

```bash
dotnet run --project tools/MapLab -c Release -- --rivers 1-24 --size 390000 --runoff 600 --out /tmp/rivers
```

Generates each seed (in parallel) and measures its rivers in real flow:
- The map's discharge is rain-weighted area, and rain is relative (about 1 over land). `--runoff` (mm a
  year per unit of rain, default 400) turns it into m³/s. That one figure is the world's water budget.
- `--canoe`, `--boat` and `--great` are the least flows (m³/s) a canoe, a laden river boat and the start
  port's great river use (defaults 5, 40, 150).
- The port river is the largest river mouth on the playable peninsula, out of the neck, in calm climate.
  Its shelter is the share of bearings from the mouth that reach open sea 60 km out.
- For the port river's own basin it reports how far a canoe and a boat can go (network length and the
  head of navigation, along the river) and the junctions of two canoe rivers on it (river-town sites).

Output: `census.txt` (a row per seed and a summary) and `rivers-SEED.png`:
- land by height, with the forbidden side dimmed;
- boat rivers bright blue, canoe rivers pale blue, smaller streams grey;
- port candidates ringed (yellow sheltered, red open);
- the largest calm mouth ringed white, and the sheltered port green;
- the neck outlined.
