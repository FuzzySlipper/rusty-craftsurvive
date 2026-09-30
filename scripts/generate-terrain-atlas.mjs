// Authors the canonical terrain atlas and its layout metadata.
//
// The four original tiles (grass-top, grass-side, dirt, stone) are copied
// pixel-for-pixel from the checked-in atlas so the world's existing appearance
// does not change. The remaining tiles of the V1 block floor are drawn
// deterministically here, which keeps the atlas reproducible from source rather
// than hand-edited. Run: node scripts/generate-terrain-atlas.mjs
import { deflateSync, inflateSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';

const ATLAS_PATH = 'content/game/textures/terrain-atlas.png';
const METADATA_PATH = 'content/game/textures/terrain-atlas.json';
const TILE = 64;
const GRID = 4;
const EXTENT = TILE * GRID;

// ---------------------------------------------------------------- PNG codec
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

function decodePng(bytes) {
  let offset = 8;
  let width = 0;
  let height = 0;
  let colorType = 6;
  const idat = [];
  while (offset < bytes.length) {
    const length = bytes.readUInt32BE(offset);
    const type = bytes.toString('ascii', offset + 4, offset + 8);
    const payload = bytes.subarray(offset + 8, offset + 8 + length);
    if (type === 'IHDR') {
      width = payload.readUInt32BE(0);
      height = payload.readUInt32BE(4);
      if (payload[8] !== 8) throw new Error('only 8-bit PNGs are supported');
      colorType = payload[9];
      if (colorType !== 6 && colorType !== 2) throw new Error(`unsupported color type ${colorType}`);
    } else if (type === 'IDAT') {
      idat.push(payload);
    } else if (type === 'IEND') {
      break;
    }
    offset += length + 12;
  }

  const channels = colorType === 6 ? 4 : 3;
  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  const pixels = Buffer.alloc(width * height * 4);
  let previous = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = Buffer.from(raw.subarray(y * (stride + 1) + 1, y * (stride + 1) + 1 + stride));
    for (let i = 0; i < stride; i++) {
      const a = i >= channels ? line[i - channels] : 0;
      const b = previous[i];
      const c = i >= channels ? previous[i - channels] : 0;
      if (filter === 1) line[i] = (line[i] + a) & 0xff;
      else if (filter === 2) line[i] = (line[i] + b) & 0xff;
      else if (filter === 3) line[i] = (line[i] + ((a + b) >> 1)) & 0xff;
      else if (filter === 4) {
        const p = a + b - c;
        const pa = Math.abs(p - a);
        const pb = Math.abs(p - b);
        const pc = Math.abs(p - c);
        line[i] = (line[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c)) & 0xff;
      }
    }
    for (let x = 0; x < width; x++) {
      const source = x * channels;
      const target = (y * width + x) * 4;
      pixels[target] = line[source];
      pixels[target + 1] = line[source + 1];
      pixels[target + 2] = line[source + 2];
      pixels[target + 3] = channels === 4 ? line[source + 3] : 255;
    }
    previous = line;
  }
  return { width, height, pixels };
}

function encodePng(width, height, pixels) {
  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (stride + 1)] = 0;
    pixels.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(width, 0);
  header.writeUInt32BE(height, 4);
  header[8] = 8;
  header[9] = 6;
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// ----------------------------------------------------------- tile painting
function hash(value) {
  let state = 0x811c9dc5;
  for (const byte of Buffer.from(value)) {
    state ^= byte;
    state = Math.imul(state, 0x01000193) >>> 0;
  }
  return state;
}

function noise(seed, x, y) {
  let state = (seed ^ Math.imul(x + 0x9e3779b9, 0x85ebca6b) ^ Math.imul(y + 0x165667b1, 0xc2b2ae35)) >>> 0;
  state ^= state >>> 15;
  state = Math.imul(state, 0x2545f491) >>> 0;
  state ^= state >>> 13;
  return (state >>> 8) / 0xffffff;
}

function shade(color, amount) {
  const clamp = (value) => Math.max(0, Math.min(255, Math.round(value)));
  return [clamp(color[0] * amount), clamp(color[1] * amount), clamp(color[2] * amount), color[3]];
}

