# Known limitations

The initial experiment is intentionally bounded:

- one generated island is fully resident and remeshed as a whole after an edit;
- movement uses a deterministic fixed-step grounded capsule envelope with gravity, one-voxel
  stepping, and grounded-only jumping; it is deliberately not a general rigid-body framework;
- placement is rejected when the edited voxel overlaps the current player capsule; broader
  construction permissions and structural rules remain out of scope;
- materials currently share one simple render color, although canonical voxels retain grass, dirt,
  and stone slots;
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
