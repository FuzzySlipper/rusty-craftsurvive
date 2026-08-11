# Known limitations

The initial experiment is intentionally bounded:

- one seeded, size-bounded generated island is fully resident and remeshed as a whole after an
  edit; there is no streaming, chunk eviction, biome system, or general procgen framework;
- movement uses a deterministic fixed-step grounded capsule envelope with gravity, one-voxel
  stepping, and grounded-only jumping; it is deliberately not a general rigid-body framework;
- each radius 0/1/2 spherical brush is one atomic revision, and placement is rejected as a whole
  when any edited voxel overlaps the current player capsule or leaves the finite world bounds; broader
  construction permissions and structural rules remain out of scope;
- one checked 2 by 2 atlas supplies grass-top, grass-side, dirt, and stone presentation; it has no
  normal maps, material variants, animated tiles, or authored blending;
- MC/DC use per-triangle geometric-normal dominant-plane world-space texture projection because
  reconstructed Engine meshes do not carry greedy tile coordinates; steep grass selects the side
  tile, but sharp projection-axis transitions can remain visually abrupt;
- the native host retains arrow-key look, while the browser shell supports pointer-lock mouse look;
- there is no save format, inventory, crafting, survival simulation, streaming, networking, or
  Studio project adapter;
- the browser HUD exposes concise authority and locomotion facts for bounded certification rather
  than a general debug console;
- the deterministic Chromium campaign covers one authored trench/wall/support route in box,
  marching-cubes, and dual-contouring presentation; it is not a broad browser, accessibility, or
  hardware compatibility campaign.

These are scope boundaries, not invitations to work around Engine ownership. Add a focused Den task
before expanding one.
