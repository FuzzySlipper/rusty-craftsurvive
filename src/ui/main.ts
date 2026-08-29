/**
 * Mounts static DOM guidance beside the Engine-owned canvas. This UI owns no
 * world facts, gameplay input, renderer resources, or application loop.
 */
export function mountProductUi(root: Element): Readonly<{ dispose(): void }> {
  const panel = document.createElement('aside');
  panel.setAttribute('aria-label', 'Rusty CraftSurvive status');

  const title = document.createElement('h1');
  title.textContent = 'Rusty CraftSurvive';
  panel.append(title);

  const status = document.createElement('p');
  status.textContent = 'C# product host active. Terrain and first-person gameplay run in the product lane.';
  panel.append(status);

  root.append(panel);
  return Object.freeze({ dispose: () => panel.remove() });
}
