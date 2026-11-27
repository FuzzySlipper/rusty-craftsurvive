import type { GoldenManifest, GoldenWorld } from '../types';

/** The continent raster's layers, decoded. Row 0 is the north edge (z = -radius); columns run east. */
export interface ContinentRaster {
  pixels: number;
  radius: number;
  metresPerPixel: number;
  elevation: Float32Array;
  temperature: Float32Array;
  moisture: Float32Array;
  rock: Float32Array;
  biome: Uint8Array;
  water: Uint8Array;
}

export type BaseMode = 'relief' | 'elevation' | 'biome' | 'temperature' | 'moisture' | 'rock' | 'water';

export const BASE_MODES: Array<{ id: BaseMode; label: string; about: string }> = [
  { id: 'relief', label: 'Relief', about: 'Biome colours greyed toward stone where rock is exposed, sea and fresh water, under a north-west hillshade.' },
  { id: 'elevation', label: 'Elevation', about: 'Height above the water level, sea by depth.' },
  { id: 'biome', label: 'Biomes', about: 'The map biome each pixel classifies as, in the world map\'s colours.' },
  { id: 'temperature', label: 'Temperature', about: 'The map\'s temperature field (relative).' },
  { id: 'moisture', label: 'Moisture', about: 'The map\'s moisture field (relative).' },
  { id: 'rock', label: 'Exposed rock', about: 'How much rock the ground exposes, 0 to 1.' },
  { id: 'water', label: 'Water', about: 'Sea, lake and river pixels as the map samples them.' },
];

const FALLBACK_BIOME = '#6f6a62';
const FALLBACK_WATER = { stone: '#6f6a62', river: '#3f7378', lake: '#335f74', sea: '#2c5560' };
/** Hillshade: light from the north-west, heights exaggerated so continental relief reads at a few hundred metres a pixel. */
const LIGHT = normalise([-1, 1.4, -1]);
const RELIEF_EXAGGERATION = 6;
const WALKING_EXAGGERATION = 2;
const SHADE_FLOOR = 0.45;
const SHADE_RANGE = 0.75;

function normalise(v: number[]): number[] {
  const length = Math.hypot(...v);
  return v.map(value => value / length);
}

