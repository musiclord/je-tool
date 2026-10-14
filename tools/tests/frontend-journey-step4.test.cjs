'use strict';
// 情境 7：第四步執行資料驗證並解讀結果；情境 8：完整性不符時能做什麼，以及執行預篩選；
// 情境 9：第四步科目分類與科目配對（收尾四項第一項第 8 批，2026-10-14）。
//
// 審計員的操作：
//   情境 7　按「執行驗證」並回應合成結果；看 GL 分錄數、三項測試的說明、總帳日期空白的來源檔與列號並載入更多；
//           在右側資料預覽打開 GL(未納入測試) 看原因；展開資料可靠性測試的抽樣明細，看攸關資料元素的金額欄。
//   情境 8　沒有驗證結果、舊驗證結果沒有判定、後端判定不可繼續、可繼續但沒有預篩選結果時，看預篩選卡、左側第五步與頁尾；
//           按鈕狀態沒更新時硬按預篩選；執行預篩選並看結果的排列。
//   情境 9　打開分類設定，新增與改名分類，名稱空白或重複，儲存失敗後重試成功；移除只被情境用到的分類；看內建分類；
//           驗證時準備科目配對檔失敗，在「在 JET 配對」分頁看到原因並到 Excel 分頁重試；在 Excel 分頁按「預覽」。
//
// 夾具：本檔的 step4Fixture。用 tools/tests/support/frontend-fixtures.cjs 的假 DOM 與記錄請求的 JetApi，
// 載入正式的 state、ui-core、各步驟腳本、data-preview 與 app.js；中欄由正式的 Ui.renderStep 畫第四步，
// 左側目錄由 app.js 正式的 renderStepNav 畫，右側資料預覽由正式的 Ui.initDataPreview 綁定 index.html 的標記。
// Store 改變時照 app.js 的條件（contentVersion 或目前步驟改變）重繪。
// 計畫原本指定的 taxonomyReviewFixture 用正規式拼出的假節點、batch8Fixture 用手寫的控制項、batch9PagerLifecycleFixture
// 用一個合成的規則項目代替正式的驗證項目，三者都不經過正式畫面，所以這裡沒有複製它們（第 6 批同樣的理由）。
//
// 取代的 C# 字串測試（src/JET/tests/JET.Tests/Architecture/，2026-10-14 第二、三波整併刪除，紀錄見 impl-item1-wave23-consolidation.md）：
//   情境 7　UserFeedbackStepsOneToFourFrontendTests.ValidationUi_UsesRequestedPlainDescriptionsAndNamesTheActualSourceQualityCheck
//           BlankPostDateFrontendTests.ValidationUi_PresentsBlankPostDateAsSourceQualityOnly
//           PopulationAndDynamicColumnFrontendTests.PopulationSummary_IsRemovedWithoutReplacingBackendCountsWithFrontendMath
//           PopulationAndDynamicColumnFrontendTests.ExcludedEntries_HaveTheirOwnBackendDatasetAndReasonLabels
//           PopulationAndDynamicColumnFrontendTests.SourceQualityPage_IsTheOnlyFullDetailPathForSourceFindings
//           PopulationAndDynamicColumnFrontendTests.DynamicResultColumns_ComeFromTheBackendColumnMetadata 的抽樣明細部分
//           （命中明細部分屬情境 13，第 9 批）
//   情境 8　CompletenessEligibilityFrontendTests 的 StepGate_UsesBackendEligibility_AllowsAdvancedFilterWithoutPrescreen_AndDoesNotRecomputeRawFacts、
//           ValidationCardAndPrescreenButton_SeparateWarningsFromAvailability、PrescreenAction_StopsBeforeBackendCallWhenBackendEligibilityIsIneligible、
//           StepFooter_EscapesDynamicBackendGateReason；ScreenWordingReviewFrontendTests.CompletenessReason_DistinguishesNeverRunFromOutdatedSummary；
//           PrescreenPositioningFrontendTests.StepFour_PresentsAggregatesBeforeSecondarySignalsAndOptionalReport
//   情境 9　AccountTaxonomyFrontendTests.Editor_ProtectsBuiltInsWithoutBlockingCategoriesUsedOnlyBySavedScenarios、
//           AccountTaxonomyFrontendTests.Save_UsesRevisionOptimisticConcurrencyAndNeverMintsCustomIds、
//           MappingNamesAndTemplateStatusFrontendTests.AccountTemplateStatus_ShowsOnTheJetTabOnlyWhilePreparingOrAfterFailure、
//           DataPreviewFrontendTests.AccountMappingPreviewButton_OpensDirectAccountMappingDataset、
//           WorkflowControlsAccessibilityFrontendTests.DisabledControlsExplainWhyInsteadOfSilentlyLocking 的第四步部分
//           （第三步配對的部分屬情境 6，第 7 批）
//   以上方法都已刪除。AccountTaxonomyFrontendTests.Save_MirrorsTheDownstreamInvalidationScope 也已刪除：第 10 批判定前端部分由
//   「journey 9 messages: saving categories…」取代，後端的失效範圍由 Domain/AuditDependencyPolicyTests 鎖住；原方法註解的
//   第一次失敗收據已補在那個測試上方（第 11 批，2026-10-14）。
//
// 第 8 批刻意不斷言訊息面板的句子：驗證完成、預篩選完成、預篩選尚無法執行、科目分類已更新、操作失敗與尚無法執行的句子，
// 計畫把它們留給第 10 批，第 10 批補在本檔最後。後端原因一律用合成字串，只確認原樣顯示。
// 逐字比對的只有使用者裁定過的文字，出處寫在各測試的註解；其他文字只比對意思。
// 斷言的失敗值一律不是假 DOM 節點（節點只用 === 轉成布林）；預期值一律寫死；action payload 用 deepEqual 精確比對，並確認沒有 `__` 鍵。
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { root, settle, plain, batch9TreeDom, recordingJetApi, timerQueue, fakeMatchMedia, cssEscape, exposeAt, indexMarkup, readWwwroot,
  remapPrescreen, noDunderKeys } = require('./support/frontend-fixtures.cjs');

const VALIDATE_INDEX = 3;

async function flush(times = 4) { for (let i = 0; i < times; i++) await settle(); }

