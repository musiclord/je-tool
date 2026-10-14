'use strict';
// 交錯操作檢查：在無頭 Edge 依序做十五段「操作順序交錯」的情境，檢查畫面狀態、後端回應與 console 錯誤。
// 只連 Claude 的測試主機（預設 127.0.0.1:4246，資料在 artifacts/browser-host-claude-qa），不會碰使用者在
// 4244 的主機，也不碰給使用者檢查的 4245。腳本會改動案件，每次執行前先用 --reset --seed-only 重建測試主機的案件。
//
// 用法：node tools/harness/browser-host/interleaving-check.cjs [--port=4246] [--out=<資料夾>] [--only=01,02]
//
// 需要 playwright-core，本儲存庫不安裝。設定 JET_PLAYWRIGHT_CORE 指到既有的 playwright-core 資料夾，
// 或沿用 Playwright MCP 外掛留在 npm npx 快取的那份；腳本不下載任何東西。
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

const options = parseArgs(process.argv.slice(2));
const port = Number(options.port || 4246);
if (port === 4244 || port === 4245) {
  throw new Error(`Port ${port} belongs to a host the user reviews; run this only against the Claude QA host.`);
}
const repoRoot = path.resolve(__dirname, '..', '..', '..');
const BASE = `http://127.0.0.1:${port}`;
const OUT = path.resolve(options.out || path.join(repoRoot, 'artifacts', 'browser-host-claude-qa', 'interleaving',
  new Date().toISOString().replace(/[:.]/g, '-')));
const ONLY = String(options.only || '').split(',').filter(Boolean);
const KCT = '台灣中小企業-KCT 情境（SQLite）';
const CUSTOM = '台灣中小企業-自訂情境（DuckDB）';
const { chromium } = require(findPlaywrightCore());
fs.mkdirSync(OUT, { recursive: true });

let phase = 'start';
const consoleErrors = [];
const actions = [];
const results = [];

async function snapshot(page) {
  return page.evaluate(() => {
    const s = window.JetStore && JetStore.getState();
    if (!s) return { ready: false };
    const prescreen = s.lastRuns && s.lastRuns.prescreen;
    return {
      view: s.view,
      step: s.currentStepIndex,
      busy: s.busy,
      busyLabel: s.busyLabel,
      saved: ((s.filter && s.filter.savedScenarios) || []).map(x => x.name),
      draftLetters: s.filter && s.filter.draft && (s.filter.draft.groups || []).map(g => (g.rules || []).map(r => r.__kctLetter || r.type)),
      stale: s.staleState,
      prescreen: prescreen && Object.fromEntries(Object.entries(prescreen)
        .filter(([, v]) => v && typeof v === 'object' && 'count' in v)
        .map(([k, v]) => [k, v.status === 'na' && v.naReason ? 'na:' + v.naReason : v.count])),
      validateRows: s.lastRuns && s.lastRuns.validate && s.lastRuns.validate.stats && s.lastRuns.validate.stats.glRowCount,
      lastPeriodStart: s.project && s.project.lastPeriodStart,
      reports: (s.reportArtifacts || []).map(a => a.kind + (a.stale ? '(過期)' : '')),
    };
  });
}

async function waitIdle(page, timeout = 90000) {
  await page.waitForFunction(() => window.JetStore && !JetStore.getState().busy, null, { timeout });
  await page.waitForTimeout(400);
}

async function click(page, selector) {
  await page.locator(selector).filter({ visible: true }).first().click({ timeout: 10000 });
}

async function goStep(page, index) {
  await click(page, `.toc-item[data-step-index="${index}"]`);
  await page.waitForTimeout(500);
  await waitIdle(page);
}

async function toPicker(page) {
  const picker = page.getByRole('button', { name: /^開啟案件 / }).first();
  if (await picker.isVisible().catch(() => false)) return;
  await click(page, '[data-action="app-back-picker"]');
  await picker.waitFor({ state: 'visible', timeout: 15000 });
}

