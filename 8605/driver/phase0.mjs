// Phase 0: take focus without touching the world, then eat a ration from the hotbar.
import { input, press, frame, capture, observe, read, log, lookTo, sleep, KEY, DIGIT } from './lane.mjs';
log('phase 0: focus and eat');
input([{ kind: 'gamepad', ry: 1, ms: 700 }]); // tilt the view up at the sky before the first click
await sleep(200);
log(`after stick: ${JSON.stringify(await observe()).slice(0, 160)}`);
input([{ kind: 'point', x: 640, y: 300, width: 1280, height: 720, ms: 50 }, { kind: 'click', button: 1, ms: 60 }]);
await sleep(400);
await lookTo(0, -10);
capture('start');
log(`start: ${await read('craft.survival.readout')}`);
press([DIGIT(6)]); await sleep(300); frame();
press([KEY.R]); await sleep(600); frame();
log(`after R: ${await read('craft.survival.readout')}`);
capture('ate-from-hotbar');
