import { chromium } from 'playwright-core';

const seed = process.env.CRAFTSURVIVE_TEST_SEED ?? `0x${BigInt(Date.now()).toString(16)}`;
const root = process.env.CRAFTSURVIVE_URL ?? 'http://127.0.0.1:4419';
const url = new URL(root);
url.searchParams.set('course', 'far');
url.searchParams.set('seed', seed);
const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_BIN ?? '/usr/bin/chromium',
  headless: true,
  args: ['--no-sandbox', '--use-gl=angle', '--use-angle=swiftshader'],
});

const residency = async (page) => {
  const text = await page.locator('[data-residency]').textContent();
  const match = text?.match(/^(-?\d+),(-?\d+) · (\d+) resident/);
  if (match === null || match === undefined) throw new Error(`invalid residency readout: ${text}`);
  return { center: [Number(match[1]), Number(match[2])], resident: Number(match[3]), text };
};

const overlayEntries = async (page) => {
  const text = await page.locator('[data-terrain]').textContent();
  const match = text?.match(/^unbounded v2 · scale \d+ · (\d+) edits$/);
  if (match === null || match === undefined) throw new Error(`invalid terrain readout: ${text}`);
  return Number(match[1]);
};

const origin = async (page) => {
  const text = await page.locator('[data-world-origin]').textContent();
  const match = text?.match(/^(-?\d+), (-?\d+), (-?\d+) · revision (\d+)$/);
  if (match === null || match === undefined) throw new Error(`invalid origin readout: ${text}`);
  return { cell: [Number(match[1]), Number(match[2]), Number(match[3])], revision: Number(match[4]) };
};

const position = async (page, selector) => {
  const text = await page.locator(selector).textContent();
  const values = text?.split(', ').map(Number);
  if (values === undefined || values.length !== 3 || values.some((value) => !Number.isFinite(value))) {
    throw new Error(`invalid position readout at ${selector}: ${text}`);
  }
  return values;
};

