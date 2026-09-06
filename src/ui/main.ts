import {
  createLiveDebugHttpTransport,
  mountLiveDebugPanel,
  mountRendererMetricsWidget,
  type LiveDebugPanelMount,
  type LiveDebugResult,
  type LiveDebugTransport,
} from '@rusty-engine/live-debug';

const READOUT_INTERVAL_MS = 750;
const PRESETS = ['accepted', 'current', 'wide', 'strict', 'scene-lighting'];
const SECTORS = ['1', '4', '8', '16'];
const LIGHTING = ['Isolated', 'Scene'];
const ANCHORS = ['BoundsCenter', 'BoundsNormalized'];
const MAPPINGS = ['PlateLocked', 'ProjectiveSurface'];
const SHELLS = ['WholeMesh', 'RepairedSource', 'StrictSource'];

type GhostControls = Readonly<{
  preset: HTMLSelectElement; visible: HTMLInputElement; sectors: HTMLSelectElement; hysteresis: HTMLInputElement;
  depth: HTMLInputElement; anchor: HTMLSelectElement; anchorValue: HTMLInputElement; mapping: HTMLSelectElement;
  shell: HTMLSelectElement; epsilon: HTMLInputElement; resolution: HTMLInputElement; azimuth: HTMLInputElement;
  elevation: HTMLInputElement; near: HTMLInputElement; far: HTMLInputElement; fov: HTMLInputElement;
  lighting: HTMLSelectElement; ambient: HTMLInputElement; key: HTMLInputElement; fill: HTMLInputElement;
  x: HTMLInputElement; y: HTMLInputElement; z: HTMLInputElement; width: HTMLInputElement; height: HTMLInputElement;
}>;
type Readout = Readonly<{ fields: ReadonlyMap<string, string>; raw: string }>;

/** Mounts DOM-only Ghost controls. C# remains the owner of every game setting. */
export function mountProductUi(root: Element): Readonly<{ dispose(): void }> {
  const panel = document.createElement('aside');
  panel.setAttribute('aria-label', 'Rusty CraftSurvive status');
  panel.setAttribute('data-rusty-ui-interactive', '');
  panel.style.cssText = 'background:rgb(15 19 25 / 88%);border:1px solid #62748a;border-radius:.35rem;color:#edf5ff;font:.76rem/1.25 ui-monospace,SFMono-Regular,Menlo,monospace;max-height:calc(100vh - 1rem);max-width:calc(100vw - 1rem);overflow:auto;padding:.35rem .45rem;pointer-events:auto;position:relative;width:fit-content;z-index:1;';
  isolateEvents(panel);
  const header = document.createElement('header');
  header.style.cssText = 'align-items:center;display:flex;gap:.55rem;justify-content:space-between;';
  const title = document.createElement('strong');
  title.textContent = 'Rusty CraftSurvive';
  const metricsToggle = button('Show metrics');
  metricsToggle.setAttribute('aria-pressed', 'false');
  const status = document.createElement('p');
  status.hidden = true;
  status.setAttribute('role', 'alert');
  header.append(title, metricsToggle);
  panel.append(header, status);

  const transport = createLiveDebugHttpTransport();
  const metricsHost = document.createElement('div');
  metricsHost.id = 'craft-renderer-metrics';
  metricsHost.setAttribute('aria-label', 'Renderer performance metrics');
  metricsHost.style.cssText = 'margin-top:.3rem;max-width:min(24rem,calc(100vw - 2rem));overflow:auto;';
  panel.append(metricsHost);
  const metrics = mountRendererMetricsWidget(metricsHost, { initiallyVisible: false, transport });
  metricsToggle.setAttribute('aria-controls', metricsHost.id);
  const debugButton = button('Open live debug');
  debugButton.setAttribute('aria-expanded', 'false');
  const debugHost = document.createElement('div');
  debugHost.id = 'craft-live-debug';
  debugHost.hidden = true;
  debugButton.setAttribute('aria-controls', debugHost.id);
  panel.append(debugButton, debugHost);
  let debugPanel: LiveDebugPanelMount | null = null;
  let disposed = false;
  let metricsVisible = false;
  metricsToggle.addEventListener('click', () => {
    if (disposed) return;
    const nextVisible = !metricsVisible;
    metricsToggle.disabled = true;
    status.hidden = true;
    void transport.execute(nextVisible ? 'engine.renderer.show' : 'engine.renderer.hide').then((result) => {
      if (disposed) return;
      if (!result.succeeded) {
        status.textContent = message(new Error(result.message), 'Renderer metrics command failed.');
        status.hidden = false;
        return;
      }

      metricsVisible = nextVisible;
      metricsToggle.textContent = metricsVisible ? 'Hide metrics' : 'Show metrics';
      metricsToggle.setAttribute('aria-pressed', String(metricsVisible));
    }).catch((error: unknown) => {
      if (disposed) return;
      status.textContent = message(error, 'Renderer metrics command failed.');
      status.hidden = false;
    }).finally(() => {
      if (!disposed) metricsToggle.disabled = false;
    });
  });
  debugButton.addEventListener('click', () => {
    if (disposed) return;
    if (debugPanel !== null) {
      debugPanel.dispose(); debugPanel = null; debugHost.replaceChildren(); debugHost.hidden = true;
      debugButton.textContent = 'Open live debug'; debugButton.setAttribute('aria-expanded', 'false');
      return;
    }
    debugButton.disabled = true; status.hidden = true; debugHost.hidden = false;
    void mountLiveDebugPanel(debugHost, { enabled: true, presentation: 'inline', transport }).then((mounted) => {
      if (disposed) { mounted.dispose(); return; }
      debugPanel = mounted; debugButton.disabled = false; debugButton.textContent = 'Close live debug';
      debugButton.setAttribute('aria-expanded', 'true');
    }).catch((error: unknown) => {
      if (disposed) return;
      debugButton.disabled = false; debugHost.replaceChildren(); debugHost.hidden = true;
      status.textContent = message(error, 'Live debug panel could not start.'); status.hidden = false;
    });
  });
  const courtyard = mountCourtyardControls(panel, transport);
  const ghost = mountGhostSettings(panel, transport);
  root.append(panel);
  return Object.freeze({ dispose: () => {
    disposed = true; courtyard.dispose(); ghost.dispose(); debugPanel?.dispose(); metrics.dispose(); panel.remove();
  } });
}