// 第四步的完整畫面：中欄、左側目錄與右側資料預覽都由正式程式畫出。
// mapped：GL 與 TB 是否已確認配對（第四步按鈕是否可以按）；validation：Store 裡已有的驗證結果（null 表示從未驗證）。
function step4Fixture({ mapped = true, validation = null, taxonomy = null } = {}) {
  const dom = batch9TreeDom({ bubbles: true, serialization: 'browser', rootAttributes: { 'data-jet-root': '' } });
  const requests = [], timers = timerQueue(), media = fakeMatchMedia(false);
  const window = { setTimeout: timers.setTimeout, clearTimeout: timers.clearTimeout, setInterval: () => 0, clearInterval() {},
    addEventListener() {}, removeEventListener() {}, matchMedia: media.matchMedia, innerWidth: 1600, CSS: { escape: cssEscape },
    JetFocus: { defer() {} }, JetApi: recordingJetApi(requests),
    // vm 沒有版面；app.js 的統計卡字級調整與捲動定位會讀計算後樣式，這裡回一份沒有格線、一般定位的樣式。
    getComputedStyle: () => ({ gridTemplateColumns: 'none', position: 'static', display: 'block' }) };
  const context = vm.createContext({ window, document: dom.document, setTimeout: timers.setTimeout, clearTimeout: timers.clearTimeout });
  dom.document.body = dom.element('body');
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js', 'filter-vouchers.js',
    'steps/create-step.js', 'steps/import-step.js', 'steps/mapping-step.js', 'steps/validate-step.js',
    'account-mapping-editor.js', 'steps/filter-step.js', 'steps/export-step.js', 'data-preview.js'])
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context, { filename: file });
  vm.runInContext(exposeAt(readWwwroot('js/app.js'), "  if (document.readyState === 'loading') {",
    '  window.step4App = { renderStepNav: renderStepNav, renderContent: renderContent, overviewStepFactsHtml: overviewStepFactsHtml };\n'), context, { filename: 'app.js' });
  dom.content.innerHTML = '<nav data-bind="step-nav"></nav><section class="content" data-bind="content"></section>' +
    indexMarkup('<aside class="data-preview"', '</aside>') + '</aside>';
  const store = window.JetStore, ui = window.JetUi, app = window.step4App;
  const middle = dom.content.querySelector('[data-bind="content"]');
  ui.initDataPreview();
  store.setProject({ projectId: 'step4-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31', lastPeriodStart: '2025-12-20' });
  store.setImportResult('gl', { batchId: 'gl-source', columns: ['Doc', 'Amount'], rowCount: 2 });
  store.setImportResult('tb', { batchId: 'tb-source', columns: ['Code', 'Amount'], rowCount: 2 });
  if (mapped) {
    store.setMappingCommitted('gl', { mode: 'signed', mapping: { docNum: 'Doc', amount: 'Amount' } });
    store.setMappingCommitted('tb', { mode: 'direct', mapping: { accNum: 'Code', amount: 'Amount' } });
  }
  if (taxonomy) store.setTaxonomy(taxonomy);
  store.setLastRun('validate', validation);
  store.setView('workflow');
  // 和 app.js 的 render 一樣，每次 Store 通知都呼叫正式的 renderStepNav 與 renderContent，由它們自己依 contentVersion 決定要不要重建。
  // renderContent 每次重建都換一個新的步驟容器，步驟腳本在綁定途中觸發的重繪不會讓同一批按鈕被綁兩次。
  function redraw() {
    const state = store.getState();
    app.renderStepNav(state, store.STEPS);
    app.renderContent(state, store.STEPS[state.currentStepIndex]);
  }
  store.subscribe(redraw);
  store.setStepIndex(VALIDATE_INDEX); redraw();
  // 目前步驟的內容；其他步驟只畫標題，不在這裡查。
  const body = () => { const node = middle.querySelector('.stepflow-item--current .stepflow-item__body'); assert.ok(node !== null, 'current step body rendered'); return node; };
  const $ = selector => body().querySelector(selector);
  const $$ = selector => body().querySelectorAll(selector);
  function click(selector) {
    const node = $(selector);
    assert.ok(node !== null, selector + ' rendered');
    assert.equal(node.disabled, false, selector + ' enabled');
    node.dispatch('click');
  }
  function type(selector, value) {
    const node = $(selector);
    assert.ok(node !== null, selector + ' rendered');
    node.value = value; node.dispatch('input');
  }
  function pending(action) { return requests.filter(r => r.action === action); }
  function request(action) { const found = pending(action); assert.ok(found.length, action + ' requested'); return found.at(-1); }
  return { window, context, dom, store, ui, app, middle, body, media, requests, timers, $, $$, click, type, pending, request,
    state: () => store.getState(),
    text: selector => { const node = $(selector); return node === null ? null : node.textContent; },
    navItem: index => dom.content.querySelector('[data-bind="step-nav"] [data-step-index="' + index + '"]') };
}

// 開啟第四步並等畫面讀完：右側資料預覽第一次進入案件畫面時自動讀目前的資料表，「在 JET 配對」分頁讀第一頁科目，
// 兩者在真實程式裡很快就回來，這裡回空結果，之後測試才開始操作。
async function openStep4(options) {
  const f = step4Fixture(options);
  await flush();
  for (const r of f.pending('queryAccountMappingPage')) r.resolve({ rows: [], nextCursor: null });
  for (const r of f.pending('queryDataPreview')) r.resolve({ columns: [], rows: [], totalCount: 0 });
  await flush();
  f.requests.length = 0;
  return f;
}

// 修改前失敗證據：20261014-122836046-2bb1fce5afab48aa8044747cce3a0bed，儲存與取消排在分頁前面。
test('section 9 F: category paging precedes cancel and save in the footer', async () => {
  const f = await openStep4();
  assert.deepEqual(f.$('.account-editor__footer').querySelectorAll('button').map(button => button.textContent),
    ['上一頁', '下一頁', '取消變更', '儲存科目分類']);
});

// 修改前失敗：20261014-125539535-e6819c0d2f8940eda66ed524fe3ca0c0，取消全選仍寫「清除勾選」。
test('section 9 F: account selection uses the shared deselection wording', async () => {
  const f = await openStep4();
  assert.equal(f.$('[data-account-clear-selection]').textContent, '全部取消');
});

// 修改前失敗：20261014-124845687-c1409ef46f0e4be099412d4659210ff4，讀取失敗與分類樹尚未共用。
test('section 9 F: account loading failures use the shared notice without changing retry', async () => {
  const f = step4Fixture(); await flush();
  f.request('queryAccountMappingPage').reject(new Error('合成讀取失敗')); await flush();
  const retry = f.$('[data-account-retry]');
  assert.ok(retry.closest('[data-load-failure].form-notice[role="alert"]'));
  assert.equal(retry.textContent, '重新載入');
  const before = f.pending('queryAccountMappingPage').length;
  f.click('[data-account-retry]');
  assert.equal(f.pending('queryAccountMappingPage').length, before + 1);
});

test('section 9 F: account classification uses a collapsible shared tree and a submitted search', async () => {
  const f = await openStep4({ taxonomy: { revision: 1, categories: [
    { categoryId: 'root', label: '合成分類', semanticRole: 'cash', ordinal: 0 },
    { categoryId: 'child', label: '合成子分類', parentCategoryId: 'root', semanticRole: 'cash', ordinal: 1 }] } });
  const tree = f.$('[data-account-category-tree] .category-tree'); assert.ok(tree);
  f.click('[data-category-toggle="root"]');
  assert.equal(f.$('[data-category-node="child"]').hidden, true);
  f.click('[data-category-toggle="root"]');
  const child = f.$('input[data-account-bulk-category][value="child"]');
  assert.equal(child.getAttribute('type'), 'radio'); child.checked = true; child.dispatch('change');
  assert.equal(f.$('input[data-account-bulk-category][value="child"]').checked, true);
  const search = f.$('[data-account-search]'); assert.ok(search.classList.contains('page-search'));
  f.type('[data-account-search] input', '1103');
  assert.equal(f.pending('queryAccountMappingPage').length, 0, 'typing does not query');
  search.dispatch('submit');
  assert.deepEqual(plain(f.request('queryAccountMappingPage').payload), { cursor: null, pageSize: 50, search: '1103' });
});

// 依標題找第四步某一項結果（.rule-item）。
function ruleItem(f, title) {
  return f.$$('.rule-item').find(item => { const node = item.querySelector('.rule-item__title'); return node !== null && node.textContent === title; }) || null;
}

function statCard(f, label) {
  const card = f.$$('.stat-card').find(item => { const node = item.querySelector('.stat-card__label'); return node !== null && node.textContent === label; });
  return card ? card.querySelector('.stat-card__value').textContent : null;
}

// 文件順序：深度優先走過整棵假 DOM，回傳每個元素出現的位置。
function documentOrder(rootNode) {
  const order = [];
  (function walk(node) { for (const child of node.children) { order.push(child); walk(child); } })(rootNode);
  return node => order.indexOf(node);
}

