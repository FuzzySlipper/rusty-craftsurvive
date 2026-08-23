import type {
  RustyApplicationContent,
  RustyApplicationHeldAnimationFrameBankReadout,
  RustyApplicationRendererPort,
  RustyApplicationResource,
  RustyApplicationVoxelSpriteExperimentPort,
  RustyApplicationVoxelSpriteReceipt,
} from '@rusty-engine/application-host';

type CameraPose = { position: [number, number, number]; yawDegrees: number; pitchDegrees: number };
type Cadence = 8 | 12 | 24;
type Representation = 'normal mesh' | 'flat capture' | 'voxel-depth enhanced';

interface ResourceDescriptor {
  identity: string; contentHash: string; byteLength: number; mediaType: string; url: string;
}
interface Clip { id: string; name: string; durationSeconds: number; category: string }
interface Manifest {
  schemaVersion: 1;
  experiment: string;
  engineRevision: string;
  assetPipeline: { task: number; run: string; revision: string; inPlacePolicy: string; knownLimitation: string };
  resources: ResourceDescriptor[];
  license: { url: string; contentHash: string; byteLength: number; name: string };
  target: Record<string, unknown>;
  clipPack: Record<string, unknown> & { clips: Clip[] };
  frameBankPolicy: { maximumSamples: number; sectorCount: 1; captureResolution: number };
}

export interface HeldAnimationGardenReadout {
  status: 'loading' | 'preparing' | 'ready' | 'failed' | 'disposed';
  clipId: string; clip: string; category: string; sourceDurationSeconds: number; sampleWindowSeconds: number;
  cadence: Cadence; sampleIndex: number; sampleCount: number; normalizedTime: number; paused: boolean; representation: Representation;
  rootPolicy: string; provenance: string; limitation: string;
  flat: BankFacts; depth: BankFacts; preparation: string; steadyState: string;
}
interface BankFacts { state: string; frameCount: number; captured: number; bytes: number; prepareMs: number | null; switchMs: number | null; }

const NORMAL_HANDLE = 9_810_010;
const SOURCE_HANDLE = 9_810_011;
const FLAT_BANK = 'craftsurvive-held-flat';
const DEPTH_BANK = 'craftsurvive-held-depth';
const REPRESENTATIONS: readonly Representation[] = ['normal mesh', 'flat capture', 'voxel-depth enhanced'];

/**
 * Downstream playback policy over Engine-owned clip admission and held frame banks.
 * It owns neither retargeting nor the player/world transform; the only animation
 * source is the explicitly admitted target-bound pack.
 */
export class HeldAnimationGarden {
  readonly #renderer: RustyApplicationRendererPort;
  readonly #emitReadout: (readout: HeldAnimationGardenReadout) => void;
  readonly #diagnostic: (message: string) => void;
  #manifest: Manifest | null = null;
  #experiment: RustyApplicationVoxelSpriteExperimentPort | null = null;
  #status: HeldAnimationGardenReadout['status'] = 'loading';
  #clipId = 'ual-idle';
  #cadence: Cadence = 12;
  #sampleIndex = 0;
  #representation: Representation = 'voxel-depth enhanced';
  #paused = true;
  #prepareTimer: number | null = null;
  #playTimer: number | null = null;
  #preparingBank: typeof FLAT_BANK | typeof DEPTH_BANK | null = null;
  #failure: string | null = null;
  #positions: { normal: [number, number, number]; flat: [number, number, number]; depth: [number, number, number] } | null = null;

