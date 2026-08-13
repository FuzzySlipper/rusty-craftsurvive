import { chromium } from 'playwright-core';

const url = process.env.CRAFTSURVIVE_URL ?? process.env.BASE_URL ?? 'http://127.0.0.1:4419';
const executablePath = process.env.CHROMIUM_BIN ?? '/usr/bin/chromium';
const captureDirectory = process.env.CRAFTSURVIVE_EDIT_CAPTURE_DIR;
const captureDelaysMs = (process.env.CRAFTSURVIVE_EDIT_CAPTURE_DELAYS_MS ?? '0,4000')
  .split(',')
  .map((value) => Number(value.trim()));
if (captureDelaysMs.some((value) => !Number.isSafeInteger(value) || value < 0 || value > 30_000)) {
  throw new Error('CRAFTSURVIVE_EDIT_CAPTURE_DELAYS_MS must contain comma-separated integers in 0..=30000');
}
const browser = await chromium.launch({
  executablePath,
  headless: true,
  args: ['--no-sandbox', '--use-gl=angle', '--use-angle=swiftshader'],
});

const number = async (page, selector) => Number(await page.locator(selector).textContent());
const clock = () => performance.now();

try {
  const healthResponse = await fetch(new URL('/health', url));
  const health = await healthResponse.json();
  if (!healthResponse.ok || health.project !== 'rusty-craftsurvive') {
    throw new Error(`service identity mismatch: ${healthResponse.status} ${JSON.stringify(health)}`);
  }

  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const pageErrors = [];
  page.on('pageerror', (error) => pageErrors.push(String(error)));
  const startupStarted = clock();
  await page.goto(url);
  await page.locator('[data-status]').filter({ hasText: 'connected' }).waitFor({ timeout: 30_000 });
  await page.locator('[data-player-position]').filter({ hasNotText: '—' }).waitFor({ timeout: 30_000 });
  const startupElapsedMs = clock() - startupStarted;
  const startupParts = (await page.locator('[data-startup]').textContent())
    .match(/^([\d.]+) \+ ([\d.]+) \+ ([\d.]+) ms$/);
  if (startupParts === null) throw new Error('startup readout was not measurable');

  const initialRevision = await number(page, '[data-world-revision]');
  await page.mouse.click(640, 360);
  await page.waitForFunction(() => document.pointerLockElement !== null);
  await page.mouse.move(640, 660);
  await page.waitForFunction(
    () => Number.parseFloat(document.querySelector('[data-player-view]')?.textContent?.split('/')[1] ?? '') < -35,
  );
  await page.mouse.move(640, 960);
  await page.waitForFunction(
    () => Number.parseFloat(document.querySelector('[data-player-view]')?.textContent?.split('/')[1] ?? '') < -70,
  );
  await page.locator('[data-target]').filter({ hasNotText: 'out of reach' }).waitFor();

  const destroyStarted = clock();
  if (captureDirectory) {
    await page.screenshot({ path: `${captureDirectory}/before-destroy.png` });
  }
  await page.mouse.down({ button: 'left' });
  await page.mouse.up({ button: 'left' });
  await page.waitForFunction(
    (revision) => Number(document.querySelector('[data-world-revision]')?.textContent) > revision,
    initialRevision,
    { timeout: 30_000 },
  );
  await page.locator('[data-edit]').filter({ hasText: 'destroy' }).waitFor();
  const destroyElapsedMs = clock() - destroyStarted;
  const captureTimesMs = [];
  if (captureDirectory) {
    const captureStarted = clock();
    for (const delayMs of captureDelaysMs) {
      const remaining = captureStarted + delayMs - clock();
      if (remaining > 0) await page.waitForTimeout(remaining);
      await page.screenshot({
        path: `${captureDirectory}/after-destroy-${String(delayMs).padStart(4, '0')}ms.png`,
      });
      captureTimesMs.push(Math.round(clock() - destroyStarted));
    }
  }
  const editText = await page.locator('[data-edit]').textContent();
  const edit = editText?.match(/^destroy ([-\d]+), ([-\d]+), ([-\d]+) · (\d+) voxels · ([\d.]+) ms \(([\d.]+) mesh\) · (\d+) dirty \/ (\d+) replaced \/ (\d+) destroyed · (\d+) bytes · revision (\d+)$/);
  if (edit === undefined || edit === null) throw new Error(`destroy readout was not measurable: ${editText}`);
  const finalRevision = await number(page, '[data-world-revision]');
  if (finalRevision !== initialRevision + 1 || Number(edit[4]) !== 1) {
    throw new Error(`expected one accepted single-voxel edit: ${initialRevision} -> ${finalRevision}; ${editText}`);
  }
  if (pageErrors.length > 0) throw new Error(`browser page errors: ${pageErrors.join('; ')}`);

  console.log(JSON.stringify({
    proof: 'CRAFTSURVIVE_EDIT_PERFORMANCE',
    url,
    surface: await page.locator('[data-surface]').textContent(),
    terrain: await page.locator('[data-terrain]').textContent(),
    startupElapsedMs,
    rustGenerationMs: Number(startupParts[1]),
    rustAuthorityBuildMs: Number(startupParts[2]),
    rustInitialMeshBuildMs: Number(startupParts[3]),
    destroyElapsedMs,
    rustEditMs: Number(edit[5]),
    rustMeshBuildMs: Number(edit[6]),
    dirtyChunks: Number(edit[7]),
    replacementCount: Number(edit[8]),
    destroyCount: Number(edit[9]),
    encodedBytes: Number(edit[10]),
    affectedVoxels: Number(edit[4]),
    initialRevision,
    finalRevision,
    voxel: edit.slice(1, 4).map(Number),
    captureTimesMs,
  }));
} finally {
  await browser.close();
}
