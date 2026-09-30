/** Small DOM helpers shared by the panels. */

export function button(text: string): HTMLButtonElement {
  const value = document.createElement('button');
  value.type = 'button';
  value.textContent = text;
  return value;
}

export function element<K extends keyof HTMLElementTagNameMap>(tag: K, css = '', text = ''): HTMLElementTagNameMap[K] {
  const value = document.createElement(tag);
  if (css.length > 0) value.style.cssText = css;
  if (text.length > 0) value.textContent = text;
  return value;
}

/** Keeps pointer and keyboard events on the panel from reaching the game canvas beneath it. */
export function isolateEvents(panel: HTMLElement): void {
  const stop = (event: Event): void => event.stopPropagation();
  for (const type of ['pointerdown', 'pointermove', 'pointerup', 'pointercancel', 'mousedown', 'mousemove', 'mouseup', 'wheel', 'keydown', 'keyup', 'click']) {
    panel.addEventListener(type, stop);
  }
}

export function message(error: unknown, fallback: string): string {
  return error instanceof Error && error.message.length > 0 ? error.message : fallback;
}
