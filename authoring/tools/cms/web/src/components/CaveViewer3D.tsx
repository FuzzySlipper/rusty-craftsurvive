import { useEffect, useRef, useState } from 'preact/hooks';
import type { CaveView } from '../types';
import type { CaveLayer, CaveSceneHandle } from '../viewer/CaveScene';

const STATES: Array<[number, string, string]> = [
  [5, 'Stands', '#60ba78'],
  [4, 'Crawls', '#dea84c'],
  [3, 'Tight', '#d67856'],
  [1, 'Beyond a pinch', '#c44840'],
  [2, 'Cut off', '#a85ccc'],
];
const LAYERS: Array<[CaveLayer, string]> = [
  ['route', 'Main route'],
  ['tendril', 'Way in'],
  ['markers', 'Features'],
];

/** The cave in 3D: three.js loads as its own chunk the first time a cave is opened. */
export function CaveViewer3D({ view }: { view: CaveView }) {
  const host = useRef<HTMLDivElement>(null);
  const handle = useRef<CaveSceneHandle | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [states, setStates] = useState<Record<number, boolean>>({ 1: true, 2: true, 3: true, 4: true, 5: true });
  const [layers, setLayers] = useState<Record<CaveLayer, boolean>>({ route: true, tendril: true, markers: true });
  const height = view.cells.size[1] * view.cells.metres;
  const width = view.cells.size[0] * view.cells.metres;
  const [ceiling, setCeiling] = useState(height);
  const [slice, setSlice] = useState(width);

  useEffect(() => {
    let cancelled = false;
    void import('../viewer/CaveScene')
      .then(module => (host.current ? module.mountCave(host.current, view) : null))
      .then(scene => {
        if (!scene) return;
        if (cancelled) {
          scene.dispose();
          return;
        }
        handle.current = scene;
      })
      .catch(err => setError(err instanceof Error ? err.message : String(err)));
    return () => {
      cancelled = true;
      handle.current?.dispose();
      handle.current = null;
    };
  }, [view.id]);

  useEffect(() => handle.current?.setStates(states), [states]);
  useEffect(() => handle.current?.setLayers(layers), [layers]);
  useEffect(() => handle.current?.setCut(ceiling, slice < width ? slice : null), [ceiling, slice]);

  return (
    <div class="cave-3d">
      <div class="cave-3d-controls">
        {STATES.map(([kind, label, colour]) => (
          <label class="toggle" key={kind}>
            <input type="checkbox" checked={states[kind]} onChange={event => setStates({ ...states, [kind]: (event.currentTarget as HTMLInputElement).checked })} />
            <i style={{ background: colour }} /> {label}
          </label>
        ))}
        {LAYERS.map(([key, label]) => (
          <label class="toggle" key={key}>
            <input type="checkbox" checked={layers[key]} onChange={event => setLayers({ ...layers, [key]: (event.currentTarget as HTMLInputElement).checked })} />
            {label}
          </label>
        ))}
        <label>
          Cut away above {ceiling.toFixed(0)} m
          <input type="range" min={0} max={height} step={1} value={ceiling} onInput={event => setCeiling(Number((event.currentTarget as HTMLInputElement).value))} />
        </label>
        <label>
          Slice at x {slice >= width ? 'off' : `${slice.toFixed(0)} m`}
          <input type="range" min={0} max={width} step={1} value={slice} onInput={event => setSlice(Number((event.currentTarget as HTMLInputElement).value))} />
        </label>
      </div>
      <div class="cave-3d-canvas" ref={host} />
      {error ? <p class="empty">Could not draw the cave: {error}</p> : null}
      <small class="muted">Drag to orbit, wheel to zoom, right-drag to pan. Each block is a metre of open space beside rock, in its reach colour.</small>
    </div>
  );
}
