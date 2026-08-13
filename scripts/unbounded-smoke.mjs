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
  if (centers.length < 4) throw new Error(`far route crossed too few centers: ${centers.join(' -> ')}`);
  if (maxResident > 64) throw new Error(`resident budget exceeded: ${maxResident}`);

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
  }));
} finally {
  await browser.close();
}
