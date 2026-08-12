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
const pointerCenter = { x: 640, y: 360 };
let mousePosition = { ...pointerCenter };
const moveMouse = async (page, movementX, movementY) => {
  mousePosition = { x: mousePosition.x + movementX, y: mousePosition.y + movementY };
  await page.mouse.move(mousePosition.x, mousePosition.y);
};
const clickCenter = async (page, options = {}) => {
  mousePosition = { ...pointerCenter };
  await page.mouse.click(pointerCenter.x, pointerCenter.y, options);
};
const clickPointer = async (page, button) => {
  await page.mouse.down({ button });
  await page.mouse.up({ button });
};

const tenCommandMovement = async (browser, codes) => {
  const sample = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  try {
    await sample.goto(url);
    await sample.locator('[data-status]').filter({ hasText: 'connected' }).waitFor({ timeout: 15_000 });
    await waitGrounded(sample);
    const start = await vector(sample, '[data-player-position]');
    const sequence = await number(sample, '[data-accepted-sequence]');
    for (const code of codes) await sample.keyboard.down(code);
    await sample.waitForFunction(
      ({ sequence, count }) => Number(document.querySelector('[data-accepted-sequence]')?.textContent) >= sequence + count,
      { sequence, count: 10 },
    );
    for (const code of codes) await sample.keyboard.up(code);
    const end = await vector(sample, '[data-player-position]');
    const accepted = await number(sample, '[data-accepted-sequence]') - sequence;
    return { distance: Math.hypot(end[0] - start[0], end[2] - start[2]), accepted };
  } finally {
    await sample.close();
  }
};

