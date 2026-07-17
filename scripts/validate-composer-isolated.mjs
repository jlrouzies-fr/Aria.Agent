#!/usr/bin/env node
import { chromium } from 'playwright';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

(async () => {
    const browser = await chromium.launch({ headless: true });
    const page = await browser.newPage();

    const filePath = 'file://' + join(__dirname, 'composer-isolated.html');
    console.log(`Opening ${filePath} ...`);
    await page.goto(filePath, { waitUntil: 'networkidle' });

    const input = page.locator('#chatInput');
    await input.waitFor({ state: 'visible' });

    const initialHeight = await input.evaluate(el => el.getBoundingClientRect().height);
    console.log(`Initial height: ${initialHeight}px`);

    const longText = Array(8).fill('This is a line of text that should make the textarea grow taller.').join('\n');
    await input.fill(longText);
    await page.waitForTimeout(300);

    const info = await input.evaluate(el => ({
        height: el.getBoundingClientRect().height,
        styleHeight: el.style.height,
        scrollHeight: el.scrollHeight,
        clientHeight: el.clientHeight,
        offsetHeight: el.offsetHeight
    }));
    console.log('After fill:', info);

    await browser.close();

    const expanded = info.height > initialHeight;
    console.log(`Expanded: ${expanded} (delta ${info.height - initialHeight}px)`);

    if (!expanded) {
        console.error('VALIDATION FAILED');
        process.exit(1);
    }
    console.log('VALIDATION PASSED');
})();
