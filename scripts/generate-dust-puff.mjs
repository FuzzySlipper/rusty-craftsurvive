// Authors the blast dust sprite: one soft white puff with a ragged edge, which the particle colour
// curve tints. It is drawn deterministically here, so the texture is reproducible from source
// rather than hand-edited. Run: node scripts/generate-dust-puff.mjs
import { deflateSync } from 'node:zlib';
import { writeFileSync } from 'node:fs';

const OUTPUT_PATH = 'content/game/textures/dust-puff.png';
const SIZE = 64;
const SEED = 0x5eed_d057;
const LOBES = 5;
const LOBE_DEPTH = 0.12;
const GRAIN = 0.12;

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function chunk(type, payload) {
  const out = Buffer.alloc(payload.length + 12);
  out.writeUInt32BE(payload.length, 0);
  out.write(type, 4, 'ascii');
  payload.copy(out, 8);
  out.writeUInt32BE(crc32(out.subarray(4, 8 + payload.length)), 8 + payload.length);
  return out;
}

function encodePng(width, height, rgba) {
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header.set([8, 6, 0, 0, 0], 8);
  const rows = Buffer.alloc((width * 4 + 1) * height);
  for (let y = 0; y < height; y++) {
    rows[y * (width * 4 + 1)] = 0;
    rgba.copy(rows, y * (width * 4 + 1) + 1, y * width * 4, (y + 1) * width * 4);
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(rows, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// A small integer hash, so the grain is the same on every run and machine.
function hash(x, y) {
  let h = (SEED ^ Math.imul(x, 0x27d4eb2d) ^ Math.imul(y, 0x165667b1)) >>> 0;
  h = Math.imul(h ^ (h >>> 15), 0x85ebca6b) >>> 0;
  h = Math.imul(h ^ (h >>> 13), 0xc2b2ae35) >>> 0;
  return ((h ^ (h >>> 16)) >>> 0) / 0xffffffff;
}

const pixels = Buffer.alloc(SIZE * SIZE * 4);
const phases = Array.from({ length: 3 }, (_, index) => hash(index, LOBES) * Math.PI * 2);
for (let y = 0; y < SIZE; y++) {
  for (let x = 0; x < SIZE; x++) {
    const dx = (x + 0.5) / SIZE * 2 - 1;
    const dy = (y + 0.5) / SIZE * 2 - 1;
    const angle = Math.atan2(dy, dx);
    // The rim wobbles in lobes, so a cloud of puffs does not read as a pile of discs.
    const rim = 0.82 - LOBE_DEPTH * 0.5
      + LOBE_DEPTH * 0.5 * (Math.sin(angle * LOBES + phases[0]) * 0.6 + Math.sin(angle * 3 + phases[1]) * 0.4);
    const r = Math.hypot(dx, dy) / rim;
    const falloff = Math.max(0, 1 - r);
    const body = falloff * falloff * (3 - 2 * falloff);
    const grain = 1 - GRAIN + GRAIN * 2 * hash(x, y);
    const alpha = Math.round(Math.min(1, body * grain) * 255);
    const index = (y * SIZE + x) * 4;
    pixels[index] = 255;
    pixels[index + 1] = 255;
    pixels[index + 2] = 255;
    pixels[index + 3] = alpha;
  }
}

writeFileSync(OUTPUT_PATH, encodePng(SIZE, SIZE, pixels));
console.log(`wrote ${OUTPUT_PATH} (${SIZE}x${SIZE})`);
