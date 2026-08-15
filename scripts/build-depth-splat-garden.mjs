import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';

const RUN_ID = 'depth-splat-20260815-001';
const sourceRoot = resolve(process.env.DEPTH_SPLAT_RUN ??
  `/home/dev/asset-pipeline/live-evidence/voxel-experiments/depth-splat-runs/${RUN_ID}`);
const outputRoot = resolve('web/public/assets/depth-splat');
const variants = ['quad', 'flat', 'compressed', 'physical', 'tangent'];
const subjects = ['spatial-wizard', 'rigged-wizard', 'knight'];
const sectors = Array.from({ length: 16 }, (_, index) => index);
mkdirSync(outputRoot, { recursive: true });

const imported = [];
const originals = [];
for (const subject of subjects) {
  const report = JSON.parse(readFileSync(join(sourceRoot, subject, 'report.json'), 'utf8'));
  const sourceBytes = readFileSync(report.source);
  const sourceHash = sha256(sourceBytes);
  const sourceResource = `mesh-resource/${sourceHash.slice('sha256:'.length)}`;
  const sourceFile = `original-${subject}-${sourceHash.slice('sha256:'.length)}.glb`;
  writeFileSync(join(outputRoot, sourceFile), sourceBytes);
  originals.push({
    subject,
    asset: {
      asset: `mesh-animation/depth-splat/${subject}/original`,
      runtimeFormat: 'glb',
      contentHash: sourceHash,
      clips: [],
      defaultClip: null,
      materialSlots: [],
      bounds: { min: [-4, -4, -4], max: [4, 4, 4] },
    },
    resource: resourceReadout(sourceResource, sourceHash, sourceBytes.byteLength, 'application/octet-stream', sourceFile),
    scale: report.normalization.scale,
    sourcePath: report.source,
    sourceFile: basename(report.source),
  });
  for (const sector of sectors) {
    for (const variant of variants) {
      imported.push(importVariant(
        subject,
        variant,
        sector,
        join(sourceRoot, subject, 'models', variant, `dir-${String(sector).padStart(2, '0')}.glb`),
      ));
    }
  }
}

const quadItems = imported.filter(({ variant }) => variant === 'quad');
const splatItems = imported.filter(({ variant }) => variant !== 'quad');
const packedResources = [
  packResource(quadItems, 'packedStreamsLeV2'),
  packResource(splatItems, 'packedStreamsLeV3'),
];
const resources = [];
for (const packed of packedResources) {
  const file = `mesh-${packed.contentHash.slice('sha256:'.length)}.rmsh`;
  writeFileSync(join(outputRoot, file), packed.bytes);
  resources.push(resourceReadout(
    packed.resource,
    packed.contentHash,
    packed.bytes.byteLength,
    'application/octet-stream',
    file,
  ));
}

const textures = [];
const materials = [material('material/depth-splat/colors', null)];
for (const item of quadItems) {
  if (item.textureBytes === null) throw new Error(`${item.label} needs one embedded PNG`);
  const hash = sha256(item.textureBytes);
  const resource = `texture-resource/${hash.slice('sha256:'.length)}`;
  const file = `texture-${hash.slice('sha256:'.length)}.png`;
  writeFileSync(join(outputRoot, file), item.textureBytes);
  const [width, height] = pngDimensions(item.textureBytes);
  const textureId = `texture/depth-splat/${item.subject}/dir-${pad(item.sector)}`;
  const materialId = `material/depth-splat/${item.subject}/dir-${pad(item.sector)}`;
  textures.push({
    id: textureId,
    width,
    height,
    filter: 'nearest',
    wrap: 'clamp',
    contentHash: hash,
    version: 1,
    payload: {
      encoding: 'pngRgba8',
      colorSpace: 'srgb',
      contentHash: hash,
      byteLength: item.textureBytes.byteLength,
      source: { kind: 'resource', resource },
    },
  });
  materials.push(material(materialId, textureId));
  item.material = materialId;
  if (!resources.some(({ identity }) => identity === resource)) {
    resources.push(resourceReadout(resource, hash, item.textureBytes.byteLength, 'image/png', file));
  }
}

