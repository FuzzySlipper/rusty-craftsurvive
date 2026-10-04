import { element } from './dom.js';
import { number, text, type Values } from './hud.js';

/** What one occupied slot holds, from the product's <c>packItems</c>. */
export interface SlotItem { readonly slot: number; readonly id: string; readonly name: string; readonly count: number; readonly use: string }

/** The placeholder equipment slots: shown, but nothing can be put in them until equipment exists. */
export const EQUIPMENT = ['Head', 'Body', 'Hands', 'Feet', 'Main hand', 'Off hand'] as const;

/** A slot's edge, and the colours slots are drawn in. */
export const SLOT_PIXELS = 44;
const SLOT_FACE = '#1b1e26';
const SLOT_EDGE = '#000';
const SLOT_LIGHT = '#3a3f4c';
const SELECTED_EDGE = '#f2ead8';

/** The colour a slot's item is marked with, by what it is for. */
const USE_COLOURS: Readonly<Record<string, string>> = {
  food: '#c98a5a',
  healing: '#c8423a',
  light: '#d8b84a',
  material: '#9aa6b8',
};

/** How far the pointer moves before a press on a slot becomes a drag rather than a click. */
const DRAG_START_PIXELS = 4;

export const slotItems = (values: Values): Map<number, SlotItem> => new Map((text(values, 'packItems') ?? '').split(';')
  .filter((entry) => entry.length > 0)
  .map((entry) => {
    const [slot = '-1', id = '', name = '', count = '0', use = 'material'] = entry.split('|');
    return [Number(slot), { slot: Number(slot), id, name, count: Number(count), use }] as const;
  }));

export const hotbarSlots = (values: Values): number => number(values, 'hotbarSlots') ?? 0;
export const packSlots = (values: Values): number => number(values, 'packSlots') ?? 0;
export const selectedSlot = (values: Values): number => number(values, 'hotbarSelected') ?? 0;

/** What a drag asks the product for: a move between slots, or a use of what one holds. */
export interface SlotRequests {
  move(from: number, to: number, count: number): void;
  use(item: SlotItem): void;
  /** A click, not a drag, on a hotbar slot: select it. */
  select(slot: number): void;
  /** Something the UI itself refuses, such as a drop on an equipment slot that does not exist yet. */
  refuse(why: string): void;
}

/**
 * Slots the player drags stacks between. Each slot is drawn from what the product publishes; a
 * drag only claims a move, and the product decides what happens and publishes the slots again, so
 * the UI never holds where anything is. A press and drag takes the whole stack, Shift or the right
 * button half of it; a double click eats or applies what can be. The drag lives on the document,
 * so it carries on while the slots under it are redrawn.
 */
export class SlotDrag {
  private dragging: { from: SlotItem | null; slot: number; count: number; startX: number; startY: number; moved: boolean } | null = null;
  private readonly ghost: HTMLElement;

  constructor(private readonly root: Element, private readonly requests: SlotRequests) {
    this.ghost = element('div', `position:fixed;z-index:10;pointer-events:none;display:none;width:${SLOT_PIXELS}px;height:${SLOT_PIXELS}px;opacity:.85;`);
    root.append(this.ghost);
    document.addEventListener('pointermove', this.onMove, true);
    document.addEventListener('pointerup', this.onUp, true);
  }

  dispose(): void {
    document.removeEventListener('pointermove', this.onMove, true);
    document.removeEventListener('pointerup', this.onUp, true);
    this.ghost.remove();
  }

