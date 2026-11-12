import type { RustyApplicationUiContext } from '@rusty-engine/product-ui';
import { createLiveDebugHttpTransport, mountDeveloperShell, type DeveloperShellMount, type DeveloperShellPanel } from '@rusty-engine/live-debug';
import { mountActions } from './actions.js';
import { mountCasting } from './casting.js';
import { button, element, isolateEvents } from './dom.js';
import { mountHud } from './hud.js';
import { layers, open as openLayer } from './layers.js';

/** The key that opens and closes the developer tools; Escape closes them too. */
export const DEVELOPER_KEY = 'Backquote';
const CLOSE_KEY = 'Escape';

/** How the developer tools mark themselves, so nobody reads them as part of the game. */
const DIAGNOSTIC = 'background:rgb(28 12 34 / 92%);border:1px dashed #c86bd8;color:#f0e2f4;'
  + 'font:.76rem/1.3 ui-monospace,SFMono-Regular,Menlo,monospace;';
const BADGE = 'Diagnostic UI · not part of the game';
const HOST_CLASS = 'craft-developer';

/**
 * Developer tools: the Engine's developer shell docked beside play (rusty-dagger's model, den-wiki `wiki/rusty-dagger/game-ui`
 * "Diagnostic UI"). It holds the product's own diagnostic panels - every published fact with the building
 * and study shortcuts, and the development magic compiler (#10529) - and, when the host runs with
 * --live-debug, the Engine's console, scene, view and metrics. It lives outside every game surface, is
 * visibly marked, holds the interface while open and gives play its pointer back when closed; it carries
 * no game state. ` opens it in play, as does the menu's Developer tools.
 */
export interface DeveloperTools {
  isOpen(): boolean;
  /** Opens the shell, or closes it; resolves once it has decided. */
  toggle(): Promise<void>;
  dispose(): void;
}

export function mountDeveloperTools(root: Element, context: RustyApplicationUiContext, controlsHelp: string): DeveloperTools {
  const transport = createLiveDebugHttpTransport();
  // The shell docks inside this layer, over every game surface; only the shell itself takes the pointer.
  const host = element('div', 'position:fixed;inset:0;pointer-events:none;z-index:40;');
  host.className = HOST_CLASS;
  const sheet = element('style', '', `.${HOST_CLASS} > * { pointer-events: auto; }`);
  host.append(sheet);
  root.append(host);

  // The one way in from the view: a small marked button where the old Details drawer was.
  const opener = button('Dev tools (`)');
  opener.style.cssText = DIAGNOSTIC + 'position:fixed;top:.5rem;left:.5rem;z-index:8;padding:.2rem .45rem;border-radius:.25rem;cursor:pointer;pointer-events:auto;opacity:.8;';
  opener.title = BADGE;
  opener.setAttribute('data-rusty-ui-interactive', '');
  root.append(opener);

  let shell: DeveloperShellMount | null = null;
  let deciding: Promise<void> | null = null;

  /** One of the product's panels: marked diagnostic, kept off the canvas's input. */
  const productPanel = (title: string, fill: (element: HTMLElement) => () => void): DeveloperShellPanel => ({
    kind: 'product',
    title,
    mount: (target) => {
      target.style.cssText = DIAGNOSTIC + 'padding:.45rem .55rem;overflow:auto;';
      isolateEvents(target);
      target.append(element('p', 'margin:0 0 .3rem;color:#e7a6f2;font-weight:700;letter-spacing:.04em;text-transform:uppercase;font-size:.68rem;', BADGE));
      const dispose = fill(target);
      return { dispose: () => { dispose(); target.replaceChildren(); } };
    },
  });

  const mountShell = (liveDebug: string | null): DeveloperShellMount => {
    const panels: DeveloperShellPanel[] = [
      productPanel('Craft', (target) => {
        if (liveDebug !== null) target.append(element('p', 'margin:0 0 .3rem;opacity:.8;', `The Engine's console, scene, view and metrics need the host started with --live-debug (${liveDebug}).`));
        const disposeFacts = mountHud(target, context.projection);
        const disposeActions = mountActions(target, context.intents, context.projection);
        target.append(element('p', 'margin:.35rem 0 0;opacity:.75;', controlsHelp));
        return () => { disposeFacts(); disposeActions(); };
      }),
      productPanel('Casting', (target) => mountCasting(target, context.intents, context.projection)),
    ];
    if (liveDebug === null) {
      panels.push({ kind: 'console' }, { kind: 'scene' },
        { kind: 'view', title: 'View', options: { viewport: context.viewport, transport } }, { kind: 'metrics' });
    }
    return mountDeveloperShell(host, {
      panels,
      presentation: 'tabs',
      side: 'right',
      transport,
      // However the shell closes, play gets its pointer back.
      onClose: () => openLayer('developer', false),
    });
  };

  const toggle = async (): Promise<void> => {
    if (shell?.isOpen) { shell.close(); return; }
    if (deciding !== null) return deciding;
    deciding = (async () => {
      if (shell === null) {
        // Without the host's live-debug there is nothing for the Engine panels to talk to; the product's
        // own panels work either way.
        const refusal = await transport.catalog()
          .then((catalog) => catalog.available ? null : 'the host serves no live-debug catalog')
          .catch((error: unknown) => error instanceof Error ? error.message : String(error));
        shell = mountShell(refusal);
      }
      openLayer('developer', true);
      await shell.open();
    })().finally(() => { deciding = null; });
    return deciding;
  };

  const typing = (): boolean => document.activeElement instanceof HTMLInputElement || document.activeElement instanceof HTMLTextAreaElement;
  const onKey = (event: KeyboardEvent): void => {
    if (event.repeat) return;
    if (event.code === DEVELOPER_KEY && !typing()) {
      event.preventDefault();
      void toggle();
    } else if (event.code === CLOSE_KEY && layers.developer && !event.defaultPrevented) {
      // The innermost surface: Escape closes the tools and nothing under them.
      event.preventDefault();
      event.stopImmediatePropagation();
      shell?.close();
    }
  };
  opener.addEventListener('click', () => void toggle());
  window.addEventListener('keydown', onKey, true);

  return {
    isOpen: () => shell?.isOpen === true,
    toggle,
    dispose() {
      window.removeEventListener('keydown', onKey, true);
      shell?.dispose();
      shell = null;
      openLayer('developer', false);
      opener.remove();
      host.remove();
    },
  };
}
