# Bundled content and packaging evidence

Engine #8186 implemented `ProductContent` named/optional reads, UTF-8 text,
logical path/name metadata and deterministic immediate/recursive directory
enumeration. CraftSurvive's procgen bank now consumes that directory API and
the authored `content/procgen/_index.json`; microvoxel reads use named lookup.

Exact installed Engine pair: `0.1.0-dev.041d70cc9255`, source
`041d70cc9255b504617712d525530bd23992f53e`. The immutable pair was built with
`scripts/build-csharp-release-pair.sh` and independently verified after extraction
into `.runtime/pair-041d70cc9255`. Package, runtime and normal launch configuration
were updated together. The broker-owned service remains on port 37300.

## Checks

- Engine generated managed content assertions passed, including authored IDs,
  ordinal Unicode ordering, directory prefix boundaries, explicit recursion,
  required/optional and empty reads, and UTF-8 BOM text.
- Engine GitHub `csharp` and `docs` checks passed on the source revision.
- CraftSurvive `pnpm run check:ui` and installed-package Release build passed;
  the latter reported zero warnings and errors.
- `VerifyRustyEngineAot` passed with a separate staging root
  `.runtime/product-content-8186/Product`, loopback port 37310, and Release
  configuration. It retained both generated CoreCLR and NativeAOT artifacts.
- A tar.gz transport sample was extracted to
  `.runtime/product-content-8186/relocated/Product`. All 92 file paths and SHA256
  values matched. `relocation.json` retains the complete inventory and local
  measurements: 78,045,128 payload bytes, 37,457,824 compressed bytes, 4,878 ms
  packing and 151 ms extraction. These are one-run measurements.
- Each relocated loader was launched from `/tmp` with the packaged
  `rusty-product-host`, explicit absolute isolated persistence/content-store
  roots and the same Product manifest. The NativeAOT host served `/`; CoreCLR
  served `/product-ui/main.js`. Temporary hosts were stopped afterward.
- The ordinary `rusty-live-debug --command craft.procgen.bank` returned identical
  JSON from the normal CoreCLR host and both relocated loaders. The three
  `bank-*.txt` files share SHA256
  `850e5ae85554775e92bd8e3ce931d2ec4ea0dd2ba9950784c44e5a10205e7b21`.
  The index selects complex-29, complex-83, workbench-11 and workbench-failure-11
  first; additional artifacts follow automatically.

The failed `nativeaot-exercise.txt` is excluded proof. Engine's generic
`--exercise` calls `complete_voxel_baseline`, which expects a fixture-specific
retained voxel frame. This implicit-surface product does not meet that fixture
assumption. The real NativeAOT host and named product command passed instead;
no fixture gate was weakened.

## Browser observation and warning limits

Independent crew-services Wolf session `018b3133-82f0-49ab-a20b-044f0d33281c`
rendered the courtyard and readable workbench controls. The dropdown visibly
showed the authored ordering; selecting complex-83 changed the visible path to
`procgen/complex-83.json`. No new scene was loaded. Slot 1 was released without
errors. Original images remain at:

- [Initial scene](/home/dev/dsh-crew/experiments/wolf-den-srv/controller/state/018b3133-82f0-49ab-a20b-044f0d33281c/1896b606-f58f-4917-9078-956fcd89679f.png)
- [Candidate dropdown](/home/dev/dsh-crew/experiments/wolf-den-srv/controller/state/018b3133-82f0-49ab-a20b-044f0d33281c/ec41677e-4189-441e-8a6a-4f536b49b1f3.png)
- [Selected candidate](/home/dev/dsh-crew/experiments/wolf-den-srv/controller/state/018b3133-82f0-49ab-a20b-044f0d33281c/2e0f4755-1cfb-46ba-ae6e-087ea48d372e.png)

The separate Engine `capture-playtest-warning-delta.mjs` report completed both
browser and Engine captures with no dropped/lagged records. It recorded one
headless Chromium ReadPixels GPU-stall warning. No compatible baseline was
supplied, so comparison is unavailable and no clean warning-delta claim is made.
This is not a motion/performance, GPU-frame-freshness or Tauri acceptance test.

## Packaging decisions

Engine Den document `packaged-tauri-host-investigation` (#7705) retains the host
design and gaps; #8251 owns the future Linux desktop implementation. Runtime
window metadata and OS-installed branding are distinct concerns; a precompiled
launcher should not force products to own Rust/Tauri workspaces.

Den document `rustypack-container-investigation` (#7706) recommends retaining
the loose directory and ordinary archive transport. No sealed container, VFS,
streaming loader or crash-safe archive update was introduced. Both investigations
use this real bundle evidence and preserve the ProductContent logical contract.
