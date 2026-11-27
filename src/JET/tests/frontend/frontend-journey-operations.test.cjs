'use strict';
// 長作業、忙碌中再按與取消，以及訊息面板「複製紀錄」的情境測試。
//
// 使用者的操作：
//   長作業：按「執行驗證」與「執行預篩選」看忙碌畫面的經過時間與提示，匯入時沒有那句提示；忙碌中又按了別的按鈕；
//           開案時按「取消作業」；產生工作底稿途中取消，取消成功、動作剛好做完與取消失敗；
//           長作業成功與失敗後在訊息面板看到的句子；後端錯誤沒有附原因。另外直接用 Ui.run 走過「完成後重新整理」的規則。
//           開案載入完成、還在讀訊息紀錄時看取消鈕；按下取消時動作剛好做完；選授權編製人員清單時連續兩個可以取消的請求。
//           各處的操作被取消，或後端回覆另一項作業還在進行：案件清單上的開案、重新整理、刪除與輸出支援日誌，建立與修改案件，
//           確認配對，匯入清單重讀來源與重新檢視，授權編製人員清單的來源檔預覽，驗證報告與科目配對範本，第五步儲存情境。
//           按「開始匯入」後看匯入中的橫幅、收起的按鈕與進度，匯入失敗後按鈕回來。
//           已經有作業在跑時再按一次：開始匯入，展開來源預覽，改編碼、分隔符或欄名在第幾列，授權編製人員清單換工作表，
//           勾選每週休息日，案件清單上開啟案件、重新整理與確認刪除，建立與修改案件，移除授權編製人員清單，
//           開發者面板換頁，確認配對；這些都沒有開始，畫面和按下前一樣。
//           勾選每週休息日後沒有存進去：送出後被取消、後端回覆另一項作業還在進行、後端回覆失敗，
//           以及步驟上方還留著失敗提示時再勾選並取消；勾選都回到已存的設定。
//   複製紀錄：有案件時按「複製紀錄」；剪貼簿拒絕；複製途中換成同名的另一個案件；沒有開啟案件時按。
//
// 夾具：
//   長作業的測試用支援模組的 fullAppFixture（經 bootFullApp 開啟，本檔叫 boot）：整個 index.html 的 body 標記與它列出的全部前端腳本（不含 ECharts 函式庫），
//   正式的 jet-api.js 經假的 chrome.webview 收發，app.js 照瀏覽器在 DOMContentLoaded 執行正式的 init()。
//   所以忙碌畫面的作業名稱由 jet-api.js 真的送出請求時的 onRequestStarted 事件帶入，不改 app.js 的內部變數。
//   不用 remapAppFixture：它在 app.js 外面放了一個自己的 busyActiveAction，忙碌畫面讀不到。
//   複製紀錄的測試用支援模組裡為它準備的 messagePanelFixture。
//
// 斷言依案例逐字核對或比對意思。預期值使用固定文字，action payload 用 deepEqual 精確比對。
// 產品裡的作業名稱只比對意思，測試自己傳入的合成作業名稱逐字核對。
// 每個測試都有 30 秒上限：有請求永遠等不到回應時，測試直接失敗，不會一直等下去。
// 「忙碌中再按」的測試也不等待被擋下的作業，只確認它立刻結束。
const nodeTest = require('node:test');
const test = (name, fn) => nodeTest(name, { timeout: 30000 }, fn);
const assert = require('node:assert/strict');
const { settle, plain, messagePanelFixture, remapPrescreen, usageJourneyFixture, savedBlankDescription, flush, bootFullApp: boot, LOADED_RULE_DEFAULTS } = require('./support/frontend-fixtures.cjs');
const { unchecked } = require('./support/action-contract.cjs');

const CASE = { projectId: '合成案件', databaseProvider: 'sqlite' };

// 從案件清單開案，回到第四步「資料驗證」所需的狀態：GL、TB 都已匯入並確認配對。
async function openCaseAtValidation() {
  const f = await boot([CASE]);
  f.click('[data-action="picker-open"]');
  f.reply('project.load', { project: { ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 3 }, ...LOADED_RULE_DEFAULTS }); await flush();
  f.reply('log.recent', { messages: [] }); await flush();
  f.store.setImportResult('gl', { batchId: 'gl-source', columns: ['Doc', 'Amount'], rowCount: 2 });
  f.store.setImportResult('tb', { batchId: 'tb-source', columns: ['Code', 'Amount'], rowCount: 2 });
  f.store.setMappingCommitted('gl', { mode: 'signed', mapping: { voucherNumber: 'Doc', amount: 'Amount' } });
  f.store.setMappingCommitted('tb', { mode: 'direct', mapping: { accountNumber: 'Code', changeAmount: 'Amount' } });
  f.store.setStepIndex(3);
  await flush();
  return f;
}

function validationData(resultId = 'v-operations') {
  return {
    resultRef: { resultId, generatedUtc: '2026-01-01T00:00:00Z' },
    stats: { glRowCount: 2, voucherCount: 1, totalDebit: 10, totalCredit: 10, net: 0, periodStart: '2025-01-01', periodEnd: '2025-12-31' },
    completenessTest: { diffAccountCount: 0, diffAccounts: [], storedTotals: { rowCountMatch: true, amountMatch: true }, naReason: null,
      eligibility: { isEligible: true, reason: null, warning: null } },
    voucherBalanceTest: { unbalancedVoucherCount: 0 },
    infSamplingTest: { sampleSize: 0 },
    blankRecordsTest: { blankVoucherNumberCount: 0, blankAccountNumberCount: 0, blankDescriptionCount: 0, approvalDateNotInPeriodCount: 0, blankRows: [] },
    sourceQuality: { findingCount: 0, sampleRows: [] }
  };
}

// ---- 長作業、忙碌中再按與取消 ----

test('validation and the prescreen show a monotonic elapsed time and the long-operation guidance, an import shows no guidance, and everything clears afterwards', async () => {
  const f = await openCaseAtValidation();
  f.click('[data-action="run-validate"]');
  const run = f.request('validate.run');
  // 抽樣筆數取自畫面上的輸入。
  assert.deepEqual(plain(run.payload), { sampleSize: 59 });
  // 忙碌畫面的作業名稱由 jet-api.js 送出請求時的事件帶入：這時才知道是資料驗證，提示句與取消鈕跟著出現。
  assert.equal(f.state().busy, true);
  assert.equal(f.state().activeRequestId, run.requestId);
  assert.match(f.overlay.label(), /驗證/);
  // 長作業提示。
  assert.equal(f.overlay.guidance(), '資料量大時需要較長時間，可按「取消作業」中止。');
  assert.match(f.overlay.elapsed(), /00:00/);
  assert.deepEqual(f.overlay.cancel(), { text: '取消作業', disabled: false });
  // 遮罩延後約 200 毫秒才顯示，快的查詢不閃一下。
  assert.equal(f.overlay.shown(), false);
  f.timers.run();
  assert.equal(f.overlay.shown(), true);
  // 經過時間每秒更新一次，讀的是單調時鐘（performance.now），不是會被系統時間調整影響的 Date.now。
  const ticking = f.liveIntervals();
  assert.equal(ticking.length, 1);
  assert.equal(ticking[0].ms, 1000);
  f.clock.advance(65000); ticking[0].fn();
  assert.match(f.overlay.elapsed(), /01:05/);
  // 驗證回來後依序產生驗證報告與科目配對檔，整個作業結束才解除忙碌。
  f.reply(run, validationData()); await flush();
  f.reply('export.validationReports', { publishedReports: [], reports: [] }); await flush();
  assert.equal(f.state().busy, true, 'still busy while the account mapping file is prepared');
  f.reply('export.accountMappingTemplate', { disposition: 'kept', fileName: '合成科目配對.xlsx' }); await flush();
  assert.equal(f.state().busy, false);
  assert.equal(f.overlay.shown(), false);
  assert.ok(f.overlay.elapsed() === null && f.overlay.guidance() === null && f.overlay.cancel() === null, 'elapsed time, guidance and cancel are hidden');
  assert.equal(f.$('[data-bind="busy-elapsed"]').textContent, '');
  assert.equal(f.liveIntervals().length, 0, 'the elapsed clock stops');
  // 成功時只留一則完成句，耗時接在後面，寫成幾分幾秒。
  assert.deepEqual(f.messages().filter(m => /^資料驗證完成/.test(m.text)), [{ text: '資料驗證完成，耗時 1 分 5 秒。', level: 'info' }]);

  // 預篩選也是長作業，同一句提示。
  f.click('[data-action="run-prescreen"]');
  const prescreen = f.request('prescreen.run');
  assert.equal(f.overlay.guidance(), '資料量大時需要較長時間，可按「取消作業」中止。');
  f.clock.advance(4200);
  f.reply(prescreen, plain(remapPrescreen('p-operations'))); await flush();
  assert.deepEqual(f.messages()[0], { text: '預篩選已更新，耗時 4.2 秒。', level: 'info' });
  assert.equal(f.overlay.guidance(), null);

  // 匯入不是資料驗證或預篩選：經過時間照樣顯示，但沒有那句提示。
  // 送出的資料照第二步實際送的格式寫（import-step.js），support/action-contract.cjs
  // 依 tests/JET.Tests/Fixtures/action-shapes.json 核對前端送出的資料形狀時才收得下。
  f.ui.run('匯入 PBC-GL', () => f.api.importGlFromFile({ mode: 'replace', sources: [{ filePath: 'synthetic.xlsx', fileName: 'synthetic.xlsx', sheetName: 'GL' }] }));
  const importing = f.request('import.gl.fromFile');
  assert.equal(f.state().activeRequestId, importing.requestId);
  assert.match(f.overlay.elapsed(), /00:00/);
  assert.equal(f.overlay.guidance(), null);
  assert.notEqual(f.overlay.cancel(), null, 'an import can be cancelled');
  f.reply(importing, {}); await flush();
  assert.equal(f.overlay.elapsed(), null);
});

// 取消鈕只跟著可取消的作業出現。「開啟資料夾」不能取消，
// 進行中不顯示取消鈕；執行驗證可以取消，上一個情境已核對它會顯示。
test('opening the case folder cannot be cancelled, so no cancel button appears while it runs', async () => {
  const f = await openCaseAtValidation();
  f.$('[data-action="open-project-folder"]').dispatch('click');
  const open = f.request('host.openFolder');
  assert.equal(f.state().busy, true);
  assert.equal(f.state().activeRequestId, null);
  f.reply(open, {}); await flush();
  assert.equal(f.state().busy, false);
});

// 驗證已完成並保存後，在產生報告時被取消，
// 訊息要說已完成的部分仍保留，不只寫「已取消」；之後的科目配對檔也不會產生。
test('cancelling while the validation reports are produced keeps the finished validation and says so', async () => {
  const f = await openCaseAtValidation();
  f.click('[data-action="run-validate"]');
  f.reply(f.request('validate.run'), validationData()); await flush();
  f.fail(f.request('export.validationReports'), { code: 'operation_cancelled', message: '合成：已取消' }); await flush();
  assert.equal(f.state().busy, false);
  assert.ok(f.messages().some(m => /^執行驗證並產生報告已取消，已完成的部分仍保留（耗時[^）]*）。$/.test(m.text)),
    'the cancellation says the finished validation is kept');
  assert.ok(f.state().results.validate, 'the finished validation stays');
  assert.equal(f.sent('export.accountMappingTemplate').length, 0);
});

