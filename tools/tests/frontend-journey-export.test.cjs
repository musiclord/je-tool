'use strict';
// 情境 16：第六步匯出工作底稿（收尾四項第一項第 10 批，2026-10-14）。
//
// 審計員的操作：到第六步依預篩選的狀態匯出：有目前的預篩選結果、還沒有、結果已過期、資料驗證判定不能繼續；
// 取消勾選「預篩選報告」；匯出途中換了案件、重新驗證或改了情境；看匯出途中忙碌畫面的進度。
// 匯出失敗時第六步標題下方的紅色提示框，已由 frontend-workflow 的「items2 a failed workpaper export shows a red notice…」
// 經同一個匯出按鈕測過（訊息分三級的產品改動時寫的），這裡不重寫；按「取消作業」保留已完成的預篩選報告，
// 由 frontend-workflow 的「remap S6-06 cancellation through the actual export button…」測過。
//
// 夾具：支援模組的 fullAppFixture，經 bootFullApp（本檔叫 boot）開啟 JET：
// 整個 index.html 的 body 標記與全部前端腳本，正式的 jet-api.js 經假的 chrome.webview 收發，app.js 照瀏覽器執行正式的 init()；
// 匯出進度事件經 jet-api.js 正式的事件通道送進 app.js 的訂閱。計畫原本寫用 usageJourneyFixture 與 remapAppFixture，
// 兩者都沒有 app.js 的進度訂閱，所以改用整個畫面；第 10 批先放在本檔，第 11 批（2026-10-14）收進 support/frontend-fixtures.cjs。
//
// 取代的 C# 字串測試（src/JET/tests/JET.Tests/Architecture/，2026-10-14 第二、三波整併都已刪除）：
//   PrescreenDefaultExportFrontendTests.ExportPanel_IncludesPrescreenReportByDefault_AndLetsTheUserCancelIt
//   PrescreenDefaultExportFrontendTests.ExportWithCurrentPrescreenRun_ProducesPrescreenReportBeforeTheWorkpaper
//   PrescreenDefaultExportFrontendTests.ExportWithoutCurrentPrescreenRun_AnnouncesTheCatchUpRun_ThenRunsItFirst
//   PrescreenDefaultExportFrontendTests.ExportSequence_ChecksCancellationAndOriginalContext_BeforeStartingEachAction
//   WorkpaperSheetMirrorTests.ExportStep_SelectsScenariosWithoutSheetsOrOutputPath
//   ExportProgressFrontendTests.ExportProgress_UsesAllContractFields_WithoutInventingPercentageOrSuccess
//   ApprovedUxFrontendTests.Workpaper_DoesNotSendTheAuditorBackToProduceAnUnrelatedReport 的第六步部分（第五步部分屬情境 13，第 9 批）
//
// 逐字比對的只有使用者確認過的句子，出處寫在旁邊；其他文字只比對意思。預期值一律寫死；action payload 用 deepEqual 精確比對，
// 並確認沒有 `__` 鍵；斷言的失敗值不是假 DOM 節點。第六步的作業名稱沒有使用者裁定，放在確認過的句型裡時只比對意思。
// 每個測試都有 30 秒上限：改壞檢查時若有請求永遠等不到回應，測試直接失敗，不會拖到改壞工具的時間上限（2026-10-14 第 10 批補）。
const nodeTest = require('node:test');
const test = (name, fn) => nodeTest(name, { timeout: 30000 }, fn);
const assert = require('node:assert/strict');
const { plain, savedG, remapPrescreen, flush, bootFullApp: boot, noDunderKeys } = require('./support/frontend-fixtures.cjs');

const CASE = { projectId: '合成匯出案', databaseProvider: 'sqlite' };
const EXPORT_INDEX = 5;

function validation(runId, eligibility = { isEligible: true, reason: null, warning: null }) {
  return { resultRef: { runId, generatedUtc: '2026-01-01T00:00:00Z' }, completenessTest: { eligibility } };
}

function artifact(artifactId, kind, sourceRef = {}) {
  return { artifactId, kind, fileName: artifactId + '.xlsx', fileState: 'present', stale: false,
    generatedUtc: '2026-01-02T03:04:05Z', sourceRef };
}

