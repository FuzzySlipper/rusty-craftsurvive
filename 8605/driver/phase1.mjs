// Phase 1: the pack (craft torches by its recipe), a bandage from the hotbar, and the journal.
import { input, press, click, frame, capture, read, log, sleep, KEY, DIGIT } from './lane.mjs';
log('phase 1: pack, bandage, journal');
input([{ kind: 'point', x: 640, y: 200, width: 1280, height: 720, ms: 50 }, { kind: 'click', button: 1, ms: 60 }]);
await sleep(500);
press([KEY.I]); await sleep(700);
capture('pack-open');
click('button[data-recipe="torch"]'); await sleep(800); frame();
log(`after craft: ${(await read('craft.inventory.readout')).slice(0, 140)}`);
capture('pack-crafted-torches');
press([KEY.ESC]); await sleep(400);
input([{ kind: 'point', x: 640, y: 200, width: 1280, height: 720, ms: 50 }, { kind: 'click', button: 1, ms: 60 }]);
await sleep(400);
press([DIGIT(7)]); await sleep(300); press([KEY.R]); await sleep(700); frame();
log(`after bandage: ${(await read('craft.survival.readout')).match(/health=\S+/)?.[0]}`);
press([KEY.M]); await sleep(800);
capture('journal');
press([KEY.ESC]); await sleep(300); frame();
