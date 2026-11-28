import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import type { SystemKarstView, UndergroundSystemDetail } from '../types';

const BACKGROUND = 0x101412;
const FIELD_OF_VIEW = 45;
const DEPTH_COLOURS: Record<string, number> = { Shallow: 0x4de0c9, Mid: 0xf2b84b, Deep: 0xd96be6 };
const BIG_COLOURS: Record<string, number> = { UndergroundLake: 0x3b8fd6, FogSpace: 0xc5b4e3, CaveRuin: 0xb88cf0, GreatHall: 0xffd166 };
const LINK_COLOURS: Record<string, number> = { DryPassage: 0x7ef0e6, StreamTube: 0x56b4ff, WetSump: 0x3a6fe6, TallRift: 0xff9e64, Shaft: 0xf2f2f2 };
const MOUTH_COLOURS: Record<string, number> = { Hillside: 0xff8a5c, Sinkhole: 0xffd166, Resurgence: 0x4de0c9, Cliff: 0xff5cf0 };
const STATE_COLOURS: Record<number, number> = { 1: 0xc44840, 2: 0xa85ccc, 3: 0xd67856, 4: 0xdea84c, 5: 0x60ba78 };
const CAVE_STATES = [1, 2, 3, 4, 5] as const;

export interface PlanSceneHandle {
  dispose: () => void;
}

export interface DetailSceneHandle {
  setStates: (visible: Record<number, boolean>) => void;
  setCut: (ceiling: number, slice: number | null) => void;
  setBoxesVisible: (visible: boolean) => void;
  dispose: () => void;
}

function decode(base64: string): Uint8Array {
  return Uint8Array.from(atob(base64), c => c.charCodeAt(0));
}

function createRenderer(container: HTMLElement) {
  const width = container.clientWidth || 800;
  const height = container.clientHeight || 480;
  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio);
  renderer.setSize(width, height);
  renderer.localClippingEnabled = true;
  container.replaceChildren(renderer.domElement);
  return { renderer, width, height };
}

