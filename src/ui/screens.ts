import type { RustyApplicationUiIntentsPort, RustyApplicationUiProjectionView } from '@rusty-engine/product-ui';
import { ACTION_CONTRACT, ACTION_INTENT } from './actions.js';
import { button, element, isolateEvents } from './dom.js';
import { number, projectionValues, text, type Values } from './hud.js';
import { drawMap, mark, places, standing } from './map.js';
import { EQUIPMENT, hotbarSlots, packSlots, SLOT_PIXELS, SlotDrag, slotItems } from './slots.js';

/** The keys that open each screen; Escape closes whichever is open. */
const PACK_KEY = 'KeyI';
const JOURNAL_KEY = 'KeyM';
const CLOSE_KEY = 'Escape';

/** The journal's map: its size in pixels, and the farthest place it will fit, in metres. */
const MAP_PIXELS = 300;
const MAP_REACH_METRES = 800;

/** The minimap on the HUD: its size, and how far its rim is. */
const MINIMAP_PIXELS = 150;
const MINIMAP_REACH_METRES = 200;
const MAP_MARGIN = 14;

/** How far the player moves, and how far they turn, before a map is drawn again. */
const MAP_STEP_METRES = 2;
const MAP_STEP_DEGREES = 6;

/** How far the player moves before the journal's distances are worked out again, in metres. */
const JOURNAL_STEP_METRES = 5;

/** The pack's slots in a row, as the hotbar has them. */
const ROW_SLOTS = 9;

/** How long the screen's own refusal shows, in milliseconds. */
const REFUSAL_MS = 2400;

/** The compass, clockwise from north; north is -Z in the world, east +X. */
const COMPASS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'] as const;

const INK = '#f2ead8';
const PAPER = 'rgb(18 20 26 / 94%)';
const READY = '#8fb34a';
const SHORT = '#c8423a';
const FONT = 'font:.8rem/1.3 ui-monospace,SFMono-Regular,Menlo,monospace;';

interface Recipe { readonly id: string; readonly makes: string; readonly count: number; readonly inputs: readonly { name: string; count: number }[]; readonly ready: boolean }

const recipes = (values: Values): Recipe[] => (text(values, 'recipeBook') ?? '').split(';').filter((entry) => entry.length > 0)
  .map((entry) => {
    const [id = '', makes = '', count = '1', inputs = '', ready = '0'] = entry.split('|');
    return {
      id, makes, count: Number(count), ready: ready === '1',
      inputs: inputs.split('+').filter((input) => input.length > 0).map((input) => {
        const [name = '', needed = '0'] = input.split('*');
        return { name, count: Number(needed) };
      }),
    };
  });

/** The compass point from one place toward another, north being -Z. */
const bearing = (dx: number, dz: number): string => {
  const degrees = (Math.atan2(dx, -dz) * 180 / Math.PI + 360) % 360;
  return COMPASS[Math.round(degrees / 45) % COMPASS.length]!;
};

const svg = (): SVGSVGElement => document.createElementNS('http://www.w3.org/2000/svg', 'svg');

/**
 * What the player works with over the view: the hotbar's slots at the foot of the screen, a minimap
 * of the journal's places in the corner, and the two screens - the pack (the equipment, pack and
 * hotbar slots, and the recipe book) and the journal (a map and a list of places found). Stacks are
 * dragged between slots and food eaten from them; each is a claim on the product's action intent,
 * and the product decides and publishes the slots again. The UI remembers only which screen is open.
 */