// 從案件清單開案，帶到第六步：GL、TB 已確認配對，資料驗證 v1 可以繼續，兩個已儲存情境版本 r1。
// prescreen：目前的預篩選結果（null 表示還沒有）；eligibility：資料驗證的判定。
async function openExport({ prescreen = 'p1', eligibility } = {}) {
  const f = await boot([CASE]);
  f.click('[data-action="picker-open"]');
  f.reply('project.load', { project: { ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 1 } }); await flush();
  f.reply('log.recent', { messages: [] }); await flush();
  for (const kind of ['gl', 'tb']) {
    f.store.setImportResult(kind, { batchId: kind + '-source', columns: ['Account'], rowCount: 2 });
    f.store.setMappingCommitted(kind, { mode: kind === 'gl' ? 'signed' : 'direct', mapping: {} });
  }
  f.store.setLastRun('validate', validation('v1', eligibility));
  if (prescreen) f.store.setLastRun('prescreen', plain(remapPrescreen(prescreen)));
  f.store.setSavedScenarios([savedG('第一個'), savedG('第二個')]);
  f.store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' });
  f.store.setStepIndex(EXPORT_INDEX);
  await flush();
  return f;
}

// 第六步目前的內容。
function step(f) { const node = f.$('.stepflow-item--current .stepflow-item__body'); assert.ok(node !== null, 'step six is shown'); return node; }

// 修改前失敗：20261014-125539535-e6819c0d2f8940eda66ed524fe3ca0c0，取消全選仍寫「清除」。
test('section 9 F: exported scenario selection uses the shared deselection wording', async () => {
  const f = await openExport();
  assert.equal(step(f).querySelector('[data-action="clear-scenario-selection"]').textContent, '全部取消');
});
function exportButton(f) { const node = step(f).querySelector('[data-action="export-workpaper"]'); assert.ok(node !== null, 'export button rendered'); return { text: node.textContent, disabled: node.disabled }; }
function pressExport(f) { const node = step(f).querySelector('[data-action="export-workpaper"]'); assert.equal(node.disabled, false, 'export button enabled'); node.dispatch('click'); }
function toggleReport(f, checked) { const node = step(f).querySelector('[data-bind="include-prescreen-report"]'); assert.ok(node !== null, 'prescreen report option rendered'); node.checked = checked; node.dispatch('change'); }
function notice(f) { const node = step(f).querySelector('[data-bind="prescreen-export-notice"]'); return node === null ? null : node.textContent; }
function exportActions(f, from = 0) { return f.posted.slice(from).map(r => r.action).filter(action => !['log.append', 'project.saveProgress'].includes(action)); }

const WORKPAPER_PAYLOAD = { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] };