function validationResult({ runId = 'v-step4', eligibility = { isEligible: true, reason: null, warning: null },
  partA = { rowCountMatch: true, amountMatch: true }, diffAccountCount = 0, diffAccounts = [] } = {}) {
  return {
    resultRef: { runId, generatedUtc: '2026-01-01T00:00:00Z' },
    // GL 分錄數 77 故意和「原始 100 減排除 20」的 80 不同，畫面若自己相減就會顯示 80。
    stats: { glRowCount: 77, voucherCount: 12, totalDebit: 1250, totalCredit: 1250, net: 0, periodStart: '2025-01-01', periodEnd: '2025-12-31' },
    populationSummary: { raw: { rowCount: 100 }, effective: { rowCount: 80 }, excluded: { rowCount: 20, byPeriodCount: 15, byPostingStatusCount: 5 } },
    completenessTest: { diffAccountCount, diffAccounts, partA, naReason: null, eligibility },
    docBalanceTest: { unbalancedDocumentCount: 0 },
    infSamplingTest: { sampleSize: 3 },
    nullRecordsTest: { nullDocumentCount: 0, nullAccountCount: 0, nullDescriptionCount: 0, outOfRangeDateCount: 0, nullRows: [] },
    sourceQuality: { findingCount: 2, sampleRows: [{ category: 'nullPostDate', sourceLabel: '合成來源檔.xlsx', sourceRowNumber: 7,
      documentNumber: 'JV-7', accountCode: '1001', description: '合成摘要' }] }
  };
}

// 抽樣清單的檔名故意帶標記，確認報告清單把檔名當文字顯示。
function reportArtifacts(runId) {
  return ['validationReport', 'infReport'].map((kind, i) => ({ artifactId: 'step4-' + kind, kind,
    fileName: kind === 'infReport' ? '合成<img src=y>抽樣.xlsx' : kind + '.xlsx', fileState: 'present', stale: false,
    generatedUtc: '2026-01-01T00:00:00Z', sizeBytes: 2000 + i, sourceRef: { validationRunId: runId } }));
}

// 從畫面按「執行驗證」開始：後端依序回應驗證、兩份報告與科目配對檔；templateOutcome 是科目配對檔的結果。
async function runValidation(f, data, templateOutcome = { resolve: { disposition: 'kept', fileName: '科目配對.xlsx' } }) {
  f.click('[data-action="run-validate"]');
  const run = f.request('validateRun');
  assert.deepEqual(plain(run.payload), {});
  run.resolve(data); await flush();
  const reports = f.request('exportValidationArtifacts');
  assert.deepEqual(plain(reports.payload), { runId: data.resultRef.runId });
  reports.resolve({ artifacts: reportArtifacts(data.resultRef.runId), reportArtifacts: reportArtifacts(data.resultRef.runId) }); await flush();
  const template = f.request('exportAccountMappingTemplate');
  assert.deepEqual(plain(template.payload), { runId: data.resultRef.runId, onlyIfMissing: true });
  if (templateOutcome === 'pending') return template;
  if (templateOutcome.reject) template.reject(templateOutcome.reject); else template.resolve(templateOutcome.resolve);
  await flush();
  return template;
}

// ---- 情境 7：執行資料驗證並解讀結果 ----

test('journey 7: running validation shows the backend GL entry count, no population summary, and the three test descriptions in the ruled words', async () => {
  const f = await openStep4();
  // 從未驗證時按鈕寫「執行驗證」，各項都還沒有結果。
  assert.equal(f.text('[data-action="run-validate"]'), '執行驗證');
  assert.equal(statCard(f, 'GL 分錄數'), '—');
  await runValidation(f, validationResult());
  // GL 分錄數直接取後端的 stats.glRowCount（77），不由原始筆數減排除筆數重算（那會是 80）。
  assert.equal(statCard(f, 'GL 分錄數'), '77');
  // 使用者 2026-10-08 第 11 則裁定整塊移除母體摘要（原 C# 註解；首次舊斷言失敗 c00059a502f54ee2bc22cce3a932f949）。
  assert.ok(f.$('[data-bind="population-summary"]') === null, 'no population summary block');
  assert.ok(f.$('.population-summary') === null, 'no population summary class');
  // 第 12 則只移除捷徑：第四步沒有「檢視 GL(未納入測試)」的按鈕，資料集仍在右側資料預覽（下一個測試）。
  assert.ok(f.$('[data-action="preview-excluded-entries"]') === null, 'no excluded-entries shortcut');
  assert.equal(f.body().textContent.includes('檢視 GL(未納入測試)'), false);

  // 三項測試的說明逐字比對。出處（docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md）：
  //   完整性測試：V2 的「處理時機」使用者選「照建議改寫」（2026-10-05），第 7245 行起「6. V2 與 W1」的評估結果寫出卡片文字
  //     「先確認 JET 存下的分錄筆數與借貸合計，和確認欄位配對時算出的一樣。這一步不和來源檔自己的合計比對。
  //       再比對各科目的總帳本期借貸淨額與試算表本期變動金額。金額不符代表總帳母體可能缺漏。」
  //     最後一句是使用者原句（D22 引用的使用者原文「金額不符代表總帳母體可能缺漏」）。
  //   借貸不平測試：W1 裁定照原件用「、」；原件是 D22 的使用者原文「借貸不平代表傳票編號、金額欄位可能有誤」。
  //   資料可靠性測試：U23 與 L42 依 D22 使用者原句「需先確認RDE的可靠性後，方執行高風險篩選條件」還原說明與先後順序，
  //     只寫成說明、不新增擋下；畫面的寫法是「需先確認攸關資料元素（RDE）的可靠性後，再執行高風險篩選條件。」
  //   原 C# 測試的第一次失敗收據：20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022。
  const desc = title => { const item = ruleItem(f, title); assert.ok(item !== null, title + ' listed'); return item.querySelector('.rule-item__desc').textContent; };
  assert.equal(desc('完整性測試'),
    '先確認 JET 存下的分錄筆數與借貸合計，和確認欄位配對時算出的一樣。這一步不和來源檔自己的合計比對。' +
    '再比對各科目的總帳本期借貸淨額與試算表本期變動金額。金額不符代表總帳母體可能缺漏。');
  assert.equal(desc('借貸不平測試'), '依傳票號碼彙總借方與貸方金額，列出不相等的傳票。借貸不平代表傳票編號、金額欄位可能有誤。');
  assert.equal(desc('資料可靠性測試'),
    '抽出分錄樣本，供核對傳票附件，確認摘要、日期等篩選欄位是否正確。需先確認攸關資料元素（RDE）的可靠性後，再執行高風險篩選條件。');
  // 來源品質這一項的名稱是「總帳日期空白」：D21 使用者原文「第三個"過帳日"空白，可能要改成"總帳日期"空白，配合前面GL欄位配對的文字」。
  // 說明只比對意思：寫出這些分錄沒有納入本次測試。
  const blank = desc('總帳日期空白');
  assert.match(blank, /未納入/);
  // 9/22 移除的報表沿革不再出現在說明裡。
  assert.equal(f.body().textContent.includes('ValidationReport 與 INF 報表中稱為'), false);
  assert.equal(f.body().textContent.includes('INF Report 只會發布'), false);
  // 驗證後按鈕改成重新執行；報告清單列出兩份報告（INF 的名稱由報告清單共用的對照顯示，只比對意思：含「抽樣」）。
  assert.equal(f.text('[data-action="run-validate"]'), '重新執行驗證');
  const labels = f.$$('.report-output .report-artifact__label').map(node => node.textContent);
  assert.equal(labels.length, 2);
  assert.ok(labels.some(label => /抽樣/.test(label)), 'the INF report is named as a sampling list: ' + labels.join('、'));
  assert.ok(f.$$('.report-output .report-artifact__name').some(node => node.textContent === '合成<img src=y>抽樣.xlsx'), 'file name shown as text');
  assert.equal(f.body().querySelectorAll('img').length, 0, 'file name never becomes an element');
  // 用途已由資料可靠性測試的說明交代，報告區不再重複同一句。
  assert.equal(f.text('.report-output').includes('資料可靠性抽樣清單供核對傳票附件'), false);
});

