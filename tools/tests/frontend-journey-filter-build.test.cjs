'use strict';
// 常用查法的程式名稱改成常用查法的字根（裁定 2026-11-03-30）：data-add-check 改 data-add-shortcut、FILTER_PURPOSE_CHECK_GROUPS 改 FIELD_SHORTCUT_GROUPS、
// findPurposeCheck 與 addPurposeCheck 改 findFieldShortcut 與 addFieldShortcut，只改名稱，核對的內容不變。第一次失敗：FrontendMapping 收據 20261103-122529439-5142ff4653664a1781743d0694d4c3af。
require('./support/resource-guard.cjs');
// 第五步組條件的情境測試（2026-10-13）。
//
// 情境 10「第五步常用篩選條件的卡片與範例」：審計員在四種科目配對狀態下看常用篩選條件的卡片與欄位的常用查法，
// 加入「貸方為收入但借方非一般對方科目」那張卡看說明，並套用每一個情境範例。2026-10-29 卡片與範例合併成「常用篩選條件」分頁、
// 拿掉卡片字母，原本要先展開「從範例開始」。2026-11-03 範例移到右側條件組合上方的「從情境範例開始」，常用查法從依審計目的的分類
// 改掛在欄位條件分頁的各欄位底下（裁定 2026-11-03-05、2026-11-03-06），同日再收進選單：先是按欄位名稱打開（裁定 2026-11-03-32），
// 使用者試用後改成按「加入」打開（裁定 2026-11-03-37），審計員用滑鼠或鍵盤選一項。2026-10-16 起另在「預篩選」分頁逐一加入每一條預篩選規則，
// 看前置不足的停用原因，並從分頁加入「未預期借貸組合」後查看傳票數量與分錄筆數。
// 情境 11「第五步自訂條件與條件組」：審計員在欄位條件分頁（2026-11-03 以前叫自訂篩選條件）從各家族各加一條、組成兩組，看組內條件的組合方式，
// 移除一條，用「新增條件到」換加入的組，改成 OR 再改成同一傳票，加借貸科目組合並切換五種查找方式，
// 在分類清單勾選分類，最後預覽，預覽的錯誤標在條件列上。
//
// 每個測試都執行正式的 state.js、ui-core.js、filter-values.js、filter-vouchers.js 與第五步的五個檔案（steps/filter-step-*.js 與 filter-step.js），
// 在假 DOM 上畫出第五步並用點擊與選單操作；合成資料只控制邊界。
// 第五步的儲存、另存、移除與重新檢查屬於情境 12、13（frontend-journey-filter-saved.test.cjs），這個檔不寫那些流程的斷言。
// 訊息面板的句子（例如當時加入卡片時的訊息）與後端原因句子不寫斷言，它們會在第 1、4 項改寫。
//
// 斷言規則照 artifacts/review/closing-four-items-20261013/plan-item1-tests.md 第 1 節：
// 只有使用者裁定過的文字逐字比對，出處寫在該斷言旁；其他文字只比對意思；預期值寫死，不由被測程式算出；
// 送出的 payload 精確比對，而且不得帶 `__` 開頭的鍵。
//
// 取代的 C# 方法（src/JET/tests/JET.Tests/Architecture/，由每波結束時統一刪除）：
//   FilterAccountMappingEligibilityFrontendTests 的三個方法、ScreenWordingReviewFrontendTests.K3_… 與核對當時卡片字母的方法、
//   FilterAstFrontendContractTests.GroupMatchScope_…、FilterBuilderFrontendTests 兩個方法、FilterCategorySelectFrontendTests 兩個方法、
//   FilterTemplatesFrontendTests.FilterStep_OffersTemplatesAndTheCombinationCard、FilterWorkspaceFrontendTests 兩個方法、
//   AccountCategoryFrontendTests.RuleControls_UseCategoryIdentitiesInsteadOfLegacyCategoryLabels。
// 對照表與改壞檢查紀錄見 artifacts/review/closing-four-items-20261013/impl-item1-batch5.md。
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { batch9TreeDom, readWwwroot, exposeAt, plain, settle, bootFullApp, flush, FILTER_STEP_PARTS } = require('./support/frontend-fixtures.cjs');
const { contractJetApi } = require('./support/action-contract.cjs');

const ALL_MAPPED = { hasAnyCategory: true, hasRevenue: true, hasCounterpart: true };

// 本檔自己的夾具：和共用模組的 filterOperationFixture 相同，執行正式腳本並訂閱重繪；
// 另外掛出 toWireDraft（情境 11 要看草稿送出的形狀），並記下預覽與傳票分頁的請求。
// 只服務這個檔的夾具留在這裡，沒有收進共用模組。
// availableGlFields 是後端 project.load 與 mapping.commit.gl 回傳的可用欄位清單（2026-10-23 加入，欄位可用性只在後端算一次）；
// 沒給時已確認的配對不帶清單，畫面不檢查欄位，和後端沒有清單時相同。
function buildFixture(options = {}) {
  const { accountMapping = ALL_MAPPED, glMapping = null, rdeFields = [], accountCategory = null, saved = [], availableGlFields = null,
    project = { projectId: 'filter-build-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: '2025-12-01' } } = options;
  const dom = batch9TreeDom(), requests = [];
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const context = vm.createContext({ window, document: dom.document });
  // 第五步拆成五個檔案：前四個先載入，主檔再掛三個函式；syncViewState 在主檔是轉呼叫，名稱仍看得到。第一次失敗：FrontendMapping 收據 20261025-053146514-770b4770cad34a55a1f25bee3a62b08f。
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js', 'filter-vouchers.js', ...FILTER_STEP_PARTS])
    vm.runInContext(readWwwroot('js/' + file), context, { filename: file });
  vm.runInContext(exposeAt(readWwwroot('js/steps/filter-step.js'), "  Ui.registerStep('filter', render);",
    '  window.filterBuild = { syncViewState: syncViewState, render: render, toWireDraft: toWireDraft };\n'), context, { filename: 'steps/filter-step.js' });
  window.JetApi = contractJetApi({});
  for (const action of ['filterPreview', 'queryFilterVoucherPage', 'queryFilterVoucherRowsPage'])
    window.JetApi[action] = payload => new Promise((resolve, reject) => requests.push({ action, payload, resolve, reject }));
  const store = window.JetStore, api = window.filterBuild;
  store.setProject(project);
  store.setAccountMappingState(accountMapping);
  if (glMapping || rdeFields.length || availableGlFields) store.setMappingCommitted('gl', { mapping: glMapping || {}, mode: 'signed', options: { rdeFields },
    ...(availableGlFields ? { availableGlFields } : {}) });
  if (accountCategory) store.setCategory(accountCategory);
  store.setSavedScenarios(saved);
  let version = -1;
  function draw() {
    const state = store.getState();
    if (state.contentVersion === version) return;
    version = state.contentVersion; api.syncViewState(state); api.render(dom.content, state);
  }
  store.subscribe(draw); draw();
  const $ = selector => dom.content.querySelector(selector);
  const $$ = selector => dom.content.querySelectorAll(selector);
  function click(selector) { const node = $(selector); assert.ok(node, 'missing ' + selector); node.fire('click'); }
  function choose(selector, value) { const node = $(selector); assert.ok(node, 'missing ' + selector); node.value = value; node.fire('change'); }
  // 分段切換（一組單選鈕）：選中值等於 value 的那一格，和審計員點那一格相同。
  function pick(selector, value) { const node = $$(selector).find(radio => radio.value === value); assert.ok(node, 'missing ' + selector + ' ' + value);
    node.checked = true; node.fire('change'); }
  const draft = () => store.getState().filter.draft;
  return { window, store, dom, requests, api, $, $$, click, choose, pick, draft, wire: () => plain(api.toWireDraft(draft())) };
}

// 欄位條件分頁。裁定 2026-11-03-05 把「自訂篩選條件」分頁改成「欄位條件」，裁定 2026-11-03-21 把程式裡的識別從 custom 改成 field；
// 原本這個函式叫 customPane、點 [data-condition-source="custom"]。本檔因此改的選擇器，第一次失敗都是 FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
const fieldPane = options => { const f = buildFixture(options); f.click('[data-condition-source="field"]'); return f; };
// 卡片以代號識別（Ui.FILTER_COMMON_CONDITIONS 的 key）。2026-10-29 拿掉卡片字母，原本以記字母的屬性找卡片；
// 本檔因此改的選擇器、測試名稱與期望值，第一次失敗都是 FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
const card = (f, key) => f.$('[data-common-card="' + key + '"]');
// 2026-11-05 常用篩選條件改成和預篩選分頁同一種清單列（裁定 2026-11-05-08）：card() 找到的是列右邊那一把鍵，鍵上只寫「加入」或「已加入」，
// 名稱與停用原因寫在同一列。原本名稱與原因寫在卡片裡，這裡改從那一列讀；本檔因此改的輔助與斷言，第一次失敗都是 FrontendMapping 收據 20261105-034631328-8ee93f9d3bd447cf96a047db4ce3c58d。
const cardRow = (f, key) => card(f, key).closest('.filter-check');
const cardName = (f, key) => cardRow(f, key).querySelector('.filter-check__label').textContent;
const cardNotes = (f, key) => cardRow(f, key).querySelectorAll('.filter-check__note');
// 停用的條件最後一段說明是停用原因。固定資產與費用、收入對方科目兩張卡原本另有固定的判定說明排在前面，2026-10-29 拿掉，只剩停用原因。
const cardReason = (f, key) => cardNotes(f, key).at(-1).textContent;
const rulesOf = f => plain(f.draft().groups.map(group => group.conditions.map(rule => rule.type)));

// 畫面上的文字與 title、aria-label 逐項列出（用換行隔開，避免相鄰節點的字黏在一起湊出代號）；skip 內的節點略過。
function screenTexts(node, skip) {
  const out = [];
  (function walk(current) {
    if (skip && current.matches && current.matches(skip)) return;
    if (current.tagName === 'JET-TEXT') out.push(current.textContent);
    for (const name of ['title', 'aria-label']) if (current.getAttribute && current.getAttribute(name)) out.push(current.getAttribute(name));
    for (const child of current.children || []) walk(child);
  })(node);
  return out;
}

// ------------------------------------------------------------------------------------------------
// 情境 10：常用篩選條件的卡片與範例
// ------------------------------------------------------------------------------------------------

// 取代 FilterAccountMappingEligibilityFrontendTests.RuleMetadata_DeclaresPerRuleAccountMappingRequirements 與
// 當時區分停用原因的 C# 方法裡卡片的部分。
// 裁定 2026-10-14-01 的逐字句：還沒匯入與匯入了但沒有任何分類同一句，缺收入或對方分類時寫出分類名稱
// （第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
// 測試名稱原本用當時的卡片字母稱呼這四張卡（2026-10-29 改名，原因見上面的 card()）。
const REVENUE = 'revenueDebitNearQuarterEnd', FIXED_ASSET = 'fixedAssetDebitExpenseCredit', COUNTERPART = 'revenueWithoutNormalCounterpart', MANUAL_REVENUE = 'manualRevenueEntry';
const MAPPING_CARD_TYPES = { [REVENUE]: 'revenueDebitNearQuarterEnd', [FIXED_ASSET]: 'specialAccountCategoryPair',
  [COUNTERPART]: 'revenueWithoutNormalCounterpart', [MANUAL_REVENUE]: 'manualRevenueEntry' };
const OTHER_CARDS = ['specificPreparer', 'specificKeywords', 'blankDescription', 'specificTrailingDigits', 'nonBusinessDay', 'preparerEqualsApprover'];
test('the four cards that need account mapping follow its content and explain whether mapping is missing or incomplete', () => {
  const states = [
    { name: 'not imported', mapping: null, enabled: [] },
    { name: 'imported without categories', mapping: { hasAnyCategory: false, hasRevenue: false, hasCounterpart: false }, enabled: [] },
    { name: 'only general categories', mapping: { hasAnyCategory: true, hasRevenue: false, hasCounterpart: true }, enabled: [FIXED_ASSET] },
    { name: 'revenue without counterpart', mapping: { hasAnyCategory: true, hasRevenue: true, hasCounterpart: false }, enabled: [REVENUE, FIXED_ASSET, MANUAL_REVENUE] },
    { name: 'revenue and counterpart', mapping: ALL_MAPPED, enabled: [REVENUE, FIXED_ASSET, COUNTERPART, MANUAL_REVENUE] }
  ];
  const notes = {};
  for (const state of states) {
    const f = buildFixture({ accountMapping: state.mapping });
    for (const key of Object.keys(MAPPING_CARD_TYPES)) {
      const button = card(f, key), on = state.enabled.includes(key);
      assert.equal(button.disabled, !on, state.name + ' ' + key + ' disabled');
      // 停用的鍵連到同一列寫著原因的那一段，和預篩選分頁相同；原本卡片另帶 aria-disabled（第一次失敗見上面的 cardRow()）。
      assert.equal(button.getAttribute('aria-describedby'), on ? null : 'filter-common-note-' + key, state.name + ' ' + key + ' described by its reason');
      if (!on) assert.ok(f.$('#filter-common-note-' + key) === cardNotes(f, key).at(-1), state.name + ' ' + key + ' reason sits on its row');
      assert.equal(!!button.closest('.filter-unavailable'), !on, state.name + ' ' + key + ' listed under unavailable cards');
      notes[state.name + key] = on ? '' : cardReason(f, key);
      if (!on) assert.ok(notes[state.name + key], state.name + ' ' + key + ' explains why');
      // 停用的卡按了不會加入條件。
      button.fire('click');
      assert.deepEqual(plain(f.draft().groups.flatMap(group => group.conditions.map(rule => rule.type))), on ? [MAPPING_CARD_TYPES[key]] : [], state.name + ' ' + key + ' click');
      if (on) f.click('[data-common-card="' + key + '"]');
    }
    for (const key of OTHER_CARDS) assert.equal(card(f, key).disabled, false, state.name + ' ' + key + ' never depends on mapping');
  }
  const noCategory = '科目配對中尚無任何科目設定分類，請先在第四步設定';
  const noRevenue = '科目配對中尚無歸類為「Revenue」的科目，請先在第四步設定';
  const noCounterpart = '科目配對中尚無歸類為「Receivables」「Cash」或「Receipt in advance」的科目，請先在第四步設定';
  // 2026-10-23 改寫這兩段，讓規則前置條件只有一個來源：停用原因改由前端照抄後端的規則需求表。後端對三張收入卡一直只看收入與對方分類
  //（改動前的 FilterScenario.cs 也是），原本前端在還沒匯入時另寫「尚無任何科目設定分類」，兩邊不同句；收入對方科目那張卡缺收入與對方分類時，
  // 後端照裁定 2026-10-13-28 的「分開說，各寫一句」寫兩句，前端原本只寫第一句。
  // 第一次失敗：FrontendMapping 收據 20261023-044331305-cc3be698efaa446eab8994f89154b247（季末前借記收入那張卡寫成缺收入分類的句子）。
  // 改後每張卡在「還沒匯入」與「匯入了但沒有任何分類」寫同一句，仍是上面那則裁定確認的情形。
  for (const key of Object.keys(MAPPING_CARD_TYPES)) assert.equal(notes['not imported' + key], notes['imported without categories' + key], key);
  assert.equal(notes['imported without categories' + FIXED_ASSET], noCategory);
  for (const key of [REVENUE, MANUAL_REVENUE]) assert.equal(notes['imported without categories' + key], noRevenue, key);
  assert.equal(notes['imported without categories' + COUNTERPART], noRevenue + '；' + noCounterpart, 'the counterpart card names both missing categories, one sentence each');
  for (const key of [REVENUE, COUNTERPART, MANUAL_REVENUE]) assert.equal(notes['only general categories' + key], noRevenue, key + ' names the missing revenue category');
  assert.equal(notes['revenue without counterpart' + COUNTERPART], noCounterpart, 'the counterpart card names the missing counterpart categories');
});

// 取代 FilterAccountMappingEligibilityFrontendTests.AvailableTypes_EvaluateTargetContentFacts_NotImportPresence：
// 樣態下拉「常用篩選條件」分組的收入選項看配對內容，不看有沒有匯入科目配對檔。
// 2026-10-29 這個分組改名「常用篩選條件」（原本的分組名稱帶小組名稱），測試名稱跟著改。
// 第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
test('the pattern menu offers revenue-only common conditions only when the mapping has revenue and counterpart content', () => {
  // 尾數改用欄位條件，不再是樣態型別，以預篩選卡檢查同一選單與收入資格。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  const commonOptions = mapping => {
    const f = buildFixture({ accountMapping: mapping });
    // 2026-10-16 預篩選條件改成條件總覽的分頁（裁定 2026-10-16-09 縮短套用預篩選條件的操作），改從分頁加入一條；
    // 第一次失敗：缺少 [data-check-category="prescreen"]。
    // 裁定 2026-10-30-30：條件列的下拉只列同一來源的條件，從預篩選加入的那一列不再列常用篩選條件，也不再分組。
    // 改從常用篩選條件加入「編製人員與核准人員相同」，看這一列下拉的選項。
    // 第一次失敗：FrontendMapping 收據 20261031-095121839-24d9cd622b3242889605b39228f69a2e。
    f.click('[data-common-card="preparerEqualsApprover"]');
    const select = f.$('.rule-row [data-pattern-subject]');
    assert.ok(select, 'the pattern row offers a pattern menu');
    assert.equal(select.querySelectorAll('optgroup').length, 0, 'the menu lists one source, so it has no groups');
    return select.querySelectorAll('option').map(option => option.getAttribute('value')).sort();
  };
  const always = ['preparerEqualsApprover'];
  assert.deepEqual(commonOptions(null), always);
  assert.deepEqual(commonOptions({ hasAnyCategory: true, hasRevenue: false, hasCounterpart: true }), always, 'imported but no revenue');
  assert.deepEqual(commonOptions({ hasAnyCategory: true, hasRevenue: true, hasCounterpart: false }),
    ['manualRevenueEntry', 'preparerEqualsApprover', 'revenueDebitNearQuarterEnd'], 'the revenue counterpart condition still needs a counterpart');
  assert.deepEqual(commonOptions(ALL_MAPPED),
    ['manualRevenueEntry', 'preparerEqualsApprover', 'revenueDebitNearQuarterEnd', 'revenueWithoutNormalCounterpart']);
});

// 取代當時區分停用原因的 C# 方法裡常用查核條件（2026-10-29 改稱「依審計目的」）的部分（裁定 2026-10-08-06 重新設計自訂篩選條件：
// 停用時按鈕帶 disabled，原因寫在該項下方並以 aria-describedby 連到按鈕；原因和固定資產與費用那張卡用同一個判斷）。
// 第一次失敗收據（原 C# 註解）：20260907-135727110、20260908-024320083。
// 2026-10-29 區塊改名、卡片拿掉字母，測試名稱跟著改；第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
// 裁定 2026-11-03-06 把常用查法改掛在欄位底下：「科目屬於分類」改成類別組的「科目分類」欄位，「借方與貸方分類組合」排在它底下，
// 「只看借方或貸方」改成同組的「借貸別」欄位。欄位本身不能用時，欄位的「加入」與底下的常用查法一起停用，原因寫在分頁最後那一段，
// 和 GL 欄位沒有配對時同一處（裁定 2026-10-22-05）。原本核對兩項常用查法的原因各寫在自己下方；改成核對科目分類的「加入」與
// 「借方與貸方分類組合」都在科目分類那一列、都連到分頁最後那一段，那一段和固定資產與費用那張卡同一句，按了都不加入；
// 借貸別不受科目配對影響。原本的測試名稱是 disabled purpose checks point to their reason, share the fixed-asset card reason and add nothing when pressed。
// 第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
test('without categories the account category field and its pair shortcut are disabled, point to the closing note with the fixed-asset card reason and add nothing', () => {
  for (const mapping of [null, { hasAnyCategory: false, hasRevenue: false, hasCounterpart: false }]) {
    const f = fieldPane({ accountMapping: mapping });
    const row = f.$('[data-add-field="accountCategory"]').closest('.filter-field');
    assert.ok(row.classList.contains('filter-field--unavailable'), 'the account category row is marked unavailable');
    for (const selector of ['[data-add-field="accountCategory"]', '[data-add-shortcut="categoryPair"]']) {
      const button = f.$(selector);
      assert.equal(button.disabled, true, selector);
      assert.equal(button.closest('.filter-field'), row, selector + ' sits on the account category row');
      const describedBy = button.getAttribute('aria-describedby');
      assert.ok(describedBy, selector + ' is described');
      const note = f.$('#' + describedBy);
      assert.ok(note, selector + ' reason exists');
      const picker = note.closest('.condition-picker--field');
      assert.ok(picker, selector + ' reason sits on the field tab');
      assert.equal(picker.children.at(-1), note, selector + ' reason is the last paragraph of the field tab');
      assert.equal(note.textContent, cardReason(f, FIXED_ASSET), selector + ' uses the same reason as the fixed-asset card');
      button.fire('click');
      assert.deepEqual(rulesOf(f), [], selector + ' adds nothing');
    }
    // 借貸別不受科目配對影響。
    const enabled = f.$('[data-add-field="drCr"]');
    assert.equal(enabled.disabled, false); assert.equal(enabled.getAttribute('aria-describedby'), null);
  }
  const f = fieldPane();
  assert.equal(f.$('[data-add-shortcut="categoryPair"]').disabled, false);
  assert.equal(f.$('#filter-check-note-categoryPair'), null);
  assert.equal(f.$('#filter-field-note'), null, 'with categories the field tab has no closing note');
  f.click('[data-add-shortcut="categoryPair"]');
  assert.deepEqual(rulesOf(f), [['specialAccountCategoryPair']]);
  f.click('[data-add-field="accountCategory"]');
  assert.deepEqual(rulesOf(f), [['specialAccountCategoryPair', 'accountSide']]);
});

// 取代 ScreenWordingReviewFrontendTests.K3_CashCounterpartExplanation_UsesExactApprovedText。
// 逐字出處：docs/history/specs/2026-10-06-kct-gl-requirements-plan.md「KCT C 的卡片說明」一節寫明收入對方科目那張卡（當時以字母稱呼）的說明逐字是這一句，
// 這一節的做法照建議實作，是裁定 2026-10-06-02。
// 第一次失敗收據（計畫檔那一節的實作紀錄）：20261006-030038300-b79b8f5c1edd404f880a2c56319d2a7c。
const K3_CASH_NOTE = '現金不算一般對方科目；要排除現金銷貨，改用預篩選條件『未預期借貸組合』。';
// 原本這句同時放在卡片與條件列，這裡斷言卡片、停用卡片與條件列都有它。條件列和卡片同在第五步的畫面上，
// 同一句出現兩次，裁定 2026-10-29-09「拿掉卡片上的」。卡片與停用卡片的斷言依預期失敗，
// 首敗收據 20261029-051153423-e931c5bb9d694a55bd6463da1ca0aa64。條件列逐字顯示這句的斷言不變。
// 同日拿掉卡片字母，測試名稱原本以字母稱呼這張卡，改用條件名稱（第一次失敗見上面的 card()）。
test('the revenue counterpart condition row explains cash with the approved sentence; the card no longer repeats it', () => {
  const f = buildFixture();
  assert.equal(cardNotes(f, COUNTERPART).length, 0, 'the card shows only its name');
  f.click('[data-common-card="' + COUNTERPART + '"]');
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row.querySelectorAll('.rule-explanation').some(node => node.textContent === K3_CASH_NOTE), 'condition row shows the approved sentence');
  // 停用時卡片只寫停用原因。
  const blocked = buildFixture({ accountMapping: { hasAnyCategory: true, hasRevenue: true, hasCounterpart: false } });
  const blockedNotes = cardNotes(blocked, COUNTERPART).map(node => node.textContent);
  assert.equal(blockedNotes.length, 1); assert.notEqual(blockedNotes[0], K3_CASH_NOTE);
});

