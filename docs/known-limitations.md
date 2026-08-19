# Known limitations

The initial experiment is intentionally bounded:

- generation version 2 has no authored terrain edge inside the supported coordinate envelope and
  uses a bounded requested/pinned/hysteresis chunk window; it has no background worker,
  generated-chunk disk cache, biome system, or general procgen framework, and its
  generation budget is measured per synchronous product input tick;
- movement uses the reusable Engine fixed-step kinematic capsule controller with checked product
  tuning, crouch/stand clearance, slopes, steps, floor snap, moving-platform carry, and external
  impulses; CraftSurvive does not expose a general rigid-body framework;
- each radius 0/1/2 spherical brush is one atomic revision, and placement is rejected as a whole
  when any edited voxel overlaps the current player capsule or leaves Engine's coordinate envelope; broader
  construction permissions and structural rules remain out of scope;
- one checked 2 by 2 atlas supplies grass-top, grass-side, dirt, and stone presentation; it has no
  normal maps, material variants, animated tiles, or authored blending;
- MC/DC use per-triangle geometric-normal dominant-plane world-space texture projection because
  reconstructed Engine meshes do not carry greedy tile coordinates; steep grass selects the side
  tile, but sharp projection-axis transitions can remain visually abrupt;
- collision and navigation projections are still rebuilt coherently across the finite resident
  authority after each accepted edit. Retained rendering is chunk-granular, while expanded textured
  MC/DC dirty chunks retain higher rebuild, transfer, and apply costs than greedy boxes;
- the native host retains arrow-key look, while the browser shell supports pointer-lock mouse look;
- browser-host edit overlays use bounded fingerprinted JSON and atomic replacement, but there is no
  migration from unsupported generation/schema versions, multi-writer merge, inventory, crafting,
  survival simulation, networking, or Studio project adapter;
- the browser HUD exposes concise authority and typed Engine controller facts for bounded certification rather
  than a general debug console;
- the deterministic Chromium campaign combines physical browser checks with focused Rust consumer
  coverage for slopes, corners, steps, blocked stand, moving support, and impulses. True sloped
  static-mesh geometry is not part of the voxel island presentation, so browser judgement of the
  reconstructed MC/DC terrain remains presentation-only rather than alternate collision authority;
- the campaign is not a broad browser, accessibility, or hardware compatibility campaign.
- exact-global/local-origin rebasing is certified through ±262,144 units in both signs. Engine's
  admitted voxel and local-frame envelope remains ±1,000,000; larger coordinates, cross-origin
  multiplayer policy, background streaming, and limitless precision are not certified here.
- the runtime voxel-sprite garden is an intentionally rough visual lab, not a production character
  renderer: it captures one orthographic view at a time, uses RGBA8 depth, approximates splat
  orientation, leaves transparent splat instances unsorted within one draw, and reports CPU
  submission timing rather than GPU timing. Its 4K option retains 256 MiB of RGBA8 outputs per
  side and needs another 64 MiB temporary depth texture during capture before driver overhead;
- runtime capture currently records the retained model's rendered color rather than canonical
  albedo/material channels. Capture direction, elevation, lighting, resolution, and RED splat-grid
  changes stay queued until an explicit selected-side or pair recapture rather than continuously
  rebaking; automatic walking-sector recapture pauses at 512px and above.
- the gold ghost plate remains one frozen capture sector over the original retained topology. It is
  deliberately nearest-sampled and sprite-like, but off-axis motion can expose topology-dependent
  folding, disconnected source regions, texture smear, or holes; it has no held animation,
  compiled plate shell, regional depth policy, or sector blending yet. Only the selected subject's
  gold plate is shown because simultaneous plates in the three depth rows visually occlude one
  another from the spawn view; subject selection recreates the isolated plate and its exact source
  view. The visible gray 3D column is an inspection copy rather than a second gameplay or capture
  authority.

These are scope boundaries, not invitations to work around Engine ownership. Add a focused Den task
before expanding one.
