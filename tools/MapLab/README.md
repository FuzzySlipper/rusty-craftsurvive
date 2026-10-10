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
- `--view X,Z`: a viewpoint in world metres for a skyline (repeatable; default `0,0`, where a world
  starts). `--reach-km`: how far a skyline looks (default 150). `--pixels`: map image size.

Output, in `--out` (default `maplab-out/`):

- `relief.png`: biome colours with rock, sea and rivers under a north-west hillshade; viewpoints in red.
- `height.png`: elevation in grey, sea to the highest point.
- `skyline-N.png`: the land's elevation angle by bearing from viewpoint N, 4 pixels a degree across and
  40 up. Red ticks mark north, north-east and so on from the left; faint lines mark whole degrees; the
  land is dark where its ridge is near and pale where it is far. At the default 70° field of view on a
  720-pixel screen, one degree is about 10 pixels.
- `stats.txt`: land share, elevation percentiles, the highest ground within 10, 30 and 60 km of each
  viewpoint, and the skyline's height by compass sector.
- `recipe.json`: the recipe the run used, in full.
