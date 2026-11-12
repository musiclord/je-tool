'use strict';
// 情境 3：開案、換案、離開與重新開案。
//
// 審計員的操作：開一個本機案件，再從案件清單換開另一個；在案件裡快速切換步驟；按頁首與第四步的「開啟案件資料夾」；
// 作業進行中想回首頁；重新開案後看到情境版本與報告清單，直接到第六步匯出。
// 夾具：support/frontend-fixtures.cjs 的 remapAppFixture（正式 state、ui-core、各步驟與 app.js 的頁首綁定、render、案件清單）。
//
// 前面的測試不斷言訊息面板的句子（包括離開失敗、進度儲存失敗）與後端原因句子，也不測第五步的儲存與移除流程；
// 離開失敗與進度儲存失敗的訊息在本檔最後核對。
// 預期值一律寫死，不讀正式常數；action payload 用 deepEqual 精確比對。
const test = require('node:test');
const assert = require('node:assert/strict');
const { remapAppFixture, settle, plain, savedBlankDescription, validationResultData } = require('./support/frontend-fixtures.cjs');

const committedMapping = { gl: { mapping: {}, amountMode: 'dual', committedUtc: '2026-01-02T03:04:00Z' },
  tb: { mapping: {}, changeMode: 'debitCredit', committedUtc: '2026-01-02T03:05:00Z' } };
const importedState = { gl: { batchId: 'gl-1', rowCount: 2, columns: ['Account'] }, tb: { batchId: 'tb-1', rowCount: 2, columns: ['Account'] } };

// 開案回應：只控制後端邊界，畫面與 Store 都跑正式程式。
function loadResponse(projectId, databaseProvider, extra = {}) {
  return { project: { projectId, databaseProvider, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 1 }, ...extra };
}

async function openThroughUi(f, projectId, response) {
  const opening = f.window.JetUi.openProject(projectId);
  const load = f.request('projectLoad');
  load.resolve(response); await settle();
  f.request('logRecent').resolve({ messages: [] }); await opening; await settle();
  return load;
}

function liveIntervals(f) { return f.intervals.filter(entry => !entry.cleared); }

// 開本機案件不設計時器，開案請求只帶案件名稱。
test('opening a case starts no background timer, and the case list sends only the case name', async () => {
  const f = remapAppFixture(); f.store.resetWorkflow(); f.store.setView('picker');

  // 本機案件不定時送心跳，工作鎖由本機檔案鎖負責。
  const before = f.intervals.length;
  const first = await openThroughUi(f, 'sqlite-synthetic', loadResponse('sqlite-synthetic', 'sqlite'));
  assert.deepEqual(plain(first.payload), { projectId: 'sqlite-synthetic' });
  assert.equal(f.state.project.projectId, 'sqlite-synthetic');
  assert.equal(f.intervals.length, before, 'a local case starts no timer');

  // 從案件清單點開另一個案件：請求只帶案件名稱。
  f.store.setView('picker');
  f.store.setProjects([{ projectId: 'second-synthetic', databaseProvider: 'sqlite' }]);
  f.app.renderPicker(f.state);
  const open = f.$('[data-action="picker-open"][data-project-id="second-synthetic"]');
  assert.ok(open, 'case row rendered');
  const loadsBefore = f.requests.filter(r => r.action === 'projectLoad').length;
  open.fire('click');
  const loads = f.requests.filter(r => r.action === 'projectLoad');
  assert.equal(loads.length, loadsBefore + 1);
  assert.deepEqual(plain(loads.at(-1).payload), { projectId: 'second-synthetic' });
  loads.at(-1).resolve(loadResponse('second-synthetic', 'sqlite')); await settle();
  f.request('logRecent').resolve({ messages: [] }); await settle();
  assert.equal(f.state.project.projectId, 'second-synthetic');
  assert.equal(liveIntervals(f).length, 0, 'no timer is running');
});