/** Sends compact C# courtyard controls without retaining product scene state in the DOM. */
function mountCourtyardControls(host: HTMLElement, transport: LiveDebugTransport): Readonly<{ dispose(): void }> {
  const toggle = button('Courtyard');
  toggle.setAttribute('aria-expanded', 'false');
  const controls = document.createElement('section');
  controls.id = 'craft-courtyard-controls';
  controls.hidden = true;
  controls.setAttribute('aria-label', 'Courtyard controls');
  controls.style.cssText = 'border-top:1px solid #415165;margin-top:.35rem;padding-top:.35rem;';
  toggle.setAttribute('aria-controls', controls.id);
  const actions = document.createElement('div');
  actions.style.cssText = 'display:flex;flex-wrap:wrap;gap:.3rem;';
  const balanced = button('Balanced');
  const faceted = button('Faceted');
  const soft = button('Soft');
  const refresh = button('Refresh');
  actions.append(balanced, faceted, soft, refresh);
  const receipt = document.createElement('p');
  receipt.setAttribute('aria-live', 'polite');
  receipt.style.cssText = 'margin:.35rem 0 0;max-width:24rem;overflow-wrap:anywhere;';
  controls.append(actions, receipt);
  host.append(toggle, controls);

  let disposed = false;
  const allActions = [balanced, faceted, soft, refresh];
  const execute = async (label: string, command: string, queued: boolean): Promise<void> => {
    if (disposed) return;
    for (const action of allActions) action.disabled = true;
    receipt.textContent = queued ? `Queueing ${label} treatment…` : 'Reading C# courtyard state…';
    try {
      const result = await transport.execute(command);
      if (!result.succeeded) throw new Error(result.message);
      receipt.textContent = queued
        ? `Queued ${label} treatment; it applies on the next product update. Receipt: ${result.message}`
        : `C# readout: ${result.message}`;
    } catch (error: unknown) {
      receipt.textContent = `Command failed: ${message(error, 'Courtyard command failed.')}`;
    } finally {
      if (!disposed) for (const action of allActions) action.disabled = false;
    }
  };
  toggle.addEventListener('click', () => {
    controls.hidden = !controls.hidden;
    toggle.setAttribute('aria-expanded', String(!controls.hidden));
  });
  balanced.addEventListener('click', () => void execute('Balanced', 'craft.courtyard.treatment balanced', true));
  faceted.addEventListener('click', () => void execute('Faceted', 'craft.courtyard.treatment faceted', true));
  soft.addEventListener('click', () => void execute('Soft', 'craft.courtyard.treatment soft', true));
  refresh.addEventListener('click', () => void execute('', 'craft.courtyard.readout', false));
  return Object.freeze({ dispose: () => { disposed = true; controls.remove(); toggle.remove(); } });
}

