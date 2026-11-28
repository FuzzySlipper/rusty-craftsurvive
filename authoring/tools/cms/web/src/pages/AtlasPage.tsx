import { useEffect, useMemo, useState } from 'preact/hooks';
import { apiRequest } from '../api';
import { atlasUrl, caveUrl, systemUrl, undergroundUrl } from '../router';
import type {
  GoldenCaveRow,
  GoldenOverview,
  GoldenPlaces,
  GoldenReach,
  GoldenTile,
  GoldenTileSummary,
  GoldenWindow,
  UndergroundOverview,
  UndergroundSystemSummary,
} from '../types';
import { AtlasView, DETAIL_METRES_PER_PIXEL, PLACE_COLOURS, crossingOf, placeOf, tileAt, type AtlasToggles, type Camera, type Pick } from '../atlas/AtlasView';
import { BASE_MODES, loadContinent, paintContinent, paintWindow, sampleAt, type BaseMode, type ContinentRaster, type Legend } from '../atlas/layers';
import { Empty, FactList, JsonView } from '../components/ui';
import { ReviewPanel } from '../components/ReviewControls';
import { useSessionState } from '../useSessionState';

/** Walked reaches below this flow (m³/s) stay on the server: there are tens of thousands of them. */
const DEFAULT_MIN_FLOW = 1;
const NAVIGABILITY_OPTIONS = [
  { value: -1, label: 'Hidden' },
  { value: 0, label: 'All drawn' },
  { value: 1, label: 'Canoe and up' },
  { value: 2, label: 'Boat and up' },
  { value: 3, label: 'Great rivers' },
];
/** How close a deep link flies: a place to read its surroundings, a town to see its whole window, anything else the region. */
const FLY_ZOOM: Record<string, number> = { place: 3, crossing: 3, town: 10, reach: 60, lake: 120, site: 120, underground: 6 };

const DEFAULT_TOGGLES: AtlasToggles = {
  drainage: 0,
  mapRivers: false,
  lakes: true,
  towns: true,
  sites: false,
  windows: true,
  detail: true,
  places: { StandingStones: false, Ruin: false, CaveMouth: true, DungeonEntrance: true, VantagePoint: false },
  caves: true,
  crossings: false,
  walkedRivers: true,
  labels: true,
  allCaves: false,
  underground: true,
  undergroundMultiOnly: false,
  undergroundMouths: true,
};

/** Region tiles kept loaded at once: a view at the detail zoom shows a few; panning keeps the nearest. */
const MOST_TILES = 48;
/** The share of the window the atlas canvas takes, for which tiles a view needs. */
const VIEW_SHARE = 0.8;

function LegendView({ legend }: { legend: Legend | null }) {
  if (!legend) return null;
  if (legend.kind === 'swatches')
    return (
      <ul class="legend">
        {legend.items!.map(item => (
          <li key={item.label}>
            <i style={{ background: item.colour }} /> {item.label}
          </li>
        ))}
      </ul>
    );
  return (
    <div class="legend-ramp">
      <i style={{ background: `linear-gradient(90deg, ${legend.stops!.join(', ')})` }} />
      <span>{legend.low}</span>
      <span>{legend.high}</span>
    </div>
  );
}

