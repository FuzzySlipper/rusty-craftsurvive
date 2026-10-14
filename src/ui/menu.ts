import type { RustyApplicationUiIntentsPort, RustyApplicationUiPort, RustyApplicationUiProjectionView } from '@rusty-engine/product-ui';
import { button, element, isolateEvents } from './dom.js';
import { number, projectionValues, text, type Values } from './hud.js';
import { layers } from './layers.js';
import { mountOptions } from './options.js';
import { WORLD_CONTRACT, WORLD_INTENT } from './world.js';

/** Esc opens the menu, steps back out of its pages and closes it. */
const MENU_KEY = 'Escape';
/** The world actions that hold the game and let it run again; the journey's own "pause" is apart (#9916 review). */
const MENU_PAUSE = 'menu-pause';
const MENU_RESUME = 'menu-resume';

/** The new world's choices, as the product offers them (WorldSelection's NewWorldSizes). */
const WORLD_SIZES = [[4096, '4 km'], [8192, '8 km'], [16384, '16 km'], [390000, '390 km continent (experimental)']] as const;
const DEFAULT_SEED = '12345';
const DEFAULT_SIZE = 8192;

const LOAD_LATER = 'Loading another world comes with save slots; the active world saves as you play.';
const QUIT_HERE = 'Close the browser tab to quit; the world saves as you play.';

const PANEL = 'pointer-events:auto;background:rgb(20 24 27 / 96%);color:#eee7d5;padding:1.1rem 1.2rem;border:1px solid #84775c;border-radius:.4rem;'
  + 'box-shadow:0 .6rem 2rem rgb(0 0 0 / 55%);max-height:86vh;overflow:auto;box-sizing:border-box;';
const ENTRY = 'display:block;width:100%;margin:.4rem 0;padding:.6rem .8rem;font:600 1rem/1.2 system-ui,sans-serif;text-align:center;cursor:pointer;';
/** An entry that is not offered here: still readable, so it shows where it will be. */
const UNOFFERED = 'background:transparent;color:#a99f88;border:1px dashed #84775c;border-radius:.2rem;cursor:not-allowed;';

type Page = 'main' | 'world' | 'options';

/**
 * The pause menu (#9916): a centred column - Continue, New world, Load, Options, Quit - over the held
 * game. Whether the game is paused is the product's fact (`menuPaused`); Esc, or losing the pointer
 * while playing, claims a pause, and Continue claims the game back. The menu shows only what the
 * product publishes and remembers only which of its pages is open.
 */
