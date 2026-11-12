'use strict';
// 情境 17：第六步完成狀態、版本紀錄與三處進度。
//
// 審計員的操作：只有過期底稿時看第六步；匯出成功；上游重跑資料驗證；情境刪光後進第六步看紀錄；新增一般報告與新底稿；看條件儲存時間。
// 夾具：support/frontend-fixtures.cjs 的 usageJourneyFixture（第六步重繪）與 remapAppFixture（左側目錄、步驟文件流與流程總覽）。
// 刪光情境用 Store.setSavedScenarios([]) 直接設定，不經第五步移除操作。
//
// 斷言的失敗值一律不是假 DOM 節點（節點只用 === 轉成布林），避免報表展開整棵假 DOM。
// 逐字比對的只有已定案的用字，其餘只比對意思。預期值一律寫死。
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { usageJourneyFixture, remapAppFixture: baseRemapAppFixture, settle, plain, savedBlankDescription, validationResultData, remapPaper, readWwwroot } = require('./support/frontend-fixtures.cjs');

// remapAppFixture 加上這個檔需要的兩件事（只服務這個檔，寫在這裡，沒有收進支援模組）：
// 流程總覽的分析區要 overview-bi.js；renderContent 排統計數字時會讀 getComputedStyle，vm 沒有版面，回傳不影響結果的固定值。
function remapAppFixture() {
  const f = baseRemapAppFixture();
  f.window.getComputedStyle = () => ({ gridTemplateColumns: 'none', display: 'block', position: 'static' });
  vm.runInContext(readWwwroot('js/overview-bi.js'), f.context, { filename: 'overview-bi.js' });
  return f;
}

// 時間顯示成當地的年月日時分，例如「2026/10/02 12:57」。
// 執行環境的 ICU 在日期與時間之間放的是窄空白（U+2009），所以用 \s 比對；只比對格式，不比對固定時刻（時區會變）。
const LOCAL_TIME = /\d{4}\/\d{2}\/\d{2}\s\d{2}:\d{2}/;

function paper(id, { stale = false, validationResultId = 'v1', scenarioRevision = 'r1', generatedUtc = '2026-01-01T00:00:00Z' } = {}) {
  return { reportId: id, kind: 'workingPaper', fileName: id + '.xlsx', fileState: 'asPublished', stale, generatedUtc,
    sourceRef: { validationResultId, scenarioRevision, scenarioPositions: [1, 2, 3] } };
}

function criteria(id, positions) {
  return { reportId: id, kind: 'criteriaSelectionReport', fileName: id + '.xlsx', fileState: 'asPublished', stale: false,
    generatedUtc: '2026-01-01T00:00:00Z', sourceRef: { validationResultId: 'v1', scenarioRevision: 'r1', scenarioPositions: positions } };
}

// 依流程逐步建立的七種狀態，加上「前五步完成、只有過期底稿」。expectedDone 是寫死的完成步數。
const STATES = [
  { name: 'no case', level: 0, expectedDone: 0 },
  { name: 'case created', level: 1, expectedDone: 1 },
  { name: 'GL and TB imported', level: 2, expectedDone: 2 },
  { name: 'both mappings confirmed', level: 3, expectedDone: 3 },
  { name: 'validation eligible', level: 4, expectedDone: 4 },
  { name: 'scenarios saved', level: 5, expectedDone: 5 },
  { name: 'current workingPaper exported', level: 6, expectedDone: 6 },
  { name: 'only an outdated workingPaper', level: 5, stalePaper: true, expectedDone: 5 }
];

