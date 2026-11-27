import * as THREE from 'three';
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import type { ModelRef } from '../types';

/** Where content/game is served, which a prop mesh's texture paths are relative to. */
const CONTENT_MOUNT = '/media/content/';
const BACKGROUND = 0x1a211c;
const FIELD_OF_VIEW = 40;
const FIT_MARGIN = 1.6;

interface PropPart {
  role: string;
  positions: number[];
  normals?: number[];
  uvs?: number[];
  colors?: number[];
  indices: number[];
}

interface PropMesh {
  name: string;
  parts: PropPart[];
  textures?: Record<string, string>;
}

export interface SceneHandle {
  clips: string[];
  play: (clip: string | null) => void;
  dispose: () => void;
}

function propMesh(data: PropMesh): THREE.Group {
  const group = new THREE.Group();
  const loader = new THREE.TextureLoader();
  for (const part of data.parts) {
    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.Float32BufferAttribute(part.positions, 3));
    if (part.normals?.length) geometry.setAttribute('normal', new THREE.Float32BufferAttribute(part.normals, 3));
    if (part.uvs?.length) geometry.setAttribute('uv', new THREE.Float32BufferAttribute(part.uvs, 2));
    const colours = part.colors && part.colors.length ? part.colors : null;
    if (colours) {
      const stride = colours.length / (part.positions.length / 3);
      geometry.setAttribute('color', new THREE.Float32BufferAttribute(colours, stride));
    }
    geometry.setIndex(part.indices);
    if (!part.normals?.length) geometry.computeVertexNormals();
    const texturePath = data.textures?.[part.role];
    const material = new THREE.MeshStandardMaterial({ vertexColors: Boolean(colours), roughness: 0.9, side: THREE.DoubleSide, alphaTest: 0.4 });
    if (texturePath) {
      const texture = loader.load(CONTENT_MOUNT + texturePath);
      texture.colorSpace = THREE.SRGBColorSpace;
      texture.wrapS = texture.wrapT = THREE.RepeatWrapping;
      material.map = texture;
      material.transparent = false;
    }
    group.add(new THREE.Mesh(geometry, material));
  }
  return group;
}

export async function mountModel(container: HTMLElement, model: ModelRef): Promise<SceneHandle> {
  const width = container.clientWidth || 640;
  const height = container.clientHeight || 420;
  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio);
  renderer.setSize(width, height);
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  container.replaceChildren(renderer.domElement);

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(BACKGROUND);
  scene.add(new THREE.HemisphereLight(0xf2eee5, 0x39443c, 1.6));
  const sun = new THREE.DirectionalLight(0xffffff, 1.8);
  sun.position.set(4, 8, 5);
  scene.add(sun);
  const camera = new THREE.PerspectiveCamera(FIELD_OF_VIEW, width / height, 0.01, 2000);
  const controls = new OrbitControls(camera, renderer.domElement);

  let root: THREE.Object3D;
  let mixer: THREE.AnimationMixer | null = null;
  let animations: THREE.AnimationClip[] = [];
  if (model.type === 'glb') {
    const gltf = await new GLTFLoader().loadAsync(model.url);
    root = gltf.scene;
    animations = gltf.animations;
    if (animations.length) mixer = new THREE.AnimationMixer(root);
  } else {
    const response = await fetch(model.url);
    root = propMesh((await response.json()) as PropMesh);
  }
  scene.add(root);

  const box = new THREE.Box3().setFromObject(root);
  const size = box.getSize(new THREE.Vector3());
  const centre = box.getCenter(new THREE.Vector3());
  const radius = Math.max(size.length() / 2, 0.1);
  const distance = (radius * FIT_MARGIN) / Math.sin(THREE.MathUtils.degToRad(FIELD_OF_VIEW / 2));
  camera.position.copy(centre).add(new THREE.Vector3(0.7, 0.45, 1).normalize().multiplyScalar(distance));
  camera.near = distance / 100;
  camera.far = distance * 100;
  camera.updateProjectionMatrix();
  controls.target.copy(centre);
  controls.update();
  const grid = new THREE.GridHelper(Math.ceil(radius * 4), Math.max(4, Math.ceil(radius * 4)), 0x5c6b5f, 0x2b332c);
  grid.position.y = box.min.y;
  scene.add(grid);

  const clock = new THREE.Clock();
  let frame = 0;
  const loop = () => {
    frame = requestAnimationFrame(loop);
    mixer?.update(clock.getDelta());
    renderer.render(scene, camera);
  };
  loop();

  const resize = new ResizeObserver(() => {
    const w = container.clientWidth;
    const h = container.clientHeight;
    if (!w || !h) return;
    renderer.setSize(w, h);
    camera.aspect = w / h;
    camera.updateProjectionMatrix();
  });
  resize.observe(container);

  return {
    clips: animations.map(clip => clip.name),
    play(name) {
      if (!mixer) return;
      mixer.stopAllAction();
      const clip = animations.find(item => item.name === name);
      if (clip) mixer.clipAction(clip).play();
    },
    dispose() {
      cancelAnimationFrame(frame);
      resize.disconnect();
      controls.dispose();
      renderer.dispose();
      scene.traverse(object => {
        if (object instanceof THREE.Mesh) {
          object.geometry.dispose();
          const materials = Array.isArray(object.material) ? object.material : [object.material];
          for (const material of materials) {
            (material as THREE.MeshStandardMaterial).map?.dispose();
            material.dispose();
          }
        }
      });
    },
  };
}