// 前端不露出系統用詞：程式本身的錯誤（TypeError 這類）不把英文原文寫進訊息，
// 而是寫呼叫端的說明或非預期錯誤那一句；後端回的錯誤與前端自己寫的說明照原句。
test('a program error inside an operation is reported in Chinese, not with its English text', async () => {
  const f = await openCaseAtValidation();
  await f.ui.run('合成作業', () => { throw new TypeError("Cannot read properties of undefined (reading 'x')"); });
  await flush();
  const shown = f.messages().find(m => m.text.startsWith('合成作業失敗：'));
  assert.ok(shown, 'the failure reaches the message panel');
  assert.ok(shown.text.startsWith('合成作業失敗：發生非預期的錯誤，此操作未完成。請再試一次'), shown.text);
  assert.doesNotMatch(shown.text, /Cannot read|undefined/);
  assert.equal(f.ui.errorText(new Error('後端回的原因。')), '後端回的原因。');
  assert.equal(f.ui.errorText(new Error(''), '讀取失敗。'), '讀取失敗。');
  assert.equal(f.ui.errorText(new RangeError('Invalid array length'), '讀取失敗。'), '讀取失敗。');
  assert.equal(f.ui.errorText(null), '發生非預期的錯誤，此操作未完成。請再試一次。');
});

test('pressing another operation while one is running sends nothing, keeps the running name and says it is still running', async () => {
  const f = await openCaseAtValidation();
  f.click('[data-action="run-validate"]');
  const runningLabel = f.state().busyLabel;
  assert.match(runningLabel, /驗證/);
  const work = () => f.posted.filter(r => r.action !== 'log.append').length;
  const before = work();
  let factoryCalls = 0, refreshCalls = 0, secondSettled = false;
  const second = f.ui.run('合成第二項作業', () => { factoryCalls++; return f.api.systemPing({}); }, { refresh: () => { refreshCalls++; } });
  // 被擋下的作業立刻結束；不 await 它，免得作業沒被擋下時測試一直等那個永遠不會回應的請求。
  second.then(() => { secondSettled = true; }, () => { secondSettled = true; });
  // 頁首的「開啟資料夾」也走同一個入口。
  f.$('[data-action="open-project-folder"]').dispatch('click');
  await flush();
  assert.equal(secondSettled, true, 'the blocked operation returns at once');
  assert.equal(factoryCalls, 0);
  assert.equal(refreshCalls, 0);
  assert.equal(work(), before, 'nothing else was sent besides writing the message log');
  assert.equal(f.state().busyLabel, runningLabel);
  // 知道是哪項作業時寫出作業名稱，屬一般說明；訊息分三級，只有「JET 回覆另一項作業還在進行」是需要注意。
  // 作業名稱本身只比對意思。
  const stillRunning = '「' + runningLabel + '」還在進行，請等它完成後再試。';
  assert.deepEqual(f.messages().slice(0, 2), [{ text: stillRunning, level: 'info' }, { text: stillRunning, level: 'info' }]);
  // 被擋下的按鈕沒有開始，不在目前步驟加提示框。
  assert.equal(f.state().operationFailure, null);
});

test('a refresh runs once after the operation is no longer busy, only on success when asked, and a failed refresh is its own message', async () => {
  const f = await boot([CASE]);
  const seen = [];
  // 預設成功或失敗都整理；整理在解除忙碌之後，所以整理裡再開一個作業也能送出。
  // 整理用的請求是 system.whoAmI；啟動時已送過一次，所以這裡是第二次。
  let done = f.ui.run('合成作業', () => f.api.systemPing({}), {
    refresh: () => { seen.push({ busy: f.state().busy }); return f.ui.run('合成整理', () => f.api.systemWhoAmI({})); } });
  const pings = f.sent('system.ping');
  f.reply(pings.at(-1), {}); await flush();
  assert.deepEqual(seen, [{ busy: false }]);
  const nested = f.sent('system.whoAmI');
  assert.equal(nested.length, 2, 'the refresh itself sent its request');
  assert.equal(f.state().busyLabel, '合成整理');
  f.reply(nested.at(-1), {}); await done; await flush();
  assert.equal(seen.length, 1, 'refreshed exactly once');

  done = f.ui.run('合成作業', () => f.api.systemPing({}), { refresh: () => { seen.push('settled'); } });
  f.fail(f.sent('system.ping').at(-1), { code: 'synthetic', message: '合成失敗' }); await done;
  assert.equal(seen.at(-1), 'settled', 'by default a failed operation still refreshes');

  done = f.ui.run('合成作業', () => f.api.systemPing({}), { refresh: () => { seen.push('success only'); }, refreshWhen: 'success' });
  f.fail(f.sent('system.ping').at(-1), { code: 'synthetic', message: '合成失敗' }); await done;
  assert.notEqual(seen.at(-1), 'success only', 'a success-only refresh does not run after a failure');
  done = f.ui.run('合成作業', () => f.api.systemPing({}), { refresh: () => { seen.push('success only'); }, refreshWhen: 'success' });
  f.reply(f.sent('system.ping').at(-1), {}); await done;
  assert.equal(seen.at(-1), 'success only');

  // 整理失敗另外說明，不改寫主作業的結果；有 refreshLabel 時用它當名稱。
  const result = await (() => {
    const pending = f.ui.run('合成作業', () => f.api.systemPing({}).then(() => '主作業結果'),
      { refresh: () => Promise.reject(new Error('合成整理失敗')), refreshLabel: '重新讀取合成清單' });
    f.reply(f.sent('system.ping').at(-1), {});
    return pending;
  })();
  assert.equal(result, '主作業結果');
  assert.deepEqual(f.messages()[0], { text: '重新讀取合成清單失敗：合成整理失敗', level: 'error' });
  const unnamed = f.ui.run('合成作業', () => f.api.systemPing({}), { refresh: () => Promise.reject(new Error('合成整理失敗')) });
  f.reply(f.sent('system.ping').at(-1), {}); await unnamed;
  assert.match(f.messages()[0].text, /失敗：合成整理失敗$/);
  assert.equal(f.messages()[0].level, 'error');
});

// 取消中的按鈕狀態在開案時核對。
test('opening a case can be cancelled with the request that is actually running', async () => {
  const f = await boot([CASE]);
  // 背景的訊息紀錄不是可以取消的作業，不能搶走開案這個請求。
  f.click('[data-action="picker-open"]');
  const load = f.request('project.load');
  f.api.logRecent({});
  assert.deepEqual(f.overlay.cancel(), { text: '取消作業', disabled: false });
  assert.equal(f.state().activeRequestId, load.requestId);
  f.click('[data-action="cancel-operation"]');
  assert.deepEqual(plain(f.request('operation.cancel').payload), { requestId: load.requestId });
  assert.deepEqual(f.overlay.cancel(), { text: '取消中…', disabled: true });
  f.reply('operation.cancel', { requested: true }); await flush();
  f.fail(load, { code: 'operation_cancelled', message: '合成：已取消' }); await flush();
  assert.equal(f.state().busy, false);
});

test('cancelling records the operation and its progress at that moment, a finished step keeps its result, and a failed cancel can be pressed again', async () => {
  const f = await boot([CASE]);
  // 送出的資料照第六步實際送的欄位寫（export-step.js），support/action-contract.cjs
  // 依 tests/JET.Tests/Fixtures/action-shapes.json 核對前端送出的資料形狀時才收得下。
  const running = () => f.ui.run('產生工作底稿', () => f.api.exportWorkingPaperStream({ validationResultId: 'v-operations', scenarioRevision: 'r1', scenarioPositions: [1] }));
  // 取消被接受：記下作業名稱與當時進度。進度接進訊息時拿掉「工作底稿：」的冒號。
  let done = running();
  f.emit('export.progress', { reportKind: 'workingPaper', phase: 'writingSheet', sheetName: '合成表', sheetsCompleted: 1, rowsWritten: 1500, elapsedMilliseconds: 4200 });
  f.click('[data-action="cancel-operation"]');
  f.reply('operation.cancel', { requested: true }); await flush();
  assert.deepEqual(f.messages()[0], {
    text: '已要求取消「產生工作底稿」，當時進度：工作底稿正在寫入「合成表」，已完成 1 張工作表、寫入 1,500 列，經過 4.2 秒。', level: 'info' });
  assert.equal(f.state().busy, true, 'the operation is still settling');
  f.clock.advance(4300);
  f.fail(f.request('export.workingPaperStream'), { code: 'operation_cancelled', message: '合成：已取消' }); await done;
  // 最後進度不重複經過時間，括號後有句號。
  assert.deepEqual(f.messages()[0], {
    text: '產生工作底稿已取消（耗時 4.3 秒，最後進度：工作底稿正在寫入「合成表」，已完成 1 張工作表、寫入 1,500 列）。', level: 'info' });
  assert.equal(f.state().operationFailure, null, 'cancelling is not a failure notice');

  // 按下取消時目前的動作剛好做完。沒有進度時不寫當時進度。
  done = running();
  f.click('[data-action="cancel-operation"]');
  f.reply('operation.cancel', { requested: false }); await flush();
  assert.deepEqual(f.messages()[0], { text: '目前的動作已完成，結果已保留；尚未開始的後續作業不會執行。', level: 'info' });
  f.reply(f.request('export.workingPaperStream'), {}); await done;

  // 取消失敗：按鈕回到可以按，再按一次會再送一次取消。
  done = running();
  f.click('[data-action="cancel-operation"]');
  const firstCancel = f.request('operation.cancel');
  f.fail(firstCancel, { code: 'synthetic', message: '合成：取消沒有送達' }); await flush();
  assert.deepEqual(f.messages()[0], { text: '取消作業失敗：合成：取消沒有送達', level: 'error' });
  assert.deepEqual(f.overlay.cancel(), { text: '取消作業', disabled: false });
  f.click('[data-action="cancel-operation"]');
  const secondCancel = f.request('operation.cancel');
  assert.ok(secondCancel !== firstCancel, 'a second cancel request is sent');
  assert.deepEqual(plain(secondCancel.payload), { requestId: f.request('export.workingPaperStream').requestId });
  f.reply(secondCancel, { requested: true }); await flush();
  // 沒有進度時沒有「當時進度」。
  assert.deepEqual(f.messages()[0], { text: '已要求取消「產生工作底稿」。', level: 'info' });
  f.fail(f.request('export.workingPaperStream'), { code: 'operation_cancelled', message: '合成：已取消' }); await done;

  // 作業沒有名稱時，取消訊息仍寫出一個名稱，不留空的引號；名稱本身只比對意思。
  done = f.ui.run('', () => f.api.exportWorkingPaperStream({ validationResultId: 'v-operations', scenarioRevision: 'r1', scenarioPositions: [1] }));
  f.click('[data-action="cancel-operation"]');
  f.reply(f.request('operation.cancel'), { requested: true }); await flush();
  assert.match(f.messages()[0].text, /^已要求取消「[^」]+」。$/);
  f.fail(f.request('export.workingPaperStream'), { code: 'operation_cancelled', message: '合成：已取消' }); await done;
});

