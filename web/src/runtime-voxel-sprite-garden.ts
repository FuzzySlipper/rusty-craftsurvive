import type {
  RustyApplicationContent,
  RustyApplicationRendererPort,
  RustyApplicationResource,
  RustyApplicationVoxelSpriteConfig,
  RustyApplicationVoxelSpriteDefinition,
  RustyApplicationVoxelSpriteExperimentPort,
  RustyApplicationVoxelSpriteMode,
  RustyApplicationVoxelSpritePreparedFrame,
  RustyApplicationVoxelSpriteReceipt,
} from '@rusty-engine/application-host';

type CameraPose = { position: [number, number, number]; yawDegrees: number; pitchDegrees: number };
export type VoxelSpriteSubject = 'spatial-wizard' | 'rigged-wizard' | 'knight';
export type VoxelSpriteProducer = 'runtime' | 'prepared';

interface FixtureResource {
  identity: string;
  contentHash: string;
  byteLength: number;
  mediaType: string;
  url: string;
}

interface FixtureFrame extends RustyApplicationVoxelSpritePreparedFrame {
  subject: VoxelSpriteSubject;
  sector: number;
  label: string;
  sourceNormalSpace: string;
}

interface FixtureOriginal {
  subject: VoxelSpriteSubject;
  asset: Record<string, unknown>;
  scale: number;
}

interface RuntimeFixture {
  schemaVersion: 1;
  source: { run: string; subjects: VoxelSpriteSubject[]; sectors: 16; preparedNormalSpace: string };
  resources: FixtureResource[];
  textures: Array<Record<string, unknown>>;
  frames: FixtureFrame[];
  originals: FixtureOriginal[];
  metrics: { frameCount: number; textureCount: number; resourceCount: number; totalResourceBytes: number };
}

export interface RuntimeVoxelSpriteGardenReadout {
  status: 'loading' | 'ready' | 'disposed';
  selectedSubject: VoxelSpriteSubject;
  producer: VoxelSpriteProducer;
  mode: RustyApplicationVoxelSpriteMode;
  sector: number;
  sectorLabel: string;
  autoSector: boolean;
  elevationDegrees: number;
  resolution: number;
  captureMilliseconds: number | null;
  steadyStateMilliseconds: number | null;
  textureBytes: number;
  drawCalls: number;
  sampleCount: number;
  fallbackPreservedCount: number;
  depthAmplitude: number;
  depthQuantizationSteps: number;
  splatOverlap: number;
  sourceNormalSpace: string;
  resourceCount: number;
  resourceBytes: number;
}

const SUBJECTS: readonly VoxelSpriteSubject[] = ['spatial-wizard', 'rigged-wizard', 'knight'];
const MODES: readonly RustyApplicationVoxelSpriteMode[] = [
  'sprite', 'relit', 'depth-parallax', 'sprite-splat', 'full-splat',
];
const PRODUCERS: Record<VoxelSpriteSubject, VoxelSpriteProducer> = {
  'spatial-wizard': 'runtime',
  'rigged-wizard': 'prepared',
  knight: 'runtime',
};
const SOURCE_HANDLE = 9_800_100;
const PLINTH_HANDLE = 9_800_200;
const LABEL_HANDLE = 9_800_300;
const ROW_DISTANCES = [7, 10.5, 14] as const;

