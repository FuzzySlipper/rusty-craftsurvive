# Live proofs and evidence lanes

Two lanes prove different things. The **managed lanes** (`tests/`) run in CI and cover product
rules and SDK values that need no host. The **live lane** runs the real product against the
installed runtime pack and is the only lane that can prove host-bound mechanisms, rendering and
load paths. A build passing is not evidence about the live lane. CI's `product` job is the one
automated live check: it serves the product, checks it keeps updating with a matching generator
fingerprint, and walks the player on input with the client below.

The live substrate proof that established the Engine constraints this product builds on left
with the authoring lane; its record is Den `history/live-substrate-proof`, and the constraints
are in [survival-direction.md](survival-direction.md) §6.1.

## Operational traps

- **A product fault is quiet.** An exception that escapes a product callback leaves the runtime
  `state=Faulted` (`craft.runtime`) and prints nothing to the console; start the host with
  `--diagnostics-log <file>` to see the exception and its stack (Engine #8992).
- **A stale staged product produces misleading catalog errors.** After changing authored catalog
  code, a previously staged build under `src/CraftSurvive.Game/obj/Rusty.Engine/Product/` can
  still be what runs. If an error does not match the source, remove that directory to force a
  clean restage.
- **A killed run can leave staging half-done.** Killing `rusty dev` mid-flight occasionally makes
  the next start fail to stage its content. It is a race, not a code problem: start again.
- **A stale save is discarded, not fatal.** A save written for another seed or generator version
  is discarded, kept as that key's backup, and the readouts say so (`restore=discarded: ...`).
  `craft.save.manifest` lists every key and whether it and its backup are stored.
- **`rusty dev` restarts a crashed worker twice, then stops.** A product that throws during
  construction appears as `child-exited-unexpectedly` twice and then
  `paused-fault: unexpected-child-exit-budget-exhausted`. Read the first `CSHARP_PRODUCT_CALL`
  line, not the last.
- **Persistence lives in the working tree.** `rusty dev` keeps the product's store under
  `.runtime/persistence`; a live experiment that edits the world changes the saved world. Copy
  that directory aside first when the edits should not stay.

## The live-lane client

`scripts/live.mjs` is the one client for a running product. It needs a host started with
`--live-debug` (as `.den-serve.json` starts it) and takes the host's address from the
environment, never from the script:

```sh
rusty dev --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj --live-debug --bind-host 127.0.0.1 --port <port>
export CRAFT_ORIGIN=http://127.0.0.1:<port>   # or CRAFT_PORT=<port>
```

```sh
node scripts/live.mjs walk --seconds 2
```

Walks the player with the forward control for two seconds and prints the distance, the
positions before and after, and how many key events the product received. It fails unless the
player moved at least `--minimum` metres (default 1) *and* the product received the key events,
so the distance is attributable to input. Other actions: `--action walk-back|strafe-left|strafe-right`.

```sh
node scripts/live.mjs discovery
```

Stands the player at the nearest unvisited standing stones and checks the journal counted a first
visit, stored itself at 32 + 51 bytes per place, and reports what its restore did (`--kind` picks
another kind of place).

```sh
node scripts/live.mjs exec <debug command>
```

Runs any debug command, including the Engine's playtest commands the product registers:
`playtest.observe` (the player's feet, look, vitals and received key events, as JSON),
`playtest.action <id>` (which physical control an action is) and `playtest.look <yaw> <pitch>`.

**How input reaches the product.** `walk` does not use a page. It claims input through the
host's harness lane (`control/claim`, `runtime/input`, `control/release`), which delivers key facts to
the product's ordinary mappings exactly as a page would; an attached page shows the input as held
by `craft-live` meanwhile.

**Keyboard input from a page reaches the product only once the Engine canvas has gameplay
focus.** Measured with a headless page: with the page just loaded (focus on the document body),
or with a product UI button focused, holding W for 1.5 s delivered no key events and moved the
player 0 m; after one click on the canvas the same hold delivered 2 key events and moved the
player about 11 m. A page-driven lane must click the canvas (not the UI panel) before sending
keys. This is why earlier scripted walks recorded no key events.

**A body placed inside the world stops the product.** The character controller refuses to step a
body that starts inside a solid cell, and that refusal faults the product. `craft.player.teleport`
therefore moves the player only where a standing body fits - the point asked for, else the ground
of that column - and building refuses cells the player's body occupies.

## Browser captures through the playtest lane

Screenshots come from the crew-services playtest lane, which serves the product from
`.den-serve.json` and drives a page from `.den-playwright.json`. Do not start a competing host on a
broker-owned session's port merely to look at it.

```sh
playtest start rusty-craftsurvive     # returns SESSION and a first screenshot
playtest observe SESSION              # the current frame
playtest capture SESSION --json '{}'  # a retained capture
playtest stop SESSION
```

A page-driven check must click the world before sending keys (see above). Retained captures
belong under [evidence](evidence/); each names the pair and machine it was taken on in Den.
