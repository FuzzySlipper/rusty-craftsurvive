// Authors the game's sounds: short retro effects and four ambience loops, synthesised here from
// square waves, filtered noise and simple envelopes, so every clip is reproducible from source
// rather than hand-edited. Each sound draws from its own seeded noise, so changing one leaves the
// rest byte-identical. Run: node scripts/generate-sounds.mjs
import { mkdirSync, writeFileSync } from 'node:fs';

const OUTPUT_DIRECTORY = 'content/game/audio';
const RATE = 22050;
const SEED = 0x50_0d_5e_ed;

/** Peak level every clip is normalised to, below full scale so mixed sounds keep some headroom. */
const PEAK = 0.85;

/** How much of an ambience loop's end is blended into its start, so the loop has no seam. */
const LOOP_BLEND_SECONDS = 0.5;

/** How long an effect fades out over its last samples, so a sound cut off mid-wave does not click. */
const TAIL_FADE_SECONDS = 0.008;

/** Retro grit: effects are held to this many levels per side before writing. */
const CRUSH_LEVELS = 48;

// --- primitives -----------------------------------------------------------------------------------

function random(name) {
  let state = SEED;
  for (const character of name) state = Math.imul(state ^ character.charCodeAt(0), 0x01000193) >>> 0;
  return () => {
    state = (state + 0x6d2b79f5) >>> 0;
    let t = state;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const samples = (seconds) => new Float64Array(Math.round(seconds * RATE));
const square = (phase, duty = 0.5) => (phase % 1 < duty ? 1 : -1);
const triangle = (phase) => 1 - 4 * Math.abs((phase % 1) - 0.5);
const sine = (phase) => Math.sin(2 * Math.PI * phase);

/** One-pole low-pass, its cutoff given per sample so it can sweep. */
function lowPass(input, cutoff) {
  const out = new Float64Array(input.length);
  let y = 0;
  for (let i = 0; i < input.length; i++) {
    const hz = typeof cutoff === 'function' ? cutoff(i / RATE) : cutoff;
    const a = 1 - Math.exp((-2 * Math.PI * hz) / RATE);
    y += a * (input[i] - y);
    out[i] = y;
  }
  return out;
}

const highPass = (input, cutoff) => {
  const low = lowPass(input, cutoff);
  return input.map((value, i) => value - low[i]);
};

const band = (input, low, high) => lowPass(highPass(input, low), high);

function noise(seconds, next) {
  const out = samples(seconds);
  for (let i = 0; i < out.length; i++) out[i] = next() * 2 - 1;
  return out;
}

/** Noise held for a number of samples at a time: the coarse grain of an old sound chip. */
function heldNoise(seconds, next, hold) {
  const out = samples(seconds);
  let value = 0;
  for (let i = 0; i < out.length; i++) {
    if (i % hold === 0) value = next() * 2 - 1;
    out[i] = value;
  }
  return out;
}

/** A tone whose frequency follows a function of time, through an oscillator shape. */
function tone(seconds, frequency, shape = square) {
  const out = samples(seconds);
  let phase = 0;
  for (let i = 0; i < out.length; i++) {
    const t = i / RATE;
    out[i] = shape(phase);
    phase += (typeof frequency === 'function' ? frequency(t) : frequency) / RATE;
  }
  return out;
}

/** Shapes a sound by an envelope of time: a quick attack, then an exponential decay. */
function envelope(input, attack, decay) {
  return input.map((value, i) => {
    const t = i / RATE;
    const rise = attack > 0 ? Math.min(1, t / attack) : 1;
    return value * rise * Math.exp(-Math.max(0, t - attack) / decay);
  });
}

function mix(length, ...parts) {
  const out = samples(length);
  for (const [part, gain = 1, offset = 0] of parts) {
    const start = Math.round(offset * RATE);
    for (let i = 0; i < part.length && start + i < out.length; i++) out[start + i] += part[i] * gain;
  }
  return out;
}

const glide = (from, to, seconds) => (t) => from * Math.pow(to / from, Math.min(1, t / seconds));

/** Notes one after another, each a short decaying square blip. */
function arpeggio(notes, step, tail, duty = 0.25) {
  const length = step * (notes.length - 1) + tail;
  return mix(length, ...notes.map((hz, index) => [envelope(tone(tail, hz, (p) => square(p, duty)), 0.002, tail / 4), 1, index * step]));
}

// --- the effects ------------------------------------------------------------------------------------

const EFFECTS = {
  // Footfalls: a soft scuff of low noise over a dull knock; three so a walk does not machine-gun.
  ...Object.fromEntries([1, 2, 3].map((variant) => [`step-${variant}`, (next) => mix(0.12,
    [envelope(band(noise(0.12, next), 120, 700 + variant * 180), 0.003, 0.022), 0.9],
    [envelope(tone(0.08, 70 + variant * 12, triangle), 0.002, 0.02), 0.4]),
  ])),
  jump: () => envelope(tone(0.16, glide(180, 420, 0.12), (p) => square(p, 0.25)), 0.004, 0.06),
  land: (next) => mix(0.18,
    [envelope(lowPass(noise(0.18, next), 380), 0.002, 0.04), 1],
    [envelope(tone(0.15, glide(110, 45, 0.15), triangle), 0.002, 0.05), 0.7]),
  'land-hard': (next) => mix(0.4,
    [envelope(lowPass(heldNoise(0.4, next, 3), glide(1400, 200, 0.3)), 0.002, 0.09), 1],
    [envelope(tone(0.35, glide(90, 35, 0.3), triangle), 0.002, 0.12), 0.9]),
  splash: (next) => {
    const wash = envelope(band(noise(0.6, next), 300, 1), 0.01, 0.16);
    const swept = lowPass(wash, glide(3200, 500, 0.5));
    const bubbles = [0.08, 0.15, 0.23, 0.3, 0.41].map((at) => [
      envelope(tone(0.05, glide(500 + next() * 600, 1400 + next() * 600, 0.05), triangle), 0.002, 0.015), 0.25, at]);
    return mix(0.6, [swept, 1.4], ...bubbles);
  },
  grip: (next) => envelope(band(heldNoise(0.12, next, 2), 1500, 5000), 0.004, 0.03),
  hurt: (next) => mix(0.24,
    [envelope(tone(0.24, glide(560, 160, 0.2), (p) => square(p, 0.4)), 0.002, 0.08), 0.8],
    [envelope(lowPass(noise(0.1, next), 1500), 0.001, 0.025), 0.6]),
  swing: (next) => {
    const air = band(noise(0.22, next), 400, 1);
    const sweep = lowPass(air, (t) => 600 + 3400 * Math.sin(Math.PI * Math.min(1, t / 0.2)));
    return envelope(sweep, 0.06, 0.05);
  },
  strike: (next) => mix(0.16,
    [envelope(lowPass(heldNoise(0.16, next, 2), 2400), 0.001, 0.03), 1],
    [envelope(tone(0.12, glide(200, 90, 0.1), (p) => square(p, 0.3)), 0.001, 0.04), 0.6]),
  defeat: (next) => mix(0.6,
    [arpeggio([392, 330, 262, 196], 0.08, 0.3, 0.5), 0.7],
    [envelope(lowPass(noise(0.3, next), 900), 0.002, 0.08), 0.5]),
  blast: (next) => mix(1.6,
    [envelope(lowPass(heldNoise(1.6, next, 2), glide(4000, 120, 1.2)), 0.004, 0.35), 1.3],
    [envelope(tone(1.2, glide(70, 28, 1.0), triangle), 0.002, 0.3), 0.9]),
  place: (next) => mix(0.12,
    [envelope(tone(0.03, 1100, (p) => square(p, 0.5)), 0.0005, 0.006), 0.4],
    [envelope(lowPass(noise(0.1, next), 1800), 0.0005, 0.012), 0.6],
    [envelope(tone(0.1, glide(220, 130, 0.08), triangle), 0.001, 0.03), 0.8]),
  craft: () => mix(0.3,
    [envelope(tone(0.1, 660, (p) => square(p, 0.25)), 0.002, 0.035), 0.8],
    [envelope(tone(0.16, 990, (p) => square(p, 0.25)), 0.002, 0.05), 0.8, 0.09]),
  pickup: () => arpeggio([523, 659, 784, 1047], 0.045, 0.14),
  discovery: () => {
    const notes = [523, 659, 784, 1047];
    return mix(1.4, ...notes.map((hz, index) => [envelope(tone(1.1, hz, triangle), 0.004, 0.35), 0.7, index * 0.12]));
  },
  refused: () => mix(0.3,
    [envelope(tone(0.1, 110, (p) => square(p, 0.5)), 0.002, 0.06), 0.6],
    [envelope(tone(0.12, 98, (p) => square(p, 0.5)), 0.002, 0.07), 0.6, 0.14]),
  portal: (next) => {
    const swell = band(noise(1.2, next), 60, 1);
    const rumble = lowPass(swell, (t) => 200 + 900 * Math.sin(Math.PI * Math.min(1, t / 1.1)));
    const shaped = rumble.map((value, i) => value * Math.sin(Math.PI * Math.min(1, i / RATE / 1.2)));
    return mix(1.2, [shaped, 2.2], [envelope(tone(1.0, glide(330, 82, 0.9), triangle), 0.08, 0.4), 0.3]);
  },
};

// --- the ambience loops -----------------------------------------------------------------------------

/** A slow wobble that repeats a whole number of times in the loop, so the loop's ends meet. */
const wobble = (length, cycles, phase = 0) => (t) => Math.sin(2 * Math.PI * (cycles * t / length + phase));

const AMBIENCE = {
  // Open air: wind gusting through a moving low-pass.
  'amb-wind': (next, length, total) => {
    const gust = wobble(length, 2, 0.1);
    const flutter = wobble(length, 7, 0.4);
    const air = lowPass(noise(total, next), (t) => 380 + 260 * gust(t) + 90 * flutter(t));
    return air.map((value, i) => value * (0.75 + 0.25 * gust(i / RATE)));
  },
  // Night: a quieter wind under crickets, each chirp a short trill of a high tone.
  'amb-night': (next, length, total) => {
    const air = lowPass(noise(total, next), 260).map((value) => value * 0.5);
    const chirps = [];
    for (let at = 0.2; at < length - 0.5; at += 0.55 + next() * 0.9) {
      const pitch = 3900 + next() * 500;
      const trill = tone(0.16, pitch, sine).map((value, i) => value * (Math.sin(2 * Math.PI * 38 * i / RATE) > 0 ? 1 : 0));
      chirps.push([envelope(trill, 0.005, 0.07), 0.12, at]);
    }
    return mix(total, [air, 1], ...chirps);
  },
  // Underground: a low drone that beats slowly, a rumble of dark noise, and drips in the distance.
  'amb-cave': (next, length, total) => {
    const drone = tone(total, 55, sine).map((value, i) => value + 0.6 * Math.sin(2 * Math.PI * (55 * 1.5 + 0.25) * i / RATE));
    const rumble = lowPass(lowPass(noise(total, next), 140), 140);
    const drips = [];
    for (let at = 0.4; at < length - 0.6; at += 1.1 + next() * 1.8) {
      const pitch = 900 + next() * 700;
      const drip = envelope(tone(0.12, glide(pitch, pitch * 0.55, 0.06), sine), 0.001, 0.03);
      drips.push([drip, 0.35, at], [drip, 0.12, at + 0.19], [drip, 0.05, at + 0.38]);
    }
    return mix(total, [drone, 0.12], [rumble, 3.0], ...drips);
  },
  // Under water: a heavy, muffled rumble that swells, with bubbles now and then.
  'amb-water': (next, length, total) => {
    const swell = wobble(length, 3, 0.2);
    const body = lowPass(lowPass(noise(total, next), (t) => 220 + 80 * swell(t)), 300);
    const bubbles = [];
    for (let at = 0.3; at < length - 0.3; at += 0.4 + next() * 1.2) {
      bubbles.push([envelope(tone(0.06, glide(300 + next() * 300, 900 + next() * 400, 0.06), triangle), 0.002, 0.02), 0.08, at]);
    }
    return mix(total, [body.map((value, i) => value * (0.8 + 0.2 * swell(i / RATE))), 3.5], ...bubbles);
  },
};

/** How long each ambience loop runs before it repeats. */
const LOOP_SECONDS = { 'amb-wind': 8, 'amb-night': 8, 'amb-cave': 10, 'amb-water': 6 };

// --- writing -----------------------------------------------------------------------------------------

function normalise(input) {
  let peak = 0;
  for (const value of input) peak = Math.max(peak, Math.abs(value));
  return peak === 0 ? input : input.map((value) => (value / peak) * PEAK);
}

function fadeTail(input) {
  const fade = Math.round(TAIL_FADE_SECONDS * RATE);
  return input.map((value, i) => value * Math.min(1, (input.length - 1 - i) / fade));
}

const crush = (input) => input.map((value) => Math.round(value * CRUSH_LEVELS) / CRUSH_LEVELS);

/** Folds the extra tail generated past the loop's end back over its start, so it repeats without a seam. */
function seamless(generated, length) {
  const loop = generated.slice(0, Math.round(length * RATE));
  const blend = Math.round(LOOP_BLEND_SECONDS * RATE);
  for (let i = 0; i < blend; i++) {
    const weight = i / blend;
    loop[i] = loop[i] * weight + generated[loop.length + i] * (1 - weight);
  }
  return loop;
}

function wav(input) {
  const data = Buffer.alloc(input.length * 2);
  input.forEach((value, i) => data.writeInt16LE(Math.max(-32768, Math.min(32767, Math.round(value * 32767))), i * 2));
  const header = Buffer.alloc(44);
  header.write('RIFF', 0, 'ascii');
  header.writeUInt32LE(36 + data.length, 4);
  header.write('WAVE', 8, 'ascii');
  header.write('fmt ', 12, 'ascii');
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20);
  header.writeUInt16LE(1, 22);
  header.writeUInt32LE(RATE, 24);
  header.writeUInt32LE(RATE * 2, 28);
  header.writeUInt16LE(2, 32);
  header.writeUInt16LE(16, 34);
  header.write('data', 36, 'ascii');
  header.writeUInt32LE(data.length, 40);
  return Buffer.concat([header, data]);
}

mkdirSync(OUTPUT_DIRECTORY, { recursive: true });
for (const [name, make] of Object.entries(EFFECTS)) {
  writeFileSync(`${OUTPUT_DIRECTORY}/${name}.wav`, wav(fadeTail(crush(normalise(make(random(name)))))));
}

for (const [name, make] of Object.entries(AMBIENCE)) {
  const length = LOOP_SECONDS[name];
  // Generated past the loop's end by the blend, with the wobbles still timed to the loop's length.
  const generated = make(random(name), length, length + LOOP_BLEND_SECONDS);
  writeFileSync(`${OUTPUT_DIRECTORY}/${name}.wav`, wav(normalise(seamless(generated, length))));
}
