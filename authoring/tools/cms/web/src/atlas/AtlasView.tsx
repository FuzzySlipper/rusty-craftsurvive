import { useEffect, useRef } from 'preact/hooks';
import type { GoldenCaveRow, GoldenCrossing, GoldenPlace, GoldenPlaces, GoldenReach, GoldenTile, GoldenTileCave, GoldenWindow, GoldenWindowSummary, GoldenWorld } from '../types';

/** One thing on the atlas a click can select, and how to refer to it in a design task. */
export interface Pick {
  ref: string;
  kind: 'town' | 'lake' | 'site' | 'reach' | 'place' | 'crossing' | 'window' | 'map-river';
  label: string;
  x: number;
  z: number;
  data: unknown;
}

export interface Camera {
  x: number;
  z: number;
  metresPerPixel: number;
}

export interface AtlasToggles {
  drainage: number; // the least navigability drawn; -1 hides the walked drainage
  mapRivers: boolean;
  lakes: boolean;
  towns: boolean;
  sites: boolean;
  windows: boolean;
  detail: boolean;
  places: Record<string, boolean>;
  caves: boolean;
  crossings: boolean;
  walkedRivers: boolean;
  labels: boolean;
  allCaves: boolean;
}

export const PLACE_COLOURS: Record<string, string> = {
  StandingStones: '#d8d2c4',
  Ruin: '#c5b4e3',
  CaveMouth: '#ff8a5c',
  DungeonEntrance: '#ff5c7a',
  VantagePoint: '#72c4ff',
};

/** A cave way's path points are x, y (height), z: its ground position is the first and third. */
export const CAVE_Z = 2;
const NAVIGABILITY_WIDTH = [0.6, 1.4, 2.4, 3.6];
const NAVIGABILITY_COLOUR = ['#4f8fa0', '#6fb6c9', '#8fd3ea', '#b5ecff'];
const TOWN_COLOUR = '#ffd166';
const SELECT_COLOUR = '#ffffff';
const PICK_PIXELS = 9;
/** Places, caves and crossings show once a pixel is this many metres or fewer: closer than that they would be a smear. */
export const DETAIL_METRES_PER_PIXEL = 40;
const MIN_METRES_PER_PIXEL = 0.5;
const ZOOM_STEP = 1.2;