test('journey 16: the prescreen report is ticked by default and named on the button, unticking makes only the workpaper, and reopening the case ticks it again', async () => {
  const f = await openExport();
  assert.equal(step(f).querySelector('[data-bind="include-prescreen-report"]').checked, true);
  // 按鈕名稱表示會一併產生預篩選報告（只比對意思）。
  assert.match(exportButton(f).text, /工作底稿.*預篩選報告/);
  // 情境是用勾選框選的，預設全選；沒有選工作表或存檔位置的控制項。
  assert.deepEqual(step(f).querySelectorAll('[data-scenario-position]').map(node => [node.getAttribute('data-scenario-position'), node.checked]), [['1', true], ['2', true]]);
  assert.ok(step(f).querySelector('[data-sheet-index]') === null, 'no worksheet picker');
  toggleReport(f, false);
  assert.doesNotMatch(exportButton(f).text, /預篩選/);
  const from = f.posted.length;
  pressExport(f); await flush();
  // 只送工作底稿，payload 正好三個鍵，沒有選存檔位置的請求。
  assert.deepEqual(exportActions(f, from), ['export.workpaperStream']);
  const sent = f.request('export.workpaperStream');
  assert.deepEqual(plain(sent.payload), WORKPAPER_PAYLOAD);
  assert.ok(noDunderKeys(sent.payload));
  f.clock.advance(3000);
  const paper = artifact('paper-1', 'workingPaper', { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] });
  f.reply(sent, { artifact: paper, reportArtifacts: [paper], sheetStats: [{ sheetName: '合成情境表', rowsWritten: 12345 }, { sheetName: '合成摘要', rowsWritten: 7 }] }); await flush();
  assert.deepEqual(exportActions(f, from), ['export.workpaperStream'], 'nothing else is sent afterwards');
  // 工作表統計：名稱與千分位筆數。
  const rows = step(f).querySelectorAll('.preview-table tbody tr').map(tr => tr.querySelectorAll('td').map(td => td.textContent));
  assert.deepEqual(rows, [['合成情境表', '12,345'], ['合成摘要', '7']]);
  // 使用者 2026-10-13 勾選頁確認 51（final-wording.json，維持原句）與 15：完成句接上耗時，成功時只有這一則。
  assert.deepEqual(f.messages()[0], { text: '已在案件資料夾產生工作底稿（2 張工作表），耗時 3.0 秒。', level: 'info' });

  // 回首頁再開同一個案件：預篩選報告又是預設勾選。
  f.click('[data-action="app-back-picker"]'); await flush();
  f.reply('project.saveProgress', {}); await flush();
  f.reply('project.releaseLock', {}); await flush();
  f.reply(f.sent('project.listLocal').at(-1), { projects: [CASE] }); await flush();
  f.click('[data-action="picker-open"]');
  f.reply(f.sent('project.load').at(-1), { project: { ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 1 } }); await flush();
  f.reply(f.sent('log.recent').at(-1), { messages: [] }); await flush();
  f.store.setLastRun('validate', validation('v1'));
  f.store.setLastRun('prescreen', plain(remapPrescreen('p1')));
  f.store.setSavedScenarios([savedG('第一個')]);
  f.store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' });
  f.store.setStepIndex(EXPORT_INDEX); await flush();
  assert.equal(step(f).querySelector('[data-bind="include-prescreen-report"]').checked, true);
});

test('journey 16: with a current prescreen result the report for that run is made first and the workpaper only after it, and both are listed', async () => {
  const f = await openExport({ prescreen: 'p1' });
  assert.equal(notice(f), null, 'nothing to announce when the prescreen result is current');
  const from = f.posted.length;
  pressExport(f); await flush();
  assert.deepEqual(exportActions(f, from), ['export.prescreenReport']);
  const report = f.request('export.prescreenReport');
  assert.deepEqual(plain(report.payload), { runId: 'p1' });
  const prescreenReport = artifact('prescreen-1', 'prescreenReport', { prescreenRunId: 'p1' });
  f.reply(report, { artifact: prescreenReport, reportArtifacts: [prescreenReport] }); await flush();
  assert.deepEqual(exportActions(f, from), ['export.prescreenReport', 'export.workpaperStream']);
  const sent = f.request('export.workpaperStream');
  assert.deepEqual(plain(sent.payload), WORKPAPER_PAYLOAD);
  const paper = artifact('paper-1', 'workingPaper', { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] });
  f.reply(sent, { artifact: paper, reportArtifacts: [prescreenReport, paper], sheetStats: [{ sheetName: '合成情境表', rowsWritten: 2 }] }); await flush();
  assert.deepEqual(plain(f.state().reportArtifacts.map(a => a.artifactId)), ['prescreen-1', 'paper-1']);
  assert.match(step(f).textContent, /paper-1\.xlsx/);
  // 使用者 2026-10-13 勾選頁確認 32、51（final-wording.json，維持原句）。
  assert.deepEqual(f.messages().slice(0, 2).map(m => m.text), ['已在案件資料夾產生工作底稿（1 張工作表），耗時不到 0.1 秒。', '已在案件資料夾產生預篩選報告。']);
});

