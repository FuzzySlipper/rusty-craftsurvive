import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { chromium } from 'playwright-core';

const baseUrl = process.env.CRAFTSURVIVE_URL ?? process.env.BASE_URL ?? 'http://127.0.0.1:4419';
const evidenceDirectory = resolve(process.env.CRAFTSURVIVE_EVIDENCE_DIR ?? 'live-evidence/task-7030');
const initialScreenshot = resolve(evidenceDirectory, 'held-animation-initial.png');
const sampledScreenshot = resolve(evidenceDirectory, 'held-animation-sample-7.png');
const headed = process.env.CRAFTSURVIVE_HEADED === '1';
const url = new URL(baseUrl);
url.searchParams.set('course', 'animation-garden');
const browser = await chromium.launch({
  executablePath: process.env.CHROMIUM_BIN ?? '/usr/bin/chromium',
  headless: !headed,
  args: ['--no-sandbox', '--use-gl=angle', '--use-angle=swiftshader'],
});

try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  await page.goto(url.href);
  await page.locator('[data-status]').filter({ hasText: 'connected' }).waitFor({ timeout: 30_000 });
  await page.waitForFunction(() => {
    const summary = document.querySelector('[data-garden-load]')?.textContent ?? '';
    return summary.startsWith('ready') && summary.includes('flat 24 frames') && summary.includes('depth 24 frames');
  }, undefined, { timeout: 90_000 });

  const canvas = page.locator('canvas[data-rusty-application-renderer="engine-owned"]');
  if (await canvas.count() !== 1) throw new Error('animation garden did not mount exactly one Engine-owned canvas');
  mkdirSync(evidenceDirectory, { recursive: true });
  await captureCanvas(page, canvas, initialScreenshot);
  const initialPixels = imageHash(initialScreenshot);

  await page.keyboard.press('KeyV');
  const panel = page.locator('[data-garden-panel]');
  await panel.waitFor({ state: 'visible' });
  if (!await panel.evaluate((element) => element.scrollWidth <= element.clientWidth)) {
    throw new Error('animation-garden controls overflow horizontally instead of staying inside the viewport');
  }
  if (await panel.locator('[data-held-clip] option').count() !== 6) throw new Error('representative clip chooser does not have six entries');
  const policy = await panel.locator('[data-held-policy]').textContent();
  if (!policy?.includes('In-place') || !policy.includes('Limitation:')) throw new Error(`root-motion policy or limitation absent: ${policy}`);
  const sourceWindow = await panel.locator('[data-held-window]').textContent();
  if (!sourceWindow?.includes('bounded window t=0.000') || !sourceWindow.includes('source 2.500 s')) throw new Error(`source duration/window is not explicit: ${sourceWindow}`);

  await selectControl(panel, '[data-held-cadence]', '8');
  // The initial selector is Idle_Loop (2.5 s): 8 Hz yields 20 frames, while the
  // following 24 Hz selection hits the public 24-frame ceiling. Assert both so
  // the capped window is an intentional proof sequence rather than an accident.
  await page.waitForFunction(() => (document.querySelector('[data-garden-load]')?.textContent ?? '').includes('flat 20 frames'));
  await selectControl(panel, '[data-held-cadence]', '24');
  await page.waitForFunction(() => (document.querySelector('[data-garden-load]')?.textContent ?? '').includes('flat 24 frames'));
  const scrub = panel.locator('[data-held-scrub]');
  await scrub.evaluate((element) => {
    element.value = '7';
    element.dispatchEvent(new Event('input', { bubbles: true }));
  });
  await page.waitForFunction(() => (document.querySelector('[data-held-scrub-value]')?.textContent ?? '').startsWith('8/24'));
  const banks = await panel.locator('[data-held-banks]').textContent();
  if (!banks?.includes('flat ready: 24/24') || !banks.includes('depth ready: 24/24') || !banks.includes('resident frame-bank')) {
    throw new Error(`held frame banks did not report ready resident selection: ${banks}`);
  }
  await captureCanvas(page, canvas, sampledScreenshot);
  const sampledPixels = imageHash(sampledScreenshot);
  if (initialPixels === sampledPixels) throw new Error('held sample 7 did not visibly change the isolated animation garden canvas');

  await panel.locator('[data-held-pause]').click();
  await page.locator('[data-held-pause]').filter({ hasText: 'Pause held samples' }).waitFor();
  await page.waitForTimeout(160);
  await panel.locator('[data-held-pause]').click();
  await page.locator('[data-held-pause]').filter({ hasText: 'Play held samples' }).waitFor();
  if (errors.length > 0) throw new Error(`page errors: ${errors.join(' | ')}`);
  console.log(`held-animation garden smoke passed: ${initialScreenshot}, ${sampledScreenshot}`);
} finally {
  await browser.close();
}

async function selectControl(panel, selector, value) {
  await panel.locator(selector).evaluate((element, next) => {
    element.value = next;
    element.dispatchEvent(new Event('change', { bubbles: true }));
  }, value);
}

async function captureCanvas(page, canvas, path) {
  const clip = await canvas.boundingBox();
  if (clip === null) throw new Error('Engine-owned animation canvas has no screenshot bounds');
  const session = await page.context().newCDPSession(page);
  try {
    const capture = await session.send('Page.captureScreenshot', { format: 'png', fromSurface: true, clip: { ...clip, scale: 1 } });
    writeFileSync(path, Buffer.from(capture.data, 'base64'));
  } finally {
    await session.detach();
  }
}

function imageHash(path) { return createHash('sha256').update(readFileSync(path)).digest('hex'); }
