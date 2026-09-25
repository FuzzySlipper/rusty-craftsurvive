import type { LiveDebugTransport } from '@rusty-engine/live-debug';

const READOUT_MS = 500;

/** DOM controls and copied diagnostic text only; C# owns every rope decision. */
export function mountRopes(host: HTMLElement, transport: LiveDebugTransport): Readonly<{ dispose(): void }> {
  const panel = document.createElement('details');
  panel.open = true;
  panel.setAttribute('aria-label', 'Rope playground');
  const title = document.createElement('summary');
  title.textContent = 'Rope playground · provisional physics';
  const help = document.createElement('p');
  help.textContent = 'R attach · T release · hold Q/Z shorten/lengthen · V next anchor. WASD builds swing; Space jumps. Cyan anchors are above the court and gateway; orange hanging object is dynamic. Reel up from the floor, swing past the walls, then release.';
  help.style.cssText = 'max-width:25rem;margin:.3rem 0';
  const actions = document.createElement('div');
  actions.style.cssText = 'display:flex;gap:.25rem;flex-wrap:wrap;max-width:25rem';
  const readout = document.createElement('output');
  readout.id = 'craft-rope-readout';
  readout.style.cssText = 'display:block;white-space:pre-wrap;overflow-wrap:anywhere;max-width:25rem;margin-top:.3rem;font-size:.7rem';
  let disposed = false;
  let reading = false;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const abort = new AbortController();
  async function execute(command: string): Promise<void> {
    try {
      const result = await transport.execute(command, abort.signal);
      if (!disposed) readout.textContent = result.succeeded ? result.message.replaceAll(';', '; ') : `Unavailable: ${result.message}`;
    } catch (error) { if (!disposed) readout.textContent = String(error); }
  }
  for (const [label, command] of [
    ['Court anchor', 'craft.rope.station 0'], ['Gateway anchor', 'craft.rope.station 1'],
    ['Dynamic object', 'craft.rope.station 2'], ['Attach (R)', 'craft.rope.attach'],
    ['Release (T)', 'craft.rope.release'], ['Reel to 4m', 'craft.rope.length 4'],
    ['Let out to 9m', 'craft.rope.length 9'],
    ['Reset to court', 'craft.rope.reset'],
  ] as const) {
    const button = document.createElement('button');
    button.type = 'button'; button.textContent = label;
    button.addEventListener('click', () => { void execute(command); });
    actions.append(button);
  }
  async function refresh(): Promise<void> {
    if (disposed) return;
    if (panel.open && !reading) {
      reading = true;
      await execute('craft.rope.inspect');
      reading = false;
    }
    if (!disposed) timer = setTimeout(() => { void refresh(); }, READOUT_MS);
  }
  panel.append(title, help, actions, readout); host.append(panel);
  void refresh();
  return { dispose() { disposed = true; abort.abort(); if (timer !== undefined) clearTimeout(timer); panel.remove(); } };
}
