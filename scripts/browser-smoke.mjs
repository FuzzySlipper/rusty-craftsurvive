import { chromium } from 'playwright-core';

const url = process.env.CRAFTSURVIVE_URL ?? process.env.BASE_URL ?? 'http://127.0.0.1:4419';
const executablePath = process.env.CHROMIUM_BIN ?? '/usr/bin/chromium';
const expectedSurface = process.env.CRAFTSURVIVE_SURFACE;
const expectedSurfaceReadout = { box: 'box', mc: 'marchingCubes', dc: 'dualContouring' }[expectedSurface];
const browser = await chromium.launch({
  executablePath,
  headless: true,
  args: ['--no-sandbox', '--use-gl=angle', '--use-angle=swiftshader'],
});

const vector = async (page, selector) => (await page.locator(selector).textContent())
  .split(',').map((value) => Number(value.trim()));
const number = async (page, selector) => Number(await page.locator(selector).textContent());
const view = async (page) => (await page.locator('[data-player-view]').textContent())
  .split('/').map((value) => Number.parseFloat(value));
const waitGrounded = (page, grounded = true) => page.waitForFunction(
  (expected) => document.querySelector('[data-motion]')?.textContent?.startsWith(expected ? 'grounded' : 'airborne'),
  grounded,
);
const moveMouse = (page, movementX, movementY) => page.evaluate(
  ({ x, y }) => window.dispatchEvent(new MouseEvent('mousemove', { movementX: x, movementY: y })),
  { x: movementX, y: movementY },
);