// 同一次裁定也涵蓋固定資產與費用那張卡：先新增並配對分類的說明不寫在卡片上，加入後在條件列寫一次。這則是新增的測試，沒有第一次失敗。
// 同日拿掉卡片字母，測試名稱原本以字母稱呼這張卡，改用條件名稱（第一次失敗見上面的 card()）。
test('the fixed-asset card no longer repeats its setup note; the condition row still shows it', () => {
  const f = buildFixture();
  const note = f.window.JetUi.FILTER_COMMON_CONDITIONS.find(item => item.key === FIXED_ASSET).description;
  assert.ok(note, 'the fixed-asset card keeps a setup note for the condition row');
  assert.equal(cardNotes(f, FIXED_ASSET).length, 0, 'the card shows only its name');
  f.click('[data-common-card="' + FIXED_ASSET + '"]');
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row.querySelectorAll('.rule-explanation').some(node => node.textContent === note), 'condition row shows the fixed-asset note');
});

// 2026-10-30 結案前的獨立複審查到：拿掉卡片上的說明後，另一張卡的說明在條件列直接看得到，這張卡的說明卻只在「查找說明」裡，
// 上面那則測試分不出來。借貸組合的說明收進「查找說明」，是使用者說「下方多出來的註解顯得整體排版很雜亂」後的處理（裁定 2026-10-30-03 維持現狀），
// 固定資產與在建工程的說明也在其中，所以維持收在「查找說明」、只出現一次。這是新增的測試，鎖住現在的做法，沒有第一次失敗。
test('the fixed-asset setup note appears once in the condition row, inside the lookup help folded for account pairs', () => {
  const f = buildFixture();
  const note = f.window.JetUi.FILTER_COMMON_CONDITIONS.find(item => item.key === FIXED_ASSET).description;
  f.click('[data-common-card="' + FIXED_ASSET + '"]');
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  const shown = row.querySelectorAll('.rule-explanation').filter(node => node.textContent === note);
  assert.equal(shown.length, 1, 'the note appears once in the condition row');
  assert.ok(shown[0].closest('details[data-rule-help]'), 'the note sits in the lookup help');
});

// 裁定 2026-11-01-06：季末那張卡拿掉天數怎麼算的說明，卡片照原本只有名稱，加入後的條件列也不另寫這句。這是新增的測試，改程式前失敗：FrontendMapping 收據 20261101-062903506-eed7a916dd2a499dbedd83ced776f260。
test('the quarter-end revenue card has no explanation on the card or in its condition row', () => {
  const f = buildFixture();
  assert.equal(f.window.JetUi.FILTER_COMMON_CONDITIONS.find(item => item.key === REVENUE).description, undefined);
  assert.equal(cardNotes(f, REVENUE).length, 0, 'the card shows only its name');
  f.click('[data-common-card="' + REVENUE + '"]');
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row, 'the card adds a condition row');
  assert.doesNotMatch(row.textContent, /季底往前算|從查核期末往前算/);
});

// 原本取代 ScreenWordingReviewFrontendTests 裡核對卡片字母的方法：分頁與標題逐字是裁定 2026-10-08-06 定的名稱
//（原話見 docs/history/specs/2026-10-06-kct-gl-requirements-plan.md「驗收發現與裁定（2026-10-08）」），每張卡前面有一個對讀屏軟體隱藏的字母。
// 裁定 2026-10-29-10 與 2026-10-29-11 合併成「常用篩選條件」分頁、拿掉卡片字母，分頁上半「單項條件」是十張卡片
//（docs/history/specs/2026-10-22-prescreen-tech-debt-deferred-items-plan.md「2026-10-29 合併常用篩選條件」）。
// 原本核對分頁與標題文字、十個字母依序出現且對讀屏軟體隱藏；改成核對新的分頁文字、區塊標題、十張卡依序出現、
// 卡片只寫條件名稱且沒有字母。卡片名稱逐字照 ui-core.js 的 FILTER_COMMON_CONDITIONS。
// 第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2（找不到原本的分頁）。
const COMMON_CARDS = [
  ['revenueDebitNearQuarterEnd', '在該季度前 X 天借記收入的會計分錄'],
  ['fixedAssetDebitExpenseCredit', '借記固定資產(PPE，不含在建)且貸記費用之分錄'],
  ['revenueWithoutNormalCounterpart', '貸方為收入但借方非一般對方科目之分錄'],
  ['manualRevenueEntry', '收入之人工分錄'],
  ['specificPreparer', '特定人員(財務長/執行長/高階主管等)建立之分錄'],
  ['specificKeywords', '特定摘要(如迴轉、調整等)'],
  ['blankDescription', '空白摘要'],
  ['specificTrailingDigits', '特定尾數(如 999999/000000 結尾)'],
  ['nonBusinessDay', '非營業日之分錄'],
  ['preparerEqualsApprover', '編製人員與核准人員相同']
];
// 裁定 2026-11-03-20 拿掉常用篩選條件分頁的小標「單項條件」，分頁只放十張卡片（裁定 2026-11-03-05）。原本核對卡片區塊的標題是「單項條件」，
// 改成核對分頁裡沒有區塊標題，也看不到「單項條件」，十張卡照樣依序列出。
// 原本的測試名稱是 the common tab reads 常用篩選條件, lists the ten cards in order under 單項條件 and shows no card letters。
// 第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
test('the common tab reads 常用篩選條件, lists the ten cards in order without a subtitle and shows no card letters', () => {
  const f = buildFixture();
  assert.equal(f.$('[data-condition-source="common"]').textContent, '常用篩選條件');
  assert.equal(f.$('[data-condition-pane="common"]').hidden, false, 'the common tab is the first one shown');
  assert.equal(f.$('[data-condition-pane="common"] .filter-source-block__title'), null, 'the tab has no subtitle');
  assert.doesNotMatch(screenTexts(f.$('[data-condition-pane="common"]')).join('\n'), /單項條件/);
  const cards = f.$$('[data-condition-pane="common"] [data-common-card]');
  // 名稱寫在列上，鍵上寫「加入」；一列只有名稱和鍵，沒有字母（第一次失敗見上面的 cardRow()）。
  assert.deepEqual(cards.map(node => [node.getAttribute('data-common-card'), cardName(f, node.getAttribute('data-common-card'))]), COMMON_CARDS);
  for (const node of cards) {
    const key = node.getAttribute('data-common-card'), name = COMMON_CARDS.find(item => item[0] === key)[1];
    assert.equal(node.textContent, '加入', key);
    assert.equal(node.getAttribute('aria-label'), '加入「' + name + '」', key);
    assert.equal(cardRow(f, key).textContent, name + '加入', key + ' row shows only the name and the key, no letter');
  }
  assert.equal(f.$('.condition-picker__title'), null, 'the old heading is gone');
  assert.doesNotMatch(screenTexts(f.$('[data-condition-pane="common"]')).join('\n'), /A 到 J/);
});

