import type { RustyApplicationUiContext } from '@rusty-engine/application-host';

type Surface = 'box' | 'marchingCubes' | 'dualContouring';
interface CameraPose { position: [number, number, number]; yawDegrees: number; pitchDegrees: number }
interface Readout {
  generation: number; acceptedSequence: number; playerRevision: number;
  camera: CameraPose; surface: Surface; worldRevision: number;
  authorityHash: number; voxelCount: number;
}
interface EditReadout { action: 'destroy' | 'place'; voxel: [number, number, number]; revision: number }
type ServerMessage =
  | { kind: 'welcome'; readout: Readout; frame: Record<string, unknown> }
  | { kind: 'update'; update: { readout: Readout; edit: EditReadout | null; frame: Record<string, unknown> | null } }
  | { kind: 'rejected'; code: string; message: string; readout: Readout };

export interface SessionView {
  status(text: string): void;
  readout(value: Readout): void;
  edit(value: EditReadout): void;
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

  constructor(context: RustyApplicationUiContext, view: SessionView) {
    this.#context = context;
    this.#view = view;
  }

  connect(): void {
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const socket = new WebSocket(`${protocol}//${location.host}/api/session`);
    this.#socket = socket;
    this.#view.status('connecting');
    socket.addEventListener('open', () => this.#view.status('connected'));
    socket.addEventListener('message', (event) => void this.#receive(String(event.data)));
    socket.addEventListener('close', () => this.#reset('disconnected'));
    socket.addEventListener('error', () => this.#view.status('connection error'));
    this.#timer = window.setInterval(() => this.#sendIntent(), 33);
  }

  key(event: KeyboardEvent, down: boolean): void {
    if (!this.#context.ui.allowsGameplayInput(event)) return;
    if (down) this.#held.add(event.code); else this.#held.delete(event.code);
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
    this.#reset('closed');
    this.#socket?.close();
    this.#socket = null;
  }

  async #receive(raw: string): Promise<void> {
    const message = JSON.parse(raw) as ServerMessage;
    if (message.kind === 'rejected') {
      this.#view.status(`rejected: ${message.code}`);
      this.#applyReadout(message.readout);
      return;
    }
    const update = message.kind === 'welcome' ? message : message.update;
    if (message.kind === 'welcome') {
      this.#generation = message.readout.generation;
      this.#sequence = message.readout.acceptedSequence;
      const receipt = await this.#context.renderer.replaceFrame(message.frame);
      if (!receipt.applied) throw new Error(receipt.diagnostics.map(({ message }) => message).join('; '));
    } else if (update.frame !== null) {
      const receipt = await this.#context.renderer.replaceFrame(update.frame);
      if (!receipt.applied) throw new Error(receipt.diagnostics.map(({ message }) => message).join('; '));
    }
    this.#applyReadout(update.readout);
    if ('edit' in update && update.edit !== null) this.#view.edit(update.edit);
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
    this.#view.status(status);
  }
}
