import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, '..');
const engineRoot = resolve(process.env.RUSTY_ENGINE_ROOT ?? join(repositoryRoot, '..', 'rusty-engine'));
const typescript = join(engineRoot, 'render', 'node_modules', '.bin', 'tsc');

await promisify(execFile)(typescript, [
  '--noEmit',
  '--project',
  join(repositoryRoot, 'src', 'ui', 'tsconfig.json'),
]);
