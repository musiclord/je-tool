'use strict';
// 第九節 D 改為名稱與動機常駐、單次儲存後清空草稿；移除舊表單開關操作並改驗證新流程。
// 原測試首次失敗：20261014-123035603-d123badefd7941119a02c58b1b431fae；保留請求、錯誤、失效與並行修改斷言。
// 情境 22：長作業、忙碌中再按與取消；情境 23：訊息面板「複製紀錄」（收尾四項第一項第 10 批，2026-10-14）。
//
// 審計員的操作：
//   情境 22　按「執行驗證」與「執行預篩選」看忙碌畫面的經過時間與提示，匯入時沒有那句提示；忙碌中又按了別的按鈕；
//           同步線上案件與開案時按「取消作業」；產生工作底稿途中取消，取消成功、動作剛好做完與取消失敗；
//           長作業成功與失敗後在訊息面板看到的句子；後端錯誤沒有附原因。另外直接用 Ui.run 走過「完成後重新整理」的規則。
//   情境 23　有案件時按「複製紀錄」；剪貼簿拒絕；複製途中換成同名的另一個案件；沒有開啟案件時按。
//
// 夾具：
//   情境 22 用支援模組的 fullAppFixture（經 bootFullApp 開啟，本檔叫 boot）：整個 index.html 的 body 標記與它列出的全部前端腳本（不含 ECharts 函式庫），
//   正式的 jet-api.js 經假的 chrome.webview 收發，app.js 照瀏覽器在 DOMContentLoaded 執行正式的 init()。
//   所以忙碌畫面的作業名稱由 jet-api.js 真的送出請求時的 onRequestStarted 事件帶入，不改 app.js 的內部變數。
//   計畫原本寫用 remapAppFixture；它在 app.js 外面放了一個自己的 busyActiveAction，忙碌畫面讀不到（第 0 批回報），所以改用整個畫面。
//   第 10 批時本檔、frontend-journey-picker 與 -export 各有一份相同的副本，第 11 批（2026-10-14）收進 support/frontend-fixtures.cjs。
//   情境 23 用支援模組的 messagePanelFixture（第 0 批為這個情境建的）。
//
// 取代的 C# 字串測試（src/JET/tests/JET.Tests/Architecture/），都已在 2026-10-14 第二、三波整併刪除；
// OperationLogFrontendTests.Run_LogsOptInCompletion_… 與 ScreenBehaviorReviewFrontendTests.FrontendTransportErrors_… 的第一次失敗收據
// 在第 11 批（2026-10-14）補進本檔對應的測試上方：
//   情境 22　ApprovedUxFrontendTests.BusyChrome_UsesMonotonicElapsedClockAndExactLongOperationGuidance
//           UiRunRefreshPipelineTests.Run_RefreshesOnceOnlyAfterBusyClears_WithSuccessOrSettledGuard
//           UiRunRefreshPipelineTests.Run_BusyEarlyReturn_ShowsFeedbackBeforeRefreshPipeline
//           OperationLogFrontendTests.Run_LogsOptInCompletion_AndAllFailuresIncludeElapsedAndLastProgress
//           OperationLogFrontendTests.CancellationRequest_PersistsTheLatestProgressBeforeTheOperationSettles
//           FrontendHardeningContractTests.JetApi_RejectsPendingOnUnload_AndProjectLoadIsCancellable 的取消開案與 support.log.export 部分
//           （連線層部分在 frontend-journey-transport，第 4 批）
//           ScreenBehaviorReviewFrontendTests.FrontendTransportErrors_UseChineseInsteadOfEnglishOriginals 的訊息面板部分
//           （連線層部分在 frontend-journey-transport，第 4 批）
//   情境 23　OperationLogFrontendTests.MessagePanel_CopiesRecentOneHundredWithProjectIdentityAsChronologicalTabSeparatedText
//           OperationLogFrontendTests.MessagePanel_CopyRequiresTheSameProjectSessionBeforeAndAfterRecentRead
//           OperationLogFrontendTests.MessagePanel_CopyWithoutActiveProject_LeavesIdentityCellsEmptyAndSkipsBackendRead
//   CompletenessEligibilityFrontendTests.UiRun_MapsCompletenessPrerequisiteFailure_AndPreservesBackendDetail 由
//   frontend-journey-step4 的「journey 8 messages」測試經正式的「執行預篩選」按鈕取代。
//
// 逐字比對的只有使用者確認過的句子，每一句的出處（final-wording.json 或 final-wording-r3.json 的編號）寫在旁邊；
// 其他文字只比對意思。預期值一律寫死；action payload 用 deepEqual 精確比對；斷言的失敗值不是假 DOM 節點。
// 產品裡的作業名稱（Ui.run 的 label）多數沒有使用者裁定，放在確認過的句型裡時只比對意思；測試自己傳入的合成作業名稱照樣逐字。
// 每個測試都有 30 秒上限：改壞檢查時若有請求永遠等不到回應，測試直接失敗，不會拖到改壞工具的時間上限。
// 2026-10-14 改壞檢查 O11（拿掉 Ui.run 的忙碌檢查）曾因「忙碌中再按」的測試等一個永遠不會回應的請求，跑到工具的 300 秒上限；
// 那個測試已改成不等待被擋下的作業，只確認它立刻結束。
const nodeTest = require('node:test');
const test = (name, fn) => nodeTest(name, { timeout: 30000 }, fn);
const assert = require('node:assert/strict');
const { settle, plain, messagePanelFixture, remapPrescreen, usageJourneyFixture, savedBlankDescription, flush, bootFullApp: boot } = require('./support/frontend-fixtures.cjs');
const { unchecked } = require('./support/action-contract.cjs');

