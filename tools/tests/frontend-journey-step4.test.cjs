'use strict';
// 情境 7：第四步執行資料驗證並解讀結果；情境 8：完整性不符時能做什麼，以及執行預篩選；
// 情境 9：第四步科目分類與科目配對（2026-10-14）。
//
// 審計員的操作：
//   情境 7　按「執行驗證」並回應合成結果；看 GL 分錄數、三項測試的說明、總帳日期空白的來源檔與列號並換頁；
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
// 計畫原本指定的 categoryReviewFixture 用正規式拼出的假節點、batch8Fixture 用手寫的控制項、batch9PagerLifecycleFixture
// 用一個合成的規則項目代替正式的驗證項目，三者都不經過正式畫面，所以這裡沒有複製它們（frontend-journey-case-setup.test.cjs 同樣的理由）。
//
// 取代的 C# 字串測試（src/JET/tests/JET.Tests/Architecture/，2026-10-14 整併刪除，紀錄見 impl-item1-wave23-consolidation.md）：
//   情境 7　UserFeedbackStepsOneToFourFrontendTests.ValidationUi_UsesRequestedPlainDescriptionsAndNamesTheActualSourceQualityCheck
//           BlankGlDateFrontendTests.ValidationUi_PresentsBlankGlDateAsSourceQualityOnly
//           PopulationAndDynamicColumnFrontendTests.PopulationSummary_IsRemovedWithoutReplacingBackendCountsWithFrontendMath
//           PopulationAndDynamicColumnFrontendTests.ExcludedEntries_HaveTheirOwnBackendDatasetAndReasonLabels
//           PopulationAndDynamicColumnFrontendTests.SourceQualityPage_IsTheOnlyFullDetailPathForSourceFindings
//           PopulationAndDynamicColumnFrontendTests.DynamicResultColumns_ComeFromTheBackendColumnMetadata 的抽樣明細部分
//           （命中明細部分屬情境 13，在 frontend-journey-filter-saved.test.cjs）
//   情境 8　CompletenessEligibilityFrontendTests 的 StepGate_UsesBackendEligibility_AllowsAdvancedFilterWithoutPrescreen_AndDoesNotRecomputeRawFacts、
//           ValidationCardAndPrescreenButton_SeparateWarningsFromAvailability、PrescreenAction_StopsBeforeBackendCallWhenBackendEligibilityIsIneligible、
//           StepFooter_EscapesDynamicBackendGateReason；ScreenWordingReviewFrontendTests.CompletenessReason_DistinguishesNeverRunFromOutdatedSummary；
//           PrescreenPositioningFrontendTests.StepFour_PresentsAggregatesBeforeSecondarySignalsAndOptionalReport
//   情境 9　AccountCategoryFrontendTests.Editor_ProtectsBuiltInsWithoutBlockingCategoriesUsedOnlyBySavedScenarios、
//           AccountCategoryFrontendTests.Save_UsesRevisionOptimisticConcurrencyAndNeverMintsCustomIds、
//           MappingNamesAndTemplateStatusFrontendTests.AccountTemplateStatus_ShowsOnTheJetTabOnlyWhilePreparingOrAfterFailure、
//           DataPreviewFrontendTests.AccountMappingPreviewButton_OpensDirectAccountMappingDataset、
//           WorkflowControlsAccessibilityFrontendTests.DisabledControlsExplainWhyInsteadOfSilentlyLocking 的第四步部分
//           （第三步配對的部分屬情境 6，在 frontend-journey-mapping.test.cjs）
//   以上方法都已刪除。AccountCategoryFrontendTests.Save_MirrorsTheDownstreamInvalidationScope 也已刪除：前端部分由
//   「messages: saving categories…」取代，後端的失效範圍由 Domain/AuditDependencyPolicyTests 鎖住；原方法註解的
//   第一次失敗收據已補在那個測試上方（2026-10-14）。
//
// 前面的測試不斷言訊息面板的句子：驗證完成、預篩選完成、預篩選尚無法執行、科目分類已更新、操作失敗與尚無法執行的句子，
// 等用字確認後補在本檔最後。後端原因一律用合成字串，只確認原樣顯示。
// 逐字比對的只有使用者裁定過的文字，出處寫在各測試的註解；其他文字只比對意思。
// 斷言的失敗值一律不是假 DOM 節點（節點只用 === 轉成布林）；預期值一律寫死；action payload 用 deepEqual 精確比對，並確認沒有 `__` 鍵。
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { root, settle, plain, batch9TreeDom, recordingJetApi, timerQueue, fakeMatchMedia, cssEscape, exposeAt, indexMarkup, readWwwroot,
  remapPrescreen, noDunderKeys, FILTER_STEP_FILES } = require('./support/frontend-fixtures.cjs');
const { unchecked } = require('./support/action-contract.cjs');

const VALIDATE_INDEX = 3;

async function flush(times = 4) { for (let i = 0; i < times; i++) await settle(); }