// ---- 取消鈕對準哪一個請求；取消與「另一項作業還在進行」不算失敗 ----

// 從案件清單開案到指定的步驟；arrange 用 store 佈置那一步需要的前置狀態。
async function openCaseAt(stepIndex, arrange) {
  const f = await boot([CASE]);
  f.click('[data-action="picker-open"]');
  f.reply('project.load', { project: { ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: stepIndex } }); await flush();
  f.reply('log.recent', { messages: [] }); await flush();
  if (arrange) { arrange(f); f.store.setStepIndex(stepIndex); await flush(); }
  return f;
}
// 目前步驟的內容；按鈕要先找到、可以按，才按下去。
function stepBody(f) { const node = f.$('.stepflow-item--current .stepflow-item__body'); assert.ok(node !== null, 'the current step is shown'); return node; }
function press(f, selector) {
  const node = stepBody(f).querySelector(selector);
  assert.ok(node !== null, selector + ' rendered'); assert.equal(node.disabled, false, selector + ' enabled');
  node.dispatch('click');
}
// 瀏覽器的 form 可以用欄位名稱直接取到欄位（form.projectName）；假 DOM 沒有，這裡補上。
function namedForm(form) {
  assert.ok(form !== null, 'form rendered');
  for (const node of form.querySelectorAll('input,select')) { const name = node.getAttribute('name'); if (name) form[name] = node; }
  return form;
}
// 後端用這兩種錯誤代碼回覆時，操作都不算失敗：使用者自己取消，或另一項作業還在進行。
// 訊息面板各記一則，等級分別是一般說明與需要注意，都不是錯誤。
const CANCELLED = { code: 'operation_cancelled', message: '合成：已取消' };
const OTHER_RUNNING = { code: 'operation_in_progress', message: '合成：另一項作業還在進行' };
const NOT_FAILURES = [{ name: 'cancelled', error: CANCELLED, level: 'info' }, { name: 'another operation running', error: OTHER_RUNNING, level: 'warn' }];
const noErrorLine = (f, label) => assert.deepEqual(f.messages().filter(m => m.level === 'error'), [], label + ': nothing is logged as an error');

test('the cancel button goes away once the request it cancels has answered, also when cancel was pressed just as it finished', async () => {
  // 開案先載入案件，再讀訊息紀錄；第二段不能取消，取消鈕收起，不再對準已經結束的載入。
  let f = await boot([CASE]);
  f.click('[data-action="picker-open"]');
  let load = f.request('project.load');
  assert.deepEqual(f.overlay.cancel(), { text: '取消作業', disabled: false });
  f.reply(load, { project: { ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 0 } }); await flush();
  assert.equal(f.sent('log.recent').length, 1, 'the message log is being read');
  assert.equal(f.state().busy, true);
  assert.equal(f.overlay.cancel(), null, 'no cancel button while only the message log is read');
  f.reply('log.recent', { messages: [] }); await flush();
  assert.equal(f.state().busy, false);

  // 按下取消時載入剛好做完：按鈕不停在「取消中…」，訊息照舊說明動作已完成。
  f = await boot([CASE]);
  f.click('[data-action="picker-open"]');
  load = f.request('project.load');
  f.click('[data-action="cancel-operation"]');
  assert.deepEqual(f.overlay.cancel(), { text: '取消中…', disabled: true });
  f.reply(load, { project: { ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 0 } }); await flush();
  assert.equal(f.overlay.cancel(), null, 'the button does not stay at 取消中… for a request that has finished');
  f.reply('operation.cancel', { requested: false }); await flush();
  assert.deepEqual(f.messages()[0], { text: '目前的動作已完成，結果已保留；尚未開始的後續作業不會執行。', level: 'info' });
  f.reply('log.recent', { messages: [] }); await flush();
  assert.equal(f.state().busy, false);
  assert.equal(f.state().view, 'workflow', 'the case that finished loading is open');
});

// 「執行驗證」之後接著產生報告。按下取消時驗證剛好做完，取消鈕收起，但這次按過取消仍然算數：還沒開始的報告不產生。
test('a cancel pressed just as the validation finishes still stops the reports that have not started', async () => {
  const f = await openCaseAtValidation();
  press(f, '[data-action="run-validate"]');
  const run = f.request('validate.run');
  f.click('[data-action="cancel-operation"]');
  f.reply('operation.cancel', { requested: false }); await flush();
  f.reply(run, validationData()); await flush();
  assert.equal(f.state().busy, false);
  assert.equal(f.sent('export.validationReports').length, 0, 'the reports are not started');
  assert.equal(f.sent('export.accountMappingTemplate').length, 0);
  assert.ok(f.state().results.validate, 'the finished validation stays');
  assert.ok(f.messages().some(m => /已取消，已完成的部分仍保留/.test(m.text) && m.level === 'info'), 'the message says what was kept');
  noErrorLine(f, 'late cancel');
});

// 選擇授權編製人員清單會連續送出兩個可以取消的請求：先檢視檔案，再讀前 10 列。
test('when the next cancellable request starts, the cancel button is back to 取消作業 and cancels that request; a cancelled preview is not a read failure', async () => {
  const f = await openCaseAt(1);
  press(f, '[data-task-toggle="authorizedPreparer"]'); await flush();
  press(f, '[data-action="import-authorized-preparer"]');
  f.reply('host.selectFile', { filePath: 'C:/synthetic/preparers.xlsx', fileName: 'preparers.xlsx' }); await flush();
  const inspect = f.request('import.inspectFile');
  f.click('[data-action="cancel-operation"]');
  const firstCancel = f.request('operation.cancel');
  assert.deepEqual(plain(firstCancel.payload), { requestId: inspect.requestId });
  // 取消送到時檢視剛好做完，接著開始讀前 10 列。
  f.reply(firstCancel, { requested: false }); await flush();
  f.reply(inspect, { fileType: 'excel', worksheets: [{ name: '合成表', columns: ['Id'], rowCountEstimate: 3 }] }); await flush();
  const preview = f.request('import.previewFile');
  assert.deepEqual(f.overlay.cancel(), { text: '取消作業', disabled: false });
  f.click('[data-action="cancel-operation"]');
  const secondCancel = f.request('operation.cancel');
  assert.ok(secondCancel !== firstCancel, 'a cancel is sent for the new request');
  assert.deepEqual(plain(secondCancel.payload), { requestId: preview.requestId });
  assert.deepEqual(f.overlay.cancel(), { text: '取消中…', disabled: true });
  f.reply(secondCancel, { requested: true }); await flush();
  f.fail(preview, CANCELLED); await flush();
  assert.equal(f.state().busy, false);
  // 讀前 10 列被取消不是讀檔失敗：不寫「來源檔預覽失敗」，卡片回到選檔前的樣子，沒有停在載入中的草稿。
  const card = stepBody(f).querySelector('[data-bind="import-card-authorized-preparer"]');
  assert.doesNotMatch(card.textContent, /來源檔預覽失敗|載入預覽中/);
  assert.ok(card.querySelector('[data-bind="authorized-preparer-draft"]') === null, 'no half-read draft is left');
  noErrorLine(f, 'cancelled preview');
});

// 換工作表與「重讀來源檔預覽」被取消或遇到另一項作業時，留著原本讀到的預覽。
test('re-reading the authorised preparer preview keeps the earlier preview when it is cancelled or another operation is running', async () => {
  const f = await openCaseAt(1);
  press(f, '[data-task-toggle="authorizedPreparer"]'); await flush();
  press(f, '[data-action="import-authorized-preparer"]');
  f.reply('host.selectFile', { filePath: 'C:/synthetic/preparers.xlsx', fileName: 'preparers.xlsx' }); await flush();
  f.reply('import.inspectFile', { fileType: 'excel', worksheets: [{ name: '合成甲表', columns: ['Id'], rowCountEstimate: 3 }, { name: '合成乙表', columns: ['Name'], rowCountEstimate: 3 }] }); await flush();
  f.reply('import.previewFile', { columns: ['Id'], sampleRows: [['合成人員甲']], notices: [] }); await flush();
  const draft = () => stepBody(f).querySelector('[data-bind="authorized-preparer-draft"]');
  assert.match(draft().textContent, /合成人員甲/);
  const reads = () => f.sent('import.previewFile').length;

  let before = reads();
  press(f, '[data-action="retry-authorized-preparer-preview"]');
  assert.equal(reads(), before + 1);
  f.fail(f.sent('import.previewFile').at(-1), OTHER_RUNNING); await flush();
  assert.match(draft().textContent, /合成人員甲/, 'the earlier preview is still shown');
  assert.doesNotMatch(draft().textContent, /來源檔預覽失敗|載入預覽中/);

  before = reads();
  const sheet = draft().querySelector('[data-ap-sheet]');
  sheet.value = '合成乙表'; sheet.dispatch('change');
  assert.equal(reads(), before + 1);
  f.fail(f.sent('import.previewFile').at(-1), CANCELLED); await flush();
  assert.match(draft().textContent, /合成人員甲/, 'the first sheet and its preview are back');
  assert.doesNotMatch(draft().textContent, /來源檔預覽失敗|載入預覽中/);
  assert.equal(draft().querySelector('[data-ap-sheet] option[selected]').getAttribute('value'), '合成甲表');
  noErrorLine(f, 'preparer preview');
});

test('on the case list, cancelling the open or being told another operation is running shows no error box', async () => {
  const noBox = (f, label) => assert.ok(f.$('.picker-feedback') === null, label + ': no error box above the case list');
  for (const { name, error, level } of NOT_FAILURES) {
    const f = await boot([CASE]);
    f.click('[data-action="picker-open"]');
    f.fail(f.request('project.load'), error); await flush();
    assert.equal(f.state().busy, false, name);
    assert.equal(f.state().view, 'picker', name + ': still on the case list');
    noBox(f, name + ', opening');
    assert.equal(f.messages()[0].level, level, name);
    noErrorLine(f, name + ', opening');
  }
  // 重新整理、刪除與輸出支援日誌都不能取消，只會遇到另一項作業還在進行。
  const f = await boot([CASE]);
  f.click('[data-action="picker-refresh-local"]');
  f.fail(f.sent('project.listLocal').at(-1), OTHER_RUNNING); await flush();
  noBox(f, 'refreshing');
  assert.ok(f.$('[data-action="picker-open"]') !== null, 'the listed case is still there');
  f.click('[data-action="picker-delete"]');
  f.reply('project.deletePreview', { reportCount: 0, workingPaperCount: 0 }); await flush();
  const listsBefore = f.sent('project.listLocal').length;
  f.click('[data-action="modal-confirm"]');
  f.fail('project.delete', OTHER_RUNNING); await flush();
  noBox(f, 'deleting');
  assert.equal(f.sent('project.listLocal').length, listsBefore, 'the list is not reloaded');
  assert.equal(f.state().pickerFeedbackContext, null, 'no failure is recorded for the case');
  f.store.setDevToolsEnabled(true); await flush();
  f.click('[data-action="picker-support-export"]');
  f.fail('support.log.export', OTHER_RUNNING); await flush();
  noBox(f, 'exporting the support log');
  noErrorLine(f, 'case list');
  assert.deepEqual(f.messages()[0], { text: '另一項作業還在進行，請等它完成後再試。', level: 'warn' });

  // Debug 組建的診斷日誌匯出：開案真的失敗後按它，遇到另一項作業還在進行時，錯誤框裡原本的開案失敗原因照樣留著。
  const g = await boot([CASE]);
  g.store.setDevToolsEnabled(true); await flush();
  g.click('[data-action="picker-open"]');
  g.fail(g.request('project.load'), { code: 'synthetic', message: '合成：案件資料庫打不開' }); await flush();
  const openFailure = g.$('.picker-feedback__body').textContent;
  assert.match(openFailure, /^開啟案件失敗：合成：案件資料庫打不開/);
  g.click('[data-action="picker-dev-log-export"]');
  g.fail(g.request('dev.log.exportFile'), OTHER_RUNNING); await flush();
  assert.equal(g.$('.picker-feedback__body').textContent, openFailure, 'the open failure is still what the error box says');
});