const CASE = { projectId: '合成案件', databaseProvider: 'sqlite' };

// 從案件清單開案，回到第四步「資料驗證」所需的狀態：GL、TB 都已匯入並確認配對。
async function openCaseAtValidation() {
  const f = await boot([CASE]);
  f.click('[data-action="picker-open"]');
  f.reply('project.load', { project: { ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 3 } }); await flush();
  f.reply('log.recent', { messages: [] }); await flush();
  f.store.setImportResult('gl', { batchId: 'gl-source', columns: ['Doc', 'Amount'], rowCount: 2 });
  f.store.setImportResult('tb', { batchId: 'tb-source', columns: ['Code', 'Amount'], rowCount: 2 });
  f.store.setMappingCommitted('gl', { mode: 'signed', mapping: { docNum: 'Doc', amount: 'Amount' } });
  f.store.setMappingCommitted('tb', { mode: 'direct', mapping: { accNum: 'Code', amount: 'Amount' } });
  f.store.setStepIndex(3);
  await flush();
  return f;
}

function validationData(runId = 'v-operations') {
  return {
    resultRef: { runId, generatedUtc: '2026-01-01T00:00:00Z' },
    stats: { glRowCount: 2, voucherCount: 1, totalDebit: 10, totalCredit: 10, net: 0, periodStart: '2025-01-01', periodEnd: '2025-12-31' },
    completenessTest: { diffAccountCount: 0, diffAccounts: [], partA: { rowCountMatch: true, amountMatch: true }, naReason: null,
      eligibility: { isEligible: true, reason: null, warning: null } },
    docBalanceTest: { unbalancedDocumentCount: 0 },
    infSamplingTest: { sampleSize: 0 },
    nullRecordsTest: { nullDocumentCount: 0, nullAccountCount: 0, nullDescriptionCount: 0, outOfRangeDateCount: 0, nullRows: [] },
    sourceQuality: { findingCount: 0, sampleRows: [] }
  };
}

// ---- 情境 22：長作業、忙碌中再按與取消 ----

