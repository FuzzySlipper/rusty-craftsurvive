import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import { apiRequest, isAbortError } from '../api';
import { atlasUrl, caveUrl } from '../router';
import type { CaveView, GoldenCaveRow, GoldenPlaces } from '../types';
import { Breadcrumb, Empty, FactList, PageHeading } from '../components/ui';
import { ReviewPanel } from '../components/ReviewControls';
import { useSessionState } from '../useSessionState';

const PAGE = 100;
const KM = 1000;

// A sample's state in the plan and sections, as the exporter's CaveView writes it (a column shows its greatest).
const STATE = { rock: 0, narrow: 1, cutOff: 2, tight: 3, crawl: 4, stand: 5 } as const;
const STATE_COLOURS: Record<number, [number, number, number]> = {
  0: [26, 31, 28],
  1: [196, 72, 64],
  2: [168, 92, 204],
  3: [214, 120, 86],
  4: [222, 168, 76],
  5: [96, 186, 120],
};
const STATE_LABELS: Array<[number, string, string]> = [
  [STATE.stand, 'A body stands', 'Reached from where the way down joins the cave, with room to stand (the generator\'s stand radius).'],
  [STATE.crawl, 'A body crawls', 'Reached from where the way down joins the cave, but only crawling room (down to the generator\'s crawl radius).'],
  [STATE.tight, 'Tight: within resolution', 'Reached only through throats within the sampling\'s resolution of a crawl: they may or may not pass a body.'],
  [STATE.narrow, 'Beyond a pinch', 'Room a body would fit in that the cave joins to the rest only through pinches narrower than a crawl.'],
  [STATE.cutOff, 'Cut off', 'Open space no opening joins to the rest of the cave.'],
];
const PROFILE_COLOURS = ['#c44840', '#d67856', '#dea84c', '#60ba78'];
const PROFILE_LABELS = ['beyond a pinch', 'tight', 'crawl', 'stand'];
// Flags: what makes a layout worth a second look.
const NARROW_SHARE = 0.05;
const BOTTOM_GAP = 5;
const VERTICAL_SHARE = 0.4;
const ROUTE_REACH = 0.9;
const ROUTE_STAND = 0.5;

function bytes(base64: string): Uint8Array {
  return Uint8Array.from(atob(base64), c => c.charCodeAt(0));
}

