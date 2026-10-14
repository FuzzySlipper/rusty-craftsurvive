import type { RustyApplicationUiIntentsPort, RustyApplicationUiPort, RustyApplicationUiProjectionView } from '@rusty-engine/product-ui';
import { button, element, isolateEvents } from './dom.js';
import { number, projectionValues, text } from './hud.js';
import { layers } from './layers.js';

export const TOWN_INTENT = 'craftsurvive.town';
export const TOWN_CONTRACT = 'craftsurvive.town.action.v1';

/** The key that accepts a town's doorstep offer while walking. */
const ENTER_KEYS = ['Enter', 'NumpadEnter'];

const STYLE = `
.town-ui { position:fixed;inset:0;z-index:12;pointer-events:none;color:#f3ead7;font:16px/1.5 Georgia,'Iowan Old Style',serif; }
.town-ui [hidden] { display:none !important; }
.town-ui * { box-sizing:border-box; }
.town-ui button { font:inherit;color:inherit;background:none;border:0;padding:0;cursor:pointer;pointer-events:auto; }
.town-ui :focus-visible { outline:2px solid #e9c98d;outline-offset:4px;border-radius:6px; }
.town-veil { position:absolute;left:0;right:0;top:0;height:11rem;background:linear-gradient(to bottom,rgb(8 11 10 / 62%),transparent); }
.town-heading { position:absolute;top:1.6rem;left:2rem;text-shadow:0 2px 12px #000c; }
.town-heading h1 { margin:0;font-size:2.1rem;font-weight:normal;letter-spacing:.02em; }
.town-heading p { margin:.1rem 0 0;font:600 .72rem/1.4 system-ui,sans-serif;letter-spacing:.16em;text-transform:uppercase;color:#e3c48a; }
.town-leave { position:absolute;top:1.9rem;right:2rem;font:.9rem/1.4 system-ui,sans-serif !important;color:#e6dcc6 !important;text-shadow:0 1px 8px #000c;
  padding:.35rem .2rem !important;border-bottom:1px solid #e3c48a66 !important; }
.town-leave:hover { color:#fff !important;border-bottom-color:#e3c48a !important; }
.town-markers { position:absolute;inset:0; }
.town-marker { position:absolute;transform:translate(-50%,-100%);padding:.2rem .7rem;border-radius:999px;white-space:nowrap;font-size:.95rem;
  background:rgb(14 19 17 / 55%);color:#f3ead7cc;box-shadow:0 2px 14px #0006;transition:background .15s,color .15s,transform .15s; }
.town-marker[data-highlight=true] { background:rgb(20 27 24 / 88%);color:#fff;box-shadow:0 0 0 1px #e3c48a99,0 4px 18px #0008;transform:translate(-50%,-110%); }
.town-overview { position:absolute;left:50%;bottom:1.6rem;transform:translateX(-50%);display:grid;justify-items:center;gap:.6rem;width:min(44rem,calc(100vw - 3rem)); }
.town-overview > p { margin:0;font-style:italic;color:#efe6d2d9;text-shadow:0 1px 10px #000; }
.town-doors { display:flex;gap:.8rem;flex-wrap:wrap;justify-content:center; }
.town-door { text-align:left;padding:.55rem 1.2rem !important;border-radius:999px !important;background:rgb(14 19 17 / 74%) !important;
  backdrop-filter:blur(6px);box-shadow:0 0 0 1px #e3c48a33,0 6px 20px #0007;transition:background .15s,box-shadow .15s; }
.town-door[data-highlight=true],.town-door:hover { background:rgb(28 36 31 / 90%) !important;box-shadow:0 0 0 1px #e3c48acc,0 6px 22px #000a; }
.town-door small { display:block;font:.78rem/1.35 system-ui,sans-serif;color:#cfc6b2; }
.town-dialogue { position:absolute;left:0;right:0;bottom:0;padding:5rem 2rem 1.6rem;pointer-events:none;
  background:linear-gradient(to top,rgb(9 12 11 / 94%) 0%,rgb(9 12 11 / 88%) 58%,transparent); }
.town-dialogue-inner { margin:0 auto;max-width:62rem;display:grid;grid-template-columns:8.5rem minmax(0,1fr);gap:1.8rem;align-items:start;pointer-events:auto; }
.town-dialogue-inner.no-portrait { grid-template-columns:minmax(0,1fr); }
.town-portrait { width:8.5rem;aspect-ratio:1;border-radius:50%;object-fit:cover;box-shadow:0 0 0 2px #e3c48a99,0 8px 24px #000a; }
.town-speaker { display:flex;align-items:baseline;gap:.9rem;flex-wrap:wrap; }
.town-speaker h2 { margin:0;font-size:1.35rem;font-weight:normal; }
.town-speaker span { font:600 .7rem/1.4 system-ui,sans-serif;letter-spacing:.14em;text-transform:uppercase;color:#e3c48a; }
.town-speaker .town-place { flex:1;text-align:right;font:italic .9rem/1.4 Georgia,serif;letter-spacing:0;text-transform:none;color:#cfc6b2; }
.town-speech { margin:.5rem 0 .8rem;font-size:1.12rem;line-height:1.55;max-width:46rem; }
.town-outcome { margin:-.3rem 0 .8rem;font:.88rem/1.45 system-ui,sans-serif;color:#e9d3a4; }
.town-outcome::before { content:'✦ ';color:#e3c48a; }
.town-choices { display:grid;gap:.15rem;margin:0;padding:0;list-style:none; }
.town-choice { width:100%;text-align:left;padding:.32rem .6rem !important;border-radius:6px !important;display:flex;gap:.6rem;align-items:baseline; }
.town-choice::before { content:'›';color:#e3c48a;font-weight:bold; }
.town-choice[data-kind=talk]::before { content:'“';font-weight:normal; }
.town-choice:hover:not(:disabled),.town-choice:focus-visible { background:rgb(227 196 138 / 12%) !important; }
.town-choice small { font:.8rem/1.4 system-ui,sans-serif;color:#bdb5a2; }
.town-choice:disabled { cursor:default;color:#a39c8c !important; }
.town-choice:disabled::before { color:#6f6a5f; }
.town-dialogue-foot { display:flex;justify-content:space-between;align-items:baseline;gap:1rem;margin-top:.9rem; }
.town-hint { margin:0;font:.72rem/1.45 system-ui,sans-serif;color:#9d978a;max-width:40rem; }
.town-back { font:.88rem/1.4 system-ui,sans-serif !important;color:#e6dcc6 !important;border-bottom:1px solid #e3c48a66 !important;white-space:nowrap; }
.town-doorstep { position:absolute;left:50%;bottom:7.5rem;transform:translateX(-50%);display:flex;align-items:center;gap:1rem;padding:.6rem .7rem .6rem 1.3rem;
  border-radius:999px;background:rgb(14 19 17 / 78%);backdrop-filter:blur(6px);box-shadow:0 0 0 1px #e3c48a44,0 8px 26px #0008;white-space:nowrap; }
.town-doorstep strong { font-weight:normal;font-size:1.1rem; }
.town-doorstep small { font:.8rem/1.4 system-ui,sans-serif;color:#cfc6b2; }
.town-doorstep button { padding:.35rem .95rem !important;border-radius:999px !important;background:#e3c48a !important;color:#1b1a15 !important;
  font:600 .85rem/1.3 system-ui,sans-serif !important; }
.town-doorstep kbd { font:600 .72rem/1 system-ui,sans-serif;padding:.15rem .35rem;border-radius:4px;background:#1b1a1533;margin-left:.35rem; }
@media (max-width:820px) { .town-dialogue-inner{grid-template-columns:5rem minmax(0,1fr);gap:1rem}.town-portrait{width:5rem}.town-heading h1{font-size:1.6rem} }
`;

