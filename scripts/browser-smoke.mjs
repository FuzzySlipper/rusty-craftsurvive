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
  await page.waitForFunction(
    () => document.querySelector('[data-player-grounded]')?.getAttribute('data-player-grounded') === 'true',
  );
  const initialWorld = Number(await readout.getAttribute('data-world-revision'));
  const initialPlayer = Number(await readout.getAttribute('data-player-revision'));
  const initialYaw = Number(await readout.getAttribute('data-player-yaw'));
  if (await page.locator('canvas[data-rusty-application-renderer="engine-owned"]').count() !== 1) {
    throw new Error('Engine-owned renderer canvas was not mounted exactly once');
  }

  await page.mouse.click(700, 350);
  await page.waitForFunction(() => document.pointerLockElement !== null);
  await page.evaluate(() => window.dispatchEvent(new MouseEvent('mousemove', {
    movementX: 300,
    movementY: 0,
  })));
  await page.waitForFunction(
    (yaw) => Number(document.querySelector('[data-player-yaw]')?.getAttribute('data-player-yaw')) > yaw + 30,
    initialYaw,
  );
  const yaw = Number(await readout.getAttribute('data-player-yaw'));
  const beforeMove = String(await readout.getAttribute('data-player-position')).split(',').map(Number);
  await page.keyboard.down('KeyW');
  await page.waitForTimeout(180);
  await page.keyboard.up('KeyW');
  await page.waitForFunction(
    (revision) => Number(document.querySelector('[data-player-revision]')?.getAttribute('data-player-revision')) > revision,
    initialPlayer,
  );
  const afterMove = String(await readout.getAttribute('data-player-position')).split(',').map(Number);
  const yawRadians = yaw * Math.PI / 180;
  // Public Engine pose convention: yaw zero faces -Z and positive yaw turns toward +X.
  const forward = [Math.sin(yawRadians), -Math.cos(yawRadians)];
  const displacement = [afterMove[0] - beforeMove[0], afterMove[2] - beforeMove[2]];
  if (displacement[0] * forward[0] + displacement[1] * forward[1] <= 0) {
    throw new Error(`W movement was not view-relative: yaw=${yaw} displacement=${displacement.join(',')}`);
  }
  await page.waitForFunction(
    () => document.querySelector('[data-player-grounded]')?.getAttribute('data-player-grounded') === 'true',
  );
  const beforeJump = String(await readout.getAttribute('data-player-position')).split(',').map(Number);
  await page.keyboard.down('Space');
  await page.waitForFunction(
    () => document.querySelector('[data-player-grounded]')?.getAttribute('data-player-grounded') === 'false',
  );
  await page.keyboard.up('Space');
  await page.waitForFunction(
    (eyeY) => Number(String(document.querySelector('[data-player-position]')?.getAttribute('data-player-position')).split(',')[1]) > eyeY + 0.1,
    beforeJump[1],
  );
  const jumpPeakSample = String(await readout.getAttribute('data-player-position')).split(',').map(Number)[1];
  await page.waitForFunction(
    () => document.querySelector('[data-player-grounded]')?.getAttribute('data-player-grounded') === 'true',
  );
  if ((await readout.getAttribute('data-targeted-voxel')) === '') {
    throw new Error('crosshair has no authoritative voxel target before edit proof');
  }

  const centerClip = { x: 450, y: 300, width: 100, height: 100 };
  const beforeDestroyPixels = await page.screenshot({ clip: centerClip });
  await page.mouse.click(500, 350, { button: 'left' });
  await page.waitForFunction(
    (revision) => Number(document.querySelector('[data-world-revision]')?.getAttribute('data-world-revision')) > revision,
    initialWorld,
  );
  await page.waitForTimeout(100);
  const destroyedWorld = Number(await readout.getAttribute('data-world-revision'));
  const afterDestroyPixels = await page.screenshot({ clip: centerClip });
  if (beforeDestroyPixels.equals(afterDestroyPixels)) {
    throw new Error('accepted destroy did not visibly change the crosshair region');
  }
  await page.mouse.click(500, 350, { button: 'right' });
  await page.waitForFunction(
    (revision) => Number(document.querySelector('[data-world-revision]')?.getAttribute('data-world-revision')) > revision,
    destroyedWorld,
  );
  await page.waitForTimeout(100);
  const afterPlacePixels = await page.screenshot({ clip: centerClip });
  if (afterDestroyPixels.equals(afterPlacePixels)) {
    throw new Error('accepted place did not visibly change the crosshair region');
  }

  if (pageErrors.length > 0) throw new Error(`browser page errors: ${pageErrors.join('; ')}`);
  console.log(JSON.stringify({
    proof: 'CRAFTSURVIVE_BROWSER_PLAYABLE',
    pointerLocked: await page.evaluate(() => document.pointerLockElement !== null),
    rightLookYawDelta: yaw - initialYaw,
    viewRelativeDisplacement: displacement,
    jumpRise: jumpPeakSample - beforeJump[1],
    playerRevision: Number(await readout.getAttribute('data-player-revision')),
    destroyedWorldRevision: destroyedWorld,
    placedWorldRevision: Number(await readout.getAttribute('data-world-revision')),
  }));
} finally {
  await browser.close();
}
