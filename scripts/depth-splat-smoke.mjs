import { chromium } from 'playwright-core';

const baseUrl = process.env.CRAFTSURVIVE_URL ?? process.env.BASE_URL ?? 'http://127.0.0.1:4419';
const url = new URL(baseUrl);
url.searchParams.set('course', 'garden');
const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_BIN ?? '/usr/bin/chromium',
  headless: true,
  args: ['--no-sandbox', '--use-gl=angle', '--use-angle=swiftshader'],
});
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const errors = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  await page.goto(url.href);
  await page.locator('[data-status]').filter({ hasText: 'connected' }).waitFor({ timeout: 30_000 });
  await page.locator('[data-garden-load]').filter({ hasText: '3 originals + 15 depictions' }).waitFor({ timeout: 60_000 });
  const canvas = page.locator('canvas[data-rusty-application-renderer="engine-owned"]');
  if (await canvas.count() !== 1) throw new Error('Engine-owned garden canvas was not mounted exactly once');
  const initialSector = await page.locator('[data-garden-sector]').textContent();
  await page.keyboard.press('Tab');
  const initialPixels = await canvas.screenshot();
  await page.keyboard.press('KeyI');
  await page.waitForFunction(() => document.querySelector('[data-garden-sector]')?.textContent?.includes('/8'));
  await page.keyboard.press('KeyO');
  await page.waitForFunction(() => document.querySelector('[data-garden-sector]')?.textContent?.includes('H:off'));
  await page.mouse.click(640, 360);
  await page.waitForFunction(() => document.pointerLockElement !== null);
  await page.keyboard.down('ShiftLeft');
  await page.keyboard.down('KeyD');
  await page.waitForTimeout(1_500);
  await page.keyboard.up('KeyD');
  await page.keyboard.up('ShiftLeft');
  const movedSector = await page.locator('[data-garden-sector]').textContent();
  const movedPixels = await canvas.screenshot();
  const initialDirection = initialSector?.match(/dir-\d{2}/)?.[0];
  const movedDirection = movedSector?.match(/dir-\d{2}/)?.[0];
  if (initialPixels.equals(movedPixels)) throw new Error('garden view did not visibly change after first-person movement');
  if (initialDirection === undefined || movedDirection === undefined || initialDirection === movedDirection) {
    throw new Error(`garden movement did not cross a direction sector: ${initialSector} -> ${movedSector}`);
  }
  await page.waitForFunction(() => document.querySelector('[data-motion]')?.textContent?.startsWith('grounded'));
  await page.keyboard.down('Space');
  await page.waitForFunction(() => document.querySelector('[data-motion]')?.textContent?.startsWith('airborne'));
  await page.keyboard.up('Space');
  if (errors.length > 0) throw new Error(`garden page errors: ${errors.join('; ')}`);
  console.log(JSON.stringify({
    proof: 'CRAFTSURVIVE_DEPTH_SPLAT_GARDEN',
    initialSector,
    movedSector,
    load: await page.locator('[data-garden-load]').textContent(),
    modeToggle: true,
    hysteresisToggle: true,
    airborne: true,
    visibleChange: true,
  }));
} finally {
  await browser.close();
}
