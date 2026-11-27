import { useEffect, useState } from 'preact/hooks';
import { apiRequest, isAbortError } from '../api';
import { listingUrl } from '../router';
import { useApp } from '../state';
import type { FileRow, ModelRef, RecordDetail } from '../types';
import { Breadcrumb, Empty, FactList, JsonView, PageHeading, Preview, formatBytes } from '../components/ui';
import { ReviewPanel } from '../components/ReviewControls';
import { ModelViewer } from '../components/ModelViewer';

const IMAGE = /\.(png|jpe?g|webp)$/i;
const TEXT = /\.(txt|md|json)$/i;
const MAX_IMAGES = 24;

function FileTable({ rows }: { rows: FileRow[] }) {
  return (
    <div class="table-wrap">
      <table>
        <thead>
          <tr>
            <th>File</th>
            <th>Type</th>
            <th>Size</th>
          </tr>
        </thead>
        <tbody>
          {rows.map(row => (
            <tr key={row.path}>
              <td>{row.url ? <a href={row.url} target="_blank" rel="noreferrer">{row.path}</a> : row.path}</td>
              <td>{row.type}</td>
              <td>{formatBytes(row.size)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function TextFile({ row }: { row: FileRow }) {
  const [text, setText] = useState<string | null>(null);
  useEffect(() => {
    if (!row.url) return;
    fetch(row.url)
      .then(response => response.text())
      .then(body => setText(body.length > 20000 ? `${body.slice(0, 20000)}\n…` : body))
      .catch(() => setText(null));
  }, [row.url]);
  return (
    <details>
      <summary>{row.path}</summary>
      <pre>{text ?? 'Loading…'}</pre>
    </details>
  );
}

export function RecordPage({ id }: { id: string }) {
  const { catalog } = useApp();
  const [record, setRecord] = useState<RecordDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [model, setModel] = useState<ModelRef | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setRecord(null);
    setError(null);
    apiRequest<RecordDetail>(`/api/records/${encodeURIComponent(id)}`, { signal: controller.signal })
      .then(found => {
        setRecord(found);
        setModel(found.models.find(item => !item.path?.includes('-far')) ?? found.models[0] ?? null);
      })
      .catch(err => {
        if (!isAbortError(err)) setError(err instanceof Error ? err.message : String(err));
      });
    return () => controller.abort();
  }, [id]);

  if (error) return <Empty>{error}</Empty>;
  if (!record) return <p class="muted">Loading…</p>;
  const category = catalog?.categories.find(item => item.id === record.category);
  const texts = record.authoring.filter(row => TEXT.test(row.path));
  const sounds = record.files.filter(row => /\.(wav|ogg|mp3)$/i.test(row.path));
  return (
    <>
      <Breadcrumb href={listingUrl(record.category)} label={category?.label ?? record.category} />
      <PageHeading title={record.name} intro={record.summary || undefined} />
      <FactList facts={{ ID: record.id, Kind: record.kind, Files: record.files.length, Size: formatBytes(record.bytes), Revision: record.revision }} />
      {model ? (
        <section class="panel">
          <div class="section-heading">
            <h2>Model</h2>
            {record.models.length > 1 ? (
              <select
                value={model.url}
                onChange={event => setModel(record.models.find(item => item.url === (event.currentTarget as HTMLSelectElement).value) ?? null)}
              >
                {record.models.map(item => (
                  <option key={item.url} value={item.url}>
                    {item.path}
                  </option>
                ))}
              </select>
            ) : null}
          </div>
          <ModelViewer model={model} key={model.url} />
        </section>
      ) : null}
      {sounds.length ? (
        <section class="panel">
          <h2>Sound</h2>
          {sounds.map(row => (
            <p key={row.path}>
              <audio controls preload="none" src={row.url ?? undefined} /> <small>{row.path}</small>
            </p>
          ))}
        </section>
      ) : null}
      {record.images.length ? (
        <section class="panel">
          <h2>Images</h2>
          <div class="gallery">
            {record.images.slice(0, MAX_IMAGES).map(url => (
              <figure key={url}>
                <a href={url} target="_blank" rel="noreferrer">
                  <Preview url={url} />
                </a>
                <figcaption>{decodeURIComponent(url.split('/').slice(3).join('/'))}</figcaption>
              </figure>
            ))}
          </div>
          {record.images.length > MAX_IMAGES ? <p class="muted">and {record.images.length - MAX_IMAGES} more in the file list.</p> : null}
        </section>
      ) : null}
      <ReviewPanel category={record.category} id={record.id} revision={record.revision} />
      <section class="panel">
        <h2>Provenance</h2>
        {record.unprovenanced.length ? (
          <p class="warning">
            No provenance entry names {record.unprovenanced.length === 1 ? 'this file' : `these ${record.unprovenanced.length} files`}:{' '}
            {record.unprovenanced.map(path => (
              <code key={path}>{path} </code>
            ))}
          </p>
        ) : null}
        {record.provenance.length ? (
          record.provenance.map(entry => (
            <details key={`${entry.source}${entry.pointer}`}>
              <summary>
                {entry.source} <code>{entry.pointer}</code>
              </summary>
              <JsonView value={entry.entry} />
            </details>
          ))
        ) : (
          <Empty>No provenance file names this record's files.</Empty>
        )}
      </section>
      {record.data ? (
        <section class="panel">
          <h2>Game data</h2>
          <JsonView value={record.data} />
        </section>
      ) : null}
      <section class="panel">
        <h2>Files in content/game</h2>
        {record.files.length ? <FileTable rows={record.files} /> : <Empty>None: this record exists only in authoring or in data.</Empty>}
      </section>
      {record.authoring.length ? (
        <section class="panel">
          <h2>Working files in authoring/assets</h2>
          {texts.map(row => (
            <TextFile row={row} key={row.path} />
          ))}
          <FileTable rows={record.authoring.filter(row => !TEXT.test(row.path) || IMAGE.test(row.path))} />
        </section>
      ) : null}
    </>
  );
}
