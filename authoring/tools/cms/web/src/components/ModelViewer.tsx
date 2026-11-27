import { useEffect, useRef, useState } from 'preact/hooks';
import type { ModelRef } from '../types';
import type { SceneHandle } from '../viewer/ModelScene';

/** A model in an orbit view; three.js loads as its own chunk the first time one opens. */
export function ModelViewer({ model }: { model: ModelRef }) {
  const host = useRef<HTMLDivElement>(null);
  const handle = useRef<SceneHandle | null>(null);
  const [clips, setClips] = useState<string[]>([]);
  const [clip, setClip] = useState<string>('');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setError(null);
    setClips([]);
    setClip('');
    void import('../viewer/ModelScene')
      .then(module => (host.current ? module.mountModel(host.current, model) : null))
      .then(scene => {
        if (!scene) return;
        if (cancelled) {
          scene.dispose();
          return;
        }
        handle.current = scene;
        setClips(scene.clips);
      })
      .catch(err => setError(err instanceof Error ? err.message : String(err)));
    return () => {
      cancelled = true;
      handle.current?.dispose();
      handle.current = null;
    };
  }, [model.url]);

  return (
    <div class="model-viewer">
      <div class="model-canvas" ref={host} />
      {error ? <p class="empty">Could not load the model: {error}</p> : null}
      {clips.length ? (
        <label class="clip-picker">
          Clip{' '}
          <select
            value={clip}
            onChange={event => {
              const next = (event.currentTarget as HTMLSelectElement).value;
              setClip(next);
              handle.current?.play(next || null);
            }}
          >
            <option value="">Bind pose</option>
            {clips.map(name => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </select>
        </label>
      ) : null}
    </div>
  );
}
