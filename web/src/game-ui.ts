import type { RustyApplicationUiContext, RustyApplicationUiOwner } from '@rusty-engine/application-host';
import { SessionClient, type SessionView } from './session-client';

export function mountCraftSurviveUi(root: HTMLElement, context: RustyApplicationUiContext): RustyApplicationUiOwner {
  root.innerHTML = `<section class="hud" aria-label="CraftSurvive status">
    <div class="brand">RUSTY <strong>CRAFTSURVIVE</strong></div>
    <output data-status>connecting</output>
    <dl><div><dt>surface</dt><dd data-surface>—</dd></div><div><dt>world</dt><dd data-revision>—</dd></div><div><dt>voxels</dt><dd data-voxels>—</dd></div></dl>
    <p data-edit>Click the world to capture the mouse.</p>
    <p class="controls">WASD move · mouse look · left break · right place · space/shift rise/fall</p>
  </section><div class="crosshair" aria-hidden="true">+</div>`;
  const get = (selector: string) => root.querySelector<HTMLElement>(selector)!;
  const view: SessionView = {
    status: (text) => { get('[data-status]').textContent = text; root.dataset.sessionStatus = text; },
    readout: (value) => {
      get('[data-surface]').textContent = value.surface;
      get('[data-revision]').textContent = String(value.worldRevision);
      get('[data-voxels]').textContent = String(value.voxelCount);
      root.dataset.worldRevision = String(value.worldRevision);
      root.dataset.playerRevision = String(value.playerRevision);
    },
    edit: (value) => { get('[data-edit]').textContent = `${value.action} ${value.voxel.join(', ')} · revision ${value.revision}`; },
  };
  const client = new SessionClient(context, view);
  const down = (event: KeyboardEvent) => client.key(event, true);
  const up = (event: KeyboardEvent) => client.key(event, false);
  const move = (event: MouseEvent) => client.look(event);
  const mouse = (event: MouseEvent) => client.mouse(event);
  window.addEventListener('keydown', down);
  window.addEventListener('keyup', up);
  window.addEventListener('mousemove', move);
  window.addEventListener('mousedown', mouse);
  window.addEventListener('contextmenu', mouse);
  client.connect();
  return { dispose: () => {
    window.removeEventListener('keydown', down); window.removeEventListener('keyup', up);
    window.removeEventListener('mousemove', move); window.removeEventListener('mousedown', mouse);
    window.removeEventListener('contextmenu', mouse); client.dispose();
  }};
}
