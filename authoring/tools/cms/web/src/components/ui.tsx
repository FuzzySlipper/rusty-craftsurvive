import type { ComponentChildren } from 'preact';
import { recordUrl } from '../router';
import type { RecordCard } from '../types';
import { TileReview } from './ReviewControls';

export function PageHeading({ title, intro, action }: { title: string; intro?: ComponentChildren; action?: ComponentChildren }) {
  return (
    <header class="page-heading">
      <div>
        <h1>{title}</h1>
        {intro ? <p>{intro}</p> : null}
      </div>
      {action ?? null}
    </header>
  );
}

export function Breadcrumb({ href, label }: { href: string; label: string }) {
  return (
    <a class="breadcrumb" href={href}>
      {label}
    </a>
  );
}

export function Chip({ label, tone = '' }: { label: string; tone?: string }) {
  return <span class={`chip ${tone}`}>{label}</span>;
}

export function Preview({ url, alt = '', className = '' }: { url: string | null | undefined; alt?: string; className?: string }) {
  return (
    <div class={`preview ${className}`}>
      {url ? <img src={url} alt={alt} loading="lazy" /> : <span class="muted">No image</span>}
    </div>
  );
}

export function FactList({ facts }: { facts: Record<string, string | number | boolean | null | undefined> }) {
  const entries = Object.entries(facts).filter(([, value]) => value !== null && value !== undefined && value !== '');
  if (!entries.length) return null;
  return (
    <dl class="facts">
      {entries.map(([name, value]) => (
        <div key={name}>
          <dt>{name}</dt>
          <dd>{typeof value === 'boolean' ? (value ? 'Yes' : 'No') : String(value)}</dd>
        </div>
      ))}
    </dl>
  );
}

export function Empty({ children }: { children: ComponentChildren }) {
  return <p class="empty">{children}</p>;
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

export function RecordTile({ record }: { record: RecordCard }) {
  const status = [record.kind, record.unprovenanced ? `${record.unprovenanced} without provenance` : ''].filter(Boolean).join(' · ');
  return (
    <article class="tile">
      <a class="tile-link" href={recordUrl(record.id)}>
        {record.preview ? (
          <Preview url={record.preview} />
        ) : (
          <div class="preview">
            <span class="muted">{record.model ? '3D model' : record.audio ? 'Sound' : record.files ? 'Data' : 'No files'}</span>
          </div>
        )}
        <div class="tile-info">
          <code>{record.id}</code>
          <strong>{record.name}</strong>
          <small>{status}</small>
        </div>
      </a>
      <TileReview category={record.category} id={record.id} />
    </article>
  );
}

export function JsonView({ value }: { value: unknown }) {
  return <pre class="json">{JSON.stringify(value, null, 2)}</pre>;
}
