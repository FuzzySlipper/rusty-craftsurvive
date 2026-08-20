import { chromium } from 'playwright-core';

const url = new URL(process.env.CRAFTSURVIVE_URL ?? 'http://127.0.0.1:4419');
url.searchParams.set('course', 'ghost-plate');
const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_BIN ?? '/usr/bin/chromium',
  headless: true,
  args: ['--no-sandbox', '--use-gl=angle', '--use-angle=swiftshader'],
});

try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  await page.goto(url.href);
  await page.locator('[data-status]').filter({ hasText: 'connected' }).waitFor({ timeout: 30_000 });
  await page.waitForFunction(() =>
    document.querySelector('[data-garden-load]')?.textContent?.includes('8 resident captures'),
  undefined, { timeout: 60_000 });

  const panel = page.locator('[data-garden-panel]');
  await page.keyboard.press('KeyV');
  await panel.waitFor({ state: 'visible' });
  if (await panel.locator('[data-lab-mode], [data-lab-splat-resolution]').count() !== 0) {
    throw new Error('focused route leaked sprite/splat controls');
  }
  await panel.locator('[data-lab-ghost-sectors]').selectOption('4');
  await page.waitForFunction(() =>
    document.querySelector('[data-garden-load]')?.textContent?.includes('4 resident captures'),
  undefined, { timeout: 60_000 });
  await panel.locator('[data-lab-ghost-transition]').selectOption('hard-cut');
  if (!(await page.locator('[data-garden-sector]').textContent())?.includes('hard-cut')) {
    throw new Error('hard-cut transition did not reach the Engine readout');
  }
  await panel.locator('[data-lab-ghost-transition]').selectOption('edge-echo');
  if (!(await page.locator('[data-garden-sector]').textContent())?.includes('edge-echo')) {
    throw new Error('edge-echo transition did not reach the Engine readout');
  }
  await page.keyboard.press('Escape');
  await page.keyboard.down('KeyD');
  await page.waitForTimeout(2_500);
  await page.keyboard.up('KeyD');
  await page.waitForTimeout(500);

  if (errors.length > 0) throw new Error(`focused ghost-plate errors: ${errors.join('; ')}`);
  const selection = await page.locator('[data-garden-sector]').textContent();
  const load = await page.locator('[data-garden-load]').textContent();
  console.log(`focused ghost-plate smoke passed: ${selection} · ${load}`);
} finally {
  await browser.close();
}
