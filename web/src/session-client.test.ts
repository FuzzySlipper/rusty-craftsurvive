import assert from 'node:assert/strict';
import test from 'node:test';
import type {
  RustyApplicationRendererPort,
  RustyApplicationUiContext,
} from '@rusty-engine/application-host';
import { SessionClient, type SessionView } from './session-client';

class FakeWebSocket {
  static readonly OPEN = 1;
  static latest: FakeWebSocket | null = null;
  readonly readyState = FakeWebSocket.OPEN;
  readonly url: string;
  readonly sent: string[] = [];
  closeCount = 0;
  readonly #listeners = new Map<string, Array<(event: { data: string }) => void>>();

  constructor(url: string) { this.url = url; FakeWebSocket.latest = this; }
  addEventListener(kind: string, listener: (event: { data: string }) => void): void {
    const listeners = this.#listeners.get(kind) ?? [];
    listeners.push(listener); this.#listeners.set(kind, listeners);
  }
  emitMessage(message: unknown): void {
    for (const listener of this.#listeners.get('message') ?? []) listener({ data: JSON.stringify(message) });
  }
  send(message: string): void { this.sent.push(message); }
  close(): void {
    this.closeCount += 1;
    for (const listener of this.#listeners.get('close') ?? []) listener({ data: '' });
  }
}

const readout = (acceptedSequence: number, playerRevision: number) => ({
  generation: 1,
  acceptedSequence,
  playerRevision,
  player: { position: [0, 7, 7], yawDegrees: -10, pitchDegrees: -20 },
  playerLocalPosition: [0, 7, 7],
  worldOrigin: [0, 0, 0],
  worldOriginRevision: 0,
  localCoordinateEnvelope: 1_000_000,
  camera: { position: [0, 7, 7], yawDegrees: -10, pitchDegrees: -20 },
  surface: 'box',
  worldRevision: 0,
  authorityHash: 1,
  voxelCount: 10,
  targetedVoxel: [0, 4, 0],
  grounded: true,
  velocity: [0, 0, 0],
  stance: 'standing',
  blockedStand: false,
  groundNormal: [0, 1, 0],
  groundSource: 'voxel',
  contactCount: 1,
  blocks: [],
  stepAttempted: false,
  stepAccepted: false,
  stepRise: 0,
  platformEntity: null,
  platformDisplacement: [0, 0, 0],
  collisionWorldHash: 2,
  castCount: 1,
  recoveryPasses: 0,
  brushRadius: 0,
  terrainSeed: '0x4352414654535552',
  terrainSize: 96,
  meshVertices: 100,
  meshTriangles: 50,
  generationMs: 1.5,
  authorityBuildMs: 2.5,
  meshBuildMs: 3.5,
  residencyCenter: [0, 0],
  requestedChunks: 9,
  preparingChunks: 0,
  residentChunks: 25,
  pinnedChunks: 9,
  evictableChunks: 16,
  admittedChunksTotal: 0,
  evictedChunksTotal: 0,
  residencyCacheHits: 9,
  residencyMissedDeadlines: 0,
  residentChunkBytes: 204800,
  residencyGenerationMs: 1.25,
  residencyAdmissionMs: 2.75,
  residencyRequestGeneration: 3,
  terrainGenerationVersion: 2,
  editOverlayEntries: 4,
});

type CurrentRendererPort = RustyApplicationRendererPort;

const rendererPort = (
  overrides: Partial<CurrentRendererPort> = {},
): CurrentRendererPort => ({
  replaceFrame: async () => ({ applied: true, diagnostics: [] }),
  applyFrame: () => ({ applied: true, diagnostics: [] }),
  applyPresentation: async () => ({ applied: 0, diagnostics: [] }),
  setCameraPose: () => undefined,
  clear: async () => undefined,
  renderOnce: () => undefined,
  replaceContent: async () => ({ applied: true, diagnostics: [] }),
  createVoxelSpriteExperiment: () => { throw new Error('voxel-sprite experiment is not used by this test'); },
  resumeAudio: async () => ({ resumed: true, diagnostics: [] }),
  ...overrides,
});

test('complete welcome projection serializes a newer incremental update', async () => {
  let releaseWelcome!: () => void;
  const welcomePending = new Promise<void>((resolve) => { releaseWelcome = resolve; });
  const projection: string[] = [];
  const projectedRevisions: number[] = [];
  const diagnostics: string[] = [];
  const context = {
    renderer: rendererPort({
      replaceFrame: async () => {
        projection.push('welcome:start');
        await welcomePending;
        projection.push('welcome:complete');
        return { applied: true, diagnostics: [] };
      },
      applyFrame: () => {
        projection.push('update:incremental');
        return { applied: true, diagnostics: [] };
      },
      applyPresentation: async () => {
        projection.push('update:presentation');
        return {
          applied: 0,
          diagnostics: [{ code: 'budgetExceeded', domain: 'particle', message: 'optional effect dropped' }],
        };
      },
      setCameraPose: () => undefined,
      clear: async () => undefined,
      renderOnce: () => undefined,
      replaceContent: async () => ({ applied: true, diagnostics: [] }),
    }),
    ui: {
      active: () => true,
      allowsGameplayInput: () => true,
      focusGameplay: () => undefined,
      interactionMode: () => 'gameplay' as const,
      setInteractionMode: () => undefined,
    },
  } satisfies RustyApplicationUiContext;
  const view: SessionView = {
    status: () => undefined,
    diagnostic: (value) => diagnostics.push(value),
    readout: (value) => projectedRevisions.push(value.playerRevision),
    edit: () => undefined,
    reject: () => undefined,
    miss: () => undefined,
  };

  Object.defineProperty(globalThis, 'WebSocket', { configurable: true, value: FakeWebSocket });
  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: { protocol: 'http:', host: 'craft.test', search: '' },
  });
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { setInterval: () => 1, clearInterval: () => undefined },
  });

  const client = new SessionClient(context, view);
  client.connect();
  const socket = FakeWebSocket.latest!;
  socket.emitMessage({ kind: 'welcome', readout: readout(0, 0), frame: { schemaVersion: 1, ops: [] } });
  socket.emitMessage({
    kind: 'update',
    update: {
      readout: readout(1, 1), action: 'destroy',
      edit: {
        action: 'destroy', voxel: [0, 4, 0], revision: 1, affectedVoxels: 7,
        meshBuildMs: 12.5, editMs: 18.5, dirtyChunks: 2, rebuiltChunks: 2,
        reusedChunks: 20, removedChunks: 0, frameOperations: 2, encodedBytes: 4096,
        replacementCount: 2, destroyCount: 0, changedHandles: [17, 18],
      },
      editRejection: null,
      frame: { schemaVersion: 1, ops: [{ op: 'replaceMeshPayload' }] },
      presentation: { schemaVersion: 1, ops: [{ domain: 'particle' }] },
    },
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(projection, ['welcome:start']);
  assert.deepEqual(projectedRevisions, []);

  releaseWelcome();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(projection, [
    'welcome:start',
    'welcome:complete',
    'update:incremental',
    'update:presentation',
  ]);
  assert.deepEqual(projectedRevisions, [0, 1]);
  assert.deepEqual(diagnostics, ['presentation budgetExceeded: optional effect dropped']);
  client.dispose();
});