test('journey 7: when the stored entries disagree with the totals counted at mapping, the completeness status says so instead of naming account differences', async () => {
  // 狀態文字是 V2 評估結果寫的「存下的分錄數字不一致」，不在使用者逐字裁定的範圍，只比對意思。
  const f = await openStep4({ validation: validationResult({ eligibility: { isEligible: false, reason: '合成原因：存下的分錄和確認配對時算出的不一致' },
    partA: { rowCountMatch: false, amountMatch: true } }) });
  const status = ruleItem(f, '完整性測試').querySelector('.rule-status').textContent;
  assert.match(status, /存下的分錄/); assert.match(status, /不一致/);
  assert.doesNotMatch(status, /科目/);
});

test('journey 7: blank GL dates list their source file and row, and loading more asks the source quality page', async () => {
  const f = await openStep4({ validation: validationResult() });
  const item = ruleItem(f, '總帳日期空白');
  assert.ok(item !== null, 'source quality item listed');
  item.querySelector('[data-action="toggle-detail"]').dispatch('click');
  const panel = ruleItem(f, '總帳日期空白').querySelector('.rule-detail');
  assert.equal(panel.hidden, false);
  const heads = panel.querySelectorAll('thead th').map(th => th.textContent);
  assert.ok(heads.includes('來源檔') && heads.includes('來源列號'), 'source file and row columns: ' + heads.join('、'));
  const firstRow = panel.querySelectorAll('tbody tr')[0].querySelectorAll('td').map(td => td.textContent);
  assert.ok(firstRow.includes('合成來源檔.xlsx') && firstRow.includes('7'), 'first row names the source file and row: ' + firstRow.join('、'));
  assert.ok(firstRow.includes('總帳日期空白'), 'row category label: ' + firstRow.join('、'));

  const before = f.requests.length;
  panel.querySelector('[data-load-more="sourceQuality"]').dispatch('click');
  const added = f.requests.slice(before);
  assert.deepEqual(added.map(r => r.action), ['querySourceQualityPage'], 'loading more asks only the source quality page');
  // 2026-09-07 工作包 G：來源品質明細表加上排序與搜尋，呼叫多帶 sort 與 search（取代 PopulationAndDynamicColumnFrontendTests.SourceQualityPage_…，
  // 原方法註解的第一次失敗收據 20260907-080325828）。
  assert.deepEqual(plain(added[0].payload), { cursor: null, pageSize: 200, sort: null, search: null });
  added[0].resolve({ rows: [{ category: 'nullPostDate', sourceLabel: '第二個來源.csv', sourceRowNumber: 12, documentNumber: 'JV-12', accountCode: '2001', description: '合成摘要二' }], nextCursor: null });
  await flush();
  const rows = ruleItem(f, '總帳日期空白').querySelectorAll('tbody tr').map(tr => tr.querySelectorAll('td').map(td => td.textContent));
  const loaded = rows.find(row => row.includes('第二個來源.csv'));
  assert.ok(loaded, 'loaded row is listed');
  assert.ok(loaded.includes('12') && loaded.includes('總帳日期空白'), 'loaded row names its source row and category: ' + loaded.join('、'));
  assert.equal(f.pending('queryNullRecordsPage').length, 0, 'blank GL dates never use the null-record pages');
});

test('journey 7: with no blank GL dates the item reads as nothing found and has no detail to open, whatever the null-record counts say', async () => {
  // 總帳日期空白只看後端的 sourceQuality；空值紀錄沒有這個計數（後端 BlankPostDateDetectionTests 鎖住 nullRecordsTest 不帶 nullPostDateCount）。
  const data = validationResult();
  data.sourceQuality = { findingCount: 0, sampleRows: [] };
  const f = await openStep4({ validation: data });
  const item = ruleItem(f, '總帳日期空白');
  assert.ok(item !== null, 'source quality item listed');
  // 狀態只比對意思：沒有發現。
  assert.match(item.querySelector('.rule-status').textContent, /未發現/);
  assert.ok(item.querySelector('[data-action="toggle-detail"]') === null, 'nothing to expand');
  assert.ok(item.querySelector('.rule-detail') === null, 'no detail table');
});

test('journey 7: entries left out of the test are their own preview dataset and their reasons read as dates or posting status, not keys', async () => {
  const f = await openStep4({ validation: validationResult() });
  const aside = f.dom.content.querySelector('[data-bind="data-preview"]');
  aside.querySelector('[data-more-toggle]').dispatch('click');
  const menuItem = aside.querySelector('[data-ds="glExcluded"]');
  assert.ok(menuItem !== null, 'GL(未納入測試) offered under other data');
  // 名稱逐字：使用者 2026-10-08（docs/history/specs/2026-10-06-kct-gl-requirements-plan.md 第 3371 行）「`未納入測試的分錄` 改成 `GL(未納入測試)`」，
  // 同日選「全系統統一 (Recommended)」。
  assert.equal(menuItem.textContent, 'GL(未納入測試)');
  const before = f.requests.length;
  menuItem.dispatch('click');
  const added = f.requests.slice(before);
  assert.deepEqual(added.map(r => r.action), ['queryDataPreview']);
  assert.deepEqual(plain(added[0].payload), { dataset: 'glExcluded' });
  added[0].resolve({ columns: ['documentNumber', 'exclusionReason'], rows: [['JV-21', 'period'], ['JV-22', 'postingStatus']], totalCount: 2,
    stats: { excludedByPeriodCount: 15, excludedByPostingStatusCount: 5 } });
  await flush();
  const body = aside.querySelector('[data-bind="data-preview-body"]');
  const reasons = body.querySelectorAll('tbody tr').map(tr => tr.querySelectorAll('td')[1].textContent);
  // 原因只比對意思（找不到使用者逐字裁定）：period 讀成日期不符、postingStatus 讀成過帳狀態不符，不顯示 wire 值。
  assert.match(reasons[0], /日期/); assert.doesNotMatch(reasons[0], /period/);
  assert.match(reasons[1], /過帳狀態/); assert.doesNotMatch(reasons[1], /postingStatus/);
  // 註記照後端的兩個計數寫出筆數，不在畫面相加或相減。
  const note = aside.querySelector('.data-preview__note').textContent;
  assert.match(note, /日期[^｜]*15/); assert.match(note, /過帳狀態[^｜]*5/);
});

test('journey 7: the sampled entries table takes its columns from the backend and reads RDE amounts from customValues', async () => {
  const f = await openStep4({ validation: validationResult() });
  ruleItem(f, '資料可靠性測試').querySelector('[data-action="toggle-detail"]').dispatch('click');
  const page = f.request('queryInfSamplePage');
  assert.deepEqual(plain(page.payload), { cursor: null, pageSize: 200, sort: null, search: null });
  page.resolve({ columns: [
    { key: 'documentNumber', label: '傳票號碼', valueType: 'text', sortable: true },
    { key: 'rde.amount1', label: '合成攸關金額', valueType: 'money', isCustom: true }
  ], rows: [{ documentNumber: 'JV-31', 'rde.amount1': 999, customValues: { 'rde.amount1': 1234.5 } }], nextCursor: null });
  await flush();
  const panel = ruleItem(f, '資料可靠性測試').querySelector('.rule-detail');
  assert.deepEqual(panel.querySelectorAll('thead th').map(th => th.textContent), ['傳票號碼', '合成攸關金額']);
  // 攸關資料元素欄的值只從 customValues 取；同名的頂層鍵 999 是干擾值，不得出現。
  assert.deepEqual(panel.querySelectorAll('tbody tr')[0].querySelectorAll('td').map(td => td.textContent), ['JV-31', '1,234.50']);
});

