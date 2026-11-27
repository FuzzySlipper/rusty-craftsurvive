import { useEffect, useState } from 'preact/hooks';
import { useApp } from '../state';
import type { Rating, ReviewPatch, ReviewRow } from '../types';

const RATINGS: Rating[] = ['good', 'maybe', 'bad'];
const ratingLabel = (value: Rating): string => value[0].toUpperCase() + value.slice(1);

export function ReviewBadge({ row }: { row: ReviewRow }) {
  if (row.rating) {
    return <span class={`chip review-${row.rating}`}>{ratingLabel(row.rating)}</span>;
  }
  if (row.previousRating) {
    return <span class="chip needs-check">Needs re-review</span>;
  }
  return null;
}

export function TileReview({ category, id }: { category: string; id: string }) {
  const { reviewFor, patchReview } = useApp();
  const row = reviewFor(category, id);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState('');

  async function toggleMark() {
    setBusy(true);
    setStatus('');
    try {
      await patchReview(category, id, { marked: !row.marked });
    } catch (error) {
      setStatus(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  }

  const hasNote = Boolean(row.note);

  return (
    <div class="tile-review" data-review-category={category} data-review-id={id}>
      <button type="button" data-mark aria-pressed={row.marked ? 'true' : 'false'} disabled={busy} onClick={toggleMark}>
        {row.marked ? 'Marked' : 'Mark'}
      </button>
      <span data-rating-badge>
        <ReviewBadge row={row} />
      </span>
      {hasNote ? <span class="muted">Note saved</span> : null}
      <output data-review-status role="status">
        {status}
      </output>
    </div>
  );
}

export function ReviewPanel({
  category,
  id,
  heading = 'Review and notes',
  revision = null,
}: {
  category: string;
  id: string;
  heading?: string;
  revision?: string | null;
}) {
  const { reviewFor, patchReview } = useApp();
  const row = reviewFor(category, id);
  const [note, setNote] = useState(row.note);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState('');

  useEffect(() => {
    setNote(row.note);
  }, [category, id, row.note]);

  async function save(change: ReviewPatch) {
    setBusy(true);
    setStatus('');
    const payload: ReviewPatch = { ...change };
    if (payload.rating && revision !== null) {
      payload.expectedRevision = revision;
    }
    try {
      await patchReview(category, id, payload);
      setStatus('Saved.');
    } catch (error) {
      setStatus(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  }

  function submitNote(event: Event) {
    event.preventDefault();
    void save({ note });
  }

  return (
    <section
      class="panel record-review"
      data-review-category={category}
      data-review-id={id}
      data-review-revision={revision ?? undefined}
    >
      <div class="section-heading">
        <h2>{heading}</h2>
        <button
          type="button"
          data-mark
          aria-pressed={row.marked ? 'true' : 'false'}
          disabled={busy}
          onClick={() => void save({ marked: !row.marked })}
        >
          {row.marked ? 'Marked' : 'Mark'}
        </button>
      </div>
      <div class="rating-bar" role="group" aria-label="Review rating">
        {RATINGS.map(rating => (
          <button
            key={rating}
            type="button"
            class={`review-${rating}`}
            data-rating={rating}
            aria-pressed={row.rating === rating ? 'true' : 'false'}
            disabled={busy}
            onClick={() => void save({ rating })}
          >
            {ratingLabel(rating)}
          </button>
        ))}
        <button
          type="button"
          data-rating=""
          disabled={busy || !(row.rating || row.previousRating)}
          onClick={() => void save({ rating: null })}
        >
          Clear rating
        </button>
        <span data-rating-badge>
          <ReviewBadge row={row} />
        </span>
      </div>
      <form data-note-form onSubmit={submitNote}>
        <label>
          Note
          <textarea
            name="note"
            rows={3}
            value={note}
            onInput={event => setNote((event.currentTarget as HTMLTextAreaElement).value)}
          />
        </label>
        <button type="submit" disabled={busy}>
          Save note
        </button>{' '}
        <output data-review-status role="status">
          {status}
        </output>
      </form>
    </section>
  );
}