/** Interactive 3D diagram of a planned multi-box cave system: boxes, water tables, face links, mouths, big spaces, and upfront rooms/conduits. */
export function mountSystemPlan(container: HTMLElement, system: UndergroundSystemDetail): PlanSceneHandle {
  const { renderer, width, height } = createRenderer(container);
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(BACKGROUND);
  scene.add(new THREE.HemisphereLight(0xf2eee5, 0x243028, 1.4));
  const light = new THREE.DirectionalLight(0xffffff, 1.2);
  light.position.set(80, 140, 90);
  scene.add(light);

  let minX = Infinity, minY = Infinity, minZ = Infinity;
  let maxX = -Infinity, maxY = -Infinity, maxZ = -Infinity;
  for (const b of system.boxes) {
    minX = Math.min(minX, b.origin[0]); minY = Math.min(minY, b.origin[1]); minZ = Math.min(minZ, b.origin[2]);
    maxX = Math.max(maxX, b.origin[0] + b.size[0]); maxY = Math.max(maxY, b.origin[1] + b.size[1]); maxZ = Math.max(maxZ, b.origin[2] + b.size[2]);
  }
  const shift = (x: number, y: number, z: number) => new THREE.Vector3(x - minX, y - minY, z - minZ);

  const sphere = new THREE.SphereGeometry(1, 14, 10);
  for (const b of system.boxes) {
    const [sx, sy, sz] = b.size;
    const col = DEPTH_COLOURS[b.depthBand] ?? 0x4de0c9;
    const centre = shift(b.origin[0] + sx / 2, b.origin[1] + sy / 2, b.origin[2] + sz / 2);

    const geom = new THREE.BoxGeometry(sx, sy, sz);
    const fill = new THREE.Mesh(geom, new THREE.MeshLambertMaterial({ color: col, transparent: true, opacity: 0.08, depthWrite: false }));
    fill.position.copy(centre);
    scene.add(fill);

    const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geom), new THREE.LineBasicMaterial({ color: col, transparent: true, opacity: 0.75 }));
    edges.position.copy(centre);
    scene.add(edges);

    // Water table plane inside each box.
    const water = new THREE.Mesh(
      new THREE.PlaneGeometry(sx - 2, sz - 2),
      new THREE.MeshBasicMaterial({ color: 0x3b8fd6, transparent: true, opacity: 0.14, side: THREE.DoubleSide, depthWrite: false }),
    );
    water.rotation.x = -Math.PI / 2;
    water.position.copy(shift(b.origin[0] + sx / 2, b.origin[1] + b.waterLocalY, b.origin[2] + sz / 2));
    scene.add(water);

    // Pre-planned interior rooms and conduits (for medium / fine / detailed).
    const roomCentres: THREE.Vector3[] = [];
    for (const r of b.rooms) {
      const pos = shift(b.origin[0] + r.at[0], b.origin[1] + r.at[1], b.origin[2] + r.at[2]);
      roomCentres.push(pos);
      const roomCol = r.kind === 'Pool' ? 0x49a6e8 : r.kind === 'Hall' ? 0xffd166 : r.kind === 'Shaft' ? 0xff8a5c : 0x8ad6a6;
      const mesh = new THREE.Mesh(sphere, new THREE.MeshLambertMaterial({ color: roomCol, transparent: true, opacity: 0.6 }));
      mesh.position.copy(pos);
      mesh.scale.set(r.radii[0], r.radii[1], r.radii[2]);
      scene.add(mesh);
    }
    for (const c of b.conduits) {
      const pa = roomCentres[c.from];
      const pb = roomCentres[c.to];
      if (!pa || !pb) continue;
      const lineGeom = new THREE.BufferGeometry().setFromPoints([pa, pb]);
      const mat = new THREE.LineBasicMaterial({ color: c.loop ? 0xd9a760 : 0xa5e8c6, transparent: true, opacity: 0.85 });
      scene.add(new THREE.Line(lineGeom, mat));
    }
  }

  // Inter-box links & face port rings.
  for (const l of system.links) {
    const pos = shift(l.at[0], l.at[1], l.at[2]);
    const col = LINK_COLOURS[l.kind] ?? 0x7ef0e6;
    const ring = new THREE.Mesh(new THREE.TorusGeometry(Math.max(1.8, l.radius), 0.45, 10, 24), new THREE.MeshBasicMaterial({ color: col }));
    ring.position.copy(pos);
    if (l.fromFace === 'PosX' || l.fromFace === 'NegX') ring.rotation.y = Math.PI / 2;
    // A shaft (round 3) crosses a floor or roof: its ring lies flat.
    if (l.fromFace === 'PosY' || l.fromFace === 'NegY') ring.rotation.x = Math.PI / 2;
    scene.add(ring);

    const a = system.boxes[l.from];
    const b = system.boxes[l.to];
    if (a && b) {
      const vertical = l.kind === 'Shaft';
      const ca = shift(a.origin[0] + a.size[0] / 2, vertical ? a.origin[1] + a.size[1] / 2 : l.at[1], a.origin[2] + a.size[2] / 2);
      const cb = shift(b.origin[0] + b.size[0] / 2, vertical ? b.origin[1] + b.size[1] / 2 : l.at[1], b.origin[2] + b.size[2] / 2);
      scene.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints([ca, pos, cb]), new THREE.LineBasicMaterial({ color: col })));
    }
  }

  // Surface mouths (round 3): a line from the port on the box face along its tendril to where it comes out.
  for (const m of system.mouths) {
    const box = system.boxes[m.box];
    if (!box) continue;
    const [ox, oy, oz] = box.origin;
    const [sx, sy, sz] = box.size;
    const port =
      m.face === 'PosY'
        ? shift(ox + m.portU, oy + sy, oz + m.portV)
        : m.face === 'PosX'
          ? shift(ox + sx, m.portY, oz + m.portU)
          : m.face === 'NegX'
            ? shift(ox, m.portY, oz + m.portU)
            : m.face === 'PosZ'
              ? shift(ox + m.portU, m.portY, oz + sz)
              : shift(ox + m.portU, m.portY, oz);
    const out = shift(m.at[0], m.at[1], m.at[2]);
    const col = MOUTH_COLOURS[m.kind] ?? 0xff8a5c;
    scene.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints([port, out]), new THREE.LineBasicMaterial({ color: col })));
    const marker = new THREE.Mesh(new THREE.OctahedronGeometry(3.2), new THREE.MeshBasicMaterial({ color: col }));
    marker.position.copy(out);
    scene.add(marker);
  }

  // Big-space markers.
  for (const bs of system.bigSpaces) {
    const box = system.boxes[bs.box];
    if (!box) continue;
    const pos = shift(box.origin[0] + bs.local[0], box.origin[1] + bs.local[1], box.origin[2] + bs.local[2]);
    const mesh = new THREE.Mesh(
      sphere,
      new THREE.MeshLambertMaterial({ color: BIG_COLOURS[bs.kind] ?? 0xffd166, transparent: true, opacity: 0.45, wireframe: true }),
    );
    mesh.position.copy(pos);
    mesh.scale.set(bs.radius, Math.max(4, bs.radius * 0.55), bs.radius);
    scene.add(mesh);
  }

  const spanX = Math.max(32, maxX - minX);
  const spanY = Math.max(32, maxY - minY);
  const spanZ = Math.max(32, maxZ - minZ);
  const centre = new THREE.Vector3(spanX / 2, spanY / 2, spanZ / 2);
  const camera = new THREE.PerspectiveCamera(FIELD_OF_VIEW, width / height, 0.5, 4000);
  const dist = Math.max(spanX, spanY, spanZ) * 1.45;
  camera.position.copy(centre).add(new THREE.Vector3(0.8, 0.7, 1.0).normalize().multiplyScalar(dist));
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.target.copy(centre);
  controls.update();

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
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
    render();
  });
  resize.observe(container);

  return {
    dispose() {
      cancelAnimationFrame(frame);
      controls.removeEventListener('change', render);
      resize.disconnect();
      controls.dispose();
      scene.traverse(obj => {
        if (obj instanceof THREE.Mesh || obj instanceof THREE.Line || obj instanceof THREE.LineSegments) {
          obj.geometry.dispose();
          (Array.isArray(obj.material) ? obj.material : [obj.material]).forEach(m => m.dispose());
        }
      });
      renderer.dispose();
    },
  };
}