test('creating a case and saving the case details leave their error boxes empty when cancelled or another operation is running', async () => {
  for (const { name, error } of NOT_FAILURES) {
    // 建立案件：案件已建立，接著的開案被取消或遇到另一項作業。JET 放掉這個案件，回到案件清單，清單上方沒有錯誤框。
    const f = await boot([]);
    f.click('[data-action="picker-new"]'); await flush();
    const form = namedForm(stepBody(f).querySelector('[data-bind="create-form"]'));
    form.projectName.value = '合成新案'; form.periodStart.value = '2025-01-01'; form.periodEnd.value = '2025-12-31';
    form.dispatch('submit', { target: form });
    f.reply('project.create', { projectId: '合成新案', warnings: [] }); await flush();
    f.fail(f.request('project.load'), error); await flush();
    f.reply('project.releaseLock', {}); await flush();
    f.reply('project.listLocal', { projects: [{ projectId: '合成新案', databaseProvider: 'sqlite' }] }); await flush();
    assert.equal(f.state().busy, false, name);
    assert.equal(f.state().view, 'picker', name + ': back on the case list');
    assert.ok(f.$('.picker-feedback') === null, name + ': no error box above the case list');
    noErrorLine(f, name + ', creating');
  }
  // 儲存案件資料不能取消，只會遇到另一項作業還在進行。
  const f = await openCaseAt(0);
  press(f, '[data-action="edit-project-metadata"]'); await flush();
  const form = namedForm(stepBody(f).querySelector('[data-bind="project-update-form"]'));
  form.clientName.value = '合成客戶';
  form.dispatch('submit', { target: form });
  f.fail(f.request('project.update'), OTHER_RUNNING); await flush();
  const box = stepBody(f).querySelector('[data-bind="project-update-error"]');
  assert.ok(box !== null, 'the edit form is still open');
  assert.equal(box.hidden, true, 'the edit form shows no error');
  assert.equal(stepBody(f).querySelector('[data-bind="project-update-form"] [name="clientName"]').value, '合成客戶', 'what was typed is kept');
  noErrorLine(f, 'saving the case details');
});

test('confirming the field mapping shows no 配對未完成 error when it is cancelled or another operation is running', async () => {
  for (const { name, error, level } of NOT_FAILURES) {
    const f = await openCaseAt(2, f => {
      f.store.setImportResult('gl', { batchId: 'gl-source', columns: ['Doc', 'Date', 'Account', 'Name', 'Memo', 'Amount', 'Side'], rowCount: 2 });
      f.store.setMappingMode('gl', 'side');
      f.store.replaceMappingDraft('gl', { voucherNumber: 'Doc', glDate: 'Date', accountNumber: 'Account', accountName: 'Name', description: 'Memo', amount: 'Amount', drCr: 'Side', drCrDebitCode: 'D' });
    });
    press(f, '[data-action="commit-gl"]');
    f.fail(f.request('mapping.commit.gl'), error); await flush();
    assert.equal(f.state().busy, false, name);
    assert.ok(stepBody(f).querySelector('[data-bind="mapping-commit-error-gl"]') === null, name + ': no error box in the mapping section');
    assert.doesNotMatch(stepBody(f).textContent, /配對未完成/, name);
    assert.equal(stepBody(f).querySelector('[data-action="commit-gl"]').disabled, false, name + ': the mapping can be confirmed again');
    assert.equal(f.state().mapping.gl.committed, null, name + ': nothing was confirmed');
    assert.equal(f.messages()[0].level, level, name);
    noErrorLine(f, name + ', confirming the mapping');

    // 上一次確認真的失敗、原因還在畫面上時再按一次：被取消或遇到另一項作業，原本的原因照樣留著。
    press(f, '[data-action="commit-gl"]');
    f.fail(f.request('mapping.commit.gl'), { code: 'synthetic', message: '合成：日期欄無法轉換。' }); await flush();
    const box = () => stepBody(f).querySelector('[data-bind="mapping-commit-error-gl"]');
    assert.ok(box() !== null, 'a real failure is shown in the mapping section');
    assert.match(box().textContent, /配對未完成：合成：日期欄無法轉換。/);
    press(f, '[data-action="commit-gl"]');
    f.fail(f.request('mapping.commit.gl'), error); await flush();
    assert.ok(box() !== null, name + ': the earlier problem is still shown');
    assert.match(box().textContent, /配對未完成：合成：日期欄無法轉換。/, name);
    assert.doesNotMatch(box().textContent, /已取消|另一項作業/, name);
  }
});

test('re-reading a source in the import list keeps the row ticked and its settings when cancelled or another operation is running', async () => {
  // 選一個文字檔，檢視成功後這一列預設勾選。
  async function pickTextFile() {
    const f = await openCaseAt(1);
    press(f, '[data-task-toggle="gl"]'); await flush();
    press(f, '[data-bind="import-card-gl"] [data-action="wizard-replace-gl"]');
    f.reply('host.selectFiles', { files: [{ filePath: 'C:/synthetic/gl.csv', fileName: 'gl.csv' }] }); await flush();
    return f;
  }
  const row = f => { const node = stepBody(f).querySelector('.pending-row'); assert.ok(node !== null, 'the source row is listed'); return node; };
  const rowState = f => ({
    ticked: row(f).querySelector('[data-pending-bind="include"]').checked,
    failed: /pending-row--error/.test(row(f).className),
    encoding: row(f).querySelector('[data-pending-bind="encoding"] option[selected]').getAttribute('value')
  });

  for (const { name, error } of NOT_FAILURES) {
    const f = await pickTextFile();
    f.reply('import.inspectFile', { fileType: 'csv', encoding: 'utf-8', delimiter: ',', columns: ['A', 'B'], notices: [] }); await flush();
    assert.deepEqual(rowState(f), { ticked: true, failed: false, encoding: 'utf-8' });
    // 改選編碼會重新讀前 10 列。
    const encoding = row(f).querySelector('[data-pending-bind="encoding"]');
    encoding.value = 'big5'; encoding.dispatch('change');
    assert.equal(plain(f.request('import.previewFile').payload).encoding, 'big5');
    f.fail(f.request('import.previewFile'), error); await flush();
    assert.equal(f.state().busy, false, name);
    // 這一列和改選前一樣：仍然勾選、不是讀不出檔案，編碼回到原本讀成功的那一個。
    assert.deepEqual(rowState(f), { ticked: true, failed: false, encoding: 'utf-8' }, name);
    assert.doesNotMatch(row(f).textContent, /讀不出這個檔案|載入預覽中/, name);
    assert.equal(stepBody(f).querySelector('[data-action="wizard-confirm"]').disabled, false, name + ': the source can still be imported');
    noErrorLine(f, name + ', re-reading');
  }

  // 已經讀不出的來源按「重新檢視」：被取消或遇到另一項作業時，留著原本的原因，不換成取消的說明。
  for (const { name, error } of NOT_FAILURES) {
    const f = await pickTextFile();
    f.fail(f.request('import.inspectFile'), { code: 'synthetic', message: '合成：編碼無法判斷。' }); await flush();
    assert.match(row(f).textContent, /讀不出這個檔案：合成：編碼無法判斷。/);
    press(f, '[data-action="pending-retry"]');
    f.fail(f.request('import.previewFile'), error); await flush();
    assert.match(row(f).textContent, /讀不出這個檔案：合成：編碼無法判斷。/, name + ': the earlier reason stays');
    assert.doesNotMatch(row(f).textContent, /已取消|另一項作業/, name);
  }

  // 活頁簿走另一條重新檢視的路，也一樣。
  const f = await openCaseAt(1);
  press(f, '[data-task-toggle="gl"]'); await flush();
  press(f, '[data-bind="import-card-gl"] [data-action="wizard-replace-gl"]');
  f.reply('host.selectFiles', { files: [{ filePath: 'C:/synthetic/gl.xlsx', fileName: 'gl.xlsx' }, { filePath: 'C:/synthetic/gl-2.xlsx', fileName: 'gl-2.xlsx' }] }); await flush();
  f.fail(f.sent('import.inspectFile').at(-1), { code: 'synthetic', message: '合成：檔案損壞。' }); await flush();
  // 逐檔檢視時遇到另一項作業還在進行：不把這個檔案列成讀不出，也不再讀後面的檔案。
  assert.equal(f.sent('import.inspectFile').length, 2);
  f.fail(f.sent('import.inspectFile').at(-1), OTHER_RUNNING); await flush();
  assert.equal(f.state().busy, false);
  assert.equal(stepBody(f).querySelectorAll('.pending-row').length, 1, 'only the file that really failed is listed as unreadable');
  press(f, '[data-action="pending-retry"]');
  assert.equal(f.sent('import.inspectFile').length, 3);
  f.fail(f.sent('import.inspectFile').at(-1), CANCELLED); await flush();
  assert.match(row(f).textContent, /讀不出這個檔案：合成：檔案損壞。/);
  assert.doesNotMatch(row(f).textContent, /已取消/);
});

