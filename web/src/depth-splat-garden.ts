import type {
  RustyApplicationContent,
  RustyApplicationRendererPort,
  RustyApplicationResource,
} from '@rusty-engine/application-host';

type CameraPose = { position: [number, number, number]; yawDegrees: number; pitchDegrees: number };
type Subject = 'spatial-wizard' | 'rigged-wizard' | 'knight';
type Variant = 'quad' | 'flat' | 'compressed' | 'physical' | 'tangent';

interface GardenResource {
  identity: string;
  contentHash: string;
  byteLength: number;
  mediaType: string;
  url: string;
}

interface GardenDepiction {
  subject: Subject;
  variant: Variant;
  sector: number;
  asset: string;
  vertices: number;
  triangles: number;
  sourceGlbBytes: number;
  uploadedBytes: number;
}

interface GardenOriginal {
  subject: Subject;
  asset: Record<string, unknown>;
  scale: number;
  sourcePath: string;
  sourceFile: string;
}

interface GardenFixture {
  schemaVersion: 1;
  source: { project: string; task: number; run: string; subjects: Subject[]; variants: Variant[]; sectors: 16 };
  resources: GardenResource[];
  textures: Array<Record<string, unknown>>;
  materials: Array<Record<string, unknown>>;
  assets: Array<Record<string, unknown>>;
  originals: GardenOriginal[];
  depictions: GardenDepiction[];
  metrics: {
    depictionCount: number;
    originalCount: number;
    packedMeshBytes: number;
    textureBytes: number;
    originalBytes: number;
    totalResourceBytes: number;
  };
}

export interface DepthSplatGardenReadout {
  status: 'loading' | 'ready';
  sectorCount: 8 | 16;
  selectedSector: number;
  selectedSectorLabel: string;
  angularOffsetDegrees: number;
  hysteresis: boolean;
  visibleOriginals: number;
  visibleDepictions: number;
  visibleTriangles: number;
  resourceCount: number;
  resourceBytes: number;
}

const SUBJECTS: readonly Subject[] = ['spatial-wizard', 'rigged-wizard', 'knight'];
const VARIANTS: readonly Variant[] = ['quad', 'flat', 'compressed', 'physical', 'tangent'];
const COLUMNS = [-2.75, -1.65, -0.55, 0.55, 1.65, 2.75] as const;
const ROW_DISTANCES = [7, 10.5, 14] as const;
const ROW_OFFSETS = [-5.5, 0, 5.5] as const;
const PLINTH_HANDLE = 9_700_000;
const ORIGINAL_HANDLE = 9_700_100;
const DEPICTION_HANDLE = 9_700_200;

export class DepthSplatGarden {
  readonly #renderer: RustyApplicationRendererPort;
  readonly #readout: (value: DepthSplatGardenReadout) => void;
  readonly #diagnostic: (value: string) => void;
  #fixture: GardenFixture | null = null;
  #active = false;
  #sectorCount: 8 | 16 = 16;
  #selectedSector = 0;
  #angularOffsetDegrees = 0;
  #hysteresis = true;
  #lastCamera: CameraPose | null = null;
  #gardenCenter: [number, number] = [0, 0];
  #baseY = 0;
  #positions: Array<Array<[number, number, number]>> = [];

  constructor(
    renderer: RustyApplicationRendererPort,
    readout: (value: DepthSplatGardenReadout) => void,
    diagnostic: (value: string) => void,
  ) {
    this.#renderer = renderer;
    this.#readout = readout;
    this.#diagnostic = diagnostic;
    this.#emit();
  }