export function mountScreens(root: Element, intents: RustyApplicationUiIntentsPort | undefined,
  projection: RustyApplicationUiProjectionView | undefined): () => void {
  const claim = (data: Readonly<Record<string, string | number>>): void => {
    intents?.claim(ACTION_INTENT, { kind: 'product-payload', contract: ACTION_CONTRACT, data });
  };

  const refusal = element('div', `position:fixed;left:50%;bottom:6.5rem;transform:translateX(-50%);z-index:11;display:none;padding:.25rem .55rem;
    background:#0b0c10cc;border:2px solid #000;color:${INK};${FONT}pointer-events:none;`);
  let refusalTimer = 0;
  const drag = new SlotDrag(root, {
    move: (from, to, count) => claim(count > 0 ? { action: 'move', from, to, count } : { action: 'move', from, to }),
    use: (item) => claim({ action: 'use', item: item.id, slot: item.slot }),
    refuse: (why) => {
      refusal.textContent = why;
      refusal.style.display = 'block';
      window.clearTimeout(refusalTimer);
      refusalTimer = window.setTimeout(() => { refusal.style.display = 'none'; }, REFUSAL_MS);
    },
  });

  // The corner: the minimap, which opens the journal, and the buttons for both screens.
  const corner = element('div', 'position:fixed;top:.5rem;right:.5rem;z-index:3;display:grid;gap:.3rem;justify-items:end;pointer-events:auto;');
  corner.setAttribute('data-rusty-ui-interactive', '');
  isolateEvents(corner);
  const minimap = svg();
  minimap.style.cssText = 'display:block;background:rgb(11 12 16 / 80%);border:2px solid #000;border-radius:50%;cursor:pointer;';
  minimap.setAttribute('aria-label', 'Map: open the journal');
  const tabs = element('div', 'display:flex;gap:.3rem;');
  const packTab = button('Pack (I)');
  const journalTab = button('Journal (M)');
  tabs.append(packTab, journalTab);
  corner.append(minimap, tabs);

  // The hotbar: its own slots, not shortcuts to the pack's.
  const hotbar = element('div', 'position:fixed;left:50%;bottom:.6rem;transform:translateX(-50%);z-index:3;display:flex;gap:2px;padding:3px;'
    + 'background:rgb(11 12 16 / 75%);border:2px solid #000;pointer-events:auto;');
  hotbar.setAttribute('data-rusty-ui-interactive', '');
  isolateEvents(hotbar);

  const screen = element('section', `position:fixed;left:50%;top:50%;transform:translate(-50%,-50%);z-index:4;display:none;
    width:min(46rem,calc(100vw - 2rem));max-height:calc(100vh - 2rem);overflow:auto;padding:.8rem 1rem;pointer-events:auto;
    background:${PAPER};border:2px solid #000;box-shadow:0 0 0 1px #62748a;color:${INK};${FONT}`);
  screen.setAttribute('data-rusty-ui-interactive', '');
  screen.setAttribute('role', 'dialog');
  isolateEvents(screen);
  const heading = element('div', 'display:flex;justify-content:space-between;align-items:center;margin-bottom:.6rem;');
  const title = element('h2', 'margin:0;font-size:1rem;letter-spacing:.08em;text-transform:uppercase;');
  const close = button('Close (Esc)');
  heading.append(title, close);
  const body = element('div', '');
  screen.append(heading, body);
  root.append(corner, hotbar, screen, refusal);

  let open: 'pack' | 'journal' | null = null;
  let latest: Values | null = null;
  const drawn = { screen: '', hotbar: '', minimap: '' };

  /** What the open screen shows, so it is rebuilt only when that changes and not on every update. */
  const screenSignature = (values: Values | null): string => {
    if (values === null) return 'waiting';
    if (open === 'pack') return ['packItems', 'recipeBook', 'packLoad', 'lastInventory'].map((key) => String(values[key])).join('/');
    const step = (key: string): number => Math.round((number(values, key) ?? 0) / JOURNAL_STEP_METRES);
    return `${text(values, 'journalPlaces')}/${step('playerX')}/${step('playerZ')}`;
  };

  const draw = (force = false): void => {
    if (latest !== null) {
      // The hotbar shows on the HUD except while the pack, which has its own row of it, is open.
      hotbar.style.display = open === 'pack' ? 'none' : 'flex';
      const hotbarNow = String(latest['packItems']);
      if (hotbarNow !== drawn.hotbar) {
        drawn.hotbar = hotbarNow;
        const held = slotItems(latest);
        hotbar.replaceChildren(...Array.from({ length: hotbarSlots(latest) }, (_, slot) => drag.slot(slot, held.get(slot))));
      }
      const me = standing(latest);
      const minimapNow = `${text(latest, 'journalPlaces')}/${Math.round(me.x / MAP_STEP_METRES)}/${Math.round(me.z / MAP_STEP_METRES)}/${Math.round(me.yaw / MAP_STEP_DEGREES)}`;
      if (minimapNow !== drawn.minimap) {
        drawn.minimap = minimapNow;
        drawMap(minimap, latest, MINIMAP_PIXELS, { reach: MINIMAP_REACH_METRES, maximumReach: MINIMAP_REACH_METRES, margin: MAP_MARGIN, labels: false });
      }
    }

    screen.style.display = open === null ? 'none' : 'block';
    if (open === null) return;
    const now = `${open}:${screenSignature(latest)}`;
    if (!force && now === drawn.screen) return;
    drawn.screen = now;
    title.textContent = open === 'pack' ? 'Pack' : 'Journal';
    body.replaceChildren(latest === null
      ? element('p', 'opacity:.75;', 'Waiting for the game…')
      : open === 'pack' ? packView(latest) : journalView(latest));
  };

  const show = (which: 'pack' | 'journal' | null): void => {
    open = open === which ? null : which;
    // The pointer is the player's again while a screen is open, so its slots and buttons can be used.
    if (open !== null && document.pointerLockElement !== null) document.exitPointerLock();
    draw(true);
  };

  const grid = (columns: number, cells: HTMLElement[]): HTMLElement => {
    const box = element('div', `display:grid;grid-template-columns:repeat(${columns},${SLOT_PIXELS}px);gap:2px;`);
    box.append(...cells);
    return box;
  };

  const packView = (values: Values): HTMLElement => {
    const view = element('div', 'display:grid;gap:.7rem;');
    const held = slotItems(values);
    const first = hotbarSlots(values);
    const load = number(values, 'packLoad') ?? 0;
    const limit = number(values, 'packLimit') ?? 0;

    const meter = element('div', 'display:flex;align-items:center;gap:.5rem;');
    const bar = element('div', 'flex:1;height:.6rem;background:#2a2d36;border:2px solid #000;');
    bar.append(element('div', `height:100%;width:${limit > 0 ? Math.min(100, load / limit * 100) : 0}%;background:#d8b84a;`));
    meter.append(element('span', '', 'Carrying'), bar, element('span', '', `${load} / ${limit}`));

    // Equipment beside the pack, as placeholders until there is equipment to wear.
    const top = element('div', 'display:flex;gap:1rem;align-items:flex-start;flex-wrap:wrap;');
    const worn = element('div', 'display:grid;gap:.3rem;');
    worn.append(element('span', 'opacity:.75;', 'Equipment'), grid(2, EQUIPMENT.map((name) => drag.equipment(name))));
    const pack = element('div', 'display:grid;gap:.3rem;');
    pack.append(element('span', 'opacity:.75;', 'Pack'),
      grid(ROW_SLOTS, Array.from({ length: packSlots(values) }, (_, index) => drag.slot(first + index, held.get(first + index)))),
      element('span', 'opacity:.75;margin-top:.3rem;', 'Hotbar'),
      grid(ROW_SLOTS, Array.from({ length: first }, (_, slot) => drag.slot(slot, held.get(slot)))));
    top.append(worn, pack);

    const book = element('div', 'display:grid;gap:.3rem;');
    book.append(element('h3', 'margin:0;font-size:.85rem;text-transform:uppercase;letter-spacing:.06em;', 'Make'));
    const counts = new Map<string, number>();
    for (const item of held.values()) counts.set(item.name, (counts.get(item.name) ?? 0) + item.count);
    for (const recipe of recipes(values)) {
      const row = element('div', 'display:flex;align-items:center;gap:.6rem;padding:.3rem .4rem;background:#0b0c10;border:2px solid #000;');
      row.append(element('strong', 'min-width:7rem;', `${recipe.makes}${recipe.count > 1 ? ` ×${recipe.count}` : ''}`));
      const needs = element('span', 'flex:1;');
      recipe.inputs.forEach((input, index) => {
        const have = counts.get(input.name) ?? 0;
        if (index > 0) needs.append(' + ');
        needs.append(element('span', `color:${have >= input.count ? READY : SHORT};`, `${input.name} ${have}/${input.count}`));
      });
      const make = button('Craft');
      make.disabled = intents === undefined || !recipe.ready;
      make.addEventListener('click', () => claim({ action: 'craft', recipe: recipe.id }));
      row.append(needs, make);
      book.append(row);
    }

    const hint = element('p', 'margin:0;opacity:.7;', 'Drag a stack to move it; Shift or right-drag for half. Double-click food to eat it, a bandage to apply it.');
    const last = text(values, 'lastInventory');
    view.append(meter, top, hint, book);
    if (last !== null && last !== '' && last !== 'none') view.append(element('p', 'margin:0;opacity:.8;', last));
    return view;
  };

  const journalView = (values: Values): HTMLElement => {
    const view = element('div', 'display:flex;flex-wrap:wrap;gap:1rem;align-items:flex-start;');
    const known = places(values);
    const me = standing(values);
    const found = number(values, 'discoveryPlaces') ?? known.length;
    const visited = number(values, 'discoveryVisited') ?? 0;
    const map = svg();
    map.style.cssText = 'background:#0b0c10;border:2px solid #000;flex:none;';
    drawMap(map, values, MAP_PIXELS, { maximumReach: MAP_REACH_METRES, margin: MAP_MARGIN, labels: true });

    const list = element('div', 'flex:1;min-width:16rem;display:grid;gap:.25rem;');
    list.append(element('p', 'margin:0 0 .3rem;', `${found} places found, ${visited} visited. Filled marks are places you have stood at.`));
    const distance = (x: number, z: number): number => Math.hypot(x - me.x, z - me.z);
    const byDistance = [...known].sort((a, b) => distance(a.x, a.z) - distance(b.x, b.z));
    if (byDistance.length === 0) list.append(element('p', 'opacity:.75;margin:0;', 'Nothing found yet. Places are noticed as you come near them.'));
    for (const place of byDistance) {
      const row = element('div', 'display:flex;gap:.5rem;align-items:center;padding:.2rem .4rem;background:#0b0c10;border:2px solid #000;');
      row.append(
        element('span', `width:.6rem;height:.6rem;flex:none;border:2px solid ${mark(place)};background:${place.visited ? mark(place) : 'transparent'};`),
        element('span', 'flex:1;', place.name),
        element('span', 'opacity:.7;', place.visited ? 'visited' : 'seen'),
        element('span', 'min-width:5.5rem;text-align:right;', `${Math.round(distance(place.x, place.z))} m ${bearing(place.x - me.x, place.z - me.z)}`));
      list.append(row);
    }
    view.append(map, list);
    return view;
  };

  const onKey = (event: KeyboardEvent): void => {
    if (event.repeat) return;
    if (event.code === PACK_KEY) show('pack');
    else if (event.code === JOURNAL_KEY) show('journal');
    else if (event.code === CLOSE_KEY && open !== null) show(null);
  };
  packTab.addEventListener('click', () => show('pack'));
  journalTab.addEventListener('click', () => show('journal'));
  minimap.addEventListener('click', () => show('journal'));
  close.addEventListener('click', () => show(null));
  window.addEventListener('keydown', onKey, true);

  const unsubscribe = projection?.subscribe((envelope) => {
    latest = projectionValues(envelope);
    draw();
  });
  latest = projection === undefined ? null : projectionValues(projection.current());
  draw();

  return () => {
    unsubscribe?.();
    window.removeEventListener('keydown', onKey, true);
    window.clearTimeout(refusalTimer);
    drag.dispose();
    corner.remove();
    hotbar.remove();
    screen.remove();
    refusal.remove();
  };
}