test('switching steps quickly saves only the first and the last position, each as { currentStep }', async () => {
  const f = remapAppFixture(); f.store.setView('workflow');
  for (const kind of ['gl', 'tb']) {
    f.store.setImportResult(kind, importedState[kind]);
    f.store.setMappingCommitted(kind, { mapping: {}, mode: kind === 'gl' ? 'dual' : 'debitCredit' });
  }
  const saves = () => f.requests.filter(r => r.action === 'projectSaveProgress');
  f.window.JetUi.gotoStep(2); f.window.JetUi.gotoStep(3); f.window.JetUi.gotoStep(4);
  await settle();
  assert.equal(f.state.currentStepIndex, 4, 'the screen follows every click');
  assert.equal(saves().length, 1, 'only one save is in flight');
  assert.deepEqual(plain(saves()[0].payload), { currentStep: 2 });
  saves()[0].resolve({}); await settle();
  assert.equal(saves().length, 2, 'the latest position is sent after the first save returns');
  assert.deepEqual(plain(saves()[1].payload), { currentStep: 4 }, 'the step in between is skipped');
  saves()[1].resolve({}); await settle();
  assert.equal(saves().length, 2, 'nothing else is sent');
});

test('the header "open case folder" button only shows inside a case and asks for the server-resolved folder, like the step-four one', async () => {
  const f = remapAppFixture();
  const folder = () => f.$('[data-action="open-project-folder"]');
  assert.ok(folder(), 'header button comes from index.html');
  assert.equal(folder().hidden, true, 'hidden before any case is open');

  f.store.setView('picker'); f.app.render();
  assert.equal(folder().hidden, true, 'hidden on the case list');
  f.store.setView('workflow'); f.app.render();
  assert.equal(folder().hidden, false, 'shown inside a case');

  folder().fire('click'); await settle();
  const opens = f.requests.filter(r => r.action === 'hostOpenFolder');
  assert.equal(opens.length, 1);
  assert.deepEqual(plain(opens[0].payload), { target: 'projectFolder' }, 'no path is sent from the screen');
  opens[0].resolve({}); await settle();
  assert.equal(f.state.busy, false);

  // 第四步科目配對區的同名按鈕送一樣的請求。
  f.store.setImportResult('gl', importedState.gl); f.store.setImportResult('tb', importedState.tb);
  f.store.setMappingCommitted('gl', { mapping: {}, mode: 'dual' }); f.store.setMappingCommitted('tb', { mapping: {}, mode: 'debitCredit' });
  f.store.setResult('validate', validationResultData('v1')); f.draw('validate');
  // 科目配對檔的按鈕在「用 Excel 配對」分頁裡：審計員先切到這個分頁。
  f.$('[data-action="toggle-account-excel"]').fire('click'); f.draw('validate');
  const stepFour = f.$('[data-action="open-account-mapping-folder"]');
  assert.ok(stepFour, 'step-four folder button rendered once the GL mapping is confirmed');
  stepFour.fire('click'); await settle();
  const second = f.requests.filter(r => r.action === 'hostOpenFolder');
  assert.equal(second.length, 2);
  assert.deepEqual(plain(second[1].payload), { target: 'projectFolder' });
  second[1].resolve({}); await settle();

  // 第四步與第五步的報告清單：「開啟資料夾」只送後端解析的產物識別碼。
  const reports = [{ reportId: 'validation-v1', kind: 'validationReport', fileName: 'validation.xlsx', fileState: 'present', stale: false,
    generatedUtc: '2026-01-01T00:00:00Z', sourceRef: { validationResultId: 'v1' } },
  { reportId: 'criteria-r1', kind: 'criteriaSelectionReport', fileName: 'criteria.xlsx', fileState: 'present', stale: false,
    generatedUtc: '2026-01-01T00:00:00Z', sourceRef: { validationResultId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2, 3] } }];
  f.store.setReports(reports);
  for (const [step, reportId] of [['validate', 'validation-v1'], ['filter', 'criteria-r1']]) {
    const body = f.draw(step);
    const reveal = body.querySelector('[data-open-report="' + reportId + '"]');
    assert.ok(reveal, step + ': report folder button rendered');
    reveal.fire('click'); await settle();
    const latest = f.requests.filter(r => r.action === 'hostOpenFolder').at(-1);
    assert.deepEqual(plain(latest.payload), { reportId }, step + ': only the server-resolved report id is sent');
    latest.resolve({}); await settle();
  }
});

