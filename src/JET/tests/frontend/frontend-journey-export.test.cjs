'use strict';
// 情境 16：第六步匯出工作底稿。
//
// 審計員的操作：到第六步依預篩選的狀態匯出：有目前的預篩選結果、還沒有、結果已過期、資料驗證判定不能繼續；
// 取消勾選「預篩選報告」；匯出途中換了案件、重新驗證或改了情境；看匯出途中忙碌畫面的進度。
// 匯出失敗時第六步標題下方的紅色提示框，由 frontend-workflow.test.cjs 的「a failed workingPaper export shows a red notice…」
// 經同一個匯出按鈕核對，這裡不重寫；按「取消作業」保留已完成的預篩選報告，
// 由 frontend-workflow.test.cjs 的「cancellation through the actual export button…」核對。
//
// 夾具：支援模組的 fullAppFixture，經 bootFullApp（本檔叫 boot）開啟 JET：
// 整個 index.html 的 body 標記與全部前端腳本，正式的 jet-api.js 經假的 chrome.webview 收發，app.js 照瀏覽器執行正式的 init()；
// 匯出進度事件經 jet-api.js 正式的事件通道送進 app.js 的訂閱。usageJourneyFixture 與 remapAppFixture
// 都沒有 app.js 的進度訂閱，所以這裡用整個畫面。
//
// 逐字比對的只有已定案的用字，其他文字只比對意思。預期值一律寫死；action payload 用 deepEqual 精確比對，
// 並確認沒有 `__` 鍵；斷言的失敗值不是假 DOM 節點。第六步的作業名稱的用字沒有定案，放在已定案的句型裡時只比對意思。
// 每個測試都有 30 秒上限：改壞檢查時若有請求永遠等不到回應，測試直接失敗，不會拖到改壞工具的時間上限。
const nodeTest = require('node:test');
const test = (name, fn) => nodeTest(name, { timeout: 30000 }, fn);
const assert = require('node:assert/strict');
const { plain, savedBlankDescription, remapPrescreen, flush, bootFullApp: boot, noDunderKeys } = require('./support/frontend-fixtures.cjs');

const CASE = { projectId: '合成匯出案', databaseProvider: 'sqlite' };
const EXPORT_INDEX = 5;

function validation(resultId, eligibility = { isEligible: true, reason: null, warning: null }) {
  return { resultRef: { resultId, generatedUtc: '2026-01-01T00:00:00Z' }, completenessTest: { eligibility } };
}

function listedReport(reportId, kind, sourceRef = {}) {
  return { reportId, kind, fileName: reportId + '.xlsx', fileState: 'present', stale: false,
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
  f.store.setResult('validate', validation('v1', eligibility));
  if (prescreen) f.store.setResult('prescreen', plain(remapPrescreen(prescreen)));
  f.store.setSavedScenarios([savedBlankDescription('第一個'), savedBlankDescription('第二個')]);
  f.store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' });
  f.store.setStepIndex(EXPORT_INDEX);
  await flush();
  return f;
}

// 第六步目前的內容。
function step(f) { const node = f.$('.stepflow-item--current .stepflow-item__body'); assert.ok(node !== null, 'step six is shown'); return node; }

test('exported scenario selection uses the shared deselection wording', async () => {
  const f = await openExport();
  assert.equal(step(f).querySelector('[data-action="clear-scenario-selection"]').textContent, '全部取消');
});
function exportButton(f) { const node = step(f).querySelector('[data-action="export-working-paper"]'); assert.ok(node !== null, 'export button rendered'); return { text: node.textContent, disabled: node.disabled }; }
function pressExport(f) { const node = step(f).querySelector('[data-action="export-working-paper"]'); assert.equal(node.disabled, false, 'export button enabled'); node.dispatch('click'); }
function toggleReport(f, checked) { const node = step(f).querySelector('[data-bind="include-prescreen-report"]'); assert.ok(node !== null, 'prescreen report option rendered'); node.checked = checked; node.dispatch('change'); }
function notice(f) { const node = step(f).querySelector('[data-bind="prescreen-export-notice"]'); return node === null ? null : node.textContent; }
function exportActions(f, from = 0) { return f.posted.slice(from).map(r => r.action).filter(action => !['log.append', 'project.saveProgress'].includes(action)); }

const WORKING_PAPER_PAYLOAD = { validationResultId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] };