// ---- 情境 8：完整性不符時能做什麼，以及執行預篩選 ----

test('journey 8: before any validation the prescreen card, the fifth step and the footer ask to run validation first; an old summary asks to run it again', async () => {
  // W7（docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md 第 2903 行）使用者裁定「W7 從未驗證時寫「請重新執行資料驗證」：要修」；
  // 修正後的句子沒有使用者逐字裁定，所以只比對「先執行」與「重新執行」的差別。
  const never = await openStep4();
  assert.equal(never.$('[data-action="run-prescreen"]').disabled, true);
  const neverGate = never.text('.rule-card--prescreen .rule-card__gate');
  assert.ok(neverGate !== null, 'prescreen card explains why it is locked');
  assert.match(neverGate, /先執行資料驗證/); assert.doesNotMatch(neverGate, /重新/);
  const neverNav = never.navItem(4);
  assert.equal(neverNav.disabled, true);
  assert.match(neverNav.getAttribute('title'), /執行資料驗證/); assert.doesNotMatch(neverNav.getAttribute('title'), /重新/);
  assert.match(never.text('.step-footer__hint'), /執行資料驗證/); assert.doesNotMatch(never.text('.step-footer__hint'), /重新/);
  assert.equal(never.$('[data-action="step-next"]').disabled, true);

  // 舊的驗證結果沒有後端的判定欄位：不能放行，請審計員重新執行。
  const old = validationResult(); delete old.completenessTest.eligibility;
  const outdated = await openStep4({ validation: old });
  assert.equal(outdated.$('[data-action="run-prescreen"]').disabled, true);
  assert.match(outdated.text('.rule-card--prescreen .rule-card__gate'), /重新執行資料驗證/);
  assert.match(outdated.navItem(4).getAttribute('title'), /重新執行資料驗證/);
  assert.match(outdated.text('.step-footer__hint'), /重新執行資料驗證/);
  assert.equal(outdated.$('[data-action="step-next"]').disabled, true);
});

test('journey 8: when the backend says the data cannot continue, matching control totals do not unlock the fifth step or the prescreen', async () => {
  const reason = '合成原因：後端判定這份資料不能繼續';
  const f = await openStep4({ validation: validationResult({ eligibility: { isEligible: false, reason } }) });
  // 兩個控制總數都相符、沒有差異科目，前端也不得自己放行。
  assert.deepEqual(plain(f.ui.stepGate(f.state(), 4).missing), [reason]);
  assert.equal(f.$('[data-action="step-next"]').disabled, true);
  assert.equal(f.text('.step-footer__hint').includes(reason), true);
  assert.equal(f.navItem(4).disabled, true);
  assert.equal(f.navItem(4).getAttribute('title').includes(reason), true);
  assert.equal(f.$('[data-action="run-prescreen"]').disabled, true);
  assert.equal(f.text('.rule-card--prescreen .rule-card__gate'), reason);
  const completeness = ruleItem(f, '完整性測試').querySelector('.rule-status').textContent;
  assert.notEqual(completeness, '通過');
  // 按鈕狀態沒更新時硬按：不送 prescreenRun（提示訊息的句子留給第 10 批）。
  f.$('[data-action="run-prescreen"]').dispatch('click');
  await flush();
  assert.equal(f.pending('prescreenRun').length, 0);
});

test('journey 8: a backend reason with markup is shown as text in the footer and never becomes an element', async () => {
  const reason = '<img src=x onerror=alert(1)>';
  const f = await openStep4({ validation: validationResult({ eligibility: { isEligible: false, reason } }) });
  const footer = f.$('.step-footer__hint');
  assert.ok(footer !== null, 'footer explains the lock');
  assert.ok(footer.innerHTML.includes('&lt;img'), 'footer escapes the reason');
  assert.equal(footer.textContent.includes(reason), true);
  assert.equal(f.dom.content.querySelectorAll('img').length, 0, 'no img element anywhere on the page');
});

test('journey 8: a warning still allows the prescreen and the fifth step without a prescreen result, and the difference table keeps the warning', async () => {
  const warning = '合成提醒：GL 與 TB 有 1 個科目差異，可說明原因並繼續。';
  const f = await openStep4({ validation: validationResult({ eligibility: { isEligible: true, reason: null, warning }, diffAccountCount: 1,
    diffAccounts: [{ accountCode: '1101', accountName: '合成科目', tbAmount: 100, glAmount: 90, diff: 10 }] }) });
  assert.equal(f.state().lastRuns.prescreen, null);
  assert.equal(f.ui.stepGate(f.state(), 4).ok, true);
  assert.ok(f.$('.step-footer__hint') === null, 'no lock hint in the footer');
  assert.equal(f.$('[data-action="run-prescreen"]').disabled, false);
  assert.ok(f.$('.rule-card--prescreen .rule-card__gate') === null, 'no gate on the prescreen card');
  // 完整性仍顯示差異：狀態不是「通過」，展開後差異表前面寫出後端提醒，並說明差額的完整數字在驗證報告（只比對意思）。
  const item = ruleItem(f, '完整性測試');
  assert.notEqual(item.querySelector('.rule-status').textContent, '通過');
  item.querySelector('[data-action="toggle-detail"]').dispatch('click');
  const prefixes = ruleItem(f, '完整性測試').querySelectorAll('.rule-detail__prefix').map(node => node.textContent);
  const differencePrefix = prefixes.find(text => text.includes(warning));
  assert.ok(differencePrefix, 'difference table repeats the backend warning');
  assert.match(differencePrefix, /驗證報告/);
  // 流程總覽也說明可以做預篩選或進階條件篩選，不要求先做預篩選（只比對意思）。
  const facts = f.dom.element('div'); facts.innerHTML = f.app.overviewStepFactsHtml(f.state(), VALIDATE_INDEX);
  assert.match(facts.textContent, /進階條件篩選/);
  // 下一步可以按，真的會到第五步。
  f.click('[data-action="step-next"]');
  assert.equal(f.state().currentStepIndex, 4);
});

test('journey 8: running the prescreen lists the two aggregates first, keeps the filter results folded and leaves the report optional', async () => {
  const f = await openStep4({ validation: validationResult() });
  f.click('[data-action="run-prescreen"]');
  const run = f.request('prescreenRun');
  assert.deepEqual(plain(run.payload), {});
  // 執行預篩選這個操作不包含產生報告（只比對意思：操作名稱沒有「報告」）。
  assert.doesNotMatch(f.state().busyLabel || '', /報告/);
  run.resolve(remapPrescreen('p-step4'));
  await flush();
  assert.equal(f.state().lastRuns.prescreen.resultRef.runId, 'p-step4');
  const card = f.$('.rule-card--prescreen');
  // 卡片標題是預篩選，主要區由這個標題命名（只比對意思）。
  const heading = card.querySelector('#prescreen-primary-title');
  assert.ok(heading !== null, 'prescreen card heading rendered');
  assert.match(heading.textContent, /預篩選/);
  assert.equal(card.querySelector('.prescreen-primary').getAttribute('aria-labelledby'), 'prescreen-primary-title');
  const at = documentOrder(card);
  const primary = card.querySelector('.prescreen-primary'), signals = card.querySelector('details.prescreen-signals'), report = card.querySelector('.report-output');
  assert.ok(primary !== null && signals !== null && report !== null, 'aggregates, filter results and report sections rendered');
  assert.ok(at(primary) < at(signals), 'aggregates come before the filter results');
  assert.ok(at(signals) < at(report), 'the optional report comes after the filter results');
  // 兩條彙總是編製者彙總與較少使用的科目（只比對意思）。
  const aggregates = primary.querySelectorAll('.rule-item__title').map(node => node.textContent);
  assert.equal(aggregates.length, 2);
  assert.match(aggregates[0], /編製者/); assert.match(aggregates[1], /較少使用/);
  assert.equal(signals.hasAttribute('open'), false, 'filter results start folded');
  assert.match(signals.querySelector('summary').textContent, /篩選結果/);
  assert.equal(f.pending('exportPrescreenReport').length, 0, 'no report is produced automatically');
  // 報告區說明報告還沒產生（只比對意思），並提供產生報告的按鈕。
  assert.match(report.textContent, /預篩選報告/);
  assert.match(report.textContent, /尚未產生/);
  assert.ok(report.querySelector('[data-action="export-prescreen-report"]') !== null, 'report can be produced on request');
});

