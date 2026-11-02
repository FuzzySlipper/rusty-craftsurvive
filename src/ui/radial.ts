import type { RustyApplicationUiIntentsPort, RustyApplicationUiProjectionView, RustyApplicationUiPort } from '@rusty-engine/product-ui';
import { ACTION_CONTRACT, ACTION_INTENT } from './actions.js';
import { element } from './dom.js';
import { projectionValues, text, type Values } from './hud.js';
import { layers } from './layers.js';

/** The key that opens and closes the ring; C (gameplay) casts what it composed. Escape closes it too. */
const RING_KEY = 'KeyV';
const CLOSE_KEY = 'Escape';

/**
 * The trigrams round the ring in the Earlier Heaven (Fu Xi) arrangement, clockwise from the top, so each faces its
 * opposite: heaven and earth, wind and thunder, water and fire, mountain and lake. Presentation only: what each does
 * is the grammar's, in C#.
 */
const EARLIER_HEAVEN = ['heaven', 'wind', 'water', 'mountain', 'earth', 'thunder', 'fire', 'lake'] as const;

/** The ring's size in CSS pixels: its radius to the trigrams' centres, each trigram's button, and the drawing's side. */
const RING_RADIUS = 150;
const BUTTON_SIZE = 84;
const SIDE = 2 * RING_RADIUS + BUTTON_SIZE + 24;

const INK = '#edf5ff';
const OUTER_COLOUR = '#e0a040';
const INNER_COLOUR = '#7fc8ff';

interface Trigram { readonly id: string; readonly glyph: string; readonly name: string }
interface Hexagram { readonly number: string; readonly upper: string; readonly lower: string; readonly name: string }

const trigramsOf = (values: Values): Map<string, Trigram> => new Map((text(values, 'castTrigrams') ?? '').split(';')
  .filter((entry) => entry.length > 0).map((entry) => {
    const [id = '', glyph = '', name = ''] = entry.split('|');
    return [id, { id, glyph, name }] as const;
  }));

const hexagramsOf = (values: Values): Hexagram[] => (text(values, 'castHexagrams') ?? '').split(';')
  .filter((entry) => entry.length > 0).map((entry) => {
    const [number = '', upper = '', lower = '', name = ''] = entry.split('|');
    return { number, upper, lower, name };
  });

/** Where a ring position's centre sits, from the drawing's top-left corner. */
const spot = (index: number): { x: number; y: number } => {
  const angle = (index / EARLIER_HEAVEN.length) * 2 * Math.PI - Math.PI / 2;
  return { x: SIDE / 2 + RING_RADIUS * Math.cos(angle), y: SIDE / 2 + RING_RADIUS * Math.sin(angle) };
};

/**
 * The casting ring (#10529): V opens it over the world and frees the pointer; the first trigram chosen is the outer
 * one (how the formula reaches the world), the second the inner (what acts), and a trail runs from the centre through
 * both. The second choice composes the hexagram in C# and returns to play, where C casts it. Moving over the ring
 * previews the hexagram the pointer would make. Escape or V closes it without composing.
 */
