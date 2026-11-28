import { useEffect, useRef, useState } from 'preact/hooks';
import { apiRequest, isAbortError } from '../api';
import { atlasUrl, systemUrl, undergroundUrl } from '../router';
import type {
  SystemKarstView,
  SystemPortRoute,
  UndergroundBox,
  UndergroundCensusBucket,
  UndergroundLevelReport,
  UndergroundMouth,
  UndergroundOverview,
  UndergroundQuantiles,
  UndergroundSystemDetail,
} from '../types';
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

/** A distribution's quantiles in one cell. */
function q(x: UndergroundQuantiles | undefined, unit = ' m'): string {
  if (!x || !x.count) return '—';
  return `${x.p10} / ${x.p50} / ${x.p90} · max ${x.max}${unit}`;
}

function pairs(record: Record<string, number> | undefined): string {
  return record ? Object.entries(record).map(([k, v]) => `${k}: ${v.toLocaleString()}`).join(' · ') : '—';
}

function pct(x: number | undefined): string {
  return x === undefined ? '—' : `${(x * 100).toFixed(1)}%`;
}

/** The spread of systems over a set of bands against each band's share of land (round 3). */
function SpreadTable({ title, rows }: { title: string; rows: Record<string, UndergroundCensusBucket> | undefined }) {
  if (!rows) return null;
  return (
    <section class="panel">
      <h2>{title}</h2>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Band</th>
              <th>Share of land</th>
              <th>Limestone in band</th>
              <th>Share of systems</th>
              <th>Systems / 1,000 km²</th>
              <th>Systems</th>
              <th>Stacked</th>
              <th>From regional floor</th>
              <th>Mean floor below ground</th>
            </tr>
          </thead>
          <tbody>
            {Object.entries(rows).map(([name, row]) => (
              <tr key={name}>
                <td>{name}</td>
                <td>{pct(row.landShare)}</td>
                <td>{pct(row.karstShareOfBand)}</td>
                <td>
                  <strong>{pct(row.systemShare)}</strong>
                </td>
                <td>{row.systemsPer1000Km2 ?? '—'}</td>
                <td>{row.systems.toLocaleString()}</td>
                <td>{row.stacked ?? '—'}</td>
                <td>{row.fromRegionalFloor ?? '—'}</td>
                <td>{row.avgDepth} m</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

/** Plan dashboard and census explorer for the CMS-only underground plan (#11209). */
export function UndergroundPage() {
  const [overview, setOverview] = useState<UndergroundOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selectedLevel, setSelectedLevel] = useSessionState<string>('ug:level', 'fine');

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
        <PageHeading title="Underground plan" intro="CMS-only exploratory cave-system plan (#11209)." />
        <Empty>
          No underground plan export found. Run <code>{overview.command}</code> from the repository root.
        </Empty>
      </section>
    );
  }

  const manifest = overview.manifest;
  const active: UndergroundLevelReport = manifest.levels.find(l => l.name === selectedLevel) ?? manifest.levels[0];
  const c = active.census;
  const depth = c.depth;

  return (
    <>
      <PageHeading
        title="Underground plan (#11209)"
        intro={`Provisional CMS-only exploration of cave systems from the continent map (seed ${manifest.world.seed}, ${(manifest.world.size / 1000).toFixed(0)} km, ${manifest.world.totalTiles} region tiles), boxes buried under the ${manifest.ground ?? 'smooth'} ground. Runtime gameplay and worldgen goldens are unchanged.`}
        action={
          <a class="button primary" href={atlasUrl()}>
            Inspect on the atlas
          </a>
        }
      />

      <section class="panel">
        <h2>Levels: time, memory, disk &amp; counts</h2>
        <p class="muted">{manifest.note}</p>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Level</th>
                <th>Lattice</th>
                <th>Box (m)</th>
                <th>Export time (gen + io)</th>
                <th>Retained RAM</th>
                <th>Gzip disk</th>
                <th>Systems</th>
                <th>Stacked</th>
                <th>Boxes</th>
                <th>Shafts</th>
                <th>Mouths</th>
                <th>Box overlaps</th>
              </tr>
            </thead>
            <tbody>
              {manifest.levels.map(lvl => {
                const m = lvl.metrics;
                const lc = lvl.census;
                const stacked = lc.levelsHistogram ? lc.systems - (lc.levelsHistogram['1'] ?? 0) : null;
                return (
                  <tr key={lvl.name} style={lvl.name === active.name ? { background: 'rgba(77, 224, 201, 0.08)' } : undefined}>
                    <td>
                      <button type="button" onClick={() => setSelectedLevel(lvl.name)}>
                        {lvl.name}
                      </button>
                    </td>
                    <td>{lvl.settings.siteSpacingMetres} m</td>
                    <td>{lvl.settings.boxSize.join('×')}</td>
                    <td>
                      <strong>{(m.exportMs ?? m.wallMs).toFixed(0)} ms</strong>
                      {m.generateMs !== undefined && m.serializeMs !== undefined ? (
                        <small class="muted">
                          {' '}
                          ({m.generateMs.toFixed(0)} + {m.serializeMs.toFixed(0)})
                        </small>
                      ) : null}
                    </td>
                    <td>
                      {m.retainedInMemoryMB.toFixed(1)} MB{m.retainedIncludesRegionTiles ? <small class="muted"> (incl. region tiles)</small> : null}
                    </td>
                    <td>{m.totalGzipMB.toFixed(2)} MB</td>
                    <td>{lc.systems.toLocaleString()}</td>
                    <td>{stacked === null ? '—' : `${stacked.toLocaleString()} (${((stacked / Math.max(1, lc.systems)) * 100).toFixed(1)}%)`}</td>
                    <td>{lc.totalBoxes.toLocaleString()}</td>
                    <td>{lc.shafts?.toLocaleString() ?? '—'}</td>
                    <td>{lc.mouths.toLocaleString()}</td>
                    <td>{lc.overlapPairs ?? '—'}</td>
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
            'Box size': `${active.settings.boxSize.join(' × ')} m; at most ${active.settings.maxBoxesPerSystem} boxes and ${active.settings.maxLevels ?? 1} levels a system`,
            'Levels per system': pairs(c.levelsHistogram),
            'Boxes per system': pairs(c.boxesHistogram),
            'Mouths by kind': pairs(c.mouthKinds),
            'Systems by levels with a mouth': pairs(c.mouthLevelsHistogram),
            'Links by kind': pairs(c.linkKinds),
            'Styles (climate)': pairs(c.styles),
            'Reach their base level': c.reachesBaseLevel !== undefined ? `${c.reachesBaseLevel.toLocaleString()} of ${c.systems.toLocaleString()}` : undefined,
            'Placed by the regional floor': c.fromRegionalFloor,
            'Lowest floor held by the world floor (y = −74)': c.floorClampedAtWorldFloor,
            'Kept out by box overlap (in tile) / overlapping pairs left': `${c.rejectedForOverlap ?? '—'} / ${c.overlapPairs ?? '—'}`,
            'Limestone (karst) country': c.land ? `${c.land.karstKm2.toLocaleString()} of ${c.land.landKm2.toLocaleString()} km² of land (${pct(c.land.karstShare)}); ${c.land.systemsPer1000KarstKm2} systems per 1,000 km² of it` : undefined,
            'Big spaces': pairs(c.byBigSpace),
          }}
        />
      </section>

      {depth ? (
        <section class="panel">
          <h2>Depth ({active.name})</h2>
          <p class="muted">Below the ground over each box (p10 / median / p90 · max). The base level is the lowest ground within 1.4 km; the lowest level runs at it where the five levels allowed reach.</p>
          <FactList
            facts={{
              'Box top below ground': q(depth.boxTopBelowGround),
              'Box floor below ground': q(depth.boxFloorBelowGround),
              'System vertical span': q(depth.systemSpan),
              'Relief above the base level': q(depth.relief),
              'Box floors by 32 m band': pairs(depth.boxFloorHistogram),
              'Side-mouth tendril': q(c.sideMouths?.tendril),
              'Side-mouth mean slope (rise / run)': q(c.sideMouths?.slope, ''),
            }}
          />
          <div class="table-wrap" style={{ marginTop: '0.75rem' }}>
            <table>
              <thead>
                <tr>
                  <th>Ground elevation</th>
                  <th>Systems</th>
                  <th>Floor below ground (median / p90 / max)</th>
                  <th>Vertical span (median / p90 / max)</th>
                </tr>
              </thead>
              <tbody>
                {Object.entries(depth.byElevationBand).map(([band, row]) => (
                  <tr key={band}>
                    <td>{band}</td>
                    <td>{row.systems.toLocaleString()}</td>
                    <td>
                      {row.floorBelowGroundP50} / {row.floorBelowGroundP90} / {row.floorBelowGroundMax} m
                    </td>
                    <td>
                      {row.spanP50} / {row.spanP90} / {row.spanMax} m
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : null}

      <SpreadTable title={`By elevation band (${active.name})`} rows={c.byElevationBand} />
      <SpreadTable title={`By moisture band (${active.name})`} rows={c.byMoistureBand} />
      <SpreadTable title={`By biome (${active.name})`} rows={c.byBiome} />

      {c.styleByBiome ? (
        <section class="panel">
          <h2>Style by biome ({active.name})</h2>
          <p class="muted">The rock decides where caves are; the climate decides what they are like: active river caves in wet ground, old dry (relict) ones in dry ground, hypogene ones where dry ground stands far above its base level, ice caves in cold ground.</p>
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Biome</th>
                  {['ActiveRiver', 'Relict', 'Hypogene', 'Ice'].map(k => (
                    <th key={k}>{k}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {Object.entries(c.styleByBiome).map(([biome, row]) => (
                  <tr key={biome}>
                    <td>{biome}</td>
                    {['ActiveRiver', 'Relict', 'Hypogene', 'Ice'].map(k => (
                      <td key={k}>{row[k] ?? 0}</td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : null}

      <section class="panel">
        <h2>Sample systems ({active.name})</h2>
        <p class="muted">A few of each kind the round asked about. Open one for its side-on section, its 3D plan and stitched KarstCave detail.</p>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>System</th>
                <th>Why</th>
                <th>Boxes / levels</th>
                <th>Span / floor below ground</th>
                <th>Mouths</th>
                <th>Style</th>
                <th>Biome</th>
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
                  <td>{s.why ?? '—'}</td>
                  <td>
                    {s.boxes} / {s.levels ?? 1}
                  </td>
                  <td>
                    {s.spanMetres ?? '—'} m / {s.floorBelowGround ?? s.maxDepth} m
                  </td>
                  <td>{s.mouthKinds ? s.mouthKinds.join(', ') : s.mouths}</td>
                  <td>{s.style ?? '—'}</td>
                  <td>{s.biome}</td>
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
    </>
  );
}

const AXIS: Record<string, [number, number]> = { PosX: [0, 1], NegX: [0, -1], PosZ: [2, 1], NegZ: [2, -1] };
const MOUTH_FILL: Record<string, string> = { Hillside: '#ff8a5c', Sinkhole: '#ffd166', Resurgence: '#4de0c9', Cliff: '#ff5cf0' };

/** A system's side-on section along its main axis (round 3): ground, base level, boxes by level, water, shafts and mouths. */
function SystemSection({ system }: { system: UndergroundSystemDetail }) {
  const section = system.section;
  if (!section) return <Empty>This export has no section for the system (re-export with round 3's exporter).</Empty>;
  const [axis, sign] = AXIS[section.axis] ?? [0, 1];
  const site = axis === 0 ? system.x : system.z;
  const along = (v: number) => (v - site) * sign;
  const ground = section.ground.map((g, i) => [section.start + i * section.step, g] as const);
  const lateral = (b: UndergroundBox) => (axis === 0 ? b.grid[2] : b.grid[0]) !== 0;
  const portAt = (m: UndergroundMouth): [number, number, number] => {
    const b = system.boxes[m.box];
    const [ox, oy, oz] = b.origin;
    const [sx, sy, sz] = b.size;
    switch (m.face) {
      case 'PosY':
        return [ox + m.portU, oy + sy, oz + m.portV];
      case 'PosX':
        return [ox + sx, m.portY, oz + m.portU];
      case 'NegX':
        return [ox, m.portY, oz + m.portU];
      case 'PosZ':
        return [ox + m.portU, m.portY, oz + sz];
      default:
        return [ox + m.portU, m.portY, oz];
    }
  };

  let minS = section.start;
  let maxS = section.start + (section.ground.length - 1) * section.step;
  let minY = Math.min(...system.boxes.map(b => b.origin[1]), system.baseLevelY ?? Infinity) - 12;
  let maxY = Math.max(...section.ground, ...system.boxes.map(b => b.origin[1] + b.size[1])) + 12;
  minS -= 8;
  maxS += 8;
  const width = 900;
  const scale = width / (maxS - minS);
  const height = Math.max(160, Math.min(720, (maxY - minY) * scale));
  const yScale = height / (maxY - minY);
  const X = (s: number) => (s - minS) * scale;
  const Y = (y: number) => (maxY - y) * yScale;
  const exaggeration = yScale / scale;

  return (
    <figure style={{ margin: 0 }}>
      <svg viewBox={`0 0 ${width} ${height}`} style={{ width: '100%', maxWidth: `${width}px`, background: '#101412', borderRadius: '6px' }}>
        <polygon
          points={[...ground.map(([s, g]) => `${X(s)},${Y(g)}`), `${X(maxS)},${height}`, `${X(minS)},${height}`].join(' ')}
          fill="#2a2620"
          stroke="none"
        />
        <polyline points={ground.map(([s, g]) => `${X(s)},${Y(g)}`).join(' ')} fill="none" stroke="#c7966c" stroke-width="2" />
        {system.baseLevelY !== undefined ? (
          <>
            <line x1={0} x2={width} y1={Y(system.baseLevelY)} y2={Y(system.baseLevelY)} stroke="#3b8fd6" stroke-dasharray="6 4" />
            <text x={6} y={Y(system.baseLevelY) - 4} fill="#7fb8e8" font-size="11">
              base level {system.baseLevelY} m
            </text>
          </>
        ) : null}
        {system.boxes.map(b => {
          const s0 = along(b.origin[axis] + (sign > 0 ? 0 : b.size[axis]));
          const s1 = along(b.origin[axis] + (sign > 0 ? b.size[axis] : 0));
          const off = lateral(b);
          return (
            <g key={b.index}>
              <rect
                x={X(Math.min(s0, s1))}
                y={Y(b.origin[1] + b.size[1])}
                width={Math.abs(s1 - s0) * scale}
                height={b.size[1] * yScale}
                fill={off ? 'rgba(77,224,201,0.04)' : 'rgba(77,224,201,0.10)'}
                stroke="#4de0c9"
                stroke-dasharray={off ? '4 3' : undefined}
              />
              <line
                x1={X(Math.min(s0, s1)) + 2}
                x2={X(Math.max(s0, s1)) - 2}
                y1={Y(b.origin[1] + b.waterLocalY)}
                y2={Y(b.origin[1] + b.waterLocalY)}
                stroke="#3b8fd6"
                stroke-opacity="0.7"
              />
              <text x={X(Math.min(s0, s1)) + 4} y={Y(b.origin[1] + b.size[1]) + 13} fill="#9fe8dc" font-size="11">
                #{b.index} L{b.grid[1]}
              </text>
            </g>
          );
        })}
        {system.links.map((l, i) =>
          l.kind === 'Shaft' ? (
            <line
              key={i}
              x1={X(along(l.at[axis]))}
              x2={X(along(l.at[axis]))}
              y1={Y(l.at[1] + 10)}
              y2={Y(l.at[1] - 10)}
              stroke="#f2f2f2"
              stroke-width="3"
            />
          ) : (
            <circle key={i} cx={X(along(l.at[axis]))} cy={Y(l.at[1])} r={3.5} fill="#7ef0e6" />
          ),
        )}
        {system.mouths.map((m, i) => {
          const p = portAt(m);
          const col = MOUTH_FILL[m.kind] ?? '#ff8a5c';
          return (
            <g key={i}>
              <line x1={X(along(p[axis]))} y1={Y(p[1])} x2={X(along(m.at[axis]))} y2={Y(m.at[1])} stroke={col} stroke-width="2" />
              <circle cx={X(along(m.at[axis]))} cy={Y(m.at[1])} r={5} fill={col} stroke="#101412" />
              <text x={X(along(m.at[axis])) + 7} y={Y(m.at[1]) - 6} fill={col} font-size="11">
                {m.kind} L{m.level}
              </text>
            </g>
          );
        })}
      </svg>
      <figcaption class="muted">
        Section along {section.axis} (toward the base level) through the anchor; {exaggeration.toFixed(1)}× vertical scale. Teal boxes by level (dashed: a side branch off the axis), blue lines the water
        tables, white bars shafts, dots side passages; coloured lines run from each mouth&apos;s port along its tendril to where it comes out.
      </figcaption>
    </figure>
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
      ctx.strokeStyle = s.seamReachedCrawl ? '#60f098' : s.seamReachedTight ? '#f2b84b' : '#ff5c5c';
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(px, py, Math.max(4, s.radius * scale), 0, Math.PI * 2);
      ctx.stroke();
    }
  }, [view, projection, scale]);

  return <canvas ref={ref} class="cave-canvas" />;
}

/** What the walkability passes carved for one side of a seam: the fallback tubes, if any, and the anchor's joining tube. */
function routeText(r: SystemPortRoute | undefined): string {
  if (!r || r.missing) return '—';
  const fired = [r.standFallback ? 'standing tube' : '', r.pocketConnector ? 'pocket connector' : ''].filter(Boolean).join(' + ');
  return `${fired || 'none'} (join ${r.anchorTubeMetres} m, ${r.before}→${r.after} m)`;
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
            Style: system.style,
            Levels: system.levels,
            'Base level / relief': system.baseLevelY !== undefined ? `${system.baseLevelY} m / ${system.relief} m${system.reachesBaseLevel ? '' : ' (levels stop above it)'}` : undefined,
            'Box floors / tops (world Y)': system.floorY !== undefined ? `${system.floorY} m to ${system.topY} m (span ${((system.topY ?? 0) - system.floorY).toFixed(0)} m)` : undefined,
            'Deepest floor below ground': `${system.maxDepth} m`,
            'Placed by the regional floor': system.fromRegionalFloor,
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
        <h2>Side-on section</h2>
        <SystemSection system={system} />
        {system.mouths.length ? (
          <div class="table-wrap" style={{ marginTop: '0.75rem' }}>
            <table>
              <thead>
                <tr>
                  <th>Mouth</th>
                  <th>Kind</th>
                  <th>Level / box</th>
                  <th>Face</th>
                  <th>Port Y</th>
                  <th>Comes out at</th>
                  <th>Tendril</th>
                  <th>Mean slope</th>
                </tr>
              </thead>
              <tbody>
                {system.mouths.map((m, i) => (
                  <tr key={i}>
                    <td>M{i}</td>
                    <td>{m.kind}</td>
                    <td>
                      L{m.level} / #{m.box}
                    </td>
                    <td>{m.face}</td>
                    <td>{m.portY} m</td>
                    <td>{m.at.join(', ')}</td>
                    <td>{m.face === 'PosY' ? `${m.tendril} m up` : `${m.tendril} m`}</td>
                    <td>{m.slope}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
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
                'Boxes reached standing': detail.stats.boxesReachedStand !== undefined ? `${detail.stats.boxesReachedStand} / ${detail.stats.boxes}` : undefined,
                'Seams crossed standing': detail.stats.seamsTraversedStand !== undefined ? `${detail.stats.seamsTraversedStand} / ${detail.stats.links}` : undefined,
                'Mouths reached standing / crawling': detail.stats.mouths !== undefined ? `${detail.stats.mouthsReachedStand} / ${detail.stats.mouthsReachedCrawl} of ${detail.stats.mouths}` : undefined,
                'Fallback tubes': detail.stats.requiredRoutes !== undefined
                  ? `${detail.stats.seamsWithFallback} of ${detail.stats.links} seams, ${detail.stats.mouthsWithFallback} mouths; ${detail.stats.standFallbacks} standing-width and ${detail.stats.pocketConnectors} pocket connectors over ${detail.stats.requiredRoutes} required routes`
                  : undefined,
                'Shaft pitches': detail.seams.filter(s => s.vertical).map(s => `L${s.index} ${s.pitchMetres} m`).join(', ') || undefined,
                'Boxes reached (crawl)': `${detail.stats.boxesReachedCrawl} / ${detail.stats.boxes}`,
                ...(detail.stats.boxesReachedTight !== undefined
                  ? { 'Boxes reached (incl. tight)': `${detail.stats.boxesReachedTight} / ${detail.stats.boxes}` }
                  : {}),
                'Seams shape-matched': `${detail.stats.seamsMatched} / ${detail.stats.links} (avg IoU ${detail.stats.avgSeamOverlapIoU.toFixed(2)})`,
                'Seams traversed (crawl)': `${detail.stats.seamsTraversedCrawl ?? detail.seams.filter(s => s.seamReachedCrawl).length} / ${detail.stats.links}`,
                'Combined volume': `${detail.size.join(' × ')} m`,
                'Open space': `${detail.stats.openCubicMetres.toLocaleString()} m³ (${detail.stats.standCubicMetres.toLocaleString()} stand, ${detail.stats.crawlCubicMetres.toLocaleString()} crawl, ${detail.stats.tightCubicMetres.toLocaleString()} tight)`,
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
                      <th>Fallback (side A · side B)</th>
                      <th>Pitch</th>
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
                        <td>{s.seamReachedStand ? 'standing' : s.seamReachedCrawl ? 'crawl' : s.seamReachedTight ? 'tight only' : 'no'}</td>
                        <td>
                          {routeText(s.routeA)} · {routeText(s.routeB)}
                        </td>
                        <td>{s.pitchMetres !== undefined ? `${s.pitchMetres} m` : '—'}</td>
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
                    ({b.grid.join(', ')})
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