test('leaving the case while an operation runs stays in the case and points at the cancel button only when one can be cancelled', async () => {
  for (const action of ['app-back-picker', 'app-exit']) {
    const f = remapAppFixture(), project = f.state.project; f.store.setView('workflow');
    f.store.setBusy(true, '執行資料驗證'); f.store.setBusyRequest('synthetic-request');
    f.$('[data-action="' + action + '"]').fire('click'); await settle();
    assert.equal(f.requests.length, 0, action + ': nothing is saved or released');
    assert.equal(f.state.view, 'workflow'); assert.equal(f.state.project, project);
    // 忙碌時畫面被遮住，提示寫在忙碌區看得到的 busyDetail，指向「取消作業」這個按鈕名稱。
    assert.match(f.state.busyDetail || '', /「取消作業」/, action + ': busy detail names the cancel button');

    f.store.setBusy(false); f.store.setBusy(true, '產生工作底稿');
    f.$('[data-action="' + action + '"]').fire('click'); await settle();
    assert.equal(f.requests.length, 0);
    assert.ok(f.state.busyDetail, action + ': busy detail explains why it stayed');
    assert.doesNotMatch(f.state.busyDetail, /取消作業/, action + ': without a cancellable request the detail does not send the auditor to cancel');
  }
});

test('reopening a case brings back the saved scenario version and report list, and step six exports that version', async () => {
  const f = remapAppFixture({ redraw: false }); f.store.resetWorkflow(); f.store.setView('picker');
  const paper = { reportId: 'paper-r9', kind: 'workingPaper', fileName: 'paper-r9.xlsx', fileState: 'asPublished', stale: false,
    generatedUtc: '2026-02-03T04:05:00Z', sourceRef: { validationResultId: 'v9', scenarioRevision: 'r9', scenarioPositions: [1, 2] } };
  const report = { reportId: 'validation-v9', kind: 'validationReport', fileName: 'validation-v9.xlsx', fileState: 'present', stale: false,
    generatedUtc: '2026-02-03T04:00:00Z', sourceRef: { validationResultId: 'v9' } };
  await openThroughUi(f, 'resume-synthetic', {
    ...loadResponse('resume-synthetic', 'sqlite'),
    importState: importedState, mapping: committedMapping,
    validationResult: { resultRef: { resultId: 'v9' }, completenessTest: { eligibility: { isEligible: true } } }, prescreenResult: null,
    filterScenarios: [savedBlankDescription('甲'), savedBlankDescription('乙')],
    filterResultRef: { revision: 'r9', populationScope: 'auditPeriod' },
    reports: [paper, report]
  });

  assert.equal(f.state.view, 'workflow');
  assert.equal(f.state.filter.populationScope, 'auditPeriod', 'the only runtime scope is the audit period');
  assert.deepEqual(plain(f.state.filterResultRef), { revision: 'r9', populationScope: 'auditPeriod' });
  assert.deepEqual(f.state.reports.map(a => a.reportId), ['paper-r9', 'validation-v9']);
  assert.equal(f.window.JetUi.stepGate(f.state, 5).ok, true, 'step six opens after reopening');

  const step = f.draw('export');
  const listed = step.querySelectorAll('.report-output [data-report-id]').map(node => node.getAttribute('data-report-id'));
  assert.deepEqual(listed, ['paper-r9'], 'the version list shows the reloaded workingPaper');
  assert.ok(step.querySelector('.completion'), 'the reloaded current workingPaper still completes the case');

  // 版本紀錄的「開啟資料夾」只送後端解析的產物識別碼，不送路徑。
  step.querySelector('[data-open-report="paper-r9"]').fire('click'); await settle();
  assert.deepEqual(plain(f.request('hostOpenFolder').payload), { reportId: 'paper-r9' });
  f.request('hostOpenFolder').resolve({}); await settle();

  // 只產生底稿（取消一併產生預篩選報告），匯出的情境版本就是重新開案帶回的 r9。
  f.change('[data-bind="include-prescreen-report"]', false);
  f.draw('export'); f.click('[data-action="export-working-paper"]'); await settle();
  assert.deepEqual(plain(f.request('exportWorkingPaperStream').payload),
    { validationResultId: 'v9', scenarioRevision: 'r9', scenarioPositions: [1, 2] });
  f.request('exportWorkingPaperStream').reject({ code: 'operation_cancelled', message: 'synthetic stop' }); await settle();
});

