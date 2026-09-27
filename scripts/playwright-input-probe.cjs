// Direct Playwright probe of the product's live lane: what has focus, and
// whether keyboard input actually reaches the Engine's canvas.
//
// Unlike the crew-playtest service, this can evaluate page JavaScript, so it
// reports `document.activeElement` and the pointer-lock state instead of
// inferring them, and it can read the product's own debug readout on the way
// through.

// Playwright lives in the playtest service's tree, not in this product's, so the
// module path is supplied by the caller; the repo copy stays the canonical probe.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE ?? 'playwright');

const URL = 'http://127.0.0.1:37305/';
const HOLD_MS = 2500;

const focusState = (page) => page.evaluate(() => {
  const active = document.activeElement;
  const canvas = document.querySelector('canvas');
  return {
    activeTag: active === null ? null : active.tagName,
    activeId: active === null ? null : active.id,
    activeIsCanvas: active !== null && active === canvas,
    pointerLocked: document.pointerLockElement !== null,
    canvasTabIndex: canvas === null ? null : canvas.tabIndex,
    canvasCount: document.querySelectorAll('canvas').length,
  };
});

const readPlayer = (page) => page.evaluate(async () => {
  const response = await fetch('/__rusty/product/runtime/debug/execute', {
    method: 'POST',
    headers: { 'content-type': 'text/plain; charset=utf-8' },
    body: 'craft.player.readout',
  });
  return { ok: response.ok, status: response.status, text: await response.text() };
});

const field = (text, name) => {
  const match = new RegExp('(?:^|;)' + name + '=([^;]*)').exec(text);
  return match === null ? null : match[1];
};

const snapshot = async (page) => {
  const read = await readPlayer(page);
  const keys = Number(field(read.text, 'keys'));
  const updates = Number(field(read.text, 'updates'));
  return {
    httpStatus: read.status,
    keys,
    updates,
    intent: field(read.text, 'intent'),
    after: field(read.text, 'after'),
    camera: field(read.text, 'cameraPosition'),
    grounded: field(read.text, 'grounded'),
    yaw: field(read.text, 'yaw'),
    stepAccepted: /accepted=(true|false)/.exec(read.text)?.[1] ?? null,
  };
};

const main = async () => {
  const executablePath = process.env.CHROMIUM_PATH;
  const browser = await chromium.launch(executablePath === undefined ? { headless: true } : { headless: true, executablePath });
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const consoleErrors = [];
  page.on('pageerror', (error) => consoleErrors.push(String(error).slice(0, 160)));

  const report = { url: URL, consoleErrors };
  await page.goto(URL, { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('canvas', { timeout: 60000 });
  report.focusOnLoad = await focusState(page);

  // Let the world stream and the player settle before measuring.
  for (let i = 0; i < 30; i++) {
    const probe = await snapshot(page);
    if (probe.grounded === 'True' && probe.keys !== null) {
      report.settledAfterPolls = i;
      break;
    }
    await page.waitForTimeout(500);
  }

  // A real mouse click on the canvas, exactly as a player would.
  const canvas = await page.$('canvas');
  const box = await canvas.boundingBox();
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await page.waitForTimeout(300);
  report.elementAtCanvasCentre = await page.evaluate(([x, y]) => {
    const element = document.elementFromPoint(x, y);
    return element === null ? null : {
      tag: element.tagName,
      id: element.id,
      className: typeof element.className === 'string' ? element.className.slice(0, 80) : null,
      isCanvas: element.tagName === 'CANVAS',
    };
  }, [box.x + box.width / 2, box.y + box.height / 2]);
  report.focusAfterCanvasClick = await focusState(page);
  report.afterClick = await snapshot(page);

  // Explicit focus, in case the click did not take it.
  await page.evaluate(() => document.querySelector('canvas')?.focus());
  await page.waitForTimeout(200);
  report.focusAfterExplicitFocus = await focusState(page);

  const before = await snapshot(page);

  await page.keyboard.down('w');
  await page.waitForTimeout(400);
  report.duringHold = await snapshot(page);
  await page.waitForTimeout(HOLD_MS - 400);
  report.atRelease = await snapshot(page);
  await page.keyboard.up('w');

  const after = await snapshot(page);
  report.before = before;
  report.after = after;
  report.keyEventsDelta = after.keys - before.keys;
  report.updatesDelta = after.updates - before.updates;

  const parse = (triple) => (triple === null ? null : triple.split(',').map(Number));
  const [bx, by, bz] = parse(before.after) ?? [];
  const [ax, ay, az] = parse(after.after) ?? [];
  if ([bx, bz, ax, az].every((value) => Number.isFinite(value))) {
    report.planarWalkMetres = Number(Math.hypot(ax - bx, az - bz).toFixed(4));
    report.verticalMetres = Number((ay - by).toFixed(4));
  }

  report.focusAtEnd = await focusState(page);
  await browser.close();
  console.log(JSON.stringify(report, null, 1));
};

main().catch((error) => {
  console.log(JSON.stringify({ probeFailed: String(error).slice(0, 400) }));
  process.exitCode = 1;
});
