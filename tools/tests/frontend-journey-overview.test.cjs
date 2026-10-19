'use strict';
// 流程總覽的情境測試（收尾四項第一項第 1 批，2026-10-13；計畫 plan-item1-tests.md 第 2 節情境 18、19）。
//
// 審計員的操作：
//   情境 18：還沒驗證時打開流程總覽；走完流程後打開；展開分析區後背景資料更新、總覽整張重畫；用鍵盤查看六個階段；
//            案件有填與沒填客戶名稱。
//   情境 19：在不同結果狀態下展開「預篩選」「分錄金額級距與累積分布」「預篩選結果分布」；切換集中度分頁；
//            收合、資料更新重畫、關閉總覽。
// 全部執行正式的 index.html 總覽標記、app.js 與 overview-bi.js，資料都是程式生成的合成資料。
//
// 這個檔取代下列只比對原始碼字串的 C# 方法（src/JET/tests/JET.Tests/Architecture/，每波結束時由另一位代理統一刪除）：
//   情境 18：DesignIntegrationFrontendTests.Overview_PopulationBlock_MirrorsValidateStatsWithoutFrontendAggregation、
//     FrontendUsabilityContractTests.Overview_IsSummaryFirstWithCollapsedAnalysesTechnicalDetailsAndFocusContainment、
//     FrontendUsabilityContractTests.Overview_StageRegisterReviewsStatusWithoutDuplicatingDetailEvidenceOrRoutingTheWorkflow、
//     FrontendUsabilityContractTests.Overview_AssignsEachVisibleFactToOneSurface、
//     PhaseFiveFollowUpFrontendTests.OverviewHead_PutsTheNameInTheTitleAndDoesNotRepeatIt。
//   情境 19：OverviewAmountDistributionFrontendTests 的 AmountDistributionRenderer_SeparatesMissingResumeZeroAndNullEcdf、
//     AmountDistributionChart_UsesUpperTicksFullRangeTooltipsAndECharts、Overview_ComposesAmountDistributionBetweenConcentrationAndAuxiliarySignals；
//     OverviewConcentrationFrontendTests 的 ConcentrationRenderer_DegradesHonestlyInsteadOfShowingZero、
//     ConcentrationTabs_PreserveFocusAndImplementTheAriaTabsKeyboardContract、Overview_RendersConcentrationOnlyWhenPopulationIsReady；
//     OverviewEChartsFrontendTests 全部三個；OverviewRulePeriodFrontendTests 的 RulePeriodRenderer_SeparatesMissingResumeNaZeroAndZeroDenominator、
//     Overview_ComposesRulePeriodBetweenPopulationAndConcentration_WithValidateStatsGate、RulePeriodChart_UsesEChartsOptionAndNeutralFullPeriodDenominatorLanguage；
//     PrescreenPositioningFrontendTests.Overview_PrioritizesConcentrationAndUsesAuxiliarySignalAndRecordLanguage。
//
// 原 C# 方法裡焦點圈限的部分（Overview_IsSummaryFirst… 的 Focus.trapDialogTab 與 Escape，使用者 2026-10-13 選定總覽改用共用焦點圈限，
// 該方法修改時的第一次失敗收據 20261013-072317663-5e4a0a6b9dcf47c49a75cdc70e9a40f3）不在這裡重寫，
// 已由 frontend-workflow.test.cjs 的「overview opened from the header button…」「overview Escape and Tab use the shared document trap…」完整涵蓋。
//
// 斷言原則（計畫第 1 節）：只有使用者裁定過的文字逐字比對，出處寫在該斷言旁；其他畫面文字只比對意思（關鍵詞、項目、順序）。
// 預期值都寫死在測試裡，不讀正式程式的常數。總覽不送後端請求，所以「沒有送出請求」用 f.requests 精確比對為空。
// 比對兩個假 DOM 節點一律用 assert.ok(a === b)：assert.equal 失敗時會序列化整棵循環的假 DOM。
//
// 孤兒測試 src/JET/tests/JET.Tests/Architecture/overview-echarts-options.test.js 的內容已修正後併進情境 19 的測試，原檔刪除。
// 它從沒被任何驗證路線執行過；第 0 批原樣直接執行一次的輸出存在本機
// artifacts/review/closing-four-items-20261013/mutation/orphan-first-run.txt（診斷輸出，不是正式收據），
// 結果是第一個斷言就失敗：期望 [null, 0, 0.3]，實際 [0, 0.3]。原檔與目前程式不符的地方共四處：
//   1. 期望不適用的規則以 null 留在圖表資料裡；現在 overview-bi.js 先排除不適用的規則，只列在圖下的「不適用規則」清單。
//      併進來時改成斷言圖表只有適用的規則，不適用的規則和原因出現在清單裡。
//   2. 期望圖表提示框寫出不適用原因；不適用的規則已不進圖表，原因改在清單裡斷言（原因裡的 < > 要照字面顯示）。
//   3. 期望提示框寫「母體 1,000 筆」；使用者 2026-10-08 裁定「納入測試的分錄」全系統改叫 GL，現在寫「GL 1,000 筆」。
//   4. 假元素沒有 closest，overview-bi.js 的 mount 呼叫 element.closest('details') 會直接丟錯，mount 之後的斷言全部跑不到。
//      併進來時改用正式 index.html 標記與共用假 DOM，經由審計員展開分析區觸發 mount。
//   另外原檔自造的預篩選鍵 completeness_test、manual_entry 不是目前的鍵，改用 ui-core.js 現有的鍵。
// 其餘斷言（不改動後端資料、animation 與 tooltip.confine、svg renderer、整份換掉設定、observe 與 resize、
// 編製人員與較少使用科目的數列、提示框跳脫、科目照後端順序）照原意搬進來。
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { overviewDialogFixture, plain } = require('./support/frontend-fixtures.cjs');