test('the prescreen report is ticked by default and named on the button, unticking makes only the working paper, and reopening the case ticks it again', async () => {
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
  assert.deepEqual(exportActions(f, from), ['export.workingPaperStream']);
  const sent = f.request('export.workingPaperStream');
  assert.deepEqual(plain(sent.payload), WORKING_PAPER_PAYLOAD);
  assert.ok(noDunderKeys(sent.payload));
  f.clock.advance(3000);
  const paper = listedReport('paper-1', 'workingPaper', { validationResultId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] });
  f.reply(sent, { publishedReport: paper, reports: [paper], sheetStats: [{ sheetName: '合成情境表', rowsWritten: 12345 }, { sheetName: '合成摘要', rowsWritten: 7 }] }); await flush();
  assert.deepEqual(exportActions(f, from), ['export.workingPaperStream'], 'nothing else is sent afterwards');
  // 工作表統計：名稱與千分位筆數。
  const rows = step(f).querySelectorAll('.preview-table tbody tr').map(tr => tr.querySelectorAll('td').map(td => td.textContent));
  assert.deepEqual(rows, [['合成情境表', '12,345'], ['合成摘要', '7']]);
  // 完成句接上耗時，成功時只有這一則。
  assert.deepEqual(f.messages()[0], { text: '已在案件資料夾產生工作底稿（2 張工作表），耗時 3.0 秒。', level: 'info' });

  // 回首頁再開同一個案件：預篩選報告又是預設勾選。
  f.click('[data-action="app-back-picker"]'); await flush();
  f.reply('project.saveProgress', {}); await flush();
  f.reply('project.releaseLock', {}); await flush();
  f.reply(f.sent('project.listLocal').at(-1), { projects: [CASE] }); await flush();
  f.click('[data-action="picker-open"]');
  f.reply(f.sent('project.load').at(-1), { project: { ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 1 } }); await flush();
  f.reply(f.sent('log.recent').at(-1), { messages: [] }); await flush();
  f.store.setResult('validate', validation('v1'));
  f.store.setResult('prescreen', plain(remapPrescreen('p1')));
  f.store.setSavedScenarios([savedBlankDescription('第一個')]);
  f.store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' });
  f.store.setStepIndex(EXPORT_INDEX); await flush();
  assert.equal(step(f).querySelector('[data-bind="include-prescreen-report"]').checked, true);
});

test('with a current prescreen result the report for that run is made first and the working paper only after it, and both are listed', async () => {
  const f = await openExport({ prescreen: 'p1' });
  assert.equal(notice(f), null, 'nothing to announce when the prescreen result is current');
  const from = f.posted.length;
  pressExport(f); await flush();
  assert.deepEqual(exportActions(f, from), ['export.prescreenReport']);
  const report = f.request('export.prescreenReport');
  assert.deepEqual(plain(report.payload), { resultId: 'p1' });
  const prescreenReport = listedReport('prescreen-1', 'prescreenReport', { prescreenResultId: 'p1' });
  f.reply(report, { publishedReport: prescreenReport, reports: [prescreenReport] }); await flush();
  assert.deepEqual(exportActions(f, from), ['export.prescreenReport', 'export.workingPaperStream']);
  const sent = f.request('export.workingPaperStream');
  assert.deepEqual(plain(sent.payload), WORKING_PAPER_PAYLOAD);
  const paper = listedReport('paper-1', 'workingPaper', { validationResultId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] });
  f.reply(sent, { publishedReport: paper, reports: [prescreenReport, paper], sheetStats: [{ sheetName: '合成情境表', rowsWritten: 2 }] }); await flush();
  assert.deepEqual(plain(f.state().reports.map(a => a.reportId)), ['prescreen-1', 'paper-1']);
  assert.match(step(f).textContent, /paper-1\.xlsx/);
  // 這兩句的用字已定案。
  assert.deepEqual(f.messages().slice(0, 2).map(m => m.text), ['已在案件資料夾產生工作底稿（1 張工作表），耗時不到 0.1 秒。', '已在案件資料夾產生預篩選報告。']);
});

