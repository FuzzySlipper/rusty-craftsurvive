# Worker delivery regression — Engine #7894

Local Chromium exercised the packaged CoreCLR product at `:37300`, switching
Dungeon whole normal → whole fine → split fine → split normal, seed 11.

- Baseline Engine `5c71990e89f8322eedaddeccab3f61eda82a629a` emitted three
  `DEV_HOST_WORKER_TELEMETRY_DROPPED` warnings. One browser attachment remained
  ready; this local baseline did not reproduce the earlier remote reattachment.
- Fixed Engine `c62aeeff6897a4687951c8f4ba1a20b8c78e68dc` emitted no new Engine
  events, browser warnings or browser errors, and retained one ready attachment.
  Final worker timing age was 14 ms. The warning checkpoint and delta are complete,
  without diagnostic lag or ring drops.
- Product readouts confirm 104,616 normal / 294,354 fine source triangles,
  3,260/3,260 clearance checks, 124/124 shell witnesses, no generation error,
  and zero boundary/non-manifold edges. Source collision remains the generated
  full mesh copy.

The host regression tests separately prove that later progress publications
cannot truncate a large transfer, while old complete publications still age out.
Worker-reader tests prove that a full publication queue delays the associated
timing sample instead of dropping it. No callback deadline was relaxed.

`fixed-checkpoint.json`, `fixed-after.json`, and `fixed-findings.json` use the
Engine warning-capture helper. `fixed-browser.json` retains attachment requests,
commands and actual product readouts; `fixed-final.png` is the rendered final view.
Baseline files preserve the reproduced warnings and command/attachment record.

The accelerated harness was unavailable: Wolf's Unix socket refused connections
while the controller service itself was running. This software-browser exercise
checks delivery and rendering; it makes no GPU performance claim.
