# Known limitations

The initial experiment is intentionally bounded:

- one generated island is fully resident and remeshed as a whole after an edit;
- movement is collision-aware flying, without gravity, grounded stepping, or jumping;
- materials currently share one simple render color, although canonical voxels retain grass, dirt,
  and stone slots;
- the native host retains arrow-key look, while the browser shell supports pointer-lock mouse look;
- there is no save format, inventory, crafting, survival simulation, streaming, networking, or
  Studio project adapter;
- the browser HUD is deliberately minimal, and the live Chromium smoke proves only the bounded
  movement/look/break/place loop rather than a broad browser compatibility campaign.

These are scope boundaries, not invitations to work around Engine ownership. Add a focused Den task
before expanding one.
