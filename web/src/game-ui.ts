import type { RustyApplicationUiContext, RustyApplicationUiOwner } from '@rusty-engine/application-host';
import {
  RuntimeVoxelSpriteGarden,
  type VoxelSpriteProducer,
  type VoxelSpriteSubject,
} from './runtime-voxel-sprite-garden';
import { SessionClient, type SessionView } from './session-client';

export function mountCraftSurviveUi(root: HTMLElement, context: RustyApplicationUiContext): RustyApplicationUiOwner {
  root.innerHTML = `<section class="hud" aria-label="CraftSurvive status">
    <div class="brand">RUSTY <strong>CRAFTSURVIVE</strong></div>
    <output data-status>connecting</output>
    <output data-presentation-diagnostic></output>
    <dl><div><dt>surface</dt><dd data-surface>—</dd></div><div><dt>terrain</dt><dd data-terrain>—</dd></div><div data-garden-row><dt>lab selection</dt><dd data-garden-sector>loading</dd></div><div data-garden-row><dt>lab cost</dt><dd data-garden-load>loading</dd></div><div><dt>residency</dt><dd data-residency>—</dd></div><div><dt>seed</dt><dd data-seed>—</dd></div><div><dt>brush</dt><dd data-brush>—</dd></div><div><dt>mesh</dt><dd data-mesh>—</dd></div><div><dt>startup</dt><dd data-startup>—</dd></div><div><dt>input</dt><dd data-accepted-sequence>—</dd></div><div><dt>player</dt><dd data-player-revision>—</dd></div><div><dt>world</dt><dd data-world-revision>—</dd></div><div><dt>origin</dt><dd data-world-origin>—</dd></div><div><dt>collision</dt><dd data-collision-world>—</dd></div><div><dt>voxels</dt><dd data-voxels>—</dd></div><div><dt>global position</dt><dd data-player-position>—</dd></div><div><dt>local position</dt><dd data-player-local-position>—</dd></div><div><dt>view</dt><dd data-player-view>—</dd></div><div><dt>motion</dt><dd data-motion>—</dd></div><div><dt>velocity</dt><dd data-player-velocity>—</dd></div><div><dt>ground</dt><dd data-ground>—</dd></div><div><dt>contacts</dt><dd data-contacts>—</dd></div><div><dt>step</dt><dd data-step>—</dd></div><div><dt>platform</dt><dd data-platform>—</dd></div><div><dt>target</dt><dd data-target>—</dd></div></dl>
    <p data-edit>Click the world to capture the mouse.</p>
    <p class="controls" data-garden-controls>Rows: spatial wizard · rigged wizard · knight. BLUE is the prepared sprite baseline; ORANGE is the selected enhancement. U subject · I mode · O source · P recapture · V lab panel.</p>
    <form class="voxel-sprite-lab" data-garden-panel hidden>
      <strong>Runtime voxel-sprite lab</strong>
      <label>subject <select data-lab-subject><option value="spatial-wizard">spatial wizard</option><option value="rigged-wizard">rigged wizard</option><option value="knight">knight</option></select></label>
      <label>source <select data-lab-producer><option value="runtime">runtime capture</option><option value="prepared">prepared frame</option></select></label>
      <label>mode <select data-lab-mode><option value="sprite">plain sprite</option><option value="relit">normal relight</option><option value="depth-parallax">quantized depth</option><option value="sprite-splat">sprite + splats</option><option value="full-splat">full replacement</option></select></label>
      <label>sector <input data-lab-sector type="range" min="0" max="15" step="1" value="0"><output data-lab-sector-value>dir-00</output></label>
      <label><input data-lab-auto-sector type="checkbox" checked> auto sector while walking</label>
      <label>capture elevation <input data-lab-elevation type="range" min="-30" max="60" step="1" value="18"><output data-lab-elevation-value>18°</output></label>
      <label>capture resolution <select data-lab-resolution><option>64</option><option selected>96</option><option>128</option><option>192</option></select></label>
      <label>depth amplitude <input data-lab-depth type="range" min="0" max="1.5" step="0.05" value="0.35"><output data-lab-depth-value>0.35</output></label>
      <label>depth steps <input data-lab-steps type="range" min="0" max="32" step="1" value="8"><output data-lab-steps-value>8</output></label>
      <label>splat overlap <input data-lab-overlap type="range" min="0" max="1.5" step="0.05" value="0.15"><output data-lab-overlap-value>0.15</output></label>
      <div class="lab-actions"><button data-lab-recapture type="button">Recapture selected</button><button data-lab-fallback type="button">Probe fallback</button><button data-lab-resume type="button">Resume game</button></div>
      <output data-lab-metrics>loading</output>
      <small>Prepared normals are the original Blender-world bake and intentionally remain an authoring-variable warning.</small>
    </form>
    <p class="controls">WASD move · mouse look · Space jump · Shift sprint · Control crouch · H impulse · left/F break · right/G place · 1/2/3 brush radius</p>
  </section><div class="crosshair" aria-hidden="true">+</div>`;
  const get = (selector: string) => root.querySelector<HTMLElement>(selector)!;
  const gardenEnabled = new URLSearchParams(location.search).get('course') === 'garden';
  for (const element of root.querySelectorAll<HTMLElement>('[data-garden-row], [data-garden-controls]')) {
    element.hidden = !gardenEnabled;
  }
  const garden = gardenEnabled ? new RuntimeVoxelSpriteGarden(
    context.renderer,
    (value) => {
      get('[data-garden-sector]').textContent = `${value.selectedSubject} · ${value.producer} · ${value.mode} · ${value.sectorLabel}${value.autoSector ? ' auto' : ' manual'} · elev ${value.elevationDegrees.toFixed(0)}°`;
      get('[data-garden-load]').textContent = value.status === 'loading'
        ? 'loading'
        : `capture ${milliseconds(value.captureMilliseconds)} · steady ${milliseconds(value.steadyStateMilliseconds)} · ${(value.textureBytes / 1024).toFixed(0)} KiB · ${value.drawCalls} draws / ${value.sampleCount} samples · fallback ${value.fallbackPreservedCount}`;
      const panel = get('[data-garden-panel]');
      panel.querySelector<HTMLSelectElement>('[data-lab-subject]')!.value = value.selectedSubject;
      panel.querySelector<HTMLSelectElement>('[data-lab-producer]')!.value = value.producer;
      panel.querySelector<HTMLSelectElement>('[data-lab-mode]')!.value = value.mode;
      panel.querySelector<HTMLInputElement>('[data-lab-sector]')!.value = String(value.sector);
      panel.querySelector<HTMLOutputElement>('[data-lab-sector-value]')!.value = value.sectorLabel;
      panel.querySelector<HTMLInputElement>('[data-lab-auto-sector]')!.checked = value.autoSector;
      panel.querySelector<HTMLOutputElement>('[data-lab-elevation-value]')!.value = `${value.elevationDegrees.toFixed(0)}°`;
      panel.querySelector<HTMLOutputElement>('[data-lab-depth-value]')!.value = value.depthAmplitude.toFixed(2);
      panel.querySelector<HTMLOutputElement>('[data-lab-steps-value]')!.value = String(value.depthQuantizationSteps);
      panel.querySelector<HTMLOutputElement>('[data-lab-overlap-value]')!.value = value.splatOverlap.toFixed(2);
      panel.querySelector<HTMLOutputElement>('[data-lab-metrics]')!.value = `${value.resourceCount} files / ${(value.resourceBytes / 1024 / 1024).toFixed(1)} MiB · capture ${milliseconds(value.captureMilliseconds)} · steady ${milliseconds(value.steadyStateMilliseconds)} · ${(value.textureBytes / 1024).toFixed(0)} KiB · ${value.drawCalls} draws / ${value.sampleCount} samples`;
    },
    (text) => { get('[data-presentation-diagnostic]').textContent = text; },
  ) : null;
  const disposeGardenPanel = garden === null
    ? () => undefined
    : bindGardenPanel(root, context, garden);
  const view: SessionView = {
    status: (text) => { get('[data-status]').textContent = text; },
    diagnostic: (text) => { get('[data-presentation-diagnostic]').textContent = text; },
    readout: (value) => {
      get('[data-surface]').textContent = value.surface;
      get('[data-terrain]').textContent = `unbounded v${value.terrainGenerationVersion} · scale ${value.terrainSize} · ${value.editOverlayEntries} edits`;
      get('[data-residency]').textContent = `${value.residencyCenter.join(',')} · ${value.residentChunks} resident / ${value.pinnedChunks} pinned / ${value.preparingChunks} loading · ${value.admittedChunksTotal} admitted / ${value.evictedChunksTotal} evicted · ${(value.residentChunkBytes / 1024).toFixed(0)} KiB · ${value.residencyGenerationMs.toFixed(1)} + ${value.residencyAdmissionMs.toFixed(1)} ms`;
      get('[data-seed]').textContent = value.terrainSeed;
      get('[data-brush]').textContent = `radius ${value.brushRadius}`;
      get('[data-mesh]').textContent = `${value.meshVertices} vertices · ${value.meshTriangles} triangles`;
      get('[data-startup]').textContent = `${value.generationMs.toFixed(1)} + ${value.authorityBuildMs.toFixed(1)} + ${value.meshBuildMs.toFixed(1)} ms`;
      get('[data-accepted-sequence]').textContent = String(value.acceptedSequence);
      get('[data-player-revision]').textContent = String(value.playerRevision);
      get('[data-world-revision]').textContent = String(value.worldRevision);
      get('[data-world-origin]').textContent = `${value.worldOrigin.join(', ')} · revision ${value.worldOriginRevision}`;
      get('[data-collision-world]').textContent = String(value.collisionWorldHash);
      get('[data-voxels]').textContent = String(value.voxelCount);
      get('[data-player-position]').textContent = value.player.position.map((coordinate) => coordinate.toFixed(2)).join(', ');
      get('[data-player-local-position]').textContent = value.playerLocalPosition.map((coordinate) => coordinate.toFixed(2)).join(', ');
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
  const client = new SessionClient(context, view, garden);
  const down = (event: KeyboardEvent) => {
    if (event.code === 'KeyV' && !event.repeat && context.ui.allowsGameplayInput(event) && garden !== null) {
      get('[data-garden-panel]').hidden = false;
      context.ui.setInteractionMode('interface');
      event.preventDefault();
      return;
    }
    if (event.code === 'Tab' && !event.repeat && context.ui.allowsGameplayInput(event)) {
      root.querySelector('.hud')?.classList.toggle('is-hidden');
      event.preventDefault();
      return;
    }
    client.key(event, true);
  };
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
    window.removeEventListener('contextmenu', mouse); disposeGardenPanel(); client.dispose();
  }};
}

