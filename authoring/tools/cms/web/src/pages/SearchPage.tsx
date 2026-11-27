import { useApp } from '../state';
import { Empty, PageHeading, RecordTile } from '../components/ui';

export function SearchPage({ term }: { term: string }) {
  const { catalog } = useApp();
  if (!catalog) return null;
  const words = term.toLowerCase().split(/\s+/).filter(Boolean);
  const found = catalog.records.filter(record => {
    const text = `${record.id} ${record.name} ${record.kind} ${record.summary} ${record.category}`.toLowerCase();
    return words.every(word => text.includes(word));
  });
  return (
    <>
      <PageHeading title={`Search: ${term}`} intro={`${found.length} records`} />
      {found.length ? (
        <div class="tiles">
          {found.map(record => (
            <RecordTile record={record} key={record.id} />
          ))}
        </div>
      ) : (
        <Empty>Nothing matches.</Empty>
      )}
    </>
  );
}
