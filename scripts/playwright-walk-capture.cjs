// Walk/distance capture for rusty-craftsurvive, through a direct Playwright
// session against the live product.
//
// Why Playwright and not the crew-playtest service: the Engine accepts gameplay
// input only while its canvas is focused or pointer-locked, and in this browser
// neither page load nor a click on the canvas leaves it focused - the service's
// browser ops cannot evaluate `canvas.focus()`, so its sessions deliver no key
// events at all. This script can, which is the one capability the capture needs.
//
// Method, per run:
//   1. reset to the spawn with the product's own `craft.player.teleport`;
//   2. wait until the player reports itself grounded, still, and facing the same
//      way, so nothing from the previous run leaks into this one;
//   3. hold `w` for HOLD_MS and sample *inside* the hold, so the distance is
//      measured while the key is down and not during the fall that follows;
//   4. attribute the movement: the product's own planar intent must be forward
//      and non-zero, yaw must hold, and the player must be grounded at both ends.
//
// Attribution deliberately does not use the readout's `keys` field: it is a
// per-update count rather than a cumulative one, and it reads zero even while
// the player is walking with a forward intent.
//
// Positions and facts come from the product's `craft.player.readout`; the
// distance, speed and deltas are this script's arithmetic on those positions.

const { chromium } = require(process.env.PLAYWRIGHT_MODULE ?? 'playwright');

const ORIGIN = process.env.PRODUCT_ORIGIN ?? 'http://127.0.0.1:37305';
const HOLD_MS = Number(process.env.HOLD_MS ?? 2000);
const RUNS = 3;
const SPAWN = { x: 8, y: 6.5, z: 12 };
const MINIMUM_RUN_METRES = 5;
const YAW_TOLERANCE_DEGREES = 2;

const field = (text, name) => {
  const match = new RegExp('(?:^|;)' + name + '=([^;]*)').exec(text);
  if (match === null) throw new Error(`readout is missing ${name}`);
  return match[1];
};

const triple = (value, what) => {
  const parts = value.split(',').map(Number);
  if (parts.length !== 3 || parts.some((part) => !Number.isFinite(part))) {
    throw new Error(`${what} is not a finite triple: ${value}`);
  }
  return { x: parts[0], y: parts[1], z: parts[2] };
};

const pair = (value, what) => {
  const parts = value.split(',').map(Number);
  if (parts.length !== 2 || parts.some((part) => !Number.isFinite(part))) {
    throw new Error(`${what} is not a finite pair: ${value}`);
  }
  return { x: parts[0], z: parts[1] };
};