export function mountCastingRing(root: Element, ui: RustyApplicationUiPort | undefined, intents: RustyApplicationUiIntentsPort | undefined,
  projection: RustyApplicationUiProjectionView | undefined): () => void {
  const overlay = element('div', 'position:fixed;inset:0;display:none;align-items:center;justify-content:center;z-index:30;'
    + 'background:radial-gradient(circle, rgb(8 10 14 / 55%) 0%, rgb(8 10 14 / 20%) 60%, rgb(8 10 14 / 0%) 75%);pointer-events:auto;');
  overlay.setAttribute('data-rusty-ui-interactive', '');
  const ring = element('div', `position:relative;width:${SIDE}px;height:${SIDE}px;`);
  // The trail: an SVG drawn from its attributes (the window draws an inline SVG from its markup), never pointed at.
  const svgNs = 'http://www.w3.org/2000/svg';
  const trail = document.createElementNS(svgNs, 'svg');
  trail.setAttribute('width', String(SIDE));
  trail.setAttribute('height', String(SIDE));
  trail.setAttribute('style', 'position:absolute;left:0;top:0;pointer-events:none;');
  const circle = document.createElementNS(svgNs, 'circle');
  circle.setAttribute('cx', String(SIDE / 2));
  circle.setAttribute('cy', String(SIDE / 2));
  circle.setAttribute('r', String(RING_RADIUS));
  circle.setAttribute('fill', 'none');
  circle.setAttribute('stroke', 'rgb(237 245 255 / 25%)');
  circle.setAttribute('stroke-width', '2');
  const path = document.createElementNS(svgNs, 'polyline');
  path.setAttribute('fill', 'none');
  path.setAttribute('stroke', OUTER_COLOUR);
  path.setAttribute('stroke-width', '5');
  path.setAttribute('stroke-linecap', 'round');
  path.setAttribute('stroke-linejoin', 'round');
  const reach = document.createElementNS(svgNs, 'line');
  reach.setAttribute('stroke', INNER_COLOUR);
  reach.setAttribute('stroke-width', '3');
  reach.setAttribute('stroke-dasharray', '6 6');
  trail.append(circle, path, reach);

  const centre = element('div', `position:absolute;left:${SIDE / 2 - 95}px;top:${SIDE / 2 - 60}px;width:190px;height:120px;`
    + `display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;color:${INK};`
    + 'font:600 .85rem/1.25 system-ui,sans-serif;pointer-events:none;');
  const stage = element('div', 'opacity:.75;font-weight:400;font-size:.75rem;text-transform:uppercase;letter-spacing:.08em;');
  const glyphs = element('div', 'font-size:2.2rem;line-height:1.05;');
  const outerGlyph = element('div', '');
  const innerGlyph = element('div', `color:${INNER_COLOUR};`);
  glyphs.append(outerGlyph, innerGlyph);
  const name = element('div', '');
  centre.append(stage, glyphs, name);
  ring.append(trail, centre);
  overlay.append(ring);
  root.append(overlay);

  const buttons = EARLIER_HEAVEN.map((id, index) => {
    const at = spot(index);
    const button = element('button', `position:absolute;left:${at.x - BUTTON_SIZE / 2}px;top:${at.y - BUTTON_SIZE / 2}px;`
      + `width:${BUTTON_SIZE}px;height:${BUTTON_SIZE}px;border-radius:50%;border:2px solid #62748a;background:rgb(15 19 25 / 88%);`
      + `color:${INK};cursor:pointer;display:flex;flex-direction:column;align-items:center;justify-content:center;padding:0;`
      + 'font:600 .7rem/1.1 system-ui,sans-serif;');
    button.type = 'button';
    button.dataset['trigram'] = id;
    // Built once and only relabelled: a press on a node replaced under the pointer never becomes a click.
    const glyph = element('span', 'font-size:1.9rem;line-height:1;pointer-events:none;');
    const label = element('span', 'pointer-events:none;');
    const plain = element('span', 'opacity:.7;font-weight:400;pointer-events:none;', id);
    button.append(glyph, label, plain);
    ring.append(button);
    return { button, glyph, label };
  });

  let open = false;
  let outer: string | null = null;
  let hovered: string | null = null;
  let trigrams = new Map<string, Trigram>();
  let hexagrams: Hexagram[] = [];

  const claim = (data: Readonly<Record<string, string>>): void => {
    intents?.claim(ACTION_INTENT, { kind: 'product-payload', contract: ACTION_CONTRACT, data });
  };
  const indexOf = (id: string): number => EARLIER_HEAVEN.indexOf(id as typeof EARLIER_HEAVEN[number]);
  const hexagram = (upper: string, lower: string): Hexagram | undefined => hexagrams.find((entry) => entry.upper === upper && entry.lower === lower);

  const draw = (): void => {
    buttons.forEach(({ button, glyph, label }, index) => {
      const id = EARLIER_HEAVEN[index]!;
      const trigram = trigrams.get(id);
      glyph.textContent = trigram?.glyph ?? '?';
      label.textContent = trigram?.name ?? id;
      const chosen = id === outer;
      const lit = id === hovered;
      button.style.borderColor = chosen ? OUTER_COLOUR : lit ? (outer === null ? OUTER_COLOUR : INNER_COLOUR) : '#62748a';
      button.style.background = chosen ? 'rgb(224 160 64 / 35%)' : lit ? 'rgb(40 52 68 / 95%)' : 'rgb(15 19 25 / 88%)';
    });
    const middle = `${SIDE / 2},${SIDE / 2}`;
    const first = outer === null ? null : spot(indexOf(outer));
    path.setAttribute('points', first === null ? `${middle} ${middle}` : `${middle} ${first.x},${first.y}`);
    const second = hovered !== null && outer !== null ? spot(indexOf(hovered)) : null;
    reach.setAttribute('display', first !== null && second !== null ? 'inline' : 'none');
    if (first !== null && second !== null) {
      reach.setAttribute('x1', String(first.x)); reach.setAttribute('y1', String(first.y));
      reach.setAttribute('x2', String(second.x)); reach.setAttribute('y2', String(second.y));
    }

    if (outer === null) {
      stage.textContent = 'Outer · how it reaches';
      const look = hovered === null ? undefined : trigrams.get(hovered);
      outerGlyph.textContent = look?.glyph ?? '';
      innerGlyph.textContent = '';
      name.textContent = look === undefined ? 'Choose the outer trigram' : look.name;
    } else {
      stage.textContent = 'Inner · what acts';
      const upper = trigrams.get(outer);
      const lower = hovered === null ? undefined : trigrams.get(hovered);
      outerGlyph.textContent = upper?.glyph ?? '';
      innerGlyph.textContent = lower?.glyph ?? '·';
      const made = hovered === null ? undefined : hexagram(outer, hovered);
      name.textContent = made === undefined ? 'Choose the inner trigram' : `${made.number} ${made.name}`;
    }
  };

  const close = (backToPlay: boolean): void => {
    if (!open) return;
    open = false;
    layers.screen = false;
    overlay.style.display = 'none';
    // Returning from a click, which is a gesture the window and browsers take the pointer back on.
    if (backToPlay) ui?.focusGameplay();
  };

  const show = (): void => {
    open = true;
    outer = null;
    hovered = null;
    // Marked as a screen, so the pause menu does not take the pointer's release as Escape.
    layers.screen = true;
    if (document.pointerLockElement !== null) document.exitPointerLock();
    overlay.style.display = 'flex';
    draw();
  };

  const choose = (id: string): void => {
    if (outer === null) {
      outer = id;
      hovered = null;
      draw();
      return;
    }

    claim({ action: 'compose', upper: outer, lower: id });
    close(true);
  };

  buttons.forEach(({ button }) => {
    const id = button.dataset['trigram']!;
    button.addEventListener('pointerenter', () => { hovered = id; draw(); });
    button.addEventListener('pointerleave', () => { if (hovered === id) { hovered = null; draw(); } });
    button.addEventListener('click', (event) => { event.stopPropagation(); choose(id); });
  });
  // A click off the ring takes back the first choice, or closes it.
  overlay.addEventListener('click', () => { if (outer !== null) { outer = null; draw(); } else close(true); });
  for (const type of ['pointerdown', 'pointerup', 'mousedown', 'mouseup', 'wheel']) overlay.addEventListener(type, (event) => event.stopPropagation());

  const onKey = (event: KeyboardEvent): void => {
    if (event.repeat || layers.menu || layers.town) return;
    if (event.code === RING_KEY && (open || !layers.screen)) {
      event.preventDefault();
      if (open) close(false); else show();
    } else if (event.code === CLOSE_KEY && open) {
      // Escape closed the ring, so the pause menu does not open on the same press.
      event.preventDefault();
      close(false);
    }
  };
  window.addEventListener('keydown', onKey, true);

  // The names change only with the grammar, so the ring redraws when they do, not on every projection.
  let published = '';
  const read = (values: Values | null): void => {
    if (values === null) return;
    const now = `${text(values, 'castTrigrams') ?? ''}/${text(values, 'castHexagrams') ?? ''}`;
    if (now === published) return;
    published = now;
    trigrams = trigramsOf(values);
    hexagrams = hexagramsOf(values);
    if (open) draw();
  };
  read(projectionValues(projection?.current() ?? null));
  const unsubscribe = projection?.subscribe((envelope) => read(projectionValues(envelope)));
  if (intents === undefined) buttons.forEach(({ button }) => { button.disabled = true; });

  return () => {
    unsubscribe?.();
    window.removeEventListener('keydown', onKey, true);
    if (open) layers.screen = false;
    overlay.remove();
  };
}
