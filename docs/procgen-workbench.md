# Offline procgen workbench

CraftSurvive is the active experiment and FPS testbed. Procgen remains donor
history. Den #7906 established the candidate-to-world loop; #7907 adds three
bounded motifs, explicit recovery/failure analysis, and a visual construction plan.

## Large branching complex (#7915)

The default sample is now `procgen/complex-29.json`: a seeded 36-room complex
on a 6×6 grid, with branching corridors, additional loops and varied rectangular
room bounds. `complex-83.json` gives a second topology. This increases layout and
voxel construction load while keeping one floor and one shared control state.
The small motifs remain selectable regression samples.

Loading places the physical player at the new entrance, even during model
inspection, so a changed layout cannot leave the old position inside a wall.
Load the complex, **Expand map**, and inspect the layout or graph. Follow the
model witness to find the control station, then physically approach its bronze
marker and use **E / Y** to open the gated routes. The goal is marked in red.
Model stepping remains separate from physical walking. Map inspection and witness
instructions are assistance, not a blind exploration test.

The readout reports construction time, triangles and vertices for the applied
Engine mesh/collision build. Loading and opening gates synchronously rebuild the
level; this slice does not claim streaming or frame-budgeted generation.
For the grid complex, separation probes test omitted connections between
neighboring rooms. Distant collinear rooms may intentionally share a hallway,
so absence of a direct graph edge is not interpreted as a wall between them.

Generate more examples with `generate-workbench --seed 101 --motif branching-complex`
and the same explicit `--out` / `--receipt` options below.

## Inspect and play

Open **Procgen workbench**, select a sample and **Load candidate**. The map shows
resolved room and passage bounds, gate slabs, preview openings and stations used
by the Engine mesh recipe. It is a plan diagram, not a view of extracted mesh
triangles or a collision guarantee. World and model overlays remain separate.
Inspect room bounds and the graph, then compare the plan with the first-person
world. Coordinate axes and distances use the candidate's X/Z world coordinates.

**Inspect witness** selects the completing abstract trace. **Inspect failure**
selects a reproducible model counterexample when a contract fails. **Step trace**
advances only that model: it never moves the player, spends a physical key or
opens physical gates. **Enter** / **Reset** reset both runs and begin physical play.
Press **E**, controller **Y**, or **Interact** near a station to perform its action.
The physical action uses the same rules plus proximity (and actual line of sight
for observation). Click the gameplay area to resume movement/look after using UI.

| Sample | Experiment | Physical sequence |
| --- | --- | --- |
| `workbench-11.json` | Return shortcut | Start → relay → control, activate, goal; return shortcut opens. |
| `recovery-11.json` | Irreversible key spending with recovery | Spend the key at relay, visit goal to recover it once, return to control to activate, then goal. |
| `preview-11.json` | Information before access | Acknowledge the red goal through the start lookout opening, walk relay → control and activate; both goal entrances then open. |

All paths are under `procgen/`. The three `*-failure-11.json` counterparts disable
the switch, remove recovery, or seal the preview opening. They remain valid
resolved artifacts so their failures can be inspected and walked. Their lack of
a completing witness is intentional. Failure traces replay legal actions leading
to the unsatisfied contract; a zero-action trace denotes failure at the initial state.

The lookout test certifies one unobstructed mesh sightline from the named point,
not human recognition, gaze direction or a full visibility volume. The aperture
is narrower/shorter than the standing player. Ray separation checks remain
separate from this information check; physical traversal is verified separately.

## Generate a candidate

From the repository root:

```bash
dotnet run --project src/CraftSurvive.Procgen.Tool -c Release -- \
  generate-workbench --seed 29 --motif spent-key-recovery \
  --out "$PWD/content/procgen/recovery-29.json" \
  --receipt "$PWD/content/procgen/recovery-29.receipt.json"
```

Motifs are `four-room-return-shortcut`, `spent-key-recovery`, `visible-before-access`, and `branching-complex`. Optional `--counterexample true` generates that motif's
intentional failure variant. Omitting `--motif` retains the shortcut default.
Wait for normal content staging, then load its `procgen/*.json` path. Game loads
only Engine-admitted product content; the Tool owns explicit filesystem output.