/** Interactive 3D viewer for the stitched multi-box KarstCave voxel shell generated from plan boundary ports (#11209). */
export function mountSystemDetail(container: HTMLElement, view: SystemKarstView): DetailSceneHandle {
  const { renderer, width, height } = createRenderer(container);
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(BACKGROUND);
  scene.add(new THREE.HemisphereLight(0xf2eee5, 0x2b332c, 1.5));
  const sun = new THREE.DirectionalLight(0xffffff, 1.6);
  sun.position.set(-60, 120, 80);
  scene.add(sun);

  const [cx, cy, cz] = view.cells.size;
  const metres = view.cells.metres;
  const state = decode(view.cells.state);
  const at = (i: number, j: number, k: number) => (i < 0 || j < 0 || k < 0 || i >= cx || j >= cy || k >= cz ? 0 : state[(k * cy + j) * cx + i]);

  const ceiling = new THREE.Plane(new THREE.Vector3(0, -1, 0), cy * metres);
  const slice = new THREE.Plane(new THREE.Vector3(-1, 0, 0), cx * metres);
  const planes = [ceiling, slice];

  const block = new THREE.BoxGeometry(metres, metres, metres);
  const meshes = new Map<number, THREE.InstancedMesh>();
  const matrix = new THREE.Matrix4();
  for (const kind of CAVE_STATES) {
    const cells: Array<[number, number, number]> = [];
    for (let k = 0; k < cz; k++)
      for (let j = 0; j < cy; j++)
        for (let i = 0; i < cx; i++) {
          const own = at(i, j, k);
          if (own !== kind) continue;
          if (at(i + 1, j, k) && at(i - 1, j, k) && at(i, j + 1, k) && at(i, j - 1, k) && at(i, j, k + 1) && at(i, j, k - 1)) continue;
          cells.push([i, j, k]);
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

  // Box wireframes + seam rings + routes.
  const boxGroup = new THREE.Group();
  for (const b of view.boxes) {
    const geom = new THREE.BoxGeometry(b.size[0], b.size[1], b.size[2]);
    const edges = new THREE.LineSegments(
      new THREE.EdgesGeometry(geom),
      new THREE.LineBasicMaterial({ color: b.reachedCrawl ? 0x6ee7d8 : 0xff5c5c, transparent: true, opacity: 0.7 }),
    );
    edges.position.set(b.offset[0] + b.size[0] / 2, b.offset[1] + b.size[1] / 2, b.offset[2] + b.size[2] / 2);
    boxGroup.add(edges);
  }
  for (const seam of view.seams) {
    const col = seam.seamReachedCrawl ? 0x60f098 : seam.seamReachedTight ? 0xf2b84b : 0xff5c5c;
    const ring = new THREE.Mesh(
      new THREE.SphereGeometry(Math.max(1.5, seam.radius), 12, 8),
      new THREE.MeshBasicMaterial({ color: col, wireframe: true, depthTest: false, transparent: true }),
    );
    ring.position.set(seam.at[0], seam.at[1], seam.at[2]);
    boxGroup.add(ring);
  }
  for (const route of view.routes) {
    if (route.length < 2) continue;
    const geom = new THREE.BufferGeometry().setFromPoints(route.map(p => new THREE.Vector3(p[0], p[1] + 0.3, p[2])));
    boxGroup.add(new THREE.Line(geom, new THREE.LineBasicMaterial({ color: 0xffffff, depthTest: false, transparent: true, opacity: 0.8 })));
  }
  scene.add(boxGroup);

  const camera = new THREE.PerspectiveCamera(FIELD_OF_VIEW, width / height, 0.5, 4000);
  const centre = new THREE.Vector3((cx * metres) / 2, (cy * metres) / 2, (cz * metres) / 2);
  const distance = Math.max(cx, cy, cz) * metres * 1.5;
  camera.position.copy(centre).add(new THREE.Vector3(0.75, 0.8, 1).normalize().multiplyScalar(distance));
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.target.copy(centre);
  controls.update();

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
    setCut(top, across) {
      ceiling.constant = top;
      slice.constant = across ?? cx * metres + 1;
      render();
    },
    setBoxesVisible(visible) {
      boxGroup.visible = visible;
      render();
    },
    dispose() {
      cancelAnimationFrame(frame);
      controls.removeEventListener('change', render);
      resize.disconnect();
      controls.dispose();
      scene.traverse(obj => {
        if (obj instanceof THREE.Mesh || obj instanceof THREE.Line || obj instanceof THREE.LineSegments || obj instanceof THREE.InstancedMesh) {
          obj.geometry.dispose();
          (Array.isArray(obj.material) ? obj.material : [obj.material]).forEach(m => m.dispose());
        }
      });
      renderer.dispose();
    },
  };
}
