import type { RustyApplicationUiProjectionView } from '@rusty-engine/product-ui';
import { element } from './dom.js';
import { number, projectionValues, text, type Values } from './hud.js';

/** How many blocks a vital's bar is drawn in: chunky, so it reads at a glance. */
const SEGMENTS = 10;

/** How long a toast stays up, and how long the hit flash takes to fade, in milliseconds. */
const TOAST_MS = 3200;
const FLASH_MS = 420;

/** The most toasts shown at once; older ones give way. */
const MAXIMUM_TOASTS = 3;

/** The longest a toast's text runs before it is cut. */
const TOAST_CHARACTERS = 72;

/** Health at or below this share of its maximum tints the screen's edges. */
const LOW_HEALTH_SHARE = 0.3;

const FONT = 'font:700 .78rem/1.1 ui-monospace,SFMono-Regular,Menlo,monospace;letter-spacing:.06em;text-transform:uppercase;';
const SHADOW = 'text-shadow:1px 1px 0 #000,-1px 1px 0 #000,1px -1px 0 #000,-1px -1px 0 #000;';

/** One vital: its label, colour, and how full it is from the projection, or null while it is hidden. */
interface Vital {
  readonly label: string;
  readonly colour: string;
  readonly read: (values: Values) => { share: number; note: string } | null;
}

const share = (value: number | null, maximum: number | null): number | null =>
  value === null || maximum === null || maximum <= 0 ? null : Math.max(0, Math.min(1, value / maximum));

const VITALS: readonly Vital[] = [
  {
    label: 'Health', colour: '#c8423a',
    read: (v) => {
      const s = share(number(v, 'health'), number(v, 'maximumHealth'));
      return s === null ? null : { share: s, note: `${number(v, 'health')}` };
    },
  },
  {
    label: 'Stamina', colour: '#d8b84a',
    read: (v) => {
      const s = share(number(v, 'stamina'), number(v, 'maximumStamina'));
      return s === null || (s >= 1 && number(v, 'climbing') !== 1) ? null : { share: s, note: number(v, 'climbing') === 1 ? 'climbing' : '' };
    },
  },
  {
    label: 'Food', colour: '#8fb34a',
    read: (v) => {
      const f = number(v, 'satiety');
      return f === null ? null : { share: Math.max(0, Math.min(1, f / 100)), note: f <= 0 ? 'starving' : f < 25 ? 'hungry' : '' };
    },
  },
  {
    // The weather over the player (#9741): the bar is how wet they are; the note names the weather and says whether they are under cover.
    // Weather that wounds warns in the expedition's reminders instead, where there is room.
    label: 'Weather', colour: '#7fa9c9',
    read: (v) => {
      const wet = number(v, 'wetness');
      const sky = text(v, 'weather') ?? '';
      if (wet === null || (wet <= 0 && sky === '')) return null;
      // Short, beside the hotbar: the weather's name and whether the player is under cover; the bar shows how wet.
      const cover = sky !== '' && number(v, 'sheltered') === 1 ? ' · sheltered' : '';
      return { share: Math.max(0, Math.min(1, wet / 100)), note: `${sky === '' ? 'drying' : sky}${cover}` };
    },
  },
  {
    label: 'Cold', colour: '#a8c8e8',
    read: (v) => {
      const chill = number(v, 'chill');
      return chill === null || chill <= 0 ? null : { share: Math.max(0, Math.min(1, chill / 100)), note: chill >= 60 ? 'freezing' : 'chilled' };
    },
  },
  {
    label: 'Air', colour: '#5aa6d8',
    read: (v) => {
      const s = share(number(v, 'breath'), number(v, 'maximumBreath'));
      return s === null || (s >= 1 && number(v, 'submerged') !== 1) ? null : { share: s, note: s <= 0 ? 'drowning' : '' };
    },
  },
];

/** Where a toast comes from: a fact the product publishes as text, and how it reads on screen. */
const TOASTS: readonly { readonly key: string; readonly show: (value: string) => string | null }[] = [
  { key: 'discoveryLastFound', show: (value) => (value === '' ? null : `Found ${value}`) },
  { key: 'lastInventory', show: (value) => (value === '' || value === 'none' ? null : value) },
  { key: 'lastAction', show: (value) => (value === '' || value === 'none' ? null : value) },
];

/**
 * The game's own HUD over the view: a crosshair, the vitals as segmented bars, the prompt the
 * product gives for where the player stands, short-lived notices when something is found or done,
 * and a red flash when the player is hurt. It draws only what the projection publishes; what it
 * remembers is what it last drew, to know when to flash or show a notice.
 */
