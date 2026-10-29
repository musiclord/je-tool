'use strict';
require('./support/resource-guard.cjs');
// 第五步組條件的情境測試（收尾四項第一項第 5 批，2026-10-13）。
//
// 情境 10「第五步常用篩選條件的卡片與範例」：審計員在四種科目配對狀態下看常用篩選條件的卡片與依審計目的的條件，
// 加入「貸方為收入但借方非一般對方科目」那張卡看說明，並套用每一個情境範例。2026-10-29 卡片與範例合併成「常用篩選條件」分頁、
// 拿掉卡片字母，原本要先展開「從範例開始」。2026-10-16 起另在「預篩選」分頁逐一加入每一條預篩選規則，
// 看前置不足的停用原因，並從分頁加入「未預期借貸組合」後查看傳票數量與分錄筆數。
// 情境 11「第五步自訂條件與條件組」：審計員在自訂分頁從各家族各加一條、組成兩組，看組內條件的組合方式，
// 移除一條，用「新增條件到」換加入的組，改成 OR 再改成同一傳票，加借貸科目組合並切換五種查找方式，
// 在分類清單勾選分類，最後預覽，預覽的錯誤標在條件列上。
//
// 每個測試都執行正式的 state.js、ui-core.js、filter-values.js、filter-vouchers.js 與第五步的五個檔案（steps/filter-step-*.js 與 filter-step.js），
// 在假 DOM 上畫出第五步並用點擊與選單操作；合成資料只控制邊界。
// 第五步的儲存、另存、移除與重新檢查屬於情境 12、13（第 9 批），這個檔不寫那些流程的斷言。
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
//   AccountTaxonomyFrontendTests.RuleControls_UseTaxonomyIdentitiesInsteadOfLegacyCategoryLabels。
// 對照表與改壞檢查紀錄見 artifacts/review/closing-four-items-20261013/impl-item1-batch5.md。
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { batch9TreeDom, readWwwroot, exposeAt, plain, settle, bootFullApp, flush, FILTER_STEP_PARTS } = require('./support/frontend-fixtures.cjs');
const { contractJetApi } = require('./support/action-contract.cjs');

const ALL_MAPPED = { hasAnyCategory: true, hasRevenue: true, hasCounterpart: true };

// 本檔自己的夾具：和共用模組的 filterOperationFixture 相同，執行正式腳本並訂閱重繪；
// 另外掛出 toWireDraft（情境 11 要看草稿送出的形狀），並記下預覽與傳票分頁的請求。
// 依計畫第 7 節第 5 點，只服務這個檔的夾具留在這裡，第 11 批整併時再決定要不要收進共用模組。
// availableGlFields 是後端 project.load 與 mapping.commit.gl 回傳的可用欄位清單（預篩選收斂計畫階段四，2026-10-23 加入）；
// 沒給時已確認的配對不帶清單，畫面不檢查欄位，和後端沒有清單時相同。
function buildFixture(options = {}) {
  const { accountMapping = ALL_MAPPED, glMapping = null, rdeFields = [], taxonomy = null, saved = [], availableGlFields = null,
    project = { projectId: 'filter-build-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31', lastPeriodStart: '2025-12-01' } } = options;
  const dom = batch9TreeDom(), requests = [];
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const context = vm.createContext({ window, document: dom.document });
  // 第五步拆成五個檔案（階段十）：前四個先載入，主檔再掛三個函式；syncViewState 在主檔是轉呼叫，名稱仍看得到。第一次失敗：FrontendMapping 收據 20261025-053146514-770b4770cad34a55a1f25bee3a62b08f。
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
  if (taxonomy) store.setTaxonomy(taxonomy);
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
  const draft = () => store.getState().filter.draft;
  return { window, store, dom, requests, api, $, $$, click, choose, draft, wire: () => plain(api.toWireDraft(draft())) };
}

const customPane = options => { const f = buildFixture(options); f.click('[data-condition-source="custom"]'); return f; };
// 卡片以代號識別（Ui.FILTER_COMMON_CONDITIONS 的 key）。2026-10-29 拿掉卡片字母，原本以記字母的屬性找卡片；
// 本檔因此改的選擇器、測試名稱與期望值，第一次失敗都是 FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
const card = (f, key) => f.$('[data-common-card="' + key + '"]');
// 停用的卡最後一段說明是停用原因。固定資產與費用、收入對方科目兩張卡原本另有固定的判定說明排在前面，2026-10-29 拿掉，只剩停用原因。
const cardReason = (f, key) => card(f, key).querySelectorAll('.picker-card__note').at(-1).textContent;
const rulesOf = f => plain(f.draft().groups.map(group => group.rules.map(rule => rule.type)));

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
// 使用者 2026-10-14 第三輪勾選頁確認 R3-15 的逐字句：還沒匯入與匯入了但沒有任何分類同一句，缺收入或對方分類時寫出分類名稱
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
      assert.equal(button.getAttribute('aria-disabled'), on ? null : 'true', state.name + ' ' + key + ' aria-disabled');
      assert.equal(!!button.closest('.filter-unavailable'), !on, state.name + ' ' + key + ' listed under unavailable cards');
      notes[state.name + key] = on ? '' : cardReason(f, key);
      if (!on) assert.ok(notes[state.name + key], state.name + ' ' + key + ' explains why');
      // 停用的卡按了不會加入條件。
      button.fire('click');
      assert.deepEqual(plain(f.draft().groups.flatMap(group => group.rules.map(rule => rule.type))), on ? [MAPPING_CARD_TYPES[key]] : [], state.name + ' ' + key + ' click');
      if (on) f.click('[data-common-card="' + key + '"]');
    }
    for (const key of OTHER_CARDS) assert.equal(card(f, key).disabled, false, state.name + ' ' + key + ' never depends on mapping');
  }
  const noCategory = '科目配對中尚無任何科目設定分類，請先在第四步設定';
  const noRevenue = '科目配對中尚無歸類為「Revenue」的科目，請先在第四步設定';
  const noCounterpart = '科目配對中尚無歸類為「Receivables」「Cash」或「Receipt in advance」的科目，請先在第四步設定';
  // 預篩選收斂計畫階段四（2026-10-23）改寫這兩段：停用原因改由前端照抄後端的規則需求表。後端對三張收入卡一直只看收入與對方分類
  //（改動前的 FilterScenario.cs 也是），原本前端在還沒匯入時另寫「尚無任何科目設定分類」，兩邊不同句；收入對方科目那張卡缺收入與對方分類時，
  // 後端照使用者 2026-10-13 確認的「分開說，各寫一句」寫兩句，前端原本只寫第一句。
  // 第一次失敗：FrontendMapping 收據 20261023-044331305-cc3be698efaa446eab8994f89154b247（季末前借記收入那張卡寫成缺收入分類的句子）。
  // 改後每張卡在「還沒匯入」與「匯入了但沒有任何分類」寫同一句，仍是 R3-15 那筆紀錄說的情形。
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
  // 第九節 G2：尾數不再是樣態型別，以預篩選卡檢查同一選單與收入資格。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  const commonOptions = mapping => {
    const f = buildFixture({ accountMapping: mapping });
    // 2026-10-16 預篩選條件改成條件總覽的分頁（使用者要求縮短套用預篩選條件的操作），改從分頁加入一條；
    // 第一次失敗：缺少 [data-check-category="prescreen"]。
    f.click('[data-condition-source="prescreen"]'); f.click('[data-add-prescreen="suspiciousKeywords"]');
    const select = f.$('.rule-row [data-pattern-subject]');
    assert.ok(select, 'the pattern row offers a pattern menu');
    const group = select.querySelectorAll('optgroup').find(node => node.getAttribute('label') === '常用篩選條件');
    return group ? group.querySelectorAll('option').map(option => option.getAttribute('value')).sort() : [];
  };
  const always = ['preparerEqualsApprover'];
  assert.deepEqual(commonOptions(null), always);
  assert.deepEqual(commonOptions({ hasAnyCategory: true, hasRevenue: false, hasCounterpart: true }), always, 'imported but no revenue');
  assert.deepEqual(commonOptions({ hasAnyCategory: true, hasRevenue: true, hasCounterpart: false }),
    ['manualRevenueEntry', 'preparerEqualsApprover', 'revenueDebitNearQuarterEnd'], 'the revenue counterpart condition still needs a counterpart');
  assert.deepEqual(commonOptions(ALL_MAPPED),
    ['manualRevenueEntry', 'preparerEqualsApprover', 'revenueDebitNearQuarterEnd', 'revenueWithoutNormalCounterpart']);
});

