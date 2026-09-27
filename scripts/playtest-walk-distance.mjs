// Walk/distance capture for rusty-craftsurvive through the live browser lane.
//
// What comes from the product and what does not: every *position* and every
// *fact* below is read from the product's own `craft.player.readout` debug
// command over its live-debug HTTP endpoint. The walk distance and the deltas
// are arithmetic this harness performs on those positions - not a product
// odometer - and the report names them as such.
//
// The capture fails loudly rather than reporting a success-looking number:
//   - a non-200 response, a missing field or an unparseable number throws
//     instead of degrading silently to zero;
//   - fields are matched on a `;name=` boundary, so a nested readout such as
//     `lastMovement=[...after=...]` cannot be mistaken for the outer field;
//   - the walk only counts when the product reports that it received key
//     input, that its planar intent was non-zero, that yaw held steady, that
//     the player stayed grounded, and that its controller accepted a step with
//     non-zero displacement - so a moving platform, a teleport, an outside
//     force or a landed fall cannot masquerade as a walk;
//   - a planar distance below MINIMUM_WALK_METRES fails the run, which is what
//     makes a blocked input loud instead of a zero that reads like a walk.
//
// Input note: the Engine's canvas accepts gameplay input only when it is
// focused or pointer-locked (`allowsGameplayInput` plus
// `document.activeElement === canvas`). The key listeners themselves live on
// the document, so a session that holds keys without focusing the canvas first
// delivers no key events at all. The click below is that focus step.

const ORIGIN = 'http://127.0.0.1:37305';
const HOLD_MS = 2500;
const MINIMUM_WALK_METRES = 5;
const YAW_TOLERANCE_DEGREES = 2;

const read = async () => {
  const response = await fetch(ORIGIN + '/__rusty/product/runtime/debug/execute', {
    method: 'POST',
    headers: { 'content-type': 'text/plain; charset=utf-8' },
    body: 'craft.player.readout',
  });
  if (!response.ok) {
    throw new Error(`player readout failed: HTTP ${response.status}`);
  }
  const text = await response.text();
  if (!text.includes('cameraPosition=')) {
    throw new Error(`player readout did not contain a cameraPosition: ${text.slice(0, 200)}`);
  }
  return text;
};

const field = (text, name) => {
  const match = new RegExp('(?:^|;)' + name + '=([^;]*)').exec(text);
  if (match === null) {
    throw new Error(`player readout is missing the ${name} field`);
  }
  return match[1];
};

const triple = (value, what) => {
  const parts = value.split(',').map(Number);
  if (parts.length !== 3 || parts.some((part) => !Number.isFinite(part))) {
    throw new Error(`${what} is not a finite triple: ${value}`);
  }
  return { x: parts[0], y: parts[1], z: parts[2] };
};

const number = (value, what) => {
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) {
    throw new Error(`${what} is not a finite number: ${value}`);
  }
  return parsed;
};

// The last `step=[...]` receipt is the product's own record of the controller
// step it admitted, including whether it was accepted and how far it moved.
const lastStep = (text) => {
  const matches = [...text.matchAll(/step=\[([^\]]*)\]/g)];
  if (matches.length === 0) {
    throw new Error('player readout carried no controller step receipt');
  }
  const body = matches[matches.length - 1][1];
  const readField = (name) => {
    const match = new RegExp('(?:^|;)' + name + '=([^;]*)').exec(body);
    return match === null ? null : match[1];
  };
  return {
    accepted: readField('accepted'),
    displacement: readField('displacement'),
    casts: readField('casts'),
  };
};

const sample = async () => {
  const text = await read();
  const step = lastStep(text);
  return {
    raw: text,
    camera: triple(field(text, 'cameraPosition'), 'cameraPosition'),
    player: triple(field(text, 'after'), 'after'),
    yaw: number(field(text, 'yaw'), 'yaw'),
    grounded: field(text, 'grounded'),
    stance: field(text, 'stance'),
    updates: number(field(text, 'updates'), 'updates'),
    events: number(field(text, 'totalEvents'), 'totalEvents'),
    keyEvents: number(field(text, 'keys'), 'keys'),
    intent: field(text, 'intent'),
    step,
    stepDisplacement: step.displacement === null ? null : triple(step.displacement, 'step displacement'),
  };
};

const planarOf = (a, b) => Math.hypot(b.x - a.x, b.z - a.z);

checkpoint('focus', await browser({ op: 'click', selector: 'canvas' }));

