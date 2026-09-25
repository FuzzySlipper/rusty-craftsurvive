# Rope playground validation

Engine pair: `b9c281937b26857a5b8e2cf5d63b907ff6a7c463`, SDK/runtime
`0.1.0-dev.b9c281937b26`, published together in the Engine GitHub release.
Task 6996 uses ordinary packaged CoreCLR. Product Release build (zero warnings
or errors), UI typecheck and TerrainResidency passed.

## Control and mechanism observations

The isolated crew-services browser smoke passed: 8.975 m maximum became 4.041 m
after 20 seconds of reeling, then 5.225 m after lengthening, followed by detach. This
replaces the failed pre-optimization collision-bound smoke, which had shortened
too slowly. This is a bounded normal-speed check, not a performance benchmark.

Native Wolf input lifted the player, created several observed arc positions,
and retained 15.881 m/s at release. The sampled tangent peak was 17.712 m/s.
The release crossed the authored courtyard edge; the later sky image is an
edge escape, not a successful landing. An explicit Reset to court control now
returns the player to spawn and releases attachment while keeping dynamic
objects alive. Setup resets/teleports and station selection are explicit
assistance; the movement/reel/release input is native keyboard delivery.

The collision-bound chain settles at Y=3.118 over the Y=3 floor (bead radius 0.12)
with sampled tension about 96 N. Earlier unbound terrain evidence is rejected.
The Engine now shares immutable terrain shapes instead of copying their voxel
compounds and deriving unused fixed-body inertia each Dynamics step.

## Feel and evidence limits

Movement pumps the swing strongly: short inputs can build large arcs and a
release can leave the court. Reeling is deliberately slow at 0.25 m/s. Letting
rope out causes repeated slack/taut transitions; the catch counter measures
those transitions, not independent swings. The debug panel is readable but
occupies a substantial part of the view. Controls/presentation remain a physics
prototype, not a tuned climbing progression.

The coding parent performed the native trials. The independent playtester was
unable to use the required crew-services CLI under its role restrictions, so
its attempt is infrastructure_error, not independent acceptance. Discrete
screenshots and readouts do not certify every intervening frame, GPU completion,
or absence of all jitter. No clean warning delta is claimed without a compatible
baseline; captured Chromium GPU ReadPixels warnings remain report-only.

The checked browser sequence is `tests/rope-playground-smoke.js`. Original
capture IDs, scripts and absolute artifact/journal paths are retained in
`index.json`; selected PNGs are unmodified copies of the original captures.

## Ownership check

C# owns selection, attachment/reel intent and orchestration. Engine owns Dynamics,
terrain collision, canonical character motion, tether correction and reactions.
DOM uses existing generated debug transport and copied diagnostics. There is no
product solver, copied Rapier integration, renderer, native binding or simulation
clock. Terrain edits/residency are adopted at the explicit update boundary.

The updated browser smoke also passed the Reset to court control: the final
readout was detached with maximum length zero. Its reel sample reached 4.000 m.

On the final collision-bound native dynamic trial, the maximum recorded reaction
was 83.576 N·s. Three release/reattach cycles completed without increasing the
prior 8.598 m/s peak speed; tangent speed peaked at 5.957 m/s. The captured
readouts reported neither saturation nor unresolved separation. The hanging
object and its line changed position while the paired objects and terrain-bound
chain remained visible. The first native startup capture had a blank game
canvas; subsequent original captures confirmed the scene was rendered.

The final native terrain sequence reached a wall with 25 blocked steps, then
returned to ground after lengthening/release (70 blocked steps total). The
post-trial player readout was grounded at (4.471, 3.890, 6.548), with no
saturation or unresolved separation. Its sampled peak speed was 23.810 m/s,
reinforcing the strong-input tuning limitation above.

The chain close view used an explicit teleport to (-5, 3.92, -3). A thin yellow
segment ends just above the floor; the separate diagnostic reports Y=3.118.
All owned sessions were stopped with successful cleanup receipts. The final
warning exercise completed browser and Engine capture without lag or drops:
four Chromium ReadPixels performance warnings, no Error/Fatal findings. The
driver diagnostic identifies GPU readback stalls; it did not prevent the control
exercise. Without a baseline it cannot establish a clean warning delta.

Selected unmodified originals: [browser reel](browser-reeled.png),
[dynamic reaction](native-reaction.png), [wall](native-wall.png),
[ground reacquisition](native-ground.png), [chain contact](native-chain.png).
The pool snapshots showed only the owned session occupied; the service still
marks performance isolation unproven. This is not a hardware benchmark.
