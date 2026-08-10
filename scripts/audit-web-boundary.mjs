import { readFile, readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

async function files(root) {
  const entries = await readdir(root, { withFileTypes: true });
  return (await Promise.all(entries.map((entry) => entry.isDirectory() ? files(join(root, entry.name)) : [join(root, entry.name)]))).flat();
}
const violations = [];
for (const path of await files(fileURLToPath(new URL('../web/src', import.meta.url)))) {
  const source = await readFile(path, 'utf8');
  for (const forbidden of ['three', 'renderer-webview', 'private/', 'createElement(\'canvas\'', 'createElement("canvas"']) {
    if (source.includes(forbidden)) violations.push(`${path}: forbidden downstream renderer ownership token ${forbidden}`);
  }
  for (const match of source.matchAll(/from\s+['"](@rusty-engine\/[^'"]+)['"]/g)) {
    if (match[1] !== '@rusty-engine/application-host') violations.push(`${path}: forbidden Engine package ${match[1]}`);
  }
}
if (violations.length > 0) { console.error(violations.join('\n')); process.exit(1); }
console.log('CraftSurvive browser boundary: application-host only');