for (const situation of ['none', 'stale']) test('with ' + (situation === 'none' ? 'no prescreen result yet' : 'an outdated prescreen result') + ' the step says it will run the prescreen first, then runs it, reports on the new run and makes the working paper', async () => {
  const f = await openExport({ prescreen: situation === 'none' ? null : 'p-old' });
  if (situation === 'stale') {
    // 例如科目分類儲存後，後端回報預篩選結果已過期。
    f.store.applyMutationEffects({ staleState: { validation: false, prescreen: true, filter: false } }); await flush();
  }
  // 按下之前就說明會先執行預篩選。
  assert.equal(notice(f), '尚無預篩選結果，匯出時會先執行預篩選；資料量大時需要數分鐘到十餘分鐘。');
  assert.match(exportButton(f).text, /先執行預篩選/);
  const from = f.posted.length;
  pressExport(f); await flush();
  assert.deepEqual(exportActions(f, from), ['prescreen.run']);
  assert.deepEqual(plain(f.request('prescreen.run').payload), {});
  f.reply('prescreen.run', plain(remapPrescreen('p-new'))); await flush();
  assert.deepEqual(exportActions(f, from), ['prescreen.run', 'export.prescreenReport']);
  assert.deepEqual(plain(f.request('export.prescreenReport').payload), { resultId: 'p-new' });
  const prescreenReport = listedReport('prescreen-new', 'prescreenReport', { prescreenResultId: 'p-new' });
  f.reply('export.prescreenReport', { publishedReport: prescreenReport, reports: [prescreenReport] }); await flush();
  assert.deepEqual(exportActions(f, from), ['prescreen.run', 'export.prescreenReport', 'export.workingPaperStream']);
  assert.deepEqual(plain(f.request('export.workingPaperStream').payload), WORKING_PAPER_PAYLOAD);
});

// 上游修改清掉已存情境後，第六步不顯示上一次匯出的工作表與資料列數；
// 重新儲存情境以後也不顯示，因為那張表描述的是舊版本的匯出。
test('the sheet table of the last export goes away once an upstream change clears the scenarios', async () => {
  const f = await openExport();
  toggleReport(f, false);
  pressExport(f); await flush();
  const paper = listedReport('paper-1', 'workingPaper', { validationResultId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] });
  f.reply('export.workingPaperStream', { publishedReport: paper, reports: [paper], sheetStats: [{ sheetName: '合成情境表', rowsWritten: 2 }] }); await flush();
  assert.equal(step(f).querySelectorAll('.preview-table tbody tr').length, 1);
  f.store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true } });
  f.store.setSavedScenarios([savedBlankDescription('第一個'), savedBlankDescription('第二個')]);
  f.store.setFilterResultRef({ revision: 'r2', populationScope: 'auditPeriod' });
  f.store.setStepIndex(EXPORT_INDEX); await flush();
  assert.equal(step(f).querySelectorAll('.preview-table tbody tr').length, 0, 'the table belonged to the export of the older scenarios');
});

// 第六步要先補跑預篩選時，和第四步的「執行預篩選」一樣，先詢問還沒儲存的科目分類。
// 選「留在這裡」什麼都不送；選擇繼續才執行預篩選。選「儲存並繼續」時，儲存會清掉已存情境，匯出就此停止。
for (const choice of ['stay', 'continue', 'save']) test('unsaved account categories are asked about before the export runs the prescreen (' + choice + ')', async () => {
  const f = await openExport({ prescreen: null });
  const editor = f.window.JetUi.accountMappingEditor, calls = [];
  editor.pendingCount = () => 1;
  editor.confirmPending = (consequence, label) => {
    calls.push([consequence, label]);
    if (choice === 'save') {
      f.store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true, accountMapping: false },
        staleState: { validation: false, prescreen: true, filter: false }, reports: [] });
    }
    return Promise.resolve(choice !== 'stay');
  };
  const from = f.posted.length;
  pressExport(f); await flush();
  assert.deepEqual(calls, [['預篩選只會使用已儲存的分類。', '先不儲存，直接執行']]);
  // 儲存清掉情境後畫面回到第五步，第五步會自己查詢資料預覽；這裡只看匯出的三個 action。
  const exportSequence = exportActions(f, from).filter(action => ['prescreen.run', 'export.prescreenReport', 'export.workingPaperStream'].includes(action));
  assert.deepEqual(exportSequence, choice === 'continue' ? ['prescreen.run'] : [],
    choice === 'save' ? 'saving clears the scenarios, so this export stops' : 'nothing is sent before the auditor chooses to continue');
});