// 裁定 2026-11-05-08：條件總覽三個分頁用同一種列，名稱在左、右邊一把鍵。常用篩選條件與預篩選的條件還不在作用中的組時寫「加入」，
// 已在這一組時鍵往下凹、寫「已加入」，再按一下就移出。欄位條件分頁的鍵永遠是「加入」，加入後再按一下就再加一條（裁定 2026-11-05-12）。
// 常用篩選條件帶入的條件不算預篩選分頁加入的。換作用中的組時，兩個分頁換成那一組的狀態，只換掉狀態變了的列，換上的鍵照樣能按。
// 「也在其他組」寫在名稱下一行，不放進名稱那一格，加入時浮起來的標籤只有名稱。這是新加的測試。
// 「加入到第 N 組」是組號的分段切換（裁定 2026-11-05-16），換組用 pick 選組號。
test('common and prescreen keys show 加入 or 已加入 for the active group, field keys always add, and 也在其他組 stays out of the name', () => {
  const f = buildFixture();
  const state = node => [node.textContent, node.getAttribute('aria-pressed'), node.getAttribute('aria-label'), node.classList.contains('condition-key--added'), node.classList.contains('btn--add')];

  // 常用篩選條件：加入後寫「已加入」、沒有加號，報讀名稱改成移出；再按一下移出。
  assert.deepEqual(state(card(f, 'blankDescription')), ['加入', 'false', '加入「空白摘要」', false, true]);
  f.click('[data-common-card="blankDescription"]');
  assert.deepEqual(state(card(f, 'blankDescription')), ['已加入', 'true', '移出「空白摘要」', true, false]);
  f.click('[data-common-card="blankDescription"]');
  assert.deepEqual(rulesOf(f), [[]]);
  assert.deepEqual(state(card(f, 'blankDescription')), ['加入', 'false', '加入「空白摘要」', false, true]);

  // 預篩選：同一把鍵加入與移出。
  const prescreen = () => f.$('[data-add-prescreen="suspiciousKeywords"]');
  assert.deepEqual(state(prescreen()), ['加入', 'false', '加入「摘要特定描述」', false, true]);
  f.click('[data-add-prescreen="suspiciousKeywords"]');
  assert.deepEqual(rulesOf(f), [['prescreen']]);
  assert.deepEqual(state(prescreen()), ['已加入', 'true', '移出「摘要特定描述」', true, false]);
  f.click('[data-add-prescreen="suspiciousKeywords"]');
  assert.deepEqual(rulesOf(f), [[]]);

  // 欄位條件：加入後那一列照樣寫「加入」，再按一下就再加一條；常用查法也一樣。
  f.click('[data-condition-source="field"]');
  const fieldKey = id => f.$('[data-add-field="' + id + '"]').closest('.filter-field').querySelector('.condition-key');
  f.click('[data-add-field="voucherNumber"]');
  assert.deepEqual([fieldKey('voucherNumber').textContent, fieldKey('voucherNumber').classList.contains('condition-key--added'), fieldKey('voucherNumber').getAttribute('aria-pressed')],
    ['加入', false, null], 'a field key has no added state');
  f.click('[data-add-field="voucherNumber"]');
  assert.deepEqual(rulesOf(f), [['fieldValue', 'fieldValue']], 'pressing it again adds another condition');
  f.click('[data-add-shortcut="rareAccounts"]');
  assert.equal(fieldKey('accountNumber').textContent, '加入');
  assert.equal(f.dom.content.querySelectorAll('.filter-field .condition-key--added, [data-remove-field]').length, 0);

  // 換作用中的組：新的一組什麼都沒有，回到第一組時預篩選又是「已加入」；狀態沒變的列不換，換上的鍵照樣能按。
  f.click('[data-add-prescreen="weekendPosting"]');
  f.click('[data-action="add-set"]');
  assert.equal(f.$('[data-add-prescreen="weekendPosting"]').textContent, '加入', 'the new empty group has nothing added');
  const untouched = f.$('[data-add-prescreen="suspiciousKeywords"]');
  f.pick('[data-condition-target]', '0');
  assert.equal(f.$('[data-add-prescreen="weekendPosting"]').textContent, '已加入', 'switching back to the first group shows its condition as added');
  assert.ok(f.$('[data-add-prescreen="suspiciousKeywords"]') === untouched, 'a row whose state did not change is kept');
  f.click('[data-add-prescreen="weekendPosting"]');
  assert.deepEqual(rulesOf(f), [['fieldValue', 'fieldValue', 'entityFrequency'], []], 'the swapped key still works');

  // 空白摘要在第 1 組，換到第 2 組時寫「也在其他組」，但不在名稱那一格裡。
  f.click('[data-condition-source="common"]');
  f.click('[data-common-card="blankDescription"]');
  f.pick('[data-condition-target]', '1');
  const row = cardRow(f, 'blankDescription');
  assert.equal(row.querySelector('.filter-check__elsewhere').textContent, '也在其他組');
  assert.equal(cardName(f, 'blankDescription'), '空白摘要', 'the name the arrival label reads is only the name');
  assert.equal(row.querySelector('.filter-check__label .filter-check__elsewhere'), null);
});

// 裁定 2026-10-07-06：動機每條自訂條件多一行「自訂：」；當時名稱用卡片字母串接，同組有自訂條件時加「+自訂」
//（第一次失敗收據，原 C# 註解：20261007-035823939-41943206b5f747f1b7fb8dfe79f987d7）。
// 裁定 2026-10-29-11 把名稱改用和自訂條件相同的寫法：依加入順序列出前兩項條件，卡片帶入的用卡片名稱，超過兩項加上總數；
// 動機每行不再以字母開頭（docs/history/specs/2026-10-22-prescreen-tech-debt-deferred-items-plan.md「2026-10-29 合併常用篩選條件」）。
// 原本名稱期望值是字母串接，改成新寫法的寫死字串；第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
test('card names name the draft, a custom condition in the same group is named after it and the rationale adds a 自訂 line', () => {
  const f = buildFixture();
  f.click('[data-common-card="blankDescription"]'); f.click('[data-common-card="specificTrailingDigits"]');
  assert.equal(f.draft().name, '空白摘要、特定尾數(如 999999/000000 結尾)');
  f.click('[data-common-card="specificTrailingDigits"]');
  assert.equal(f.draft().name, '空白摘要');
  // 分頁識別改成 field（裁定 2026-11-03-21），第一次失敗見本檔的 fieldPane()。
  f.click('[data-condition-source="field"]'); f.click('[data-add-field="description"]');
  assert.equal(f.draft().name, '空白摘要、傳票摘要 包含任一文字「」');
  const lines = f.draft().rationale.split('\n');
  assert.equal(lines.length, 2);
  assert.equal(lines[0], '空白摘要');
  assert.match(lines[1], /^自訂：/);
  f.click('[data-action="add-set"]'); f.click('[data-add-field="voucherNumber"]');
  assert.equal(f.draft().name, '空白摘要、傳票摘要 包含任一文字「」等3項條件');
});

// 範例全部可用的案件狀態：查核截止日、期末財報準備日、核准人員與建立人員都已配對，有一個文字型攸關資料元素欄位。
const EXAMPLE_READY = { glMapping: { approvedBy: 'Checker', createdBy: 'Maker' },
  rdeFields: [{ fieldId: 'rde.dept', sourceColumn: 'Dept', label: '部門', valueType: 'text' }] };
const EXAMPLE_KEYS = ['cashDebitNonCashCredit', 'cashCreditNonCashDebit', 'nonBusinessDayExcludingDates', 'periodEndManual', 'blankDescriptionOrApprover',
  'preparerLargeAmount', 'weekendManualDescription', 'manualDepartment', 'accountNumberPair', 'accountPairLargeAmount', 'preparationManualHoliday'];
// 裁定 2026-10-08-06 重新設計自訂篩選條件時的原話（同上「驗收發現與裁定（2026-10-08）」）：
// 「名稱 `舊表 X:XXX` 可以改為其他名稱，因為 "舊表" 聽起來非常不恰當且不專業」，以及「一下子有範例1範例2的，一下子註解顯示什麼 "舊表P+S"」。
const CATALOGUE_CODES = /舊表|A 到 U|A–U|範例 ?\d|工作簿範例|[A-U](\+[A-U])+/;

// 原本取代 ScreenWordingReviewFrontendTests 裡核對卡片字母的方法的自訂分頁與範例部分，以及 FilterTemplatesFrontendTests.FilterStep_OffersTemplatesAndTheCombinationCard。
// 自訂分頁沒有收合鈕的第一次失敗收據（原 C# 註解）：20261012-060708654-d3fdcf4d785843d3b09ab6fadd009c5c。
// 每個範例都在新的案件畫面套用（空白草稿直接套用，不會跳出「還沒儲存」的詢問，那屬於 frontend-journey-filter-saved.test.cjs 的儲存流程）。
// 裁定 2026-10-29-10 拿掉「從範例開始」按鈕，範例一直列在「常用篩選條件」分頁下半的「情境範例」。原本斷言清單一開始收起、
// 按鈕展開後才列出、套用後收起；改成斷言清單一開始就在常用篩選條件分頁列出、沒有展開按鈕、套用後清單仍在。
// 原本略過的是放卡片字母的分頁，現在範例和卡片同一頁且卡片不再有字母，所以整個條件總覽都不能出現目錄代號。
// 第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2（examples start closed）。
// 裁定 2026-11-03-05 把範例移到右側條件組合上方的「從情境範例開始」，裁定 2026-11-03-19 定它預設收起、按標題打開；
// 裁定 2026-11-03-08 把「借方與貸方科目編號組合」移到情境範例，範例從十個變成十一個。原本斷言清單在常用篩選條件分頁、
// 區塊標題是「情境範例」、套用後仍在那一頁；改成斷言清單在托盤裡、托盤標題是「從情境範例開始」、一開始收起、排在條件組合上方、
// 任何條件分頁都沒有範例，套用後清單仍在托盤裡。範例代號清單多了 accountNumberPair，自訂分頁改點欄位條件分頁。
// 托盤和條件總覽同在篩選工作區，整個工作區照樣不能出現目錄代號。第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
test('every example applies under its own name and the condition screens never show catalogue codes or card letters', () => {
  const offered = buildFixture(EXAMPLE_READY);
  const list = offered.$('[data-example-list]');
  const tray = list.closest('.filter-template-tray');
  assert.ok(tray, 'examples sit in the tray');
  assert.equal(tray.querySelector('.filter-template-tray__title').textContent, '從情境範例開始');
  assert.equal(tray.getAttribute('open'), null, 'the tray starts closed');
  assert.equal(list.closest('[data-condition-pane]'), null, 'examples are not on a condition tab');
  assert.equal(offered.$('[data-condition-pane] [data-template-key]'), null, 'no condition tab lists an example');
  const column = tray.parentElement.children;
  assert.ok(column.indexOf(tray) >= 0 && column.indexOf(tray) < column.indexOf(offered.$('.scenario-builder')), 'the tray sits above the composition');
  assert.equal(offered.$('[data-action="toggle-examples"]'), null, 'there is no separate button to open the examples');
  assert.deepEqual(offered.$$('[data-template-key]').map(button => button.getAttribute('data-template-key')), EXAMPLE_KEYS);
  assert.doesNotMatch(screenTexts(offered.$('[data-filter-pane="filter"]')).join('\n'), CATALOGUE_CODES);
  // 欄位條件分頁沒有收合鈕，也沒有退場的排除區或待判定說法。
  offered.click('[data-condition-source="field"]');
  assert.equal(offered.$('[data-action="toggle-custom-picker"]'), null);
  assert.doesNotMatch(screenTexts(offered.$('[data-filter-pane="filter"]')).join('\n'), /排除區域|待判定/);
  assert.doesNotMatch(screenTexts(offered.$('[data-filter-pane="filter"]')).join('\n'), CATALOGUE_CODES);

  for (const key of EXAMPLE_KEYS) {
    const f = buildFixture(EXAMPLE_READY);
    const button = f.$('[data-template-key="' + key + '"]');
    assert.equal(button.disabled, false, key + ' is available');
    const label = button.textContent;
    button.fire('click');
    assert.equal(f.draft().name, label, key + ' is named after the example');
    assert.ok(f.draft().groups.some(group => group.conditions.length), key + ' brings conditions');
    assert.ok(f.$('.filter-template-tray [data-example-list]'), key + ' leaves the list in the tray');
    const texts = screenTexts(f.$('[data-filter-pane="filter"]'));
    assert.doesNotMatch(texts.join('\n'), CATALOGUE_CODES, key);
    assert.doesNotMatch(f.draft().name + '\n' + f.draft().rationale, CATALOGUE_CODES, key);
    // 條件組裡沒有常用篩選條件的卡片、欄位按鈕或欄位選單裡的常用查法。
    assert.equal(f.dom.content.querySelectorAll('.scenario-set [data-common-card], .scenario-set [data-add-field], .scenario-set [data-add-shortcut]').length, 0, key);
    if (key === 'manualDepartment') {
      // 只有一個文字型攸關資料元素欄位時直接帶入它。
      assert.deepEqual(plain(f.draft().groups[0].conditions.map(rule => [rule.type, rule.fieldId ?? null])), [['manualAuto', null], ['fieldValue', 'rde.dept']]);
    }
  }

  // 已有同名情境時，範例名稱後面加數字，不加任何代號。
  const named = buildFixture({ ...EXAMPLE_READY, saved: [{ name: '借現金、貸非現金', rationale: 'r', groups: [] }] });
  named.click('[data-template-key="cashDebitNonCashCredit"]');
  assert.equal(named.draft().name, '借現金、貸非現金 2');
});

// ------------------------------------------------------------------------------------------------
// 情境 11：自訂條件與條件組
// ------------------------------------------------------------------------------------------------

// 取代 FilterBuilderFrontendTests.GroupHeader_ReadsAsOneSentence 與 FilterAstFrontendContractTests.GroupMatchScope_OffersClosedOptionsAndAccessibleSameVoucherGuidance。
// 「AND（全部符合）」「OR（任一符合）」逐字出處：docs/history/specs/2026-10-06-kct-gl-requirements-plan.md
// 「使用者對兩項待決事項的選擇（2026-10-11）」，使用者選了「統一用 AND、OR」（裁定 2026-10-11-04），選項說明原文是「下拉照舊，左側欄位與摘要改寫 AND、OR。」
// 比對範圍的選項名稱、主要條件與佐證的標示沒有使用者逐字裁定，只比對意思；選項值是送出的契約，精確比對。
// 第一次失敗收據（原 C# 註解）：20260907-135727110。
// 「同一傳票」併入整張傳票條件後，停用比對範圍與主要條件；保留 AND、OR、可讀名稱及送出結構檢查。
// 首次失敗：20261014-135637069-066eb7e965824f89bf2b8671156595f1。
// 裁定 2026-10-30-32 把連接簡化成只寫 AND 與 OR，下拉改成兩格的分段切換（一組單選鈕），選項字不再帶「全部符合」「任一符合」。
// 第一次失敗：FrontendMapping 收據 20261031-095121839-24d9cd622b3242889605b39228f69a2e。
test('two conditions offer AND or OR and explicit voucher conditions replace the scope menu', () => {
  const f = fieldPane();
  f.click('[data-add-field="description"]');
  assert.equal(f.$('[data-set-combinator]'), null, 'one condition has no combination choice');
  f.click('[data-add-field="voucherNumber"]');
  const options = () => f.$$('[data-set-combinator]');
  const chosen = () => options().filter(radio => radio.checked).map(radio => radio.value);
  assert.deepEqual(options().map(radio => radio.closest('label').textContent), ['AND', 'OR']);
  assert.deepEqual(options().map(radio => radio.getAttribute('value')), ['AND', 'OR']);
  assert.deepEqual(options().map(radio => radio.getAttribute('type')), ['radio', 'radio']);
  const toggle = options()[0].closest('.join-toggle');
  assert.equal(toggle.getAttribute('role'), 'radiogroup');
  assert.ok(toggle.getAttribute('aria-label'), 'the combination has an accessible name');
  assert.equal(f.$('[data-group-bind="matchScope"]'), null);
  assert.equal(f.$('.filter-group-menu'), null);

  f.pick('[data-set-combinator]', 'OR');
  assert.deepEqual(plain(f.draft().groups[0].conditions.map(rule => rule.join)), ['OR', 'OR']);
  assert.deepEqual(chosen(), ['OR']);

  f.pick('[data-set-combinator]', 'AND');
  assert.equal(f.draft().groups[0].matchScope, 'row');
  assert.deepEqual(plain(f.draft().groups[0].conditions.map(rule => rule.join)), ['AND', 'AND']);
  assert.ok(options().every(radio => !radio.disabled));
  assert.deepEqual(chosen(), ['AND']);
  assert.equal(options().length, 2);
  assert.equal(f.$('[data-action="make-primary"]'), null);
  assert.deepEqual(f.wire().groups.map(group => [group.matchScope, group.conditions.map(rule => rule.join)]), [['row', ['AND', 'AND']]]);
  f.click('[data-add-compound="voucher"]');
  assert.equal(f.draft().groups[0].conditions[2].quantifier, 'any');
  assert.match(f.$('.rule-row--compound').textContent, /同一筆分錄.*整張傳票/);
});

