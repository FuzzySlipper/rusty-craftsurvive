import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';

// Audits the canonical terrain atlas against its layout metadata, and the layout
// against itself. The generator writes both in one step; this check is what makes
// a hand edit or a stale metadata file fail instead of reaching the product.
const metadata = JSON.parse(await readFile('content/game/textures/terrain-atlas.json', 'utf8'));
const canonical = await readFile(`content/game/textures/${metadata.image}`);
const hash = `sha256:${createHash('sha256').update(canonical).digest('hex')}`;
const sky = await readFile('content/game/textures/sky-panorama.png');
const skyWidth = sky.readUInt32BE(16);
const skyHeight = sky.readUInt32BE(20);

assert.equal(hash, metadata.contentHash, 'terrain atlas hash must match checked metadata');
assert.equal(metadata.schemaVersion, 2, 'terrain atlas layout schema must be 2');
assert.equal(canonical.readUInt32BE(16), metadata.extent[0], 'atlas width must match the layout extent');
assert.equal(canonical.readUInt32BE(20), metadata.extent[1], 'atlas height must match the layout extent');
assert.equal(canonical[24], 8, 'terrain atlas must use 8-bit channels');
assert.equal(canonical[25], 6, 'terrain atlas must be RGBA');

const [extentX, extentY] = metadata.extent;
const [tileX, tileY] = metadata.tileExtent;
assert.equal(extentX % tileX, 0, 'extent must be a whole number of tiles wide');
assert.equal(extentY % tileY, 0, 'extent must be a whole number of tiles tall');

const ids = new Set();
const positions = new Set();
for (const region of metadata.regions) {
  assert.ok(region.id, 'every region needs an id');
  assert.ok(!ids.has(region.id), `duplicate region id ${region.id}`);
  ids.add(region.id);
  assert.ok(region.block, `region ${region.id} needs a block`);
  assert.ok(['base', 'top'].includes(region.face), `region ${region.id} has an unsupported face`);
  const [x, y] = region.contentMin;
  assert.ok(Number.isInteger(x) && Number.isInteger(y), `region ${region.id} needs integer coordinates`);
  assert.ok(x % tileX === 0 && y % tileY === 0, `region ${region.id} is not tile-aligned`);
  assert.ok(x + tileX <= extentX && y + tileY <= extentY, `region ${region.id} falls outside the atlas`);
  const key = `${x},${y}`;
  assert.ok(!positions.has(key), `two regions share position ${key}`);
  positions.add(key);
}

// Every block in the V1 floor owns a base tile, and grass additionally owns a top
// tile. A block that appears in the product without a tile here would fail at
// product create instead, which is a worse place to learn it.
const bases = new Set(metadata.regions.filter((region) => region.face === 'base').map((region) => region.block));
for (const block of [
  'grass', 'dirt', 'stone', 'sand', 'gravel', 'cobblestone', 'brick',
  'log', 'planks', 'tree-core', 'water', 'glass', 'lamp', 'bedrock', 'snow',
]) {
  assert.ok(bases.has(block), `block ${block} has no base tile in the atlas`);
}
const grassTops = metadata.regions.filter((region) => region.block === 'grass' && region.face === 'top');
assert.equal(grassTops.length, 1, 'grass must own exactly one top tile');
assert.equal(metadata.regions.length, 16, 'the V1 floor is sixteen tiles');

assert.equal(skyWidth, skyHeight * 2, 'sky panorama must have an exact 2:1 aspect ratio');
assert.equal(sky[24], 8, 'sky panorama must use 8-bit channels');
assert.equal(sky[25], 6, 'sky panorama must be RGBA');
console.log(`CraftSurvive canonical terrain atlas: ${hash} (${extentX}x${extentY}, ${metadata.regions.length} tiles)`);
console.log(
  `CraftSurvive sky panorama: sha256:${createHash('sha256').update(sky).digest('hex')} (${skyWidth}x${skyHeight})`,
);