test('journey 22: validation and the prescreen show a monotonic elapsed time and the long-operation guidance, an import shows no guidance, and everything clears afterwards', async () => {
  const f = await openCaseAtValidation();
  f.click('[data-action="run-validate"]');
  const run = f.request('validate.run');
  assert.deepEqual(plain(run.payload), {});
  // 忙碌畫面的作業名稱由 jet-api.js 送出請求時的事件帶入：這時才知道是資料驗證，提示句與取消鈕跟著出現。
  assert.equal(f.state().busy, true);
  assert.equal(f.state().activeRequestId, run.requestId);
  assert.match(f.overlay.label(), /驗證/);
  // 長作業提示：使用者 2026-10-14 第三輪勾選頁確認 R3-40（final-wording-r3.json），取代 2026-08-20 核准的舊句。
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
  f.reply('export.validationArtifacts', { artifacts: [], reportArtifacts: [] }); await flush();
  assert.equal(f.state().busy, true, 'still busy while the account mapping file is prepared');
  f.reply('export.accountMappingTemplate', { disposition: 'kept', fileName: '合成科目配對.xlsx' }); await flush();
  assert.equal(f.state().busy, false);
  assert.equal(f.overlay.shown(), false);
  assert.ok(f.overlay.elapsed() === null && f.overlay.guidance() === null && f.overlay.cancel() === null, 'elapsed time, guidance and cancel are hidden');
  assert.equal(f.$('[data-bind="busy-elapsed"]').textContent, '');
  assert.equal(f.liveIntervals().length, 0, 'the elapsed clock stops');
  // 成功時只留一則完成句，耗時接在後面：使用者 2026-10-13 勾選頁確認 35（final-wording.json）與「耗時寫成 2 分 51 秒」的裁定。
  assert.deepEqual(f.messages().filter(m => /^資料驗證完成/.test(m.text)), [{ text: '資料驗證完成，耗時 1 分 5 秒。', level: 'info' }]);

  // 預篩選也是長作業，同一句提示。
  f.click('[data-action="run-prescreen"]');
  const prescreen = f.request('prescreen.run');
  assert.equal(f.overlay.guidance(), '資料量大時需要較長時間，可按「取消作業」中止。');
  f.clock.advance(4200);
  f.reply(prescreen, plain(remapPrescreen('p-operations'))); await flush();
  // 使用者 2026-10-13 勾選頁確認 37（final-wording.json）。
  assert.deepEqual(f.messages()[0], { text: '預篩選已更新，耗時 4.2 秒。', level: 'info' });
  assert.equal(f.overlay.guidance(), null);

  // 匯入不是資料驗證或預篩選：經過時間照樣顯示，但沒有那句提示。
  // 送出的資料照第二步實際送的格式寫（import-step.js），前後端契合的核對才收得下。
  f.ui.run('匯入 PBC-GL', () => f.api.importGlFromFile({ mode: 'replace', sources: [{ filePath: 'synthetic.xlsx', fileName: 'synthetic.xlsx', sheetName: 'GL' }] }));
  const importing = f.request('import.gl.fromFile');
  assert.equal(f.state().activeRequestId, importing.requestId);
  assert.match(f.overlay.elapsed(), /00:00/);
  assert.equal(f.overlay.guidance(), null);
  assert.notEqual(f.overlay.cancel(), null, 'an import can be cancelled');
  f.reply(importing, {}); await flush();
  assert.equal(f.overlay.elapsed(), null);
});

// 低嚴重度事項 F4（使用者 2026-10-19 要求處理）：取消鈕只跟著可取消的作業出現。開啟案件資料夾不能取消，
// 進行中不顯示取消鈕；執行驗證可以取消，上一個情境已核對它會顯示。
test('journey 22: opening the case folder cannot be cancelled, so no cancel button appears while it runs', async () => {
  const f = await openCaseAtValidation();
  f.$('[data-action="open-project-folder"]').dispatch('click');
  const open = f.request('host.openFolder');
  assert.equal(f.state().busy, true);
  assert.equal(f.state().activeRequestId, null);
  f.reply(open, {}); await flush();
  assert.equal(f.state().busy, false);
});