for (const item of splatItems) {
  if (item.textureBytes !== null || item.payload.source.colorsByteOffset === undefined) {
    throw new Error(`${item.label} must use vertex colors without a texture`);
  }
  item.material = 'material/depth-splat/colors';
}

const assets = imported.map((item) => ({
  asset: `mesh/depth-splat/${item.subject}/${item.variant}/dir-${pad(item.sector)}`,
  payload: item.payload,
  materialSlots: [{ slot: 0, material: item.material }],
  collision: { kind: 'visualOnly' },
}));
const fixture = {
  schemaVersion: 1,
  source: {
    project: 'asset-pipeline',
    task: 6977,
    run: RUN_ID,
    subjects,
    variants,
    sectors: sectors.length,
  },
  resources: [...resources, ...originals.map(({ resource }) => resource)],
  textures,
  materials,
  assets,
  originals: originals.map(({ subject, asset, scale, sourcePath, sourceFile }) => ({
    subject,
    asset,
    scale,
    sourcePath,
    sourceFile,
  })),
  depictions: imported.map((item, index) => ({
    subject: item.subject,
    variant: item.variant,
    sector: item.sector,
    asset: assets[index].asset,
    vertices: item.payload.layout.vertexCount,
    triangles: item.payload.layout.indexCount / 3,
    sourceGlbBytes: item.sourceBytes,
    uploadedBytes: item.uploadedBytes,
  })),
  metrics: {
    depictionCount: imported.length,
    originalCount: originals.length,
    packedMeshBytes: packedResources.reduce((sum, item) => sum + item.bytes.byteLength, 0),
    textureBytes: resources.filter(({ mediaType }) => mediaType === 'image/png')
      .reduce((sum, item) => sum + item.byteLength, 0),
    originalBytes: originals.reduce((sum, item) => sum + item.resource.byteLength, 0),
    totalResourceBytes: [...resources, ...originals.map(({ resource }) => resource)]
      .reduce((sum, item) => sum + item.byteLength, 0),
  },
};
writeFileSync(join(outputRoot, 'garden-v1.json'), `${JSON.stringify(fixture, null, 2)}\n`);
console.log(JSON.stringify(fixture.metrics));

function importVariant(subject, variant, sector, sourcePath) {
  const bytes = readFileSync(sourcePath);
  const { json, binary } = parseGlb(bytes);
  const scene = json.scenes?.[json.scene ?? 0];
  const label = `${subject}/${variant}/dir-${pad(sector)}`;
  if (scene?.nodes?.length !== 1) throw new Error(`${label} must contain one scene root`);
  const node = json.nodes?.[scene.nodes[0]];
  if (node?.mesh === undefined || hasTransform(node)) throw new Error(`${label} must contain one untransformed mesh node`);
  const mesh = json.meshes?.[node.mesh];
  if (mesh?.primitives?.length !== 1) throw new Error(`${label} must contain one mesh primitive`);
  const primitive = mesh.primitives[0];
  if (primitive.mode !== undefined && primitive.mode !== 4) throw new Error(`${label} is not triangles`);
  const positions = accessor(json, binary, primitive.attributes.POSITION, 'VEC3');
  const normals = accessor(json, binary, primitive.attributes.NORMAL, 'VEC3');
  const uvs = primitive.attributes.TEXCOORD_0 === undefined
    ? undefined : accessor(json, binary, primitive.attributes.TEXCOORD_0, 'VEC2');
  const colors = primitive.attributes.COLOR_0 === undefined
    ? undefined : accessor(json, binary, primitive.attributes.COLOR_0, 'VEC4');
  const indices = accessor(json, binary, primitive.indices, 'SCALAR', true);
  const vertexCount = positions.length / 3;
  if (normals.length !== positions.length || (uvs !== undefined && uvs.length !== vertexCount * 2)
    || (colors !== undefined && colors.length !== vertexCount * 4) || indices.length % 3 !== 0) {
    throw new Error(`${label} has mismatched retained streams`);
  }
  return {
    subject,
    variant,
    sector,
    label,
    sourceBytes: bytes.byteLength,
    textureBytes: embeddedTexture(json, binary, primitive.material),
    uploadedBytes: (positions.length + normals.length + (uvs?.length ?? 0)
      + (colors?.length ?? 0) + indices.length) * 4,
    payload: {
      layout: {
        vertexCount,
        indexCount: indices.length,
        indexWidth: 'u32',
        attributes: [
          { name: 'position', components: 3, kind: 'f32' },
          { name: 'normal', components: 3, kind: 'f32' },
          ...(uvs === undefined ? [] : [{ name: 'uv', components: 2, kind: 'f32' }]),
          ...(colors === undefined ? [] : [{ name: 'color', components: 4, kind: 'f32' }]),
        ],
      },
      groups: [{ materialSlot: 0, start: 0, count: indices.length }],
      bounds: boundsOf(positions),
      source: {
        kind: 'inline', positions, normals,
        ...(uvs === undefined ? {} : { uvs }),
        ...(colors === undefined ? {} : { colors }),
        indices,
      },
      provenance: 'staticAsset',
    },
  };
}