// Each painter returns RGBA for one tile pixel. Deterministic by construction:
// the only entropy is the tile-name hash and integer pixel coordinates.
const painters = {
  sand: (x, y) => shade([223, 210, 160, 255], 0.94 + noise(hash('sand'), x, y) * 0.12),
  gravel: (x, y) => shade([138, 138, 134, 255], 0.8 + noise(hash('gravel'), x >> 1, y >> 1) * 0.4),
  cobblestone: (x, y) => {
    const cell = 16;
    const mortar = x % cell < 2 || y % cell < 2 || (x + y) % (cell * 2) < 2;
    return shade([125, 125, 122, 255], mortar ? 0.66 : 0.92 + noise(hash('cobble'), x, y) * 0.22);
  },
  brick: (x, y) => {
    const row = Math.floor(y / 16);
    const offset = row % 2 === 0 ? 0 : 16;
    const mortar = y % 16 < 3 || (x + offset) % 32 < 3;
    return shade([156, 90, 74, 255], mortar ? 1.28 : 0.88 + noise(hash('brick'), x, y) * 0.20);
  },
  log: (x, y) => {
    const striation = Math.abs(Math.sin((x / TILE) * Math.PI * 7)) * 0.16;
    return shade([107, 79, 47, 255], 0.86 + striation + noise(hash('log'), x >> 2, y) * 0.12);
  },
  planks: (x, y) => {
    const seam = y % 16 < 2 ? 0.72 : 1;
    return shade([185, 138, 78, 255], seam * (0.92 + noise(hash('planks'), x, y >> 2) * 0.14));
  },
  leaves: (x, y) => {
    const sample = noise(hash('leaves'), x, y);
    if (sample < 0.18) return [0, 0, 0, 0];
    return shade([74, 122, 53, 255], 0.78 + sample * 0.44);
  },
  water: (x, y) => {
    const wave = Math.sin(((x + y * 0.6) / TILE) * Math.PI * 4) * 0.08;
    return [47 + wave * 90, 111 + wave * 90, 191 + wave * 70, 150];
  },
  glass: (x, y) => {
    const border = x < 3 || y < 3 || x >= TILE - 3 || y >= TILE - 3;
    return border ? [214, 236, 244, 140] : [206, 230, 240, 48];
  },
  lamp: (x, y) => {
    const dx = (x - TILE / 2) / (TILE / 2);
    const dy = (y - TILE / 2) / (TILE / 2);
    const centre = Math.max(0, 1 - Math.sqrt(dx * dx + dy * dy));
    return shade([255, 217, 138, 255], 0.78 + centre * 0.5 + noise(hash('lamp'), x, y) * 0.06);
  },
  bedrock: (x, y) => shade([74, 74, 78, 255], 0.7 + noise(hash('bedrock'), x >> 1, y >> 1) * 0.55),
  snow: (x, y) => shade([242, 246, 250, 255], 0.97 + noise(hash('snow'), x >> 1, y) * 0.05),
};

// ------------------------------------------------------------- composition
// Original tiles keep their exact coordinates; new tiles fill the rest of the
// 4x4 grid. The order here is the layout of record written to the metadata.
const layout = [
  { id: 'grass-top', block: 'grass', face: 'top', source: [0, 0] },
  { id: 'grass-side', block: 'grass', face: 'base', source: [TILE, 0] },
  { id: 'dirt', block: 'dirt', face: 'base', source: [0, TILE] },
  { id: 'stone', block: 'stone', face: 'base', source: [TILE, TILE] },
  { id: 'sand', block: 'sand', face: 'base', at: [TILE * 2, 0] },
  { id: 'gravel', block: 'gravel', face: 'base', at: [TILE * 3, 0] },
  { id: 'cobblestone', block: 'cobblestone', face: 'base', at: [TILE * 2, TILE] },
  { id: 'brick', block: 'brick', face: 'base', at: [TILE * 3, TILE] },
  { id: 'log', block: 'log', face: 'base', at: [0, TILE * 2] },
  { id: 'planks', block: 'planks', face: 'base', at: [TILE, TILE * 2] },
  { id: 'leaves', block: 'leaves', face: 'base', at: [TILE * 2, TILE * 2] },
  { id: 'water', block: 'water', face: 'base', at: [TILE * 3, TILE * 2] },
  { id: 'glass', block: 'glass', face: 'base', at: [0, TILE * 3] },
  { id: 'lamp', block: 'lamp', face: 'base', at: [TILE, TILE * 3] },
  { id: 'bedrock', block: 'bedrock', face: 'base', at: [TILE * 2, TILE * 3] },
  { id: 'snow', block: 'snow', face: 'base', at: [TILE * 3, TILE * 3] },
];

const previous = decodePng(readFileSync(ATLAS_PATH));
if (previous.width !== 128 || previous.height !== 128) {
  throw new Error(`expected the checked-in atlas to be 128x128, found ${previous.width}x${previous.height}`);
}

const canvas = Buffer.alloc(EXTENT * EXTENT * 4);
const regions = [];
for (const entry of layout) {
  const [x0, y0] = entry.source ?? entry.at;
  if (entry.source) {
    for (let y = 0; y < TILE; y++) {
      const from = ((y0 + y) * previous.width + x0) * 4;
      const to = ((y0 + y) * EXTENT + x0) * 4;
      previous.pixels.copy(canvas, to, from, from + TILE * 4);
    }
  } else {
    const painter = painters[entry.id];
    if (!painter) throw new Error(`no painter for tile ${entry.id}`);
    for (let y = 0; y < TILE; y++) {
      for (let x = 0; x < TILE; x++) {
        const colour = painter(x, y);
        const to = ((y0 + y) * EXTENT + x0 + x) * 4;
        canvas[to] = colour[0];
        canvas[to + 1] = colour[1];
        canvas[to + 2] = colour[2];
        canvas[to + 3] = colour[3];
      }
    }
  }
  regions.push({
    id: entry.id,
    block: entry.block,
    face: entry.face,
    contentMin: [x0, y0],
    ...(entry.source
      ? { source: `source/${entry.id}-gpt.png` }
      : { authored: 'scripts/generate-terrain-atlas.mjs' }),
  });
}

const image = encodePng(EXTENT, EXTENT, canvas);
writeFileSync(ATLAS_PATH, image);
const metadata = {
  schemaVersion: 2,
  image: 'terrain-atlas.png',
  contentHash: `sha256:${createHash('sha256').update(image).digest('hex')}`,
  extent: [EXTENT, EXTENT],
  tileExtent: [TILE, TILE],
  filter: 'nearest',
  wrap: 'clamp',
  inset: 'halfTexel',
  regions,
};
writeFileSync(METADATA_PATH, `${JSON.stringify(metadata, null, 2)}\n`);
console.log(`wrote ${ATLAS_PATH} (${EXTENT}x${EXTENT}, ${regions.length} regions) ${metadata.contentHash}`);