function buildState(f, { level, stalePaper = false }) {
  const store = f.store;
  store.resetWorkflow();
  if (level >= 1) store.setProject({ projectId: 'completion-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31' });
  if (level >= 2) for (const kind of ['gl', 'tb']) store.setImportResult(kind, { batchId: kind, rowCount: 2, columns: ['Account'] });
  if (level >= 3) for (const kind of ['gl', 'tb']) store.setMappingCommitted(kind, { mapping: {}, mode: kind === 'gl' ? 'dual' : 'debitCredit' });
  if (level >= 4) store.setResult('validate', validationResultData('v1'));
  if (level >= 5) { store.setSavedScenarios([savedBlankDescription('第一個'), savedBlankDescription('第二個'), savedBlankDescription('第三個')]); store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' }); }
  if (level >= 6) store.setReports([paper('current')]);
  if (stalePaper) store.setReports([paper('outdated', { stale: true })]);
  store.setView('workflow');
}

function overviewOf(f) { const element = f.dom.element(); element.innerHTML = f.app.overviewHtml(f.state); return element; }

test('the workflow keeps six steps with mapping on its own and export last, and the table of contents lists exactly those', () => {
  const f = remapAppFixture();
  // 流程是這六步，欄位配對自成一步，最後一步是匯出底稿。
  assert.deepEqual(plain(f.store.STEPS), [
    { id: 'create', label: '建立案件' },
    { id: 'import', label: '匯入資料' },
    { id: 'mapping', label: '欄位配對' },
    { id: 'validate', label: '資料驗證與測試' },
    { id: 'filter', label: '進階條件篩選' },
    { id: 'export', label: '匯出底稿' }
  ]);
  f.store.setView('workflow'); f.app.renderStepNav(f.state, f.store.STEPS);
  const items = f.$('[data-bind="step-nav"]').querySelectorAll('.toc-item');
  assert.equal(items.length, 6);
  assert.equal(items.at(-1).querySelector('.toc-item__label').textContent, '匯出底稿');
});

// 左側進度、總覽與第六步共用同一個完成判斷，只認目前版本的底稿。
test('the left progress, the overview count and the step-six completion agree in every state and wherever the auditor stands', () => {
  const f = remapAppFixture();
  for (const entry of STATES) {
    for (const currentStepIndex of [0, 3, 5]) {
      buildState(f, entry); f.store.setStepIndex(currentStepIndex);
      const label = entry.name + ' at step ' + (currentStepIndex + 1);
      f.app.renderStepNav(f.state, f.store.STEPS);
      assert.match(f.$('.toc-progress').textContent, new RegExp('進度 ' + entry.expectedDone + '/6'), label + ': left progress');
      // 目錄的勾選：完成的步驟打勾，只有目前所在那一步改標成目前位置。
      const tocDone = f.$('[data-bind="step-nav"]').querySelectorAll('.toc-item').map((item, index) => item.className.includes('toc-item--done') ? index : -1).filter(index => index >= 0);
      assert.deepEqual(tocDone, [0, 1, 2, 3, 4, 5].slice(0, entry.expectedDone).filter(index => index !== currentStepIndex), label + ': table of contents marks');
      const overview = overviewOf(f);
      assert.equal(overview.querySelector('[role="progressbar"]').getAttribute('aria-valuenow'), String(entry.expectedDone), label + ': overview count');
      const doneStages = overview.querySelectorAll('.overview-stage').map((stage, index) => stage.className.includes('overview-stage--done') ? index : -1).filter(index => index >= 0);
      assert.deepEqual(doneStages, [0, 1, 2, 3, 4, 5].slice(0, entry.expectedDone), label + ': overview stage marks');
      const exportStep = f.draw('export');
      assert.equal(!!exportStep.querySelector('.completion'), entry.expectedDone === 6, label + ': completion area');
      assert.equal(f.state.currentStepIndex, currentStepIndex, label + ': nothing moves the auditor');
    }
  }
});

test('exporting a workingPaper completes the case inside step six, and a re-run validation withdraws it while the history stays', async () => {
  const f = usageJourneyFixture('export', { redraw: true });
  f.store.setStepIndex(5);
  f.store.setReports([remapPaper('older', true)]);
  assert.ok(f.$('.completion') === null, 'an outdated workingPaper does not complete the case');

  f.change('[data-bind="include-prescreen-report"]', false);
  f.click('[data-action="export-working-paper"]'); await settle();
  // 沒有條件篩選報告也能匯出；匯出的是全部已儲存情境。
  assert.deepEqual(plain(f.request('exportWorkingPaperStream').payload), { validationResultId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2, 3] });
  const fresh = remapPaper('fresh');
  f.request('exportWorkingPaperStream').resolve({ publishedReport: fresh, reports: [remapPaper('older', true), fresh],
    sheetStats: [{ sheetName: 'Synthetic', rowsWritten: 3 }], filterResultsCurrent: true });
  await settle(); await settle();

  const completion = f.$('.completion');
  assert.ok(completion, 'completion appears after a successful export');
  assert.equal(completion.getAttribute('role'), 'status');
  // 「案件流程已完成」是第六步的完成標題，逐字比對。
  assert.equal(completion.querySelector('.completion__title').textContent, '案件流程已完成');
  assert.equal(f.state.currentStepIndex, 5, 'completion is a state of step six, not a seventh step');
  assert.equal(f.requests.some(r => r.action === 'projectSaveProgress'), false, 'completion does not save a new position');

  f.store.setResult('validate', { resultRef: { resultId: 'v2' }, completenessTest: { eligibility: { isEligible: true } } });
  assert.ok(f.$('.completion') === null, 'a re-run validation makes the exported workingPaper outdated');
  assert.deepEqual(f.$('.report-output').querySelectorAll('[data-report-id]').map(node => node.getAttribute('data-report-id')), ['fresh', 'older'],
    'both versions stay in the history');
});

