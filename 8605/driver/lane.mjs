// Drives the S9 core-loop playtest through the crew playtest lane: every game action is player input
// (keys, mouse, the virtual controller, clicks on the product's own UI) delivered by `playtest`.
// Steering reads the Engine's read-only playtest.observe and the product's readouts; nothing here
// runs a craft.* command that changes the game.
import { execFileSync } from 'node:child_process';
import { copyFileSync, mkdirSync, appendFileSync } from 'node:fs';

export const SESSION = process.env.PT_SESSION;
export const ORIGIN = process.env.CRAFT_ORIGIN ?? 'http://127.0.0.1:37305';
export const OUT = new URL('./out/', import.meta.url).pathname;
mkdirSync(`${OUT}frames`, { recursive: true });

export const KEY = { W: 87, S: 83, A: 65, D: 68, SHIFT: 16, SPACE: 32, J: 74, R: 82, I: 73, M: 77, ESC: 27, E: 69 };
export const DIGIT = (n) => 48 + n;
const DEGREES_PER_POINTER_UNIT = 0.12;
const MAX_BATCH_MS = 9000;

let frameIndex = 0;
export const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function pt(args) {
  const out = execFileSync('playtest', args, { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024, timeout: 120000 });
  try { return JSON.parse(out); } catch { return out; }
}

export function log(line) {
  console.log(line);
  appendFileSync(`${OUT}journal.txt`, `${new Date().toISOString()} ${line}\n`);
}

/** One lane input batch: keys held, mouse moves, clicks, controller states. */
export const input = (steps) => pt(['input', SESSION, '--json', JSON.stringify(steps)]);

/** A click on the product's own DOM UI, by selector. */
export const click = (selector) => pt(['browser', SESSION, '--json', JSON.stringify({ op: 'click', selector })]);

/** A lane observation kept as the next frame of the recording. */
export function frame() {
  const shot = pt(['observe', SESSION]);
  if (shot?.path) copyFileSync(shot.path, `${OUT}frames/${String(frameIndex++).padStart(5, '0')}.png`);
  return shot;
}

/** A retained lane capture at a milestone, also kept as a frame. */
export function capture(label) {
  const shot = pt(['capture', SESSION, '--json', JSON.stringify({ label })]);
  const path = shot?.path ?? shot?.capture?.path;
  if (path) copyFileSync(path, `${OUT}frames/${String(frameIndex++).padStart(5, '0')}.png`);
  log(`capture ${label}: ${path ?? JSON.stringify(shot).slice(0, 200)}`);
  return shot;
}

/** Reads the product: the Engine's playtest observation or a product readout. Never changes anything. */
export async function read(command) {
  const response = await fetch(`${ORIGIN}/__rusty/product/runtime/debug/execute`, { method: 'POST', headers: { 'content-type': 'text/plain; charset=utf-8' }, body: command });
  return (await response.text()).trim();
}
export const observe = async () => JSON.parse(await read('playtest.observe'));

/** Presses keys briefly. */
export const press = (keys, ms = 90) => input([{ kind: 'hold', keys, ms }]);

/** Holds keys for a while, capturing a frame every slice of it. */
export function hold(keys, ms, slice = 700) {
  for (let left = ms; left > 0; left -= slice) {
    input([{ kind: 'hold', keys, ms: Math.min(slice, left, MAX_BATCH_MS) }]);
    frame();
  }
}

/** Gives the world pointer lock again with a click high on the view, where the aim is at the sky or far ground. */
export async function lock() {
  input([{ kind: 'point', x: 640, y: 120, width: 1280, height: 720, ms: 50 }, { kind: 'click', button: 1, ms: 60 }]);
  await sleep(300);
}

/** Turns the view by mouse (the page holds pointer lock) to a yaw and pitch, in degrees. */
export async function lookTo(yaw, pitch) {
  for (let attempt = 0; attempt < 4; attempt++) {
    const o = await observe();
    let dyaw = ((yaw - o.yawDegrees) % 360 + 540) % 360 - 180;
    const dpitch = pitch - o.pitchDegrees;
    if (Math.abs(dyaw) < 3 && Math.abs(dpitch) < 3) return;
    try {
      input([{ kind: 'move', dx: Math.round(dyaw / DEGREES_PER_POINTER_UNIT), dy: Math.round(-dpitch / DEGREES_PER_POINTER_UNIT), ms: 60 }]);
    } catch (error) {
      if (!String(error.stderr ?? error).includes('pointer lock')) throw error;
      await lock();
    }
    await sleep(150);
  }
}

/** The yaw that faces from one place toward another: clockwise from north, north being -Z. */
export const bearing = (fromX, fromZ, toX, toZ) => (Math.atan2(toX - fromX, -(toZ - fromZ)) * 180) / Math.PI;

/**
 * Walks (sprinting) toward a place until within `near` metres, steering by the player's own
 * position and jumping when a step makes no progress.
 */
export async function walkTo(x, z, near = 2, budget = 60) {
  let last = null;
  for (let step = 0; step < budget; step++) {
    const o = await observe();
    const distance = Math.hypot(x - o.feet.x, z - o.feet.z);
    if (distance <= near) return { arrived: true, distance };
    await lookTo(bearing(o.feet.x, o.feet.z, x, z), -8);
    const stuck = last !== null && Math.hypot(o.feet.x - last.x, o.feet.z - last.z) < 0.4;
    const keys = stuck ? [KEY.W, KEY.SHIFT, KEY.SPACE] : [KEY.W, KEY.SHIFT];
    input([{ kind: 'hold', keys, ms: Math.round(Math.min(700, Math.max(150, distance * 90))) }]);
    frame();
    last = { x: o.feet.x, z: o.feet.z };
  }
  const o = await observe();
  return { arrived: false, distance: Math.hypot(x - o.feet.x, z - o.feet.z) };
}

/** The value of one name in a key=value readout. */
export const field = (readout, name) => readout.match(new RegExp(`${name}=([^\\s;]+)`))?.[1];

/**
 * Presses a button in the Menu drawer, opening the drawer first: frees the pointer (a screen opened
 * and closed, since a synthetic Escape does not release pointer lock), then clicks the drawer's
 * summary until the button can be clicked, whether the drawer started open or closed.
 */
export async function menuButton(selector) {
  press([KEY.I]); await sleep(400); press([KEY.ESC]); await sleep(400);
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      click(selector);
      return true;
    } catch {
      click('aside > details > summary');
      await sleep(300);
    }
  }
  click(selector);
  return true;
}