// 取代當時區分停用原因的 C# 方法裡常用查核條件（2026-10-29 改稱「依審計目的」）的部分（2026-10-08 使用者要求重新設計自訂篩選條件：
// 停用時按鈕帶 disabled，原因寫在該項下方並以 aria-describedby 連到按鈕；原因和固定資產與費用那張卡用同一個判斷）。
// 第一次失敗收據（原 C# 註解）：20260907-135727110、20260908-024320083。
// 2026-10-29 區塊改名、卡片拿掉字母，測試名稱跟著改；第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
test('disabled purpose checks point to their reason, share the fixed-asset card reason and add nothing when pressed', () => {
  for (const mapping of [null, { hasAnyCategory: false, hasRevenue: false, hasCounterpart: false }]) {
    const f = customPane({ accountMapping: mapping });
    f.click('[data-check-category="accounts"]');
    for (const key of ['accountCategory', 'categoryPair']) {
      const button = f.$('[data-add-check="' + key + '"]');
      assert.equal(button.disabled, true, key);
      const describedBy = button.getAttribute('aria-describedby');
      assert.ok(describedBy, key + ' is described');
      const note = f.$('#' + describedBy);
      assert.ok(note, key + ' reason exists');
      assert.equal(note.closest('.filter-check'), button.closest('.filter-check'), key + ' reason sits under the same check');
      assert.equal(note.textContent, cardReason(f, FIXED_ASSET), key + ' uses the same reason as the fixed-asset card');
      button.fire('click');
      assert.deepEqual(rulesOf(f), [], key + ' adds nothing');
    }
    // 其他常用查核條件不受科目配對影響。
    const enabled = f.$('[data-add-check="debitOrCreditOnly"]');
    assert.equal(enabled.disabled, false); assert.equal(enabled.getAttribute('aria-describedby'), null);
  }
  const f = customPane();
  f.click('[data-check-category="accounts"]');
  assert.equal(f.$('[data-add-check="categoryPair"]').disabled, false);
  assert.equal(f.$('#filter-check-note-categoryPair'), null);
  f.click('[data-add-check="categoryPair"]');
  assert.deepEqual(rulesOf(f), [['specialAccountCategoryPair']]);
});

// 取代 ScreenWordingReviewFrontendTests.K3_CashCounterpartExplanation_UsesExactApprovedText。
// 逐字出處：docs/history/specs/2026-10-06-kct-gl-requirements-plan.md「#### K3」一節寫明收入對方科目那張卡（當時以字母稱呼）的說明逐字是這一句，
// 同檔處理方式表「K1、K2、K3、K10 | 照建議 | 第一批實作」是使用者的裁定。
// 第一次失敗收據（計畫檔 K3 的實作紀錄）：20261006-030038300-b79b8f5c1edd404f880a2c56319d2a7c。
const K3_CASH_NOTE = '現金不算一般對方科目；要排除現金銷貨，改用預篩選條件『未預期借貸組合』。';
// 原本 K3 定下這句同時放在卡片與條件列，這裡斷言卡片、停用卡片與條件列都有它。條件列和卡片同在第五步的畫面上，
// 同一句出現兩次，使用者 2026-10-29 裁定「拿掉卡片上的」（原文見預篩選收斂計畫附錄一）。卡片與停用卡片的斷言依預期失敗，
// 首敗收據 20261029-051153423-e931c5bb9d694a55bd6463da1ca0aa64。條件列逐字顯示這句的斷言不變。
// 同日拿掉卡片字母，測試名稱原本以字母稱呼這張卡，改用條件名稱（第一次失敗見上面的 card()）。
test('the revenue counterpart condition row explains cash with the approved sentence; the card no longer repeats it', () => {
  const f = buildFixture();
  assert.equal(card(f, COUNTERPART).querySelectorAll('.picker-card__note').length, 0, 'the card shows only its name');
  f.click('[data-common-card="' + COUNTERPART + '"]');
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row.querySelectorAll('.rule-explanation').some(node => node.textContent === K3_CASH_NOTE), 'condition row shows the approved sentence');
  // 停用時卡片只寫停用原因。
  const blocked = buildFixture({ accountMapping: { hasAnyCategory: true, hasRevenue: true, hasCounterpart: false } });
  const blockedNotes = card(blocked, COUNTERPART).querySelectorAll('.picker-card__note').map(node => node.textContent);
  assert.equal(blockedNotes.length, 1); assert.notEqual(blockedNotes[0], K3_CASH_NOTE);
});

// 同一次裁定也涵蓋固定資產與費用那張卡：先新增並配對分類的說明不寫在卡片上，加入後在條件列寫一次。這則是新增的測試，沒有第一次失敗。
// 同日拿掉卡片字母，測試名稱原本以字母稱呼這張卡，改用條件名稱（第一次失敗見上面的 card()）。
test('the fixed-asset card no longer repeats its setup note; the condition row still shows it', () => {
  const f = buildFixture();
  const note = f.window.JetUi.FILTER_COMMON_CONDITIONS.find(item => item.key === FIXED_ASSET).description;
  assert.ok(note, 'the fixed-asset card keeps a setup note for the condition row');
  assert.equal(card(f, FIXED_ASSET).querySelectorAll('.picker-card__note').length, 0, 'the card shows only its name');
  f.click('[data-common-card="' + FIXED_ASSET + '"]');
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row.querySelectorAll('.rule-explanation').some(node => node.textContent === note), 'condition row shows the fixed-asset note');
});

// 2026-10-30 結案前的獨立複審查到：拿掉卡片上的說明後，另一張卡的說明在條件列直接看得到，這張卡的說明卻只在「查找說明」裡，
// 上面那則測試分不出來。借貸組合的說明收進「查找說明」，是使用者在 KCT 需求計畫第一批第 13 則說「下方多出來的註解顯得整體排版很雜亂」後的處理，
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

// 原本取代 ScreenWordingReviewFrontendTests 裡核對卡片字母的方法：分頁與標題逐字是使用者 2026-10-08 定的名稱
//（原話見 docs/history/specs/2026-10-06-kct-gl-requirements-plan.md「驗收發現與裁定（2026-10-08）」），每張卡前面有一個對讀屏軟體隱藏的字母。
// 使用者 2026-10-29 裁定合併成「常用篩選條件」分頁、拿掉卡片字母，分頁上半「單項條件」是十張卡片
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
test('the common tab reads 常用篩選條件, lists the ten cards in order under 單項條件 and shows no card letters', () => {
  const f = buildFixture();
  assert.equal(f.$('[data-condition-source="common"]').textContent, '常用篩選條件');
  assert.equal(f.$('[data-condition-pane="common"]').hidden, false, 'the common tab is the first one shown');
  assert.equal(f.$('[data-condition-pane="common"] [data-common-cards] .filter-source-block__title').textContent, '單項條件');
  const cards = f.$$('[data-condition-pane="common"] [data-common-card]');
  assert.deepEqual(cards.map(node => [node.getAttribute('data-common-card'), node.textContent]), COMMON_CARDS);
  for (const node of cards) assert.equal(node.querySelectorAll('.picker-card__letter').length, 0, node.getAttribute('data-common-card') + ' has no letter');
  assert.equal(f.$('.condition-picker__title'), null, 'the old heading is gone');
  assert.doesNotMatch(screenTexts(f.$('[data-condition-pane="common"]')).join('\n'), /A 到 J/);
});