async function openCase(page, name) {
  await toPicker(page);
  await page.getByRole('button', { name: `開啟案件 ${name}` }).click();
  await page.waitForTimeout(800);
  await waitIdle(page);
}

/** 第五步已存情境的「編輯、另存副本、移除」收在「操作」摘要選單；先展開所在的 details 再點。 */
async function menuAction(page, selector) {
  const target = page.locator(selector).first();
  if (!(await target.isVisible().catch(() => false))) {
    await target.locator('xpath=ancestor::details[1]/summary').click({ timeout: 10000 });
    await page.waitForTimeout(250);
  }
  await target.click({ timeout: 10000 });
}

/** 第二步每一類資料都由「操作：名稱」按鈕展開，展開後才有該資料的動作按鈕。 */
async function importRowAction(page, rowName, selector) {
  await page.locator(`button[aria-label="操作：${rowName}"]`).first().click({ timeout: 10000 });
  await page.waitForTimeout(300);
  await click(page, selector);
}

async function savedPane(page) {
  await click(page, 'button[data-filter-pane-select="saved"]');
  await page.waitForTimeout(400);
}

async function editorPane(page) {
  await page.locator('button[data-filter-pane-select]').filter({ hasNotText: '已儲存' }).first().click();
  await page.waitForTimeout(400);
}

async function countHits(page, position) {
  return page.evaluate(async scenarioPosition => {
    const ref = JetStore.getState().filterResultRef;
    let cursor = null; let total = 0; let pages = 0;
    do {
      const payload = { scenarioPosition, scenarioRevision: ref.revision, pageSize: 500 };
      if (cursor) payload.cursor = cursor;
      const result = await JetApi.invoke('query.filterHitsPage', payload);
      total += result.rows.length; cursor = result.nextCursor; pages++;
    } while (cursor && pages < 40);
    return total;
  }, position);
}

function phaseActions() {
  return actions.filter(a => a.phase === phase);
}

async function run(page, name, body) {
  if (ONLY.length && !ONLY.includes(name.slice(0, 2))) return;
  phase = name;
  const record = { phase: name, checks: [], notes: [] };
  const check = (label, ok, detail) => record.checks.push({ label, ok: !!ok, detail });
  const note = text => record.notes.push(text);
  try {
    await body({ check, note });
  } catch (error) {
    record.checks.push({ label: '執行過程沒有中斷', ok: false, detail: String(error && error.stack || error).slice(0, 600) });
  }
  record.state = await snapshot(page).catch(error => ({ error: String(error) }));
  record.failedActions = phaseActions().filter(a => a.ok === false);
  record.actionCounts = phaseActions().reduce((acc, a) => { acc[a.action] = (acc[a.action] || 0) + 1; return acc; }, {});
  record.consoleErrors = consoleErrors.filter(e => e.phase === name);
  await page.screenshot({ path: path.join(OUT, `${name}.png`) }).catch(() => {});
  results.push(record);
  const failed = record.checks.filter(c => !c.ok);
  console.log(`[${name}] ${failed.length ? '有 ' + failed.length + ' 項不符' : '通過'}；失敗動作 ${record.failedActions.length}；console 錯誤 ${record.consoleErrors.length}`);
  for (const text of record.notes) console.log(`  ${text}`);
}

