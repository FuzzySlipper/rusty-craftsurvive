import { useEffect, useState } from 'preact/hooks';
import { apiRequest } from '../api';
import { atlasUrl, listingUrl, recordUrl } from '../router';
import { useApp } from '../state';
import type { GoldenOverview } from '../types';
import { Empty, FactList, PageHeading, formatBytes } from '../components/ui';

export function OverviewPage() {
  const { catalog, reviews, card } = useApp();
  const [golden, setGolden] = useState<GoldenOverview | null>(null);
  useEffect(() => {
    apiRequest<GoldenOverview>('/api/golden').then(setGolden).catch(() => setGolden(null));
  }, []);
  if (!catalog) return null;
  const marked = reviews.filter(row => row.marked);
  const noted = reviews.filter(row => row.note || row.rating);
  const missing = catalog.categories.reduce((sum, category) => sum + (category.unprovenanced ?? 0), 0);
  const world = golden?.manifest?.world;
  return (
    <>
      <PageHeading
        title="Rusty Wandering content"
        intro={`${catalog.records.length} records over ${catalog.contentFiles} files in content/game, with their provenance and the working files behind them.`}
      />
      <section class="collections">
        {catalog.categories.map(category => (
          <a class="collection" href={listingUrl(category.id)} key={category.id}>
            <strong>{category.records ?? 0}</strong>
            <span>{category.label}</span>
            <small>
              {category.files ?? 0} files · {formatBytes(category.bytes ?? 0)}
              {category.unprovenanced ? ` · ${category.unprovenanced} without provenance` : ''}
            </small>
          </a>
        ))}
      </section>
      <section class="panel">
        <div class="section-heading">
          <h2>Golden seed</h2>
          <a class="button" href={atlasUrl()}>
            Open the atlas
          </a>
        </div>
        {world ? (
          <>
            <FactList
              facts={{
                Seed: world.seed,
                Size: `${(world.size / 1000).toFixed(0)} km`,
                Generator: `v${world.generatorVersion}`,
                Fingerprint: world.fingerprint,
                Exported: golden?.manifest?.exportedAt,
                'Generator commits since': golden?.drift?.generatorCommitsSince ?? '',
              }}
            />
            {golden?.configChanged ? <p class="warning">golden-seed.json has changed since this export: run <code>{golden.command}</code>.</p> : null}
          </>
        ) : (
          <Empty>
            No export yet. Run <code>{golden?.command ?? 'python3 authoring/tools/golden-seed/export.py'}</code>.
          </Empty>
        )}
      </section>
      <section class="panel">
        <div class="section-heading">
          <h2>Provenance</h2>
          <a class="button" href="#provenance">
            Review gaps
          </a>
        </div>
        <p class="muted">
          {missing
            ? `${missing} generated or made files are not named by any content/*.sources.json entry.`
            : 'Every generated file is named by a provenance entry.'}
        </p>
      </section>
      <section class="panel">
        <h2>Marked and noted</h2>
        {marked.length || noted.length ? (
          <ul class="plain">
            {[...new Map([...marked, ...noted].map(row => [`${row.category}/${row.id}`, row])).values()].map(row => (
              <li key={`${row.category}/${row.id}`}>
                <a href={row.category === 'golden' ? atlasUrl(row.id) : recordUrl(row.id)}>{card(row.id)?.name ?? row.id}</a>{' '}
                <code>{row.id}</code> {row.marked ? <span class="chip">Marked</span> : null}{' '}
                {row.rating ? <span class={`chip review-${row.rating}`}>{row.rating}</span> : null} <span class="muted">{row.note}</span>
              </li>
            ))}
          </ul>
        ) : (
          <Empty>Nothing marked or noted yet.</Empty>
        )}
      </section>
    </>
  );
}
