'use strict';
// Headless screenshots of the JET synthetic frontend preview, for design review without opening a window
// on the user's desktop. It never talks to the browser host the user runs on 127.0.0.1:4244.
//
// Usage:
//   node preview-shot.cjs --out=artifacts/frontend-preview/shots/step5.png [--step=5] [--scene=matches]
//        [--width=1536] [--height=864] [--css=extra.css] [--overview] [--full] [--port=4243] [--channel=msedge]
//
// Requires playwright-core, which this repository does not install. Set JET_PLAYWRIGHT_CORE to its folder,
// or reuse the copy the Playwright MCP plugin left in the npm npx cache. The script never downloads anything.
const fs = require('node:fs');
const path = require('node:path');

function parseArgs(argv) {
  const options = {};
  for (const arg of argv) {
    const match = /^--([a-z-]+)(?:=(.*))?$/.exec(arg);
    if (!match) throw new Error(`Unknown argument: ${arg}`);
    options[match[1]] = match[2] === undefined ? true : match[2];
  }
  return options;
}

function findPlaywrightCore() {
  const fromEnv = process.env.JET_PLAYWRIGHT_CORE;
  if (fromEnv) {
    if (!fs.existsSync(path.join(fromEnv, 'package.json'))) throw new Error(`JET_PLAYWRIGHT_CORE has no package.json: ${fromEnv}`);
    return fromEnv;
  }
  const npxRoot = path.join(process.env.LOCALAPPDATA || '', 'npm-cache', '_npx');
  const candidates = fs.existsSync(npxRoot)
    ? fs.readdirSync(npxRoot)
        .map(name => path.join(npxRoot, name, 'node_modules', 'playwright-core'))
        .filter(dir => fs.existsSync(path.join(dir, 'package.json')))
        .sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs)
    : [];
  if (candidates.length === 0) {
    throw new Error('playwright-core not found. Set JET_PLAYWRIGHT_CORE to an existing playwright-core folder; ' +
      'ask the user before installing anything.');
  }
  return candidates[0];
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  if (!options.out) throw new Error('--out=<png path> is required.');
  const port = Number(options.port || 4243);
  if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('--port must be an integer.');
  if (port === 4244) throw new Error('Port 4244 is the browser host the user runs in Codex. Use the synthetic preview instead.');
  const scene = options.scene || 'matches';
  if (!['matches', 'empty', 'stale'].includes(scene)) throw new Error('--scene must be matches, empty or stale.');
  const step = options.step === undefined ? null : Number(options.step);
  if (step !== null && !(Number.isInteger(step) && step >= 1 && step <= 6)) throw new Error('--step must be 1 to 6.');

  const { chromium } = require(findPlaywrightCore());
  const browser = await chromium.launch({ channel: options.channel || 'msedge', headless: true });
  try {
    const page = await browser.newPage({
      viewport: { width: Number(options.width || 1536), height: Number(options.height || 864) },
      deviceScaleFactor: 1,
    });
    const problems = [];
    page.on('pageerror', error => problems.push(String(error)));
    page.on('console', message => {
      const source = (message.location() && message.location().url) || '';
      if (message.type() === 'error' && !/favicon\.ico$/.test(source)) problems.push(message.text());
    });
    await page.goto(`http://127.0.0.1:${port}/?scene=${scene}`, { waitUntil: 'networkidle' });
    await page.waitForFunction(() => {
      const status = document.getElementById('preview-status');
      return status && !/正在載入/.test(status.textContent);
    }, null, { timeout: 30000 });
    await page.addStyleTag({ content: 'aside[aria-label="設計預覽工具"]{display:none !important}' });
    if (options.css) await page.addStyleTag({ path: path.resolve(String(options.css)) });
    if (step !== null) {
      await page.evaluate(index => {
        const items = document.querySelectorAll('.toc-item');
        if (items[index]) items[index].click();
      }, step - 1);
      await page.waitForTimeout(1200);
    }
    if (options.overview) {
      await page.evaluate(() => {
        const button = document.querySelector('.overview-btn');
        if (button) button.click();
      });
      await page.waitForTimeout(1200);
    }
    await page.evaluate(() => {
      if (document.activeElement) document.activeElement.blur();
      const content = document.querySelector('.content');
      const current = document.querySelector('.stepflow-item--current');
      if (content && current) content.scrollTop += current.getBoundingClientRect().top - content.getBoundingClientRect().top - 6;
    });
    await page.waitForTimeout(300);
    const out = path.resolve(String(options.out));
    fs.mkdirSync(path.dirname(out), { recursive: true });
    await page.screenshot({ path: out, fullPage: !!options.full });
    console.log(`saved ${out}`);
    if (problems.length) console.log(`page problems:\n- ${problems.slice(0, 10).join('\n- ')}`);
  } finally {
    await browser.close();
  }
}

main().catch(error => {
  console.error(error.message || error);
  process.exit(1);
});