const main = async () => {
  const executablePath = process.env.CHROMIUM_PATH;
  const browser = await chromium.launch(
    executablePath === undefined ? { headless: true } : { headless: true, executablePath });
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const pageErrors = [];
  page.on('pageerror', (error) => pageErrors.push(String(error).slice(0, 160)));

  const execute = (command) => page.evaluate(async ([origin, line]) => {
    const response = await fetch(origin + '/__rusty/product/runtime/debug/execute', {
      method: 'POST',
      headers: { 'content-type': 'text/plain; charset=utf-8' },
      body: line,
    });
    return { status: response.status, text: await response.text() };
  }, [ORIGIN, command]);

  const sample = async () => {
    const read = await execute('craft.player.readout');
    if (read.status !== 200) throw new Error(`readout HTTP ${read.status}`);
    const intent = pair(field(read.text, 'intent'), 'intent');
    return {
      player: triple(field(read.text, 'after'), 'after'),
      intent,
      yaw: Number(field(read.text, 'yaw')),
      grounded: field(read.text, 'grounded'),
      stance: field(read.text, 'stance'),
      updates: Number(field(read.text, 'updates')),
    };
  };

  await page.goto(ORIGIN + '/', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('canvas', { timeout: 60000 });
  // Wait for the product to be live before touching input: a click that lands
  // before the host has armed its input layer is ignored, which is how the first
  // attempts at this capture ended up measuring a session that never received a
  // key at all.
  let previousUpdates = -1;
  for (let attempt = 0; attempt < 40; attempt++) {
    await page.waitForTimeout(500);
    const live = await sample();
    if (live.grounded === 'True' && live.updates > previousUpdates && live.updates > 60) break;
    previousUpdates = live.updates;
  }

  // Two steps are needed and neither is sufficient alone: a real click on the
  // canvas puts the Engine into its gameplay interaction mode (without it,
  // `allowsGameplayInput` refuses keys even when the canvas has focus), and an
  // explicit `focus()` satisfies the focus half of the gate, which a click alone
  // does not do in this browser. The crew-playtest service can take the first
  // step but not the second. Whether the pair has taken effect is not assumed:
  // a short probe hold must produce a forward intent before measuring begins.
  const canvas = await page.$('canvas');
  const box = await canvas.boundingBox();
  const centre = [box.x + box.width / 2, box.y + box.height / 2];
  const armAttempts = [];
  let armed = false;
  for (let attempt = 1; attempt <= 6 && !armed; attempt++) {
    await page.mouse.click(centre[0], centre[1]);
    await page.waitForTimeout(250);
    await page.evaluate(() => document.querySelector('canvas')?.focus());
    await page.waitForTimeout(250);
    for (const key of (process.env.WALK_KEYS ?? 'w').split(',')) { await page.keyboard.down(key); }
    await page.waitForTimeout(400);
    const probe = await sample();
    for (const key of (process.env.WALK_KEYS ?? 'w').split(',')) { await page.keyboard.up(key); }
    armed = Math.hypot(probe.intent.x, probe.intent.z) > 0;
    armAttempts.push({
      attempt,
      canvasFocused: await page.evaluate(() => document.activeElement === document.querySelector('canvas')),
      intent: probe.intent,
      updates: probe.updates,
    });
  }
  if (!armed) {
    throw new Error(`input never armed: ${JSON.stringify(armAttempts)}`);
  }

  const runs = [];
  for (let run = 1; run <= RUNS; run++) {
    const reset = await execute(`craft.player.teleport ${SPAWN.x} ${SPAWN.y} ${SPAWN.z}`);
    if (reset.status !== 200) throw new Error(`teleport failed: ${reset.text.slice(0, 120)}`);

    // Settle: grounded, still, and (from the spawn) facing the same way.
    let settled = null;
    for (let attempt = 0; attempt < 20; attempt++) {
      await page.waitForTimeout(300);
      const probe = await sample();
      const magnitude = Math.hypot(probe.intent.x, probe.intent.z);
      if (probe.grounded === 'True' && magnitude < 0.001) { settled = probe; break; }
    }
    if (settled === null) throw new Error(`run ${run}: the player never settled after the reset`);

    const before = settled;
    for (const key of (process.env.WALK_KEYS ?? 'w').split(',')) { await page.keyboard.down(key); }
    await page.waitForTimeout(HOLD_MS / 2);
    const duringHold = await sample();
    await page.waitForTimeout(HOLD_MS / 2);
    const atRelease = await sample();
    for (const key of (process.env.WALK_KEYS ?? 'w').split(',')) { await page.keyboard.up(key); }

    const travel = Math.hypot(atRelease.player.x - before.player.x, atRelease.player.z - before.player.z);
    const duringTravel = Math.hypot(duringHold.player.x - before.player.x, duringHold.player.z - before.player.z);
    const intentMagnitude = Math.hypot(duringHold.intent.x, duringHold.intent.z);
    const yawDrift = Math.abs(atRelease.yaw - before.yaw);

    const failures = [];
    if (intentMagnitude <= 0) failures.push('no forward intent during the hold');
    if (duringTravel <= 0) failures.push('no movement during the first half of the hold');
    if (travel < MINIMUM_RUN_METRES) failures.push(`travel ${travel.toFixed(2)} m below the ${MINIMUM_RUN_METRES} m floor`);
    if (yawDrift > YAW_TOLERANCE_DEGREES) failures.push(`yaw drifted ${yawDrift.toFixed(2)} degrees`);
    if (before.grounded !== 'True') failures.push('not grounded before the hold');
    if (atRelease.grounded !== 'True') failures.push('left the ground during the hold');

    runs.push({
      run,
      verdict: failures.length === 0 ? 'walked' : 'inconclusive',
      failures,
      travelMetres: Number(travel.toFixed(4)),
      metresPerSecond: Number((travel / (HOLD_MS / 1000)).toFixed(3)),
      intentDuringHold: duringHold.intent,
      yawBefore: before.yaw,
      yawAtRelease: atRelease.yaw,
      groundedBefore: before.grounded,
      groundedAtRelease: atRelease.grounded,
      stanceAtRelease: atRelease.stance,
      playerUpdatesInHold: atRelease.updates - before.updates,
      from: before.player,
      to: atRelease.player,
    });
  }

  await browser.close();

  const good = runs.filter((run) => run.verdict === 'walked');
  const sorted = good.map((run) => run.travelMetres).sort((a, b) => a - b);
  const median = sorted.length === 0 ? null : sorted[Math.floor(sorted.length / 2)];
  const summary = {
    runs: runs.length,
    attributedRuns: good.length,
    medianTravelMetres: median,
    medianMetresPerSecond: median === null ? null : Number((median / (HOLD_MS / 1000)).toFixed(3)),
    spreadMetres: sorted.length === 0 ? null : Number((sorted[sorted.length - 1] - sorted[0]).toFixed(4)),
    holdMilliseconds: HOLD_MS,
    spawn: SPAWN,
    source: "product 'after' (playerLocal) positions; distance and speed computed here",
  };

  console.log(JSON.stringify({ summary, armAttempts, runs, pageErrors }, null, 1));
  if (good.length !== RUNS) {
    throw new Error(`${RUNS - good.length} of ${RUNS} runs could not be attributed to input`);
  }
};

main().catch((error) => {
  console.log(JSON.stringify({ captureFailed: String(error).slice(0, 400) }));
  process.exitCode = 1;
});