test('the validation reports and the account mapping template are not marked 尚未產生 when cancelled or another operation is running', async () => {
  for (const { name, error, level } of NOT_FAILURES) {
    const f = await openCaseAtValidation();
    f.store.setResult('validate', validationData()); await flush();
    press(f, '[data-action="export-validation-reports"]');
    f.fail(f.request('export.validationReports'), error); await flush();
    assert.equal(f.state().busy, false, name);
    assert.ok(stepBody(f).querySelector('[data-validation-output="reports"]') === null, name + ': no status line is left in the report block');
    assert.doesNotMatch(stepBody(f).textContent, /報告尚未產生：|正在產生報告/, name);
    assert.equal(f.state().operationFailure, null, name + ': no notice above the step');
    assert.equal(f.messages()[0].level, level, name);

    // 「建立科目配對範本」在「用 Excel 配對」分頁；回到「在 JET 配對」分頁也沒有留下失敗的狀態。
    press(f, '[data-action="toggle-account-excel"]'); await flush();
    press(f, '[data-action="ensure-account-mapping-template"]');
    f.fail(f.request('export.accountMappingTemplate'), error); await flush();
    assert.equal(f.state().busy, false, name);
    assert.ok(stepBody(f).querySelector('[data-validation-output="template"]') === null, name + ': no status line is left in the template block');
    // 開案後第一次建立範本，按下前這一區沒有任何狀態；取消後回到那個樣子，按鈕可以再按。
    assert.equal(f.state().validationOutput.template, null, name + ': the template block is back to how it was before the press');
    assert.equal(stepBody(f).querySelector('[data-action="ensure-account-mapping-template"]').disabled, false, name + ': the template can be built again');
    press(f, '[data-action="select-account-jet"]'); await flush();
    assert.ok(stepBody(f).querySelector('[data-account-template-status]') === null, name + ': no template status on the other tab');
    assert.doesNotMatch(stepBody(f).textContent, /範本尚未產生|科目配對範本尚未準備好|正在建立科目配對範本/, name);
    noErrorLine(f, name + ', validation outputs');
  }

  // 執行驗證之後接著產生報告時遇到另一項作業還在進行：驗證結果保留，不說部分檔案尚未產生，也不再送科目配對範本。
  const f = await openCaseAtValidation();
  press(f, '[data-action="run-validate"]');
  f.reply(f.request('validate.run'), validationData()); await flush();
  f.fail(f.request('export.validationReports'), OTHER_RUNNING); await flush();
  assert.equal(f.state().busy, false);
  assert.ok(f.state().results.validate, 'the finished validation stays');
  assert.equal(f.sent('export.accountMappingTemplate').length, 0);
  assert.doesNotMatch(stepBody(f).textContent, /報告尚未產生：/);
  assert.deepEqual(f.messages()[0], { text: '另一項作業還在進行，請等它完成後再試。', level: 'warn' });
  noErrorLine(f, 'validation then reports');
});

// ---- 已經有作業在跑時再按一次，這次操作沒有開始，畫面維持原樣 ----

// 讓一項合成作業先佔著整個畫面的作業；回傳的函式讓它結束。
function holdAnotherOperation(f) {
  const held = f.ui.run('合成第一項作業', () => f.api.systemPing({}));
  const ping = f.sent('system.ping').at(-1);
  return async () => { f.reply(ping, {}); await held; await flush(); };
}
// 作業名稱是測試自己傳入的合成名稱。
const HELD_STILL_RUNNING = { text: '「合成第一項作業」還在進行，請等它完成後再試。', level: 'info' };

// 第二步打開 GL 的匯入精靈，選一個文字檔，檢視成功後列在待匯入清單。
async function glWizardWithTextFile() {
  const f = await openCaseAt(1);
  press(f, '[data-task-toggle="gl"]'); await flush();
  press(f, '[data-bind="import-card-gl"] [data-action="wizard-replace-gl"]');
  f.reply('host.selectFiles', { files: [{ filePath: 'C:/synthetic/gl.csv', fileName: 'gl.csv' }] }); await flush();
  f.reply('import.inspectFile', { fileType: 'csv', encoding: 'utf-8', delimiter: ',', columns: ['A', 'B'], notices: [] }); await flush();
  return f;
}
const glWizard = f => { const node = stepBody(f).querySelector('[data-bind="import-card-gl"] .wizard-pane'); assert.ok(node !== null, 'the GL wizard is open'); return node; };
const WIZARD_ACTIONS = ['wizard-pick', 'wizard-confirm', 'wizard-cancel'];
const wizardActions = f => WIZARD_ACTIONS.filter(action => glWizard(f).querySelector('[data-action="' + action + '"]') !== null);
const wizardProgress = f => { const node = glWizard(f).querySelector('[data-bind="wizard-progress"]'); return node.hidden ? null : node.querySelector('[data-bind="wizard-progress-label"]').textContent; };
const GL_PROGRESS = { kind: 'gl', sourceNo: 1, sourceCount: 1, fileName: 'gl.csv', sheetName: null, rowsRead: 20000 };
const glImported = { batchId: 'gl-one-file', rowCount: 3, addedRowCount: 3, columns: ['A', 'B'],
  sources: [{ sourceNo: 1, fileName: 'gl.csv', sheetName: null, encoding: 'utf-8', delimiter: ',', headerRow: null, rowCount: 3, importedUtc: '2026-11-09T08:00:00Z' }],
  warnings: [], openElsewhereWarnings: [], invalidatedResults: { validation: true, prescreen: true, filter: true, filterScenarios: true },
  staleState: { validation: true, prescreen: true, filter: false }, reports: [] };

test('while an import runs the wizard says 匯入中 and shows no buttons; a failed import brings the buttons back and stops following the progress', async () => {
  const f = await glWizardWithTextFile();
  assert.deepEqual(wizardActions(f), WIZARD_ACTIONS);
  assert.match(glWizard(f).querySelector('.wizard-pane__hint').textContent, /選擇一個或多個來源檔/);

  press(f, '[data-action="wizard-confirm"]');
  const request = f.request('import.gl.fromFile');
  assert.deepEqual(plain(request.payload), { mode: 'replace', sources: [{ filePath: 'C:/synthetic/gl.csv', fileName: 'gl.csv', encoding: 'utf-8', delimiter: ',' }] });
  // 匯入期間：橫幅換成「匯入中」，選檔、開始匯入與取消都收起。
  assert.equal(glWizard(f).querySelector('.wizard-pane__hint').textContent, '匯入中…請稍候，完成後會顯示匯入結果。');
  assert.deepEqual(wizardActions(f), [], 'no button can change the sources while the import runs');
  assert.equal(wizardProgress(f), null, 'no progress yet');
  f.emit('import.progress', GL_PROGRESS);
  assert.match(wizardProgress(f), /gl\.csv.*已寫入 20,000 列/);

  // 匯入失敗：按鈕回來，橫幅回到匯入前的說明，進度收起；之後的進度事件不再畫出來。
  f.fail(request, { code: 'synthetic', message: '合成：欄位名稱不一致。' }); await flush();
  assert.equal(f.state().busy, false);
  assert.deepEqual(wizardActions(f), WIZARD_ACTIONS, 'the sources can be corrected and imported again');
  assert.match(glWizard(f).querySelector('.wizard-pane__hint').textContent, /選擇一個或多個來源檔/);
  assert.equal(wizardProgress(f), null);
  f.emit('import.progress', GL_PROGRESS);
  f.store.touch(); await flush();
  assert.equal(wizardProgress(f), null, 'a finished import no longer follows the progress');
});

test('pressing 開始匯入 while another operation is running starts no import: the wizard keeps its buttons and follows no progress', async () => {
  const f = await glWizardWithTextFile();
  const finishHeld = holdAnotherOperation(f);
  press(f, '[data-action="wizard-confirm"]');
  assert.equal(f.sent('import.gl.fromFile').length, 0, 'nothing is imported');
  assert.deepEqual(f.messages()[0], HELD_STILL_RUNNING);
  // 之後畫面不論何時重畫，精靈都和按下前一樣。
  f.store.touch(); await flush();
  assert.deepEqual(wizardActions(f), WIZARD_ACTIONS, 'the wizard still has its buttons');
  assert.match(glWizard(f).querySelector('.wizard-pane__hint').textContent, /選擇一個或多個來源檔/);
  assert.doesNotMatch(glWizard(f).textContent, /匯入中/);
  // 沒有開始的匯入不跟著進度事件畫進度。
  f.emit('import.progress', GL_PROGRESS);
  assert.equal(wizardProgress(f), null, 'no progress is drawn for an import that never started');
  f.store.touch(); await flush();
  assert.equal(wizardProgress(f), null, 'and none appears on a later redraw');

  // 前一項作業結束後再按一次，照常匯入一次。
  await finishHeld();
  press(f, '[data-action="wizard-confirm"]');
  assert.equal(f.sent('import.gl.fromFile').length, 1);
  assert.deepEqual(wizardActions(f), []);
  f.reply('import.gl.fromFile', glImported); await flush();
  assert.equal(f.state().busy, false);
  assert.match(stepBody(f).querySelector('[data-bind="import-card-gl"]').textContent, /已匯入 3 列、2 欄（1 個來源）/);
});

test('expanding a source preview or changing its encoding, delimiter or header row while another operation is running leaves the source row as it was', async () => {
  const f = await glWizardWithTextFile();
  const row = () => { const node = stepBody(f).querySelector('.pending-row'); assert.ok(node !== null, 'the source row is listed'); return node; };
  const control = key => { const node = row().querySelector('[data-pending-bind="' + key + '"]'); assert.ok(node !== null, key + ' is shown'); return node; };
  const settings = () => ({ encoding: control('encoding').value, delimiter: control('delimiter').value, headerRow: control('headerRow').value });
  assert.deepEqual(settings(), { encoding: 'utf-8', delimiter: ',', headerRow: '1' });
  const finishHeld = holdAnotherOperation(f);

  // 展開預覽：沒有開始讀，這一列不停在「載入預覽中」。
  press(f, '[data-action="pending-preview"]');
  assert.equal(f.sent('import.previewFile').length, 0, 'the preview is not read');
  assert.doesNotMatch(row().textContent, /載入預覽中/);
  assert.equal(row().querySelector('[data-action="pending-preview"]').getAttribute('aria-expanded'), 'false');
  f.store.touch(); await flush();
  assert.doesNotMatch(row().textContent, /載入預覽中/, 'nor on a later redraw');

  // 改編碼、分隔符與欄名在第幾列：沒有重新讀取，畫面上的三格都回到原本的值。
  for (const [key, value] of [['encoding', 'big5'], ['delimiter', ';'], ['headerRow', '3']]) {
    const node = control(key);
    node.value = value; node.dispatch('change');
    assert.equal(f.sent('import.previewFile').length, 0, key + ': the source is not read again');
    assert.deepEqual(settings(), { encoding: 'utf-8', delimiter: ',', headerRow: '1' }, key + ' goes back to what it was');
    f.store.touch(); await flush();
    assert.deepEqual(settings(), { encoding: 'utf-8', delimiter: ',', headerRow: '1' }, key + ' is still unchanged after a redraw');
  }
  assert.equal(row().querySelector('[data-pending-bind="include"]').checked, true, 'the row is still ticked');
  assert.deepEqual(f.messages().slice(0, 4), [HELD_STILL_RUNNING, HELD_STILL_RUNNING, HELD_STILL_RUNNING, HELD_STILL_RUNNING]);

  // 前一項作業結束後匯入，送出的設定就是畫面上的設定。
  await finishHeld();
  press(f, '[data-action="wizard-confirm"]');
  assert.deepEqual(plain(f.request('import.gl.fromFile').payload),
    { mode: 'replace', sources: [{ filePath: 'C:/synthetic/gl.csv', fileName: 'gl.csv', encoding: 'utf-8', delimiter: ',' }] });
});

