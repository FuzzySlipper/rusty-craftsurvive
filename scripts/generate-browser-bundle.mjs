import { execFile } from 'node:child_process';
import { copyFile, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { promisify } from 'node:util';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const [output] = process.argv.slice(2);
if (output === undefined) {
  throw new Error('usage: generate-browser-bundle.mjs <output-directory>');
}

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, '..');
const engineRoot = resolve(process.env.RUSTY_ENGINE_ROOT ?? join(repositoryRoot, '..', 'rusty-engine'));
const engineHostRoot = join(engineRoot, 'render', 'artifacts', 'product-browser-host');
const engineLiveDebugPanel = join(engineRoot, 'studio', 'artifacts', 'live-debug-panel', 'index.js');
const uiDirectory = join(repositoryRoot, 'src', 'ui');
const uiProject = join(uiDirectory, 'tsconfig.json');
const compiledUiSource = join(uiDirectory, 'generated', 'source', 'main.js');
const runtimeAdapterSource = 'export const PRODUCT_RUNTIME_HTTP_BASE_PATH = "/__rusty/product/runtime/";\n';
const run = promisify(execFile);

const { productBrowserBundleAssets } = await import(pathToFileURL(join(engineHostRoot, 'index.js')).href);
const engineHostModule = await readFile(join(engineHostRoot, 'product-browser-host.js'), 'utf8');
const assets = productBrowserBundleAssets({
  engineHostModule,
  uiModule: './ui/main.js',
  runtimeAdapterModule: './runtime-adapter.js',
  lifecycleMode: 'realtime',
  realtimeAdvanceOwner: 'browser',
  uiProjection: {
    expectedStream: 'craftsurvive.terrain',
    expectedContract: 'craftsurvive.terrain.v1',
  },
});

await run(join(engineRoot, 'render', 'node_modules', '.bin', 'tsc'), ['--project', uiProject]);
await mkdir(join(output, 'ui'), { recursive: true });
await rm(join(output, 'renderer-preload.json'), { force: true });
for (const asset of assets) {
  const path = join(output, asset.name);
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, asset.content);
}
await copyFile(compiledUiSource, join(output, 'ui', 'main.js'));
await copyFile(engineLiveDebugPanel, join(output, 'ui', 'live-debug-panel.js'));
await writeFile(join(output, 'runtime-adapter.js'), runtimeAdapterSource);
