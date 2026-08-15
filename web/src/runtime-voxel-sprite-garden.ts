import type {
  RustyApplicationContent,
  RustyApplicationRendererPort,
  RustyApplicationResource,
  RustyApplicationVoxelSpriteCaptureSettings,
  RustyApplicationVoxelSpriteConfig,
  RustyApplicationVoxelSpriteDefinition,
  RustyApplicationVoxelSpriteEnhancementReadout,
  RustyApplicationVoxelSpriteExperimentPort,
  RustyApplicationVoxelSpriteMode,
  RustyApplicationVoxelSpriteReceipt,
} from '@rusty-engine/application-host';

type CameraPose = { position: [number, number, number]; yawDegrees: number; pitchDegrees: number };
export type VoxelSpriteSubject = 'spatial-wizard' | 'rigged-wizard' | 'knight';
export type VoxelSpriteSide = 'baseline' | 'enhanced';
export type VoxelSpriteCaptureLightingMode = 'isolated' | 'scene';
export type VoxelSpritePostLightingMode = 'captured' | 'normal';
export type VoxelSpriteSplatBlendMode = RustyApplicationVoxelSpriteConfig['splatBlendMode'];

interface ModelResource {
  identity: string;
  contentHash: string;
  byteLength: number;
  mediaType: string;
  url: string;
}

interface RuntimeModel {
  subject: VoxelSpriteSubject;
  asset: Record<string, unknown>;
  scale: number;
}

interface RuntimeModelManifest {
  schemaVersion: 2;
  source: { kind: 'runtime-models'; subjects: VoxelSpriteSubject[] };
  resources: ModelResource[];
  models: RuntimeModel[];
  metrics: { resourceCount: number; totalResourceBytes: number };
}

interface SideLighting {
  captureMode: VoxelSpriteCaptureLightingMode;
  captureAmbient: number;
  captureKey: number;
  captureFill: number;
  postMode: VoxelSpritePostLightingMode;
  postAmbient: number;
  postDiffuse: number;
  outputGain: number;
  lightAzimuthDegrees: number;
  lightElevationDegrees: number;
}

export interface RuntimeVoxelSpriteGardenReadout {
  status: 'loading' | 'ready' | 'disposed';
  selectedSubject: VoxelSpriteSubject;
  selectedSide: VoxelSpriteSide;
  source: 'retained/runtime';
  mode: RustyApplicationVoxelSpriteMode;
  sector: number;
  sectorLabel: string;
  autoSector: boolean;
  elevationDegrees: number;
  resolution: number;
  appliedResolution: number;
  capturePending: boolean;
  captureOutputBytesEstimate: number;
  captureTemporaryDepthBytesEstimate: number;
  captureLightingMode: VoxelSpriteCaptureLightingMode;
  captureAmbient: number;
  captureKey: number;
  captureFill: number;
  postLightingMode: VoxelSpritePostLightingMode;
  postAmbient: number;
  postDiffuse: number;
  outputGain: number;
  lightAzimuthDegrees: number;
  lightElevationDegrees: number;
  captureSettingsMatched: boolean;
  allLightingMatched: boolean;
  captureMilliseconds: number | null;
  steadyStateMilliseconds: number | null;
  textureBytes: number;
  drawCalls: number;
  sampleCount: number;
  fallbackPreservedCount: number;
  depthAmplitude: number;
  depthQuantizationSteps: number;
  splatResolution: number;
  appliedSplatResolution: number;
  splatDensityPending: boolean;
  splatOverlap: number;
  splatOpacity: number;
  splatBlendMode: VoxelSpriteSplatBlendMode;
  composition: RustyApplicationVoxelSpriteEnhancementReadout['composition'] | 'n/a';
  resourceCount: number;
  resourceBytes: number;
}

const SUBJECTS: readonly VoxelSpriteSubject[] = ['spatial-wizard', 'rigged-wizard', 'knight'];
const SIDES: readonly VoxelSpriteSide[] = ['baseline', 'enhanced'];
const MODES: readonly RustyApplicationVoxelSpriteMode[] = [
  'sprite', 'depth-parallax', 'sprite-splat', 'full-splat',
];
const DEFAULT_LIGHTING: SideLighting = {
  captureMode: 'isolated',
  captureAmbient: 1.8,
  captureKey: 3,
  captureFill: 1.4,
  postMode: 'captured',
  postAmbient: 0.35,
  postDiffuse: 0.9,
  outputGain: 1.1,
  lightAzimuthDegrees: 35,
  lightElevationDegrees: 45,
};
const SOURCE_HANDLE = 9_800_100;
const PLINTH_HANDLE = 9_800_200;
const LABEL_HANDLE = 9_800_300;
const ROW_DISTANCES = [7, 10.5, 14] as const;
const HIGH_COST_CAPTURE_RESOLUTION = 512;

