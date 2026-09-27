// Walk/distance capture for rusty-craftsurvive through the live browser lane.
// Holds W for two seconds and measures the player's own reported movement.
// Every number here comes from the product's debug command, not from the harness.

const ORIGIN = 'http://127.0.0.1:37305';

const readPlayer = async () => {
  const response = await fetch(ORIGIN + '/__rusty/product/runtime/debug/execute', {
    method: 'POST',
    headers: { 'content-type': 'text/plain; charset=utf-8' },
    body: 'craft.player.readout',
  });
  return await response.text();
};

const field = (text, name) => {
  const match = new RegExp(name + '=([^;]*)').exec(text);
  return match ? match[1] : null;
};

const triple = (value) => {
  if (value === null) return null;
  const parts = value.split(',').map(Number);
  return { x: parts[0], y: parts[1], z: parts[2] };
};

const sample = async () => {
  const text = await readPlayer();
  return {
    camera: triple(field(text, 'cameraPosition')),
    player: triple(field(text, 'after')),
    yaw: Number(field(text, 'yaw')),
    grounded: field(text, 'grounded'),
    updates: Number(field(text, 'updates')),
    events: Number(field(text, 'totalEvents')),
    keys: Number(field(text, 'keys')),
    intent: field(text, 'intent'),
  };
};

// The Engine's canvas accepts key input only while it is focused or pointer-locked,
// and its keydown listener is bound to the canvas itself, so focus it first.
const focus = await browser({ op: 'click', selector: 'canvas' });
checkpoint('focus', focus);

const before = await sample();
const beforeShot = await observe();
checkpoint('before', { sample: before, shot: beforeShot });

await keyboard.hold(['w'], 2500);
await sleep(400);

const after = await sample();
const afterShot = await observe();
checkpoint('after', { sample: after, shot: afterShot });

const dx = after.camera.x - before.camera.x;
const dy = after.camera.y - before.camera.y;
const dz = after.camera.z - before.camera.z;
const planar = Math.hypot(dx, dz);
const updates = after.updates - before.updates;

const result = {
  before: before.camera,
  after: after.camera,
  delta: { x: Number(dx.toFixed(4)), y: Number(dy.toFixed(4)), z: Number(dz.toFixed(4)) },
  planarDistanceMetres: Number(planar.toFixed(4)),
  straightLineMetres: Number(Math.hypot(dx, dy, dz).toFixed(4)),
  updatesElapsed: updates,
  metresPerUpdate: updates > 0 ? Number((planar / updates).toFixed(4)) : null,
  yawBefore: before.yaw,
  yawAfter: after.yaw,
  groundedBefore: before.grounded,
  groundedAfter: after.grounded,
  inputEventsBefore: before.events,
  inputEventsAfter: after.events,
  keyEventsBefore: before.keys,
  keyEventsAfter: after.keys,
  intentAfter: after.intent,
  screenshots: { before: beforeShot, after: afterShot },
};

console.log(JSON.stringify(result));
return result;
