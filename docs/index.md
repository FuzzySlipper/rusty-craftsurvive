# Documentation map

Four documents carry current meaning. Everything else — experiment records,
provenance, and superseded state — lives in Den and is deliberately not copied here.

| Document | What it is |
| --- | --- |
| [survival-direction.md](survival-direction.md) | The durable design record: target, settled decisions, and the Engine boundary |
| [csharp-migration-map.md](csharp-migration-map.md) | Where product modules, Engine services, and content live |
| [known-limitations.md](known-limitations.md) | What the product does not do today, and the limits a change must respect |
| [live-proofs.md](live-proofs.md) | The live lane: the client, what it proves, captures, and the operational traps |

`README.md` at the repository root is the entry point for developing and running the
product. `AGENTS.md` holds the working rules.

## Where everything else lives

Den project `rusty-craftsurvive`:

- **Work state**: campaign #8595 with slices #8596–#8606 is the work record. If a
  document here and a task disagree, the task wins.
- **History**: retired study records and superseded sections are documents with
  `history/` slugs — for example `history/courtyard-verdict`,
  `history/procgen-workbench`, `history/known-limitations-full`,
  `history/survival-direction-campaign-record`, `history/survival-direction-module-survey`,
  `history/live-substrate-proof`, `history/authoring-lane` and `history/s0-decisions`.
- **Board**: durable discussion that is not a task.

A `history/` document keeps its text as written, including the Engine pair the study
ran against. That is provenance, not current state: nothing in those documents is
maintained, and none of them overrides a task or a live document.

## Evidence

`docs/evidence/` is where retained live captures go (S10, #8606). Review evidence for individual
tasks lives on the repository's orphan `evidence` branch, one directory per task, so it never
weighs on the working tree. Evidence belonging to retired studies stays recoverable from git
history, and the Den history index records where.
