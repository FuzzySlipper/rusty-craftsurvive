# Documentation map

Five documents carry current meaning. Everything else — experiment records,
provenance, and superseded state — lives in Den and is deliberately not copied here.

| Document | What it is |
| --- | --- |
| [survival-direction.md](survival-direction.md) | The durable design record: target, module-level work, settled decisions, and the Engine boundary |
| [csharp-migration-map.md](csharp-migration-map.md) | Where product modules, Engine services, and content live |
| [known-limitations.md](known-limitations.md) | What the product does not do today, and the limits a change must respect |
| [live-proofs.md](live-proofs.md) | How to run the live evidence lane, what it proves, and the operational traps |

`README.md` at the repository root is the entry point for developing and running the
product. `AGENTS.md` holds the working rules.

## Where everything else lives

Den project `rusty-craftsurvive`:

- **Work state**: campaign #8595 with slices #8596–#8606 is the work record. If a
  document here and a task disagree, the task wins.
- **History**: retired study records and superseded sections are documents with
  `history/` slugs — for example `history/courtyard-verdict`,
  `history/procgen-workbench`, `history/known-limitations-full`,
  `history/survival-direction-campaign-record`, and `history/s0-decisions`.
- **Board**: durable discussion that is not a task.

A `history/` document keeps its text as written, including the Engine pair the study
ran against. That is provenance, not current state: nothing in those documents is
maintained, and none of them overrides a task or a live document.

## Evidence

`docs/evidence/8596/` holds the S0 live-proof captures. Evidence belonging to retired
studies was removed from the working tree to keep the repository navigable; it stays
recoverable from git history, and the Den history index records which directories
were retired and where to find them.
