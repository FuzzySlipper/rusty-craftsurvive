import type {
  RustyApplicationContent,
  RustyApplicationRendererPort,
  RustyApplicationResource,
  RustyApplicationVoxelSpriteCaptureSettings,
  RustyApplicationVoxelSpriteConfig,
  RustyApplicationVoxelSpriteDefinition,
  RustyApplicationVoxelSpriteEnhancementReadout,
  RustyApplicationVoxelSpriteExperimentPort,
  RustyApplicationVoxelSpriteGhostPlateReadout,
  RustyApplicationVoxelSpriteMode,
  RustyApplicationVoxelSpriteReceipt,
} from '@rusty-engine/application-host';

type CameraPose = { position: [number, number, number]; yawDegrees: number; pitchDegrees: number };
export type VoxelSpriteSubject = 'spatial-wizard' | 'rigged-wizard' | 'knight';
export type VoxelSpriteSide = 'baseline' | 'enhanced';
export type VoxelSpriteRepresentation = 'canonical' | VoxelSpriteSide | 'ghost';
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
  selectedRepresentation: VoxelSpriteRepresentation;
  canonicalVisible: boolean;
  source: 'retained/runtime';
  mode: RustyApplicationVoxelSpriteMode;
  sector: number;
  sectorLabel: string;
  captureAzimuthDegrees: number;
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
  ghostDepthRetention: number;
  ghostAnchorPolicy: 'bounds-center' | 'bounds-normalized';
  ghostAnchorValue: number;
  ghostAnchorDepth: number | null;
  ghostPlateMapping: 'plate-locked' | 'projective-surface';
  ghostShellMode: 'whole-mesh' | 'strict-source' | 'repaired-source';
  ghostShellDepthEpsilon: number;
  ghostShellDepthQuantizationStep: number | null;
  ghostShellEffectiveDepthEpsilon: number | null;
  ghostRejectedFragmentRatioStatus: 'unavailable' | null;
  ghostRepairedBoundaryRatioStatus: 'unavailable' | null;
  ghostMatchedPose: boolean;
  ghostFallbackActive: boolean;
  ghostFallbackReason: string | null;
  ghostAngularOffsetDegrees: number | null;
  ghostSourceViewAgreement: 'exact' | 'offset' | 'unavailable';
  ghostDrawCalls: number;
  ghostMeshCount: number;
  ghostMaterialResourceCount: number;
  ghostBorrowedTextureCount: number;
  ghostLimitations: readonly string[];
  ghostSectorCount: 1 | 4 | 8 | 16;
  ghostSelectedSector: number;
  ghostPendingSector: number | null;
  ghostPreviousSector: number | null;
  ghostLocalAzimuthDegrees: number | null;
  ghostSectorHysteresisDegrees: number;
  ghostTransitionMode: 'hard-cut' | 'ordered-dither' | 'noise-dissolve';
  ghostTransitionProgress: number;
  ghostTransitionDurationMilliseconds: number;
  ghostResidentSectorCount: number;
  ghostPreparationCpuMilliseconds: number | null;
  resourceCount: number;
  resourceBytes: number;
}

