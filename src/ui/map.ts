import { number, text, type Values } from './hud.js';

/** A place the journal knows, from the product's <c>journalPlaces</c>. */
export interface Place { readonly name: string; readonly visited: boolean; readonly x: number; readonly z: number }

const SVG = 'http://www.w3.org/2000/svg';
const INK = '#f2ead8';
const RING = '#2a2d36';

/** The closest a map ever zooms: its rim is at least this far, in metres. */
const NEAREST_REACH_METRES = 50;

/** How a place kind is marked; one not listed is marked in ink. */
export const MARKS: Readonly<Record<string, string>> = {
  'Standing stones': '#d8b84a',
  Ruin: '#c98a5a',
  'Cave mouth': '#9aa6b8',
  'Dungeon entrance': '#c8423a',
  'Vantage point': '#5aa6d8',
};

export const mark = (place: Place): string => MARKS[place.name] ?? INK;

export const places = (values: Values): Place[] => (text(values, 'journalPlaces') ?? '').split(';')
  .filter((entry) => entry.length > 0)
  .map((entry) => {
    const [name = '', stage = '', x = '0', z = '0'] = entry.split('|');
    return { name, visited: stage === 'visited', x: Number(x), z: Number(z) };
  });

/** Where the player stands, in world metres, and which way they face: yaw clockwise from north (-Z). */
export const standing = (values: Values): { x: number; z: number; yaw: number } =>
  ({ x: number(values, 'playerX') ?? 0, z: number(values, 'playerZ') ?? 0, yaw: number(values, 'yawDegrees') ?? 0 });

/**
 * Draws the journal's places into an SVG as a north-up map around the player: north is -Z and east
 * +X, as the world's yaw has it. The rim is <paramref name="reach"/> metres away, or, without one,
 * the farthest place up to <paramref name="maximumReach"/>. Filled marks are places stood at.
 */
export function drawMap(svg: SVGSVGElement, values: Values, size: number, options: { reach?: number; maximumReach: number; margin: number; labels: boolean }): void {
  const known = places(values);
  const me = standing(values);
  const shape = (tag: string, attributes: Readonly<Record<string, string | number>>, parent: Element = svg): SVGElement => {
    const node = document.createElementNS(SVG, tag);
    for (const [name, value] of Object.entries(attributes)) node.setAttribute(name, String(value));
    parent.append(node);
    return node;
  };

  svg.replaceChildren();
  svg.setAttribute('width', String(size));
  svg.setAttribute('height', String(size));
  svg.setAttribute('viewBox', `0 0 ${size} ${size}`);
  const centre = size / 2;
  const radius = centre - options.margin;
  const reach = options.reach ?? Math.min(options.maximumReach,
    Math.max(NEAREST_REACH_METRES, ...known.map((place) => Math.hypot(place.x - me.x, place.z - me.z))));
  const scale = radius / reach;
  shape('circle', { cx: centre, cy: centre, r: radius, fill: 'none', stroke: RING });
  shape('circle', { cx: centre, cy: centre, r: radius / 2, fill: 'none', stroke: RING });
  shape('text', { x: centre, y: options.margin - 2, fill: INK, 'font-size': 10, 'text-anchor': 'middle' }).textContent = 'N';
  if (options.labels) {
    shape('text', { x: size - 6, y: size - 6, fill: INK, opacity: 0.6, 'font-size': 9, 'text-anchor': 'end' }).textContent = `${Math.round(reach)} m`;
  }

  for (const place of known) {
    const dx = place.x - me.x;
    const dz = place.z - me.z;
    if (Math.hypot(dx, dz) > reach) continue;
    const colour = mark(place);
    const square = shape('rect', { x: centre + dx * scale - 4, y: centre + dz * scale - 4, width: 8, height: 8,
      fill: place.visited ? colour : 'none', stroke: colour, 'stroke-width': 2 });
    shape('title', {}, square).textContent = `${place.name}, ${place.visited ? 'visited' : 'seen'}`;
  }

  // The player: an arrow pointing the way they face.
  shape('path', { d: `M ${centre} ${centre - 7} L ${centre + 5} ${centre + 5} L ${centre} ${centre + 2} L ${centre - 5} ${centre + 5} Z`,
    fill: INK, stroke: '#000', 'stroke-width': 1, transform: `rotate(${me.yaw} ${centre} ${centre})` });
}