Schema `craftsurvive.workbench_candidate.v2` stores resolved bounds, routes and
explicit switch/recovery/preview options. v1 samples must be regenerated; this
experimental schema does not silently default missing fields or load generic
2D DungeonArtifacts. Receipt is offline provenance; candidate JSON is runtime
input. Seed gives shallow spacing/width variants, not a promise of diversity.

## Ownership and verification

- Pure `WorkbenchExperiment` owns the three fixed motifs and the bounded complex
  experiment (at most 36 rooms × 32 state combinations). It reports completing witnesses, reachable states unable to
  complete, separate contracts and reproducible counterexamples. This is not a
  general planner API. No generic mechanism was needed for Engine promotion.
- Pure `WorkbenchLayout` resolves the room/passage/gate/window volumes and
  stations once from versioned candidate meaning. `WorkbenchRecipe` uses these
  same volumes through Engine implicit boxes and booleans. The readout sends
  them to the DOM/SVG diagram without reconstructing them from a seed in UI.
- `ProcgenWorkbench` owns accepted identity, revision-checked commands, model
  replay and physical switch/resource/observation state. Room membership is a
  scenario trigger. Engine still owns input, collision, presentation and lifetime.
- DOM UI retains only display preferences and copied readouts, uses the packaged
  live-debug transport, and preserves map controls across background refreshes.

Actual mesh checks retain nine straight rays per route/separation and add the
Engine standing-capsule queries described below. Required connections must clear;
closed gates and selected forbidden room pairs must block. These are sampled
clearance checks, not exhaustive navigation or proof against every bypass. Switch changes
rebuild the small scene synchronously. A different courtyard study deactivates
the workbench until reloaded. Runtime history is bounded, not a durable replay bank.

Current installed Engine pair: `0.1.0-dev.2e4255bd3ad5`, downloaded as the published
matched archive and verified with its checksum and pair verifier. Game and
TerrainResidency pins, both host manifests and current setup instructions agree.
No Engine source checkout is needed by the product.

Focused checks: `tests/Workbench`, `tests/Procgen`, Tool `--self-check`, UI
typecheck, Game Release build and ordinary CoreCLR staging. GPU interaction uses
the crew-services Wolf profile. Evidence for #7906 and #7907 is retained separately
under `docs/evidence/procgen-workbench` and `docs/evidence/procgen-motifs-7907`.
The larger #7915 sample measurements and scoped native evidence are retained in
`docs/evidence/procgen-complex-7915`.

GPU acceptance: guided native keyboard map controls and controller completion of
both new motifs passed. An independent observer inspected the original images;
their pointer activation attempt was inconclusive. Map/readout assistance and
submitted-presentation metadata are recorded separately from visible evidence.
See the #7907 evidence README for exact scope and cleanup.

## Realization faults and scoped checks (#7908)

Load a candidate, then use **Introduce side bypass**. This cuts a one-unit-wide,
standing-height aperture at the side of one closed gate while preserving its
center. Candidate identity and graph requirements remain unchanged. **Repair
realization** restores the intact geometry; both actions close the gates and
return the player to the entrance. Tab switches between UI inspection and
gameplay input in the browser. **Check realization** reruns current Engine
queries without rebuilding. Model witness/failure inspection remains separate.

The red map aperture shows the treatment; red probe lines show failed measured
requirements. The graph continues to show intended connections. Opening all
gates removes the gate protection requirement, so the same passage must then
be clear. The treatment remains selected until repaired or another candidate is
loaded, including across Enter/reset.

`WorkbenchProbePlan` derives positions only from candidate intent and gate state,
never from the injected aperture. `WorkbenchSpatialChecks` uses Engine
`OverlapCapsule` and `CastCapsule` with the player's standing height 1.75 and
radius 0.30. It checks both directions on three passage lanes, five gate lanes,
and selected forbidden room pairs. Initial overlap or nonconverged casts are
unavailable, never a passing separation. These read-only queries do not move the
player or run a downstream collision/navigation implementation.

Five retained samples (all three small motifs plus both large seeds) were checked
intact, breached, repaired and open. Each breach produces four failures while
legacy rays still pass. Each repair restores the original collision projection
hash. The large seeds run 340 body checks each; UI readouts retain at most 64
rows, failures/unknowns first, and explicitly report omitted rows. Summary counts
cover the complete probe set, including rows not drawn in the map.

