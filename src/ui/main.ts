/**
 * Mounts static DOM guidance beside the Engine-owned canvas. This UI owns no
 * world facts, gameplay input, renderer resources, or application loop.
 */
export function mountProductUi(root: Element): Readonly<{ dispose(): void }> {
  const panel = document.createElement('aside');
  panel.setAttribute('aria-label', 'Rusty CraftSurvive controls');

  const title = document.createElement('h1');
  title.textContent = 'Rusty CraftSurvive';
  panel.append(title);

  const status = document.createElement('p');
  status.textContent = 'C# product migration shell. Terrain and player gameplay are not yet migrated.';
  panel.append(status);

  root.append(panel);
  return Object.freeze({ dispose: () => panel.remove() });
}
