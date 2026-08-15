import { mkdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { chromium } from 'playwright-core';

const baseUrl = process.env.CRAFTSURVIVE_URL ?? process.env.BASE_URL ?? 'http://127.0.0.1:4419';
const evidenceDirectory = resolve(process.env.CRAFTSURVIVE_EVIDENCE_DIR ?? 'live-evidence/task-7018');
const screenshotPath = resolve(evidenceDirectory, 'runtime-only-default.png');
const controlsScreenshotPath = resolve(evidenceDirectory, 'runtime-only-controls.png');
const url = new URL(baseUrl);
url.searchParams.set('course', 'garden');
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
  await page.waitForFunction(() => {
    const text = document.querySelector('[data-garden-load]')?.textContent ?? '';
    return text.includes('capture') && !text.includes('loading') && !text.includes('capture n/a');
  }, undefined, { timeout: 60_000 });

  const canvas = page.locator('canvas[data-rusty-application-renderer="engine-owned"]');
  if (await canvas.count() !== 1) throw new Error('Engine-owned lab canvas was not mounted exactly once');
  const initialSelection = await page.locator('[data-garden-sector]').textContent();
  const initialLoad = await page.locator('[data-garden-load]').textContent();
  if (!initialSelection?.includes('spatial-wizard · retained/runtime · RED sprite-splat')) {
    throw new Error(`unexpected initial lab selection: ${initialSelection}`);
  }
  if (!initialSelection.includes('192px') || !initialLoad?.includes('draws') || !initialLoad.includes('samples')) {
    throw new Error(`missing runtime capture resolution/cost readout: ${initialSelection} / ${initialLoad}`);
  }
  mkdirSync(evidenceDirectory, { recursive: true });
  await canvas.screenshot({ path: screenshotPath });

  await page.keyboard.press('KeyV');
  const panel = page.locator('[data-garden-panel]');
  await panel.waitFor({ state: 'visible' });
  const initialComparison = await panel.locator('[data-lab-comparison]').textContent();
  const initialMetrics = await panel.locator('[data-lab-metrics]').textContent();
  if (!initialComparison?.includes('capture MATCHED') || !initialComparison.includes('all lighting MATCHED')) {
    throw new Error(`default pair is not controlled: ${initialComparison}`);
  }
  if (!initialMetrics?.includes('3 GLBs') || initialMetrics.includes('files')) {
    throw new Error(`active lab admitted more than the three runtime GLBs: ${initialMetrics}`);
  }

  await panel.locator('[data-lab-subject]').selectOption('rigged-wizard');
  for (const mode of ['sprite', 'depth-parallax', 'sprite-splat', 'full-splat']) {
    await panel.locator('[data-lab-mode]').selectOption(mode);
    await page.waitForFunction((selectedMode) =>
      document.querySelector('[data-garden-sector]')?.textContent?.includes(`RED ${selectedMode}`), mode);
  }

  const beforeLightingPixels = await canvas.screenshot();
  await panel.locator('[data-lab-output-gain]').evaluate((element) => {
    element.value = '1.6';
    element.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await page.waitForFunction(() => {
    const text = document.querySelector('[data-lab-comparison]')?.textContent ?? '';
    return text.includes('capture MATCHED') && text.includes('all lighting DIFFERENT');
  });
  const redGain = await panel.locator('[data-lab-output-gain-value]').textContent();
  const afterRedLightingPixels = await canvas.screenshot();
  if (beforeLightingPixels.equals(afterRedLightingPixels)) {
    throw new Error('RED post-capture output gain did not visibly affect the selected side');
  }

  await panel.locator('[data-lab-side]').selectOption('baseline');
  const blueGain = await panel.locator('[data-lab-output-gain-value]').textContent();
  if (redGain !== '1.60' || blueGain !== '1.10') {
    throw new Error(`post lighting leaked across sides: red=${redGain} blue=${blueGain}`);
  }

  await panel.locator('[data-lab-capture-key]').evaluate((element) => {
    element.value = '3.2';
    element.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await page.waitForFunction(() =>
    document.querySelector('[data-lab-comparison]')?.textContent?.includes('capture DIFFERENT'));
  const blueCaptureKey = await panel.locator('[data-lab-capture-key-value]').textContent();
  await panel.locator('[data-lab-side]').selectOption('enhanced');
  const redCaptureKey = await panel.locator('[data-lab-capture-key-value]').textContent();
  if (blueCaptureKey !== '3.2' || redCaptureKey !== '3.0') {
    throw new Error(`capture lighting leaked across sides: blue=${blueCaptureKey} red=${redCaptureKey}`);
  }
  await panel.locator('[data-lab-side]').selectOption('baseline');
  await panel.locator('[data-lab-recapture-selected]').click();
  await page.waitForFunction(() => !(document.querySelector('[data-garden-load]')?.textContent ?? '').includes('capture n/a'));

  await panel.locator('[data-lab-match]').click();
  await page.waitForFunction(() => {
    const text = document.querySelector('[data-lab-comparison]')?.textContent ?? '';
    return text.includes('capture MATCHED') && text.includes('all lighting MATCHED');
  });
  await panel.locator('[data-lab-recapture-pair]').click();
  await panel.locator('[data-lab-fallback]').click();
  await page.locator('[data-presentation-diagnostic]').filter({ hasText: 'fallback probe passed' }).waitFor();

  await page.screenshot({ path: controlsScreenshotPath, fullPage: true });

  await panel.locator('[data-lab-resume]').click();
  await panel.waitFor({ state: 'hidden' });
  await page.mouse.click(720, 450);
  await page.waitForFunction(() => document.pointerLockElement !== null);
  const beforeMovement = await page.locator('[data-player-position]').textContent();
  await page.keyboard.down('KeyD');
  await page.waitForTimeout(500);
  await page.keyboard.up('KeyD');
  const afterMovement = await page.locator('[data-player-position]').textContent();
  if (beforeMovement === afterMovement) throw new Error('first-person movement stopped after closing the lab panel');

  if (errors.length > 0) throw new Error(`runtime voxel-sprite page errors: ${errors.join('; ')}`);
  console.log(JSON.stringify({
    proof: 'CRAFTSURVIVE_RUNTIME_ONLY_VOXEL_SPRITE_LAB',
    initialSelection,
    initialLoad,
    initialComparison,
    initialMetrics,
    independentPostLighting: { redGain, blueGain },
    independentCaptureLighting: { blueCaptureKey, redCaptureKey },
    matchedRecapture: true,
    screenshotPath,
    controlsScreenshotPath,
    firstPersonMovementAfterPanel: true,
  }));
} finally {
  await browser.close();
}
