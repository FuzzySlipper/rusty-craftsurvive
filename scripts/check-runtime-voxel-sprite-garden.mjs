import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const manifestPath = 'web/public/assets/voxel-sprite/runtime-models-v2.json';
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
const implementation = readFileSync('web/src/runtime-voxel-sprite-garden.ts', 'utf8');
const ui = readFileSync('web/src/game-ui.ts', 'utf8');
const fail = (message) => { throw new Error(`runtime voxel-sprite garden audit: ${message}`); };

if (manifest.schemaVersion !== 2 || manifest.source?.kind !== 'runtime-models') {
  fail('unexpected runtime-model manifest contract');
}
if (manifest.source?.subjects?.join(',') !== 'spatial-wizard,rigged-wizard,knight') {
  fail('expected the three comparison subjects');
}
if (manifest.models?.length !== 3 || manifest.resources?.length !== 3) {
  fail('the active lab must admit exactly three models and three resources');
}

let totalBytes = 0;
const resources = new Map();
for (const resource of manifest.resources) {
  if (resource.mediaType !== 'application/octet-stream' || !resource.url?.endsWith('.glb')) {
    fail(`non-GLB resource entered the runtime lab: ${resource.url}`);
  }
  if (resource.url.includes('runtime-color-') || resource.url.endsWith('.png')) {
    fail(`prepared texture entered the runtime lab: ${resource.url}`);
  }
  const bytes = readFileSync(resolve('web/public', resource.url.slice(1)));
  const hash = `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
  if (bytes.byteLength !== resource.byteLength || hash !== resource.contentHash) {
    fail(`resource bytes drifted for ${resource.identity}`);
  }
  if (resource.identity !== `mesh-resource/${hash.slice('sha256:'.length)}`) {
    fail(`resource identity drifted for ${resource.identity}`);
  }
  resources.set(resource.identity, resource);
  totalBytes += bytes.byteLength;
}

for (const model of manifest.models) {
  const identity = `mesh-resource/${model.asset?.contentHash?.slice('sha256:'.length)}`;
  if (!resources.has(identity)) fail(`model ${model.subject} has no admitted GLB resource`);
}
if (manifest.metrics?.resourceCount !== 3 || manifest.metrics?.totalResourceBytes !== totalBytes) {
  fail('manifest metrics drifted');
}

for (const forbidden of [
  'runtime-v1.json',
  "kind: 'prepared'",
  'PreparedFrame',
  'prepared texture',
  'data-lab-producer',
]) {
  if (`${implementation}\n${ui}`.includes(forbidden)) fail(`active lab still contains ${forbidden}`);
}
for (const required of [
  'BLUE runtime proxy',
  'RED runtime enhanced',
  'data-lab-capture-mode',
  'data-lab-post-mode',
  'data-lab-match',
  '<option>4096</option>',
  'data-lab-splat-resolution',
  'data-lab-splat-opacity',
  'data-lab-splat-blend',
]) {
  if (!ui.includes(required)) fail(`active lab is missing ${required}`);
}
for (const required of ['splatColumns', 'splatRows', 'splatOpacity', 'splatBlendMode']) {
  if (!implementation.includes(required)) fail(`runtime configuration is missing ${required}`);
}

console.log(`runtime voxel-sprite garden audit passed: 3 runtime GLBs, 0 prepared frames, 0 prepared textures, ${totalBytes} bytes`);