export class RuntimeVoxelSpriteGarden {
  readonly #renderer: RustyApplicationRendererPort;
  readonly #readout: (value: RuntimeVoxelSpriteGardenReadout) => void;
  readonly #diagnostic: (value: string) => void;
  readonly #producer = { ...PRODUCERS };
  #experiment: RustyApplicationVoxelSpriteExperimentPort | null = null;
  #fixture: RuntimeFixture | null = null;
  #positions = new Map<VoxelSpriteSubject, {
    ground: [number, number, number];
    baseline: [number, number, number];
    enhanced: [number, number, number];
  }>();
  #gardenCenter: [number, number] = [0, 0];
  #selectedSubject: VoxelSpriteSubject = 'spatial-wizard';
  #mode: RustyApplicationVoxelSpriteMode = 'sprite-splat';
  #sector = 0;
  #autoSector = true;
  #elevationDegrees = 18;
  #resolution = 96;
  #depthAmplitude = 0.35;
  #depthQuantizationSteps = 8;
  #splatOverlap = 0.15;
  #status: RuntimeVoxelSpriteGardenReadout['status'] = 'loading';

  constructor(
    renderer: RustyApplicationRendererPort,
    readout: (value: RuntimeVoxelSpriteGardenReadout) => void,
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
    this.#configureLayout(camera);
    this.#sector = nearestSector(camera.position, this.#gardenCenter);
    const resources = await Promise.all(fixture.resources.map(fetchResource));
    return {
      frame: withOps(frame, [...frameOps(frame), ...this.#initialOps(fixture)]),
      resources: uniqueResources([...baseResources, ...resources]),
    };
  }

  activate(): void {
    if (this.#fixture === null || this.#status === 'disposed') return;
    this.#experiment?.dispose();
    this.#experiment = this.#renderer.createVoxelSpriteExperiment();
    for (const subject of SUBJECTS) {
      this.#apply(this.#experiment.create(this.#definition(subject, 'baseline')),
        `create ${subject} baseline`);
      this.#apply(this.#experiment.create(this.#definition(subject, 'enhanced')),
        `create ${subject} enhancement`);
    }
    this.#status = 'ready';
    void this.#publishLabels();
    this.#emit();
  }

  observe(camera: CameraPose): void {
    if (this.#status !== 'ready' || !this.#autoSector) return;
    const sector = nearestSector(camera.position, this.#gardenCenter);
    if (sector !== this.#sector) this.setSector(sector, true);
  }

  key(event: KeyboardEvent, down: boolean): boolean {
    if (!down || event.repeat || this.#status !== 'ready') return false;
    if (event.code === 'KeyU') {
      const index = (SUBJECTS.indexOf(this.#selectedSubject) + 1) % SUBJECTS.length;
      this.#selectedSubject = SUBJECTS[index]!;
      this.#emit();
      return true;
    }
    if (event.code === 'KeyI') {
      const index = (MODES.indexOf(this.#mode) + 1) % MODES.length;
      this.setMode(MODES[index]!);
      return true;
    }
    if (event.code === 'KeyO') {
      this.setProducer(this.#producer[this.#selectedSubject] === 'runtime' ? 'prepared' : 'runtime');
      return true;
    }
    if (event.code === 'KeyP') {
      this.recapture();
      return true;
    }
    return false;
  }

  setSubject(subject: VoxelSpriteSubject): void {
    this.#selectedSubject = subject;
    this.#emit();
  }

  setMode(mode: RustyApplicationVoxelSpriteMode): void {
    this.#mode = mode;
    this.#configureEnhanced({ mode });
  }

  setProducer(producer: VoxelSpriteProducer): void {
    if (this.#experiment === null || this.#fixture === null) return;
    const subject = this.#selectedSubject;
    const previous = this.#producer[subject];
    this.#producer[subject] = producer;
    const receipt = this.#experiment.replace(this.#definition(subject, 'enhanced'));
    if (!receipt.applied) this.#producer[subject] = previous;
    this.#apply(receipt, `switch ${subject} to ${producer}`);
    this.#emit();
  }

  setSector(sector: number, preserveAuto = false): void {
    if (this.#experiment === null || this.#fixture === null) return;
    const next = ((Math.round(sector) % 16) + 16) % 16;
    if (!preserveAuto) this.#autoSector = false;
    this.#sector = next;
    for (const subject of SUBJECTS) {
      this.#apply(this.#experiment.replace(this.#definition(subject, 'baseline')),
        `replace ${subject} baseline sector`);
      if (this.#producer[subject] === 'prepared') {
        this.#apply(this.#experiment.replace(this.#definition(subject, 'enhanced')),
          `replace ${subject} prepared sector`);
      }
    }
    this.#emit();
  }

  setAutoSector(enabled: boolean): void { this.#autoSector = enabled; this.#emit(); }

  setElevation(value: number): void {
    this.#elevationDegrees = bounded(value, -45, 75);
    this.#emit();
  }

  setResolution(value: number): void {
    this.#resolution = Math.round(bounded(value, 32, 256));
    this.#emit();
  }

  setDepthAmplitude(value: number): void {
    this.#depthAmplitude = bounded(value, 0, 2);
    this.#configureEnhanced({ depthAmplitude: this.#depthAmplitude });
  }

  setDepthQuantizationSteps(value: number): void {
    this.#depthQuantizationSteps = Math.round(bounded(value, 0, 32));
    this.#configureEnhanced({ depthQuantizationSteps: this.#depthQuantizationSteps });
  }

  setSplatOverlap(value: number): void {
    this.#splatOverlap = bounded(value, 0, 1.5);
    this.#configureEnhanced({ splatOverlap: this.#splatOverlap });
  }

  recapture(): void {
    if (this.#experiment === null) return;
    const subject = this.#selectedSubject;
    if (this.#producer[subject] !== 'runtime') {
      this.#diagnostic(`${subject} uses a prepared frame; switch source to runtime before recapture`);
      return;
    }
    this.#apply(this.#experiment.recapture(`${subject}-enhanced`, this.#captureSettings()),
      `recapture ${subject}`);
    this.#emit();
  }

  probeFailureFallback(): void {
    if (this.#experiment === null) return;
    const subject = this.#selectedSubject;
    const before = this.#entry(subject)?.enhancement.revision;
    const failed = this.#experiment.replace({
      ...this.#definition(subject, 'enhanced'),
      source: { kind: 'retained', handle: 9_999_999, capture: this.#captureSettings() },
    });
    const after = failed.readout.entries.find(({ id }) => id === `${subject}-enhanced`)
      ?.enhancement.revision;
    if (failed.applied || before !== after) {
      this.#diagnostic('fallback probe unexpectedly changed the live representation');
      return;
    }
    this.#diagnostic(`fallback probe passed: ${subject} kept revision ${String(after)}`);
    this.#emit();
  }

  dispose(): void {
    if (this.#status === 'disposed') return;
    this.#experiment?.dispose();
    this.#experiment = null;
    this.#status = 'disposed';
    this.#emit();
  }

  #configureEnhanced(patch: Partial<RustyApplicationVoxelSpriteConfig>): void {
    if (this.#experiment === null) return;
    for (const subject of SUBJECTS) {
      this.#apply(this.#experiment.configure(`${subject}-enhanced`, patch),
        `configure ${subject}`);
    }
    this.#emit();
  }

  #definition(
    subject: VoxelSpriteSubject,
    role: 'baseline' | 'enhanced',
  ): RustyApplicationVoxelSpriteDefinition {
    const positions = this.#positions.get(subject);
    if (positions === undefined) throw new Error(`missing ${subject} garden position`);
    const prepared = preparedFrame(this.#fixture!, subject, this.#sector);
    const producer = role === 'baseline' ? 'prepared' : this.#producer[subject];
    return {
      id: `${subject}-${role}`,
      source: producer === 'prepared'
        ? { kind: 'prepared', frame: prepared }
        : {
            kind: 'retained',
            handle: SOURCE_HANDLE + SUBJECTS.indexOf(subject),
            capture: this.#captureSettings(),
          },
      transform: {
        position: positions[role],
        width: 2.5,
        height: 2.5,
      },
      mode: role === 'baseline' ? 'sprite' : this.#mode,
      config: {
        sampleColumns: 48,
        sampleRows: 48,
        depthAmplitude: role === 'baseline' ? 0 : this.#depthAmplitude,
        depthQuantizationSteps: this.#depthQuantizationSteps,
        splatOverlap: this.#splatOverlap,
        baseSpriteContribution: 0.7,
        normalInfluence: 0.65,
      },
    };
  }

  #captureSettings() {
    return {
      resolution: this.#resolution,
      azimuthDegrees: this.#sector * 22.5,
      elevationDegrees: this.#elevationDegrees,
      near: 0.1,
      far: 40,
    };
  }

  #entry(subject: VoxelSpriteSubject) {
    return this.#experiment?.readout().entries.find(({ id }) => id === `${subject}-enhanced`);
  }

  #apply(receipt: RustyApplicationVoxelSpriteReceipt, operation: string): void {
    if (!receipt.applied) {
      this.#diagnostic(`${operation} rejected: ${receipt.diagnostics.map(({ message }) => message).join('; ')}`);
    }
  }

  #configureLayout(camera: CameraPose): void {
    const yaw = camera.yawDegrees * Math.PI / 180;
    const forward: readonly [number, number] = [Math.sin(yaw), -Math.cos(yaw)];
    const right: readonly [number, number] = [Math.cos(yaw), Math.sin(yaw)];
    const baseY = camera.position[1] - 1.95;
    SUBJECTS.forEach((subject, index) => {
      const distance = ROW_DISTANCES[index];
      const center: [number, number, number] = [
        camera.position[0] + forward[0] * distance,
        baseY,
        camera.position[2] + forward[1] * distance,
      ];
      this.#positions.set(subject, {
        ground: center,
        baseline: [center[0] - right[0] * 1.5, baseY + 1.25, center[2] - right[1] * 1.5],
        enhanced: [center[0] + right[0] * 1.5, baseY + 1.25, center[2] + right[1] * 1.5],
      });
    });
    const center = this.#positions.get('rigged-wizard')!.ground;
    this.#gardenCenter = [center[0], center[2]];
  }

  #initialOps(fixture: RuntimeFixture): unknown[] {
    const ops: unknown[] = [
      ...fixture.textures.map((texture) => ({ op: 'defineTexture', texture })),
      ...fixture.originals.map((original) => ({ op: 'defineAnimatedMesh', asset: original.asset })),
    ];
    fixture.originals.forEach((original, index) => {
      const positions = this.#positions.get(original.subject)!;
      ops.push({
        op: 'createAnimatedMeshInstance',
        handle: SOURCE_HANDLE + index,
        parent: null,
        instance: {
          asset: original.asset['asset'],
          transform: transform(positions.ground, original.scale),
          visible: false,
          materialOverrides: [],
          playback: null,
          metadata: metadata(`runtime-voxel-sprite-source-${original.subject}`, 700_600 + index),
        },
      });
      for (const [column, position] of [positions.baseline, positions.enhanced].entries()) {
        ops.push({
          op: 'create',
          handle: PLINTH_HANDLE + index * 2 + column,
          parent: null,
          node: primitiveNode(
            `runtime-voxel-sprite-${original.subject}-${column === 0 ? 'baseline' : 'enhanced'}-plinth`,
            [position[0], positions.ground[1] - 0.1, position[2]],
            column === 0 ? [0.1, 0.5, 1, 1] : [1, 0.3, 0.05, 1],
          ),
        });
      }
    });
    return ops;
  }

  async #publishLabels(): Promise<void> {
    const ops = SUBJECTS.flatMap((subject, index) => {
      const positions = this.#positions.get(subject)!;
      return (['baseline', 'enhanced'] as const).map((role, column) => ({
        domain: 'billboard' as const,
        meta: { sequence: index * 2 + column },
        op: {
          op: 'create' as const,
          handle: LABEL_HANDLE + index * 2 + column,
          descriptor: {
            anchor: {
              kind: 'world' as const,
              position: [positions[role][0], positions[role][1] + 1.55, positions[role][2]],
            },
            content: {
              kind: 'text' as const,
              localizationKey: `craftsurvive.voxelSprite.${subject}.${role}`,
              fallbackText: `${subject.replaceAll('-', ' ')} · ${role === 'baseline' ? 'BLUE baseline' : 'ORANGE enhanced'}`,
              arguments: [],
            },
            font: { kind: 'system' as const, family: 'monospace' },
            heightPixels: 15,
            color: [1, 1, 1, 1] as const,
            background: role === 'baseline'
              ? [0.02, 0.18, 0.45, 0.9] as const
              : [0.55, 0.12, 0.02, 0.9] as const,
            maxDistance: 45,
            layer: 'alwaysOnTop' as const,
            visible: true,
          },
        },
      }));
    });
    const receipt = await this.#renderer.applyPresentation({ schemaVersion: 1, ops });
    for (const diagnostic of receipt.diagnostics) {
      this.#diagnostic(`voxel-sprite label ${diagnostic.code}: ${diagnostic.message}`);
    }
  }

  #emit(): void {
    const entry = this.#entry(this.#selectedSubject);
    const fixture = this.#fixture;
    this.#readout({
      status: this.#status,
      selectedSubject: this.#selectedSubject,
      producer: this.#producer[this.#selectedSubject],
      mode: this.#mode,
      sector: this.#sector,
      sectorLabel: `dir-${pad(this.#sector)}`,
      autoSector: this.#autoSector,
      elevationDegrees: this.#elevationDegrees,
      resolution: this.#resolution,
      captureMilliseconds: entry?.enhancement.captureCpuSubmissionMilliseconds ?? null,
      steadyStateMilliseconds: entry?.enhancement.steadyStateCpuSubmissionMilliseconds ?? null,
      textureBytes: entry?.enhancement.frameTextureBytes ?? 0,
      drawCalls: entry?.enhancement.expectedDrawCalls ?? 0,
      sampleCount: entry?.enhancement.geometrySampleCount ?? 0,
      fallbackPreservedCount: entry?.fallbackPreservedCount ?? 0,
      depthAmplitude: this.#depthAmplitude,
      depthQuantizationSteps: this.#depthQuantizationSteps,
      splatOverlap: this.#splatOverlap,
      sourceNormalSpace: fixture?.source.preparedNormalSpace ?? 'loading',
      resourceCount: fixture?.metrics.resourceCount ?? 0,
      resourceBytes: fixture?.metrics.totalResourceBytes ?? 0,
    });
  }
}