export function mountOverlay(root: Element, projection: RustyApplicationUiProjectionView | undefined): () => void {
  const layer = element('div', 'position:fixed;inset:0;pointer-events:none;z-index:2;color:#f2ead8;' + FONT + SHADOW);

  const crosshair = element('div', 'position:absolute;left:50%;top:50%;width:14px;height:14px;transform:translate(-50%,-50%);');
  for (const css of ['left:6px;top:0;width:2px;height:14px;', 'left:0;top:6px;width:14px;height:2px;']) {
    crosshair.append(element('div', `position:absolute;${css}background:#f2ead8;box-shadow:0 0 0 1px #000;`));
  }

  // The expedition reminder stacks above the vitals, however many vital rows are shown.
  const corner = element('div', 'position:absolute;left:1rem;bottom:1rem;display:flex;flex-direction:column;align-items:flex-start;gap:.4rem;');
  const vitals = element('div', 'display:grid;grid-template-columns:auto auto auto;gap:.3rem .5rem;align-items:center;');
  const rows = VITALS.map((vital) => {
    const label = element('span', 'opacity:.9;', vital.label);
    const bar = element('div', 'display:flex;gap:2px;padding:2px;background:#0b0c10cc;border:2px solid #000;');
    const blocks = Array.from({ length: SEGMENTS }, () => {
      const block = element('div', 'width:12px;height:10px;background:#2a2d36;');
      bar.append(block);
      return block;
    });
    const note = element('span', 'opacity:.85;');
    vitals.append(label, bar, note);
    // Each part keeps its own display while shown: the bar's is the flex row of its blocks.
    return { vital, parts: [label, bar, note].map((part) => ({ part, shown: part.style.display })), blocks, note };
  });

  const prompt = element('div', 'position:absolute;left:50%;bottom:18%;transform:translateX(-50%);padding:.3rem .6rem;background:#0b0c10b3;border:2px solid #000;display:none;white-space:nowrap;');
  // The expedition's standing reminders (#9553): a sled far behind, exhaustion. Shown while they hold.
  const expedition = element('div', 'max-width:32rem;padding:.25rem .55rem;background:#0b0c10b3;border:2px solid #000;display:none;');
  corner.append(expedition, vitals);
  const toasts = element('div', 'position:absolute;left:50%;top:1.2rem;transform:translateX(-50%);display:flex;flex-direction:column;align-items:center;gap:.3rem;');
  const flash = element('div', `position:absolute;inset:0;box-shadow:inset 0 0 12vmin 4vmin #b0141480;opacity:0;transition:opacity ${FLASH_MS}ms ease-out;`);
  const lowHealth = element('div', 'position:absolute;inset:0;box-shadow:inset 0 0 10vmin 2vmin #7a0c0c66;display:none;');

  layer.append(lowHealth, flash, crosshair, corner, prompt, toasts);
  root.append(layer);
  if (projection === undefined) return () => layer.remove();

  const toast = (message: string): void => {
    const cut = message.length > TOAST_CHARACTERS ? `${message.slice(0, TOAST_CHARACTERS - 1)}…` : message;
    const note = element('div', 'padding:.25rem .55rem;background:#0b0c10cc;border:2px solid #000;transition:opacity .4s;', cut);
    toasts.append(note);
    while (toasts.childElementCount > MAXIMUM_TOASTS) toasts.firstElementChild?.remove();
    setTimeout(() => { note.style.opacity = '0'; }, TOAST_MS);
    setTimeout(() => note.remove(), TOAST_MS + 500);
  };

  // What was last drawn: a first projection sets these without flashing or announcing anything.
  let drawn: { hits: number; toasts: Map<string, string> } | null = null;

  const draw = (values: Values | null): void => {
    layer.style.display = values === null ? 'none' : 'block';
    if (values === null) return;

    for (const row of rows) {
      const reading = row.vital.read(values);
      for (const { part, shown } of row.parts) part.style.display = reading === null ? 'none' : shown;
      if (reading === null) continue;
      const lit = Math.ceil(reading.share * SEGMENTS - 1e-6);
      row.blocks.forEach((block, index) => { block.style.background = index < lit ? row.vital.colour : '#2a2d36'; });
      row.note.textContent = reading.note;
    }

    const health = share(number(values, 'health'), number(values, 'maximumHealth'));
    lowHealth.style.display = health !== null && health > 0 && health <= LOW_HEALTH_SHARE ? 'block' : 'none';

    const said = text(values, 'dungeonPrompt') ?? '';
    prompt.style.display = said === '' ? 'none' : 'block';
    prompt.textContent = said;

    const reminder = text(values, 'expeditionPrompt') ?? '';
    expedition.style.display = reminder === '' ? 'none' : 'block';
    expedition.textContent = reminder;

    const hits = number(values, 'hitsTaken') ?? 0;
    const now = new Map(TOASTS.map((source) => [source.key, text(values, source.key) ?? '']));
    if (drawn !== null) {
      if (hits > drawn.hits) {
        // Full at once, then fade: the reflow between makes the full flash the fade's start.
        flash.style.transition = 'none';
        flash.style.opacity = '1';
        void flash.offsetWidth;
        flash.style.transition = `opacity ${FLASH_MS}ms ease-out`;
        flash.style.opacity = '0';
      }
      for (const source of TOASTS) {
        const value = now.get(source.key) ?? '';
        const message = value !== drawn.toasts.get(source.key) ? source.show(value) : null;
        if (message !== null) toast(message);
      }
    }
    drawn = { hits, toasts: now };
  };

  draw(projectionValues(projection.current()));
  const unsubscribe = projection.subscribe((envelope) => draw(projectionValues(envelope)));
  return () => { unsubscribe(); layer.remove(); };
}