// ---- 合成資料 ----

const AMOUNT_KEYS = ['zero', 'lt1k', '1k-2k', '2k-5k', '5k-10k', '10k-20k', '20k-50k', '50k-100k', '100k-200k',
  '200k-500k', '500k-1M', '1M-2M', '2M-5M', '5M-10M', 'gt10M'];

// 15 個級距；第 0、7 格沒有累積比例（後端回 null）。筆數用 index × 1000，第 10 格（500k–1M）是 10,000 筆。
function amountDistribution({ bins = 15 } = {}) {
  return { bins: AMOUNT_KEYS.slice(0, bins).map((key, index) => ({ key, count: index * 1000,
    ecdfPct: index === 0 || index === 7 ? null : Math.round(index * 6.5 * 10) / 10 })) };
}

function validateRun({ runId = 'v-ov', amount = amountDistribution(), extra = {} } = {}) {
  const run = { resultRef: { runId, generatedUtc: '2025-12-02T03:04:05Z', logicVersion: 'synthetic-logic-7' },
    stats: { glRowCount: 1234, voucherCount: 321, totalDebit: 98765.4, totalCredit: 98765.4, net: 0 },
    // 第四步另有分錄測試範圍的摘要；總覽只能顯示 stats，不得改用這裡的數字。
    populationSummary: { raw: { rowCount: 5555 }, effective: { rowCount: 999 }, excluded: { rowCount: 4556, byPeriodCount: 4000, byPostingStatusCount: 556 } },
    completenessTest: { eligibility: { isEligible: true } }, ...extra };
  if (amount) run.amountDistribution = amount;
  return run;
}

function preparers({ top5SharePct = 90 } = {}) {
  return { top: [
    { createdBy: '甲<&', entryCount: 20, manualCount: 3, cumulativePct: 40 },
    { createdBy: '乙', entryCount: 15, manualCount: 2, cumulativePct: 70 }
  ], othersEntryCount: 10, totalPreparerCount: 8, totalEntryCount: 45, top5SharePct };
}

function concentration(extra = {}) {
  return { status: 'V', naReason: null, preparers: preparers(), distinctAccountCount: 42,
    rareAccounts: [
      // 後端已排好順序（使用次數由少到多，同數時依後端規則）；前端照原順序畫，不得重排。
      { accountCode: '9002', accountName: '合成第二', entryCount: 2 },
      { accountCode: '9001', accountName: '合成第一', entryCount: 1 }
    ], ...extra };
}

const NA_REASON = '缺少 <摘要> 欄位（合成）';
function rulePeriod({ population = 1000 } = {}) {
  const rate = value => (population === 0 ? null : value);
  return { population, rules: [
    { key: 'blankDescription', naReason: NA_REASON, hitLines: null, hitVouchers: null, ratePct: null },
    { key: 'weekendPosting', naReason: null, hitLines: 0, hitVouchers: 0, ratePct: rate(0) },
    { key: 'holidayPosting', naReason: null, hitLines: 1, hitVouchers: 1, ratePct: rate(0.0000003) },
    { key: 'suspiciousKeywords', naReason: null, hitLines: 3, hitVouchers: 2, ratePct: rate(0.3) }
  ] };
}

function prescreenRun(extra = {}) {
  return { resultRef: { runId: 'p-ov', generatedUtc: '2025-12-02T04:05:06Z', logicVersion: 'synthetic-logic-7' },
    concentration: concentration(), rulePeriod: rulePeriod(), ...extra };
}

// ---- 操作小工具 ----

function setup({ charts = true } = {}) {
  const f = overviewDialogFixture({ charts });
  f.opener = f.$('[data-action="overview-open"]');
  f.card = () => f.$('.overview-modal__card');
  f.open = () => { f.opener.focus(); f.opener.fire('click'); assert.equal(f.state.overviewOpen, true, 'the overview opened'); };
  f.section = key => f.card().querySelector('[data-overview-analysis="' + key + '"]');
  f.analysisKeys = () => f.card().querySelectorAll('[data-overview-analysis]').map(node => node.getAttribute('data-overview-analysis'));
  // 審計員點分析區標題：瀏覽器先改 details.open，再送 toggle。
  f.toggle = (key, open) => { const section = f.section(key); assert.ok(section, key + ' section rendered'); section.open = open; section.fire('toggle'); return section; };
  f.factKeys = () => f.card().querySelectorAll('[data-overview-fact-key]').map(node => node.getAttribute('data-overview-fact-key'));
  f.fact = key => f.card().querySelector('[data-overview-fact-key="' + key + '"]');
  f.text = node => (node ? node.textContent : '');
  // 畫在某個分析區裡、目前還沒被釋放的圖。
  f.liveChartsIn = key => f.charts.live().filter(chart => f.section(key) && f.section(key).contains(chart.element));
  f.lastOption = kind => { const chart = f.charts.charts.filter(item => item.element.getAttribute('data-overview-chart') === kind).at(-1);
    assert.ok(chart, kind + ' chart was drawn'); return chart.options.at(-1).option; };
  return f;
}

