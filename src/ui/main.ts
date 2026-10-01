import type { RustyApplicationUiContext } from '@rusty-engine/product-ui';
import { mountActions } from './actions.js';
import { mountDeveloperTools } from './developer.js';
import { element, isolateEvents } from './dom.js';
import { mountHud } from './hud.js';
import { mountLoading } from './loading.js';

const CONTROLS_HELP = 'WASD and mouse to move and look; J attacks; F clears and G places terrain. '
  + 'Controller: left stick moves, right stick looks, A jumps, B crouches, RT clears, LT places.';

/**
 * The DOM companion. It shows what the product publishes through the projection and claims the
 * product's action intent; C# owns every game fact and decides what each request does.
 */
export function mountProductUi(root: Element, context: RustyApplicationUiContext): Readonly<{ dispose(): void }> {
  const panel = element('aside', 'background:rgb(15 19 25 / 88%);border:1px solid #62748a;border-radius:.35rem;color:#edf5ff;'
    + 'font:.76rem/1.25 ui-monospace,SFMono-Regular,Menlo,monospace;max-height:calc(100vh - 1rem);max-width:min(26rem,calc(100vw - 1rem));'
    + 'overflow:auto;padding:.35rem .45rem;pointer-events:auto;position:relative;width:fit-content;z-index:1;');
  panel.setAttribute('aria-label', 'Rusty CraftSurvive');
  panel.setAttribute('data-rusty-ui-interactive', '');
  isolateEvents(panel);

  panel.append(element('strong', '', 'Rusty CraftSurvive'));
  const status = element('p', 'margin:.3rem 0 0;');
  status.hidden = true;
  status.setAttribute('role', 'alert');
  panel.append(status);

  const unsubscribe = mountHud(panel, context.projection);
  const unsubscribeActions = mountActions(panel, context.intents, context.projection);
  panel.append(element('p', 'margin:.35rem 0 0;opacity:.75;', CONTROLS_HELP));
  const disposeDeveloperTools = mountDeveloperTools(panel, status);
  root.append(panel);
  const disposeLoading = mountLoading(root, context.projection);

  return Object.freeze({
    dispose: () => {
      unsubscribe();
      unsubscribeActions();
      disposeLoading();
      disposeDeveloperTools();
      panel.remove();
    },
  });
}
