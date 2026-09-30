import type { RustyApplicationUiIntentsPort } from '@rusty-engine/product-ui';
import { button, element } from './dom.js';

/** The payload intent the product declares; see PlayerAction in C#. */
export const ACTION_INTENT = 'craftsurvive.ui';
export const ACTION_CONTRACT = 'craftsurvive.ui.action.v1';

/** One control: a label, and the request it claims. The product aims it where the player looks. */
interface ActionControl {
  readonly label: string;
  readonly title: string;
  readonly data: Readonly<Record<string, string | number>>;
}

const CONTROLS: readonly ActionControl[] = [
  { label: 'Blast', title: 'Fire a radius-2 charge at the block you are aiming at', data: { action: 'blast', radius: 2 } },
  { label: 'Floor', title: 'Lay a 3x3 floor on the face you are aiming at', data: { action: 'plate', width: 3, depth: 3 } },
  { label: 'Wall', title: 'Raise a 4-long, 3-high wall on the face you are aiming at', data: { action: 'wall', length: 4, height: 3 } },
  { label: 'Door', title: 'Place a door on the face you are aiming at', data: { action: 'door' } },
  { label: 'Light', title: 'Place a light on the face you are aiming at', data: { action: 'light' } },
  { label: 'Chest', title: 'Place a container on the face you are aiming at', data: { action: 'container' } },
  { label: 'Undo', title: 'Take back the last floor or wall', data: { action: 'undo' } },
];

/** Claims the product's action intent; the outcome comes back through the projection, not here. */
export function mountActions(host: HTMLElement, intents: RustyApplicationUiIntentsPort | undefined): void {
  const bar = element('div', 'display:flex;flex-wrap:wrap;gap:.25rem;margin-top:.35rem;');
  for (const control of CONTROLS) {
    const claim = button(control.label);
    claim.title = control.title;
    claim.disabled = intents === undefined;
    claim.addEventListener('click', () => intents?.claim(ACTION_INTENT, { kind: 'product-payload', contract: ACTION_CONTRACT, data: control.data }));
    bar.append(claim);
  }

  host.append(bar);
}
