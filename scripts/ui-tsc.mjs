// Compiles the DOM companion, or checks it with --check, against the Engine SDK's product-UI types.
// The product build passes the types from $(RustyEngineProductUiTypes); check:ui asks MSBuild for
// the same property, so both compile against the pinned pair's declarations and nothing mirrors them.
//
// usage: node scripts/ui-tsc.mjs --types <rusty-engine-product-ui.d.ts> [--check]
import { execFile } from 'node:child_process';
import { mkdir, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { promisify } from 'node:util';

const repositoryRoot = resolve(import.meta.dirname, '..');
const uiRoot = join(repositoryRoot, 'src', 'ui');

export async function compileUi(types, { check = false } = {}) {
  if (!types || !existsSync(types)) {
    throw new Error(`The Engine SDK's product-UI types were not found at '${types}'. Run \`rusty install\`, then restore the product.`);
  }

  // A generated project that extends the authored one and adds the SDK's declarations.
  const generated = join(uiRoot, 'generated');
  await mkdir(generated, { recursive: true });
  const project = join(generated, 'tsconfig.sdk.json');
  await writeFile(project, JSON.stringify({ extends: '../tsconfig.json', include: ['../*.ts'], files: [types] }, null, 2));
  const args = ['exec', 'tsc', '--project', project];
  if (check) {
    args.push('--noEmit');
  } else {
    // The staged UI is exactly this compile's output: nothing a removed source once emitted stays.
    await rm(join(generated, 'source'), { recursive: true, force: true });
  }
  await promisify(execFile)('pnpm', args, { cwd: repositoryRoot });
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const argv = process.argv.slice(2);
  const typesIndex = argv.indexOf('--types');
  try {
    await compileUi(typesIndex >= 0 ? argv[typesIndex + 1] : undefined, { check: argv.includes('--check') });
  } catch (error) {
    process.stderr.write(`${error.stdout ?? ''}${error.stderr ?? ''}${error.message}\n`);
    process.exit(1);
  }
}