  async prepare(
    frame: Record<string, unknown>,
    baseResources: RustyApplicationResource[],
    camera: CameraPose,
  ): Promise<RustyApplicationContent> {
    const fixture = this.#fixture ?? await loadFixture();
    this.#fixture = fixture;
    this.#active = false;
    this.#lastCamera = camera;
    this.#configureLayout(camera);
    this.#selectedSector = this.#nearestSector(camera.position);
    this.#angularOffsetDegrees = this.#sectorOffset(camera.position, this.#selectedSector);
    const gardenResources = await Promise.all(fixture.resources.map(fetchResource));
    const ops = [...frameOps(frame), ...this.#initialOps(fixture)];
    return {
      frame: withOps(frame, ops),
      resources: uniqueResources([...baseResources, ...gardenResources]),
    };
  }

  activate(): void {
    this.#active = true;
    this.#emit();
  }

  observe(camera: CameraPose): void {
    this.#lastCamera = camera;
    if (!this.#active || this.#fixture === null) return;
    const next = this.#selectSector(camera.position);
    this.#angularOffsetDegrees = this.#sectorOffset(camera.position, next);
    if (next !== this.#selectedSector) {
      const receipt = this.#renderer.applyFrame(this.#sectorFrame(this.#fixture, next));
      if (!receipt.applied) {
        this.#diagnostic(`depth-splat sector rejected: ${receipt.diagnostics.map(({ message }) => message).join('; ')}`);
        return;
      }
      this.#selectedSector = next;
    }
    this.#emit();
  }

  key(event: KeyboardEvent, down: boolean): boolean {
    if (!down || event.repeat) return false;
    if (event.code === 'KeyI') {
      this.#sectorCount = this.#sectorCount === 16 ? 8 : 16;
      this.#reselect();
      return true;
    }
    if (event.code === 'KeyO') {
      this.#hysteresis = !this.#hysteresis;
      this.#reselect();
      return true;
    }
    return false;
  }

  dispose(): void { this.#active = false; }

  #reselect(): void {
    if (this.#lastCamera === null || this.#fixture === null || !this.#active) {
      this.#emit();
      return;
    }
    const next = this.#nearestSector(this.#lastCamera.position);
    this.#angularOffsetDegrees = this.#sectorOffset(this.#lastCamera.position, next);
    if (next !== this.#selectedSector) {
      const receipt = this.#renderer.applyFrame(this.#sectorFrame(this.#fixture, next));
      if (!receipt.applied) {
        this.#diagnostic(`depth-splat mode rejected: ${receipt.diagnostics.map(({ message }) => message).join('; ')}`);
        return;
      }
      this.#selectedSector = next;
    }
    this.#emit();
  }

  #selectSector(position: readonly [number, number, number]): number {
    if (!this.#hysteresis) return this.#nearestSector(position);
    const step = 360 / this.#sectorCount;
    const offset = Math.abs(this.#sectorOffset(position, this.#selectedSector));
    return offset <= step / 2 + 3 ? this.#selectedSector : this.#nearestSector(position);
  }

  #nearestSector(position: readonly [number, number, number]): number {
    const step = 360 / this.#sectorCount;
    const selection = Math.round(this.#cameraAngle(position) / step) % this.#sectorCount;
    return selection * (16 / this.#sectorCount);
  }

  #sectorOffset(position: readonly [number, number, number], sector: number): number {
    return signedAngle(this.#cameraAngle(position) - sector * 22.5);
  }

  #cameraAngle(position: readonly [number, number, number]): number {
    const degrees = Math.atan2(
      position[0] - this.#gardenCenter[0],
      position[2] - this.#gardenCenter[1],
    ) * 180 / Math.PI;
    return (degrees + 360) % 360;
  }

  #configureLayout(camera: CameraPose): void {
    const yaw = camera.yawDegrees * Math.PI / 180;
    const forward: readonly [number, number] = [Math.sin(yaw), -Math.cos(yaw)];
    const right: readonly [number, number] = [Math.cos(yaw), Math.sin(yaw)];
    this.#baseY = camera.position[1] - 1.95;
    this.#gardenCenter = [
      camera.position[0] + forward[0] * ROW_DISTANCES[1],
      camera.position[2] + forward[1] * ROW_DISTANCES[1],
    ];
    this.#positions = ROW_DISTANCES.map((distance, row) => COLUMNS.map((offset) => [
      camera.position[0] + forward[0] * distance + right[0] * (ROW_OFFSETS[row] + offset),
      this.#baseY,
      camera.position[2] + forward[1] * distance + right[1] * (ROW_OFFSETS[row] + offset),
    ]));
  }

  #initialOps(fixture: GardenFixture): unknown[] {
    const ops: unknown[] = [
      ...fixture.textures.map((texture) => ({ op: 'defineTexture', texture })),
      ...fixture.materials.map((material) => ({ op: 'defineMaterial', material })),
      ...fixture.assets.map((asset) => ({ op: 'defineStaticMesh', asset })),
      ...fixture.originals.map((original) => ({ op: 'defineAnimatedMesh', asset: original.asset })),
    ];
    for (let row = 0; row < SUBJECTS.length; row += 1) {
      for (let column = 0; column < COLUMNS.length; column += 1) {
        ops.push({
          op: 'create',
          handle: PLINTH_HANDLE + row * COLUMNS.length + column,
          parent: null,
          node: primitiveNode(
            `depth-splat-plinth-${SUBJECTS[row]}-${String(column)}`,
            [this.#positions[row][column][0], this.#baseY - 0.12, this.#positions[row][column][2]],
            [1.15, 0.1, 1.05],
            row === 0 ? [0.15, 0.26, 0.34, 1] : row === 1 ? [0.25, 0.18, 0.34, 1] : [0.34, 0.24, 0.14, 1],
          ),
        });
      }
      const original = requiredOriginal(fixture, SUBJECTS[row]);
      ops.push({
        op: 'createAnimatedMeshInstance',
        handle: ORIGINAL_HANDLE + row,
        parent: null,
        instance: {
          asset: original.asset['asset'],
          transform: transform(this.#positions[row][0], original.scale),
          visible: true,
          materialOverrides: [],
          playback: null,
          metadata: metadata(`depth-splat-${SUBJECTS[row]}-original`, 697_900 + row, ['depth-splat', 'original', SUBJECTS[row]]),
        },
      });
    }
    ops.push(...this.#depictionCreates(fixture, this.#selectedSector));
    return ops;
  }

  #sectorFrame(fixture: GardenFixture, sector: number): Record<string, unknown> {
    return {
      schemaVersion: 1,
      ops: [
        ...Array.from({ length: SUBJECTS.length * VARIANTS.length }, (_, index) => ({
          op: 'destroy', handle: DEPICTION_HANDLE + index,
        })),
        ...this.#depictionCreates(fixture, sector),
      ],
    };
  }

  #depictionCreates(fixture: GardenFixture, sector: number): unknown[] {
    return SUBJECTS.flatMap((subject, row) => VARIANTS.map((variant, variantIndex) => {
      const index = row * VARIANTS.length + variantIndex;
      const depiction = requiredDepiction(fixture, subject, variant, sector);
      return {
        op: 'createStaticMeshInstance',
        handle: DEPICTION_HANDLE + index,
        parent: null,
        instance: {
          asset: depiction.asset,
          transform: transform(this.#positions[row][variantIndex + 1], 1),
          visible: true,
          materialOverrides: [],
          metadata: metadata(
            `depth-splat-${subject}-${variant}-dir-${pad(sector)}`,
            697_920 + index,
            ['depth-splat', subject, variant].sort(),
          ),
        },
      };
    }));
  }

  #emit(): void {
    const fixture = this.#fixture;
    const triangles = fixture === null ? 0 : SUBJECTS.reduce((sum, subject) =>
      sum + VARIANTS.reduce((variantSum, variant) =>
        variantSum + requiredDepiction(fixture, subject, variant, this.#selectedSector).triangles, 0), 0);
    this.#readout({
      status: this.#active ? 'ready' : 'loading',
      sectorCount: this.#sectorCount,
      selectedSector: this.#selectedSector,
      selectedSectorLabel: `dir-${pad(this.#selectedSector)}`,
      angularOffsetDegrees: this.#angularOffsetDegrees,
      hysteresis: this.#hysteresis,
      visibleOriginals: fixture?.metrics.originalCount ?? 0,
      visibleDepictions: fixture === null ? 0 : SUBJECTS.length * VARIANTS.length,
      visibleTriangles: triangles,
      resourceCount: fixture?.resources.length ?? 0,
      resourceBytes: fixture?.metrics.totalResourceBytes ?? 0,
    });
  }
}

