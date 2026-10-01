import type { RustyApplicationUiIntentsPort, RustyApplicationUiProjectionView } from '@rusty-engine/product-ui';
import { button, element } from './dom.js';
import { projectionValues, text } from './hud.js';

/** The payload intent the product declares; see PlayerAction in C#. */
export const ACTION_INTENT = 'craftsurvive.ui';
export const ACTION_CONTRACT = 'craftsurvive.ui.action.v1';

/** One control: a label, and the request it claims. The product aims it where the player looks. */
interface ActionControl {
  readonly label: string;
  readonly title: string;
  readonly data: Readonly<Record<string, string | number>>;
  /** Whether the request carries the block chosen from the product's build palette. */
  readonly built?: true;
}

const CONTROLS: readonly ActionControl[] = [
  { label: 'Blast', title: 'Fire a radius-2 charge at the block you are aiming at', data: { action: 'blast', radius: 2 } },
  { label: 'Floor', title: 'Lay a 3x3 floor on the face you are aiming at', data: { action: 'plate', width: 3, depth: 3 }, built: true },
  { label: 'Wall', title: 'Raise a 4-long, 3-high wall on the face you are aiming at', data: { action: 'wall', length: 4, height: 3 }, built: true },
  { label: 'Door', title: 'Place a door on the face you are aiming at', data: { action: 'door' } },
  { label: 'Light', title: 'Place a light on the face you are aiming at', data: { action: 'light' } },
  { label: 'Chest', title: 'Place a container on the face you are aiming at', data: { action: 'container' } },
  { label: 'Undo', title: 'Take back the last floor or wall', data: { action: 'undo' } },
];

/**
 * Claims the product's action intent; the outcome comes back through the projection, not here. The
 * block picker offers the build palette the product publishes, so the UI names blocks the product
 * chose and never their ids.
 */
export function mountActions(host: HTMLElement, intents: RustyApplicationUiIntentsPort | undefined,
  projection: RustyApplicationUiProjectionView | undefined): () => void {
  const bar = element('div', 'display:flex;flex-wrap:wrap;gap:.25rem;margin-top:.35rem;');
  const block = element('select');
  block.title = 'The block floors and walls are built from';
  block.disabled = true;
  bar.append(block);
  for (const control of CONTROLS) {
    const claim = button(control.label);
    claim.title = control.title;
    claim.disabled = intents === undefined;
    claim.addEventListener('click', () => {
      const data = control.built === true && block.value !== '' ? { ...control.data, material: block.value } : control.data;
      intents?.claim(ACTION_INTENT, { kind: 'product-payload', contract: ACTION_CONTRACT, data });
    });
    bar.append(claim);
  }

  host.append(bar);
  if (projection === undefined) return () => {};

  let offered = '';
  const offer = (palette: string | null): void => {
    if (palette === null || palette === offered) return;
    offered = palette;
    const chosen = block.value;
    block.replaceChildren(...palette.split(',').filter((name) => name.length > 0).map((name) => {
      const option = element('option', '', name);
      option.value = name;
      return option;
    }));
    if (palette.split(',').includes(chosen)) block.value = chosen;
    block.disabled = block.options.length === 0;
  };
  const read = (values: ReturnType<typeof projectionValues>): void => offer(values === null ? null : text(values, 'buildPalette'));
  read(projectionValues(projection.current()));
  return projection.subscribe((envelope) => read(projectionValues(envelope)));
}