export function hexRgb(hex: string): [number, number, number] {
  const n = parseInt(hex.replace('#', ''), 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function mix(a: [number, number, number], b: [number, number, number], t: number): [number, number, number] {
  const u = Math.max(0, Math.min(1, t));
  return [a[0] + (b[0] - a[0]) * u, a[1] + (b[1] - a[1]) * u, a[2] + (b[2] - a[2]) * u];
}

/** A ramp through colour stops at 0..1. */
function ramp(stops: string[]): (t: number) => [number, number, number] {
  const colours = stops.map(hexRgb);
  return t => {
    const x = Math.max(0, Math.min(0.9999, t)) * (colours.length - 1);
    const i = Math.floor(x);
    return mix(colours[i], colours[i + 1], x - i);
  };
}

export const RAMPS = {
  elevation: ramp(['#3f6b3c', '#8a9a55', '#c9b77a', '#9c7b5b', '#e8e4dc']),
  depth: ramp(['#3b6683', '#14283a']),
  temperature: ramp(['#3b4cc0', '#8db0fe', '#dddddd', '#f49a7b', '#b40426']),
  moisture: ramp(['#c9a66b', '#d9d59c', '#6fae7a', '#2c7f8a', '#1d3f6e']),
  rock: ramp(['#2b332c', '#6f6a62', '#d8d2c4']),
};

export async function loadContinent(manifest: GoldenManifest, mediaBase: string): Promise<ContinentRaster> {
  const { pixels } = manifest.continent;
  const read = async (name: string) => (await fetch(`${mediaBase}continent/${name}`)).arrayBuffer();
  const [elevation, temperature, moisture, rock, biome, water] = await Promise.all([
    read('elevation.f32'),
    read('temperature.f32'),
    read('moisture.f32'),
    read('rock.f32'),
    read('biome.u8'),
    read('water.u8'),
  ]);
  return {
    pixels,
    radius: manifest.world.radius,
    metresPerPixel: manifest.continent.metresPerPixel,
    elevation: new Float32Array(elevation),
    temperature: new Float32Array(temperature),
    moisture: new Float32Array(moisture),
    rock: new Float32Array(rock),
    biome: new Uint8Array(biome),
    water: new Uint8Array(water),
  };
}

function range(values: Float32Array, include: (i: number) => boolean): [number, number] {
  let low = Infinity;
  let high = -Infinity;
  for (let i = 0; i < values.length; i++) {
    if (!include(i)) continue;
    const v = values[i];
    if (v < low) low = v;
    if (v > high) high = v;
  }
  return low < high ? [low, high] : [0, 1];
}

/** Shade of a unit-spaced height grid at (x, y): the normal against the light. */
function shade(heights: ArrayLike<number>, side: number, x: number, y: number, spacing: number, exaggeration: number): number {
  const at = (cx: number, cy: number) => heights[Math.min(side - 1, Math.max(0, cy)) * side + Math.min(side - 1, Math.max(0, cx))];
  const dx = ((at(x + 1, y) - at(x - 1, y)) * exaggeration) / (2 * spacing);
  const dz = ((at(x, y + 1) - at(x, y - 1)) * exaggeration) / (2 * spacing);
  const normal = normalise([-dx, 1, -dz]);
  const lit = normal[0] * LIGHT[0] + normal[1] * LIGHT[1] + normal[2] * LIGHT[2];
  return SHADE_FLOOR + SHADE_RANGE * Math.max(0, lit);
}

export interface Legend {
  kind: 'swatches' | 'ramp';
  items?: Array<{ label: string; colour: string }>;
  stops?: string[];
  low?: string;
  high?: string;
}

/** The base image for a mode, one canvas pixel per raster pixel. */
export function paintContinent(raster: ContinentRaster, world: GoldenWorld, mode: BaseMode): { canvas: HTMLCanvasElement; legend: Legend } {
  const { pixels } = raster;
  const canvas = document.createElement('canvas');
  canvas.width = pixels;
  canvas.height = pixels;
  const context = canvas.getContext('2d')!;
  const image = context.createImageData(pixels, pixels);
  const out = image.data;
  const biomeColours = (world.biomes ?? []).map((_, i) => hexRgb(world.biomeColours?.[i] ?? FALLBACK_BIOME));
  const colours = world.mapColours ?? FALLBACK_WATER;
  const stone = hexRgb(colours.stone);
  const river = hexRgb(colours.river);
  const lake = hexRgb(colours.lake);
  const sea = hexRgb(world.biomeColours?.[0] ?? FALLBACK_WATER.sea);
  const land = (i: number) => raster.water[i] !== 1;
  const [, highest] = range(raster.elevation, land);
  const [deepest] = range(raster.elevation, i => !land(i));
  const scalar = mode === 'temperature' || mode === 'moisture' || mode === 'rock' ? range(raster[mode], land) : [0, 1];
  let legend: Legend;
  for (let y = 0; y < pixels; y++) {
    for (let x = 0; x < pixels; x++) {
      const i = y * pixels + x;
      const water = raster.water[i];
      const elevation = raster.elevation[i];
      let colour: [number, number, number];
      switch (mode) {
        case 'relief': {
          if (water === 1) colour = mix(sea, RAMPS.depth(1)!, (world.waterLevel - elevation) / Math.max(1, world.waterLevel - deepest) / 2);
          else if (water === 2) colour = lake;
          else if (water === 3) colour = river;
          else {
            const base = mix(biomeColours[raster.biome[i]] ?? stone, stone, raster.rock[i]);
            const light = shade(raster.elevation, pixels, x, y, raster.metresPerPixel, RELIEF_EXAGGERATION);
            colour = [base[0] * light, base[1] * light, base[2] * light];
          }
          break;
        }
        case 'elevation':
          colour =
            water === 1
              ? RAMPS.depth((world.waterLevel - elevation) / Math.max(1, world.waterLevel - deepest))
              : RAMPS.elevation((elevation - world.waterLevel) / Math.max(1, highest - world.waterLevel));
          break;
        case 'biome':
          colour = biomeColours[raster.biome[i]] ?? stone;
          break;
        case 'water':
          colour = water === 1 ? sea : water === 2 ? lake : water === 3 ? [120, 200, 230] : [43, 51, 44];
          break;
        default: {
          const t = (raster[mode][i] - scalar[0]) / Math.max(1e-9, scalar[1] - scalar[0]);
          colour = water === 1 ? [30, 38, 34] : RAMPS[mode](t);
        }
      }
      out[i * 4] = colour[0];
      out[i * 4 + 1] = colour[1];
      out[i * 4 + 2] = colour[2];
      out[i * 4 + 3] = 255;
    }
  }
  context.putImageData(image, 0, 0);
  const swatches = (world.biomes ?? []).map((label, i) => ({ label, colour: world.biomeColours?.[i] ?? FALLBACK_BIOME }));
  switch (mode) {
    case 'relief':
    case 'biome':
      legend = { kind: 'swatches', items: swatches };
      break;
    case 'water':
      legend = {
        kind: 'swatches',
        items: [
          { label: 'Sea', colour: world.biomeColours?.[0] ?? FALLBACK_WATER.sea },
          { label: 'Lake', colour: colours.lake },
          { label: 'River', colour: '#78c8e6' },
          { label: 'Land', colour: '#2b332c' },
        ],
      };
      break;
    case 'elevation':
      legend = { kind: 'ramp', stops: ['#3f6b3c', '#8a9a55', '#c9b77a', '#9c7b5b', '#e8e4dc'], low: `${world.waterLevel} m`, high: `${Math.round(highest)} m` };
      break;
    default:
      legend = {
        kind: 'ramp',
        stops: mode === 'temperature' ? ['#3b4cc0', '#8db0fe', '#dddddd', '#f49a7b', '#b40426'] : mode === 'moisture' ? ['#c9a66b', '#d9d59c', '#6fae7a', '#2c7f8a', '#1d3f6e'] : ['#2b332c', '#6f6a62', '#d8d2c4'],
        low: scalar[0].toFixed(2),
        high: scalar[1].toFixed(2),
      };
  }
  return { canvas, legend };
}

/** A window's walking-detail image: ground in biome colours under a hillshade, outside the world transparent. */
export function paintWindow(heights: Int16Array, biomes: Uint8Array, side: number, step: number, world: GoldenWorld): HTMLCanvasElement {
  const canvas = document.createElement('canvas');
  canvas.width = side;
  canvas.height = side;
  const context = canvas.getContext('2d')!;
  const image = context.createImageData(side, side);
  const out = image.data;
  const biomeColours = (world.biomes ?? []).map((_, i) => hexRgb(world.biomeColours?.[i] ?? FALLBACK_BIOME));
  const sea = hexRgb(world.biomeColours?.[0] ?? FALLBACK_WATER.sea);
  for (let y = 0; y < side; y++) {
    for (let x = 0; x < side; x++) {
      const i = y * side + x;
      const h = heights[i];
      if (h === -32768) continue;
      const base = h < world.waterLevel ? sea : biomeColours[biomes[i]] ?? sea;
      const light = h < world.waterLevel ? 1 : shade(heights, side, x, y, step, WALKING_EXAGGERATION);
      out[i * 4] = base[0] * light;
      out[i * 4 + 1] = base[1] * light;
      out[i * 4 + 2] = base[2] * light;
      out[i * 4 + 3] = 255;
    }
  }
  context.putImageData(image, 0, 0);
  return canvas;
}

/** The continent raster's values at a world point, or null outside it. */
export function sampleAt(raster: ContinentRaster, x: number, z: number) {
  const px = Math.floor((x + raster.radius) / raster.metresPerPixel);
  const py = Math.floor((z + raster.radius) / raster.metresPerPixel);
  if (px < 0 || py < 0 || px >= raster.pixels || py >= raster.pixels) return null;
  const i = py * raster.pixels + px;
  return {
    elevation: raster.elevation[i],
    temperature: raster.temperature[i],
    moisture: raster.moisture[i],
    rock: raster.rock[i],
    biome: raster.biome[i],
    water: raster.water[i],
  };
}
