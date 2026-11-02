import type { RustyApplicationUiIntentsPort, RustyApplicationUiProjectionView } from '@rusty-engine/product-ui';
import { ACTION_CONTRACT, ACTION_INTENT } from './actions.js';
import { button, element } from './dom.js';
import { projectionValues, text, type Values } from './hud.js';

/** One `a|b|c` entry of a published list, split on its fields. */
const entries = (values: Values, key: string): string[][] => (text(values, key) ?? '').split(';').filter((entry) => entry.length > 0)
  .map((entry) => entry.split('|'));

/**
 * The development magic compiler (#10529): choose an outer trigram over an inner one, read what C# compiled
 * them into, and cast it where the player aims (or press C in the world). The formulas the game carries cast
 * as they stand, and the overlay shows one quantity of the field. C# owns the composition and every outcome;
 * this panel only claims the requests and shows what is published.
 */
export function mountCasting(host: HTMLElement, intents: RustyApplicationUiIntentsPort | undefined,
  projection: RustyApplicationUiProjectionView | undefined): () => void {
  const claim = (data: Readonly<Record<string, string | number>>): void => {
    intents?.claim(ACTION_INTENT, { kind: 'product-payload', contract: ACTION_CONTRACT, data });
  };

  const section = element('details', 'margin-top:.5rem;');
  section.append(element('summary', 'cursor:pointer;font-weight:700;', 'Casting (development compiler)'));
  const compose = element('div', 'display:flex;flex-wrap:wrap;gap:.25rem;align-items:center;margin-top:.25rem;');
  const upper = element('select');
  upper.title = 'The outer (upper) trigram: how the formula reaches the world';
  const lower = element('select');
  lower.title = 'The inner (lower) trigram: what acts';
  const cast = button('Cast');
  cast.title = 'Cast the composed formula where you are aiming (C in the world)';
  compose.append(upper, element('span', 'opacity:.75;', 'over'), lower, cast);

  const composed = element('p', 'margin:.25rem 0 0;font-weight:700;');
  const detail = element('p', 'margin:.1rem 0 0;opacity:.75;');
  const steps = element('p', 'margin:.1rem 0 0;');
  const binary = element('details', 'margin-top:.2rem;');
  binary.append(element('summary', 'cursor:pointer;opacity:.75;', 'Compiled formula (JSON)'));
  const json = element('pre', 'margin:.2rem 0 0;max-height:12rem;overflow:auto;user-select:text;white-space:pre-wrap;');
  binary.append(json);

  const carried = element('div', 'display:flex;flex-wrap:wrap;gap:.25rem;align-items:center;margin-top:.35rem;');
  const formula = element('select');
  formula.title = 'A compiled formula the game carries, cast as it stands';
  const castFormula = button('Cast formula');
  const overlay = element('select');
  overlay.title = 'Show one quantity of the field around you as cubes';
  carried.append(formula, castFormula, element('span', 'opacity:.75;margin-left:.4rem;', 'Show'), overlay);
  const last = element('p', 'margin:.25rem 0 0;opacity:.85;');
  section.append(compose, composed, detail, steps, binary, carried, last);
  host.append(section);

  const disabled = intents === undefined;
  for (const control of [upper, lower, cast, formula, castFormula, overlay]) control.disabled = disabled;
  const recompose = (): void => { if (upper.value !== '' && lower.value !== '') claim({ action: 'compose', upper: upper.value, lower: lower.value }); };
  upper.addEventListener('change', recompose);
  lower.addEventListener('change', recompose);
  cast.addEventListener('click', () => claim({ action: 'cast' }));
  castFormula.addEventListener('click', () => { if (formula.value !== '') claim({ action: 'formula', formula: formula.value }); });
  overlay.addEventListener('change', () => claim({ action: 'overlay', quantity: overlay.value }));
  if (projection === undefined) return () => section.remove();

  /** Replaces a picker's options when the published list changes, then shows the published choice. */
  const fill = (picker: HTMLSelectElement, options: readonly (readonly [string, string])[], chosen: string): void => {
    const key = options.map(([value, label]) => `${value}=${label}`).join(',');
    if (picker.dataset['options'] !== key) {
      picker.dataset['options'] = key;
      picker.replaceChildren(...options.map(([value, label]) => {
        const option = element('option', '', label);
        option.value = value;
        return option;
      }));
    }
    if (chosen !== '') picker.value = chosen;
  };

  const read = (values: Values | null): void => {
    if (values === null || text(values, 'castComposed') === null) return;
    const trigrams = entries(values, 'castTrigrams').map(([id = '', glyph = '', name = '']) => [id, `${glyph} ${id} (${name})`] as const);
    fill(upper, trigrams, text(values, 'castUpper') ?? '');
    fill(lower, trigrams, text(values, 'castLower') ?? '');
    fill(formula, entries(values, 'castFormulas').map(([id = '', label = '']) => [id, label] as const), formula.value);
    fill(overlay, [['none', 'nothing'] as const, ...entries(values, 'castOverlays').map(([id = '', label = '']) => [id, label] as const)],
      text(values, 'castOverlay') ?? 'none');
    composed.textContent = text(values, 'castComposed');
    detail.textContent = text(values, 'castDetail');
    steps.textContent = text(values, 'castSteps');
    json.textContent = text(values, 'castJson');
    last.textContent = text(values, 'castLast');
  };
  read(projectionValues(projection.current()));
  const unsubscribe = projection.subscribe((envelope) => read(projectionValues(envelope)));
  return () => { unsubscribe(); section.remove(); };
}