test('a pending optional presentation does not delay an accepted edit or later updates', async () => {
  const presentationPending = new Promise<never>(() => undefined);
  const projectedRevisions: number[] = [];
  const edits: number[] = [];
  const context = {
    renderer: rendererPort({
      applyPresentation: () => presentationPending,
    }),
    ui: {
      active: () => true,
      allowsGameplayInput: () => true,
      focusGameplay: () => undefined,
      interactionMode: () => 'gameplay' as const,
      setInteractionMode: () => undefined,
    },
  } satisfies RustyApplicationUiContext;
  const view: SessionView = {
    status: () => undefined,
    diagnostic: () => undefined,
    readout: (value) => projectedRevisions.push(value.playerRevision),
    edit: (value) => edits.push(value.revision),
    reject: () => undefined,
    miss: () => undefined,
  };
  Object.defineProperty(globalThis, 'WebSocket', { configurable: true, value: FakeWebSocket });
  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: { protocol: 'http:', host: 'craft.test', search: '' },
  });
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { setInterval: () => 1, clearInterval: () => undefined },
  });

  const client = new SessionClient(context, view);
  client.connect();
  const socket = FakeWebSocket.latest!;
  socket.emitMessage({ kind: 'welcome', readout: readout(0, 0), frame: { schemaVersion: 1, ops: [] } });
  await new Promise((resolve) => setTimeout(resolve, 0));
  socket.emitMessage({
    kind: 'update',
    update: {
      readout: readout(1, 1), action: 'destroy',
      edit: {
        action: 'destroy', voxel: [0, 4, 0], revision: 1, affectedVoxels: 1,
        meshBuildMs: 2, editMs: 3, dirtyChunks: 1, rebuiltChunks: 1, reusedChunks: 2,
        removedChunks: 0, frameOperations: 1, encodedBytes: 128, replacementCount: 1,
        destroyCount: 0, changedHandles: [17],
      },
      editRejection: null, frame: null,
      presentation: { schemaVersion: 1, ops: [{ domain: 'particle' }] },
    },
  });
  socket.emitMessage({
    kind: 'update',
    update: {
      readout: readout(2, 2), action: null, edit: null, editRejection: null,
      frame: null, presentation: null,
    },
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.deepEqual(projectedRevisions, [0, 1, 2]);
  assert.deepEqual(edits, [1]);
  assert.equal(socket.closeCount, 0);
  client.dispose();
});

