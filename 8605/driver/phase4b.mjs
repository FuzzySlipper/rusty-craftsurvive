// Phase 4b: the torch again, aimed off the player's own cell.
import { press, frame, capture, read, observe, log, sleep, lookTo, lock, KEY, DIGIT } from './lane.mjs';
await lock();
const o = await observe();
await lookTo(o.yawDegrees + 150, -28); frame();
press([DIGIT(1)]); await sleep(300); press([KEY.R]); await sleep(800); frame();
log(`torch: lights ${(await read('craft.build.entities')).match(/entities=\d+/)?.[0]}, ${(await read('craft.inventory.readout')).match(/torch \d+/)?.[0] ?? 'no torch'}`);
capture('torch-placed');