async function loadFixture(): Promise<RuntimeFixture> {
  const response = await fetch('/assets/depth-splat/runtime-v1.json', { cache: 'no-store' });
  if (!response.ok) throw new Error(`runtime voxel-sprite fixture returned ${String(response.status)}`);
  const fixture = await response.json() as RuntimeFixture;
  if (fixture.schemaVersion !== 1 || fixture.source.run !== 'depth-splat-20260815-001'
    || fixture.frames.length !== 48 || fixture.textures.length !== 192
    || fixture.originals.length !== 3) {
    throw new Error('runtime voxel-sprite fixture inventory is incomplete');
  }
  return fixture;
}

async function fetchResource(resource: FixtureResource): Promise<RustyApplicationResource> {
  const response = await fetch(resource.url, { cache: 'no-store' });
  if (!response.ok) throw new Error(`voxel-sprite resource ${resource.url} returned ${String(response.status)}`);
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.byteLength !== resource.byteLength) throw new Error(`voxel-sprite resource ${resource.identity} changed length`);
  return { identity: resource.identity, contentHash: resource.contentHash, mediaType: resource.mediaType, bytes };
}

function preparedFrame(
  fixture: RuntimeFixture,
  subject: VoxelSpriteSubject,
  sector: number,
): RustyApplicationVoxelSpritePreparedFrame {
  const frame = fixture.frames.find((candidate) => candidate.subject === subject && candidate.sector === sector);
  if (frame === undefined) throw new Error(`missing prepared frame ${subject}/dir-${pad(sector)}`);
  return frame;
}

