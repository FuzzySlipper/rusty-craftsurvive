// Presses the product's attack key in a live session, using only the input gate the
// walk lane established: a real click on the canvas puts the Engine into gameplay
// interaction mode, and an explicit focus() satisfies the focus half of the gate.
// Neither alone is sufficient. No debug command is sent from here: the point is to
// exercise the key path and nothing else.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const ORIGIN = process.env.PRODUCT_ORIGIN ?? 'http://127.0.0.1:37305';
const KEY = process.env.ATTACK_KEY ?? 'j';
const PRESSES = Number(process.env.PRESSES ?? 12);
const GAP_MS = Number(process.env.GAP_MS ?? 1000);

(async () => {
  const launchOptions = process.env.CHROMIUM_PATH === undefined
    ? { headless: true }
    : { headless: true, executablePath: process.env.CHROMIUM_PATH };
  const browser = await chromium.launch(launchOptions);
  const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
  const pageErrors = [];
  page.on('pageerror', (error) => pageErrors.push(String(error).slice(0, 160)));
  await page.goto(ORIGIN + '/', { waitUntil: 'domcontentloaded' });
  await page.waitForSelector('canvas', { timeout: 60000 });
  await page.waitForTimeout(6000);
  await page.click('canvas');
  await page.evaluate(() => { const canvas = document.querySelector('canvas'); if (canvas) canvas.focus(); });
  await page.waitForTimeout(500);
  for (let i = 0; i < PRESSES; i += 1) {
    await page.keyboard.press(KEY);
    await page.waitForTimeout(GAP_MS);
  }
  console.log(JSON.stringify({ key: KEY, presses: PRESSES, pageErrors }));
  await browser.close();
})().catch((error) => { console.error(String(error).slice(0, 300)); process.exit(1); });