for (const situation of ['none', 'stale']) test('journey 16: with ' + (situation === 'none' ? 'no prescreen result yet' : 'an outdated prescreen result') + ' the step says it will run the prescreen first, then runs it, reports on the new run and makes the workpaper', async () => {
  const f = await openExport({ prescreen: situation === 'none' ? null : 'p-old' });
  if (situation === 'stale') {
    // 例如科目分類儲存後，後端回報預篩選結果已過期。
    f.store.applyMutationEffects({ staleState: { validation: false, prescreen: true, filter: false } }); await flush();
  }
  // 按下之前就說明會先執行預篩選：使用者 2026-10-14 第三輪勾選頁確認 R3-40（final-wording-r3.json）的第二句。
  assert.equal(notice(f), '尚無預篩選結果，匯出時會先執行預篩選；資料量大時需要數分鐘到十餘分鐘。');
  assert.match(exportButton(f).text, /先執行預篩選/);
  const from = f.posted.length;
  pressExport(f); await flush();
  assert.deepEqual(exportActions(f, from), ['prescreen.run']);
  assert.deepEqual(plain(f.request('prescreen.run').payload), {});
  f.reply('prescreen.run', plain(remapPrescreen('p-new'))); await flush();
  assert.deepEqual(exportActions(f, from), ['prescreen.run', 'export.prescreenReport']);
  assert.deepEqual(plain(f.request('export.prescreenReport').payload), { runId: 'p-new' });
  const prescreenReport = artifact('prescreen-new', 'prescreenReport', { prescreenRunId: 'p-new' });
  f.reply('export.prescreenReport', { artifact: prescreenReport, reportArtifacts: [prescreenReport] }); await flush();
  assert.deepEqual(exportActions(f, from), ['prescreen.run', 'export.prescreenReport', 'export.workpaperStream']);
  assert.deepEqual(plain(f.request('export.workpaperStream').payload), WORKPAPER_PAYLOAD);
});

// 使用者 2026-10-15 在待決事項勾選頁裁定（U8）：上游修改清掉已存情境後，第六步不再顯示上一次匯出的工作表與資料列數；
// 重新儲存情境以後也不顯示，因為那張表描述的是舊版本的匯出。修改前這張表一直留到換案件。
test('journey 16: the sheet table of the last export goes away once an upstream change clears the scenarios', async () => {
  const f = await openExport();
  toggleReport(f, false);
  pressExport(f); await flush();
  const paper = artifact('paper-1', 'workingPaper', { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] });
  f.reply('export.workpaperStream', { artifact: paper, reportArtifacts: [paper], sheetStats: [{ sheetName: '合成情境表', rowsWritten: 2 }] }); await flush();
  assert.equal(step(f).querySelectorAll('.preview-table tbody tr').length, 1);
  f.store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true } });
  f.store.setSavedScenarios([savedG('第一個'), savedG('第二個')]);
  f.store.setFilterResultRef({ revision: 'r2', populationScope: 'auditPeriod' });
  f.store.setStepIndex(EXPORT_INDEX); await flush();
  assert.equal(step(f).querySelectorAll('.preview-table tbody tr').length, 0, 'the table belonged to the export of the older scenarios');
});

// 使用者 2026-10-15 在待決事項勾選頁裁定（U4）：第六步要先補跑預篩選時，和第四步的「執行預篩選」一樣，先詢問還沒儲存的科目分類。
// 選「留在這裡」什麼都不送；選擇繼續才執行預篩選。選「儲存並繼續」時，儲存會清掉已存情境，這次匯出就不再送出。
for (const choice of ['stay', 'continue', 'save']) test('journey 16: unsaved account categories are asked about before the export runs the prescreen (' + choice + ')', async () => {
  const f = await openExport({ prescreen: null });
  const editor = f.window.JetUi.accountMappingEditor, calls = [];
  editor.pendingCount = () => 1;
  editor.confirmPending = (consequence, label) => {
    calls.push([consequence, label]);
    if (choice === 'save') {
      f.store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true, accountMapping: false },
        staleState: { validation: false, prescreen: true, filter: false }, reportArtifacts: [] });
    }
    return Promise.resolve(choice !== 'stay');
  };
  const from = f.posted.length;
  pressExport(f); await flush();
  assert.deepEqual(calls, [['預篩選只會使用已儲存的分類。', '先不儲存，直接執行']]);
  // 儲存清掉情境後畫面回到第五步，第五步會自己查詢資料預覽；這裡只看匯出的三個 action。
  const exportSequence = exportActions(f, from).filter(action => ['prescreen.run', 'export.prescreenReport', 'export.workpaperStream'].includes(action));
  assert.deepEqual(exportSequence, choice === 'continue' ? ['prescreen.run'] : [],
    choice === 'save' ? 'saving clears the scenarios, so this export stops' : 'nothing is sent before the auditor chooses to continue');
});

