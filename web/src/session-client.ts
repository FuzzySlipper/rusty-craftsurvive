import type { RustyApplicationUiContext } from '@rusty-engine/application-host';

type Surface = 'box' | 'marchingCubes' | 'dualContouring';
interface CameraPose { position: [number, number, number]; yawDegrees: number; pitchDegrees: number }
interface Readout {
  generation: number; acceptedSequence: number; playerRevision: number;
  camera: CameraPose; surface: Surface; worldRevision: number;
  authorityHash: number; voxelCount: number; targetedVoxel: [number, number, number] | null;
}
interface EditReadout { action: 'destroy' | 'place'; voxel: [number, number, number]; revision: number }
type ServerMessage =
  | { kind: 'welcome'; readout: Readout; frame: Record<string, unknown> }
  | { kind: 'update'; update: { readout: Readout; action: 'destroy' | 'place' | null; edit: EditReadout | null; frame: Record<string, unknown> | null } }
  | { kind: 'rejected'; code: string; message: string; readout: Readout };

export interface SessionView {
  status(text: string): void;
  readout(value: Readout): void;
  edit(value: EditReadout): void;
  miss(action: 'destroy' | 'place', target: [number, number, number] | null): void;
}

export class SessionClient {
  readonly #context: RustyApplicationUiContext;
  readonly #view: SessionView;
  readonly #held = new Set<string>();
  #socket: WebSocket | null = null;
  #generation = 0;
  #sequence = 0;
  #look: [number, number] = [0, 0];
  #action: 'destroy' | 'place' | null = null;
  #timer = 0;
  #projectionTail: Promise<void> = Promise.resolve();
  #protocolFailed = false;
  #lifecycleEpoch = 0;

  constructor(context: RustyApplicationUiContext, view: SessionView) {
    this.#context = context;
    this.#view = view;
  }

