# CraftSurvive.Procgen

Pure deterministic graph planning, layout generation, and bounded workload utilities used by CraftSurvive's level-planning lane. This local library has no Rusty.Engine, host, UI, filesystem, or adjacent-repository dependency.

`CraftSurvive.Procgen.Artifacts` and `CraftSurvive.Procgen.Tool` retain the donor's offline strict-artifact generation and atomic file-pair writer. They reference only this local library; the Tool's self-check consumes [the one retained workload corpus](../../tests/Procgen/fixtures/csharp-workload-corpus.v1.json). Product, workbench, and UI code are intentionally absent.

## Donor consultation

- Corpus and snapshot: `/home/dev/rusty-procgen`, `c9ee6bb6e69e9280fd13859ae8cc58c2b661b400`.
- Inspected: `RustyProcgen.Core` graph/canonical/validation/analytics sources, `Generation`, `Workloads`, `RustyProcgen.Core.Checks`, and the offline `RustyProcgen.Artifacts`/`RustyProcgen.Tool` flow.
- Outcome: adapted into the `CraftSurvive.Procgen` namespace with source semantics and pure checks retained.
- Offline artifact kind names now use `craftsurvive_procgen`; generator provenance names use `CraftSurvive.Procgen`. The retained corpus was regenerated accordingly, so artifact hashes change with that provenance. No compatibility decoder is added; archived Procgen artifacts remain in the donor.
- Deliberate deviations: no Product, workbench, Engine, or UI code was imported. The Core checks construct their fixtures in code. The offline Tool consumes only the current C# workload corpus; all catalog, geometry, CA trace, artifact-result, and historical corpus fixtures stay with the archived workbench.
