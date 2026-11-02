import type { RustyApplicationUiPort } from '@rusty-engine/product-ui';
import { layers } from './layers.js';

/**
 * The one owner of interface versus gameplay (after rusty-dagger's `interaction.ts`).
 *
 * The Engine owns pointer lock and input; the page tells it, through `setInteractionMode`, whether
 * an open surface needs the cursor: the pack or journal, the casting ring, the pause menu or the
 * developer tools (`layers`). Leaving the last of them returns to play and takes gameplay focus back
 * at once, so closing one re-takes the pointer without a click. The world map and the town are not
 * among them: they are free-cursor scenes whose clicks reach the product as positions, so they stay
 * in gameplay with an unlocked cursor (`world.ts`).
 *
 * A pointer lock lost while the world has it (Escape, which the browser and the desktop shell spend
 * on releasing the lock, or focus loss) is the player asking for the menu.
 */
export interface Interaction {
  /** Re-decide the mode now; `focus` asks for gameplay focus even if the mode did not change. */
  sync(focus?: boolean): void;
  mode(): 'interface' | 'gameplay' | null;
  dispose(): void;
}

export interface InteractionOptions {
  /** A lock the world held was lost during play, with no surface open. */
  readonly onLockLostInPlay?: () => void;
}

/** Whether an open surface needs the cursor. */
export const needsCursor = (): boolean => layers.screen || layers.ring || layers.menu || layers.developer;

export function mountInteraction(ui: RustyApplicationUiPort, options: InteractionOptions = {}): Interaction {
  let mode: 'interface' | 'gameplay' | null = null;

  const sync = (focus = false): void => {
    const next = needsCursor() ? 'interface' : 'gameplay';
    const resuming = next === 'gameplay' && mode === 'interface';
    if (next !== mode) { mode = next; ui.setInteractionMode(next); }
    if (next === 'gameplay' && (focus || resuming)) ui.focusGameplay();
  };

  let pointerWasLocked = document.pointerLockElement !== null;
  const onPointerLockChange = (): void => {
    const locked = document.pointerLockElement !== null;
    // A surface opening releases the lock as it enters interface mode, after its flag is set: a lock
    // lost to a surface that already needs the cursor is not the player asking for the menu.
    if (pointerWasLocked && !locked && mode === 'gameplay' && !needsCursor()) options.onLockLostInPlay?.();
    pointerWasLocked = locked;
  };
  document.addEventListener('pointerlockchange', onPointerLockChange);
  layers.changed = sync;
  sync();

  return {
    sync,
    mode: () => mode,
    dispose() {
      document.removeEventListener('pointerlockchange', onPointerLockChange);
      layers.changed = () => {};
    },
  };
}
