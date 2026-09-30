// Type-checks the DOM companion against the pinned Engine pair's product-UI declarations.
import { execFile } from 'node:child_process';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';
import { compileUi } from './ui-tsc.mjs';

const repositoryRoot = resolve(import.meta.dirname, '..');
const project = join(repositoryRoot, 'src', 'CraftSurvive.Game', 'CraftSurvive.Game.csproj');
const run = promisify(execFile);

// The SDK declares where its types are; restoring the product makes the property resolve.
async function sdkTypes() {
  const read = async () => (await run('dotnet', ['msbuild', project, '-getProperty:RustyEngineProductUiTypes'], { cwd: repositoryRoot })).stdout.trim();
  const types = await read();
  if (types.length > 0) return types;
  await run('dotnet', ['restore', project], { cwd: repositoryRoot });
  return read();
}

try {
  await compileUi(await sdkTypes(), { check: true });
} catch (error) {
  process.stderr.write(`${error.stdout ?? ''}${error.stderr ?? ''}${error.message}\n`);
  process.exit(1);
}