const SUBJECTS: readonly VoxelSpriteSubject[] = ['spatial-wizard', 'rigged-wizard', 'knight'];
const SIDES: readonly VoxelSpriteSide[] = ['baseline', 'enhanced'];
const REPRESENTATIONS: readonly VoxelSpriteRepresentation[] = ['canonical', 'baseline', 'enhanced', 'ghost'];
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
const CANONICAL_HANDLE = 9_800_150;
const PLINTH_HANDLE = 9_800_200;
const LABEL_HANDLE = 9_800_300;
const ROW_DISTANCES = [7, 10.5, 14] as const;
const HIGH_COST_CAPTURE_RESOLUTION = 512;
const DEFAULT_GHOST_DEPTH_RETENTION = 0.15;
const DEFAULT_GHOST_ANCHOR_POLICY = 'bounds-center' as const;
const DEFAULT_GHOST_ANCHOR_VALUE = 0.5;
const DEFAULT_GHOST_PLATE_MAPPING = 'plate-locked' as const;
const DEFAULT_GHOST_SHELL_MODE = 'whole-mesh' as const;
const DEFAULT_GHOST_SHELL_DEPTH_EPSILON = 0.12;

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
    source: [number, number, number];
    canonical: [number, number, number];
    baseline: [number, number, number];
    enhanced: [number, number, number];
    ghost: [number, number, number];
  }>();
  #gardenCenter: [number, number] = [0, 0];
  #lastCamera: CameraPose | null = null;
  #selectedSubject: VoxelSpriteSubject = 'spatial-wizard';
  #selectedSide: VoxelSpriteSide = 'enhanced';
  #selectedRepresentation: VoxelSpriteRepresentation = 'ghost';
  #canonicalVisible = true;
  #mode: RustyApplicationVoxelSpriteMode = 'sprite-splat';
  #sector = 0;
  #captureAzimuthDegrees = 0;
  #autoSector = false;
  #elevationDegrees = 18;
  #resolution = 128;
  #depthAmplitude = 0.35;
  #depthQuantizationSteps = 8;
  #splatResolution = 48;
  #splatOverlap = 0.15;
  #splatOpacity = 1;
  #splatBlendMode: VoxelSpriteSplatBlendMode = 'depth-write';
  #ghostDepthRetention = DEFAULT_GHOST_DEPTH_RETENTION;
  #ghostAnchorPolicy: 'bounds-center' | 'bounds-normalized' = DEFAULT_GHOST_ANCHOR_POLICY;
  #ghostAnchorValue = DEFAULT_GHOST_ANCHOR_VALUE;
  #ghostPlateMapping: 'plate-locked' | 'projective-surface' = DEFAULT_GHOST_PLATE_MAPPING;
  #ghostShellMode: 'whole-mesh' | 'strict-source' | 'repaired-source' = DEFAULT_GHOST_SHELL_MODE;
  #ghostShellDepthEpsilon = DEFAULT_GHOST_SHELL_DEPTH_EPSILON;
  #ghostSectorCount: 1 | 4 | 8 | 16 = 8;
  #ghostSectorHysteresisDegrees = 3;
  #ghostTransitionMode: 'hard-cut' | 'ordered-dither' | 'noise-dissolve' = 'ordered-dither';
  #ghostTransitionDurationMilliseconds = 180;
  readonly #ghostOnly: boolean;
  #ghostAngularBucket: number | null | undefined;
  #alignmentTimer: number | null = null;
  #status: RuntimeVoxelSpriteGardenReadout['status'] = 'loading';

  constructor(
    renderer: RustyApplicationRendererPort,
    readout: (value: RuntimeVoxelSpriteGardenReadout) => void,
    diagnostic: (value: string) => void,
    options: { readonly ghostOnly?: boolean } = {},
  ) {
    this.#renderer = renderer;
    this.#readout = readout;
    this.#diagnostic = diagnostic;
    this.#ghostOnly = options.ghostOnly ?? false;
    if (this.#ghostOnly) this.#ghostTransitionMode = 'noise-dissolve';
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
    this.#lastCamera = camera;
    this.#matchCaptureToView(camera);
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
    if (!this.#ghostOnly) {
      for (const subject of SUBJECTS) {
        for (const side of SIDES) {
          this.#apply(this.#experiment.create(this.#definition(subject, side)), `create ${subject} ${side}`);
        }
      }
    }
    this.#apply(
      this.#experiment.create(this.#definition(this.#selectedSubject, 'ghost')),
      `create ${this.#selectedSubject} ghost`,
    );
    this.#status = 'ready';
    this.#scheduleSourceViewAlignment();
    void this.#publishLabels();
    this.#emit();
  }

  observe(camera: CameraPose): void {
    if (this.#status !== 'ready') return;
    this.#lastCamera = camera;
    if (this.#autoSector && this.#alignmentTimer === null) {
      const sector = nearestSector(camera.position, this.#gardenCenter);
      if (sector !== this.#sector) this.setSector(sector, true);
    }
    const angularOffset = this.#entry(this.#selectedSubject, 'ghost')?.ghostPlate?.angularOffsetDegrees ?? null;
    const bucket = angularOffset === null ? null : Math.round(angularOffset * 10) / 10;
    if (bucket !== this.#ghostAngularBucket) {
      this.#ghostAngularBucket = bucket;
      this.#emit();
    }
  }

  key(event: KeyboardEvent, down: boolean): boolean {
    if (!down || event.repeat || this.#status !== 'ready') return false;
    if (event.code === 'KeyU') {
      this.setSubject(SUBJECTS[(SUBJECTS.indexOf(this.#selectedSubject) + 1) % SUBJECTS.length]!);
      return true;
    }
    if (event.code === 'KeyI') {
      this.setMode(MODES[(MODES.indexOf(this.#mode) + 1) % MODES.length]!);
      return true;
    }
    if (event.code === 'KeyO') {
      this.#selectedRepresentation = REPRESENTATIONS[
        (REPRESENTATIONS.indexOf(this.#selectedRepresentation) + 1) % REPRESENTATIONS.length
      ]!;
      if (this.#selectedRepresentation === 'baseline' || this.#selectedRepresentation === 'enhanced') {
        this.#selectedSide = this.#selectedRepresentation;
      }
      this.#emit();
      return true;
    }
    if (event.code === 'KeyP') {
      this.recapturePair();
      return true;
    }
    return false;
  }

  setSubject(subject: VoxelSpriteSubject): void {
    if (!SUBJECTS.includes(subject) || subject === this.#selectedSubject) {
      this.#emit();
      return;
    }
    const previousSubject = this.#selectedSubject;
    this.#selectedSubject = subject;
    const ghost = this.#positions.get(subject)?.ghost;
    if (ghost !== undefined) this.#gardenCenter = [ghost[0], ghost[2]];
    if (this.#lastCamera !== null) this.#matchCaptureToView(this.#lastCamera);
    if (this.#experiment !== null) {
      const created = this.#experiment.create(this.#definition(subject, 'ghost'));
      if (!created.applied) {
        this.#selectedSubject = previousSubject;
        const previousGhost = this.#positions.get(previousSubject)?.ghost;
        if (previousGhost !== undefined) this.#gardenCenter = [previousGhost[0], previousGhost[2]];
        this.#diagnostic(
          `select ${subject} ghost rejected: ${created.diagnostics.map(({ message }) => message).join('; ')}`,
        );
        this.#emit();
        return;
      }
      this.#apply(this.#experiment.destroy(this.#id(previousSubject, 'ghost')), `hide ${previousSubject} ghost`);
      if (!this.#ghostOnly) {
        for (const side of SIDES) this.#recapture(subject, side, 'subject comparison');
      }
      void this.#publishSelectedGhostMarkers();
      this.#renderer.renderOnce();
    }
    this.#emit();
    this.#scheduleSourceViewAlignment();
  }
  setSide(side: VoxelSpriteSide): void { this.#selectedSide = side; this.#emit(); }
  setRepresentation(representation: VoxelSpriteRepresentation): void {
    if (!REPRESENTATIONS.includes(representation)) return;
    this.#selectedRepresentation = representation;
    if (representation === 'baseline' || representation === 'enhanced') this.#selectedSide = representation;
    this.#emit();
  }

  setCanonicalVisible(visible: boolean): void {
    const receipt = this.#renderer.applyFrame({
      schemaVersion: 1,
      ops: SUBJECTS.map((_, index) => ({
        op: 'update' as const,
        handle: CANONICAL_HANDLE + index,
        transform: null,
        material: null,
        visible,
        metadata: null,
      })),
    });
    if (!receipt.applied) {
      this.#diagnostic(`canonical visibility rejected: ${receipt.diagnostics.map(({ message }) => message).join('; ')}`);
      return;
    }
    this.#canonicalVisible = visible;
    this.#renderer.renderOnce();
    this.#emit();
  }

  setMode(mode: RustyApplicationVoxelSpriteMode): void {
    if (!MODES.includes(mode)) return;
    this.#mode = mode;
    this.#configureSide('enhanced', { mode });
  }

  setSector(sector: number, preserveAuto = false): void {
    if (this.#experiment === null) return;
    if (!preserveAuto) this.#autoSector = false;
    this.#sector = ((Math.round(sector) % 16) + 16) % 16;
    this.#captureAzimuthDegrees = this.#sector * 22.5;
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

  setGhostDepthRetention(value: number): void {
    this.#ghostDepthRetention = bounded(value, 0.02, 1);
    this.#configureGhost({ ghostDepthRetention: this.#ghostDepthRetention });
  }

  setGhostAnchorPolicy(value: 'bounds-center' | 'bounds-normalized'): void {
    if (value !== 'bounds-center' && value !== 'bounds-normalized') return;
    this.#ghostAnchorPolicy = value;
    this.#configureGhost({ ghostAnchorPolicy: value });
  }

  setGhostAnchorValue(value: number): void {
    this.#ghostAnchorValue = bounded(value, 0, 1);
    this.#configureGhost({ ghostAnchorValue: this.#ghostAnchorValue });
  }

  setGhostPlateMapping(value: 'plate-locked' | 'projective-surface'): void {
    if (value !== 'plate-locked' && value !== 'projective-surface') return;
    this.#ghostPlateMapping = value;
    this.#configureGhost({ ghostPlateMapping: value });
  }

  setGhostShellMode(value: 'whole-mesh' | 'strict-source' | 'repaired-source'): void {
    if (value !== 'whole-mesh' && value !== 'strict-source' && value !== 'repaired-source') return;
    this.#ghostShellMode = value;
    this.#configureGhost({ ghostShellMode: value });
  }

  setGhostShellDepthEpsilon(value: number): void {
    this.#ghostShellDepthEpsilon = bounded(value, 0, 2);
    this.#configureGhost({ ghostShellDepthEpsilon: this.#ghostShellDepthEpsilon });
  }

  setGhostSectorCount(value: 1 | 4 | 8 | 16): void {
    if (![1, 4, 8, 16].includes(value) || value === this.#ghostSectorCount) return;
    this.#ghostSectorCount = value;
    this.#replaceGhost('replace sector bank');
  }

  setGhostSectorHysteresisDegrees(value: number): void {
    this.#ghostSectorHysteresisDegrees = bounded(value, 0, 22.5);
    this.#configureGhost({ ghostSectorHysteresisDegrees: this.#ghostSectorHysteresisDegrees });
  }

  setGhostTransitionMode(value: 'hard-cut' | 'ordered-dither' | 'noise-dissolve'): void {
    if (value !== 'hard-cut' && value !== 'ordered-dither' && value !== 'noise-dissolve') return;
    this.#ghostTransitionMode = value;
    this.#configureGhost({ ghostTransitionMode: value });
  }

  setGhostTransitionDurationMilliseconds(value: number): void {
    this.#ghostTransitionDurationMilliseconds = bounded(value, 0, 5_000);
    this.#configureGhost({
      ghostTransitionDurationMilliseconds: this.#ghostTransitionDurationMilliseconds,
    });
  }

  resetGhostDefaults(): void {
    this.#ghostDepthRetention = DEFAULT_GHOST_DEPTH_RETENTION;
    this.#ghostAnchorPolicy = DEFAULT_GHOST_ANCHOR_POLICY;
    this.#ghostAnchorValue = DEFAULT_GHOST_ANCHOR_VALUE;
    this.#ghostPlateMapping = DEFAULT_GHOST_PLATE_MAPPING;
    this.#ghostShellMode = DEFAULT_GHOST_SHELL_MODE;
    this.#ghostShellDepthEpsilon = DEFAULT_GHOST_SHELL_DEPTH_EPSILON;
    this.#configureGhost({
      ghostDepthRetention: this.#ghostDepthRetention,
      ghostAnchorPolicy: this.#ghostAnchorPolicy,
      ghostAnchorValue: this.#ghostAnchorValue,
      ghostPlateMapping: this.#ghostPlateMapping,
      ghostShellMode: this.#ghostShellMode,
      ghostShellDepthEpsilon: this.#ghostShellDepthEpsilon,
    });
    this.freezeCurrentSourceView();
  }

  freezeCurrentSourceView(): void {
    if (this.#lastCamera === null) return;
    this.#autoSector = false;
    this.#matchCaptureToView(this.#lastCamera);
    this.#recaptureAll('freeze current source view');
    this.#scheduleSourceViewAlignment();
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
    this.#recapture(this.#selectedSubject, 'ghost', 'recapture comparison');
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
    const before = this.#entry(this.#selectedSubject, this.#selectedSide)?.enhancement?.revision;
    const failed = this.#experiment.replace({
      ...this.#definition(this.#selectedSubject, this.#selectedSide),
      source: { kind: 'retained', handle: 9_999_999, capture: this.#captureSettings(this.#selectedSide) },
    });
    const after = failed.readout.entries.find((entry) => entry.id === id)?.enhancement?.revision;
    if (failed.applied || before !== after) {
      this.#diagnostic('fallback probe unexpectedly changed the live representation');
      return;
    }
    this.#diagnostic(`fallback probe passed: ${id} kept revision ${String(after)}`);
    this.#emit();
  }

  dispose(): void {
    if (this.#status === 'disposed') return;
    if (this.#alignmentTimer !== null) clearTimeout(this.#alignmentTimer);
    this.#alignmentTimer = null;
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

  #configureGhost(patch: Partial<RustyApplicationVoxelSpriteConfig>): void {
    if (this.#experiment === null) return;
    this.#apply(
      this.#experiment.configure(this.#id(this.#selectedSubject, 'ghost'), patch),
      `configure ${this.#selectedSubject} ghost`,
    );
    this.#renderer.renderOnce();
    this.#emit();
  }

  #recaptureAll(operation: string): void {
    if (this.#experiment === null) return;
    if (!this.#ghostOnly) {
      for (const side of SIDES) this.#recapture(this.#selectedSubject, side, operation);
    }
    this.#recapture(this.#selectedSubject, 'ghost', operation);
    this.#renderer.renderOnce();
    this.#emit();
  }

  #replaceGhost(operation: string): void {
    if (this.#experiment === null) return;
    this.#apply(
      this.#experiment.replace(this.#definition(this.#selectedSubject, 'ghost')),
      `${operation}: ${this.#selectedSubject} ghost`,
    );
    this.#renderer.renderOnce();
    this.#emit();
  }

  #recapture(subject: VoxelSpriteSubject, side: VoxelSpriteSide | 'ghost', operation: string): void {
    if (this.#experiment === null) return;
    const entry = this.#entry(subject, side);
    const rebuildSplatGeometry = side === 'enhanced'
      && (entry?.enhancement?.config.splatColumns !== this.#splatResolution
        || entry?.enhancement?.config.splatRows !== this.#splatResolution);
    const receipt = rebuildSplatGeometry
      ? this.#experiment.replace(this.#definition(subject, side))
      : this.#experiment.recapture(this.#id(subject, side), this.#captureSettings(side === 'ghost' ? 'enhanced' : side));
    this.#apply(receipt, `${operation}: ${subject} ${side}${rebuildSplatGeometry ? ' + splat rebuild' : ''}`);
  }

  #definition(subject: VoxelSpriteSubject, side: VoxelSpriteSide | 'ghost'): RustyApplicationVoxelSpriteDefinition {
    const positions = this.#positions.get(subject);
    if (positions === undefined) throw new Error(`missing ${subject} garden position`);
    const source = {
      kind: 'retained' as const,
      handle: SOURCE_HANDLE + SUBJECTS.indexOf(subject),
      capture: this.#captureSettings(side === 'ghost' ? 'enhanced' : side),
    };
    if (side === 'ghost') {
      return {
        id: this.#id(subject, side),
        source,
        transform: { position: positions.ghost, width: 2.5, height: 2.5 },
        mode: 'ghost-plate',
        config: {
          ghostDepthRetention: this.#ghostDepthRetention,
          ghostAnchorPolicy: this.#ghostAnchorPolicy,
          ghostAnchorValue: this.#ghostAnchorValue,
          ghostPlateMapping: this.#ghostPlateMapping,
          ghostShellMode: this.#ghostShellMode,
          ghostShellDepthEpsilon: this.#ghostShellDepthEpsilon,
          ghostSectorCount: this.#ghostSectorCount,
          ghostSectorHysteresisDegrees: this.#ghostSectorHysteresisDegrees,
          ghostTransitionMode: this.#ghostTransitionMode,
          ghostTransitionDurationMilliseconds: this.#ghostTransitionDurationMilliseconds,
        },
      };
    }
    return {
      id: this.#id(subject, side),
      source,
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
      azimuthDegrees: this.#captureAzimuthDegrees,
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

  #id(subject: VoxelSpriteSubject, side: VoxelSpriteSide | 'ghost'): string { return `${subject}-${side}`; }

  #entry(subject: VoxelSpriteSubject, side: VoxelSpriteSide | 'ghost') {
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
        source: center,
        canonical: [center[0] - right[0] * 4.5, baseY, center[2] - right[1] * 4.5],
        baseline: [center[0] - right[0] * 1.5, baseY + 1.25, center[2] - right[1] * 1.5],
        enhanced: [center[0] + right[0] * 1.5, baseY + 1.25, center[2] + right[1] * 1.5],
        ghost: this.#ghostOnly
          ? [center[0], baseY + 1.25, center[2]]
          : [center[0] + right[0] * 4.5, baseY + 1.25, center[2] + right[1] * 4.5],
      });
    });
    const ghost = this.#positions.get(this.#selectedSubject)!.ghost;
    this.#gardenCenter = [ghost[0], ghost[2]];
  }

  #matchCaptureToView(camera: CameraPose): void {
    const position = this.#positions.get(this.#selectedSubject)?.ghost;
    if (position === undefined) return;
    const dx = camera.position[0] - position[0];
    const dz = camera.position[2] - position[2];
    this.#captureAzimuthDegrees = normalizeDegrees(Math.atan2(dx, dz) * 180 / Math.PI);
    this.#elevationDegrees = Math.atan2(
      camera.position[1] - position[1],
      Math.hypot(dx, dz),
    ) * 180 / Math.PI;
    this.#sector = nearestSector(camera.position, this.#gardenCenter);
  }

  #alignCaptureToRenderedSourceView(camera: CameraPose): boolean {
    const position = this.#positions.get(this.#selectedSubject)?.ghost;
    const sourcePosition = this.#entry(this.#selectedSubject, 'ghost')?.ghostPlate?.sourceViewBasis.position;
    if (position === undefined || sourcePosition === undefined) return false;
    const source = sphericalDirection(sourcePosition, position);
    const viewer = sphericalDirection(camera.position, position);
    const azimuthCorrection = signedDegrees(viewer.azimuthDegrees - source.azimuthDegrees);
    const elevationCorrection = viewer.elevationDegrees - source.elevationDegrees;
    if (Math.abs(azimuthCorrection) <= 0.05 && Math.abs(elevationCorrection) <= 0.05) return false;
    this.#captureAzimuthDegrees = normalizeDegrees(this.#captureAzimuthDegrees + azimuthCorrection);
    this.#elevationDegrees = bounded(this.#elevationDegrees + elevationCorrection, -45, 75);
    this.#sector = nearestSector(camera.position, this.#gardenCenter);
    return true;
  }

  #scheduleSourceViewAlignment(remainingPasses = 30): void {
    if (this.#alignmentTimer !== null) clearTimeout(this.#alignmentTimer);
    this.#alignmentTimer = window.setTimeout(() => {
      this.#alignmentTimer = null;
      if (this.#status !== 'ready' || this.#lastCamera === null) return;
      if (this.#alignCaptureToRenderedSourceView(this.#lastCamera)) {
        this.#recaptureAll('align initial source view');
        if (remainingPasses > 1) this.#scheduleSourceViewAlignment(remainingPasses - 1);
      }
    }, 250);
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
          transform: transform(positions.source, model.scale),
          visible: false,
          materialOverrides: [],
          playback: null,
          metadata: metadata(`runtime-voxel-sprite-source-${model.subject}`, 701_800 + index),
        },
      });
      if (!this.#ghostOnly) ops.push({
        op: 'createAnimatedMeshInstance',
        handle: CANONICAL_HANDLE + index,
        parent: null,
        instance: {
          asset: model.asset['asset'],
          transform: transform(positions.canonical, model.scale),
          visible: this.#canonicalVisible,
          materialOverrides: [],
          playback: null,
          metadata: metadata(`runtime-voxel-sprite-canonical-${model.subject}`, 701_850 + index),
        },
      });
      const representations = this.#ghostOnly ? ['ghost'] as const : REPRESENTATIONS;
      for (const representation of representations) {
        const column = REPRESENTATIONS.indexOf(representation);
        const position = positions[representation];
        ops.push({
          op: 'create',
          handle: PLINTH_HANDLE + index * REPRESENTATIONS.length + column,
          parent: null,
          node: primitiveNode(
            `runtime-voxel-sprite-${model.subject}-${representation}-plinth`,
            [position[0], positions.source[1] - 0.1, position[2]],
            representation === 'canonical' ? [0.22, 0.24, 0.2, 1]
              : representation === 'baseline' ? [0.1, 0.5, 1, 1]
                : representation === 'enhanced' ? [0.9, 0.06, 0.08, 1]
                  : [0.82, 0.6, 0.08, 1],
            representation !== 'ghost' || model.subject === this.#selectedSubject,
          ),
        });
      }
    });
    return ops;
  }

  async #publishLabels(): Promise<void> {
    const ops = SUBJECTS.flatMap((subject, index) => {
      const positions = this.#positions.get(subject)!;
      const representations = this.#ghostOnly ? ['ghost'] as const : REPRESENTATIONS;
      return representations.map((representation) => {
        const column = REPRESENTATIONS.indexOf(representation);
        return ({
        domain: 'billboard' as const,
        meta: { sequence: index * REPRESENTATIONS.length + column },
        op: {
          op: 'create' as const,
          handle: LABEL_HANDLE + index * REPRESENTATIONS.length + column,
          descriptor: {
            anchor: { kind: 'world' as const, position: [positions[representation][0], positions[representation][1] + 1.55, positions[representation][2]] },
            content: {
              kind: 'text' as const,
              localizationKey: `craftsurvive.voxelSprite.${subject}.${representation}`,
              fallbackText: `${subject.replaceAll('-', ' ')} · ${representationLabel(representation)}`,
              arguments: [],
            },
            font: { kind: 'system' as const, family: 'monospace' },
            heightPixels: 15,
            color: [1, 1, 1, 1] as const,
            background: representation === 'canonical' ? [0.12, 0.14, 0.11, 0.9] as const
              : representation === 'baseline' ? [0.02, 0.18, 0.45, 0.9] as const
                : representation === 'enhanced' ? [0.5, 0.01, 0.03, 0.9] as const
                  : [0.45, 0.3, 0.02, 0.92] as const,
            maxDistance: 45,
            layer: 'alwaysOnTop' as const,
            visible: representation !== 'ghost' || subject === this.#selectedSubject,
          },
        },
        });
      });
    });
    const receipt = await this.#renderer.applyPresentation({ schemaVersion: 1, ops });
    for (const diagnostic of receipt.diagnostics) {
      this.#diagnostic(`voxel-sprite label ${diagnostic.code}: ${diagnostic.message}`);
    }
  }

  async #publishSelectedGhostMarkers(): Promise<void> {
    const ghostColumn = REPRESENTATIONS.indexOf('ghost');
    const frameReceipt = this.#renderer.applyFrame({
      schemaVersion: 1,
      ops: SUBJECTS.map((subject, index) => ({
        op: 'update' as const,
        handle: PLINTH_HANDLE + index * REPRESENTATIONS.length + ghostColumn,
        transform: null,
        material: null,
        visible: subject === this.#selectedSubject,
        metadata: null,
      })),
    });
    if (!frameReceipt.applied) {
      this.#diagnostic(
        `GOLD plinth focus rejected: ${frameReceipt.diagnostics.map(({ message }) => message).join('; ')}`,
      );
    }
    const presentationReceipt = await this.#renderer.applyPresentation({
      schemaVersion: 1,
      ops: SUBJECTS.map((subject, index) => ({
        domain: 'billboard' as const,
        meta: { sequence: index },
        op: {
          op: 'update' as const,
          handle: LABEL_HANDLE + index * REPRESENTATIONS.length + ghostColumn,
          patch: {
            anchor: null,
            content: null,
            font: null,
            heightPixels: null,
            color: null,
            background: null,
            maxDistance: null,
            layer: null,
            visible: subject === this.#selectedSubject,
          },
        },
      })),
    });
    for (const diagnostic of presentationReceipt.diagnostics) {
      this.#diagnostic(`GOLD label focus ${diagnostic.code}: ${diagnostic.message}`);
    }
  }

  #emit(): void {
    const lighting = this.#lighting[this.#selectedSide];
    const entry = this.#entry(this.#selectedSubject, this.#selectedSide);
    const enhancedEntry = this.#entry(this.#selectedSubject, 'enhanced');
    const ghostEntry = this.#entry(this.#selectedSubject, 'ghost');
    const ghost = ghostEntry?.ghostPlate ?? null;
    this.#ghostAngularBucket = ghost?.angularOffsetDegrees === null || ghost?.angularOffsetDegrees === undefined
      ? null
      : Math.round(ghost.angularOffsetDegrees * 10) / 10;
    const manifest = this.#manifest;
    this.#readout({
      status: this.#status,
      selectedSubject: this.#selectedSubject,
      selectedSide: this.#selectedSide,
      selectedRepresentation: this.#selectedRepresentation,
      canonicalVisible: this.#canonicalVisible,
      source: 'retained/runtime',
      mode: this.#mode,
      sector: this.#sector,
      sectorLabel: `dir-${pad(this.#sector)} / az ${this.#captureAzimuthDegrees.toFixed(1)}°`,
      captureAzimuthDegrees: this.#captureAzimuthDegrees,
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
      captureMilliseconds: entry?.enhancement?.captureCpuSubmissionMilliseconds ?? null,
      steadyStateMilliseconds: entry?.enhancement?.steadyStateCpuSubmissionMilliseconds ?? null,
      textureBytes: entry?.enhancement?.frameTextureBytes ?? 0,
      drawCalls: entry?.enhancement?.expectedDrawCalls ?? 0,
      sampleCount: entry?.enhancement?.geometrySampleCount ?? 0,
      fallbackPreservedCount: entry?.fallbackPreservedCount ?? 0,
      depthAmplitude: this.#depthAmplitude,
      depthQuantizationSteps: this.#depthQuantizationSteps,
      splatResolution: this.#splatResolution,
      appliedSplatResolution: enhancedEntry?.enhancement?.config.splatColumns ?? 0,
      splatDensityPending: enhancedEntry?.enhancement?.config.splatColumns !== this.#splatResolution,
      splatOverlap: this.#splatOverlap,
      splatOpacity: this.#splatOpacity,
      splatBlendMode: this.#splatBlendMode,
      composition: enhancedEntry?.enhancement?.composition ?? 'n/a',
      ghostDepthRetention: this.#ghostDepthRetention,
      ghostAnchorPolicy: this.#ghostAnchorPolicy,
      ghostAnchorValue: this.#ghostAnchorValue,
      ghostAnchorDepth: ghost?.anchorDepth ?? null,
      ghostPlateMapping: this.#ghostPlateMapping,
      ghostShellMode: this.#ghostShellMode,
      ghostShellDepthEpsilon: this.#ghostShellDepthEpsilon,
      ghostShellDepthQuantizationStep: ghost?.shellDepthQuantizationStep ?? null,
      ghostShellEffectiveDepthEpsilon: ghost?.shellEffectiveDepthEpsilon ?? null,
      ghostRejectedFragmentRatioStatus: ghost?.rejectedFragmentRatio.status ?? null,
      ghostRepairedBoundaryRatioStatus: ghost?.repairedBoundaryRatio.status ?? null,
      ghostMatchedPose: ghost?.matchedPose ?? false,
      ghostFallbackActive: ghost?.fallbackActive ?? false,
      ghostFallbackReason: ghost?.fallbackReason ?? null,
      ghostAngularOffsetDegrees: ghost?.angularOffsetDegrees ?? null,
      ghostSourceViewAgreement: ghostSourceViewAgreement(ghost),
      ghostDrawCalls: ghost?.expectedDrawCalls ?? 0,
      ghostMeshCount: ghost?.meshCount ?? 0,
      ghostMaterialResourceCount: ghost?.materialResourceCount ?? 0,
      ghostBorrowedTextureCount: ghost?.borrowedTextureCount ?? 0,
      ghostLimitations: ghost?.limitations ?? [],
      ghostSectorCount: ghost?.sectorCount ?? this.#ghostSectorCount,
      ghostSelectedSector: ghost?.selectedSector ?? 0,
      ghostPendingSector: ghost?.pendingSector ?? null,
      ghostPreviousSector: ghost?.previousSector ?? null,
      ghostLocalAzimuthDegrees: ghost?.localAzimuthDegrees ?? null,
      ghostSectorHysteresisDegrees: ghost?.sectorHysteresisDegrees ?? this.#ghostSectorHysteresisDegrees,
      ghostTransitionMode: ghost?.transitionMode ?? this.#ghostTransitionMode,
      ghostTransitionProgress: ghost?.transitionProgress ?? 1,
      ghostTransitionDurationMilliseconds: ghost?.transitionDurationMilliseconds
        ?? this.#ghostTransitionDurationMilliseconds,
      ghostResidentSectorCount: ghost?.residentSectorCount ?? 0,
      ghostPreparationCpuMilliseconds: ghost?.preparationCpuMilliseconds ?? null,
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

