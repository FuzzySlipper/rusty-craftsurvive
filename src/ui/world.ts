import type { RustyApplicationUiIntentsPort, RustyApplicationUiProjectionView } from '@rusty-engine/product-ui';
import { button, element, isolateEvents } from './dom.js';
import { number, projectionValues, text } from './hud.js';

const INTENT = 'craftsurvive.world';
const CONTRACT = 'craftsurvive.world.action.v1';
const PANEL = 'pointer-events:auto;background:rgb(20 24 27 / 94%);color:#eee7d5;padding:1rem;border:1px solid #84775c;border-radius:.4rem;';

/** Controls and descriptions only. The geographic overview is an Engine mesh behind this UI. */
export function mountWorld(host: Element, gameUi: HTMLElement, intents: RustyApplicationUiIntentsPort | undefined,
  projection: RustyApplicationUiProjectionView | undefined): () => void {
  const claim = (data: Readonly<Record<string, string | number>>): void => {
    intents?.claim(INTENT, { kind: 'product-payload', contract: CONTRACT, data });
  };
  const open = button('World');
  open.style.cssText = 'position:fixed;top:.75rem;left:50%;pointer-events:auto;z-index:8;';
  open.disabled = intents === undefined;
  open.addEventListener('click', () => { document.exitPointerLock(); claim({ action: 'open' }); });
  const screen = element('section', 'position:fixed;inset:0;pointer-events:none;z-index:9;font:15px/1.45 system-ui,sans-serif;');
  screen.setAttribute('aria-label', 'World map');
  screen.hidden = true;
  const heading = element('header', PANEL + 'position:absolute;top:1rem;left:1rem;right:1rem;display:flex;gap:1rem;align-items:center;');
  heading.append(element('strong', '', 'The wider world'));
  const identity = element('span', 'flex:1;opacity:.8;');
  const close = button('Explore here');
  close.addEventListener('click', () => claim({ action: 'close' }));
  // Prototype comparison (#9436): the product switches between its smooth and faceted relief.
  const style = button('Faceted relief');
  style.addEventListener('click', () => claim({ action: 'style' }));
  heading.append(identity, style, close);
  const destinations = element('nav', PANEL + 'position:absolute;left:1rem;bottom:1rem;width:19rem;max-height:42vh;overflow:auto;');
  destinations.setAttribute('aria-label', 'Travel to a place');
  const sites = element('div');
  destinations.append(element('h3', 'margin:0 0 .5rem;', 'Travel to a place'),
    element('p', 'font-size:.85rem;opacity:.75;', 'North is toward the far edge. Choose a place to plan a route, or move the blue waypoint with W/A/S/D and press T.'), sites);
  // Travel controls: the product owns the journey; these buttons only claim its actions.
  const journey = element('section', PANEL + 'position:absolute;left:50%;bottom:1rem;transform:translateX(-50%);width:26rem;');
  journey.setAttribute('aria-label', 'Journey');
  const journeyStatus = element('p', 'margin:0 0 .5rem;font-size:.9rem;');
  const travelButtons: Record<string, HTMLButtonElement> = {};
  const journeyRow = element('div', 'display:flex;gap:.4rem;flex-wrap:wrap;');
  for (const [action, label] of [['go', 'Set out'], ['pause', 'Pause'], ['halt', 'Halt'], ['waypoint', 'Route to waypoint']] as const) {
    const control = button(label);
    control.addEventListener('click', () => claim({ action }));
    travelButtons[action] = control; journeyRow.append(control);
  }
  journey.append(journeyStatus, journeyRow);
  const form = element('form', PANEL + 'position:absolute;right:1rem;bottom:1rem;width:18rem;');
  form.append(element('h3', 'margin:0 0 .5rem;', 'Begin a new world'));
  const seedLabel = element('label', 'display:block;', 'World seed');
  const seed = element('input', 'box-sizing:border-box;width:100%;margin:.3rem 0 .7rem;');
  seed.name = 'seed'; seed.inputMode = 'numeric'; seed.pattern = '[0-9]+'; seed.required = true; seed.maxLength = 20;
  seed.value = '12345'; seedLabel.append(seed);
  const sizeLabel = element('label', 'display:block;', 'World width');
  const size = element('select', 'margin:.3rem 0 .7rem;width:100%;');
  size.name = 'size';
  for (const [value, label] of [[4096, '4 km'], [8192, '8 km'], [16384, '16 km']] as const) {
    const option = element('option', '', label); option.value = String(value); size.append(option);
  }
  size.value = '8192'; sizeLabel.append(size);
  const create = button('Create new world'); create.type = 'submit';
  form.append(seedLabel, sizeLabel, element('p', 'font-size:.85rem;opacity:.75;', 'Starts a fresh expedition and replaces your active world.'), create);
  form.addEventListener('submit', event => { event.preventDefault(); claim({ action: 'create', seed: seed.value, size: Number(size.value) }); });
  const status = element('p', PANEL + 'position:absolute;top:5rem;left:50%;transform:translateX(-50%);max-width:40rem;');
  status.setAttribute('role', 'status');
  for (const panel of [heading, destinations, form, status, journey]) {
    panel.setAttribute('data-rusty-ui-interactive', ''); isolateEvents(panel);
  }
  screen.append(heading, destinations, form, status, journey); host.append(open, screen);
  let renderedSites = '';
  const render = (): void => {
    const values = projectionValues(projection?.current() ?? null);
    if (values === null) return;
    const visible = number(values, 'worldMapOpen') === 1;
    screen.hidden = !visible; open.hidden = visible; gameUi.hidden = visible;
    if (visible && document.pointerLockElement !== null) document.exitPointerLock();
    identity.textContent = `Seed ${text(values, 'worldSeed') ?? ''} · ${((number(values, 'worldSize') ?? 0) / 1000).toFixed(1)} km across`;
    status.textContent = text(values, 'worldMessage') ?? ''; status.hidden = status.textContent.length === 0;
    style.textContent = number(values, 'worldMapFaceted') === 1 ? 'Smooth relief' : 'Faceted relief';
    style.hidden = (text(values, 'worldSites') ?? '') === '';
    const phase = text(values, 'worldTravelPhase') ?? 'idle';
    journeyStatus.textContent = text(values, 'worldTravel') ?? '';
    journey.hidden = style.hidden;
    const go = travelButtons['go'], pause = travelButtons['pause'], halt = travelButtons['halt'];
    if (go) { go.disabled = !(phase === 'planned' || phase === 'paused'); go.textContent = phase === 'paused' ? 'Resume' : 'Set out'; }
    if (pause) pause.disabled = phase !== 'travelling';
    if (halt) halt.disabled = phase === 'idle' || phase === 'arrived';
    const source = text(values, 'worldSites') ?? '';
    if (source === renderedSites) return;
    renderedSites = source; sites.replaceChildren();
    for (const entry of source.split(';').filter(Boolean)) {
      const [id = '', name = '', region = '', x = '', z = '', elevation = ''] = entry.split('|');
      const visit = button(name); visit.style.cssText = 'display:block;width:100%;text-align:left;margin-top:.7rem;padding:.5rem;';
      visit.addEventListener('click', () => claim({ action: 'travel', site: Number(id) }));
      sites.append(visit, element('small', 'display:block;opacity:.8;', `${region} · ${elevation} m · (${x}, ${z})`));
    }
  };
  render(); const unsubscribe = projection?.subscribe(render);
  return () => { unsubscribe?.(); open.remove(); screen.remove(); gameUi.hidden = false; };
}