  constructor(
    renderer: RustyApplicationRendererPort,
    readout: (readout: HeldAnimationGardenReadout) => void,
    diagnostic: (message: string) => void,
  ) {
    this.#renderer = renderer;
    this.#emitReadout = readout;
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
    this.#positions = layout(camera);
    const resources = await Promise.all(manifest.resources.map(fetchResource));
    const target: Record<string, unknown> = { ...manifest.target, clipPacks: [{ ...manifest.clipPack, clips: manifest.clipPack.clips.map(stripCategory) }] };
    return {
      frame: withOps(frame, [
        ...frameOps(frame),
        { op: 'defineAnimatedMesh', asset: target },
        {
          op: 'createAnimatedMeshInstance', handle: NORMAL_HANDLE, parent: null,
          instance: animatedInstance(target['asset'], transform(this.#positions.normal, 2.5), true, this.#clipId, 'held animation normal mesh'),
        },
        {
          op: 'createAnimatedMeshInstance', handle: SOURCE_HANDLE, parent: null,
          instance: animatedInstance(target['asset'], transform(this.#positions.normal), false, this.#clipId, 'held animation capture source'),
        },
      ]),
      resources: uniqueResources([...baseResources, ...resources]),
    };
  }

  activate(): void {
    if (this.#status === 'disposed' || this.#manifest === null || this.#positions === null) return;
    this.#experiment?.dispose();
    this.#experiment = this.#renderer.createVoxelSpriteExperiment();
    this.#status = 'preparing';
    this.#failure = null;
    this.#startBanks();
    this.#emit();
  }

  observe(_camera: CameraPose): void {
    // The player camera and world movement are Rust-owned. Bank direction is fixed to one authored view.
  }

  key(event: KeyboardEvent, down: boolean): boolean {
    if (!down || event.repeat || this.#status === 'disposed') return false;
    if (event.code === 'KeyU') { this.selectNextClip(); return true; }
    if (event.code === 'KeyI') { this.setCadence(this.#cadence === 8 ? 12 : this.#cadence === 12 ? 24 : 8); return true; }
    if (event.code === 'KeyO') { this.setRepresentation(REPRESENTATIONS[(REPRESENTATIONS.indexOf(this.#representation) + 1) % REPRESENTATIONS.length]!); return true; }
    if (event.code === 'KeyP') { this.setPaused(!this.#paused); return true; }
    return false;
  }

  clips(): readonly Clip[] { return this.#manifest?.clipPack.clips ?? []; }
  selectedClip(): string { return this.#clipId; }
  cadence(): Cadence { return this.#cadence; }
  sampleIndex(): number { return this.#sampleIndex; }
  isPaused(): boolean { return this.#paused; }
  setClip(id: string): void {
    if (!this.#manifest?.clipPack.clips.some((clip) => clip.id === id) || id === this.#clipId) return;
    this.#clipId = id; this.#sampleIndex = 0; this.#hold(); this.#restartBanks();
  }
  selectNextClip(): void {
    const clips = this.clips();
    const next = clips[(clips.findIndex((clip) => clip.id === this.#clipId) + 1) % clips.length];
    if (next !== undefined) this.setClip(next.id);
  }
  setCadence(value: Cadence): void {
    if (![8, 12, 24].includes(value) || value === this.#cadence) return;
    this.#cadence = value; this.#sampleIndex = 0; this.#hold(); this.#restartBanks();
  }
  setRepresentation(value: Representation): void { if (REPRESENTATIONS.includes(value)) { this.#representation = value; this.#emit(); } }
  setPaused(value: boolean): void {
    if (value === this.#paused) return;
    this.#hold();
    if (!value && this.#status === 'ready') {
      this.#paused = false;
      this.#playTimer = window.setInterval(() => this.#advance(), Math.max(1, Math.round(1_000 / this.#cadence)));
    }
    this.#emit();
  }
  setSampleIndex(value: number): void {
    const count = this.#sampleCount();
    this.#sampleIndex = Math.max(0, Math.min(count - 1, Math.round(value)));
    this.#hold();
    this.#select();
  }

  dispose(): void {
    if (this.#status === 'disposed') return;
    if (this.#prepareTimer !== null) window.clearTimeout(this.#prepareTimer);
    this.#prepareTimer = null;
    this.#hold();
    if (this.#experiment !== null) {
      const readout = this.#experiment.readout();
      for (const candidate of readout.frameBankCandidates) this.#experiment.cancelHeldAnimationFrameBank(candidate.id);
      for (const bank of readout.frameBanks) this.#experiment.destroyHeldAnimationFrameBank(bank.id);
      this.#experiment.dispose(); this.#experiment = null;
    }
    this.#status = 'disposed'; this.#emit();
  }

  #restartBanks(): void {
    if (this.#experiment === null) return;
    if (this.#prepareTimer !== null) window.clearTimeout(this.#prepareTimer);
    this.#prepareTimer = null;
    this.#cancelCandidate();
    this.#status = 'preparing'; this.#failure = null; this.#startBanks(); this.#emit();
  }

  #startBanks(): void {
    const experiment = this.#experiment;
    const positions = this.#positions;
    if (experiment === null || positions === null) return;
    const capture = { resolution: this.#manifest!.frameBankPolicy.captureResolution, azimuthDegrees: 0, elevationDegrees: 12, near: 0.1, far: 40,
      lighting: { mode: 'isolated' as const, ambientIntensity: 1.6, keyIntensity: 3, fillIntensity: 1.2 } };
    this.#preparingBank = FLAT_BANK;
    this.#apply(experiment.beginHeldAnimationFrameBank({ id: FLAT_BANK, animatedMesh: SOURCE_HANDLE, clip: this.#clipId, samples: this.#samplePlan(),
      sectorCount: 1, capture, transform: { position: positions.flat, width: 2.5, height: 2.5 }, mode: 'sprite' }), 'begin flat bank');
    this.#prepareStep();
  }

  #prepareStep(): void {
    const experiment = this.#experiment;
    if (experiment === null || this.#status !== 'preparing') return;
    const current = this.#preparingBank;
    if (current === null) { this.#fail('held frame-bank preparation lost its selected bank'); return; }
    this.#apply(experiment.prepareHeldAnimationFrameBank(current, 8), `prepare ${current}`);
    const after = experiment.readout();
    const candidateStillPreparing = after.frameBankCandidates.some((candidate) => candidate.id === current);
    if (!candidateStillPreparing && after.frameBanks.some((bank) => bank.id === current)) {
      if (current === FLAT_BANK) {
        this.#beginDepthBank();
        return;
      }
      this.#preparingBank = null;
      this.#status = 'ready'; this.#select(); this.#emit(); return;
    }
    if (!candidateStillPreparing) { this.#fail(`Engine did not retain ${current} held-frame bank candidate`); return; }
    this.#emit();
    this.#prepareTimer = window.setTimeout(() => { this.#prepareTimer = null; this.#prepareStep(); }, 0);
  }

  #select(): void {
    const experiment = this.#experiment;
    if (experiment === null || this.#status !== 'ready') { this.#emit(); return; }
    for (const id of [FLAT_BANK, DEPTH_BANK]) this.#apply(experiment.selectHeldAnimationFrameBank(id, this.#sampleIndex, 0), `select ${id}`);
    // `sample` is an Engine-owned retained playback command. It uses the exact normalized time selected in both banks;
    // CraftSurvive does not derive a pose, mutate a mixer, or recreate an off-contract capture here.
    const receipt = this.#renderer.applyFrame({ schemaVersion: 1, ops: [
      { op: 'setAnimatedMeshPlayback', handle: NORMAL_HANDLE, playback: { kind: 'sample', clip: this.#clipId, normalizedTime: this.#normalizedTime() } },
    ] });
    if (!receipt.applied) this.#fail(`normal mesh playback rejected: ${receipt.diagnostics.map(({ message }) => message).join('; ')}`);
    this.#renderer.renderOnce(); this.#emit();
  }

  #advance(): void {
    if (this.#status !== 'ready') return;
    this.#sampleIndex = (this.#sampleIndex + 1) % this.#sampleCount(); this.#select();
  }
  #beginDepthBank(): void {
    const experiment = this.#experiment;
    const positions = this.#positions;
    if (experiment === null || positions === null || this.#manifest === null) { this.#fail('depth bank lost required activation state'); return; }
    const capture = { resolution: this.#manifest.frameBankPolicy.captureResolution, azimuthDegrees: 0, elevationDegrees: 12, near: 0.1, far: 40,
      lighting: { mode: 'isolated' as const, ambientIntensity: 1.6, keyIntensity: 3, fillIntensity: 1.2 } };
    this.#preparingBank = DEPTH_BANK;
    this.#apply(experiment.beginHeldAnimationFrameBank({ id: DEPTH_BANK, animatedMesh: SOURCE_HANDLE, clip: this.#clipId, samples: this.#samplePlan(),
      sectorCount: 1, capture, transform: { position: positions.depth, width: 2.5, height: 2.5 }, mode: 'full-splat',
      config: { depthAmplitude: 0.35, depthQuantizationSteps: 8, splatColumns: 48, splatRows: 48, splatOpacity: 1, splatBlendMode: 'depth-write' } }), 'begin depth bank');
    if (this.#status === 'preparing') this.#prepareStep();
  }
  #cancelCandidate(): void {
    const experiment = this.#experiment;
    if (experiment === null) return;
    const candidate = experiment.readout().frameBankCandidates[0];
    if (candidate !== undefined) experiment.cancelHeldAnimationFrameBank(candidate.id);
    this.#preparingBank = null;
  }
  #hold(): void {
    if (this.#playTimer !== null) window.clearInterval(this.#playTimer);
    this.#playTimer = null;
    this.#paused = true;
  }
  #samplePlan(): { kind: 'exact'; normalizedTimes: readonly number[] } { return { kind: 'exact', normalizedTimes: this.#normalizedTimes() }; }
  #normalizedTimes(): readonly number[] {
    const clip = this.#manifest?.clipPack.clips.find((candidate) => candidate.id === this.#clipId);
    if (clip === undefined) return [0];
    return Array.from({ length: this.#sampleCount() }, (_value, index) => Math.min(1, index / (this.#cadence * clip.durationSeconds)));
  }
  #normalizedTime(): number { return this.#normalizedTimes()[this.#sampleIndex] ?? 0; }
  #sampleCount(): number {
    const clip = this.#manifest?.clipPack.clips.find((candidate) => candidate.id === this.#clipId);
    if (clip === undefined || this.#manifest === null) return 1;
    return Math.min(this.#manifest.frameBankPolicy.maximumSamples, Math.max(1, Math.ceil(clip.durationSeconds * this.#cadence)));
  }
  #bank(id: string): RustyApplicationHeldAnimationFrameBankReadout | null { return this.#experiment?.readout().frameBanks.find((bank) => bank.id === id) ?? null; }
  #bankFacts(id: string): BankFacts {
    const bank = this.#bank(id); return bank === null
      ? { state: this.#status === 'preparing' ? 'preparing' : 'unavailable', frameCount: 0, captured: 0, bytes: 0, prepareMs: null, switchMs: null }
      : { state: bank.state, frameCount: bank.frameCount, captured: bank.capturedFrameCount, bytes: bank.estimatedResidentBytes, prepareMs: bank.preparationCpuMilliseconds, switchMs: bank.lastSwitchCpuMilliseconds };
  }
  #apply(receipt: RustyApplicationVoxelSpriteReceipt, operation: string): void { if (!receipt.applied) this.#fail(`${operation}: ${receipt.diagnostics.map(({ message }) => message).join('; ')}`); }
  #fail(message: string): void { this.#failure = message; this.#status = 'failed'; this.#diagnostic(message); this.#emit(); }
  #emit(): void {
    const clip = this.#manifest?.clipPack.clips.find((candidate) => candidate.id === this.#clipId);
    const sampleCount = this.#sampleCount();
    const flat = this.#bankFacts(FLAT_BANK); const depth = this.#bankFacts(DEPTH_BANK);
    this.#emitReadout({ status: this.#status, clipId: this.#clipId, clip: clip?.name ?? 'loading', category: clip?.category ?? 'loading',
      sourceDurationSeconds: clip?.durationSeconds ?? 0, sampleWindowSeconds: this.#sampleWindowSeconds(), cadence: this.#cadence,
      sampleIndex: this.#sampleIndex, sampleCount, normalizedTime: this.#normalizedTime(), paused: this.#paused,
      representation: this.#representation, rootPolicy: this.#manifest?.assetPipeline.inPlacePolicy ?? 'loading',
      provenance: this.#manifest === null ? 'loading' : `Engine ${this.#manifest.engineRevision.slice(0, 12)} · Asset Pipeline #${this.#manifest.assetPipeline.task} ${this.#manifest.assetPipeline.run}`,
      limitation: this.#failure ?? this.#manifest?.assetPipeline.knownLimitation ?? 'loading', flat, depth,
      preparation: `${flat.captured + depth.captured}/${flat.frameCount + depth.frameCount} captures · candidate memory ${((flat.bytes + depth.bytes) / 1024 / 1024).toFixed(1)} MiB`,
      steadyState: `selection uses resident frame-bank entries; flat ${milliseconds(flat.switchMs)}, depth ${milliseconds(depth.switchMs)}`,
    });
  }
  #sampleWindowSeconds(): number { return Math.max(0, this.#sampleCount() - 1) / this.#cadence; }
}

async function loadManifest(): Promise<Manifest> {
  const response = await fetch('/assets/animation-lab/held-animation-garden-v1.json', { cache: 'no-store' });
  if (!response.ok) throw new Error(`held-animation manifest returned ${String(response.status)}`);
  const manifest = await response.json() as Manifest;
  if (manifest.schemaVersion !== 1 || manifest.resources?.length !== 2 || manifest.clipPack?.clips?.length !== 6
    || manifest.frameBankPolicy?.sectorCount !== 1 || manifest.clipPack.rig === undefined) throw new Error('held-animation manifest inventory is invalid');
  const ids = new Set(manifest.clipPack.clips.map((clip) => clip.id));
  if (ids.size !== 6 || !['ual-idle', 'ual-jog', 'ual-spell', 'ual-sword', 'ual-roll', 'ual-death'].every((id) => ids.has(id))) throw new Error('held-animation manifest clip chooser drifted');
  return manifest;
}
async function fetchResource(resource: ResourceDescriptor): Promise<RustyApplicationResource> {
  const response = await fetch(resource.url, { cache: 'no-store' });
  if (!response.ok) throw new Error(`held-animation resource ${resource.url} returned ${String(response.status)}`);
  const bytes = new Uint8Array(await response.arrayBuffer());
  if (bytes.byteLength !== resource.byteLength || `sha256:${await sha256(bytes)}` !== resource.contentHash) throw new Error(`held-animation resource drifted: ${resource.identity}`);
  return { identity: resource.identity, contentHash: resource.contentHash, mediaType: resource.mediaType, bytes };
}
async function sha256(bytes: Uint8Array): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new Uint8Array(bytes).buffer); return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, '0')).join('');
}
function stripCategory(clip: Clip): Omit<Clip, 'category'> { const { category: _category, ...descriptor } = clip; return descriptor; }
function animatedInstance(asset: unknown, value: Record<string, unknown>, visible: boolean, clip: string, label: string): Record<string, unknown> {
  return { asset, transform: value, visible, materialOverrides: [], playback: { kind: 'play', clip, loop: 'repeat', speed: 1, weight: 1, restart: true, fadeSeconds: null }, metadata: { sourceEntity: null, sourceSceneNode: null, tags: ['held-animation-garden'], label } };
}
function layout(camera: CameraPose) {
  const yaw = camera.yawDegrees * Math.PI / 180; const forward: [number, number] = [Math.sin(yaw), -Math.cos(yaw)]; const right: [number, number] = [Math.cos(yaw), Math.sin(yaw)];
  const center: [number, number, number] = [camera.position[0] + forward[0] * 10, camera.position[1] - 1.95, camera.position[2] + forward[1] * 10];
  const at = (offset: number): [number, number, number] => [center[0] + right[0] * offset, center[1], center[2] + right[1] * offset];
  // Keep all three simultaneous columns clear of the compact left HUD at the
  // supported viewport while leaving a visible gap between the comparisons.
  return { normal: at(-0.6), flat: at(1.1), depth: at(2.8) };
}
function transform(position: readonly [number, number, number], scale = 1): Record<string, unknown> { return { translation: position, rotation: [0, 0, 0, 1], scale: [scale, scale, scale] }; }
function frameOps(frame: Record<string, unknown>): unknown[] { return Array.isArray(frame.ops) ? frame.ops : []; }
function withOps(frame: Record<string, unknown>, ops: unknown[]): Record<string, unknown> {
  // The first complete content frame composes product-owned retained operations with the terrain
  // stream. Keep its monotonic publication identity and update its declared operation count, so
  // later terrain revision frames remain guarded against gaps or reordering.
  const publication = frame['publication'];
  return {
    ...frame,
    schemaVersion: 1,
    ops,
    ...(typeof publication === 'object' && publication !== null
      ? { publication: { ...publication, operationCount: ops.length } }
      : {}),
  };
}
function uniqueResources(resources: readonly RustyApplicationResource[]): RustyApplicationResource[] { const map = new Map<string, RustyApplicationResource>(); for (const resource of resources) map.set(resource.identity, resource); return [...map.values()]; }
function milliseconds(value: number | null): string { return value === null ? 'n/a' : `${value.toFixed(2)} ms`; }