try {
  const healthResponse = await fetch(new URL('/health', url));
  const health = await healthResponse.json();
  const healthIdentity = healthResponse.headers.get('x-den-project');
  if (!healthResponse.ok || health.project !== 'rusty-craftsurvive'
      || healthIdentity !== 'rusty-craftsurvive') {
    throw new Error(`service identity mismatch: ${healthResponse.status} ${healthIdentity} ${JSON.stringify(health)}`);
  }
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const pageErrors = [];
  page.on('pageerror', (error) => pageErrors.push(String(error)));
  await page.goto(url);
  await page.locator('[data-status]').filter({ hasText: 'connected' }).waitFor({ timeout: 15_000 });
  await page.locator('[data-player-position]').filter({ hasNotText: '—' }).waitFor();
  const initialPosition = await vector(page, '[data-player-position]');
  const initialPlayerRevision = await number(page, '[data-player-revision]');
  await waitGrounded(page);
  const landedPosition = await vector(page, '[data-player-position]');
  const landedRevision = await number(page, '[data-player-revision]');
  if (landedPosition[1] >= initialPosition[1] - 0.5 || landedRevision <= initialPlayerRevision) {
    throw new Error(`gravity/landing lacked newer Rust evidence: ${initialPosition} -> ${landedPosition}`);
  }
  if (expectedSurfaceReadout && await page.locator('[data-surface]').textContent() !== expectedSurfaceReadout) {
    throw new Error(`surface mismatch: expected ${expectedSurfaceReadout}`);
  }
  if (await page.locator('canvas[data-rusty-application-renderer="engine-owned"]').count() !== 1) {
    throw new Error('Engine-owned renderer canvas was not mounted exactly once');
  }

  await page.mouse.click(640, 360);
  await page.waitForFunction(() => document.pointerLockElement !== null);
  const [initialYaw] = await view(page);
  await moveMouse(page, 300, 0);
  await page.waitForFunction(
    (yaw) => Number.parseFloat(document.querySelector('[data-player-view]')?.textContent ?? '') > yaw + 30,
    initialYaw,
  );
  const [rightYaw] = await view(page);
  await moveMouse(page, -300, 0);
  await page.waitForFunction(
    (yaw) => Math.abs(Number.parseFloat(document.querySelector('[data-player-view]')?.textContent ?? '') - yaw) < 2,
    initialYaw,
  );

  const beforeJump = await vector(page, '[data-player-position]');
  const jumpRevision = await number(page, '[data-player-revision]');
  await page.keyboard.down('KeyW');
  await page.keyboard.down('Space');
  await waitGrounded(page, false);
  await page.waitForFunction(
    (eyeY) => Number(document.querySelector('[data-player-position]')?.textContent?.split(',')[1]) > eyeY + 0.2,
    beforeJump[1],
  );
  const jumpSample = await vector(page, '[data-player-position]');
  await page.waitForTimeout(620);
  await page.keyboard.up('Space');
  await page.keyboard.up('KeyW');
  await waitGrounded(page);
  const afterCourse = await vector(page, '[data-player-position]');
  const afterCourseRevision = await number(page, '[data-player-revision]');
  if (afterCourse[2] >= 4.8 || afterCourseRevision <= jumpRevision) {
    throw new Error(`jump did not clear the one-voxel trench: ${beforeJump} -> ${afterCourse}`);
  }

  const beforeWallSequence = await number(page, '[data-accepted-sequence]');
  const wallPosition = await vector(page, '[data-player-position]');
  await page.keyboard.down('KeyW');
  await page.waitForTimeout(300);
  const wallPositionLater = await vector(page, '[data-player-position]');
  await page.keyboard.up('KeyW');
  await page.waitForFunction(
    (sequence) => Number(document.querySelector('[data-accepted-sequence]')?.textContent) > sequence,
    beforeWallSequence,
  );
  if (Math.abs(wallPositionLater[2] - wallPosition[2]) > 0.06) {
    throw new Error(`solid wall did not hold the player: ${wallPosition} -> ${wallPositionLater}`);
  }

  await moveMouse(page, 0, 300);
  await page.waitForFunction(
    () => Number.parseFloat(document.querySelector('[data-player-view]')?.textContent?.split('/')[1] ?? '') < -50,
  );
  await moveMouse(page, 0, 300);
  await page.waitForFunction(
    () => Number.parseFloat(document.querySelector('[data-player-view]')?.textContent?.split('/')[1] ?? '') < -70,
  );
  const initialWorld = await number(page, '[data-world-revision]');
  const canvas = page.locator('canvas[data-rusty-application-renderer="engine-owned"]');
  const beforeDestroyPixels = await canvas.screenshot();
  await page.mouse.click(640, 360, { button: 'left' });
  await page.waitForFunction(
    (revision) => Number(document.querySelector('[data-world-revision]')?.textContent) > revision,
    initialWorld,
  );
  await page.locator('[data-edit]').filter({ hasText: 'destroy' }).waitFor();
  const destroyedWorld = await number(page, '[data-world-revision]');
  await waitGrounded(page, false);
  await waitGrounded(page);
  const afterSupportFall = await vector(page, '[data-player-position]');
  const afterDestroyPixels = await canvas.screenshot();
  if (beforeDestroyPixels.equals(afterDestroyPixels)) {
    throw new Error('accepted support destroy did not visibly change the Engine renderer');
  }
  if (afterSupportFall[1] >= wallPosition[1] - 0.5) {
    throw new Error(`support removal did not cause a lower landing: ${wallPosition} -> ${afterSupportFall}`);
  }

  const rejectionSequence = await number(page, '[data-accepted-sequence]');
  await page.mouse.click(640, 360, { button: 'right' });
  await page.waitForFunction(
    (sequence) => Number(document.querySelector('[data-accepted-sequence]')?.textContent) > sequence,
    rejectionSequence,
  );
  await page.locator('[data-edit]').filter({ hasText: 'place rejected · playerOverlap' }).waitFor();
  if (await number(page, '[data-world-revision]') !== destroyedWorld) {
    throw new Error('rejected player-overlap placement changed the Rust world revision');
  }

  await moveMouse(page, 0, -300);
  await page.waitForFunction(
    () => Number.parseFloat(document.querySelector('[data-player-view]')?.textContent?.split('/')[1] ?? '') > -60,
  );
  await moveMouse(page, 0, -300);
  await page.waitForFunction(
    () => Number.parseFloat(document.querySelector('[data-player-view]')?.textContent?.split('/')[1] ?? '') > -20,
  );
  await page.keyboard.down('KeyS');
  await page.waitForTimeout(240);
  await page.keyboard.up('KeyS');
  await waitGrounded(page);
  const beforePlacePixels = await canvas.screenshot();
  await page.mouse.click(640, 360, { button: 'right' });
  await page.waitForFunction(
    (revision) => Number(document.querySelector('[data-world-revision]')?.textContent) > revision,
    destroyedWorld,
  );
  await page.locator('[data-edit]').filter({ hasText: 'place' }).waitFor();
  await page.waitForTimeout(100);
  const placedWorld = await number(page, '[data-world-revision]');
  const afterPlacePixels = await canvas.screenshot();
  if (beforePlacePixels.equals(afterPlacePixels)) {
    throw new Error('accepted blocking placement did not visibly change the Engine renderer');
  }

  const beforePlacedWall = await vector(page, '[data-player-position]');
  await page.keyboard.down('KeyW');
  await page.waitForTimeout(500);
  const againstPlacedWall = await vector(page, '[data-player-position]');
  await page.waitForTimeout(250);
  const heldAgainstPlacedWall = await vector(page, '[data-player-position]');
  await page.keyboard.up('KeyW');
  if (againstPlacedWall[2] >= beforePlacedWall[2] - 0.2
      || Math.abs(heldAgainstPlacedWall[2] - againstPlacedWall[2]) > 0.06) {
    throw new Error(`accepted placement did not become an immediate blocker: ${beforePlacedWall} -> ${againstPlacedWall} -> ${heldAgainstPlacedWall}`);
  }

  if (pageErrors.length > 0) throw new Error(`browser page errors: ${pageErrors.join('; ')}`);
  console.log(JSON.stringify({
    proof: 'CRAFTSURVIVE_GROUNDED_EDIT_ROUTE',
    healthIdentity,
    surface: await page.locator('[data-surface]').textContent(),
    pointerLocked: await page.evaluate(() => document.pointerLockElement !== null),
    rightLookYawDelta: rightYaw - initialYaw,
    landingDrop: initialPosition[1] - landedPosition[1],
    jumpRise: jumpSample[1] - beforeJump[1],
    trenchAndWallPosition: afterCourse,
    supportLandingDrop: wallPosition[1] - afterSupportFall[1],
    destroyedWorldRevision: destroyedWorld,
    rejectedPlacementSequence: rejectionSequence + 1,
    placedWorldRevision: placedWorld,
    placedWallPosition: heldAgainstPlacedWall,
  }));
} finally {
  await browser.close();
}
