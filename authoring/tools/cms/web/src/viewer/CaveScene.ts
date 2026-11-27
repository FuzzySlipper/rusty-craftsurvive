import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { LineSegments2 } from 'three/examples/jsm/lines/LineSegments2.js';
import { LineSegmentsGeometry } from 'three/examples/jsm/lines/LineSegmentsGeometry.js';
import { LineMaterial } from 'three/examples/jsm/lines/LineMaterial.js';
import type { CaveView } from '../types';

/** The cave view's states, as the exporter writes them (CaveView): rock, beyond a pinch, cut off, tight, crawl, stand. */
export const CAVE_STATES = [1, 2, 3, 4, 5] as const;
export type CaveLayer = 'route' | 'tendril' | 'markers';

const BACKGROUND = 0x101412;
const FIELD_OF_VIEW = 45;
/** How far the camera stands back, in the cave's largest extents. */
const CAMERA_BACK = 1.75;
/** Overlays draw over the blocks, as an x-ray: the route and the features run inside the open space the blocks enclose. */
const OVERLAY = { depthTest: false, transparent: true } as const;
const MARKER_RADIUS = 0.9;
/** The route's width on screen, in pixels. */
const ROUTE_PIXELS = 3;
const STATE_COLOURS: Record<number, number> = { 1: 0xc44840, 2: 0xa85ccc, 3: 0xd67856, 4: 0xdea84c, 5: 0x60ba78 };
const ROUTE_COLOURS = [0xc44840, 0xd67856, 0xdea84c, 0x60ba78];
const MARKER_COLOURS = { chamber: 0xffd166, sink: 0x72c4ff, spring: 0x4de0c9, water: 0x3b8fd6, ruin: 0xb88cf0, start: 0xffffff, deadEnd: 0x9a8f7d };

export interface CaveSceneHandle {
  /** Which reach states show. */
  setStates: (visible: Record<number, boolean>) => void;
  setLayers: (visible: Record<CaveLayer, boolean>) => void;
  /** Cuts the scene away above a height and, optionally, beyond a slice across x (metres in the box), to look inside. */
  setCut: (ceiling: number, slice: number | null) => void;
  dispose: () => void;
}

function decode(base64: string): Uint8Array {
  return Uint8Array.from(atob(base64), c => c.charCodeAt(0));
}

/**
 * The cave's open space in three dimensions: each metre cell the shell of open space touches rock, as a block in its reach
 * state's colour (the inside of a space is left out, so its walls show), with the route, the way in and the features over
 * it. Orbit with the mouse; the cut-away planes open the inside.
 */
