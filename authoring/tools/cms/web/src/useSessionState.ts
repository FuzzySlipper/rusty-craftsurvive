import { useEffect, useState } from 'preact/hooks';

/** State that survives a page change within the tab, keyed per view. */
export function useSessionState<T>(key: string, initial: T): [T, (value: T) => void] {
  const storageKey = `wandering-cms:${key}`;
  const [value, setValue] = useState<T>(() => {
    const stored = sessionStorage.getItem(storageKey);
    return stored === null ? initial : (JSON.parse(stored) as T);
  });
  useEffect(() => {
    const stored = sessionStorage.getItem(storageKey);
    setValue(stored === null ? initial : (JSON.parse(stored) as T));
  }, [storageKey]);
  return [
    value,
    next => {
      sessionStorage.setItem(storageKey, JSON.stringify(next));
      setValue(next);
    },
  ];
}