/** Every cave mouth on the golden continent that has a way underground. */
export function CavesPage() {
  const [caves, setCaves] = useState<GoldenCaveRow[] | null>(null);
  const [places, setPlaces] = useState<GoldenPlaces | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [sort, setSort] = useSessionState('caves:sort', 'town');
  const [within, setWithin] = useSessionState('caves:within', 0);
  const [shown, setShown] = useState(PAGE);
  useEffect(() => {
    Promise.all([apiRequest<{ caves: GoldenCaveRow[] }>('/api/golden/caves'), apiRequest<GoldenPlaces>('/api/golden/places')])
      .then(([list, named]) => {
        setCaves(list.caves);
        setPlaces(named);
      })
      .catch(err => setError(err instanceof Error ? err.message : String(err)));
  }, []);
  const rows = useMemo(() => {
    if (!caves || !places) return [];
    const rows = caves.map(cave => {
      let nearest = { name: '', metres: Infinity };
      for (const town of places.towns) {
        const metres = Math.hypot(town.x - cave.x, town.z - cave.z);
        if (metres < nearest.metres) nearest = { name: town.name, metres };
      }
      return { cave, nearest };
    });
    const filtered = within > 0 ? rows.filter(row => row.nearest.metres <= within * KM) : rows;
    return filtered.sort((a, b) =>
      sort === 'depth' ? b.cave.setting[2] - a.cave.setting[2] : sort === 'id' ? a.cave.id.localeCompare(b.cave.id) : a.nearest.metres - b.nearest.metres,
    );
  }, [caves, places, sort, within]);
  if (error) return <Empty>{error}</Empty>;
  if (!caves) return <p class="muted">Loading…</p>;
  return (
    <>
      <PageHeading
        title="Caves"
        intro={`${caves.length} cave mouths on the golden continent have a way underground. Open one to generate its cave as the game would and see its layout.`}
      />
      <form class="filters" onSubmit={event => event.preventDefault()}>
        <label>
          Order
          <select value={sort} onChange={event => setSort((event.currentTarget as HTMLSelectElement).value)}>
            <option value="town">Nearest a town first</option>
            <option value="depth">Deepest first</option>
            <option value="id">By cell</option>
          </select>
        </label>
        <label>
          Within (km of a town, 0 for all)
          <input type="number" min={0} value={within} onChange={event => setWithin(Number((event.currentTarget as HTMLInputElement).value) || 0)} />
        </label>
        <output>{rows.length} caves</output>
      </form>
      <div class="table-wrap panel">
        <table>
          <thead>
            <tr>
              <th>Cave</th>
              <th>Where</th>
              <th>Ground</th>
              <th>Depth</th>
              <th>Climate above</th>
              <th>Nearest town</th>
            </tr>
          </thead>
          <tbody>
            {rows.slice(0, shown).map(({ cave, nearest }) => (
              <tr key={cave.id}>
                <td>
                  <a href={caveUrl(cave.id)}>{cave.id}</a>
                </td>
                <td>
                  {(cave.x / KM).toFixed(1)}, {(cave.z / KM).toFixed(1)} km <a href={atlasUrl(cave.id)}>atlas</a>
                </td>
                <td>{cave.ground} m</td>
                <td>{cave.setting[2].toFixed(0)} m</td>
                <td>
                  temperature {cave.setting[0].toFixed(2)}, moisture {cave.setting[1].toFixed(2)}
                </td>
                <td>
                  {nearest.name} ({(nearest.metres / KM).toFixed(1)} km)
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {rows.length > shown ? (
        <button type="button" onClick={() => setShown(shown + PAGE)}>
          Show more ({rows.length - shown} left)
        </button>
      ) : null}
    </>
  );
}

function findings(view: CaveView): Array<{ text: string; bad: boolean }> {
  const s = view.stats;
  const found: Array<{ text: string; bad: boolean }> = [];
  if (s.cutOffSpaces > 0) found.push({ text: `${s.cutOffSpaces} open spaces no opening joins to the cave (${s.cutOffCubicMetres} m³)`, bad: true });
  if (s.openCubicMetres > 0 && s.narrowCubicMetres / s.openCubicMetres > NARROW_SHARE)
    found.push({ text: `${s.narrowCubicMetres} m³ of open space lies beyond pinches narrower than a crawl`, bad: true });
  if (s.lowestOpen !== null && (s.lowestReached === null || s.lowestReached - s.lowestOpen > BOTTOM_GAP))
    found.push({ text: `The bottom is out of reach: open down to ${s.lowestOpen} m in the box, a body gets down to ${s.lowestReached ?? 'nowhere'} m`, bad: true });
  if (s.climbsOverReach > 0)
    found.push({ text: `The main route climbs ${s.longestClimb} m at once, more than a full stamina bar's ${s.climbReach} m`, bad: true });
  if (s.routeMetres > 0 && s.routeClimb / s.routeMetres > VERTICAL_SHARE)
    found.push({ text: `The main route is mostly vertical: ${Math.round((s.routeClimb / s.routeMetres) * 100)}% climbs`, bad: true });
  const routeBeyond = 1 - s.routeReachedShare - s.routeTightShare;
  if (routeBeyond > 1 - ROUTE_REACH) found.push({ text: `${Math.round(routeBeyond * 100)}% of the main route lies beyond pinches narrower than a crawl`, bad: true });
  if (s.routeTightShare > 0)
    found.push({ text: `${Math.round(s.routeTightShare * 100)}% of the main route is reached only through throats within ${s.resolutionMetres} m of a crawl`, bad: false });
  if (s.routeStandShare < ROUTE_STAND) found.push({ text: `Most of the main route is a crawl (${Math.round(s.routeStandShare * 100)}% stands)`, bad: false });
  if (s.deadEnds > 0) found.push({ text: `${s.deadEnds} dead ends, as the generator marks them (damp pockets)`, bad: false });
  if (!found.some(item => item.bad)) found.unshift({ text: 'Nothing flagged: the space is joined, the bottom and the route within reach', bad: false });
  return found;
}

type Projection = 'plan' | 'sideX' | 'sideZ';

/** One projection of the cave: each cell its state's colour, the plan shaded by its floor's height, with the cave's features drawn on. */
function CaveCanvas({ view, projection, scale }: { view: CaveView; projection: Projection; scale: number }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [sx, sy, sz] = view.grid;
  const step = view.step;
  const width = projection === 'sideZ' ? sz : sx;
  const height = projection === 'plan' ? sz : sy;
  useEffect(() => {
    const element = canvas.current;
    if (!element) return;
    const context = element.getContext('2d')!;
    const image = context.createImageData(width, height);
    const state = bytes(projection === 'plan' ? view.plan.state : projection === 'sideX' ? view.sideX : view.sideZ);
    const top = projection === 'plan' ? new Int16Array(bytes(view.plan.top).buffer) : null;
    for (let row = 0; row < height; row++)
      for (let column = 0; column < width; column++) {
        // Sections are drawn with height up: their rows are y from the top.
        const source = projection === 'plan' ? row * width + column : (sy - 1 - row) * width + column;
        const at = (row * width + column) * 4;
        let [r, g, b] = STATE_COLOURS[state[source]] ?? STATE_COLOURS[0];
        if (top && top[source] >= 0) {
          const light = 0.55 + (0.45 * top[source]) / sy;
          [r, g, b] = [r * light, g * light, b * light];
        }
        image.data.set([r, g, b, 255], at);
      }
    const offscreen = document.createElement('canvas');
    offscreen.width = width;
    offscreen.height = height;
    offscreen.getContext('2d')!.putImageData(image, 0, 0);
    element.width = width * scale;
    element.height = height * scale;
    context.imageSmoothingEnabled = false;
    context.drawImage(offscreen, 0, 0, width * scale, height * scale);

    // A cave point (x, y, z in the box's metres) on this projection.
    const k = scale / step;
    const at = (p: number[]): [number, number] =>
      projection === 'plan' ? [p[0] * k, p[2] * k] : projection === 'sideX' ? [p[0] * k, (sy - p[1] / step) * scale] : [p[2] * k, (sy - p[1] / step) * scale];
    const path = (points: number[][], colour: string, lineWidth: number, dash: number[] = []) => {
      if (points.length < 2) return;
      context.strokeStyle = colour;
      context.lineWidth = lineWidth;
      context.setLineDash(dash);
      context.beginPath();
      points.forEach((p, k) => (k ? context.lineTo(...at(p)) : context.moveTo(...at(p))));
      context.stroke();
      context.setLineDash([]);
    };
    const mark = (p: number[] | undefined, colour: string, label?: string, radius = 5) => {
      if (!p) return;
      const [x, y] = at(p);
      context.fillStyle = colour;
      context.strokeStyle = '#101412';
      context.lineWidth = 1.5;
      context.beginPath();
      context.arc(x, y, radius, 0, Math.PI * 2);
      context.fill();
      context.stroke();
      if (label) {
        context.font = '600 11px system-ui, sans-serif';
        context.fillStyle = '#f2eee5';
        context.fillText(label, x + radius + 2, y + 4);
      }
    };
    path(view.tendril, '#ff8a5c', 3, [6, 4]);
    // The main route, each leg coloured by whether its end is walked, reached with climbs, or out of reach.
    for (let leg = 1; leg < view.route.length; leg++) path([view.route[leg - 1], view.route[leg]], PROFILE_COLOURS[view.profile[leg][2]], 2.5);
    const f = view.features;
    for (const water of f.water ?? []) if (water.at) mark(water.at, '#3b8fd6', water.kind, 4);
    (f.chambers ?? []).forEach((p, n) => mark(p, '#ffd166', `C${n + 1}`));
    (f.sinks ?? []).forEach((p, n) => mark(p, '#72c4ff', `S${n + 1}`, 4));
    (f.deadEnds ?? []).forEach(p => mark(p, '#9a8f7d', 'dead end', 3.5));
    mark(f.spring, '#4de0c9', 'spring');
    mark(f.maze, '#c5b4e3', 'maze');
    mark(f.ruin, '#b88cf0', 'ruin');
    if (view.start) mark(view.start, '#ffffff', 'start', 6);
  }, [view, projection, scale]);
  return <canvas ref={canvas} class="cave-canvas" />;
}

/** The main route's profile: height against distance along it, each point walked, reached with climbs, or out of reach. */
function RouteProfile({ view }: { view: CaveView }) {
  const width = 760;
  const height = 220;
  const pad = 34;
  const points = view.profile;
  if (points.length < 2) return <Empty>The generator laid no main route.</Empty>;
  const length = points[points.length - 1][0] || 1;
  const ys = points.map(p => p[1]);
  const low = Math.min(...ys) - 2;
  const high = Math.max(...ys) + 2;
  const sx = (d: number) => pad + ((width - 2 * pad) * d) / length;
  const sy = (y: number) => height - pad - ((height - 2 * pad) * (y - low)) / (high - low);
  const reach = view.stats.climbReach;
  return (
    <svg class="route-profile" viewBox={`0 0 ${width} ${height}`} role="img" aria-label="The main route's height along its length">
      <line x1={pad} y1={height - pad} x2={width - pad} y2={height - pad} stroke="#39443c" />
      <line x1={pad} y1={pad} x2={pad} y2={height - pad} stroke="#39443c" />
      {points.slice(1).map((p, k) => (
        <line key={k} x1={sx(points[k][0])} y1={sy(points[k][1])} x2={sx(p[0])} y2={sy(p[1])} stroke={PROFILE_COLOURS[p[2]]} stroke-width={3} />
      ))}
      <line x1={width - pad - 6} y1={sy(low + 2)} x2={width - pad - 6} y2={sy(low + 2 + reach)} stroke="#f2eee5" stroke-width={2} />
      <text x={width - pad - 10} y={sy(low + 2 + reach / 2)} fill="#b1bdb2" font-size="11" text-anchor="end">
        full stamina climb {reach} m
      </text>
      <text x={pad} y={height - 10} fill="#b1bdb2" font-size="11">
        0 m
      </text>
      {PROFILE_LABELS.map((label, n) => (
        <text key={label} x={pad + 10 + n * 130} y={pad - 12} fill={PROFILE_COLOURS[n]} font-size="12">
          ■ {label}
        </text>
      ))}
      <text x={width - pad} y={height - 10} fill="#b1bdb2" font-size="11" text-anchor="end">
        {length.toFixed(0)} m along the route
      </text>
      <text x={pad - 4} y={sy(high) + 4} fill="#b1bdb2" font-size="11" text-anchor="end">
        {high.toFixed(0)}
      </text>
      <text x={pad - 4} y={sy(low) + 4} fill="#b1bdb2" font-size="11" text-anchor="end">
        {low.toFixed(0)}
      </text>
    </svg>
  );
}

/** A cave under one mouth, simplified: where its floor is and whether a player gets to it. */
export function CavePage({ id }: { id: string }) {
  const [view, setView] = useState<CaveView | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    setView(null);
    setError(null);
    apiRequest<CaveView>(`/api/golden/caves/${encodeURIComponent(id)}`, { signal: controller.signal })
      .then(setView)
      .catch(err => {
        if (!isAbortError(err)) setError(err instanceof Error ? err.message : String(err));
      });
    return () => controller.abort();
  }, [id]);
  if (error) return <Empty>{error}</Empty>;
  if (!view)
    return (
      <>
        <Breadcrumb href="#caves" label="Caves" />
        <p class="muted">Generating the cave under {id} as the game does (about ten seconds the first time)…</p>
      </>
    );
  const s = view.stats;
  const [sx, sy, sz] = view.size;
  const scale = Math.max(1, Math.round(640 / Math.max(view.grid[0], view.grid[2])));
  return (
    <>
      <Breadcrumb href="#caves" label="Caves" />
      <PageHeading
        title={`Cave under ${id}`}
        intro={`Generated as the walking session admits it: the karst cave under the mouth, with the way down from its tendril. Box ${sx} × ${sy} × ${sz} m.`}
        action={
          <a class="button" href={atlasUrl(id)}>
            Show on the atlas
          </a>
        }
      />
      <section class="panel">
        <h2>What stands out</h2>
        <ul class="findings">
          {findings(view).map(item => (
            <li key={item.text} class={item.bad ? 'bad' : ''}>
              {item.text}
            </li>
          ))}
        </ul>
        <FactList
          facts={{
            'Open space': `${s.openCubicMetres} m³`,
            Stands: `${s.standCubicMetres} m³`,
            Crawls: `${s.crawlCubicMetres} m³`,
            Tight: `${s.tightCubicMetres} m³`,
            'Beyond pinches': `${s.narrowCubicMetres} m³`,
            'Cut off': `${s.cutOffCubicMetres} m³`,
            'Lowest open / reached': `${s.lowestOpen ?? '-'} / ${s.lowestReached ?? '-'} m`,
            'Main route': `${s.routeMetres} m: ${s.routeWalk} walk, ${s.routeSteep} steep, ${s.routeClimb} climb`,
            'Longest climb': `${s.longestClimb} m`,
            Descent: `${s.routeDescent} m`,
            'Cave depth': `${view.setting[2]} m`,
            Generated: `${view.generateMs} + ${view.viewMs} ms`,
          }}
        />
      </section>
      <section class="panel">
        <h2>Plan and sections</h2>
        <p class="muted">
          Looking down, and from two sides, in the cave box's own axes (the box is turned to face its mouth in the world). Each column shows the best of its{' '}
          {view.step} m samples; the plan is lighter where the reached space is higher. Red is open space beyond a pinch, purple space no opening joins.
        </p>
        <ul class="legend cave-legend">
          {STATE_LABELS.map(([state, label, about]) => (
            <li key={state} title={about}>
              <i style={{ background: `rgb(${STATE_COLOURS[state].join(',')})` }} /> {label}
            </li>
          ))}
          <li>
            <i style={{ background: '#ff8a5c' }} /> Way in (tendril)
          </li>
          <li>
            <i style={{ background: '#ffd166' }} /> Chambers C1…
          </li>
          <li>
            <i style={{ background: '#72c4ff' }} /> Sinks S1…
          </li>
          <li>
            <i style={{ background: '#3b8fd6' }} /> Water
          </li>
          <li>
            <i style={{ background: '#9a8f7d' }} /> Dead ends
          </li>
        </ul>
        <div class="cave-views">
          <figure>
            <CaveCanvas view={view} projection="plan" scale={scale} />
            <figcaption>Plan (x across, z down)</figcaption>
          </figure>
          <figure>
            <CaveCanvas view={view} projection="sideX" scale={scale} />
            <figcaption>Section along z (x across, height up)</figcaption>
          </figure>
          <figure>
            <CaveCanvas view={view} projection="sideZ" scale={scale} />
            <figcaption>Section along x (z across, height up)</figcaption>
          </figure>
        </div>
      </section>
      <section class="panel">
        <h2>The main route</h2>
        <p class="muted">
          The route the generator laid from the way in (the way down first), its height along its length: green where a body stands, amber where it crawls,
          orange where it is reached only through tight throats, red where a pinch keeps a body out. The bar is the tallest face a starting character's full stamina bar climbs; a steeper leg taller than it is a climb the
          player cannot make on one bar.
        </p>
        <RouteProfile view={view} />
      </section>
      <ReviewPanel category="golden" id={id} heading="Notes on this cave" />
      <section class="panel">
        <details>
          <summary>How reach is worked out</summary>
          <p class="muted">
            As the karst generator judges passage: by whether a body fits, not by walking. The cave's own field is sampled every {view.step} m (open where it is
            not negative), each open sample gets its clearance (the distance to the rock's surface, where the field crosses zero), and the space a body fits
            through is filled from where the game's way down from the tendril joins the generator's route:
            a body stands where the clearance is at least {s.standRadius} m and crawls down to {s.crawlRadius} m (KarstCave's own radii). Clearance runs to where the field crosses zero, so it is finer than the
            samples, but a body's centre is judged at sample centres, up to {s.resolutionMetres} m off a throat's axis: space reached only through
            throats within that of a crawl is marked tight, since they may or may not pass a body. Gravity is judged
            along the main route: each climb against a full stamina bar ({s.climbReach} m). The route is the prepared one, starting with the way down the game
            carves from the tendril.
          </p>
        </details>
        <details>
          <summary>The generator's own readout</summary>
          <pre>{view.verdict}</pre>
        </details>
      </section>
    </>
  );
}
