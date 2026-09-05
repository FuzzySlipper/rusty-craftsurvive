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
