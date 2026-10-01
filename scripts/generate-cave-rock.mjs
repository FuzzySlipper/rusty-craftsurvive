// Authors the sculpted cave's rock texture: a tileable grey-brown stone with soft mottling and
// darker cracks, drawn deterministically so it is reproducible from source. The sculpted rock mesh
// maps it by world position. Run: node scripts/generate-cave-rock.mjs
import { deflateSync } from 'node:zlib';
import { writeFileSync } from 'node:fs';

const OUTPUT_PATH = 'content/game/textures/cave-rock.png';
const SIZE = 128;
const SEED = 0x0c4f_e70c;
const BASE = [92, 86, 78];
const MOTTLE = 34;
const CRACK = 0.95;

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

function hash(x, y, salt) {
  let h = (SEED ^ salt ^ Math.imul(x, 0x27d4eb2d) ^ Math.imul(y, 0x165667b1)) >>> 0;
  h = Math.imul(h ^ (h >>> 15), 0x85ebca6b) >>> 0;
  h = Math.imul(h ^ (h >>> 13), 0xc2b2ae35) >>> 0;
  return ((h ^ (h >>> 16)) >>> 0) / 0xffffffff;
}

// Tileable value noise: lattice coordinates wrap at the period.
function noise(x, y, period, salt) {
  const x0 = Math.floor(x);
  const y0 = Math.floor(y);
  const fx = x - x0;
  const fy = y - y0;
  const sx = fx * fx * (3 - 2 * fx);
  const sy = fy * fy * (3 - 2 * fy);
  const at = (ix, iy) => hash(((ix % period) + period) % period, ((iy % period) + period) % period, salt);
  const top = at(x0, y0) + (at(x0 + 1, y0) - at(x0, y0)) * sx;
  const bottom = at(x0, y0 + 1) + (at(x0 + 1, y0 + 1) - at(x0, y0 + 1)) * sx;
  return top + (bottom - top) * sy;
}

const rows = Buffer.alloc((SIZE * 4 + 1) * SIZE);
for (let y = 0; y < SIZE; y++) {
  rows[y * (SIZE * 4 + 1)] = 0;
  for (let x = 0; x < SIZE; x++) {
    let value = 0;
    let amplitude = 0.5;
    for (const period of [8, 16, 32]) {
      value += amplitude * noise((x / SIZE) * period, (y / SIZE) * period, period, period);
      amplitude *= 0.5;
    }
    const ridge = 1 - Math.abs(noise((x / SIZE) * 12, (y / SIZE) * 12, 12, 77) * 2 - 1);
    const crack = ridge > CRACK ? 0.78 : 1;
    const shade = (value - 0.44) * 2 * MOTTLE;
    const index = y * (SIZE * 4 + 1) + 1 + x * 4;
    for (let channel = 0; channel < 3; channel++) {
      rows[index + channel] = Math.max(0, Math.min(255, Math.round((BASE[channel] + shade) * crack)));
    }
    rows[index + 3] = 255;
  }
}

const header = Buffer.alloc(13);
header.writeUInt32BE(SIZE, 0);
header.writeUInt32BE(SIZE, 4);
// RGBA: the Engine admits 8-bit RGBA textures.
header.set([8, 6, 0, 0, 0], 8);
writeFileSync(OUTPUT_PATH, Buffer.concat([
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  chunk('IHDR', header),
  chunk('IDAT', deflateSync(rows, { level: 9 })),
  chunk('IEND', Buffer.alloc(0)),
]));
console.log(`wrote ${OUTPUT_PATH} (${SIZE}x${SIZE})`);
