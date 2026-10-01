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
  { label: 'Light', title: 'Place a light on the face you are aiming at; it burns a torch', data: { action: 'light' } },
  { label: 'Chest', title: 'Place a container on the face you are aiming at', data: { action: 'container' } },
  { label: 'Undo', title: 'Take back the last floor or wall', data: { action: 'undo' } },
];

/** One option of a picker: the value the request carries, what it reads as, and whether it can be chosen. */
interface PickerOption {
  readonly value: string;
  readonly label: string;
  readonly enabled: boolean;
}

/**
 * Claims the product's action intent; the outcome comes back through the projection, not here. The
 * block, recipe and item pickers offer what the product publishes - its build palette, its recipes
 * and what can be used - so the UI names what the product chose and never its ids.
 */
export function mountActions(host: HTMLElement, intents: RustyApplicationUiIntentsPort | undefined,
  projection: RustyApplicationUiProjectionView | undefined): () => void {
  const claim = (data: Readonly<Record<string, string | number>>): void => {
    intents?.claim(ACTION_INTENT, { kind: 'product-payload', contract: ACTION_CONTRACT, data });
  };

  const bar = element('div', 'display:flex;flex-wrap:wrap;gap:.25rem;margin-top:.35rem;');
  const block = element('select');
  block.title = 'The block floors and walls are built from';
  block.disabled = true;
  bar.append(block);
  for (const control of CONTROLS) {
    const press = button(control.label);
    press.title = control.title;
    press.disabled = intents === undefined;
    press.addEventListener('click', () => {
      claim(control.built === true && block.value !== '' ? { ...control.data, material: block.value } : control.data);
    });
    bar.append(press);
  }

  // Crafting and use: the product publishes the recipes and the usable items; the UI offers them.
  const kit = element('div', 'display:flex;flex-wrap:wrap;gap:.25rem;margin-top:.25rem;');
  const recipe = element('select');
  recipe.title = 'A recipe: what it makes, from what';
  const craft = button('Craft');
  craft.title = 'Craft the chosen recipe from what you carry';
  const item = element('select');
  item.title = 'Something you carry that can be eaten or applied';
  const use = button('Use');
  use.title = 'Eat or apply the chosen item';
  for (const control of [recipe, craft, item, use]) control.disabled = true;
  craft.addEventListener('click', () => { if (recipe.value !== '') claim({ action: 'craft', recipe: recipe.value }); });
  use.addEventListener('click', () => { if (item.value !== '') claim({ action: 'use', item: item.value }); });
  recipe.addEventListener('change', () => { craft.disabled = recipe.selectedOptions[0]?.disabled !== false; });
  const rest = button('Rest');
  rest.title = 'Sleep until morning: only at night, with no hostile creature near';
  rest.disabled = intents === undefined;
  rest.addEventListener('click', () => claim({ action: 'rest' }));
  const difficulty = element('select');
  difficulty.title = 'How hard the world is: hunger, air and recovery';
  difficulty.disabled = true;
  difficulty.addEventListener('change', () => { if (difficulty.value !== '') claim({ action: 'difficulty', level: difficulty.value }); });
  const enter = button('Enter');
  enter.title = 'Go down into the dungeon at this entrance';
  enter.disabled = true;
  enter.addEventListener('click', () => claim({ action: 'enter' }));
  const leave = button('Leave');
  leave.title = 'Climb out of the dungeon from its way out';
  leave.disabled = true;
  leave.addEventListener('click', () => claim({ action: 'leave' }));
  kit.append(recipe, craft, item, use, rest, difficulty, enter, leave);
  host.append(bar, kit);
  if (projection === undefined) return () => {};

  /** Replaces a picker's options, keeping the chosen one while it can still be chosen. */
  const fill = (picker: HTMLSelectElement, options: readonly PickerOption[]): void => {
    const chosen = picker.value;
    picker.replaceChildren(...options.map((entry) => {
      const option = element('option', '', entry.label);
      option.value = entry.value;
      option.disabled = !entry.enabled;
      return option;
    }));
    const keep = options.find((entry) => entry.value === chosen && entry.enabled) ?? options.find((entry) => entry.enabled);
    if (keep !== undefined) picker.value = keep.value;
    picker.disabled = options.length === 0 || intents === undefined;
  };

  const published = { palette: '', recipes: '', usable: '', difficulty: '' };
  const read = (values: ReturnType<typeof projectionValues>): void => {
    if (values === null) return;
    const palette = text(values, 'buildPalette') ?? '';
    if (palette !== published.palette) {
      published.palette = palette;
      fill(block, palette.split(',').filter((name) => name.length > 0).map((name) => ({ value: name, label: name, enabled: true })));
    }
    const recipes = text(values, 'recipes') ?? '';
    if (recipes !== published.recipes) {
      published.recipes = recipes;
      fill(recipe, recipes.split(',').filter((entry) => entry.length > 0).map((entry) => {
        const [id = '', description = '', ready = '0'] = entry.split(':');
        return { value: id, label: description, enabled: ready === '1' };
      }));
      craft.disabled = recipe.disabled || recipe.selectedOptions[0]?.disabled !== false;
    }
    enter.disabled = intents === undefined || values['dungeonCanEnter'] !== 1;
    leave.disabled = intents === undefined || values['dungeonCanLeave'] !== 1;
    const difficulties = text(values, 'difficulties') ?? '';
    const current = text(values, 'difficulty') ?? '';
    if (`${difficulties}/${current}` !== published.difficulty) {
      published.difficulty = `${difficulties}/${current}`;
      fill(difficulty, difficulties.split(',').filter((name) => name.length > 0).map((name) => ({ value: name, label: name, enabled: true })));
      if (current !== '') difficulty.value = current;
    }
    const usable = text(values, 'usable') ?? '';
    if (usable !== published.usable) {
      published.usable = usable;
      fill(item, usable.split(',').filter((id) => id.length > 0).map((id) => ({ value: id, label: id, enabled: true })));
      use.disabled = item.disabled;
    }
  };
  read(projectionValues(projection.current()));
  return projection.subscribe((envelope) => read(projectionValues(envelope)));
}