// ---- 情境 9：科目分類與科目配對 ----

const TAXONOMY_WITH_BANK = { revision: 2, categories: [
  { categoryId: 'builtin.cash', label: 'Cash', ordinal: 0, semanticRole: 'cash', isBuiltIn: true },
  { categoryId: 'bank', label: '銀行存款', ordinal: 1, semanticRole: 'cash', parentCategoryId: null }
] };

test('journey 9: built-in categories cannot be removed or repurposed, while a category used only by a saved scenario can be removed and saved', async () => {
  const f = await openStep4({ validation: validationResult(), taxonomy: TAXONOMY_WITH_BANK });
  const scenarios = [{ name: '銀行情境', groups: [{ join: 'AND', matchScope: 'row', rules: [{ type: 'accountCategory', categoryIds: ['bank'] }] }] }];
  f.store.setSavedScenarios(scenarios);
  f.click('[data-action="toggle-taxonomy"]');
  // 2026-10-07：儲存分類會清除情境，所以被情境引用不能擋住移除（原 C# 註解；首次失敗 a8ad54d4ecdb4e859b12b3d4a7ee03df、16649cbb7e60449ca9bbdd2e532e22bc）。
  assert.ok(f.$('[data-taxonomy-remove="builtin.cash"]') === null, 'built-in has no remove button');
  const builtInRole = f.$('[data-taxonomy-role="builtin.cash"]');
  assert.equal(builtInRole.disabled, true);
  assert.equal(builtInRole.getAttribute('aria-disabled'), 'true');
  const customRole = f.$('[data-taxonomy-role="bank"]');
  assert.equal(customRole.disabled, false);
  assert.equal(customRole.getAttribute('aria-disabled'), null);
  assert.equal(f.$('[data-taxonomy-remove="bank"]').disabled, false);
  assert.equal(f.body().textContent.includes('不可刪除'), false);
  // 停用狀態沒更新時硬改內建分類的用途：草稿不採用，儲存時仍是 cash。
  builtInRole.value = 'revenue'; builtInRole.dispatch('change');
  f.click('[data-taxonomy-remove="bank"]');
  assert.ok(f.$('[data-taxonomy-label="bank"]') === null, 'removed category leaves the list');
  assert.equal(f.state().filter.savedScenarios, scenarios, 'scenarios stay until the save succeeds');
  f.click('[data-action="save-taxonomy"]');
  const save = f.request('accountTaxonomySave');
  assert.deepEqual(plain(save.payload), { revision: 2, categories: [
    { label: 'Cash', ordinal: 0, semanticRole: 'cash', parentCategoryId: null, categoryId: 'builtin.cash' }
  ] });
  assert.ok(noDunderKeys(save.payload));
});

test('journey 9: new and renamed categories save with the current revision, list order and draft references; blank or duplicate names block saving and say why; a failed save can be retried', async () => {
  const f = await openStep4({ validation: validationResult() });
  f.click('[data-action="toggle-taxonomy"]');
  // 還沒有案件自己的分類設定：畫面列出五個內建分類。
  assert.deepEqual(f.$$('[data-taxonomy-label]').map(node => node.getAttribute('data-taxonomy-label')),
    ['builtin.revenue', 'builtin.receivables', 'builtin.cash', 'builtin.receipt_in_advance', 'builtin.others']);
  assert.equal(f.$('[data-action="save-taxonomy"]').disabled, true, 'nothing to save before a change');
  f.click('[data-action="add-taxonomy-category"]');
  // 新列名稱空白：儲存停用，按鈕上方寫出缺什麼（只比對意思，計畫第 4 節 A15；前綴查不到逐字裁定）。
  const problems = () => { const node = f.$('[data-bind="taxonomy-problems"]'); return node.hidden ? '' : node.textContent; };
  assert.equal(f.$('[data-action="save-taxonomy"]').disabled, true);
  assert.match(problems(), /名稱/);
  f.type('[data-taxonomy-label="draft-1"]', '新分類');
  assert.equal(problems(), '');
  assert.equal(f.$('[data-action="save-taxonomy"]').disabled, false);
  // 改名成和另一個分類重複：儲存停用並說明重複。
  f.type('[data-taxonomy-label="builtin.others"]', 'cash');
  assert.equal(f.$('[data-action="save-taxonomy"]').disabled, true);
  assert.match(problems(), /重複/);
  f.type('[data-taxonomy-label="builtin.others"]', '其他');
  assert.equal(problems(), '');
  f.click('[data-action="save-taxonomy"]');
  // 沒有分類設定時版本號是 1；順序照清單；新列只帶草稿代號 draft-1，正式識別碼由後端在同一次儲存時配置，前端不自己產生。
  const expected = { revision: 1, categories: [
    { label: 'Revenue', ordinal: 0, semanticRole: 'revenue', parentCategoryId: null, categoryId: 'builtin.revenue' },
    { label: 'Receivables', ordinal: 1, semanticRole: 'receivables', parentCategoryId: null, categoryId: 'builtin.receivables' },
    { label: 'Cash', ordinal: 2, semanticRole: 'cash', parentCategoryId: null, categoryId: 'builtin.cash' },
    { label: 'Receipt in advance', ordinal: 3, semanticRole: 'receipt_in_advance', parentCategoryId: null, categoryId: 'builtin.receipt_in_advance' },
    { label: '其他', ordinal: 4, semanticRole: 'others', parentCategoryId: null, categoryId: 'builtin.others' },
    { label: '新分類', ordinal: 5, semanticRole: 'others', parentCategoryId: null, categoryId: 'draft-1' }
  ] };
  const first = f.request('accountTaxonomySave');
  assert.deepEqual(plain(first.payload), expected);
  assert.ok(noDunderKeys(first.payload));
  first.reject({ code: 'synthetic_failure', message: '合成：儲存失敗' });
  await flush();
  // 失敗後草稿還在，可以直接再按儲存，送出同一份內容。
  assert.equal(f.$('[data-taxonomy-label="draft-1"]').value, '新分類');
  assert.equal(f.$('[data-bind="taxonomy-dirty-notice"]').hidden, false);
  f.click('[data-action="save-taxonomy"]');
  const second = f.request('accountTaxonomySave');
  assert.ok(second !== first, 'retry sends a new request');
  assert.deepEqual(plain(second.payload), expected);
  const saved = expected.categories.map(item => item.categoryId === 'draft-1' ? { ...item, categoryId: 'cat-new' } : item);
  second.resolve({ revision: 2, categories: saved, invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: true, filter: false }, reportArtifacts: [] });
  await flush();
  assert.equal(f.state().taxonomy.revision, 2);
  assert.equal(f.$('[data-taxonomy-label="cat-new"]').value, '新分類');
  assert.equal(f.$('[data-bind="taxonomy-dirty-notice"]').hidden, true);
  assert.equal(f.$('[data-action="save-taxonomy"]').disabled, true);
});

