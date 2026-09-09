export type ProcgenPoint = Readonly<{ x: number; y: number; z: number }>;
export type ProcgenState = Readonly<{
  room: string;
  switchOpen: boolean;
  token: boolean;
  spent: boolean;
  recovered: boolean;
  observed: boolean;
}>;
export type ProcgenReadout = Readonly<{
  revision: number;
  identity: string;
  source: string;
  seed: string;
  motif: string;
  status: string;
  error: string;
  active: boolean;
  mode: string;
  state: ProcgenState;
  modelState: ProcgenState;
  physicalState: ProcgenState;
  playerPosition: ProcgenPoint;
  legalActions: readonly string[];
  replayLabel: string;
  witness: readonly string[];
  cursor: number;
  completed: boolean;
  rooms: readonly Readonly<{ id: string; minimum: ProcgenPoint; maximum: ProcgenPoint }> [];
  routes: readonly Readonly<{ id: string; from: string; to: string; width: number; requiresSwitch: boolean }> [];
  checks: Readonly<{ model: string; routes: string; separations: string; coverage: string; information: string }>;
  history: readonly string[];
  analysis: Readonly<{
    completable: boolean;
    reachableStates: number;
    unrecoverableStates: number;
    witness: readonly string[];
    contracts: readonly Readonly<{ kind: string; requirement: string; passed: boolean; evidence: string }> [];
    counterexamples: readonly Readonly<{ requirement: string; actions: readonly string[]; reason: string }> [];
  }> | null;
  layout: Readonly<{
    volumes: readonly Readonly<{ id: string; kind: string; minimum: ProcgenPoint; maximum: ProcgenPoint }> [];
    markers: readonly Readonly<{ id: string; room: string; action: string; position: ProcgenPoint; label: string }> [];
    gateRoute: string;
  }>;
}>;

type LayerName = 'rooms' | 'passages' | 'gates' | 'markers' | 'grid';
type Overlay = 'both' | 'model' | 'world';

