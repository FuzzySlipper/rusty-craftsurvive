# Candidate comparison and semantic repair (#7909)

This slice compares retained candidates and applies a canonical one-field repair
without regenerating unrelated accepted decisions. The matched Engine pair is
`0.1.0-dev.2e4255bd3ad5` (latest published pair checked September 12 UTC).

## Source and runtime checks

- `tests/Workbench` verifies all three failing fixtures become contract-passing,
  every other serialized candidate field is unchanged, invalid/no-op edits reject,
  and altered result/identity/cost/null analyses cannot pass strict receipt readback.
- Tool `--self-check` verifies atomic repair result/receipt output, coherent strict
  readback and preservation of the original input, including direct alias rejection.
- Procgen checks, UI typecheck and the ordinary Game Release build pass.
- `runtime.json` records actual pre/post C# analyses, scoped mesh check summaries,
  identities, geometry projection receipts and one-field differences for all three
  repairs. Passing probe rows are omitted from this evidence file; full counts are
  retained. The parent/result JSON is retained in each `*.repair.json`.
- Return-switch and recovery repairs each pass 38/38 body samples. Preview passes
  48/48; its closed-gate lookout ray changes from occluded to clear. These checks
  do not certify every navigation path or human recognition of the goal.
- `large-comparison.json` records pinning complex-29, loading complex-83 and
  comparing the retained baseline against the current plan. The comparison was
  approximately 37 KB and passed through the ordinary bounded debug channel.
  Pinning intentionally survives a plain load to support this workflow.
- `reopened.json` records loading the exported preview repair from admitted content.
  Its parent is labelled `offline parent`, with physical checks unavailable. The
  result is actually rebuilt and freshly checked; receipt data does not fabricate
  historical mesh or screenshot evidence.

Runtime setup used ordinary admitted debug commands, not native traversal. UI
captures and native interaction observations are recorded separately below.

## Remote native UI and world observations

Session `25ed2d71-a35b-45cd-ad75-a73d2f3c9f34`, Wolf slot-1, September 12 UTC.
The configured LAN URL was `http://192.168.1.22:37300/`; the remote browser used
its managed `http://localhost:37300/` forwarding origin. This does not re-certify
LAN-origin browser startup.

An independent playtester loaded the failing preview through the UI, pinned it,
activated Restore Preview once, and observed the changed decision plus progression
moving from three counterexamples/unrecoverable states to zero. The prepared
receipt link remained visible at +3 and +6 seconds. Their initial empty map panes
were a display bug; the final UI hides maps until baseline facts exist. The root
also shortened provenance captions and kept full diagnostics expandable.

The root then checked the final UI through native inputs. `layout-comparison.png`
shows the one-field change, cost and two resolved maps; `graph-and-receipt.png`
shows unchanged graphs and the prepared download link after another three seconds.
The graphs deliberately stay identical for this information-opening repair.
`native-inputs.json` retains the input receipts; `captures.json` indexes the
original captures and sidecars. Images were copied unchanged.

The matched final world pair uses an admitted candidate load for setup and the
ordinary UI Restore Preview action. `world-before.png` shows a solid wall;
`world-after.png` shows the opening and red goal landmark through it. Both actual
player positions are identical in `matched-entrance.json`, and the visible framing
matches. This supersedes the independent observer's earlier unmatched world views.
The snapshot metadata does not establish GPU completion or identify the PNG's
Engine frame. These are original visible observations plus separately recorded
runtime facts, not exhaustive navigation or attention proof.

Screenshot file attachments and saving via the browser download dialog were not
interactively exercised. Receipt export/reopen and the offline atomic CLI path
were exercised. The optional image UI remains explicitly manual and transient.

The owned session was stopped. `cleanup.json` records release/cleanup;
`pool-after-stop.json` records subsequent pool occupancy. No other session was
stopped.