function Inspector({ pick, citation }: { pick: Pick; citation: string }) {
  const [copied, setCopied] = useState(false);
  const data = pick.data as Record<string, unknown>;
  const facts: Record<string, string | number | boolean> = { Reference: pick.ref, X: Math.round(pick.x), Z: Math.round(pick.z) };
  for (const [key, value] of Object.entries(data)) {
    if (typeof value === 'number' || typeof value === 'string' || typeof value === 'boolean') facts[key] = value;
  }
  return (
    <section class="panel atlas-inspector">
      <div class="section-heading">
        <h2>{pick.label}</h2>
        <button
          type="button"
          onClick={() => {
            void navigator.clipboard?.writeText(citation);
            setCopied(true);
            setTimeout(() => setCopied(false), 1500);
          }}
        >
          {copied ? 'Copied' : 'Copy reference'}
        </button>
      </div>
      <p class="muted">
        <code>{citation}</code>
      </p>
      {pick.kind === 'place' && (data.cave || data.seed) ? (
        <p>
          <a class="button primary" href={caveUrl(pick.ref)}>
            Open the cave view
          </a>
        </p>
      ) : null}
      {pick.kind === 'underground' && typeof data.level === 'string' && typeof data.id === 'string' ? (
        <p>
          <a class="button primary" href={systemUrl(data.level, data.id)}>
            Open system view (3D / Karst detail)
          </a>
        </p>
      ) : null}
      <FactList facts={facts} />
      {Object.values(data).some(value => typeof value === 'object' && value !== null) ? (
        <details>
          <summary>All fields</summary>
          <JsonView value={data} />
        </details>
      ) : null}
      <ReviewPanel category="golden" id={pick.ref} heading="Notes on this element" />
    </section>
  );
}

