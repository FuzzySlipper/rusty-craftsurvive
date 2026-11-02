/**
 * Which of the UI's own surfaces is open - the pack or journal, the casting ring, the pause menu, the
 * town, the developer tools - so Escape goes to the innermost one and the interaction owner
 * (`interaction.ts`) knows whether a surface needs the cursor. A surface sets its flag with `open`;
 * nothing here is a game fact.
 */
export const layers = {
  screen: false, ring: false, menu: false, town: false, developer: false,
  /** Re-decides interface or gameplay; the interaction owner installs it. */
  changed: (_focus?: boolean): void => {},
};

type Surface = 'screen' | 'ring' | 'menu' | 'town' | 'developer';

/** Marks a surface open or closed and re-decides the interaction mode. */
export function open(surface: Surface, value: boolean): void {
  if (layers[surface] === value) return;
  layers[surface] = value;
  layers.changed();
}