/** Renders resolved layout facts as a schematic SVG, without owning any game or graph state. */
export function mountProcgenWorkbenchMap(host: HTMLElement): Readonly<{
  render(readout: ProcgenReadout): void;
  expand(expanded: boolean): void;
  unavailable(reason: string): void;
  dispose(): void;
}> {
  const section = document.createElement('section');
  section.setAttribute('aria-label', 'Resolved layout');
  section.style.cssText = 'background:linear-gradient(145deg,rgb(18 26 35),rgb(10 15 22));border:1px solid #40566f;border-radius:.45rem;margin:.45rem 0;max-width:100%;padding:.45rem;';
  const header = document.createElement('header');
  header.style.cssText = 'align-items:center;display:flex;flex-wrap:wrap;gap:.4rem;justify-content:space-between;';
  const title = document.createElement('strong'); title.textContent = 'Resolved layout';
  const caption = document.createElement('span'); caption.textContent = 'Awaiting layout facts'; caption.style.cssText = 'color:#a9bed0;font-size:.9em;';
  header.append(title, caption);
  const controls = document.createElement('div');
  controls.style.cssText = 'align-items:center;display:flex;flex-wrap:wrap;gap:.45rem;margin:.4rem 0;';
  const view = select('View', [['layout', 'Layout'], ['graph', 'Graph']]);
  const overlay = select('State overlay', [['both', 'Model + world'], ['model', 'Model only'], ['world', 'World only']]);
  const layers = new Map<LayerName, HTMLInputElement>();
  for (const layer of ['rooms', 'passages', 'gates', 'markers', 'grid'] as const) layers.set(layer, checkbox(layer[0].toUpperCase() + layer.slice(1), true));
  controls.append(view.field, overlay.field, ...Array.from(layers.values()).map((input) => input.parentElement!));
  const inspect = document.createElement('p');
  inspect.setAttribute('aria-live', 'polite');
  inspect.textContent = 'Select a room to inspect its bounds.';
  inspect.style.cssText = 'background:rgb(4 9 14 / 55%);border-radius:.25rem;margin:.25rem 0;padding:.3rem .4rem;overflow-wrap:anywhere;';
  const svg = document.createElementNS(svgNs, 'svg');
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label', 'Top-down resolved candidate plan');
  svg.setAttribute('viewBox', '0 0 640 620');
  svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
  svg.style.cssText = 'background:rgb(7 12 18);border:1px solid #263b50;border-radius:.3rem;display:block;height:16rem;width:100%;';
  const legend = document.createElement('p');
  legend.textContent = 'Plan: loaded candidate volumes. Amber bar: closed gate; dashed green: open gate. Cyan diamond: player. Magenta dot: model state. Blue dot: world state. Dashed cyan: preview aperture / sightline.';
  legend.style.cssText = 'color:#aec4d7;margin:.35rem 0 0;';
  section.append(header, controls, inspect, svg, legend);
  host.append(section);

  let latest: ProcgenReadout | null = null;
  let selectedRoom: string | null = null;
  const rerender = (): void => { if (latest !== null) draw(svg, latest, state()); };
  const state = (): Readonly<{ view: string; overlay: Overlay; layers: ReadonlySet<LayerName>; selectedRoom: string | null }> => ({
    view: view.control.value,
    overlay: overlay.control.value as Overlay,
    layers: new Set(Array.from(layers.entries()).filter(([, input]) => input.checked).map(([name]) => name)),
    selectedRoom,
  });
  view.control.addEventListener('change', rerender);
  overlay.control.addEventListener('change', rerender);
  for (const input of layers.values()) input.addEventListener('change', rerender);
  const inspectRoom = (target: EventTarget | null): void => {
    const clicked = target;
    const roomTarget = clicked instanceof Element ? clicked.closest('[data-room-id]') : null;
    const id = roomTarget?.getAttribute('data-room-id');
    if (id === null || id === undefined) return;
    selectedRoom = id;
    updateInspection(inspect, latest, selectedRoom);
    rerender();
  };
  svg.addEventListener('click', (event) => {
    inspectRoom(event.target);
  });
  svg.addEventListener('keydown', (event) => {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    const clicked = event.target;
    const roomTarget = clicked instanceof Element ? clicked.closest('[data-room-id]') : null;
    if (roomTarget === null) return;
    event.preventDefault();
    inspectRoom(roomTarget);
  });

  return Object.freeze({
    render: (readout) => {
      latest = readout;
      if (selectedRoom !== null && !readout.rooms.some((room) => room.id === selectedRoom)) selectedRoom = null;
      caption.textContent = (readout.active ? '' : 'Inactive · ') + shortIdentity(readout.identity) + ' · seed ' + readout.seed + ' · ' + readout.motif;
      updateInspection(inspect, readout, selectedRoom);
      rerender();
    },
    unavailable: (reason) => {
      selectedRoom = null;
      caption.textContent = latest === null ? 'Layout unavailable' : 'Stale layout: latest readout unavailable';
      inspect.textContent = 'Layout unavailable: ' + reason;
      if (latest === null) svg.replaceChildren();
    },
    expand: (expanded) => { svg.style.height = expanded ? 'max(16rem, calc(100vh - 290px))' : '16rem'; },
    dispose: () => section.remove(),
  });
}

function draw(svg: SVGSVGElement, readout: ProcgenReadout, state: Readonly<{ view: string; overlay: Overlay; layers: ReadonlySet<LayerName>; selectedRoom: string | null }>): void {
  const focused = document.activeElement instanceof Element ? document.activeElement.closest('[data-room-id]')?.getAttribute('data-room-id') : null;
  const volumes = readout.layout.volumes;
  const bounds = extent(volumes.length > 0 ? volumes : readout.rooms);
  const transform = planTransform(bounds);
  const nodes: SVGElement[] = [];
  if (state.layers.has('grid')) nodes.push(grid(bounds, transform));
  if (state.view === 'graph') drawGraph(nodes, readout, transform, state);
  else drawLayout(nodes, readout, transform, state);
  drawOverlays(nodes, readout, transform, state.overlay);
  svg.replaceChildren(...nodes);
  if (focused !== null) Array.from(svg.querySelectorAll<SVGElement>('[data-room-id]')).find((room) => room.getAttribute('data-room-id') === focused)?.focus({ preventScroll: true });
}

