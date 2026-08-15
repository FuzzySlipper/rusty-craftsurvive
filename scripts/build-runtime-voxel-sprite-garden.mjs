import { createHash } from 'node:crypto';
import {
  copyFileSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { execFileSync } from 'node:child_process';

const RUN_ID = 'depth-splat-20260815-001';
const sourceRoot = resolve(process.env.DEPTH_SPLAT_RUN
  ?? `/home/dev/asset-pipeline/live-evidence/voxel-experiments/depth-splat-runs/${RUN_ID}`);
const outputRoot = resolve('web/public/assets/depth-splat');
const oldFixture = JSON.parse(readFileSync(join(outputRoot, 'garden-v1.json'), 'utf8'));
const subjects = ['spatial-wizard', 'rigged-wizard', 'knight'];
const sectors = Array.from({ length: 16 }, (_, index) => index);
const resources = new Map();
const textures = [];
const frames = [];
const temporaryRoot = mkdtempSync(join(tmpdir(), 'craftsurvive-voxel-sprite-'));

try {
  for (const original of oldFixture.originals) {
    const hash = original.asset.contentHash;
    const resource = oldFixture.resources.find(({ identity }) =>
      identity === `mesh-resource/${hash.slice('sha256:'.length)}`);
    if (resource === undefined) throw new Error(`missing original resource for ${original.subject}`);
    resources.set(resource.identity, resource);
  }

  for (const subject of subjects) {
    const report = JSON.parse(readFileSync(join(sourceRoot, subject, 'report.json'), 'utf8'));
    for (const sector of sectors) {
      const label = `dir-${pad(sector)}`;
      const capture = JSON.parse(readFileSync(
        join(sourceRoot, subject, 'raw', label, 'capture.json'),
        'utf8',
      ));
      const channelIds = {};
      for (const channel of ['color', 'depth', 'normal', 'coverage']) {
        const source = channel === 'coverage'
          ? join(sourceRoot, subject, 'raw', label, 'depth.png')
          : join(sourceRoot, subject, 'raw', label, `${channel}.png`);
        const temporary = join(temporaryRoot, `${subject}-${label}-${channel}.png`);
        if (channel === 'color') copyFileSync(source, temporary);
        else if (channel === 'coverage') {
          execFileSync('magick', [source, '-threshold', '0', '-strip', '-depth', '8', `PNG32:${temporary}`]);
        } else {
          execFileSync('magick', [source, '-strip', '-depth', '8', `PNG32:${temporary}`]);
        }
        const bytes = readFileSync(temporary);
        const digest = createHash('sha256').update(bytes).digest('hex');
        const hash = `sha256:${digest}`;
        const identity = `texture-resource/${digest}`;
        const file = `runtime-${channel}-${digest}.png`;
        const destination = join(outputRoot, file);
        copyFileSync(temporary, destination);
        resources.set(identity, {
          identity,
          contentHash: hash,
          byteLength: bytes.byteLength,
          mediaType: 'image/png',
          url: `/assets/depth-splat/${file}`,
        });
        const id = `texture/runtime-voxel-sprite/${subject}/${label}/${channel}`;
        channelIds[channel] = id;
        textures.push({
          id,
          width: capture.resolution[0],
          height: capture.resolution[1],
          filter: 'nearest',
          wrap: 'clamp',
          contentHash: hash,
          version: 1,
          payload: {
            encoding: 'pngRgba8',
            colorSpace: channel === 'color' ? 'srgb' : 'linear',
            contentHash: hash,
            byteLength: bytes.byteLength,
            source: { kind: 'resource', resource: identity },
          },
        });
      }
      const basis = captureBasis(capture.direction.yaw_degrees, capture.direction.elevation_degrees);
      const scale = report.camera.orthographic_scale;
      frames.push({
        subject,
        sector,
        label,
        width: capture.resolution[0],
        height: capture.resolution[1],
        textures: channelIds,
        depth: { near: 0, far: capture.depth.scale_units },
        capture: {
          projection: 'orthographic',
          ...basis,
          boundsMinimum: [-scale / 2, -scale / 2, -scale / 2],
          boundsMaximum: [scale / 2, scale / 2, scale / 2],
        },
        sourceNormalSpace: 'blender-world-remapped',
      });
    }
  }
} finally {
  rmSync(temporaryRoot, { recursive: true, force: true });
}

const fixture = {
  schemaVersion: 1,
  source: {
    project: 'asset-pipeline',
    task: 6977,
    run: RUN_ID,
    subjects,
    sectors: sectors.length,
    preparedNormalSpace: 'blender-world-remapped-experimental',
  },
  resources: [...resources.values()],
  textures,
  frames,
  originals: oldFixture.originals,
  metrics: {
    frameCount: frames.length,
    textureCount: textures.length,
    resourceCount: resources.size,
    totalResourceBytes: [...resources.values()].reduce((sum, item) => sum + item.byteLength, 0),
  },
};
writeFileSync(join(outputRoot, 'runtime-v1.json'), `${JSON.stringify(fixture, null, 2)}\n`);
console.log(JSON.stringify(fixture.metrics));

function captureBasis(yawDegrees, elevationDegrees) {
  const yaw = yawDegrees * Math.PI / 180;
  const elevation = elevationDegrees * Math.PI / 180;
  const position = [
    Math.sin(yaw) * Math.cos(elevation) * 10,
    Math.sin(elevation) * 10,
    Math.cos(yaw) * Math.cos(elevation) * 10,
  ];
  const forward = normalize(position.map((value) => -value));
  const right = normalize([forward[2], 0, -forward[0]]);
  const up = normalize(cross(right, forward));
  return { position, right, up, forward };
}

function cross(left, right) {
  return [
    left[1] * right[2] - left[2] * right[1],
    left[2] * right[0] - left[0] * right[2],
    left[0] * right[1] - left[1] * right[0],
  ];
}

function normalize(value) {
  const length = Math.hypot(...value);
  return value.map((component) => component / length);
}

function pad(value) { return String(value).padStart(2, '0'); }
