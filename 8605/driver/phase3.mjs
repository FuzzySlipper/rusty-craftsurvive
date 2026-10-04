// Phase 3: heal from the hotbar, walk to a dungeon entrance, go in through the UI, look around, come out.
import { input, press, click, frame, capture, read, observe, log, sleep, lookTo, walkTo, lock, KEY, DIGIT } from './lane.mjs';
log('phase 3: dungeon');
await lock();
const bandageSlot = (await read('craft.inventory.readout')) && 7;
// (healed already in the first run of this phase)
log(`healed: ${(await observe()).health}`);
const entrance = { x: 5.5, z: 240.5 };
const walked = await walkTo(entrance.x, entrance.z, 1.5, 90);
log(`walk to entrance: ${JSON.stringify(walked)} dungeon: ${(await read('craft.dungeon.readout')).slice(0, 160)}`);
capture('at-entrance');
// The Menu drawer holds Enter; open it, press Enter, close it.
press([KEY.I]); await sleep(400); press([KEY.ESC]); await sleep(400);
click('aside > details > summary'); await sleep(300);
click('button[title^="Go down"]'); await sleep(250);
for (let i = 0; i < 6; i++) { frame(); await sleep(150); }
capture('loading');
for (let i = 0; i < 120; i++) { if (!(await read('craft.dungeon.readout')).includes('state=Loading')) break; await sleep(250); if (i % 4 === 0) frame(); }
log(`inside: ${(await read('craft.dungeon.readout')).slice(0, 200)}`);
click('aside > details > summary'); await sleep(300);
await lock();
await lookTo(90, -5); frame();
capture('inside-dungeon');
await lookTo(270, -5); frame();
input([{ kind: 'hold', keys: [KEY.W], ms: 1500 }]); frame();
capture('inside-dungeon-walked');
// Back out the way in.
press([KEY.I]); await sleep(400); press([KEY.ESC]); await sleep(400);
click('aside > details > summary'); await sleep(300);
click('button[title^="Climb out"]'); await sleep(800);
for (let i = 0; i < 40; i++) { if ((await read('craft.dungeon.readout')).includes('state=Outside')) break; await sleep(250); }
click('aside > details > summary'); await sleep(300);
await lock();
frame();
log(`outside: ${(await read('craft.dungeon.readout')).slice(0, 160)}`);
capture('back-outside');
