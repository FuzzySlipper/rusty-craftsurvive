import type { RustyApplicationUiContext, RustyApplicationUiOwner } from '@rusty-engine/application-host';
import { SessionClient, type SessionView } from './session-client';

export function mountCraftSurviveUi(root: HTMLElement, context: RustyApplicationUiContext): RustyApplicationUiOwner {
  root.innerHTML = `<section class="hud" aria-label="CraftSurvive status">
    <div class="brand">RUSTY <strong>CRAFTSURVIVE</strong></div>
    <output data-status>connecting</output>
    <dl><div><dt>surface</dt><dd data-surface>—</dd></div><div><dt>terrain</dt><dd data-terrain>—</dd></div><div><dt>residency</dt><dd data-residency>—</dd></div><div><dt>seed</dt><dd data-seed>—</dd></div><div><dt>brush</dt><dd data-brush>—</dd></div><div><dt>mesh</dt><dd data-mesh>—</dd></div><div><dt>startup</dt><dd data-startup>—</dd></div><div><dt>input</dt><dd data-accepted-sequence>—</dd></div><div><dt>player</dt><dd data-player-revision>—</dd></div><div><dt>world</dt><dd data-world-revision>—</dd></div><div><dt>collision</dt><dd data-collision-world>—</dd></div><div><dt>voxels</dt><dd data-voxels>—</dd></div><div><dt>position</dt><dd data-player-position>—</dd></div><div><dt>view</dt><dd data-player-view>—</dd></div><div><dt>motion</dt><dd data-motion>—</dd></div><div><dt>velocity</dt><dd data-player-velocity>—</dd></div><div><dt>ground</dt><dd data-ground>—</dd></div><div><dt>contacts</dt><dd data-contacts>—</dd></div><div><dt>step</dt><dd data-step>—</dd></div><div><dt>platform</dt><dd data-platform>—</dd></div><div><dt>target</dt><dd data-target>—</dd></div></dl>
    <p data-edit>Click the world to capture the mouse.</p>
    <p class="controls">WASD move · mouse look · Space jump · Shift sprint · Control crouch · H impulse · left/F break · right/G place · 1/2/3 brush radius</p>
  </section><div class="crosshair" aria-hidden="true">+</div>`;
  const get = (selector: string) => root.querySelector<HTMLElement>(selector)!;
  const view: SessionView = {
    status: (text) => { get('[data-status]').textContent = text; },
    readout: (value) => {
      get('[data-surface]').textContent = value.surface;
      get('[data-terrain]').textContent = `${value.terrainSize}²`;
      get('[data-residency]').textContent = `${value.residencyCenter.join(',')} · ${value.residentChunks} resident / ${value.pinnedChunks} pinned / ${value.preparingChunks} loading · ${value.admittedChunksTotal} admitted / ${value.evictedChunksTotal} evicted · ${(value.residentChunkBytes / 1024).toFixed(0)} KiB · ${value.residencyGenerationMs.toFixed(1)} + ${value.residencyAdmissionMs.toFixed(1)} ms`;
      get('[data-seed]').textContent = value.terrainSeed;
      get('[data-brush]').textContent = `radius ${value.brushRadius}`;
      get('[data-mesh]').textContent = `${value.meshVertices} vertices · ${value.meshTriangles} triangles`;
      get('[data-startup]').textContent = `${value.generationMs.toFixed(1)} + ${value.authorityBuildMs.toFixed(1)} + ${value.meshBuildMs.toFixed(1)} ms`;
      get('[data-accepted-sequence]').textContent = String(value.acceptedSequence);
      get('[data-player-revision]').textContent = String(value.playerRevision);
      get('[data-world-revision]').textContent = String(value.worldRevision);
      get('[data-collision-world]').textContent = String(value.collisionWorldHash);
      get('[data-voxels]').textContent = String(value.voxelCount);
      get('[data-player-position]').textContent = value.camera.position.map((coordinate) => coordinate.toFixed(2)).join(', ');
      get('[data-player-view]').textContent = `${value.camera.yawDegrees.toFixed(1)}° / ${value.camera.pitchDegrees.toFixed(1)}°`;
      get('[data-player-velocity]').textContent = value.velocity.map((component) => component.toFixed(1)).join(', ');
      get('[data-motion]').textContent = `${value.grounded ? 'grounded' : `airborne · vy ${value.velocity[1].toFixed(1)}`} · ${value.stance}${value.blockedStand ? ' · stand blocked' : ''}`;
      get('[data-ground]').textContent = value.groundNormal === null ? 'none' : `${value.groundSource ?? 'unknown'} · ${value.groundNormal.map((component) => component.toFixed(2)).join(', ')}`;
      get('[data-contacts]').textContent = `${value.contactCount} · ${value.blocks.length === 0 ? 'clear' : value.blocks.join(', ')} · ${value.castCount} casts · ${value.recoveryPasses} recovery`;
      get('[data-step]').textContent = value.stepAttempted ? `${value.stepAccepted ? 'accepted' : 'rejected'} · ${value.stepRise.toFixed(2)}` : 'none';
      get('[data-platform]').textContent = value.platformEntity === null ? 'none' : `${value.platformEntity} · ${value.platformDisplacement.map((component) => component.toFixed(2)).join(', ')}`;
      get('[data-target]').textContent = value.targetedVoxel === null ? 'out of reach' : value.targetedVoxel.join(', ');
    },
    edit: (value) => { get('[data-edit]').textContent = `${value.action} ${value.voxel.join(', ')} · ${value.affectedVoxels} voxels · ${value.editMs.toFixed(1)} ms (${value.meshBuildMs.toFixed(1)} mesh) · ${value.dirtyChunks} dirty / ${value.replacementCount} replaced / ${value.destroyCount} destroyed · ${value.encodedBytes} bytes · revision ${value.revision}`; },
    reject: (value) => { get('[data-edit]').textContent = `edit rejected · ${value.code} at ${value.voxel.join(', ')}`; },
    miss: (action, target) => { get('[data-edit]').textContent = `${action} missed · ${target === null ? 'nothing in reach' : `target ${target.join(', ')}`}`; },
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
