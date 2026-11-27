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