test('a criteria report counts toward completion only when it covers every saved scenario, whatever is ticked for export', () => {
  const f = usageJourneyFixture('export', { redraw: true });
  const tags = () => f.$('.completion').querySelectorAll('.completion__tag').map(node => node.textContent);
  f.store.setReports([remapPaper('fresh'), criteria('criteria-all', [1, 2, 3])]);
  assert.deepEqual(tags(), ['條件篩選報告', '工作底稿']);
  // 只匯出其中兩個情境時，涵蓋全部情境的條件篩選報告仍算目前版本。
  f.change('[data-scenario-position="3"]', false); f.store.touch();
  assert.deepEqual(tags(), ['條件篩選報告', '工作底稿']);
  f.store.setReports([remapPaper('fresh'), criteria('criteria-part', [1, 2])]);
  assert.deepEqual(tags(), ['工作底稿'], 'a report for only some scenarios is not the current one');
  f.store.setReports([]);
  assert.equal(f.window.JetUi.stepGate(f.state, 5).ok, true, 'step six needs saved scenarios, not an intermediate report file');
});

test('the overview counts a criteria report as current only when it covers every saved scenario', () => {
  const f = remapAppFixture();
  buildState(f, { level: 6 }); f.store.setStepIndex(4);
  const fact = () => overviewOf(f).querySelector('[data-overview-fact-key="filter.criteria-report"] .overview-step-fact__value').textContent;
  f.store.setReports([paper('current'), criteria('criteria-all', [1, 2, 3])]);
  assert.match(fact(), /criteria-all\.xlsx/);
  f.store.setReports([paper('current'), criteria('criteria-part', [1, 2])]);
  assert.doesNotMatch(fact(), /criteria-part/, 'a report for only some scenarios is not shown as the current one');
});

