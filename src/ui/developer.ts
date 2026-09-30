import {
  createLiveDebugHttpTransport,
  mountLiveDebugPanel,
  mountRendererMetricsWidget,
  type LiveDebugPanelMount,
  type RendererMetricsWidgetMount,
} from '@rusty-engine/live-debug';
import { button, element, message } from './dom.js';

/**
 * Developer tools over the live-debug channel: renderer metrics and the command panel. They work
 * only when the host was started with --live-debug, and nothing here is mounted, polled or asked
 * for until the player opens it.
 */
export function mountDeveloperTools(host: HTMLElement, status: HTMLElement): () => void {
  const details = element('details', 'margin-top:.35rem;');
  const summary = element('summary', 'cursor:pointer;opacity:.75;', 'Developer tools (needs --live-debug)');
  const metricsButton = button('Show metrics');
  const debugButton = button('Open live debug');
  const metricsHost = element('div', 'margin-top:.3rem;max-width:min(24rem,calc(100vw - 2rem));overflow:auto;');
  const debugHost = element('div');
  metricsHost.hidden = true;
  debugHost.hidden = true;
  details.append(summary, metricsButton, debugButton, metricsHost, debugHost);
  host.append(details);

  let transport: ReturnType<typeof createLiveDebugHttpTransport> | null = null;
  let metrics: RendererMetricsWidgetMount | null = null;
  let debugPanel: LiveDebugPanelMount | null = null;
  let disposed = false;
  const channel = (): ReturnType<typeof createLiveDebugHttpTransport> => (transport ??= createLiveDebugHttpTransport());
  const report = (error: unknown, fallback: string): void => {
    if (disposed) return;
    status.textContent = message(error, fallback);
    status.hidden = false;
  };

  metricsButton.addEventListener('click', () => {
    const show = metrics === null;
    metricsButton.disabled = true;
    status.hidden = true;
    void channel().execute(show ? 'engine.renderer.show' : 'engine.renderer.hide').then((result) => {
      if (disposed) return;
      if (!result.succeeded) { report(new Error(result.message), 'Renderer metrics command failed.'); return; }
      if (show) {
        metrics = mountRendererMetricsWidget(metricsHost, { initiallyVisible: true, transport: channel() });
      } else {
        metrics?.dispose();
        metrics = null;
        metricsHost.replaceChildren();
      }
      metricsHost.hidden = !show;
      metricsButton.textContent = show ? 'Hide metrics' : 'Show metrics';
    }).catch((error: unknown) => report(error, 'Renderer metrics command failed.'))
      .finally(() => { if (!disposed) metricsButton.disabled = false; });
  });

  debugButton.addEventListener('click', () => {
    if (debugPanel !== null) {
      debugPanel.dispose();
      debugPanel = null;
      debugHost.replaceChildren();
      debugHost.hidden = true;
      debugButton.textContent = 'Open live debug';
      return;
    }

    debugButton.disabled = true;
    status.hidden = true;
    debugHost.hidden = false;
    void mountLiveDebugPanel(debugHost, { enabled: true, presentation: 'inline', transport: channel() }).then((mounted) => {
      if (disposed) { mounted.dispose(); return; }
      debugPanel = mounted;
      debugButton.textContent = 'Close live debug';
    }).catch((error: unknown) => {
      debugHost.replaceChildren();
      debugHost.hidden = true;
      report(error, 'Live debug panel could not start.');
    }).finally(() => { if (!disposed) debugButton.disabled = false; });
  });

  return () => {
    disposed = true;
    debugPanel?.dispose();
    metrics?.dispose();
  };
}