function mountGhostSettings(host: HTMLElement, transport: LiveDebugTransport): Readonly<{ dispose(): void }> {
  const toggle = button('Open Ghost Settings');
  toggle.setAttribute('aria-expanded', 'false');
  const settings = document.createElement('section');
  settings.id = 'craft-ghost-settings';
  settings.hidden = true;
  settings.setAttribute('role', 'dialog');
  settings.setAttribute('aria-label', 'Ghost Settings');
  settings.setAttribute('aria-modal', 'false');
  settings.style.cssText = 'background:rgb(15 19 25 / 96%);border:1px solid #62748a;border-radius:.4rem;color:#edf5ff;font:.78rem/1.35 ui-monospace,SFMono-Regular,Menlo,monospace;margin-top:.6rem;max-height:min(42rem,calc(100vh - 9rem));overflow:auto;padding:.7rem;width:min(38rem,calc(100vw - 2rem));';
  toggle.setAttribute('aria-controls', settings.id);
  const title = document.createElement('strong');
  title.textContent = 'Ghost Settings';
  const close = button('Close');
  const header = document.createElement('header');
  header.append(title, close);
  const notice = document.createElement('p');
  notice.setAttribute('aria-live', 'polite');
  notice.textContent = 'Open the panel to read the current C# settings.';
  settings.append(header, notice);
  const controls = createControls(settings);
  setControls(controls, false);
  const actions = document.createElement('div');
  const apply = button('Apply settings');
  const usePreset = button('Use preset');
  const reset = button('Reset to accepted');
  const recapture = button('Recapture');
  const refresh = button('Refresh readout');
  const useObserved = button('Use observed values');
  const views = document.createElement('span');
  for (const [label, angle] of [['Front', 0], ['Right', 90], ['Back', 180], ['Left', 270]] as const) {
    const view = button(label);
    view.addEventListener('click', () => void run('View ' + label, ['craft.ghost.view ' + String(angle)]));
    views.append(view);
  }
  actions.append(usePreset, apply, reset, recapture, refresh, useObserved, views);
  settings.append(actions);
  const viewHint = document.createElement('p');
  viewHint.textContent = 'View buttons move the C# player and aim its camera. WASD circles the plate and mouse look alone keeps the Engine-selected sector.';
  settings.append(viewHint);
  const observed = document.createElement('dl');
  observed.style.cssText = 'display:grid;gap:.2rem .6rem;grid-template-columns:max-content minmax(0,1fr);';
  const raw = document.createElement('pre');
  raw.hidden = true;
  raw.style.cssText = 'max-height:9rem;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere;';
  settings.append(observed, raw);
  host.append(toggle, settings);

  let active = false;
  let disposed = false;
  let working = false;
  let timer: ReturnType<typeof setInterval> | null = null;
  let request: AbortController | null = null;
  let current: Readout | null = null;
  let initialized = false;
  const controlsDisabled = (value: boolean): void => {
    working = value;
    const disabled = value || current === null;
    for (const control of [usePreset, apply, reset, recapture, useObserved, ...Array.from(views.querySelectorAll('button'))]) control.disabled = disabled;
    refresh.disabled = value;
    setControls(controls, !disabled);
  };
  const show = (readout: Readout, replaceDraft = false): void => {
    current = readout;
    renderObserved(observed, readout);
    raw.textContent = readout.raw;
    raw.hidden = false;
    if (!initialized || replaceDraft) { populate(controls, readout); initialized = true; }
    controlsDisabled(false);
  };
  const read = async (note: string, updateNotice: boolean): Promise<void> => {
    if (disposed || !active || working) return;
    request?.abort();
    const abort = new AbortController();
    request = abort;
    try {
      const result = await transport.execute('craft.ghost.readout', abort.signal);
      if (disposed || abort.signal.aborted) return;
      if (!result.succeeded) throw new Error(result.message);
      show(parseReadout(result));
      if (updateNotice) notice.textContent = note;
    } catch (error: unknown) {
      if (!disposed && !abort.signal.aborted && updateNotice) notice.textContent = message(error, 'Ghost readout failed.');
    } finally {
      if (request === abort) {
        request = null;
        if (!disposed) controlsDisabled(false);
      }
    }
  };
  const run = async (label: string, commands: readonly string[], replaceDraft = false): Promise<void> => {
    if (disposed || working || current === null) return;
    controlsDisabled(true); notice.textContent = label + '…';
    request?.abort();
    const abort = new AbortController();
    request = abort;
    try {
      for (const command of commands) {
        const result = await transport.execute(command, abort.signal);
        if (!result.succeeded) throw new Error(result.message);
      }
      if (disposed || abort.signal.aborted) return;
      const result = await transport.execute('craft.ghost.readout', abort.signal);
      if (!result.succeeded) throw new Error(result.message);
      show(parseReadout(result), replaceDraft);
      notice.textContent = label + ' queued. Observed C# state remains separate from this editable draft.';
    } catch (error: unknown) {
      if (!disposed && !abort.signal.aborted) {
        notice.textContent = message(error, label + ' failed.'); controlsDisabled(false);
      }
    } finally {
      if (request === abort) {
        request = null;
        if (!disposed) controlsDisabled(false);
      }
    }
  };
  const open = (): void => {
    active = true; settings.hidden = false; toggle.hidden = true; toggle.setAttribute('aria-expanded', 'true');
    notice.textContent = 'Reading current C# settings…';
    void read('Current C# Ghost Plate readout.', true);
    timer = setInterval(() => void read('Live C# Ghost Plate readout.', false), READOUT_INTERVAL_MS);
  };
  const hide = (): void => {
    active = false; request?.abort(); request = null;
    if (timer !== null) clearInterval(timer);
    timer = null; controlsDisabled(false); settings.hidden = true; toggle.hidden = false; toggle.setAttribute('aria-expanded', 'false');
  };
  toggle.addEventListener('click', open);
  close.addEventListener('click', hide);
  refresh.addEventListener('click', () => void read('Current C# Ghost Plate readout.', true));
  useObserved.addEventListener('click', () => {
    if (current === null) return;
    populate(controls, current); notice.textContent = 'Editable draft replaced with the observed C# settings.';
  });
  apply.addEventListener('click', () => {
    try { void run('Apply settings', commands(controls)); }
    catch (error: unknown) { notice.textContent = message(error, 'Settings are incomplete.'); }
  });
  usePreset.addEventListener('click', () => void run('Use preset', ['craft.ghost.preset ' + controls.preset.value], true));
  reset.addEventListener('click', () => void run('Reset to accepted preset', ['craft.ghost.preset accepted'], true));
  recapture.addEventListener('click', () => void run('Recapture', ['craft.ghost.recapture']));
  return Object.freeze({ dispose: () => { disposed = true; hide(); settings.remove(); toggle.remove(); } });
}