// 走完前五步、匯出過目前版本底稿的合成案件。
function completeCase(f, { entityName = '合成客戶股份有限公司' } = {}) {
  f.store.setProject({ projectId: '合成案件甲', entityName, periodStart: '2025-01-01', periodEnd: '2025-11-30',
    databaseProvider: 'sqlite', operatorId: 'synthetic.operator', createdUtc: '2025-12-01T01:02:03Z' });
  f.store.setImportResult('gl', { batchId: 'gl', rowCount: 1234, columns: ['Account'], importedUtc: '2025-12-01T02:00:00Z' });
  f.store.setImportResult('tb', { batchId: 'tb', rowCount: 56, columns: ['Account'], importedUtc: '2025-12-01T02:10:00Z' });
  f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed', committedUtc: '2025-12-01T02:20:00Z' });
  f.store.setMappingCommitted('tb', { mapping: {}, mode: 'direct', committedUtc: '2025-12-01T02:30:00Z' });
  f.store.setLastRun('validate', validateRun());
  f.store.setLastRun('prescreen', prescreenRun());
  f.store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod', generatedUtc: '2025-12-02T05:00:00Z' });
  const source = { validationRunId: 'v-ov', scenarioRevision: 'r1', scenarioPositions: [1, 2, 3] };
  f.store.setReportArtifacts([
    { artifactId: 'c1', kind: 'criteriaSelectionReport', fileName: '合成條件篩選報告.xlsx', fileState: 'asPublished', stale: false,
      generatedUtc: '2025-12-02T06:00:00Z', sourceRef: source },
    { artifactId: 'w1', kind: 'workingPaper', fileName: '合成工作底稿.xlsx', fileState: 'asPublished', stale: false, bytes: 1234567,
      generatedUtc: '2025-12-02T07:00:00Z', sourceRef: source }
  ]);
}

const elementChildren = node => node.children.filter(child => child.tagName !== 'JET-TEXT');

// =====================================================================================================
// 情境 18　流程總覽
// =====================================================================================================

test('journey 18: before validation the overview shows one empty statistics state; afterwards it mirrors validation statistics in a summary-first layout with collapsed analyses', () => {
  const f = setup();
  // usageJourneyFixture 的合成案件已有一次驗證結果，但沒有統計（舊結果或尚未驗證）。
  f.open();
  const empty = f.card().querySelector('.overview__population');
  assert.ok(empty, 'the statistics area is shown');
  assert.ok(empty.classList.contains('overview__population--empty'), 'without statistics the area is an empty state');
  assert.ok(empty.querySelector('h4') && empty.querySelector('h4').textContent.trim(), 'the empty state has a heading');
  assert.doesNotMatch(empty.textContent, /\d/, 'the empty state does not show 0 or any figure in place of statistics');
  assert.equal(f.factKeys().filter(key => key.startsWith('validation.stats.')).length, 0, 'no statistic is rendered');
  assert.deepEqual(f.analysisKeys(), [], 'no analysis section appears before statistics exist');
  assert.ok(f.card().querySelector('.overview__analyses') === null, 'no second empty state for analyses');

  // 驗證完成：數字直接取自驗證結果的 stats；第四步的分錄測試範圍摘要（999）是不同的數字，不得出現。
  f.store.setLastRun('validate', validateRun());
  assert.ok(!f.card().querySelector('.overview__population').classList.contains('overview__population--empty'));
  assert.match(f.text(f.fact('validation.stats.gl-row-count')), /1,234/);
  assert.match(f.text(f.fact('validation.stats.voucher-count')), /321/);
  assert.match(f.text(f.fact('validation.stats.total-debit')), /98,765\.40/);
  assert.match(f.text(f.fact('validation.stats.total-credit')), /98,765\.40/);
  assert.doesNotMatch(f.card().textContent, /999/, 'the population summary is not shown in place of the statistics');

  // 摘要在前：抬頭、進度、六個階段與所選階段、統計、分析、其他資訊、提醒，順序固定；訊息不複製進總覽。
  const overview = f.card().querySelector('.overview');
  assert.deepEqual(elementChildren(overview).map(child => child.className.split(/\s+/)[0]), [
    'overview__head', 'overview__progress', 'overview__pipeline', 'overview-step-detail',
    'overview__population', 'overview__analyses', 'overview__technical', 'overview__disclaimer'
  ]);
  assert.ok(f.card().querySelector('.messages__list') === null);
  // 三個分析區與其他資訊預設收合。
  assert.deepEqual(f.analysisKeys(), ['concentration', 'amount', 'rule-period']);
  for (const key of f.analysisKeys()) assert.equal(f.section(key).tagName, 'DETAILS');
  for (const key of f.analysisKeys()) assert.equal(f.section(key).open, false, key + ' starts collapsed');
  assert.equal(f.card().querySelector('.overview__technical').tagName, 'DETAILS');
  assert.ok(!f.card().querySelector('.overview__technical').hasAttribute('open'), 'technical details start collapsed');

  // 展開一個分析區後，背景資料更新讓總覽整張重畫：展開的仍展開，其他仍收合。
  const before = f.section('amount');
  f.toggle('amount', true);
  f.store.touch();
  assert.ok(f.section('amount') !== before, 'the card was rebuilt');
  assert.equal(f.section('amount').open, true, 'the expanded section stays expanded after a redraw');
  assert.equal(f.section('concentration').open, false);
  assert.equal(f.section('rule-period').open, false);
  // 關上再打開是新的一次檢視，全部回到收合。
  f.store.setOverviewOpen(false); f.timers.run();
  f.open();
  for (const key of f.analysisKeys()) assert.equal(f.section(key).open, false, key + ' collapsed again after reopening');
  assert.deepEqual(plain(f.requests), [], 'the overview never calls the backend');
});

