export type Rating = 'good' | 'maybe' | 'bad';

export interface ReviewRow {
  category: string;
  id: string;
  marked: boolean;
  rating: Rating | null;
  previousRating?: Rating | null;
  note: string;
  revision: string | null;
  updated: string | null;
}

export interface ReviewPatch {
  marked?: boolean;
  rating?: Rating | null;
  note?: string;
  expectedRevision?: string;
}

export interface Category {
  id: string;
  label: string;
  about: string;
  records?: number;
  files?: number;
  bytes?: number;
  unprovenanced?: number;
}

export interface ModelRef {
  type: 'glb' | 'prop-mesh';
  url: string;
  path?: string;
}

export interface RecordCard {
  id: string;
  category: string;
  kind: string;
  name: string;
  summary: string;
  tags: string[];
  preview: string | null;
  model: ModelRef | null;
  audio: string | null;
  revision: string;
  bytes: number;
  files: number;
  authoring: number;
  provenance: number;
  unprovenanced: number;
}

export interface FileRow {
  path: string;
  url: string | null;
  size: number;
  type: string;
}

export interface ProvenanceEntry {
  source: string;
  pointer: string;
  field: string;
  entry: Record<string, unknown>;
}

export interface RecordDetail extends Omit<RecordCard, 'files' | 'authoring' | 'provenance' | 'unprovenanced'> {
  files: FileRow[];
  authoring: FileRow[];
  provenance: ProvenanceEntry[];
  unprovenanced: string[];
  data: unknown;
  images: string[];
  models: ModelRef[];
}

export interface CatalogResponse {
  categories: Category[];
  records: RecordCard[];
  contentFiles: number;
  unclaimed: string[];
}

export interface ProvenanceResponse {
  sources: Array<{ path: string; purpose: string | null; generated?: string | null; paths: number; error?: string }>;
  missing: Record<string, Array<{ id: string; name: string; path: string }>>;
  dangling: string[];
}

// ---- golden seed

export interface GoldenWorld {
  seed: number;
  size: number;
  radius: number;
  generatorVersion: number;
  fingerprint: string;
  continental: boolean;
  design: string | null;
  waterLevel: number;
  mapSpacing: number;
  drainageSpacing: number;
  placeCell: number;
  mapRivers: number;
  drainageReaches: number;
  biomes: string[];
  placeKinds: string[];
  navigability: string[];
  channels: string[];
  water: string[];
  biomeColours?: string[];
  mapColours?: { stone: string; river: string; lake: string; saltLake: string; playa: string; town: string };
}

export interface GoldenWindowSummary {
  id: string;
  name: string;
  town: string | null;
  x: number;
  z: number;
  half: number;
  step: number;
  side: number;
  places: number;
  crossings: number;
  caves: number;
}

export interface GoldenManifest {
  format: number;
  identity?: { key: string; map: string; terrain: string; sources: string };
  tiles?: { spacing: number; land: number; sea: number; built: number; cached: number; failed?: number; caves: number; complete: boolean };
  world: GoldenWorld;
  config: Record<string, unknown>;
  exportedAt: string;
  source: { commit: string; dirty: boolean; enginePair: string | null };
  continent: { pixels: number; metresPerPixel: number; layers: Record<string, string> };
  windows: GoldenWindowSummary[];
  timings: Record<string, number>;
}

export interface GoldenOverview {
  available: boolean;
  command: string;
  config: Record<string, unknown> | null;
  manifest?: GoldenManifest;
  configChanged?: boolean;
  mediaBase?: string;
  exporterBuilt?: boolean;
  drift?: { head: string | null; commitsSince: number | null; generatorCommitsSince: number | null; dirtyNow: boolean; dirtyAtExport: boolean } | null;
}

export interface GoldenTown {
  key: string;
  name: string;
  kind: string;
  reason: string;
  x: number;
  z: number;
  ground: number;
  facingX: number;
  facingZ: number;
  arrivalX: number;
  arrivalZ: number;
  recipe: string;
  layout: string;
  palette: string;
  landmark: string;
  reach: number;
  reachFlow: number;
  score: number;
  shelter: number;
  flat: number;
  services: Record<string, string>;
  keepers: Record<string, string>;
}

export interface GoldenLake {
  key: string;
  id: number;
  name: string;
  kind: string;
  level: number;
  areaKm2: number;
  depth: number;
  closed: boolean;
  inflow: number;
  x: number;
  z: number;
  bounds: number[];
}

export interface GoldenSite {
  name: string;
  x: number;
  z: number;
  elevation: number;
  biome: string;
}

export interface GoldenPlaces {
  towns: GoldenTown[];
  lakes: GoldenLake[];
  sites: GoldenSite[];
}

export interface GoldenReach {
  key: string;
  id: number;
  downstream: number;
  mouth: boolean;
  flow: number;
  catchmentKm2: number;
  lengthM: number;
  width: number;
  depth: number;
  navigability: number;
  channel: number;
  points: number[];
}