test('a rejected optional presentation is diagnostic and does not poison the session', async () => {
  const diagnostics: string[] = [];
  const projectedRevisions: number[] = [];
  const context = {
    renderer: rendererPort({
      applyPresentation: async () => { throw new Error('particle sink unavailable'); },
    }),
    ui: {
      active: () => true,
      allowsGameplayInput: () => true,
      focusGameplay: () => undefined,
      interactionMode: () => 'gameplay' as const,
      setInteractionMode: () => undefined,
    },
  } satisfies RustyApplicationUiContext;
  const view: SessionView = {
    status: () => undefined,
    diagnostic: (value) => diagnostics.push(value),
    readout: (value) => projectedRevisions.push(value.playerRevision),
    edit: () => undefined,
    reject: () => undefined,
    miss: () => undefined,
  };
  Object.defineProperty(globalThis, 'WebSocket', { configurable: true, value: FakeWebSocket });
  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: { protocol: 'http:', host: 'craft.test', search: '' },
  });
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { setInterval: () => 1, clearInterval: () => undefined },
  });

  const client = new SessionClient(context, view);
  client.connect();
  const socket = FakeWebSocket.latest!;
  socket.emitMessage({ kind: 'welcome', readout: readout(0, 0), frame: { schemaVersion: 1, ops: [] } });
  await new Promise((resolve) => setTimeout(resolve, 0));
  socket.emitMessage({
    kind: 'update',
    update: {
      readout: readout(1, 1), action: null, edit: null, editRejection: null, frame: null,
      presentation: { schemaVersion: 1, ops: [{ domain: 'particle' }] },
    },
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));
  socket.emitMessage({
    kind: 'update',
    update: {
      readout: readout(2, 2), action: null, edit: null, editRejection: null,
      frame: null, presentation: null,
    },
  });
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.deepEqual(projectedRevisions, [0, 1, 2]);
  assert.deepEqual(diagnostics, ['presentation failed: particle sink unavailable']);
  assert.equal(socket.closeCount, 0);
  client.dispose();
});