// 裁定 2026-11-01-07：組內與組間連接的分段切換前面不寫「組內條件」「組與組之間」，只留 AND、OR；
// 連接的是什麼由版面呈現，螢幕報讀仍讀得到各自的名稱。這是新增的測試，改程式前失敗：FrontendMapping 收據 20261101-062903506-eed7a916dd2a499dbedd83ced776f260。
// 裁定 2026-11-05-16 把組內的切換從組標頭移到兩條條件之間的分隔線上，所以組內的切換改在第一條分隔線 .rule-join--switch 找，
// 原本在組標頭的 .filter-group-settings；斷言不變。第一次失敗：FrontendMapping 收據 20261105-053106063-57492b5a836344aa879416abd4af9423。
test('the join toggles inside and between groups show only AND and OR, and keep their accessible names', () => {
  const f = fieldPane();
  f.click('[data-add-field="description"]'); f.click('[data-add-field="voucherNumber"]');
  f.click('[data-action="add-set"]'); f.click('[data-add-field="voucherNumber"]');
  const inside = f.$('[data-set-combinator]').closest('.join-toggle');
  const between = f.$('[data-set-join]').closest('.join-toggle');
  for (const [toggle, holder] of [[inside, f.$('.rule-join--switch')], [between, f.$('.set-rail')]]) {
    assert.equal(holder.textContent.replace(/\s+/g, ''), 'ANDOR', 'only AND and OR are written next to the toggle');
    assert.ok(toggle.getAttribute('aria-label'), 'the toggle keeps an accessible name');
  }
});

