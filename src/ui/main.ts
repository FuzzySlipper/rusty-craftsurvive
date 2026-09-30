import {
  createLiveDebugHttpTransport,
  mountLiveDebugPanel,
  mountRendererMetricsWidget,
  type LiveDebugPanelMount,
} from '@rusty-engine/live-debug';

/** Mounts the DOM-only status panel. C# remains the owner of every game setting. */
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
  const controlsHelp = document.createElement('p');
  controlsHelp.textContent = 'Controls: WASD and mouse; controller left stick moves and right stick looks. A jumps, B crouches, left-stick click sprints, X impulses, RT clears terrain, and LT sets terrain.';
  controlsHelp.style.cssText = 'margin:.3rem 0 0;max-width:24rem;';
  panel.append(controlsHelp);

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
  root.append(panel);
  return Object.freeze({ dispose: () => {
    disposed = true; debugPanel?.dispose(); metrics.dispose(); panel.remove();
  } });
}

function isolateEvents(panel: HTMLElement): void {
  const stop = (event: Event): void => event.stopPropagation();
  for (const type of ['pointerdown', 'pointermove', 'pointerup', 'pointercancel', 'mousedown', 'mousemove', 'mouseup', 'wheel', 'keydown', 'keyup', 'click']) panel.addEventListener(type, stop);
}
function button(text: string): HTMLButtonElement { const value = document.createElement('button'); value.type = 'button'; value.textContent = text; return value; }
function message(error: unknown, fallback: string): string { return error instanceof Error && error.message.length > 0 ? error.message : fallback; }