// 第四步的完整畫面：中欄、左側目錄與右側資料預覽都由正式程式畫出。
// mapped：GL 與 TB 是否已確認配對（第四步按鈕是否可以按）；validation：Store 裡已有的驗證結果（null 表示從未驗證）。
// laptop：視窗寬度是否不到 1536px（小筆電版面，裁定 2026-11-05-09）；只有這一個斷點跟著它，其他斷點照舊是寬視窗。
function step4Fixture({ mapped = true, validation = null, accountCategory = null, laptop = false } = {}) {
  const dom = batch9TreeDom({ bubbles: true, serialization: 'browser', rootAttributes: { 'data-jet-root': '' } });
  const requests = [], timers = timerQueue(), media = fakeMatchMedia(query => laptop && query === '(max-width: 1535px)');
  const window = { setTimeout: timers.setTimeout, clearTimeout: timers.clearTimeout, setInterval: () => 0, clearInterval() {},
    addEventListener() {}, removeEventListener() {}, matchMedia: media.matchMedia, innerWidth: 1600, CSS: { escape: cssEscape },
    JetFocus: { defer() {} }, JetApi: recordingJetApi(requests),
    // vm 沒有版面；app.js 的統計卡字級調整與捲動定位會讀計算後樣式，這裡回一份沒有格線、一般定位的樣式。
    getComputedStyle: () => ({ gridTemplateColumns: 'none', position: 'static', display: 'block' }) };
  const context = vm.createContext({ window, document: dom.document, setTimeout: timers.setTimeout, clearTimeout: timers.clearTimeout });
  dom.document.body = dom.element('body');
  // 第五步拆成五個檔案，照 index.html 的順序載入。第一次失敗：FrontendMapping 收據 20261025-053146514-770b4770cad34a55a1f25bee3a62b08f。
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js', 'filter-vouchers.js',
    'steps/create-step.js', 'steps/import-step.js', 'steps/mapping-step.js', 'steps/validate-step.js',
    'account-mapping-editor.js', ...FILTER_STEP_FILES, 'steps/export-step.js', 'data-preview.js'])
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context, { filename: file });
  vm.runInContext(exposeAt(readWwwroot('js/app.js'), "  if (document.readyState === 'loading') {",
    '  window.step4App = { renderStepNav: renderStepNav, renderContent: renderContent, overviewStepFactsHtml: overviewStepFactsHtml };\n'), context, { filename: 'app.js' });
  dom.content.innerHTML = '<nav data-bind="step-nav"></nav><section class="content" data-bind="content"></section>' +
    indexMarkup('<aside class="data-preview"', '</aside>') + '</aside>';
  const store = window.JetStore, ui = window.JetUi, app = window.step4App;
  const middle = dom.content.querySelector('[data-bind="content"]');
  ui.initDataPreview();
  store.setProject({ projectId: 'step4-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: '2025-12-20' });
  store.setImportResult('gl', { batchId: 'gl-source', columns: ['Doc', 'Amount'], rowCount: 2 });
  store.setImportResult('tb', { batchId: 'tb-source', columns: ['Code', 'Amount'], rowCount: 2 });
  if (mapped) {
    store.setMappingCommitted('gl', { mode: 'signed', mapping: { voucherNumber: 'Doc', amount: 'Amount' } });
    store.setMappingCommitted('tb', { mode: 'direct', mapping: { accountNumber: 'Code', changeAmount: 'Amount' } });
  }
  if (accountCategory) store.setCategory(accountCategory);
  store.setResult('validate', validation);
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
test('category paging precedes cancel and save in the footer', async () => {
  const f = await openStep4();
  // 2026-10-16 改為圖示分頁，無障礙名稱仍保留前後頁；首敗見本輪 093720476 收據。
  assert.deepEqual(f.$('.account-editor__footer').querySelectorAll('button').map(button => button.getAttribute('aria-label') || button.textContent),
    ['上一頁', '下一頁', '取消變更', '儲存科目分類']);
});

// 修改前失敗：20261014-125539535-e6819c0d2f8940eda66ed524fe3ca0c0，取消全選仍寫「清除勾選」。
test('account selection uses the shared deselection wording', async () => {
  const f = await openStep4();
  assert.equal(f.$('[data-account-clear-selection]').textContent, '全部取消');
});

// 修改前失敗：20261014-124845687-c1409ef46f0e4be099412d4659210ff4，讀取失敗與分類樹尚未共用。
test('account loading failures use the shared notice without changing retry', async () => {
  const f = step4Fixture(); await flush();
  f.request('queryAccountMappingPage').reject(new Error('合成讀取失敗')); await flush();
  const retry = f.$('[data-account-retry]');
  assert.ok(retry.closest('[data-load-failure].form-notice[role="alert"]'));
  assert.equal(retry.textContent, '重新載入');
  const before = f.pending('queryAccountMappingPage').length;
  f.click('[data-account-retry]');
  assert.equal(f.pending('queryAccountMappingPage').length, before + 1);
});

// 裁定 2026-10-15-09：換步驟時「點下去的時候我會預期元素開始移動，但他會頓一下才開始移動」。進入第四步時，
// 「在 JET 配對」第一次讀科目，畫面本來就寫著正在載入，讀取卻又要求整個中欄再重建一次，換到第四步的那一下多花七、八十毫秒。
// 第一次讀取只送出查詢，不另外重建；讀回來時照常重建。
// 第一次失敗：FrontendMapping 20261015-154942488-49178d0183b34fc5b37af4e987ce64ae，顯示科目清單時重建了兩次。
test('showing the account list for the first time redraws once while it loads', async () => {
  const f = step4Fixture({ mapped: false });
  await flush();
  const before = f.state().contentVersion;
  f.store.setMappingCommitted('gl', { mode: 'signed', mapping: { voucherNumber: 'Doc', amount: 'Amount' } });
  assert.equal(f.pending('queryAccountMappingPage').length, 1, 'the first page is requested');
  assert.match(f.text('[data-bind="account-editor"]'), /正在載入科目/);
  assert.equal(f.state().contentVersion - before, 1, 'the page is rebuilt once, not again for the loading state');
  f.request('queryAccountMappingPage').resolve({ rows: [{ accountNumber: '1101', accountName: '合成現金', categoryId: null }], nextCursor: null });
  await flush();
  assert.equal(f.state().contentVersion - before, 2, 'the loaded rows are drawn');
  assert.match(f.text('[data-bind="account-editor"]'), /合成現金/);
});

// 同一句反映的另一個原因：換步驟原本要等放開滑鼠才開始，按住的那一下畫面不動。左側目錄與中欄的步驟列改成按下就換，
// 和 macOS 側邊欄一樣；放開時不再換一次。鍵盤的 Enter 與空白鍵照舊。
// 第一次失敗：FrontendMapping 20261015-154942488-49178d0183b34fc5b37af4e987ce64ae，按下時步驟沒有換。
test('step navigation switches on press and the release does not switch again', async () => {
  const f = await openStep4();
  const navItem = f.navItem(1);
  navItem.dispatch('pointerdown', { button: 0, isPrimary: true, pointerType: 'mouse' });
  assert.equal(f.state().currentStepIndex, 1, 'pressing a step in the list switches at once');
  const version = f.state().contentVersion;
  navItem.dispatch('click', { detail: 1 });
  assert.equal(f.state().currentStepIndex, 1);
  assert.equal(f.state().contentVersion, version, 'releasing on the same step does not redraw again');
  const row = f.middle.querySelector('.stepflow-item[data-step-nav][data-step-index="2"]');
  assert.ok(row !== null, 'the mapping row is shown in the middle column');
  row.dispatch('pointerdown', { button: 0, isPrimary: true, pointerType: 'mouse' });
  assert.equal(f.state().currentStepIndex, 2, 'pressing a step row in the middle column switches at once');
  f.navItem(VALIDATE_INDEX).dispatch('pointerdown', { button: 2, isPrimary: true, pointerType: 'mouse' });
  assert.equal(f.state().currentStepIndex, 2, 'a right press does not switch');
  f.navItem(VALIDATE_INDEX).dispatch('click', { detail: 0 });
  assert.equal(f.state().currentStepIndex, VALIDATE_INDEX, 'the keyboard still switches with a click');
});

// 裁定 2026-11-05-09：小筆電（視窗寬度不到 1536px）上，目前步驟上方的已完成步驟收成一列「已完成的步驟」，後面寫各步驟名稱；
// 點這一列在原地展開回原本的各列，再點一次或按 Enter 收起。寬視窗照舊一列一步；上方只有一步完成時不收。
// 視窗跨過斷點時流程跟著重畫。這是新加的測試。
test('on a laptop the finished steps above the current one fold into one row that opens in place', async () => {
  const doneRows = f => f.middle.querySelectorAll('.stepflow-item--done[data-step-nav]').map(row => row.getAttribute('data-step-index'));
  const wide = await openStep4();
  assert.equal(wide.middle.querySelector('[data-done-group-toggle]'), null, 'a wide window lists every finished step');
  assert.deepEqual(doneRows(wide), ['0', '1', '2']);

  const f = await openStep4({ laptop: true });
  const group = () => f.middle.querySelector('[data-done-group-toggle]');
  assert.ok(group() !== null, 'the finished steps fold into one row');
  assert.equal(group().querySelector('.stepflow-item__name').textContent, '已完成的步驟');
  assert.equal(group().querySelector('.stepflow-item__summary').textContent, '建立案件、匯入資料、欄位配對');
  assert.deepEqual([group().getAttribute('role'), group().getAttribute('tabindex'), group().getAttribute('aria-expanded')], ['button', '0', 'false']);
  assert.deepEqual(doneRows(f), [], 'folded steps are not listed one by one');
  assert.ok(f.middle.querySelector('.stepflow-item--current') !== null, 'the current step stays open below the folded row');

  group().dispatch('click');
  assert.equal(group().getAttribute('aria-expanded'), 'true');
  assert.deepEqual(doneRows(f), ['0', '1', '2'], 'opening the row lists the finished steps again in place');
  const row = f.middle.querySelector('.stepflow-item[data-step-nav][data-step-index="2"]');
  row.dispatch('pointerdown', { button: 0, isPrimary: true, pointerType: 'mouse' });
  assert.equal(f.state().currentStepIndex, 2, 'an opened row still switches steps');
  assert.equal(group().getAttribute('aria-expanded'), 'true', 'the row stays open after switching steps');
  group().dispatch('keydown', { key: 'Enter' });
  assert.equal(group().getAttribute('aria-expanded'), 'false');
  assert.deepEqual(doneRows(f), [], 'Enter folds the steps again');

  // 上方只有一步完成時不收；下方已完成的步驟本來就一列一步，不收。
  f.navItem(1).dispatch('pointerdown', { button: 0, isPrimary: true, pointerType: 'mouse' });
  assert.equal(group(), null, 'a single finished step is not folded');
  assert.deepEqual(doneRows(f), ['0', '2']);

  // 視窗放寬到 1536px 以上時改回一列一步。
  f.navItem(VALIDATE_INDEX).dispatch('pointerdown', { button: 0, isPrimary: true, pointerType: 'mouse' });
  assert.ok(group() !== null);
  f.media.lists.find(list => list.media === '(max-width: 1535px)').change(false);
  assert.equal(group(), null, 'widening the window lists every finished step again');
  assert.deepEqual(doneRows(f), ['0', '1', '2']);
});

// 2026-11-09 修正捲動卡頓（docs/specs/2026-11-09-scroll-performance-plan.md）：WebView2 維護無障礙樹時，掛著 ::before 或 ::after 的元素
// 一改樣式，就把底下整棵子樹重送一遍；目前步驟那一列包著整個工作面，捲動因此頓一下。流程軸線與目前步驟的藍色圓環改成每一列裡
// aria-hidden 的空元素：軸線是第一個子元素、圓環是最後一個，疊放順序和原本的 ::before、::after 相同。這是新加的測試；
// app.css 不在大型版面容器掛虛擬元素，由 DesignIntegrationFrontendTests 檢查。
test('each flow row draws its rail and the current ring as hidden empty children', async () => {
  const f = await openStep4({ laptop: true });
  const classes = node => (node.getAttribute('class') || '').split(/\s+/);
  const kind = node => classes(node).includes('stepflow-item__rail') ? 'rail' : classes(node).includes('stepflow-item__mark--current') ? 'ring' : null;
  const check = label => {
    const rows = f.middle.querySelectorAll('.stepflow > .stepflow-item');
    assert.ok(rows.length >= 3, label + ': the flow rows are drawn');
    for (const row of rows) {
      const current = classes(row).includes('stepflow-item--current');
      const marks = row.children.filter(kind);
      assert.deepEqual(marks.map(kind), current ? ['rail', 'ring'] : ['rail'], label + ': ' + row.getAttribute('class'));
      assert.equal(row.children[0], marks[0], label + ': the rail comes first, under the row like the old ::before');
      if (current) assert.equal(row.children.at(-1), marks[1], label + ': the ring comes last, over the row like the old ::after');
      for (const mark of marks) {
        assert.equal(mark.getAttribute('aria-hidden'), 'true', label + ': screen readers skip the ' + kind(mark));
        assert.equal(mark.children.length + mark.textContent.length, 0, label + ': the ' + kind(mark) + ' is empty');
      }
    }
  };
  check('finished steps folded');
  f.middle.querySelector('[data-done-group-toggle]').dispatch('click');
  check('finished steps opened');
  f.store.setStepFlowCollapsed(true);
  assert.ok(f.middle.querySelector('.stepflow-item--current.stepflow-item--collapsed') !== null, 'the current step is collapsed');
  check('current step collapsed');
});

// 2026-10-16 提交前複審找到按下就換步驟的三個漏洞：正在編輯的欄位來不及失焦，只在 change 存值的欄位會遺失；
// 按下的預設動作會把焦點移回按下的步驟，蓋過換步驟時開出的確認對話框；觸控與觸控筆按下時可能只是要捲動。
test('pressing a step lets the field being edited save first and leaves touch presses to the tap', async () => {
  const f = await openStep4();
  let stepWhenBlurred = null;
  f.dom.document.activeElement = { blur() { stepWhenBlurred = f.state().currentStepIndex; f.dom.document.activeElement = null; } };
  const before = f.state().currentStepIndex;
  const press = f.navItem(1).dispatch('pointerdown', { button: 0, isPrimary: true, pointerType: 'mouse' });
  assert.equal(stepWhenBlurred, before, 'the field being edited loses focus before the page is rebuilt');
  assert.equal(press.defaultPrevented, true, 'the press does not move focus back to the pressed step');
  assert.equal(f.state().currentStepIndex, 1);
  f.navItem(2).dispatch('pointerdown', { button: 0, isPrimary: true, pointerType: 'touch' });
  f.navItem(2).dispatch('pointerdown', { button: 0, isPrimary: true, pointerType: 'pen' });
  assert.equal(f.state().currentStepIndex, 1, 'a touch or pen press may be a scroll and does not switch');
  f.navItem(2).dispatch('click', { detail: 1 });
  assert.equal(f.state().currentStepIndex, 2, 'the tap switches when it is released');
});

// 同一次複審：第一次讀取科目時不再重建，搜尋框沒有畫成停用。讀取中送出的搜尋原本被默默略過，讀完後畫面寫著搜尋字，清單卻沒有篩選。
test('a search sent while the account list first loads is applied once it arrives', async () => {
  const f = step4Fixture({ mapped: false });
  await flush();
  f.store.setMappingCommitted('gl', { mode: 'signed', mapping: { voucherNumber: 'Doc', amount: 'Amount' } });
  assert.equal(f.pending('queryAccountMappingPage').length, 1);
  f.type('[data-account-search] input', '1101');
  f.$('[data-account-search]').dispatch('submit');
  assert.equal(f.pending('queryAccountMappingPage').length, 1, 'the first read is still running');
  f.request('queryAccountMappingPage').resolve({ rows: [{ accountNumber: '1101', accountName: '合成現金', categoryId: null }, { accountNumber: '4101', accountName: '合成收入', categoryId: null }], nextCursor: null });
  await flush();
  assert.equal(f.pending('queryAccountMappingPage').length, 2, 'the list is read again with the search');
  assert.deepEqual(plain(f.request('queryAccountMappingPage').payload), { cursor: null, pageSize: 200, search: '1101' });
});

// 裁定 2026-10-15-12：第四步「套用分類」改回下拉選單並保留上下層縮排，第五步分類樹不變。
// 修改前失敗：20261015-011855792-4174f66464c84ffe990d516f57676fae，舊測試鎖住摺疊分類樹與單選鈕。
test('account classification uses an indented dropdown and a submitted search', async () => {
  const f = await openStep4({ accountCategory: { revision: 1, categories: [
    { categoryId: 'root', label: '合成分類', semanticRole: 'cash', ordinal: 0 },
    { categoryId: 'child', label: '合成子分類', parentCategoryId: 'root', semanticRole: 'cash', ordinal: 1 }] } });
  assert.equal(f.$('[data-bind="account-editor"] .category-tree'), null);
  const dropdown = f.$('[data-account-bulk-category]');
  assert.equal(dropdown.tagName.toLowerCase(), 'select');
  assert.equal(f.$('[data-account-bulk-category] option[value="root"]').textContent, '合成分類');
  assert.equal(f.$('[data-account-bulk-category] option[value="child"]').textContent, '　└ 合成子分類');
  dropdown.value = 'child'; dropdown.dispatch('change');
  assert.equal(f.$('[data-account-bulk-category] option[value="child"]').hasAttribute('selected'), true);
  const search = f.$('[data-account-search]'); assert.ok(search.classList.contains('page-search'));
  f.type('[data-account-search] input', '1103');
  assert.equal(f.pending('queryAccountMappingPage').length, 0, 'typing does not query');
  search.dispatch('submit');
  assert.deepEqual(plain(f.request('queryAccountMappingPage').payload), { cursor: null, pageSize: 200, search: '1103' });
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

function validationResult({ resultId = 'v-step4', eligibility = { isEligible: true, reason: null, warning: null },
  storedTotals = { rowCountMatch: true, amountMatch: true }, diffAccountCount = 0, diffAccounts = [] } = {}) {
  return {
    resultRef: { resultId, generatedUtc: '2026-01-01T00:00:00Z' },
    // GL 分錄數 77 故意和「原始 100 減排除 20」的 80 不同，畫面若自己相減就會顯示 80。
    stats: { glRowCount: 77, voucherCount: 12, totalDebit: 1250, totalCredit: 1250, net: 0, periodStart: '2025-01-01', periodEnd: '2025-12-31' },
    populationSummary: { raw: { rowCount: 100 }, gl: { rowCount: 80 }, excluded: { rowCount: 20, byPeriodCount: 15, byPostingStatusCount: 5 } },
    completenessTest: { diffAccountCount, diffAccounts, storedTotals, naReason: null, eligibility },
    voucherBalanceTest: { unbalancedVoucherCount: 0 },
    infSamplingTest: { sampleSize: 3 },
    blankRecordsTest: { blankVoucherNumberCount: 0, blankAccountNumberCount: 0, blankDescriptionCount: 0, approvalDateNotInPeriodCount: 0, blankRows: [] },
    sourceQuality: { findingCount: 2, sampleRows: [{ category: 'blankGlDate', sourceLabel: '合成來源檔.xlsx', sourceRowNumber: 7,
      voucherNumber: 'JV-7', accountNumber: '1001', description: '合成摘要' }] }
  };
}

// 抽樣清單的檔名故意帶標記，確認報告清單把檔名當文字顯示。
// 2026-10-15 前後端契合：拿掉沒有人讀的 sizeBytes（正式後端與畫面都用 bytes），畫面不變。
function listedReports(resultId) {
  return ['validationReport', 'infReport'].map(kind => ({ reportId: 'step4-' + kind, kind,
    fileName: kind === 'infReport' ? '合成<img src=y>抽樣.xlsx' : kind + '.xlsx', fileState: 'present', stale: false,
    generatedUtc: '2026-01-01T00:00:00Z', sourceRef: { validationResultId: resultId } }));
}

// 從畫面按「執行驗證」開始：後端依序回應驗證、兩份報告與科目配對檔；templateOutcome 是科目配對檔的結果。
async function runValidation(f, data, templateOutcome = { resolve: { disposition: 'kept', fileName: '科目配對.xlsx' } }) {
  f.click('[data-action="run-validate"]');
  const run = f.request('validateRun');
  // 抽樣筆數改為可設定；首次失敗 20261106-130605159-4db39e263ddc490bb199a5b42411bb42。
  assert.deepEqual(plain(run.payload), { sampleSize: 59 });
  run.resolve(data); await flush();
  const reports = f.request('exportValidationReports');
  assert.deepEqual(plain(reports.payload), { resultId: data.resultRef.resultId });
  reports.resolve({ publishedReports: listedReports(data.resultRef.resultId), reports: listedReports(data.resultRef.resultId) }); await flush();
  const template = f.request('exportAccountMappingTemplate');
  assert.deepEqual(plain(template.payload), { resultId: data.resultRef.resultId, onlyIfMissing: true });
  if (templateOutcome === 'pending') return template;
  if (templateOutcome.reject) template.reject(templateOutcome.reject); else template.resolve(templateOutcome.resolve);
  await flush();
  return template;
}

// ---- 情境 7：執行資料驗證並解讀結果 ----

test('running validation shows the backend GL entry count, no population summary, and the three test descriptions in the ruled words', async () => {
  const f = await openStep4();
  // 從未驗證時按鈕寫「執行驗證」，各項都還沒有結果。
  assert.equal(f.text('[data-action="run-validate"]'), '執行驗證');
  // 裁定 2026-11-05-22：還沒執行時「執行驗證」是這個工作面的藍色主要按鈕；已經有結果時重新執行改成白色次要按鈕。這是新加的斷言。
  const buttonClasses = selector => f.$(selector).getAttribute('class').split(/\s+/);
  assert.ok(!buttonClasses('[data-action="run-validate"]').includes('btn--ghost'), 'running for the first time is the primary action');
  assert.equal(statCard(f, 'GL 分錄數'), '—');
  await runValidation(f, validationResult());
  // GL 分錄數直接取後端的 stats.glRowCount（77），不由原始筆數減排除筆數重算（那會是 80）。
  assert.equal(statCard(f, 'GL 分錄數'), '77');
  // 裁定 2026-10-08-13 整塊移除母體摘要（原 C# 註解；首次舊斷言失敗 c00059a502f54ee2bc22cce3a932f949）。
  assert.ok(f.$('[data-bind="population-summary"]') === null, 'no population summary block');
  assert.ok(f.$('.population-summary') === null, 'no population summary class');
  // 同一批畫面註解的下一則只移除捷徑：第四步沒有「檢視 GL(未納入測試)」的按鈕，資料集仍在右側資料預覽（下一個測試）。
  assert.ok(f.$('[data-action="preview-excluded-entries"]') === null, 'no excluded-entries shortcut');
  assert.equal(f.body().textContent.includes('檢視 GL(未納入測試)'), false);

  // 三項測試的說明逐字比對。出處（docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md）：
  //   完整性測試：「處理時機」一項，使用者選了「照建議改寫」（裁定 2026-10-05-01），第 7245 行起的評估結果寫出卡片文字
  //     「先確認 JET 存下的分錄筆數與借貸合計，和確認欄位配對時算出的一樣。這一步不和來源檔自己的合計比對。
  //       再比對各科目的總帳本期借貸淨額與試算表本期變動金額。金額不符代表總帳母體可能缺漏。」
  //     最後一句原本是使用者原文「金額不符代表總帳母體可能缺漏」，要逐字保留。
  //   借貸不平測試：裁定 2026-10-05-01 照原件用「、」；原件是使用者原文「借貸不平代表傳票編號、金額欄位可能有誤」。
  //   裁定 2026-10-30-20：畫面上的「可能」照確認頁的表改寫，取代逐字保留的說法。兩句改成
  //     「金額不符時，要確認總帳母體是否完整。」與「借貸不平時，要確認傳票號碼與金額欄位是否正確。」，
  //     改字前依預期失敗：FrontendMapping 收據 20261031-005738048-537d55bb503744e797c25b2b6ef9f583。
  //   資料可靠性測試：依裁定 2026-09-09-01 的使用者原句「需先確認RDE的可靠性後，方執行高風險篩選條件」還原說明與先後順序，
  //     只寫成說明、不新增擋下；畫面的寫法是「需先確認攸關資料元素（RDE）的可靠性後，再執行高風險篩選條件。」
  //   原 C# 測試的第一次失敗收據：20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022。
  // 使用者指出常駐說明太多（裁定 2026-10-29-02）：說明不再常駐在標題下方，滑鼠停在標題上時出現，展開「檢視」後寫在明細最上方。
  // 字沒有改，這裡改讀標題的提示；原本讀 .rule-item__desc，改版後依預期失敗，首敗收據 20261029-040252723-dde1d637675742fd85d9576ba633dd2d。
  assert.equal(f.$$('.rule-item__desc').length, 0, 'no description is kept under the titles');
  const desc = title => { const item = ruleItem(f, title); assert.ok(item !== null, title + ' listed'); return item.querySelector('.rule-item__title').getAttribute('title'); };
  assert.equal(desc('完整性測試'),
    '先確認 JET 存下的分錄筆數與借貸合計，和確認欄位配對時算出的一樣。這一步不和來源檔自己的合計比對。' +
    // 裁定 2026-11-03-10 改畫面用字；第一次失敗：FrontendMapping 收據 20261103-075637257-ceeda6449f334daeaa9b121cbec08fb0。
    '再比對各科目的總帳本期借貸淨額與年度變動金額。金額不符時，要確認 GL 分錄是否完整。');
  assert.equal(desc('借貸不平測試'), '依傳票號碼彙總借方與貸方金額，列出不相等的傳票。借貸不平時，要確認傳票號碼與金額欄位是否正確。');
  assert.equal(desc('資料可靠性測試'),
    '抽出分錄樣本，供核對傳票附件，確認摘要、日期等篩選欄位是否正確。需先確認攸關資料元素的可靠性後，再到第五步進階條件篩選。');
  // 總帳日期空白這一項的名稱是「總帳日期空白」：使用者原文「第三個"過帳日"空白，可能要改成"總帳日期"空白，配合前面GL欄位配對的文字」。
  // 說明只比對意思：寫出這些分錄沒有納入本次測試。
  const blank = desc('總帳日期空白');
  assert.match(blank, /未納入/);
  // 9/22 移除的報表沿革不再出現在說明裡。
  assert.equal(f.body().textContent.includes('ValidationReport 與 INF 報表中稱為'), false);
  assert.equal(f.body().textContent.includes('INF Report 只會發布'), false);
  // 驗證後按鈕改成重新執行；報告清單列出兩份報告（INF 的名稱由報告清單共用的對照顯示，只比對意思：含「抽樣」）。
  assert.equal(f.text('[data-action="run-validate"]'), '重新執行驗證');
  assert.ok(buttonClasses('[data-action="run-validate"]').includes('btn--ghost'), 'running again is a secondary button');
  const labels = f.$$('.report-output .report-item__label').map(node => node.textContent);
  assert.equal(labels.length, 2);
  assert.ok(labels.some(label => /抽樣/.test(label)), 'the INF report is named as a sampling list: ' + labels.join('、'));
  assert.ok(f.$$('.report-output .report-item__name').some(node => node.textContent === '合成<img src=y>抽樣.xlsx'), 'file name shown as text');
  assert.equal(f.body().querySelectorAll('img').length, 0, 'file name never becomes an element');
  // 用途已由資料可靠性測試的說明交代，報告區不再重複同一句。
  assert.equal(f.text('.report-output').includes('資料可靠性抽樣清單供核對傳票附件'), false);
});

test('when the stored entries disagree with the totals counted at mapping, the completeness status says so instead of naming account differences', async () => {
  // 狀態文字照名詞表的「存入筆數與合計不一致」（裁定 2026-10-29-27）。
  // 原本找「存下的分錄」的斷言首敗：20261030-093230267-1652b44bb2aa4567aadb670a1fee0598；改找新的名稱。
  const f = await openStep4({ validation: validationResult({ eligibility: { isEligible: false, reason: '合成原因：存下的分錄和確認配對時算出的不一致' },
    storedTotals: { rowCountMatch: false, amountMatch: true } }) });
  const status = ruleItem(f, '完整性測試').querySelector('.rule-status').textContent;
  assert.match(status, /存入筆數與合計/); assert.match(status, /不一致/);
  assert.doesNotMatch(status, /科目/);
});

test('blank GL dates list their source file and row, and opening asks the source quality page', async () => {
  const f = await openStep4({ validation: validationResult() });
  const item = ruleItem(f, '總帳日期空白');
  assert.ok(item !== null, 'source quality item listed');
  const before = f.requests.length;
  item.querySelector('[data-action="toggle-detail"]').dispatch('click');
  const panel = ruleItem(f, '總帳日期空白').querySelector('.rule-detail');
  assert.equal(panel.hidden, false);
  const heads = panel.querySelectorAll('thead th').map(th => th.textContent);
  assert.ok(heads.includes('來源檔') && heads.includes('來源列號'), 'source file and row columns: ' + heads.join('、'));
  const firstRow = panel.querySelectorAll('tbody tr')[0].querySelectorAll('td').map(td => td.textContent);
  assert.ok(firstRow.includes('合成來源檔.xlsx') && firstRow.includes('7'), 'first row names the source file and row: ' + firstRow.join('、'));
  assert.ok(firstRow.includes('總帳日期空白'), 'row category label: ' + firstRow.join('、'));

  const added = f.requests.slice(before);
  assert.deepEqual(added.map(r => r.action), ['querySourceQualityPage'], 'opening asks only the source quality page');
  // 2026-09-07 工作包 G：總帳日期空白明細表加上排序與搜尋，呼叫多帶 sort 與 search（取代 PopulationAndDynamicColumnFrontendTests.SourceQualityPage_…，
  // 原方法註解的第一次失敗收據 20260907-080325828）。
  assert.deepEqual(plain(added[0].payload), { cursor: null, pageSize: 200, sort: null, search: null });
  added[0].resolve({ rows: [{ category: 'blankGlDate', sourceLabel: '第二個來源.csv', sourceRowNumber: 12, voucherNumber: 'JV-12', accountNumber: '2001', description: '合成摘要二' }], nextCursor: null });
  await flush();
  const rows = ruleItem(f, '總帳日期空白').querySelectorAll('tbody tr').map(tr => tr.querySelectorAll('td').map(td => td.textContent));
  const loaded = rows.find(row => row.includes('第二個來源.csv'));
  assert.ok(loaded, 'loaded row is listed');
  assert.ok(loaded.includes('12') && loaded.includes('總帳日期空白'), 'loaded row names its source row and category: ' + loaded.join('、'));
  assert.equal(f.pending('queryBlankRecordsPage').length, 0, 'blank GL dates never use the null-record pages');
});

test('with no blank GL dates the item reads as nothing found and has no detail to open, whatever the null-record counts say', async () => {
  // 總帳日期空白只看後端的 sourceQuality；資料缺漏輔助檢視沒有這個計數（後端 BlankGlDateDetectionTests 鎖住 blankRecordsTest 不帶 blankGlDateCount）。
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

// 裁定 2026-10-21-04：GL(未納入測試) 的「日期不符」分成三種原因，選項原文：「分別標成「總帳日期空白」「早於查核期間」「晚於查核期間」
// 並各自計數，資料預覽可只看其中一種。GL 範圍與審計結果都不變。」（現行計畫「總帳日期在期末之後的分錄」一節）。
// 這個測試原本斷言改版前的 period 讀成日期不符與兩個計數，依這個裁定改成四種原因、四個計數與依原因篩選；
// 改之前的第一次失敗收據記在計畫。
test('entries left out of the test are their own preview dataset, each reason has its own count, and one reason can be viewed alone', async () => {
  const f = await openStep4({ validation: validationResult() });
  const aside = f.dom.content.querySelector('[data-bind="data-preview"]');
  const filterRow = aside.querySelector('[data-bind="data-preview-filter"]');
  assert.ok(filterRow !== null, 'reason filter row is part of the panel markup');
  assert.equal(filterRow.hidden, true, 'no reason filter while PBC-GL is shown');
  aside.querySelector('[data-more-toggle]').dispatch('click');
  const menuItem = aside.querySelector('[data-ds="glExcluded"]');
  assert.ok(menuItem !== null, 'GL(未納入測試) offered under other data');
  // 名稱逐字：裁定 2026-10-08-04（docs/history/specs/2026-10-06-kct-gl-requirements-plan.md 第 3371 行）「`未納入測試的分錄` 改成 `GL(未納入測試)`」，
  // 同日選「全系統統一 (Recommended)」。
  assert.equal(menuItem.textContent, 'GL(未納入測試)');
  const before = f.requests.length;
  menuItem.dispatch('click');
  const added = f.requests.slice(before);
  assert.deepEqual(added.map(r => r.action), ['queryDataPreview']);
  assert.deepEqual(plain(added[0].payload), { dataset: 'glExcluded', limit: 200, offset: 0 });
  assert.equal(filterRow.hidden, false, 'reason filter offered for GL(未納入測試)');
  const select = filterRow.querySelector('select');
  assert.ok(select !== null, 'reason filter is a select');
  // 三種日期原因逐字照選項原文；「全部」表示不篩選。
  assert.deepEqual(select.querySelectorAll('option').map(option => option.textContent),
    ['全部', '總帳日期空白', '早於查核期間', '晚於查核期間', '過帳狀態不符']);
  assert.equal(select.value, '');

  // 第一頁還沒回來就改看晚於查核期間：晚到的全部原因那一頁不能蓋掉篩選後的結果。
  select.value = 'afterPeriod'; select.dispatch('change');
  const filtered = f.requests.slice(before + 1);
  assert.deepEqual(filtered.map(r => r.action), ['queryDataPreview']);
  assert.deepEqual(plain(filtered[0].payload), { dataset: 'glExcluded', limit: 200, offset: 0, exclusionReason: 'afterPeriod' });
  const stats = { blankGlDateCount: 3, beforePeriodCount: 5, afterPeriodCount: 207, postingStatusCount: 11 };
  added[0].resolve({ columns: ['voucherNumber', 'exclusionReason'],
    rows: [['JV-21', 'blankGlDate'], ['JV-22', 'beforePeriod'], ['JV-23', 'afterPeriod'], ['JV-24', 'postingStatus']],
    totalCount: 226, stats });
  await flush();
  const body = aside.querySelector('[data-bind="data-preview-body"]');
  const reasons = () => body.querySelectorAll('tbody tr').map(tr => tr.querySelectorAll('td')[1].textContent);
  assert.deepEqual(reasons(), [], 'the late unfiltered page is not shown');
  filtered[0].resolve({ columns: ['voucherNumber', 'exclusionReason'], rows: [['JV-23', 'afterPeriod']], totalCount: 207, stats });
  await flush();
  assert.deepEqual(reasons(), ['晚於查核期間']);
  // 註記照後端的四個計數寫出筆數，不在畫面相加或相減；篩選時四個計數仍是全部未納入測試的分錄，總筆數是這種原因的筆數。
  const note = () => aside.querySelector('.data-preview__note').textContent;
  assert.match(note(), /總帳日期空白 3 筆/); assert.match(note(), /早於查核期間 5 筆/);
  assert.match(note(), /晚於查核期間 207 筆/); assert.match(note(), /過帳狀態不符 11 筆/);
  assert.doesNotMatch(note(), /日期不符/);
  assert.match(note(), /共 207 筆/);

  // 換頁仍只看同一種原因；續頁不回總筆數與計數，沿用第一頁的。
  const beforeNext = f.requests.length;
  aside.querySelector('[data-page-next]').dispatch('click');
  const next = f.requests.slice(beforeNext);
  assert.deepEqual(next.map(r => r.action), ['queryDataPreview']);
  assert.deepEqual(plain(next[0].payload), { dataset: 'glExcluded', limit: 200, offset: 200, exclusionReason: 'afterPeriod' });
  next[0].resolve({ columns: ['voucherNumber', 'exclusionReason'], rows: [['JV-99', 'afterPeriod']], totalCount: null, stats: null });
  await flush();
  assert.equal(aside.querySelector('[data-page-position]').textContent, '2/2');
  assert.match(note(), /晚於查核期間 207 筆/); assert.match(note(), /共 207 筆/);

  // 改回全部：不帶原因，從第一頁讀。
  const beforeAll = f.requests.length;
  select.value = ''; select.dispatch('change');
  const all = f.requests.slice(beforeAll);
  assert.deepEqual(all.map(r => r.action), ['queryDataPreview']);
  assert.deepEqual(plain(all[0].payload), { dataset: 'glExcluded', limit: 200, offset: 0 });
  all[0].resolve({ columns: ['voucherNumber', 'exclusionReason'],
    rows: [['JV-21', 'blankGlDate'], ['JV-22', 'beforePeriod'], ['JV-23', 'afterPeriod'], ['JV-24', 'postingStatus']],
    totalCount: 226, stats });
  await flush();
  assert.deepEqual(reasons(), ['總帳日期空白', '早於查核期間', '晚於查核期間', '過帳狀態不符']);

  // 換到 GL 時收起篩選，請求不帶原因；再回到 GL(未納入測試) 從全部開始。
  select.value = 'beforePeriod'; select.dispatch('change');
  for (const r of f.pending('queryDataPreview').slice(-1)) r.resolve({ columns: ['voucherNumber', 'exclusionReason'], rows: [['JV-22', 'beforePeriod']], totalCount: 5, stats });
  await flush();
  const beforeGl = f.requests.length;
  aside.querySelector('[data-ds="gl"]').dispatch('click');
  assert.deepEqual(plain(f.requests.slice(beforeGl)[0].payload), { dataset: 'gl', limit: 200, offset: 0 });
  assert.equal(filterRow.hidden, true, 'no reason filter on GL');
  const beforeBack = f.requests.length;
  aside.querySelector('[data-more-toggle]').dispatch('click');
  aside.querySelector('[data-ds="glExcluded"]').dispatch('click');
  assert.deepEqual(plain(f.requests.slice(beforeBack)[0].payload), { dataset: 'glExcluded', limit: 200, offset: 0 });
  assert.equal(filterRow.hidden, false);
  assert.equal(select.value, '');
});

test('the sampled entries table takes its columns from the backend and reads RDE amounts from customValues', async () => {
  const f = await openStep4({ validation: validationResult() });
  ruleItem(f, '資料可靠性測試').querySelector('[data-action="toggle-detail"]').dispatch('click');
  const page = f.request('queryInfSamplePage');
  assert.deepEqual(plain(page.payload), { cursor: null, pageSize: 200, sort: null, search: null });
  page.resolve(unchecked('列的頂層刻意放一個和攸關資料元素同名的干擾值；正式後端不會送這個鍵', { columns: [
    { key: 'voucherNumber', label: '傳票號碼', valueType: 'text', sortable: true },
    { key: 'rde.amount1', label: '合成攸關金額', valueType: 'amount', isCustom: true }
  ], rows: [{ voucherNumber: 'JV-31', 'rde.amount1': 999, customValues: { 'rde.amount1': 1234.5 } }], nextCursor: null }));
  await flush();
  const panel = ruleItem(f, '資料可靠性測試').querySelector('.rule-detail');
  assert.deepEqual(panel.querySelectorAll('thead th').map(th => th.textContent), ['傳票號碼', '合成攸關金額']);
  // 攸關資料元素欄的值只從 customValues 取；同名的頂層鍵 999 是干擾值，不得出現。
  assert.deepEqual(panel.querySelectorAll('tbody tr')[0].querySelectorAll('td').map(td => td.textContent), ['JV-31', '1,234.50']);
});

// ---- 情境 8：完整性不符時能做什麼，以及執行預篩選 ----

test('before any validation the prescreen card, the fifth step and the footer ask to run validation first; an old summary asks to run it again', async () => {
  // 裁定 2026-10-02-01（docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md 第 2903 行）的原話是「W7 從未驗證時寫「請重新執行資料驗證」：要修」；
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

test('when the backend says the data cannot continue, matching control totals do not unlock the fifth step or the prescreen', async () => {
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
  // 按鈕狀態沒更新時硬按：不送 prescreenRun（提示訊息的句子在本檔最後核對）。
  f.$('[data-action="run-prescreen"]').dispatch('click');
  await flush();
  assert.equal(f.pending('prescreenRun').length, 0);
});

test('a backend reason with markup is shown as text in the footer and never becomes an element', async () => {
  const reason = '<img src=x onerror=alert(1)>';
  const f = await openStep4({ validation: validationResult({ eligibility: { isEligible: false, reason } }) });
  const footer = f.$('.step-footer__hint');
  assert.ok(footer !== null, 'footer explains the lock');
  assert.ok(footer.innerHTML.includes('&lt;img'), 'footer escapes the reason');
  assert.equal(footer.textContent.includes(reason), true);
  assert.equal(f.dom.content.querySelectorAll('img').length, 0, 'no img element anywhere on the page');
});

test('a warning still allows the prescreen and the fifth step without a prescreen result, and the difference table keeps the warning', async () => {
  const warning = '合成提醒：GL 與 TB 有 1 個科目差異，可說明原因並繼續。';
  const f = await openStep4({ validation: validationResult({ eligibility: { isEligible: true, reason: null, warning }, diffAccountCount: 1,
    diffAccounts: [{ accountNumber: '1101', accountName: '合成科目', tbAmount: 100, glAmount: 90, diff: 10 }] }) });
  assert.equal(f.state().results.prescreen, null);
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

test('running the prescreen lists the two aggregates first, keeps the filter results folded and leaves the report optional', async () => {
  const f = await openStep4({ validation: validationResult() });
  // 裁定 2026-11-05-22：驗證已經有結果，重新執行驗證是白色；還沒執行的預篩選是藍色，執行後也改成白色。這是新加的斷言。
  const buttonClasses = selector => f.$(selector).getAttribute('class').split(/\s+/);
  assert.ok(buttonClasses('[data-action="run-validate"]').includes('btn--ghost'));
  assert.ok(!buttonClasses('[data-action="run-prescreen"]').includes('btn--ghost'), 'the prescreen still to run is the primary action');
  f.click('[data-action="run-prescreen"]');
  const run = f.request('prescreenRun');
  assert.deepEqual(plain(run.payload), {});
  // 執行預篩選這個操作不包含產生報告（只比對意思：操作名稱沒有「報告」）。
  assert.doesNotMatch(f.state().busyLabel || '', /報告/);
  run.resolve(remapPrescreen('p-step4'));
  await flush();
  assert.equal(f.state().results.prescreen.resultRef.resultId, 'p-step4');
  assert.ok(buttonClasses('[data-action="run-prescreen"]').includes('btn--ghost'), 'running the prescreen again is a secondary button');
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

// 裁定 2026-10-22-13 一條規則一個定義：第四步的空白摘要只留預篩選訊號那一處，「資料缺漏輔助檢視」只列空白傳票號碼與
// 空白科目編號；驗證報告 V_Report 3 照舊由後端寫出。第一次失敗：收據 20261024-190820658-e1f6ec57c899499f8f9f3d0be434178e。
test('the data-gap view lists blank voucher numbers and blank account codes only, and blank descriptions appear once as a prescreen signal', async () => {
  const f = await openStep4({ validation: validationResult() });
  f.click('[data-action="run-prescreen"]');
  f.request('prescreenRun').resolve(remapPrescreen('p-gap')); await flush();
  const signals = f.$('.rule-card--prescreen details.prescreen-signals');
  const lists = signals.querySelectorAll('.rule-list');
  assert.equal(lists.length, 2, 'the filter results and the data-gap view');
  const titles = list => list.querySelectorAll('.rule-item__title').map(node => node.textContent);
  assert.deepEqual(titles(lists[1]), ['空白傳票號碼', '空白科目編號']);
  assert.equal(titles(lists[0]).filter(title => title === '空白摘要').length, 1, 'blank descriptions stay one prescreen signal');
  assert.equal(signals.querySelectorAll('.rule-item__title').filter(node => node.textContent === '空白摘要').length, 1);
});

// ---- 規則無法執行時只顯示後端的一句原因，前端不再另接補救文字 ----
// 後端的句子已寫出缺什麼、到哪裡補，畫面不再另接補救文字；週末與假日卡只有核准側不能檢查時，照常顯示總帳日期那一側。

// 每條規則都是 0 筆的預篩選結果，再依測試覆寫個別規則。
function prescreenResult(resultId, overrides) { return Object.assign(remapPrescreen(resultId), overrides); }

async function runPrescreen(f, result) {
  f.click('[data-action="run-prescreen"]');
  f.request('prescreenRun').resolve(result);
  await flush();
}

// 按一條規則的「檢視」，回傳展開後的明細。
// 並排的兩項彙總把明細放在清單下方（使用者指出展開右格時左格被撐高、內容被推到中間，裁定 2026-10-29-05），所以依「檢視」的 aria-controls 找明細，不假設明細在同一列裡。
function openDetail(f, title) {
  const item = ruleItem(f, title);
  assert.ok(item !== null, title + ' listed');
  item.querySelector('[data-action="toggle-detail"]').dispatch('click');
  const panel = f.$('#' + ruleItem(f, title).querySelector('[data-action="toggle-detail"]').getAttribute('aria-controls'));
  assert.ok(panel !== null && panel.hidden === false, title + ' detail open');
  return panel;
}

// 明細最上方的項目名稱與規則說明（2026-10-29 起），之後才是這一項的結果或原因。
function detailLead(panel) {
  return ['.rule-detail__title', '.rule-detail__about'].map(selector => panel.querySelector(selector)).filter(Boolean).map(node => node.textContent).join('');
}

test('a prescreen rule that cannot run shows only the backend sentence, on its badge and in its opened detail', async () => {
  const f = await openStep4({ validation: validationResult() });
  const cases = [
    ['期末財報準備日起核准之分錄', 'prepPeriodApproval', '合成原因：甲欄尚未選擇來源欄，請回第三步欄位配對補上。'],
    ['未預期出現之特定借貸組合', 'unexpectedAccountPair', '合成原因：科目配對中尚無乙分類的科目，請先在第四步設定。'],
    ['依分錄編製者彙總', 'preparerSummary', '合成原因：丙欄尚未選擇來源欄，請回第三步欄位配對補上。'],
    ['假日過帳／核准之分錄', 'holidayActivity', '合成原因：尚未上傳丁檔，請先在第二步上傳。'],
    // 裁定 2026-10-29-27：第四步照舊版拿掉編製分錄較少的人員與使用較少的科目，原本第六項的編製分錄較少的人員跟著拿掉；第一次失敗：FrontendMapping 收據 20261030-170713163-f548633afe0749a791469b97942983be。
    ['非授權編製人員之分錄', 'nonAuthorizedPreparer', '合成原因：尚未匯入戊清單，請先在第二步匯入。']
  ];
  const overrides = {};
  for (const [, key, naReason] of cases) {
    overrides[key] = key === 'preparerSummary' ? { status: 'na', naReason, preparers: [], totalPreparerCount: null }
      : key === 'holidayActivity' ? { status: 'na', naReason, approvalNaReason: null, postingCount: 0, approvalCount: null }
        : { status: 'na', naReason, count: 0 };
  }
  await runPrescreen(f, prescreenResult('p-step4-na', overrides));
  for (const [title, , naReason] of cases) {
    const badge = ruleItem(f, title).querySelector('.rule-status');
    assert.equal(badge.textContent, '無法執行', title);
    assert.equal(badge.getAttribute('title'), naReason, title);
    const panel = openDetail(f, title);
    // 2026-10-29 起明細最上方先寫這一項在檢查什麼（原本常駐在標題下方的說明），之後只接後端那一句，畫面不自己寫原因或下一步。
    // 原本比對整個明細等於後端那一句，改版後依預期失敗，首敗收據 20261029-040252723-dde1d637675742fd85d9576ba633dd2d。
    assert.equal(panel.querySelector('.rule-detail__about').textContent, ruleItem(f, title).querySelector('.rule-item__title').getAttribute('title'), title);
    assert.equal(panel.textContent, detailLead(panel) + naReason, title + ' detail adds only the backend sentence');
    assert.ok(panel.querySelector('.rule-detail__remedy') === null, title + ' has no screen-written next step');
  }
});

test('without the approval date mapping, the weekend and holiday cards keep the posting side, open its entries and say only the posting date was checked', async () => {
  const note = '合成說明：只檢查了總帳日期。庚欄尚未選擇來源欄，請回第三步欄位配對補上。';
  const f = await openStep4({ validation: validationResult() });
  await runPrescreen(f, prescreenResult('p-step4-activity', {
    weekendActivity: { status: 'V', naReason: null, approvalNaReason: note, postingCount: 2, approvalCount: null },
    holidayActivity: { status: 'V', naReason: null, approvalNaReason: note, postingCount: 1, approvalCount: null } }));
  for (const [title, postingKey, count] of [['週末過帳／核准之分錄', 'weekendPosting', '2'], ['假日過帳／核准之分錄', 'holidayPosting', '1']]) {
    const badge = ruleItem(f, title).querySelector('.rule-status');
    // 狀態照常寫兩側的筆數，核准側沒有檢查寫成「—」；提示是後端那一句，不是畫面自己寫的句子。
    // 裁定 2026-11-03-10 改畫面用字；第一次失敗：FrontendMapping 收據 20261103-075637257-ceeda6449f334daeaa9b121cbec08fb0。
    assert.equal(badge.textContent, '總帳日期 ' + count + ' 筆，傳票核准日 —', title);
    assert.equal(badge.getAttribute('title'), note, title);
    f.requests.length = 0;
    const panel = openDetail(f, title);
    assert.deepEqual(panel.querySelectorAll('.rule-detail__reason').map(node => node.textContent), [note], title);
    assert.ok(panel.querySelector('.rule-detail__remedy') === null, title + ' has no screen-written next step');
    assert.deepEqual(panel.querySelectorAll('[data-prescreen-key]').map(node => node.getAttribute('data-prescreen-key')), [postingKey], title);
    assert.deepEqual(f.pending('queryPrescreenPage').map(r => plain(r.payload).ruleKey), [postingKey], title + ' loads the posting side entries');
  }
});

// ---- 傳票核准日不可用時「核准日不在期間」無法執行（裁定 2026-10-23-01） ----
// 後端的筆數是 null、原因寫缺什麼與到哪裡補；畫面和預篩選規則卡一樣只顯示這一句，不寫「通過」，也沒有分錄明細可載入。
// 第一次失敗：FrontendMapping 收據 20261023-102604534-70863c25b29d45eda2c9504cb762cc77（狀態顯示「通過」）。
test('without the approval date mapping, the out-of-period approval item cannot run and shows only the backend sentence', async () => {
  const reason = '合成原因：辛欄尚未選擇來源欄，請回第三步欄位配對補上。';
  const data = validationResult({ resultId: 'v-step4-no-approval' });
  data.blankRecordsTest = { status: 'na', blankVoucherNumberCount: 0, blankAccountNumberCount: 0, blankDescriptionCount: 0,
    approvalDateNotInPeriodCount: null, approvalDateNotInPeriodNaReason: reason, blankRows: [] };
  const f = await openStep4();
  await runValidation(f, data);
  const badge = ruleItem(f, '核准日不在期間').querySelector('.rule-status');
  assert.equal(badge.textContent, '無法執行');
  assert.equal(badge.getAttribute('title'), reason);
  f.requests.length = 0;
  const panel = openDetail(f, '核准日不在期間');
  // 明細最上方先寫這一項在檢查什麼（2026-10-29 起），之後只接後端那一句；首敗收據 20261029-040252723-dde1d637675742fd85d9576ba633dd2d。
  assert.equal(panel.textContent, detailLead(panel) + reason, 'detail adds only the backend sentence');
  assert.deepEqual(f.pending('queryBlankRecordsPage').map(r => plain(r.payload)), [], 'no entries to load');
});

test('when the approval date can be checked and nothing is out of period, the item still reads as passed', async () => {
  const data = validationResult({ resultId: 'v-step4-approval-ok' });
  data.blankRecordsTest = { status: 'na', blankVoucherNumberCount: 0, blankAccountNumberCount: 0, blankDescriptionCount: 0,
    approvalDateNotInPeriodCount: 0, approvalDateNotInPeriodNaReason: null, blankRows: [] };
  const f = await openStep4();
  await runValidation(f, data);
  const badge = ruleItem(f, '核准日不在期間').querySelector('.rule-status');
  assert.equal(badge.textContent, '通過');
  assert.equal(badge.getAttribute('title'), null);
});

test('when the backend says the validation must run again, the completeness detail shows that one sentence and the screen adds nothing', async () => {
  const reason = '合成原因：目前的驗證結果已過期，請重新執行資料驗證。';
  const f = await openStep4({ validation: validationResult({ eligibility: { isEligible: false, reason } }) });
  const panel = openDetail(f, '完整性測試');
  assert.deepEqual(panel.querySelectorAll('.rule-detail__reason').map(node => node.textContent), [reason]);
  assert.ok(panel.querySelector('.rule-detail__remedy') === null, 'no screen-written next step');
});

test('validate-step.js names itself the fourth step and the next-step paragraph style is gone', () => {
  const header = readWwwroot('js/steps/validate-step.js').split('*/')[0];
  assert.match(header, /第四步/);
  assert.doesNotMatch(header, /Step 3/);
  assert.doesNotMatch(readWwwroot('css/app.css'), /rule-detail__remedy/);
});

// ---- 情境 9：科目分類與科目配對 ----

const CATEGORY_WITH_BANK = { revision: 2, categories: [
  { categoryId: 'builtin.cash', label: 'Cash', ordinal: 0, semanticRole: 'cash', isBuiltIn: true },
  { categoryId: 'bank', label: '銀行存款', ordinal: 1, semanticRole: 'cash', parentCategoryId: null }
] };

// 裁定 2026-11-03-27 先隱藏科目分類的上下層級與分類用途，開關 Ui.FEATURES.categoryHierarchy 預設關著：分類設定沒有「進階設定」，
// 內建分類與自訂分類都沒有分類用途的選單，用途改不了。原本直接核對內建分類的用途選單停用、自訂分類的用途選單可以改；
// 改成先用一份夾具核對開關關著時兩者都沒有用途選單、內建分類照樣沒有移除鈕，再用另一份夾具打開開關，原本的斷言照舊。
// 開關在畫面重畫時讀取，每份夾具各自一個執行環境，不影響其他測試。第一次失敗：FrontendMapping 收據 20261103-102610249-33dada705bc54432a817530311880e4f。
test('built-in categories cannot be removed or repurposed, while a category used only by a saved scenario can be removed and saved', async () => {
  const hidden = await openStep4({ validation: validationResult(), accountCategory: CATEGORY_WITH_BANK });
  hidden.click('[data-action="toggle-category"]');
  assert.ok(hidden.$('[data-category-remove="builtin.cash"]') === null, 'built-in has no remove button with the switch off');
  assert.equal(hidden.$$('[data-category-role]').length, 0, 'with the switch off no category offers a role menu');
  assert.ok(hidden.$('[data-action="toggle-category-advanced"]') === null, 'with the switch off there is no 進階設定');
  assert.equal(hidden.$('[data-category-remove="bank"]').disabled, false);

  const f = await openStep4({ validation: validationResult(), accountCategory: CATEGORY_WITH_BANK });
  f.ui.FEATURES.categoryHierarchy = true;
  const scenarios = [{ name: '銀行情境', groups: [{ join: 'AND', matchScope: 'row', conditions: [{ type: 'accountSide', categoryMode: 'is', categoryIds: ['bank'], categorySelection: 'subtree' }] }] }];
  f.store.setSavedScenarios(scenarios);
  f.click('[data-action="toggle-category"]');
  // 2026-10-07：儲存分類會清除情境，所以被情境引用不能擋住移除（原 C# 註解；首次失敗 a8ad54d4ecdb4e859b12b3d4a7ee03df、16649cbb7e60449ca9bbdd2e532e22bc）。
  assert.ok(f.$('[data-category-remove="builtin.cash"]') === null, 'built-in has no remove button');
  const builtInRole = f.$('[data-category-role="builtin.cash"]');
  assert.equal(builtInRole.disabled, true);
  assert.equal(builtInRole.getAttribute('aria-disabled'), 'true');
  const customRole = f.$('[data-category-role="bank"]');
  assert.equal(customRole.disabled, false);
  assert.equal(customRole.getAttribute('aria-disabled'), null);
  assert.equal(f.$('[data-category-remove="bank"]').disabled, false);
  assert.equal(f.body().textContent.includes('不可刪除'), false);
  // 停用狀態沒更新時硬改內建分類的用途：草稿不採用，儲存時仍是 cash。
  builtInRole.value = 'revenue'; builtInRole.dispatch('change');
  f.click('[data-category-remove="bank"]');
  assert.ok(f.$('[data-category-label="bank"]') === null, 'removed category leaves the list');
  assert.equal(f.state().filter.savedScenarios, scenarios, 'scenarios stay until the save succeeds');
  f.click('[data-action="save-category"]');
  const save = f.request('accountCategorySave');
  assert.deepEqual(plain(save.payload), { revision: 2, categories: [
    { label: 'Cash', ordinal: 0, semanticRole: 'cash', parentCategoryId: null, categoryId: 'builtin.cash' }
  ] });
  assert.ok(noDunderKeys(save.payload));
});

test('new and renamed categories save with the current revision, list order and draft references; blank or duplicate names block saving and say why; a failed save can be retried', async () => {
  const f = await openStep4({ validation: validationResult() });
  f.click('[data-action="toggle-category"]');
  // 還沒有案件自己的分類設定：畫面列出五個內建分類。
  assert.deepEqual(f.$$('[data-category-label]').map(node => node.getAttribute('data-category-label')),
    ['builtin.revenue', 'builtin.receivables', 'builtin.cash', 'builtin.receipt_in_advance', 'builtin.others']);
  assert.equal(f.$('[data-action="save-category"]').disabled, true, 'nothing to save before a change');
  f.click('[data-action="add-category"]');
  // 新列名稱空白：儲存停用，按鈕上方寫出缺什麼（只比對意思；前綴查不到逐字裁定）。
  const problems = () => { const node = f.$('[data-bind="category-problems"]'); return node.hidden ? '' : node.textContent; };
  assert.equal(f.$('[data-action="save-category"]').disabled, true);
  assert.match(problems(), /名稱/);
  f.type('[data-category-label="draft-1"]', '新分類');
  assert.equal(problems(), '');
  assert.equal(f.$('[data-action="save-category"]').disabled, false);
  // 改名成和另一個分類重複：儲存停用並說明重複。
  f.type('[data-category-label="builtin.others"]', 'cash');
  assert.equal(f.$('[data-action="save-category"]').disabled, true);
  assert.match(problems(), /重複/);
  f.type('[data-category-label="builtin.others"]', '其他');
  assert.equal(problems(), '');
  f.click('[data-action="save-category"]');
  // 沒有分類設定時版本號是 1；順序照清單；新列只帶草稿代號 draft-1，正式識別碼由後端在同一次儲存時配置，前端不自己產生。
  const expected = { revision: 1, categories: [
    { label: 'Revenue', ordinal: 0, semanticRole: 'revenue', parentCategoryId: null, categoryId: 'builtin.revenue' },
    { label: 'Receivables', ordinal: 1, semanticRole: 'receivables', parentCategoryId: null, categoryId: 'builtin.receivables' },
    { label: 'Cash', ordinal: 2, semanticRole: 'cash', parentCategoryId: null, categoryId: 'builtin.cash' },
    { label: 'Receipt in advance', ordinal: 3, semanticRole: 'receipt_in_advance', parentCategoryId: null, categoryId: 'builtin.receipt_in_advance' },
    { label: '其他', ordinal: 4, semanticRole: 'others', parentCategoryId: null, categoryId: 'builtin.others' },
    { label: '新分類', ordinal: 5, semanticRole: 'others', parentCategoryId: null, categoryId: 'draft-1' }
  ] };
  const first = f.request('accountCategorySave');
  assert.deepEqual(plain(first.payload), expected);
  assert.ok(noDunderKeys(first.payload));
  first.reject({ code: 'synthetic_failure', message: '合成：儲存失敗' });
  await flush();
  // 失敗後草稿還在，可以直接再按儲存，送出同一份內容。
  assert.equal(f.$('[data-category-label="draft-1"]').value, '新分類');
  assert.equal(f.$('[data-bind="category-dirty-notice"]').hidden, false);
  f.click('[data-action="save-category"]');
  const second = f.request('accountCategorySave');
  assert.ok(second !== first, 'retry sends a new request');
  assert.deepEqual(plain(second.payload), expected);
  const saved = expected.categories.map(item => item.categoryId === 'draft-1' ? { ...item, categoryId: 'cat-new' } : item);
  second.resolve({ revision: 2, categories: saved, invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: true, filter: false }, reports: [] });
  await flush();
  assert.equal(f.state().accountCategory.revision, 2);
  assert.equal(f.$('[data-category-label="cat-new"]').value, '新分類');
  assert.equal(f.$('[data-bind="category-dirty-notice"]').hidden, true);
  assert.equal(f.$('[data-action="save-category"]').disabled, true);
});

test('a failed account mapping file shows its own reason on the JET tab, and retrying moves focus to the Excel tab prepare button', async () => {
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
  assert.deepEqual(plain(retry.payload), { resultId: 'v-step4', onlyIfMissing: true });
  retry.resolve({ disposition: 'created', fileName: '科目配對.xlsx', rowCount: 3 });
  await flush();
  f.click('[data-action="select-account-jet"]');
  // 準備好以後，「在 JET 配對」分頁不再顯示狀態。
  assert.ok(f.$('[data-account-template-status]') === null, 'no status on the JET tab after success');
});

// 用字依裁定 2026-10-14-01：準備科目配對檔失敗而沒有原因時，「在 JET 配對」分頁只請審計員再試一次，逐字比對。
// 取代 MappingNamesAndTemplateStatusFrontendTests.AccountTemplateStatus_… 裡同一句的斷言；那段斷言的第一次失敗收據
// 20261014-012448805-68a8e8ea71bc4d6392585a586ab6b91e。有原因時接冒號寫原因，由上一個測試確認。
test('a failed account mapping file without a reason asks to try again on the JET tab', async () => {
  const f = await openStep4();
  const template = await runValidation(f, validationResult(), 'pending');
  template.reject({ code: 'synthetic_failure', message: '' });
  await flush();
  const failed = f.$('[data-account-template-status="failed"]');
  assert.ok(failed !== null, 'failure status on the JET tab');
  const retry = failed.querySelector('[data-action="retry-account-template-in-excel"]');
  assert.ok(retry !== null, 'retry button sits in the JET tab status');
  // 裁定 2026-11-03-10 改畫面用字；第一次失敗：FrontendMapping 收據 20261103-075637257-ceeda6449f334daeaa9b121cbec08fb0。
  assert.equal(failed.textContent.replace(retry.textContent, '').trim(), '科目配對範本尚未準備好，請再試一次。');
});

test('the Excel tab preview opens the account mapping dataset in the data preview', async () => {
  const f = await openStep4({ validation: validationResult() });
  f.store.setAccountMappingState({ batchId: 'am-1', rowCount: 3, fileName: '科目配對.xlsx' });
  f.click('[data-action="toggle-account-excel"]');
  const before = f.requests.length;
  f.click('[data-action="preview-account-mapping"]');
  const added = f.requests.slice(before);
  assert.deepEqual(added.map(r => r.action), ['queryDataPreview']);
  assert.deepEqual(plain(added[0].payload), { dataset: 'accountMapping', limit: 200, offset: 0 });
  const tab = f.dom.content.querySelector('[data-bind="data-preview"] [data-ds="accountMapping"]');
  assert.equal(tab.getAttribute('aria-selected'), 'true');
});

// 裁定 2026-11-05-07：「用 Excel 配對」選了有多個工作表的 Excel 檔，例如工作底稿，先在托盤列出有欄名的工作表請審計員選；
// 有「自動化工具-科目配對資訊」時預先選它。選定後才送出匯入，帶 sheetName；取消時什麼都不送。
// 只有一個有欄名的工作表時照舊直接匯入，文字檔不讀工作表清單。
const WORKING_PAPER = { filePath: 'C:\\synthetic\\合成_WorkingPaper.xlsx', fileName: '合成_WorkingPaper.xlsx' };
const WORKING_PAPER_SHEETS = [
  { name: '封面', columns: ['合成標題'], rowCountEstimate: 5 },
  { name: '空白頁', columns: [], rowCountEstimate: null },
  { name: '自動化工具-科目配對資訊', columns: ['GL_NUMBER', 'GL_NAME', 'STANDARDIZED_ACCOUNT_NAME'], rowCountEstimate: 3 },
];
const inspected = worksheets => ({ fileType: 'xlsx', worksheets, columns: null, encoding: null, delimiter: null, notices: null });
const mappingImported = { batchId: 'am-wp', rowCount: 3, columns: ['GL_NUMBER', 'GL_NAME', 'STANDARDIZED_ACCOUNT_NAME'],
  fileName: '合成_WorkingPaper.xlsx', importedUtc: '2026-11-05T00:00:00Z', hasAnyCategory: true, hasRevenue: true, hasCounterpart: true,
  blankCategoryCount: 0, mappingOnlyCount: 0, unmappedCount: 0, columnMappingWarning: null, warnings: [] };

test('Excel mapping lists the sheets of a workbook, preselects the working paper account mapping sheet and imports the chosen one', async () => {
  const f = await openStep4({ validation: validationResult() });
  f.click('[data-action="toggle-account-excel"]');
  f.click('[data-action="import-account-mapping"]'); await flush();
  assert.deepEqual(plain(f.request('hostSelectFile').payload.extensions), ['.xlsx', '.csv']);
  f.request('hostSelectFile').resolve(WORKING_PAPER); await flush();
  assert.deepEqual(plain(f.request('importInspectFile').payload), { filePath: WORKING_PAPER.filePath });
  f.request('importInspectFile').resolve(inspected(WORKING_PAPER_SHEETS)); await flush();
  assert.equal(f.pending('importAccountMappingFromFile').length, 0, 'nothing is imported before a sheet is chosen');
  const pick = f.$('[data-bind="account-mapping-sheet-pick"]');
  assert.ok(pick !== null, 'the sheet choice is in the tray');
  assert.equal(pick.querySelector('.excel-mapping__hint').textContent, '「合成_WorkingPaper.xlsx」有多個工作表，請選擇科目配對所在的工作表。');
  const select = pick.querySelector('[data-account-mapping-sheet]');
  assert.deepEqual(select.querySelectorAll('option').map(option => option.textContent), ['封面', '自動化工具-科目配對資訊']);
  assert.deepEqual(select.querySelectorAll('option').filter(option => option.getAttribute('selected') !== null).map(option => option.textContent),
    ['自動化工具-科目配對資訊']);
  assert.deepEqual(f.$$('.excel-mapping .btn:not(.btn--ghost)').map(button => button.textContent), ['匯入這個工作表'], 'one primary button while choosing');
  f.click('[data-action="import-account-mapping-sheet"]'); await flush();
  assert.deepEqual(plain(f.request('importAccountMappingFromFile').payload),
    { filePath: WORKING_PAPER.filePath, fileName: WORKING_PAPER.fileName, sheetName: '自動化工具-科目配對資訊' });
  f.request('importAccountMappingFromFile').resolve(mappingImported); await flush();
  assert.equal(f.$('[data-bind="account-mapping-sheet-pick"]'), null, 'the choice closes after importing');
  assert.equal(f.state().importState.accountMapping.rowCount, 3);
  assert.ok(f.state().messages.some(m => /^科目配對匯入完成，共 3 個科目，耗時/.test(m.text)));
});

test('Excel mapping imports another chosen sheet, cancels without sending, and imports a single-sheet workbook directly', async () => {
  const f = await openStep4({ validation: validationResult() });
  f.click('[data-action="toggle-account-excel"]');
  f.click('[data-action="import-account-mapping"]'); await flush();
  f.request('hostSelectFile').resolve(WORKING_PAPER); await flush();
  f.request('importInspectFile').resolve(inspected(WORKING_PAPER_SHEETS)); await flush();
  const messagesBefore = f.state().messages.length;
  f.click('[data-action="cancel-account-mapping-sheet"]'); await flush();
  assert.equal(f.$('[data-bind="account-mapping-sheet-pick"]'), null);
  assert.equal(f.pending('importAccountMappingFromFile').length, 0, 'cancelling sends nothing');
  assert.equal(f.state().messages.length, messagesBefore, 'and logs nothing');

  f.click('[data-action="import-account-mapping"]'); await flush();
  f.request('hostSelectFile').resolve(WORKING_PAPER); await flush();
  f.request('importInspectFile').resolve(inspected(WORKING_PAPER_SHEETS)); await flush();
  const select = f.$('[data-account-mapping-sheet]');
  select.value = '封面'; select.dispatch('change');
  f.click('[data-action="import-account-mapping-sheet"]'); await flush();
  assert.equal(f.request('importAccountMappingFromFile').payload.sheetName, '封面');
  f.request('importAccountMappingFromFile').resolve(mappingImported); await flush();

  const before = f.pending('importAccountMappingFromFile').length;
  f.click('[data-action="import-account-mapping"]'); await flush();
  f.request('hostSelectFile').resolve({ filePath: 'C:\\synthetic\\合成科目配對.xlsx', fileName: '合成科目配對.xlsx' }); await flush();
  f.request('importInspectFile').resolve(inspected([WORKING_PAPER_SHEETS[2], WORKING_PAPER_SHEETS[1]])); await flush();
  assert.equal(f.$('[data-bind="account-mapping-sheet-pick"]'), null, 'one sheet with headers needs no choice');
  assert.equal(f.pending('importAccountMappingFromFile').length, before + 1);
  assert.equal(f.request('importAccountMappingFromFile').payload.sheetName, '自動化工具-科目配對資訊');
  f.request('importAccountMappingFromFile').resolve(mappingImported); await flush();

  const inspectedBefore = f.pending('importInspectFile').length;
  f.click('[data-action="import-account-mapping"]'); await flush();
  f.request('hostSelectFile').resolve({ filePath: 'C:\\synthetic\\合成科目配對.csv', fileName: '合成科目配對.csv' }); await flush();
  assert.equal(f.pending('importInspectFile').length, inspectedBefore, 'a text file is imported without reading sheets');
  assert.deepEqual(plain(f.request('importAccountMappingFromFile').payload),
    { filePath: 'C:\\synthetic\\合成科目配對.csv', fileName: '合成科目配對.csv' });
});

// 提交前複審發現：JET 匯出的科目配對範本有範本那一張、分類清單 List 與隱藏的中繼資料工作表，原本只要有兩張以上就要先選工作表，
// 按「準備科目配對範本」、在 Excel 填好再匯入回來的主要路徑因此多了一步，選單裡還有審計員在 Excel 看不到的中繼資料工作表。
// 改成 JET 自己的範本照舊直接匯入範本那一張，中繼資料工作表不列進選單。範本是依名稱認的，第一列沒有欄名照樣認得。這是新加的測試。
test('Excel mapping imports JET\'s own template sheet directly and never lists the hidden metadata sheet', async () => {
  const f = await openStep4({ validation: validationResult() });
  f.click('[data-action="toggle-account-excel"]');
  f.click('[data-action="import-account-mapping"]'); await flush();
  f.request('hostSelectFile').resolve({ filePath: 'C:\\synthetic\\合成_AccountMapping.xlsx', fileName: '合成_AccountMapping.xlsx' }); await flush();
  f.request('importInspectFile').resolve(inspected([
    { name: 'AccountMapping', columns: [], rowCountEstimate: 6 },
    { name: 'List', columns: ['合成分類'], rowCountEstimate: 4 },
    { name: 'JET_Metadata', columns: ['JET_REPORT_METADATA'], rowCountEstimate: 2 }
  ])); await flush();
  assert.equal(f.$('[data-bind="account-mapping-sheet-pick"]'), null, 'JET\'s own template needs no sheet choice');
  assert.deepEqual(plain(f.request('importAccountMappingFromFile').payload),
    { filePath: 'C:\\synthetic\\合成_AccountMapping.xlsx', fileName: '合成_AccountMapping.xlsx', sheetName: 'AccountMapping' });
  f.request('importAccountMappingFromFile').resolve(mappingImported); await flush();

  // 工作底稿也有中繼資料工作表，選單照樣不列它。
  f.click('[data-action="import-account-mapping"]'); await flush();
  f.request('hostSelectFile').resolve(WORKING_PAPER); await flush();
  f.request('importInspectFile').resolve(inspected(WORKING_PAPER_SHEETS.concat([{ name: 'JET_Metadata', columns: ['JET_REPORT_METADATA'], rowCountEstimate: 2 }]))); await flush();
  assert.deepEqual(f.$('[data-account-mapping-sheet]').querySelectorAll('option').map(option => option.textContent), ['封面', '自動化工具-科目配對資訊']);
});

// 裁定 2026-11-06-12：使用者回報「用 Excel 配對」選到 Excel 正開著的檔案時，原本的錯誤只出現在收合的訊息面板，
// 看起來像什麼都沒發生。現在照樣匯入，後端另外列出的開著檔案提醒放在托盤的匯入結果上方，訊息面板照舊另記一則。
// 提醒跟著那一次匯入走：上游修改清掉科目配對後不再畫（2026-11-06 獨立複審找到的殘留），下一次乾淨的匯入後也拿掉。這是新增的測試。
test('Excel mapping of a workbook held open by Excel puts the reminder above the results in the tray and drops it after a clean import', async () => {
  const held = '檔案「合成科目配對.xlsx」正被其他程式開啟（例如 Excel）。JET 匯入的是這個檔案最後一次存檔的內容，尚未存檔的修改沒有匯入。';
  const f = await openStep4({ validation: validationResult() });
  f.click('[data-action="toggle-account-excel"]');
  let imports = 0;
  const importOnce = async openElsewhereWarnings => {
    imports++;
    f.click('[data-action="import-account-mapping"]'); await flush();
    f.request('hostSelectFile').resolve({ filePath: 'C:\\synthetic\\合成科目配對.xlsx', fileName: '合成科目配對.xlsx' }); await flush();
    f.request('importInspectFile').resolve(inspected([WORKING_PAPER_SHEETS[2]])); await flush();
    f.request('importAccountMappingFromFile').resolve({ ...mappingImported, batchId: 'am-held-' + imports, fileName: '合成科目配對.xlsx',
      warnings: openElsewhereWarnings.slice(), openElsewhereWarnings }); await flush();
  };
  const reminders = () => f.$$('.excel-mapping [data-open-elsewhere="accountMapping"]');

  await importOnce([held]);
  const notices = reminders();
  assert.deepEqual(notices.map(notice => notice.textContent), [held]);
  assert.ok(notices[0].classList.contains('form-notice--warning'), 'an amber notice, not an error');
  assert.equal(notices[0].getAttribute('role'), 'status');
  assert.ok(f.state().messages.some(m => m.text === held && m.level === 'warn'), 'the message panel keeps the record');

  // 重新匯入 GL 或 TB 等上游修改會清掉科目配對，托盤上已經沒有這次匯入的結果，提醒跟著不畫。
  f.store.applyMutationEffects({ invalidatedResults: { accountMapping: true } }); await flush();
  assert.equal(f.state().importState.accountMapping, null);
  assert.deepEqual(reminders(), []);

  await importOnce([held]);
  assert.deepEqual(reminders().map(notice => notice.textContent), [held]);
  await importOnce([]);
  assert.deepEqual(reminders(), []);
});

// ---- 訊息面板的句子（2026-10-14 補上） ----
// 前面的測試刻意不斷言訊息面板，等文字確認並改進產品後才補上。逐字比對的句子都註明出自 final-wording.json 或
// final-wording-r3.json；後端原因一律用合成字串。這幾個測試另取代：
//   BlankGlDateFrontendTests.ValidationUi_ReportsSourceQualityCountSeparatelyFromBlankRecordTotal
//   CompletenessEligibilityFrontendTests.UiRun_MapsCompletenessPrerequisiteFailure_AndPreservesBackendDetail
//   AccountCategoryFrontendTests.Save_MirrorsTheDownstreamInvalidationScope 的前端部分（後端失效範圍由 Domain/AuditDependencyPolicyTests 鎖住）
const ELAPSED = /耗時(?: \d+\.\d 秒|不到 0\.1 秒|\d+ 分 \d+ 秒)/.source;
const messageList = f => plain(f.state().messages.map(m => ({ text: m.text, level: m.level })));

test('messages: a finished validation leaves one short completion line, and blank GL dates and blank fields keep their own counts in their own items', async () => {
  const f = await openStep4();
  const data = validationResult();
  data.blankRecordsTest = { blankVoucherNumberCount: 3, blankAccountNumberCount: 4, blankDescriptionCount: 0, approvalDateNotInPeriodCount: 0, blankRows: [] };
  data.sourceQuality.findingCount = 2;
  await runValidation(f, data);
  const messages = messageList(f);
  // 用字依裁定 2026-10-13-28（final-wording.json）：完成句只留「資料驗證完成，耗時 X。」，各項筆數寫在下方各項結果；
  // 耗時的寫法是裁定 2026-10-13-17「保留，改成秒並合併」，不到 0.1 秒的寫法依裁定 2026-10-14-01。
  const done = messages.filter(m => m.text.startsWith('資料驗證完成'));
  assert.equal(done.length, 1, 'one completion line');
  assert.match(done[0].text, new RegExp('^資料驗證完成，' + ELAPSED + '。$'));
  assert.equal(done[0].level, 'info');
  // 裁定 2026-10-13-28 維持原句（final-wording.json）。
  assert.ok(messages.some(m => m.text === '已在案件資料夾產生驗證階段的 2 份報告。' && m.level === 'info'), 'the report line');
  // 舊的合計說法不再出現，也不逐欄串成「欄位異常」。
  assert.ok(!messages.some(m => /異常項次合計|空值項目合計|獨立計數|欄位異常/.test(m.text)), 'no combined count wording');
  // 總帳日期空白的 2 筆只在自己的一項，不和空白傳票號碼 3 筆、空白科目編號 4 筆相加（只比對意思）。
  const status = title => { const item = ruleItem(f, title); assert.ok(item !== null, title + ' item rendered'); return item.querySelector('.rule-status').textContent; };
  assert.match(status('總帳日期空白'), /^2\D/);
  assert.match(status('空白傳票號碼'), /^3\D/);
  assert.match(status('空白科目編號'), /^4\D/);
  assert.ok(!f.$$('.rule-status').some(node => /^(?:5|7|9)\D/.test(node.textContent)), 'no item adds the counts together');
  // 空白傳票號碼的說明：用字依裁定 2026-10-14-01（final-wording-r3.json）。
  // 說明 2026-10-29 起放在標題的提示（首敗收據 20261029-040252723-dde1d637675742fd85d9576ba633dd2d），字不變。
  assert.equal(ruleItem(f, '空白傳票號碼').querySelector('.rule-item__title').getAttribute('title'),
    '傳票號碼空白的分錄。與空白科目編號分開列示；傳票號碼與科目編號都空白的分錄，兩份明細都會列出。');
});

test('messages: the prescreen says it cannot run yet with the reason, from the button and from the backend, and a finished prescreen leaves one line', async () => {
  // 按鈕狀態沒更新時硬按：用字依裁定 2026-10-13-28（final-wording.json），後端判定的原因照原樣接在後面。
  const reason = '合成原因：後端判定這份資料不能繼續';
  let f = await openStep4({ validation: validationResult({ eligibility: { isEligible: false, reason } }) });
  f.$('[data-action="run-prescreen"]').dispatch('click'); await flush();
  assert.deepEqual(messageList(f)[0], { text: '預篩選尚無法執行：' + reason, level: 'warn' });

  // 畫面判定可以執行，送出後後端以完整性前提擋下：用字依裁定 2026-10-13-28（final-wording.json）。
  // 擋下時沒有開始做事，不附耗時；屬於需要注意，第四步標題下方是琥珀色提示框（2026-10-13 的實作細節，計畫檔「進度」一節）。
  f = await openStep4({ validation: validationResult() });
  f.click('[data-action="run-prescreen"]');
  f.request('prescreenRun').reject({ code: 'completeness_prerequisite_failed', message: '合成：後端判定完整性前提不足。' });
  await flush();
  // 作業名稱沒有使用者裁定，只確認提到預篩選；句型照上一則確認的句子。
  const blocked = messageList(f)[0];
  assert.match(blocked.text, /^[^：]*預篩選[^：]*尚無法執行：合成：後端判定完整性前提不足。$/);
  assert.equal(blocked.level, 'warn');
  const failure = f.middle.querySelector('.stepflow-item__failure');
  assert.ok(failure !== null, 'the step shows why it stopped');
  assert.equal(failure.textContent, blocked.text, 'the notice repeats the same line');
  assert.match(failure.className, /form-notice--warning/);
  assert.equal(f.state().results.prescreen, null, 'nothing was produced');

  // 再按一次成功：提示框拿掉，只留一則完成句。用字依裁定 2026-10-13-28（final-wording.json）。
  f.click('[data-action="run-prescreen"]');
  f.request('prescreenRun').resolve(remapPrescreen('p-step4')); await flush();
  assert.ok(f.middle.querySelector('.stepflow-item__failure') === null, 'the notice is gone after the retry');
  const done = messageList(f).filter(m => m.text.startsWith('預篩選已更新'));
  assert.equal(done.length, 1);
  assert.match(done[0].text, new RegExp('^預篩選已更新，' + ELAPSED + '。$'));
});

// 取代 AccountCategoryFrontendTests.Save_MirrorsTheDownstreamInvalidationScope 的前端部分（後端失效範圍由 Domain/AuditDependencyPolicyTests 鎖住）。
// 原方法註解：裁定 2026-10-07-03 上游修改清除下游：分類設定的修改也清掉全部已存情境。第一次失敗收據 20261007-032842584-7df9061f933d49778fe4b6b9f292a9a4。
test('messages: saving categories logs the count and that the prescreen must run again, applies what the backend says went stale, and a failed save is an error line', async () => {
  const f = await openStep4({ validation: validationResult() });
  f.store.setResult('prescreen', remapPrescreen('p-before')); await flush();
  f.click('[data-action="toggle-category"]');
  f.click('[data-action="add-category"]');
  f.type('[data-category-label="draft-1"]', '新分類');
  f.click('[data-action="save-category"]');
  f.request('accountCategorySave').reject({ code: 'synthetic_failure', message: '合成：儲存失敗' }); await flush();
  // 失敗句：用字依裁定 2026-10-13-28（final-wording.json），括號後的句號依裁定 2026-10-14-01。
  // 作業名稱沒有使用者裁定，只確認提到分類。
  assert.match(messageList(f)[0].text, new RegExp('^[^：]*分類[^：]*失敗：合成：儲存失敗（' + ELAPSED + '）。$'));
  assert.equal(messageList(f)[0].level, 'error');
  f.click('[data-action="save-category"]');
  const saved = f.request('accountCategorySave');
  const categories = plain(saved.payload.categories).map(item => item.categoryId === 'draft-1' ? { ...item, categoryId: 'cat-new' } : item);
  saved.resolve({ revision: 2, categories, invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: true, filter: false }, reports: [] });
  await flush();
  // 用字依裁定 2026-10-13-28（final-wording.json），句尾接上耗時。
  const done = messageList(f)[0];
  assert.match(done.text, new RegExp('^科目分類已更新，共 6 類；預篩選要重新執行，' + ELAPSED + '。$'));
  assert.equal(done.level, 'info');
  // 前端只照後端這次回應的失效範圍：驗證結果保留，預篩選結果清掉並標成要重新執行。
  assert.equal(f.state().results.validate.resultRef.resultId, 'v-step4');
  assert.equal(f.state().results.prescreen, null);
  assert.equal(f.state().staleState.prescreen, true);
  assert.equal(f.state().staleState.validation, false);
});

// 儲存流程留下的訊息斷言：後端回報「變更已儲存，但報告清單沒有更新」時是一則需要注意的訊息（applyMutationEffects 的 reportWarning）。
// 句子由後端提供，畫面只照原樣顯示；這裡用裁定 2026-10-13-28 的句子（final-wording.json）當合成回應，
// 等級依裁定 2026-10-13-15 訊息分三級（spec-item2-levels.md 第 4 節，這句屬需要注意）。報告清單維持原樣，不當成清空。
test('messages: when the backend saved the categories but could not refresh the report list, the auditor gets one attention line and the list stays', async () => {
  const f = await openStep4({ validation: validationResult() });
  const listed = [{ reportId: 'validation-1', kind: 'validationReport', fileName: 'validation-1.xlsx', fileState: 'present', stale: false,
    generatedUtc: '2026-01-02T03:04:05Z', sourceRef: { validationResultId: 'v-step4' } }];
  f.store.setReports(listed); await flush();
  f.click('[data-action="toggle-category"]');
  f.click('[data-action="add-category"]');
  f.type('[data-category-label="draft-1"]', '新分類');
  f.click('[data-action="save-category"]');
  const saved = f.request('accountCategorySave');
  const categories = plain(saved.payload.categories).map(item => item.categoryId === 'draft-1' ? { ...item, categoryId: 'cat-new' } : item);
  const warning = '變更已儲存，但報告清單沒有更新；重新開啟案件就會更新，不必重做變更。';
  saved.resolve({ revision: 2, categories, invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: true, filter: false }, reports: null, reportWarning: warning });
  await flush();
  const lines = messageList(f).filter(m => m.text === warning);
  assert.deepEqual(lines, [{ text: warning, level: 'warn' }]);
  assert.deepEqual(plain(f.state().reports.map(a => a.reportId)), ['validation-1']);
});

// 裁定 2026-10-19-08：同功能的元件只留一套，規則的「檢視」是共用的展開按鈕，字固定，開關寫在 aria-expanded，
// aria-controls 指向它打開的明細；原本字在「檢視」與「收合」之間切換。
// 裁定 2026-10-30-36：清單的列整列可點。規則有明細時整列就是開關，箭頭在列首，不再另放「檢視」；字是規則名稱與狀態，開合時不變。
// 第一次失敗：FrontendMapping 收據 20261031-095121839-24d9cd622b3242889605b39228f69a2e。
test('a rule row with details is one toggle that keeps its label and reports the detail it opens through aria-expanded and aria-controls', async () => {
  const f = await openStep4({ validation: validationResult() });
  const toggle = () => ruleItem(f, '總帳日期空白').querySelector('[data-action="toggle-detail"]');
  const panel = () => ruleItem(f, '總帳日期空白').querySelector('.rule-detail');
  const label = toggle().textContent;
  assert.match(label, /^總帳日期空白/, 'the whole row is the toggle and reads as the rule');
  assert.ok(toggle().classList.contains('row-toggle'));
  assert.ok(toggle().classList.contains('rule-item__head'));
  assert.equal(ruleItem(f, '總帳日期空白').querySelectorAll('button').filter(button => button.textContent === '檢視').length, 0, 'no separate 檢視 button');
  assert.equal(toggle().getAttribute('aria-expanded'), 'false');
  assert.ok(panel().getAttribute('id'), 'the detail has an id');
  assert.equal(toggle().getAttribute('aria-controls'), panel().getAttribute('id'));
  toggle().dispatch('click');
  assert.equal(panel().hidden, false);
  assert.equal(toggle().getAttribute('aria-expanded'), 'true');
  assert.equal(toggle().textContent, label, 'the label stays the same while open');
  toggle().dispatch('click');
  assert.equal(panel().hidden, true);
  assert.equal(toggle().getAttribute('aria-expanded'), 'false');
});

test('reliability sample count defaults to 59 and sends a chosen count', async () => {
  const f = await openStep4();
  const input = f.$('#inf-sample-size');
  assert.equal(input.value, '59');
  f.type('#inf-sample-size', '120');
  f.click('[data-action="run-validate"]');
  assert.deepEqual(plain(f.request('validateRun').payload), { sampleSize: 120 });
});