export function AtlasPage({ initialRef }: { initialRef?: string }) {
  const [overview, setOverview] = useState<GoldenOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [raster, setRaster] = useState<ContinentRaster | null>(null);
  const [places, setPlaces] = useState<GoldenPlaces | null>(null);
  const [drainage, setDrainage] = useState<GoldenReach[]>([]);
  const [drainageTotal, setDrainageTotal] = useState(0);
  const [mapRivers, setMapRivers] = useState<number[][] | null>(null);
  const [details, setDetails] = useState<Record<string, GoldenWindow>>({});
  const [detailImages, setDetailImages] = useState<Record<string, HTMLCanvasElement>>({});
  const [tileIndex, setTileIndex] = useState<GoldenTileSummary[]>([]);
  const [tiles, setTiles] = useState<Map<string, GoldenTile>>(() => new Map());
  const [caveList, setCaveList] = useState<GoldenCaveRow[] | null>(null);
  const [ugOverview, setUgOverview] = useState<UndergroundOverview | null>(null);
  const [ugLevel, setUgLevel] = useSessionState<string>('atlas:ugLevel', 'fine');
  const [ugSystems, setUgSystems] = useState<UndergroundSystemSummary[] | null>(null);
  const [mode, setMode] = useSessionState<BaseMode>('atlas:mode', 'relief');
  const [storedToggles, setToggles] = useSessionState<AtlasToggles>('atlas:toggles', DEFAULT_TOGGLES);
  const toggles: AtlasToggles = { ...DEFAULT_TOGGLES, ...storedToggles, places: { ...DEFAULT_TOGGLES.places, ...storedToggles.places } };
  const [minFlow, setMinFlow] = useSessionState('atlas:minFlow', DEFAULT_MIN_FLOW);
  const [camera, setCamera] = useState<Camera | null>(null);
  const [selected, setSelected] = useState<Pick | null>(null);
  const [hover, setHover] = useState<{ x: number; z: number } | null>(null);

  useEffect(() => {
    apiRequest<GoldenOverview>('/api/golden')
      .then(async found => {
        setOverview(found);
        if (!found.available || !found.manifest) return;
        const radius = found.manifest.world.radius;
        setCamera({ x: 0, z: 0, metresPerPixel: (radius * 2) / Math.max(600, window.innerHeight - 200) });
        const [continent, named] = await Promise.all([
          loadContinent(found.manifest, found.mediaBase ?? '/media/golden/'),
          apiRequest<GoldenPlaces>('/api/golden/places'),
        ]);
        setRaster(continent);
        setPlaces(named);
        if (found.manifest.tiles) {
          const [index, caves] = await Promise.all([
            apiRequest<GoldenTileSummary[]>('/api/golden/tiles'),
            apiRequest<{ caves: GoldenCaveRow[] }>('/api/golden/caves'),
          ]);
          setTileIndex(index);
          setCaveList(caves.caves);
        }
        apiRequest<UndergroundOverview>('/api/golden/underground')
          .then(setUgOverview)
          .catch(() => setUgOverview(null));
      })
      .catch(err => setError(err instanceof Error ? err.message : String(err)));
  }, []);

  useEffect(() => {
    if (!ugOverview?.available || !toggles.underground) return;
    let cancelled = false;
    apiRequest<{ level: string; systems: UndergroundSystemSummary[] }>(`/api/golden/underground/${encodeURIComponent(ugLevel)}/summary`)
      .then(data => {
        if (!cancelled) setUgSystems(data.systems);
      })
      .catch(() => {
        if (!cancelled) setUgSystems(null);
      });
    return () => {
      cancelled = true;
    };
  }, [ugOverview, ugLevel, toggles.underground]);

  useEffect(() => {
    if (!overview?.available) return;
    apiRequest<{ reaches: GoldenReach[]; total: number }>(`/api/golden/drainage?minFlow=${minFlow}`)
      .then(found => {
        setDrainage(found.reaches);
        setDrainageTotal(found.total);
      })
      .catch(err => setError(err instanceof Error ? err.message : String(err)));
  }, [overview, minFlow]);

  useEffect(() => {
    if (!overview?.available || !toggles.mapRivers || mapRivers) return;
    apiRequest<{ reaches: number[][] }>('/api/golden/rivers').then(found => setMapRivers(found.reaches));
  }, [overview, toggles.mapRivers]);

  const world = overview?.manifest?.world;
  const windows = overview?.manifest?.windows ?? [];

  // Walking detail loads once the view is close enough to show it, window by window.
  useEffect(() => {
    if (!camera || !world || camera.metresPerPixel > DETAIL_METRES_PER_PIXEL * 4) return;
    const span = camera.metresPerPixel * 1200;
    for (const window of windows) {
      if (details[window.id] || Math.abs(window.x - camera.x) > window.half + span || Math.abs(window.z - camera.z) > window.half + span) continue;
      void loadWindow(window.id);
    }
  }, [camera, world]);

  // The region tiles about the view, once it is close enough to draw places; the farthest are let go past MOST_TILES.
  const spacing = overview?.manifest?.tiles?.spacing ?? 16384;
  const landTiles = useMemo(() => new Set(tileIndex.filter(tile => !tile.sea).map(tile => tile.tile.join('_'))), [tileIndex]);
  useEffect(() => {
    if (!camera || !landTiles.size || camera.metresPerPixel > DETAIL_METRES_PER_PIXEL) return;
    const halfX = (camera.metresPerPixel * window.innerWidth * VIEW_SHARE) / 2;
    const halfZ = (camera.metresPerPixel * window.innerHeight * VIEW_SHARE) / 2;
    const [x0, z0] = tileAt(camera.x - halfX, camera.z - halfZ, spacing);
    const [x1, z1] = tileAt(camera.x + halfX, camera.z + halfZ, spacing);
    for (let tz = z0; tz <= z1; tz++)
      for (let tx = x0; tx <= x1; tx++) {
        const key = `${tx}_${tz}`;
        if (landTiles.has(key) && !tiles.has(key)) void loadTile(tx, tz);
      }
  }, [camera, landTiles]);

  const pending = useMemo(() => new Set<string>(), []);
  async function loadTile(tx: number, tz: number): Promise<GoldenTile | undefined> {
    const key = `${tx}_${tz}`;
    if (tiles.has(key)) return tiles.get(key);
    if (pending.has(key)) return undefined;
    pending.add(key);
    try {
      const tile = await apiRequest<GoldenTile>(`/api/golden/tiles/${tx}/${tz}`);
      setTiles(current => {
        const next = new Map(current).set(key, tile);
        while (next.size > MOST_TILES) {
          // Let go of the tile farthest from the view.
          const far = [...next.keys()].sort((a, b) => distance(b) - distance(a))[0];
          next.delete(far);
        }
        return next;
      });
      return tile;
    } finally {
      pending.delete(key);
    }
  }

  function distance(key: string): number {
    const [tx, tz] = key.split('_').map(Number);
    return camera ? Math.hypot(tx * spacing - camera.x, tz * spacing - camera.z) : 0;
  }

  async function loadWindow(id: string) {
    if (!world || details[id]) return details[id];
    const detail = await apiRequest<GoldenWindow>(`/api/golden/windows/${id}`);
    const [height, biome] = await Promise.all([
      fetch(detail.heightUrl).then(response => response.arrayBuffer()),
      fetch(detail.biomeUrl).then(response => response.arrayBuffer()),
    ]);
    const image = paintWindow(new Int16Array(height), new Uint8Array(biome), detail.side, detail.step, world);
    setDetails(current => ({ ...current, [id]: detail }));
    setDetailImages(current => ({ ...current, [id]: image }));
    return detail;
  }

  const painted = useMemo(() => (raster && world ? paintContinent(raster, world, mode) : null), [raster, world, mode]);

  // A deep link (#atlas/<reference>) selects the element and flies to it.
  useEffect(() => {
    if (!initialRef || !places || !world) return;
    void resolve(initialRef).then(pick => {
      if (!pick) return;
      setSelected(pick);
      setCamera({ x: pick.x, z: pick.z, metresPerPixel: FLY_ZOOM[pick.kind] ?? 60 });
    });
  }, [initialRef, places, world, tileIndex, drainage, ugSystems]);

  async function resolve(ref: string): Promise<Pick | null> {
    if (!places) return null;
    if (ref.startsWith('ug:')) {
      const parts = ref.split(':');
      const level = parts[1] ?? ugLevel;
      if (level !== ugLevel) setUgLevel(level);
      const found = ugSystems?.find(s => s.id === ref);
      if (found) {
        return {
          ref,
          kind: 'underground',
          label: `Underground ${found.m ? `${found.boxes.length}-box system` : 'single cave'} (${level})`,
          x: found.x,
          z: found.z,
          data: { level, id: found.id, boxes: found.boxes.length, links: found.links.length, mouths: found.mouths.length, maxDepthMetres: found.d, solubility: found.s, hardness: found.h, biome: found.b },
        };
      }
      try {
        const res = await apiRequest<{ level: string; system: { id: string; x: number; z: number; boxes: unknown[]; links: unknown[]; mouths: unknown[]; maxDepth: number; solubility: number; hardness: number; biome: string } }>(
          `/api/golden/underground/${encodeURIComponent(level)}/systems/${encodeURIComponent(ref)}`,
        );
        const s = res.system;
        return {
          ref,
          kind: 'underground',
          label: `Underground ${s.boxes.length > 1 ? `${s.boxes.length}-box system` : 'single cave'} (${level})`,
          x: s.x,
          z: s.z,
          data: { level, id: s.id, boxes: s.boxes.length, links: s.links.length, mouths: s.mouths.length, maxDepthMetres: s.maxDepth, solubility: s.solubility, hardness: s.hardness, biome: s.biome },
        };
      } catch {
        return null;
      }
    }
    const town = places.towns.find(item => item.key === ref);
    if (town) return { ref, kind: 'town', label: town.name, x: town.x, z: town.z, data: town };
    if (ref.startsWith('lake:')) {
      const lake = places.lakes.find(item => item.key === ref);
      if (lake) return { ref, kind: 'lake', label: lake.name || `${lake.kind} lake ${lake.key}`, x: lake.x, z: lake.z, data: lake };
    }
    if (ref.startsWith('site:')) {
      const site = places.sites.find(item => `site:${item.name}` === ref);
      if (site) return { ref, kind: 'site', label: site.name, x: site.x, z: site.z, data: site };
    }
    if (ref.startsWith('reach:')) {
      const reach = drainage.find(item => item.key === ref);
      if (reach) return { ref, kind: 'reach', label: `Reach ${reach.key}`, x: reach.points[0], z: reach.points[1], data: reach };
    }
    if (ref.startsWith('poi:') || ref.startsWith('crossing:')) {
      const [, cx, cz] = ref.split(':').map(Number);
      const [tx, tz] = tileAt(cx * world!.placeCell, cz * world!.placeCell, spacing);
      const tile = await loadTile(tx, tz);
      if (!tile) return null;
      if (ref.startsWith('poi:')) {
        const row = tile.places.find(place => place[0] === cx && place[1] === cz);
        const kind = row ? world!.placeKinds[row[2]] : '';
        if (row) return { ref, kind: 'place', label: `${kind} ${ref}`, x: row[3], z: row[4], data: placeOf(row, kind, tile.caves.find(cave => cave.id === ref)) };
      } else {
        const row = tile.crossings.find(crossing => crossing[0] === cx && crossing[1] === cz);
        if (row) return { ref, kind: 'crossing', label: `Crossing ${ref}`, x: (row[2] + row[4]) / 2, z: (row[3] + row[5]) / 2, data: crossingOf(row) };
      }
    }
    return null;
  }

  if (error) return <Empty>{error}</Empty>;
  if (!overview) return <p class="muted">Loading…</p>;
  if (!overview.available || !overview.manifest || !world)
    return (
      <section class="panel">
        <h1>Golden-seed atlas</h1>
        <Empty>
          There is no export yet. Run <code>{overview.command}</code> from the repository root; it generates the world golden-seed.json
          names with the product's generator and writes authoring/assets/golden-seed/.
        </Empty>
      </section>
    );

  const manifest = overview.manifest;
  const drift = overview.drift;
  const citation = selected
    ? `golden ${world.seed}/${world.size} ${selected.ref} (export ${manifest.exportedAt.slice(0, 10)}, fingerprint ${world.fingerprint})`
    : '';
  const sample = hover && raster ? sampleAt(raster, hover.x, hover.z) : null;
  const setToggle = <K extends keyof AtlasToggles>(key: K, value: AtlasToggles[K]) => setToggles({ ...toggles, [key]: value });
  const counts = tileIndex.reduce(
    (sum, tile) => {
      for (const [kind, count] of Object.entries(tile.places ?? {})) sum[kind] = (sum[kind] ?? 0) + count;
      return sum;
    },
    {} as Record<string, number>,
  );

  return (
    <div class="atlas">
      <header class="atlas-heading">
        <div>
          <h1>Golden seed {world.seed}</h1>
          <p class="muted">
            {(world.size / 1000).toFixed(0)} km {world.continental ? `continent (${world.design ?? 'no design'})` : 'regional world'} · generator v
            {world.generatorVersion} · fingerprint <code>{world.fingerprint}</code> · exported {manifest.exportedAt} from{' '}
            <code>{manifest.source.commit.slice(0, 10)}</code>
            {manifest.source.dirty ? ' (with uncommitted changes)' : ''}
          </p>
          {drift?.generatorCommitsSince ? (
            <p class="warning">
              {drift.generatorCommitsSince} commit{drift.generatorCommitsSince === 1 ? '' : 's'} since this export touch the product, its content or the
              Engine pair. Rerun <code>{overview.command}</code> to see the current world.
            </p>
          ) : null}
          {overview.configChanged ? (
            <p class="warning">
              golden-seed.json has changed since this export. Rerun <code>{overview.command}</code>.
            </p>
          ) : null}
        </div>
      </header>
      <div class="atlas-body">
        <aside class="atlas-controls panel">
          <label>
            Base layer
            <select value={mode} onChange={event => setMode((event.currentTarget as HTMLSelectElement).value as BaseMode)}>
              {BASE_MODES.map(item => (
                <option key={item.id} value={item.id}>
                  {item.label}
                </option>
              ))}
            </select>
          </label>
          <small class="muted">{BASE_MODES.find(item => item.id === mode)?.about}</small>
          <LegendView legend={painted?.legend ?? null} />
          <h3>Water</h3>
          <label>
            Walked drainage (250 m)
            <select value={toggles.drainage} onChange={event => setToggle('drainage', Number((event.currentTarget as HTMLSelectElement).value))}>
              {NAVIGABILITY_OPTIONS.map(option => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
          <label>
            Least flow (m³/s)
            <input type="number" min={0} step={0.5} value={minFlow} onChange={event => setMinFlow(Number((event.currentTarget as HTMLInputElement).value) || 0)} />
          </label>
          <small class="muted">
            {drainage.length} of {drainageTotal} reaches loaded
          </small>
          <Toggle label="Map rivers (1 km)" value={toggles.mapRivers} onChange={value => setToggle('mapRivers', value)} />
          <Toggle label="Lakes" value={toggles.lakes} onChange={value => setToggle('lakes', value)} />
          <h3>Settlement</h3>
          <Toggle label="Towns" value={toggles.towns} onChange={value => setToggle('towns', value)} />
          <Toggle label="Representative sites" value={toggles.sites} onChange={value => setToggle('sites', value)} />
          <Toggle label="Labels" value={toggles.labels} onChange={value => setToggle('labels', value)} />
          <h3>Underground plan (#11209)</h3>
          {ugOverview?.available && ugOverview.manifest ? (
            <>
              <Toggle label="Show planned cave systems" value={toggles.underground} onChange={value => setToggle('underground', value)} />
              <label>
                Coarseness level
                <select value={ugLevel} onChange={event => setUgLevel((event.currentTarget as HTMLSelectElement).value)}>
                  {ugOverview.manifest.levels.map(lvl => (
                    <option key={lvl.name} value={lvl.name}>
                      {lvl.name} ({lvl.settings.siteSpacingMetres} m · {lvl.census.systems} sys)
                    </option>
                  ))}
                </select>
              </label>
              <Toggle label="Multi-box systems only" value={toggles.undergroundMultiOnly} onChange={value => setToggle('undergroundMultiOnly', value)} />
              <Toggle label="Planned mouths (diamonds)" value={toggles.undergroundMouths} onChange={value => setToggle('undergroundMouths', value)} />
              <ul class="legend">
                <li>
                  <i style={{ background: '#4de0c9' }} /> Shallow (&lt;30 m)
                </li>
                <li>
                  <i style={{ background: '#f2b84b' }} /> Mid (30–60 m)
                </li>
                <li>
                  <i style={{ background: '#d96be6' }} /> Deep (60+ m)
                </li>
              </ul>
              {(() => {
                const active = ugOverview.manifest.levels.find(l => l.name === ugLevel);
                return active ? (
                  <small class="muted">
                    {active.census.systems} systems ({(active.census.multiBoxShare * 100).toFixed(1)}% multi), {active.census.totalBoxes} boxes · export{' '}
                    {(active.metrics.exportMs ?? active.metrics.wallMs).toFixed(0)} ms · {active.metrics.retainedInMemoryMB} MB RAM ·{' '}
                    <a href={undergroundUrl()}>full comparison</a>
                  </small>
                ) : null;
              })()}
            </>
          ) : (
            <small class="muted">
              No underground plan exported yet. Run <code>{ugOverview?.command ?? 'golden-seed underground'}</code>.
            </small>
          )}
          <h3>Walking detail</h3>
          <small class="muted">
            Every place, cave way and crossing on the continent, drawn once a pixel is {DETAIL_METRES_PER_PIXEL} m or less (about a town's width
            across the view). The walking ground's own heights are exported about each town.
          </small>
          <Toggle label="Cave mouths far out" value={toggles.allCaves} onChange={value => setToggle('allCaves', value)} />
          <Toggle label="Window outlines" value={toggles.windows} onChange={value => setToggle('windows', value)} />
          <Toggle label="Walking ground about towns" value={toggles.detail} onChange={value => setToggle('detail', value)} />
          <Toggle label="Walked rivers, shaped" value={toggles.walkedRivers} onChange={value => setToggle('walkedRivers', value)} />
          {world.placeKinds
            .filter(kind => kind !== 'None')
            .map(kind => (
              <Toggle
                key={kind}
                label={`${kind}${counts[kind] ? ` (${counts[kind]})` : ''}`}
                swatch={PLACE_COLOURS[kind]}
                value={Boolean(toggles.places[kind])}
                onChange={value => setToggle('places', { ...toggles.places, [kind]: value })}
              />
            ))}
          <Toggle label="Cave ways underground" value={toggles.caves} onChange={value => setToggle('caves', value)} />
          <Toggle label="Crossings" value={toggles.crossings} onChange={value => setToggle('crossings', value)} />
          {tileIndex.some(tile => tile.failed) ? (
            <>
              <h3>Tiles that failed</h3>
              <ul class="plain">
                {tileIndex
                  .filter(tile => tile.failed)
                  .map(tile => (
                    <li key={tile.tile.join('_')}>
                      <a
                        href="#atlas"
                        onClick={event => {
                          event.preventDefault();
                          setCamera({ x: tile.tile[0] * spacing, z: tile.tile[1] * spacing, metresPerPixel: spacing / 600 });
                        }}
                      >
                        tile {tile.tile.join(', ')}
                      </a>{' '}
                      <small class="warning-text">{tile.failed}</small>
                    </li>
                  ))}
              </ul>
            </>
          ) : null}
          <h3>Windows</h3>
          <ul class="plain">
            {windows.map(window => (
              <li key={window.id}>
                <a
                  href={atlasUrl(window.town ?? `window:${window.id}`)}
                  onClick={event => {
                    event.preventDefault();
                    setCamera({ x: window.x, z: window.z, metresPerPixel: (window.half * 2.2) / 800 });
                  }}
                >
                  {window.name}
                </a>{' '}
                <small class="muted">
                  {window.places} places, {window.caves} caves
                </small>
              </li>
            ))}
          </ul>
        </aside>
        <div class="atlas-main">
          {camera ? (
            <AtlasView
              world={world}
              base={painted?.canvas ?? null}
              places={places}
              drainage={drainage}
              mapRivers={mapRivers}
              windows={windows}
              details={details}
              detailImages={detailImages}
              tiles={[...tiles.values()]}
              caveList={caveList}
              undergroundSystems={ugSystems}
              undergroundLevel={ugLevel}
              failedTiles={tileIndex.filter(tile => tile.failed).map(tile => tile.tile)}
              tileSpacing={spacing}
              toggles={toggles}
              camera={camera}
              selected={selected}
              onCamera={setCamera}
              onPick={pick => {
                setSelected(pick);
                if (pick) history.replaceState(null, '', atlasUrl(pick.ref));
              }}
              onHover={setHover}
            />
          ) : null}
          <div class="atlas-readout">
            {hover ? (
              <>
                <span>
                  x {Math.round(hover.x)} · z {Math.round(hover.z)}
                </span>
                {sample ? (
                  <span>
                    {world.water[sample.water]} · {world.biomes[sample.biome]} · {sample.elevation.toFixed(0)} m · temperature {sample.temperature.toFixed(2)} ·
                    moisture {sample.moisture.toFixed(2)} · rock {sample.rock.toFixed(2)}
                    {sample.solubility !== undefined ? ` · karst ${sample.solubility.toFixed(2)}` : ''}
                  </span>
                ) : null}
              </>
            ) : (
              <span class="muted">Drag to pan, wheel to zoom, click an element to select it.</span>
            )}
          </div>
          {selected ? <Inspector pick={selected} citation={citation} /> : null}
        </div>
      </div>
    </div>
  );
}

function Toggle({ label, value, onChange, swatch }: { label: string; value: boolean; onChange: (value: boolean) => void; swatch?: string }) {
  return (
    <label class="toggle">
      <input type="checkbox" checked={value} onChange={event => onChange((event.currentTarget as HTMLInputElement).checked)} />
      {swatch ? <i style={{ background: swatch }} /> : null}
      {label}
    </label>
  );
}
