import type { RustyApplicationUiIntentsPort, RustyApplicationUiProjectionView } from '@rusty-engine/product-ui';
import type { ProductVideoOption, VideoOptionsMount } from '@rusty-engine/video-options';
import { ACTION_CONTRACT, ACTION_INTENT } from './actions.js';
import { element, message } from './dom.js';
import { projectionValues, text, type Values } from './hud.js';

/**
 * How long a change the player made is awaited before the product's published value is shown
 * again: a slider dragged through its range sends a value per step, and a publication for an
 * earlier step would otherwise snap it back. A refused change returns to the product's value then.
 */
const SETTLE_MS = 1500;

/** The game's own options are drawn in this group of the panel, after the Engine's. */
const GAME_GROUP = 'Game';

/**
 * The panel's colours and type, from the HUD's. The Engine panel declares its own defaults on
 * `.rusty-video-options` itself, so they are set there, by a rule under this menu's holder that
 * outranks the panel's.
 */
const HOLDER_CLASS = 'craftsurvive-options';
const PANEL_STYLE: Readonly<Record<string, string>> = {
  '--rusty-video-options-background': 'rgb(15 19 25 / 0%)',
  '--rusty-video-options-foreground': '#edf5ff',
  '--rusty-video-options-muted': '#9fb0c4',
  '--rusty-video-options-accent': '#8fb34a',
  '--rusty-video-options-border': '#62748a',
  '--rusty-video-options-warning': '#e0a040',
  '--rusty-video-options-radius': '.35rem',
  '--rusty-video-options-font': '.76rem/1.25 ui-monospace,SFMono-Regular,Menlo,monospace',
};

/** One of the game's options as the product publishes it: id|label|min|max|step|unit|value|description. */
interface GameOption { readonly id: string; readonly label: string; readonly min: number; readonly max: number; readonly step: number; readonly unit: string; readonly value: number; readonly description: string }

const gameOptions = (values: Values): GameOption[] => (text(values, 'gameOptions') ?? '').split(';').filter((entry) => entry.length > 0)
  .map((entry) => {
    const [id = '', label = '', min = '0', max = '0', step = '1', unit = '', value = '0', description = ''] = entry.split('|');
    return { id, label, min: Number(min), max: Number(max), step: Number(step), unit, value: Number(value), description };
  });

/**
 * The options menu (#9759): the Engine's video options panel - every renderer setting this pair has,
 * kept for the install by the Engine - with the game's own options beside them. The game's options
 * are what C# publishes; a change is a claim on the product's action intent, and C# applies and keeps
 * it and publishes them again. Nothing is mounted or fetched until the player opens it.
 */
export function mountOptions(host: HTMLElement, intents: RustyApplicationUiIntentsPort | undefined,
  projection: RustyApplicationUiProjectionView | undefined, status: HTMLElement): () => void {
  const details = element('details', 'margin-top:.35rem;');
  details.append(element('summary', 'cursor:pointer;font-weight:700;', 'Options'));
  const holder = element('div', 'margin-top:.3rem;');
  holder.className = HOLDER_CLASS;
  const theme = element('style');
  theme.textContent = `.${HOLDER_CLASS} .rusty-video-options { ${Object.entries(PANEL_STYLE).map(([name, value]) => `${name}: ${value};`).join(' ')} padding: .35rem .2rem; }`;
  details.append(theme, holder);
  host.append(details);

  let published: GameOption[] = [];
  let panel: VideoOptionsMount | null = null;
  let mounting = false;
  let disposed = false;
  /** The values sent and not yet published back, by option id. */
  const sent = new Map<string, number>();
  let settle: ReturnType<typeof setTimeout> | undefined;
  const show = (): void => { panel?.setProductOptions(productOptions()); };

  const productOptions = (): ProductVideoOption[] => published.map((option) => ({
    id: option.id,
    label: option.label,
    group: GAME_GROUP,
    description: option.description,
    kind: 'range',
    min: option.min,
    max: option.max,
    step: option.step,
    unit: option.unit,
    value: option.value,
    onChange: (value) => {
      if (typeof value !== 'number') return;
      const chosen = Math.round(value);
      sent.set(option.id, chosen);
      clearTimeout(settle);
      settle = setTimeout(() => { sent.clear(); show(); }, SETTLE_MS);
      intents?.claim(ACTION_INTENT, { kind: 'product-payload', contract: ACTION_CONTRACT, data: { action: 'option', name: option.id, value: chosen } });
    },
  }));

  const read = (values: Values | null): void => {
    if (values === null) return;
    const next = gameOptions(values);
    if (JSON.stringify(next) === JSON.stringify(published)) return;
    published = next;
    // A publication for an earlier step of a change still being made is not drawn over it.
    if (next.some((option) => sent.has(option.id) && sent.get(option.id) !== option.value)) return;
    sent.clear();
    clearTimeout(settle);
    show();
  };

  details.addEventListener('toggle', () => {
    if (!details.open || panel !== null || mounting) return;
    mounting = true;
    status.hidden = true;
    void import('@rusty-engine/video-options').then(async ({ mountVideoOptions }) => {
      const mounted = await mountVideoOptions(holder, { title: '', productOptions: productOptions() });
      if (disposed) { mounted.dispose(); return; }
      panel = mounted;
    }).catch((error: unknown) => {
      if (disposed) return;
      status.textContent = message(error, 'The options could not open.');
      status.hidden = false;
    }).finally(() => { mounting = false; });
  });

  if (projection !== undefined) read(projectionValues(projection.current()));
  const unsubscribe = projection?.subscribe((envelope) => read(projectionValues(envelope))) ?? ((): void => {});
  return () => {
    disposed = true;
    clearTimeout(settle);
    unsubscribe();
    panel?.dispose();
    details.remove();
  };
}
