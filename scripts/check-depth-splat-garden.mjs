import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const root = resolve('web/public/assets/depth-splat');
const fixture = JSON.parse(readFileSync(resolve(root, 'garden-v1.json'), 'utf8'));
const fail = (message) => { throw new Error(`depth-splat garden audit: ${message}`); };
if (fixture.schemaVersion !== 1 || fixture.source?.run !== 'depth-splat-20260815-001') fail('unexpected provenance');
if (fixture.source?.subjects?.length !== 3 || fixture.source?.variants?.length !== 5 || fixture.source?.sectors !== 16) {
  fail('expected three subjects, five variants, and sixteen sectors');
}
if (fixture.assets?.length !== 240 || fixture.depictions?.length !== 240 || fixture.originals?.length !== 3) {
  fail('comparison inventory is incomplete');
}
if (fixture.assets.some((asset) => asset.collision?.kind !== 'visualOnly')) fail('depictions must remain presentation-only');
const keys = new Set(fixture.depictions.map(({ subject, variant, sector }) => `${subject}/${variant}/${sector}`));
if (keys.size !== 240) fail('depiction identities are not unique');
const resourceIdentities = new Set();
let totalBytes = 0;
for (const resource of fixture.resources ?? []) {
  if (resourceIdentities.has(resource.identity)) fail(`duplicate resource ${resource.identity}`);
  resourceIdentities.add(resource.identity);
  if (!resource.url?.startsWith('/assets/depth-splat/')) fail(`resource escaped bounded asset root: ${resource.url}`);
  const bytes = readFileSync(resolve('web/public', resource.url.slice(1)));
  const contentHash = `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
  if (bytes.byteLength !== resource.byteLength || contentHash !== resource.contentHash) {
    fail(`resource bytes drifted for ${resource.identity}`);
  }
  const digest = contentHash.slice('sha256:'.length);
  const prefix = resource.mediaType === 'image/png' ? 'texture-resource/' : 'mesh-resource/';
  if (resource.identity !== `${prefix}${digest}`) fail(`resource identity drifted for ${resource.identity}`);
  totalBytes += bytes.byteLength;
}
if (totalBytes !== fixture.metrics?.totalResourceBytes || totalBytes > 64 * 1024 * 1024) {
  fail(`resource total is unexpected: ${totalBytes}`);
}
for (const asset of fixture.assets) {
  const resource = asset.payload?.source?.resource;
  if (!resourceIdentities.has(resource)) fail(`asset ${asset.asset} references missing ${resource}`);
}
for (const original of fixture.originals) {
  const digest = original.asset?.contentHash?.slice('sha256:'.length);
  if (!resourceIdentities.has(`mesh-resource/${digest}`)) fail(`original ${original.subject} resource is missing`);
}
console.log(`depth-splat garden audit passed: 240 depictions, 3 originals, ${fixture.resources.length} resources, ${totalBytes} bytes`);