test('journey 18: the six stage tabs follow the ARIA tabs keyboard contract, explain unfinished and locked stages, and never move the workflow', () => {
  const f = setup({ charts: false });
  // 案件只做到匯入：欄位配對可處理但沒完成，後面三步等待前置步驟。
  f.store.setImportResult('gl', { batchId: 'gl', rowCount: 2, columns: ['Account'] });
  f.store.setImportResult('tb', { batchId: 'tb', rowCount: 2, columns: ['Account'] });
  f.store.setLastRun('validate', null); f.store.setLastRun('prescreen', null);
  f.store.setSavedScenarios([]); f.store.setFilterResultRef(null);
  f.store.setStepIndex(2);
  f.open();
  const tabs = () => f.card().querySelectorAll('[role="tablist"][aria-label] > [role="tab"]').filter(tab => tab.hasAttribute('data-overview-review'));
  const panel = () => f.card().querySelector('#overview-step-detail');
  function expectSelected(index, label) {
    const all = tabs();
    assert.equal(all.length, 6, label + ': six stages');
    all.forEach((tab, at) => {
      assert.equal(tab.getAttribute('aria-selected'), at === index ? 'true' : 'false', label + ': aria-selected of stage ' + at);
      assert.equal(tab.getAttribute('tabindex'), at === index ? '0' : '-1', label + ': tabindex of stage ' + at);
      assert.equal(tab.getAttribute('aria-controls'), 'overview-step-detail');
    });
    assert.equal(panel().getAttribute('role'), 'tabpanel');
    assert.equal(panel().getAttribute('aria-live'), 'polite', label + ': the stage detail is announced when it changes');
    assert.equal(panel().getAttribute('aria-labelledby'), all[index].getAttribute('id'), label + ': the panel is labelled by the selected tab');
  }
  expectSelected(2, 'opened on the current stage');
  tabs()[2].focus();
  // 使用者 2026-10-02 整體複審 T4 裁定「尚未提交」改「尚未完成」（docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md
  // 「T4 尚未提交／完成；投影資料：照建議」與實作紀錄「T4：「尚未提交」改「尚未完成」」），逐字比對。
  for (const key of ['mapping.gl.commit', 'mapping.tb.commit']) {
    assert.equal(f.fact(key).querySelector('.overview-step-fact__value').textContent, '尚未完成', key);
  }
  assert.doesNotMatch(panel().textContent, /尚未提交/);

  const sequence = [['ArrowRight', 3], ['Home', 0], ['End', 5], ['ArrowLeft', 4], ['Home', 0], ['ArrowLeft', 5], ['ArrowRight', 0]];
  for (const [key, expected] of sequence) {
    const event = f.key(key);
    assert.ok(event.defaultPrevented, key + ' is handled by the tabs');
    expectSelected(expected, key);
    assert.ok(f.dom.document.activeElement === tabs()[expected], key + ': focus follows the selection');
  }
  // 被鎖住的階段（第四步）：說明在等待前置步驟，並寫出缺什麼（只比對意思：提到配對）。
  f.key('End'); f.key('ArrowLeft'); f.key('ArrowLeft');
  expectSelected(3, 'locked stage');
  assert.equal(f.text(f.fact('workflow.stage.3.status')).includes('完成'), false, 'the locked stage is not marked done');
  const attention = panel().querySelector('[role="note"]');
  assert.ok(attention, 'a locked stage explains why it is waiting');
  assert.match(attention.textContent, /等待/);
  assert.match(attention.textContent, /配對/);
  assert.match(f.text(tabs()[3]), /等待/, 'the locked stage tab itself says it is waiting');
  // 第 4 個階段（資料驗證與測試）的紀錄區標題表示預篩選與執行紀錄（只比對意思）。
  assert.match(panel().querySelector('.overview-step-detail__eyebrow').textContent, /預篩選.*紀錄/);

  // 用滑鼠點階段也一樣：只換總覽裡的詳情。
  for (const index of [0, 1, 2]) { tabs()[index].fire('click'); expectSelected(index, 'click ' + index); }
  assert.equal(f.state.currentStepIndex, 2, 'reviewing stages never moves the workflow');
  assert.equal(f.state.overviewOpen, true, 'reviewing stages keeps the overview open');
  assert.deepEqual(plain(f.requests), [], 'reviewing stages sends nothing to the backend');
});

test('journey 18: across all six stages every visible fact appears on exactly one surface and the audit period is written once', () => {
  const f = setup({ charts: false });
  completeCase(f);
  f.open();
  const union = new Set();
  for (let index = 0; index < 6; index++) {
    f.card().querySelector('#overview-stage-tab-' + index).fire('click');
    const keys = f.factKeys();
    assert.equal(new Set(keys).size, keys.length, 'stage ' + index + ': no fact is shown twice: ' + keys.join(','));
    keys.forEach(key => union.add(key));
    const text = f.card().textContent;
    assert.equal(text.split('2025-01-01').length - 1, 1, 'stage ' + index + ': the audit period start appears once');
    assert.equal(text.split('2025-11-30').length - 1, 1, 'stage ' + index + ': the audit period end appears once');
    assert.equal(text.split('全查核期間').length - 1, 1, 'stage ' + index + ': the analysis scope is written once');
  }
  // 現行總覽的全部事實（寫死的清單，取代原 C# 方法逐一數 app.js 定義點的做法）。
  // 使用者 2026-10-19 裁定「其他資訊」只留資料庫，執行識別碼與規則版本兩項移出清單；
  // 第一次失敗：FrontendMapping 收據 20261019-122433278-c327738e178a4e6e829f666f3c1bd5c1。
  assert.deepEqual([...union].sort(), [
    'analysis.scope',
    'export.scenario-count', 'export.working-paper', 'export.working-paper-bytes',
    'filter.criteria-report', 'filter.current-result', 'filter.population-scope', 'filter.saved-scenarios',
    'import.gl.source', 'import.supporting', 'import.tb.source',
    'mapping.gl.commit', 'mapping.tb.commit',
    'prescreen.current-result',
    'project.audit-period', 'project.case-id', 'project.created-at', 'project.database-provider', 'project.operator',
    'validation.completeness', 'validation.current-result',
    'validation.stats.gl-row-count', 'validation.stats.total-credit', 'validation.stats.total-debit', 'validation.stats.voucher-count',
    'workflow.aggregate',
    'workflow.location',
    'workflow.stage.0.status', 'workflow.stage.1.status', 'workflow.stage.2.status', 'workflow.stage.3.status', 'workflow.stage.4.status', 'workflow.stage.5.status'
  ]);
  // 案件編號只在抬頭；「其他資訊」只放資料庫，執行識別碼與規則版本不再顯示（同上一段的裁定與收據）。
  const technical = f.card().querySelector('.overview__technical').textContent;
  assert.doesNotMatch(technical, /合成案件甲|2025-01-01|案件編號|查核期間/);
  assert.doesNotMatch(f.card().querySelector('.overview__population').textContent, /查核期間|2025-01-01/, 'the statistics do not repeat the period');
  assert.equal(f.card().querySelector('.overview__technical summary').textContent, '其他資訊');
  assert.match(f.text(f.fact('project.database-provider')), /SQLite|DuckDB|SQL Server/);
  assert.doesNotMatch(technical, /p-ov|synthetic-logic-7/);
  assert.doesNotMatch(f.card().textContent, /執行識別碼|規則版本|技術資訊/);
  assert.deepEqual(plain(f.requests), []);
});