// Focus is not guaranteed by a single click: the Engine gates gameplay input on
// the canvas being focused or pointer-locked and on its interaction mode, and a
// click that lands before the page is ready, or during an interface-mode moment,
// leaves the session deaf. Probe with a short hold and re-focus until the
// product reports that it actually received key input, so the measurement below
// cannot run against a session that is silently ignoring us.
const settle = async () => {
  for (let attempt = 0; attempt < 10; attempt++) {
    const probe = await sample();
    if (probe.grounded === 'True') {
      return probe;
    }
    await sleep(300);
  }
  throw new Error('the player never reported itself grounded, so there was nothing to measure');
};

let focused = false;
for (let attempt = 1; attempt <= 4 && !focused; attempt++) {
  const keysBefore = (await sample()).keyEvents;
  await keyboard.hold(['w'], 400);
  await sleep(150);
  const keysAfter = (await sample()).keyEvents;
  focused = keysAfter > keysBefore;
  checkpoint(`focus-probe-${attempt}`, { receivedKeyInput: focused, keysBefore, keysAfter });
  if (!focused) {
    await browser({ op: 'click', selector: 'canvas' });
    await sleep(300);
  }
}
if (!focused) {
  throw new Error('the product received no key input after four focus attempts, so the walk cannot be attributed');
}

const before = await settle();
checkpoint('before', { sample: before, shot: await observe() });

await keyboard.hold(['w'], HOLD_MS);

// Sampled immediately on release: no trailing wait, so the product's update
// counter spans the hold rather than an idle period after it.
const after = await sample();
checkpoint('after', { sample: after, shot: await observe() });

const playerWalk = planarOf(before.player, after.player);
const cameraWalk = planarOf(before.camera, after.camera);
const vertical = after.player.y - before.player.y;
const updates = after.updates - before.updates;
const keyEvents = after.keyEvents - before.keyEvents;
const events = after.events - before.events;
const intentMagnitude = Math.hypot(...after.intent.split(',').map(Number));
const yawDrift = Math.abs(after.yaw - before.yaw);

const failures = [];
if (playerWalk < MINIMUM_WALK_METRES) {
  failures.push(`the player moved ${playerWalk.toFixed(3)} m, below the ${MINIMUM_WALK_METRES} m floor - input was probably blocked`);
}
if (Math.abs(playerWalk - cameraWalk) > 0.01) {
  failures.push(`player travel ${playerWalk.toFixed(3)} m disagrees with camera travel ${cameraWalk.toFixed(3)} m`);
}
if (keyEvents <= 0) {
  failures.push(`the product received no key events during the hold (keys delta ${keyEvents})`);
}
if (Math.abs(yawDrift) > YAW_TOLERANCE_DEGREES) {
  failures.push(`yaw drifted ${yawDrift.toFixed(2)} degrees, so this was not a straight walk`);
}
if (before.grounded !== 'True' || after.grounded !== 'True') {
  failures.push(`the player was not grounded throughout (${before.grounded} -> ${after.grounded})`);
}
if (after.step.accepted !== 'true') {
  failures.push(`the product's last controller step was not accepted (accepted=${after.step.accepted})`);
}
if (after.stepDisplacement === null || Math.hypot(after.stepDisplacement.x, after.stepDisplacement.z) <= 0) {
  failures.push('the product reported no controller-step displacement for the final step');
}

const result = {
  verdict: failures.length === 0 ? 'walked' : 'inconclusive',
  failures,
  walk: {
    metresPlanar: Number(playerWalk.toFixed(4)),
    metresStraightLine: Number(Math.hypot(playerWalk, vertical).toFixed(4)),
    verticalMetres: Number(vertical.toFixed(4)),
    source: "product 'after' position (playerLocal), differenced by the harness",
    cameraCrossCheckMetres: Number(cameraWalk.toFixed(4)),
  },
  window: {
    holdMilliseconds: HOLD_MS,
    productUpdates: updates,
    productUpdateRateHz: Number((updates / (HOLD_MS / 1000)).toFixed(1)),
    note: 'productUpdates counts every product update tick in the window, so it is a frame count, not a locomotion sample count',
  },
  inputAttribution: {
    keyEventsDelta: keyEvents,
    totalEventsDelta: events,
    intentAfter: after.intent,
    intentMagnitude: Number(intentMagnitude.toFixed(4)),
    yawBeforeDegrees: before.yaw,
    yawAfterDegrees: after.yaw,
    groundedBefore: before.grounded,
    groundedAfter: after.grounded,
    stanceAfter: after.stance,
    controllerStepAccepted: after.step.accepted,
    controllerStepDisplacement: after.step.displacement,
    controllerStepCasts: after.step.casts,
  },
  positions: {
    beforePlayer: before.player,
    afterPlayer: after.player,
    beforeCamera: before.camera,
    afterCamera: after.camera,
  },
};

console.log(JSON.stringify(result));
if (failures.length > 0) {
  throw new Error(`walk capture inconclusive: ${failures.join('; ')}`);
}
return result;
