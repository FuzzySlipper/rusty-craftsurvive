import { chromium } from 'playwright-core';

const url = process.env.CRAFTSURVIVE_URL ?? 'http://127.0.0.1:4419';
const executablePath = process.env.CHROMIUM_BIN ?? '/usr/bin/chromium';
const browser = await chromium.launch({
  executablePath,
  headless: true,
  args: ['--no-sandbox', '--use-gl=angle', '--use-angle=swiftshader'],
});
try {
  const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
  const pageErrors = [];
  page.on('pageerror', (error) => pageErrors.push(String(error)));
  await page.goto(url);
  const readout = page.locator('[data-world-revision]');
  await page.locator('[data-session-status="connected"]').waitFor({ timeout: 15_000 });
  const initialWorld = Number(await readout.getAttribute('data-world-revision'));
  const initialPlayer = Number(await readout.getAttribute('data-player-revision'));
  if (await page.locator('canvas[data-rusty-application-renderer="engine-owned"]').count() !== 1) {
    throw new Error('Engine-owned renderer canvas was not mounted exactly once');
  }

  await page.mouse.click(700, 350);
  await page.waitForFunction(() => document.pointerLockElement !== null);
  await page.keyboard.down('KeyW');
  await page.waitForTimeout(180);
  await page.keyboard.up('KeyW');
  await page.waitForFunction(
    (revision) => Number(document.querySelector('[data-player-revision]')?.getAttribute('data-player-revision')) > revision,
    initialPlayer,
  );

  await page.mouse.click(500, 350, { button: 'left' });
  await page.waitForFunction(
    (revision) => Number(document.querySelector('[data-world-revision]')?.getAttribute('data-world-revision')) > revision,
    initialWorld,
  );
  const destroyedWorld = Number(await readout.getAttribute('data-world-revision'));
  await page.mouse.click(500, 350, { button: 'right' });
  await page.waitForFunction(
    (revision) => Number(document.querySelector('[data-world-revision]')?.getAttribute('data-world-revision')) > revision,
    destroyedWorld,
  );

  if (pageErrors.length > 0) throw new Error(`browser page errors: ${pageErrors.join('; ')}`);
  console.log(JSON.stringify({
    proof: 'CRAFTSURVIVE_BROWSER_PLAYABLE',
    pointerLocked: await page.evaluate(() => document.pointerLockElement !== null),
    playerRevision: Number(await readout.getAttribute('data-player-revision')),
    destroyedWorldRevision: destroyedWorld,
    placedWorldRevision: Number(await readout.getAttribute('data-world-revision')),
  }));
} finally {
  await browser.close();
}
