# Owner-machine pacing investigation (#7784)

Use the current paired runtime declared in `CraftSurvive.Game.csproj` and the
Den launch manifests. Restart with `den-serve restart rusty-craftsurvive -repo
/home/dev/rusty-craftsurvive -public-host 192.168.1.22` after changing that pair.
The owner browser is at <http://192.168.1.22:37300/>. Refresh after a restart.

The reported problem is uneven overall motion, while voxel edits already feel
immediate and keep graphics/collision synchronized. Do not infer a GPU or C#
bottleneck from an instantaneous admission time. Engine #7785/#7786 provide
standard managed attachment, native symbols, and worker/callback attribution.

## First capture on the owner machine

Use one accelerated browser tab, the same window size and scene, and allow
startup work to settle. Capture about 12 seconds stationary, then about 12
seconds walking and turning through already resident terrain. Keep terrain
streaming and edits as separate observations. Record how each interval feels.

Open the existing live-debug panel and retain `engine.renderer.detail` for each
interval, including adapter, canvas/DPR, receipt/application intervals,
presentation CPU phases and GPU timing. Keep the page foregrounded. An agent's
software-rendered browser is functional evidence, not this hardware comparison.

On the Linux runtime machine, retain Engine diagnostics immediately before and
after each interval, outside watched product sources:

```bash
mkdir -p /tmp/craftsurvive-7784
curl -fsS -X POST -H 'Content-Type: application/json' -d '{}' \
  http://127.0.0.1:37300/__rusty/product/runtime/diagnostics/read \
  > /tmp/craftsurvive-7784/stationary-before.json
# Repeat after the interval as stationary-after.json, then movement-before/after.
```

Inspect `telemetry.updateAttribution` and `telemetry.workerUpdate`: callback
p50/p95/max, native service time nested inside the callback, post-callback time,
worker conversion/write, and shell decode/queue/publication. Keep runtime
instance/generation and observation age with the samples. The reported runtime
progress rate is callback completion frequency, not fixed simulation frequency.

If those elapsed phases identify a material gap, capture only the corresponding
managed/native profile. Engine contributors can use
`/home/dev/rusty-engine/scripts/find-coreclr-worker.py --project
/home/dev/rusty-craftsurvive/src/CraftSurvive.Game/CraftSurvive.Game.csproj` to
rediscover the actual CoreCLR worker before attachment. This is investigation
tooling; ordinary product launch needs no Engine checkout. Follow the Engine's
`docs/coreclr-diagnostics.md` and `docs/runtime-profiling.md` recipes for
EventPipe, counters and user-mode native CPU sampling. Retain the matching
runtime's `symbols/build-info.txt`, manifest, and product DLL/PDBs. Rediscover
PID and runtime identity after any restage or restart.

Only implement an optimization after the capture identifies its owning phase.
Compare tail gaps and movement under matching conditions, then confirm adding
and removing voxels remain immediate and collision stays synchronized.

## Chunk boundary capture (#7793)

The September 5 live CoreCLR capture identified repeated terrain generation in
`TerrainResidencyPolicy.PlanFor`. Every center change generated all 75 candidate
chunks, then admission generated its selected payloads again. Height and slope
were also recalculated for each voxel in a column.

The product now retains generated payloads only for the current candidate
window, reuses overlap on crossings, and passes those payloads to Engine
residency. Accepted edits regenerate affected candidates; unreported overlay
revision changes and restore invalidate the cache. Generation calculates height
and slope once per column. Chunk edge remains 16, requested/retained radii remain
1/2, and the 64 resident / 16 operations-per-update limits are unchanged.

With Engine `3c6be904f792`, the same twelve `craft.player.teleport` commands
crossing X=1/17/33 at Y=20, Z=8 measured median 240.549 -> 21.192 ms and maximum
375.598 -> 82.602 ms. Both runs used the normal Debug product worker with a
12-second EventPipe trace attached. These are end-to-end debug-command timings
through the real synchronization path, not ordinary update callback tails or
browser frame times. The profile identifies generation as substantially
reduced; native residency still contributes. Owner movement and edit acceptance
remain separate from this route. The original player position was restored.

Local trace, route, worker identity, and material-hash evidence is under ignored
`live-evidence/7793/`. Run `dotnet run --project tests/TerrainResidency -c Release`
for original material snapshots, fresh-generation comparisons, overlap reuse,
edit/restore invalidation, priority ordering, and candidate eviction. This pure
product regression is also exercised by the `verify-terrain` CI job.