  /**
   * One slot, numbered as the product numbers it, holding what the product says it holds. A hotbar
   * slot can be clicked to select it, and the selected one is drawn with a light edge.
   */
  slot(index: number, item: SlotItem | undefined, hotbar = false, selected = false): HTMLElement {
    const cell = this.frame(selected);
    cell.dataset['slot'] = String(index);
    if (hotbar && item === undefined) {
      cell.addEventListener('pointerdown', (event) => {
        if (event.button !== 0) return;
        this.dragging = { from: null, slot: index, count: 0, startX: event.clientX, startY: event.clientY, moved: false };
      });
    }
    if (hotbar) cell.dataset['hotbar'] = '';
    if (item === undefined) return cell;
    cell.append(...face(item));
    cell.title = `${item.name} ×${item.count} - ${item.use}${item.use === 'food' ? ' (double-click to eat)' : item.use === 'healing' ? ' (double-click to apply)' : ''}`;
    cell.style.cursor = 'grab';
    cell.addEventListener('contextmenu', (event) => event.preventDefault());
    cell.addEventListener('pointerdown', (event) => {
      if (event.button !== 0 && event.button !== 2) return;
      event.preventDefault();
      const half = event.shiftKey || event.button === 2;
      this.dragging = { from: item, slot: index, count: half ? Math.ceil(item.count / 2) : 0, startX: event.clientX, startY: event.clientY, moved: false };
    });
    cell.addEventListener('dblclick', () => {
      if (item.use === 'food' || item.use === 'healing') this.requests.use(item);
    });
    return cell;
  }

  /** A placeholder equipment slot: it shows where equipment will go and refuses anything dropped on it. */
  equipment(name: string): HTMLElement {
    const cell = this.frame(false);
    cell.dataset['equipment'] = name;
    cell.title = `${name}: equipment is not in the game yet`;
    cell.style.opacity = '.55';
    cell.append(element('span', 'position:absolute;inset:0;display:grid;place-items:center;font-size:.55rem;text-align:center;line-height:1;opacity:.8;', name));
    return cell;
  }

  private frame(selected: boolean): HTMLElement {
    return element('div', `position:relative;width:${SLOT_PIXELS}px;height:${SLOT_PIXELS}px;box-sizing:border-box;background:${SLOT_FACE};`
      + `border:2px solid ${selected ? SELECTED_EDGE : SLOT_EDGE};box-shadow:inset 2px 2px 0 ${SLOT_LIGHT};user-select:none;touch-action:none;`);
  }

  private readonly onMove = (event: PointerEvent): void => {
    const drag = this.dragging;
    if (drag === null || drag.from === null) return;
    if (!drag.moved && Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY) < DRAG_START_PIXELS) return;
    if (!drag.moved) {
      drag.moved = true;
      const shown = drag.count > 0 ? { ...drag.from, count: drag.count } : drag.from;
      this.ghost.replaceChildren(...face(shown));
      this.ghost.style.display = 'block';
    }
    this.ghost.style.left = `${event.clientX - SLOT_PIXELS / 2}px`;
    this.ghost.style.top = `${event.clientY - SLOT_PIXELS / 2}px`;
  };

  private readonly onUp = (event: PointerEvent): void => {
    const drag = this.dragging;
    this.dragging = null;
    this.ghost.style.display = 'none';
    if (drag === null) return;
    if (!drag.moved) {
      // A click rather than a drag: on a hotbar slot, it selects that slot.
      const clicked = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-slot]');
      if (clicked?.dataset['hotbar'] !== undefined && Number(clicked.dataset['slot']) === drag.slot) this.requests.select(drag.slot);
      return;
    }
    if (drag.from === null) return;
    const under = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-slot],[data-equipment]');
    if (under === null || under === undefined) return;
    if (under.dataset['equipment'] !== undefined) {
      this.requests.refuse(`${under.dataset['equipment']}: equipment is not in the game yet`);
      return;
    }
    const to = Number(under.dataset['slot']);
    if (to !== drag.from.slot) this.requests.move(drag.from.slot, to, drag.count);
  };
}

/** What a slot shows of its item: a block of its use's colour, its initial, and its count. */
function face(item: SlotItem): HTMLElement[] {
  const colour = USE_COLOURS[item.use] ?? USE_COLOURS['material']!;
  return [
    element('span', `position:absolute;inset:7px;background:${colour};border:2px solid #000;box-shadow:inset -3px -3px 0 rgb(0 0 0 / 30%);`),
    element('span', 'position:absolute;left:0;right:0;top:9px;text-align:center;font:700 .85rem/1 ui-monospace,monospace;color:#0b0c10;', item.name.slice(0, 1)),
    element('span', 'position:absolute;right:3px;bottom:1px;font:700 .72rem/1 ui-monospace,monospace;color:#f2ead8;'
      + 'text-shadow:1px 1px 0 #000,-1px 1px 0 #000,1px -1px 0 #000,-1px -1px 0 #000;', item.count > 1 ? String(item.count) : ''),
  ];
}