function nearestSector(position: readonly [number, number, number], center: readonly [number, number]): number {
  const degrees = Math.atan2(position[0] - center[0], position[2] - center[1]) * 180 / Math.PI;
  return Math.round(((degrees + 360) % 360) / 22.5) % 16;
}

function frameOps(frame: Record<string, unknown>): unknown[] {
  const value = frame['ops'];
  if (!Array.isArray(value)) throw new Error('session frame ops are unavailable');
  return value;
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

function uniqueResources(resources: RustyApplicationResource[]): RustyApplicationResource[] {
  const result = new Map<string, RustyApplicationResource>();
  for (const resource of resources) result.set(resource.identity, resource);
  return [...result.values()];
}

function transform(position: readonly [number, number, number], scale: number) {
  return { translation: position, rotation: [0, 0, 0, 1], scale: [scale, scale, scale] };
}

function metadata(label: string, entity: number) {
  return { sourceEntity: entity, sourceSceneNode: null, tags: ['runtime-voxel-sprite'], label };
}

function primitiveNode(label: string, position: readonly number[], color: readonly number[]) {
  return {
    geometry: { kind: 'cube' },
    material: { color, wireframe: false },
    transform: { translation: position, rotation: [0, 0, 0, 1], scale: [1.15, 0.1, 1.05] },
    visible: true,
    layer: 'scene',
    metadata: { sourceEntity: null, sourceSceneNode: null, tags: ['runtime-voxel-sprite', 'plinth'], label },
  };
}

function bounded(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, value));
}

function pad(value: number): string { return String(value).padStart(2, '0'); }
