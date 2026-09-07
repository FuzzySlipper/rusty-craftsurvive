# GPU cave test — 7 September 2026

Implementation: CraftSurvive `718ca496556a1bbb75750c3709fd0005325af120`,
then platform exclusion `23be7207b13f1b929db41c371fd6204153d564e7`.
The existing local contributor controller pair was preserved: SDK
`0.1.0-dev.2574cc89fd30.gamepad1`, runtime `runtime-pack-2574cc89fd30-gamepad2`.
Controller changes were already uncommitted in Engine/CraftSurvive and are not
part of these cave commits. The cave uses the same existing implicit APIs.

Wolf on den-srv rendered/encoded the 1280×720 stream; the local MCP bridge
captured Moonlight's private window. The virtual controller was created before
Firefox. A loopback forwarder inside the disposable Firefox container exposed
CraftSurvive at `http://localhost:37300/`. No installed configuration changed.

## Results

| Normal detail | Triangles | Vertices | Route body witnesses | Boundary / non-manifold / winding edges |
|---|---:|---:|---:|---|
| Seed 11, dressed | 66,628 | 43,551 | 1,008 / 1,008 | 0 / 0 / 0 |
| Seed 29, dressed | 68,922 | 45,489 | 1,008 / 1,008 | 0 / 0 / 0 |
| Seed 47, dressed | 65,388 | 42,933 | 1,008 / 1,008 | 0 / 0 / 0 |
| Seed 47, bare | 38,442 | 22,402 | 1,008 / 1,008 | 0 / 0 / 0 |

Seed 47's bare/dressed studies retained the same plan identity. Seed 11 generation
was about 1.5–1.7 seconds in the observed runs; that is generation latency, not
frame pacing or a representative performance benchmark.

Native left-stick movement took the ordinary player from entrance z=-12 to
z=-4.841, then to z=8.972 near the hub, grounded. The hub capture visibly shows
branching passages. The complete loops were not walked. After the platform
change reloaded the product, a burst was correctly ignored while the new canvas
lacked focus. One Tab restored focus; a native one-second forward burst again
moved from z=-12 to -4.841, grounded. Failed focus attempts remain in the journal.

The inherited beige moving platform appeared in earlier room views. It is now
excluded from visibility, movement and character obstacles for `level` and
`level-layout`, with the older studies preserved. Source/build verification
covers this change. The requested postchange goal screenshot actually shows an
approach passage: it does **not** establish camera-aligned visual proof of removal.

## Evidence boundaries

`index.json` carries root assessments and portable image paths;
`controller-index.original.json` preserves the operator's submitted labels;
`controller-events.jsonl` preserves original input/capture/release receipts.
Preset labels identify requested camera/seed setup, not certified frame binding.
The stream has no game frame ID or measured freshness; some captures lagged the
requested camera. Do not infer the renderer backing size from screenshot size.

`final-readouts.json` preserves product/mesh/route and postreload movement facts.
`engine-diagnostics.json` uses the Engine warning-capture script's checkpoint
drain helper. It is Engine-only, report-only capture: five warnings during hot
reload, zero errors/drops, complete drain, and a subsequent ready transition.
The late renderer-observation binding warning is tracked under Engine **#7865**.
There is no compatible browser warning baseline or GPU Firefox console capture;
no clean warning-delta, audio or frame-pacing claim is made.

Root image inspection found coherent rounded rooms, branched passages and
readable stepped strata, with small dark notches/rough cut edges still visible.
A bounded independent image critic agreed, while finding the very even pale
bands closer to painted trim than varied geology. This is art feedback, not a
mandate or proof of watertightness.

Release succeeded with no errors. Local capture stopped; subsequent status had
no lease, sessions or lobbies, and the disposable Wolf UI/Firefox containers were
gone. The installed controller service remained active. No private Moonlight
logs were copied.

A second root-operated session corrected the comparison evidence. The original
seed-47 bare label still shows dressed surfaces and must not be used as a bare
comparison. [Settled seed-11 bare](seed11-settled-bare.png) and
[dressed](seed11-settled-dressed.png) visibly distinguish plain rock from carved
courses, pale bands and moss. Both retained the same plan hash; bare had 39,210
triangles and dressed 66,628. The dressed capture followed confirmed generation,
a goal camera request and a five-second settle. Neither image has exact frame
binding. No moving platform is visible in this comparison.

The second browser initially displayed DEV_HOST_REQUEST_TIMEOUT (request header
timeout); native Ctrl+R recovered. Its cause was not diagnosed, and this is not
a clean infrastructure-health claim. The timeout screenshot and second action
journal are retained. Release at 09:54:40Z succeeded without errors and stopped
local capture; status at 09:54:50Z had no lease, sessions or lobbies.