function drawLayout(nodes: SVGElement[], readout: ProcgenReadout, transform: Transform, state: Readonly<{ layers: ReadonlySet<LayerName>; selectedRoom: string | null }>): void {
  const kindOrder = ['passage', 'room', 'gate', 'window'];
  for (const kind of kindOrder) for (const volume of readout.layout.volumes.filter((item) => item.kind === kind)) {
    if (kind === 'room' && !state.layers.has('rooms')) continue;
    if (kind === 'passage' && !state.layers.has('passages')) continue;
    if ((kind === 'gate' || kind === 'window') && !state.layers.has('gates')) continue;
    const rect = volumeRect(volume, transform);
    rect.dataset.volumeKind = kind;
    if (kind === 'room') {
      const room = roomForVolume(readout, volume.id);
      const roomId = room?.id ?? volume.id;
      rect.dataset.roomId = roomId;
      rect.setAttribute('tabindex', '0'); rect.setAttribute('role', 'button');
      rect.setAttribute('aria-label', 'Inspect room ' + roomId);
      rect.setAttribute('fill', state.selectedRoom === roomId ? '#355e89' : '#203c55');
      rect.setAttribute('stroke', state.selectedRoom === roomId ? '#eef6ff' : '#8bb6d7');
      rect.setAttribute('stroke-width', state.selectedRoom === roomId ? '4' : '2');
      nodes.push(rect, text(transform.x(centerX(volume)), transform.z(centerZ(volume)) + 5, roomId, '#f2f7fb', '24'));
    } else if (kind === 'passage') {
      rect.setAttribute('fill', '#36536a'); rect.setAttribute('stroke', '#8ca8bd'); rect.setAttribute('stroke-width', '1.5'); nodes.push(rect);
    } else if (kind === 'gate') {
      const open = readout.physicalState.switchOpen;
      rect.setAttribute('fill', open ? '#3c9e87' : '#d58e42'); rect.setAttribute('fill-opacity', open ? '.14' : '.9'); rect.setAttribute('stroke', open ? '#7af1ce' : '#ffe0a5'); rect.setAttribute('stroke-width', '2.5');
      if (open) rect.setAttribute('stroke-dasharray', '6 4');
      nodes.push(rect);
    } else {
      rect.setAttribute('fill', '#8cddf0'); rect.setAttribute('fill-opacity', '.18'); rect.setAttribute('stroke', '#8cddf0'); rect.setAttribute('stroke-dasharray', '5 4');
      nodes.push(rect, text(transform.x(centerX(volume)), transform.z(centerZ(volume)) - 7, 'preview aperture · ' + readout.layout.gateRoute, '#a8efff', '16'));
    }
  }
  if (state.layers.has('gates') && readout.layout.volumes.some(v => v.kind === 'window')) {
    const lookout = readout.layout.markers.find(m => m.action === 'observe');
    const goal = readout.layout.markers.find(m => m.id === 'goal');
    if (lookout !== undefined && goal !== undefined) {
      const line = element('line');
      line.setAttribute('x1', String(transform.x(lookout.position.x))); line.setAttribute('y1', String(transform.z(lookout.position.z)));
      line.setAttribute('x2', String(transform.x(goal.position.x))); line.setAttribute('y2', String(transform.z(goal.position.z)));
      line.setAttribute('stroke', '#8cddf0'); line.setAttribute('stroke-width', '2'); line.setAttribute('stroke-dasharray', '8 6');
      line.setAttribute('pointer-events', 'none'); nodes.push(line);
    }
  }
  if (state.layers.has('markers')) for (const marker of readout.layout.markers) drawMarker(nodes, marker, transform);
}

function drawGraph(nodes: SVGElement[], readout: ProcgenReadout, transform: Transform, state: Readonly<{ layers: ReadonlySet<LayerName>; selectedRoom: string | null }>): void {
  const rooms = new Map(readout.rooms.map((room) => [room.id, room]));
  if (state.layers.has('passages')) for (const route of readout.routes) {
    const from = rooms.get(route.from); const to = rooms.get(route.to); if (from === undefined || to === undefined) continue;
    const edge = element('line'); edge.setAttribute('x1', String(transform.x(centerX(from)))); edge.setAttribute('y1', String(transform.z(centerZ(from))));
    edge.setAttribute('x2', String(transform.x(centerX(to)))); edge.setAttribute('y2', String(transform.z(centerZ(to))));
    const conditional = route.requiresSwitch;
    edge.setAttribute('stroke', conditional ? (readout.physicalState.switchOpen ? '#3c9e87' : '#d58e42') : '#8ca8bd'); edge.setAttribute('stroke-width', conditional ? '5' : '3');
    nodes.push(edge, text((transform.x(centerX(from)) + transform.x(centerX(to))) / 2, (transform.z(centerZ(from)) + transform.z(centerZ(to))) / 2 - 8, route.id, '#c4d8e7', '18'));
  }
  if (state.layers.has('rooms')) for (const room of readout.rooms) {
    const circle = element('circle'); circle.dataset.roomId = room.id; circle.setAttribute('tabindex', '0'); circle.setAttribute('role', 'button'); circle.setAttribute('aria-label', 'Inspect room ' + room.id);
    circle.setAttribute('cx', String(transform.x(centerX(room)))); circle.setAttribute('cy', String(transform.z(centerZ(room)))); circle.setAttribute('r', state.selectedRoom === room.id ? '22' : '17');
    circle.setAttribute('fill', state.selectedRoom === room.id ? '#355e89' : '#203c55'); circle.setAttribute('stroke', '#d7edfc'); circle.setAttribute('stroke-width', '2'); nodes.push(circle, text(transform.x(centerX(room)), transform.z(centerZ(room)) + 44, room.id, '#fff', '22'));
  }
  if (state.layers.has('markers')) for (const marker of readout.layout.markers) drawMarker(nodes, marker, transform);
}

