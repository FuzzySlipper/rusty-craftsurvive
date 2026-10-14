// Original, reproducible audio sketches for the composed town study. No samples
// or borrowed tune: filtered noise, resonant foley and an authored plucked-string
// miniature. These are synthesis trials, not recordings of people or places.
import { mkdirSync, writeFileSync } from 'node:fs';
const DIRECTORY = 'content/game/towns/audio';
const RATE = 24000;
const TAU = Math.PI * 2;
const SEED = 9892;
const PEAK = .68;
function random(label) {
  let state = SEED;
  for (const c of label) state = Math.imul(state ^ c.charCodeAt(0), 16777619) >>> 0;
  return () => { state ^= state << 13; state ^= state >>> 17; state ^= state << 5; return (state >>> 0) / 4294967296; };
}
const samples = seconds => new Float64Array(Math.round(seconds * RATE));
function filter(input, frequency) {
  const output = new Float64Array(input.length), a = 1 - Math.exp(-TAU * frequency / RATE);
  let value = 0;
  for (let i = 0; i < input.length; i++) { value += a * (input[i] - value); output[i] = value; }
  return output;
}
function noise(seconds, rng) { return samples(seconds).map(() => rng() * 2 - 1); }
function add(loop, sound, at, gain = 1) {
  const start = Math.round(at * RATE);
  for (let i = 0; i < sound.length; i++) loop[(start + i) % loop.length] += sound[i] * gain;
}
function resonator(seconds, hz, decay, gain = 1) {
  return samples(seconds).map((_, i) => Math.sin(TAU * hz * i / RATE) * Math.min(1, i / (RATE * .003)) * Math.exp(-i / RATE / decay) * gain);
}
function pluck(midi, seconds, rng) {
  const frequency = 440 * 2 ** ((midi - 69) / 12), period = Math.round(RATE / frequency);
  const ring = filter(noise(period / RATE, rng), 3400), out = samples(seconds);
  for (let i = 0; i < out.length; i++) {
    const at = i % period, next = (at + 1) % period;
    const value = ring[at]; ring[at] = .996 * (.53 * value + .47 * ring[next]);
    out[i] = value * Math.min(1, i / (RATE * .006)) * Math.min(1, (out.length - 1 - i) / (RATE * .15));
  }
  const body = resonator(seconds, frequency, .35, .018);
  return out.map((v, i) => v + body[i]);
}
function water() {
  const length = 24, rng = random('water'), base = filter(noise(length, rng), 1650), low = filter(base, 150);
  const out = base.map((v, i) => (v - low[i]) * (.5 + .1 * Math.sin(TAU * 3 * i / outLength(length))));
  for (let i = 0; i < 65; i++) {
    const hz = 500 + rng() * 650;
    add(out, resonator(.12, hz, .017, .09), rng() * length);
  }
  return out;
}
const outLength = seconds => Math.round(seconds * RATE);
function hearth() {
  const length = 28, rng = random('hearth'), out = filter(noise(length, rng), 450).map(v => v * .11);
  for (let i = 0; i < 82; i++) {
    const pop = filter(noise(.12, rng), 1800 + rng() * 2500).map((v, j) => v * Math.exp(-j / RATE / .013));
    add(out, pop, rng() * length, .04 + rng() * .08);
  }
  return out;
}
function activity() {
  const rng = random('activity'), out = samples(32);
  for (const start of [1.8, 9.1, 19.8, 27.2]) {
    for (let foot = 0; foot < 5; foot++) {
      const step = filter(noise(.16, rng), 700).map((v, i) => v * Math.exp(-i / RATE / .025));
      add(step, resonator(.10, 95 + rng() * 15, .023, .12), 0);
      add(out, step, start + foot * (.42 + rng() * .04), .28);
    }
  }
  for (const at of [5.8, 6.2, 14.3, 24.9]) {
    add(out, resonator(.7, 780, .18, .085), at);
    add(out, resonator(.6, 1273, .10, .034), at);
    add(out, resonator(.3, 2137, .045, .022), at);
  }
  for (const at of [12.5, 12.85, 22.5]) add(out, resonator(.22, 160, .055, .11), at);
  return out;
}
function music() {
  const beat = 60 / 68, bar = beat * 3, rng = random('music'), out = samples(bar * 16);
  // Eight original bars repeated with a different final cadence. Small D-major
  // voicings and gaps keep this below foreground activity; no rhythmic drums.
  const roots = [50, 47, 43, 45, 50, 47, 43, 45, 50, 47, 43, 45, 50, 43, 45, 50];
  const phrases = [[62,66],[64,62],[67,66],[64], [66,69],[67,66],[64,62],[61],
    [62,66],[69,67],[66,64],[64], [66,62],[67,66],[64,61],[62]];
  for (let i = 0; i < roots.length; i++) {
    const at = i * bar;
    add(out, pluck(roots[i], 2.8, rng), at, .31);
    add(out, pluck(roots[i] + 7, 2.2, rng), at + beat, .18);
    for (let n = 0; n < phrases[i].length; n++) add(out, pluck(phrases[i][n], 2.6, rng), at + beat * (.35 + n * 1.3), .62);
  }
  // Short, quiet early reflections, not a conspicuous delay line.
  const dry = out.slice();
  add(out, dry, .083, .13); add(out, dry, .137, .08); add(out, dry, .227, .045);
  return out;
}
function save(name, input) {
  // Remove DC and leave headroom. Tiny endpoint ramp prevents a PCM seam;
  // musical/foley tails are already wrapped into the loop above.
  let mean = 0, peak = 0;
  for (const v of input) mean += v / input.length;
  for (const v of input) peak = Math.max(peak, Math.abs(v - mean));
  const bytes = Buffer.alloc(44 + input.length * 2);
  bytes.write('RIFF'); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVEfmt ', 8);
  bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22);
  bytes.writeUInt32LE(RATE, 24); bytes.writeUInt32LE(RATE * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34);
  bytes.write('data', 36); bytes.writeUInt32LE(input.length * 2, 40);
  for (let i = 0; i < input.length; i++) {
    const ramp = Math.min(1, i / (RATE * .015), (input.length - 1 - i) / (RATE * .015));
    bytes.writeInt16LE(Math.round((input[i] - mean) / peak * PEAK * ramp * 32767), 44 + i * 2);
  }
  writeFileSync(`${DIRECTORY}/${name}.wav`, bytes);
  return { file: `audio/${name}.wav`, seconds: input.length / RATE, sampleRate: RATE, channels: 1,
    source: 'scripts/generate-town-audio.mjs', seed: SEED, authorship: 'Original procedural composition/foley for this project; no external samples or borrowed melody.' };
}
mkdirSync(DIRECTORY, { recursive: true });
const clips = [save('river', water()), save('hearth', hearth()), save('activity', activity()), save('shelter-music', music())];
writeFileSync('content/game/towns/audio-provenance.json', JSON.stringify({
  method: 'Deterministic filtered noise, decaying resonators and plucked-string synthesis; editing the source regenerates the media.',
  purpose: 'Bounded ambience/music trials for town presentation, not final recordings or simulated people.', clips,
}, null, 2) + '\n');
console.log(clips);
