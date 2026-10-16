'use strict';
require('./support/resource-guard.cjs');
// 第五步組條件的情境測試（收尾四項第一項第 5 批，2026-10-13）。
//
// 情境 10「第五步 KCT 卡片與範例」：審計員在四種科目配對狀態下看 KCT 卡片與常用查核條件，加入 C 卡看說明，
// 展開「從範例開始」並套用每一個範例。2026-10-16 起另在「預篩選」分頁逐一加入每一條預篩選規則，
// 看前置不足的停用原因，並從分頁加入「未預期借貸組合」後查看傳票數量與分錄筆數。
// 情境 11「第五步自訂條件與條件組」：審計員在自訂分頁從各家族各加一條、組成兩組，看組內條件的組合方式，
// 移除一條，用「新增條件到」換加入的組，改成 OR 再改成同一傳票，加借貸科目組合並切換五種查找方式，
// 在分類清單勾選分類，最後預覽，預覽的錯誤標在條件列上。
//
// 每個測試都執行正式的 state.js、ui-core.js、filter-values.js、filter-vouchers.js 與 steps/filter-step.js，
// 在假 DOM 上畫出第五步並用點擊與選單操作；合成資料只控制邊界。
// 第五步的儲存、另存、移除與重新檢查屬於情境 12、13（第 9 批），這個檔不寫那些流程的斷言。
// 訊息面板的句子（例如「已加入 KCT 條件」）與後端原因句子不寫斷言，它們會在第 1、4 項改寫。
//
// 斷言規則照 artifacts/review/closing-four-items-20261013/plan-item1-tests.md 第 1 節：
// 只有使用者裁定過的文字逐字比對，出處寫在該斷言旁；其他文字只比對意思；預期值寫死，不由被測程式算出；
// 送出的 payload 精確比對，而且不得帶 `__` 開頭的鍵。
//
// 取代的 C# 方法（src/JET/tests/JET.Tests/Architecture/，由每波結束時統一刪除）：
//   FilterAccountMappingEligibilityFrontendTests 的三個方法、ScreenWordingReviewFrontendTests.K3_… 與 KctLetters…、
//   FilterAstFrontendContractTests.GroupMatchScope_…、FilterBuilderFrontendTests 兩個方法、FilterCategorySelectFrontendTests 兩個方法、
//   FilterTemplatesFrontendTests.FilterStep_OffersTemplatesAndTheCombinationCard、FilterWorkspaceFrontendTests 兩個方法、
//   AccountTaxonomyFrontendTests.RuleControls_UseTaxonomyIdentitiesInsteadOfLegacyCategoryLabels。
// 對照表與改壞檢查紀錄見 artifacts/review/closing-four-items-20261013/impl-item1-batch5.md。
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { batch9TreeDom, readWwwroot, exposeAt, plain, settle, bootFullApp, flush } = require('./support/frontend-fixtures.cjs');
const { contractJetApi } = require('./support/action-contract.cjs');

const ALL_MAPPED = { hasAnyCategory: true, hasRevenue: true, hasCounterpart: true };

