#!/usr/bin/env node
/**
 * Generate the deliberately quiet 32px studies used by StoneworksMaterials.
 *
 * Courses, joints, and silhouettes belong to the terrain recipes. These tiles
 * only carry material value, hue, and a few broad, seamless age marks.
 */
import { deflateSync } from "node:zlib";
import { mkdirSync, writeFileSync, readFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const size = 32;
const output = resolve(dirname(fileURLToPath(import.meta.url)), "../content/textures/stoneworks");

const studies = {
  "stoneworks-limestone.png": { seed: 0x1093a2c1, base: [193, 181, 151], tones: [[201, 190, 162], [185, 173, 145], [197, 184, 154]], marks: 15, radius: 4 },
  "stoneworks-brick.png": { seed: 0x2c7465b9, base: [139, 78, 60], tones: [[151, 88, 66], [126, 69, 55], [144, 80, 61]], marks: 19, radius: 4 },
  "stoneworks-mortar.png": { seed: 0x3d31e8a7, base: [60, 65, 69], tones: [[66, 72, 76], [53, 59, 63], [62, 67, 71]], marks: 17, radius: 3 },
  "stoneworks-plaster.png": { seed: 0x4f92bd15, base: [218, 205, 173], tones: [[226, 214, 184], [207, 194, 163], [221, 208, 178]], marks: 13, radius: 5 },
  "stoneworks-paving.png": { seed: 0x56ab74d3, base: [101, 117, 125], tones: [[111, 127, 135], [91, 106, 115], [104, 120, 128]], marks: 18, radius: 4 },
  "stoneworks-moss.png": { seed: 0x6835ca91, base: [52, 71, 45], tones: [[61, 82, 51], [43, 60, 39], [56, 76, 47]], marks: 20, radius: 4 },
  "stoneworks-timber.png": { seed: 0x7d1c4e63, base: [67, 42, 31], tones: [[80, 51, 35], [55, 34, 27], [72, 45, 32]], marks: 16, radius: 4 },
  "stoneworks-bronze.png": { seed: 0x8ae9f027, base: [80, 96, 82], tones: [[94, 111, 91], [65, 79, 71], [84, 101, 83]], marks: 18, radius: 3 },
};

function mix(value) {
  value ^= value >>> 16;
  value = Math.imul(value, 0x7feb352d) >>> 0;
  value ^= value >>> 15;
  value = Math.imul(value, 0x846ca68b) >>> 0;
  return (value ^ (value >>> 16)) >>> 0;
}

function colourAt(study, x, y) {
  let colour = study.base;
  for (let mark = 0; mark < study.marks; mark++) {
    const value = mix(study.seed ^ Math.imul(mark + 1, 0x9e3779b9));
    const centerX = value & 31;
    const centerY = (value >>> 5) & 31;
    const radiusX = study.radius + ((value >>> 10) & 1);
    const radiusY = Math.max(2, study.radius - 1 + ((value >>> 11) & 1));
    const dx = Math.min(Math.abs(x - centerX), size - Math.abs(x - centerX));
    const dy = Math.min(Math.abs(y - centerY), size - Math.abs(y - centerY));
    if ((dx * dx) / (radiusX * radiusX) + (dy * dy) / (radiusY * radiusY) < 1) {
      colour = study.tones[(value >>> 13) % study.tones.length];
    }
  }

  // A restrained directional cue keeps timber and bronze recognizable while
  // leaving the texture much quieter than a literal plank or plate pattern.
  if (study === studies["stoneworks-timber.png"] && (y + ((x >>> 3) & 1)) % 11 === 0) colour = study.tones[1];
  if (study === studies["stoneworks-bronze.png"] && (x + y * 3) % 17 === 0) colour = study.tones[1];
  return colour;
}

function pngChunk(kind, payload) {
  const chunk = Buffer.alloc(12 + payload.length);
  chunk.writeUInt32BE(payload.length, 0);
  chunk.write(kind, 4, 4, "ascii");
  payload.copy(chunk, 8);
  chunk.writeUInt32BE(crc32(chunk.subarray(4, 8 + payload.length)), 8 + payload.length);
  return chunk;
}

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function encode(study) {
  const raw = Buffer.alloc(size * (1 + size * 4));
  for (let y = 0; y < size; y++) {
    const row = y * (1 + size * 4);
    raw[row] = 0;
    for (let x = 0; x < size; x++) {
      const [red, green, blue] = colourAt(study, x, y);
      const pixel = row + 1 + x * 4;
      raw[pixel] = red;
      raw[pixel + 1] = green;
      raw[pixel + 2] = blue;
      raw[pixel + 3] = 255;
    }
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(size, 0);
  header.writeUInt32BE(size, 4);
  header.set([8, 6, 0, 0, 0], 8);
  return Buffer.concat([Buffer.from("89504e470d0a1a0a", "hex"), pngChunk("IHDR", header), pngChunk("IDAT", deflateSync(raw, { level: 9 })), pngChunk("IEND", Buffer.alloc(0))]);
}

mkdirSync(output, { recursive: true });
for (const [filename, study] of Object.entries(studies)) {
  const target = resolve(output, filename);
  writeFileSync(target, encode(study));
  const hash = createHash("sha256").update(readFileSync(target)).digest("hex");
  console.log(`content/textures/stoneworks/${filename} sha256=${hash}`);
}
