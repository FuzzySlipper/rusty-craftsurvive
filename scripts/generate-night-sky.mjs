// Authors the night panorama the sky blends to after dusk: a deep blue gradient, scattered stars
// above the horizon and a moon. It matches the day panorama's 2:1 shape so the two crossfade
// without a seam, and is drawn deterministically so it is reproducible from source.
// Run: node scripts/generate-night-sky.mjs
import { deflateSync } from 'node:zlib';
import { writeFileSync } from 'node:fs';

const OUTPUT_PATH = 'content/game/textures/sky-night.png';
const WIDTH = 1024;
const HEIGHT = 512;
const SEED = 0x7a11_57a5;
const STAR_CHANCE = 0.0016;
const MOON = { x: 0.7, y: 0.28, radius: 0.018 };
const ZENITH = [6, 9, 24];
const HORIZON = [26, 36, 62];
const GROUND = [8, 10, 16];

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

function hash(x, y) {
  let h = (SEED ^ Math.imul(x, 0x27d4eb2d) ^ Math.imul(y, 0x165667b1)) >>> 0;
  h = Math.imul(h ^ (h >>> 15), 0x85ebca6b) >>> 0;
  h = Math.imul(h ^ (h >>> 13), 0xc2b2ae35) >>> 0;
  return ((h ^ (h >>> 16)) >>> 0) / 0xffffffff;
}

const mix = (a, b, t) => a.map((value, index) => Math.round(value + (b[index] - value) * t));
const rows = Buffer.alloc((WIDTH * 4 + 1) * HEIGHT);
for (let y = 0; y < HEIGHT; y++) {
  const row = y * (WIDTH * 4 + 1);
  rows[row] = 0;
  const v = y / (HEIGHT - 1);
  for (let x = 0; x < WIDTH; x++) {
    const u = x / WIDTH;
    let rgb = v < 0.5 ? mix(ZENITH, HORIZON, Math.pow(v / 0.5, 2.2)) : mix(HORIZON, GROUND, Math.min(1, (v - 0.5) / 0.08));
    if (v < 0.46 && hash(x, y) < STAR_CHANCE * (1 - v)) {
      const shine = 150 + Math.round(hash(y, x) * 105);
      rgb = [shine, shine, Math.min(255, shine + 20)];
    }
    const du = (u - MOON.x) * 2;
    const dv = v - MOON.y;
    const moon = Math.hypot(du, dv) / MOON.radius;
    if (moon < 1) rgb = [222, 228, 240];
    else if (moon < 3) rgb = mix(rgb, [70, 82, 110], (3 - moon) / 2 * 0.5);
    rows.set([...rgb, 255], row + 1 + x * 4);
  }
}

const header = Buffer.alloc(13);
header.writeUInt32BE(WIDTH, 0);
header.writeUInt32BE(HEIGHT, 4);
header.set([8, 6, 0, 0, 0], 8);
writeFileSync(OUTPUT_PATH, Buffer.concat([
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  chunk('IHDR', header),
  chunk('IDAT', deflateSync(rows, { level: 9 })),
  chunk('IEND', Buffer.alloc(0)),
]));
console.log(`wrote ${OUTPUT_PATH} (${WIDTH}x${HEIGHT})`);