export class RuntimeVoxelSpriteGarden {
  readonly #renderer: RustyApplicationRendererPort;
  readonly #readout: (value: RuntimeVoxelSpriteGardenReadout) => void;
  readonly #diagnostic: (value: string) => void;
  readonly #lighting: Record<VoxelSpriteSide, SideLighting> = {
    baseline: { ...DEFAULT_LIGHTING },
    enhanced: { ...DEFAULT_LIGHTING },
  };
  #experiment: RustyApplicationVoxelSpriteExperimentPort | null = null;
  #manifest: RuntimeModelManifest | null = null;
  #positions = new Map<VoxelSpriteSubject, {
    ground: [number, number, number];
    baseline: [number, number, number];
    enhanced: [number, number, number];
  }>();
  #gardenCenter: [number, number] = [0, 0];
  #selectedSubject: VoxelSpriteSubject = 'spatial-wizard';
  #selectedSide: VoxelSpriteSide = 'enhanced';
  #mode: RustyApplicationVoxelSpriteMode = 'sprite-splat';
  #sector = 0;
  #autoSector = true;
  #elevationDegrees = 18;
  #resolution = 192;
  #depthAmplitude = 0.35;
  #depthQuantizationSteps = 8;
  #splatResolution = 48;
  #splatOverlap = 0.15;
  #splatOpacity = 1;
  #splatBlendMode: VoxelSpriteSplatBlendMode = 'depth-write';
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
    const manifest = this.#manifest ?? await loadManifest();
    this.#manifest = manifest;
    this.#configureLayout(camera);
    this.#sector = nearestSector(camera.position, this.#gardenCenter);
    const resources = await Promise.all(manifest.resources.map(fetchResource));
    return {
      frame: withOps(frame, [...frameOps(frame), ...this.#initialOps(manifest)]),
      resources: uniqueResources([...baseResources, ...resources]),
    };
  }

  activate(): void {
    if (this.#manifest === null || this.#status === 'disposed') return;
    this.#experiment?.dispose();
    this.#experiment = this.#renderer.createVoxelSpriteExperiment();
    for (const subject of SUBJECTS) {
      for (const side of SIDES) {
        this.#apply(this.#experiment.create(this.#definition(subject, side)), `create ${subject} ${side}`);
      }
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
      this.#selectedSubject = SUBJECTS[(SUBJECTS.indexOf(this.#selectedSubject) + 1) % SUBJECTS.length]!;
      this.#emit();
      return true;
    }
    if (event.code === 'KeyI') {
      this.setMode(MODES[(MODES.indexOf(this.#mode) + 1) % MODES.length]!);
      return true;
    }
    if (event.code === 'KeyO') {
      this.#selectedSide = this.#selectedSide === 'baseline' ? 'enhanced' : 'baseline';
      this.#emit();
      return true;
    }
    if (event.code === 'KeyP') {
      this.recapturePair();
      return true;
    }
    return false;
  }

  setSubject(subject: VoxelSpriteSubject): void { this.#selectedSubject = subject; this.#emit(); }
  setSide(side: VoxelSpriteSide): void { this.#selectedSide = side; this.#emit(); }

  setMode(mode: RustyApplicationVoxelSpriteMode): void {
    if (!MODES.includes(mode)) return;
    this.#mode = mode;
    this.#configureSide('enhanced', { mode });
  }

  setSector(sector: number, preserveAuto = false): void {
    if (this.#experiment === null) return;
    if (!preserveAuto) this.#autoSector = false;
    this.#sector = ((Math.round(sector) % 16) + 16) % 16;
    if (this.#resolution >= HIGH_COST_CAPTURE_RESOLUTION) {
      this.#diagnostic(
        `sector ${String(this.#sector)} queued at ${String(this.#resolution)}px; use an explicit recapture action`,
      );
      this.#emit();
      return;
    }
    this.#recaptureAll('sector');
  }

  setAutoSector(enabled: boolean): void { this.#autoSector = enabled; this.#emit(); }
  setElevation(value: number): void { this.#elevationDegrees = bounded(value, -45, 75); this.#emit(); }
  setResolution(value: number): void { this.#resolution = Math.round(bounded(value, 64, 4096)); this.#emit(); }

  setCaptureLightingMode(value: VoxelSpriteCaptureLightingMode): void {
    this.#lighting[this.#selectedSide].captureMode = value;
    this.#emit();
  }

  setCaptureAmbient(value: number): void {
    this.#lighting[this.#selectedSide].captureAmbient = bounded(value, 0, 8);
    this.#emit();
  }

  setCaptureKey(value: number): void {
    this.#lighting[this.#selectedSide].captureKey = bounded(value, 0, 8);
    this.#emit();
  }

  setCaptureFill(value: number): void {
    this.#lighting[this.#selectedSide].captureFill = bounded(value, 0, 8);
    this.#emit();
  }

  setPostLightingMode(value: VoxelSpritePostLightingMode): void {
    this.#lighting[this.#selectedSide].postMode = value;
    this.#configureSelectedPost();
  }

  setPostAmbient(value: number): void {
    this.#lighting[this.#selectedSide].postAmbient = bounded(value, 0, 4);
    this.#configureSelectedPost();
  }

  setPostDiffuse(value: number): void {
    this.#lighting[this.#selectedSide].postDiffuse = bounded(value, 0, 4);
    this.#configureSelectedPost();
  }

  setOutputGain(value: number): void {
    this.#lighting[this.#selectedSide].outputGain = bounded(value, 0, 4);
    this.#configureSelectedPost();
  }

  setPostLightAzimuth(value: number): void {
    this.#lighting[this.#selectedSide].lightAzimuthDegrees = bounded(value, -180, 180);
    this.#configureSelectedPost();
  }

  setPostLightElevation(value: number): void {
    this.#lighting[this.#selectedSide].lightElevationDegrees = bounded(value, -90, 90);
    this.#configureSelectedPost();
  }

  setDepthAmplitude(value: number): void {
    this.#depthAmplitude = bounded(value, 0, 2);
    this.#configureSide('enhanced', { depthAmplitude: this.#depthAmplitude });
  }

  setDepthQuantizationSteps(value: number): void {
    this.#depthQuantizationSteps = Math.round(bounded(value, 0, 32));
    this.#configureSide('enhanced', { depthQuantizationSteps: this.#depthQuantizationSteps });
  }

  setSplatResolution(value: number): void {
    this.#splatResolution = Math.round(bounded(value, 8, 512));
    this.#emit();
  }

  setSplatOverlap(value: number): void {
    this.#splatOverlap = bounded(value, 0, 1.5);
    this.#configureSide('enhanced', { splatOverlap: this.#splatOverlap });
  }

  setSplatOpacity(value: number): void {
    this.#splatOpacity = bounded(value, 0, 1);
    this.#configureSide('enhanced', { splatOpacity: this.#splatOpacity });
  }

  setSplatBlendMode(value: VoxelSpriteSplatBlendMode): void {
    if (!['depth-write', 'alpha-blend', 'additive'].includes(value)) return;
    this.#splatBlendMode = value;
    this.#configureSide('enhanced', { splatBlendMode: value });
  }

  recaptureSelected(): void {
    if (this.#experiment === null) return;
    this.#recapture(this.#selectedSubject, this.#selectedSide, 'recapture');
    this.#renderer.renderOnce();
    this.#emit();
  }

  recapturePair(): void {
    if (this.#experiment === null) return;
    for (const side of SIDES) {
      this.#recapture(this.#selectedSubject, side, 'recapture pair');
    }
    this.#renderer.renderOnce();
    this.#emit();
  }

  matchLightingFromSelected(): void {
    const other = this.#selectedSide === 'baseline' ? 'enhanced' : 'baseline';
    this.#lighting[other] = { ...this.#lighting[this.#selectedSide] };
    this.#configureSide(other, this.#postConfig(other));
    this.recapturePair();
  }

  probeFailureFallback(): void {
    if (this.#experiment === null) return;
    const id = this.#id(this.#selectedSubject, this.#selectedSide);
    const before = this.#entry(this.#selectedSubject, this.#selectedSide)?.enhancement.revision;
    const failed = this.#experiment.replace({
      ...this.#definition(this.#selectedSubject, this.#selectedSide),
      source: { kind: 'retained', handle: 9_999_999, capture: this.#captureSettings(this.#selectedSide) },
    });
    const after = failed.readout.entries.find((entry) => entry.id === id)?.enhancement.revision;
    if (failed.applied || before !== after) {
      this.#diagnostic('fallback probe unexpectedly changed the live representation');
      return;
    }
    this.#diagnostic(`fallback probe passed: ${id} kept revision ${String(after)}`);
    this.#emit();
  }

  dispose(): void {
    if (this.#status === 'disposed') return;
    this.#experiment?.dispose();
    this.#experiment = null;
    this.#status = 'disposed';
    this.#emit();
  }

  #configureSelectedPost(): void {
    this.#configureSide(this.#selectedSide, this.#postConfig(this.#selectedSide));
  }

  #configureSide(side: VoxelSpriteSide, patch: Partial<RustyApplicationVoxelSpriteConfig>): void {
    if (this.#experiment === null) return;
    for (const subject of SUBJECTS) {
      this.#apply(this.#experiment.configure(this.#id(subject, side), patch), `configure ${subject} ${side}`);
    }
    this.#renderer.renderOnce();
    this.#emit();
  }

  #recaptureAll(operation: string): void {
    if (this.#experiment === null) return;
    for (const subject of SUBJECTS) {
      for (const side of SIDES) {
        this.#recapture(subject, side, operation);
      }
    }
    this.#renderer.renderOnce();
    this.#emit();
  }

  #recapture(subject: VoxelSpriteSubject, side: VoxelSpriteSide, operation: string): void {
    if (this.#experiment === null) return;
    const entry = this.#entry(subject, side);
    const rebuildSplatGeometry = side === 'enhanced'
      && (entry?.enhancement.config.splatColumns !== this.#splatResolution
        || entry.enhancement.config.splatRows !== this.#splatResolution);
    const receipt = rebuildSplatGeometry
      ? this.#experiment.replace(this.#definition(subject, side))
      : this.#experiment.recapture(this.#id(subject, side), this.#captureSettings(side));
    this.#apply(receipt, `${operation}: ${subject} ${side}${rebuildSplatGeometry ? ' + splat rebuild' : ''}`);
  }

  #definition(subject: VoxelSpriteSubject, side: VoxelSpriteSide): RustyApplicationVoxelSpriteDefinition {
    const positions = this.#positions.get(subject);
    if (positions === undefined) throw new Error(`missing ${subject} garden position`);
    return {
      id: this.#id(subject, side),
      source: {
        kind: 'retained',
        handle: SOURCE_HANDLE + SUBJECTS.indexOf(subject),
        capture: this.#captureSettings(side),
      },
      transform: { position: positions[side], width: 2.5, height: 2.5 },
      mode: side === 'baseline' ? 'sprite' : this.#mode,
      config: {
        sampleColumns: 48,
        sampleRows: 48,
        depthAmplitude: side === 'baseline' ? 0 : this.#depthAmplitude,
        depthQuantizationSteps: this.#depthQuantizationSteps,
        splatOverlap: this.#splatOverlap,
        baseSpriteContribution: 0.7,
        normalInfluence: 0.65,
        ...(side === 'enhanced' ? {
          splatColumns: this.#splatResolution,
          splatRows: this.#splatResolution,
          splatOpacity: this.#splatOpacity,
          splatBlendMode: this.#splatBlendMode,
        } : {}),
        ...this.#postConfig(side),
      },
    };
  }

  #captureSettings(side: VoxelSpriteSide): RustyApplicationVoxelSpriteCaptureSettings {
    const lighting = this.#lighting[side];
    return {
      resolution: this.#resolution,
      azimuthDegrees: this.#sector * 22.5,
      elevationDegrees: this.#elevationDegrees,
      near: 0.1,
      far: 40,
      lighting: lighting.captureMode === 'scene'
        ? { mode: 'scene' }
        : {
            mode: 'isolated',
            ambientIntensity: lighting.captureAmbient,
            keyIntensity: lighting.captureKey,
            fillIntensity: lighting.captureFill,
          },
    };
  }

  #postConfig(side: VoxelSpriteSide): Partial<RustyApplicationVoxelSpriteConfig> {
    const lighting = this.#lighting[side];
    return {
      lightingMode: lighting.postMode,
      ambientLight: lighting.postAmbient,
      diffuseLight: lighting.postDiffuse,
      outputGain: lighting.outputGain,
      lightDirection: direction(lighting.lightAzimuthDegrees, lighting.lightElevationDegrees),
    };
  }

  #id(subject: VoxelSpriteSubject, side: VoxelSpriteSide): string { return `${subject}-${side}`; }

  #entry(subject: VoxelSpriteSubject, side: VoxelSpriteSide) {
    return this.#experiment?.readout().entries.find(({ id }) => id === this.#id(subject, side));
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

  #initialOps(manifest: RuntimeModelManifest): unknown[] {
    const ops: unknown[] = manifest.models.map((model) => ({ op: 'defineAnimatedMesh', asset: model.asset }));
    manifest.models.forEach((model, index) => {
      const positions = this.#positions.get(model.subject)!;
      ops.push({
        op: 'createAnimatedMeshInstance',
        handle: SOURCE_HANDLE + index,
        parent: null,
        instance: {
          asset: model.asset['asset'],
          transform: transform(positions.ground, model.scale),
          visible: false,
          materialOverrides: [],
          playback: null,
          metadata: metadata(`runtime-voxel-sprite-source-${model.subject}`, 701_800 + index),
        },
      });
      for (const [column, position] of [positions.baseline, positions.enhanced].entries()) {
        ops.push({
          op: 'create',
          handle: PLINTH_HANDLE + index * 2 + column,
          parent: null,
          node: primitiveNode(
            `runtime-voxel-sprite-${model.subject}-${column === 0 ? 'baseline' : 'enhanced'}-plinth`,
            [position[0], positions.ground[1] - 0.1, position[2]],
            column === 0 ? [0.1, 0.5, 1, 1] : [0.9, 0.06, 0.08, 1],
          ),
        });
      }
    });
    return ops;
  }

  async #publishLabels(): Promise<void> {
    const ops = SUBJECTS.flatMap((subject, index) => {
      const positions = this.#positions.get(subject)!;
      return SIDES.map((side, column) => ({
        domain: 'billboard' as const,
        meta: { sequence: index * 2 + column },
        op: {
          op: 'create' as const,
          handle: LABEL_HANDLE + index * 2 + column,
          descriptor: {
            anchor: { kind: 'world' as const, position: [positions[side][0], positions[side][1] + 1.55, positions[side][2]] },
            content: {
              kind: 'text' as const,
              localizationKey: `craftsurvive.voxelSprite.${subject}.${side}`,
              fallbackText: `${subject.replaceAll('-', ' ')} · ${side === 'baseline' ? 'BLUE runtime proxy' : 'RED runtime enhanced'}`,
              arguments: [],
            },
            font: { kind: 'system' as const, family: 'monospace' },
            heightPixels: 15,
            color: [1, 1, 1, 1] as const,
            background: side === 'baseline'
              ? [0.02, 0.18, 0.45, 0.9] as const
              : [0.5, 0.01, 0.03, 0.9] as const,
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
    const lighting = this.#lighting[this.#selectedSide];
    const entry = this.#entry(this.#selectedSubject, this.#selectedSide);
    const enhancedEntry = this.#entry(this.#selectedSubject, 'enhanced');
    const manifest = this.#manifest;
    this.#readout({
      status: this.#status,
      selectedSubject: this.#selectedSubject,
      selectedSide: this.#selectedSide,
      source: 'retained/runtime',
      mode: this.#mode,
      sector: this.#sector,
      sectorLabel: `dir-${pad(this.#sector)}`,
      autoSector: this.#autoSector,
      elevationDegrees: this.#elevationDegrees,
      resolution: this.#resolution,
      appliedResolution: entry?.capture?.resolution ?? 0,
      capturePending: !captureSettingsMatch(entry?.capture ?? null, this.#captureSettings(this.#selectedSide)),
      captureOutputBytesEstimate: this.#resolution * this.#resolution * 16,
      captureTemporaryDepthBytesEstimate: this.#resolution * this.#resolution * 4,
      captureLightingMode: lighting.captureMode,
      captureAmbient: lighting.captureAmbient,
      captureKey: lighting.captureKey,
      captureFill: lighting.captureFill,
      postLightingMode: lighting.postMode,
      postAmbient: lighting.postAmbient,
      postDiffuse: lighting.postDiffuse,
      outputGain: lighting.outputGain,
      lightAzimuthDegrees: lighting.lightAzimuthDegrees,
      lightElevationDegrees: lighting.lightElevationDegrees,
      captureSettingsMatched: captureLightingEqual(this.#lighting.baseline, this.#lighting.enhanced),
      allLightingMatched: sideLightingEqual(this.#lighting.baseline, this.#lighting.enhanced),
      captureMilliseconds: entry?.enhancement.captureCpuSubmissionMilliseconds ?? null,
      steadyStateMilliseconds: entry?.enhancement.steadyStateCpuSubmissionMilliseconds ?? null,
      textureBytes: entry?.enhancement.frameTextureBytes ?? 0,
      drawCalls: entry?.enhancement.expectedDrawCalls ?? 0,
      sampleCount: entry?.enhancement.geometrySampleCount ?? 0,
      fallbackPreservedCount: entry?.fallbackPreservedCount ?? 0,
      depthAmplitude: this.#depthAmplitude,
      depthQuantizationSteps: this.#depthQuantizationSteps,
      splatResolution: this.#splatResolution,
      appliedSplatResolution: enhancedEntry?.enhancement.config.splatColumns ?? 0,
      splatDensityPending: enhancedEntry?.enhancement.config.splatColumns !== this.#splatResolution,
      splatOverlap: this.#splatOverlap,
      splatOpacity: this.#splatOpacity,
      splatBlendMode: this.#splatBlendMode,
      composition: enhancedEntry?.enhancement.composition ?? 'n/a',
      resourceCount: manifest?.metrics.resourceCount ?? 0,
      resourceBytes: manifest?.metrics.totalResourceBytes ?? 0,
    });
  }
}

async function loadManifest(): Promise<RuntimeModelManifest> {
  const response = await fetch('/assets/voxel-sprite/runtime-models-v2.json', { cache: 'no-store' });
  if (!response.ok) throw new Error(`runtime voxel-sprite model manifest returned ${String(response.status)}`);
  const manifest = await response.json() as RuntimeModelManifest;
  if (manifest.schemaVersion !== 2 || manifest.source.kind !== 'runtime-models'
    || manifest.models.length !== 3 || manifest.resources.length !== 3
    || manifest.resources.some((resource) => resource.mediaType !== 'application/octet-stream')) {
    throw new Error('runtime voxel-sprite model manifest is incomplete or admits non-model resources');
  }
  return manifest;
}

async function fetchResource(resource: ModelResource): Promise<RustyApplicationResource> {
  const response = await fetch(resource.url, { cache: 'no-store' });
  if (!response.ok) throw new Error(`voxel-sprite resource ${resource.url} returned ${String(response.status)}`);
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.byteLength !== resource.byteLength) throw new Error(`voxel-sprite resource ${resource.identity} changed length`);
  return { identity: resource.identity, contentHash: resource.contentHash, mediaType: resource.mediaType, bytes };
}

function captureLightingEqual(left: SideLighting, right: SideLighting): boolean {
  return left.captureMode === right.captureMode
    && left.captureAmbient === right.captureAmbient
    && left.captureKey === right.captureKey
    && left.captureFill === right.captureFill;
}

function sideLightingEqual(left: SideLighting, right: SideLighting): boolean {
  return captureLightingEqual(left, right)
    && left.postMode === right.postMode
    && left.postAmbient === right.postAmbient
    && left.postDiffuse === right.postDiffuse
    && left.outputGain === right.outputGain
    && left.lightAzimuthDegrees === right.lightAzimuthDegrees
    && left.lightElevationDegrees === right.lightElevationDegrees;
}

function captureSettingsMatch(
  actual: RustyApplicationVoxelSpriteCaptureSettings | null,
  desired: RustyApplicationVoxelSpriteCaptureSettings,
): boolean {
  if (actual === null
    || actual.resolution !== desired.resolution
    || actual.azimuthDegrees !== desired.azimuthDegrees
    || actual.elevationDegrees !== desired.elevationDegrees
    || actual.near !== desired.near
    || actual.far !== desired.far
    || actual.lighting?.mode !== desired.lighting?.mode) return false;
  if (desired.lighting?.mode !== 'isolated' || actual.lighting?.mode !== 'isolated') return true;
  return actual.lighting.ambientIntensity === desired.lighting.ambientIntensity
    && actual.lighting.keyIntensity === desired.lighting.keyIntensity
    && actual.lighting.fillIntensity === desired.lighting.fillIntensity;
}

function direction(azimuthDegrees: number, elevationDegrees: number): [number, number, number] {
  const azimuth = azimuthDegrees * Math.PI / 180;
  const elevation = elevationDegrees * Math.PI / 180;
  return [
    Math.sin(azimuth) * Math.cos(elevation),
    Math.sin(elevation),
    Math.cos(azimuth) * Math.cos(elevation),
  ];
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