Provenance consists of the candidate identity, requirement-state identity
(candidate plus switch state), realization identity (recipe version, candidate,
treatment and switch state), and the Engine `ReplaceCollision` revision and
projection hash checked against the current static-mesh revision. Other physical
resource/observation flags remain in the readout, but do not change these geometric
requirements. Evidence and controller scope: [#7908 evidence](evidence/procgen-realization-7908/README.md).

The standing casts establish clearance only at the sampled positions. They do
not prove a controller path exists everywhere, nor cover jumping, climbing,
crouch-only paths, destruction, or movable-prop bypasses. Those require separate
scenarios. This is one reversible realization fault/repair experiment, not the
semantic candidate editor proposed in #7909.

## Tentative expansion

These are hypotheses to revise after the first working slice, not requirements
to build a framework before experimenting. Den is the durable task record.

| Task | Next experiment | What determines whether to expand |
| --- | --- | --- |
| #7907 | Three motifs, state contracts and map inspection | Completed baseline; retained as small regression samples |
| #7908 | Side-breach detection and restored realization | Scoped standing-capsule baseline; further bypass types need explicit scenarios |
| #7909 | Candidate comparison and small semantic repairs | Repeated failures worth expressing as design operations |
| #7910 | Small agent selection/editing/adversarial trial | Stable motifs, checks and tools, then human calibration |
| #7911 | Resolved level bank and scoped runtime variation | Measured artifact sizes and meaningful structural diversity |
| #7912 | Engine promotion checkpoint | Concrete reusable mechanisms demonstrated by consumers |
| #7915 | Seeded 36-room complex | Current scale experiment: topology variety, mesh costs and guided traversal |
| #7916 | Vertical routes and mixed chamber shapes | Select height/shape complexity after the large complex exposes practical limits |

The longer-term direction is propose → diagnose → repair → compare → publish.
Preserve progression, spatial and information requirements as distinct concepts.
Future motifs should include negative requirements, recoverability, and intended
visibility before access. Agent editors must not alter acceptance rules; blind
exploration and adversarial testing answer different questions. The bank should
curate experiences and structural families rather than count seeds. These
intentions are recorded in the tentative tasks without choosing a solver,
training a learned generator or promising a universal intermediate format.

## Donor consultation

- Corpus: `/home/dev/rusty-procgen`, retained HEAD
  `5287e849a6e44585498df2870112de2e59c11ade`; the existing pure migration records
  its earlier donor snapshot in `src/CraftSurvive.Procgen/README.md`.
- Inspected: `GenerationSession`, `RustyProcgenProduct`, `StructuredUiProjection`,
  `ui/main.js`, and the already-migrated Artifacts/Tool flow. Indexed donor lookup
  was unavailable; exact filesystem source was used.
- Adapted: accepted-session identity, revision-aware commands, separate model
  inspection, trace/readout controls, strict offline artifacts and atomic output.
- Deliberate deviations: no old Product/host import, historical fixture decoder,
  CA page parity or handwritten UI transport. This motif uses its own bounded
  state model; the donor's monotone reachability closure is not a general solver.

## Realtime pacing follow-up (#7917)

Engine pair `0.1.0-dev.a6ac601db5b8` introduced absolute worker deadlines instead of
adding a full tick sleep after callback/output work. The fixed physics steps
and input/publication authority remain unchanged. Native GPU-harness runs in
`complex-29` measured about 54 Hz product publication before and about 60 Hz
after during look; look receipt intervals improved from 18 ms median to 17 ms.
The after-look p95 was 18 ms, but combined walk/look still had 34 ms receipt
and 35 ms applied p95 tails. Host observations stayed near 60 Hz and managed
callback p95 was about 1.37 ms. These are bounded windows, not a claim that all
hitching is fixed. Preliminary post-restart courtyard readings were excluded.

Engine #7918 owns opt-in render-time camera presentation; #7919 owns remaining
delivery-gap diagnosis. Renderer ran around 59 Hz with accelerated classification;
GPU timer cost was unavailable in its completion-only mode. Native stream was
30 Hz, so still captures do not certify motion smoothness. Evidence:
`/home/agent/.codex/pacing-7917/`. Engine warning capture was complete with zero
warnings/errors/drops; remote browser console remained unavailable.