test('textured welcome fetches retained atlas bytes before replacing content', async () => {
  const calls: Array<{ frame: Record<string, unknown>; resources: Array<{ identity: string; contentHash: string; mediaType: string; bytes: Uint8Array }> }> = [];
  const context = {
    renderer: rendererPort({
      replaceFrame: async () => { throw new Error('textured welcome must replace complete content'); },
      applyFrame: () => ({ applied: true, diagnostics: [] }),
      setCameraPose: () => undefined,
      clear: async () => undefined,
      renderOnce: () => undefined,
      replaceContent: async (content: (typeof calls)[number]) => {
        calls.push(content);
        return { applied: true, diagnostics: [] };
      },
    }),
    ui: {
      active: () => true,
      allowsGameplayInput: () => true,
      focusGameplay: () => undefined,
      interactionMode: () => 'gameplay' as const,
      setInteractionMode: () => undefined,
    },
  } satisfies RustyApplicationUiContext;
  const view: SessionView = {
    status: () => undefined,
    diagnostic: () => undefined,
    readout: () => undefined,
    edit: () => undefined,
    reject: () => undefined,
    miss: () => undefined,
  };
  Object.defineProperty(globalThis, 'WebSocket', { configurable: true, value: FakeWebSocket });
  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: { protocol: 'http:', host: 'craft.test', search: '' },
  });
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { setInterval: () => 1, clearInterval: () => undefined },
  });
  Object.defineProperty(globalThis, 'fetch', {
    configurable: true,
    value: async (url: string) => {
      assert.equal(url, '/assets/terrain-atlas.png');
      return new Response(new Uint8Array([137, 80, 78, 71]), { status: 200 });
    },
  });

  const client = new SessionClient(context, view);
  client.connect();
  FakeWebSocket.latest!.emitMessage({
    kind: 'welcome',
    readout: readout(0, 0),
    frame: { schemaVersion: 1, ops: [] },
    resources: [{
      identity: 'texture-resource/terrain-atlas',
      contentHash: `sha256:${'a'.repeat(64)}`,
      mediaType: 'image/png',
      url: '/assets/terrain-atlas.png',
    }],
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.equal(calls.length, 1);
  assert.deepEqual([...calls[0]!.resources[0]!.bytes], [137, 80, 78, 71]);
  assert.equal(calls[0]!.resources[0]!.identity, 'texture-resource/terrain-atlas');
  client.dispose();
});

test('dispose invalidates an in-flight welcome before readout publication', async () => {
  let releaseWelcome!: () => void;
  const welcomePending = new Promise<void>((resolve) => { releaseWelcome = resolve; });
  const projection: string[] = [];
  const published: string[] = [];
  const context = {
    renderer: rendererPort({
      replaceFrame: async () => {
        projection.push('welcome:start');
        await welcomePending;
        projection.push('welcome:complete');
        return { applied: true, diagnostics: [] };
      },
      applyFrame: () => { published.push('incremental'); return { applied: true, diagnostics: [] }; },
      setCameraPose: () => { published.push('camera'); },
      clear: async () => undefined,
      renderOnce: () => undefined,
      replaceContent: async () => ({ applied: true, diagnostics: [] }),
    }),
    ui: {
      active: () => true,
      allowsGameplayInput: () => true,
      focusGameplay: () => undefined,
      interactionMode: () => 'gameplay' as const,
      setInteractionMode: () => undefined,
    },
  } satisfies RustyApplicationUiContext;
  const view: SessionView = {
    status: (value) => published.push(`status:${value}`),
    diagnostic: (value) => published.push(`diagnostic:${value}`),
    readout: () => published.push('readout'),
    edit: () => published.push('edit'),
    reject: () => published.push('reject'),
    miss: () => published.push('miss'),
  };
  Object.defineProperty(globalThis, 'WebSocket', { configurable: true, value: FakeWebSocket });
  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: { protocol: 'http:', host: 'craft.test', search: '' },
  });
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { setInterval: () => 1, clearInterval: () => undefined },
  });

  const client = new SessionClient(context, view);
  client.connect();
  FakeWebSocket.latest!.emitMessage({
    kind: 'welcome', readout: readout(0, 0), frame: { schemaVersion: 1, ops: [] },
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(projection, ['welcome:start']);
  client.dispose();
  const afterDispose = [...published];
  releaseWelcome();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));

  assert.deepEqual(projection, ['welcome:start', 'welcome:complete']);
  assert.deepEqual(published, afterDispose);
  assert.ok(published.includes('status:closed'));
  assert.ok(!published.includes('camera'));
  assert.ok(!published.includes('readout'));
});

