import type { RustyApplicationUiContext, RustyApplicationUiOwner } from '@rusty-engine/application-host';
import {
  RuntimeVoxelSpriteGarden,
  type VoxelSpriteCaptureLightingMode,
  type VoxelSpritePostLightingMode,
  type VoxelSpriteRepresentation,
  type VoxelSpriteSide,
  type VoxelSpriteSplatBlendMode,
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
    <p class="controls" data-garden-controls>Rows: spatial wizard · rigged wizard · knight. GRAY is frozen 3D, BLUE a plain capture, RED the 7035 control, and GOLD the selected subject's ghost plate. U subject · I RED mode · O inspect representation · P recapture comparison · V lab panel.</p>
    <form class="voxel-sprite-lab" data-garden-panel hidden>
      <strong>Matched runtime voxel-sprite lab</strong>
      <label>subject <select data-lab-subject><option value="spatial-wizard">spatial wizard</option><option value="rigged-wizard">rigged wizard</option><option value="knight">knight</option></select></label>
      <label>inspect representation <select data-lab-representation><option value="canonical">GRAY frozen 3D</option><option value="baseline">BLUE plain capture</option><option value="enhanced">RED 7035 control</option><option value="ghost" selected>GOLD ghost plate</option></select></label>
      <label><input data-lab-canonical-visible type="checkbox" checked> show frozen 3D inspection copies</label>
      <label>edit side <select data-lab-side><option value="baseline">BLUE runtime proxy</option><option value="enhanced" selected>RED runtime enhanced</option></select></label>
      <label>RED geometry <select data-lab-mode><option value="sprite">plain sprite</option><option value="depth-parallax">quantized depth</option><option value="sprite-splat" selected>sprite + splats</option><option value="full-splat">full replacement</option></select></label>
      <label>sector <input data-lab-sector type="range" min="0" max="15" step="1" value="0"><output data-lab-sector-value>dir-00</output></label>
      <label><input data-lab-auto-sector type="checkbox"> follow capture sector while walking (off freezes source view)</label>
      <label>capture elevation <input data-lab-elevation type="range" min="-30" max="60" step="1" value="18"><output data-lab-elevation-value>18°</output></label>
      <label>capture texture resolution <select data-lab-resolution><option>96</option><option selected>128</option><option>192</option><option>256</option><option>512</option><option>1024</option><option>2048</option><option>4096</option></select></label>
      <output data-lab-capture-cost>four RGBA8 outputs 0.6 MiB + temporary depth 0.1 MiB per capture</output>
      <fieldset><legend>selected side · capture lighting</legend>
        <label>capture mode <select data-lab-capture-mode><option value="isolated">isolated light rig</option><option value="scene">authored scene lights</option></select></label>
        <label>ambient <input data-lab-capture-ambient type="range" min="0" max="4" step="0.1" value="1.8"><output data-lab-capture-ambient-value>1.8</output></label>
        <label>key light <input data-lab-capture-key type="range" min="0" max="6" step="0.1" value="3"><output data-lab-capture-key-value>3.0</output></label>
        <label>fill light <input data-lab-capture-fill type="range" min="0" max="6" step="0.1" value="1.4"><output data-lab-capture-fill-value>1.4</output></label>
      </fieldset>
      <fieldset><legend>selected side · post-capture lighting</legend>
        <label>post mode <select data-lab-post-mode><option value="captured">preserve capture</option><option value="normal">normal relight</option></select></label>
        <label>ambient <input data-lab-post-ambient type="range" min="0" max="2" step="0.05" value="0.35"><output data-lab-post-ambient-value>0.35</output></label>
        <label>diffuse <input data-lab-post-diffuse type="range" min="0" max="3" step="0.05" value="0.9"><output data-lab-post-diffuse-value>0.90</output></label>
        <label>output gain <input data-lab-output-gain type="range" min="0" max="3" step="0.05" value="1.1"><output data-lab-output-gain-value>1.10</output></label>
        <label>light azimuth <input data-lab-light-azimuth type="range" min="-180" max="180" step="5" value="35"><output data-lab-light-azimuth-value>35°</output></label>
        <label>light elevation <input data-lab-light-elevation type="range" min="-90" max="90" step="5" value="45"><output data-lab-light-elevation-value>45°</output></label>
      </fieldset>
      <fieldset><legend>RED geometric enhancement</legend>
      <label>depth amplitude <input data-lab-depth type="range" min="0" max="1.5" step="0.05" value="0.35"><output data-lab-depth-value>0.35</output></label>
      <label>depth quantization levels (not density) <input data-lab-steps type="range" min="0" max="32" step="1" value="8"><output data-lab-steps-value>8</output></label>
      <label>splat sampling grid <select data-lab-splat-resolution><option>32</option><option selected>48</option><option>64</option><option>96</option><option>128</option><option>192</option><option>256</option><option>384</option><option>512</option></select></label>
      <label>splat overlap <input data-lab-overlap type="range" min="0" max="1.5" step="0.05" value="0.15"><output data-lab-overlap-value>0.15</output></label>
      <label>splat opacity <input data-lab-splat-opacity type="range" min="0" max="1" step="0.05" value="1"><output data-lab-splat-opacity-value>1.00</output></label>
      <label>splat composition <select data-lab-splat-blend><option value="depth-write">depth-writing alpha</option><option value="alpha-blend">transparent alpha blend</option><option value="additive">additive blend</option></select></label>
      </fieldset>
      <fieldset><legend>GOLD ghost plate</legend>
        <label>depth retention <select data-lab-ghost-retention><option value="0.02">0.02 near-flat</option><option value="0.08">0.08 restrained</option><option value="0.15" selected>0.15 default</option><option value="0.30">0.30 pronounced</option><option value="1">1.00 original depth</option></select></label>
        <label>anchor policy <select data-lab-ghost-anchor-policy><option value="bounds-center">bounds center</option><option value="bounds-normalized">normalized depth</option></select></label>
        <label>anchor depth <input data-lab-ghost-anchor type="range" min="0" max="1" step="0.01" value="0.5"><output data-lab-ghost-anchor-value>0.50</output></label>
        <label>mapping <select data-lab-ghost-mapping><option value="plate-locked">plate locked</option><option value="projective-surface">projective surface</option></select></label>
        <label>source shell <select data-lab-ghost-shell><option value="whole-mesh">whole mesh (accepted control)</option><option value="strict-source">strict captured shell</option><option value="repaired-source">one-texel repaired shell</option></select></label>
        <label>shell depth tolerance <input data-lab-ghost-shell-epsilon type="range" min="0" max="1" step="0.02" value="0.12"><output data-lab-ghost-shell-epsilon-value>0.12</output></label>
      </fieldset>
      <div class="lab-actions"><button data-lab-reset-ghost type="button">Reset GOLD defaults + current view</button><button data-lab-freeze-view type="button">Capture + freeze current source view</button><button data-lab-recapture-selected type="button">Apply queued capture to selected side</button><button data-lab-recapture-pair type="button">Apply queued capture to comparison</button><button data-lab-match type="button">Copy selected lighting + recapture comparison</button><button data-lab-fallback type="button">Probe fallback</button><button data-lab-resume type="button">Resume game</button></div>
      <output data-lab-comparison>loading</output>
      <output data-lab-metrics>loading</output>
      <output data-lab-ghost>ghost plate loading</output>
      <small>The GOLD plate uses a frozen retained pose, nearest-sampled low-resolution color/coverage/depth, and the same capture sector and isolated lighting as RED. Whole mesh is the accepted task-7087 look. Strict shell rejects geometry that disagrees with captured source depth; repaired shell may recover only from one adjacent texel. Only the selected subject's GOLD plate and marker are shown. GOLD configuration is absolute rather than cumulative; reset restores the accepted whole-mesh defaults and current exact source view. Capture settings and the RED splat grid remain queued until recapture.</small>
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
      get('[data-garden-sector]').textContent = `${value.selectedSubject} · inspect ${representationLabel(value.selectedRepresentation)} · RED ${value.mode} · GOLD ${value.ghostDepthRetention.toFixed(2)} ${value.ghostPlateMapping} ${value.ghostShellMode} · ${value.sectorLabel}${value.autoSector ? ' follow' : ' frozen'} · capture ${value.appliedResolution}px${value.capturePending ? ` → ${value.resolution}px queued` : ''}`;
      get('[data-garden-load]').textContent = value.status === 'loading'
        ? 'loading'
        : `capture ${milliseconds(value.captureMilliseconds)} · steady ${milliseconds(value.steadyStateMilliseconds)} · ${(value.textureBytes / 1024).toFixed(0)} KiB · ${value.drawCalls} draws / ${value.sampleCount} samples · fallback ${value.fallbackPreservedCount}`;
      const panel = get('[data-garden-panel]');
      panel.querySelector<HTMLSelectElement>('[data-lab-subject]')!.value = value.selectedSubject;
      panel.querySelector<HTMLSelectElement>('[data-lab-representation]')!.value = value.selectedRepresentation;
      panel.querySelector<HTMLInputElement>('[data-lab-canonical-visible]')!.checked = value.canonicalVisible;
      panel.querySelector<HTMLSelectElement>('[data-lab-side]')!.value = value.selectedSide;
      panel.querySelector<HTMLSelectElement>('[data-lab-mode]')!.value = value.mode;
      panel.querySelector<HTMLInputElement>('[data-lab-sector]')!.value = String(value.sector);
      panel.querySelector<HTMLOutputElement>('[data-lab-sector-value]')!.value = value.sectorLabel;
      panel.querySelector<HTMLInputElement>('[data-lab-auto-sector]')!.checked = value.autoSector;
      panel.querySelector<HTMLInputElement>('[data-lab-elevation]')!.value = String(value.elevationDegrees);
      panel.querySelector<HTMLOutputElement>('[data-lab-elevation-value]')!.value = `${value.elevationDegrees.toFixed(0)}°`;
      panel.querySelector<HTMLSelectElement>('[data-lab-resolution]')!.value = String(value.resolution);
      panel.querySelector<HTMLOutputElement>('[data-lab-capture-cost]')!.value = `four RGBA8 outputs ${mebibytes(value.captureOutputBytesEstimate)} + temporary depth ${mebibytes(value.captureTemporaryDepthBytesEstimate)} per capture; pair retains ${mebibytes(value.captureOutputBytesEstimate * 2)}`;
      panel.querySelector<HTMLSelectElement>('[data-lab-capture-mode]')!.value = value.captureLightingMode;
      panel.querySelector<HTMLInputElement>('[data-lab-capture-ambient]')!.value = String(value.captureAmbient);
      panel.querySelector<HTMLOutputElement>('[data-lab-capture-ambient-value]')!.value = value.captureAmbient.toFixed(1);
      panel.querySelector<HTMLInputElement>('[data-lab-capture-key]')!.value = String(value.captureKey);
      panel.querySelector<HTMLOutputElement>('[data-lab-capture-key-value]')!.value = value.captureKey.toFixed(1);
      panel.querySelector<HTMLInputElement>('[data-lab-capture-fill]')!.value = String(value.captureFill);
      panel.querySelector<HTMLOutputElement>('[data-lab-capture-fill-value]')!.value = value.captureFill.toFixed(1);
      panel.querySelector<HTMLSelectElement>('[data-lab-post-mode]')!.value = value.postLightingMode;
      panel.querySelector<HTMLInputElement>('[data-lab-post-ambient]')!.value = String(value.postAmbient);
      panel.querySelector<HTMLOutputElement>('[data-lab-post-ambient-value]')!.value = value.postAmbient.toFixed(2);
      panel.querySelector<HTMLInputElement>('[data-lab-post-diffuse]')!.value = String(value.postDiffuse);
      panel.querySelector<HTMLOutputElement>('[data-lab-post-diffuse-value]')!.value = value.postDiffuse.toFixed(2);
      panel.querySelector<HTMLInputElement>('[data-lab-output-gain]')!.value = String(value.outputGain);
      panel.querySelector<HTMLOutputElement>('[data-lab-output-gain-value]')!.value = value.outputGain.toFixed(2);
      panel.querySelector<HTMLInputElement>('[data-lab-light-azimuth]')!.value = String(value.lightAzimuthDegrees);
      panel.querySelector<HTMLOutputElement>('[data-lab-light-azimuth-value]')!.value = `${value.lightAzimuthDegrees.toFixed(0)}°`;
      panel.querySelector<HTMLInputElement>('[data-lab-light-elevation]')!.value = String(value.lightElevationDegrees);
      panel.querySelector<HTMLOutputElement>('[data-lab-light-elevation-value]')!.value = `${value.lightElevationDegrees.toFixed(0)}°`;
      panel.querySelector<HTMLOutputElement>('[data-lab-depth-value]')!.value = value.depthAmplitude.toFixed(2);
      panel.querySelector<HTMLOutputElement>('[data-lab-steps-value]')!.value = String(value.depthQuantizationSteps);
      panel.querySelector<HTMLSelectElement>('[data-lab-splat-resolution]')!.value = String(value.splatResolution);
      panel.querySelector<HTMLOutputElement>('[data-lab-overlap-value]')!.value = value.splatOverlap.toFixed(2);
      panel.querySelector<HTMLOutputElement>('[data-lab-splat-opacity-value]')!.value = value.splatOpacity.toFixed(2);
      panel.querySelector<HTMLSelectElement>('[data-lab-splat-blend]')!.value = value.splatBlendMode;
      panel.querySelector<HTMLSelectElement>('[data-lab-ghost-retention]')!.value =
        value.ghostDepthRetention === 1 ? '1' : value.ghostDepthRetention.toFixed(2);
      panel.querySelector<HTMLSelectElement>('[data-lab-ghost-anchor-policy]')!.value = value.ghostAnchorPolicy;
      panel.querySelector<HTMLInputElement>('[data-lab-ghost-anchor]')!.value = String(value.ghostAnchorValue);
      panel.querySelector<HTMLOutputElement>('[data-lab-ghost-anchor-value]')!.value = `${value.ghostAnchorValue.toFixed(2)} → ${value.ghostAnchorDepth?.toFixed(3) ?? 'n/a'} world depth`;
      panel.querySelector<HTMLSelectElement>('[data-lab-ghost-mapping]')!.value = value.ghostPlateMapping;
      panel.querySelector<HTMLSelectElement>('[data-lab-ghost-shell]')!.value = value.ghostShellMode;
      panel.querySelector<HTMLInputElement>('[data-lab-ghost-shell-epsilon]')!.value = String(value.ghostShellDepthEpsilon);
      panel.querySelector<HTMLOutputElement>('[data-lab-ghost-shell-epsilon-value]')!.value = `${value.ghostShellDepthEpsilon.toFixed(2)} + ${value.ghostShellDepthQuantizationStep === null ? 'n/a' : (value.ghostShellDepthQuantizationStep * 0.5).toFixed(3)} half-step = ${value.ghostShellEffectiveDepthEpsilon?.toFixed(3) ?? 'n/a'} effective`;
      panel.querySelector<HTMLOutputElement>('[data-lab-comparison]')!.value = `capture ${value.captureSettingsMatched ? 'MATCHED' : 'DIFFERENT'} · all lighting ${value.allLightingMatched ? 'MATCHED' : 'DIFFERENT'} · selected ${value.selectedSide.toUpperCase()} ${value.captureLightingMode}/${value.postLightingMode}${value.capturePending ? ' · CAPTURE QUEUED' : ''}${value.splatDensityPending ? ' · SPLAT GRID QUEUED' : ''}`;
      panel.querySelector<HTMLOutputElement>('[data-lab-metrics]')!.value = `${value.resourceCount} GLBs / ${(value.resourceBytes / 1024 / 1024).toFixed(1)} MiB · capture texture ${value.appliedResolution}² / ${mebibytes(value.textureBytes)} resident · RED splats ${value.appliedSplatResolution}² / ${value.splatBlendMode} / ${value.splatOpacity.toFixed(2)} opacity · depth ${value.depthQuantizationSteps} levels · ${value.composition} · capture ${milliseconds(value.captureMilliseconds)} · steady ${milliseconds(value.steadyStateMilliseconds)} · ${value.drawCalls} draws / ${value.sampleCount} samples`;
      panel.querySelector<HTMLOutputElement>('[data-lab-ghost]')!.value = `GOLD ${value.ghostShellMode} · source view ${value.ghostSourceViewAgreement}${value.ghostAngularOffsetDegrees === null ? '' : ` / ${value.ghostAngularOffsetDegrees.toFixed(2)}°`} · pose ${value.ghostMatchedPose ? 'MATCHED' : 'MISMATCH'} · shell ratios reject ${value.ghostRejectedFragmentRatioStatus ?? 'n/a'} / repair ${value.ghostRepairedBoundaryRatioStatus ?? 'n/a'} · fallback ${value.ghostFallbackActive ? value.ghostFallbackReason ?? 'active' : 'none'} · ${value.ghostDrawCalls} draws / ${value.ghostMeshCount} meshes · ${value.ghostMaterialResourceCount} materials + ${value.ghostBorrowedTextureCount} borrowed textures · limits ${value.ghostLimitations.join(', ') || 'none'}`;
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
    else if (control.matches('[data-lab-representation]')) garden.setRepresentation(control.value as VoxelSpriteRepresentation);
    else if (control.matches('[data-lab-canonical-visible]')) garden.setCanonicalVisible((control as HTMLInputElement).checked);
    else if (control.matches('[data-lab-side]')) garden.setSide(control.value as VoxelSpriteSide);
    else if (control.matches('[data-lab-mode]')) garden.setMode(control.value as Parameters<typeof garden.setMode>[0]);
    else if (control.matches('[data-lab-capture-mode]')) garden.setCaptureLightingMode(control.value as VoxelSpriteCaptureLightingMode);
    else if (control.matches('[data-lab-post-mode]')) garden.setPostLightingMode(control.value as VoxelSpritePostLightingMode);
    else if (control.matches('[data-lab-auto-sector]')) garden.setAutoSector((control as HTMLInputElement).checked);
    else if (control.matches('[data-lab-resolution]')) garden.setResolution(Number(control.value));
    else if (control.matches('[data-lab-splat-resolution]')) garden.setSplatResolution(Number(control.value));
    else if (control.matches('[data-lab-splat-blend]')) garden.setSplatBlendMode(control.value as VoxelSpriteSplatBlendMode);
    else if (control.matches('[data-lab-ghost-retention]')) garden.setGhostDepthRetention(Number(control.value));
    else if (control.matches('[data-lab-ghost-anchor-policy]')) garden.setGhostAnchorPolicy(control.value as Parameters<typeof garden.setGhostAnchorPolicy>[0]);
    else if (control.matches('[data-lab-ghost-mapping]')) garden.setGhostPlateMapping(control.value as Parameters<typeof garden.setGhostPlateMapping>[0]);
    else if (control.matches('[data-lab-ghost-shell]')) garden.setGhostShellMode(control.value as Parameters<typeof garden.setGhostShellMode>[0]);
    else if (control.matches('[data-lab-sector]')) garden.setSector(Number(control.value));
  };
  const input = (event: Event) => {
    if (!(event.target instanceof HTMLInputElement)) return;
    const control = event.target;
    if (control.matches('[data-lab-elevation]')) garden.setElevation(Number(control.value));
    else if (control.matches('[data-lab-capture-ambient]')) garden.setCaptureAmbient(Number(control.value));
    else if (control.matches('[data-lab-capture-key]')) garden.setCaptureKey(Number(control.value));
    else if (control.matches('[data-lab-capture-fill]')) garden.setCaptureFill(Number(control.value));
    else if (control.matches('[data-lab-post-ambient]')) garden.setPostAmbient(Number(control.value));
    else if (control.matches('[data-lab-post-diffuse]')) garden.setPostDiffuse(Number(control.value));
    else if (control.matches('[data-lab-output-gain]')) garden.setOutputGain(Number(control.value));
    else if (control.matches('[data-lab-light-azimuth]')) garden.setPostLightAzimuth(Number(control.value));
    else if (control.matches('[data-lab-light-elevation]')) garden.setPostLightElevation(Number(control.value));
    else if (control.matches('[data-lab-depth]')) garden.setDepthAmplitude(Number(control.value));
    else if (control.matches('[data-lab-steps]')) garden.setDepthQuantizationSteps(Number(control.value));
    else if (control.matches('[data-lab-overlap]')) garden.setSplatOverlap(Number(control.value));
    else if (control.matches('[data-lab-splat-opacity]')) garden.setSplatOpacity(Number(control.value));
    else if (control.matches('[data-lab-ghost-anchor]')) garden.setGhostAnchorValue(Number(control.value));
    else if (control.matches('[data-lab-ghost-shell-epsilon]')) garden.setGhostShellDepthEpsilon(Number(control.value));
  };
  const closePanel = () => {
    panel.hidden = true;
    context.ui.setInteractionMode('gameplay');
    context.ui.focusGameplay();
  };
  const panelKey = (event: KeyboardEvent) => {
    if (panel.hidden || event.repeat || (event.code !== 'Escape' && event.code !== 'KeyV')) return;
    closePanel();
    event.preventDefault();
    event.stopImmediatePropagation();
  };
  const click = (event: MouseEvent) => {
    if (!(event.target instanceof Element)) return;
    if (event.target.closest('[data-lab-reset-ghost]') !== null) garden.resetGhostDefaults();
    else if (event.target.closest('[data-lab-freeze-view]') !== null) garden.freezeCurrentSourceView();
    else if (event.target.closest('[data-lab-recapture-selected]') !== null) garden.recaptureSelected();
    else if (event.target.closest('[data-lab-recapture-pair]') !== null) garden.recapturePair();
    else if (event.target.closest('[data-lab-match]') !== null) garden.matchLightingFromSelected();
    else if (event.target.closest('[data-lab-fallback]') !== null) garden.probeFailureFallback();
    else if (event.target.closest('[data-lab-resume]') !== null) closePanel();
  };
  window.addEventListener('keydown', panelKey);
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
    window.removeEventListener('keydown', panelKey);
  };
}

function milliseconds(value: number | null): string {
  return value === null ? 'n/a' : `${value.toFixed(2)} ms`;
}

function mebibytes(value: number): string {
  return `${(value / 1024 / 1024).toFixed(value < 1024 * 1024 ? 2 : 1)} MiB`;
}

function representationLabel(value: VoxelSpriteRepresentation): string {
  if (value === 'canonical') return 'GRAY 3D';
  if (value === 'baseline') return 'BLUE plain';
  if (value === 'enhanced') return 'RED 7035';
  return 'GOLD ghost';
}
