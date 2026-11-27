# Authoring CMS

How to run it, and what it shows, is in [authoring/README.md](../../README.md). This file is for changing it.

- `server.py` composes the workspace and the route table (`router.py`); `library.py` holds every root the CMS reads
  and the browser mounts that serve them (`/media/content/`, `/media/authoring/`, `/media/golden/`).
- `catalog.py` sorts `content/game/` into records, one builder per category, and indexes the provenance files. A
  record id is `<category>-<key>`; keep keys stable, since marks and notes are stored by id.
- `reviews.py` keeps marks, ratings and notes (category and id); `golden.py` serves the golden-seed export: its layers,
  its region tiles (gzip pages, sent as they are) and cave views, which it asks of the exporter
  (`authoring/tools/golden-seed`, `cave poi:CX:CZ`), one at a time.
- `cache.py` caches parsed files by mtime and size; the catalog is rebuilt when anything under `content/` or
  `authoring/assets/creatures/` changes.

The web app is Preact and TypeScript under `web/src/`, bundled by esbuild into `web/dist/` (tracked):

```bash
pnpm --dir authoring/tools/cms/web install
pnpm --dir authoring/tools/cms/web run check
pnpm --dir authoring/tools/cms/web run build
python3 -m unittest discover -s authoring/tools/cms
```

A page is a component under `web/src/pages/` with a hash route in `web/src/router.ts` and an entry in
`web/src/App.tsx`. The atlas draws the export on a 2D canvas (`web/src/atlas/`), loading the region tiles about the view once it is close
enough to show places; the cave pages are `web/src/pages/CavesPage.tsx`; the 3D view loads three.js as its
own chunk (`web/src/viewer/`). To test against scratch data, pass `--repo`, `--reviews` and `--golden`.