function createControls(host: HTMLElement): GhostControls {
  const form = document.createElement('div');
  form.style.cssText = 'display:grid;gap:.45rem;grid-template-columns:repeat(2,minmax(0,1fr));';
  host.append(form);
  const preset = select(form, 'Preset', PRESETS);
  const visible = checkbox(form, 'Visible');
  const sectors = select(form, 'Sectors', SECTORS);
  const hysteresis = number(form, 'Hysteresis');
  const depth = number(form, 'Depth retention');
  const anchor = select(form, 'Anchor', ANCHORS);
  const anchorValue = number(form, 'Anchor value');
  const mapping = select(form, 'Mapping', MAPPINGS);
  const shell = select(form, 'Shell', SHELLS);
  const epsilon = number(form, 'Shell epsilon');
  const resolution = number(form, 'Capture resolution', '1');
  const azimuth = number(form, 'Capture azimuth');
  const elevation = number(form, 'Capture elevation');
  const near = number(form, 'Near clip');
  const far = number(form, 'Far clip');
  const fov = number(form, 'Field of view');
  const lighting = select(form, 'Lighting', LIGHTING);
  const ambient = number(form, 'Ambient');
  const key = number(form, 'Key');
  const fill = number(form, 'Fill');
  const x = number(form, 'Position X');
  const y = number(form, 'Position Y');
  const z = number(form, 'Position Z');
  const width = number(form, 'Plate width');
  const height = number(form, 'Plate height');
  return Object.freeze({ preset, visible, sectors, hysteresis, depth, anchor, anchorValue, mapping, shell, epsilon, resolution, azimuth, elevation, near, far, fov, lighting, ambient, key, fill, x, y, z, width, height });
}

