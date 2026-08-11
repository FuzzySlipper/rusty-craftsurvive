import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const metadata = JSON.parse(await readFile('content/textures/terrain-atlas.json', 'utf8'));
const canonical = await readFile(`content/textures/${metadata.image}`);
const served = await readFile('web/public/assets/terrain-atlas.png');
const hash = `sha256:${createHash('sha256').update(canonical).digest('hex')}`;

assert.equal(hash, metadata.contentHash, 'terrain atlas hash must match checked metadata');
assert.deepEqual(served, canonical, 'browser-served atlas must match canonical content');
assert.deepEqual(metadata.extent, [128, 128]);
assert.deepEqual(metadata.tileExtent, [64, 64]);
assert.equal(metadata.regions.length, 4);
console.log(`CraftSurvive terrain atlas: ${hash}`);