// 使用者 2026-10-07 裁定動機每條自訂條件多一行「自訂：」；當時名稱用卡片字母串接，同組有自訂條件時加「+自訂」
//（第一次失敗收據，原 C# 註解：20261007-035823939-41943206b5f747f1b7fb8dfe79f987d7）。
// 使用者 2026-10-29 裁定名稱改用和自訂條件相同的寫法：依加入順序列出前兩項條件，卡片帶入的用卡片名稱，超過兩項加上總數；
// 動機每行不再以字母開頭（docs/history/specs/2026-10-22-prescreen-tech-debt-deferred-items-plan.md「2026-10-29 合併常用篩選條件」）。
// 原本名稱期望值是字母串接，改成新寫法的寫死字串；第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
test('card names name the draft, a custom condition in the same group is named after it and the rationale adds a 自訂 line', () => {
  const f = buildFixture();
  f.click('[data-common-card="blankDescription"]'); f.click('[data-common-card="specificTrailingDigits"]');
  assert.equal(f.draft().name, '空白摘要、特定尾數(如 999999/000000 結尾)');
  f.click('[data-common-card="specificTrailingDigits"]');
  assert.equal(f.draft().name, '空白摘要');
  f.click('[data-condition-source="custom"]'); f.click('[data-add-field="description"]');
  assert.equal(f.draft().name, '空白摘要、傳票摘要 包含任一文字「」');
  const lines = f.draft().rationale.split('\n');
  assert.equal(lines.length, 2);
  assert.equal(lines[0], '空白摘要');
  assert.match(lines[1], /^自訂：/);
  f.click('[data-action="add-set"]'); f.click('[data-add-field="docNum"]');
  assert.equal(f.draft().name, '空白摘要、傳票摘要 包含任一文字「」等3項條件');
});

// 範例全部可用的案件狀態：查核截止日、期末財報準備日、核准人員與建立人員都已配對，有一個文字型攸關資料元素欄位。
const EXAMPLE_READY = { glMapping: { approveBy: 'Checker', createBy: 'Maker' },
  rdeFields: [{ fieldId: 'rde.dept', sourceColumn: 'Dept', label: '部門', valueType: 'text' }] };
const EXAMPLE_KEYS = ['cashDebitNonCashCredit', 'cashCreditNonCashDebit', 'nonBusinessDayExcludingDates', 'periodEndManual', 'blankDescriptionOrApprover',
  'preparerLargeAmount', 'weekendManualDescription', 'manualDepartment', 'accountPairLargeAmount', 'preparationManualHoliday'];
// 使用者 2026-10-08 要求重新設計自訂篩選條件時的原話（同上「驗收發現與裁定（2026-10-08）」）：
// 「名稱 `舊表 X:XXX` 可以改為其他名稱，因為 "舊表" 聽起來非常不恰當且不專業」，以及「一下子有範例1範例2的，一下子註解顯示什麼 "舊表P+S"」。
const CATALOGUE_CODES = /舊表|A 到 U|A–U|範例 ?\d|工作簿範例|[A-U](\+[A-U])+/;

