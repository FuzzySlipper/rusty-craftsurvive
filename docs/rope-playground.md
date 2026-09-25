# Rope playground

Campaign 6992 / task 6996 adds provisional physics stations to the default
Stoneworks court near spawn. The Engine pair is `b9c281937b26`: ordinary C#
SDK and runtime are adopted together. No product physics solver or game renderer
is involved. Yellow segments are Engine retained lines; cyan spheres mark
anchors and orange cubes are dynamic objects. The chain has six Engine-owned
beads and can settle against the existing terrain.

- R attaches the selected anchor within 10 metres; T releases.
- V cycles court, gateway, and the orange hanging dynamic object.
- Hold Q to shorten or Z to lengthen at 0.25 metres/second, within 2–10 metres.
- WASD and Space keep their ordinary move/jump behavior. While attached, planar
  input adds to swing momentum; reeling can lift the character from the floor.
- The panel's Reset to court action releases and returns the player to spawn.
  Strong input and release can carry the player beyond the authored courtyard;
  use reset to start another trial. Dynamic objects keep their current state.
- The Rope playground panel offers the same actions, a 4m/9m length target,
  and live copied diagnostics. These buttons use the product's generated debug
  actions and are explicit assistance, not proof of physical anchor picking.

Start with the court anchor, attach from spawn, shorten until clear of the floor,
and build an arc with movement. Release and observe continued travel. The
second fixed anchor is near the gateway/stairs for wall, ledge and ground
contact. The orange hanging object receives bounded character reactions;
the other two orange objects are tethered together, with one hung from above.

The existing admitted character steps explicitly observe the dynamic anchor,
propose a character step, then apply its reaction in one Dynamics step. Engine
owns collision, catch, effective mass, impulse bounds and solver continuation.
Product owns anchor selection, reel intent, presentation and diagnostic counters.
The panel reports current/effective maximum distance, radial/tangential velocity,
swept correction, reaction, saturation, unresolved distance, catches/releases,
query count and rope solver work. Tension is the Engine's sampled force proxy,
not a certified peak or breaking load.

This is a feel prototype, not a climbing progression or save feature. Attachments
are not persisted. Reeling deliberately performs work. Impulse saturation and
terrain can leave unresolved rope separation; the panel exposes it. Generated
procgen levels disable the courtyard playground. Changing the authored courtyard
may change station clearance. Visual, deterministic-control and subjective-feel
results are recorded separately after live validation; source compilation alone
does not certify gameplay.


The deterministic browser control sequence is
`tests/rope-playground-smoke.js`, submitted with the crew-services
`playtest run SESSION --file /absolute/path/to/tests/rope-playground-smoke.js
--budget-ms 45000` command. It uses DOM assistance, asserts copied diagnostics,
and retains original captures. Normal-speed native input trials are separate.

See [validation evidence](evidence/rope-6996/README.md) for separate control,
normal-speed, subjective-feel and warning-capture results.
