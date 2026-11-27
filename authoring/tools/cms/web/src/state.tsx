import { createContext, type ComponentChildren } from 'preact';
import { useCallback, useContext, useEffect, useState } from 'preact/hooks';
import { apiRequest } from './api';
import type { CatalogResponse, RecordCard, ReviewPatch, ReviewRow } from './types';

const entryKey = (category: string, id: string): string => JSON.stringify([category, id]);

export interface AppState {
  catalog: CatalogResponse | null;
  loading: boolean;
  error: string | null;
  refreshCatalog: () => Promise<void>;
  card: (id: string) => RecordCard | undefined;
  reviewFor: (category: string, id: string) => ReviewRow;
  reviews: ReviewRow[];
  patchReview: (category: string, id: string, change: ReviewPatch) => Promise<ReviewRow>;
  clearMarks: (category: string) => Promise<number>;
}

const AppContext = createContext<AppState | null>(null);

export function AppProvider({ children }: { children: ComponentChildren }) {
  const [catalog, setCatalog] = useState<CatalogResponse | null>(null);
  const [cards, setCards] = useState<Map<string, RecordCard>>(() => new Map());
  const [reviews, setReviews] = useState<Map<string, ReviewRow>>(() => new Map());
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const refreshCatalog = useCallback(async () => {
    const [data, rows] = await Promise.all([
      apiRequest<CatalogResponse>('/api/catalog'),
      apiRequest<ReviewRow[]>('/api/reviews'),
    ]);
    setCatalog(data);
    setCards(new Map(data.records.map(record => [record.id, record])));
    setReviews(new Map(rows.map(row => [entryKey(row.category, row.id), row])));
    setError(null);
  }, []);

  useEffect(() => {
    refreshCatalog()
      .catch(err => setError(err instanceof Error ? err.message : String(err)))
      .finally(() => setLoading(false));
  }, [refreshCatalog]);

  const reviewFor = useCallback(
    (category: string, id: string): ReviewRow =>
      reviews.get(entryKey(category, id)) ?? { category, id, marked: false, rating: null, note: '', revision: null, updated: null },
    [reviews],
  );

  const patchReview = useCallback(async (category: string, id: string, change: ReviewPatch) => {
    const row = await apiRequest<ReviewRow>(`/api/reviews/${encodeURIComponent(category)}/${encodeURIComponent(id)}`, {
      method: 'POST',
      body: change,
    });
    setReviews(current => new Map(current).set(entryKey(category, id), row));
    return row;
  }, []);

  const clearMarks = useCallback(async (category: string) => {
    const result = await apiRequest<{ cleared: number }>(`/api/marks/${encodeURIComponent(category)}/clear`, { method: 'POST' });
    setReviews(current => {
      const next = new Map(current);
      for (const [key, row] of next) if (row.category === category) next.set(key, { ...row, marked: false });
      return next;
    });
    return result.cleared;
  }, []);

  const value: AppState = {
    catalog,
    loading,
    error,
    refreshCatalog,
    card: id => cards.get(id),
    reviewFor,
    reviews: [...reviews.values()],
    patchReview,
    clearMarks,
  };
  return <AppContext.Provider value={value}>{children}</AppContext.Provider>;
}

export function useApp(): AppState {
  const state = useContext(AppContext);
  if (!state) throw new Error('useApp outside AppProvider');
  return state;
}