test('journey 9: a failed account mapping file shows its own reason on the JET tab, and retrying moves focus to the Excel tab prepare button', async () => {
  const f = await openStep4();
  assert.equal(f.$('#account-method-jet').getAttribute('aria-selected'), 'true');
  const template = await runValidation(f, validationResult(), 'pending');
  // 準備中：「在 JET 配對」分頁也看得到狀態。
  assert.ok(f.$('[data-account-template-status="pending"]') !== null, 'pending status on the JET tab');
  template.reject({ code: 'synthetic_failure', message: '合成：科目配對檔被其他程式開啟' });
  await flush();
  const failed = f.$('[data-account-template-status="failed"]');
  assert.ok(failed !== null, 'failure status on the JET tab');
  assert.equal(failed.textContent.includes('合成：科目配對檔被其他程式開啟'), true);
  // 原因另存一份：JET 分頁只寫後端原因與到 Excel 分頁重試的按鈕，不沿用 Excel 分頁「此區按鈕」的說法（只比對意思）。
  assert.equal(failed.textContent.includes('此區'), false);
  assert.ok(failed.querySelector('[data-action="retry-account-template-in-excel"]') !== null, 'retry button sits in the JET tab status');
  // Excel 分頁用 data-validation-output="template" 讀狀態，JET 分頁不重用。
  assert.ok(f.$('#account-method-panel [data-validation-output]') === null, 'JET tab does not reuse the Excel status hook');
  f.click('[data-action="retry-account-template-in-excel"]');
  assert.equal(f.$('#account-method-excel').getAttribute('aria-selected'), 'true');
  const active = f.dom.document.activeElement;
  assert.ok(active !== null && active === f.$('[data-action="ensure-account-mapping-template"]'), 'focus lands on the prepare button');
  assert.ok(f.$('[data-validation-output="template"]') !== null, 'Excel tab keeps its full status');
  f.click('[data-action="ensure-account-mapping-template"]');
  const retry = f.request('exportAccountMappingTemplate');
  assert.ok(retry !== template, 'retry sends a new request');
  assert.deepEqual(plain(retry.payload), { runId: 'v-step4', onlyIfMissing: true });
  retry.resolve({ disposition: 'created', fileName: '科目配對.xlsx', rowCount: 3 });
  await flush();
  f.click('[data-action="select-account-jet"]');
  // 準備好以後，「在 JET 配對」分頁不再顯示狀態。
  assert.ok(f.$('[data-account-template-status]') === null, 'no status on the JET tab after success');
});

// R3-32（使用者 2026-10-14 第三輪勾選頁，照建議）：準備科目配對檔失敗而沒有原因時，「在 JET 配對」分頁只請審計員再試一次，逐字比對。
// 取代 MappingNamesAndTemplateStatusFrontendTests.AccountTemplateStatus_… 裡 R3-32 的斷言；那段斷言的第一次失敗收據
// 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e。有原因時接冒號寫原因，由上一個測試確認。
test('journey 9: a failed account mapping file without a reason asks to try again on the JET tab', async () => {
  const f = await openStep4();
  const template = await runValidation(f, validationResult(), 'pending');
  template.reject({ code: 'synthetic_failure', message: '' });
  await flush();
  const failed = f.$('[data-account-template-status="failed"]');
  assert.ok(failed !== null, 'failure status on the JET tab');
  const retry = failed.querySelector('[data-action="retry-account-template-in-excel"]');
  assert.ok(retry !== null, 'retry button sits in the JET tab status');
  assert.equal(failed.textContent.replace(retry.textContent, '').trim(), '科目配對檔尚未準備好，請再試一次。');
});

test('journey 9: the Excel tab preview opens the account mapping dataset in the data preview', async () => {
  const f = await openStep4({ validation: validationResult() });
  f.store.setAccountMappingState({ batchId: 'am-1', rowCount: 3, fileName: '科目配對.xlsx' });
  f.click('[data-action="toggle-account-excel"]');
  const before = f.requests.length;
  f.click('[data-action="preview-account-mapping"]');
  const added = f.requests.slice(before);
  assert.deepEqual(added.map(r => r.action), ['queryDataPreview']);
  assert.deepEqual(plain(added[0].payload), { dataset: 'accountMappings' });
  const tab = f.dom.content.querySelector('[data-bind="data-preview"] [data-ds="accountMappings"]');
  assert.equal(tab.getAttribute('aria-selected'), 'true');
});

// ---- 第 10 批補：訊息面板的句子（收尾四項第一項第 10 批，2026-10-14） ----
// 第 8 批刻意不斷言訊息面板，留給第 10 批在文字確認並改進產品後補上。逐字比對的句子都寫出 final-wording.json 或
// final-wording-r3.json 的編號；後端原因一律用合成字串。這幾個測試另取代：
//   BlankPostDateFrontendTests.ValidationUi_ReportsSourceQualityCountSeparatelyFromNullRecordTotal
//   CompletenessEligibilityFrontendTests.UiRun_MapsCompletenessPrerequisiteFailure_AndPreservesBackendDetail
//   AccountTaxonomyFrontendTests.Save_MirrorsTheDownstreamInvalidationScope 的前端部分（後端失效範圍由 Domain/AuditDependencyPolicyTests 鎖住）
const ELAPSED = /耗時(?: \d+\.\d 秒|不到 0\.1 秒|\d+ 分 \d+ 秒)/.source;
const messageList = f => plain(f.state().messages.map(m => ({ text: m.text, level: m.level })));

test('journey 7 messages: a finished validation leaves one short completion line, and blank GL dates and blank fields keep their own counts in their own items', async () => {
  const f = await openStep4();
  const data = validationResult();
  data.nullRecordsTest = { nullDocumentCount: 3, nullAccountCount: 4, nullDescriptionCount: 0, outOfRangeDateCount: 0, nullRows: [] };
  data.sourceQuality.findingCount = 2;
  await runValidation(f, data);
  const messages = messageList(f);
  // 使用者 2026-10-13 勾選頁確認 35（final-wording.json）：完成句只留「資料驗證完成，耗時 X。」，各項筆數寫在下方各項結果；
  // 耗時的寫法是同日「保留，改成秒並合併」的裁定，不到 0.1 秒的寫法是 2026-10-14 第三輪 R3-26。
  const done = messages.filter(m => m.text.startsWith('資料驗證完成'));
  assert.equal(done.length, 1, 'one completion line');
  assert.match(done[0].text, new RegExp('^資料驗證完成，' + ELAPSED + '。$'));
  assert.equal(done[0].level, 'info');
  // 使用者 2026-10-13 勾選頁確認 31（final-wording.json，維持原句）。
  assert.ok(messages.some(m => m.text === '已在案件資料夾產生驗證階段的 2 份報告。' && m.level === 'info'), 'the report line');
  // 舊的合計說法不再出現，也不逐欄串成「欄位異常」。
  assert.ok(!messages.some(m => /異常項次合計|空值項目合計|獨立計數|欄位異常/.test(m.text)), 'no combined count wording');
  // 總帳日期空白的 2 筆只在自己的一項，不和空白傳票號碼 3 筆、空白科目編號 4 筆相加（只比對意思）。
  const status = title => { const item = ruleItem(f, title); assert.ok(item !== null, title + ' item rendered'); return item.querySelector('.rule-status').textContent; };
  assert.match(status('總帳日期空白'), /^2\D/);
  assert.match(status('空白傳票號碼'), /^3\D/);
  assert.match(status('空白科目編號'), /^4\D/);
  assert.ok(!f.$$('.rule-status').some(node => /^(?:5|7|9)\D/.test(node.textContent)), 'no item adds the counts together');
  // 空白傳票號碼的說明：使用者 2026-10-14 第三輪勾選頁確認 R3-44（final-wording-r3.json）。
  assert.equal(ruleItem(f, '空白傳票號碼').querySelector('.rule-item__desc').textContent,
    '傳票號碼空白的分錄。與空白科目編號分開列示；傳票號碼與科目編號都空白的分錄，兩份明細都會列出。');
});

