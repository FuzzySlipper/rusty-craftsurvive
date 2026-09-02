import { mountLiveDebugPanel, type LiveDebugPanelMount } from './live-debug-panel.js';

/**
 * Mounts static DOM guidance beside the Engine-owned canvas. This UI owns no
 * world facts, gameplay input, renderer resources, or application loop.
 */
export function mountProductUi(root: Element): Readonly<{ dispose(): void }> {
  const panel = document.createElement('aside');
  panel.setAttribute('aria-label', 'Rusty CraftSurvive status');

  const title = document.createElement('h1');
  title.textContent = 'Rusty CraftSurvive';
  panel.append(title);

  const status = document.createElement('p');
  status.textContent = 'C# product host active. Terrain and first-person gameplay run in the product lane.';
  panel.append(status);

  const debugButton = document.createElement('button');
  debugButton.type = 'button';
  debugButton.textContent = 'Open live debug';
  debugButton.setAttribute('aria-expanded', 'false');
  panel.append(debugButton);

  const debugHost = document.createElement('div');
  debugHost.hidden = true;
  panel.append(debugHost);

  let debugPanel: LiveDebugPanelMount | null = null;
  let disposed = false;
  debugButton.addEventListener('click', () => {
    if (disposed) return;
    if (debugPanel !== null) {
      debugPanel.dispose();
      debugPanel = null;
      debugHost.replaceChildren();
      debugHost.hidden = true;
      debugButton.textContent = 'Open live debug';
      debugButton.setAttribute('aria-expanded', 'false');
      return;
    }

    debugButton.disabled = true;
    debugHost.hidden = false;
    void mountLiveDebugPanel(debugHost, { enabled: true, presentation: 'inline' }).then((mounted) => {
      if (disposed) {
        mounted.dispose();
        return;
      }
      debugPanel = mounted;
      debugButton.textContent = 'Close live debug';
      debugButton.setAttribute('aria-expanded', 'true');
      debugButton.disabled = false;
    }).catch((error: unknown) => {
      if (disposed) return;
      debugButton.disabled = false;
      debugHost.replaceChildren();
      debugHost.hidden = true;
      status.textContent = error instanceof Error ? error.message : 'Live debug panel could not start.';
    });
  });

  root.append(panel);
  return Object.freeze({
    dispose: () => {
      disposed = true;
      debugPanel?.dispose();
      debugPanel = null;
      debugHost.replaceChildren();
      debugHost.hidden = true;
      panel.remove();
    },
  });
}