function normalizeDegrees(value: number): number {
  return ((value % 360) + 360) % 360;
}

function signedDegrees(value: number): number {
  return ((value + 180) % 360 + 360) % 360 - 180;
}

function sphericalDirection(
  point: readonly [number, number, number],
  center: readonly [number, number, number],
): { azimuthDegrees: number; elevationDegrees: number } {
  const dx = point[0] - center[0];
  const dy = point[1] - center[1];
  const dz = point[2] - center[2];
  return {
    azimuthDegrees: normalizeDegrees(Math.atan2(dx, dz) * 180 / Math.PI),
    elevationDegrees: Math.atan2(dy, Math.hypot(dx, dz)) * 180 / Math.PI,
  };
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

function primitiveNode(
  label: string,
  position: readonly number[],
  color: readonly number[],
  visible = true,
) {
  return {
    geometry: { kind: 'cube' },
    material: { color, wireframe: false },
    transform: { translation: position, rotation: [0, 0, 0, 1], scale: [1.15, 0.1, 1.05] },
    visible,
    layer: 'scene',
    metadata: { sourceEntity: null, sourceSceneNode: null, tags: ['runtime-voxel-sprite', 'plinth'], label },
  };
}

function representationLabel(representation: VoxelSpriteRepresentation): string {
  if (representation === 'canonical') return 'GRAY frozen 3D';
  if (representation === 'baseline') return 'BLUE plain capture';
  if (representation === 'enhanced') return 'RED 7035 control';
  return 'GOLD ghost plate';
}

function ghostSourceViewAgreement(
  ghost: RustyApplicationVoxelSpriteGhostPlateReadout | null,
): RuntimeVoxelSpriteGardenReadout['ghostSourceViewAgreement'] {
  if (ghost?.angularOffsetDegrees === null || ghost?.angularOffsetDegrees === undefined) return 'unavailable';
  return Math.abs(ghost.angularOffsetDegrees) <= 0.1 ? 'exact' : 'offset';
}

function bounded(value: number, minimum: number, maximum: number): number {
  return Math.max(minimum, Math.min(maximum, value));
}

function pad(value: number): string { return String(value).padStart(2, '0'); }