// 低嚴重度事項 F5（使用者 2026-10-19 要求處理）：驗證已完成並保存後，在產生報告時被取消，
// 訊息要說已完成的部分仍保留，不只寫「已取消」；之後的科目配對檔也不再產生。
test('journey 22: cancelling while the validation reports are produced keeps the finished validation and says so', async () => {
  const f = await openCaseAtValidation();
  f.click('[data-action="run-validate"]');
  f.reply(f.request('validate.run'), validationData()); await flush();
  f.fail(f.request('export.validationArtifacts'), { code: 'operation_cancelled', message: '合成：已取消' }); await flush();
  assert.equal(f.state().busy, false);
  assert.ok(f.messages().some(m => /^執行驗證並產生報告已取消，已完成的部分仍保留（耗時[^）]*）。$/.test(m.text)),
    'the cancellation says the finished validation is kept');
  assert.ok(f.state().lastRuns.validate, 'the finished validation stays');
  assert.equal(f.sent('export.accountMappingTemplate').length, 0);
});

// 使用者 2026-10-19 要求前端不露出系統用詞：程式本身的錯誤（TypeError 這類）不把英文原文寫進訊息，
// 改寫呼叫端的說明或非預期錯誤那一句；後端回的錯誤與前端自己寫的說明照原句。
test('journey 22: a program error inside an operation is reported in Chinese, not with its English text', async () => {
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

test('journey 22: pressing another operation while one is running sends nothing, keeps the running name and says it is still running', async () => {
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
  // 頁首的「開啟案件資料夾」也走同一個入口。
  f.$('[data-action="open-project-folder"]').dispatch('click');
  await flush();
  assert.equal(secondSettled, true, 'the blocked operation returns at once');
  assert.equal(factoryCalls, 0);
  assert.equal(refreshCalls, 0);
  assert.equal(work(), before, 'nothing else was sent besides writing the message log');
  assert.equal(f.state().busyLabel, runningLabel);
  // 使用者 2026-10-13 勾選頁確認 14（final-wording.json）：知道是哪項作業時寫出作業名稱，一般說明（2026-10-13 訊息分三級的裁定，
  // spec-item2-levels.md 第 4 節：只有 16 號「JET 回覆另一項作業還在進行」是需要注意）。作業名稱本身只比對意思。
  const stillRunning = '「' + runningLabel + '」還在進行，請等它完成後再試。';
  assert.deepEqual(f.messages().slice(0, 2), [{ text: stillRunning, level: 'info' }, { text: stillRunning, level: 'info' }]);
  // 被擋下的按鈕沒有開始，不在目前步驟加提示框。
  assert.equal(f.state().operationFailure, null);
});

test('journey 22: a refresh runs once after the operation is no longer busy, only on success when asked, and a failed refresh is its own message', async () => {
  const f = await boot([CASE]);
  const seen = [];
  // 預設成功或失敗都整理；整理在解除忙碌之後，所以整理裡再開一個作業也能送出。
  let done = f.ui.run('合成作業', () => f.api.systemPing({}), {
    refresh: () => { seen.push({ busy: f.state().busy }); return f.ui.run('合成整理', () => f.api.systemDatabaseInfo({})); } });
  const pings = f.sent('system.ping');
  f.reply(pings.at(-1), {}); await flush();
  assert.deepEqual(seen, [{ busy: false }]);
  const nested = f.sent('system.databaseInfo');
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

test('journey 22: listing online cases and opening a case can be cancelled with the request that is actually running', async () => {
  const f = await boot([CASE]);
  f.click('[data-action="picker-sync-online"]');
  const list = f.request('project.list');
  assert.equal(f.state().activeRequestId, list.requestId);
  assert.deepEqual(f.overlay.cancel(), { text: '取消作業', disabled: false });
  f.click('[data-action="cancel-operation"]');
  assert.deepEqual(plain(f.request('operation.cancel').payload), { requestId: list.requestId });
  assert.deepEqual(f.overlay.cancel(), { text: '取消中…', disabled: true });
  f.reply('operation.cancel', { requested: true }); await flush();
  f.fail(list, { code: 'operation_cancelled', message: '合成：已取消' }); await flush();
  assert.equal(f.state().busy, false);

  // 背景的心跳與訊息紀錄不是可以取消的作業，不能搶走開案這個請求。
  f.click('[data-action="picker-open"]');
  const load = f.request('project.load');
  f.api.projectHeartbeat({});
  assert.equal(f.state().activeRequestId, load.requestId);
  f.click('[data-action="cancel-operation"]');
  assert.deepEqual(plain(f.request('operation.cancel').payload), { requestId: load.requestId });
});

test('journey 22: cancelling records the operation and its progress at that moment, a finished step keeps its result, and a failed cancel can be pressed again', async () => {
  const f = await boot([CASE]);
  // 送出的資料照第六步實際送的欄位寫（export-step.js），前後端契合的核對才收得下。
  const running = () => f.ui.run('產生工作底稿', () => f.api.exportWorkpaperStream({ validationRunId: 'v-operations', scenarioRevision: 'r1', scenarioPositions: [1] }));
  // 取消被接受：記下作業名稱與當時進度。進度接進訊息時拿掉「工作底稿：」的冒號。
  let done = running();
  f.emit('export.progress', { artifactKind: 'workingPaper', phase: 'writingSheet', sheetName: '合成表', sheetsCompleted: 1, rowsWritten: 1500, elapsedMilliseconds: 4200 });
  f.click('[data-action="cancel-operation"]');
  f.reply('operation.cancel', { requested: true }); await flush();
  // 使用者 2026-10-13 勾選頁確認 4（final-wording.json）與 2026-10-14 第三輪 R3-28（final-wording-r3.json）的第二句。
  assert.deepEqual(f.messages()[0], {
    text: '已要求取消「產生工作底稿」，當時進度：工作底稿正在寫入「合成表」，已完成 1 張工作表、寫入 1,500 列，經過 4.2 秒。', level: 'info' });
  assert.equal(f.state().busy, true, 'the operation is still settling');
  f.clock.advance(4300);
  f.fail(f.request('export.workpaperStream'), { code: 'operation_cancelled', message: '合成：已取消' }); await done;
  // 使用者 2026-10-13 勾選頁確認 17（final-wording.json）、2026-10-14 第三輪 R3-27、R3-28：最後進度不重複經過時間，括號後有句號。
  assert.deepEqual(f.messages()[0], {
    text: '產生工作底稿已取消（耗時 4.3 秒，最後進度：工作底稿正在寫入「合成表」，已完成 1 張工作表、寫入 1,500 列）。', level: 'info' });
  assert.equal(f.state().operationFailure, null, 'cancelling is not a failure notice');

  // 按下取消時目前的動作剛好做完：使用者 2026-10-13 勾選頁確認 3（final-wording.json）。沒有進度時不寫當時進度。
  done = running();
  f.click('[data-action="cancel-operation"]');
  f.reply('operation.cancel', { requested: false }); await flush();
  assert.deepEqual(f.messages()[0], { text: '目前的動作已完成，結果已保留；尚未開始的後續作業不會執行。', level: 'info' });
  f.reply(f.request('export.workpaperStream'), {}); await done;

  // 取消失敗：使用者 2026-10-13 勾選頁確認 5（final-wording.json，維持原句）。按鈕回到可以按，再按一次會再送一次取消。
  done = running();
  f.click('[data-action="cancel-operation"]');
  const firstCancel = f.request('operation.cancel');
  f.fail(firstCancel, { code: 'synthetic', message: '合成：取消沒有送達' }); await flush();
  assert.deepEqual(f.messages()[0], { text: '取消作業失敗：合成：取消沒有送達', level: 'error' });
  assert.deepEqual(f.overlay.cancel(), { text: '取消作業', disabled: false });
  f.click('[data-action="cancel-operation"]');
  const secondCancel = f.request('operation.cancel');
  assert.ok(secondCancel !== firstCancel, 'a second cancel request is sent');
  assert.deepEqual(plain(secondCancel.payload), { requestId: f.request('export.workpaperStream').requestId });
  f.reply(secondCancel, { requested: true }); await flush();
  // 沒有進度時沒有「當時進度」。
  assert.deepEqual(f.messages()[0], { text: '已要求取消「產生工作底稿」。', level: 'info' });
  f.fail(f.request('export.workpaperStream'), { code: 'operation_cancelled', message: '合成：已取消' }); await done;

  // 作業沒有名稱時，取消訊息仍寫出一個名稱，不留空的引號（名稱本身沒有使用者裁定，只比對意思）。
  done = f.ui.run('', () => f.api.exportWorkpaperStream({ validationRunId: 'v-operations', scenarioRevision: 'r1', scenarioPositions: [1] }));
  f.click('[data-action="cancel-operation"]');
  f.reply(f.request('operation.cancel'), { requested: true }); await flush();
  assert.match(f.messages()[0].text, /^已要求取消「[^」]+」。$/);
  f.fail(f.request('export.workpaperStream'), { code: 'operation_cancelled', message: '合成：已取消' }); await done;
});

// 取代 OperationLogFrontendTests.Run_LogsOptInCompletion_AndAllFailuresIncludeElapsedAndLastProgress，原方法註解：使用者 2026-10-13 裁定耗時保留、改成秒並合併，
// 成功時只留一則。第一次失敗收據 20261013-095529696-bc66ff9ab56e4bd29575a25a71138c23。
test('journey 22: a long operation leaves one completion line with minutes and seconds, a failure adds the elapsed time and last progress, and each line appears while still busy', async () => {
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
  // 使用者 2026-10-13 勾選頁確認 15（final-wording.json）與「耗時寫成 2 分 51 秒」的裁定。
  assert.deepEqual(f.messages()[0], { text: '合成作業完成，耗時 2 分 51 秒。', level: 'info' });

  // 這裡借用 system.ping 當作一般作業，完成句由測試自己組，count 是測試的資料，不是 ping 的回應。
  const pingStandIn = '借用 system.ping 測 Ui.run 的完成句；count 由測試自己的完成句讀取';
  done = f.ui.run('合成作業', () => f.api.systemPing({}), { completionMessage: data => data && data.count ? '合成完成句，共 ' + data.count + ' 筆。' : '' });
  f.reply(f.sent('system.ping').at(-1), unchecked(pingStandIn, { count: 3 })); await done;
  // 有自己完成句的操作只留那一句，句尾的句號換成耗時：使用者 2026-10-13 勾選頁確認 15；不到 0.1 秒是 2026-10-14 第三輪 R3-26。
  assert.deepEqual(f.messages()[0], { text: '合成完成句，共 3 筆，耗時不到 0.1 秒。', level: 'info' });
  done = f.ui.run('合成作業', () => f.api.systemPing({}), { completionMessage: data => data && data.count ? '合成完成句。' : '' });
  f.reply(f.sent('system.ping').at(-1), unchecked(pingStandIn, { count: 0 })); await done;
  assert.notEqual(f.messages()[0].text, '合成完成句，耗時不到 0.1 秒。', 'an empty completion line means nothing was done');

  done = f.ui.run('合成作業', () => f.api.systemPing({}));
  f.store.setBusyDetail('合成進度：已讀取 30 列，經過 1.0 秒');
  f.clock.advance(1500);
  f.fail(f.sent('system.ping').at(-1), { code: 'synthetic', message: '合成原因。' }); await done;
  // 使用者 2026-10-13 勾選頁確認 19（final-wording.json）、2026-10-14 第三輪 R3-27：原因結尾的句號拿掉，括號後加句號；最後進度不重複經過時間。
  assert.deepEqual(f.messages()[0], { text: '合成作業失敗：合成原因（耗時 1.5 秒，最後進度：合成進度：已讀取 30 列）。', level: 'error' });
  assert.deepEqual(busyWhenLogged, [true, true, true], 'every line is written before the busy state clears');
});

test('journey 22: a backend error without a reason is written in Chinese without codes, and every action the screens use is a supported bridge action', async () => {
  const f = await boot([CASE]);
  let done = f.ui.run('合成作業', () => f.api.systemPing({}));
  f.fail(f.sent('system.ping').at(-1), { code: 'internal_error' }); await done;
  // 取代 ScreenBehaviorReviewFrontendTests.FrontendTransportErrors_UseChineseInsteadOfEnglishOriginals 的訊息面板部分，原方法註解：使用者 2026-10-13 勾選頁確認改字，
  // 預期值換成確認後的句子（第一次失敗：收據 20261013-130645716-968482adb634494a97e2594c7e73decf）。
  // 沒有附原因時用 A12 的句子（使用者 2026-10-13 勾選頁確認，final-wording.json 的 A12）。
  assert.deepEqual(f.messages()[0], { text: '合成作業失敗：發生非預期的錯誤，此操作未完成。請再試一次（耗時不到 0.1 秒）。', level: 'error' });
  done = f.ui.run('合成作業', () => f.api.systemPing({}));
  f.fail(f.sent('system.ping').at(-1), null); await done;
  assert.equal(f.messages()[0].text, '合成作業失敗：發生非預期的錯誤，此操作未完成。請再試一次（耗時不到 0.1 秒）。');
  assert.doesNotMatch(f.messages()[0].text, /[A-Za-z]/, 'no English or error code reaches the message panel');
  // 案件清單的「輸出支援日誌」用的 support.log.export 是正式的 bridge action：真的送出，不是在前端就失敗。
  // 這顆按鈕只在 Debug 組建出現（使用者 2026-10-20 選「只藏案件清單的」），原本以 Release 開啟就按
  // （第一次失敗：FrontendMapping 收據 20261020-010431620-e949dfcb3f2b4aab9c7c44495ccd0dc3）。
  f.store.setDevToolsEnabled(true); await flush();
  f.click('[data-action="picker-support-export"]');
  assert.deepEqual(plain(f.request('support.log.export').payload), {});
});

// ---- 情境 23：訊息面板「複製紀錄」 ----

// 複製的紀錄寫畫面上的說法（使用者 2026-10-19 要求前端不露出系統用詞）：中文欄名、資料庫名稱、三級訊息的名稱，
// 時間是這台電腦的當地日期與時間到秒；sv-SE 的日期格式正好是 YYYY-MM-DD HH:mm:ss，用來獨立算出預期值。
// 原本的英文欄名與原始值寫法第一次失敗：FrontendMapping 收據 20261019-110348881-1bfcfa4630b64f05ba7a8384060f5269。
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

test('journey 23: copying the log takes the newest one hundred after pending writes, oldest first, as six tab-separated columns with the case identity', async () => {
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

test('journey 23: when the clipboard refuses, copying falls back to the copy command with the same text', async () => {
  const f = messagePanelFixture({ clipboard: 'reject' });
  f.store.setProject({ projectId: '合成案件', projectCode: null, databaseProvider: 'duckdb' });
  f.copyButton().dispatch('click'); await settle(); await settle();
  f.request('logRecent').resolve({ messages: recentEntries(2) }); await settle(); await settle(); await settle();
  assert.equal(f.clipboardWrites.length, 0);
  assert.equal(f.copyCommands.length, 1);
  assert.equal(f.copyCommands[0].command, 'copy');
  // 案件編號是 null 時第二欄是空字串。
  assert.deepEqual(f.copyCommands[0].value.split('\r\n'), [COPIED_HEADER,
    '合成案件\t\tDuckDB\t' + localTime(recentEntries(2)[1].occurredUtc) + '\t' + COPIED_LEVEL[recentEntries(2)[1].level] + '\t第 1 則',
    '合成案件\t\tDuckDB\t' + localTime(recentEntries(2)[0].occurredUtc) + '\t' + COPIED_LEVEL[recentEntries(2)[0].level] + '\t第 2 則']);
  assert.match(f.copyButton().textContent, /已複製/);
});

test('journey 23: switching to another case while copying, even one with the same name, copies nothing and says the case changed', async () => {
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

test('journey 23: without an open case copying reads nothing and says no case is open', async () => {
  const f = messagePanelFixture();
  f.store.resetWorkflow();
  assert.equal(f.store.getState().project, null);
  f.copyButton().dispatch('click'); await settle(); await settle();
  assert.deepEqual(f.requests.map(r => r.action), [], 'nothing is read from the backend');
  assert.equal(f.clipboardWrites.length + f.copyCommands.length, 0);
  assert.match(f.copyButton().textContent, /尚未開啟案件/);
});

// ---- 第 7、9 批留下的句子（收尾四項第一項第 10 批，2026-10-14） ----
// 第五步的句子原本屬第 9 批（frontend-journey-filter-saved），那個檔不在第 10 批可改的範圍，所以寫在本檔。
// 這兩個測試用共用夾具 usageJourneyFixture 畫出正式的第五步，或用支援模組的整個畫面夾具讀第五步的條件編輯器。

// 第五步日期條件的寫法說明：使用者 2026-10-14 第三輪勾選頁確認 R3-43 的第一句（final-wording-r3.json）。
// 第二句（第二步假日檔與補班檔）已由文字批次 D 在 frontend-workflow 逐字比對，這裡不重寫。
// 第九節 E 改用原生日期框後，單日與區間不能再直接輸入 Excel 序列值或民國年（使用者 2026-10-14 要求統一成建立案件的日期框），
// 獨立複審因此拿掉這兩處的格式說明；新行為由 frontend-shared-controls 的 section 9 E 測試守住。本測試原本斷言兩處都有這句，
// 與新行為矛盾，改成斷言不再出現。第一次失敗：20261014-153311138-1c5e283b547049c3a64e3f4f368617b3。
test('step 5 native single dates and date ranges no longer promise the pasted date formats', async () => {
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
  f.store.setLastRun('validate', { resultRef: { runId: 'v1' }, completenessTest: { eligibility: { isEligible: true } } });
  f.store.setAccountMappingState({ hasAnyCategory: true, hasRevenue: true, hasCounterpart: true });
  f.store.setSavedScenarios(saved);
  f.store.setFilterResultRef(saved.length ? { revision: 'r1', populationScope: 'auditPeriod' } : null);
  f.store.setReportArtifacts([]);
  f.store.setFilterDraft(null);
  return f;
}
const ELAPSED = /耗時(?: \d+\.\d 秒|不到 0\.1 秒|\d+ 分 \d+ 秒)/.source;
const panelMessages = f => plain(f.store.getState().messages.map(m => ({ text: m.text, level: m.level })));
// 確認過的失敗句型（勾選頁確認 19、第三輪 R3-27），作業名稱只比對意思。
function assertFailureLine(f, reason, labelMeaning) {
  const line = panelMessages(f)[0];
  const sentence = line.text.match(new RegExp('^(.+)失敗：' + reason + '（' + ELAPSED + '）。$'));
  assert.ok(sentence !== null, 'the confirmed failure sentence: ' + line.text);
  assert.match(sentence[1], labelMeaning);
  assert.equal(line.level, 'error');
}

test('step 5 messages: a preview logs the confirmed count line with the elapsed time, and a failed save, removal or criteria report is an error line', async () => {
  let f = filterStepFixture();
  // 卡片改用代號識別（2026-10-29 拿掉卡片字母）。第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
  f.click('[data-common-card="blankDescription"]');
  f.click('[data-action="preview-scenario"]'); await settle();
  f.request('filterPreview').resolve({ scenario: { count: 3, voucherCount: 2, summaryRowCount: 3, populationScope: 'auditPeriod' } }); await settle();
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [], nextCursor: null }); await settle(); await settle();
  // 使用者 2026-10-13 勾選頁確認 58（final-wording.json，維持原句），句尾接上耗時（同日確認 15）。
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