test('changing the worksheet of the authorised preparer list while another operation is running keeps the sheet and its preview', async () => {
  const f = await openCaseAt(1);
  press(f, '[data-task-toggle="authorizedPreparer"]'); await flush();
  press(f, '[data-action="import-authorized-preparer"]');
  f.reply('host.selectFile', { filePath: 'C:/synthetic/preparers.xlsx', fileName: 'preparers.xlsx' }); await flush();
  f.reply('import.inspectFile', { fileType: 'excel', worksheets: [{ name: '合成甲表', columns: ['Id'], rowCountEstimate: 3 }, { name: '合成乙表', columns: ['Name'], rowCountEstimate: 3 }] }); await flush();
  f.reply('import.previewFile', { columns: ['Id'], sampleRows: [['合成人員甲']], notices: [] }); await flush();
  const draft = () => { const node = stepBody(f).querySelector('[data-bind="authorized-preparer-draft"]'); assert.ok(node !== null, 'the draft is shown'); return node; };
  const shown = () => ({ sheet: draft().querySelector('[data-ap-sheet]').value,
    columns: draft().querySelectorAll('[data-ap-column] option').map(option => option.getAttribute('value')) });
  assert.deepEqual(shown(), { sheet: '合成甲表', columns: ['', 'Id'] });
  const reads = f.sent('import.previewFile').length;

  const finishHeld = holdAnotherOperation(f);
  const sheet = draft().querySelector('[data-ap-sheet]');
  sheet.value = '合成乙表'; sheet.dispatch('change');
  assert.equal(f.sent('import.previewFile').length, reads, 'the other sheet is not read');
  assert.deepEqual(f.messages()[0], HELD_STILL_RUNNING);
  for (const when of ['at once', 'after a redraw']) {
    assert.deepEqual(shown(), { sheet: '合成甲表', columns: ['', 'Id'] }, when + ': the first sheet and its columns are still chosen');
    assert.match(draft().textContent, /合成人員甲/, when + ': its preview is still shown');
    assert.doesNotMatch(draft().textContent, /載入預覽中|來源檔預覽失敗/, when);
    f.store.touch(); await flush();
  }
  await finishHeld();
});

test('ticking a weekly rest day keeps the boxes equal to the saved setting when the change does not go through', async () => {
  const f = await openCaseAt(1);
  press(f, '[data-task-toggle="calendar"]'); await flush();
  const box = day => { const node = stepBody(f).querySelector('[data-bind="nonworking-days"] input[data-weekday="' + day + '"]'); assert.ok(node !== null, 'weekday ' + day); return node; };
  const ticked = () => stepBody(f).querySelectorAll('[data-bind="nonworking-days"] input[data-weekday]').filter(node => node.checked).map(node => node.getAttribute('data-weekday'));
  assert.deepEqual(ticked(), ['6', '0'], 'Saturday and Sunday by default');

  // 已經有作業在跑時勾選星期一：沒有送出，勾選回到已存的設定。
  const finishHeld = holdAnotherOperation(f);
  box(1).checked = true; box(1).dispatch('change');
  assert.equal(f.sent('calendar.setNonWorkingDays').length, 0, 'nothing is saved');
  assert.deepEqual(f.messages()[0], HELD_STILL_RUNNING);
  assert.deepEqual(ticked(), ['6', '0'], 'the boxes show the saved setting again');
  await finishHeld();

  // 送出後被取消，或後端回覆另一項作業還在進行：一樣沒有存，勾選回到已存的設定。
  for (const { name, error } of NOT_FAILURES) {
    box(1).checked = true; box(1).dispatch('change');
    const save = f.request('calendar.setNonWorkingDays');
    assert.deepEqual(plain(save.payload), { days: [1, 6, 0] });
    f.fail(save, error); await flush();
    assert.equal(f.state().busy, false, name);
    assert.deepEqual(ticked(), ['6', '0'], name + ': the boxes show the saved setting again');
    assert.equal(f.state().importState.calendar, null, name + ': nothing was saved');
  }
  noErrorLine(f, 'weekly rest days');

  // 後端回覆一般失敗：沒有存，勾選回到已存的設定，步驟上方寫出原因（作業名稱只比對意思）。
  const notice = () => { const node = f.$('.stepflow-item__failure'); return node === null ? null : node.textContent; };
  box(1).checked = true; box(1).dispatch('change');
  const failed = f.request('calendar.setNonWorkingDays');
  assert.deepEqual(plain(failed.payload), { days: [1, 6, 0] });
  f.fail(failed, { code: 'synthetic', message: '合成：每週休息日存不進去。' }); await flush();
  assert.equal(f.state().busy, false);
  assert.equal(f.state().importState.calendar, null, 'a real failure: nothing was saved');
  assert.match(f.messages()[0].text, /每週休息日.*失敗：合成：每週休息日存不進去/);
  assert.equal(f.messages()[0].level, 'error');
  for (const when of ['at once', 'after a redraw']) {
    assert.deepEqual(ticked(), ['6', '0'], 'a real failure, ' + when + ': the boxes show the saved setting again');
    assert.match(notice(), /失敗：合成：每週休息日存不進去。$/, when + ': the step says why');
    f.store.touch(); await flush();
  }

  // 步驟失敗提示還在畫面上時再勾選星期一，送出後被取消：提示拿掉，沒有存，勾選回到已存的設定。
  const saves = f.sent('calendar.setNonWorkingDays').length;
  box(1).checked = true; box(1).dispatch('change');
  assert.equal(f.sent('calendar.setNonWorkingDays').length, saves + 1, 'the change is sent');
  const cancelled = f.request('calendar.setNonWorkingDays');
  assert.deepEqual(plain(cancelled.payload), { days: [1, 6, 0] });
  assert.equal(notice(), null, 'sending takes the earlier step notice away');
  f.fail(cancelled, CANCELLED); await flush();
  assert.equal(f.state().busy, false);
  assert.equal(f.state().importState.calendar, null, 'cancelled after a failure: nothing was saved');
  for (const when of ['at once', 'after a redraw']) {
    assert.deepEqual(ticked(), ['6', '0'], 'cancelled after a failure, ' + when + ': the boxes show the saved setting again');
    assert.equal(notice(), null, when + ': a cancelled change leaves no step notice');
    f.store.touch(); await flush();
  }

  // 存成功時勾選照送出的樣子留著。
  box(1).checked = true; box(1).dispatch('change');
  f.reply('calendar.setNonWorkingDays', { nonWorkingDays: [0, 1, 6] }); await flush();
  assert.deepEqual(ticked(), ['1', '6', '0']);
});

test('on the case list, opening a case while the list is still loading does not throw away the list that is on its way', async () => {
  const OTHER = { projectId: '合成另一案', databaseProvider: 'sqlite' };
  // 清單還在讀的時候按了開啟案件：沒有開案，稍後回來的清單照樣換上。
  const f = await boot([CASE]);
  f.click('[data-action="picker-refresh-local"]');
  const list = f.request('project.listLocal');
  f.$('[data-action="picker-open"]').dispatch('click');
  assert.equal(f.sent('project.load').length, 0);
  f.reply(list, { projects: [CASE, OTHER] }); await flush();
  assert.equal(f.state().busy, false);
  assert.deepEqual(plain(f.state().projects.map(p => p.projectId)), ['合成案件', '合成另一案'], 'the refreshed list is shown');
});

// 開案真的失敗、原因還在清單上方時，別的作業進行中又按了開啟案件、重新整理或確認刪除：都沒有開始，原因照樣留著。
test('on the case list, a press that starts nothing because another operation is running leaves the error box and the row to delete alone', async () => {
  for (const pressed of ['picker-open', 'picker-refresh-local', 'modal-confirm']) {
    const f = await boot([CASE]);
    f.click('[data-action="picker-open"]');
    f.fail(f.request('project.load'), { code: 'synthetic', message: '合成：案件資料庫打不開' }); await flush();
    const openFailure = f.$('.picker-feedback__body').textContent;
    assert.match(openFailure, /^開啟案件失敗：合成：案件資料庫打不開/);
    if (pressed === 'modal-confirm') {
      f.click('[data-action="picker-delete"]');
      f.reply('project.deletePreview', { reportCount: 0, workingPaperCount: 0 }); await flush();
    }
    // 刪除時先記下要拿掉哪一列，等清單重新讀回才收起；沒有開始刪除就不該記。
    const removals = [];
    const removing = f.window.JetMotion.removing;
    f.window.JetMotion.removing = intent => { removals.push(plain(intent)); return removing(intent); };
    const finishHeld = holdAnotherOperation(f);
    const sent = () => f.posted.filter(r => ['project.load', 'project.listLocal', 'project.delete'].includes(r.action)).length;
    const before = sent();
    f.$('[data-action="' + pressed + '"]').dispatch('click');
    await flush();
    assert.equal(sent(), before, pressed + ': nothing is sent');
    assert.ok(f.$('.picker-feedback__body') !== null, pressed + ': the error box is still there');
    assert.equal(f.$('.picker-feedback__body').textContent, openFailure, pressed + ': and still says why the open failed');
    assert.deepEqual(removals, [], pressed + ': no row is marked as being removed');
    await finishHeld();
  }
});

test('submitting the create form or the case details while another operation is running keeps the error that is shown', async () => {
  // 建立案件真的失敗、原因還在表單上時，別的作業進行中又送出一次：沒有建立，原因照樣留著。
  const f = await boot([]);
  f.click('[data-action="picker-new"]'); await flush();
  const form = namedForm(stepBody(f).querySelector('[data-bind="create-form"]'));
  form.projectName.value = '合成新案'; form.periodStart.value = '2025-01-01'; form.periodEnd.value = '2025-12-31';
  form.dispatch('submit', { target: form });
  f.fail(f.request('project.create'), { code: 'synthetic', message: '合成：案件名稱已經存在。' }); await flush();
  const box = () => stepBody(f).querySelector('[data-bind="create-error"]');
  assert.equal(box().hidden, false, 'the real failure is shown on the form');
  assert.match(box().textContent, /合成：案件名稱已經存在。/);
  let finishHeld = holdAnotherOperation(f);
  form.dispatch('submit', { target: form });
  assert.equal(f.sent('project.create').length, 1, 'no second create is sent');
  assert.equal(box().hidden, false, 'the reason is still on the form');
  assert.match(box().textContent, /合成：案件名稱已經存在。/);
  await finishHeld();

  // 修改案件資料也一樣；之後重畫表單，原因還在。
  const g = await openCaseAt(0);
  press(g, '[data-action="edit-project-metadata"]'); await flush();
  const edit = namedForm(stepBody(g).querySelector('[data-bind="project-update-form"]'));
  edit.clientName.value = '合成客戶';
  edit.dispatch('submit', { target: edit });
  g.fail(g.request('project.update'), { code: 'synthetic', message: '合成：案件資料存不進去。' }); await flush();
  const editBox = () => stepBody(g).querySelector('[data-bind="project-update-error"]');
  assert.equal(editBox().hidden, false, 'the real failure is shown on the edit form');
  finishHeld = holdAnotherOperation(g);
  edit.dispatch('submit', { target: edit });
  assert.equal(g.sent('project.update').length, 1, 'no second save is sent');
  for (const when of ['at once', 'after a redraw']) {
    assert.equal(editBox().hidden, false, when + ': the reason is still on the edit form');
    assert.match(editBox().textContent, /合成：案件資料存不進去。/, when);
    g.store.touch(); await flush();
  }
  await finishHeld();
});