test('when validation says the data cannot continue, the step says only the working paper would be made and export stays off', async () => {
  const reason = '合成原因：後端判定這份資料不能繼續';
  const f = await openExport({ prescreen: null, eligibility: { isEligible: false, reason, warning: null } });
  // 說明本次只產生工作底稿（只比對意思），原因照後端原樣。
  assert.match(notice(f), /合成原因：後端判定這份資料不能繼續/);
  assert.match(notice(f), /只會產生工作底稿/);
  assert.equal(exportButton(f).disabled, true);
  assert.doesNotMatch(exportButton(f).text, /預篩選/);
});

test('if the case, the validation or the saved scenarios change during export, nothing more is sent and finished reports stay listed', async () => {
  // 預篩選還在執行時換了案件：不再產生預篩選報告。
  let f = await openExport({ prescreen: null });
  let from = f.posted.length;
  pressExport(f); await flush();
  f.store.setProject({ ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31' });
  f.reply('prescreen.run', plain(remapPrescreen('p-new'))); await flush();
  assert.deepEqual(exportActions(f, from), ['prescreen.run']);
  assert.equal(f.state().busy, false);
  // 作業名稱只確認提到工作底稿。
  const cancelled = f.messages()[0].text.match(/^(.+)已取消，已完成的部分仍保留（耗時[^）]*）。$/);
  assert.ok(cancelled !== null, 'the confirmed cancellation sentence: ' + f.messages()[0].text);
  assert.match(cancelled[1], /工作底稿/);

  // 預篩選報告還在產生時重新驗證：不再產生工作底稿。
  f = await openExport({ prescreen: 'p1' });
  from = f.posted.length;
  pressExport(f); await flush();
  f.store.setResult('validate', validation('v2'));
  const lateReport = listedReport('prescreen-late', 'prescreenReport', { prescreenResultId: 'p1' });
  f.reply('export.prescreenReport', { publishedReport: lateReport, reports: [lateReport] }); await flush();
  assert.deepEqual(exportActions(f, from), ['export.prescreenReport']);

  // 工作底稿還在產生時改了情境：已完成的預篩選報告留在清單，晚到的工作底稿不算數。
  f = await openExport({ prescreen: 'p1' });
  from = f.posted.length;
  pressExport(f); await flush();
  const report = listedReport('prescreen-1', 'prescreenReport', { prescreenResultId: 'p1' });
  f.reply('export.prescreenReport', { publishedReport: report, reports: [report] }); await flush();
  f.store.setFilterResultRef({ revision: 'r2', populationScope: 'auditPeriod' });
  const paper = listedReport('paper-late', 'workingPaper', { validationResultId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] });
  f.reply('export.workingPaperStream', { publishedReport: paper, reports: [report, paper], sheetStats: [{ sheetName: '合成情境表', rowsWritten: 2 }] }); await flush();
  assert.deepEqual(exportActions(f, from), ['export.prescreenReport', 'export.workingPaperStream']);
  assert.deepEqual(plain(f.state().reports.map(a => a.reportId)), ['prescreen-1']);
  assert.ok(step(f).querySelector('.preview-table') === null, 'the late workingPaper statistics are not shown');
  // 已完成的保留（只比對意思）。
  assert.match(f.messages()[0].text, /已完成的部分仍保留/);
  assert.equal(f.messages()[0].level, 'info');
});

test('export progress shows each reported phase on the busy screen without percentages or early success, and odd progress events are ignored', async () => {
  const f = await openExport({ prescreen: 'p1' });
  toggleReport(f, false);
  pressExport(f); await flush();
  const before = f.messages().length;
  const label = () => f.$('[data-bind="busy-label"]').textContent;
  const progress = (phase, sheetName, sheetsCompleted, rowsWritten) => ({ reportKind: 'workingPaper', phase, sheetName, sheetsCompleted, rowsWritten, elapsedMilliseconds: 4200 });
  const seen = [];
  for (const [event, meaning] of [
    [progress('preparingData', null, 0, 0), /準備資料/],
    [progress('writingSheet', '合成情境表', 1, 1500), /寫入「合成情境表」.*1 張工作表.*1,500 列/],
    [progress('finalizingWorkbook', null, 2, 3000), /完成檔案.*2 張工作表.*3,000 列/],
    [progress('publishingReport', null, 2, 3000), /存進案件資料夾/]
  ]) {
    f.emit('export.progress', event);
    assert.match(f.state().busyDetail, meaning);
    assert.match(label(), meaning, 'the busy screen shows the phase');
    seen.push(f.state().busyDetail);
  }
  // 四個階段的進度句。
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
  // 正式後端匯出成功一定帶回這份底稿（ExportWorkingPaperStreamHandler）。
  const finished = listedReport('paper-progress', 'workingPaper', { validationResultId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2] });
  f.reply('export.workingPaperStream', { publishedReport: finished, reports: [finished], sheetStats: [] }); await flush();
});