function bindGardenPanel(
  root: HTMLElement,
  context: RustyApplicationUiContext,
  garden: RuntimeVoxelSpriteGarden,
): () => void {
  const panel = root.querySelector<HTMLFormElement>('[data-garden-panel]')!;
  const submit = (event: Event) => {
    if (event.target === panel) event.preventDefault();
  };
  const pointerDown = (event: PointerEvent) => {
    if (event.target instanceof Element && event.target.closest('[data-garden-panel]') !== null) {
      context.ui.setInteractionMode('interface');
    }
  };
  const change = (event: Event) => {
    if (!(event.target instanceof HTMLInputElement || event.target instanceof HTMLSelectElement)) return;
    const control = event.target;
    if (control.matches('[data-lab-subject]')) garden.setSubject(control.value as VoxelSpriteSubject);
    else if (control.matches('[data-lab-producer]')) garden.setProducer(control.value as VoxelSpriteProducer);
    else if (control.matches('[data-lab-mode]')) garden.setMode(control.value as Parameters<typeof garden.setMode>[0]);
    else if (control.matches('[data-lab-auto-sector]')) garden.setAutoSector((control as HTMLInputElement).checked);
    else if (control.matches('[data-lab-resolution]')) garden.setResolution(Number(control.value));
  };
  const input = (event: Event) => {
    if (!(event.target instanceof HTMLInputElement)) return;
    const control = event.target;
    if (control.matches('[data-lab-sector]')) garden.setSector(Number(control.value));
    else if (control.matches('[data-lab-elevation]')) garden.setElevation(Number(control.value));
    else if (control.matches('[data-lab-depth]')) garden.setDepthAmplitude(Number(control.value));
    else if (control.matches('[data-lab-steps]')) garden.setDepthQuantizationSteps(Number(control.value));
    else if (control.matches('[data-lab-overlap]')) garden.setSplatOverlap(Number(control.value));
  };
  const click = (event: MouseEvent) => {
    if (!(event.target instanceof Element)) return;
    if (event.target.closest('[data-lab-recapture]') !== null) garden.recapture();
    else if (event.target.closest('[data-lab-fallback]') !== null) garden.probeFailureFallback();
    else if (event.target.closest('[data-lab-resume]') !== null) {
      panel.hidden = true;
      context.ui.setInteractionMode('gameplay');
      context.ui.focusGameplay();
    }
  };
  root.addEventListener('submit', submit);
  root.addEventListener('pointerdown', pointerDown);
  root.addEventListener('change', change);
  root.addEventListener('input', input);
  root.addEventListener('click', click);
  return () => {
    root.removeEventListener('submit', submit);
    root.removeEventListener('pointerdown', pointerDown);
    root.removeEventListener('change', change);
    root.removeEventListener('input', input);
    root.removeEventListener('click', click);
  };
}

function milliseconds(value: number | null): string {
  return value === null ? 'n/a' : `${value.toFixed(2)} ms`;
}