  connect(): void {
    const epoch = ++this.#lifecycleEpoch;
    this.#protocolFailed = false;
    this.#projectionTail = Promise.resolve();
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const socket = new WebSocket(`${protocol}//${location.host}/api/session`);
    this.#socket = socket;
    this.#view.status('connecting');
    socket.addEventListener('open', () => this.#view.status('connected'));
    socket.addEventListener('message', (event) => {
      const raw = String(event.data);
      this.#projectionTail = this.#projectionTail.then(async () => {
        if (this.#active(epoch) && !this.#protocolFailed) await this.#receive(raw, epoch);
      }).catch((error: unknown) => {
        if (!this.#active(epoch)) return;
        this.#protocolFailed = true;
        this.#view.status(`protocol error: ${error instanceof Error ? error.message : String(error)}`);
        socket.close();
      });
    });
    socket.addEventListener('close', () => {
      if (this.#active(epoch)) this.#reset('disconnected');
    });
    socket.addEventListener('error', () => {
      if (this.#active(epoch)) this.#view.status('connection error');
    });
    this.#timer = window.setInterval(() => this.#sendIntent(), 33);
  }

  key(event: KeyboardEvent, down: boolean): void {
    if (!this.#context.ui.allowsGameplayInput(event)) return;
    if (down) this.#held.add(event.code); else this.#held.delete(event.code);
    if (down && !event.repeat && event.code === 'KeyF') this.#action = 'destroy';
    if (down && !event.repeat && event.code === 'KeyG') this.#action = 'place';
    if (['KeyW', 'KeyA', 'KeyS', 'KeyD', 'Space', 'ShiftLeft'].includes(event.code)) {
      event.preventDefault();
    }
  }

  look(event: MouseEvent): void {
    if (!this.#context.ui.allowsGameplayInput(event) || document.pointerLockElement === null) return;
    this.#look[0] += event.movementX * 0.12;
    this.#look[1] -= event.movementY * 0.12;
  }

  mouse(event: MouseEvent): void {
    if (!this.#context.ui.allowsGameplayInput(event)) return;
    if (document.pointerLockElement === null) {
      this.#context.ui.focusGameplay();
      return;
    }
    if (event.button === 0) this.#action = 'destroy';
    if (event.button === 2) this.#action = 'place';
    event.preventDefault();
  }

  dispose(): void {
    window.clearInterval(this.#timer);
    const socket = this.#socket;
    this.#socket = null;
    this.#reset('closed');
    socket?.close();
  }

  async #receive(raw: string, epoch: number): Promise<void> {
    if (!this.#active(epoch)) return;
    const message = decodeServerMessage(raw);
    if (message.kind === 'rejected') {
      this.#view.status(`rejected: ${message.code}`);
      if (!this.#active(epoch)) return;
      this.#applyReadout(message.readout);
      return;
    }
    const update = message.kind === 'welcome' ? message : message.update;
    if (message.kind === 'welcome') {
      const receipt = await this.#context.renderer.replaceFrame(message.frame);
      if (!this.#active(epoch)) return;
      if (!receipt.applied) throw new Error(receipt.diagnostics.map(({ message }) => message).join('; '));
      this.#generation = message.readout.generation;
      this.#sequence = message.readout.acceptedSequence;
    } else if (update.frame !== null) {
      if (!this.#active(epoch)) return;
      const receipt = this.#context.renderer.applyFrame(update.frame);
      if (!receipt.applied) throw new Error(receipt.diagnostics.map(({ message }) => message).join('; '));
    }
    this.#applyReadout(update.readout);
    if ('edit' in update && update.edit !== null) this.#view.edit(update.edit);
    else if ('action' in update && update.action !== null) this.#view.miss(update.action, update.readout.targetedVoxel);
  }

  #applyReadout(readout: Readout): void {
    this.#generation = readout.generation;
    this.#sequence = Math.max(this.#sequence, readout.acceptedSequence);
    this.#context.renderer.setCameraPose(readout.camera);
    this.#view.readout(readout);
  }

  #sendIntent(): void {
    if (this.#socket?.readyState !== WebSocket.OPEN || this.#generation === 0) return;
    const axis = (positive: string, negative: string) => Number(this.#held.has(positive)) - Number(this.#held.has(negative));
    this.#sequence += 1;
    this.#socket.send(JSON.stringify({
      kind: 'input', protocolVersion: 1, generation: this.#generation, sequence: this.#sequence,
      command: {
        movement: [axis('KeyW', 'KeyS'), axis('KeyD', 'KeyA'), axis('Space', 'ShiftLeft')],
        lookDeltaDegrees: this.#look, deltaSeconds: 0.033, action: this.#action,
      },
    }));
    this.#look = [0, 0];
    this.#action = null;
  }

  #reset(status: string): void {
    this.#held.clear(); this.#look = [0, 0]; this.#action = null; this.#generation = 0;
    this.#protocolFailed = false;
    this.#view.status(status);
    this.#lifecycleEpoch += 1;
  }

  #active(epoch: number): boolean {
    return epoch === this.#lifecycleEpoch && this.#socket !== null;
  }
}

function decodeServerMessage(raw: string): ServerMessage {
  const value: unknown = JSON.parse(raw);
  const object = record(value, 'server message');
  const kind = text(object['kind'], 'server message kind');
  if (kind === 'welcome') {
    return { kind, readout: decodeReadout(object['readout']), frame: record(object['frame'], 'welcome frame') };
  }
  if (kind === 'update') {
    const update = record(object['update'], 'session update');
    return {
      kind,
      update: {
        readout: decodeReadout(update['readout']),
        action: update['action'] === null ? null : decodeAction(update['action']),
        edit: update['edit'] === null ? null : decodeEdit(update['edit']),
        frame: update['frame'] === null ? null : record(update['frame'], 'update frame'),
      },
    };
  }
  if (kind === 'rejected') {
    return {
      kind,
      code: text(object['code'], 'rejection code'),
      message: text(object['message'], 'rejection message'),
      readout: decodeReadout(object['readout']),
    };
  }
  throw new Error(`unsupported Rust session message kind: ${kind}`);
}

function decodeReadout(value: unknown): Readout {
  const object = record(value, 'session readout');
  const camera = record(object['camera'], 'camera pose');
  const position = tuple3(camera['position'], 'camera position');
  const surface = text(object['surface'], 'surface');
  if (!['box', 'marchingCubes', 'dualContouring'].includes(surface)) throw new Error(`unsupported surface: ${surface}`);
  return {
    generation: number(object['generation'], 'generation'),
    acceptedSequence: number(object['acceptedSequence'], 'accepted sequence'),
    playerRevision: number(object['playerRevision'], 'player revision'),
    camera: {
      position,
      yawDegrees: number(camera['yawDegrees'], 'camera yaw'),
      pitchDegrees: number(camera['pitchDegrees'], 'camera pitch'),
    },
    surface: surface as Surface,
    worldRevision: number(object['worldRevision'], 'world revision'),
    authorityHash: number(object['authorityHash'], 'authority hash'),
    voxelCount: number(object['voxelCount'], 'voxel count'),
    targetedVoxel: object['targetedVoxel'] === null ? null : tuple3(object['targetedVoxel'], 'targeted voxel'),
  };
}

function decodeAction(value: unknown): 'destroy' | 'place' {
  const action = text(value, 'session action');
  if (action !== 'destroy' && action !== 'place') throw new Error(`unsupported session action: ${action}`);
  return action;
}

function decodeEdit(value: unknown): EditReadout {
  const object = record(value, 'edit readout');
  const action = text(object['action'], 'edit action');
  if (action !== 'destroy' && action !== 'place') throw new Error(`unsupported edit action: ${action}`);
  return { action, voxel: tuple3(object['voxel'], 'edit voxel'), revision: number(object['revision'], 'edit revision') };
}

function record(value: unknown, label: string): Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new Error(`${label} must be an object`);
  return value as Record<string, unknown>;
}

function text(value: unknown, label: string): string {
  if (typeof value !== 'string') throw new Error(`${label} must be a string`);
  return value;
}

function number(value: unknown, label: string): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) throw new Error(`${label} must be finite`);
  return value;
}

function tuple3(value: unknown, label: string): [number, number, number] {
  if (!Array.isArray(value) || value.length !== 3) throw new Error(`${label} must contain three numbers`);
  return [number(value[0], label), number(value[1], label), number(value[2], label)];
}