function commands(c: GhostControls): readonly string[] {
  const n = (field: HTMLInputElement): string => numberValue(field);
  return [
    'craft.ghost.visible ' + String(c.visible.checked),
    'craft.ghost.direction ' + integer(c.sectors.value, 'Sectors') + ' ' + n(c.hysteresis),
    'craft.ghost.relief ' + n(c.depth) + ' ' + c.anchor.value + ' ' + n(c.anchorValue) + ' ' + c.mapping.value + ' ' + c.shell.value + ' ' + n(c.epsilon),
    'craft.ghost.capture ' + integer(n(c.resolution), 'Capture resolution') + ' ' + n(c.azimuth) + ' ' + n(c.elevation) + ' ' + n(c.near) + ' ' + n(c.far) + ' ' + n(c.fov) + ' ' + c.lighting.value,
    'craft.ghost.lighting ' + c.lighting.value + ' ' + n(c.ambient) + ' ' + n(c.key) + ' ' + n(c.fill),
    'craft.ghost.place ' + n(c.x) + ' ' + n(c.y) + ' ' + n(c.z) + ' ' + n(c.width) + ' ' + n(c.height),
  ];
}

function parseReadout(result: LiveDebugResult): Readout {
  const fields = new Map<string, string>();
  for (const part of result.message.split(';')) {
    const separator = part.indexOf('=');
    if (separator > 0) fields.set(part.slice(0, separator), part.slice(separator + 1));
  }
  if (!fields.has('preset') || !fields.has('resolution')) throw new Error('Ghost readout is missing C# settings.');
  return { fields, raw: result.message };
}

function populate(c: GhostControls, readout: Readout): void {
  const f = readout.fields;
  pick(c.preset, f.get('preset')); c.visible.checked = f.get('visible') === 'True'; pick(c.sectors, f.get('sectors'));
  set(c.hysteresis, f.get('hysteresis')); set(c.depth, f.get('depth'));
  let [kind, value] = split(f.get('anchor')); pick(c.anchor, kind); set(c.anchorValue, value);
  pick(c.mapping, f.get('mapping')); [kind, value] = split(f.get('shell')); pick(c.shell, kind); set(c.epsilon, value);
  set(c.resolution, f.get('resolution')); set(c.azimuth, f.get('azimuth')); set(c.elevation, f.get('elevation'));
  set(c.near, f.get('near')); set(c.far, f.get('far')); set(c.fov, f.get('fov'));
  [kind, value] = split(f.get('lighting')); pick(c.lighting, kind);
  const [ambient, key, fill] = (value ?? '').split('/'); set(c.ambient, ambient); set(c.key, key); set(c.fill, fill);
  const [x, y, z] = (f.get('placement') ?? '').split(','); set(c.x, x); set(c.y, y); set(c.z, z);
  const [width, height] = (f.get('size') ?? '').split('x'); set(c.width, width); set(c.height, height);
}

