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