function packResource(items, encoding) {
  const chunks = [];
  let length = 16;
  for (const item of items) {
    const source = item.payload.source;
    const offsets = {};
    for (const [name, values] of [['positions', source.positions], ['normals', source.normals],
      ['uvs', source.uvs], ['colors', source.colors], ['indices', source.indices]]) {
      if (values === undefined) continue;
      offsets[`${name}ByteOffset`] = length;
      const chunk = name === 'indices' ? u32Bytes(values) : f32Bytes(values);
      chunks.push(chunk);
      length += chunk.byteLength;
    }
    item.resourceOffsets = offsets;
  }
  if (length > 64 * 1024 * 1024) throw new Error(`packed resource exceeds Engine limit: ${length}`);
  const bytes = Buffer.alloc(length);
  bytes.write(encoding === 'packedStreamsLeV2' ? 'RMSHLE02' : 'RMSHLE03', 0, 'ascii');
  bytes.writeUInt32LE(length, 8);
  bytes.writeUInt32LE(items.length, 12);
  let cursor = 16;
  for (const chunk of chunks) { chunk.copy(bytes, cursor); cursor += chunk.byteLength; }
  const contentHash = sha256(bytes);
  const resource = `mesh-resource/${contentHash.slice('sha256:'.length)}`;
  for (const item of items) {
    item.payload.source = {
      kind: 'resource', resource, contentHash, byteLength: bytes.byteLength, encoding,
      ...item.resourceOffsets,
    };
    delete item.resourceOffsets;
  }
  return { resource, contentHash, bytes };
}

function material(id, texture) {
  return {
    schemaVersion: 3,
    id,
    color: [1, 1, 1, 1],
    texture,
    roughness: 1,
    textureTint: [1, 1, 1, 1],
    emissionColor: [1, 1, 1],
    emissionIntensity: 0.06,
    uvStrategy: texture === null ? 'flat' : 'planar',
    alphaMode: { kind: 'mask', cutoff: 0.1 },
    doubleSided: true,
  };
}

function resourceReadout(identity, contentHash, byteLength, mediaType, file) {
  return { identity, contentHash, byteLength, mediaType, url: `/assets/depth-splat/${file}` };
}

function parseGlb(bytes) {
  if (bytes.toString('ascii', 0, 4) !== 'glTF' || bytes.readUInt32LE(4) !== 2
    || bytes.readUInt32LE(8) !== bytes.byteLength) throw new Error('invalid GLB header');
  let json = null;
  let binary = null;
  for (let offset = 12; offset < bytes.byteLength;) {
    const length = bytes.readUInt32LE(offset);
    const type = bytes.readUInt32LE(offset + 4);
    const chunk = bytes.subarray(offset + 8, offset + 8 + length);
    if (type === 0x4e4f534a) json = JSON.parse(chunk.toString('utf8').replace(/[\0 ]+$/u, ''));
    if (type === 0x004e4942) binary = chunk;
    offset += 8 + length;
  }
  if (json === null || binary === null) throw new Error('GLB needs JSON and BIN chunks');
  return { json, binary };
}