export interface GoldenPlace {
  id: string;
  kind: string;
  x: number;
  z: number;
  ground: number;
  height: number;
  variant: number;
  aspect: number;
  cave?: { path: number[]; box: number[] };
}

export interface GoldenCrossing {
  id: string;
  fromX: number;
  fromZ: number;
  toX: number;
  toZ: number;
  deck: number;
}

export interface GoldenWindow {
  id: string;
  name: string;
  town: string | null;
  x: number;
  z: number;
  half: number;
  step: number;
  side: number;
  places: GoldenPlace[];
  crossings: GoldenCrossing[];
  rivers: Array<{ id: number; points: number[] }>;
  heightUrl: string;
  biomeUrl: string;
}

// ---- golden seed, whole continent (export format 2)

export interface GoldenTileCave {
  id: string;
  x: number;
  z: number;
  ground: number;
  seed: string;
  setting: number[];
  turns: number;
  path: number[];
  box: number[];
}

/** A region tile's page: places are [cellX, cellZ, kind, x, z, ground, height, variant, aspect]; crossings [cellX, cellZ, fromX, fromZ, toX, toZ, deck]. */
export interface GoldenTile {
  tile: [number, number];
  cells: number;
  places: number[][];
  caves: GoldenTileCave[];
  crossings: number[][];
}

export interface GoldenTileSummary {
  tile: [number, number];
  sea?: boolean;
  failed?: string;
  places?: Record<string, number>;
  caves?: number;
  crossings?: number;
}

export interface GoldenCaveRow {
  id: string;
  x: number;
  z: number;
  ground: number;
  seed: string;
  setting: number[];
  tile: [number, number];
}

export interface CaveViewStats {
  openCubicMetres: number;
  standCubicMetres: number;
  crawlCubicMetres: number;
  tightCubicMetres: number;
  narrowCubicMetres: number;
  resolutionMetres: number;
  cutOffSpaces: number;
  cutOffCubicMetres: number;
  unreachedShare: number;
  lowestOpen: number | null;
  lowestReached: number | null;
  standRadius: number;
  crawlRadius: number;
  climbReach: number;
  routeMetres: number;
  routeWalk: number;
  routeSteep: number;
  routeClimb: number;
  longestClimb: number;
  climbsOverReach: number;
  routeDescent: number;
  routeAscent: number;
  routeReachedShare: number;
  routeTightShare: number;
  routeStandShare: number;
  deadEnds: number;
}

export interface CaveView {
  id: string;
  seed: string;
  setting: number[];
  size: [number, number, number];
  grid: [number, number, number];
  step: number;
  generateMs: number;
  viewMs: number;
  verdict: string;
  arrival: number[];
  start: number[] | null;
  tendril: number[][];
  route: number[][];
  profile: number[][];
  features: {
    sinks?: number[][];
    spring?: number[];
    chambers?: number[][];
    rifts?: number[][];
    beddingRooms?: number[][];
    sponge?: number[];
    maze?: number[];
    tiankeng?: { floor: number[]; radius: number };
    chasm?: number[];
    hall?: number[];
    passages?: Array<{ name: string; metres: number; narrowest: number; widened: number }>;
    skyOpen?: boolean;
    water?: Array<{ kind: string; surface: number; cells: number; at: number[] | null }>;
    ruin?: number[];
    deadEnds?: number[][];
  };
  plan: { state: string; top: string };
  sideX: string;
  sideZ: string;
  cells: { size: [number, number, number]; metres: number; state: string };
  stats: CaveViewStats;
}

// ---- underground plan (#11209, CMS-only exploratory)

export interface UndergroundCensusBucket {
  systems: number;
  singleBox: number;
  multiBox: number;
  multiShare: number;
  boxes: number;
  mouths: number;
  avgDepth: number;
  avgSolubility: number;
}

export interface UndergroundSampleSystem {
  id: string;
  tile: [number, number];
  x: number;
  z: number;
  boxes: number;
  links: number;
  mouths: number;
  bigSpaces: string[];
  biome: string;
  maxDepth: number;
  solubility: number;
  nearestTown: string;
  nearestTownKm: number;
}

export interface UndergroundLevelReport {
  name: string;
  description: string;
  settings: {
    siteSpacingMetres: number;
    boxSize: [number, number, number];
    boxChunks: [number, number, number];
    minSystemSeparationMetres: number;
    maxBoxesPerSystem: number;
    targetMultiBoxShare: number;
    subChambersPerBox: number;
    includeFieldGrid: boolean;
  };
  metrics: {
    wallMs: number;
    exportMs?: number;
    generateMs?: number;
    serializeMs?: number;
    avgLandTileMs: number;
    maxTileMs: number;
    landTiles: number;
    totalTiles: number;
    allocatedMB: number;
    retainedInMemoryMB: number;
    retainedPerLandTileKB: number;
    rawJsonMB: number;
    gzipTilesMB: number;
    rawGridMB: number;
    gzipGridMB: number;
    totalGzipMB: number;
    summaryGzipMB: number;
  };
  census: {
    systems: number;
    singleBoxSystems: number;
    multiBoxSystems: number;
    multiBoxShare: number;
    totalBoxes: number;
    avgBoxesPerSystem: number;
    avgBoxesPerMultiSystem: number;
    maxBoxesInSystem: number;
    links: number;
    mouths: number;
    rooms: number;
    conduits: number;
    boxesHistogram: Record<string, number>;
    byDepthBand: Record<string, number>;
    byBigSpace: Record<string, number>;
    byBiome: Record<string, UndergroundCensusBucket>;
    byHardness: Record<string, UndergroundCensusBucket>;
  };
  sampleMultiSystems: UndergroundSampleSystem[];
}