function drawOverlays(nodes: SVGElement[], readout: ProcgenReadout, transform: Transform, overlay: Overlay): void {
  const rooms = new Map(readout.rooms.map((room) => [room.id, room]));
  if (overlay === 'both' || overlay === 'model') drawState(nodes, rooms.get(readout.modelState.room), transform, readout.modelState, 'model', -9, -9);
  if (overlay === 'both' || overlay === 'world') {
    drawState(nodes, rooms.get(readout.physicalState.room), transform, readout.physicalState, 'world', 9, -9);
    const player = element('path'); const x = transform.x(readout.playerPosition.x), y = transform.z(readout.playerPosition.z);
    player.setAttribute('d', `M ${x} ${y - 8} L ${x + 8} ${y} L ${x} ${y + 8} L ${x - 8} ${y} Z`); player.setAttribute('fill', '#6de2ff'); player.setAttribute('stroke', '#eafcff'); player.setAttribute('stroke-width', '1.5'); player.setAttribute('aria-label', 'Physical player position'); nodes.push(player);
  }
}

function drawState(nodes: SVGElement[], room: ProcgenReadout['rooms'][number] | undefined, transform: Transform, state: ProcgenState, name: string, dx: number, dy: number): void {
  if (room === undefined) return;
  const dot = element('circle'); dot.setAttribute('cx', String(transform.x(centerX(room)) + dx)); dot.setAttribute('cy', String(transform.z(centerZ(room)) + dy)); dot.setAttribute('r', '6');
  dot.setAttribute('fill', name === 'model' ? '#f48cff' : '#64baff'); dot.setAttribute('stroke', '#fff'); dot.setAttribute('stroke-width', '1.5'); dot.setAttribute('aria-label', name + ' state: ' + state.room); nodes.push(dot);
}

function drawMarker(nodes: SVGElement[], marker: ProcgenReadout['layout']['markers'][number], transform: Transform): void {
  const x = transform.x(marker.position.x), y = transform.z(marker.position.z);
  const mark = element('rect'); mark.setAttribute('x', String(x - 6)); mark.setAttribute('y', String(y - 6)); mark.setAttribute('width', '12'); mark.setAttribute('height', '12'); mark.setAttribute('rx', '2'); mark.setAttribute('fill', '#f0d36e'); mark.setAttribute('stroke', '#261e08'); mark.setAttribute('stroke-width', '1.5'); mark.setAttribute('aria-label', marker.label + ': ' + marker.action);
  const shortLabel = marker.action === 'spend' ? 'Spend key' : marker.action === 'recover' ? 'Recover key' : marker.action === 'observe' ? 'Lookout' : marker.label;
  nodes.push(mark, text(x, y - 15, shortLabel, '#ffe89a', '18'));

}