test('export stays off without a current validation result or when validation says the data cannot continue', () => {
  const f = usageJourneyFixture('export', { redraw: true });
  const exportOff = () => f.$('[data-action="export-working-paper"]').disabled;
  assert.equal(exportOff(), false, 'baseline: saved scenarios and an eligible validation allow export');
  f.store.setResult('validate', { resultRef: { resultId: 'v1' }, completenessTest: { eligibility: { isEligible: false, reason: '合成原因' } } });
  assert.equal(exportOff(), true, 'ineligible validation');
  f.store.setResult('validate', null);
  assert.equal(exportOff(), true, 'no validation result');
  // 驗證摘要還在但沒有結果識別碼（例如結果已被後端撤回）：沒有可綁定的驗證結果，也不能匯出。
  f.store.setResult('validate', { completenessTest: { eligibility: { isEligible: true } } });
  assert.equal(exportOff(), true, 'no validation result id');
});

test('a new ordinary report replaces the older one of the same kind while every workingPaper version is kept', () => {
  const f = usageJourneyFixture('export', { redraw: true });
  const validation = id => ({ reportId: id, kind: 'validationReport', fileName: id + '.xlsx', stale: false, generatedUtc: '2026-01-01T00:00:00Z', sourceRef: { validationResultId: 'v1' } });
  f.store.setReports([paper('paper-old', { generatedUtc: '2026-01-01T00:00:00Z' }), validation('validation-old')]);
  f.store.applyReportExport({ publishedReport: validation('validation-new') });
  assert.deepEqual(f.state.reports.map(a => a.reportId), ['paper-old', 'validation-new']);
  f.store.applyReportExport({ publishedReport: paper('paper-new', { generatedUtc: '2026-01-02T00:00:00Z' }) });
  assert.deepEqual(f.state.reports.map(a => a.reportId), ['paper-old', 'validation-new', 'paper-new']);
  assert.deepEqual(f.$('.report-output').querySelectorAll('[data-report-id]').map(node => node.getAttribute('data-report-id')), ['paper-new', 'paper-old']);
});

// 情境全刪後仍能進第六步看版本紀錄。
// 沒有已儲存的篩選情境也能匯出底稿，第三步與第四步留空。
test('with every scenario deleted the auditor can still open step six to read the history and export', () => {
  const f = remapAppFixture();
  buildState(f, { level: 6 }); f.store.setSavedScenarios([]);
  assert.equal(f.window.JetUi.stepGate(f.state, 5).ok, true, 'the workingPaper history keeps step six open');
  assert.equal(f.window.JetUi.stepComplete(f.state, 4), false, 'step five is not complete without saved scenarios');

  const step = f.draw('export');
  assert.equal(step.querySelector('[data-bind="export-no-scenario"]'), null, 'no notice asking to save a scenario first');
  assert.ok(step.querySelector('[data-scenario-position]') === null, 'no scenario selection without scenarios');
  assert.equal(step.querySelector('[data-action="export-working-paper"]').disabled, false);
  assert.deepEqual(step.querySelector('.report-output').querySelectorAll('[data-report-id]').map(node => node.getAttribute('data-report-id')), ['current']);

  // 沒有版本紀錄時第六步也不鎖，文件流與左側目錄都能進入。
  f.store.setReports([]); f.store.setStepIndex(3);
  assert.equal(f.window.JetUi.stepGate(f.state, 5).ok, true);
  f.app.renderContent(f.state, f.store.STEPS[3]);
  assert.equal(f.$('.stepflow-item--locked[data-step-index="5"]'), null, 'step six is not locked');
  f.app.renderStepNav(f.state, f.store.STEPS);
  assert.equal(f.$('.toc-item[data-step-index="5"]').disabled, false);
});

test('the step-six line in the workflow names the export time only for the current workingPaper', () => {
  const f = remapAppFixture();
  const summary = () => { f.app.renderContent(f.state, f.store.STEPS[3]); const row = f.$('.stepflow-item[data-step-index="5"]'); assert.ok(row); return row.querySelector('.stepflow-item__summary').textContent; };
  buildState(f, { level: 6 }); f.store.setStepIndex(3);
  const current = summary();
  assert.match(current, /已匯出/); assert.match(current, LOCAL_TIME); assert.doesNotMatch(current, /T\d|Z/);
  buildState(f, { level: 5, stalePaper: true }); f.store.setStepIndex(3);
  const outdated = summary();
  assert.doesNotMatch(outdated, /已匯出/, 'an outdated workingPaper is not reported as exported');
  assert.match(outdated, /匯出工作底稿/);
  assert.doesNotMatch(outdated, LOCAL_TIME);
});

