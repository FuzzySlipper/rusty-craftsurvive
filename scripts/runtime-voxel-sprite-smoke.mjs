import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { chromium } from 'playwright-core';

const baseUrl = process.env.CRAFTSURVIVE_URL ?? process.env.BASE_URL ?? 'http://127.0.0.1:4419';
const evidenceDirectory = resolve(process.env.CRAFTSURVIVE_EVIDENCE_DIR ?? 'live-evidence/task-7088');
const screenshotPath = resolve(evidenceDirectory, 'runtime-only-default.png');
const controlsScreenshotPath = resolve(evidenceDirectory, 'runtime-only-controls.png');
const riggedScreenshotPath = resolve(evidenceDirectory, 'runtime-only-rigged-gold.png');
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
  await page.waitForFunction(() =>
    document.querySelector('[data-lab-ghost]')?.textContent?.includes('pose MATCHED'),
  undefined, { timeout: 60_000 });

  const canvas = page.locator('canvas[data-rusty-application-renderer="engine-owned"]');
  if (await canvas.count() !== 1) throw new Error('Engine-owned lab canvas was not mounted exactly once');
  const captureCanvas = async (path) => {
    const clip = await canvas.boundingBox();
    if (clip === null) throw new Error('Engine-owned lab canvas has no visible screenshot bounds');
    const session = await page.context().newCDPSession(page);
    try {
      const capture = await session.send('Page.captureScreenshot', {
        format: 'png',
        fromSurface: true,
        clip: { ...clip, scale: 1 },
      });
      writeFileSync(path, Buffer.from(capture.data, 'base64'));
    } finally {
      await session.detach();
    }
  };
  const initialSelection = await page.locator('[data-garden-sector]').textContent();
  const initialLoad = await page.locator('[data-garden-load]').textContent();
  if (!initialSelection?.includes('spatial-wizard · inspect GOLD ghost · RED sprite-splat · GOLD 0.15 plate-locked')) {
    throw new Error(`unexpected initial lab selection: ${initialSelection}`);
  }
  if (!initialSelection.includes('128px') || !initialLoad?.includes('draws') || !initialLoad.includes('samples')) {
    throw new Error(`missing runtime capture resolution/cost readout: ${initialSelection} / ${initialLoad}`);
  }
  mkdirSync(evidenceDirectory, { recursive: true });

  await page.keyboard.press('KeyV');
  const panel = page.locator('[data-garden-panel]');
  await panel.waitFor({ state: 'visible' });
  await clickControl(panel, '[data-lab-freeze-view]');
  await page.waitForFunction(() =>
    document.querySelector('[data-lab-ghost]')?.textContent?.includes('source view exact'),
  undefined, { timeout: 60_000 });
  const initialComparison = await panel.locator('[data-lab-comparison]').textContent();
  const initialMetrics = await panel.locator('[data-lab-metrics]').textContent();
  const initialGhost = await panel.locator('[data-lab-ghost]').textContent();
  if (!initialComparison?.includes('capture MATCHED') || !initialComparison.includes('all lighting MATCHED')) {
    throw new Error(`default pair is not controlled: ${initialComparison}`);
  }
  if (!initialMetrics?.includes('3 GLBs') || initialMetrics.includes('files')) {
    throw new Error(`active lab admitted more than the three runtime GLBs: ${initialMetrics}`);
  }
  if (!initialGhost?.includes('pose MATCHED') || !initialGhost.includes('fallback none')
    || initialGhost.includes('source view unavailable')
    || !initialGhost.includes('whole-mesh') || !initialGhost.includes('rgba8-shell-depth')) {
    throw new Error(`ghost-plate readout is incomplete: ${initialGhost}`);
  }
  await page.keyboard.press('Escape');
  await panel.waitFor({ state: 'hidden' });
  await captureCanvas(screenshotPath);
  await page.keyboard.press('KeyV');
  await panel.waitFor({ state: 'visible' });
  await selectControl(panel, '[data-lab-ghost-retention]', '0.30');
  await selectControl(panel, '[data-lab-ghost-anchor-policy]', 'bounds-normalized');
  await panel.locator('[data-lab-ghost-anchor]').evaluate((element) => {
    element.value = '0.2';
    element.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await selectControl(panel, '[data-lab-ghost-mapping]', 'projective-surface');
  await selectControl(panel, '[data-lab-ghost-shell]', 'strict-source');
  await page.waitForFunction(() =>
    document.querySelector('[data-lab-ghost]')?.textContent?.includes('GOLD strict-source'));
  await selectControl(panel, '[data-lab-ghost-shell]', 'repaired-source');
  await panel.locator('[data-lab-ghost-shell-epsilon]').evaluate((element) => {
    element.value = '0.2';
    element.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await page.waitForFunction(() => {
    const selection = document.querySelector('[data-garden-sector]')?.textContent ?? '';
    const anchor = document.querySelector('[data-lab-ghost-anchor-value]')?.textContent ?? '';
    const shell = document.querySelector('[data-lab-ghost]')?.textContent ?? '';
    const epsilon = document.querySelector('[data-lab-ghost-shell-epsilon-value]')?.textContent ?? '';
    return selection.includes('GOLD 0.30 projective-surface repaired-source')
      && anchor.startsWith('0.20 →') && shell.includes('ratios reject unavailable')
      && epsilon.startsWith('0.20 +');
  });
  const captureOptions = await panel.locator('[data-lab-resolution] option').allTextContents();
  if (!captureOptions.includes('4096')) throw new Error(`4K capture option is unavailable: ${captureOptions.join(', ')}`);
  await selectControl(panel, '[data-lab-resolution]', '4096');
  const fourKCost = await panel.locator('[data-lab-capture-cost]').textContent();
  if (!fourKCost?.includes('256.0 MiB') || !fourKCost.includes('64.0 MiB')) {
    throw new Error(`4K capture memory warning is incomplete: ${fourKCost}`);
  }
  await panel.locator('[data-lab-sector]').evaluate((element) => {
    element.value = '1';
    element.dispatchEvent(new Event('change', { bubbles: true }));
  });
  await page.locator('[data-presentation-diagnostic]')
    .filter({ hasText: 'queued at 4096px' })
    .waitFor();
  const queuedFourKSelection = await page.locator('[data-garden-sector]').textContent();
  if (!queuedFourKSelection?.includes('capture 128px → 4096px queued')) {
    throw new Error(`high-cost sector change recaptured implicitly: ${queuedFourKSelection}`);
  }
  await page.waitForFunction(() =>
    document.querySelector('[data-lab-comparison]')?.textContent?.includes('CAPTURE QUEUED'));
  await selectControl(panel, '[data-lab-resolution]', '512');

  await panel.locator('[data-lab-subject]').evaluate((element) => {
    element.value = 'rigged-wizard';
    element.dispatchEvent(new Event('change', { bubbles: true }));
  });
  await page.waitForFunction(() => {
    const selection = document.querySelector('[data-garden-sector]')?.textContent ?? '';
    const ghost = document.querySelector('[data-lab-ghost]')?.textContent ?? '';
    return selection.startsWith('rigged-wizard') && ghost.includes('source view exact')
      && ghost.includes('1 draws / 1 meshes');
  }, undefined, { timeout: 60_000 });
  await page.keyboard.press('Escape');
  await panel.waitFor({ state: 'hidden' });
  await captureCanvas(riggedScreenshotPath);
  await page.keyboard.press('KeyV');
  await panel.waitFor({ state: 'visible' });
  for (const mode of ['sprite', 'depth-parallax', 'sprite-splat', 'full-splat']) {
    await selectControl(panel, '[data-lab-mode]', mode);
    await page.waitForFunction((selectedMode) =>
      document.querySelector('[data-garden-sector]')?.textContent?.includes(`RED ${selectedMode}`), mode);
  }
  await selectControl(panel, '[data-lab-mode]', 'sprite-splat');
  await selectControl(panel, '[data-lab-splat-resolution]', '96');
  await selectControl(panel, '[data-lab-splat-blend]', 'alpha-blend');
  await panel.locator('[data-lab-splat-opacity]').evaluate((element) => {
    element.value = '0.45';
    element.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await page.waitForFunction(() =>
    document.querySelector('[data-lab-comparison]')?.textContent?.includes('SPLAT GRID QUEUED'));
  await clickControl(panel, '[data-lab-recapture-selected]');
  await page.waitForFunction(() => {
    const text = document.querySelector('[data-lab-metrics]')?.textContent ?? '';
    return text.includes('capture texture 512²')
      && text.includes('RED splats 96² / alpha-blend / 0.45 opacity');
  }, undefined, { timeout: 60_000 });
  await panel.locator('[data-lab-steps]').evaluate((element) => {
    element.value = '12';
    element.dispatchEvent(new Event('input', { bubbles: true }));
  });
  const independentGeometryControls = await panel.locator('[data-lab-metrics]').textContent();
  if (!independentGeometryControls?.includes('RED splats 96²')
    || !independentGeometryControls.includes('depth 12 levels')) {
    throw new Error(`splat density and depth quantization are not independent: ${independentGeometryControls}`);
  }
  await selectControl(panel, '[data-lab-resolution]', '128');
  await clickControl(panel, '[data-lab-recapture-pair]');
  await page.waitForFunction(() =>
    document.querySelector('[data-lab-metrics]')?.textContent?.includes('capture texture 128²'));

  const beforeLightingPixels = await canvasChecksum(canvas);
  await panel.locator('[data-lab-output-gain]').evaluate((element) => {
    element.value = '1.6';
    element.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await page.waitForFunction(() => {
    const text = document.querySelector('[data-lab-comparison]')?.textContent ?? '';
    return text.includes('capture MATCHED') && text.includes('all lighting DIFFERENT');
  });
  const redGain = await panel.locator('[data-lab-output-gain-value]').textContent();
  await page.waitForTimeout(100);
  const afterRedLightingPixels = await canvasChecksum(canvas);
  if (beforeLightingPixels === afterRedLightingPixels) {
    throw new Error('RED post-capture output gain did not visibly affect the selected side');
  }

  await selectControl(panel, '[data-lab-side]', 'baseline');
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
  await selectControl(panel, '[data-lab-side]', 'enhanced');
  const redCaptureKey = await panel.locator('[data-lab-capture-key-value]').textContent();
  if (blueCaptureKey !== '3.2' || redCaptureKey !== '3.0') {
    throw new Error(`capture lighting leaked across sides: blue=${blueCaptureKey} red=${redCaptureKey}`);
  }
  await selectControl(panel, '[data-lab-side]', 'baseline');
  await clickControl(panel, '[data-lab-recapture-selected]');
  await page.waitForFunction(() => !(document.querySelector('[data-garden-load]')?.textContent ?? '').includes('capture n/a'));

  await clickControl(panel, '[data-lab-match]');
  await page.waitForFunction(() => {
    const text = document.querySelector('[data-lab-comparison]')?.textContent ?? '';
    return text.includes('capture MATCHED') && text.includes('all lighting MATCHED');
  });
  await clickControl(panel, '[data-lab-recapture-pair]');
  await clickControl(panel, '[data-lab-fallback]');
  await page.locator('[data-presentation-diagnostic]').filter({ hasText: 'fallback probe passed' }).waitFor();

  await page.screenshot({ path: controlsScreenshotPath, fullPage: true });
  await clickControl(panel, '[data-lab-reset-ghost]');
  await page.waitForFunction(() => {
    const selection = document.querySelector('[data-garden-sector]')?.textContent ?? '';
    const retention = document.querySelector('[data-lab-ghost-retention]')?.value;
    const anchorPolicy = document.querySelector('[data-lab-ghost-anchor-policy]')?.value;
    const anchor = document.querySelector('[data-lab-ghost-anchor]')?.value;
    const mapping = document.querySelector('[data-lab-ghost-mapping]')?.value;
    const shell = document.querySelector('[data-lab-ghost-shell]')?.value;
    const epsilon = document.querySelector('[data-lab-ghost-shell-epsilon]')?.value;
    const ghost = document.querySelector('[data-lab-ghost]')?.textContent ?? '';
    return selection.includes('GOLD 0.15 plate-locked')
      && retention === '0.15' && anchorPolicy === 'bounds-center' && anchor === '0.5'
      && mapping === 'plate-locked' && shell === 'whole-mesh' && epsilon === '0.12'
      && ghost.includes('source view exact');
  }, undefined, { timeout: 60_000 });

  await page.keyboard.press('KeyV');
  await panel.waitFor({ state: 'hidden' });
  await page.keyboard.press('KeyV');
  await panel.waitFor({ state: 'visible' });
  await page.keyboard.press('Escape');
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
    initialGhost,
    ghostControls: { depthRetention: 0.3, anchorPolicy: 'bounds-normalized', anchorValue: 0.2, mapping: 'projective-surface', shellMode: 'repaired-source', shellDepthEpsilon: 0.2 },
    shellModeRoundTrip: true,
    independentPostLighting: { redGain, blueGain },
    independentCaptureLighting: { blueCaptureKey, redCaptureKey },
    independentGeometryControls,
    fourKCost,
    queuedFourKSelection,
    matchedRecapture: true,
    screenshotPath,
    controlsScreenshotPath,
    riggedScreenshotPath,
    ghostResetRoundTrip: true,
    firstPersonMovementAfterPanel: true,
  }));
} finally {
  await browser.close();
}

async function canvasChecksum(canvas) {
  return canvas.evaluate((element) => {
    const context = element.getContext('webgl2') ?? element.getContext('webgl');
    if (context === null) throw new Error('Engine canvas has no WebGL context');
    const pixels = new Uint8Array(context.drawingBufferWidth * context.drawingBufferHeight * 4);
    context.readPixels(
      0,
      0,
      context.drawingBufferWidth,
      context.drawingBufferHeight,
      context.RGBA,
      context.UNSIGNED_BYTE,
      pixels,
    );
    let checksum = 0;
    for (let index = 0; index < pixels.length; index += 1) {
      checksum = (checksum + pixels[index] * ((index % 251) + 1)) % 2_147_483_647;
    }
    return checksum;
  });
}

async function clickControl(panel, selector) {
  await panel.locator(selector).evaluate((element) => element.click());
}

async function selectControl(panel, selector, value) {
  await panel.locator(selector).evaluate((element, selectedValue) => {
    element.value = selectedValue;
    element.dispatchEvent(new Event('change', { bubbles: true }));
  }, value);
}