function accessor(json, binary, accessorIndex, expectedType, integer = false) {
  const value = json.accessors?.[accessorIndex];
  if (value?.type !== expectedType || value.bufferView === undefined || value.sparse !== undefined) {
    throw new Error(`unsupported ${expectedType} accessor`);
  }
  const view = json.bufferViews?.[value.bufferView];
  if (view?.buffer !== 0) throw new Error('accessor must use the embedded BIN buffer');
  const components = { SCALAR: 1, VEC2: 2, VEC3: 3, VEC4: 4 }[value.type];
  const componentBytes = { 5121: 1, 5123: 2, 5125: 4, 5126: 4 }[value.componentType];
  if (componentBytes === undefined) throw new Error(`unsupported component type ${value.componentType}`);
  const stride = view.byteStride ?? componentBytes * components;
  const start = (view.byteOffset ?? 0) + (value.byteOffset ?? 0);
  const data = new DataView(binary.buffer, binary.byteOffset, binary.byteLength);
  const result = [];
  for (let item = 0; item < value.count; item += 1) {
    for (let component = 0; component < components; component += 1) {
      const offset = start + item * stride + component * componentBytes;
      let number = value.componentType === 5121 ? data.getUint8(offset)
        : value.componentType === 5123 ? data.getUint16(offset, true)
          : value.componentType === 5125 ? data.getUint32(offset, true)
            : data.getFloat32(offset, true);
      if (value.normalized === true) {
        number /= value.componentType === 5121 ? 255 : value.componentType === 5123 ? 65535 : 4294967295;
      }
      result.push(integer ? Math.trunc(number) : number);
    }
  }
  return result;
}

function embeddedTexture(json, binary, materialIndex) {
  const textureIndex = json.materials?.[materialIndex]?.pbrMetallicRoughness?.baseColorTexture?.index;
  if (textureIndex === undefined) return null;
  const imageIndex = json.textures?.[textureIndex]?.source;
  const image = json.images?.[imageIndex];
  if (image?.mimeType !== 'image/png' || image.bufferView === undefined) throw new Error('texture must be embedded PNG');
  const view = json.bufferViews[image.bufferView];
  return Buffer.from(binary.subarray(view.byteOffset ?? 0, (view.byteOffset ?? 0) + view.byteLength));
}

function hasTransform(node) {
  return node.matrix !== undefined || node.translation !== undefined
    || node.rotation !== undefined || node.scale !== undefined;
}

function boundsOf(positions) {
  const min = [Infinity, Infinity, Infinity];
  const max = [-Infinity, -Infinity, -Infinity];
  for (let index = 0; index < positions.length; index += 3) {
    for (let axis = 0; axis < 3; axis += 1) {
      min[axis] = Math.min(min[axis], positions[index + axis]);
      max[axis] = Math.max(max[axis], positions[index + axis]);
    }
  }
  return { min, max };
}

function f32Bytes(values) {
  const bytes = Buffer.alloc(values.length * 4);
  values.forEach((value, index) => bytes.writeFloatLE(value, index * 4));
  return bytes;
}

function u32Bytes(values) {
  const bytes = Buffer.alloc(values.length * 4);
  values.forEach((value, index) => bytes.writeUInt32LE(value, index * 4));
  return bytes;
}

function sha256(bytes) { return `sha256:${createHash('sha256').update(bytes).digest('hex')}`; }
function pad(value) { return String(value).padStart(2, '0'); }
function pngDimensions(bytes) {
  if (bytes.toString('hex', 0, 8) !== '89504e470d0a1a0a' || bytes.toString('ascii', 12, 16) !== 'IHDR') {
    throw new Error('invalid PNG texture');
  }
  return [bytes.readUInt32BE(16), bytes.readUInt32BE(20)];
}