export interface UndergroundManifest {
  format: number;
  provisional: boolean;
  note: string;
  world: { seed: number; size: number; radius: number; fingerprint: string; tileSpacing: number; totalTiles: number };
  solubilityRaster: { file: string; pixels: number; metresPerPixel: number };
  levels: UndergroundLevelReport[];
}

export interface UndergroundOverview {
  available: boolean;
  command: string;
  manifest?: UndergroundManifest;
  solubilityUrl?: string | null;
  exporterBuilt?: boolean;
}

export interface UndergroundSystemSummary {
  id: string;
  tile: [number, number];
  x: number;
  z: number;
  d: number; // max depth
  s: number; // solubility
  h: number; // hardness
  b: string; // biome
  m: number; // 1 if multi-box
  boxes: Array<[number, number, number, number, number]>; // originX, originZ, sizeX, sizeZ, depthBand
  links: Array<[number, number, number, number, number]>; // x0, z0, x1, z1, kind
  mouths: Array<[number, number, number]>; // x, z, kind
  big: Array<[number, number, number]>; // x, z, kind
}

export interface UndergroundRoom {
  id: number;
  kind: string;
  at: [number, number, number];
  radii: [number, number, number];
  water: number;
}

export interface UndergroundConduit {
  from: number;
  to: number;
  radius: number;
  water: number;
  loop: boolean;
}

export interface UndergroundBox {
  index: number;
  grid: [number, number];
  origin: [number, number, number];
  chunks: [number, number, number];
  size: [number, number, number];
  ground: number;
  depth: number;
  waterLocalY: number;
  depthBand: string;
  seed: string;
  rooms: UndergroundRoom[];
  conduits: UndergroundConduit[];
}

export interface UndergroundLink {
  from: number;
  to: number;
  fromFace: string;
  toFace: string;
  faceU: number;
  faceV: number;
  at: [number, number, number];
  radius: number;
  waterY: number;
  kind: string;
  seamSeed: string;
}

export interface UndergroundMouth {
  box: number;
  at: [number, number, number];
  local: [number, number];
  kind: string;
}

export interface UndergroundBigSpace {
  box: number;
  kind: string;
  local: [number, number, number];
  radius: number;
}

export interface UndergroundSystemDetail {
  id: string;
  tile: [number, number];
  seed: string;
  x: number;
  z: number;
  ground: number;
  biome: string;
  hardness: number;
  solubility: number;
  discharge: number;
  temperature: number;
  moisture: number;
  maxDepth: number;
  multi: boolean;
  boxes: UndergroundBox[];
  links: UndergroundLink[];
  mouths: UndergroundMouth[];
  bigSpaces: UndergroundBigSpace[];
}

export interface SystemKarstSeam {
  index: number;
  from: number;
  to: number;
  kind: string;
  radius: number;
  at: [number, number, number];
  openShareA: number;
  openShareB: number;
  overlapIoU: number;
  connectedBothSides: boolean;
  seamReachedCrawl: boolean;
  seamReachedTight?: boolean;
}

export interface SystemKarstView {
  id: string;
  level: string;
  origin: [number, number, number];
  size: [number, number, number];
  step: number;
  start: [number, number, number];
  generateMs: number;
  totalMs: number;
  stats: {
    boxes: number;
    boxesReachedCrawl: number;
    boxesReachedTight?: number;
    links: number;
    seamsMatched: number;
    seamsTraversedCrawl?: number;
    seamsTraversedTight?: number;
    avgSeamOverlapIoU: number;
    openCubicMetres: number;
    standCubicMetres: number;
    crawlCubicMetres: number;
    tightCubicMetres: number;
    narrowCubicMetres: number;
    cutOffCubicMetres: number;
  };
  boxes: Array<{
    index: number;
    offset: [number, number, number];
    size: [number, number, number];
    depth: number;
    waterLocalY: number;
    generateMs: number;
    reachedCrawl: boolean;
    reachedTight?: boolean;
    verdict: string;
  }>;
  seams: SystemKarstSeam[];
  routes: number[][][];
  mouths: Array<{ box: number; kind: string; at: [number, number, number] }>;
  bigSpaces: Array<{ box: number; kind: string; radius: number; at: [number, number, number] }>;
  plan: { state: string; top: string };
  sideX: string;
  sideZ: string;
  cells: { metres: number; size: [number, number, number]; state: string };
}