test('the scenario choice says it uses the saved conditions, exporting needs no criteria report, and the step uses no internal wording', async () => {
  const f = await openExport({ prescreen: 'p1' });
  const text = step(f).textContent;
  // 第六步沒有常駐的「使用目前資料及已儲存條件；草稿不會自動納入。」與底稿標示方式的說明；
  // 只有「進階條件篩選」真的有尚未儲存的修改時，才提醒要先儲存成情境。
  assert.doesNotMatch(text, /草稿不會自動納入|同傳票/);
  assert.equal(step(f).querySelector('[data-bind="export-unsaved-draft"]'), null, 'no reminder without unsaved changes');
  const hasUnsavedScenarioChanges = f.ui.FilterStep.hasUnsavedScenarioChanges;
  f.ui.FilterStep.hasUnsavedScenarioChanges = () => true;
  f.store.touch(); await flush();
  assert.match(step(f).querySelector('[data-bind="export-unsaved-draft"]').textContent, /尚未儲存.*儲存成情境.*納入底稿/);
  f.ui.FilterStep.hasUnsavedScenarioChanges = hasUnsavedScenarioChanges;
  f.store.touch(); await flush();
  // 沒有條件篩選報告也能匯出，不叫審計員回第五步先產生報告。
  assert.equal(f.state().reports.some(a => a.kind === 'criteriaSelectionReport'), false);
  assert.equal(exportButton(f).disabled, false);
  for (const jargon of ['版本參照', '測試母體區', '有界預覽', 'CriteriaSelectionReport', '匯出時會依目前資料重新計算']) {
    assert.equal(text.includes(jargon), false, 'no internal wording: ' + jargon);
  }
  // 一個情境都沒勾選時仍能匯出，底稿第三步與第四步留空。
  // 全部取消勾選後，匯出按鈕可按，也沒有要求至少選一個的提示。
  step(f).querySelector('[data-action="clear-scenario-selection"]').dispatch('click');
  assert.equal(exportButton(f).disabled, false);
  assert.equal(step(f).querySelector('[data-bind="scenario-selection-error"]'), null);
  step(f).querySelector('[data-scenario-position="2"]').checked = true;
  step(f).querySelector('[data-scenario-position="2"]').dispatch('change');
  toggleReport(f, false);
  pressExport(f); await flush();
  assert.deepEqual(plain(f.request('export.workingPaperStream').payload), { validationResultId: 'v1', scenarioRevision: 'r1', scenarioPositions: [2] });
});