export function AtlasView({
  world,
  base,
  places,
  drainage,
  mapRivers,
  windows,
  details,
  detailImages,
  tiles,
  caveList,
  failedTiles,
  tileSpacing,
  toggles,
  camera,
  selected,
  onCamera,
  onPick,
  onHover,
}: {
  world: GoldenWorld;
  base: HTMLCanvasElement | null;
  places: GoldenPlaces | null;
  drainage: GoldenReach[];
  mapRivers: number[][] | null;
  windows: GoldenWindowSummary[];
  details: Record<string, GoldenWindow>;
  detailImages: Record<string, HTMLCanvasElement>;
  tiles: GoldenTile[];
  caveList: GoldenCaveRow[] | null;
  failedTiles: Array<[number, number]>;
  tileSpacing: number;
  toggles: AtlasToggles;
  camera: Camera;
  selected: Pick | null;
  onCamera: (camera: Camera) => void;
  onPick: (pick: Pick | null) => void;
  onHover: (point: { x: number; z: number } | null) => void;
}) {
  const wrap = useRef<HTMLDivElement>(null);
  const canvas = useRef<HTMLCanvasElement>(null);
  const cameraRef = useRef(camera);
  cameraRef.current = camera;
  const drag = useRef<{ x: number; y: number; camera: Camera; moved: boolean } | null>(null);
  const size = useRef({ width: 800, height: 600 });

  const toScreen = (x: number, z: number, c = cameraRef.current) => ({
    sx: (x - c.x) / c.metresPerPixel + size.current.width / 2,
    sy: (z - c.z) / c.metresPerPixel + size.current.height / 2,
  });
  const toWorld = (sx: number, sy: number, c = cameraRef.current) => ({
    x: (sx - size.current.width / 2) * c.metresPerPixel + c.x,
    z: (sy - size.current.height / 2) * c.metresPerPixel + c.z,
  });

  function draw() {
    const element = canvas.current;
    if (!element) return;
    const context = element.getContext('2d')!;
    const { width, height } = size.current;
    const ratio = window.devicePixelRatio || 1;
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    context.fillStyle = '#101412';
    context.fillRect(0, 0, width, height);
    const c = cameraRef.current;
    const radius = world.radius;
    const fine = c.metresPerPixel <= DETAIL_METRES_PER_PIXEL;
    context.imageSmoothingEnabled = false;
    if (base) {
      const topLeft = toScreen(-radius, -radius);
      context.drawImage(base, topLeft.sx, topLeft.sy, (2 * radius) / c.metresPerPixel, (2 * radius) / c.metresPerPixel);
    }
    if (toggles.detail) {
      for (const window of windows) {
        const image = detailImages[window.id];
        if (!image) continue;
        const half = window.half + window.step / 2;
        const corner = toScreen(window.x - half, window.z - half);
        context.drawImage(image, corner.sx, corner.sy, (2 * half) / c.metresPerPixel, (2 * half) / c.metresPerPixel);
      }
    }
    // A flat point list: x then z at zOffset within each stride (rivers x,z[,halfWidth]; cave ways x,y,z).
    const line = (points: number[], stride: number, zOffset = 1) => {
      context.beginPath();
      for (let i = 0; i < points.length; i += stride) {
        const p = toScreen(points[i], points[i + zOffset]);
        if (i === 0) context.moveTo(p.sx, p.sy);
        else context.lineTo(p.sx, p.sy);
      }
      context.stroke();
    };
    context.lineCap = 'round';
    context.lineJoin = 'round';
    if (toggles.mapRivers && mapRivers) {
      context.strokeStyle = 'rgba(255, 230, 140, 0.75)';
      context.lineWidth = 1;
      for (const reach of mapRivers) line(reach, 3);
    }
    if (toggles.drainage >= 0) {
      for (const reach of drainage) {
        if (reach.navigability < toggles.drainage) continue;
        context.strokeStyle = NAVIGABILITY_COLOUR[reach.navigability] ?? NAVIGABILITY_COLOUR[0];
        context.lineWidth = NAVIGABILITY_WIDTH[reach.navigability] ?? 1;
        line(reach.points, 2);
      }
    }
    if (fine) {
      for (const detail of Object.values(details)) {
        if (toggles.walkedRivers) {
          for (const river of detail.rivers) {
            context.strokeStyle = 'rgba(120, 200, 230, 0.55)';
            for (let i = 0; i + 5 < river.points.length; i += 3) {
              const a = toScreen(river.points[i], river.points[i + 1]);
              const b = toScreen(river.points[i + 3], river.points[i + 4]);
              context.lineWidth = Math.max(1, (2 * river.points[i + 2]) / c.metresPerPixel);
              context.beginPath();
              context.moveTo(a.sx, a.sy);
              context.lineTo(b.sx, b.sy);
              context.stroke();
            }
          }
        }
      }
      // The whole continent's places, cave ways and crossings, from the region tiles loaded about the view.
      for (const tile of tiles) {
        if (toggles.crossings) {
          context.strokeStyle = '#e4b282';
          context.lineWidth = 3;
          for (const crossing of tile.crossings) line([crossing[2], crossing[3], crossing[4], crossing[5]], 2);
        }
        if (toggles.caves) {
          context.strokeStyle = 'rgba(255, 138, 92, 0.85)';
          context.lineWidth = 2;
          context.setLineDash([4, 3]);
          for (const cave of tile.caves) line(cave.path, 3, CAVE_Z);
          context.setLineDash([]);
        }
        for (const place of tile.places) {
          const kind = world.placeKinds[place[2]];
          if (!toggles.places[kind]) continue;
          const p = toScreen(place[3], place[4]);
          context.fillStyle = PLACE_COLOURS[kind] ?? '#ffffff';
          context.beginPath();
          context.arc(p.sx, p.sy, kind === 'CaveMouth' || kind === 'DungeonEntrance' ? 4.5 : 3, 0, Math.PI * 2);
          context.fill();
        }
      }
    } else if (toggles.allCaves && caveList) {
      // Far out, every cave mouth that has a way underground, as a dot.
      context.fillStyle = 'rgba(255, 138, 92, 0.55)';
      for (const cave of caveList) {
        const p = toScreen(cave.x, cave.z);
        if (p.sx < -2 || p.sy < -2 || p.sx > width + 2 || p.sy > height + 2) continue;
        context.fillRect(p.sx - 0.75, p.sy - 0.75, 1.5, 1.5);
      }
    }
    // Tiles the generator could not make: the export recorded their error.
    context.strokeStyle = '#ff5c5c';
    context.lineWidth = 2;
    context.setLineDash([6, 4]);
    for (const [tx, tz] of failedTiles) {
      const corner = toScreen((tx - 0.5) * tileSpacing, (tz - 0.5) * tileSpacing);
      context.strokeRect(corner.sx, corner.sy, tileSpacing / c.metresPerPixel, tileSpacing / c.metresPerPixel);
    }
    context.setLineDash([]);
    if (toggles.windows) {
      context.strokeStyle = 'rgba(228, 178, 130, 0.8)';
      context.lineWidth = 1;
      context.setLineDash([5, 4]);
      for (const window of windows) {
        const corner = toScreen(window.x - window.half, window.z - window.half);
        context.strokeRect(corner.sx, corner.sy, (2 * window.half) / c.metresPerPixel, (2 * window.half) / c.metresPerPixel);
      }
      context.setLineDash([]);
    }
    context.font = '12px system-ui, sans-serif';
    context.textBaseline = 'middle';
    if (places && toggles.lakes) {
      for (const lake of places.lakes) {
        const p = toScreen(lake.x, lake.z);
        context.strokeStyle = '#9fbcc0';
        context.lineWidth = 1.5;
        context.beginPath();
        context.arc(p.sx, p.sy, 4, 0, Math.PI * 2);
        context.stroke();
        if (toggles.labels) {
          context.fillStyle = '#cfe3e6';
          context.fillText(lake.name || `${lake.kind} lake ${lake.id}`, p.sx + 7, p.sy);
        }
      }
    }
    if (places && toggles.sites) {
      for (const site of places.sites) {
        const p = toScreen(site.x, site.z);
        context.fillStyle = '#c5b4e3';
        context.fillRect(p.sx - 3, p.sy - 3, 6, 6);
        if (toggles.labels) {
          context.fillStyle = '#e4dcf0';
          context.fillText(site.name, p.sx + 7, p.sy);
        }
      }
    }
    if (places && toggles.towns) {
      for (const town of places.towns) {
        const p = toScreen(town.x, town.z);
        context.fillStyle = TOWN_COLOUR;
        context.strokeStyle = '#271c13';
        context.lineWidth = 2;
        context.beginPath();
        context.arc(p.sx, p.sy, town.kind === 'Port' ? 7 : 5.5, 0, Math.PI * 2);
        context.fill();
        context.stroke();
        if (toggles.labels) {
          context.font = '600 13px system-ui, sans-serif';
          context.strokeStyle = 'rgba(16, 20, 18, 0.85)';
          context.lineWidth = 3;
          context.strokeText(town.name, p.sx + 10, p.sy);
          context.fillStyle = '#fff3d0';
          context.fillText(town.name, p.sx + 10, p.sy);
          context.font = '12px system-ui, sans-serif';
        }
      }
    }
    if (selected) {
      const p = toScreen(selected.x, selected.z);
      context.strokeStyle = SELECT_COLOUR;
      context.lineWidth = 2;
      context.beginPath();
      context.arc(p.sx, p.sy, 11, 0, Math.PI * 2);
      context.stroke();
      if (selected.kind === 'reach') line((selected.data as GoldenReach).points, 2);
    }
    // A scale bar, so distances read without a grid.
    const target = 120 * c.metresPerPixel;
    const magnitude = 10 ** Math.floor(Math.log10(target));
    const metres = [1, 2, 5, 10].map(m => m * magnitude).filter(m => m <= target).pop() ?? magnitude;
    const barWidth = metres / c.metresPerPixel;
    context.fillStyle = '#f2eee5';
    context.fillRect(16, height - 22, barWidth, 3);
    context.fillText(metres >= 1000 ? `${metres / 1000} km` : `${metres} m`, 16 + barWidth + 8, height - 21);
  }

  function pickAt(sx: number, sy: number): Pick | null {
    const c = cameraRef.current;
    const candidates: Pick[] = [];
    if (places && toggles.towns)
      for (const town of places.towns) candidates.push({ ref: town.key, kind: 'town', label: town.name, x: town.x, z: town.z, data: town });
    if (places && toggles.sites)
      for (const site of places.sites) candidates.push({ ref: `site:${site.name}`, kind: 'site', label: site.name, x: site.x, z: site.z, data: site });
    if (places && toggles.lakes)
      for (const lake of places.lakes)
        candidates.push({ ref: lake.key, kind: 'lake', label: lake.name || `${lake.kind} lake ${lake.id}`, x: lake.x, z: lake.z, data: lake });
    if (c.metresPerPixel <= DETAIL_METRES_PER_PIXEL) {
      for (const tile of tiles) {
        const caves = new Map(tile.caves.map(cave => [cave.id, cave]));
        for (const row of tile.places) {
          const kind = world.placeKinds[row[2]];
          if (!toggles.places[kind]) continue;
          const id = `poi:${row[0]}:${row[1]}`;
          candidates.push({ ref: id, kind: 'place', label: `${kind} ${id}`, x: row[3], z: row[4], data: placeOf(row, kind, caves.get(id)) });
        }
        if (toggles.crossings)
          for (const row of tile.crossings)
            candidates.push({
              ref: `crossing:${row[0]}:${row[1]}`,
              kind: 'crossing',
              label: `Crossing crossing:${row[0]}:${row[1]}`,
              x: (row[2] + row[4]) / 2,
              z: (row[3] + row[5]) / 2,
              data: crossingOf(row),
            });
      }
    } else if (toggles.allCaves && caveList) {
      for (const cave of caveList) candidates.push({ ref: cave.id, kind: 'place', label: `CaveMouth ${cave.id}`, x: cave.x, z: cave.z, data: cave });
    }
    let best: Pick | null = null;
    let bestDistance = PICK_PIXELS;
    for (const candidate of candidates) {
      const p = toScreen(candidate.x, candidate.z);
      const distance = Math.hypot(p.sx - sx, p.sy - sy);
      if (distance < bestDistance) {
        best = candidate;
        bestDistance = distance;
      }
    }
    if (best || toggles.drainage < 0) return best;
    // Otherwise the nearest walked reach drawn under the cursor.
    let reachBest: GoldenReach | null = null;
    let reachDistance = PICK_PIXELS / 2 + 2;
    const point = toWorld(sx, sy);
    for (const reach of drainage) {
      if (reach.navigability < toggles.drainage) continue;
      for (let i = 0; i + 3 < reach.points.length; i += 2) {
        const distance = segmentDistance(point.x, point.z, reach.points[i], reach.points[i + 1], reach.points[i + 2], reach.points[i + 3]) / c.metresPerPixel;
        if (distance < reachDistance) {
          reachDistance = distance;
          reachBest = reach;
        }
      }
    }
    if (!reachBest) return null;
    const middle = Math.floor(reachBest.points.length / 4) * 2;
    return { ref: reachBest.key, kind: 'reach', label: `Reach ${reachBest.key}`, x: reachBest.points[middle], z: reachBest.points[middle + 1], data: reachBest };
  }

  const drawRef = useRef(draw);
  drawRef.current = draw;
  useEffect(() => {
    const element = wrap.current;
    if (!element) return;
    const observer = new ResizeObserver(() => {
      const ratio = window.devicePixelRatio || 1;
      size.current = { width: element.clientWidth, height: element.clientHeight };
      if (canvas.current) {
        canvas.current.width = element.clientWidth * ratio;
        canvas.current.height = element.clientHeight * ratio;
      }
      drawRef.current();
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  useEffect(draw);

  function onWheel(event: WheelEvent) {
    event.preventDefault();
    const rect = canvas.current!.getBoundingClientRect();
    const sx = event.clientX - rect.left;
    const sy = event.clientY - rect.top;
    const c = cameraRef.current;
    const anchor = toWorld(sx, sy, c);
    const limit = (world.radius * 2.4) / Math.min(size.current.width, size.current.height);
    const metresPerPixel = Math.min(limit, Math.max(MIN_METRES_PER_PIXEL, c.metresPerPixel * (event.deltaY > 0 ? ZOOM_STEP : 1 / ZOOM_STEP)));
    onCamera({
      metresPerPixel,
      x: anchor.x - (sx - size.current.width / 2) * metresPerPixel,
      z: anchor.z - (sy - size.current.height / 2) * metresPerPixel,
    });
  }

  return (
    <div class="atlas-canvas" ref={wrap}>
      <canvas
        ref={canvas}
        onWheel={onWheel}
        onPointerDown={event => {
          (event.currentTarget as HTMLCanvasElement).setPointerCapture(event.pointerId);
          drag.current = { x: event.clientX, y: event.clientY, camera: cameraRef.current, moved: false };
        }}
        onPointerMove={event => {
          const rect = canvas.current!.getBoundingClientRect();
          onHover(toWorld(event.clientX - rect.left, event.clientY - rect.top));
          const d = drag.current;
          if (!d) return;
          const dx = event.clientX - d.x;
          const dy = event.clientY - d.y;
          if (Math.abs(dx) + Math.abs(dy) > 3) d.moved = true;
          if (d.moved) onCamera({ ...d.camera, x: d.camera.x - dx * d.camera.metresPerPixel, z: d.camera.z - dy * d.camera.metresPerPixel });
        }}
        onPointerUp={event => {
          const d = drag.current;
          drag.current = null;
          if (d && !d.moved) {
            const rect = canvas.current!.getBoundingClientRect();
            onPick(pickAt(event.clientX - rect.left, event.clientY - rect.top));
          }
        }}
        onPointerLeave={() => onHover(null)}
      />
    </div>
  );
}

function segmentDistance(px: number, pz: number, ax: number, az: number, bx: number, bz: number): number {
  const dx = bx - ax;
  const dz = bz - az;
  const length = dx * dx + dz * dz;
  const t = length > 0 ? Math.max(0, Math.min(1, ((px - ax) * dx + (pz - az) * dz) / length)) : 0;
  return Math.hypot(px - (ax + t * dx), pz - (az + t * dz));
}

/** A tile's place row as the inspector shows it, with its cave way when it has one. */
export function placeOf(row: number[], kind: string, cave?: GoldenTileCave): GoldenPlace & { seed?: string; setting?: number[] } {
  return {
    id: `poi:${row[0]}:${row[1]}`,
    kind,
    x: row[3],
    z: row[4],
    ground: row[5],
    height: row[6],
    variant: row[7],
    aspect: row[8],
    ...(cave ? { cave: { path: cave.path, box: cave.box }, seed: cave.seed, setting: cave.setting } : {}),
  };
}

export function crossingOf(row: number[]): GoldenCrossing {
  return { id: `crossing:${row[0]}:${row[1]}`, fromX: row[2], fromZ: row[3], toX: row[4], toZ: row[5], deck: row[6] };
}

/** The region tile owning a world position (MapRegions.TileAt). */
export function tileAt(x: number, z: number, spacing: number): [number, number] {
  return [Math.floor(x / spacing + 0.5), Math.floor(z / spacing + 0.5)];
}
