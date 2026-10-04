import type { RustyApplicationUiIntentsPort, RustyApplicationUiProjectionView } from '@rusty-engine/product-ui';
import { ACTION_CONTRACT, ACTION_INTENT } from './actions.js';
import { button, element, isolateEvents } from './dom.js';
import { number, projectionValues, text, type Values } from './hud.js';

/** The keys that open each screen; Escape closes whichever is open. */
const PACK_KEY = 'KeyI';
const JOURNAL_KEY = 'KeyM';
const CLOSE_KEY = 'Escape';

/** The journal map's size in pixels, and the farthest place it will fit, in metres. */
const MAP_PIXELS = 300;
const MAP_REACH_METRES = 800;
const MAP_MARGIN = 14;

/** How far the player moves before the journal's distances are worked out again, in metres. */
const JOURNAL_STEP_METRES = 5;

/** The compass, clockwise from north; north is -Z in the world, east +X. */
const COMPASS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'] as const;

const INK = '#f2ead8';
const PAPER = 'rgb(18 20 26 / 94%)';
const READY = '#8fb34a';
const SHORT = '#c8423a';
const FONT = 'font:.8rem/1.3 ui-monospace,SFMono-Regular,Menlo,monospace;';

/** How a place kind is marked on the map. */
const MARKS: Readonly<Record<string, string>> = {
  'Standing stones': '#d8b84a',
  Ruin: '#c98a5a',
  'Cave mouth': '#9aa6b8',
  'Dungeon entrance': '#c8423a',
  'Vantage point': '#5aa6d8',
};

interface Item { readonly id: string; readonly name: string; readonly count: number; readonly use: string }
interface Recipe { readonly id: string; readonly makes: string; readonly count: number; readonly inputs: readonly { name: string; count: number }[]; readonly ready: boolean }
interface Place { readonly name: string; readonly visited: boolean; readonly x: number; readonly z: number }

/** A published list: entries separated by ';', fields by '|'. */
const entries = (values: Values, key: string): string[][] =>
  (text(values, key) ?? '').split(';').filter((entry) => entry.length > 0).map((entry) => entry.split('|'));

const items = (values: Values): Item[] => entries(values, 'packItems')
  .map(([id = '', name = '', count = '0', use = 'material']) => ({ id, name, count: Number(count), use }));

const recipes = (values: Values): Recipe[] => entries(values, 'recipeBook')
  .map(([id = '', makes = '', count = '1', inputs = '', ready = '0']) => ({
    id, makes, count: Number(count), ready: ready === '1',
    inputs: inputs.split('+').filter((input) => input.length > 0).map((input) => {
      const [name = '', needed = '0'] = input.split('*');
      return { name, count: Number(needed) };
    }),
  }));

const places = (values: Values): Place[] => entries(values, 'journalPlaces')
  .map(([name = '', stage = '', x = '0', z = '0']) => ({ name, visited: stage === 'visited', x: Number(x), z: Number(z) }));

/** The compass point from one place toward another, north being -Z. */
const bearing = (dx: number, dz: number): string => {
  const degrees = (Math.atan2(dx, -dz) * 180 / Math.PI + 360) % 360;
  return COMPASS[Math.round(degrees / 45) % COMPASS.length]!;
};

/**
 * The game's two screens over the view: the pack - what is carried, what can be eaten or applied,
 * and what can be made from it - and the journal of places found, as a map around the player and a
 * list by distance. Each shows only what the product publishes; eating, applying and crafting are
 * claims on the product's action intent, as the menu's are. The UI remembers only which screen is
 * open.
 */
