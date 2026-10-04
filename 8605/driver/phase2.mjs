// Phase 2: walk to a hostile creature and fight it with the attack key; its drops come into the pack.
import { input, press, frame, capture, read, observe, log, sleep, lookTo, bearing, walkTo, lock, KEY } from './lane.mjs';
log('phase 2: fight');
await lock();
const creatures = async () => [...(await read('craft.creatures.readout')).matchAll(/id=(\d+) kind=(\S+) at=(-?[\d.]+),(-?[\d.]+) awake=(\w+) route=\S+ state=(\w+) hp=(\d+)\/(\d+)/g)]
  .map((m) => ({ id: m[1], kind: m[2], x: Number(m[3]), z: Number(m[4]), awake: m[5], state: m[6], hp: Number(m[7]) }));
let target = (await creatures()).find((c) => c.kind === 'hostile-walker');
log(`target ${JSON.stringify(target)}`);
const before = (await read('craft.inventory.readout')).slice(0, 120);
for (let round = 0; round < 40 && target; round++) {
  const o = await observe();
  const d = Math.hypot(target.x - o.feet.x, target.z - o.feet.z);
  if (d > 3) {
    await walkTo(target.x, target.z, 3, 4);
  } else {
    await lookTo(bearing(o.feet.x, o.feet.z, target.x, target.z), -15);
    press([KEY.J]); await sleep(250); frame();
  }
  if (round === 6) capture('fight-closing');
  const now = (await creatures()).find((c) => c.id === target.id);
  if (!now) { log(`creature ${target.id} is gone: defeated`); break; }
  target = now;
  if (round % 5 === 0) log(`round ${round}: d=${d.toFixed(1)} creature ${now.state} hp=${now.hp} player=${(await observe()).health}`);
}
await sleep(800); frame();
capture('after-fight');
log(`creatures: ${(await read('craft.creatures.readout')).match(/defeated=\d+ items=\d+ experience=\d+ level=\d+ player=\S+ defeats=\d+/)?.[0]}`);
log(`inventory before: ${before}`);
log(`inventory after:  ${(await read('craft.inventory.readout')).slice(0, 120)}`);
