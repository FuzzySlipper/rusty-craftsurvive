# Known limitations

The initial experiment is intentionally bounded:

- one seeded, size-bounded generated island is fully resident and remeshed as a whole after an
  edit; there is no streaming, chunk eviction, biome system, or general procgen framework;
- movement uses the reusable Engine fixed-step kinematic capsule controller with checked product
  tuning, crouch/stand clearance, slopes, steps, floor snap, moving-platform carry, and external
  impulses; CraftSurvive does not expose a general rigid-body framework;
- each radius 0/1/2 spherical brush is one atomic revision, and placement is rejected as a whole
  when any edited voxel overlaps the current player capsule or leaves the finite world bounds; broader
  construction permissions and structural rules remain out of scope;
- one checked 2 by 2 atlas supplies grass-top, grass-side, dirt, and stone presentation; it has no
  normal maps, material variants, animated tiles, or authored blending;
- MC/DC use per-triangle geometric-normal dominant-plane world-space texture projection because
  reconstructed Engine meshes do not carry greedy tile coordinates; steep grass selects the side
  tile, but sharp projection-axis transitions can remain visually abrupt;
- the finite terrain is still coherently rebuilt after each accepted edit; optimized box-mode
  edits are interactive, while expanded textured MC/DC streams retain higher browser transfer and
  apply costs until the renderer owns a bounded incremental mesh-update contract;
- the native host retains arrow-key look, while the browser shell supports pointer-lock mouse look;
- there is no save format, inventory, crafting, survival simulation, streaming, networking, or
  Studio project adapter;
- the browser HUD exposes concise authority and typed Engine controller facts for bounded certification rather
  than a general debug console;
- the deterministic Chromium campaign combines physical browser checks with focused Rust consumer
  coverage for slopes, corners, steps, blocked stand, moving support, and impulses. True sloped
  static-mesh geometry is not part of the voxel island presentation, so browser judgement of the
  reconstructed MC/DC terrain remains presentation-only rather than alternate collision authority;
- the campaign is not a broad browser, accessibility, or hardware compatibility campaign.

These are scope boundaries, not invitations to work around Engine ownership. Add a focused Den task
before expanding one.
