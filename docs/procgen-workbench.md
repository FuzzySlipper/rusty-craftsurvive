# Offline procgen workbench

CraftSurvive is the active experiment and FPS testbed. Procgen remains donor
history. Den #7906 established the candidate-to-world loop; #7907 adds three
bounded motifs, explicit recovery/failure analysis, and a visual construction plan.

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

Motifs are `four-room-return-shortcut`, `spent-key-recovery`, and
`visible-before-access`. Optional `--counterexample true` generates that motif's
intentional failure variant. Omitting `--motif` retains the shortcut default.
Wait for normal content staging, then load its `procgen/*.json` path. Game loads
only Engine-admitted product content; the Tool owns explicit filesystem output.

Schema `craftsurvive.workbench_candidate.v2` stores resolved bounds, routes and
explicit switch/recovery/preview options. v1 samples must be regenerated; this
experimental schema does not silently default missing fields or load generic
2D DungeonArtifacts. Receipt is offline provenance; candidate JSON is runtime
input. Seed gives shallow spacing/width variants, not a promise of diversity.

## Ownership and verification

- Pure `WorkbenchExperiment` owns the three fixed motifs and bounded 128-state
  enumeration. It reports completing witnesses, reachable states unable to
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

Actual mesh checks cast nine straight rays per route/separation, at three heights
and lateral offsets. Gate rays must hit while closed and routes must clear while
open. Nonadjacent diagonals must hit. These do not exhaustively certify capsule
clearance, jumping, climbing, terrain destruction or all bypasses. Switch changes
rebuild the small scene synchronously. A different courtyard study deactivates
the workbench until reloaded. Runtime history is bounded, not a durable replay bank.

Current installed Engine pair: `0.1.0-dev.538724836d65`, downloaded as the published
matched archive and verified with its checksum and pair verifier. Game and
TerrainResidency pins, both host manifests and current setup instructions agree.
No Engine source checkout is needed by the product.

Focused checks: `tests/Workbench`, `tests/Procgen`, Tool `--self-check`, UI
typecheck, Game Release build and ordinary CoreCLR staging. GPU interaction uses
the crew-services Wolf profile. Evidence for #7906 and #7907 is retained separately
under `docs/evidence/procgen-workbench` and `docs/evidence/procgen-motifs-7907`.

GPU acceptance: guided native keyboard map controls and controller completion of
both new motifs passed. An independent observer inspected the original images;
their pointer activation attempt was inconclusive. Map/readout assistance and
submitted-presentation metadata are recorded separately from visible evidence.
See the #7907 evidence README for exact scope and cleanup.

## Tentative expansion

These are hypotheses to revise after the first working slice, not requirements
to build a framework before experimenting. Den is the durable task record.

| Task | Next experiment | What determines whether to expand |
| --- | --- | --- |
| #7907 | Three motifs, state contracts and map inspection | Current slice; use its actual failures to select the next experiment |
| #7908 | Protected passages and separations after realization | Which mesh/controller mismatches sampled rays miss |
| #7909 | Candidate comparison and small semantic repairs | Repeated failures worth expressing as design operations |
| #7910 | Small agent selection/editing/adversarial trial | Stable motifs, checks and tools, then human calibration |
| #7911 | Resolved level bank and scoped runtime variation | Measured artifact sizes and meaningful structural diversity |
| #7912 | Engine promotion checkpoint | Concrete reusable mechanisms demonstrated by consumers |

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
