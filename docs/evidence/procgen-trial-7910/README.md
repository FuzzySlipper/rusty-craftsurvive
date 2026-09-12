# Small selection/repair trial (#7910)

Read [the participant sheet](participant.md) before trial results if providing
blind ratings. The assignment key and lane submissions unblind the sample labels.

## Retained experiment

- `protocol.md`: fixed instructions, acceptance and interpretation rules.
- `bank.json`: twelve resolved candidates bound by identity
  `b27472c6ed8c910e158c5d85a5f9325fd21defa335841f64f4e837bf8b3cbf0a`.
- `*.submission.json`: submitted selections; `*.receipt.json`: strict canonical
  evaluated copies; `*.result.json`: C# evaluation and canonical repair provenance.
- `technical-summary.json`: acceptance, structure and repair-field metrics.
- `timing.json`: parent-observed dispatch/completion times. These include harness
  overhead and observation delay, not precise inference duration or billed cost.
- `selection.notes.md`, `editing.notes.md`, `repair-challenge.notes.md`: actual
  reads/commands and assumptions reported by the independent agents. Both main
  arms inherited the parent default model; exact runtime/model and token billing
  telemetry were not exposed, so they are recorded as unknown.
- `calibration-key.json`: six deduplicated main-arm output identities assigned
  neutral B01–B06 labels; copies are staged under `content/procgen/trial-Bxx.json`.
- `human-ratings.json`: actual human feedback only; never agent-filled judgments.

The deterministic, selection-only and optional-editing arms each returned three
technically accepted candidates. Neither agent arm requested edits. That is an
allowed outcome: the mixed bank already contains passing counterparts for every
failure. The separate matched challenge repaired all three failing parents at one
field each; the negative control accepted none and retains executable traces.
These results do not show an editing advantage over selecting a working artifact.

All outputs retain the same four-room cycle. Route-condition signatures distinguish
two patterns; differences in seed or resource flags do not create new room/passage
structure. Human ratings are needed to decide whether dimension/mechanic changes
feel meaningfully different or enjoyable.

## Physical and observational layers

`physical-initial.json` records actual Engine checks for all six deduplicated
outputs. Four samples pass 38/38 standing-body probes; the two preview samples pass
48/48 and have clear before-access sightlines. These are scoped samples, not
exhaustive pathfinding, jump/crouch/destruction or human-attention guarantees.

The remote Wolf session is `b6c8f1b6-eb78-44c0-a8ea-fc5046365b5f`, slot-1. It uses
the managed localhost browser forwarding origin for LAN URL
`http://192.168.1.22:37300/`. This is not fresh LAN-origin startup evidence.

The solution-blind observer received only ordinary controls, visible scene and a
goal description. It did not receive candidate maps, witness traces, source or
runtime state. It moved through the level but did not observe completion within
the bounded exploration. Parent-only readback reached the control room with the
switch still closed (`blind-start.json`, `blind-end.json`). This is limited native
exploration, not a rejection of solvability and not a human enjoyment rating.

The subsequent informed fault probe is separate from blind exploration. At an
assisted, identical approach pose, a 1500 ms native forward input stopped at the
intact gate (z=6.185); with the existing injected side breach, the same input
crossed to z=15.278 while the switch remained closed. Scoped checks changed from
38/38 passing to 34/38. This confirms detection of a controlled shortcut fault,
not discovery of a normal-candidate defect or successful level completion.
`exploit-*.json`, `native-inputs.json` and `captures.json` retain the states,
inputs and original capture references. `cleanup.json` confirms the GPU lease
was released without errors. B01 was restored intact before human testing.

## First human calibration

`human-ratings.json` retains the participant's exact response and parsed scores.
Enjoyment/clarity respectively: B01 5/5, B02 3/4, B03 2/4, B04 1/1, B05 3/3.
B06 was initially reported as seeming bugged, then clarified as seeming exactly
the same as B01; it has no numerical score. B04 completion
is uncertain; B01, B02, B03 and B05 were reported completed. Help use was not supplied. The participant explicitly grouped B01 and B06 as
perceptually equivalent.

Under the predeclared enjoyment >=4 descriptive threshold, one of the five
scored unique outputs qualifies (B01). Selection and optional editing both
included this output, with zero repair fields; their repair-field cost per
observed enjoyable identity is therefore zero. This excludes inference cost.
The deterministic arm has no qualifying scored output and one unscored output,
so its final denominator and cost ratio remain unresolved. Challenge outputs
were not separately rated. B01/B06 count as one reported perceived family, not two enjoyable levels; the
missing B06 score is not filled from B01. Dollar cost remains unknown. This single-participant exercise does not establish a method ranking.

The positive response to seeing the goal and the poor clarity of B04 motivate
stronger in-world communication before scaling the bank. Technical solvability
did not predict clear completion or enjoyment. The B06 readout was preserved
without resetting the user's world (`human-feedback-readout.json`). The later
clarification identifies perceived repetition, not a reported runtime failure.
The two preview candidates differ in dimensions but retain the same mechanic
and topology; those differences did not make a distinct experience here.