async function loadFixture(): Promise<GardenFixture> {
  const response = await fetch('/assets/depth-splat/garden-v1.json', { cache: 'no-store' });
  if (!response.ok) throw new Error(`depth-splat fixture returned ${String(response.status)}`);
  const value: unknown = await response.json();
  if (typeof value !== 'object' || value === null || (value as { schemaVersion?: unknown }).schemaVersion !== 1) {
    throw new Error('depth-splat fixture has an unsupported schema');
  }
  const fixture = value as GardenFixture;
  if (fixture.source?.run !== 'depth-splat-20260815-001' || fixture.assets?.length !== 240
    || fixture.depictions?.length !== 240 || fixture.originals?.length !== 3) {
    throw new Error('depth-splat fixture inventory is incomplete');
  }
  return fixture;
}

async function fetchResource(resource: GardenResource): Promise<RustyApplicationResource> {
  const response = await fetch(resource.url, { cache: 'no-store' });
  if (!response.ok) throw new Error(`depth-splat resource ${resource.url} returned ${String(response.status)}`);
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.byteLength !== resource.byteLength) throw new Error(`depth-splat resource ${resource.identity} changed length`);
  return { identity: resource.identity, contentHash: resource.contentHash, mediaType: resource.mediaType, bytes };
}

function uniqueResources(resources: RustyApplicationResource[]): RustyApplicationResource[] {
  const unique = new Map<string, RustyApplicationResource>();
  for (const resource of resources) {
    const existing = unique.get(resource.identity);
    if (existing !== undefined && existing.contentHash !== resource.contentHash) {
      throw new Error(`resource identity collision for ${resource.identity}`);
    }
    unique.set(resource.identity, resource);
  }
  return [...unique.values()];
}

