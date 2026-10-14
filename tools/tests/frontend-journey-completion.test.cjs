'use strict';
// 情境 17：第六步完成狀態、版本紀錄與三處進度（收尾四項第一項第 3 批，2026-10-13）。
//
// 審計員的操作：只有過期底稿時看第六步；匯出成功；上游重跑資料驗證；情境刪光後進第六步看紀錄；新增一般報告與新底稿；看條件儲存時間。
// 夾具：tools/tests/support/frontend-fixtures.cjs 的 usageJourneyFixture（第六步重繪）與 remapAppFixture（左側目錄、步驟文件流與流程總覽）。
// 刪光情境用 Store.setSavedScenarios([]) 直接設定，不經第五步移除操作，避開正在改寫的第五步儲存與移除流程（第 3 項）。
//
// 取代的 C# 字串測試（src/JET/tests/JET.Tests/Architecture/）：
//   DesignIntegrationFrontendTests.Store_KeepsSixStepModel_WithMappingStepAndExportLast
//   DesignIntegrationFrontendTests.ExportStep_RendersCompletionSummary_WithoutAddingAStep
//   FrontendUsabilityContractTests.Overview_CompletionEvidenceReusesExistingGatesAndCurrentArtifactPredicates
//   ScreenBehaviorReviewFrontendTests.StepCompletion_LeftProgressOverviewAndExportShareOneDefinition
//   ScreenBehaviorReviewFrontendTests.ExportStep_OpensForWorkpaperHistoryWithoutScenario_ButExportStaysDisabled
//   WorkpaperSheetMirrorTests.ReportArtifactUpsert_PreservesWorkpaperHistoryAndReplacesOrdinaryReports
//   WorkpaperSheetMirrorTests.WorkpaperHistory_DoesNotReplaceCurrentCompletionEvidence
//   WorkpaperSheetMirrorTests.WorkpaperEntry_RequiresSavedDefinitionsNotAnIntermediateFile
//   ScreenWordingReviewFrontendTests.ScenarioSaveTime_UsesSharedLocalDateTimeFormat
//   PhaseFiveFollowUpFrontendTests.ExportStepSummary_FollowsTheCurrentWorkpaper
//
// 斷言的失敗值一律不是假 DOM 節點（節點只用 === 轉成布林），避免報表展開整棵假 DOM。
// 逐字比對的只有使用者裁定過、找得到出處的文字（出處寫在各斷言旁）；其餘只比對意思。預期值一律寫死。
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { usageJourneyFixture, remapAppFixture: baseRemapAppFixture, settle, plain, savedG, batch8ValidationData, remapPaper, readWwwroot } = require('./support/frontend-fixtures.cjs');

// remapAppFixture 加上這個檔需要的兩件事（只服務這個檔，先寫在這裡，第 11 批再決定要不要收進支援模組）：
// 流程總覽的分析區要 overview-bi.js；renderContent 排統計數字時會讀 getComputedStyle，vm 沒有版面，回傳不影響結果的固定值。
function remapAppFixture() {
  const f = baseRemapAppFixture();
  f.window.getComputedStyle = () => ({ gridTemplateColumns: 'none', display: 'block', position: 'static' });
  vm.runInContext(readWwwroot('js/overview-bi.js'), f.context, { filename: 'overview-bi.js' });
  return f;
}

// W14（2026-09-17 計畫，使用者回覆「W14 條件保存時間是原始字串：要修」）：時間顯示成當地的年月日時分，例如「2026/10/02 12:57」。
// 執行環境的 ICU 在日期與時間之間放的是窄空白（U+2009），所以用 \s 比對；只比對格式，不比對固定時刻（時區會變）。
const LOCAL_TIME = /\d{4}\/\d{2}\/\d{2}\s\d{2}:\d{2}/;

