import { useEffect, useState } from 'preact/hooks';
import { apiRequest } from '../api';
import { recordUrl } from '../router';
import { useApp } from '../state';
import type { ProvenanceResponse } from '../types';
import { Empty, PageHeading } from '../components/ui';

export function ProvenancePage() {
  const { catalog } = useApp();
  const [data, setData] = useState<ProvenanceResponse | null>(null);
  useEffect(() => {
    apiRequest<ProvenanceResponse>('/api/provenance').then(setData).catch(() => setData(null));
  }, []);
  if (!data || !catalog) return <p class="muted">Loading…</p>;
  return (
    <>
      <PageHeading
        title="Provenance"
        intro="What content/*.sources.json records about the files the game loads: each source file, the paths it names that are gone, and the generated or made files no entry names."
      />
      <section class="panel">
        <h2>Source files</h2>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>File</th>
                <th>Paths named</th>
                <th>Purpose</th>
              </tr>
            </thead>
            <tbody>
              {data.sources.map(source => (
                <tr key={source.path}>
                  <td>
                    <a href={`/media/content/${source.path.replace(/^content\/game\//, '')}`}>{source.path}</a>
                  </td>
                  <td>{source.error ? <span class="chip review-bad">unreadable</span> : source.paths}</td>
                  <td class="muted">{source.purpose}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
      <section class="panel">
        <h2>Named but missing</h2>
        {data.dangling.length ? (
          <ul class="plain">
            {data.dangling.map(path => (
              <li key={path}>
                <code>{path}</code>
              </li>
            ))}
          </ul>
        ) : (
          <Empty>Every path a provenance file names exists.</Empty>
        )}
      </section>
      {catalog.categories.map(category => {
        const rows = data.missing[category.id] ?? [];
        return rows.length ? (
          <section class="panel" key={category.id}>
            <details>
              <summary>
                {category.label}: {rows.length} files without provenance
              </summary>
              <ul class="plain">
                {rows.map(row => (
                  <li key={row.path}>
                    <a href={recordUrl(row.id)}>{row.name}</a> <code>{row.path}</code>
                  </li>
                ))}
              </ul>
            </details>
          </section>
        ) : null;
      })}
    </>
  );
}