test('journey 18: the overview title is the client name, or the case name when no client is filled in, and the name is not repeated', () => {
  const f = setup({ charts: false });
  completeCase(f);
  f.open();
  const head = () => f.card().querySelector('.overview__head');
  assert.equal(head().querySelector('.overview__title').textContent, '合成客戶股份有限公司');
  assert.match(f.text(f.fact('project.case-id')), /合成案件甲/, 'the case name is written next to the period');
  assert.ok(head().querySelector('[data-overview-fact-key="project.case-id"]'), 'the case name sits in the heading');
  // 小標逐字：使用者 2026-10-03 對 O8「總覽標題用「—」連接公司名稱與「分錄測試」：要修」，實作紀錄寫明改成小標「分錄測試流程總覽」
  // （docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md 的 O8 兩處）；KCT 需求計畫 docs/history/specs/2026-10-06-kct-gl-requirements-plan.md
  // 記為「先前裁定的畫面用語」，保留不改。
  // O8 的第一次失敗收據：20261003-065529947-9b08f413915748fe812bdead0a6d6d89（原寫在已刪除的
  // UserFeedbackStepsOneToFourFrontendTests.CreateForm_OnlyCollectsOptionalCaseMetadataAndDoesNotCollectOperator，2026-10-14 第一波整併搬來）。
  assert.equal(head().querySelector('.overview__eyebrow').textContent, '分錄測試流程總覽');
  assert.doesNotMatch(head().textContent, /—/, 'the name is not joined to a label with a dash');
  assert.equal(head().textContent.split('合成客戶股份有限公司').length - 1, 1);
  assert.equal(head().textContent.split('合成案件甲').length - 1, 1);

  f.store.setProject({ ...f.state.project, entityName: '' });
  assert.equal(head().querySelector('.overview__title').textContent, '合成案件甲', 'without a client the case name is the title');
  assert.ok(head().querySelector('[data-overview-fact-key="project.case-id"]') === null, 'the case name is not repeated below the title');
  assert.equal(head().textContent.split('合成案件甲').length - 1, 1);
  assert.match(f.text(f.fact('project.audit-period')), /2025-01-01.*2025-11-30/);
});

// =====================================================================================================
// 情境 19　總覽分析圖表
// =====================================================================================================

test('journey 19: analyses appear only with validation statistics, after the statistics, in the order concentration, amount, prescreen result distribution', () => {
  const f = setup();
  f.store.setLastRun('prescreen', prescreenRun());
  f.open();
  assert.deepEqual(f.analysisKeys(), [], 'a prescreen result alone does not bring analyses before statistics exist');
  f.store.setLastRun('validate', validateRun());
  assert.deepEqual(f.analysisKeys(), ['concentration', 'amount', 'rule-period']);
  const order = elementChildren(f.card().querySelector('.overview')).map(child => child.className.split(/\s+/)[0]);
  assert.ok(order.indexOf('overview__population') < order.indexOf('overview__analyses'), 'statistics come before the analyses');
  // 分析區標題：預篩選、金額級距與累積分布、預篩選結果分布（只比對意思）。
  const titles = f.analysisKeys().map(key => f.section(key).querySelector('summary').textContent);
  assert.match(titles[0], /^預篩選/);
  assert.doesNotMatch(titles[0], /結果分布/);
  assert.match(titles[1], /金額.*累積/);
  assert.match(titles[2], /預篩選.*結果分布/);
  assert.doesNotMatch(f.card().textContent, /輔助訊號|常用母體彙總/, 'the retired names are gone');
  f.store.setLastRun('validate', validateRun({ extra: { stats: null } }));
  assert.deepEqual(f.analysisKeys(), [], 'the analyses disappear again when the statistics are gone');
  assert.equal(f.charts.charts.length, 0, 'nothing is drawn while every section is collapsed');
});