function paper(id, { stale = false, validationRunId = 'v1', scenarioRevision = 'r1', generatedUtc = '2026-01-01T00:00:00Z' } = {}) {
  return { artifactId: id, kind: 'workingPaper', fileName: id + '.xlsx', fileState: 'asPublished', stale, generatedUtc,
    sourceRef: { validationRunId, scenarioRevision, scenarioPositions: [1, 2, 3] } };
}

function criteria(id, positions) {
  return { artifactId: id, kind: 'criteriaSelectionReport', fileName: id + '.xlsx', fileState: 'asPublished', stale: false,
    generatedUtc: '2026-01-01T00:00:00Z', sourceRef: { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: positions } };
}

// 依流程逐步建立的七種狀態，加上「前五步完成、只有過期底稿」。expectedDone 是寫死的完成步數。
const STATES = [
  { name: 'no case', level: 0, expectedDone: 0 },
  { name: 'case created', level: 1, expectedDone: 1 },
  { name: 'GL and TB imported', level: 2, expectedDone: 2 },
  { name: 'both mappings confirmed', level: 3, expectedDone: 3 },
  { name: 'validation eligible', level: 4, expectedDone: 4 },
  { name: 'scenarios saved', level: 5, expectedDone: 5 },
  { name: 'current workpaper exported', level: 6, expectedDone: 6 },
  { name: 'only an outdated workpaper', level: 5, stalePaper: true, expectedDone: 5 }
];