/**
 * The town's labels and controls (#9895). The projection supplies every fact - the town, its doors,
 * who speaks and what can be chosen; this keeps DOM identity and focus only, and claims the player's
 * choices for C# to decide.
 */
export function mountTown(host: Element, ui: RustyApplicationUiPort, intents: RustyApplicationUiIntentsPort | undefined,
  projection: RustyApplicationUiProjectionView | undefined): () => void {
  const facts = () => projectionValues(projection?.current() ?? null);
  const claim = (action: string, destination = ''): void => {
    const values = facts();
    intents?.claim(TOWN_INTENT, { kind: 'product-payload', contract: TOWN_CONTRACT, data: {
      action, destination, service: values === null ? '' : text(values, 'townSelected') ?? '',
      visit: values === null ? '0' : text(values, 'townVisit') ?? '0',
    } });
  };
  const interactive = <K extends keyof HTMLElementTagNameMap>(tag: K, className: string): HTMLElementTagNameMap[K] => {
    const node = element(tag); node.className = className;
    node.setAttribute('data-rusty-ui-interactive', ''); isolateEvents(node); return node;
  };

  const root = element('section'); root.className = 'town-ui'; root.setAttribute('aria-label', 'Town');
  root.append(element('style', '', STYLE));
  const scene = element('div'); scene.hidden = true;
  const veil = element('div'); veil.className = 'town-veil';
  const heading = element('div'); heading.className = 'town-heading';
  const title = element('h1'), subtitle = element('p'); heading.append(title, subtitle);
  const leave = interactive('button', 'town-leave'); leave.type = 'button'; leave.textContent = 'Leave town';
  leave.addEventListener('click', () => { claim('leave'); ui.focusGameplay(); });
  const markers = element('div'); markers.className = 'town-markers'; markers.setAttribute('aria-hidden', 'true');

  const overview = interactive('nav', 'town-overview'); overview.setAttribute('aria-label', 'Doors');
  overview.append(element('p', '', 'Put your pack down. There is room to rest here.'));
  const doors = element('div'); doors.className = 'town-doors'; overview.append(doors);

  const dialogue = interactive('section', 'town-dialogue'); dialogue.setAttribute('role', 'dialog');
  const inner = element('div'); inner.className = 'town-dialogue-inner';
  const portrait = element('img'); portrait.className = 'town-portrait';
  const words = element('div');
  const speaker = element('div'); speaker.className = 'town-speaker';
  const speakerName = element('h2'), speakerRole = element('span'), place = element('span'); place.className = 'town-place';
  speaker.append(speakerName, speakerRole, place);
  const speech = element('p'); speech.className = 'town-speech'; speech.setAttribute('role', 'status');
  const outcome = element('p'); outcome.className = 'town-outcome'; outcome.setAttribute('role', 'status');
  const choices = element('ul'); choices.className = 'town-choices'; choices.setAttribute('aria-label', 'What to say or ask for');
  const foot = element('div'); foot.className = 'town-dialogue-foot';
  const hint = element('p'); hint.className = 'town-hint';
  const back = button('Return to town · Esc'); back.className = 'town-back'; back.addEventListener('click', () => claim('back'));
  foot.append(hint, back);
  words.append(speaker, speech, outcome, choices, foot);
  inner.append(portrait, words); dialogue.append(inner);
  scene.append(veil, markers, heading, leave, overview, dialogue);

  const doorstep = interactive('div', 'town-doorstep'); doorstep.hidden = true;
  const doorstepName = element('strong'), doorstepHint = element('small', '', 'Lights ahead. Go in, or walk on.');
  const enter = button('Enter'); enter.append(element('kbd', '', '⏎'));
  enter.addEventListener('click', () => claim('enter'));
  doorstep.append(element('div', '', ''), enter);
  (doorstep.firstChild as HTMLElement).append(doorstepName, element('br'), doorstepHint);
  root.append(scene, doorstep);
  host.append(root);

  const doorControls = new Map<string, HTMLButtonElement>(), markerControls = new Map<string, HTMLElement>();
  const choiceControls = new Map<string, HTMLButtonElement>();
  // Remembered only to move focus on a presentation transition, never as game state.
  let renderedView = '', returnDoor = '';

  const render = (): void => {
    const values = facts();
    const open = values !== null && number(values, 'townOpen') === 1;
    const offer = values === null ? '' : text(values, 'townPrompt') ?? '';
    layers.town = open;
    scene.hidden = !open;
    doorstep.hidden = open || offer === '';
    doorstepName.textContent = offer;
    enter.setAttribute('aria-label', `Enter ${offer}`);
    if (!open || values === null) { renderedView = ''; return; }

    const selected = text(values, 'townSelected') ?? '', hover = text(values, 'townHover') ?? '';
    const view = `${text(values, 'townVisit')}/${selected}`, changed = view !== renderedView;
    title.textContent = text(values, 'townTitle') ?? '';
    subtitle.textContent = text(values, 'townSubtitle') ?? '';
    overview.hidden = selected !== ''; markers.hidden = selected !== ''; dialogue.hidden = selected === ''; leave.hidden = selected !== '';

    for (const entry of (text(values, 'townAnchors') ?? '').split(';').filter(Boolean)) {
      const [key = '', label = '', summary = '', x = '', y = '', visible = ''] = entry.split('|');
      let door = doorControls.get(key), marker = markerControls.get(key);
      if (door === undefined) {
        door = button(''); door.className = 'town-door'; door.dataset.door = key;
        door.addEventListener('click', () => claim('select', key));
        door.addEventListener('focus', () => claim('focus', key));
        door.addEventListener('pointerenter', () => claim('focus', key));
        door.addEventListener('pointerleave', () => { if (document.activeElement !== door) claim('focus'); });
        door.addEventListener('blur', () => claim('focus'));
        doorControls.set(key, door); doors.append(door);
        marker = element('div'); marker.className = 'town-marker'; markerControls.set(key, marker); markers.append(marker);
      }
      // The button node stays stable so a projected camera update cannot steal focus.
      if (door.dataset.caption !== `${label}|${summary}`) {
        door.dataset.caption = `${label}|${summary}`;
        door.replaceChildren(element('span', '', label), element('small', '', summary));
      }
      door.dataset.highlight = String(hover === key);
      marker!.textContent = label; marker!.dataset.highlight = String(hover === key); marker!.hidden = visible !== '1';
      marker!.style.left = `${Number(x) * 100}%`; marker!.style.top = `${(1 - Number(y)) * 100}%`;
    }

    speakerName.textContent = text(values, 'townSpeaker') ?? '';
    speakerRole.textContent = text(values, 'townRole') ?? '';
    place.textContent = text(values, 'townSelectedName') ?? '';
    dialogue.setAttribute('aria-label', place.textContent);
    const url = text(values, 'townPortrait') ?? '';
    portrait.hidden = url === ''; inner.classList.toggle('no-portrait', url === '');
    if (url !== '' && portrait.getAttribute('src') !== url) portrait.src = url;
    portrait.alt = speakerName.textContent;
    const said = text(values, 'townSpeech') ?? '';
    if (speech.textContent !== said) speech.textContent = said;
    const did = text(values, 'townOutcome') ?? '';
    outcome.hidden = did === ''; if (outcome.textContent !== did) outcome.textContent = did;
    hint.textContent = text(values, 'townHint') ?? '';

    const shown = new Set<string>();
    for (const row of (text(values, 'townChoices') ?? '').split(';').filter(Boolean)) {
      const [key = '', label = '', detail = '', enabled = '', reason = '', kind = ''] = row.split('|');
      shown.add(key);
      let control = choiceControls.get(key);
      if (control === undefined) {
        control = button(''); control.className = 'town-choice'; control.dataset.choice = key;
        control.addEventListener('click', () => claim('choose', key));
        const item = element('li'); item.append(control); choices.append(item);
        choiceControls.set(key, control);
      }
      control.parentElement!.hidden = false; control.disabled = enabled !== '1'; control.dataset.kind = kind;
      if (control.dataset.row !== row) {
        control.dataset.row = row;
        control.replaceChildren(element('span', '', label), element('small', '', reason !== '' ? `— ${reason}` : detail));
      }
    }
    for (const [key, control] of choiceControls) control.parentElement!.hidden = !shown.has(key);

    renderedView = view;
    if (changed && !layers.menu) {
      if (selected !== '') { returnDoor = selected; (Array.from(choiceControls.values()).find(c => !c.disabled && !c.parentElement!.hidden) ?? back).focus({ preventScroll: true }); }
      else (doorControls.get(returnDoor) ?? doorControls.values().next().value)?.focus({ preventScroll: true });
    }
    if (document.activeElement instanceof HTMLButtonElement && document.activeElement.disabled && dialogue.contains(document.activeElement)) back.focus({ preventScroll: true });
  };

  const visible = (scope: Element): HTMLElement[] => Array.from(scope.querySelectorAll<HTMLElement>('button:not(:disabled)'))
    .filter(node => node.getClientRects().length > 0);
  const onKey = (event: KeyboardEvent): void => {
    if (layers.menu || event.repeat) return;
    const values = facts();
    if (!layers.town) {
      // Walking up to a town: Enter accepts its doorstep offer, unless a control has the keyboard.
      if (ENTER_KEYS.includes(event.code) && values !== null && (text(values, 'townPrompt') ?? '') !== '' && !layers.screen
        && !(document.activeElement instanceof HTMLButtonElement || document.activeElement instanceof HTMLInputElement || document.activeElement instanceof HTMLSelectElement)) {
        event.preventDefault(); claim('enter');
      }
      return;
    }
    const reading = values !== null && (text(values, 'townSelected') ?? '') !== '';
    // Esc closes a conversation; at the overview it is left to the pause menu.
    if (event.code === 'Escape' && reading) { event.preventDefault(); event.stopImmediatePropagation(); claim('back'); }
    else if (event.code === 'Tab') {
      const controls = visible(reading ? dialogue : scene);
      const at = controls.indexOf(document.activeElement as HTMLElement);
      if (controls.length > 0) { event.preventDefault(); controls[(at + (event.shiftKey ? -1 : 1) + controls.length) % controls.length]!.focus(); }
    } else if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.code)) {
      const scope = reading ? choices : doors;
      if (!scope.contains(document.activeElement)) return;
      const controls = visible(scope), at = controls.indexOf(document.activeElement as HTMLElement);
      event.preventDefault();
      controls[(at + (event.code === 'ArrowLeft' || event.code === 'ArrowUp' ? -1 : 1) + controls.length) % controls.length]!.focus();
    }
  };
  window.addEventListener('keydown', onKey, true);
  render();
  const unsubscribe = projection?.subscribe(render);
  return () => { unsubscribe?.(); window.removeEventListener('keydown', onKey, true); layers.town = false; root.remove(); };
}
