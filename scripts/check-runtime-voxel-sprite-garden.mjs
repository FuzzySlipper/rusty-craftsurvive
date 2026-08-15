import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const fixture = JSON.parse(readFileSync('web/public/assets/depth-splat/runtime-v1.json', 'utf8'));
const fail = (message) => { throw new Error(`runtime voxel-sprite garden audit: ${message}`); };
if (fixture.schemaVersion !== 1 || fixture.source?.run !== 'depth-splat-20260815-001') fail('unexpected provenance');
if (fixture.source?.subjects?.length !== 3 || fixture.source?.sectors !== 16
  || fixture.source?.preparedNormalSpace !== 'blender-world-remapped-experimental') {
  fail('expected three subjects, sixteen sectors, and the explicit prepared-normal limitation');
}
if (fixture.frames?.length !== 48 || fixture.textures?.length !== 192
  || fixture.originals?.length !== 3 || fixture.resources?.length !== 195) {
  fail('runtime fixture inventory is incomplete');
}
const resources = new Map();
let totalBytes = 0;
for (const resource of fixture.resources) {
  if (resources.has(resource.identity)) fail(`duplicate resource ${resource.identity}`);
  if (!resource.url?.startsWith('/assets/depth-splat/')) fail(`resource escaped asset root: ${resource.url}`);
  const bytes = readFileSync(resolve('web/public', resource.url.slice(1)));
  const hash = `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
  if (bytes.byteLength !== resource.byteLength || hash !== resource.contentHash) {
    fail(`resource bytes drifted for ${resource.identity}`);
  }
  const prefix = resource.mediaType === 'image/png' ? 'texture-resource/' : 'mesh-resource/';
  if (resource.identity !== `${prefix}${hash.slice('sha256:'.length)}`) fail(`resource identity drifted for ${resource.identity}`);
  resources.set(resource.identity, resource);
  totalBytes += bytes.byteLength;
}
const textures = new Map(fixture.textures.map((texture) => [texture.id, texture]));
for (const frame of fixture.frames) {
  if (frame.width !== 96 || frame.height !== 96 || frame.depth?.near !== 0 || frame.depth?.far !== 100) {
    fail(`frame metadata drifted for ${frame.subject}/${frame.label}`);
  }
  for (const channel of ['color', 'depth', 'normal', 'coverage']) {
    const texture = textures.get(frame.textures?.[channel]);
    if (texture === undefined) fail(`missing ${channel} texture for ${frame.subject}/${frame.label}`);
    if (texture.width !== frame.width || texture.height !== frame.height
      || texture.payload?.encoding !== 'pngRgba8'
      || texture.payload?.colorSpace !== (channel === 'color' ? 'srgb' : 'linear')
      || !resources.has(texture.payload?.source?.resource)) {
      fail(`invalid ${channel} texture for ${frame.subject}/${frame.label}`);
    }
  }
}
if (totalBytes !== fixture.metrics?.totalResourceBytes || totalBytes > 16 * 1024 * 1024) {
  fail(`resource total is unexpected: ${totalBytes}`);
}
console.log(`runtime voxel-sprite garden audit passed: 3 subjects, 48 frames, 192 textures, ${totalBytes} bytes`);
