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
  await page.waitForFunction(() => {
    const text = document.querySelector('[data-garden-load]')?.textContent ?? '';
    return text.includes('capture') && !text.includes('loading');
  }, undefined, { timeout: 60_000 });

  const canvas = page.locator('canvas[data-rusty-application-renderer="engine-owned"]');
  if (await canvas.count() !== 1) throw new Error('Engine-owned lab canvas was not mounted exactly once');
  const initialPixels = await canvas.screenshot();
  const initialSelection = await page.locator('[data-garden-sector]').textContent();
  const initialLoad = await page.locator('[data-garden-load]').textContent();
  if (!initialSelection?.includes('spatial-wizard · runtime · sprite-splat')) {
    throw new Error(`unexpected initial lab selection: ${initialSelection}`);
  }
  if (!initialLoad?.includes('draws') || !initialLoad.includes('samples')) {
    throw new Error(`missing bounded cost readout: ${initialLoad}`);
  }

  await page.keyboard.press('KeyV');
  const panel = page.locator('[data-garden-panel]');
  await panel.waitFor({ state: 'visible' });
  await panel.locator('[data-lab-subject]').selectOption('rigged-wizard');
  await panel.locator('[data-lab-sector]').fill('7');
  for (const mode of ['sprite', 'relit', 'depth-parallax', 'sprite-splat', 'full-splat']) {
    await panel.locator('[data-lab-mode]').selectOption(mode);
    await page.waitForFunction((selectedMode) =>
      document.querySelector('[data-garden-sector]')?.textContent?.includes(` · ${selectedMode} · `), mode);
  }
  await panel.locator('[data-lab-depth]').evaluate((element) => {
    element.value = '0.7';
    element.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await page.waitForFunction(() => {
    const text = document.querySelector('[data-garden-sector]')?.textContent ?? '';
    return text.includes('rigged-wizard · prepared · full-splat · dir-07 manual');
  });
  await page.waitForFunction(() => document.querySelector('[data-lab-metrics]')?.textContent?.includes('195 files'));
  const preparedPixels = await canvas.screenshot();
  if (initialPixels.equals(preparedPixels)) throw new Error('prepared mode/sector controls did not visibly change the lab');

  await panel.locator('[data-lab-producer]').selectOption('runtime');
  await panel.locator('[data-lab-recapture]').click();
  await page.waitForFunction(() => {
    const text = document.querySelector('[data-garden-load]')?.textContent ?? '';
    return text.includes('rigged-wizard') || (!text.includes('capture n/a') && text.includes('capture'));
  });
  const runtimeSelection = await page.locator('[data-garden-sector]').textContent();
  const runtimeLoad = await page.locator('[data-garden-load]').textContent();
  if (!runtimeSelection?.includes('rigged-wizard · runtime · full-splat')) {
    throw new Error(`runtime source switch was not retained: ${runtimeSelection}`);
  }
  if (runtimeLoad?.includes('capture n/a')) throw new Error(`runtime recapture did not publish cost: ${runtimeLoad}`);

  await panel.locator('[data-lab-fallback]').click();
  await page.locator('[data-presentation-diagnostic]')
    .filter({ hasText: 'fallback probe passed' }).waitFor();
  const fallbackLoad = await page.locator('[data-garden-load]').textContent();

  await panel.locator('[data-lab-resume]').click();
  await panel.waitFor({ state: 'hidden' });
  await page.mouse.click(640, 360);
  await page.waitForFunction(() => document.pointerLockElement !== null);
  const beforeMovement = await page.locator('[data-player-position]').textContent();
  await page.keyboard.down('KeyD');
  await page.waitForTimeout(500);
  await page.keyboard.up('KeyD');
  const afterMovement = await page.locator('[data-player-position]').textContent();
  if (beforeMovement === afterMovement) throw new Error('first-person movement stopped after closing the lab panel');

  if (errors.length > 0) throw new Error(`runtime voxel-sprite page errors: ${errors.join('; ')}`);
  console.log(JSON.stringify({
    proof: 'CRAFTSURVIVE_RUNTIME_VOXEL_SPRITE_LAB',
    initialSelection,
    initialLoad,
    runtimeSelection,
    runtimeLoad,
    fallbackLoad,
    preparedControlVisibleChange: true,
    firstPersonMovementAfterPanel: true,
  }));
} finally {
  await browser.close();
}
