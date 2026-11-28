import { useEffect, useRef, useState } from 'preact/hooks';
import { apiRequest, isAbortError } from '../api';
import { atlasUrl, systemUrl, undergroundUrl } from '../router';
import type { SystemKarstView, UndergroundLevelReport, UndergroundOverview, UndergroundSystemDetail } from '../types';
import { Breadcrumb, Empty, FactList, PageHeading } from '../components/ui';
import { useSessionState } from '../useSessionState';
import type { DetailSceneHandle, PlanSceneHandle } from '../viewer/SystemScene';

const STATE_COLOURS: Record<number, [number, number, number]> = {
  0: [26, 31, 28],
  1: [196, 72, 64],
  2: [168, 92, 204],
  3: [214, 120, 86],
  4: [222, 168, 76],
  5: [96, 186, 120],
};
const STATES: Array<[number, string, string]> = [
  [5, 'Stands', '#60ba78'],
  [4, 'Crawls', '#dea84c'],
  [3, 'Tight', '#d67856'],
  [1, 'Beyond a pinch', '#c44840'],
  [2, 'Cut off', '#a85ccc'],
];

function decode(base64: string): Uint8Array {
  return Uint8Array.from(atob(base64), c => c.charCodeAt(0));
}

/** Coarseness comparison dashboard and census explorer for the CMS-only underground plan (#11209). */
export function UndergroundPage() {
  const [overview, setOverview] = useState<UndergroundOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selectedLevel, setSelectedLevel] = useSessionState<string>('ug:level', 'medium');

  useEffect(() => {
    apiRequest<UndergroundOverview>('/api/golden/underground')
      .then(setOverview)
      .catch(err => setError(err instanceof Error ? err.message : String(err)));
  }, []);

  if (error) return <Empty>{error}</Empty>;
  if (!overview) return <p class="muted">Loading underground plan…</p>;
  if (!overview.available || !overview.manifest) {
    return (
      <section class="panel">
        <PageHeading title="Underground plan" intro="CMS-only exploratory cave-system plan across coarseness levels (#11209)." />
        <Empty>
          No underground plan export found. Run <code>{overview.command}</code> from the repository root.
        </Empty>
      </section>
    );
  }

  const manifest = overview.manifest;
  const active: UndergroundLevelReport = manifest.levels.find(l => l.name === selectedLevel) ?? manifest.levels[0];

  return (
    <>
      <PageHeading
        title="Underground plan (#11209)"
        intro={`Provisional CMS-only exploration of cave systems from the continent map (seed ${manifest.world.seed}, ${(manifest.world.size / 1000).toFixed(0)} km, ${manifest.world.totalTiles} region tiles). Runtime gameplay and worldgen goldens are unchanged.`}
        action={
          <a class="button primary" href={atlasUrl()}>
            Inspect on the atlas
          </a>
        }
      />

      <section class="panel">
        <h2>Coarseness levels: time, memory, disk &amp; census</h2>
        <p class="muted">{manifest.note}</p>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Level</th>
                <th>Lattice</th>
                <th>Box (m)</th>
                <th>Wall time</th>
                <th>Avg / max tile</th>
                <th>Retained RAM (all tiles)</th>
                <th>Per land tile</th>
                <th>Gzip disk</th>
                <th>Systems</th>
                <th>Multi-box</th>
                <th>Boxes</th>
                <th>Mouths</th>
                <th>Links</th>
                <th>Upfront rooms</th>
              </tr>
            </thead>
            <tbody>
              {manifest.levels.map(lvl => {
                const m = lvl.metrics;
                const c = lvl.census;
                const st = lvl.settings;
                const isSelected = lvl.name === active.name;
                return (
                  <tr key={lvl.name} style={isSelected ? { background: 'rgba(77, 224, 201, 0.08)' } : undefined}>
                    <td>
                      <button type="button" onClick={() => setSelectedLevel(lvl.name)}>
                        {lvl.name}
                      </button>
                    </td>
                    <td>{st.siteSpacingMetres} m</td>
                    <td>{st.boxSize.join('×')}</td>
                    <td>{m.wallMs.toFixed(0)} ms</td>
                    <td>
                      {m.avgLandTileMs.toFixed(2)} / {m.maxTileMs.toFixed(1)} ms
                    </td>
                    <td>
                      <strong>{m.retainedInMemoryMB.toFixed(1)} MB</strong>
                    </td>
                    <td>{m.retainedPerLandTileKB.toFixed(1)} KB</td>
                    <td>
                      {m.totalGzipMB.toFixed(2)} MB{m.gzipGridMB > 0 ? ` (grid ${m.gzipGridMB.toFixed(2)})` : ''}
                    </td>
                    <td>{c.systems.toLocaleString()}</td>
                    <td>
                      {(c.multiBoxShare * 100).toFixed(1)}% ({c.multiBoxSystems.toLocaleString()})
                    </td>
                    <td>{c.totalBoxes.toLocaleString()}</td>
                    <td>{c.mouths.toLocaleString()}</td>
                    <td>{c.links.toLocaleString()}</td>
                    <td>{c.rooms.toLocaleString()}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </section>

      <section class="panel">
        <div class="section-heading">
          <h2>Level census: {active.name}</h2>
          <label>
            Inspect level{' '}
            <select value={active.name} onChange={event => setSelectedLevel((event.currentTarget as HTMLSelectElement).value)}>
              {manifest.levels.map(lvl => (
                <option key={lvl.name} value={lvl.name}>
                  {lvl.name} ({lvl.settings.siteSpacingMetres} m)
                </option>
              ))}
            </select>
          </label>
        </div>
        <p class="muted">{active.description}</p>
        <FactList
          facts={{
            'Scan lattice': `${active.settings.siteSpacingMetres} m`,
            'Box footprint': `${active.settings.boxSize.join(' × ')} m (${active.settings.boxChunks.join('×')} chunks)`,
            'Single / multi split': `${active.census.singleBoxSystems} single (${((1 - active.census.multiBoxShare) * 100).toFixed(1)}%) · ${active.census.multiBoxSystems} multi (${(active.census.multiBoxShare * 100).toFixed(1)}%)`,
            'Boxes per multi-box system': `avg ${active.census.avgBoxesPerMultiSystem}, max ${active.census.maxBoxesInSystem}`,
            'Boxes histogram': Object.entries(active.census.boxesHistogram)
              .map(([k, v]) => `${k} box: ${v}`)
              .join(' · '),
            'Depth bands (boxes)': Object.entries(active.census.byDepthBand)
              .map(([k, v]) => `${k}: ${v}`)
              .join(' · '),
            'Big-space markers': Object.entries(active.census.byBigSpace)
              .map(([k, v]) => `${k}: ${v}`)
              .join(' · '),
            'Per-tile field grid': active.settings.includeFieldGrid
              ? `yes (${active.metrics.rawGridMB.toFixed(1)} MB raw / ${active.metrics.gzipGridMB.toFixed(2)} MB gz)`
              : 'no (sparse graph only)',
          }}
        />
      </section>

      <section class="panel">
        <h2>Notable multi-box systems ({active.name})</h2>
        <p class="muted">Open any system to view its boxes, inter-box links, depth, and generate stitched multi-box KarstCave detail from its boundary ports.</p>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>System</th>
                <th>Boxes / links</th>
                <th>Mouths</th>
                <th>Max depth</th>
                <th>Solubility</th>
                <th>Biome</th>
                <th>Big spaces</th>
                <th>Nearest town</th>
                <th>Inspect</th>
              </tr>
            </thead>
            <tbody>
              {active.sampleMultiSystems.map(s => (
                <tr key={s.id}>
                  <td>
                    <a href={systemUrl(active.name, s.id)}>{s.id}</a>
                  </td>
                  <td>
                    {s.boxes} boxes · {s.links} links
                  </td>
                  <td>{s.mouths}</td>
                  <td>{s.maxDepth} m</td>
                  <td>{s.solubility.toFixed(2)}</td>
                  <td>{s.biome}</td>
                  <td>{s.bigSpaces.length ? s.bigSpaces.join(', ') : '—'}</td>
                  <td>{s.nearestTown ? `${s.nearestTown} (${s.nearestTownKm} km)` : '—'}</td>
                  <td>
                    <a href={systemUrl(active.name, s.id)}>System view</a> · <a href={atlasUrl(s.id)}>Atlas</a>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <section class="panel">
        <h2>By biome ({active.name})</h2>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Biome</th>
                <th>Systems</th>
                <th>Single</th>
                <th>Multi</th>
                <th>Multi %</th>
                <th>Boxes</th>
                <th>Mouths</th>
                <th>Avg depth</th>
                <th>Avg solubility</th>
              </tr>
            </thead>
            <tbody>
              {Object.entries(active.census.byBiome).map(([name, row]) => (
                <tr key={name}>
                  <td>{name}</td>
                  <td>{row.systems}</td>
                  <td>{row.singleBox}</td>
                  <td>{row.multiBox}</td>
                  <td>{(row.multiShare * 100).toFixed(1)}%</td>
                  <td>{row.boxes}</td>
                  <td>{row.mouths}</td>
                  <td>{row.avgDepth} m</td>
                  <td>{row.avgSolubility.toFixed(2)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>

      <section class="panel">
        <h2>By rock hardness ({active.name})</h2>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Hardness band</th>
                <th>Systems</th>
                <th>Single</th>
                <th>Multi</th>
                <th>Multi %</th>
                <th>Boxes</th>
                <th>Mouths</th>
                <th>Avg depth</th>
                <th>Avg solubility</th>
              </tr>
            </thead>
            <tbody>
              {Object.entries(active.census.byHardness).map(([band, row]) => (
                <tr key={band}>
                  <td>{band}</td>
                  <td>{row.systems}</td>
                  <td>{row.singleBox}</td>
                  <td>{row.multiBox}</td>
                  <td>{(row.multiShare * 100).toFixed(1)}%</td>
                  <td>{row.boxes}</td>
                  <td>{row.mouths}</td>
                  <td>{row.avgDepth} m</td>
                  <td>{row.avgSolubility.toFixed(2)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </>
  );
}

/** 2D projection canvas for stitched multi-box KarstCave detail. */
function DetailProjectionCanvas({ view, projection }: { view: SystemKarstView; projection: 'plan' | 'sideX' | 'sideZ' }) {
  const ref = useRef<HTMLCanvasElement>(null);
  const [gx, gy, gz] = view.size;
  const width = projection === 'sideZ' ? gz : gx;
  const height = projection === 'plan' ? gz : gy;
  const scale = Math.max(1, Math.min(4, Math.floor(560 / Math.max(width, height))));

  useEffect(() => {
    const canvas = ref.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d')!;
    const image = ctx.createImageData(width, height);
    const state = decode(projection === 'plan' ? view.plan.state : projection === 'sideX' ? view.sideX : view.sideZ);
    const top = projection === 'plan' ? new Int16Array(decode(view.plan.top).buffer) : null;
    for (let row = 0; row < height; row++) {
      for (let col = 0; col < width; col++) {
        const src = projection === 'plan' ? row * width + col : (gy - 1 - row) * width + col;
        let [r, g, b] = STATE_COLOURS[state[src]] ?? STATE_COLOURS[0];
        if (top && top[src] >= 0) {
          const light = 0.55 + (0.45 * top[src]) / gy;
          [r, g, b] = [r * light, g * light, b * light];
        }
        image.data.set([r, g, b, 255], (row * width + col) * 4);
      }
    }
    const off = document.createElement('canvas');
    off.width = width;
    off.height = height;
    off.getContext('2d')!.putImageData(image, 0, 0);
    canvas.width = width * scale;
    canvas.height = height * scale;
    ctx.imageSmoothingEnabled = false;
    ctx.drawImage(off, 0, 0, width * scale, height * scale);

    // Draw box boundaries & seam circles on top.
    ctx.strokeStyle = 'rgba(110, 231, 216, 0.7)';
    ctx.lineWidth = 1.5;
    for (const b of view.boxes) {
      if (projection === 'plan') {
        ctx.strokeRect(b.offset[0] * scale, b.offset[2] * scale, b.size[0] * scale, b.size[2] * scale);
      } else if (projection === 'sideX') {
        ctx.strokeRect(b.offset[0] * scale, (gy - b.offset[1] - b.size[1]) * scale, b.size[0] * scale, b.size[1] * scale);
      } else {
        ctx.strokeRect(b.offset[2] * scale, (gy - b.offset[1] - b.size[1]) * scale, b.size[2] * scale, b.size[1] * scale);
      }
    }
    for (const s of view.seams) {
      const px = projection === 'sideZ' ? s.at[2] * scale : s.at[0] * scale;
      const py = projection === 'plan' ? s.at[2] * scale : (gy - s.at[1]) * scale;
      ctx.strokeStyle = s.connectedBothSides && s.seamReachedCrawl ? '#60f098' : '#ff5c5c';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(px, py, Math.max(4, s.radius * scale), 0, Math.PI * 2);
      ctx.stroke();
    }
  }, [view, projection, scale]);

  return <canvas ref={ref} class="cave-canvas" />;
}

/** Interactive system view: 3D box/link/room plan + on-demand multi-box KarstCave detail stitched across boundary ports. */
export function SystemPage({ level, id }: { level: string; id: string }) {
  const [system, setSystem] = useState<UndergroundSystemDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [detail, setDetail] = useState<SystemKarstView | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState<string | null>(null);
  const [states, setStates] = useState<Record<number, boolean>>({ 1: true, 2: true, 3: true, 4: true, 5: true });
  const [showBoxes, setShowBoxes] = useState(true);
  const [ceiling, setCeiling] = useState(120);
  const [slice, setSlice] = useState(400);

  const planHost = useRef<HTMLDivElement>(null);
  const planHandle = useRef<PlanSceneHandle | null>(null);
  const detailHost = useRef<HTMLDivElement>(null);
  const detailHandle = useRef<DetailSceneHandle | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setSystem(null);
    setError(null);
    setDetail(null);
    setDetailError(null);
    apiRequest<{ level: string; system: UndergroundSystemDetail }>(
      `/api/golden/underground/${encodeURIComponent(level)}/systems/${encodeURIComponent(id)}`,
      { signal: controller.signal },
    )
      .then(res => setSystem(res.system))
      .catch(err => {
        if (!isAbortError(err)) setError(err instanceof Error ? err.message : String(err));
      });
    return () => controller.abort();
  }, [level, id]);

  useEffect(() => {
    if (!system) return;
    let cancelled = false;
    void import('../viewer/SystemScene')
      .then(mod => (planHost.current ? mod.mountSystemPlan(planHost.current, system) : null))
      .then(scene => {
        if (!scene) return;
        if (cancelled) {
          scene.dispose();
          return;
        }
        planHandle.current = scene;
      });
    return () => {
      cancelled = true;
      planHandle.current?.dispose();
      planHandle.current = null;
    };
  }, [system]);

  useEffect(() => {
    if (!detail) return;
    setCeiling(detail.size[1]);
    setSlice(detail.size[0]);
    let cancelled = false;
    void import('../viewer/SystemScene')
      .then(mod => (detailHost.current ? mod.mountSystemDetail(detailHost.current, detail) : null))
      .then(scene => {
        if (!scene) return;
        if (cancelled) {
          scene.dispose();
          return;
        }
        detailHandle.current = scene;
      });
    return () => {
      cancelled = true;
      detailHandle.current?.dispose();
      detailHandle.current = null;
    };
  }, [detail]);

  useEffect(() => detailHandle.current?.setStates(states), [states]);
  useEffect(() => detailHandle.current?.setBoxesVisible(showBoxes), [showBoxes]);
  useEffect(() => {
    if (detail) detailHandle.current?.setCut(ceiling, slice < detail.size[0] ? slice : null);
  }, [ceiling, slice, detail]);

  function generateDetail() {
    setDetailLoading(true);
    setDetailError(null);
    apiRequest<SystemKarstView>(`/api/golden/underground/${encodeURIComponent(level)}/detail/${encodeURIComponent(id)}`)
      .then(setDetail)
      .catch(err => setDetailError(err instanceof Error ? err.message : String(err)))
      .finally(() => setDetailLoading(false));
  }

  if (error) return <Empty>{error}</Empty>;
  if (!system)
    return (
      <>
        <Breadcrumb href={undergroundUrl()} label="Underground plan" />
        <p class="muted">Loading system {id}…</p>
      </>
    );

  return (
    <>
      <Breadcrumb href={undergroundUrl()} label="Underground plan" />
      <PageHeading
        title={`System ${system.id}`}
        intro={`Coarseness '${level}' · ${system.boxes.length} box${system.boxes.length === 1 ? '' : 'es'}, ${system.links.length} link${system.links.length === 1 ? '' : 's'}, ${system.mouths.length} surface mouth${system.mouths.length === 1 ? '' : 's'} under ${system.biome}.`}
        action={
          <a class="button" href={atlasUrl(system.id)}>
            Show on the atlas
          </a>
        }
      />

      <section class="panel">
        <h2>System overview</h2>
        <FactList
          facts={{
            Location: `x ${system.x} m, z ${system.z} m (tile ${system.tile.join(', ')})`,
            'Ground elevation': `${system.ground} m`,
            'Max depth': `${system.maxDepth} m`,
            Biome: system.biome,
            'Karst solubility': system.solubility.toFixed(3),
            'Rock hardness': system.hardness.toFixed(3),
            Discharge: `${system.discharge} m³/s`,
            Climate: `temp ${system.temperature.toFixed(2)}, moisture ${system.moisture.toFixed(2)}`,
            'Big spaces': system.bigSpaces.length ? system.bigSpaces.map(b => `${b.kind} (box ${b.box}, r=${b.radius} m)`).join(', ') : 'none',
          }}
        />
      </section>

      <section class="panel">
        <h2>3D plan diagram (boxes, water tables, ports &amp; upfront rooms)</h2>
        <div class="cave-3d-canvas" ref={planHost} />
        <small class="muted">
          Drag to orbit, wheel to zoom, right-drag to pan. Boxes are tinted by depth band (teal shallow, amber mid, magenta deep); torus rings mark shared-face ports; orange diamonds mark surface mouths.
        </small>
      </section>

      <section class="panel">
        <div class="section-heading">
          <h2>Stitched KarstCave detail (from plan ports)</h2>
          <button type="button" class="button primary" disabled={detailLoading} onClick={generateDetail}>
            {detailLoading ? `Generating ${system.boxes.length} box(es)…` : detail ? 'Regenerate detail' : `Generate KarstCave detail (${system.boxes.length} boxes)`}
          </button>
        </div>
        <p class="muted">
          Runs <code>KarstCave.Generate</code> on every box in this system using the plan&apos;s shared-face <code>BoundaryPorts</code> and water table (CMS-only), then stitches the boxes and tests port seam alignment and cross-box reachability.
        </p>
        {detailError ? <Empty>{detailError}</Empty> : null}
        {detail ? (
          <>
            <FactList
              facts={{
                'Boxes reached across seams': `${detail.stats.boxesReachedCrawl} / ${detail.stats.boxes}`,
                'Seams matched': `${detail.stats.seamsMatched} / ${detail.stats.links} (avg IoU ${detail.stats.avgSeamOverlapIoU.toFixed(2)})`,
                'Combined volume': `${detail.size.join(' × ')} m`,
                'Open space': `${detail.stats.openCubicMetres.toLocaleString()} m³ (${detail.stats.standCubicMetres.toLocaleString()} stand, ${detail.stats.crawlCubicMetres.toLocaleString()} crawl)`,
                'Generation time': `${detail.generateMs.toFixed(0)} ms KarstCave · ${detail.totalMs.toFixed(0)} ms total`,
              }}
            />
            {detail.seams.length > 0 ? (
              <div class="table-wrap" style={{ marginTop: '0.75rem' }}>
                <table>
                  <thead>
                    <tr>
                      <th>Link</th>
                      <th>Boxes</th>
                      <th>Kind</th>
                      <th>Radius</th>
                      <th>Side A open</th>
                      <th>Side B open</th>
                      <th>Seam IoU</th>
                      <th>Traversed</th>
                    </tr>
                  </thead>
                  <tbody>
                    {detail.seams.map(s => (
                      <tr key={s.index}>
                        <td>L{s.index}</td>
                        <td>
                          box {s.from} ↔ box {s.to}
                        </td>
                        <td>{s.kind}</td>
                        <td>{s.radius} m</td>
                        <td>{(s.openShareA * 100).toFixed(0)}%</td>
                        <td>{(s.openShareB * 100).toFixed(0)}%</td>
                        <td>
                          <strong>{(s.overlapIoU * 100).toFixed(0)}%</strong>
                        </td>
                        <td>{s.connectedBothSides && s.seamReachedCrawl ? 'yes' : 'no'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : null}
            <div class="cave-3d" style={{ marginTop: '1rem' }}>
              <div class="cave-3d-controls">
                {STATES.map(([kind, label, colour]) => (
                  <label class="toggle" key={kind}>
                    <input type="checkbox" checked={states[kind]} onChange={e => setStates({ ...states, [kind]: (e.currentTarget as HTMLInputElement).checked })} />
                    <i style={{ background: colour }} /> {label}
                  </label>
                ))}
                <label class="toggle">
                  <input type="checkbox" checked={showBoxes} onChange={e => setShowBoxes((e.currentTarget as HTMLInputElement).checked)} />
                  Boxes &amp; seams
                </label>
                <label>
                  Cut above {ceiling} m
                  <input type="range" min={0} max={detail.size[1]} step={1} value={ceiling} onInput={e => setCeiling(Number((e.currentTarget as HTMLInputElement).value))} />
                </label>
                <label>
                  Slice x {slice >= detail.size[0] ? 'off' : `${slice} m`}
                  <input type="range" min={0} max={detail.size[0]} step={1} value={slice} onInput={e => setSlice(Number((e.currentTarget as HTMLInputElement).value))} />
                </label>
              </div>
              <div class="cave-3d-canvas" ref={detailHost} />
            </div>
            <div class="cave-views" style={{ marginTop: '1rem' }}>
              <figure>
                <DetailProjectionCanvas view={detail} projection="plan" />
                <figcaption>Stitched plan (x across, z down)</figcaption>
              </figure>
              <figure>
                <DetailProjectionCanvas view={detail} projection="sideX" />
                <figcaption>Stitched section along z</figcaption>
              </figure>
              <figure>
                <DetailProjectionCanvas view={detail} projection="sideZ" />
                <figcaption>Stitched section along x</figcaption>
              </figure>
            </div>
          </>
        ) : null}
      </section>

      <section class="panel">
        <h2>Boxes ({system.boxes.length}) &amp; links ({system.links.length})</h2>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Box</th>
                <th>Grid</th>
                <th>World origin (m)</th>
                <th>Size (m)</th>
                <th>Ground / depth</th>
                <th>Depth band</th>
                <th>Water table (local Y)</th>
                <th>Upfront rooms / conduits</th>
              </tr>
            </thead>
            <tbody>
              {system.boxes.map(b => (
                <tr key={b.index}>
                  <td>#{b.index}</td>
                  <td>
                    ({b.grid[0]}, {b.grid[1]})
                  </td>
                  <td>{b.origin.join(', ')}</td>
                  <td>{b.size.join('×')}</td>
                  <td>
                    {b.ground} m / {b.depth} m
                  </td>
                  <td>{b.depthBand}</td>
                  <td>{b.waterLocalY} m</td>
                  <td>
                    {b.rooms.length} / {b.conduits.length}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {system.links.length > 0 ? (
          <div class="table-wrap" style={{ marginTop: '0.75rem' }}>
            <table>
              <thead>
                <tr>
                  <th>Link</th>
                  <th>Faces</th>
                  <th>Kind</th>
                  <th>Radius</th>
                  <th>Face U / V (m)</th>
                  <th>World point (m)</th>
                  <th>Water Y</th>
                </tr>
              </thead>
              <tbody>
                {system.links.map((l, idx) => (
                  <tr key={idx}>
                    <td>
                      #{l.from} ↔ #{l.to}
                    </td>
                    <td>
                      {l.fromFace} → {l.toFace}
                    </td>
                    <td>{l.kind}</td>
                    <td>{l.radius} m</td>
                    <td>
                      {l.faceU} / {l.faceV}
                    </td>
                    <td>{l.at.join(', ')}</td>
                    <td>{l.waterY} m</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </section>
    </>
  );
}
