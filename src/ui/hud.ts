import type { RuntimeUiProjectionEnvelope, RustyApplicationUiProjectionView } from '@rusty-engine/product-ui';
import { element } from './dom.js';

/** The projection the product publishes; see ProductUiProjection in C#. */
export const PROJECTION_STREAM = 'craftsurvive.terrain';
export const PROJECTION_CONTRACT = 'craftsurvive.terrain.v1';

export type Values = Readonly<Record<string, unknown>>;

/** The product's values from a projection envelope, or null when it is not the product's stream. */
export function projectionValues(envelope: RuntimeUiProjectionEnvelope | null): Values | null {
  return envelope !== null && envelope.stream === PROJECTION_STREAM && envelope.contract === PROJECTION_CONTRACT
    && typeof envelope.value === 'object' && envelope.value !== null && !Array.isArray(envelope.value)
    ? envelope.value as Values
    : null;
}

/** One HUD row: a label and how to show it from the projection, or null while the fact is absent. */
interface HudRow {
  readonly label: string;
  readonly show: (values: Values) => string | null;
}

export const number = (values: Values, key: string): number | null => (typeof values[key] === 'number' ? values[key] as number : null);
export const text = (values: Values, key: string): string | null => (typeof values[key] === 'string' ? values[key] as string : null);
const fixed = (value: number | null, digits: number): string | null => (value === null ? null : value.toFixed(digits));

const ROWS: readonly HudRow[] = [
  { label: 'Dungeon', show: (v) => { const p = text(v, 'dungeonPrompt'); return p === null || p === '' ? null : p; } },
  { label: 'Time', show: (v) => { const t = text(v, 'worldTime'); return t === null ? null : number(v, 'night') === 1 ? `${t} (night)` : t; } },
  { label: 'Health', show: (v) => { const h = number(v, 'health'); const m = number(v, 'maximumHealth'); return h === null || m === null ? null : `${h} / ${m}`; } },
  { label: 'Food', show: (v) => { const f = number(v, 'satiety'); return f === null ? null : `${f.toFixed(0)}%${f <= 0 ? ' (starving)' : f < 25 ? ' (hungry)' : ''}`; } },
  { label: 'Air', show: (v) => { const b = number(v, 'breath'); const m = number(v, 'maximumBreath'); return b === null || m === null || b >= m ? null : `${b.toFixed(0)} s${b <= 0 ? ' (drowning)' : ''}`; } },
  { label: 'Level', show: (v) => { const l = number(v, 'level'); const x = number(v, 'experience'); return l === null || x === null ? null : `${l} (${x} xp)`; } },
  { label: 'Carrying', show: (v) => { const c = text(v, 'carried'); return c === null ? null : c === '' ? 'nothing' : c; } },
  { label: 'Defeats', show: (v) => fixed(number(v, 'defeats'), 0) },
  { label: 'Position', show: (v) => { const x = number(v, 'playerX'); const y = number(v, 'playerY'); const z = number(v, 'playerZ'); return x === null || y === null || z === null ? null : `${x.toFixed(1)}, ${y.toFixed(1)}, ${z.toFixed(1)}`; } },
  { label: 'Places', show: (v) => { const p = number(v, 'discoveryPlaces'); const s = number(v, 'discoveryVisited'); return p === null || s === null ? null : `${p} found, ${s} visited`; } },
  { label: 'Last found', show: (v) => { const t = text(v, 'discoveryLastFound'); return t === null || t === '' ? null : t; } },
  { label: 'Nearest place', show: (v) => { const d = number(v, 'discoveryNearest'); return d === null || d <= 0 ? null : `${d.toFixed(0)} m`; } },
  { label: 'Edits', show: (v) => fixed(number(v, 'overlayEntries'), 0) },
  { label: 'Last action', show: (v) => text(v, 'lastAction') },
  { label: 'Underground', show: (v) => { const t = text(v, 'dungeonLast'); return t === null || t === 'none' ? null : t; } },
  { label: 'Inventory', show: (v) => { const t = text(v, 'lastInventory'); return t === null || t === 'none' ? null : t; } },
];

/** Shows the product's projection; it reads only what the product publishes and keeps nothing. */
export function mountHud(host: HTMLElement, projection: RustyApplicationUiProjectionView | undefined): () => void {
  const table = element('dl', 'display:grid;gap:.1rem .6rem;grid-template-columns:auto 1fr;margin:.3rem 0 0;');
  const cells = ROWS.map((row) => {
    const term = element('dt', 'opacity:.75;', row.label);
    const value = element('dd', 'margin:0;', '—');
    table.append(term, value);
    return value;
  });
  const waiting = element('p', 'margin:.3rem 0 0;opacity:.75;', projection === undefined ? 'This host provides no projection.' : 'Waiting for the game…');
  host.append(waiting, table);

  const render = (envelope: RuntimeUiProjectionEnvelope | null): void => {
    const values = projectionValues(envelope);
    waiting.hidden = values !== null;
    ROWS.forEach((row, index) => { cells[index]!.textContent = values === null ? '—' : row.show(values) ?? '—'; });
  };

  if (projection === undefined) return () => {};
  render(projection.current());
  return projection.subscribe(render);
}
