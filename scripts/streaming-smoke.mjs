import { chromium } from 'playwright-core';

const url = process.env.CRAFTSURVIVE_URL ?? 'http://127.0.0.1:4419/?course=stream';
const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_BIN ?? '/usr/bin/chromium',
  headless: true,
  args: ['--no-sandbox', '--use-gl=angle', '--use-angle=swiftshader'],
});

const parseResidency = (text) => {
  const match = text?.match(/^(-?\d+),(-?\d+) · (\d+) resident \/ (\d+) pinned \/ (\d+) loading · (\d+) admitted \/ (\d+) evicted · (\d+) KiB · ([\d.]+) \+ ([\d.]+) ms$/);
  if (match === null || match === undefined) throw new Error(`invalid residency readout: ${text}`);
  return {
    center: [Number(match[1]), Number(match[2])],
    resident: Number(match[3]),
    pinned: Number(match[4]),
    loading: Number(match[5]),
    admitted: Number(match[6]),
    evicted: Number(match[7]),
    residentKiB: Number(match[8]),
    generationMs: Number(match[9]),
    admissionMs: Number(match[10]),
  };
};

try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const errors = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  await page.goto(url);
  await page.locator('[data-status]').filter({ hasText: 'connected' }).waitFor({ timeout: 30_000 });
  await page.mouse.click(640, 360);
  await page.waitForFunction(() => document.pointerLockElement !== null);
  await page.waitForFunction(() => document.querySelector('[data-motion]')?.textContent?.includes('grounded'), null, { timeout: 15_000 });

  const samples = [];
  const sample = async () => samples.push(parseResidency(await page.locator('[data-residency]').textContent()));
  await sample();
  await page.keyboard.down('ShiftLeft');
  await page.keyboard.down('KeyW');
  for (let step = 0; step < 18; step += 1) {
    if (step % 3 === 0) await page.keyboard.press('Space');
    await page.waitForTimeout(500);
    await sample();
  }
  await page.keyboard.up('KeyW');
  await page.keyboard.down('KeyS');
  for (let step = 0; step < 8; step += 1) {
    await page.waitForTimeout(500);
    await sample();
  }
  await page.keyboard.up('KeyS');
  await page.keyboard.up('ShiftLeft');

  const centers = [...new Set(samples.map((value) => value.center.join(',')))];
  const maxResident = Math.max(...samples.map((value) => value.resident));
  const final = samples.at(-1);
  if (centers.length < 3) throw new Error(`streaming route crossed too few chunk centers: ${centers.join(' -> ')}`);
  if (maxResident > 80) throw new Error(`resident chunk budget exceeded: ${maxResident}`);
  if (final.evicted === 0) throw new Error('streaming route did not evict any chunk');
  if (errors.length > 0) throw new Error(`browser page errors: ${errors.join('; ')}`);
  console.log(JSON.stringify({
    proof: 'CRAFTSURVIVE_BOUNDED_STREAMING_ROUTE',
    url,
    centers,
    maxResident,
    final,
    samples,
  }));
} finally {
  await browser.close();
}
