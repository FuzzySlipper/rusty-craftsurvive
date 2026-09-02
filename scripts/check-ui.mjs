import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { join, resolve } from 'node:path';

const repositoryRoot = resolve(import.meta.dirname, '..');

await promisify(execFile)('pnpm', [
  'exec',
  'tsc',
  '--noEmit',
  '--project',
  join(repositoryRoot, 'src', 'ui', 'tsconfig.json'),
], { cwd: repositoryRoot });