(async () => {
  const browser = await chromium.launch({ channel: options.channel || 'msedge', headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.on('console', m => {
    // 瀏覽器自動要求的 /favicon.ico 在主機上沒有檔案，這則 404 和 JET 無關。
    if (m.type() === 'error' && !/status of 404/.test(m.text())) consoleErrors.push({ phase, text: m.text().slice(0, 400) });
  });
  page.on('pageerror', e => consoleErrors.push({ phase, text: 'pageerror: ' + String(e).slice(0, 400) }));
  page.on('dialog', d => { consoleErrors.push({ phase, text: 'native dialog: ' + d.message() }); d.accept(); });
  page.on('response', async response => {
    if (response.status() >= 400 && !response.url().endsWith('/favicon.ico')) {
      consoleErrors.push({ phase, text: `HTTP ${response.status()} ${response.url()}` });
    }
    if (!response.url().endsWith('/__jet/action')) return;
    let request = null;
    try { request = response.request().postDataJSON(); } catch { /* 沒有 JSON 本文的請求不記 */ }
    let body = null;
    try { body = await response.json(); } catch { /* 頁面重新整理時回應可能讀不到 */ }
    actions.push({
      phase,
      action: request && request.action,
      ok: body ? body.ok : null,
      code: body && body.error ? body.error.code : null,
      message: body && body.error ? String(body.error.message).slice(0, 200) : null,
    });
  });

  await page.goto(BASE, { waitUntil: 'domcontentloaded' });
  await page.getByRole('button', { name: /^開啟案件 / }).first().waitFor({ timeout: 30000 });

  await run(page, '01-open-kct', async ({ check }) => {
    await openCase(page, KCT);
    const s = await snapshot(page);
    check('開啟後停在第六步', s.step === 5, s.step);
    check('已儲存 10 個 KCT 情境（測試主機的案件是剛重建的）', s.saved.length === 10, s.saved.length);
    check('沒有任何結果過期', s.stale && !s.stale.filter && !s.stale.prescreen && !s.stale.validation, s.stale);
  });

  await run(page, '02-draft-survives-step-switch', async ({ check, note }) => {
    await goStep(page, 4);
    await savedPane(page);
    await menuAction(page, '[data-action="edit-scenario"][data-index="0"]');
    await page.waitForTimeout(600);
    await editorPane(page).catch(() => {});
    await click(page, 'button[data-kct-letter="J"]');
    await page.waitForTimeout(400);
    const before = await snapshot(page);
    await goStep(page, 3);
    await goStep(page, 4);
    const after = await snapshot(page);
    note('編輯 KCT A 並加入 J：' + JSON.stringify(before.draftLetters) + '；切到第四步再回來：' + JSON.stringify(after.draftLetters));
    check('切換步驟後未儲存的草稿仍在', JSON.stringify(before.draftLetters) === JSON.stringify(after.draftLetters), after.draftLetters);
    check('已儲存情境沒有被未儲存的修改改變', after.saved.length === 10, after.saved);
    const cancel = page.locator('[data-action="cancel-edit-scenario"]').filter({ visible: true }).first();
    if (await cancel.isVisible().catch(() => false)) { await cancel.click(); await page.waitForTimeout(400); }
  });

  await run(page, '03-prescreen-while-navigating', async ({ check }) => {
    await goStep(page, 3);
    const before = await snapshot(page);
    await click(page, '[data-action="run-prescreen"]');
    for (const index of [4, 5, 3, 4]) {
      await page.click(`.toc-item[data-step-index="${index}"]`).catch(() => {});
      await page.waitForTimeout(120);
    }
    await waitIdle(page);
    const after = await snapshot(page);
    check('預篩選筆數和重跑前相同', JSON.stringify(before.prescreen) === JSON.stringify(after.prescreen), after.prescreen);
    const runs = phaseActions().filter(a => a.action === 'prescreen.run').length;
    check('預篩選只送出一次', runs === 1, runs);
    check('畫面沒有卡在處理中', after.busy === false, after.busyLabel);
  });

  await run(page, '04-rapid-triple-click-validate', async ({ check }) => {
    await goStep(page, 3);
    // 不等按鈕恢復可按，三下都在 150 毫秒內送出，和使用者手滑連點相同。
    const button = page.locator('[data-action="run-validate"]').filter({ visible: true }).first();
    await button.click();
    await button.click({ force: true, noWaitAfter: true, timeout: 500 }).catch(() => {});
    await page.waitForTimeout(60);
    await button.click({ force: true, noWaitAfter: true, timeout: 500 }).catch(() => {});
    await waitIdle(page);
    const runs = phaseActions().filter(a => a.action === 'validate.run');
    check('快速連點三次只執行一次驗證', runs.length === 1, runs.map(r => (r.ok ? 'ok' : r.code)));
    const s = await snapshot(page);
    check('驗證後 GL 筆數仍是 30,479', s.validateRows === 30479, s.validateRows);
  });

  await run(page, '05-change-preparation-date', async ({ check, note }) => {
    await goStep(page, 0);
    await click(page, '[data-action="edit-project-metadata"]');
    await page.fill('input[name="lastPeriodStart"]', '2026-01-20');
    await page.getByRole('button', { name: '儲存案件資料' }).click();
    await page.waitForTimeout(500);
    await waitIdle(page);
    let s = await snapshot(page);
    note('改準備日後的報告清單：' + s.reports.join('、'));
    check('準備日已存成 2026-01-20', s.lastPeriodStart === '2026-01-20', s.lastPeriodStart);
    check('已儲存情境全部清除', s.saved.length === 0, s.saved.length);
    check('預篩選標成需要重算，驗證結果保留', s.stale && s.stale.prescreen === true && s.stale.validation === false, s.stale);
    await goStep(page, 5);
    const notice = await page.locator('[data-bind="export-no-scenario"]').filter({ visible: true }).count();
    check('第六步提醒目前沒有可匯出的情境', notice > 0, notice);
    await goStep(page, 3);
    await click(page, '[data-action="run-prescreen"]');
    await waitIdle(page);
    s = await snapshot(page);
    // 2026-01-20 以後核准：存貨跌價與信用減損（1/20）、收入截止（1/21）、所得稅（2/6），共 9 筆分錄。
    check('重算後財報準備日起核准為 9 筆', s.prescreen && s.prescreen.postPeriodApproval === 9, s.prescreen && s.prescreen.postPeriodApproval);
    check('重算後預篩選不再過期', s.stale && s.stale.prescreen === false, s.stale);
  });

  await run(page, '06-switch-case-during-validate', async ({ check }) => {
    await openCase(page, CUSTOM);
    let s = await snapshot(page);
    check('DuckDB 案件有 10 個自訂情境', s.saved.length === 10, s.saved.length);
    await goStep(page, 3);
    await click(page, '[data-action="run-validate"]');
    await page.waitForTimeout(80);
    await page.click('[data-action="app-back-picker"]').catch(() => {});
    await page.waitForTimeout(300);
    await toPicker(page).catch(() => {});
    await openCase(page, KCT);
    s = await snapshot(page);
    check('切回 KCT 案件後顯示 KCT 的準備日', s.lastPeriodStart === '2026-01-20', s.lastPeriodStart);
    check('KCT 案件沒有帶入 DuckDB 案件的情境', s.saved.length === 0, s.saved);
    check('KCT 案件驗證筆數正確', s.validateRows === 30479, s.validateRows);
    await openCase(page, CUSTOM);
    s = await snapshot(page);
    check('DuckDB 案件情境仍是 10 個', s.saved.length === 10, s.saved.length);
    check('DuckDB 案件驗證未過期', s.stale && s.stale.validation === false, s.stale);
  });

  // 使用者 2026-10-11 裁定：情境有修改還沒儲存時，開啟另一個已存情境前先詢問；沒有修改時直接換。
  await run(page, '07-edit-a-then-open-b', async ({ check, note }) => {
    const savedJson = index => page.evaluate(i => JSON.stringify(JetStore.getState().filter.savedScenarios[i]), index);
    const draftInfo = () => page.evaluate(() => {
      const draft = JetStore.getState().filter.draft;
      return { name: draft.name, editing: draft.__editingIndex };
    });
    const modal = page.locator('[data-bind="filter-pending-modal"]');
    const asking = () => modal.isVisible().catch(() => false);
    async function openSaved(index) {
      await savedPane(page);
      await menuAction(page, `[data-action="edit-scenario"][data-index="${index}"]`);
      await page.waitForTimeout(400);
    }
    await goStep(page, 4);
    const firstBefore = await savedJson(0), thirdBefore = await savedJson(2);
    await openSaved(0);
    await editorPane(page).catch(() => {});
    await click(page, 'button[data-kct-letter="J"]');
    await page.waitForTimeout(300);
    const edited = await snapshot(page);
    const names = edited.saved;

    await openSaved(1);
    check('有未儲存的修改時先詢問', await asking(), null);
    const body = (await modal.locator('.modal__body').innerText().catch(() => '')).trim();
    check('詢問寫出哪個情境的修改會被換掉', body === `情境「${names[0]}」的修改尚未儲存。開啟「${names[1]}」會換掉這些修改。`, body);
    const buttons = (await modal.locator('.modal__actions button').allInnerTexts()).map(text => text.trim());
    check('按鈕依序是留在這裡、不儲存直接開啟、儲存並開啟',
      JSON.stringify(buttons) === JSON.stringify(['留在這裡', '不儲存，直接開啟', '儲存並開啟']), buttons);
    const focused = await page.evaluate(() => document.activeElement && document.activeElement.getAttribute('data-pending-choice'));
    check('預設焦點在儲存', focused === 'save', focused);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);
    let s = await snapshot(page);
    check('按 Esc 等於留在這裡，修改還在', !(await asking()) && JSON.stringify(s.draftLetters) === JSON.stringify(edited.draftLetters), s.draftLetters);

    await openSaved(1);
    await modal.locator('.modal__actions [data-pending-choice="stay"]').click();
    await page.waitForTimeout(300);
    s = await snapshot(page);
    check('選「留在這裡」後修改還在', !(await asking()) && JSON.stringify(s.draftLetters) === JSON.stringify(edited.draftLetters), s.draftLetters);

    await openSaved(1);
    await modal.locator('.modal__actions [data-pending-choice="continue"]').click();
    await page.waitForTimeout(500);
    let draft = await draftInfo();
    check('選「不儲存，直接開啟」後換成第 2 個情境', draft.name === names[1] && draft.editing === 1, draft);
    check('第 1 個情境沒有存進未儲存的修改', (await savedJson(0)) === firstBefore, null);

    await openSaved(2);
    check('沒有修改時直接換，不詢問', !(await asking()), null);
    draft = await draftInfo();
    check('換成第 3 個情境', draft.name === names[2] && draft.editing === 2, draft);

    await editorPane(page).catch(() => {});
    await click(page, 'button[data-kct-letter="J"]');
    await page.waitForTimeout(300);
    const commitsBefore = phaseActions().filter(a => a.action === 'filter.scenario.save').length;
    await openSaved(3);
    await modal.locator('.modal__actions [data-pending-choice="save"]').click();
    await page.waitForTimeout(600);
    await waitIdle(page);
    draft = await draftInfo();
    const commits = phaseActions().filter(a => a.action === 'filter.scenario.save').length - commitsBefore;
    check('選「儲存並開啟」先送出一次儲存', commits === 1, commits);
    check('存好後換成第 4 個情境', draft.name === names[3] && draft.editing === 3, draft);
    const thirdAfter = await savedJson(2);
    check('第 3 個情境存進了 KCT J', thirdAfter !== thirdBefore && /preparerEqualsApprover/.test(thirdAfter), thirdAfter && thirdAfter.slice(0, 160));
    s = await snapshot(page);
    check('情境仍是 10 個', s.saved.length === 10, s.saved.length);
    note(`詢問內容：${body}`);
    // 收尾：取消編輯，讓下一段從空白草稿開始。
    const cancel = page.locator('[data-action="cancel-edit-scenario"]').filter({ visible: true }).first();
    if (await cancel.isVisible().catch(() => false)) { await cancel.click(); await page.waitForTimeout(300); }
  });

  await run(page, '08-scenario-limit-and-double-save', async ({ check, note }) => {
    await goStep(page, 4);
    await editorPane(page).catch(() => {});
    await click(page, 'button[data-kct-letter="J"]');
    await page.waitForTimeout(400);
    await click(page, '[data-action="save-scenario"]');
    await page.waitForTimeout(600);
    await waitIdle(page);
    let s = await snapshot(page);
    check('已有 10 個時沒有存進第 11 個', s.saved.length === 10, s.saved.length);
    const notices = await page.locator('.form-notice, [role="alert"]').filter({ visible: true }).allInnerTexts();
    note('畫面提醒：' + notices.map(t => t.replace(/\s+/g, ' ').slice(0, 140)).join('｜'));
    await savedPane(page);
    await menuAction(page, '[data-action="remove-scenario"][data-index="9"]');
    await page.waitForTimeout(300);
    await click(page, '[data-action="confirm-remove-scenario"]');
    await page.waitForTimeout(500);
    await waitIdle(page);
    s = await snapshot(page);
    check('移除第 10 個後剩 9 個', s.saved.length === 9, s.saved.length);
    await editorPane(page).catch(() => {});
    if (!JSON.stringify(s.draftLetters || []).includes('J')) await click(page, 'button[data-kct-letter="J"]');
    await page.waitForTimeout(400);
    const save = page.locator('[data-action="save-scenario"]').filter({ visible: true }).first();
    const before = phaseActions().filter(a => a.action === 'filter.scenario.save').length;
    await save.click();
    await save.click({ force: true, noWaitAfter: true, timeout: 500 }).catch(() => {});
    await page.waitForTimeout(600);
    await waitIdle(page);
    s = await snapshot(page);
    const commits = phaseActions().filter(a => a.action === 'filter.scenario.save').length - before;
    check('快速連按兩次儲存只送出一次', commits === 1, commits);
    check('儲存後共 10 個情境，名稱沒有重複', s.saved.length === 10 && new Set(s.saved).size === 10, s.saved);
  });

  await run(page, '09-taxonomy-change-then-new-kct', async ({ check }) => {
    await goStep(page, 3);
    const toggle = page.locator('[data-action="toggle-taxonomy"]').filter({ visible: true }).first();
    if (await toggle.isVisible().catch(() => false)) await toggle.click();
    await page.waitForTimeout(400);
    await click(page, '[data-action="add-taxonomy-category"]');
    await page.waitForTimeout(400);
    const inputs = page.locator('input[type="text"]').filter({ visible: true });
    let filled = false;
    for (let i = (await inputs.count()) - 1; i >= 0 && !filled; i--) {
      if (!(await inputs.nth(i).inputValue())) { await inputs.nth(i).fill('其他應收款類'); filled = true; }
    }
    check('找到新增分類的名稱欄', filled, null);
    await click(page, '[data-action="save-taxonomy"]');
    await page.waitForTimeout(500);
    await waitIdle(page);
    let s = await snapshot(page);
    check('分類設定儲存後情境全部清除', s.saved.length === 0, s.saved.length);
    check('分類設定儲存後預篩選標成需要重算', s.stale && s.stale.prescreen === true, s.stale);
    await goStep(page, 4);
    await editorPane(page).catch(() => {});
    await click(page, 'button[data-kct-letter="J"]');
    await page.waitForTimeout(300);
    await page.waitForTimeout(400);
    await click(page, '[data-action="save-scenario"]');
    await page.waitForTimeout(500);
    await waitIdle(page);
    s = await snapshot(page);
    check('KCT J 情境儲存成功', s.saved.length === 1, s.saved);
    const hits = await countHits(page, 1).catch(error => 'error: ' + error);
    check('KCT J 命中 189 筆（和建案比對的答案相同）', hits === 189, hits);
  });

  await run(page, '10-reload-during-export', async ({ check, note }) => {
    await goStep(page, 5);
    const before = (await snapshot(page)).reports.filter(r => r.startsWith('workingPaper')).length;
    await click(page, '[data-action="export-workpaper"]');
    await page.waitForTimeout(150);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(1500);
    const reopen = page.getByRole('button', { name: `開啟案件 ${CUSTOM}` });
    if (await reopen.isVisible().catch(() => false)) { await reopen.click(); await page.waitForTimeout(800); }
    await waitIdle(page);
    await goStep(page, 5);
    const s = await snapshot(page);
    const after = s.reports.filter(r => r.startsWith('workingPaper')).length;
    // 預篩選需要重算時，第六步會先重跑預篩選再匯出；重新整理會中斷前端後續的匯出呼叫，後端已收到的作業照常完成。
    note(`重新整理前 Working Paper ${before} 份，之後 ${after} 份；預篩選過期：${s.stale && s.stale.prescreen}`);
    check('重新整理後畫面不卡在處理中', s.busy === false, s.busyLabel);
    check('Working Paper 份數沒有減少', after >= before, { before, after });
  });

  await run(page, '11-unsaved-account-category-then-leave', async ({ check, note }) => {
    await goStep(page, 3);
    const jet = page.locator('[data-action="select-account-jet"]').filter({ visible: true }).first();
    if (await jet.isVisible().catch(() => false)) { await jet.click(); await page.waitForTimeout(500); }
    const editor = page.locator('[data-bind="account-editor"]');
    const search = editor.locator('input').first();
    await search.fill('4610');
    await search.press('Enter');
    await page.waitForTimeout(1000);
    await editor.locator('[data-account-select="0"]').check();
    await editor.locator('[data-account-category-tree] summary').click();
    await editor.getByRole('radio', { name: 'Others', exact: true }).click();
    await editor.locator('[data-account-apply]').click();
    await page.waitForTimeout(300);
    await page.click('.toc-item[data-step-index="4"]');
    await page.waitForTimeout(500);
    const modal = page.locator('[data-bind="account-pending-modal"]');
    const asked = await modal.isVisible().catch(() => false);
    check('有未儲存分類時離開會先詢問', asked, null);
    if (!asked) return;
    note('詢問內容：' + (await modal.innerText()).replace(/\s+/g, ' ').slice(0, 200));
    await modal.locator('button[data-pending-choice="stay"]').click();
    await page.waitForTimeout(300);
    let s = await snapshot(page);
    check('選「留在這裡」後仍在第四步', s.step === 3, s.step);
    await page.click('.toc-item[data-step-index="4"]');
    await page.waitForTimeout(500);
    await modal.locator('button[data-pending-choice="save"]').click();
    await page.waitForTimeout(800);
    await waitIdle(page);
    s = await snapshot(page);
    check('選「儲存並繼續」後到第五步', s.step === 4, s.step);
    check('分類儲存後已存情境全部清除', s.saved.length === 0, s.saved.length);
    const saves = phaseActions().filter(a => a.action === 'accountMapping.save').length;
    check('只送出一次科目分類儲存', saves === 1, saves);
  });

  await run(page, '12-clear-authorized-list', async ({ check }) => {
    await openCase(page, KCT);
    await goStep(page, 1);
    await importRowAction(page, '授權編製人員清單', '[data-action="clear-authorized-preparer"]');
    await page.waitForTimeout(500);
    const confirm = page.getByRole('button', { name: /確認移除|移除名單|確定/ }).filter({ visible: true }).first();
    if (await confirm.isVisible().catch(() => false)) await confirm.click();
    await page.waitForTimeout(500);
    await waitIdle(page);
    let s = await snapshot(page);
    check('移除名單後預篩選標成需要重算', s.stale && s.stale.prescreen === true, s.stale);
    await goStep(page, 3);
    await click(page, '[data-action="run-prescreen"]');
    await waitIdle(page);
    s = await snapshot(page);
    const value = s.prescreen && s.prescreen.nonAuthorizedPreparer;
    check('非授權編製人員改為說明缺少名單', typeof value === 'string' && value.startsWith('na:'), value);
  });

  await run(page, '13-reconfirm-gl-mapping-then-jump', async ({ check, note }) => {
    await goStep(page, 2);
    await click(page, '[data-action="remap-gl"]');
    await page.waitForTimeout(800);
    // 取消「傳票來源」的配對，讓配對和已確認的不同，再重新確認。
    const field = page.locator('select[data-mapping-key="jeSource"]').filter({ visible: true }).first();
    await field.selectOption('');
    await page.waitForTimeout(500);
    await click(page, '[data-action="commit-gl"]');
    await page.waitForTimeout(800);
    const dialogs = page.locator('[role="dialog"]').filter({ visible: true });
    if (await dialogs.count()) {
      const proceed = dialogs.first().getByRole('button', { name: /確認|繼續|重新確認/ }).last();
      if (await proceed.isVisible().catch(() => false)) await proceed.click();
    }
    await page.waitForTimeout(800);
    await waitIdle(page, 180000);
    let s = await snapshot(page);
    note('重新確認 GL 配對後的報告清單：' + s.reports.join('、'));
    check('驗證與預篩選都標成需要重跑', s.stale && s.stale.validation === true && s.stale.prescreen === true, s.stale);
    await page.click('.toc-item[data-step-index="5"]').catch(() => {});
    await page.waitForTimeout(800);
    s = await snapshot(page);
    check('驗證過期時不能直接跳到第六步', s.step !== 5, s.step);
    await goStep(page, 3);
    await click(page, '[data-action="run-validate"]');
    await waitIdle(page, 180000);
    s = await snapshot(page);
    check('重新驗證後 GL 筆數 30,479 且不再過期', s.validateRows === 30479 && s.stale.validation === false, { rows: s.validateRows, stale: s.stale });
  });

  await run(page, '14-rapid-step-hopping', async ({ check }) => {
    const order = [0, 5, 2, 4, 1, 3, 5, 0, 4, 2, 3, 1, 5, 4, 0, 3, 2, 5, 1, 4];
    for (const index of order) {
      await page.click(`.toc-item[data-step-index="${index}"]`).catch(() => {});
      await page.waitForTimeout(40);
    }
    await page.waitForTimeout(1500);
    await waitIdle(page);
    const s = await snapshot(page);
    check('最後停在最後點的步驟', s.step === 4, s.step);
    check('畫面沒有卡在處理中', s.busy === false, s.busyLabel);
  });

  await run(page, '15-back-to-picker', async ({ check }) => {
    await toPicker(page);
    const s = await snapshot(page);
    check('回到案件清單且不在處理中', s.busy === false, s.busyLabel);
  });

  fs.writeFileSync(path.join(OUT, 'results.json'), JSON.stringify({ base: BASE, results }, null, 2));
  await browser.close();
  const failed = results.flatMap(r => r.checks.filter(c => !c.ok).map(c => `${r.phase}: ${c.label} → ${JSON.stringify(c.detail)}`));
  const errors = results.flatMap(r => r.consoleErrors.map(e => `${r.phase}: ${e.text}`))
    .concat(results.flatMap(r => r.failedActions.map(a => `${r.phase}: ${a.action} ${a.code} ${a.message}`)));
  console.log(failed.length || errors.length
    ? ['不符項目：', ...failed, ...errors].join('\n')
    : `全部檢查通過，截圖與明細在 ${path.relative(repoRoot, OUT)}`);
  process.exitCode = failed.length || errors.length ? 1 : 0;
})().catch(error => { console.error(error); process.exit(1); });