type Bounds = Readonly<{ minimum: ProcgenPoint; maximum: ProcgenPoint }>;
type Transform = Readonly<{ x(value: number): number; z(value: number): number }>;
const svgNs = 'http://www.w3.org/2000/svg';
function element(name: string): SVGElement { return document.createElementNS(svgNs, name); }
function extent(items: readonly Bounds[]): Bounds {
  if (items.length === 0) return { minimum: { x: -1, y: 0, z: -1 }, maximum: { x: 1, y: 0, z: 1 } };
  return items.reduce<Bounds>((value, item) => ({ minimum: { x: Math.min(value.minimum.x, item.minimum.x), y: 0, z: Math.min(value.minimum.z, item.minimum.z) }, maximum: { x: Math.max(value.maximum.x, item.maximum.x), y: 0, z: Math.max(value.maximum.z, item.maximum.z) } }), items[0]);
}
function planTransform(bounds: Bounds): Transform {
  const padding = 42, width = 640 - padding * 2, height = 620 - padding * 2;
  const spanX = Math.max(1, bounds.maximum.x - bounds.minimum.x), spanZ = Math.max(1, bounds.maximum.z - bounds.minimum.z), scale = Math.min(width / spanX, height / spanZ);
  const xOffset = padding + (width - spanX * scale) / 2 - bounds.minimum.x * scale;
  const zOffset = padding + (height - spanZ * scale) / 2 + bounds.maximum.z * scale;
  return { x: (value) => xOffset + value * scale, z: (value) => zOffset - value * scale };
}
function volumeRect(volume: Bounds, transform: Transform): SVGElement {
  const rect = element('rect'); const x = transform.x(volume.minimum.x), y = transform.z(volume.maximum.z), right = transform.x(volume.maximum.x), bottom = transform.z(volume.minimum.z);
  rect.setAttribute('x', String(x)); rect.setAttribute('y', String(y)); rect.setAttribute('width', String(Math.max(1, right - x))); rect.setAttribute('height', String(Math.max(1, bottom - y))); return rect;
}
function centerX(bounds: Bounds): number { return (bounds.minimum.x + bounds.maximum.x) / 2; }
function centerZ(bounds: Bounds): number { return (bounds.minimum.z + bounds.maximum.z) / 2; }
function roomForVolume(readout: ProcgenReadout, id: string): ProcgenReadout['rooms'][number] | undefined { return readout.rooms.find((room) => room.id === id || id.endsWith('.' + room.id)); }
function grid(bounds: Bounds, transform: Transform): SVGElement {
  const group = element('g'); group.setAttribute('stroke', '#203347'); group.setAttribute('stroke-width', '1');
  const step = 5; const minX = Math.floor(bounds.minimum.x / step) * step, maxX = Math.ceil(bounds.maximum.x / step) * step, minZ = Math.floor(bounds.minimum.z / step) * step, maxZ = Math.ceil(bounds.maximum.z / step) * step;
  for (let x = minX; x <= maxX; x += step) { const line = element('line'); line.setAttribute('x1', String(transform.x(x))); line.setAttribute('x2', String(transform.x(x))); line.setAttribute('y1', String(transform.z(maxZ))); line.setAttribute('y2', String(transform.z(minZ))); group.append(line); }
  for (let z = minZ; z <= maxZ; z += step) { const line = element('line'); line.setAttribute('x1', String(transform.x(minX))); line.setAttribute('x2', String(transform.x(maxX))); line.setAttribute('y1', String(transform.z(z))); line.setAttribute('y2', String(transform.z(z))); group.append(line); }
  group.append(text(14, 606, '↑ +Z   → +X   ·   grid 5 world units', '#aec4d7', '18', 'start')); return group;
}
function text(x: number, y: number, value: string, fill: string, size: string, anchor = 'middle'): SVGElement { const label = element('text'); label.setAttribute('x', String(x)); label.setAttribute('y', String(y)); label.setAttribute('fill', fill); label.setAttribute('font-size', size); label.setAttribute('text-anchor', anchor); label.setAttribute('pointer-events', 'none'); label.textContent = value; return label; }
function updateInspection(host: HTMLElement, readout: ProcgenReadout | null, selected: string | null): void {
  const room = selected === null ? undefined : readout?.rooms.find((item) => item.id === selected);
  host.textContent = room === undefined ? 'Select a room to inspect its bounds.' : room.id + ' · min (' + point(room.minimum) + ') · max (' + point(room.maximum) + ')';
}
function point(value: ProcgenPoint): string { return value.x + ', ' + value.y + ', ' + value.z; }
function shortIdentity(value: string): string { return value.length > 14 ? value.slice(0, 14) + '…' : value; }
function select(label: string, options: readonly (readonly [string, string])[]): Readonly<{ field: HTMLLabelElement; control: HTMLSelectElement }> { const field = document.createElement('label'); field.textContent = label + ' '; const control = document.createElement('select'); for (const [value, textValue] of options) { const option = document.createElement('option'); option.value = value; option.textContent = textValue; control.append(option); } field.append(control); return { field, control }; }
function checkbox(label: string, checked: boolean): HTMLInputElement { const field = document.createElement('label'); const control = document.createElement('input'); control.type = 'checkbox'; control.checked = checked; field.append(control, document.createTextNode(' ' + label)); return control; }
