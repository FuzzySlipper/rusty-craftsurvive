import type { RustyApplicationUiContext } from '@rusty-engine/product-ui';
import { mountWorld } from './world.js';
import { mountCastingRing } from './radial.js';
import { mountDeveloperTools } from './developer.js';
import { element } from './dom.js';
import { mountInteraction } from './interaction.js';
import { mountLoading } from './loading.js';
import { mountOverlay } from './overlay.js';
import { mountMenu } from './menu.js';
import { mountScreens } from './screens.js';
import { mountTown } from './town.js';

const CONTROLS_HELP = 'WASD and mouse to move and look; Space jumps, Shift sprints, Ctrl crouches; E takes hold of a wall to climb '
  + '(and lets go); J attacks; F digs; T starts building, where G places and F removes a piece (Q steps the piece, Z its material), T again switches to the terrain brush (G places, F clears, B changes the brush) and T a third time stops building; 1-9 or the wheel select a hotbar slot and R uses it; '
  + 'V opens the casting ring (outer trigram, then inner) and C casts what it composed where you aim (the Casting panel); I opens the pack, M the journal of places; Esc closes whichever surface is open (pack, journal, ring, town, these tools), and otherwise pauses the game under the menu (Continue, New world, Options, Developer tools, Quit). ` opens these tools. '
  + 'Controller: left stick moves, right stick looks, A jumps, B crouches, Y climbs, RT clears, LT places, D-pad down steps building as T does, LB and RB step the hotbar, D-pad up uses, Start pauses.';

/**
 * The DOM companion. It shows what the product publishes through the projection and claims the
 * product's action intent; C# owns every game fact and decides what each request does. Game UI - the
 * HUD over the view, the pack and journal screens, the casting ring, the town, the world map and the
 * pause menu over everything - is apart from the diagnostic UI, the developer tools docked beside play
 * (`developer.ts`). One owner (`interaction.ts`) decides interface or gameplay from the open surfaces.
 */
export function mountProductUi(root: Element, context: RustyApplicationUiContext): Readonly<{ dispose(): void }> {
  // First, so Escape closes the tools before any game surface sees it.
  const developer = mountDeveloperTools(root, context, CONTROLS_HELP);
  const gameUi = element('div');
  root.append(gameUi);
  const disposeOverlay = mountOverlay(gameUi, context.projection);
  const disposeLoading = mountLoading(gameUi, context.projection);
  const disposeScreens = mountScreens(gameUi, context.intents, context.projection);
  const disposeTown = mountTown(root, context.ui, context.intents, context.projection);
  const disposeRing = mountCastingRing(root, context.intents, context.projection);
  // After the screens, town and ring, so their own Escape takes precedence.
  const menu = mountMenu(root, context.input, context.intents, context.projection, () => { if (!developer.isOpen()) void developer.toggle(); });
  const disposeWorld = mountWorld(root, gameUi, context.ui, context.intents, context.projection);
  // Escape in play reaches the page only as the pointer lock ending; that is the player asking for the menu.
  const interaction = mountInteraction(context.ui, { onLockLostInPlay: () => menu.pause() });

  return Object.freeze({
    dispose: () => {
      interaction.dispose();
      disposeTown();
      disposeRing();
      menu.dispose();
      disposeOverlay();
      disposeLoading();
      disposeScreens();
      developer.dispose();
      disposeWorld();
      gameUi.remove();
    },
  });
}