test('journey 8 messages: the prescreen says it cannot run yet with the reason, from the button and from the backend, and a finished prescreen leaves one line', async () => {
  // 按鈕狀態沒更新時硬按：使用者 2026-10-13 勾選頁確認 36（final-wording.json），後端判定的原因照原樣接在後面。
  const reason = '合成原因：後端判定這份資料不能繼續';
  let f = await openStep4({ validation: validationResult({ eligibility: { isEligible: false, reason } }) });
  f.$('[data-action="run-prescreen"]').dispatch('click'); await flush();
  assert.deepEqual(messageList(f)[0], { text: '預篩選尚無法執行：' + reason, level: 'warn' });

  // 畫面判定可以執行，送出後後端以完整性前提擋下：使用者 2026-10-13 勾選頁確認 18（final-wording.json）。
  // 擋下時沒有開始做事，不附耗時；屬於需要注意，第四步標題下方是琥珀色提示框（2026-10-13 的實作細節，計畫檔「進度」一節）。
  f = await openStep4({ validation: validationResult() });
  f.click('[data-action="run-prescreen"]');
  f.request('prescreenRun').reject({ code: 'completeness_prerequisite_failed', message: '合成：後端判定完整性前提不足。' });
  await flush();
  // 作業名稱沒有使用者裁定，只確認提到預篩選；句型是勾選頁確認 18。
  const blocked = messageList(f)[0];
  assert.match(blocked.text, /^[^：]*預篩選[^：]*尚無法執行：合成：後端判定完整性前提不足。$/);
  assert.equal(blocked.level, 'warn');
  const failure = f.middle.querySelector('.stepflow-item__failure');
  assert.ok(failure !== null, 'the step shows why it stopped');
  assert.equal(failure.textContent, blocked.text, 'the notice repeats the same line');
  assert.match(failure.className, /form-notice--warning/);
  assert.equal(f.state().lastRuns.prescreen, null, 'nothing was produced');

  // 再按一次成功：提示框拿掉，只留一則完成句。使用者 2026-10-13 勾選頁確認 37（final-wording.json）。
  f.click('[data-action="run-prescreen"]');
  f.request('prescreenRun').resolve(remapPrescreen('p-step4')); await flush();
  assert.ok(f.middle.querySelector('.stepflow-item__failure') === null, 'the notice is gone after the retry');
  const done = messageList(f).filter(m => m.text.startsWith('預篩選已更新'));
  assert.equal(done.length, 1);
  assert.match(done[0].text, new RegExp('^預篩選已更新，' + ELAPSED + '。$'));
});

// 取代 AccountTaxonomyFrontendTests.Save_MirrorsTheDownstreamInvalidationScope 的前端部分（後端失效範圍由 Domain/AuditDependencyPolicyTests 鎖住）。
// 原方法註解：使用者 2026-10-07 裁定上游修改清除下游：分類設定的修改也清掉全部已存情境。第一次失敗收據 20261007-032842584-7df9061f933d49778fe4b6b9f292a9a4。
test('journey 9 messages: saving categories logs the count and that the prescreen must run again, applies what the backend says went stale, and a failed save is an error line', async () => {
  const f = await openStep4({ validation: validationResult() });
  f.store.setLastRun('prescreen', remapPrescreen('p-before')); await flush();
  f.click('[data-action="toggle-taxonomy"]');
  f.click('[data-action="add-taxonomy-category"]');
  f.type('[data-taxonomy-label="draft-1"]', '新分類');
  f.click('[data-action="save-taxonomy"]');
  f.request('accountTaxonomySave').reject({ code: 'synthetic_failure', message: '合成：儲存失敗' }); await flush();
  // 失敗句：使用者 2026-10-13 勾選頁確認 19（final-wording.json），2026-10-14 第三輪 R3-27 括號後加句號。
  // 作業名稱沒有使用者裁定，只確認提到分類。
  assert.match(messageList(f)[0].text, new RegExp('^[^：]*分類[^：]*失敗：合成：儲存失敗（' + ELAPSED + '）。$'));
  assert.equal(messageList(f)[0].level, 'error');
  f.click('[data-action="save-taxonomy"]');
  const saved = f.request('accountTaxonomySave');
  const categories = plain(saved.payload.categories).map(item => item.categoryId === 'draft-1' ? { ...item, categoryId: 'cat-new' } : item);
  saved.resolve({ revision: 2, categories, invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: true, filter: false }, reportArtifacts: [] });
  await flush();
  // 使用者 2026-10-13 勾選頁確認 30（final-wording.json），句尾接上耗時（同日確認 15）。
  const done = messageList(f)[0];
  assert.match(done.text, new RegExp('^科目分類已更新，共 6 類；預篩選要重新執行，' + ELAPSED + '。$'));
  assert.equal(done.level, 'info');
  // 前端只照後端這次回應的失效範圍：驗證結果保留，預篩選結果清掉並標成要重新執行。
  assert.equal(f.state().lastRuns.validate.resultRef.runId, 'v-step4');
  assert.equal(f.state().lastRuns.prescreen, null);
  assert.equal(f.state().staleState.prescreen, true);
  assert.equal(f.state().staleState.validation, false);
});

// 第 9 批留下的訊息斷言：後端回報「變更已儲存，但報告清單沒有更新」時是一則需要注意的訊息（applyMutationEffects 的 reportArtifactWarning）。
// 句子由後端提供，畫面只照原樣顯示；這裡用使用者 2026-10-13 勾選頁確認 13 的句子（final-wording.json）當合成回應，
// 等級是 2026-10-13 訊息分三級的裁定（spec-item2-levels.md 第 4 節，#13 屬需要注意）。報告清單維持原樣，不當成清空。
test('journey 9 messages: when the backend saved the categories but could not refresh the report list, the auditor gets one attention line and the list stays', async () => {
  const f = await openStep4({ validation: validationResult() });
  const listed = [{ artifactId: 'validation-1', kind: 'validationReport', fileName: 'validation-1.xlsx', fileState: 'present', stale: false,
    generatedUtc: '2026-01-02T03:04:05Z', sizeBytes: 1, sourceRef: { validationRunId: 'v-step4' } }];
  f.store.setReportArtifacts(listed); await flush();
  f.click('[data-action="toggle-taxonomy"]');
  f.click('[data-action="add-taxonomy-category"]');
  f.type('[data-taxonomy-label="draft-1"]', '新分類');
  f.click('[data-action="save-taxonomy"]');
  const saved = f.request('accountTaxonomySave');
  const categories = plain(saved.payload.categories).map(item => item.categoryId === 'draft-1' ? { ...item, categoryId: 'cat-new' } : item);
  const warning = '變更已儲存，但報告清單沒有更新；重新開啟案件就會更新，不必重做變更。';
  saved.resolve({ revision: 2, categories, invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: true, filter: false }, reportArtifacts: null, reportArtifactWarning: warning });
  await flush();
  const lines = messageList(f).filter(m => m.text === warning);
  assert.deepEqual(lines, [{ text: warning, level: 'warn' }]);
  assert.deepEqual(plain(f.state().reportArtifacts.map(a => a.artifactId)), ['validation-1']);
});
