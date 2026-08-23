import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const manifestPath = 'web/public/assets/animation-lab/held-animation-garden-v1.json';
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
const implementation = readFileSync('web/src/held-animation-garden.ts', 'utf8');
const ui = readFileSync('web/src/game-ui.ts', 'utf8');
const fail = (message) => { throw new Error(`held-animation garden audit: ${message}`); };
const acceptedPack = 'sha256:d6df4c2d69c3aca66a6a4da8bf080b147a49a78dffcc19887806ed779417d4e2';
const acceptedTarget = 'sha256:13997da6bb5e3f9661a72be6e955653b1c18bf0a203a9016b14b9ee972325202';
const expectedIds = ['ual-idle', 'ual-jog', 'ual-spell', 'ual-sword', 'ual-roll', 'ual-death'];
const expectedCategories = ['idle', 'walk/jog', 'spell', 'sword/punch', 'roll/jump', 'death'];

if (manifest.schemaVersion !== 1 || manifest.experiment !== 'craftsurvive-held-animation-garden') fail('unexpected experiment contract');
if (manifest.engineRevision !== '30428957aeb850d31323a0d96a20a20631569341') fail('exact Engine retained-sample revision drifted');
if (manifest.assetPipeline?.task !== 7027 || manifest.assetPipeline?.run !== 'task-7027-direction-aligned-20260819-007') fail('accepted direction-aligned provenance drifted');
if (!manifest.assetPipeline?.inPlacePolicy?.toLowerCase().includes('in-place')) fail('animation root policy is not explicit in-place');
if (manifest.frameBankPolicy?.maximumSamples !== 24 || manifest.frameBankPolicy?.sectorCount !== 1 || manifest.frameBankPolicy?.captureResolution !== 128) fail('bounded held-frame policy drifted');
if (manifest.resources?.length !== 2 || manifest.license?.name !== 'CC0-1.0') fail('expected exactly target plus external pack and their license');
if (manifest.target?.contentHash !== acceptedTarget || manifest.clipPack?.contentHash !== acceptedPack) fail('accepted target or direction-aligned pack hash drifted');
if (manifest.target?.clips?.length !== 0 || manifest.target?.defaultClip !== null) fail('target may not silently own an embedded clip library');
if (manifest.clipPack?.rig?.bindRestHash !== 'sha256:f0f51ffe2071eb24156bbeb51db9c55eedb6f67811054abb9dd960ee785c067c' || manifest.clipPack?.rig?.rootJointId !== 'mixamorigHips' || manifest.clipPack?.rig?.rootConvention !== 'inPlace') fail('target-bound rig compatibility drifted');
if (manifest.clipPack?.rig?.joints?.length !== 46) fail('rig joint inventory drifted');
if (manifest.clipPack?.clips?.map((clip) => clip.id).join(',') !== expectedIds.join(',')) fail('representative chooser clips drifted');
if (manifest.clipPack?.clips?.map((clip) => clip.category).join(',') !== expectedCategories.join(',')) fail('representative chooser categories drifted');
if (JSON.stringify(manifest).includes('18d507')) fail('rejected Godot-rest-basis artifact entered active garden data');

for (const resource of manifest.resources) {
  const bytes = readFileSync(resolve('web/public', resource.url.slice(1)));
  const hash = `sha256:${createHash('sha256').update(bytes).digest('hex')}`;
  if (bytes.byteLength !== resource.byteLength || hash !== resource.contentHash) fail(`resource bytes drifted for ${resource.identity}`);
}
const licenseBytes = readFileSync(resolve('web/public', manifest.license.url.slice(1)));
const licenseHash = `sha256:${createHash('sha256').update(licenseBytes).digest('hex')}`;
if (licenseBytes.byteLength !== manifest.license.byteLength || licenseHash !== manifest.license.contentHash) fail('license bytes drifted');

for (const required of [
  'beginHeldAnimationFrameBank', 'prepareHeldAnimationFrameBank', 'selectHeldAnimationFrameBank', 'destroyHeldAnimationFrameBank',
  "kind: 'sample'", 'normalizedTimes: this.#normalizedTimes()', 'index / (this.#cadence * clip.durationSeconds)',
  "mode: 'sprite'", "mode: 'full-splat'", '#beginDepthBank', '#cancelCandidate', '#hold', 'createVoxelSpriteExperiment',
]) {
  if (!implementation.includes(required)) fail(`implementation is missing ${required}`);
}
for (const forbidden of ['18d507', "from 'three'", 'runtime retarget', 'setInterval(() => this.#advance(), 0)']) {
  if (implementation.includes(forbidden)) fail(`implementation contains forbidden ${forbidden}`);
}
for (const required of ['data-held-clip', 'data-held-cadence', 'data-held-representation', 'data-held-scrub', 'data-held-window', 'data-held-pause', 'animation-garden']) {
  if (!ui.includes(required)) fail(`bounded viewport UI is missing ${required}`);
}

console.log(`held-animation garden audit passed: target + accepted D6 clip pack, ${expectedIds.length} representative clips, 24-frame bound, sequential flat/depth preparation`);
