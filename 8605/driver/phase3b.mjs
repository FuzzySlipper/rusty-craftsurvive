// Phase 3b: back to the way in, and out through the UI.
import { press, click, frame, capture, read, log, sleep, walkTo, lock, menuButton, KEY } from './lane.mjs';
log('phase 3b: leave the dungeon');
await lock();
log(`walk back: ${JSON.stringify(await walkTo(68.5, 52.5, 0.8, 30))}`);
await menuButton('button[title^="Climb out"]'); await sleep(800);
for (let i = 0; i < 40; i++) { if ((await read('craft.dungeon.readout')).includes('state=Outside')) break; await sleep(250); if (i % 3 === 0) frame(); }
click('aside > details > summary'); await sleep(300);
await lock();
frame();
log(`outside: ${(await read('craft.dungeon.readout')).slice(0, 120)}`);
capture('back-outside');