// 本檔自己的夾具：和共用模組的 filterOperationFixture 相同，執行正式腳本並訂閱重繪；
// 另外掛出 toWireDraft（情境 11 要看草稿送出的形狀），並記下預覽與傳票分頁的請求。
// 依計畫第 7 節第 5 點，只服務這個檔的夾具留在這裡，第 11 批整併時再決定要不要收進共用模組。
function buildFixture(options = {}) {
  const { accountMapping = ALL_MAPPED, glMapping = null, rdeFields = [], taxonomy = null, saved = [],
    project = { projectId: 'filter-build-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31', lastPeriodStart: '2025-12-01' } } = options;
  const dom = batch9TreeDom(), requests = [];
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const context = vm.createContext({ window, document: dom.document });
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js', 'filter-vouchers.js'])
    vm.runInContext(readWwwroot('js/' + file), context, { filename: file });
  vm.runInContext(exposeAt(readWwwroot('js/steps/filter-step.js'), "  Ui.registerStep('filter', render);",
    '  window.filterBuild = { syncViewState: syncViewState, render: render, toWireDraft: toWireDraft };\n'), context, { filename: 'steps/filter-step.js' });
  window.JetApi = contractJetApi({});
  for (const action of ['filterPreview', 'queryFilterVoucherPage', 'queryFilterVoucherRowsPage'])
    window.JetApi[action] = payload => new Promise((resolve, reject) => requests.push({ action, payload, resolve, reject }));
  const store = window.JetStore, api = window.filterBuild;
  store.setProject(project);
  store.setAccountMappingState(accountMapping);
  if (glMapping || rdeFields.length) store.setMappingCommitted('gl', { mapping: glMapping || {}, mode: 'signed', options: { rdeFields } });
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
const card = (f, letter) => f.$('[data-kct-letter="' + letter + '"]');
// 停用的卡最後一段說明是停用原因（B、C 卡另有固定的判定說明排在前面）。
const cardReason = (f, letter) => card(f, letter).querySelectorAll('.picker-card__note').at(-1).textContent;
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
// 情境 10：KCT 卡片與範例
// ------------------------------------------------------------------------------------------------

// 取代 FilterAccountMappingEligibilityFrontendTests.RuleMetadata_DeclaresPerRuleAccountMappingRequirements 與
// KctDisabledNotes_DistinguishMissingImportAndMissingTargetContent 的 KCT 卡部分。
// 使用者 2026-10-14 第三輪勾選頁確認 R3-15 的逐字句：還沒匯入與匯入了但沒有任何分類同一句，缺收入或對方分類時寫出分類名稱
// （第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
const KCT_TYPES = { A: 'revenueDebitNearQuarterEnd', B: 'specialAccountCategoryPair', C: 'revenueWithoutNormalCounterpart', D: 'manualRevenueEntry' };
test('KCT cards A to D follow the account mapping content and explain whether mapping is missing or incomplete', () => {
  const states = [
    { name: 'not imported', mapping: null, enabled: [] },
    { name: 'imported without categories', mapping: { hasAnyCategory: false, hasRevenue: false, hasCounterpart: false }, enabled: [] },
    { name: 'only general categories', mapping: { hasAnyCategory: true, hasRevenue: false, hasCounterpart: true }, enabled: ['B'] },
    { name: 'revenue without counterpart', mapping: { hasAnyCategory: true, hasRevenue: true, hasCounterpart: false }, enabled: ['A', 'B', 'D'] },
    { name: 'revenue and counterpart', mapping: ALL_MAPPED, enabled: ['A', 'B', 'C', 'D'] }
  ];
  const notes = {};
  for (const state of states) {
    const f = buildFixture({ accountMapping: state.mapping });
    for (const letter of ['A', 'B', 'C', 'D']) {
      const button = card(f, letter), on = state.enabled.includes(letter);
      assert.equal(button.disabled, !on, state.name + ' ' + letter + ' disabled');
      assert.equal(button.getAttribute('aria-disabled'), on ? null : 'true', state.name + ' ' + letter + ' aria-disabled');
      assert.equal(!!button.closest('.filter-unavailable'), !on, state.name + ' ' + letter + ' listed under unavailable cards');
      notes[state.name + letter] = on ? '' : cardReason(f, letter);
      if (!on) assert.ok(notes[state.name + letter], state.name + ' ' + letter + ' explains why');
      // 停用的卡按了不會加入條件。
      button.fire('click');
      assert.deepEqual(plain(f.draft().groups.flatMap(group => group.rules.map(rule => rule.type))), on ? [KCT_TYPES[letter]] : [], state.name + ' ' + letter + ' click');
      if (on) f.click('[data-kct-letter="' + letter + '"]');
    }
    for (const letter of ['E', 'F', 'G', 'H', 'I', 'J']) assert.equal(card(f, letter).disabled, false, state.name + ' ' + letter + ' never depends on mapping');
  }
  const noCategory = '科目配對中尚無任何科目設定分類，請先在第四步設定';
  const noRevenue = '科目配對中尚無歸類為「Revenue」的科目，請先在第四步設定';
  const noCounterpart = '科目配對中尚無歸類為「Receivables」「Cash」或「Receipt in advance」的科目，請先在第四步設定';
  // 還沒匯入：四張卡同一句。
  for (const letter of ['A', 'B', 'C', 'D']) assert.equal(notes['not imported' + letter], noCategory, letter);
  // 匯入了但沒有任何分類：B 同一句；A、C、D 先檢查收入分類，寫出缺的分類名稱。
  assert.equal(notes['imported without categoriesB'], noCategory);
  for (const letter of ['A', 'C', 'D']) assert.equal(notes['imported without categories' + letter], noRevenue, letter);
  for (const letter of ['A', 'C', 'D']) assert.equal(notes['only general categories' + letter], noRevenue, letter + ' names the missing revenue category');
  assert.equal(notes['revenue without counterpartC'], noCounterpart, 'C names the missing counterpart categories');
});

// 取代 FilterAccountMappingEligibilityFrontendTests.AvailableTypes_EvaluateTargetContentFacts_NotImportPresence：
// 樣態下拉的 KCT 專屬收入選項看配對內容，不看有沒有匯入科目配對檔。
test('the pattern menu offers revenue-only KCT conditions only when the mapping has revenue and counterpart content', () => {
  // 第九節 G2：尾數不再是樣態型別，以預篩選卡檢查同一選單與收入資格。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  const kctOptions = mapping => {
    const f = buildFixture({ accountMapping: mapping });
    // 2026-10-16 預篩選條件改成條件總覽的分頁（使用者要求縮短套用預篩選條件的操作），改從分頁加入一條；
    // 第一次失敗：缺少 [data-check-category="prescreen"]。
    f.click('[data-condition-source="prescreen"]'); f.click('[data-add-prescreen="suspiciousKeywords"]');
    const select = f.$('.rule-row [data-pattern-subject]');
    assert.ok(select, 'the pattern row offers a pattern menu');
    const group = select.querySelectorAll('optgroup').find(node => node.getAttribute('label') === 'KCT 專屬');
    return group ? group.querySelectorAll('option').map(option => option.getAttribute('value')).sort() : [];
  };
  const always = ['preparerEqualsApprover'];
  assert.deepEqual(kctOptions(null), always);
  assert.deepEqual(kctOptions({ hasAnyCategory: true, hasRevenue: false, hasCounterpart: true }), always, 'imported but no revenue');
  assert.deepEqual(kctOptions({ hasAnyCategory: true, hasRevenue: true, hasCounterpart: false }),
    ['manualRevenueEntry', 'preparerEqualsApprover', 'revenueDebitNearQuarterEnd'], 'C still needs a counterpart');
  assert.deepEqual(kctOptions(ALL_MAPPED),
    ['manualRevenueEntry', 'preparerEqualsApprover', 'revenueDebitNearQuarterEnd', 'revenueWithoutNormalCounterpart']);
});

