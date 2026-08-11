import assert from 'node:assert/strict';
import test from 'node:test';
import type { RustyApplicationUiContext } from '@rusty-engine/application-host';
import { SessionClient, type SessionView } from './session-client';

class FakeWebSocket {
  static readonly OPEN = 1;
  static latest: FakeWebSocket | null = null;
  readonly readyState = FakeWebSocket.OPEN;
  readonly sent: string[] = [];
  readonly #listeners = new Map<string, Array<(event: { data: string }) => void>>();

  constructor(_url: string) { FakeWebSocket.latest = this; }
  addEventListener(kind: string, listener: (event: { data: string }) => void): void {
    const listeners = this.#listeners.get(kind) ?? [];
    listeners.push(listener); this.#listeners.set(kind, listeners);
  }
  emitMessage(message: unknown): void {
    for (const listener of this.#listeners.get('message') ?? []) listener({ data: JSON.stringify(message) });
  }
  send(message: string): void { this.sent.push(message); }
  close(): void {
    for (const listener of this.#listeners.get('close') ?? []) listener({ data: '' });
  }
}

const readout = (acceptedSequence: number, playerRevision: number) => ({
  generation: 1,
  acceptedSequence,
  playerRevision,
  camera: { position: [0, 7, 7], yawDegrees: -10, pitchDegrees: -20 },
  surface: 'box',
  worldRevision: 0,
  authorityHash: 1,
  voxelCount: 10,
  targetedVoxel: [0, 4, 0],
  grounded: true,
  velocity: [0, 0, 0],
});

test('complete welcome projection serializes a newer incremental update', async () => {
  let releaseWelcome!: () => void;
  const welcomePending = new Promise<void>((resolve) => { releaseWelcome = resolve; });
  const projection: string[] = [];
  const projectedRevisions: number[] = [];
  const context = {
    renderer: {
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
      setCameraPose: () => undefined,
      clear: async () => undefined,
      renderOnce: () => undefined,
      replaceContent: async () => ({ applied: true, diagnostics: [] }),
    },
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
    readout: (value) => projectedRevisions.push(value.playerRevision),
    edit: () => undefined,
    miss: () => undefined,
  };

  Object.defineProperty(globalThis, 'WebSocket', { configurable: true, value: FakeWebSocket });
  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: { protocol: 'http:', host: 'craft.test' },
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
      readout: readout(1, 1), action: null, edit: null,
      frame: { schemaVersion: 1, ops: [{ op: 'replaceMeshPayload' }] },
    },
  });
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(projection, ['welcome:start']);
  assert.deepEqual(projectedRevisions, []);

  releaseWelcome();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert.deepEqual(projection, ['welcome:start', 'welcome:complete', 'update:incremental']);
  assert.deepEqual(projectedRevisions, [0, 1]);
  client.dispose();
});

test('dispose invalidates an in-flight welcome before readout publication', async () => {
  let releaseWelcome!: () => void;
  const welcomePending = new Promise<void>((resolve) => { releaseWelcome = resolve; });
  const projection: string[] = [];
  const published: string[] = [];
  const context = {
    renderer: {
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
    },
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
    readout: () => published.push('readout'),
    edit: () => published.push('edit'),
    miss: () => published.push('miss'),
  };
  Object.defineProperty(globalThis, 'WebSocket', { configurable: true, value: FakeWebSocket });
  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: { protocol: 'http:', host: 'craft.test' },
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

test('browser input sends the shared horizontal movement and jump intent', async () => {
  let tick!: () => void;
  const context = {
    renderer: {
      replaceFrame: async () => ({ applied: true, diagnostics: [] }),
      applyFrame: () => ({ applied: true, diagnostics: [] }),
      setCameraPose: () => undefined,
      clear: async () => undefined,
      renderOnce: () => undefined,
      replaceContent: async () => ({ applied: true, diagnostics: [] }),
    },
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
    readout: () => undefined,
    edit: () => undefined,
    miss: () => undefined,
  };
  Object.defineProperty(globalThis, 'WebSocket', { configurable: true, value: FakeWebSocket });
  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: { protocol: 'http:', host: 'craft.test' },
  });
  Object.defineProperty(globalThis, 'window', {
    configurable: true,
    value: { setInterval: (callback: () => void) => { tick = callback; return 1; }, clearInterval: () => undefined },
  });

  const client = new SessionClient(context, view);
  client.connect();
  const socket = FakeWebSocket.latest!;
  socket.emitMessage({ kind: 'welcome', readout: readout(0, 0), frame: { schemaVersion: 1, ops: [] } });
  await new Promise((resolve) => setTimeout(resolve, 0));
  const key = (code: string) => ({ code, repeat: false, preventDefault: () => undefined } as KeyboardEvent);
  client.key(key('KeyW'), true);
  client.key(key('Space'), true);
  tick();

  const sent = JSON.parse(socket.sent.at(-1)!) as {
    protocolVersion: number;
    command: { movement: number[]; jump: boolean };
  };
  assert.equal(sent.protocolVersion, 2);
  assert.deepEqual(sent.command.movement, [1, 0]);
  assert.equal(sent.command.jump, true);
  client.dispose();
});