// 裁定 2026-11-03-26：連接條件的 AND、OR 兩格一樣寬；只靠左右留白撐開時 AND 比 OR 寬，切換鍵看起來一邊長一邊短。
// 這是新增的測試：組內與組間的切換鍵都只有兩格，由同一條規則排成等寬的兩欄。
test('the AND and OR halves of every join toggle are equally wide', () => {
  assert.match(readWwwroot('css/app.css'), /\n\.join-toggle \{\n  display: inline-grid;\n  grid-template-columns: 1fr 1fr;\n/);
  const f = fieldPane();
  f.click('[data-add-field="description"]'); f.click('[data-add-field="voucherNumber"]');
  f.click('[data-action="add-set"]'); f.click('[data-add-field="voucherNumber"]');
  for (const selector of ['[data-set-combinator]', '[data-set-join]']) {
    const toggle = f.$(selector).closest('.join-toggle');
    assert.deepEqual(toggle.querySelectorAll('.join-toggle__option').map(option => option.textContent.trim()), ['AND', 'OR'], selector);
  }
});

// 取代 FilterBuilderFrontendTests.EveryRuleRow_IsOneSentenceWithoutATypeDropdown 與 FilterWorkspaceFrontendTests.SimpleConditions_HaveVisibleRemovalAndDiscoverableGroups。
// 移除鈕的名稱沒有使用者逐字裁定，只比對有「移除」。第一次失敗收據（原 C# 註解）：20260908-064216425。
// 裁定 2026-11-03-06 把常用查法改掛在欄位底下：「科目屬於分類」改成類別組的「科目分類」欄位，只加入一條科目分類條件，借貸方向改從同組的「借貸別」加入；
// 「人工分錄」改成「人工/自動分錄」欄位（裁定 2026-11-03-22）；「金額尾數連續多個 0」改成一條條件（裁定 2026-11-03-07）。
// 原本兩組各有一個子條件組，共 8 列，移除第 1 組的子條件組；改成每個入口各加一列，共 5 列，移除第 1 組的借貸別那一列，
// 另核對排在它後面的科目分類條件不變。原本核對子條件組裡明示借貸方向的那一列、尾數子條件用欄位的比較方式；
// 改核對借貸別那一列與尾數連續多個 0 那一列。第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
test('each family adds a one-sentence row without a type menu, and removing one row leaves every other condition', () => {
  const f = fieldPane();
  f.click('[data-add-field="description"]');
  f.click('[data-add-field="drCr"]'); f.click('[data-add-field="accountCategory"]');
  assert.match(f.$('[data-action="add-set"]').textContent, /條件組/);
  f.click('[data-action="add-set"]');
  f.click('[data-add-shortcut="trailingZeros"]');
  f.click('[data-add-field="manualAuto"]');
  assert.deepEqual(rulesOf(f), [['fieldValue', 'drCrOnly', 'accountSide'], ['fieldValue', 'manualAuto']]);
  const rows = f.$$('.rule-row');
  assert.equal(rows.length, 5);
  for (const row of rows) {
    assert.equal(row.querySelector('[data-rule-bind="type"]'), null, 'no type menu');
    assert.equal(row.querySelector('.rule-row__type'), null); assert.equal(row.querySelector('.rule-row__family'), null);
    const remove = row.querySelector(row.classList.contains('rule-row--compound') ? '[data-compound-remove]' : '[data-action="remove-rule"]');
    assert.ok(remove, 'every row can be removed'); assert.match(remove.textContent, /移除/);
  }
  const row = (gi, ri) => f.$('.rule-row[data-gi="' + gi + '"][data-ri="' + ri + '"]');
  assert.ok(row(0, 0).querySelector('[data-value-kind]'), 'a field row chooses how to compare');
  assert.equal(row(0, 0).querySelector('[data-value-key="field"]'), null, 'the field is fixed on the row, not a second menu');
  assert.ok(row(0, 1).querySelector('[data-rule-bind="drCr"]'), 'the debit or credit row chooses debit or credit');
  assert.equal(row(0, 2).querySelector('[data-account-subject]'), null, 'classification has no hidden direction');
  assert.ok(row(1, 0).querySelector('[data-value-kind]'), 'the trailing-zero row uses the shared field comparison');
  assert.equal(f.$('.filter-draft-note'), null);

  const second = plain(f.draft().groups[1]), first = plain(f.draft().groups[0].conditions[0]), third = plain(f.draft().groups[0].conditions[2]);
  row(0, 1).querySelector('[data-action="remove-rule"]').fire('click');
  assert.deepEqual(rulesOf(f), [['fieldValue', 'accountSide'], ['fieldValue', 'manualAuto']]);
  assert.deepEqual(plain(f.draft().groups[0].conditions[0]), first);
  assert.deepEqual(plain(f.draft().groups[0].conditions[1]), third);
  assert.deepEqual(plain(f.draft().groups[1]), second);
});

// 取代 FilterWorkspaceFrontendTests.GroupEditor_DoesNotRelocateTheConditionCatalog。
test('"add conditions to" moves the target group and the groups never embed the condition catalogue', () => {
  const f = buildFixture();
  // 卡片改用代號識別（2026-10-29 拿掉卡片字母，第一次失敗見本檔的 card()）。
  f.click('[data-common-card="blankDescription"]');
  assert.equal(f.$('[data-condition-target]'), null, 'one group needs no target choice');
  // 空白摘要與特定尾數兩張卡都以欄位條件加入，分組與隔離斷言不變。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  assert.match(f.$('.rule-row[data-gi="0"][data-ri="0"] .filter-rule-heading').textContent, /傳票摘要/);
  f.click('[data-action="add-set"]');
  // 裁定 2026-11-05-16 把「加入到」的下拉選單改成條件總覽標題同一列的組號分段切換：選項從 option 改成單選鈕，
  // 目前的組改看選中的單選鈕，換組改用 pick；斷言的意思不變。標題同一列的斷言是新加的。第一次失敗：FrontendMapping 收據 20261105-053106063-57492b5a836344aa879416abd4af9423。
  const target = f.$('[data-condition-target]');
  assert.ok(target.closest('.filter-entry'), 'the target choice sits with the condition catalogue');
  assert.ok(target.closest('.filter-entry-heading'), 'the target choice sits on the catalogue heading row');
  assert.equal(target.closest('.scenario-set'), null);
  assert.deepEqual(f.$$('[data-condition-target]').map(radio => radio.getAttribute('value')), ['0', '1']);
  assert.deepEqual(f.$$('[data-condition-target]').filter(radio => radio.checked).map(radio => radio.value), ['1'], 'the new group is the target');
  assert.doesNotMatch(screenTexts(f.$('[data-filter-pane="filter"]')).join('\n'), /目前加入目標/);
  f.pick('[data-condition-target]', '0');
  f.click('[data-common-card="specificTrailingDigits"]');
  assert.deepEqual(rulesOf(f), [['fieldValue', 'fieldValue'], []]);
  // 分頁識別改成 field（裁定 2026-11-03-21），第一次失敗見本檔的 fieldPane()。
  f.click('[data-condition-source="field"]'); f.click('[data-add-field="voucherNumber"]');
  assert.deepEqual(rulesOf(f), [['fieldValue', 'fieldValue', 'fieldValue'], []]);
  assert.equal(f.dom.content.querySelectorAll('.scenario-set [data-common-card], .scenario-set [data-add-field], .scenario-set [data-add-shortcut], .scenario-set [data-template-key]').length, 0);
  assert.equal(f.$('[data-inline-picker]'), null); assert.equal(f.$('[data-pick-for-group]'), null);
  assert.equal(f.dom.content.querySelectorAll('.scenario-set [data-select-group]').length, 0);
});

// 裁定 2026-11-05-16：AND、OR 放在被連接的兩者之間的分隔線上。組內第一條分隔線是這一組的分段切換，後面的只寫同一個字；
// 兩組之間與子條件之間也是分隔線上的切換。組標頭不再放切換，條件列也不再帶只給連接詞用的 data-join。
// 多組時「加入到第 1、2 組」和條件總覽的標題同一列，組號是同一種分段切換，點右側的組時選中的組號跟著換。這是新加的測試。
test('AND and OR sit on the dividers between what they join, and the group numbers choose where conditions go', () => {
  const f = fieldPane();
  f.click('[data-add-field="description"]');
  assert.equal(f.$('.rule-join'), null, 'one condition has no divider');
  f.click('[data-add-field="voucherNumber"]'); f.click('[data-add-field="voucherNumber"]');
  const list = f.$('.scenario-set .scenario-flat__list');
  const kinds = () => list.children.map(node => node.classList.contains('rule-join--switch') ? 'switch' : node.classList.contains('rule-join') ? node.textContent : 'condition');
  assert.deepEqual(kinds(), ['condition', 'switch', 'condition', 'AND', 'condition']);
  assert.equal(f.$('.filter-group-settings'), null, 'the group heading no longer holds the toggle');
  assert.equal(f.dom.content.querySelectorAll('.rule-row[data-join]').length, 0);
  f.pick('[data-set-combinator]', 'OR');
  assert.deepEqual(plain(f.draft().groups[0].conditions.map(rule => rule.join)), ['OR', 'OR', 'OR']);
  assert.equal(f.$('.rule-join__word').textContent, 'OR', 'the later dividers repeat the group setting');

  f.click('[data-add-compound="group"]');
  const compound = f.$('.rule-row--compound');
  for (const kind of ['field:glDate', 'type:drCrOnly']) {
    f.choose('.rule-row--compound > label > [data-child-kind]', kind); f.click('.rule-row--compound > [data-child-add]');
  }
  assert.ok(compound, 'the sub-condition group was added');
  assert.deepEqual(f.$$('.rule-row--compound > .rule-join--child [data-child-join]').map(radio => radio.value), ['AND', 'OR']);
  f.pick('.rule-row--compound > .rule-join--child [data-child-join]', 'OR');
  assert.equal(f.draft().groups[0].conditions[3].conditions[1].join, 'OR', 'the divider between sub-conditions sets that join');

  f.click('[data-action="add-set"]'); f.click('[data-add-field="voucherNumber"]');
  assert.ok(f.$('.set-rail [data-set-join]'), 'the groups are joined on the divider between them');
  const heading = f.$('.filter-entry-heading');
  const numbers = () => heading.querySelectorAll('[data-condition-target]').map(radio => [radio.value, radio.getAttribute('aria-label'), radio.checked]);
  assert.deepEqual(numbers(), [['0', '第 1 組', false], ['1', '第 2 組', true]]);
  f.click('[data-group-index="0"]');
  assert.deepEqual(numbers().map(item => item[2]), [true, false], 'choosing a group on the right moves the chosen number');
});

// 取代 FilterCategorySelectFrontendTests 的兩個方法（2026-10-02 分類多選的參數少一個，欄位標題顯示成內部鍵名，
// 「整張傳票不得有這些貸方分類」消失；收據 20261002-122456569）。分類欄位的標題沒有使用者逐字裁定，只比對意思。
test('the account pair card switches between five lookup modes and every category legend stays in plain words', () => {
  // 「借方與貸方分類組合」改掛在科目分類欄位底下，不再先展開分類（裁定 2026-11-03-06）。第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
  const f = fieldPane();
  f.click('[data-add-shortcut="categoryPair"]');
  // 新加的組合條件預設借方應收、貸方收入，送出的是分類身分。
  assert.deepEqual(plain([f.draft().groups[0].conditions[0].debitCategoryIds, f.draft().groups[0].conditions[0].creditCategoryIds]),
    [['builtin.receivables'], ['builtin.revenue']]);
  const select = () => f.$('[data-pair-selection]');
  assert.deepEqual(select().querySelectorAll('option').map(option => option.getAttribute('value')), [
    'specialAccountCategoryPair|drAndCr', 'specialAccountCategoryPair|drNotCr', 'specialAccountCategoryPair|notDrCr', 'accountPair|debitAnchor', 'accountPair|creditAnchor']);
  const expected = {
    'specialAccountCategoryPair|drAndCr': { sides: ['debitCategoryIds', 'creditCategoryIds'], excluded: [] },
    'specialAccountCategoryPair|drNotCr': { sides: ['debitCategoryIds', 'creditCategoryIds'], excluded: ['creditCategoryIds'] },
    'specialAccountCategoryPair|notDrCr': { sides: ['debitCategoryIds', 'creditCategoryIds'], excluded: ['debitCategoryIds'] },
    'accountPair|debitAnchor': { sides: ['debitCategoryIds'], excluded: [] },
    'accountPair|creditAnchor': { sides: ['creditCategoryIds'], excluded: [] }
  };
  const results = new Set();
  for (const [value, shape] of Object.entries(expected)) {
    f.choose('[data-pair-selection]', value);
    const [type, mode] = value.split('|'), rule = f.draft().groups[0].conditions[0];
    assert.deepEqual([rule.type, rule.pairMode], [type, mode], value + ' draft');
    const wired = f.wire().groups[0].conditions[0];
    assert.deepEqual([wired.type, wired.pairMode, wired.debitCategoryIds, wired.creditCategoryIds],
      [type, mode, ['builtin.receivables'], ['builtin.revenue']], value + ' keeps both sides when the mode changes');
    assert.equal(select().value, value);
    const pickers = f.$$('.rule-row [data-category-picker]');
    assert.deepEqual(pickers.map(picker => picker.getAttribute('data-category-picker')), shape.sides, value + ' shows the sides it uses');
    for (const picker of pickers) {
      const key = picker.getAttribute('data-category-picker'), legend = picker.querySelector('legend').textContent;
      assert.notEqual(legend, key, value + ' legend is not the internal key');
      assert.doesNotMatch(legend, /[A-Za-z]/, value + ' legend has no internal identifier');
      assert.match(legend, key === 'debitCategoryIds' ? /借方/ : /貸方/, value + ' legend names its side');
      assert.equal(/不得/.test(legend), shape.excluded.includes(key), value + ' ' + key + ' says whether it excludes');
    }
    const result = f.$('.rule-row .filter-pair-result').textContent;
    assert.ok(result, value + ' explains the result'); results.add(result);
  }
  assert.equal(results.size, 5, 'each lookup mode has its own explanation');
});

// 取代 AccountCategoryFrontendTests.RuleControls_UseCategoryIdentitiesInsteadOfLegacyCategoryLabels。
// 首次失敗收據（原 C# 註解）：20261004-075340533-87179f5019724ec89d7c19b4243fdb09。
const ACCOUNT_CATEGORIES = { revision: 3, categories: [
  { categoryId: 'builtin.revenue', label: 'Revenue', ordinal: 0, semanticRole: 'revenue', isBuiltIn: true },
  { categoryId: 'builtin.cash', label: 'Cash', ordinal: 1, semanticRole: 'cash', isBuiltIn: true },
  { categoryId: 'custom.ppe', label: '固定資產', ordinal: 2, semanticRole: 'others', isBuiltIn: false }] };
// 裁定 2026-11-03-06 把「科目屬於分類」改成類別組的「科目分類」欄位：只加入一條科目分類條件，分類留空待選，不再預選現金，
// 也不再帶借貸別的子條件（確認頁寫明照 2026-10-14-08 一起做）。原本核對一開始勾著現金、分類條件在子條件組第 2 條；
// 改成核對一開始沒有勾選，先勾現金再勾固定資產，分類條件就是第 1 組第 1 條。重複勾選、改名與讀回的斷言不變。
// 第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
test('category ticks store each identity once and a renamed category keeps the condition and reads back the new name', () => {
  const f = fieldPane({ accountCategory: ACCOUNT_CATEGORIES });
  f.click('[data-add-field="accountCategory"]');
  const box = id => f.$('.rule-row [data-category-bind="categoryIds"][value="' + id + '"]');
  assert.deepEqual(f.$$('.rule-row [data-category-bind="categoryIds"]').map(input => input.getAttribute('value')), ['builtin.revenue', 'builtin.cash', 'custom.ppe']);
  const category = () => f.draft().groups[0].conditions[0];
  assert.deepEqual(plain(category().categoryIds), [], 'the classification starts with no category ticked');
  assert.deepEqual(f.$$('.rule-row [data-category-bind="categoryIds"]').filter(input => input.checked), []);
  box('builtin.cash').checked = true; box('builtin.cash').fire('change');
  assert.deepEqual(plain(category().categoryIds), ['builtin.cash']);
  assert.equal(box('builtin.cash').checked, true);
  assert.equal(box('custom.ppe').closest('label').textContent, '固定資產', 'each tick shows the category name');
  box('custom.ppe').checked = true; box('custom.ppe').fire('change');
  box('custom.ppe').fire('change');
  assert.deepEqual(plain(category().categoryIds), ['builtin.cash', 'custom.ppe']);
  box('custom.ppe').checked = false; box('custom.ppe').fire('change');
  box('custom.ppe').checked = true; box('custom.ppe').fire('change');
  assert.deepEqual(plain(category().categoryIds), ['builtin.cash', 'custom.ppe']);
  assert.deepEqual(f.wire().groups[0].conditions[0].categoryIds, ['builtin.cash', 'custom.ppe']);
  // 完整的篩選條件讀回在兩條以上時才出現。
  f.click('[data-add-field="description"]');
  assert.match(f.$('.scenario-readback').textContent, /固定資產/);

  f.store.setCategory({ revision: 4, categories: ACCOUNT_CATEGORIES.categories.map(c => c.categoryId === 'custom.ppe' ? { ...c, label: '不動產廠房設備' } : c) });
  assert.deepEqual(plain(category().categoryIds), ['builtin.cash', 'custom.ppe'], 'renaming keeps the identities');
  assert.equal(box('custom.ppe').checked, true);
  assert.match(f.$('.scenario-readback').textContent, /不動產廠房設備/);
  assert.doesNotMatch(f.$('.scenario-readback').textContent, /固定資產/);
  // 借貸科目組合的讀回同樣寫分類名稱，不寫分類身分。
  f.click('[data-add-shortcut="categoryPair"]');
  const pairBox = f.$('.rule-row[data-gi="0"][data-ri="2"] [data-category-bind="debitCategoryIds"][value="custom.ppe"]');
  pairBox.checked = true; pairBox.fire('change');
  assert.deepEqual(plain(f.draft().groups[0].conditions[2].debitCategoryIds), ['custom.ppe']);
  f.store.touch();
  const readback = f.$('.scenario-readback').textContent;
  assert.match(readback, /不動產廠房設備/);
  assert.doesNotMatch(readback, /builtin\.|custom\./);

  // 只有舊式單一分類名稱、沒有分類身分陣列的條件，畫面不替它換算成分類身分。
  const legacy = fieldPane({ accountCategory: ACCOUNT_CATEGORIES });
  legacy.store.setFilterDraft({ name: '舊條件', rationale: 'r', groups: [{ join: 'AND', matchScope: 'row', conditions: [
    { type: 'specialAccountCategoryPair', join: 'AND', pairMode: 'drAndCr', categorySelection: 'node', debitCategory: 'Cash', creditCategory: 'Revenue' }] }] });
  assert.deepEqual(legacy.$$('.rule-row [data-category-bind]').filter(input => input.checked), []);
});

// 裁定 2026-11-03-07：「金額尾數連續多個 0」改成一條條件，用新的比較方式，排在篩選方式的「尾數」那一組。這是新增的測試：
// 審計員從欄位條件加入金額，篩選方式選「尾數連續多個 0」，個數預設帶第四步預篩選的 6 個，輸入框只收 1 到 12，後面寫「個 0」，
// 條件列寫出整數部分為 0 的金額不符合；改成「排除」換成沒有連續的比較方式；個數填 0 時說要填 1 到 12 的整數。
// 這個比較方式不看正負號，攸關資料元素的金額選它時不出現比較基準。
test('the trailing-zero comparison defaults to six zeros, takes one to twelve and has no amount basis', () => {
  const f = fieldPane({ rdeFields: [{ fieldId: 'rde.money', sourceColumn: 'Tax', label: '稅額', valueType: 'amount' }] });
  const ui = f.window.JetUi;
  f.click('[data-add-field="amount"]');
  const row = () => f.$('.rule-row[data-gi="0"][data-ri="0"]');
  const rule = () => f.draft().groups[0].conditions[0];
  const option = row().querySelector('[data-value-kind]').querySelectorAll('option').find(node => node.getAttribute('value') === 'zeros');
  assert.equal(option.textContent, '尾數連續多個 0');
  assert.equal(option.closest('optgroup').getAttribute('label'), '尾數');
  f.choose('.rule-row[data-gi="0"][data-ri="0"] [data-value-kind]', 'zeros');
  assert.deepEqual(plain([rule().operator, rule().value, rule().amountBasis]), ['endsWithZeros', '6', 'absolute']);
  const count = row().querySelector('input[data-value-key="value"]');
  assert.deepEqual([count.getAttribute('type'), count.getAttribute('min'), count.getAttribute('max'), count.value], ['number', '1', '12', '6']);
  assert.ok(row().querySelectorAll('span').some(node => node.textContent === '個 0'), 'the count is followed by 個 0');
  assert.ok(row().querySelectorAll('.rule-explanation').some(node => node.textContent.includes('整數部分為 0 的金額不符合')));
  assert.equal(ui.FilterValues.summary(rule(), f.store.getState()), '金額 尾數連續「6」個 0；空白不列入');
  f.choose('.rule-row[data-gi="0"][data-ri="0"] [data-value-polarity]', 'exclude');
  assert.equal(rule().operator, 'notEndsWithZeros');
  assert.equal(ui.FilterValues.summary(rule(), f.store.getState()), '金額 尾數沒有連續「6」個 0；空白不列入');
  const input = row().querySelector('input[data-value-key="value"]');
  input.value = '0'; input.fire('input');
  assert.equal(ui.FilterValues.problem(rule(), f.store.getState()), '連續 0 的個數須為 1 到 12 的整數');
  input.value = '12'; input.fire('input');
  assert.equal(ui.FilterValues.problem(rule(), f.store.getState()), '');

  f.click('[data-add-field="rde.money"]');
  const extra = () => f.$('.rule-row[data-gi="0"][data-ri="1"]');
  assert.ok(extra().querySelector('[data-value-key="amountBasis"]'), 'an extra amount compared by value offers the basis');
  f.choose('.rule-row[data-gi="0"][data-ri="1"] [data-value-kind]', 'zeros');
  assert.equal(f.draft().groups[0].conditions[1].operator, 'endsWithZeros');
  assert.equal(extra().querySelector('[data-value-key="amountBasis"]'), null, 'the trailing-zero comparison ignores the sign, so no basis is offered');
});

// 裁定 2026-11-03-27 先隱藏科目分類的上下層級，新案件的分類都是平的一層。這是新增的測試：分類都是平的時，科目分類條件的分類清單
// 不畫展開鈕的空位，從科目樹選取時不寫「包含下層分類」；案件已有分類設上層時，兩處照舊畫出展開鈕、寫出包含下層分類。
// 開關關著，兩種情形都沒有「選取依據」。
test('with flat categories the category list leaves no chevron slots and the account tree does not mention subcategories', () => {
  const f = fieldPane({ accountCategory: ACCOUNT_CATEGORIES });
  f.click('[data-add-field="accountCategory"]'); f.click('[data-add-field="accountNumber"]');
  const category = () => f.$('.rule-row[data-gi="0"][data-ri="0"]');
  const tree = () => f.$('.rule-row[data-gi="0"][data-ri="1"] [data-account-tree]');
  assert.ok(category().querySelectorAll('[data-category-bind="categoryIds"]').length, 'the category list is shown');
  assert.equal(category().querySelectorAll('.category-tree__toggle').length, 0, 'flat categories leave no chevron slot');
  assert.equal(category().querySelector('[data-rule-bind="categorySelection"]'), null);
  assert.ok(tree(), 'the account number condition offers the account tree');
  assert.match(tree().textContent, /勾選此分類的科目/);
  assert.doesNotMatch(tree().textContent, /包含下層分類/);

  f.store.setCategory({ revision: 4, categories: [...ACCOUNT_CATEGORIES.categories,
    { categoryId: 'custom.bank', label: '銀行存款', ordinal: 3, semanticRole: 'cash', isBuiltIn: false, parentCategoryId: 'builtin.cash' }] });
  assert.ok(category().querySelector('[data-category-toggle="builtin.cash"]'), 'a category with a subcategory gets its chevron');
  assert.equal(category().querySelector('[data-rule-bind="categorySelection"]'), null, 'the selection menu stays hidden while the switch is off');
  assert.match(tree().textContent, /勾選此分類的科目（包含下層分類）/);
  assert.match(tree().textContent, /選分類時包含下層分類的科目/);
});

// 預覽送出的 payload：精確比對、不帶 `__` 鍵、空的組不送；錯誤標在畫面上對應的條件列（不比對原因句子）。
// 預覽錯誤標在條件列，原本由 FilterTemplatesFrontendTests.InvalidScenarioDetails_AreMarkedOnTheRuleRow 用原始碼字串檢查；
// 那個方法已由第五步修正寫好取代的行為測試後刪除（artifacts/review/closing-four-items-20261013/impl-scenario-save.md 第 4 節），這裡只補預覽這一條路的行為。
test('preview sends the composed groups exactly and marks a rejected condition on its row', async () => {
  const f = buildFixture();
  f.click('[data-common-card="blankDescription"]');
  f.click('[data-action="add-set"]');
  f.click('[data-action="add-set"]');
  f.click('[data-common-card="specificTrailingDigits"]');
  // 「人工分錄」改成類別組的「人工/自動分錄」欄位（裁定 2026-11-03-18、2026-11-03-22），加入的條件不變；分頁識別改成 field。
  // 第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
  f.click('[data-condition-source="field"]'); f.click('[data-add-field="manualAuto"]');
  // 組內與組間的連接改成分段切換（裁定 2026-10-30-32），改點 OR、AND 那一格。原本對下拉設值；
  // 改成單選鈕後設值只改到第一格，組間預設是 OR，那一格沒被選中，送出的組間連接因此錯成 OR。
  // 第一次失敗：FrontendMapping 收據 20261031-095121839-24d9cd622b3242889605b39228f69a2e。
  f.pick('[data-set-combinator][data-gi="2"]', 'OR');
  f.pick('[data-set-join][data-set-join-index="2"]', 'AND');
  // 空白摘要卡改用欄位條件：精確核對新欄位格式，其他請求與錯誤位置斷言保留。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  assert.deepEqual(rulesOf(f), [['fieldValue'], [], ['fieldValue', 'manualAuto']]);
  f.click('[data-action="preview-scenario"]');
  await settle();
  const request = f.requests.filter(r => r.action === 'filterPreview');
  assert.equal(request.length, 1);
  // 停用的尾數位數與筆數條件拿掉後，新條件不再帶 digits 與 maxEntries 兩個預設值，送出的資料跟著少這兩個欄位。
  // 第一次失敗：FrontendMapping 收據 20261030-140327263-4b841c5d85844416830ffd4884cd0fae。
  const filler = { field: 'description', values: [], mode: 'contains', normalization: 'preserve', from: '', to: '', drCr: 'debit', isManual: 'true',
    pairMode: 'exact', debitCategoryIds: ['builtin.receivables'], creditCategoryIds: ['builtin.revenue'], fieldId: '', operator: '',
    amountBasis: 'absolute', value: '', windowDays: '' };
  // 2026-10-29 拿掉卡片字母：名稱改用條件名稱的新寫法、動機每行不再以字母開頭、編輯來源改送第 2 版的卡片代號、
  // 來源代碼改成 common（原本是字母串接的名稱、第 1 版字母來源與舊來源代碼）。
  // 第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
  assert.deepEqual(plain(request[0].payload), {
    populationScope: 'auditPeriod',
    scenario: {
      name: '空白摘要、特定尾數(如 999999/000000 結尾)等3項條件',
      rationale: '空白摘要\n特定尾數(如 999999/000000 結尾)\n自訂：人工分錄',
      groups: [
        { join: 'AND', matchScope: 'row', conditions: [{ join: 'AND', type: 'fieldValue', field: 'description', operator: 'isBlank', includeBlank: false }] },
        { join: 'AND', matchScope: 'row', conditions: [
          { join: 'OR', type: 'fieldValue', field: 'amount', operator: 'endsWithDigits', value: '000000', amountBasis: 'absolute', includeBlank: false },
          { join: 'OR', type: 'manualAuto', prescreenKey: 'suspiciousKeywords', keywords: '', ...filler }] }],
      editorOrigins: { version: 2, nameIsAutomatic: true, rationaleIsAutomatic: true,
        groups: [{ cards: ['blankDescription'] }, { cards: ['specificTrailingDigits', null] }] },
      source: 'common'
    }
  });
  assert.doesNotMatch(JSON.stringify(request[0].payload), /"__/);
  // 草稿本身沒有被送出的投影改動：空的第 2 組還在，標記仍在草稿裡。
  assert.deepEqual(rulesOf(f), [['fieldValue'], [], ['fieldValue', 'manualAuto']]);
  assert.equal(f.draft().groups[0].conditions[0].__commonCard, 'blankDescription');

  // 後端說送出的第 2 組第 2 條有問題：畫面上是第 3 組（空的第 2 組沒有送出）的第 2 條。
  const error = new Error('synthetic'); error.details = [{ group: 2, rule: 2, message: '合成原因' }];
  request[0].reject(error);
  await settle();
  const invalid = f.$$('.rule-row--invalid');
  assert.deepEqual(invalid.map(row => [row.getAttribute('data-gi'), row.getAttribute('data-ri')]), [['2', '1']]);
  assert.equal(invalid[0].querySelectorAll('[data-rule-error]').length, 1);
  assert.equal(invalid[0].querySelector('[data-rule-error]').getAttribute('role'), 'alert');
});

// ------------------------------------------------------------------------------------------------
// 加入條件後，延遲的計時器不會取消選取
// ------------------------------------------------------------------------------------------------

// 裁定 2026-10-15-01：Gui 原本在加入條件後固定等 7 秒，再核對條件仍是「選取中」。
// 這裡改用整個畫面的夾具（setTimeout 排進佇列），加入條件後跑完所有排定的計時器、各觸發一次 setInterval，
// 再核對同一件事；確認這個測試抓得到計時器回來取消選取後，才刪掉 Gui 的等待。
// 分頁識別改成 field（裁定 2026-11-03-21），測試名稱的 custom pane 跟著改成 field tab；原本的名稱是
// a condition added from the custom pane stays selected after every pending timer has run。第一次失敗見本檔的 fieldPane()。
test('a condition added from the field tab stays selected after every pending timer has run', async () => {
  const CASE = { projectId: '合成條件案', databaseProvider: 'sqlite' };
  const f = await bootFullApp([CASE]);
  f.click('[data-action="picker-open"]');
  f.reply('project.load', { project: { ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 1 } }); await flush();
  f.reply('log.recent', { messages: [] }); await flush();
  for (const kind of ['gl', 'tb']) {
    f.store.setImportResult(kind, { batchId: kind + '-source', columns: ['Account'], rowCount: 2 });
    f.store.setMappingCommitted(kind, { mode: kind === 'gl' ? 'signed' : 'direct', mapping: {} });
  }
  f.store.setResult('validate', { resultRef: { resultId: 'v1', generatedUtc: '2026-01-01T00:00:00Z' },
    completenessTest: { eligibility: { isEligible: true, reason: null, warning: null } } });
  f.store.setAccountMappingState(ALL_MAPPED);
  f.store.setStepIndex(4); await flush();
  f.click('[data-condition-source="field"]'); await flush();
  f.click('[data-add-field="voucherNumber"]'); await flush();
  const selected = () => {
    const rows = f.dom.content.querySelectorAll('.rule-row--selected');
    return rows.length === 1 && rows[0].querySelector('.filter-rule-selected').textContent === '選取中';
  };
  assert.equal(f.state().filter.draft.groups[0].conditions[0].field, 'voucherNumber');
  assert.ok(selected(), 'the new condition is selected');
  f.timers.run();
  for (const entry of f.liveIntervals()) entry.fn();
  f.timers.run(); await flush();
  assert.ok(selected(), 'the condition is still selected after the timers ran');
});

// ------------------------------------------------------------------------------------------------
// 逐一點每一項常用查法
// ------------------------------------------------------------------------------------------------

// 裁定 2026-10-15-01：Gui 的 legacy-form-catalog 情境逐一加入及移除每一項常用查核條件，
// 核對名稱、所屬分類一次只展開一類及加入的條件型別。改在這裡用畫面上的按鈕做同一件事，之後刪掉那個 Gui 情境。
// 分類、名稱與型別原本逐字照那個情境的清單（tools/harness/gui-driver/GuiLegacyFormWorkflow.cs），不從畫面推導。
// 裁定 2026-11-03-06 把常用查法從依審計目的的五類改掛在各欄位底下，不再有分類可展開；裁定 2026-11-03-20 定傳票核准日底下另有週末、
// 國定假日與日期區間，「財報準備日起核准」也放在這裡；借貸別、人工/自動分錄與科目分類改成類別組的欄位（裁定 2026-11-03-18），
// 「借方與貸方科目編號組合」移到情境範例（裁定 2026-11-03-08），「未預期借貸組合」只留在預篩選分頁；「金額尾數連續多個 0」改成一條條件
//（裁定 2026-11-03-07）。原本核對每一項在所屬分類裡、加入時只展開那一類、名稱寫在項目上；改成核對每一項排在所屬欄位那一列、
// 鍵上寫短名稱、完整名稱在滑鼠提示與螢幕報讀，畫面上的常用查法依序就是這張清單，也沒有分類的展開鈕。
// 類別組的三個欄位另外逐一加入與移除，借方與貸方科目編號組合改從情境範例核對借方與貸方各一個子條件。
// 欄位、鍵上的字、完整名稱與型別逐字照 ui-core.js 的 FIELD_SHORTCUT_GROUPS 寫死，不從畫面推導。
// 原本的測試名稱是 every common check is added by its own button with the exact type, opens only its category and can be removed。
// 第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
test('every field shortcut sits on its field row, is added by its own button with the exact type and can be removed', () => {
  const checks = [
    ['glDate', 'glDateWeekend', '週末', '總帳日期在週末', 'fieldValue'],
    ['glDate', 'glDateHoliday', '國定假日', '總帳日期在國定假日', 'fieldValue'],
    ['glDate', 'glDateNonBusinessDay', '非營業日', '總帳日期在非營業日', 'fieldValue'],
    ['glDate', 'glDateRange', '日期區間', '總帳日期在指定日期區間', 'fieldValue'],
    ['glDate', 'periodEndDays', '期末最後幾天', '查核期末最後幾天', 'fieldValue'],
    ['approvalDate', 'approvalDateWeekend', '週末', '傳票核准日在週末', 'fieldValue'],
    ['approvalDate', 'approvalDateHoliday', '國定假日', '傳票核准日在國定假日', 'fieldValue'],
    ['approvalDate', 'approvalDateNonBusinessDay', '非營業日', '傳票核准日在非營業日', 'fieldValue'],
    ['approvalDate', 'approvalDateRange', '日期區間', '傳票核准日在指定日期區間', 'fieldValue'],
    ['approvalDate', 'prepPeriodApproval', '財報準備日起核准', '財報準備日起核准的分錄', 'prescreen'],
    ['amount', 'largeAmount', '大額', '大額分錄', 'fieldValue'],
    ['amount', 'trailingZeros', '尾數連續多個 0', '金額尾數連續多個 0', 'fieldValue'],
    ['description', 'descriptionKeywords', '含特定字詞', '摘要含特定字詞', 'fieldValue'],
    ['description', 'blankDescription', '空白', '空白摘要', 'fieldValue'],
    ['accountNumber', 'rareAccounts', '較少使用的科目', '較少使用的科目', 'entityFrequency'],
    ['createdBy', 'rarePreparers', '編製分錄較少的人員', '編製分錄較少的人員', 'entityFrequency'],
    ['accountCategory', 'categoryPair', '借方與貸方分類組合', '借方與貸方分類組合', 'specialAccountCategoryPair']
  ];
  const f = fieldPane({ glMapping: { createdBy: 'Maker', approvedBy: 'Checker', isManual: 'Manual' } });
  const allRules = () => f.draft().groups.flatMap(group => group.conditions);
  assert.equal(f.$('[data-check-category]'), null, 'there are no categories to open');
  assert.deepEqual(f.$$('[data-add-shortcut]').map(button => button.getAttribute('data-add-shortcut')), checks.map(([, key]) => key),
    'the tab lists exactly these shortcuts in this order');
  for (const [field, key, label, name, type] of checks) {
    const button = f.$('[data-add-shortcut="' + key + '"]');
    assert.equal(button.disabled, false, key + ' can be added');
    assert.ok(button.closest('.filter-field').querySelector('[data-add-field="' + field + '"]'), key + ' sits on the ' + field + ' row');
    assert.equal(button.textContent, label, key + ' key text');
    assert.equal(button.getAttribute('title'), name, key + ' full name');
    assert.equal(button.getAttribute('aria-label'), '加入「' + name + '」', key + ' accessible name');
    button.fire('click');
    const rules = allRules();
    assert.equal(rules.length, 1, key);
    assert.equal(rules[0].type, type, key);
    // 新建的分類條件預設包含下層分類。
    if (key === 'categoryPair') assert.equal(rules[0].categorySelection, 'subtree');
    if (key === 'trailingZeros') assert.deepEqual([rules[0].field, rules[0].operator, rules[0].value, rules[0].amountBasis], ['amount', 'endsWithZeros', '6', 'absolute']);
    const remove = f.$('.rule-row[data-ri="0"]').querySelector('[data-action="remove-rule"]');
    assert.ok(remove, key + ' offers a remove control on its row');
    remove.fire('click');
    assert.equal(allRules().length, 0, key + ' is removed');
  }
  // 類別組的三個欄位各加入一條既有型別的條件（裁定 2026-11-03-18）。
  for (const [field, type] of [['drCr', 'drCrOnly'], ['manualAuto', 'manualAuto'], ['accountCategory', 'accountSide']]) {
    f.click('[data-add-field="' + field + '"]');
    assert.deepEqual(plain(allRules().map(rule => rule.type)), [type], field);
    f.$('.rule-row[data-ri="0"] [data-action="remove-rule"]').fire('click');
    assert.equal(allRules().length, 0, field + ' is removed');
  }
  // 借方與貸方科目編號組合移到情境範例，套用後照舊是借方與貸方各一個明示方向的子條件。
  const example = buildFixture();
  example.click('[data-template-key="accountNumberPair"]');
  const pair = example.draft().groups[0].conditions[0];
  assert.deepEqual([pair.conditions[0].conditions[0].drCr, pair.conditions[1].conditions[0].drCr], ['debit', 'credit']);
});

// ------------------------------------------------------------------------------------------------
// 欄位「加入」打開的選單
// ------------------------------------------------------------------------------------------------

// 裁定 2026-11-03-37：有常用查法的欄位，「加入」鍵打開選單，第一項是這個欄位的一般條件、寫欄位名稱，下面是常用查法；欄位名稱只是文字。
// 審計員按「加入」打開選單，按一項只加入那一條，選單隨即收起；也可以在「加入」上按方向鍵下打開，用方向鍵選，Esc 收起並回到「加入」。
// 沒有常用查法的欄位按「加入」直接加入。選單是共用的 .jet-menu（裁定 2026-10-19-08），一次只開一個。
// 這個測試在第 13 步新加時核對按欄位名稱打開選單（裁定 2026-11-03-32）；使用者第二次驗收試用兩種開法後選了從「加入」打開，
// 所以改成核對「加入」是選單的開關、選單第一項是一般條件，以及名稱不再是按鈕。
// 第一次失敗：FrontendMapping 收據 20261103-160322547-996b0a7863f643239742942bd3bba3a2。
test('a field\'s add key opens its menu with the general condition first, adds one condition per choice and closes again', () => {
  const f = fieldPane({ glMapping: { createdBy: 'Maker', approvedBy: 'Checker', isManual: 'Manual' } });
  const active = () => f.dom.document.activeElement;
  const row = field => f.$('[data-add-field="' + field + '"]').closest('.filter-field');
  const key = field => row(field).querySelector('.btn--add');
  const menu = field => f.$('#filter-field-menu-' + field);
  const items = field => menu(field).querySelectorAll('[role="menuitem"]');
  const added = () => plain(f.draft().groups.flatMap(group => group.conditions.map(rule => [rule.type, rule.field ?? null, rule.operator ?? null])));

  // 有常用查法的七個欄位，「加入」是選單的開關，指向自己那一列的選單，選單一開始收著；名稱不是按鈕。
  const withMenu = ['glDate', 'approvalDate', 'amount', 'description', 'accountNumber', 'createdBy', 'accountCategory'];
  assert.deepEqual(f.$$('.filter-field .jet-menu__toggle').map(node => node.closest('.filter-field').querySelector('[data-add-field]').getAttribute('data-add-field')),
    withMenu);
  for (const field of withMenu) {
    assert.ok(key(field).classList.contains('jet-menu__toggle'), field + ' add key opens the menu');
    assert.equal(key(field).textContent, '加入', field);
    assert.equal(key(field).getAttribute('data-add-field'), null, field + ' add key itself adds nothing');
    assert.equal(key(field).getAttribute('aria-haspopup'), 'menu', field);
    assert.equal(key(field).getAttribute('aria-controls'), 'filter-field-menu-' + field, field);
    assert.equal(key(field).getAttribute('aria-expanded'), 'false', field);
    assert.equal(menu(field).getAttribute('role'), 'menu', field);
    assert.equal(menu(field).hidden, true, field + ' menu starts closed');
    assert.ok(row(field).contains(menu(field)), field + ' menu belongs to its row');
    assert.equal(row(field).querySelector('.filter-field__name').closest('button'), null, field + ' name is not a button');
    // 第一項是一般條件：帶著欄位識別，寫和這一列一樣的欄位名稱。
    assert.equal(items(field)[0].getAttribute('data-add-field'), field);
    assert.equal(items(field)[0].textContent, row(field).querySelector('.filter-field__name').textContent);
  }
  // 沒有常用查法的欄位，「加入」直接加入，沒有選單；名稱也不是按鈕。
  for (const field of ['accountName', 'approvedBy', 'voucherNumber', 'sourceModule', 'lineItem', 'drCr', 'manualAuto']) {
    assert.equal(row(field).querySelector('.jet-menu__toggle'), null, field + ' has no menu');
    assert.equal(key(field).getAttribute('data-add-field'), field, field + ' add key adds directly');
    assert.equal(row(field).querySelector('.filter-field__name').closest('button'), null, field + ' name is not a button');
  }
  // 報讀名稱：鍵與選單都是「加入總帳日期的條件」；選單第一項和沒有選單的「加入」一樣寫「加入條件：總帳日期」。
  assert.equal(key('glDate').getAttribute('aria-label'), '加入總帳日期的條件');
  assert.equal(menu('glDate').getAttribute('aria-label'), '加入總帳日期的條件');
  assert.equal(items('glDate')[0].getAttribute('aria-label'), '加入條件：總帳日期');

  // 滑鼠：按「加入」打開，再按一次收起；打開時不加入任何條件。打開後按一項，只加入那一條，選單收起。
  key('glDate').dispatch('click');
  assert.equal(key('glDate').getAttribute('aria-expanded'), 'true');
  assert.equal(menu('glDate').hidden, false);
  assert.deepEqual(added(), [], 'opening the menu adds nothing');
  key('glDate').dispatch('click');
  assert.equal(menu('glDate').hidden, true, 'pressing the add key again closes its menu');
  key('glDate').dispatch('click');
  assert.deepEqual(items('glDate').map(item => item.textContent), ['總帳日期', '週末', '國定假日', '非營業日', '日期區間', '期末最後幾天']);
  items('glDate')[2].dispatch('click');
  assert.deepEqual(added(), [['fieldValue', 'glDate', 'isHoliday']]);
  assert.equal(menu('glDate').hidden, true, 'the menu closes after a choice');
  assert.equal(key('glDate').getAttribute('aria-expanded'), 'false');
  // 第一項加入一般條件，日期欄位的一般條件一開始是「指定日期」。
  key('glDate').dispatch('click');
  items('glDate')[0].dispatch('click');
  assert.deepEqual(added(), [['fieldValue', 'glDate', 'isHoliday'], ['fieldValue', 'glDate', 'in']]);
  assert.equal(menu('glDate').hidden, true, 'the menu closes after the general condition too');

  // 鍵盤：「加入」上按方向鍵下打開並停在第一項，方向鍵移到下一項，Esc 收起並回到「加入」。
  // 停在一項上按 Enter 時，瀏覽器對按鈕送出的是點擊，這裡用點擊代表。
  key('amount').focus();
  assert.equal(key('amount').dispatch('keydown', { key: 'ArrowDown' }).defaultPrevented, true);
  assert.equal(menu('amount').hidden, false);
  assert.ok(active() === items('amount')[0], 'ArrowDown opens the menu on the general condition');
  active().dispatch('keydown', { key: 'ArrowDown' });
  assert.ok(active() === items('amount')[1], 'ArrowDown moves to the first shortcut');
  active().dispatch('keydown', { key: 'Escape' });
  assert.equal(menu('amount').hidden, true);
  assert.ok(active() === key('amount'), 'Esc returns focus to the add key');
  key('amount').dispatch('keydown', { key: 'ArrowDown' });
  active().dispatch('keydown', { key: 'ArrowDown' });
  active().dispatch('keydown', { key: 'ArrowDown' });
  active().dispatch('click');
  assert.deepEqual(added(), [['fieldValue', 'glDate', 'isHoliday'], ['fieldValue', 'glDate', 'in'], ['fieldValue', 'amount', 'endsWithZeros']]);

  // 沒有選單的欄位，按「加入」直接加入一條；文字欄位的一般條件一開始是「包含」。
  key('voucherNumber').dispatch('click');
  assert.deepEqual(added().at(-1), ['fieldValue', 'voucherNumber', 'contains']);

  // 一次只開一個：打開傳票摘要的選單時，會計科目編號的選單收起。
  key('accountNumber').dispatch('click');
  key('description').dispatch('click');
  assert.equal(menu('accountNumber').hidden, true);
  assert.equal(menu('description').hidden, false);
});

// 裁定 2026-11-03-32 之後，欄位不能用時打不開選單，原因照舊是分頁最後那一段（裁定 2026-10-22-05）；
// 欄位能用但常用查法另有前置條件時，那一項在選單裡停用，原因寫在那一項的下一行，只寫一次。這是新加的測試。
// 2026-11-03-37 起選單的開關從欄位名稱改成「加入」，這裡用的 .jet-menu__toggle 跟著指到「加入」，斷言不用改；
// 另外補一條：選單第一項的一般條件也跟著停用，連到同一段原因。
test('a field that cannot be used keeps its menu shut and a shortcut that needs more data says why right under it in the menu', () => {
  const everyField = ['voucherNumber', 'lineItem', 'glDate', 'approvalDate', 'accountNumber', 'accountName', 'description', 'sourceModule', 'createdBy', 'approvedBy', 'isManual', 'amount'];
  const f = fieldPane({ availableGlFields: everyField.filter(field => field !== 'createdBy'),
    project: { projectId: 'filter-build-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31' } });
  const row = field => f.$('[data-add-field="' + field + '"]').closest('.filter-field');
  const toggle = field => row(field).querySelector('.jet-menu__toggle');
  // 傳票建立人員沒有來源欄：開選單的「加入」停用並連到分頁最後那一段，選單裡的一般條件與常用查法也停用。
  assert.equal(toggle('createdBy').disabled, true);
  assert.equal(toggle('createdBy').getAttribute('aria-describedby'), 'filter-field-note');
  assert.equal(f.$('[data-add-shortcut="rarePreparers"]').disabled, true);
  assert.equal(f.$('[data-add-field="createdBy"]').disabled, true, 'the general condition in the menu is disabled too');
  assert.equal(f.$('[data-add-field="createdBy"]').getAttribute('aria-describedby'), 'filter-field-note');
  // 沒有期末財報準備日：傳票核准日照樣打得開，「財報準備日起核准」在選單裡停用，原因就在它的下一行。
  assert.equal(toggle('approvalDate').disabled, false);
  toggle('approvalDate').dispatch('click');
  const menu = f.$('#filter-field-menu-approvalDate');
  assert.equal(menu.hidden, false);
  const item = f.$('[data-add-shortcut="prepPeriodApproval"]');
  assert.equal(item.disabled, true);
  const next = menu.children[menu.children.indexOf(item) + 1];
  assert.equal(next.getAttribute('id'), 'filter-check-note-prepPeriodApproval');
  assert.equal(item.getAttribute('aria-describedby'), 'filter-check-note-prepPeriodApproval');
  // 這句的用字依裁定 2026-10-14-01。
  assert.equal(next.textContent, '尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫');
  assert.equal(row('approvalDate').querySelectorAll('.filter-check__note').length, 1, 'the reason is written once, inside the menu');
});

// ------------------------------------------------------------------------------------------------
// 預篩選分頁
// ------------------------------------------------------------------------------------------------

// 使用者指出審計員習慣直接套用預篩選條件（裁定 2026-10-16-09），原本要先加入「沿用第四步預篩選規則」，再到條件卡片的下拉改選。
// 條件總覽改成三個分頁，預篩選分頁條列每一條規則，按「加入」直接帶入那一條。第一個分頁 2026-10-29 改名「常用篩選條件」
//（裁定 2026-10-29-10 合併卡片與範例、拿掉小組名稱），原本的期望值是舊分頁名稱；第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
// 名稱逐字照預篩選規則的畫面名稱（ui-core.js 的 PRESCREEN_RULES，鏡像 Domain 標籤表），不從畫面推導。
// 裁定 2026-10-29-27：第四步照舊版拿掉編製分錄較少的人員與使用較少的科目，清單由十二條減為十條；第一次失敗：FrontendMapping 收據 20261030-170713163-f548633afe0749a791469b97942983be。
const PRESCREEN_RULES = [
  ['prepPeriodApproval', '財報準備日起核准'], ['suspiciousKeywords', '摘要特定描述'], ['unexpectedAccountPair', '未預期借貸組合'],
  ['trailingZeros', '金額尾數連續 6 個 0'], ['weekendPosting', '週末過帳'], ['weekendApproval', '週末核准'],
  ['holidayPosting', '假日過帳'], ['holidayApproval', '假日核准'], ['blankDescription', '空白摘要'],
  ['nonAuthorizedPreparer', '非授權編製人員']
];
const prescreenWire = f => f.wire().groups.map(group => group.conditions.map(rule => [rule.type, rule.prescreenKey]));

test('the prescreen tab lists every prescreen rule and each add button brings in exactly that rule', () => {
  // 「非授權編製人員」要用傳票建立人員，這裡配好欄位，十條都能加入。
  const f = buildFixture({ glMapping: { createdBy: 'Maker' } });
  f.store.setAuthorizedPreparerState({ rowCount: 2 });
  // 裁定 2026-11-03-05 把第三個分頁「自訂篩選條件」改成「欄位條件」。第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
  assert.deepEqual(f.$$('[data-condition-source]').map(button => button.textContent), ['常用篩選條件', '預篩選', '欄位條件']);
  f.click('[data-condition-source="prescreen"]');
  assert.equal(f.$('[data-condition-pane="prescreen"]').hidden, false);
  assert.deepEqual(f.$$('[data-add-prescreen]').map(button =>
    [button.getAttribute('data-add-prescreen'), button.closest('.filter-check').querySelector('.filter-check__label').textContent]), PRESCREEN_RULES);
  // 和常用查核條件一樣，每一列只有名稱與「加入」，不另加說明。
  for (const item of f.$('[data-condition-pane="prescreen"]').querySelectorAll('.filter-check')) {
    assert.equal(item.querySelector('[data-add-prescreen]').textContent, '加入');
    assert.deepEqual(item.children.filter(child => !child.matches('.filter-check__label') && !child.matches('[data-add-prescreen]')), []);
  }
  for (const [key] of PRESCREEN_RULES) {
    assert.equal(f.$('[data-add-prescreen="' + key + '"]').disabled, false, key + ' can be added');
    f.click('[data-add-prescreen="' + key + '"]');
    assert.deepEqual(prescreenWire(f), [[['prescreen', key]]], key + ' is added as itself, with no menu change needed');
    f.$('.rule-row[data-ri="0"] [data-action="remove-rule"]').fire('click');
    assert.equal(f.draft().groups.flatMap(group => group.conditions).length, 0, key + ' is removed');
  }
});

// 前置不足時按鈕停用，原因寫在該項下方並由 aria-describedby 連到按鈕，和常用查核條件同一種做法；後端一定會擋的條件不會被帶入。
// 2026-10-23 改了兩處：前端改讀後端算好的可用欄位，原本靠「沒有確認 GL 配對」表示傳票建立人員未配對，
// 改成給一份沒有傳票建立人員的可用欄位清單；欄位的原因接上和第四步相同的下一步（裁定 2026-10-22-05 寫出可以回欄位配對補上）。
// 第一次失敗：FrontendMapping 收據 20261023-044331305-cc3be698efaa446eab8994f89154b247（「編製分錄較少的人員」沒有停用）。
const WITHOUT_CREATED_BY = ['voucherNumber', 'lineItem', 'glDate', 'approvalDate', 'accountNumber', 'accountName', 'description', 'sourceModule', 'approvedBy', 'isManual', 'amount'];
test('a prescreen rule the backend would reject is disabled with its reason and adds nothing when pressed', () => {
  const f = buildFixture({ accountMapping: { hasAnyCategory: true, hasRevenue: false, hasCounterpart: true }, availableGlFields: WITHOUT_CREATED_BY,
    project: { projectId: 'filter-build-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: null } });
  f.click('[data-condition-source="prescreen"]');
  // 這個夾具沒有配對傳票建立人員，後端會擋「非授權編製人員」（2026-10-16 複審找到原本沒有停用）。
  // 裁定 2026-10-29-27：第四步照舊版拿掉編製分錄較少的人員與使用較少的科目，原本也停用的 rarePreparer 不再列出；傳票建立人員未配對的原因改由下面非授權編製人員那一句核對。
  // 第一次失敗：FrontendMapping 收據 20261030-170713163-f548633afe0749a791469b97942983be。
  assert.deepEqual(f.$$('[data-add-prescreen]').filter(button => button.disabled).map(button => button.getAttribute('data-add-prescreen')),
    ['prepPeriodApproval', 'unexpectedAccountPair', 'nonAuthorizedPreparer']);
  const reason = key => f.$('#' + f.$('[data-add-prescreen="' + key + '"]').getAttribute('aria-describedby')).textContent;
  // 這句的用字依裁定 2026-10-14-01。
  assert.equal(reason('prepPeriodApproval'), '尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫');
  assert.match(reason('unexpectedAccountPair'), /Revenue.*第四步/);
  assert.match(reason('nonAuthorizedPreparer'), /授權編製人員清單/);
  f.$('[data-add-prescreen="unexpectedAccountPair"]').fire('click');
  f.$('[data-add-prescreen="nonAuthorizedPreparer"]').fire('click');
  assert.equal(f.draft().groups.flatMap(group => group.conditions).length, 0, 'a disabled rule adds nothing');
  // 授權清單匯入後，「非授權編製人員」仍缺傳票建立人員，原因改寫這一項。
  f.store.setAuthorizedPreparerState({ rowCount: 2 });
  assert.equal(f.$('[data-add-prescreen="nonAuthorizedPreparer"]').disabled, true);
  assert.equal(reason('nonAuthorizedPreparer'), '「傳票建立人員」尚未選擇來源欄，請回第三步欄位配對補上');
});

// 裁定 2026-10-16-08 與 2026-10-16-09 一起走一遍：從預篩選分頁一次加入「未預期借貸組合」，再查看符合的傳票。
// 摘要分開寫傳票數量與分錄筆數；分錄筆數照後端的 summaryRowCount，只用這一個條件時是整張傳票的分錄數，
// 例如直接符合 3 列、整張傳票 7 列時寫 7，和條件篩選報告的摘要相同，畫面不自己換算。
test('an auditor adds the unexpected account pair from the prescreen tab and sees the voucher count and the whole-voucher entry count', async () => {
  const f = buildFixture();
  f.click('[data-condition-source="prescreen"]');
  f.click('[data-add-prescreen="unexpectedAccountPair"]');
  assert.deepEqual(prescreenWire(f), [[['prescreen', 'unexpectedAccountPair']]]);
  f.click('[data-action="preview-scenario"]'); await settle();
  f.requests.find(request => request.action === 'filterPreview').resolve({ scenario: {
    name: '未預期借貸組合', populationScope: 'auditPeriod', count: 3, voucherCount: 1, summaryRowCount: 7, countsWholeVoucherRows: true, previewRows: [] } });
  await settle();
  f.requests.find(request => request.action === 'queryFilterVoucherPage').resolve({ queryRevision: 'q1', nextCursor: null,
    rows: [{ voucherNumber: 'SYN-1', glDate: '2025-03-05', hitRowCount: 7, totalRowCount: 7, voucherTotal: 1000 }] });
  await settle(); await settle();
  const pane = f.$('[data-bind="preview-pane-body"]').textContent;
  assert.match(pane, /1符合條件的傳票數量7符合條件的分錄筆數/);
  // 口徑說明 2026-10-29 改成只在這種情形出現的一句（使用者指出常駐說明太多，裁定 2026-10-29-02），意思不變；
  // 原本比對舊句，改版後依預期失敗，首敗收據 20261029-040252723-dde1d637675742fd85d9576ba633dd2d。
  assert.match(pane, /分錄筆數包含整張傳票的分錄/);
  assert.doesNotMatch(pane, /標示為參考的分錄不計入/);
  assert.match(pane, /SYN-1/);
});

// 裁定 2026-10-16-13：「突然擴增文字輸入欄的高度讓我感覺很突兀」。值清單一開始就是固定的多行輸入框，
// 貼上以逗號分隔的人員、拆成多行後重繪，框的高度都不由內容決定；單行輸入（每月幾日、單值）才隨內容調整。
test('a pasted comma list keeps the value list at its fixed height instead of stretching the box', () => {
  const f = fieldPane({ glMapping: { createdBy: 'Maker' } });
  f.store.setFilterDraft({ name: '合成條件', rationale: '合成', groups: [{ join: 'AND', matchScope: 'row', conditions: [
    { type: 'fieldValue', join: 'AND', field: 'createdBy', operator: 'notIn', values: [] }
  ] }] });
  const list = () => f.$('textarea[data-value-key="values"]');
  assert.ok(list(), 'the person list is rendered');
  assert.ok(list().getAttribute('class').split(/\s+/).includes('value-editor__values'), 'the list carries the fixed-height class');
  list().value = '甲員,乙員,丙員,丁員,戊員,己員,庚員';
  list().fire('input');
  assert.deepEqual(plain(f.draft().groups[0].conditions[0].values), ['甲員', '乙員', '丙員', '丁員', '戊員', '己員', '庚員']);
  f.store.touch();
  assert.equal(list().value.split('\n').length, 7, 'the redrawn list shows one value per line');
  assert.equal(list().style.height, undefined, 'the list height is not set from its content');
  const css = readWwwroot('css/app.css');
  assert.match(css, /\.filter-workspace \.value-editor__values \{ flex: 1 1 100%; height: calc\(5 \* 20px \+ 14px\);/,
    'the list is five lines tall on its own row');
});

// ------------------------------------------------------------------------------------------------
// 2026-10-23：第五步不能用 GL 沒有配對的欄位
// ------------------------------------------------------------------------------------------------

// 使用者選了「停用並寫原因」（裁定 2026-10-22-05），選項說明是欄位按鈕照樣列出但不能按，下方寫出原因以及可以回欄位配對補上，
// 常用查核條件與範例用同一套判斷（docs/history/specs/2026-10-22-prescreen-tech-debt-deferred-items-plan.md 附錄一）。
// 可用欄位照後端在「傳票核准日、分錄來源模組、傳票建立人員、傳票核准人員與人工/自動分錄都沒有選來源欄」時送的清單寫死；
// 傳票文件項次沒有來源欄時系統自動編號，同一則裁定算已配對，所以在清單裡。
// 原因是後端第五步的句子；GL 欄位的句子接上和第四步相同的下一步，同一個入口缺幾個欄位就寫在同一句。
const PARTLY_MAPPED = ['voucherNumber', 'lineItem', 'glDate', 'accountNumber', 'accountName', 'description', 'amount'];
const EVERY_GL_FIELD = ['voucherNumber', 'lineItem', 'glDate', 'approvalDate', 'accountNumber', 'accountName', 'description', 'sourceModule', 'createdBy', 'approvedBy', 'isManual', 'amount'];
const DEPARTMENT = [{ fieldId: 'rde.dept', sourceColumn: 'Dept', label: '部門', valueType: 'text' }];
const unmapped = (...names) => names.map(name => '「' + name + '」').join('') + '尚未選擇來源欄，請回第三步欄位配對補上';
const CARD_KEYS = COMMON_CARDS.map(([key]) => key);
function describedText(f, node) {
  const id = node.getAttribute('aria-describedby');
  assert.ok(id, 'the disabled control is described');
  return f.$('#' + id).textContent;
}
const disabledKeys = (f, attribute) => f.$$('[' + attribute + ']').filter(button => button.disabled).map(button => button.getAttribute(attribute));
// 審計員回第三步把欄位都配好、按「確認配對」：後端回傳完整的可用欄位清單，其他配對設定不變。
function mapEverything(f) {
  const committed = plain(f.store.getState().mapping.gl.committed);
  f.store.setMappingCommitted('gl', { ...committed, availableGlFields: EVERY_GL_FIELD });
}

// 裁定 2026-11-03-06 與 2026-11-03-18 把欄位條件分成日期、數字、文字、類別四組，類別組放借貸別、人工/自動分錄與科目分類，三個入口也用 data-add-field；
// 人工/自動分錄要 GL 的人工/自動分錄欄位，這個夾具沒有配對，跟著停用並寫進同一句。原因那一段改放在欄位條件分頁的最後。
// 原本核對十一個核心欄位、四個停用、原因和欄位按鈕在同一個 .filter-source-block；改成核對四組依序列出的十四個入口、五個停用、
// 原因是欄位條件分頁的最後一段。第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
test('every field button stays listed, the unmapped ones cannot be pressed and the note under them names each field and where to fill it', () => {
  const f = fieldPane({ availableGlFields: PARTLY_MAPPED });
  assert.deepEqual(f.$$('.filter-field-group').map(group => group.querySelector('.filter-field-group__title').textContent), ['日期', '數字', '文字', '類別']);
  assert.deepEqual(f.$$('[data-add-field]').map(button => button.getAttribute('data-add-field')),
    ['glDate', 'approvalDate', 'amount', 'description', 'accountNumber', 'accountName', 'createdBy', 'approvedBy', 'voucherNumber', 'sourceModule', 'lineItem',
      'drCr', 'manualAuto', 'accountCategory'], 'every core field is still listed');
  assert.deepEqual(disabledKeys(f, 'data-add-field'), ['approvalDate', 'createdBy', 'approvedBy', 'sourceModule', 'manualAuto']);
  const notes = [...new Set(f.$$('[data-add-field]').filter(button => button.disabled).map(button => describedText(f, button)))];
  assert.deepEqual(notes, [unmapped('傳票核准日', '傳票建立人員', '傳票核准人員', '分錄來源模組', '人工/自動分錄')], 'one note under the buttons names every unmapped field');
  const note = f.$('#' + f.$('[data-add-field="approvalDate"]').getAttribute('aria-describedby'));
  const picker = note.closest('.condition-picker--field');
  assert.ok(picker, 'the note sits on the field tab');
  assert.equal(picker, f.$('[data-add-field="approvalDate"]').closest('.condition-picker--field'), 'the note sits with the field buttons');
  assert.equal(picker.children.at(-1), note, 'the note closes the field tab, under every field');
  // 傳票文件項次沒有來源欄也能用：系統自動編號。
  assert.equal(f.$('[data-add-field="lineItem"]').disabled, false);
  assert.equal(f.$('[data-add-field="lineItem"]').getAttribute('aria-describedby'), null);
  f.$('[data-add-field="createdBy"]').fire('click');
  assert.deepEqual(rulesOf(f), [], 'an unmapped field adds nothing');
  f.click('[data-add-field="lineItem"]');
  assert.deepEqual(plain(f.draft().groups.flatMap(group => group.conditions.map(rule => [rule.type, rule.field]))), [['fieldValue', 'lineItem']]);

  mapEverything(f);
  assert.deepEqual(disabledKeys(f, 'data-add-field'), [], 'after mapping every button can be pressed');
  assert.equal(f.$('[data-add-field="approvalDate"]').getAttribute('aria-describedby'), null);
  assert.doesNotMatch(screenTexts(f.$('[data-condition-pane="field"]')).join('\n'), /尚未選擇來源欄/);
});

// 2026-10-29 範例移到「常用篩選條件」分頁並一直列出（原本要先按「從範例開始」），卡片改用代號識別，測試名稱跟著改；
// 範例的停用斷言改在常用篩選條件分頁核對。第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
// 裁定 2026-11-03-06 把依審計目的的常用查法改掛在各欄位底下：欄位沒有配對時，底下的常用查法跟著停用，原因是分頁最後那一段，
// 寫出每個沒有配對的欄位。「人工分錄」改成類別組的「人工/自動分錄」欄位（裁定 2026-11-03-22），傳票核准日底下多了週末、國定假日與
// 日期區間（裁定 2026-11-03-20）。原本核對四項停用、原因各寫自己缺的欄位；改成核對傳票核准日底下五項與編製分錄較少的人員停用、
// 都連到分頁最後那一段，人工/自動分錄改核對欄位的「加入」。範例移到右側托盤（裁定 2026-11-03-05），停用的範例與原因不變。
// 原本的測試名稱是 purpose checks, examples, common cards and prescreen rules that need an unmapped field are disabled with the same reason until it is mapped。
// 第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da。
test('field shortcuts, examples, common cards and prescreen rules that need an unmapped field are disabled with the same reason until it is mapped', () => {
  const f = fieldPane({ availableGlFields: PARTLY_MAPPED, rdeFields: DEPARTMENT });
  const fieldNote = unmapped('傳票核准日', '傳票建立人員', '傳票核准人員', '分錄來源模組', '人工/自動分錄');
  const blocked = ['approvalDateWeekend', 'approvalDateHoliday', 'approvalDateNonBusinessDay', 'approvalDateRange', 'prepPeriodApproval', 'rarePreparers'];
  assert.deepEqual(disabledKeys(f, 'data-add-shortcut'), blocked);
  for (const key of blocked) {
    const button = f.$('[data-add-shortcut="' + key + '"]');
    assert.equal(button.getAttribute('aria-describedby'), 'filter-field-note', key + ' points to the closing note of the field tab');
    assert.equal(describedText(f, button), fieldNote, key);
  }
  assert.equal(describedText(f, f.$('[data-add-field="manualAuto"]')), fieldNote);
  f.$('[data-add-field="manualAuto"]').fire('click');
  f.$('[data-add-shortcut="prepPeriodApproval"]').fire('click');
  assert.deepEqual(rulesOf(f), [], 'a disabled check adds nothing');

  // 範例缺兩個欄位時寫在同一句。
  f.click('[data-condition-source="common"]');
  assert.deepEqual(disabledKeys(f, 'data-template-key'),
    ['periodEndManual', 'blankDescriptionOrApprover', 'preparerLargeAmount', 'weekendManualDescription', 'manualDepartment', 'preparationManualHoliday']);
  const exampleNote = key => describedText(f, f.$('[data-template-key="' + key + '"]'));
  assert.equal(exampleNote('periodEndManual'), unmapped('人工/自動分錄'));
  assert.equal(exampleNote('blankDescriptionOrApprover'), unmapped('傳票核准人員'));
  assert.equal(exampleNote('preparerLargeAmount'), unmapped('傳票建立人員'));
  assert.equal(exampleNote('weekendManualDescription'), unmapped('人工/自動分錄'));
  assert.equal(exampleNote('manualDepartment'), unmapped('人工/自動分錄'));
  assert.equal(exampleNote('preparationManualHoliday'), unmapped('傳票核准日', '人工/自動分錄'));

  assert.deepEqual(CARD_KEYS.filter(key => card(f, key).disabled), ['manualRevenueEntry', 'specificPreparer', 'preparerEqualsApprover']);
  assert.equal(cardReason(f, 'manualRevenueEntry'), unmapped('人工/自動分錄'));
  assert.equal(cardReason(f, 'specificPreparer'), unmapped('傳票建立人員'));
  assert.equal(cardReason(f, 'preparerEqualsApprover'), unmapped('傳票建立人員', '傳票核准人員'));

  // 預篩選分頁：週末核准與假日核准也要傳票核准日；缺授權編製人員清單的句子照後端的順序排在欄位前面。
  f.click('[data-condition-source="prescreen"]');
  // 裁定 2026-10-29-27：第四步照舊版拿掉編製分錄較少的人員與使用較少的科目，原本也停用的 rarePreparer 不再列出；第一次失敗：FrontendMapping 收據 20261030-170713163-f548633afe0749a791469b97942983be。
  assert.deepEqual(disabledKeys(f, 'data-add-prescreen'), ['prepPeriodApproval', 'weekendApproval', 'holidayApproval', 'nonAuthorizedPreparer']);
  const prescreenNote = key => describedText(f, f.$('[data-add-prescreen="' + key + '"]'));
  for (const key of ['prepPeriodApproval', 'weekendApproval', 'holidayApproval']) assert.equal(prescreenNote(key), unmapped('傳票核准日'), key);
  assert.equal(prescreenNote('nonAuthorizedPreparer'), '尚未匯入授權編製人員清單，請先在第二步匯入；' + unmapped('傳票建立人員'));
  f.$('[data-add-prescreen="weekendApproval"]').fire('click');
  assert.deepEqual(rulesOf(f), [], 'a disabled prescreen rule adds nothing');

  // 回第三步配好欄位、匯入授權編製人員清單之後，每個入口都能用。
  mapEverything(f);
  f.store.setAuthorizedPreparerState({ rowCount: 2 });
  assert.deepEqual(disabledKeys(f, 'data-add-prescreen'), []);
  assert.deepEqual(disabledKeys(f, 'data-add-shortcut'), []);
  assert.deepEqual(disabledKeys(f, 'data-add-field'), []);
  assert.deepEqual(disabledKeys(f, 'data-template-key'), []);
  assert.deepEqual(CARD_KEYS.filter(key => card(f, key).disabled), []);
  f.click('[data-add-prescreen="weekendApproval"]');
  assert.deepEqual(prescreenWire(f), [[['prescreen', 'weekendApproval']]]);
});

test('the field menus on condition cards disable unmapped fields and a condition already using one says why and is not previewed', async () => {
  const f = fieldPane({ availableGlFields: PARTLY_MAPPED });
  f.click('[data-add-compound="group"]');
  const option = value => f.$('[data-child-kind]').querySelectorAll('option').find(node => node.getAttribute('value') === value);
  for (const [value, label] of [['field:approvalDate', '傳票核准日'], ['field:createdBy', '傳票建立人員'], ['field:approvedBy', '傳票核准人員'], ['field:sourceModule', '分錄來源模組']]) {
    assert.equal(option(value).disabled, true, value);
    assert.equal(option(value).textContent, label + '（尚未選擇來源欄，請回第三步欄位配對補上）', value);
  }
  assert.equal(option('type:manualAuto').disabled, true);
  // 裁定 2026-11-03-22 把子條件組類型選單的「人工或自動」改成「人工/自動分錄」，和第三步的欄位同名。
  // 第一次失敗：FrontendMapping 收據 20261103-101157066-e201e78933f2478c9fa86b8c916700da（當時先停在找不到欄位條件分頁；
  // 改掉分頁識別後直接執行這個檔，停在這一句）。
  assert.equal(option('type:manualAuto').textContent, '人工/自動分錄（' + unmapped('人工/自動分錄') + '）');
  for (const value of ['field:glDate', 'field:lineItem', 'type:drCrOnly']) assert.equal(option(value).disabled, false, value);
  assert.equal(option('field:glDate').textContent, '總帳日期');
  // 就算選到停用的項目再按「加入」，也不會加入子條件。
  for (const value of ['field:createdBy', 'type:manualAuto']) {
    f.$('[data-child-kind]').value = value;
    f.click('[data-child-add]');
    assert.deepEqual(plain(f.draft().groups[0].conditions[0].conditions), [], value + ' adds nothing');
  }

  // 草稿裡已經有用到未配對欄位的條件（例如舊情境，或改了配對之後）：條件卡片寫出原因，預覽前就擋下、不送出。
  f.store.setFilterDraft({ name: '合成條件', rationale: '合成', groups: [{ join: 'AND', matchScope: 'row', conditions: [
    { type: 'fieldValue', join: 'AND', field: 'approvedBy', operator: 'isBlank', values: [] },
    { type: 'dateRange', join: 'AND', field: 'approvalDate', from: '2025-01-01', to: '2025-01-31' },
    { type: 'prescreen', join: 'AND', prescreenKey: 'weekendApproval' }] }] });
  const row = index => f.$('.rule-row[data-gi="0"][data-ri="' + index + '"]');
  assert.ok(screenTexts(row(0)).includes(unmapped('傳票核准人員')), 'the field condition says why');
  const dateOption = value => row(1).querySelector('[data-rule-bind="field"]').querySelectorAll('option').find(node => node.getAttribute('value') === value);
  assert.equal(dateOption('approvalDate').disabled, true);
  assert.notEqual(dateOption('approvalDate').getAttribute('selected'), null, 'the saved field stays chosen');
  assert.equal(dateOption('glDate').disabled, false);
  assert.ok(screenTexts(row(1)).includes(unmapped('傳票核准日')), 'the old date range condition says why');
  // 預篩選條件也一樣：樣態選單照樣寫條件的名稱，原因寫在卡片上。
  const chosen = row(2).querySelector('[data-pattern-subject]').querySelectorAll('option').find(node => node.getAttribute('selected') !== null);
  assert.equal(chosen.getAttribute('value'), 'prescreen:weekendApproval');
  assert.equal(chosen.textContent, '週末核准');
  assert.ok(screenTexts(row(2)).includes(unmapped('傳票核准日')), 'the prescreen condition says why');

  f.click('[data-action="preview-scenario"]'); await settle();
  assert.equal(f.requests.filter(request => request.action === 'filterPreview').length, 0, 'nothing is sent');
  const notice = f.$('[data-bind="scenario-notice"]');
  assert.equal(notice.hidden, false);
  for (const text of ['第 1 組第 1 條：' + unmapped('傳票核准人員'), '第 1 組第 2 條：' + unmapped('傳票核准日'), '第 1 組第 3 條：' + unmapped('傳票核准日')])
    assert.ok(notice.textContent.includes(text), text + ' in ' + notice.textContent);

  mapEverything(f);
  for (const index of [0, 1, 2]) assert.doesNotMatch(screenTexts(row(index)).join('\n'), /尚未選擇來源欄/, 'row ' + index);
  f.click('[data-action="preview-scenario"]'); await settle();
  assert.equal(f.requests.filter(request => request.action === 'filterPreview').length, 1, 'once mapped the scenario can be previewed');
});

// 接手 FilterTemplatesFrontendTests 原本核對兩個現金範例標了 requires: 'accountMapping' 的斷言：同一階段拿掉範例上的 requires，
// 範例的前置條件改由條件內容查和後端同一張需求表。沒有科目分類時停用，原因和固定資產與費用那張卡同一句，否則套用後要到預覽才被後端擋下。
// 現有程式已經這樣做，這則測試沒有第一次失敗。
// 2026-10-29 範例改在「常用篩選條件」分頁一直列出（原本在自訂分頁按「從範例開始」展開），卡片拿掉字母，測試名稱跟著改；
// 第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
test('the two cash examples stay disabled until the account mapping has categories and give the fixed-asset card reason', () => {
  for (const mapping of [null, { hasAnyCategory: false, hasRevenue: false, hasCounterpart: false }]) {
    const f = buildFixture({ accountMapping: mapping });
    for (const key of ['cashDebitNonCashCredit', 'cashCreditNonCashDebit']) {
      const button = f.$('[data-template-key="' + key + '"]');
      assert.equal(button.disabled, true, key);
      assert.equal(describedText(f, button), cardReason(f, FIXED_ASSET), key + ' gives the fixed-asset card reason');
      button.fire('click');
      assert.deepEqual(rulesOf(f), [], key + ' adds nothing');
    }
  }
  const f = buildFixture({ accountMapping: { hasAnyCategory: true, hasRevenue: false, hasCounterpart: false } });
  f.click('[data-template-key="cashDebitNonCashCredit"]');
  assert.deepEqual(rulesOf(f), [['specialAccountCategoryPair']]);
});
