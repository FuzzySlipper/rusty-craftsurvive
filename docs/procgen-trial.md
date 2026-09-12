# Selection and repair trial (#7910)

This is the first bounded experiment using the workbench's retained artifacts,
state contracts and canonical repairs. It lives in CraftSurvive's offline C#
Artifacts/Tool lane. Engine still owns rendered worlds, input and spatial queries.
The experiment does not add another generator, runtime or acceptance evaluator.

The frozen protocol and raw evidence are in
[evidence/procgen-trial-7910](evidence/procgen-trial-7910/protocol.md).
The [blind participant sheet](evidence/procgen-trial-7910/participant.md) deliberately
omits candidate assignments. Read reports and the assignment key only after rating.

## Reproduce the offline experiment

Use fresh explicit output paths so the retained trial is not overwritten:

```bash
dotnet run --project src/CraftSurvive.Procgen.Tool -c Release -- trial-bank \
  --out /absolute/trial/bank.json --baseline /absolute/trial/baseline.json

dotnet run --project src/CraftSurvive.Procgen.Tool -c Release -- trial-inspect \
  --bank /absolute/trial/bank.json

dotnet run --project src/CraftSurvive.Procgen.Tool -c Release -- trial-evaluate \
  --bank /absolute/trial/bank.json --submission /absolute/trial/baseline.json \
  --out /absolute/trial/result.json --receipt /absolute/trial/submission-copy.json
```

`trial-bank` retains twelve resolved candidates and a deterministic baseline in
an atomic pair. The same bank identity binds all submissions. `trial-inspect`
provides existing state-contract analysis; it is omniscient assistance and must
not be passed to the solution-blind observer. Submissions select exactly one
candidate per motif. Selection cannot edit; editing can request at most one
existing applicable repair per choice. Unknown fields, changed bank/candidate
identities, duplicate motifs and forbidden edits reject. Results and canonical
submission copies are committed atomically without replacing input files.
Technical rejection returns exit code 3 while retaining executable counterexamples.

The main editing arm may choose already valid candidates and make no edits.
A separately labelled failed-parent challenge exercises repairs without biasing
that comparison. Human ratings are collected separately from C# acceptance.

## Scope

The bank has two room-size/spacing configurations and three mechanics, all on
four rooms and one cycle. Exact resolved identities are not a claim of distinct
play experiences. Structure metrics deliberately ignore seed and repairable
flags, and report room/passage signatures separately from route-condition patterns.
No large-bank or model-quality conclusion follows from one agent per arm.

Cost is retained as canonical repair fields and observed decision wall time,
with its measurement limits. Token billing and dollar cost are unavailable.
The first human ratings and their missing values are retained in the evidence
README and `human-ratings.json`. One of five scored outputs meets the predeclared
enjoyment threshold; B06 is unscored and was reported as feeling identical to B01.
A passing graph or mesh is not an enjoyment rating.