test('the scenario save time reads as a local date and time in step six and the overview, and never as the raw value', () => {
  const f = remapAppFixture();
  buildState(f, { level: 6 });
  const iso = '2026-03-04T05:06:00Z';
  f.store.setFilterResultRef({ revision: iso, populationScope: 'auditPeriod' });
  f.store.setReports([paper('current', { scenarioRevision: iso, generatedUtc: iso })]);
  const hint = () => f.draw('export').querySelectorAll('.scenario-export-selection__hint').map(node => node.textContent).find(text => text.includes('條件儲存時間：'));
  // 「條件儲存時間：」與「儲存」的用字已定案。
  const text = hint();
  assert.ok(text && text.startsWith('條件儲存時間：'), 'the line starts with the save-time label');
  const shown = text.slice('條件儲存時間：'.length).match(LOCAL_TIME);
  assert.ok(shown, 'local date and time: ' + text);
  assert.doesNotMatch(text, /T\d|Z|上午|下午/);
  assert.equal(text.includes(iso), false);

  f.store.setStepIndex(5);
  const overviewTime = overviewOf(f).querySelector('[data-overview-fact-key="export.working-paper"] .overview-step-fact__note').textContent;
  assert.equal(overviewTime, shown[0], 'the overview uses the same format for the same moment');

  f.store.setFilterResultRef({ revision: 'not-a-time', populationScope: 'auditPeriod' });
  const unreadable = hint();
  assert.ok(unreadable.startsWith('條件儲存時間：'));
  assert.equal(unreadable.includes('not-a-time'), false, 'an unreadable value is not shown as is');
  assert.doesNotMatch(unreadable, LOCAL_TIME);
});

test('a workingPaper tied to a result saved under a retired scope does not complete the case anywhere', () => {
  const f = remapAppFixture();
  buildState(f, { level: 6 });
  f.store.setFilterResultRef({ revision: 'r1', populationScope: 'allProjected' });
  f.store.setStepIndex(3);
  assert.equal(f.window.JetUi.stepComplete(f.state, 5), false);
  f.app.renderStepNav(f.state, f.store.STEPS);
  assert.match(f.$('.toc-progress').textContent, /進度 4\/6/);
  assert.equal(overviewOf(f).querySelector('[role="progressbar"]').getAttribute('aria-valuenow'), '4');
  assert.ok(f.draw('export').querySelector('.completion') === null, 'no completion area for a retired-scope workingPaper');
});

test('step six lists each saved scenario that still needs editing and keeps export off until they are fixed', () => {
  const f = usageJourneyFixture('export', { redraw: true });
  // 開案檢查後還有情境要修改：後端不給可用版本（resultRef 是 null），並逐筆列出待修改的情境。這裡只看結構與名稱。
  f.store.setFilterResultRef(null);
  f.store.setFilterScenarioCheck({ status: 'needsEdit', recalculatedCount: 1,
    problems: [{ position: 1, name: '第一個', messages: ['合成原因一'] }, { position: 3, name: '第三個', messages: ['合成原因三'] }] });
  const list = f.$('.filter-scenario-problems');
  assert.ok(list, 'the scenarios to fix are listed in step six too');
  const items = list.querySelectorAll('li').map(item => item.textContent);
  assert.equal(items.length, 2);
  assert.match(items[0], /第一個/); assert.match(items[1], /第三個/);
  assert.equal(f.$('[data-action="export-working-paper"]').disabled, true);
  assert.equal(f.window.JetUi.stepComplete(f.state, 4), false);
});
