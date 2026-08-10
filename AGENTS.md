# Rusty CraftSurvive agent guidance

## Den bootstrap

- Project ID: `rusty-craftsurvive`
- Resolve live Den guidance before substantial work.
- If Den is unreachable, stop and report the failed Den operation. Do not reconstruct task state
  from local files.

## Project role

This repository is an experimental downstream Rusty Engine game, not an Engine provider and not a
second Studio. Keep experiments product-specific here until a concrete second consumer proves a
mechanism reusable enough to promote upstream.

## Dependency and ownership posture

- Consume exactly one unconditional sibling path dependency: the complete `rusty-engine` facade.
- Preserve owner namespaces such as `rusty_engine::engine_spatial`; do not select individual Engine
  crates.
- Do not add versions, git pins, SHA records, updater scripts, pull checks, or freshness CI. The
  sibling Engine checkout is rolling-current local source and downstream fixes forward when it
  breaks.
- Rust owns live terrain, player pose, input meaning, edits, collision, navigation, remeshing,
  gameplay consequences, and future persistence.
- TypeScript, if introduced, may own trusted content composition and disposable shell/HUD state.
  It must not become a second gameplay or voxel authority.
- Use the Engine-owned Rust renderer host. Never import renderer packages, private bridge code,
  Three/WebGL, or Studio internals downstream.
- Studio runs from the Engine repository and opens downstream projects through a deliberately
  added `.rusty-studio.json` adapter. This repository has no adapter until it owns a persisted
  project contract that needs one.

## Voxel experiment contract

- `VoxelCollisionScene` material voxels are the sole terrain authority.
- Apply break/place through named, revision-checked `VoxelEditService` transactions.
- Treat box, marching-cubes, and dual-contouring output as disposable projections of the same
  accepted cells. Surface selection is startup configuration, not authority.
- Keep generation deterministic and bounded. Do not silently grow this into streaming, infinite
  world generation, networking, or a universal procgen framework.
- Any renderer-visible update must follow an accepted Rust edit and rebuild; browser picks or UI
  proposals are never self-authorizing.

## Working and verification

- Preserve unrelated dirty work and inspect `git status` before and after edits.
- Run the narrowest tests first, then `./scripts/verify.sh`.
- User-visible renderer changes need a real headed check when the environment supports it; report
  explicitly when only contract/headless evidence ran.
- Update `README.md`, `docs/donor-provenance.md`, and the Den `known-limitations` document when an
  ownership boundary or deliberate limitation changes.

