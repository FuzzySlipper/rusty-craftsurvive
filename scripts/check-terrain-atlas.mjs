import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const metadata = JSON.parse(await readFile('content/textures/terrain-atlas.json', 'utf8'));
const canonical = await readFile(`content/textures/${metadata.image}`);
const served = await readFile('web/public/assets/terrain-atlas.png');
const hash = `sha256:${createHash('sha256').update(canonical).digest('hex')}`;
const sky = await readFile('content/textures/sky-panorama.png');
const servedSky = await readFile('web/public/assets/sky-panorama.png');
const skyWidth = sky.readUInt32BE(16);
const skyHeight = sky.readUInt32BE(20);

assert.equal(hash, metadata.contentHash, 'terrain atlas hash must match checked metadata');
assert.deepEqual(served, canonical, 'browser-served atlas must match canonical content');
assert.deepEqual(metadata.extent, [128, 128]);
assert.deepEqual(metadata.tileExtent, [64, 64]);
assert.equal(metadata.regions.length, 4);
assert.deepEqual(servedSky, sky, 'browser-served sky must match canonical content');
assert.equal(skyWidth, skyHeight * 2, 'sky panorama must have an exact 2:1 aspect ratio');
assert.equal(sky[24], 8, 'sky panorama must use 8-bit channels');
assert.equal(sky[25], 6, 'sky panorama must be RGBA');
console.log(`CraftSurvive terrain atlas: ${hash}`);
console.log(
  `CraftSurvive sky panorama: sha256:${createHash('sha256').update(sky).digest('hex')} (${skyWidth}x${skyHeight})`,
);