test('journey 16: when validation says the data cannot continue, the step says only the workpaper would be made and export stays off', async () => {
  const reason = '合成原因：後端判定這份資料不能繼續';
  const f = await openExport({ prescreen: null, eligibility: { isEligible: false, reason, warning: null } });
  // 說明本次只產生工作底稿（只比對意思），原因照後端原樣。
  assert.match(notice(f), /合成原因：後端判定這份資料不能繼續/);
  assert.match(notice(f), /只會產生工作底稿/);
  assert.equal(exportButton(f).disabled, true);
  assert.doesNotMatch(exportButton(f).text, /預篩選/);
});

test('journey 16: if the case, the validation or the saved scenarios change during export, nothing more is sent and finished reports stay listed', async () => {
  // 預篩選還在執行時換了案件：不再產生預篩選報告。
  let f = await openExport({ prescreen: null });
  let from = f.posted.length;
  pressExport(f); await flush();
  f.store.setProject({ ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31' });
  f.reply('prescreen.run', plain(remapPrescreen('p-new'))); await flush();
  assert.deepEqual(exportActions(f, from), ['prescreen.run']);
  assert.equal(f.state().busy, false);
  // 使用者 2026-10-13 勾選頁確認 17（final-wording.json），2026-10-14 第三輪 R3-27 括號後加句號；作業名稱只確認提到工作底稿。
  const cancelled = f.messages()[0].text.match(/^(.+)已取消，已完成的部分仍保留（耗時[^）]*）。$/);
  assert.ok(cancelled !== null, 'the confirmed cancellation sentence: ' + f.messages()[0].text);
  assert.match(cancelled[1], /工作底稿/);

  // 預篩選報告還在產生時重新驗證：不再產生工作底稿。
  f = await openExport({ prescreen: 'p1' });
  from = f.posted.length;
  pressExport(f); await flush();
  f.store.setLastRun('validate', validation('v2'));
  const lateReport = artifact('prescreen-late', 'prescreenReport', { prescreenRunId: 'p1' });
  f.reply('export.prescreenReport', { artifact: lateReport, reportArtifacts: [lateReport] }); await flush();
  assert.deepEqual(exportActions(f, from), ['export.prescreenReport']);

  // 工作底稿還在產生時改了情境：已完成的預篩選報告留在清單，晚到的工作底稿不算數。
  f = await openExport({ prescreen: 'p1' });
  from = f.posted.length;
  pressExport(f); await flush();
  const report = artifact('prescreen-1', 'prescreenReport', { prescreenRunId: 'p1' });
  f.reply('export.prescreenReport', { artifact: report, reportArtifacts: [report] }); await flush();
  f.store.setFilterResultRef({ revision: 'r2', populationScope: 'auditPeriod' });
  const paper = artifact('paper-late', 'workingPaper', { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] });
  f.reply('export.workpaperStream', { artifact: paper, reportArtifacts: [report, paper], sheetStats: [{ sheetName: '合成情境表', rowsWritten: 2 }] }); await flush();
  assert.deepEqual(exportActions(f, from), ['export.prescreenReport', 'export.workpaperStream']);
  assert.deepEqual(plain(f.state().reportArtifacts.map(a => a.artifactId)), ['prescreen-1']);
  assert.ok(step(f).querySelector('.preview-table') === null, 'the late workpaper statistics are not shown');
  // 已完成的保留（只比對意思）。
  assert.match(f.messages()[0].text, /已完成的部分仍保留/);
  assert.equal(f.messages()[0].level, 'info');
});

test('journey 16: export progress shows each reported phase on the busy screen without percentages or early success, and odd progress events are ignored', async () => {
  const f = await openExport({ prescreen: 'p1' });
  toggleReport(f, false);
  pressExport(f); await flush();
  const before = f.messages().length;
  const label = () => f.$('[data-bind="busy-label"]').textContent;
  const progress = (phase, sheetName, sheetsCompleted, rowsWritten) => ({ artifactKind: 'workingPaper', phase, sheetName, sheetsCompleted, rowsWritten, elapsedMilliseconds: 4200 });
  const seen = [];
  for (const [event, meaning] of [
    [progress('preparingData', null, 0, 0), /準備資料/],
    [progress('writingSheet', '合成情境表', 1, 1500), /寫入「合成情境表」.*1 張工作表.*1,500 列/],
    [progress('finalizingWorkbook', null, 2, 3000), /完成檔案.*2 張工作表.*3,000 列/],
    [progress('publishingArtifact', null, 2, 3000), /存進案件資料夾/]
  ]) {
    f.emit('export.progress', event);
    assert.match(f.state().busyDetail, meaning);
    assert.match(label(), meaning, 'the busy screen shows the phase');
    seen.push(f.state().busyDetail);
  }
  // 四個階段的進度句：使用者 2026-10-13 勾選頁確認 PROGRESS（final-wording.json），經過時間的寫法是 2026-10-14 第三輪 R3-26。
  assert.deepEqual(seen, [
    '工作底稿：正在準備資料，經過 4.2 秒',
    '工作底稿：正在寫入「合成情境表」，已完成 1 張工作表、寫入 1,500 列，經過 4.2 秒',
    '工作底稿：正在完成檔案，已完成 2 張工作表、寫入 3,000 列，經過 4.2 秒',
    '工作底稿：正在存進案件資料夾，已完成 2 張工作表、寫入 3,000 列，經過 4.2 秒'
  ]);
  // 沒有總量可以推算比例，也不提前說匯出完成。
  for (const text of seen) assert.doesNotMatch(text, /%|％|百分|匯出(?:成功|完成)/);
  // 契約的六個欄位少一個、多一個、或數字是負的，都不採用。
  const last = f.state().busyDetail;
  const valid = progress('writingSheet', '合成情境表', 3, 4500);
  const missing = { ...valid }; delete missing.elapsedMilliseconds;
  for (const odd of [missing, { ...valid, rowsTotal: 9000 }, { ...valid, rowsWritten: -1 }, { ...valid, sheetsCompleted: 1.5 }, { ...valid, phase: 'done' }]) {
    f.emit('export.progress', odd);
    assert.equal(f.state().busyDetail, last, 'an odd progress event leaves the busy screen as it was');
  }
  // 進度只更新忙碌畫面，不新增訊息。
  assert.equal(f.messages().length, before);
  // 正式後端匯出成功一定帶回這份底稿（ExportWorkpaperStreamHandler）；2026-10-15 前後端契合時改掉 artifact: null。
  const finished = artifact('paper-progress', 'workingPaper', { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] });
  f.reply('export.workpaperStream', { artifact: finished, reportArtifacts: [finished], sheetStats: [] }); await flush();
});