function buildState(f, { level, stalePaper = false }) {
  const store = f.store;
  store.resetWorkflow();
  if (level >= 1) store.setProject({ projectId: 'completion-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31' });
  if (level >= 2) for (const kind of ['gl', 'tb']) store.setImportResult(kind, { batchId: kind, rowCount: 2, columns: ['Account'] });
  if (level >= 3) for (const kind of ['gl', 'tb']) store.setMappingCommitted(kind, { mapping: {}, mode: kind === 'gl' ? 'dual' : 'debitCredit' });
  if (level >= 4) store.setLastRun('validate', batch8ValidationData('v1'));
  if (level >= 5) { store.setSavedScenarios([savedG('第一個'), savedG('第二個'), savedG('第三個')]); store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' }); }
  if (level >= 6) store.setReportArtifacts([paper('current')]);
  if (stalePaper) store.setReportArtifacts([paper('outdated', { stale: true })]);
  store.setView('workflow');
}

function overviewOf(f) { const element = f.dom.element(); element.innerHTML = f.app.overviewHtml(f.state); return element; }

test('the workflow keeps six steps with mapping on its own and export last, and the table of contents lists exactly those', () => {
  const f = remapAppFixture();
  // 設計稿把「完成」畫成第六步並把欄位配對併入匯入；整合時沒有採用，流程仍是這六步。
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

// W15（2026-10-02 整體複審，使用者回覆「進度 5/6 與「6 個完成」矛盾：要修」）：左側進度、總覽與第六步共用同一個完成判斷，只認目前版本的底稿。
// 原 C# 斷言改到共用函式時的第一次失敗收據：20261002-143207412-706428487ae84c538ac9734c4afe9f33（FrontendUsabilityContractTests）、
// 20261002-143202027-0932a0e4100f4e419c7609149b4cb025（WorkpaperSheetMirrorTests.WorkpaperHistory_…）。
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

test('exporting a workpaper completes the case inside step six, and a re-run validation withdraws it while the history stays', async () => {
  const f = usageJourneyFixture('export', { redraw: true });
  f.store.setStepIndex(5);
  f.store.setReportArtifacts([remapPaper('older', true)]);
  assert.ok(f.$('.completion') === null, 'an outdated workpaper does not complete the case');

  f.change('[data-bind="include-prescreen-report"]', false);
  f.click('[data-action="export-workpaper"]'); await settle();
  // 沒有條件篩選報告也能匯出；匯出的是全部已儲存情境。
  assert.deepEqual(plain(f.request('exportWorkpaperStream').payload), { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2, 3] });
  const fresh = remapPaper('fresh');
  f.request('exportWorkpaperStream').resolve({ artifact: fresh, reportArtifacts: [remapPaper('older', true), fresh],
    sheetStats: [{ sheetName: 'Synthetic', rowsWritten: 3 }], filterResultsCurrent: true });
  await settle(); await settle();

  const completion = f.$('.completion');
  assert.ok(completion, 'completion appears after a successful export');
  assert.equal(completion.getAttribute('role'), 'status');
  // 「案件流程已完成」：W15（docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md:4276）沿用的第六步完成標題，
  // 計畫第 4 節列為有出處、逐字比對的文字。
  assert.equal(completion.querySelector('.completion__title').textContent, '案件流程已完成');
  assert.equal(f.state.currentStepIndex, 5, 'completion is a state of step six, not a seventh step');
  assert.equal(f.requests.some(r => r.action === 'projectSaveProgress'), false, 'completion does not save a new position');

  f.store.setLastRun('validate', { resultRef: { runId: 'v2' }, completenessTest: { eligibility: { isEligible: true } } });
  assert.ok(f.$('.completion') === null, 'a re-run validation makes the exported workpaper outdated');
  assert.deepEqual(f.$('.report-output').querySelectorAll('[data-artifact-id]').map(node => node.getAttribute('data-artifact-id')), ['fresh', 'older'],
    'both versions stay in the history');
});

test('a criteria report counts toward completion only when it covers every saved scenario, whatever is ticked for export', () => {
  const f = usageJourneyFixture('export', { redraw: true });
  const tags = () => f.$('.completion').querySelectorAll('.completion__tag').map(node => node.textContent);
  f.store.setReportArtifacts([remapPaper('fresh'), criteria('criteria-all', [1, 2, 3])]);
  assert.deepEqual(tags(), ['條件篩選報告', '工作底稿']);
  // 只匯出其中兩個情境時，涵蓋全部情境的條件篩選報告仍算目前版本。
  f.change('[data-scenario-position="3"]', false); f.store.touch();
  assert.deepEqual(tags(), ['條件篩選報告', '工作底稿']);
  f.store.setReportArtifacts([remapPaper('fresh'), criteria('criteria-part', [1, 2])]);
  assert.deepEqual(tags(), ['工作底稿'], 'a report for only some scenarios is not the current one');
  f.store.setReportArtifacts([]);
  assert.equal(f.window.JetUi.stepGate(f.state, 5).ok, true, 'step six needs saved scenarios, not an intermediate report file');
});

test('the overview counts a criteria report as current only when it covers every saved scenario', () => {
  const f = remapAppFixture();
  buildState(f, { level: 6 }); f.store.setStepIndex(4);
  const fact = () => overviewOf(f).querySelector('[data-overview-fact-key="filter.criteria-report"] .overview-step-fact__value').textContent;
  f.store.setReportArtifacts([paper('current'), criteria('criteria-all', [1, 2, 3])]);
  assert.match(fact(), /criteria-all\.xlsx/);
  f.store.setReportArtifacts([paper('current'), criteria('criteria-part', [1, 2])]);
  assert.doesNotMatch(fact(), /criteria-part/, 'a report for only some scenarios is not shown as the current one');
});

test('export stays off without a current validation result or when validation says the data cannot continue', () => {
  const f = usageJourneyFixture('export', { redraw: true });
  const exportOff = () => f.$('[data-action="export-workpaper"]').disabled;
  assert.equal(exportOff(), false, 'baseline: saved scenarios and an eligible validation allow export');
  f.store.setLastRun('validate', { resultRef: { runId: 'v1' }, completenessTest: { eligibility: { isEligible: false, reason: '合成原因' } } });
  assert.equal(exportOff(), true, 'ineligible validation');
  f.store.setLastRun('validate', null);
  assert.equal(exportOff(), true, 'no validation result');
  // 驗證摘要還在但沒有結果識別碼（例如結果已被後端撤回）：沒有可綁定的驗證結果，也不能匯出。
  f.store.setLastRun('validate', { completenessTest: { eligibility: { isEligible: true } } });
  assert.equal(exportOff(), true, 'no validation result id');
});

test('a new ordinary report replaces the older one of the same kind while every workpaper version is kept', () => {
  const f = usageJourneyFixture('export', { redraw: true });
  const validation = id => ({ artifactId: id, kind: 'validationReport', fileName: id + '.xlsx', stale: false, generatedUtc: '2026-01-01T00:00:00Z', sourceRef: { validationRunId: 'v1' } });
  f.store.setReportArtifacts([paper('paper-old', { generatedUtc: '2026-01-01T00:00:00Z' }), validation('validation-old')]);
  f.store.applyReportExport({ artifact: validation('validation-new') });
  assert.deepEqual(f.state.reportArtifacts.map(a => a.artifactId), ['paper-old', 'validation-new']);
  f.store.applyReportExport({ artifact: paper('paper-new', { generatedUtc: '2026-01-02T00:00:00Z' }) });
  assert.deepEqual(f.state.reportArtifacts.map(a => a.artifactId), ['paper-old', 'validation-new', 'paper-new']);
  assert.deepEqual(f.$('.report-output').querySelectorAll('[data-artifact-id]').map(node => node.getAttribute('data-artifact-id')), ['paper-new', 'paper-old']);
});

// W17：情境全刪後仍能進第六步看版本紀錄，但不能匯出。原 C# 方法的收據：W10「已儲存」用語 20261003-023349721-0ccefea0a80c412aa8460624eaae563a，
// 紅色改琥珀色 20261010-170756738-519e29ffbd8f4b9c85569b0d94086796。
test('with every scenario deleted the auditor can still open step six to read the history but cannot export', () => {
  const f = remapAppFixture();
  buildState(f, { level: 6 }); f.store.setSavedScenarios([]);
  assert.equal(f.window.JetUi.stepGate(f.state, 5).ok, true, 'the workpaper history keeps step six open');
  assert.equal(f.window.JetUi.stepComplete(f.state, 4), false, 'step five is not complete without saved scenarios');

  const step = f.draw('export');
  const notice = step.querySelector('[data-bind="export-no-scenario"]');
  assert.ok(notice);
  // 文字出自 W17（docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md:4278）；使用者 2026-10-11 選「前置未完成一律黃色」，
  // 選項說明寫「文字不變，只換顏色」（docs/history/specs/2026-10-06-kct-gl-requirements-plan.md 第 5721 行起）。計畫第 4 節列為逐字比對。
  // 使用者 2026-10-14 要求黃色提示不過度解釋，句子依同日確認的清單縮短（第一次失敗：FrontendMapping 收據 20261014-085644389-9bd9b20c579e4d95926d6ad0c4881504）。
  assert.equal(notice.textContent, '還沒有已儲存的篩選情境，請先到「進階條件篩選」儲存，再匯出工作底稿。');
  assert.ok(notice.className.split(/\s+/).includes('form-notice--warning'), 'a prerequisite notice is amber, not red');
  assert.ok(step.querySelector('[data-scenario-position]') === null, 'no scenario selection without scenarios');
  assert.equal(step.querySelector('[data-action="export-workpaper"]').disabled, true);
  assert.deepEqual(step.querySelector('.report-output').querySelectorAll('[data-artifact-id]').map(node => node.getAttribute('data-artifact-id')), ['current']);

  // 沒有版本紀錄時第六步鎖住，文件流與左側目錄寫出要先儲存情境。
  f.store.setReportArtifacts([]); f.store.setStepIndex(3);
  assert.equal(f.window.JetUi.stepGate(f.state, 5).ok, false);
  f.app.renderContent(f.state, f.store.STEPS[3]);
  const row = f.$('.stepflow-item--locked');
  assert.ok(row, 'step six is locked');
  assert.match(row.querySelector('.stepflow-item__name').textContent, /匯出底稿/);
  assert.match(row.querySelector('.stepflow-item__reason').textContent, /儲存.*篩選情境/);
  f.app.renderStepNav(f.state, f.store.STEPS);
  const tocExport = f.$('.toc-item[data-step-index="5"]');
  assert.equal(tocExport.disabled, true);
  assert.match(tocExport.getAttribute('title'), /儲存.*篩選情境/);
});

test('the step-six line in the workflow names the export time only for the current workpaper', () => {
  const f = remapAppFixture();
  const summary = () => { f.app.renderContent(f.state, f.store.STEPS[3]); const row = f.$('.stepflow-item[data-step-index="5"]'); assert.ok(row); return row.querySelector('.stepflow-item__summary').textContent; };
  buildState(f, { level: 6 }); f.store.setStepIndex(3);
  const current = summary();
  assert.match(current, /已匯出/); assert.match(current, LOCAL_TIME); assert.doesNotMatch(current, /T\d|Z/);
  buildState(f, { level: 5, stalePaper: true }); f.store.setStepIndex(3);
  const outdated = summary();
  assert.doesNotMatch(outdated, /已匯出/, 'an outdated workpaper is not reported as exported');
  assert.match(outdated, /匯出工作底稿/);
  assert.doesNotMatch(outdated, LOCAL_TIME);
});

test('the scenario save time reads as a local date and time in step six and the overview, and never as the raw value', () => {
  const f = remapAppFixture();
  buildState(f, { level: 6 });
  const iso = '2026-03-04T05:06:00Z';
  f.store.setFilterResultRef({ revision: iso, populationScope: 'auditPeriod' });
  f.store.setReportArtifacts([paper('current', { scenarioRevision: iso, generatedUtc: iso })]);
  const hint = () => f.draw('export').querySelectorAll('.scenario-export-selection__hint').map(node => node.textContent).find(text => text.includes('條件儲存時間：'));
  // 「條件儲存時間：」：W14 的修正寫法（docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md:4255），「儲存」依 W10 裁定。
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

test('a workpaper tied to a result saved under a retired scope does not complete the case anywhere', () => {
  const f = remapAppFixture();
  buildState(f, { level: 6 });
  f.store.setFilterResultRef({ revision: 'r1', populationScope: 'allProjected' });
  f.store.setStepIndex(3);
  assert.equal(f.window.JetUi.stepComplete(f.state, 5), false);
  f.app.renderStepNav(f.state, f.store.STEPS);
  assert.match(f.$('.toc-progress').textContent, /進度 4\/6/);
  assert.equal(overviewOf(f).querySelector('[role="progressbar"]').getAttribute('aria-valuenow'), '4');
  assert.ok(f.draw('export').querySelector('.completion') === null, 'no completion area for a retired-scope workpaper');
});

test('step six lists each saved scenario that still needs editing and keeps export off until they are fixed', () => {
  const f = usageJourneyFixture('export', { redraw: true });
  // 開案檢查後還有情境要修改：後端不給可用版本（resultRef 是 null），並逐筆列出待修改的情境。原因句子由第 4 項改寫，這裡只看結構與名稱。
  f.store.setFilterResultRef(null);
  f.store.setFilterScenarioCheck({ status: 'needsEdit', recalculatedCount: 1,
    problems: [{ position: 1, name: '第一個', messages: ['合成原因一'] }, { position: 3, name: '第三個', messages: ['合成原因三'] }] });
  const list = f.$('.filter-scenario-problems');
  assert.ok(list, 'the scenarios to fix are listed in step six too');
  const items = list.querySelectorAll('li').map(item => item.textContent);
  assert.equal(items.length, 2);
  assert.match(items[0], /第一個/); assert.match(items[1], /第三個/);
  assert.equal(f.$('[data-action="export-workpaper"]').disabled, true);
  assert.equal(f.window.JetUi.stepComplete(f.state, 4), false);
});
