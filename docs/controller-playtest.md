# Native controller playtest — 2026-09-07

The actual demo at http://192.168.1.22:37300/ was tested through a Wolf virtual
Xbox controller on den-srv, Firefox 155.0.1, and a 1280×720 Moonlight stream.
Input reached Engine's native gamepad ingress and the C# player controller.
No mouse emulation or browser input replacement was used.

| Control | Action |
| --- | --- |
| Left stick | Proportional movement |
| Right stick | Look, up to 108 degrees/second |
| A / B / LS | Jump / crouch / sprint |
| RT / LT | Primary clear / secondary set |
| X | Impulse |

Both sticks have a radial 15% deadzone. Look uses admitted simulation time;
keyboard and mouse remain supported independently. Engine now exposes analog
trigger pressure too, although terrain interaction uses digital trigger edges.

Native tests verified proportional movement, yaw, pitch, jump, crouch, sprint,
and return to neutral. Right-stick X 0.4 and 0.8 for one second turned about
29 and 92 degrees. Left-stick 0.45 and 0.85 produced movement intents 0.353 and
0.823. Wall collisions constrain displacement; these are response checks.
RT/LT reached terrain interaction but returned `cast-miss`, so successful edits
are not certified. X has focused input-test coverage only.

[Evidence index](/home/dev/dsh-crew/experiments/wolf-den-srv/controller/evidence/analog-fps/index.json)
contains screenshots, the native action journal, read-only C# readouts, and
Engine health captures. The Engine reported zero warnings/errors/dropped events;
this does not establish browser-console silence, audio playback, or frame pacing.
One controller was created before browser launch. Late hotplug and multiple
controllers remain untested. The remote session was released after testing.

The installed pair is SDK `0.1.0-dev.2574cc89fd30.gamepad1` and
`.runtime/runtime-pack-2574cc89fd30-gamepad2`. These are local development
artifacts. Focused player-input tests cover timestep-independent look,
deadzones, retained analog magnitude, button edges, and independent keyboard
state. The demo remains running through Den Serve on its original port.

### Camera presentation comparison

The player opts into Engine position interpolation with a 1/60 s presentation
buffer. Look uses the latest authoritative orientation; physics and gameplay
rays still use the current player state. Teleports and rebases cut the sample
history. Compare through the ordinary debug commands:

- `craft.player.camera latest 0.0166666667`: immediate pose publication.
- `craft.player.camera position 0.0166666667`: interpolate translation only.
- `craft.player.camera pose 0.0166666667`: interpolate translation and orientation.

The delay is a product choice; full-pose mode deliberately delays look too.
`craft.player.readout` includes the selected mode and delay. Renderer presentation
readback separates submitted cameras from authoritative `sourceCameras`, and
reports the sample identity, source/presentation times, and local receipt/sample
times. A stream screenshot is not an exact rendered-frame or GPU-completion
measurement. Changing this mode does not change the fixed simulation cadence.

The #7918 comparison used pair `0.1.0-dev.be140ba1ccd2`, `complex-29`, a temporary
20 Hz simulation, and a native accelerated renderer at about 59 Hz. With a
50 ms buffer, position mode showed up to 6.2 cm of translation separation from
the current authoritative pose while keeping yaw identical; pose mode also
showed intermediate orientation (up to 1.5 degrees during controller look).
Latest mode matched the published pose. Each focused mode consumed 120 native
mouse deltas and remained grounded during the movement sequence. A deliberate
2-second buffer did not blend across a teleport or the 1024 m rebase threshold:
the first fresh queried submission already used the new coordinate frame.
Normal configuration remains 60 Hz with position-only presentation at 1/60 s.

Raw actions, concurrent pose/runtime snapshots, originals, excluded attempts,
and warning capture are retained at `/home/agent/.codex/camera-7918/`.
Submission facts are CPU-side observations; the native video stream was 30 Hz
and does not provide exact screenshot/frame correlation or GPU timing.
The preliminary unfocused controller run was rejected by interaction mode and
is excluded. Transport comparison evidence is separate under
`/home/agent/.codex/pacing-7919/`; the courtyard reload window is excluded.

The final #7919 native run at 60 Hz used the same complex-29 dungeon and
latest-camera mode to isolate delivery from smoothing. Receipt interval p95
was 18 ms in controller look, walk/look, and mouse windows; apply p95 was
18–19 ms, versus 34 ms in the matched earlier controller windows. Rendering
remained about 59 Hz. This is a bounded comparison, not proof that all
network-related hitches are eliminated. The SSE connection now uses TCP_NODELAY.
Final scene restoration and native lease release are recorded under
`/home/agent/.codex/pacing-7919/final60-fresh/`.

Diagnostics are report-only: no compatible browser-console baseline exists.
Engine #7920 tracks a recovered startup input NetworkError; #7921 tracks a
retired browser connection failing after whole-host replacement, including a
terminal transport diagnostic from that excluded attempt. A fresh native
browser completed the final run. These findings preclude a clean diagnostics
claim; they do not invalidate the explicitly separated fresh-session results.