test('removing the authorised preparer list while another operation is running marks nothing as being removed', async () => {
  const f = await openCaseAt(1, f => f.store.setAuthorizedPreparerState({ rowCount: 2, sourceColumn: 'Id', sourceRowCount: 2, blankRowCount: 0, duplicateRowCount: 0, matchedPreparerCount: 1 }));
  press(f, '[data-task-toggle="authorizedPreparer"]'); await flush();
  // 移除時先記下要拿掉的摘要與兩個鈕，等後端回來重畫時才收起；沒有開始移除就不該記。
  const removals = [];
  const removing = f.window.JetMotion.removing;
  f.window.JetMotion.removing = intent => { removals.push(plain(intent.keys)); return removing(intent); };
  const finishHeld = holdAnotherOperation(f);
  press(f, '[data-action="clear-authorized-preparer"]');
  assert.equal(f.sent('import.authorizedPreparer.clear').length, 0, 'nothing is removed');
  assert.deepEqual(removals, [], 'nothing is marked as being removed');
  await finishHeld();
  // 真的開始移除時照樣先記下。
  press(f, '[data-action="clear-authorized-preparer"]');
  assert.equal(f.sent('import.authorizedPreparer.clear').length, 1);
  assert.deepEqual(removals, [['preparer-summary', 'preparer-preview-action', 'preparer-clear-action']]);
});

// 開發者面板的資料庫檢視：換頁沒有開始時，頁次停在原本那一頁，之後按下一頁讀的是緊接著的那一頁。
test('DEV database view: paging while another operation is running does not skip a page', async () => {
  const f = await openCaseAt(1);
  f.$('[data-action="dev-refresh"]').dispatch('click');
  f.reply('dev.db.overview', { databasePath: '合成/case.db', databaseProvider: 'sqlite', engineVersion: '3.0', fileSizeBytes: 2048,
    tables: [{ name: 'synthetic_first', rowCount: 900 }, { name: 'synthetic_second', rowCount: 5 }] }); await flush();
  const select = f.$('[data-bind="dev-table-select"]');
  select.value = 'synthetic_first';
  f.$('[data-action="dev-query"]').dispatch('click');
  const first = f.request('dev.db.tableData');
  assert.deepEqual(plain(first.payload), { tableName: 'synthetic_first', limit: 200, offset: 0 });
  f.reply(first, { tableName: 'synthetic_first', columns: ['a'], rows: [['1']], limit: 200, offset: 0, totalCount: 900 }); await flush();

  const finishHeld = holdAnotherOperation(f);
  // 換了下拉選單的資料表又按兩次下一頁，都沒有開始。
  select.value = 'synthetic_second';
  f.$('[data-action="dev-next"]').dispatch('click');
  f.$('[data-action="dev-next"]').dispatch('click');
  assert.equal(f.sent('dev.db.tableData').length, 1, 'nothing is queried');
  await finishHeld();
  select.value = 'synthetic_first';
  f.$('[data-action="dev-next"]').dispatch('click');
  assert.deepEqual(plain(f.request('dev.db.tableData').payload), { tableName: 'synthetic_first', limit: 200, offset: 200 });
});

test('confirming the field mapping while another operation is running keeps the problem left by the last confirmation', async () => {
  const f = await openCaseAt(2, f => {
    f.store.setImportResult('gl', { batchId: 'gl-source', columns: ['Doc', 'Date', 'Account', 'Name', 'Memo', 'Amount', 'Side'], rowCount: 2 });
    f.store.setMappingMode('gl', 'side');
    f.store.replaceMappingDraft('gl', { voucherNumber: 'Doc', glDate: 'Date', accountNumber: 'Account', accountName: 'Name', description: 'Memo', amount: 'Amount', drCr: 'Side', drCrDebitCode: 'D' });
  });
  press(f, '[data-action="commit-gl"]');
  f.fail(f.request('mapping.commit.gl'), { code: 'synthetic', message: '合成：日期欄無法轉換。' }); await flush();
  const box = () => stepBody(f).querySelector('[data-bind="mapping-commit-error-gl"]');
  assert.ok(box() !== null, 'a real failure is shown in the mapping section');
  const commits = f.sent('mapping.commit.gl').length;

  const finishHeld = holdAnotherOperation(f);
  press(f, '[data-action="commit-gl"]');
  assert.equal(f.sent('mapping.commit.gl').length, commits, 'nothing is confirmed');
  f.store.touch(); await flush();
  assert.ok(box() !== null, 'the earlier problem is still shown after a redraw');
  assert.match(box().textContent, /配對未完成：合成：日期欄無法轉換。/);
  await finishHeld();
});

// 耗時接在訊息後面，成功時只留一則。
test('a long operation leaves one completion line with minutes and seconds, a failure adds the elapsed time and last progress, and each line appears while still busy', async () => {
  const f = await boot([CASE]);
  const busyWhenLogged = [];
  let count = f.state().messages.length;
  f.store.subscribe(next => { if (next.messages.length !== count) { count = next.messages.length; busyWhenLogged.push(next.busy); } });

  // 只有明示要記完成句的操作才記；沒有要求的成功操作不另記一則。
  let done = f.ui.run('合成查詢', () => f.api.systemPing({}));
  f.reply(f.sent('system.ping').at(-1), {}); await done;
  assert.equal(busyWhenLogged.length, 0, 'an operation without a completion line adds nothing');

  done = f.ui.run('合成作業', () => f.api.systemPing({}), { logCompletion: true });
  f.clock.advance(171000);
  f.reply(f.sent('system.ping').at(-1), {}); await done;
  // 耗時寫成幾分幾秒。
  assert.deepEqual(f.messages()[0], { text: '合成作業完成，耗時 2 分 51 秒。', level: 'info' });

  // 這裡借用 system.ping 當作一般作業，完成句由測試自己組，count 是測試的資料，不是 ping 的回應。
  const pingStandIn = '借用 system.ping 測 Ui.run 的完成句；count 由測試自己的完成句讀取';
  done = f.ui.run('合成作業', () => f.api.systemPing({}), { completionMessage: data => data && data.count ? '合成完成句，共 ' + data.count + ' 筆。' : '' });
  f.reply(f.sent('system.ping').at(-1), unchecked(pingStandIn, { count: 3 })); await done;
  // 有自己完成句的操作只留那一句，句尾的句號換成耗時；不到 0.1 秒時寫「不到 0.1 秒」。
  assert.deepEqual(f.messages()[0], { text: '合成完成句，共 3 筆，耗時不到 0.1 秒。', level: 'info' });
  done = f.ui.run('合成作業', () => f.api.systemPing({}), { completionMessage: data => data && data.count ? '合成完成句。' : '' });
  f.reply(f.sent('system.ping').at(-1), unchecked(pingStandIn, { count: 0 })); await done;
  assert.notEqual(f.messages()[0].text, '合成完成句，耗時不到 0.1 秒。', 'an empty completion line means nothing was done');

  done = f.ui.run('合成作業', () => f.api.systemPing({}));
  f.store.setBusyDetail('合成進度：已讀取 30 列，經過 1.0 秒');
  f.clock.advance(1500);
  f.fail(f.sent('system.ping').at(-1), { code: 'synthetic', message: '合成原因。' }); await done;
  // 原因結尾的句號拿掉，括號後加句號；最後進度不重複經過時間。
  assert.deepEqual(f.messages()[0], { text: '合成作業失敗：合成原因（耗時 1.5 秒，最後進度：合成進度：已讀取 30 列）。', level: 'error' });
  assert.deepEqual(busyWhenLogged, [true, true, true], 'every line is written before the busy state clears');
});

test('a backend error without a reason is written in Chinese without codes, and every action the screens use is a supported bridge action', async () => {
  const f = await boot([CASE]);
  let done = f.ui.run('合成作業', () => f.api.systemPing({}));
  f.fail(f.sent('system.ping').at(-1), { code: 'internal_error' }); await done;
  // 沒有附原因時寫非預期錯誤那一句。
  assert.deepEqual(f.messages()[0], { text: '合成作業失敗：發生非預期的錯誤，此操作未完成。請再試一次（耗時不到 0.1 秒）。', level: 'error' });
  done = f.ui.run('合成作業', () => f.api.systemPing({}));
  f.fail(f.sent('system.ping').at(-1), null); await done;
  assert.equal(f.messages()[0].text, '合成作業失敗：發生非預期的錯誤，此操作未完成。請再試一次（耗時不到 0.1 秒）。');
  assert.doesNotMatch(f.messages()[0].text, /[A-Za-z]/, 'no English or error code reaches the message panel');
  // 案件清單的「輸出支援日誌」用的 support.log.export 是正式的 bridge action：真的送出，不是在前端就失敗。
  // 這顆按鈕只在 Debug 組建出現，所以先用 setDevToolsEnabled(true) 讓它出現再按。
  f.store.setDevToolsEnabled(true); await flush();
  f.click('[data-action="picker-support-export"]');
  assert.deepEqual(plain(f.request('support.log.export').payload), {});
});

// ---- 訊息面板「複製紀錄」 ----

// 複製的紀錄寫畫面上的說法（前端不露出系統用詞）：中文欄名、資料庫名稱、三級訊息的名稱，
// 時間是這台電腦的當地日期與時間到秒；sv-SE 的日期格式正好是 YYYY-MM-DD HH:mm:ss，用來獨立算出預期值。
const COPIED_HEADER = '案件名稱\t案件編號\t資料庫\t時間\t等級\t訊息';
const COPIED_LEVEL = { info: '一般', warn: '需要注意', error: '錯誤' };
const localTime = utc => new Date(utc).toLocaleString('sv-SE', { hour12: false });

// 後端 log.recent 回的紀錄：新到舊，第 n 則的時間與等級可以由 n 推回來。
function recentEntries(count) {
  const levels = ['info', 'warn', 'error'];
  return Array.from({ length: count }, (_, i) => {
    const n = count - i;
    return { occurredUtc: '2026-01-01T00:' + String(Math.floor(n / 60)).padStart(2, '0') + ':' + String(n % 60).padStart(2, '0') + 'Z',
      level: levels[n % 3], text: n === 110 ? '第 110 則\t含定位字元\n與換行' : '第 ' + n + ' 則' };
  });
}

