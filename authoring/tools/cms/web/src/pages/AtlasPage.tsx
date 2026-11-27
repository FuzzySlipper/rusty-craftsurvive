import { useEffect, useMemo, useState } from 'preact/hooks';
import { apiRequest } from '../api';
import { atlasUrl } from '../router';
import type { GoldenOverview, GoldenPlaces, GoldenReach, GoldenWindow } from '../types';
import { AtlasView, DETAIL_METRES_PER_PIXEL, PLACE_COLOURS, type AtlasToggles, type Camera, type Pick } from '../atlas/AtlasView';
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
const FLY_ZOOM: Record<string, number> = { place: 3, crossing: 3, town: 10, reach: 60, lake: 120, site: 120 };

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
};

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
  const [mode, setMode] = useSessionState<BaseMode>('atlas:mode', 'relief');
  const [toggles, setToggles] = useSessionState<AtlasToggles>('atlas:toggles', DEFAULT_TOGGLES);
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
      })
      .catch(err => setError(err instanceof Error ? err.message : String(err)));
  }, []);

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
  }, [initialRef, places, world]);

  async function resolve(ref: string): Promise<Pick | null> {
    if (!places) return null;
    const town = places.towns.find(item => item.key === ref);
    if (town) return { ref, kind: 'town', label: town.name, x: town.x, z: town.z, data: town };
    if (ref.startsWith('lake:')) {
      const lake = places.lakes.find(item => `lake:${item.id}` === ref);
      if (lake) return { ref, kind: 'lake', label: lake.name || `${lake.kind} lake ${lake.id}`, x: lake.x, z: lake.z, data: lake };
    }
    if (ref.startsWith('site:')) {
      const site = places.sites.find(item => `site:${item.name}` === ref);
      if (site) return { ref, kind: 'site', label: site.name, x: site.x, z: site.z, data: site };
    }
    if (ref.startsWith('reach:')) {
      const reach = drainage.find(item => `reach:${item.id}` === ref);
      if (reach) return { ref, kind: 'reach', label: `Reach ${reach.id}`, x: reach.points[0], z: reach.points[1], data: reach };
    }
    if (ref.startsWith('poi:') || ref.startsWith('crossing:')) {
      const [, cx, cz] = ref.split(':').map(Number);
      const x = (cx + 0.5) * world!.placeCell;
      const z = (cz + 0.5) * world!.placeCell;
      const window = windows.find(item => Math.abs(item.x - x) <= item.half + world!.placeCell && Math.abs(item.z - z) <= item.half + world!.placeCell);
      const detail = window ? await loadWindow(window.id) : undefined;
      const place = detail?.places.find(item => item.id === ref);
      if (place) return { ref, kind: 'place', label: `${place.kind} ${place.id}`, x: place.x, z: place.z, data: place };
      const crossing = detail?.crossings.find(item => item.id === ref);
      if (crossing)
        return { ref, kind: 'crossing', label: `Crossing ${ref}`, x: (crossing.fromX + crossing.toX) / 2, z: (crossing.fromZ + crossing.toZ) / 2, data: crossing };
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
          There is no export yet. Run <code>{overview.command}</code> from the repository root; it starts a headless host, generates the world
          golden-seed.json names, and writes authoring/assets/golden-seed/.
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
  const counts = Object.values(details).reduce(
    (sum, detail) => {
      for (const place of detail.places) sum[place.kind] = (sum[place.kind] ?? 0) + 1;
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
          <h3>Walking detail</h3>
          <small class="muted">Places, caves and crossings are exported for the windows about each town. Zoom in on one to see them.</small>
          <Toggle label="Window outlines" value={toggles.windows} onChange={value => setToggle('windows', value)} />
          <Toggle label="Walking ground" value={toggles.detail} onChange={value => setToggle('detail', value)} />
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