export function mountScreens(root: Element, intents: RustyApplicationUiIntentsPort | undefined,
  projection: RustyApplicationUiProjectionView | undefined): () => void {
  const claim = (data: Readonly<Record<string, string | number>>): void => {
    intents?.claim(ACTION_INTENT, { kind: 'product-payload', contract: ACTION_CONTRACT, data });
  };

  const tabs = element('div', 'position:fixed;top:.5rem;right:.5rem;display:flex;gap:.3rem;z-index:3;pointer-events:auto;');
  tabs.setAttribute('data-rusty-ui-interactive', '');
  isolateEvents(tabs);
  const packTab = button('Pack (I)');
  const journalTab = button('Journal (M)');
  tabs.append(packTab, journalTab);

  const screen = element('section', `position:fixed;left:50%;top:50%;transform:translate(-50%,-50%);z-index:4;display:none;
    width:min(46rem,calc(100vw - 2rem));max-height:calc(100vh - 4rem);overflow:auto;padding:.8rem 1rem;pointer-events:auto;
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
  root.append(tabs, screen);

  let open: 'pack' | 'journal' | null = null;
  let latest: Values | null = null;
  let drawn = '';

  /** What the open screen shows, so it is rebuilt only when that changes and not on every update. */
  const signature = (values: Values | null): string => {
    if (values === null) return 'waiting';
    if (open === 'pack') return ['packItems', 'recipeBook', 'packLoad', 'lastInventory'].map((key) => String(values[key])).join('/');
    const step = (key: string): number => Math.round((number(values, key) ?? 0) / JOURNAL_STEP_METRES);
    return `${text(values, 'journalPlaces')}/${step('playerX')}/${step('playerZ')}`;
  };

  const draw = (force = false): void => {
    screen.style.display = open === null ? 'none' : 'block';
    if (open === null) return;
    const now = `${open}:${signature(latest)}`;
    if (!force && now === drawn) return;
    drawn = now;
    title.textContent = open === 'pack' ? 'Pack' : 'Journal';
    body.replaceChildren(latest === null
      ? element('p', 'opacity:.75;', 'Waiting for the game…')
      : open === 'pack' ? packView(latest) : journalView(latest));
  };

  const show = (which: 'pack' | 'journal' | null): void => {
    open = open === which ? null : which;
    // The pointer is the player's again while a screen is open, so its buttons can be pressed.
    if (open !== null && document.pointerLockElement !== null) document.exitPointerLock();
    draw(true);
  };

  const packView = (values: Values): HTMLElement => {
    const view = element('div', 'display:grid;gap:.8rem;');
    const load = number(values, 'packLoad') ?? 0;
    const limit = number(values, 'packLimit') ?? 0;
    const meter = element('div', 'display:flex;align-items:center;gap:.5rem;');
    const bar = element('div', 'flex:1;height:.6rem;background:#2a2d36;border:2px solid #000;');
    bar.append(element('div', `height:100%;width:${limit > 0 ? Math.min(100, load / limit * 100) : 0}%;background:#d8b84a;`));
    meter.append(element('span', '', 'Carrying'), bar, element('span', '', `${load} / ${limit}`));

    const held = items(values);
    const grid = element('div', 'display:grid;grid-template-columns:repeat(auto-fill,minmax(9rem,1fr));gap:.4rem;');
    if (held.length === 0) grid.append(element('p', 'opacity:.75;margin:0;', 'Nothing carried yet. Creatures drop supplies; places hold caches.'));
    for (const item of held) {
      const tile = element('div', 'padding:.4rem;background:#0b0c10;border:2px solid #000;display:grid;gap:.25rem;');
      tile.append(element('strong', '', `${item.name} ×${item.count}`), element('span', 'opacity:.7;', item.use));
      if (item.use === 'food' || item.use === 'healing') {
        const use = button(item.use === 'food' ? 'Eat' : 'Apply');
        use.disabled = intents === undefined;
        use.addEventListener('click', () => claim({ action: 'use', item: item.id }));
        tile.append(use);
      }
      grid.append(tile);
    }

    const book = element('div', 'display:grid;gap:.3rem;');
    book.append(element('h3', 'margin:0;font-size:.85rem;text-transform:uppercase;letter-spacing:.06em;', 'Make'));
    const counts = new Map(held.map((item) => [item.name, item.count]));
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

    const last = text(values, 'lastInventory');
    view.append(meter, grid, book);
    if (last !== null && last !== '' && last !== 'none') view.append(element('p', 'margin:0;opacity:.8;', last));
    return view;
  };

  const journalView = (values: Values): HTMLElement => {
    const view = element('div', 'display:flex;flex-wrap:wrap;gap:1rem;align-items:flex-start;');
    const known = places(values);
    const px = number(values, 'playerX') ?? 0;
    const pz = number(values, 'playerZ') ?? 0;
    const found = number(values, 'discoveryPlaces') ?? known.length;
    const visited = number(values, 'discoveryVisited') ?? 0;

    // The map: north up, the player in the middle, scaled to the farthest place shown.
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('width', String(MAP_PIXELS));
    svg.setAttribute('height', String(MAP_PIXELS));
    svg.setAttribute('viewBox', `0 0 ${MAP_PIXELS} ${MAP_PIXELS}`);
    svg.style.cssText = 'background:#0b0c10;border:2px solid #000;flex:none;';
    const shape = (tag: string, attributes: Readonly<Record<string, string | number>>): SVGElement => {
      const node = document.createElementNS('http://www.w3.org/2000/svg', tag);
      for (const [name, value] of Object.entries(attributes)) node.setAttribute(name, String(value));
      svg.append(node);
      return node;
    };
    const centre = MAP_PIXELS / 2;
    const reach = Math.min(MAP_REACH_METRES, Math.max(50, ...known.map((place) => Math.hypot(place.x - px, place.z - pz))));
    const scale = (centre - MAP_MARGIN) / reach;
    shape('circle', { cx: centre, cy: centre, r: centre - MAP_MARGIN, fill: 'none', stroke: '#2a2d36' });
    shape('circle', { cx: centre, cy: centre, r: (centre - MAP_MARGIN) / 2, fill: 'none', stroke: '#2a2d36' });
    shape('text', { x: centre, y: 11, fill: INK, 'font-size': 10, 'text-anchor': 'middle' }).textContent = 'N';
    shape('text', { x: MAP_PIXELS - 6, y: MAP_PIXELS - 6, fill: INK, opacity: 0.6, 'font-size': 9, 'text-anchor': 'end' }).textContent = `${Math.round(reach)} m`;
    for (const place of known) {
      const dx = place.x - px;
      const dz = place.z - pz;
      if (Math.hypot(dx, dz) > reach) continue;
      const colour = MARKS[place.name] ?? INK;
      const mark = shape('rect', { x: centre + dx * scale - 4, y: centre + dz * scale - 4, width: 8, height: 8,
        fill: place.visited ? colour : 'none', stroke: colour, 'stroke-width': 2 });
      mark.append(Object.assign(document.createElementNS('http://www.w3.org/2000/svg', 'title'), { textContent: place.name }));
    }
    shape('circle', { cx: centre, cy: centre, r: 4, fill: INK, stroke: '#000' });

    const list = element('div', 'flex:1;min-width:16rem;display:grid;gap:.25rem;');
    list.append(element('p', 'margin:0 0 .3rem;', `${found} places found, ${visited} visited. Filled marks are places you have stood at.`));
    const byDistance = [...known].sort((a, b) => Math.hypot(a.x - px, a.z - pz) - Math.hypot(b.x - px, b.z - pz));
    if (byDistance.length === 0) list.append(element('p', 'opacity:.75;margin:0;', 'Nothing found yet. Places are noticed as you come near them.'));
    for (const place of byDistance) {
      const dx = place.x - px;
      const dz = place.z - pz;
      const row = element('div', 'display:flex;gap:.5rem;align-items:center;padding:.2rem .4rem;background:#0b0c10;border:2px solid #000;');
      row.append(
        element('span', `width:.6rem;height:.6rem;flex:none;border:2px solid ${MARKS[place.name] ?? INK};background:${place.visited ? MARKS[place.name] ?? INK : 'transparent'};`),
        element('span', 'flex:1;', place.name),
        element('span', 'opacity:.7;', place.visited ? 'visited' : 'seen'),
        element('span', 'min-width:5.5rem;text-align:right;', `${Math.round(Math.hypot(dx, dz))} m ${bearing(dx, dz)}`));
      list.append(row);
    }
    view.append(svg, list);
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
  close.addEventListener('click', () => show(null));
  window.addEventListener('keydown', onKey, true);

  const unsubscribe = projection?.subscribe((envelope) => {
    latest = projectionValues(envelope);
    if (open !== null) draw();
  });
  latest = projection === undefined ? null : projectionValues(projection.current());

  return () => {
    unsubscribe?.();
    window.removeEventListener('keydown', onKey, true);
    tabs.remove();
    screen.remove();
  };
}