function renderObserved(host: HTMLElement, readout: Readout): void {
  const f = readout.fields;
  const [retainedSectors, retainedMeshes, retainedMaterials] = (f.get('retained') ?? '').split('/');
  const rows: readonly [string, string][] = [
    ['Configured', 'visible ' + (f.get('visible') ?? 'unknown') + ' · applied ' + (f.get('appliedVisible') ?? 'unknown') + ' · pending ' + (f.get('pending') ?? 'unknown')],
    ['Configured sectors', f.get('sectors') ?? 'unknown'], ['Current sector', f.get('sector') ?? 'unknown'], ['Local azimuth', f.get('offset') ?? 'unknown'],
    ['Source match', f.get('sourceMatch') ?? 'unknown'], ['Renderer observed', f.get('observed') ?? 'unknown'],
    ['Fallback', f.get('fallback') ?? 'unknown'], ['Retained sectors', retainedSectors || 'unknown'],
    ['Retained meshes', retainedMeshes || 'unknown'], ['Retained materials', retainedMaterials || 'unknown'],
  ];
  host.replaceChildren(...rows.flatMap(([label, value]) => {
    const term = document.createElement('dt'); term.textContent = label;
    const description = document.createElement('dd'); description.textContent = value; description.style.margin = '0';
    return [term, description];
  }));
}

function isolateEvents(panel: HTMLElement): void {
  const stop = (event: Event): void => event.stopPropagation();
  for (const type of ['pointerdown', 'pointermove', 'pointerup', 'pointercancel', 'mousedown', 'mousemove', 'mouseup', 'wheel', 'keydown', 'keyup', 'click']) panel.addEventListener(type, stop);
}
function select(host: HTMLElement, label: string, values: readonly string[]): HTMLSelectElement {
  const field = document.createElement('label'); field.textContent = label; field.style.cssText = 'display:grid;gap:.15rem;min-width:0;';
  const control = document.createElement('select');
  control.append(...values.map((value) => { const option = document.createElement('option'); option.value = value; option.textContent = value; return option; }));
  field.append(control); host.append(field); return control;
}
function checkbox(host: HTMLElement, label: string): HTMLInputElement {
  const field = document.createElement('label'); const control = document.createElement('input'); control.type = 'checkbox';
  field.append(control, document.createTextNode(' ' + label)); host.append(field); return control;
}
function number(host: HTMLElement, label: string, step = 'any'): HTMLInputElement {
  const field = document.createElement('label'); field.textContent = label; field.style.cssText = 'display:grid;gap:.15rem;min-width:0;';
  const control = document.createElement('input'); control.type = 'number'; control.style.cssText = 'box-sizing:border-box;width:100%;min-width:0;'; control.step = step; control.inputMode = 'decimal';
  field.append(control); host.append(field); return control;
}
function button(text: string): HTMLButtonElement { const value = document.createElement('button'); value.type = 'button'; value.textContent = text; return value; }
function setControls(c: GhostControls, enabled: boolean): void { for (const control of Object.values(c)) control.disabled = !enabled; }
function split(value: string | undefined): readonly [string | undefined, string | undefined] {
  if (value === undefined) return [undefined, undefined]; const index = value.indexOf(':');
  return index < 0 ? [value, undefined] : [value.slice(0, index), value.slice(index + 1)];
}
function pick(control: HTMLSelectElement, value: string | undefined): void {
  if (value !== undefined && Array.from(control.options).some((option) => option.value === value)) control.value = value;
}
function set(control: HTMLInputElement, value: string | undefined): void { control.value = value ?? ''; }
function numberValue(field: HTMLInputElement): string {
  const value = field.value.trim();
  if (value.length === 0 || !Number.isFinite(Number(value))) throw new Error((field.parentElement?.textContent ?? 'Value') + ' must be a finite number.');
  return value;
}
function integer(value: string, label: string): string { if (!/^\d+$/u.test(value)) throw new Error(label + ' must be a whole number.'); return value; }
function message(error: unknown, fallback: string): string { return error instanceof Error && error.message.length > 0 ? error.message : fallback; }