export function mountCave(container: HTMLElement, view: CaveView): CaveSceneHandle {
  const width = container.clientWidth || 800;
  const height = container.clientHeight || 520;
  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio);
  renderer.setSize(width, height);
  renderer.localClippingEnabled = true;
  container.replaceChildren(renderer.domElement);

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(BACKGROUND);
  scene.add(new THREE.HemisphereLight(0xf2eee5, 0x2b332c, 1.5));
  const sun = new THREE.DirectionalLight(0xffffff, 1.6);
  sun.position.set(-40, 90, 60);
  scene.add(sun);

  const [cx, cy, cz] = view.cells.size;
  const metres = view.cells.metres;
  const state = decode(view.cells.state);
  const at = (i: number, j: number, k: number) => (i < 0 || j < 0 || k < 0 || i >= cx || j >= cy || k >= cz ? 0 : state[(k * cy + j) * cx + i]);

  // Clipping: a ceiling plane (keeps what is below it) and a slice plane across x (keeps what is short of it).
  const ceiling = new THREE.Plane(new THREE.Vector3(0, -1, 0), cy * metres);
  const slice = new THREE.Plane(new THREE.Vector3(-1, 0, 0), cx * metres);
  const planes = [ceiling, slice];

  // The colour a shell block takes: the best state within a cell of it, so a hall's walls read as the hall's standing room
  // and a crawl tube stays a crawl (the cells along a wall are only ever crawling room themselves).
  const best = (i: number, j: number, k: number) => {
    let found = 0;
    for (let dk = -1; dk <= 1; dk++) for (let dj = -1; dj <= 1; dj++) for (let di = -1; di <= 1; di++) found = Math.max(found, at(i + di, j + dj, k + dk));
    return found;
  };

  // One instanced block mesh per state, holding the cells of open space beside rock.
  const block = new THREE.BoxGeometry(metres, metres, metres);
  const meshes = new Map<number, THREE.InstancedMesh>();
  const matrix = new THREE.Matrix4();
  for (const kind of CAVE_STATES) {
    const cells: Array<[number, number, number]> = [];
    for (let k = 0; k < cz; k++)
      for (let j = 0; j < cy; j++)
        for (let i = 0; i < cx; i++) {
          if (!at(i, j, k)) continue;
          if (at(i + 1, j, k) && at(i - 1, j, k) && at(i, j + 1, k) && at(i, j - 1, k) && at(i, j, k + 1) && at(i, j, k - 1)) continue;
          // Cut-off space keeps its own colour: nothing reached borders it.
          const own = at(i, j, k);
          const shown = own === 2 ? 2 : Math.max(own, best(i, j, k) === 2 ? own : best(i, j, k));
          if (shown === kind) cells.push([i, j, k]);
        }
    const material = new THREE.MeshLambertMaterial({ color: STATE_COLOURS[kind], clippingPlanes: planes, side: THREE.DoubleSide });
    const mesh = new THREE.InstancedMesh(block, material, Math.max(1, cells.length));
    mesh.count = cells.length;
    cells.forEach(([i, j, k], n) => {
      matrix.makeTranslation((i + 0.5) * metres, (j + 0.5) * metres, (k + 0.5) * metres);
      mesh.setMatrixAt(n, matrix);
    });
    meshes.set(kind, mesh);
    scene.add(mesh);
  }

  const layers: Record<CaveLayer, THREE.Group> = { route: new THREE.Group(), tendril: new THREE.Group(), markers: new THREE.Group() };
  for (const group of Object.values(layers)) {
    group.renderOrder = 1;
    scene.add(group);
  }
  const lift = 0.3;
  // The route, each leg in the colour of how its end is reached, drawn as screen-width segments (WebGL lines are one pixel).
  const positions: number[] = [];
  const colours: number[] = [];
  const colour = new THREE.Color();
  for (let n = 1; n < view.route.length; n++) {
    const a = view.route[n - 1];
    const b = view.route[n];
    positions.push(a[0], a[1] + lift, a[2], b[0], b[1] + lift, b[2]);
    colour.setHex(ROUTE_COLOURS[view.profile[n][2]]);
    colours.push(colour.r, colour.g, colour.b, colour.r, colour.g, colour.b);
  }
  const routeMaterial = new LineMaterial({ linewidth: ROUTE_PIXELS, vertexColors: true, clippingPlanes: planes, ...OVERLAY });
  routeMaterial.resolution.set(width, height);
  if (positions.length) {
    const geometry = new LineSegmentsGeometry().setPositions(positions).setColors(colours);
    layers.route.add(new LineSegments2(geometry, routeMaterial));
  }
  if (view.tendril.length > 1) {
    const geometry = new THREE.BufferGeometry().setFromPoints(view.tendril.map(p => new THREE.Vector3(p[0], p[1], p[2])));
    layers.tendril.add(new THREE.Line(geometry, new THREE.LineDashedMaterial({ color: 0xff8a5c, dashSize: 1, gapSize: 0.6, clippingPlanes: planes, ...OVERLAY })).computeLineDistances());
  }
  const sphere = new THREE.SphereGeometry(MARKER_RADIUS, 12, 8);
  const marker = (p: number[] | undefined | null, colour: number, scale = 1) => {
    if (!p) return;
    const mesh = new THREE.Mesh(sphere, new THREE.MeshBasicMaterial({ color: colour, clippingPlanes: planes, ...OVERLAY }));
    mesh.position.set(p[0], p[1], p[2]);
    mesh.scale.setScalar(scale);
    layers.markers.add(mesh);
  };
  const f = view.features;
  for (const p of f.chambers ?? []) marker(p, MARKER_COLOURS.chamber, 1.3);
  for (const p of f.sinks ?? []) marker(p, MARKER_COLOURS.sink);
  marker(f.spring, MARKER_COLOURS.spring, 1.2);
  for (const w of f.water ?? []) marker(w.at, MARKER_COLOURS.water);
  marker(f.ruin, MARKER_COLOURS.ruin, 1.2);
  for (const p of f.deadEnds ?? []) marker(p, MARKER_COLOURS.deadEnd, 0.7);
  marker(view.start, MARKER_COLOURS.start, 1.1);

  const camera = new THREE.PerspectiveCamera(FIELD_OF_VIEW, width / height, 0.1, 2000);
  const centre = new THREE.Vector3((cx * metres) / 2, (cy * metres) / 2, (cz * metres) / 2);
  const distance = Math.max(cx, cy, cz) * metres * CAMERA_BACK;
  camera.position.copy(centre).add(new THREE.Vector3(0.75, 0.8, 1).normalize().multiplyScalar(distance));
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.target.copy(centre);
  controls.update();

  // Drawn when something changes, not every frame: a cave is tens of thousands of blocks.
  let frame = 0;
  const render = () => {
    if (frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      renderer.render(scene, camera);
    });
  };
  controls.addEventListener('change', render);
  render();
  const resize = new ResizeObserver(() => {
    const w = container.clientWidth;
    const h = container.clientHeight;
    if (!w || !h) return;
    renderer.setSize(w, h);
    routeMaterial.resolution.set(w, h);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    render();
  });
  resize.observe(container);

  return {
    setStates(visible) {
      for (const [kind, mesh] of meshes) mesh.visible = visible[kind] !== false;
      render();
    },
    setLayers(visible) {
      for (const [name, group] of Object.entries(layers)) group.visible = visible[name as CaveLayer] !== false;
      render();
    },
    setCut(top, across) {
      ceiling.constant = top;
      slice.constant = across ?? cx * metres + 1;
      render();
    },
    dispose() {
      cancelAnimationFrame(frame);
      controls.removeEventListener('change', render);
      resize.disconnect();
      controls.dispose();
      scene.traverse(object => {
        if (object instanceof THREE.Mesh || object instanceof THREE.Line || object instanceof THREE.InstancedMesh) {
          object.geometry.dispose();
          (Array.isArray(object.material) ? object.material : [object.material]).forEach(material => material.dispose());
        }
      });
      renderer.dispose();
    },
  };
}