function frameOps(frame: Record<string, unknown>): unknown[] {
  if (frame['schemaVersion'] !== 1 || !Array.isArray(frame['ops'])) throw new Error('welcome frame is malformed');
  return frame['ops'];
}

function withOps(frame: Record<string, unknown>, ops: unknown[]): Record<string, unknown> {
  const publication = frame['publication'];
  return {
    ...frame,
    ops,
    ...(typeof publication === 'object' && publication !== null
      ? { publication: { ...publication, operationCount: ops.length } }
      : {}),
  };
}

function requiredOriginal(fixture: GardenFixture, subject: Subject): GardenOriginal {
  const value = fixture.originals.find((candidate) => candidate.subject === subject);
  if (value === undefined) throw new Error(`missing original ${subject}`);
  return value;
}

function requiredDepiction(
  fixture: GardenFixture,
  subject: Subject,
  variant: Variant,
  sector: number,
): GardenDepiction {
  const value = fixture.depictions.find((candidate) =>
    candidate.subject === subject && candidate.variant === variant && candidate.sector === sector);
  if (value === undefined) throw new Error(`missing depiction ${subject}/${variant}/dir-${pad(sector)}`);
  return value;
}

function signedAngle(value: number): number { return ((value + 540) % 360) - 180; }
function pad(value: number): string { return String(value).padStart(2, '0'); }
function transform(translation: readonly [number, number, number], scale: number) {
  return { translation, rotation: [0, 0, 0, 1], scale: [scale, scale, scale] };
}
function metadata(label: string, sourceEntity: number, tags: string[]) {
  return { sourceEntity, sourceSceneNode: null, tags: [...tags].sort(), label };
}
function primitiveNode(
  label: string,
  translation: readonly [number, number, number],
  scale: readonly [number, number, number],
  color: readonly [number, number, number, number],
) {
  return {
    geometry: { kind: 'cube' },
    material: { color, wireframe: false },
    transform: { translation, rotation: [0, 0, 0, 1], scale },
    visible: true,
    layer: 'scene',
    metadata: metadata(label, 697_990, ['comparison', 'depth-splat']),
  };
}
