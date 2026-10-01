import type { RustyApplicationUiProjectionView } from '@rusty-engine/product-ui';
import { element } from './dom.js';
import { number, projectionValues, text } from './hud.js';

/**
 * The loading screen: while the product reports a dungeon loading, a dark overlay with its progress
 * covers the view. It shows only what the projection says and decides nothing.
 */
export function mountLoading(root: Element, projection: RustyApplicationUiProjectionView | undefined): () => void {
  const overlay = element('div', 'position:fixed;inset:0;background:#07080b;color:#d9e2ee;display:none;flex-direction:column;'
    + 'align-items:center;justify-content:center;gap:.8rem;font:1rem/1.3 ui-monospace,SFMono-Regular,Menlo,monospace;z-index:10;pointer-events:none;');
  const caption = element('p', 'margin:0;letter-spacing:.08em;', 'Descending');
  const track = element('div', 'width:min(22rem,70vw);height:.35rem;background:#1b2230;border-radius:.2rem;overflow:hidden;');
  const bar = element('div', 'height:100%;width:0;background:#c8a46a;transition:width .1s linear;');
  track.append(bar);
  overlay.append(caption, track);
  root.append(overlay);
  if (projection === undefined) return () => overlay.remove();

  const show = (values: ReturnType<typeof projectionValues>): void => {
    const loading = values !== null && text(values, 'dungeonState') === 'loading';
    // The overlay's own layout sets display, so it is shown and hidden by display, not by `hidden`.
    overlay.style.display = loading ? 'flex' : 'none';
    if (loading) {
      caption.textContent = text(values, 'dungeonPrompt') || 'Descending';
      bar.style.width = `${Math.round((number(values, 'dungeonProgress') ?? 0) * 100)}%`;
    }
  };
  show(projectionValues(projection.current()));
  const unsubscribe = projection.subscribe((envelope) => show(projectionValues(envelope)));
  return () => { unsubscribe(); overlay.remove(); };
}