test('copying the log takes the newest one hundred after pending writes, oldest first, as six tab-separated columns with the case identity', async () => {
  const f = messagePanelFixture();
  const button = f.copyButton();
  // 報讀名稱在載入的正式標記上（只比對意思：說明是複製最近 100 則）。
  assert.match(button.getAttribute('aria-label'), /複製.*100/);
  const project = { projectId: '合成案件', projectCode: 'C-01', databaseProvider: 'sqlite', periodStart: '2025-01-01', periodEnd: '2025-12-31' };
  f.store.setProject(project); f.store.setBridgeReady(true);
  // 剛發生的訊息還在寫入：複製要等它寫完才讀最近的紀錄。
  f.store.addMessage('合成：剛發生的訊息', 'error');
  f.app.persistNewMessages(f.store.getState()); await settle(); await settle();
  const append = f.request('logAppend');
  assert.deepEqual(plain(append.payload), { level: 'error', text: '合成：剛發生的訊息' });
  button.dispatch('click'); await settle(); await settle();
  assert.equal(f.requests.filter(r => r.action === 'logRecent').length, 0, 'the recent read waits for the pending write');
  append.resolve({}); await settle(); await settle();
  const recent = f.request('logRecent');
  assert.deepEqual(plain(recent.payload), { limit: 100 });
  recent.resolve({ messages: recentEntries(120) }); await settle(); await settle(); await settle();
  assert.equal(f.clipboardWrites.length, 1);
  const lines = f.clipboardWrites[0].split('\r\n');
  assert.equal(lines[0], COPIED_HEADER);
  assert.equal(lines.length, 101);
  // 最近 100 則是第 21 到 120 則，由舊到新；欄內的定位字元與換行換成空白，等級照存的值（含錯誤）。
  const expected = [];
  for (let n = 21; n <= 120; n++) {
    const entry = recentEntries(120)[120 - n];
    expected.push(['合成案件', 'C-01', 'SQLite', localTime(entry.occurredUtc), COPIED_LEVEL[entry.level], n === 110 ? '第 110 則 含定位字元 與換行' : '第 ' + n + ' 則'].join('\t'));
  }
  assert.deepEqual(lines.slice(1), expected);
  assert.ok(lines.slice(1).every(line => line.split('\t').length === 6), 'every row has six columns');
  assert.ok(lines.some(line => line.split('\t')[4] === '錯誤'), 'the error level is copied by its on-screen name');
  // 按鈕說明已複製（只比對意思），稍後回到原本的名稱。
  assert.match(button.textContent, /已複製/);
  assert.equal(button.disabled, false);
  f.timers.run();
  assert.match(button.textContent, /複製/);
  assert.match(button.getAttribute('aria-label'), /100/);
});

test('when the clipboard refuses, copying falls back to the copy command with the same text', async () => {
  const f = messagePanelFixture({ clipboard: 'reject' });
  // 案件是 SQLite 案件，第三欄寫 SQLite。
  f.store.setProject({ projectId: '合成案件', projectCode: null, databaseProvider: 'sqlite' });
  f.copyButton().dispatch('click'); await settle(); await settle();
  f.request('logRecent').resolve({ messages: recentEntries(2) }); await settle(); await settle(); await settle();
  assert.equal(f.clipboardWrites.length, 0);
  assert.equal(f.copyCommands.length, 1);
  assert.equal(f.copyCommands[0].command, 'copy');
  // 案件編號是 null 時第二欄是空字串。
  assert.deepEqual(f.copyCommands[0].value.split('\r\n'), [COPIED_HEADER,
    '合成案件\t\tSQLite\t' + localTime(recentEntries(2)[1].occurredUtc) + '\t' + COPIED_LEVEL[recentEntries(2)[1].level] + '\t第 1 則',
    '合成案件\t\tSQLite\t' + localTime(recentEntries(2)[0].occurredUtc) + '\t' + COPIED_LEVEL[recentEntries(2)[0].level] + '\t第 2 則']);
  assert.match(f.copyButton().textContent, /已複製/);
});

test('switching to another case while copying, even one with the same name, copies nothing and says the case changed', async () => {
  // 讀最近紀錄途中換成同名的另一個案件物件（重新開同一個案件也是新的一次）。
  let f = messagePanelFixture();
  f.store.setProject({ projectId: '合成案件', databaseProvider: 'sqlite' });
  f.copyButton().dispatch('click'); await settle(); await settle();
  const recent = f.request('logRecent');
  f.store.setProject({ projectId: '合成案件', databaseProvider: 'sqlite' });
  recent.resolve({ messages: recentEntries(3) }); await settle(); await settle(); await settle();
  assert.equal(f.clipboardWrites.length, 0);
  assert.match(f.copyButton().textContent, /切換/);
  assert.equal(f.copyButton().disabled, false);

  // 等待中的訊息還在寫入時換案件：不讀最近紀錄，也不複製。
  f = messagePanelFixture();
  f.store.setProject({ projectId: '合成案件', databaseProvider: 'sqlite' }); f.store.setBridgeReady(true);
  f.store.addMessage('合成：寫入中的訊息', 'info');
  f.app.persistNewMessages(f.store.getState()); await settle(); await settle();
  const append = f.request('logAppend');
  f.copyButton().dispatch('click'); await settle(); await settle();
  f.store.setProject({ projectId: '合成案件', databaseProvider: 'sqlite' });
  append.resolve({}); await settle(); await settle(); await settle();
  assert.equal(f.requests.filter(r => r.action === 'logRecent').length, 0);
  assert.equal(f.clipboardWrites.length, 0);
  assert.match(f.copyButton().textContent, /切換/);
});

test('without an open case copying reads nothing and says no case is open', async () => {
  const f = messagePanelFixture();
  f.store.resetWorkflow();
  assert.equal(f.store.getState().project, null);
  f.copyButton().dispatch('click'); await settle(); await settle();
  assert.deepEqual(f.requests.map(r => r.action), [], 'nothing is read from the backend');
  assert.equal(f.clipboardWrites.length + f.copyCommands.length, 0);
  assert.match(f.copyButton().textContent, /尚未開啟案件/);
});

// ---- 其他情境的句子：第五步 ----
// 這兩個測試用共用夾具 usageJourneyFixture 畫出正式的第五步，或用支援模組的整個畫面夾具讀第五步的條件編輯器。

// 第五步的單日與區間用原生日期框，和建立案件的日期框相同，不能直接輸入 Excel 序列值或民國年，所以不列日期寫法說明。
// 原生日期框的行為由 frontend-shared-controls 的測試核對。
// 第二步假日檔與補班檔的日期寫法說明在 frontend-workflow 逐字比對，這裡不重寫。
test('step 5 native single dates and date ranges do not list the pasted date formats', async () => {
  const f = await boot([CASE]);
  const values = f.ui.FilterValues;
  const html = operator => values.render({ ...values.create('date'), operator, value: operator === 'between' ? { from: '', to: '' } : '' }, f.state(), 'synthetic-date');
  const hint = '<p class="rule-field__hint">日期寫法和 GL 相同，例如 2026/12/31、2026.12.31、20261231 或 Excel 序列值；民國年依案件設定。</p>';
  assert.equal(html('between').includes(hint), false, 'a date range uses the native date box');
  assert.equal(html('equals').includes(hint), false, 'a single date uses the native date box');
  // 日期清單用自己的說明，不重複這一句。
  assert.equal(html('in').includes(hint), false);
});

// 第五步的夾具：正式的第一到第六步腳本，訂閱重繪；驗證可以繼續，科目分類齊全。
function filterStepFixture({ saved = [] } = {}) {
  const f = usageJourneyFixture('filter', { redraw: true });
  f.window.getComputedStyle = () => ({ gridTemplateColumns: 'none' });
  f.window.JetFocus = {
    defer(target) { const node = target(); if (node && node.focus) node.focus(); },
    deferFirst(selectors) { for (const selector of selectors) { const node = f.$(selector); if (node) { node.focus(); return; } } }
  };
  f.store.setResult('validate', { resultRef: { resultId: 'v1' }, completenessTest: { eligibility: { isEligible: true } } });
  f.store.setAccountMappingState({ hasAnyCategory: true, hasRevenue: true, hasCounterpart: true });
  f.store.setSavedScenarios(saved);
  f.store.setFilterResultRef(saved.length ? { revision: 'r1', populationScope: 'auditPeriod' } : null);
  f.store.setReports([]);
  f.store.setFilterDraft(null);
  return f;
}
const ELAPSED = /耗時(?: \d+\.\d 秒|不到 0\.1 秒|\d+ 分 \d+ 秒)/.source;
const panelMessages = f => plain(f.store.getState().messages.map(m => ({ text: m.text, level: m.level })));
// 失敗句中的作業名稱只比對意思。
function assertFailureLine(f, reason, labelMeaning) {
  const line = panelMessages(f)[0];
  const sentence = line.text.match(new RegExp('^(.+)失敗：' + reason + '（' + ELAPSED + '）。$'));
  assert.ok(sentence !== null, 'the confirmed failure sentence: ' + line.text);
  assert.match(sentence[1], labelMeaning);
  assert.equal(line.level, 'error');
}

test('step 5 messages: a preview logs the confirmed count line with the elapsed time, and a failed save, removal or criteria report is an error line', async () => {
  let f = filterStepFixture();
  // 卡片用代號識別。
  f.click('[data-common-card="blankDescription"]');
  f.click('[data-action="preview-scenario"]'); await settle();
  f.request('filterPreview').resolve({ scenario: { count: 3, voucherCount: 2, summaryRowCount: 3, populationScope: 'auditPeriod' } }); await settle();
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [], nextCursor: null }); await settle(); await settle();
  // 句尾接上耗時。
  const previews = panelMessages(f).filter(m => m.text.startsWith('情境預覽'));
  assert.equal(previews.length, 1, 'one preview line');
  assert.match(previews[0].text, new RegExp('^情境預覽：符合條件 3 筆分錄，2 張傳票，' + ELAPSED + '。$'));
  assert.equal(previews[0].level, 'info');

  
  f.click('[data-action="save-scenario"]'); await settle();
  f.request('filterScenarioSave').reject(new Error('合成儲存失敗。')); await settle(); await settle();
  assertFailureLine(f, '合成儲存失敗', /儲存/);

  f = filterStepFixture({ saved: [{ ...savedBlankDescription(), name: '甲' }, { ...savedBlankDescription(), name: '乙' }] });
  f.click('[data-filter-pane-select="saved"]');
  f.click('[data-action="remove-scenario"][data-index="1"]');
  f.click('[data-action="confirm-remove-scenario"][data-index="1"]'); await settle();
  f.request('filterScenarioRemove').reject(new Error('合成移除失敗。')); await settle(); await settle();
  assertFailureLine(f, '合成移除失敗', /移除/);

  f.click('[data-action="export-criteria-report"]'); await settle();
  f.request('exportCriteriaSelectionReport').reject(new Error('合成報告失敗。')); await settle(); await settle();
  assertFailureLine(f, '合成報告失敗', /條件篩選報告/);
});

// 儲存情境被取消或遇到另一項作業還在進行，不是儲存失敗：儲存鈕上方不寫原因，草稿照舊。
test('step 5: saving a scenario shows no failure above the save button when it is cancelled or another operation is running', async () => {
  for (const [code, level] of [['operation_cancelled', 'info'], ['operation_in_progress', 'warn']]) {
    const f = filterStepFixture();
    f.click('[data-common-card="blankDescription"]');
    f.click('[data-action="save-scenario"]'); await settle();
    f.request('filterScenarioSave').reject(Object.assign(new Error('合成：沒有儲存'), { code })); await settle(); await settle();
    const notice = f.$('[data-bind="scenario-notice"]');
    assert.ok(notice !== null, 'the save panel is still shown');
    assert.equal(notice.hidden, true, code + ': nothing is written above the save button');
    assert.equal(notice.textContent, '', code);
    assert.equal(f.store.getState().filter.draft.groups[0].conditions.length, 1, code + ': the draft is kept');
    assert.equal(f.store.getState().filter.savedScenarios.length, 0, code + ': nothing was saved');
    assert.equal(panelMessages(f)[0].level, level, code);
    assert.deepEqual(panelMessages(f).filter(m => m.level === 'error'), [], code + ': nothing is logged as an error');
  }
});
