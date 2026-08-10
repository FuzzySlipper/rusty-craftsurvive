# Known limitations

The initial experiment is intentionally bounded:

- one generated island is fully resident and remeshed as a whole after an edit;
- movement is collision-aware flying, without gravity, grounded stepping, or jumping;
- materials currently share one simple render color, although canonical voxels retain grass, dirt,
  and stone slots;
- look uses arrow keys because the private renderer host readout currently exposes pointer position
  and buttons, not downstream-owned relative mouse-look deltas;
- there is no save format, inventory, crafting, survival simulation, streaming, networking, rich DOM
  shell, or Studio project adapter;
- automated evidence is headless contract/mechanism coverage; a headed native run remains the
  appropriate proof for renderer-visible behavior.

These are scope boundaries, not invitations to work around Engine ownership. Add a focused Den task
before expanding one.

