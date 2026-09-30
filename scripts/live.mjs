// The live-lane client: one small program for driving a running CraftSurvive over its host's HTTP
// surface. It needs a host started with --live-debug (as `rusty dev` in .den-serve.json is), and
// takes the host's address from the environment, so it runs on any machine:
//
//   CRAFT_ORIGIN=http://127.0.0.1:<port> node scripts/live.mjs <command> [options]
//   (or CRAFT_PORT=<port>, with CRAFT_HOST defaulting to 127.0.0.1)
//
// Commands:
//   exec <debug command...>              run one debug command and print its answer
//   walk [--action walk-forward] [--seconds 2] [--minimum 1]
//                                        hold the action's control as harness input and report the
//                                        distance walked; fails unless the player moved at least
//                                        --minimum metres and the product received the key events
//   discovery [--kind StandingStones]    stand at an unvisited place and check the journal noticed,
//                                        saved and counted it as a first visit
//
// Input goes through the Engine's harness lane (control/claim, runtime/input, control/release; see
// the Engine's playtest-inspection guide): it reaches the product exactly as a page's key would,
// without a page, and an attached page shows the input as held by this client.

const INPUT_CONTEXT = 'gameplay.default';
const CLAIM_LABEL = 'craft-live';
const LEASE_MS = '30000';
const SETTLE_MS = 400;
const DISCOVERY_WAIT_MS = 3000;
const JOURNAL_HEADER_BYTES = 32;
const JOURNAL_RECORD_BYTES = 51;

function origin() {
  if (process.env.CRAFT_ORIGIN) return process.env.CRAFT_ORIGIN.replace(/\/$/, '');
  if (process.env.CRAFT_PORT) return `http://${process.env.CRAFT_HOST ?? '127.0.0.1'}:${process.env.CRAFT_PORT}`;
  throw new Error('Set CRAFT_ORIGIN (or CRAFT_PORT) to the running host, for example CRAFT_ORIGIN=http://127.0.0.1:43123.');
}

async function exec(command) {
  const response = await fetch(`${origin()}/__rusty/product/runtime/debug/execute`, {
    method: 'POST', headers: { 'content-type': 'text/plain; charset=utf-8' }, body: command,
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${command} answered HTTP ${response.status}: ${text.slice(0, 200)} (is the host running with --live-debug?)`);
  return text.trim();
}

async function post(path, body) {
  const response = await fetch(`${origin()}${path}`, {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body),
  });
  const text = await response.text();
  const result = text.length > 0 ? JSON.parse(text) : {};
  if (!response.ok || result.accepted === false) throw new Error(`${path} refused: ${text.slice(0, 300)}`);
  return result;
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const observe = async () => JSON.parse(await exec('playtest.observe'));
const field = (readout, name) => readout.match(new RegExp(`${name}=([^\\s;]+)`))?.[1];

function options(argv, defaults) {
  const result = { ...defaults };
  for (let index = 0; index < argv.length; index += 2) {
    const name = argv[index].replace(/^--/, '');
    if (!(name in defaults)) throw new Error(`unknown option --${name}`);
    result[name] = typeof defaults[name] === 'number' ? Number(argv[index + 1]) : argv[index + 1];
  }
  return result;
}

/** Holds one action's control for a while through the harness lane, then gives input back. */
async function hold(key, milliseconds) {
  const presentation = JSON.parse(await exec('engine.renderer.presentation'));
  let claim = await post('/__rusty/product/runtime/control/claim', { runtime: presentation.runtime, label: CLAIM_LABEL, leaseMs: LEASE_MS });
  let binding = claim.binding;
  let sequence = BigInt(claim.nextInputSequence);
  const send = async (edge) => {
    const result = await post('/__rusty/product/runtime/input', {
      batch: [{ runtime: binding, sequence: String(sequence), context: INPUT_CONTEXT, fact: { kind: 'key', code: key, edge } }],
    });
    sequence = result.nextInputSequence !== undefined ? BigInt(result.nextInputSequence) : sequence + 1n;
    binding = result.binding ?? binding;
  };
  try {
    await send('pressed');
    await sleep(milliseconds);
    await send('released');
    await sleep(SETTLE_MS);
  } finally {
    await post('/__rusty/product/runtime/control/release', { runtime: binding });
  }
}

async function walk(argv) {
  const { action, seconds, minimum } = options(argv, { action: 'walk-forward', seconds: 2, minimum: 1 });
  const control = JSON.parse(await exec(`playtest.action ${action}`));
  if (!control.available) throw new Error(`${action}: ${control.reason}`);
  const before = await observe();
  await hold(control.key, seconds * 1000);
  const after = await observe();
  const distance = Math.hypot(after.feet.x - before.feet.x, after.feet.z - before.feet.z);
  const keyEvents = after.keyEvents - before.keyEvents;
  const report = { action, key: control.key, seconds, distance: Number(distance.toFixed(3)), keyEvents, before: before.feet, after: after.feet };
  console.log(JSON.stringify(report));
  if (keyEvents < 2) throw new Error(`the product received ${keyEvents} key events, so the movement is not attributable to input`);
  if (distance < minimum) throw new Error(`walked ${distance.toFixed(3)} m, less than --minimum ${minimum}`);
}

async function discovery(argv) {
  const { kind } = options(argv, { kind: 'StandingStones' });
  // The journal looks around every few steps; let it catch up with wherever the player already is.
  await sleep(DISCOVERY_WAIT_MS);
  const before = await exec('craft.discovery.readout');
  const firstVisits = Number(field(before, 'firstVisits'));
  const rows = (await exec(`craft.discovery.find ${kind} 4096`)).split('; ');
  const unvisited = rows.map((row) => row.match(/@(-?\d+),(-?\d+) d=[\d.]+ ground=(-?\d+) known=(\w+)/)).find((match) => match && match[4] === 'none');
  if (!unvisited) throw new Error(`no unvisited ${kind} within reach: ${rows.slice(0, 3).join('; ')}`);
  const [, x, z, ground] = unvisited.map(Number);
  console.log(`standing at the ${kind} at ${x},${z} (ground ${ground})`);
  await exec(`craft.player.teleport ${x + 0.5} ${ground + 3} ${z + 0.5}`);
  await sleep(DISCOVERY_WAIT_MS);
  const after = await exec('craft.discovery.readout');
  console.log(after);
  const places = Number(field(after, 'places'));
  const stored = field(after, 'stored')?.split('/');
  const failures = [];
  if (Number(field(after, 'firstVisits')) !== firstVisits + 1) failures.push(`firstVisits went ${firstVisits} -> ${field(after, 'firstVisits')}, not up by one`);
  if (stored?.[0] !== 'True') failures.push('the journal is not stored');
  else if (Number(stored[1]) !== JOURNAL_HEADER_BYTES + JOURNAL_RECORD_BYTES * places) failures.push(`the stored journal is ${stored[1]} bytes for ${places} places`);
  if (!['absent', 'restored'].includes(field(after, 'restore')) && !field(after, 'restore')?.startsWith('discarded')) failures.push('restore does not say what it did');
  if (failures.length > 0) throw new Error(failures.join('; '));
}

const [command, ...rest] = process.argv.slice(2);
const commands = {
  exec: async (argv) => console.log(await exec(argv.join(' '))),
  walk,
  discovery,
};
try {
  if (!(command in commands)) throw new Error(`usage: node scripts/live.mjs <${Object.keys(commands).join('|')}> [options]`);
  await commands[command](rest);
} catch (error) {
  console.error(error.message);
  process.exit(1);
}