export function mountMenu(host: Element, ui: RustyApplicationUiPort, intents: RustyApplicationUiIntentsPort | undefined,
  projection: RustyApplicationUiProjectionView | undefined): () => void {
  const claim = (data: Readonly<Record<string, string | number>>): void => {
    intents?.claim(WORLD_INTENT, { kind: 'product-payload', contract: WORLD_CONTRACT, data });
  };

  // The backdrop lets clicks through, so the drawer and developer tools stay usable while paused.
  const overlay = element('div', 'position:fixed;inset:0;z-index:20;display:none;align-items:center;justify-content:center;'
    + 'background:rgb(6 8 10 / 45%);pointer-events:none;font:15px/1.45 system-ui,sans-serif;');
  const panel = element('section', PANEL + 'width:min(19rem,calc(100vw - 2rem));');
  panel.setAttribute('role', 'dialog');
  panel.setAttribute('aria-label', 'Paused');
  panel.setAttribute('data-rusty-ui-interactive', '');
  isolateEvents(panel);
  overlay.append(panel);
  host.append(overlay);

  const heading = element('h2', 'margin:0 0 .6rem;text-align:center;font:700 1.25rem/1.2 system-ui,sans-serif;letter-spacing:.04em;', 'Paused');
  const status = element('p', 'margin:.6rem 0 0;font-size:.85rem;color:#e0b070;');
  status.setAttribute('role', 'status');
  status.hidden = true;

  // The main page.
  const main = element('nav', '');
  const entry = (label: string, action: () => void): HTMLButtonElement => {
    const control = button(label);
    control.style.cssText = ENTRY;
    control.addEventListener('click', action);
    main.append(control);
    return control;
  };
  entry('Continue', () => claim({ action: MENU_RESUME }));
  entry('New world', () => show('world'));
  const load = entry('Load', () => {});
  load.disabled = true; load.title = LOAD_LATER; load.style.cssText = ENTRY + UNOFFERED;
  load.append(element('small', 'display:block;font-weight:400;font-size:.72rem;opacity:.85;margin-top:.15rem;', 'Coming with save slots'));
  entry('Options', () => show('options'));
  const quit = entry('Quit', () => claim({ action: 'quit' }));

  const back = (): HTMLButtonElement => {
    const control = button('Back');
    control.style.cssText = ENTRY;
    control.addEventListener('click', () => show('main'));
    return control;
  };

  // A new world: the product checks the seed and size, then generates it on the map.
  const world = element('form', '');
  const seedLabel = element('label', 'display:block;', 'World seed');
  const seed = element('input', 'box-sizing:border-box;width:100%;margin:.3rem 0 .7rem;');
  seed.name = 'seed'; seed.inputMode = 'numeric'; seed.pattern = '[0-9]+'; seed.required = true; seed.maxLength = 20;
  seed.value = DEFAULT_SEED; seedLabel.append(seed);
  const sizeLabel = element('label', 'display:block;', 'World width');
  const size = element('select', 'margin:.3rem 0 .7rem;width:100%;');
  size.name = 'size';
  for (const [value, label] of WORLD_SIZES) {
    const option = element('option', '', label); option.value = String(value); size.append(option);
  }
  size.value = String(DEFAULT_SIZE); sizeLabel.append(size);
  const create = button('Create new world'); create.type = 'submit'; create.style.cssText = ENTRY;
  world.append(seedLabel, sizeLabel, element('p', 'font-size:.85rem;opacity:.75;margin:0;', 'Starts a fresh expedition and replaces your active world.'), create, back());
  world.addEventListener('submit', (event) => { event.preventDefault(); claim({ action: 'create', seed: seed.value, size: Number(size.value) }); });

  // The options: the Engine's video options with the game's own.
  const settings = element('div', '');
  const options = mountOptions(settings, intents, projection, status);
  const settingsBack = back();
  const optionsPage = element('div', '');
  optionsPage.append(settings, settingsBack);

  panel.append(heading, main, world, optionsPage, status);

  let page: Page = 'main';
  let paused = false;
  let message = '';
  const show = (next: Page): void => {
    page = next;
    main.hidden = page !== 'main';
    world.hidden = page !== 'world';
    optionsPage.hidden = page !== 'options';
    heading.textContent = page === 'world' ? 'New world' : page === 'options' ? 'Options' : 'Paused';
    panel.style.width = page === 'options' ? 'min(30rem,calc(100vw - 2rem))' : 'min(19rem,calc(100vw - 2rem))';
    status.hidden = true;
    if (page === 'options') options.show();
    if (page === 'world') seed.focus();
  };
  show('main');

  const freeScene = (values: Values | null): boolean => values !== null && (number(values, 'worldMapOpen') === 1 || number(values, 'townOpen') === 1);

  const render = (values: Values | null): void => {
    if (values === null) return;
    const now = number(values, 'menuPaused') === 1;
    const canQuit = number(values, 'menuQuit') === 1;
    quit.disabled = !canQuit; quit.title = canQuit ? '' : QUIT_HERE; quit.style.cssText = ENTRY + (canQuit ? '' : UNOFFERED);
    // A refused new world or quit says why on the menu.
    const said = text(values, 'worldMessage') ?? '';
    if (now && said !== message && page === 'world') { status.textContent = said; status.hidden = said.length === 0; }
    message = said;
    if (now === paused) return;
    paused = now;
    layers.menu = paused;
    overlay.style.display = paused ? 'flex' : 'none';
    if (paused) {
      show('main');
      // The pointer is the player's while the game is held, and a click on the view does not take it back.
      ui.setCursorMode('unlocked');
    } else if (!freeScene(values)) {
      ui.setCursorMode('pointer-lock');
      ui.focusGameplay();
    }
  };

  const onKey = (event: KeyboardEvent): void => {
    // The pack or journal takes its own Esc first.
    if (event.code !== MENU_KEY || event.repeat || event.defaultPrevented || layers.screen) return;
    event.preventDefault();
    if (!paused) claim({ action: MENU_PAUSE });
    else if (page !== 'main') show('main');
    else claim({ action: MENU_RESUME });
  };

  // A browser takes the Esc that frees a held pointer for itself, so losing the pointer while
  // playing - not to the pack, journal or map, which free it on purpose - is what opens the menu.
  let locked = document.pointerLockElement !== null;
  const onPointerLock = (): void => {
    const now = document.pointerLockElement !== null;
    const lost = locked && !now;
    locked = now;
    if (!lost || paused || layers.screen || freeScene(projectionValues(projection?.current() ?? null))) return;
    claim({ action: MENU_PAUSE });
  };

  window.addEventListener('keydown', onKey, true);
  document.addEventListener('pointerlockchange', onPointerLock);
  render(projectionValues(projection?.current() ?? null));
  const unsubscribe = projection?.subscribe((envelope) => render(projectionValues(envelope)));
  return () => {
    unsubscribe?.();
    window.removeEventListener('keydown', onKey, true);
    document.removeEventListener('pointerlockchange', onPointerLock);
    options.dispose();
    layers.menu = false;
    overlay.remove();
  };
}
