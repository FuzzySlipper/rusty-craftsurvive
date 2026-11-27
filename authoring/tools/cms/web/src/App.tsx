import { useEffect, useState } from 'preact/hooks';
import { apiRequest } from './api';
import { activeSection, atlasUrl, listingUrl, parseHash, searchUrl, type Route } from './router';
import { useApp } from './state';
import { Empty } from './components/ui';
import { OverviewPage } from './pages/OverviewPage';
import { ListingPage } from './pages/ListingPage';
import { RecordPage } from './pages/RecordPage';
import { ProvenancePage } from './pages/ProvenancePage';
import { SearchPage } from './pages/SearchPage';
import { AtlasPage } from './pages/AtlasPage';

interface GitStatus {
  gitAvailable: boolean;
  modified: Array<{ status: string; path: string }>;
}

function renderRoute(route: Route) {
  switch (route.page) {
    case 'overview':
      return <OverviewPage />;
    case 'listing':
      return <ListingPage category={route.category} />;
    case 'record':
      return <RecordPage id={route.id} />;
    case 'provenance':
      return <ProvenancePage />;
    case 'search':
      return <SearchPage term={route.term} />;
    case 'atlas':
      return <AtlasPage initialRef={route.ref} />;
  }
}

export function App() {
  const { catalog, loading, error, refreshCatalog } = useApp();
  const [route, setRoute] = useState<Route>(() => parseHash(window.location.hash));
  const [query, setQuery] = useState('');
  const [git, setGit] = useState<GitStatus | null>(null);

  useEffect(() => {
    const onHashChange = () => {
      setRoute(parseHash(window.location.hash));
      window.scrollTo(0, 0);
    };
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);

  useEffect(() => {
    apiRequest<GitStatus>('/api/workspace/status').then(setGit).catch(() => setGit(null));
  }, [route.page]);

  const section = activeSection(route);
  const atlas = route.page === 'atlas';
  return (
    <div class={`shell${atlas ? ' wide' : ''}`}>
      <aside class="sidebar">
        <a class="brand" href="#overview">
          <span class="monogram">RW</span>
          <span>
            Wandering
            <small>authoring</small>
          </span>
        </a>
        <nav class="navigation">
          <a href="#overview" aria-current={section === 'overview' ? 'page' : undefined}>
            Overview
          </a>
          <a href={atlasUrl()} aria-current={section === 'atlas' ? 'page' : undefined}>
            Golden-seed atlas
          </a>
          {(catalog?.categories ?? []).map(category => (
            <a key={category.id} href={listingUrl(category.id)} aria-current={section === category.id ? 'page' : undefined}>
              {category.label}
            </a>
          ))}
          <a href="#provenance" aria-current={section === 'provenance' ? 'page' : undefined}>
            Provenance
          </a>
        </nav>
        {git?.modified.length ? (
          <details class="git-status">
            <summary>{git.modified.length} uncommitted under content and authoring</summary>
            <ul class="plain">
              {git.modified.slice(0, 40).map(row => (
                <li key={row.path}>
                  <code>{row.status}</code> {row.path}
                </li>
              ))}
            </ul>
          </details>
        ) : null}
      </aside>
      <main class="workspace">
        <div class="topbar">
          <form
            id="asset-search"
            onSubmit={event => {
              event.preventDefault();
              if (query.trim()) window.location.hash = searchUrl(query.trim());
            }}
          >
            <input type="search" placeholder="Search records" value={query} onInput={event => setQuery((event.currentTarget as HTMLInputElement).value)} />
            <button type="submit">Search</button>
            <button type="button" onClick={() => void refreshCatalog()}>
              Rescan
            </button>
          </form>
        </div>
        {loading ? <p class="muted">Loading catalog…</p> : error ? <Empty>{error}</Empty> : renderRoute(route)}
      </main>
    </div>
  );
}
