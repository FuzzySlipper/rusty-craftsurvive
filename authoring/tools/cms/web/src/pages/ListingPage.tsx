import { useMemo, useState } from 'preact/hooks';
import { useApp } from '../state';
import { Empty, PageHeading, RecordTile } from '../components/ui';
import { useSessionState } from '../useSessionState';

const PAGE = 120;

export function ListingPage({ category }: { category: string }) {
  const { catalog, reviewFor, clearMarks } = useApp();
  const [kind, setKind] = useSessionState(`kind:${category}`, '');
  const [text, setText] = useSessionState(`text:${category}`, '');
  const [markedOnly, setMarkedOnly] = useSessionState(`marked:${category}`, false);
  const [missingOnly, setMissingOnly] = useSessionState(`missing:${category}`, false);
  const [shown, setShown] = useState(PAGE);
  const info = catalog?.categories.find(item => item.id === category);
  const records = useMemo(() => (catalog?.records ?? []).filter(record => record.category === category), [catalog, category]);
  const kinds = useMemo(() => [...new Set(records.map(record => record.kind))].sort(), [records]);
  if (!catalog) return null;
  if (!info) return <Empty>There is no {category} category.</Empty>;
  const needle = text.trim().toLowerCase();
  const filtered = records.filter(
    record =>
      (!kind || record.kind === kind) &&
      (!needle || `${record.id} ${record.name} ${record.summary}`.toLowerCase().includes(needle)) &&
      (!markedOnly || reviewFor(record.category, record.id).marked) &&
      (!missingOnly || record.unprovenanced > 0),
  );
  const markedCount = records.filter(record => reviewFor(record.category, record.id).marked).length;
  return (
    <>
      <PageHeading title={info.label} intro={info.about} />
      <form class="filters" onSubmit={event => event.preventDefault()}>
        <label>
          Kind
          <select value={kind} onChange={event => setKind((event.currentTarget as HTMLSelectElement).value)}>
            <option value="">All ({records.length})</option>
            {kinds.map(item => (
              <option key={item} value={item}>
                {item} ({records.filter(record => record.kind === item).length})
              </option>
            ))}
          </select>
        </label>
        <label>
          Filter
          <input value={text} placeholder="name, id, summary" onInput={event => setText((event.currentTarget as HTMLInputElement).value)} />
        </label>
        <label class="mark-filter">
          <input type="checkbox" checked={markedOnly} onChange={event => setMarkedOnly((event.currentTarget as HTMLInputElement).checked)} />
          Marked only
        </label>
        <label class="mark-filter">
          <input type="checkbox" checked={missingOnly} onChange={event => setMissingOnly((event.currentTarget as HTMLInputElement).checked)} />
          Missing provenance
        </label>
        <output>{filtered.length} shown</output>
      </form>
      {markedCount ? (
        <p class="mark-bar">
          {markedCount} marked{' '}
          <button type="button" onClick={() => void clearMarks(category)}>
            Clear all marks
          </button>
        </p>
      ) : null}
      {filtered.length ? (
        <div class="tiles">
          {filtered.slice(0, shown).map(record => (
            <RecordTile record={record} key={record.id} />
          ))}
        </div>
      ) : (
        <Empty>No records match.</Empty>
      )}
      {filtered.length > shown ? (
        <p>
          <button type="button" onClick={() => setShown(shown + PAGE)}>
            Show more ({filtered.length - shown} left)
          </button>
        </p>
      ) : null}
    </>
  );
}