test('journey 19: the concentration section explains each missing, outdated and not-applicable result instead of showing zero', () => {
  const f = setup();
  f.store.setLastRun('validate', validateRun());
  f.open();
  f.toggle('concentration', true);
  const content = () => f.section('concentration').querySelector('.overview__analysis-content');
  const cases = [
    ['no prescreen run', null, text => { assert.match(text, /尚未執行預篩選/); }],
    ['outdated run without the statistic', prescreenRun({ concentration: undefined }), text => { assert.match(text, /重新執行預篩選/); }],
    ['not applicable with a reason', prescreenRun({ concentration: { status: 'NA', naReason: '合成原因：查核期間沒有編製人員' } }),
      text => { assert.match(text, /不適用/); assert.match(text, /合成原因：查核期間沒有編製人員/); }],
    ['not applicable without a reason', prescreenRun({ concentration: { status: 'NA', naReason: null } }),
      text => { assert.match(text, /不適用/); assert.match(text, /0/, 'the default explanation says not applicable is different from zero'); assert.ok(text.length > 10); }]
  ];
  for (const [label, run, check] of cases) {
    f.store.setLastRun('prescreen', run);
    const text = content().textContent;
    check(text);
    assert.doesNotMatch(text, /\d+\.\d%/, label + ': no percentage is shown');
    assert.ok(content().querySelector('[data-overview-chart]') === null, label + ': no chart is drawn');
    assert.ok(content().querySelector('[role="tablist"]') === null, label + ': no view tabs without data');
  }
  // 正常結果但後端沒給前五位占比：照樣畫圖，說明不寫前五位占比，也不寫 0.0%。
  f.store.setLastRun('prescreen', prescreenRun({ concentration: concentration({ preparers: preparers({ top5SharePct: null }) }) }));
  const text = content().textContent;
  assert.ok(content().querySelector('[data-overview-chart]'), 'the chart is drawn');
  assert.doesNotMatch(text, /前 ?5 ?位.*占/, 'no top-five share sentence without the figure');
  assert.doesNotMatch(text, /0\.0%/);
  assert.match(text, /8 位.*45 筆/, 'the denominator still comes from the backend totals');
  f.store.setLastRun('prescreen', prescreenRun());
  assert.match(content().textContent, /前 ?5 ?位.*90\.0%/, 'with the figure the share is written');
  assert.deepEqual(plain(f.requests), []);
});

test('journey 19: the amount distribution asks for a rerun when the statistic is missing and keeps missing cumulative points as gaps', () => {
  const f = setup();
  f.store.setLastRun('validate', validateRun({ amount: null }));
  f.open();
  f.toggle('amount', true);
  const content = () => f.section('amount').querySelector('.overview__analysis-content');
  for (const [label, amount] of [['missing statistic', null], ['incomplete bins', amountDistribution({ bins: 14 })]]) {
    f.store.setLastRun('validate', validateRun({ amount }));
    assert.match(content().textContent, /重新執行資料驗證/, label + ': asks for a validation rerun');
    assert.ok(content().querySelector('[data-overview-chart]') === null, label + ': no chart');
  }
  const run = validateRun(), snapshot = JSON.stringify(run);
  f.store.setLastRun('validate', run);
  f.timers.run();
  const option = f.lastOption('amount');
  assert.equal(JSON.stringify(run), snapshot, 'drawing does not change the backend result');
  // 零元另計，累積分布以非零金額分錄為分母，沒有累積比例的級距不連線（只比對意思）。
  assert.match(content().textContent, /零元.*另計/);
  assert.match(content().textContent, /非零.*分母/);
  assert.match(content().textContent, /重要性門檻/);
  assert.deepEqual(plain(option.series.map(series => series.type)), ['bar', 'line']);
  assert.equal(option.series[1].data.length, 15);
  assert.equal(option.series[1].data[0], null, 'a missing cumulative point stays null, not 0');
  assert.equal(option.series[1].data[7], null);
  assert.equal(option.series[1].data[1], 6.5);
  assert.equal(option.series[1].connectNulls, false);
  // 橫軸只標 15 個上界；提示框寫完整區間與完整筆數；筆數軸用 k、M 縮寫。
  assert.deepEqual(plain(option.xAxis.data), ['零元', '1k', '2k', '5k', '10k', '20k', '50k', '100k', '200k', '500k', '1M', '2M', '5M', '10M', '>10M']);
  assert.equal(option.series[0].data[14].value, 14000);
  const tip = index => option.tooltip.formatter([{ seriesName: '分錄筆數', data: option.series[0].data[index] }]);
  assert.match(tip(10), /500k–1M/);
  assert.match(tip(10), /10,000/);
  assert.match(tip(10), /65\.0%/);
  assert.match(tip(14), /&gt;10M/, 'the tooltip text is escaped');
  assert.match(tip(0), /—/, 'a missing cumulative value is a dash in the tooltip');
  assert.equal(option.yAxis[0].axisLabel.formatter(12000), '12k');
  assert.equal(option.yAxis[0].axisLabel.formatter(2500000), '2.5M');
  assert.deepEqual(plain(f.requests), []);
});