test('browser input sends movement, stance, sprint, jump, and impulse intent', async () => {
  let tick!: () => void;
  const context = {
    renderer: rendererPort({
      replaceFrame: async () => ({ applied: true, diagnostics: [] }),
      applyFrame: () => ({ applied: true, diagnostics: [] }),
      setCameraPose: () => undefined,
      clear: async () => undefined,
      renderOnce: () => undefined,
      replaceContent: async () => ({ applied: true, diagnostics: [] }),
    }),
    ui: {
      active: () => true,
      allowsGameplayInput: () => true,
      focusGameplay: () => undefined,
      interactionMode: () => 'gameplay' as const,
      setInteractionMode: () => undefined,
    },
  } satisfies RustyApplicationUiContext;
  const view: SessionView = {
    status: () => undefined,
    diagnostic: () => undefined,
    readout: () => undefined,
    edit: () => undefined,
    reject: () => undefined,
    miss: () => undefined,
  };
  Object.defineProperty(globalThis, 'WebSocket', { configurable: true, value: FakeWebSocket });
  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: { protocol: 'http:', host: 'craft.test', search: '?surface=mc&seed=0x2a&size=64' },
  });
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { setInterval: (callback: () => void) => { tick = callback; return 1; }, clearInterval: () => undefined },
  });

  const client = new SessionClient(context, view);
  client.connect();
  const socket = FakeWebSocket.latest!;
  assert.equal(socket.url, 'ws://craft.test/api/session?surface=mc&seed=0x2a&size=64');
  socket.emitMessage({ kind: 'welcome', readout: readout(0, 0), frame: { schemaVersion: 1, ops: [] } });
  await new Promise((resolve) => setTimeout(resolve, 0));
  const key = (code: string) => ({ code, repeat: false, preventDefault: () => undefined } as KeyboardEvent);
  client.key(key('KeyW'), true);
  client.key(key('Space'), true);
  client.key(key('ControlLeft'), true);
  client.key(key('ShiftLeft'), true);
  client.key(key('KeyH'), true);
  client.key(key('Digit3'), true);
  tick();

  const sent = JSON.parse(socket.sent.at(-1)!) as {
    protocolVersion: number;
    command: { movement: number[]; jump: boolean; crouch: boolean; sprint: boolean; impulse: boolean; brushRadius: number };
  };
  assert.equal(sent.protocolVersion, 6);
  assert.deepEqual(sent.command.movement, [1, 0]);
  assert.equal(sent.command.jump, true);
  assert.equal(sent.command.crouch, true);
  assert.equal(sent.command.sprint, true);
  assert.equal(sent.command.impulse, true);
  assert.equal(sent.command.brushRadius, 2);
  client.dispose();
});

test('typed player-overlap edit rejection reaches the HUD view', async () => {
  const rejected: Array<{ code: string; voxel: [number, number, number] }> = [];
  const context = {
    renderer: rendererPort({
      replaceFrame: async () => ({ applied: true, diagnostics: [] }),
      applyFrame: () => ({ applied: true, diagnostics: [] }),
      setCameraPose: () => undefined,
      clear: async () => undefined,
      renderOnce: () => undefined,
      replaceContent: async () => ({ applied: true, diagnostics: [] }),
    }),
    ui: {
      active: () => true,
      allowsGameplayInput: () => true,
      focusGameplay: () => undefined,
      interactionMode: () => 'gameplay' as const,
      setInteractionMode: () => undefined,
    },
  } satisfies RustyApplicationUiContext;
  const view: SessionView = {
    status: () => undefined,
    diagnostic: () => undefined,
    readout: () => undefined,
    edit: () => undefined,
    reject: (value) => rejected.push(value),
    miss: () => undefined,
  };
  Object.defineProperty(globalThis, 'WebSocket', { configurable: true, value: FakeWebSocket });
  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: { protocol: 'http:', host: 'craft.test', search: '' },
  });
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { setInterval: () => 1, clearInterval: () => undefined },
  });

  const client = new SessionClient(context, view);
  client.connect();
  const socket = FakeWebSocket.latest!;
  socket.emitMessage({ kind: 'welcome', readout: readout(0, 0), frame: { schemaVersion: 1, ops: [] } });
  await new Promise((resolve) => setTimeout(resolve, 0));
  socket.emitMessage({
    kind: 'update',
    update: {
      readout: readout(1, 0), action: 'place', edit: null,
      editRejection: { code: 'playerOverlap', voxel: [0, 1, 0] }, frame: null,
    },
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(rejected, [{ code: 'playerOverlap', voxel: [0, 1, 0] }]);
  client.dispose();
});