// 取代 KctDisabledNotes_DistinguishMissingImportAndMissingTargetContent 的常用查核條件部分（2026-10-08 使用者要求重新設計自訂篩選條件：
// 停用時按鈕帶 disabled，原因寫在該項下方並以 aria-describedby 連到按鈕；原因和 KCT 卡用同一個判斷）。
// 第一次失敗收據（原 C# 註解）：20260907-135727110、20260908-024320083。
test('disabled common checks point to their reason, share the KCT card reason and add nothing when pressed', () => {
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
      assert.equal(note.textContent, cardReason(f, 'B'), key + ' uses the same reason as the KCT B card');
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
// 逐字出處：docs/history/specs/2026-10-06-kct-gl-requirements-plan.md「#### K3：KCT C 的卡片說明」寫明 C 的說明逐字是這一句，
// 同檔處理方式表「K1、K2、K3、K10 | 照建議 | 第一批實作」是使用者的裁定。
// 第一次失敗收據（計畫檔 K3 的實作紀錄）：20261006-030038300-b79b8f5c1edd404f880a2c56319d2a7c。
const K3_CASH_NOTE = '現金不算一般對方科目；要排除現金銷貨，改用預篩選條件『未預期借貸組合』。';
test('the C card and its condition row explain cash with the approved sentence', () => {
  const f = buildFixture();
  assert.ok(card(f, 'C').querySelectorAll('.picker-card__note').some(node => node.textContent === K3_CASH_NOTE), 'card shows the approved sentence');
  f.click('[data-kct-letter="C"]');
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row.querySelectorAll('.rule-explanation').some(node => node.textContent === K3_CASH_NOTE), 'condition row shows the approved sentence');
  // 停用時說明仍在，原因另列。
  const blocked = buildFixture({ accountMapping: { hasAnyCategory: true, hasRevenue: true, hasCounterpart: false } });
  const blockedNotes = card(blocked, 'C').querySelectorAll('.picker-card__note').map(node => node.textContent);
  assert.equal(blockedNotes[0], K3_CASH_NOTE); assert.equal(blockedNotes.length, 2);
});

// 取代 ScreenWordingReviewFrontendTests.KctLettersStayOnKctCards_AndCustomConditionsUseNoCatalogueCodes 的 KCT 卡部分。
// 「KCT 條件」逐字出處：docs/history/specs/2026-10-06-kct-gl-requirements-plan.md「驗收發現與裁定（2026-10-08）」使用者原話：
// 「請改名 `KCT條件A到J` 改為 `KCT 條件` 就好」。
test('the KCT tab and heading read KCT 條件 and each card letter is hidden from screen readers', () => {
  const f = buildFixture();
  assert.equal(f.$('[data-condition-source="kct"]').textContent, 'KCT 條件');
  assert.equal(f.$('[data-condition-pane="kct"] .condition-picker__title').textContent, 'KCT 條件');
  const cards = f.$$('[data-condition-pane="kct"] [data-kct-letter]');
  assert.deepEqual(cards.map(node => node.getAttribute('data-kct-letter')), ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J']);
  for (const node of cards) {
    const letters = node.querySelectorAll('.picker-card__letter');
    assert.equal(letters.length, 1);
    assert.equal(letters[0].textContent, node.getAttribute('data-kct-letter'));
    assert.equal(letters[0].getAttribute('aria-hidden'), 'true');
  }
  assert.doesNotMatch(screenTexts(f.$('[data-condition-pane="kct"]')).join('\n'), /A 到 J/);
});

// 使用者 2026-10-07 裁定（計畫檔 2026-10-07 那一輪：「同組有 KCT 與自訂條件時名稱寫「G+自訂」，動機每條自訂條件多一行「自訂：」」）。
// 第一次失敗收據（原 C# 註解）：20261007-035823939-41943206b5f747f1b7fb8dfe79f987d7。
test('KCT letters name the draft and a group that also has custom conditions reads G+自訂', () => {
  const f = buildFixture();
  f.click('[data-kct-letter="G"]'); f.click('[data-kct-letter="H"]');
  assert.equal(f.draft().name, 'G+H');
  f.click('[data-kct-letter="H"]');
  f.click('[data-condition-source="custom"]'); f.click('[data-add-field="description"]');
  assert.equal(f.draft().name, 'G+自訂');
  const lines = f.draft().rationale.split('\n');
  assert.equal(lines.length, 2);
  assert.match(lines[0], /^G：/); assert.match(lines[0], /空白摘要/);
  assert.match(lines[1], /^自訂：/);
  f.click('[data-action="add-set"]'); f.click('[data-add-field="docNum"]');
  assert.equal(f.draft().name, 'G+自訂｜自訂');
});

// 範例全部可用的案件狀態：查核截止日、期末財報準備日、核准人員與建立人員都已配對，有一個文字型攸關資料元素欄位。
const EXAMPLE_READY = { glMapping: { approveBy: 'Checker', createBy: 'Maker' },
  rdeFields: [{ fieldId: 'rde.dept', sourceColumn: 'Dept', label: '部門', valueType: 'text' }] };
const EXAMPLE_KEYS = ['cashDebitNonCashCredit', 'cashCreditNonCashDebit', 'nonBusinessDayExcludingDates', 'periodEndManual', 'blankDescriptionOrApprover',
  'preparerLargeAmount', 'weekendManualDescription', 'manualDepartment', 'accountPairLargeAmount', 'preparationManualHoliday'];
// 使用者 2026-10-08 要求重新設計自訂篩選條件時的原話（同上「驗收發現與裁定（2026-10-08）」）：
// 「名稱 `舊表 X:XXX` 可以改為其他名稱，因為 "舊表" 聽起來非常不恰當且不專業」，以及「一下子有範例1範例2的，一下子註解顯示什麼 "舊表P+S"」。
const CATALOGUE_CODES = /舊表|A 到 U|A–U|範例 ?\d|工作簿範例|[A-U](\+[A-U])+/;

// 取代 KctLettersStayOnKctCards_… 的自訂分頁與範例部分，以及 FilterTemplatesFrontendTests.FilterStep_OffersTemplatesAndTheCombinationCard。
// 自訂分頁沒有收合鈕的第一次失敗收據（原 C# 註解）：20261012-060708654-d3fdcf4d785843d3b09ab6fadd009c5c。
// 每個範例都在新的案件畫面套用（空白草稿直接套用，不會跳出「還沒儲存」的詢問，那屬於第 9 批的儲存流程）。
test('every example applies under its own name and the custom screen never shows catalogue codes or KCT letters', () => {
  const offered = customPane(EXAMPLE_READY);
  assert.equal(offered.$('[data-example-list]').hidden, true, 'examples start closed');
  offered.click('[data-action="toggle-examples"]');
  assert.equal(offered.$('[data-action="toggle-examples"]').getAttribute('aria-expanded'), 'true');
  assert.deepEqual(offered.$$('[data-template-key]').map(button => button.getAttribute('data-template-key')), EXAMPLE_KEYS);
  // 自訂分頁沒有收合鈕，也沒有退場的排除區或待判定說法。
  assert.equal(offered.$('[data-action="toggle-custom-picker"]'), null);
  assert.doesNotMatch(screenTexts(offered.$('[data-filter-pane="filter"]')).join('\n'), /排除區域|待判定/);
  assert.doesNotMatch(screenTexts(offered.$('[data-filter-pane="filter"]'), '[data-condition-pane="kct"]').join('\n'), CATALOGUE_CODES);

  for (const key of EXAMPLE_KEYS) {
    const f = customPane(EXAMPLE_READY);
    f.click('[data-action="toggle-examples"]');
    const button = f.$('[data-template-key="' + key + '"]');
    assert.equal(button.disabled, false, key + ' is available');
    const label = button.textContent;
    button.fire('click');
    assert.equal(f.draft().name, label, key + ' is named after the example');
    assert.ok(f.draft().groups.some(group => group.rules.length), key + ' brings conditions');
    assert.equal(f.$('[data-example-list]').hidden, true, key + ' closes the list');
    const texts = screenTexts(f.$('[data-filter-pane="filter"]'), '[data-condition-pane="kct"]');
    assert.doesNotMatch(texts.join('\n'), CATALOGUE_CODES, key);
    assert.doesNotMatch(f.draft().name + '\n' + f.draft().rationale, CATALOGUE_CODES, key);
    // 條件組裡沒有 KCT 卡片、欄位按鈕或常用查核條件的按鈕。
    assert.equal(f.dom.content.querySelectorAll('.scenario-set [data-kct-letter], .scenario-set [data-add-field], .scenario-set [data-add-check]').length, 0, key);
    if (key === 'manualDepartment') {
      // 只有一個文字型攸關資料元素欄位時直接帶入它。
      assert.deepEqual(plain(f.draft().groups[0].rules.map(rule => [rule.type, rule.fieldId ?? null])), [['manualAuto', null], ['fieldValue', 'rde.dept']]);
    }
  }

  // 已有同名情境時，範例名稱後面加數字，不加任何代號。
  const named = customPane({ ...EXAMPLE_READY, saved: [{ name: '借現金、貸非現金', rationale: 'r', groups: [] }] });
  named.click('[data-action="toggle-examples"]'); named.click('[data-template-key="cashDebitNonCashCredit"]');
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
  f.click('[data-kct-letter="G"]');
  assert.equal(f.$('[data-condition-target]'), null, 'one group needs no target choice');
  // 第九節 G2：G、H 都以欄位條件加入，分組與隔離斷言不變。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  assert.match(f.$('.rule-row[data-gi="0"][data-ri="0"] .filter-rule-heading').textContent, /傳票摘要/);
  f.click('[data-action="add-set"]');
  const target = f.$('[data-condition-target]');
  assert.ok(target.closest('.filter-entry'), 'the target choice sits with the condition catalogue');
  assert.equal(target.closest('.scenario-set'), null);
  assert.deepEqual(target.querySelectorAll('option').map(option => option.getAttribute('value')), ['0', '1']);
  assert.equal(target.value, '1', 'the new group is the target');
  assert.doesNotMatch(screenTexts(f.$('[data-filter-pane="filter"]')).join('\n'), /目前加入目標/);
  f.choose('[data-condition-target]', '0');
  f.click('[data-kct-letter="H"]');
  assert.deepEqual(rulesOf(f), [['fieldValue', 'fieldValue'], []]);
  f.click('[data-condition-source="custom"]'); f.click('[data-add-field="docNum"]');
  assert.deepEqual(rulesOf(f), [['fieldValue', 'fieldValue', 'fieldValue'], []]);
  assert.equal(f.dom.content.querySelectorAll('.scenario-set [data-kct-letter], .scenario-set [data-add-field], .scenario-set [data-add-check], .scenario-set [data-template-key]').length, 0);
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
  f.click('[data-kct-letter="G"]');
  f.click('[data-action="add-set"]');
  f.click('[data-action="add-set"]');
  f.click('[data-kct-letter="H"]');
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
  assert.deepEqual(plain(request[0].payload), {
    populationScope: 'auditPeriod',
    scenario: {
      name: 'G｜H+自訂',
      rationale: 'G：空白摘要\nH：特定尾數(如 999999/000000 結尾)\n自訂：人工分錄',
      groups: [
        { join: 'AND', matchScope: 'row', rules: [{ join: 'AND', type: 'fieldValue', field: 'description', operator: 'isBlank', includeBlank: false }] },
        { join: 'AND', matchScope: 'row', rules: [
          { join: 'OR', type: 'fieldValue', field: 'amount', operator: 'endsWithDigits', value: '000000', amountBasis: 'absolute', includeBlank: false },
          { join: 'OR', type: 'manualAuto', prescreenKey: 'suspiciousKeywords', keywords: '', ...filler }] }],
      editorOrigins: { version: 1, legacyKctSource: false, nameIsAutomatic: true, rationaleIsAutomatic: true, groups: [{ letters: ['G'] }, { letters: ['H', null] }] },
      source: 'kct'
    }
  });
  assert.doesNotMatch(JSON.stringify(request[0].payload), /"__/);
  // 草稿本身沒有被送出的投影改動：空的第 2 組還在，標記仍在草稿裡。
  assert.deepEqual(rulesOf(f), [['fieldValue'], [], ['fieldValue', 'manualAuto']]);
  assert.equal(f.draft().groups[0].rules[0].__kctLetter, 'G');

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
// 條件總覽改成「KCT 條件、預篩選、自訂篩選條件」三個分頁，預篩選分頁條列每一條規則，按「加入」直接帶入那一條。
// 名稱逐字照預篩選規則的畫面名稱（ui-core.js 的 PRESCREEN_KEY_OPTIONS，鏡像 Domain 標籤表），不從畫面推導。
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
  assert.deepEqual(f.$$('[data-condition-source]').map(button => button.textContent), ['KCT 條件', '預篩選', '自訂篩選條件']);
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
test('a prescreen rule the backend would reject is disabled with its reason and adds nothing when pressed', () => {
  const f = buildFixture({ accountMapping: { hasAnyCategory: true, hasRevenue: false, hasCounterpart: true },
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
  assert.equal(reason('lowFrequencyPreparer'), '「傳票建立人員」尚未選擇來源欄');
  f.$('[data-add-prescreen="unexpectedAccountPair"]').fire('click');
  f.$('[data-add-prescreen="lowFrequencyPreparer"]').fire('click');
  assert.equal(f.draft().groups.flatMap(group => group.rules).length, 0, 'a disabled rule adds nothing');
  // 授權清單匯入後，「非授權編製人員」仍缺傳票建立人員，原因改寫這一項。
  f.store.setAuthorizedPreparerState({ rowCount: 2 });
  assert.equal(f.$('[data-add-prescreen="nonAuthorizedPreparer"]').disabled, true);
  assert.equal(reason('nonAuthorizedPreparer'), '「傳票建立人員」尚未選擇來源欄');
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
  assert.match(pane, /整張傳票的分錄都計入分錄筆數/);
  assert.doesNotMatch(pane, /標示為參考的分錄不計入/);
  assert.match(pane, /SYN-1/);
});