test('journey 19: the prescreen result distribution keeps not-applicable rules out of the chart, separates a true zero from tiny rates, and says GL is zero instead of 0%', () => {
  const f = setup();
  f.store.setLastRun('validate', validateRun());
  f.open();
  f.toggle('rule-period', true);
  const content = () => f.section('rule-period').querySelector('.overview__analysis-content');
  for (const [label, run, pattern] of [['no prescreen run', null, /尚未執行預篩選/],
    ['outdated run without the statistic', prescreenRun({ rulePeriod: undefined }), /重新執行預篩選/],
    ['no rules', prescreenRun({ rulePeriod: { population: 10, rules: [] } }), /重新執行預篩選/]]) {
    f.store.setLastRun('prescreen', run);
    assert.match(content().textContent, pattern, label);
    assert.ok(content().querySelector('[data-overview-chart]') === null, label + ': no chart');
  }

  const run = prescreenRun(), snapshot = JSON.stringify(run);
  f.store.setLastRun('prescreen', run);
  const option = f.lastOption('rule-period');
  assert.equal(JSON.stringify(run), snapshot, 'drawing does not change the backend result');
  // 不適用的規則只列在「不適用規則」，寫出後端的原因；原因裡的 < > 照字面顯示（Ui.esc 跳脫後才放進畫面）。
  const naList = content().querySelector('[aria-label="不適用規則"]');
  assert.ok(naList, 'not-applicable rules are listed below the chart');
  const naRows = naList.querySelectorAll('.overview-bi__na-row');
  assert.equal(naRows.length, 1);
  assert.match(naRows[0].textContent, /缺少 <摘要> 欄位（合成）/);
  const naLabel = naRows[0].querySelector('span').textContent;
  assert.ok(naLabel && !plain(option.yAxis.data).includes(naLabel), 'the not-applicable rule is not a bar');
  assert.equal(option.yAxis.data.length, 3);
  // 真正的 0、極小值與一般值依後端順序分開保留（原孤兒測試期望 [null, 0, 0.3]，見檔頭第 1 點）。
  assert.deepEqual(plain(option.series[0].data.map(row => row.value)), [0, 0.0000003, 0.3]);
  const axis = option.xAxis.axisLabel.formatter;
  assert.match(axis(0), /^0(\.0+)?%$/, 'a true zero is shown as 0%');
  assert.doesNotMatch(axis(0.0000003), /^0(\.0+)?%$|—/, 'a tiny non-zero rate is not shown as 0%');
  assert.match(axis(0.0000003), /3/);
  // 提示框：全查核期間、符合的分錄與傳票數、分母寫 GL（使用者 2026-10-08 裁定「納入測試的分錄」改名為「GL」：
  // docs/history/specs/2026-10-06-kct-gl-requirements-plan.md「`納入測試的分錄` 改成 `GL`」與「全系統統一 (Recommended)」，名稱逐字）。
  const tip = option.tooltip.formatter([{ data: option.series[0].data[2] }]);
  assert.match(tip, /查核期間/);
  assert.match(tip, /3 筆/);
  assert.match(tip, /2 張/);
  assert.match(tip, /GL 1,000 筆/);
  assert.doesNotMatch(tip, /母體/);
  const note = content().querySelector('.overview-bi__note').textContent;
  assert.match(note, /GL 分錄數 1,000 筆/, 'the denominator is the GL entry count');
  assert.match(note, /非風險評估/, 'the note says this describes a distribution, not a risk assessment');
  assert.doesNotMatch(note, /不代表 0%/);

  // GL 為 0 筆：比例都沒有，說明寫出 GL 為 0 筆、以「—」表示、不代表 0%（GL 逐字，句子只比對意思）。
  f.store.setLastRun('prescreen', prescreenRun({ rulePeriod: rulePeriod({ population: 0 }) }));
  const zeroNote = content().querySelector('.overview-bi__note').textContent;
  assert.match(zeroNote, /GL 為 0 筆/);
  assert.match(zeroNote, /—/);
  assert.match(zeroNote, /不代表 0%/);
  const zeroOption = f.lastOption('rule-period');
  assert.deepEqual(plain(zeroOption.series[0].data.map(row => row.value)), [null, null, null]);
  assert.match(zeroOption.tooltip.formatter([{ data: zeroOption.series[0].data[0] }]), /符合比例 —/);
  // 全部不適用：沒有圖，只有清單。
  f.store.setLastRun('prescreen', prescreenRun({ rulePeriod: { population: 10, rules: [rulePeriod().rules[0]] } }));
  assert.ok(content().querySelector('[data-overview-chart]') === null);
  assert.ok(content().querySelector('[aria-label="不適用規則"]'));
  assert.deepEqual(plain(f.requests), []);
});

test('journey 19: charts use the local SVG renderer, replace their whole option, follow window and container size, and keep tooltips inside the card', () => {
  const f = setup();
  f.store.setLastRun('validate', validateRun());
  f.store.setLastRun('prescreen', prescreenRun());
  const snapshot = JSON.stringify(f.state.lastRuns);
  f.open();
  assert.equal(f.charts.charts.length, 0, 'opening the overview draws nothing while sections are collapsed');
  for (const key of ['concentration', 'amount', 'rule-period']) f.toggle(key, true);
  assert.equal(f.charts.charts.length, 3);
  for (const chart of f.charts.charts) {
    assert.deepEqual(plain(chart.initOptions), { renderer: 'svg' });
    assert.equal(chart.theme, null);
    assert.equal(chart.options.length, 1);
    assert.equal(chart.options[0].notMerge, true, 'every draw replaces the whole option');
    assert.equal(chart.options[0].option.animation, false);
    assert.equal(chart.options[0].option.tooltip.confine, true);
    assert.ok(f.charts.observers[0].observed.includes(chart.element), 'each chart is watched for container size changes');
  }
  assert.equal(JSON.stringify(f.state.lastRuns), snapshot, 'drawing does not change the backend results');
  // 排定的 30 毫秒重算大小、視窗縮放、容器大小改變，各讓每個圖重算一次。
  assert.ok(f.timers.pending().some(entry => entry.delay === 30), 'a size recalculation is scheduled after drawing');
  f.timers.run();
  for (const chart of f.charts.charts) assert.ok(chart.resizeCount >= 1, 'the scheduled recalculation reaches every chart');
  const counts = () => f.charts.charts.map(chart => chart.resizeCount);
  const before = counts();
  f.resizeWindow();
  assert.deepEqual(counts(), before.map(count => count + 1), 'a window resize recalculates every chart once');
  f.charts.observers[0].trigger();
  assert.deepEqual(counts(), before.map(count => count + 2), 'a container size change recalculates every chart once');
  assert.equal(f.windowListeners.filter(entry => entry.type === 'resize').length, 1, 'one window resize listener for all charts');

  // 另外兩個圖的設定：較少使用之科目切換後才畫。
  const preparerOption = f.lastOption('concentration');
  assert.deepEqual(plain(preparerOption.series.map(series => series.type)), ['bar', 'bar', 'line']);
  assert.deepEqual(plain(preparerOption.series[0].data), [20, 15, 10]);
  assert.deepEqual(plain(preparerOption.series[1].data), [3, 2, null], 'the others bar has no manual count');
  assert.deepEqual(plain(preparerOption.series[2].data), [40, 70, null], 'the others bar has no cumulative share');
  assert.equal(preparerOption.series[2].connectNulls, false);
  assert.match(preparerOption.tooltip.formatter([{ dataIndex: 0 }]), /甲&lt;&amp;/, 'preparer names are escaped');
  assert.deepEqual(plain(f.requests), []);
});

