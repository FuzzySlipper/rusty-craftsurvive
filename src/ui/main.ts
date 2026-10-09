import type { RustyApplicationUiContext } from '@rusty-engine/product-ui';
import { mountWorld } from './world.js';
import { mountActions } from './actions.js';
import { mountDeveloperTools } from './developer.js';
import { element, isolateEvents } from './dom.js';
import { mountHud } from './hud.js';
import { mountLoading } from './loading.js';
import { mountOverlay } from './overlay.js';
import { mountOptions } from './options.js';
import { mountScreens } from './screens.js';

const CONTROLS_HELP = 'WASD and mouse to move and look; Space jumps, Shift sprints, Ctrl crouches; E takes hold of a wall to climb '
  + '(and lets go); J attacks; G places and F removes a building piece (Q steps the piece, Z its material; T switches to the terrain brush, where F clears, G places and B changes the brush); 1-9 or the wheel select a hotbar slot and R uses it; '
  + 'I opens the pack, M the journal of places, Esc closes them. '
  + 'Controller: left stick moves, right stick looks, A jumps, B crouches, Y climbs, RT clears, LT places, LB and RB step the hotbar, D-pad up uses.';

/**
 * The DOM companion. It shows what the product publishes through the projection and claims the
 * product's action intent; C# owns every game fact and decides what each request does. The game's
 * HUD sits over the view, with the pack and journal screens opened over it; the menu - every
 * published fact, the building actions and the tools - is a drawer, closed until the player opens it.
 */
export function mountProductUi(root: Element, context: RustyApplicationUiContext): Readonly<{ dispose(): void }> {
  const gameUi = element('div');
  root.append(gameUi);
  const panel = element('aside', 'background:rgb(15 19 25 / 88%);border:1px solid #62748a;border-radius:.35rem;color:#edf5ff;'
    + 'font:.76rem/1.25 ui-monospace,SFMono-Regular,Menlo,monospace;max-height:calc(100vh - 1rem);max-width:min(26rem,calc(100vw - 1rem));'
    + 'overflow:auto;padding:.35rem .45rem;pointer-events:auto;position:relative;width:fit-content;z-index:1;');
  panel.setAttribute('aria-label', 'Rusty CraftSurvive');
  panel.setAttribute('data-rusty-ui-interactive', '');
  isolateEvents(panel);

  const drawer = element('details', '');
  drawer.append(element('summary', 'cursor:pointer;font-weight:700;', 'Menu'));
  const contents = element('div', '');
  drawer.append(contents);
  panel.append(drawer);
  const status = element('p', 'margin:.3rem 0 0;');
  status.hidden = true;
  status.setAttribute('role', 'alert');
  panel.append(status);

  const unsubscribe = mountHud(contents, context.projection);
  const unsubscribeActions = mountActions(contents, context.intents, context.projection);
  contents.append(element('p', 'margin:.35rem 0 0;opacity:.75;', CONTROLS_HELP));
  const disposeOptions = mountOptions(contents, context.intents, context.projection, status);
  const disposeDeveloperTools = mountDeveloperTools(contents, status);
  gameUi.append(panel);
  const disposeOverlay = mountOverlay(gameUi, context.projection);
  const disposeLoading = mountLoading(gameUi, context.projection);
  const disposeScreens = mountScreens(gameUi, context.intents, context.projection);

  const disposeWorld = mountWorld(root, gameUi, context.ui, context.intents, context.projection);

  return Object.freeze({
    dispose: () => {
      unsubscribe();
      unsubscribeActions();
      disposeOptions();
      disposeOverlay();
      disposeLoading();
      disposeScreens();
      disposeDeveloperTools();
      disposeWorld();
      gameUi.remove();
    },
  });
}