test('journey 16: the scenario choice says it uses the saved conditions, exporting needs no criteria report, and the step uses no internal wording', async () => {
  const f = await openExport({ prescreen: 'p1' });
  const text = step(f).textContent;
  // 「使用目前資料及已儲存條件；草稿不會自動納入。」：「已儲存」是 2026-10-03 用語統一 W10 的使用者裁定（計畫第 4 節 A5）。
  assert.match(text, /使用目前資料及已儲存條件；草稿不會自動納入。/);
  // 情境選擇說明底稿會標示符合的分錄並保留同傳票的參考分錄（只比對意思，原 WorkpaperSheetMirrorTests.ExportStep_… 的句子沒有使用者裁定）。
  assert.match(text, /標示.*符合.*情境.*分錄/);
  assert.match(text, /同傳票/);
  // 沒有條件篩選報告也能匯出，不叫審計員回第五步先產生報告。
  assert.equal(f.state().reportArtifacts.some(a => a.kind === 'criteriaSelectionReport'), false);
  assert.equal(exportButton(f).disabled, false);
  for (const jargon of ['版本參照', '測試母體區', '有界預覽', 'CriteriaSelectionReport', '匯出時會依目前資料重新計算']) {
    assert.equal(text.includes(jargon), false, 'no internal wording: ' + jargon);
  }
  // 清除全部情境時不能匯出，並說明要至少選一個（只比對意思）；全選後恢復。
  step(f).querySelector('[data-action="clear-scenario-selection"]').dispatch('click');
  assert.equal(exportButton(f).disabled, true);
  const error = step(f).querySelector('[data-bind="scenario-selection-error"]');
  assert.equal(error.hidden, false);
  assert.match(error.textContent, /至少.*情境/);
  step(f).querySelector('[data-scenario-position="2"]').checked = true;
  step(f).querySelector('[data-scenario-position="2"]').dispatch('change');
  toggleReport(f, false);
  pressExport(f); await flush();
  assert.deepEqual(plain(f.request('export.workpaperStream').payload), { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: [2] });
});