try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const errors = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  await page.goto(url.href);
  await page.waitForFunction(
    () => document.querySelector('[data-status]')?.textContent === 'connected',
    null,
    { timeout: 30_000 },
  );
  await page.locator('[data-terrain]').filter({ hasNotText: '—' }).waitFor({ timeout: 30_000 });
  await page.mouse.click(640, 360);
  await page.waitForFunction(() => document.pointerLockElement !== null);
  await page.mouse.move(640, 620);
  await page.locator('[data-target]').filter({ hasNotText: 'out of reach' }).waitFor({ timeout: 10_000 });
  const beforeEntries = await overlayEntries(page);
  await page.keyboard.press('KeyF');
  await page.waitForFunction((count) => {
    const text = document.querySelector('[data-terrain]')?.textContent ?? '';
    const match = text.match(/(\d+) edits$/);
    return match !== null && Number(match[1]) > count;
  }, beforeEntries);

  const initialOrigin = await origin(page);
  const samples = [await residency(page)];
  await page.keyboard.down('ShiftLeft');
  await page.keyboard.down('KeyW');
  for (let step = 0; step < 18; step += 1) {
    await page.waitForTimeout(500);
    samples.push(await residency(page));
  }
  await page.keyboard.up('KeyW');
  await page.keyboard.up('ShiftLeft');
  const centers = [...new Set(samples.map((sample) => sample.center.join(',')))];
  const maxResident = Math.max(...samples.map((sample) => sample.resident));
  const finalOrigin = await origin(page);
  const finalGlobal = await position(page, '[data-player-position]');
  const finalLocal = await position(page, '[data-player-local-position]');
  if (centers.length < 4) throw new Error(`far route crossed too few centers: ${centers.join(' -> ')}`);
  if (maxResident > 64) throw new Error(`resident budget exceeded: ${maxResident}`);
  if (finalOrigin.revision < initialOrigin.revision + 2) {
    throw new Error(`far route crossed too few rebase thresholds: ${initialOrigin.revision} -> ${finalOrigin.revision}`);
  }
  if (finalGlobal[0] < 262_000 || Math.max(Math.abs(finalLocal[0]), Math.abs(finalLocal[2])) >= 32) {
    throw new Error(`positive far route lost global/local frame: global ${finalGlobal}, local ${finalLocal}`);
  }

  await page.reload();
  await page.waitForFunction(
    () => document.querySelector('[data-status]')?.textContent === 'connected',
    null,
    { timeout: 30_000 },
  );
  await page.locator('[data-terrain]').filter({ hasNotText: '—' }).waitFor({ timeout: 30_000 });
  const reloadedEntries = await overlayEntries(page);
  if (reloadedEntries !== beforeEntries + 1) {
    throw new Error(`overlay did not reload exactly once: ${beforeEntries} -> ${reloadedEntries}`);
  }

  const changedSeedUrl = new URL(url);
  changedSeedUrl.searchParams.set('seed', `0x${(BigInt(seed) + 1n).toString(16)}`);
  await page.goto(changedSeedUrl.href);
  await page.waitForFunction(
    () => document.querySelector('[data-status]')?.textContent === 'connected',
    null,
    { timeout: 30_000 },
  );
  await page.locator('[data-terrain]').filter({ hasNotText: '—' }).waitFor({ timeout: 30_000 });
  if (await overlayEntries(page) !== 0) throw new Error('seed change reused the prior overlay');

  const negativeUrl = new URL(url);
  negativeUrl.searchParams.set('course', 'far-negative');
  await page.goto(negativeUrl.href);
  await page.waitForFunction(
    () => document.querySelector('[data-status]')?.textContent === 'connected',
    null,
    { timeout: 30_000 },
  );
  await page.locator('[data-terrain]').filter({ hasNotText: '—' }).waitFor({ timeout: 30_000 });
  const negativeInitialOrigin = await origin(page);
  await page.mouse.click(640, 360);
  await page.waitForFunction(() => document.pointerLockElement !== null);
  await page.keyboard.down('ShiftLeft');
  await page.keyboard.down('KeyW');
  await page.waitForTimeout(9_000);
  await page.keyboard.up('KeyW');
  await page.keyboard.up('ShiftLeft');
  const negativeFinalOrigin = await origin(page);
  const negativeGlobal = await position(page, '[data-player-position]');
  const negativeLocal = await position(page, '[data-player-local-position]');
  if (negativeInitialOrigin.cell[0] >= 0 || negativeGlobal[0] > -262_000) {
    throw new Error(`negative far route did not retain signed global identity: ${negativeGlobal}`);
  }
  if (negativeFinalOrigin.revision < negativeInitialOrigin.revision + 2) {
    throw new Error(`negative route crossed too few rebase thresholds: ${negativeInitialOrigin.revision} -> ${negativeFinalOrigin.revision}`);
  }
  if (Math.max(Math.abs(negativeLocal[0]), Math.abs(negativeLocal[2])) >= 32) {
    throw new Error(`negative far route escaped local frame: ${negativeLocal}`);
  }
  if (errors.length > 0) throw new Error(`browser page errors: ${errors.join('; ')}`);

  console.log(JSON.stringify({
    proof: 'CRAFTSURVIVE_UNBOUNDED_PERSISTENCE_ROUTE',
    seed,
    startUrl: url.href,
    centers,
    maxResident,
    persistedOverlayEntries: reloadedEntries,
    seedIsolation: true,
    first: samples[0],
    last: samples.at(-1),
    positiveOrigin: { initial: initialOrigin, final: finalOrigin },
    positiveGlobal: finalGlobal,
    positiveLocal: finalLocal,
    negativeOrigin: { initial: negativeInitialOrigin, final: negativeFinalOrigin },
    negativeGlobal,
    negativeLocal,
  }));
} finally {
  await browser.close();
}