// 原本取代 ScreenWordingReviewFrontendTests 裡核對卡片字母的方法的自訂分頁與範例部分，以及 FilterTemplatesFrontendTests.FilterStep_OffersTemplatesAndTheCombinationCard。
// 自訂分頁沒有收合鈕的第一次失敗收據（原 C# 註解）：20261012-060708654-d3fdcf4d785843d3b09ab6fadd009c5c。
// 每個範例都在新的案件畫面套用（空白草稿直接套用，不會跳出「還沒儲存」的詢問，那屬於第 9 批的儲存流程）。
// 使用者 2026-10-29 裁定拿掉「從範例開始」按鈕，範例一直列在「常用篩選條件」分頁下半的「情境範例」。原本斷言清單一開始收起、
// 按鈕展開後才列出、套用後收起；改成斷言清單一開始就在常用篩選條件分頁列出、沒有展開按鈕、套用後清單仍在。
// 原本略過的是放卡片字母的分頁，現在範例和卡片同一頁且卡片不再有字母，所以整個條件總覽都不能出現目錄代號。
// 第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2（examples start closed）。
test('every example applies under its own name and the condition screens never show catalogue codes or card letters', () => {
  const offered = buildFixture(EXAMPLE_READY);
  const list = offered.$('[data-example-list]');
  assert.equal(list.hidden, false, 'examples are listed from the start');
  assert.equal(list.closest('[data-condition-pane]').getAttribute('data-condition-pane'), 'common', 'examples sit on the common tab');
  assert.equal(list.closest('.filter-source-block').querySelector('.filter-source-block__title').textContent, '情境範例');
  assert.equal(offered.$('[data-condition-pane="common"]').hidden, false);
  assert.equal(offered.$('[data-action="toggle-examples"]'), null, 'there is no button to open the examples');
  assert.deepEqual(offered.$$('[data-template-key]').map(button => button.getAttribute('data-template-key')), EXAMPLE_KEYS);
  assert.doesNotMatch(screenTexts(offered.$('[data-filter-pane="filter"]')).join('\n'), CATALOGUE_CODES);
  // 自訂分頁沒有收合鈕，也沒有退場的排除區或待判定說法。
  offered.click('[data-condition-source="custom"]');
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
    assert.ok(f.draft().groups.some(group => group.rules.length), key + ' brings conditions');
    assert.equal(f.$('[data-example-list]').hidden, false, key + ' leaves the list on the common tab');
    const texts = screenTexts(f.$('[data-filter-pane="filter"]'));
    assert.doesNotMatch(texts.join('\n'), CATALOGUE_CODES, key);
    assert.doesNotMatch(f.draft().name + '\n' + f.draft().rationale, CATALOGUE_CODES, key);
    // 條件組裡沒有常用篩選條件的卡片、欄位按鈕或依審計目的的條件按鈕。
    assert.equal(f.dom.content.querySelectorAll('.scenario-set [data-common-card], .scenario-set [data-add-field], .scenario-set [data-add-check]').length, 0, key);
    if (key === 'manualDepartment') {
      // 只有一個文字型攸關資料元素欄位時直接帶入它。
      assert.deepEqual(plain(f.draft().groups[0].rules.map(rule => [rule.type, rule.fieldId ?? null])), [['manualAuto', null], ['fieldValue', 'rde.dept']]);
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
// 「使用者對兩項待決事項的選擇（2026-10-11）」，使用者選「統一用 AND、OR」，選項說明原文是「下拉照舊，左側欄位與摘要改寫 AND、OR。」
// 比對範圍的選項名稱、主要條件與佐證的標示沒有使用者逐字裁定，只比對意思；選項值是送出的契約，精確比對。
// 第一次失敗收據（原 C# 註解）：20260907-135727110。
// 第九節 G3 停用比對範圍與主要條件；保留 AND、OR、可讀名稱及送出結構檢查。
// 首次失敗：20261014-135637069-066eb7e965824f89bf2b8671156595f1。
test('two conditions offer AND or OR and explicit voucher conditions replace the scope menu', () => {
  const f = customPane();
  f.click('[data-add-field="description"]');
  assert.equal(f.$('[data-set-combinator]'), null, 'one condition has no combination choice');
  f.click('[data-add-field="docNum"]');
  const combinator = () => f.$('[data-set-combinator]');
  assert.deepEqual(combinator().querySelectorAll('option').map(option => option.textContent), ['AND（全部符合）', 'OR（任一符合）']);
  assert.deepEqual(combinator().querySelectorAll('option').map(option => option.getAttribute('value')), ['AND', 'OR']);
  assert.ok(combinator().getAttribute('aria-label'), 'the combination has an accessible name');
  assert.equal(f.$('[data-group-bind="matchScope"]'), null);
  assert.equal(f.$('.filter-group-menu'), null);

  f.choose('[data-set-combinator]', 'OR');
  assert.deepEqual(plain(f.draft().groups[0].rules.map(rule => rule.join)), ['OR', 'OR']);
  assert.equal(combinator().value, 'OR');

  f.choose('[data-set-combinator]', 'AND');
  assert.equal(f.draft().groups[0].matchScope, 'row');
  assert.deepEqual(plain(f.draft().groups[0].rules.map(rule => rule.join)), ['AND', 'AND']);
  assert.equal(combinator().disabled, false);
  assert.equal(combinator().value, 'AND');
  assert.equal(combinator().querySelectorAll('option').length, 2);
  assert.equal(f.$('[data-action="make-primary"]'), null);
  assert.deepEqual(f.wire().groups.map(group => [group.matchScope, group.rules.map(rule => rule.join)]), [['row', ['AND', 'AND']]]);
  f.click('[data-add-compound="voucher"]');
  assert.equal(f.draft().groups[0].rules[2].quantifier, 'any');
  assert.match(f.$('.rule-row--compound').textContent, /同一筆分錄.*整張傳票/);
});

// 取代 FilterBuilderFrontendTests.EveryRuleRow_IsOneSentenceWithoutATypeDropdown 與 FilterWorkspaceFrontendTests.SimpleConditions_HaveVisibleRemovalAndDiscoverableGroups。
// 移除鈕的名稱沒有使用者逐字裁定，只比對有「移除」。第一次失敗收據（原 C# 註解）：20260908-064216425。
test('each family adds a one-sentence row without a type menu, and removing one row leaves every other condition', () => {
  const f = customPane();
  f.click('[data-add-field="description"]');
  f.click('[data-check-category="accounts"]'); f.click('[data-add-check="accountCategory"]');
  assert.match(f.$('[data-action="add-set"]').textContent, /條件組/);
  f.click('[data-action="add-set"]');
  f.click('[data-check-category="amounts"]'); f.click('[data-add-check="trailingZeros"]');
  f.click('[data-check-category="people"]'); f.click('[data-add-check="manualEntries"]');
  // 第九節 G1：科目分類預設改為子條件組，仍完整檢查每條與整組的移除。首次失敗：20261014-132501058-ef503fecc9824f3b9e6f009a7b154327。
  // 第九節 G2：尾數改成兩條欄位條件的子條件組，保留全部移除斷言。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  assert.deepEqual(rulesOf(f), [['fieldValue', 'group'], ['group', 'manualAuto']]);
  const rows = f.$$('.rule-row');
  assert.equal(rows.length, 8);
  for (const row of rows) {
    assert.equal(row.querySelector('[data-rule-bind="type"]'), null, 'no type menu');
    assert.equal(row.querySelector('.rule-row__type'), null); assert.equal(row.querySelector('.rule-row__family'), null);
    const remove = row.querySelector(row.classList.contains('rule-row--compound') ? '[data-compound-remove]' : '[data-action="remove-rule"]');
    assert.ok(remove, 'every row can be removed'); assert.match(remove.textContent, /移除/);
  }
  const row = (gi, ri) => f.$('.rule-row[data-gi="' + gi + '"][data-ri="' + ri + '"]');
  assert.ok(row(0, 0).querySelector('[data-value-kind]'), 'a field row chooses how to compare');
  assert.equal(row(0, 0).querySelector('[data-value-key="field"]'), null, 'the field is fixed on the row, not a second menu');
  assert.equal(row(0, 1).querySelector('[data-account-subject]'), null, 'classification has no hidden direction');
  assert.ok(row(0, '1.0').querySelector('[data-rule-bind="drCr"]'), 'the explicit child chooses debit or credit');
  assert.ok(row(1, '0.0').querySelector('[data-value-kind]'), 'the tail row uses the shared field comparison');
  assert.equal(f.$('.filter-draft-note'), null);

  const second = plain(f.draft().groups[1]), first = plain(f.draft().groups[0].rules[0]);
  row(0, 1).querySelector('[data-compound-remove]').fire('click');
  assert.deepEqual(rulesOf(f), [['fieldValue'], ['group', 'manualAuto']]);
  assert.deepEqual(plain(f.draft().groups[0].rules[0]), first);
  assert.deepEqual(plain(f.draft().groups[1]), second);
});

// 取代 FilterWorkspaceFrontendTests.GroupEditor_DoesNotRelocateTheConditionCatalog。
test('"add conditions to" moves the target group and the groups never embed the condition catalogue', () => {
  const f = buildFixture();
  // 卡片改用代號識別（2026-10-29 拿掉卡片字母，第一次失敗見本檔的 card()）。
  f.click('[data-common-card="blankDescription"]');
  assert.equal(f.$('[data-condition-target]'), null, 'one group needs no target choice');
  // 第九節 G2：空白摘要與特定尾數兩張卡都以欄位條件加入，分組與隔離斷言不變。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  assert.match(f.$('.rule-row[data-gi="0"][data-ri="0"] .filter-rule-heading').textContent, /傳票摘要/);
  f.click('[data-action="add-set"]');
  const target = f.$('[data-condition-target]');
  assert.ok(target.closest('.filter-entry'), 'the target choice sits with the condition catalogue');
  assert.equal(target.closest('.scenario-set'), null);
  assert.deepEqual(target.querySelectorAll('option').map(option => option.getAttribute('value')), ['0', '1']);
  assert.equal(target.value, '1', 'the new group is the target');
  assert.doesNotMatch(screenTexts(f.$('[data-filter-pane="filter"]')).join('\n'), /目前加入目標/);
  f.choose('[data-condition-target]', '0');
  f.click('[data-common-card="specificTrailingDigits"]');
  assert.deepEqual(rulesOf(f), [['fieldValue', 'fieldValue'], []]);
  f.click('[data-condition-source="custom"]'); f.click('[data-add-field="docNum"]');
  assert.deepEqual(rulesOf(f), [['fieldValue', 'fieldValue', 'fieldValue'], []]);
  assert.equal(f.dom.content.querySelectorAll('.scenario-set [data-common-card], .scenario-set [data-add-field], .scenario-set [data-add-check], .scenario-set [data-template-key]').length, 0);
  assert.equal(f.$('[data-inline-picker]'), null); assert.equal(f.$('[data-pick-for-group]'), null);
  assert.equal(f.dom.content.querySelectorAll('.scenario-set [data-select-group]').length, 0);
});

// 取代 FilterCategorySelectFrontendTests 的兩個方法（2026-10-02 分類多選的參數少一個，欄位標題顯示成內部鍵名，
// 「整張傳票不得有這些貸方分類」消失；收據 20261002-122456569）。分類欄位的標題沒有使用者逐字裁定，只比對意思。
test('the account pair card switches between five lookup modes and every category legend stays in plain words', () => {
  const f = customPane();
  f.click('[data-check-category="accounts"]'); f.click('[data-add-check="categoryPair"]');
  // 新加的組合條件預設借方應收、貸方收入，送出的是分類身分。
  assert.deepEqual(plain([f.draft().groups[0].rules[0].debitCategoryIds, f.draft().groups[0].rules[0].creditCategoryIds]),
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
    const [type, mode] = value.split('|'), rule = f.draft().groups[0].rules[0];
    assert.deepEqual([rule.type, rule.pairMode], [type, mode], value + ' draft');
    const wired = f.wire().groups[0].rules[0];
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

// 取代 AccountTaxonomyFrontendTests.RuleControls_UseTaxonomyIdentitiesInsteadOfLegacyCategoryLabels。
// 首次失敗收據（原 C# 註解）：20261004-075340533-87179f5019724ec89d7c19b4243fdb09。
const TAXONOMY = { revision: 3, categories: [
  { categoryId: 'builtin.revenue', label: 'Revenue', ordinal: 0, semanticRole: 'revenue', isBuiltIn: true },
  { categoryId: 'builtin.cash', label: 'Cash', ordinal: 1, semanticRole: 'cash', isBuiltIn: true },
  { categoryId: 'custom.ppe', label: '固定資產', ordinal: 2, semanticRole: 'others', isBuiltIn: false }] };
test('category ticks store each identity once and a renamed category keeps the condition and reads back the new name', () => {
  const f = customPane({ taxonomy: TAXONOMY });
  f.click('[data-check-category="accounts"]'); f.click('[data-add-check="accountCategory"]');
  const box = id => f.$('.rule-row [data-category-bind="categoryIds"][value="' + id + '"]');
  assert.deepEqual(f.$$('.rule-row [data-category-bind="categoryIds"]').map(input => input.getAttribute('value')), ['builtin.revenue', 'builtin.cash', 'custom.ppe']);
  // 第九節 G1 的分類位於明示方向後面；保留全部選取與改名斷言。首次失敗：20261014-132501058-ef503fecc9824f3b9e6f009a7b154327。
  const category = () => f.draft().groups[0].rules[0].rules[1];
  assert.deepEqual(plain(category().categoryIds), ['builtin.cash']);
  assert.equal(box('builtin.cash').checked, true);
  assert.equal(box('custom.ppe').closest('label').textContent, '固定資產', 'each tick shows the category name');
  box('custom.ppe').checked = true; box('custom.ppe').fire('change');
  box('custom.ppe').fire('change');
  assert.deepEqual(plain(category().categoryIds), ['builtin.cash', 'custom.ppe']);
  box('custom.ppe').checked = false; box('custom.ppe').fire('change');
  box('custom.ppe').checked = true; box('custom.ppe').fire('change');
  assert.deepEqual(plain(category().categoryIds), ['builtin.cash', 'custom.ppe']);
  assert.deepEqual(f.wire().groups[0].rules[0].rules[1].categoryIds, ['builtin.cash', 'custom.ppe']);
  // 完整的篩選條件讀回在兩條以上時才出現。
  f.click('[data-add-field="description"]');
  assert.match(f.$('.scenario-readback').textContent, /固定資產/);

  f.store.setTaxonomy({ revision: 4, categories: TAXONOMY.categories.map(c => c.categoryId === 'custom.ppe' ? { ...c, label: '不動產廠房設備' } : c) });
  assert.deepEqual(plain(category().categoryIds), ['builtin.cash', 'custom.ppe'], 'renaming keeps the identities');
  assert.equal(box('custom.ppe').checked, true);
  assert.match(f.$('.scenario-readback').textContent, /不動產廠房設備/);
  assert.doesNotMatch(f.$('.scenario-readback').textContent, /固定資產/);
  // 借貸科目組合的讀回同樣寫分類名稱，不寫分類身分。
  f.click('[data-add-check="categoryPair"]');
  const pairBox = f.$('.rule-row[data-gi="0"][data-ri="2"] [data-category-bind="debitCategoryIds"][value="custom.ppe"]');
  pairBox.checked = true; pairBox.fire('change');
  assert.deepEqual(plain(f.draft().groups[0].rules[2].debitCategoryIds), ['custom.ppe']);
  f.store.touch();
  const readback = f.$('.scenario-readback').textContent;
  assert.match(readback, /不動產廠房設備/);
  assert.doesNotMatch(readback, /builtin\.|custom\./);

  // 只有舊式單一分類名稱、沒有分類身分陣列的條件，畫面不替它換算成分類身分。
  const legacy = customPane({ taxonomy: TAXONOMY });
  legacy.store.setFilterDraft({ name: '舊條件', rationale: 'r', groups: [{ join: 'AND', matchScope: 'row', rules: [
    { type: 'specialAccountCategoryPair', join: 'AND', pairMode: 'drAndCr', categorySelection: 'node', debitCategory: 'Cash', creditCategory: 'Revenue' }] }] });
  assert.deepEqual(legacy.$$('.rule-row [data-category-bind]').filter(input => input.checked), []);
});

// 預覽送出的 payload：精確比對、不帶 `__` 鍵、空的組不送；錯誤標在畫面上對應的條件列（不比對原因句子）。
// 預覽錯誤標在條件列，原本由 FilterTemplatesFrontendTests.InvalidScenarioDetails_AreMarkedOnTheRuleRow 用原始碼字串檢查；
// 那個方法已由第五步修正（第 3 項）寫好取代的行為測試後刪除（artifacts/review/closing-four-items-20261013/impl-scenario-save.md 第 4 節），這裡只補預覽這一條路的行為。
test('preview sends the composed groups exactly and marks a rejected condition on its row', async () => {
  const f = buildFixture();
  f.click('[data-common-card="blankDescription"]');
  f.click('[data-action="add-set"]');
  f.click('[data-action="add-set"]');
  f.click('[data-common-card="specificTrailingDigits"]');
  f.click('[data-condition-source="custom"]'); f.click('[data-check-category="people"]'); f.click('[data-add-check="manualEntries"]');
  f.choose('[data-set-combinator][data-gi="2"]', 'OR');
  f.choose('[data-set-join][data-set-join-index="2"]', 'AND');
  // 第九節 G2：精確核對新欄位格式，其他請求與錯誤位置斷言保留。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  assert.deepEqual(rulesOf(f), [['fieldValue'], [], ['fieldValue', 'manualAuto']]);
  f.click('[data-action="preview-scenario"]');
  await settle();
  const request = f.requests.filter(r => r.action === 'filterPreview');
  assert.equal(request.length, 1);
  const filler = { field: 'description', values: [], mode: 'contains', normalization: 'preserve', from: '', to: '', drCr: 'debit', isManual: 'true',
    pairMode: 'exact', debitCategoryIds: ['builtin.receivables'], creditCategoryIds: ['builtin.revenue'], fieldId: '', operator: '',
    amountBasis: 'absolute', value: '', digits: '3', maxEntries: '', windowDays: '' };
  // 2026-10-29 拿掉卡片字母：名稱改用條件名稱的新寫法、動機每行不再以字母開頭、編輯來源改送第 2 版的卡片代號、
  // 來源代碼改成 common（原本是字母串接的名稱、第 1 版字母來源與舊來源代碼）。
  // 第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
  assert.deepEqual(plain(request[0].payload), {
    populationScope: 'auditPeriod',
    scenario: {
      name: '空白摘要、特定尾數(如 999999/000000 結尾)等3項條件',
      rationale: '空白摘要\n特定尾數(如 999999/000000 結尾)\n自訂：人工分錄',
      groups: [
        { join: 'AND', matchScope: 'row', rules: [{ join: 'AND', type: 'fieldValue', field: 'description', operator: 'isBlank', includeBlank: false }] },
        { join: 'AND', matchScope: 'row', rules: [
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
  assert.equal(f.draft().groups[0].rules[0].__commonCard, 'blankDescription');

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

// 使用者 2026-10-15 在待決事項勾選頁選 S2：Gui 原本在加入條件後固定等 7 秒，再核對條件仍是「選取中」。
// 這裡改用整個畫面的夾具（setTimeout 排進佇列），加入條件後跑完所有排定的計時器、各觸發一次 setInterval，
// 再核對同一件事；確認這個測試抓得到計時器回來取消選取後，才刪掉 Gui 的等待。
test('a condition added from the custom pane stays selected after every pending timer has run', async () => {
  const CASE = { projectId: '合成條件案', databaseProvider: 'sqlite' };
  const f = await bootFullApp([CASE]);
  f.click('[data-action="picker-open"]');
  f.reply('project.load', { project: { ...CASE, periodStart: '2025-01-01', periodEnd: '2025-12-31', currentStep: 1 } }); await flush();
  f.reply('log.recent', { messages: [] }); await flush();
  for (const kind of ['gl', 'tb']) {
    f.store.setImportResult(kind, { batchId: kind + '-source', columns: ['Account'], rowCount: 2 });
    f.store.setMappingCommitted(kind, { mode: kind === 'gl' ? 'signed' : 'direct', mapping: {} });
  }
  f.store.setLastRun('validate', { resultRef: { runId: 'v1', generatedUtc: '2026-01-01T00:00:00Z' },
    completenessTest: { eligibility: { isEligible: true, reason: null, warning: null } } });
  f.store.setAccountMappingState(ALL_MAPPED);
  f.store.setStepIndex(4); await flush();
  f.click('[data-condition-source="custom"]'); await flush();
  f.click('[data-add-field="docNum"]'); await flush();
  const selected = () => {
    const rows = f.dom.content.querySelectorAll('.rule-row--selected');
    return rows.length === 1 && rows[0].querySelector('.filter-rule-selected').textContent === '選取中';
  };
  assert.equal(f.state().filter.draft.groups[0].rules[0].field, 'docNum');
  assert.ok(selected(), 'the new condition is selected');
  f.timers.run();
  for (const entry of f.liveIntervals()) entry.fn();
  f.timers.run(); await flush();
  assert.ok(selected(), 'the condition is still selected after the timers ran');
});

// ------------------------------------------------------------------------------------------------
// 逐一點每一項常用查核條件的「加入」
// ------------------------------------------------------------------------------------------------

// 使用者 2026-10-15 在待決事項勾選頁選 S4：Gui 的 legacy-form-catalog 情境逐一加入及移除每一項常用查核條件，
// 核對名稱、所屬分類一次只展開一類及加入的條件型別。改在這裡用畫面上的按鈕做同一件事，之後刪掉那個 Gui 情境。
// 分類、名稱與型別逐字照那個情境原本的清單（tools/harness/gui-driver/GuiLegacyFormWorkflow.cs），不從畫面推導。
test('every common check is added by its own button with the exact type, opens only its category and can be removed', () => {
  const checks = [
    ['dates', 'postDateWeekend', '總帳日期在週末', 'fieldValue'],
    ['dates', 'postDateHoliday', '總帳日期在國定假日', 'fieldValue'],
    ['dates', 'postDateNonBusinessDay', '總帳日期在非營業日', 'fieldValue'],
    ['dates', 'docDateNonBusinessDay', '傳票核准日在非營業日', 'fieldValue'],
    ['dates', 'postPeriodApproval', '財報準備日起核准的分錄', 'prescreen'],
    ['dates', 'postDateRange', '指定日期區間', 'fieldValue'],
    ['dates', 'periodEndDays', '查核期末最後幾天', 'fieldValue'],
    ['amounts', 'largeAmount', '大額分錄', 'fieldValue'],
    ['amounts', 'trailingZeros', '金額尾數連續多個 0', 'group'],
    ['description', 'descriptionKeywords', '摘要含特定字詞', 'fieldValue'],
    ['description', 'blankDescription', '空白摘要', 'fieldValue'],
    ['accounts', 'debitOrCreditOnly', '只看借方或貸方', 'drCrOnly'],
    ['accounts', 'accountCategory', '科目屬於指定分類', 'group'],
    ['accounts', 'categoryPair', '借方與貸方分類組合', 'specialAccountCategoryPair'],
    ['accounts', 'accountCodePair', '借方與貸方科目編號組合', 'group'],
    ['accounts', 'unexpectedAccountPair', '未預期的借貸組合', 'prescreen'],
    ['accounts', 'rareAccounts', '較少使用的科目', 'entityFrequency'],
    ['people', 'manualEntries', '人工分錄', 'manualAuto'],
    ['people', 'rarePreparers', '編製分錄較少的人員', 'entityFrequency']
    // 原本最後一列是「預篩選結果」的「沿用第四步預篩選規則」。使用者 2026-10-16 要求縮短套用預篩選條件的操作，
    // 這一類改成條件總覽的「預篩選」分頁，由下面的分頁測試逐一加入；第一次失敗：讀不到 prescreen 分類的按鈕。
  ];
  const f = customPane({ glMapping: { createBy: 'Maker', approveBy: 'Checker', manual: 'Manual' } });
  const allRules = () => f.draft().groups.flatMap(group => group.rules);
  for (const [category, key, label, type] of checks) {
    if (f.$('[data-check-category="' + category + '"]').getAttribute('aria-expanded') !== 'true') f.click('[data-check-category="' + category + '"]');
    assert.equal(f.$('[data-add-check="' + key + '"]').disabled, false, key + ' can be added');
    f.click('[data-add-check="' + key + '"]');
    const rules = allRules();
    assert.equal(rules.length, 1, key);
    assert.equal(rules[0].type, type, key);
    assert.deepEqual(f.$$('[data-check-category][aria-expanded="true"]').map(node => node.getAttribute('data-check-category')), [category],
      key + ' leaves only its own category open');
    assert.equal(f.$('[data-add-check="' + key + '"]').closest('.filter-check').querySelector('.filter-check__label').textContent, label);
    if (key === 'accountCodePair') assert.deepEqual([rules[0].rules[0].rules[0].drCr, rules[0].rules[1].rules[0].drCr], ['debit', 'credit']);
    // 新建的分類條件預設包含下層分類。
    if (key === 'categoryPair') assert.equal(rules[0].categorySelection, 'subtree');
    if (key === 'trailingZeros') assert.deepEqual([rules[0].rules[0].value, rules[0].rules[1].value], ['000000', '1']);
    const row = f.$('.rule-row[data-ri="0"]');
    const remove = type === 'group' ? row.querySelector('.filter-rule-heading [data-compound-remove]') : row.querySelector('[data-action="remove-rule"]');
    assert.ok(remove, key + ' offers a remove control on its row');
    remove.fire('click');
    assert.equal(allRules().length, 0, key + ' is removed');
  }
});

// ------------------------------------------------------------------------------------------------
// 預篩選分頁
// ------------------------------------------------------------------------------------------------

// 使用者 2026-10-16 指出審計員習慣直接套用預篩選條件，原本要先加入「沿用第四步預篩選規則」，再到條件卡片的下拉改選。
// 條件總覽改成三個分頁，預篩選分頁條列每一條規則，按「加入」直接帶入那一條。第一個分頁 2026-10-29 改名「常用篩選條件」
//（使用者裁定合併卡片與範例、拿掉小組名稱），原本的期望值是舊分頁名稱；第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
// 名稱逐字照預篩選規則的畫面名稱（ui-core.js 的 PRESCREEN_RULES，鏡像 Domain 標籤表），不從畫面推導。
const PRESCREEN_RULES = [
  ['postPeriodApproval', '財報準備日起核准'], ['suspiciousKeywords', '摘要特定描述'], ['unexpectedAccountPair', '未預期借貸組合'],
  ['trailingZeros', '金額尾數連續 6 個 0'], ['weekendPosting', '週末過帳'], ['weekendApproval', '週末核准'],
  ['holidayPosting', '假日過帳'], ['holidayApproval', '假日核准'], ['blankDescription', '空白摘要'],
  ['nonAuthorizedPreparer', '非授權編製人員'], ['lowFrequencyPreparer', '編製分錄較少的人員（11 筆以下）'],
  ['lowFrequencyAccount', '使用較少的科目（11 筆以下）']
];
const prescreenWire = f => f.wire().groups.map(group => group.rules.map(rule => [rule.type, rule.prescreenKey]));

test('the prescreen tab lists every prescreen rule and each add button brings in exactly that rule', () => {
  // 「編製分錄較少的人員」與「非授權編製人員」要用傳票建立人員，這裡配好欄位，十二條都能加入。
  const f = buildFixture({ glMapping: { createBy: 'Maker' } });
  f.store.setAuthorizedPreparerState({ rowCount: 2 });
  assert.deepEqual(f.$$('[data-condition-source]').map(button => button.textContent), ['常用篩選條件', '預篩選', '自訂篩選條件']);
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
    assert.equal(f.draft().groups.flatMap(group => group.rules).length, 0, key + ' is removed');
  }
});

// 前置不足時按鈕停用，原因寫在該項下方並由 aria-describedby 連到按鈕，和常用查核條件同一種做法；後端一定會擋的條件不會被帶入。
// 預篩選收斂計畫階段四（2026-10-23）改了兩處：前端改讀後端算好的可用欄位，原本靠「沒有確認 GL 配對」表示傳票建立人員未配對，
// 改成給一份沒有傳票建立人員的可用欄位清單；欄位的原因接上和第四步相同的下一步（使用者 2026-10-22 裁定寫出可以回欄位配對補上）。
// 第一次失敗：FrontendMapping 收據 20261023-044331305-cc3be698efaa446eab8994f89154b247（「編製分錄較少的人員」沒有停用）。
const WITHOUT_CREATED_BY = ['docNum', 'lineID', 'postDate', 'docDate', 'accNum', 'accName', 'description', 'jeSource', 'approveBy', 'manual', 'amount'];
test('a prescreen rule the backend would reject is disabled with its reason and adds nothing when pressed', () => {
  const f = buildFixture({ accountMapping: { hasAnyCategory: true, hasRevenue: false, hasCounterpart: true }, availableGlFields: WITHOUT_CREATED_BY,
    project: { projectId: 'filter-build-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31', lastPeriodStart: null } });
  f.click('[data-condition-source="prescreen"]');
  // 這個夾具沒有配對傳票建立人員，後端會擋「編製分錄較少的人員」與「非授權編製人員」（2026-10-16 複審找到原本沒有停用）。
  assert.deepEqual(f.$$('[data-add-prescreen]').filter(button => button.disabled).map(button => button.getAttribute('data-add-prescreen')),
    ['postPeriodApproval', 'unexpectedAccountPair', 'nonAuthorizedPreparer', 'lowFrequencyPreparer']);
  const reason = key => f.$('#' + f.$('[data-add-prescreen="' + key + '"]').getAttribute('aria-describedby')).textContent;
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-17。
  assert.equal(reason('postPeriodApproval'), '尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫');
  assert.match(reason('unexpectedAccountPair'), /Revenue.*第四步/);
  assert.match(reason('nonAuthorizedPreparer'), /授權編製人員清單/);
  assert.equal(reason('lowFrequencyPreparer'), '「傳票建立人員」尚未選擇來源欄，請回第三步欄位配對補上');
  f.$('[data-add-prescreen="unexpectedAccountPair"]').fire('click');
  f.$('[data-add-prescreen="lowFrequencyPreparer"]').fire('click');
  assert.equal(f.draft().groups.flatMap(group => group.rules).length, 0, 'a disabled rule adds nothing');
  // 授權清單匯入後，「非授權編製人員」仍缺傳票建立人員，原因改寫這一項。
  f.store.setAuthorizedPreparerState({ rowCount: 2 });
  assert.equal(f.$('[data-add-prescreen="nonAuthorizedPreparer"]').disabled, true);
  assert.equal(reason('nonAuthorizedPreparer'), '「傳票建立人員」尚未選擇來源欄，請回第三步欄位配對補上');
});

// 使用者 2026-10-16 的兩項要求一起走一遍：從預篩選分頁一次加入「未預期借貸組合」，再查看符合的傳票。
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
    rows: [{ documentNumber: 'SYN-1', postDate: '2025-03-05', hitRowCount: 7, totalRowCount: 7, voucherTotal: 1000 }] });
  await settle(); await settle();
  const pane = f.$('[data-bind="preview-pane-body"]').textContent;
  assert.match(pane, /1符合條件的傳票數量7符合條件的分錄筆數/);
  // 口徑說明 2026-10-29 改成只在這種情形出現的一句（使用者指出常駐說明太多），意思不變；
  // 原本比對舊句，改版後依預期失敗，首敗收據 20261029-040252723-dde1d637675742fd85d9576ba633dd2d。
  assert.match(pane, /分錄筆數包含整張傳票的分錄/);
  assert.doesNotMatch(pane, /標示為參考的分錄不計入/);
  assert.match(pane, /SYN-1/);
});

// 使用者 2026-10-16（圖四）：「突然擴增文字輸入欄的高度讓我感覺很突兀」。值清單一開始就是固定的多行輸入框，
// 貼上以逗號分隔的人員、拆成多行後重繪，框的高度都不由內容決定；單行輸入（每月幾日、單值）才隨內容調整。
test('a pasted comma list keeps the value list at its fixed height instead of stretching the box', () => {
  const f = customPane({ glMapping: { createBy: 'Maker' } });
  f.store.setFilterDraft({ name: '合成條件', rationale: '合成', groups: [{ join: 'AND', matchScope: 'row', rules: [
    { type: 'fieldValue', join: 'AND', field: 'createBy', operator: 'notIn', values: [] }
  ] }] });
  const list = () => f.$('textarea[data-value-key="values"]');
  assert.ok(list(), 'the person list is rendered');
  assert.ok(list().getAttribute('class').split(/\s+/).includes('value-editor__values'), 'the list carries the fixed-height class');
  list().value = '甲員,乙員,丙員,丁員,戊員,己員,庚員';
  list().fire('input');
  assert.deepEqual(plain(f.draft().groups[0].rules[0].values), ['甲員', '乙員', '丙員', '丁員', '戊員', '己員', '庚員']);
  f.store.touch();
  assert.equal(list().value.split('\n').length, 7, 'the redrawn list shows one value per line');
  assert.equal(list().style.height, undefined, 'the list height is not set from its content');
  const css = readWwwroot('css/app.css');
  assert.match(css, /\.filter-workspace \.value-editor__values \{ flex: 1 1 100%; height: calc\(5 \* 20px \+ 14px\);/,
    'the list is five lines tall on its own row');
});

// ------------------------------------------------------------------------------------------------
// 預篩選收斂計畫階段四（2026-10-23）：第五步不能用 GL 沒有配對的欄位
// ------------------------------------------------------------------------------------------------

// 使用者 2026-10-22 用視窗選項裁定「停用並寫原因」，選項說明是欄位按鈕照樣列出但不能按，下方寫出原因以及可以回欄位配對補上，
// 常用查核條件與範例用同一套判斷（docs/history/specs/2026-10-22-prescreen-tech-debt-deferred-items-plan.md 附錄一）。
// 可用欄位照後端在「傳票核准日、分錄來源模組、傳票建立人員、傳票核准人員與人工/自動分錄都沒有選來源欄」時送的清單寫死；
// 傳票文件項次沒有來源欄時系統自動編號，使用者 2026-10-22 裁定算已配對，所以在清單裡。
// 原因是後端第五步的句子；GL 欄位的句子接上和第四步相同的下一步，同一個入口缺幾個欄位就寫在同一句。
const PARTLY_MAPPED = ['docNum', 'lineID', 'postDate', 'accNum', 'accName', 'description', 'amount'];
const EVERY_GL_FIELD = ['docNum', 'lineID', 'postDate', 'docDate', 'accNum', 'accName', 'description', 'jeSource', 'createBy', 'approveBy', 'manual', 'amount'];
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

test('stage four: every field button stays listed, the unmapped ones cannot be pressed and the note under them names each field and where to fill it', () => {
  const f = customPane({ availableGlFields: PARTLY_MAPPED });
  assert.deepEqual(f.$$('[data-add-field]').map(button => button.getAttribute('data-add-field')),
    ['postDate', 'docDate', 'amount', 'description', 'accNum', 'accName', 'createBy', 'approveBy', 'docNum', 'jeSource', 'lineID'], 'every core field is still listed');
  assert.deepEqual(disabledKeys(f, 'data-add-field'), ['docDate', 'createBy', 'approveBy', 'jeSource']);
  const notes = [...new Set(f.$$('[data-add-field]').filter(button => button.disabled).map(button => describedText(f, button)))];
  assert.deepEqual(notes, [unmapped('傳票核准日', '傳票建立人員', '傳票核准人員', '分錄來源模組')], 'one note under the buttons names every unmapped field');
  const note = f.$('#' + f.$('[data-add-field="docDate"]').getAttribute('aria-describedby'));
  // 區塊的 class 2026-10-29 從 filter-custom-block 改成 filter-source-block（三個分頁共用）；舊選擇器找不到節點時兩邊都是 null，
  // 這個斷言不會失敗，所以另確認找得到區塊。FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2 沒有因此失敗。
  assert.ok(note.closest('.filter-source-block'), 'the note sits inside a source block');
  assert.equal(note.closest('.filter-source-block'), f.$('[data-add-field="docDate"]').closest('.filter-source-block'), 'the note sits under the field buttons');
  // 傳票文件項次沒有來源欄也能用：系統自動編號。
  assert.equal(f.$('[data-add-field="lineID"]').disabled, false);
  assert.equal(f.$('[data-add-field="lineID"]').getAttribute('aria-describedby'), null);
  f.$('[data-add-field="createBy"]').fire('click');
  assert.deepEqual(rulesOf(f), [], 'an unmapped field adds nothing');
  f.click('[data-add-field="lineID"]');
  assert.deepEqual(plain(f.draft().groups.flatMap(group => group.rules.map(rule => [rule.type, rule.field]))), [['fieldValue', 'lineID']]);

  mapEverything(f);
  assert.deepEqual(disabledKeys(f, 'data-add-field'), [], 'after mapping every button can be pressed');
  assert.equal(f.$('[data-add-field="docDate"]').getAttribute('aria-describedby'), null);
  assert.doesNotMatch(screenTexts(f.$('[data-condition-pane="custom"]')).join('\n'), /尚未選擇來源欄/);
});

// 2026-10-29 範例移到「常用篩選條件」分頁並一直列出（原本要先按「從範例開始」），卡片改用代號識別，測試名稱跟著改；
// 範例的停用斷言改在常用篩選條件分頁核對。第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
test('stage four: purpose checks, examples, common cards and prescreen rules that need an unmapped field are disabled with the same reason until it is mapped', () => {
  const f = customPane({ availableGlFields: PARTLY_MAPPED, rdeFields: DEPARTMENT });
  assert.deepEqual(disabledKeys(f, 'data-add-check'), ['docDateNonBusinessDay', 'postPeriodApproval', 'manualEntries', 'rarePreparers']);
  const checkNote = key => describedText(f, f.$('[data-add-check="' + key + '"]'));
  assert.equal(checkNote('docDateNonBusinessDay'), unmapped('傳票核准日'));
  assert.equal(checkNote('postPeriodApproval'), unmapped('傳票核准日'));
  assert.equal(checkNote('manualEntries'), unmapped('人工/自動分錄'));
  assert.equal(checkNote('rarePreparers'), unmapped('傳票建立人員'));
  f.$('[data-add-check="manualEntries"]').fire('click');
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
  assert.deepEqual(disabledKeys(f, 'data-add-prescreen'), ['postPeriodApproval', 'weekendApproval', 'holidayApproval', 'nonAuthorizedPreparer', 'lowFrequencyPreparer']);
  const prescreenNote = key => describedText(f, f.$('[data-add-prescreen="' + key + '"]'));
  for (const key of ['postPeriodApproval', 'weekendApproval', 'holidayApproval']) assert.equal(prescreenNote(key), unmapped('傳票核准日'), key);
  assert.equal(prescreenNote('nonAuthorizedPreparer'), '尚未匯入授權編製人員清單，請先在第二步匯入；' + unmapped('傳票建立人員'));
  assert.equal(prescreenNote('lowFrequencyPreparer'), unmapped('傳票建立人員'));
  f.$('[data-add-prescreen="weekendApproval"]').fire('click');
  assert.deepEqual(rulesOf(f), [], 'a disabled prescreen rule adds nothing');

  // 回第三步配好欄位、匯入授權編製人員清單之後，每個入口都能用。
  mapEverything(f);
  f.store.setAuthorizedPreparerState({ rowCount: 2 });
  assert.deepEqual(disabledKeys(f, 'data-add-prescreen'), []);
  assert.deepEqual(disabledKeys(f, 'data-add-check'), []);
  assert.deepEqual(disabledKeys(f, 'data-template-key'), []);
  assert.deepEqual(CARD_KEYS.filter(key => card(f, key).disabled), []);
  f.click('[data-add-prescreen="weekendApproval"]');
  assert.deepEqual(prescreenWire(f), [[['prescreen', 'weekendApproval']]]);
});

test('stage four: the field menus on condition cards disable unmapped fields and a condition already using one says why and is not previewed', async () => {
  const f = customPane({ availableGlFields: PARTLY_MAPPED });
  f.click('[data-add-compound="group"]');
  const option = value => f.$('[data-child-kind]').querySelectorAll('option').find(node => node.getAttribute('value') === value);
  for (const [value, label] of [['field:docDate', '傳票核准日'], ['field:createBy', '傳票建立人員'], ['field:approveBy', '傳票核准人員'], ['field:jeSource', '分錄來源模組']]) {
    assert.equal(option(value).disabled, true, value);
    assert.equal(option(value).textContent, label + '（尚未選擇來源欄，請回第三步欄位配對補上）', value);
  }
  assert.equal(option('type:manualAuto').disabled, true);
  assert.equal(option('type:manualAuto').textContent, '人工或自動（' + unmapped('人工/自動分錄') + '）');
  for (const value of ['field:postDate', 'field:lineID', 'type:drCrOnly']) assert.equal(option(value).disabled, false, value);
  assert.equal(option('field:postDate').textContent, '總帳日期');
  // 就算選到停用的項目再按「加入」，也不會加入子條件。
  for (const value of ['field:createBy', 'type:manualAuto']) {
    f.$('[data-child-kind]').value = value;
    f.click('[data-child-add]');
    assert.deepEqual(plain(f.draft().groups[0].rules[0].rules), [], value + ' adds nothing');
  }

  // 草稿裡已經有用到未配對欄位的條件（例如舊情境，或改了配對之後）：條件卡片寫出原因，預覽前就擋下、不送出。
  f.store.setFilterDraft({ name: '合成條件', rationale: '合成', groups: [{ join: 'AND', matchScope: 'row', rules: [
    { type: 'fieldValue', join: 'AND', field: 'approveBy', operator: 'isBlank', values: [] },
    { type: 'dateRange', join: 'AND', field: 'docDate', from: '2025-01-01', to: '2025-01-31' },
    { type: 'prescreen', join: 'AND', prescreenKey: 'weekendApproval' }] }] });
  const row = index => f.$('.rule-row[data-gi="0"][data-ri="' + index + '"]');
  assert.ok(screenTexts(row(0)).includes(unmapped('傳票核准人員')), 'the field condition says why');
  const dateOption = value => row(1).querySelector('[data-rule-bind="field"]').querySelectorAll('option').find(node => node.getAttribute('value') === value);
  assert.equal(dateOption('docDate').disabled, true);
  assert.notEqual(dateOption('docDate').getAttribute('selected'), null, 'the saved field stays chosen');
  assert.equal(dateOption('postDate').disabled, false);
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
test('stage four: the two cash examples stay disabled until the account mapping has categories and give the fixed-asset card reason', () => {
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
