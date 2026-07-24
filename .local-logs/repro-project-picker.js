// Repro: /project picker flow on the local Aria.Web instance.
const { chromium } = require('/Users/jeanlaurentrouzies/.local/.playwright/package');

(async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
  page.on('console', m => { if (m.type() === 'error' || m.type() === 'warning') console.log('[console]', m.type(), m.text()); });
  page.on('pageerror', e => console.log('[pageerror]', e.message));

  await page.goto('http://localhost:5129/', { waitUntil: 'networkidle', timeout: 30000 });
  await page.screenshot({ path: '.local-logs/repro-1-landing.png' });

  // Open a fresh cogitation so the chat input exists.
  await page.getByText('NEW COGITATION', { exact: false }).first().click();
  await page.waitForTimeout(3000);

  const input = page.locator('#chatInput');
  if (await input.count() === 0) {
    console.log('NO chatInput found — landing page is not the chat. Screenshot saved.');
    await browser.close();
    return;
  }

  await input.click();
  await input.pressSequentially('/project', { delay: 40 });
  await page.waitForTimeout(800); // let the debounce fire
  await page.screenshot({ path: '.local-logs/repro-2-typed.png' });

  const pickerOpen = await input.getAttribute('data-picker-open');
  console.log('data-picker-open after typing:', pickerOpen);
  console.log('palette visible:', await page.locator('.ref-picker').count());

  await input.press('Enter');
  await page.waitForTimeout(800);
  await page.screenshot({ path: '.local-logs/repro-3-after-enter.png' });

  console.log('data-picker-open after Enter:', await input.getAttribute('data-picker-open'));
  console.log('picker rows:', await page.locator('.ref-picker .ref-row').count());
  console.log('picker header:', await page.locator('.ref-picker-header').allTextContents());

  await browser.close();
})().catch(e => { console.error('FATAL', e); process.exit(1); });