try {
  const healthResponse = await fetch(new URL('/health', url));
  const health = await healthResponse.json();
  const healthIdentity = healthResponse.headers.get('x-den-project');
  if (!healthResponse.ok || health.project !== 'rusty-craftsurvive'
      || healthIdentity !== 'rusty-craftsurvive') {
    throw new Error(`service identity mismatch: ${healthResponse.status} ${healthIdentity} ${JSON.stringify(health)}`);
  }
  const cardinalTenCommand = await tenCommandMovement(browser, ['KeyD']);
  const diagonalTenCommand = await tenCommandMovement(browser, ['KeyW', 'KeyD']);
  const cardinalPerCommand = cardinalTenCommand.distance / cardinalTenCommand.accepted;
  const diagonalPerCommand = diagonalTenCommand.distance / diagonalTenCommand.accepted;
  if (cardinalTenCommand.distance <= 0.2
      || diagonalPerCommand > cardinalPerCommand * 1.05
      || diagonalPerCommand < cardinalPerCommand * 0.7) {
    throw new Error(`ten-command diagonal normalization failed: cardinal=${JSON.stringify(cardinalTenCommand)} diagonal=${JSON.stringify(diagonalTenCommand)}`);
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
  if (landedPosition[1] >= initialPosition[1] || landedRevision <= initialPlayerRevision) {
    throw new Error(`gravity/landing lacked newer Rust evidence: ${initialPosition} -> ${landedPosition}`);
  }
  if (expectedSurfaceReadout && await page.locator('[data-surface]').textContent() !== expectedSurfaceReadout) {
    throw new Error(`surface mismatch: expected ${expectedSurfaceReadout}`);
  }
  if (await page.locator('canvas[data-rusty-application-renderer="engine-owned"]').count() !== 1) {
    throw new Error('Engine-owned renderer canvas was not mounted exactly once');
  }

  await clickCenter(page);
  await page.waitForFunction(() => document.pointerLockElement !== null);
  await page.locator('[data-collision-world]').filter({ hasNotText: '—' }).waitFor();
  await page.keyboard.down('ControlLeft');
  await page.locator('[data-motion]').filter({ hasText: 'crouched' }).waitFor();
  await page.keyboard.up('ControlLeft');
  await page.locator('[data-motion]').filter({ hasText: 'standing' }).waitFor();
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
  await clickPointer(page, 'left');
  await page.waitForFunction(
    (revision) => Number(document.querySelector('[data-world-revision]')?.textContent) > revision,
    initialWorld,
  );
  await page.locator('[data-edit]').filter({ hasText: 'destroy' }).waitFor();
  const destroyedWorld = await number(page, '[data-world-revision]');
  await page.waitForFunction(
    (eyeY) => Number(document.querySelector('[data-player-position]')?.textContent?.split(',')[1]) < eyeY - 0.5,
    wallPosition[1],
  );
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
  await clickPointer(page, 'right');
  await page.waitForFunction(
    (sequence) => Number(document.querySelector('[data-accepted-sequence]')?.textContent) > sequence,
    rejectionSequence,
  );
  await page.locator('[data-edit]').filter({ hasText: 'playerOverlap' }).waitFor();
  if (await number(page, '[data-world-revision]') !== destroyedWorld) {
    throw new Error('rejected player-overlap placement changed the Rust world revision');
  }

  await page.keyboard.press('Digit2');
  await page.locator('[data-brush]').filter({ hasText: 'radius 1' }).waitFor();
  const beforeVolumeRevision = await number(page, '[data-world-revision]');
  await clickPointer(page, 'left');
  await page.waitForFunction(
    (revision) => Number(document.querySelector('[data-world-revision]')?.textContent) > revision,
    beforeVolumeRevision,
  );
  const volumeRevision = await number(page, '[data-world-revision]');
  const volumeEdit = await page.locator('[data-edit]').textContent();
  const affectedVoxels = Number(volumeEdit?.match(/· (\d+) voxels/)?.[1]);
  const volumeEditMs = Number(volumeEdit?.match(/· ([\d.]+) ms/)?.[1]);
  if (volumeRevision !== beforeVolumeRevision + 1 || affectedVoxels <= 1) {
    throw new Error(`volume brush was not one multi-voxel revision: ${beforeVolumeRevision} -> ${volumeRevision}; ${volumeEdit}`);
  }
  await page.keyboard.press('Digit3');
  await page.locator('[data-brush]').filter({ hasText: 'radius 2' }).waitFor();

  const beforeImpulse = await vector(page, '[data-player-position]');
  await page.keyboard.down('KeyH');
  await page.waitForTimeout(80);
  await page.keyboard.up('KeyH');
  await page.waitForFunction(
    (x) => Math.abs(Number(document.querySelector('[data-player-position]')?.textContent?.split(',')[0]) - x) > 0.08,
    beforeImpulse[0],
  );
  const afterImpulse = await vector(page, '[data-player-position]');

  if (pageErrors.length > 0) throw new Error(`browser page errors: ${pageErrors.join('; ')}`);
  console.log(JSON.stringify({
    proof: 'CRAFTSURVIVE_GROUNDED_EDIT_ROUTE',
    healthIdentity,
    surface: await page.locator('[data-surface]').textContent(),
    pointerLocked: await page.evaluate(() => document.pointerLockElement !== null),
    rightLookYawDelta: rightYaw - initialYaw,
    cardinalTenCommand,
    diagonalTenCommand,
    landingDrop: initialPosition[1] - landedPosition[1],
    jumpRise: jumpSample[1] - beforeJump[1],
    trenchAndWallPosition: afterCourse,
    supportLandingDrop: wallPosition[1] - afterSupportFall[1],
    destroyedWorldRevision: destroyedWorld,
    rejectedPlacementSequence: rejectionSequence + 1,
    volumeBrushRadius: 1,
    volumeBrushAffectedVoxels: affectedVoxels,
    volumeBrushEditMs: volumeEditMs,
    volumeBrushWorldRevision: volumeRevision,
    largestBrushSelectable: true,
    impulseDisplacement: Math.abs(afterImpulse[0] - beforeImpulse[0]),
    controllerDiagnostics: {
      collisionWorld: await page.locator('[data-collision-world]').textContent(),
      ground: await page.locator('[data-ground]').textContent(),
      contacts: await page.locator('[data-contacts]').textContent(),
      step: await page.locator('[data-step]').textContent(),
      platform: await page.locator('[data-platform]').textContent(),
    },
  }));
} finally {
  await browser.close();
}