test('journey 19: charts are drawn only in expanded sections, released when a section collapses or the overview closes, and redrawn into the new card', () => {
  const f = setup();
  f.store.setLastRun('validate', validateRun());
  f.store.setLastRun('prescreen', prescreenRun());
  f.open();
  f.toggle('amount', true);
  assert.equal(f.charts.charts.length, 1, 'only the expanded section is drawn');
  assert.equal(f.liveChartsIn('amount').length, 1);
  f.toggle('rule-period', true);
  assert.equal(f.charts.live().length, 2);

  // 收合：收的動畫結束後才釋放，其他區不受影響。
  const amountChart = f.liveChartsIn('amount')[0];
  f.toggle('amount', false);
  assert.equal(amountChart.disposed, false, 'the chart stays while the section folds');
  f.timers.run();
  assert.equal(amountChart.disposed, true, 'the collapsed section releases its chart');
  assert.ok(!f.charts.observers[0].observed.includes(amountChart.element), 'the released chart is no longer watched');
  assert.equal(f.liveChartsIn('rule-period').length, 1, 'the other section keeps its chart');
  // 收合途中又展開：不會被延後的釋放拿掉。
  f.toggle('amount', true); f.toggle('amount', false); f.toggle('amount', true); f.timers.run();
  assert.equal(f.liveChartsIn('amount').length, 1, 'reopening during the fold keeps the new chart');

  // 背景資料更新讓總覽重畫：舊圖先全部釋放，新圖畫在目前畫面裡的新容器，每區只有一個圖。
  const old = f.charts.live();
  f.store.touch();
  assert.ok(old.every(chart => chart.disposed), 'every old chart is released before the card is replaced');
  const live = f.charts.live();
  assert.equal(live.length, 2);
  for (const chart of live) {
    assert.ok(f.card().contains(chart.element), 'the new chart is mounted in the current card');
    assert.ok(old.every(previous => previous.element !== chart.element));
  }
  assert.equal(f.liveChartsIn('amount').length, 1);
  assert.equal(f.liveChartsIn('rule-period').length, 1);

  // 關閉總覽：淡出結束後全部釋放並停止觀察大小。
  f.store.setOverviewOpen(false);
  f.timers.run();
  assert.equal(f.charts.live().length, 0, 'closing the overview releases every chart');
  assert.equal(f.charts.observers[0].observed.length, 0);
  assert.deepEqual(plain(f.requests), []);
});

test('journey 19: the concentration view tabs switch with arrow keys, Home and End, keep focus and ARIA links, and redraw only their own chart', () => {
  const f = setup();
  f.store.setLastRun('validate', validateRun());
  f.store.setLastRun('prescreen', prescreenRun());
  f.open();
  f.toggle('concentration', true);
  f.toggle('amount', true);
  const amountChart = f.liveChartsIn('amount')[0];
  const tab = key => f.card().querySelector('[data-overview-tab="' + key + '"]');
  const panel = () => f.card().querySelector('#overview-concentration-panel');
  function expectView(key, label) {
    for (const other of ['preparers', 'accounts']) {
      assert.equal(tab(other).getAttribute('role'), 'tab');
      assert.equal(tab(other).getAttribute('aria-controls'), 'overview-concentration-panel');
      assert.equal(tab(other).getAttribute('aria-selected'), other === key ? 'true' : 'false', label + ': aria-selected ' + other);
      assert.equal(tab(other).getAttribute('tabindex'), other === key ? '0' : '-1', label + ': tabindex ' + other);
    }
    assert.equal(panel().getAttribute('role'), 'tabpanel');
    assert.equal(panel().getAttribute('aria-labelledby'), tab(key).getAttribute('id'), label + ': the panel is labelled by the selected tab');
    assert.ok(f.dom.document.activeElement === tab(key), label + ': focus is on the selected tab');
    assert.equal(f.liveChartsIn('concentration').length, 1, label + ': one chart in the view');
  }
  tab('preparers').focus();
  const sequence = [['ArrowRight', 'accounts'], ['Home', 'preparers'], ['End', 'accounts'], ['ArrowLeft', 'preparers'], ['ArrowLeft', 'accounts'], ['ArrowRight', 'preparers']];
  for (const [key, expected] of sequence) {
    assert.ok(f.key(key).defaultPrevented, key + ' is handled');
    expectView(expected, key);
    assert.match(panel().textContent, expected === 'accounts' ? /使用次數最低/ : /編製人員/, key + ': the panel shows the selected view');
  }
  f.key('End');
  const rare = f.lastOption('concentration');
  assert.equal(rare.series[0].type, 'scatter');
  assert.deepEqual(plain(rare.yAxis.data), ['9002 合成第二', '9001 合成第一'], 'accounts keep the backend order');
  assert.equal(rare.animation, false);
  assert.equal(rare.tooltip.confine, true);
  tab('preparers').fire('click');
  expectView('preparers', 'click');
  assert.equal(amountChart.disposed, false, 'switching the view does not redraw other sections');
  assert.equal(f.state.currentStepIndex, 0, 'switching the view does not move the workflow');
  assert.deepEqual(plain(f.requests), [], 'switching the view does not call the backend');
});