test('reopening a case whose saved result used a retired scope keeps step six locked instead of reviving that scope', async () => {
  const f = remapAppFixture(); f.store.resetWorkflow(); f.store.setView('picker');
  assert.equal(f.state.filter.populationScope, 'auditPeriod', 'a fresh workflow starts with the audit period');
  await openThroughUi(f, 'old-scope-synthetic', {
    ...loadResponse('old-scope-synthetic', 'sqlite'),
    importState: importedState, mapping: committedMapping,
    validationResult: { resultRef: { resultId: 'v9' }, completenessTest: { eligibility: { isEligible: true } } }, prescreenResult: null,
    filterScenarios: [savedBlankDescription('甲')],
    filterResultRef: { revision: 'r-old', populationScope: 'allProjected' }
  });
  assert.equal(f.state.filter.populationScope, 'auditPeriod');
  assert.equal(f.window.JetUi.stepGate(f.state, 5).ok, false, 'a result saved under another scope cannot be exported');
  assert.equal(f.window.JetUi.stepComplete(f.state, 4), false);
  const step = f.draw('export');
  assert.equal(step.querySelector('[data-action="export-working-paper"]').disabled, true);
});

// ---- 離開案件失敗時的訊息 ----
// 前面的測試沒有斷言的訊息句子在這裡逐字比對；後端原因一律用合成字串。
// 先存目前步驟、再放案件鎖、放鎖成功才離開的順序，另由 frontend-workflow.test.cjs 的「… waits for saving and lock release before departure」「… blocks departure while busy and preserves the project when lock release fails」測過。
const messageList = f => plain(f.state.messages.map(m => ({ text: m.text, level: m.level })));

for (const action of ['app-back-picker', 'app-exit']) test('leaving the case through ' + action + ' that cannot release the lock keeps the auditor in the case with the reason as an error', async () => {
  const f = remapAppFixture(), project = f.state.project; f.store.setView('workflow');
  f.click('[data-action="' + action + '"]'); await settle();
  f.request('projectSaveProgress').resolve({}); await settle();
  f.request('projectReleaseLock').reject({ code: 'synthetic', message: '合成：案件鎖沒有釋放' }); await settle();
  assert.ok(f.state.project === project, 'still in the same case');
  assert.equal(f.state.view, 'workflow');
  assert.equal(f.state.busy, false);
  assert.equal(f.requests.some(r => r.action === 'hostExitApp'), false);
  // 訊息分三級，離開案件失敗屬於錯誤。
  assert.deepEqual(messageList(f)[0], { text: '無法離開案件，已保留目前畫面：合成：案件鎖沒有釋放', level: 'error' });
});

test('leaving while the backend says another operation is running is an attention line, with the cancel hint only when there is something to cancel', async () => {
  // 後端回覆另一項作業還在進行、畫面上沒有可以取消的作業。
  let f = remapAppFixture(); f.store.setView('workflow');
  f.click('[data-action="app-back-picker"]'); await settle();
  f.request('projectSaveProgress').resolve({}); await settle();
  f.request('projectReleaseLock').reject({ code: 'operation_in_progress', message: '合成：忙碌' }); await settle();
  assert.deepEqual(messageList(f)[0], { text: '作業還在進行，請等它完成後再試一次。', level: 'warn' });
  assert.equal(f.state.busy, false);
  assert.equal(f.state.view, 'workflow');

  // 作業進行中按「回首頁」：有可以取消的作業時，忙碌畫面與訊息都說明先按「取消作業」。
  f = remapAppFixture(); f.store.setView('workflow');
  f.store.setBusy(true, '產生工作底稿'); f.store.setBusyRequest('synthetic-request');
  f.click('[data-action="app-back-picker"]'); await settle();
  assert.deepEqual(messageList(f)[0], { text: '作業還在進行。請先按「取消作業」，等取消完成後再試一次。', level: 'warn' });
  assert.equal(f.state.busyDetail, '作業還在進行。請先按「取消作業」，等取消完成後再試一次。');
  assert.equal(f.requests.some(r => r.action === 'projectReleaseLock'), false);
});

test('when the current step cannot be saved on the way out, the auditor is told where the case will reopen and still leaves', async () => {
  const f = remapAppFixture(); f.store.setView('workflow');
  f.click('[data-action="app-back-picker"]'); await settle();
  f.request('projectSaveProgress').reject({ code: 'synthetic', message: '合成：位置寫不進去。' }); await settle();
  // 原因的句號拿掉後接固定的下一句。
  assert.ok(messageList(f).some(m => m.text === '目前所在的步驟未能儲存：合成：位置寫不進去。下次開啟此案件時，會回到上次儲存的步驟。' && m.level === 'error'),
    'the step that could not be saved is explained');
  f.request('projectReleaseLock').resolve({}); await settle();
  assert.equal(f.state.view, 'picker', 'leaving continues after the position could not be saved');
});
