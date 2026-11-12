'use strict';
require('./support/resource-guard.cjs');
// 第五步組條件的情境測試。
//
// 情境 10「第五步常用篩選條件的卡片與範例」：審計員在四種科目配對狀態下看常用篩選條件的卡片與欄位的常用查法，
// 加入「貸方為收入但借方非一般對方科目」那張卡看說明，並套用每一個情境範例。卡片列在「常用篩選條件」分頁，
// 範例在右側條件組合上方的「從情境範例開始」；常用查法掛在欄位條件分頁的各欄位底下，收在按「加入」打開的選單裡，
// 審計員用滑鼠或鍵盤選一項。另在「預篩選」分頁逐一加入每一條預篩選規則，
// 看前置不足的停用原因，並從分頁加入「未預期借貸組合」後查看傳票數量與分錄筆數。
// 情境 11「第五步自訂條件與條件組」：審計員在欄位條件分頁從各家族各加一條、組成兩組，看組內條件的組合方式，
// 移除一條，用「加入到」的組號換加入的組，切換 AND 與 OR 並加整張傳票條件，加借貸科目組合並切換五種查找方式，
// 在分類清單勾選分類，最後預覽，預覽的錯誤標在條件列上。
//
// 每個測試都執行正式的 state.js、ui-core.js、filter-values.js、filter-vouchers.js 與第五步的五個檔案（steps/filter-step-*.js 與 filter-step.js），
// 在假 DOM 上畫出第五步並用點擊與選單操作；合成資料只控制邊界。
// 第五步的儲存、另存、移除與重新檢查屬於情境 12、13（frontend-journey-filter-saved.test.cjs），這個檔不寫那些流程的斷言。
//
// 斷言規則：用字已經定案的文字逐字比對，其他文字只比對意思；預期值寫死，不由被測程式算出；
// 送出的 payload 精確比對，而且不得帶 `__` 開頭的鍵。
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { treeDom, readWwwroot, exposeAt, plain, settle, bootFullApp, flush, FILTER_STEP_PARTS } = require('./support/frontend-fixtures.cjs');
const { contractJetApi } = require('./support/action-contract.cjs');

const ALL_MAPPED = { hasAnyCategory: true, hasRevenue: true, hasCounterpart: true };

// 本檔自己的夾具：和共用模組的 filterOperationFixture 相同，執行正式腳本並訂閱重繪；
// 另外掛出 toWireDraft（情境 11 要看草稿送出的形狀），並記下預覽與傳票分頁的請求。
// 只服務這個檔的夾具留在這裡，沒有收進共用模組。
// availableGlFields 是後端 project.load 與 mapping.commit.gl 回傳的可用欄位清單（欄位可用性只在後端算一次）；
// 沒給時已確認的配對不帶清單，畫面不檢查欄位，和後端沒有清單時相同。
function buildFixture(options = {}) {
  // zerosThreshold 模擬後端照 GL 借方平均算出、跟著已確認 GL 配對帶回的尾數 0 的個數。
  // 沒有國定假日清單時，第五步的假日條件停用並寫原因；補班日清單也比照。
  // 這個夾具代表兩份清單都已上傳的案件，預設帶 3 天國定假日與 1 天補班日；要看沒有清單的畫面時傳 calendar: null 或只帶其中一份。
  const { accountMapping = ALL_MAPPED, glMapping = null, rdeFields = [], accountCategory = null, saved = [], availableGlFields = null, zerosThreshold = 4,
    calendar = { holidayCount: 3, makeupDayCount: 1 },
    project = { projectId: 'filter-build-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: '2025-12-01' } } = options;
  const dom = treeDom(), requests = [];
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const context = vm.createContext({ window, document: dom.document });
  // 第五步分成五個檔案：前四個先載入，主檔再掛三個函式；syncViewState 在主檔是轉呼叫，名稱仍看得到。
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
  if (calendar) store.setCalendarState(calendar);
  if (glMapping || rdeFields.length || availableGlFields) store.setMappingCommitted('gl', { mapping: glMapping || {}, mode: 'signed', options: { rdeFields },
    zerosThreshold, ...(availableGlFields ? { availableGlFields } : {}) });
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

// 欄位條件分頁，程式裡的識別是 field。
const fieldPane = options => { const f = buildFixture(options); f.click('[data-condition-source="field"]'); return f; };
// 卡片以代號識別（Ui.FILTER_COMMON_CONDITIONS 的 key）。
const card = (f, key) => f.$('[data-common-card="' + key + '"]');
// 常用篩選條件和預篩選分頁用同一種清單列：card() 找到的是列右邊那一把鍵，鍵上只寫「加入」或「已加入」，
// 名稱與停用原因寫在同一列，這裡從那一列讀。
const cardRow = (f, key) => card(f, key).closest('.filter-check');
const cardName = (f, key) => cardRow(f, key).querySelector('.filter-check__label').textContent;
const cardNotes = (f, key) => cardRow(f, key).querySelectorAll('.filter-check__note');
// 停用的條件最後一段說明是停用原因。
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

// 停用原因的句子逐字比對：還沒匯入與匯入了但沒有任何分類同一句，缺收入或對方分類時寫出分類名稱。
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
      // 停用的鍵連到同一列寫著原因的那一段，和預篩選分頁相同。
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
  // 規則前置條件只有一個來源：停用原因由前端照抄後端的規則需求表，後端對三張收入卡只看收入與對方分類。
  // 收入對方科目那張卡缺收入與對方分類時分開說，各寫一句。
  // 每張卡在「還沒匯入」與「匯入了但沒有任何分類」寫同一句。
  for (const key of Object.keys(MAPPING_CARD_TYPES)) assert.equal(notes['not imported' + key], notes['imported without categories' + key], key);
  assert.equal(notes['imported without categories' + FIXED_ASSET], noCategory);
  for (const key of [REVENUE, MANUAL_REVENUE]) assert.equal(notes['imported without categories' + key], noRevenue, key);
  assert.equal(notes['imported without categories' + COUNTERPART], noRevenue + '；' + noCounterpart, 'the counterpart card names both missing categories, one sentence each');
  for (const key of [REVENUE, COUNTERPART, MANUAL_REVENUE]) assert.equal(notes['only general categories' + key], noRevenue, key + ' names the missing revenue category');
  assert.equal(notes['revenue without counterpart' + COUNTERPART], noCounterpart, 'the counterpart card names the missing counterpart categories');
});

// 樣態下拉裡常用篩選條件的收入選項看配對內容，不看有沒有匯入科目配對檔。
test('the pattern menu offers revenue-only common conditions only when the mapping has revenue and counterpart content', () => {
  const commonOptions = mapping => {
    const f = buildFixture({ accountMapping: mapping });
    // 條件列的下拉只列同一來源的條件，沒有分組。
    // 從常用篩選條件加入「編製人員與核准人員相同」，看這一列下拉的選項。
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

// 常用查法掛在欄位底下：類別組的「科目分類」欄位底下排著「借方與貸方分類組合」，同組另有「借貸別」欄位。
// 欄位本身不能用時，欄位的「加入」與底下的常用查法一起停用，原因寫在分頁最後那一段，和 GL 欄位沒有配對時同一處。
// 這裡核對科目分類的「加入」與「借方與貸方分類組合」都在科目分類那一列、都連到分頁最後那一段，
// 那一段和固定資產與費用那張卡同一句，按了都不加入；借貸別不受科目配對影響。
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

// 收入對方科目那張卡的說明逐字是這一句。
const COUNTERPART_CASH_NOTE = '現金不算一般對方科目；要排除現金銷貨，改用預篩選條件『未預期借貸組合』。';
// 這句只寫在條件列，卡片上不寫：條件列和卡片同在第五步的畫面上，兩處都寫，同一句會出現兩次。
test('the revenue counterpart condition row explains cash and the card shows only its name', () => {
  const f = buildFixture();
  assert.equal(cardNotes(f, COUNTERPART).length, 0, 'the card shows only its name');
  f.click('[data-common-card="' + COUNTERPART + '"]');
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row.querySelectorAll('.rule-explanation').some(node => node.textContent === COUNTERPART_CASH_NOTE), 'condition row shows the approved sentence');
  // 停用時卡片只寫停用原因。
  const blocked = buildFixture({ accountMapping: { hasAnyCategory: true, hasRevenue: true, hasCounterpart: false } });
  const blockedNotes = cardNotes(blocked, COUNTERPART).map(node => node.textContent);
  assert.equal(blockedNotes.length, 1); assert.notEqual(blockedNotes[0], COUNTERPART_CASH_NOTE);
});

// 固定資產與費用那張卡也一樣：先新增並配對分類的說明不寫在卡片上，加入後在條件列寫一次。
test('the fixed-asset card shows only its name and the condition row shows its setup note', () => {
  const f = buildFixture();
  const note = f.window.JetUi.FILTER_COMMON_CONDITIONS.find(item => item.key === FIXED_ASSET).description;
  assert.ok(note, 'the fixed-asset card keeps a setup note for the condition row');
  assert.equal(cardNotes(f, FIXED_ASSET).length, 0, 'the card shows only its name');
  f.click('[data-common-card="' + FIXED_ASSET + '"]');
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row.querySelectorAll('.rule-explanation').some(node => node.textContent === note), 'condition row shows the fixed-asset note');
});

// 另一張卡的說明在條件列直接看得到，這張卡的說明卻只在「查找說明」裡，上面那則測試分不出來。
// 借貸組合的說明收在「查找說明」，免得條件列下方多出來的註解讓版面顯得雜亂；
// 固定資產與在建工程的說明也在其中，所以收在「查找說明」、只出現一次。
test('the fixed-asset setup note appears once in the condition row, inside the lookup help folded for account pairs', () => {
  const f = buildFixture();
  const note = f.window.JetUi.FILTER_COMMON_CONDITIONS.find(item => item.key === FIXED_ASSET).description;
  f.click('[data-common-card="' + FIXED_ASSET + '"]');
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  const shown = row.querySelectorAll('.rule-explanation').filter(node => node.textContent === note);
  assert.equal(shown.length, 1, 'the note appears once in the condition row');
  assert.ok(shown[0].closest('details[data-rule-help]'), 'the note sits in the lookup help');
});

// 季末那張卡沒有天數怎麼算的說明：卡片只有名稱，加入後的條件列也不寫這句。
test('the quarter-end revenue card has no explanation on the card or in its condition row', () => {
  const f = buildFixture();
  assert.equal(f.window.JetUi.FILTER_COMMON_CONDITIONS.find(item => item.key === REVENUE).description, undefined);
  assert.equal(cardNotes(f, REVENUE).length, 0, 'the card shows only its name');
  f.click('[data-common-card="' + REVENUE + '"]');
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row, 'the card adds a condition row');
  assert.doesNotMatch(row.textContent, /季底往前算|從查核期末往前算/);
});

// 核對「常用篩選條件」分頁的文字、十張卡依序出現、卡片只寫條件名稱且沒有字母。
// 卡片名稱逐字照 ui-core.js 的 FILTER_COMMON_CONDITIONS。
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
// 常用篩選條件分頁只放十張卡片，沒有區塊標題，也看不到小標「單項條件」。
test('the common tab reads 常用篩選條件, lists the ten cards in order without a subtitle and shows no card letters', () => {
  const f = buildFixture();
  assert.equal(f.$('[data-condition-source="common"]').textContent, '常用篩選條件');
  assert.equal(f.$('[data-condition-pane="common"]').hidden, false, 'the common tab is the first one shown');
  assert.equal(f.$('[data-condition-pane="common"] .filter-source-block__title'), null, 'the tab has no subtitle');
  assert.doesNotMatch(screenTexts(f.$('[data-condition-pane="common"]')).join('\n'), /單項條件/);
  const cards = f.$$('[data-condition-pane="common"] [data-common-card]');
  // 名稱寫在列上，鍵上寫「加入」；一列只有名稱和鍵，沒有字母。
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

// 條件總覽三個分頁用同一種列，名稱在左、右邊一把鍵。常用篩選條件與預篩選的條件還不在作用中的組時寫「加入」，
// 已在這一組時鍵往下凹、寫「已加入」，再按一下就移出。欄位條件分頁的鍵永遠是「加入」，加入後再按一下就再加一條。
// 常用篩選條件帶入的條件不算預篩選分頁加入的。換作用中的組時，兩個分頁換成那一組的狀態，只換掉狀態變了的列，換上的鍵照樣能按。
// 「也在其他組」寫在名稱下一行，不放進名稱那一格，加入時浮起來的標籤只有名稱。
// 「加入到第 N 組」是組號的分段切換，換組用 pick 選組號。
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

// 名稱和自訂條件用相同的寫法：依加入順序列出前兩項條件，卡片帶入的用卡片名稱，超過兩項加上總數。
// 動機每條自訂條件多一行「自訂：」。
test('card names name the draft, a custom condition in the same group is named after it and the rationale adds a 自訂 line', () => {
  const f = buildFixture();
  f.click('[data-common-card="blankDescription"]'); f.click('[data-common-card="specificTrailingDigits"]');
  assert.equal(f.draft().name, '空白摘要、特定尾數(如 999999/000000 結尾)');
  f.click('[data-common-card="specificTrailingDigits"]');
  assert.equal(f.draft().name, '空白摘要');
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
// 條件畫面不能出現目錄代號，例如「舊表」、「範例1」或「P+S」這類字母串接；這些代號在畫面上顯得不專業。
const CATALOGUE_CODES = /舊表|A 到 U|A–U|範例 ?\d|工作簿範例|[A-U](\+[A-U])+/;

// 每個範例都在新的案件畫面套用（空白草稿直接套用，不會跳出「還沒儲存」的詢問，那屬於 frontend-journey-filter-saved.test.cjs 的儲存流程）。
// 範例列在右側條件組合上方的「從情境範例開始」，預設收起、按標題打開，共十一個。這裡核對清單在托盤裡、托盤標題、一開始收起、
// 排在條件組合上方、沒有另外的展開按鈕、任何條件分頁都沒有範例，套用後清單仍在托盤裡。
// 托盤和條件總覽同在篩選工作區，整個工作區都不能出現目錄代號。
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
  // 欄位條件分頁沒有收合鈕，也沒有排除區域或待判定的說法。
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

// 連接只寫 AND 與 OR，是兩格的分段切換（一組單選鈕），選項字沒有「全部符合」「任一符合」；選項值是送出的契約，精確比對。
// 「同一傳票」併在整張傳票條件裡，沒有比對範圍的選單，也沒有主要條件。
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

// 組內與組間連接的分段切換前面不寫「組內條件」「組與組之間」，只留 AND、OR；
// 連接的是什麼由版面呈現，螢幕報讀仍讀得到各自的名稱。組內的切換在兩條條件之間的第一條分隔線 .rule-join--switch 上。
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

// 連接條件的 AND、OR 兩格一樣寬；只靠左右留白撐開時 AND 比 OR 寬，切換鍵看起來一邊長一邊短。
// 組內與組間的切換鍵都只有兩格，由同一條規則排成等寬的兩欄。
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

// 移除鈕的名稱不逐字比對，只比對有「移除」。
// 類別組的「科目分類」欄位只加入一條科目分類條件，借貸方向從同組的「借貸別」加入；人工分錄從「人工/自動分錄」欄位加入，
// 「金額尾數連續多個 0」是一條條件。每個入口各加一列，共 5 列；移除第 1 組的借貸別那一列，
// 排在它後面的科目分類條件不變。另核對借貸別那一列與尾數連續多個 0 那一列。
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

test('"add conditions to" moves the target group and the groups never embed the condition catalogue', () => {
  const f = buildFixture();
  f.click('[data-common-card="blankDescription"]');
  assert.equal(f.$('[data-condition-target]'), null, 'one group needs no target choice');
  // 空白摘要與特定尾數兩張卡都以欄位條件加入。
  assert.match(f.$('.rule-row[data-gi="0"][data-ri="0"] .filter-rule-heading').textContent, /傳票摘要/);
  f.click('[data-action="add-set"]');
  // 「加入到」是條件總覽標題同一列的組號分段切換：選項是單選鈕，目前的組看選中的單選鈕，換組用 pick。
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
  f.click('[data-condition-source="field"]'); f.click('[data-add-field="voucherNumber"]');
  assert.deepEqual(rulesOf(f), [['fieldValue', 'fieldValue', 'fieldValue'], []]);
  assert.equal(f.dom.content.querySelectorAll('.scenario-set [data-common-card], .scenario-set [data-add-field], .scenario-set [data-add-shortcut], .scenario-set [data-template-key]').length, 0);
  assert.equal(f.$('[data-inline-picker]'), null); assert.equal(f.$('[data-pick-for-group]'), null);
  assert.equal(f.dom.content.querySelectorAll('.scenario-set [data-select-group]').length, 0);
});

// AND、OR 放在被連接的兩者之間的分隔線上。組內第一條分隔線是這一組的分段切換，後面的只寫同一個字；
// 兩組之間與子條件之間也是分隔線上的切換。組標頭沒有切換，條件列也沒有只給連接詞用的 data-join。
// 多組時「加入到第 1、2 組」和條件總覽的標題同一列，組號是同一種分段切換，點右側的組時選中的組號跟著換。
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

// 分類欄位的標題不逐字比對，只比對意思。
test('the account pair card switches between five lookup modes and every category legend stays in plain words', () => {
  // 「借方與貸方分類組合」掛在科目分類欄位底下。
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

const ACCOUNT_CATEGORIES = { revision: 3, categories: [
  { categoryId: 'builtin.revenue', label: 'Revenue', ordinal: 0, semanticRole: 'revenue', isBuiltIn: true },
  { categoryId: 'builtin.cash', label: 'Cash', ordinal: 1, semanticRole: 'cash', isBuiltIn: true },
  { categoryId: 'custom.ppe', label: '固定資產', ordinal: 2, semanticRole: 'others', isBuiltIn: false }] };
// 類別組的「科目分類」欄位只加入一條科目分類條件，分類留空待選，也不帶借貸別的子條件。
// 這裡核對一開始沒有勾選，先勾現金再勾固定資產，分類條件就是第 1 組第 1 條；另核對重複勾選、改名與讀回。
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

// 「金額尾數連續多個 0」是一條條件，用自己的比較方式，排在篩選方式的「尾數」那一組。
// 審計員從欄位條件加入金額，篩選方式選「尾數連續多個 0」，個數預設帶第四步預篩選的個數，輸入框只收 1 到 12，後面寫「個 0」，
// 條件列寫出整數部分為 0 的金額不符合；改成「排除」換成沒有連續的比較方式；個數填 0 時說要填 1 到 12 的整數。
// 這個比較方式不看正負號，攸關資料元素的金額選它時不出現比較基準。
// 0 的個數照 IDEA 版由 GL 算出、跟著已確認的 GL 配對帶回，預設是這個案件的個數，夾具帶 4。
test('the trailing-zero comparison defaults to the case zero count, takes one to twelve and has no amount basis', () => {
  const f = fieldPane({ rdeFields: [{ fieldId: 'rde.money', sourceColumn: 'Tax', label: '稅額', valueType: 'amount' }] });
  const ui = f.window.JetUi;
  f.click('[data-add-field="amount"]');
  const row = () => f.$('.rule-row[data-gi="0"][data-ri="0"]');
  const rule = () => f.draft().groups[0].conditions[0];
  const option = row().querySelector('[data-value-kind]').querySelectorAll('option').find(node => node.getAttribute('value') === 'zeros');
  assert.equal(option.textContent, '尾數連續多個 0');
  assert.equal(option.closest('optgroup').getAttribute('label'), '尾數');
  f.choose('.rule-row[data-gi="0"][data-ri="0"] [data-value-kind]', 'zeros');
  assert.deepEqual(plain([rule().operator, rule().value, rule().amountBasis]), ['endsWithZeros', '4', 'absolute']);
  const count = row().querySelector('input[data-value-key="value"]');
  assert.deepEqual([count.getAttribute('type'), count.getAttribute('min'), count.getAttribute('max'), count.value], ['number', '1', '12', '4']);
  assert.ok(row().querySelectorAll('span').some(node => node.textContent === '個 0'), 'the count is followed by 個 0');
  assert.ok(row().querySelectorAll('.rule-explanation').some(node => node.textContent.includes('整數部分為 0 的金額不符合')));
  assert.equal(ui.FilterValues.summary(rule(), f.store.getState()), '金額 尾數連續「4」個 0；空白不列入');
  f.choose('.rule-row[data-gi="0"][data-ri="0"] [data-value-polarity]', 'exclude');
  assert.equal(rule().operator, 'notEndsWithZeros');
  assert.equal(ui.FilterValues.summary(rule(), f.store.getState()), '金額 尾數沒有連續「4」個 0；空白不列入');
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

// 額外加入的攸關資料元素數值欄位，金額比較的預設照舊版用絕對值，仍可改成含正負號；
// 舊版 IDEA JET 的數字條件一律用 @Abs 比較。從條件總覽按「加入」與在子條件組加入，
// 兩條路徑都預設絕對值，改成含正負號後照實送出。
test('an extra amount field added from the overview or inside a sub-condition group compares absolute amounts by default', () => {
  const f = fieldPane({ rdeFields: [{ fieldId: 'rde.money', sourceColumn: 'Tax', label: '稅額', valueType: 'amount' }] });
  const ui = f.window.JetUi;
  const selectedBasis = row => row.querySelector('[data-value-key="amountBasis"]').querySelectorAll('option')
    .find(option => option.getAttribute('selected') !== null).getAttribute('value');
  f.click('[data-add-field="rde.money"]');
  const rule = () => f.draft().groups[0].conditions[0];
  assert.equal(rule().amountBasis, 'absolute');
  assert.equal(ui.FilterValues.wire(rule(), f.store.getState()).amountBasis, 'absolute');
  assert.equal(selectedBasis(f.$('.rule-row[data-gi="0"][data-ri="0"]')), 'absolute');
  f.choose('.rule-row[data-gi="0"][data-ri="0"] [data-value-key="amountBasis"]', 'signed');
  assert.equal(ui.FilterValues.wire(rule(), f.store.getState()).amountBasis, 'signed', 'the auditor can still compare signed amounts');
  f.click('[data-add-compound="group"]');
  const kind = f.$('[data-child-kind]');
  assert.ok(kind.querySelectorAll('option').some(option => option.getAttribute('value') === 'field:rde.money'), 'the group offers the extra amount field');
  kind.value = 'field:rde.money';
  f.click('[data-child-add]');
  const child = f.draft().groups[0].conditions[1].conditions[0];
  assert.equal(child.fieldId, 'rde.money');
  assert.equal(ui.FilterValues.wire(child, f.store.getState()).amountBasis, 'absolute');
});

// 文字欄位的否定比較，例如「不包含」「不在清單中」，照舊版預設把欄位空白的分錄也列入，審計員可以取消；日期欄位不列入。
// 後端收到沒帶這個值的條件時當成不列入，所以這個預設只在畫面上。
// 傳票摘要改成「排除」時自動勾選空白也列入，改回「符合」時取消；
// 「完整內容相同」改成排除就是「不在清單中」，審計員取消勾選後照實送出，換到另一種排除的比較方式也保留這個選擇；傳票核准日與金額改成「排除」時不勾選。
test('excluding text values counts blank entries by default and the auditor can untick it, while dates and amounts stay unticked', () => {
  const f = fieldPane();
  const ui = f.window.JetUi;
  const at = index => '.rule-row[data-gi="0"][data-ri="' + index + '"] ';
  const rule = index => f.draft().groups[0].conditions[index];
  const blank = index => f.$(at(index) + 'input[data-value-key="includeBlank"]');
  const sent = index => ui.FilterValues.wire(rule(index), f.store.getState()).includeBlank;

  f.click('[data-add-field="description"]');
  assert.deepEqual(plain([rule(0).operator, rule(0).includeBlank, sent(0), blank(0).checked]), ['contains', false, false, false]);
  f.choose(at(0) + '[data-value-polarity]', 'exclude');
  assert.deepEqual(plain([rule(0).operator, rule(0).includeBlank, sent(0), blank(0).checked]), ['notContains', true, true, true]);
  assert.match(ui.FilterValues.summary(rule(0), f.store.getState()), /；空白也符合$/);
  f.choose(at(0) + '[data-value-polarity]', 'keep');
  assert.deepEqual(plain([rule(0).operator, rule(0).includeBlank, sent(0), blank(0).checked]), ['contains', false, false, false]);

  f.choose(at(0) + '[data-value-kind]', 'exact');
  f.choose(at(0) + '[data-value-polarity]', 'exclude');
  assert.deepEqual(plain([rule(0).operator, rule(0).includeBlank, sent(0)]), ['notIn', true, true]);
  blank(0).checked = false; blank(0).fire('change');
  assert.deepEqual(plain([rule(0).includeBlank, sent(0), blank(0).checked]), [false, false, false]);
  f.choose(at(0) + '[data-value-kind]', 'starts');
  assert.deepEqual(plain([rule(0).operator, rule(0).includeBlank, sent(0)]), ['notStartsWith', false, false]);

  f.click('[data-add-field="approvalDate"]');
  f.choose(at(1) + '[data-value-polarity]', 'exclude');
  assert.deepEqual(plain([rule(1).operator, rule(1).includeBlank, sent(1), blank(1).checked]), ['notIn', false, false, false]);
  f.click('[data-add-field="amount"]');
  f.choose(at(2) + '[data-value-polarity]', 'exclude');
  assert.deepEqual(plain([rule(2).operator, rule(2).includeBlank, sent(2)]), ['notEquals', false, false]);
});

// 科目分類先隱藏上下層級，新案件的分類都是平的一層。分類都是平的時，科目分類條件的分類清單
// 不畫展開鈕的空位，從科目樹選取時不寫「包含下層分類」；案件已有分類設上層時，兩處仍畫出展開鈕、寫出包含下層分類。
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
test('preview sends the composed groups exactly and marks a rejected condition on its row', async () => {
  const f = buildFixture();
  f.click('[data-common-card="blankDescription"]');
  f.click('[data-action="add-set"]');
  f.click('[data-action="add-set"]');
  f.click('[data-common-card="specificTrailingDigits"]');
  // 人工分錄條件從類別組的「人工/自動分錄」欄位加入。
  f.click('[data-condition-source="field"]'); f.click('[data-add-field="manualAuto"]');
  // 組內與組間的連接是分段切換，用 pick 點 OR、AND 那一格。
  f.pick('[data-set-combinator][data-gi="2"]', 'OR');
  f.pick('[data-set-join][data-set-join-index="2"]', 'AND');
  // 空白摘要卡以欄位條件加入，精確核對送出的欄位條件格式。
  assert.deepEqual(rulesOf(f), [['fieldValue'], [], ['fieldValue', 'manualAuto']]);
  f.click('[data-action="preview-scenario"]');
  await settle();
  const request = f.requests.filter(r => r.action === 'filterPreview');
  assert.equal(request.length, 1);
  const filler = { field: 'description', values: [], mode: 'contains', normalization: 'preserve', from: '', to: '', drCr: 'debit', isManual: 'true',
    pairMode: 'exact', debitCategoryIds: ['builtin.receivables'], creditCategoryIds: ['builtin.revenue'], fieldId: '', operator: '',
    amountBasis: 'absolute', value: '', windowDays: '' };
  // 名稱用條件名稱的寫法，動機每行不以字母開頭，編輯來源送第 2 版的卡片代號，來源代碼是 common。
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

// 用整個畫面的夾具（setTimeout 排進佇列），加入條件後跑完所有排定的計時器、各觸發一次 setInterval，
// 再核對條件仍是「選取中」。測試名稱由 GuiScenarioCoverageTests 核對，改名時一起改。
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

// 用畫面上的按鈕逐一加入及移除每一項常用查法，核對加入的條件型別。
// 常用查法掛在各欄位底下；傳票核准日底下也有週末、國定假日與日期區間，「財報準備日起核准」也放在這裡。
// 借貸別、人工/自動分錄與科目分類是類別組的欄位，「借方與貸方科目編號組合」在情境範例，「未預期借貸組合」只在預篩選分頁，
// 「金額尾數連續多個 0」是一條條件。這裡核對每一項排在所屬欄位那一列、鍵上寫短名稱、完整名稱在滑鼠提示與螢幕報讀，
// 畫面上的常用查法依序就是這張清單，也沒有分類的展開鈕。
// 類別組的三個欄位另外逐一加入與移除，借方與貸方科目編號組合從情境範例核對借方與貸方各一個子條件。
// 欄位、鍵上的字、完整名稱與型別逐字照 ui-core.js 的 FIELD_SHORTCUT_GROUPS 寫死，不從畫面推導。
// 測試名稱由 GuiScenarioCoverageTests 核對，改名時一起改。
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
    // 0 的個數照 IDEA 版由 GL 算出、跟著已確認的 GL 配對帶回：加入時帶這個案件的個數，夾具帶 4。
    if (key === 'trailingZeros') assert.deepEqual([rules[0].field, rules[0].operator, rules[0].value, rules[0].amountBasis], ['amount', 'endsWithZeros', '4', 'absolute']);
    const remove = f.$('.rule-row[data-ri="0"]').querySelector('[data-action="remove-rule"]');
    assert.ok(remove, key + ' offers a remove control on its row');
    remove.fire('click');
    assert.equal(allRules().length, 0, key + ' is removed');
  }
  // 類別組的三個欄位各加入一條既有型別的條件。
  for (const [field, type] of [['drCr', 'drCrOnly'], ['manualAuto', 'manualAuto'], ['accountCategory', 'accountSide']]) {
    f.click('[data-add-field="' + field + '"]');
    assert.deepEqual(plain(allRules().map(rule => rule.type)), [type], field);
    f.$('.rule-row[data-ri="0"] [data-action="remove-rule"]').fire('click');
    assert.equal(allRules().length, 0, field + ' is removed');
  }
  // 借方與貸方科目編號組合在情境範例，套用後是借方與貸方各一個明示方向的子條件。
  const example = buildFixture();
  example.click('[data-template-key="accountNumberPair"]');
  const pair = example.draft().groups[0].conditions[0];
  assert.deepEqual([pair.conditions[0].conditions[0].drCr, pair.conditions[1].conditions[0].drCr], ['debit', 'credit']);
});

// ------------------------------------------------------------------------------------------------
// 欄位「加入」打開的選單
// ------------------------------------------------------------------------------------------------

// 有常用查法的欄位，「加入」鍵打開選單，第一項是這個欄位的一般條件、寫欄位名稱，下面是常用查法；欄位名稱只是文字。
// 審計員按「加入」打開選單，按一項只加入那一條，選單隨即收起；也可以在「加入」上按方向鍵下打開，用方向鍵選，Esc 收起並回到「加入」。
// 沒有常用查法的欄位按「加入」直接加入。選單是共用的 .jet-menu，一次只開一個。
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

// 欄位不能用時打不開選單，原因是分頁最後那一段；
// 欄位能用但常用查法另有前置條件時，那一項在選單裡停用，原因寫在那一項的下一行，只寫一次。
// 選單的開關 .jet-menu__toggle 是「加入」；欄位不能用時，選單第一項的一般條件也停用，連到同一段原因。
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
  // 這句的用字已定案。
  assert.equal(next.textContent, '尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫');
  assert.equal(row('approvalDate').querySelectorAll('.filter-check__note').length, 1, 'the reason is written once, inside the menu');
});

// ------------------------------------------------------------------------------------------------
// 預篩選分頁
// ------------------------------------------------------------------------------------------------

// 條件總覽有三個分頁；審計員習慣直接套用預篩選條件，所以預篩選分頁條列每一條規則，按「加入」直接帶入那一條。
// 名稱逐字照預篩選規則的畫面名稱（ui-core.js 的 PRESCREEN_RULES，鏡像 Domain 標籤表），不從畫面推導。
// 清單共十條，第四步照舊版不含編製分錄較少的人員與使用較少的科目。
const PRESCREEN_RULES = [
  ['prepPeriodApproval', '財報準備日起核准'], ['suspiciousKeywords', '摘要特定描述'], ['unexpectedAccountPair', '未預期借貸組合'],
  // 0 的個數照 IDEA 版由 GL 算出、跟著已確認的 GL 配對帶回：知道個數時寫「金額尾數連續 N 個 0」，夾具帶 4。
  ['trailingZeros', '金額尾數連續 4 個 0'], ['weekendPosting', '週末過帳'], ['weekendApproval', '週末核准'],
  ['holidayPosting', '假日過帳'], ['holidayApproval', '假日核准'], ['blankDescription', '空白摘要'],
  ['nonAuthorizedPreparer', '非授權編製人員']
];
const prescreenWire = f => f.wire().groups.map(group => group.conditions.map(rule => [rule.type, rule.prescreenKey]));

test('the prescreen tab lists every prescreen rule and each add button brings in exactly that rule', () => {
  // 「非授權編製人員」要用傳票建立人員，這裡配好欄位，十條都能加入。
  const f = buildFixture({ glMapping: { createdBy: 'Maker' } });
  f.store.setAuthorizedPreparerState({ rowCount: 2 });
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
// 前端讀後端算好的可用欄位，這裡給一份沒有傳票建立人員的可用欄位清單，表示傳票建立人員未配對；
// 欄位的原因接上和第四步相同的下一步，寫出可以回欄位配對補上。
const WITHOUT_CREATED_BY = ['voucherNumber', 'lineItem', 'glDate', 'approvalDate', 'accountNumber', 'accountName', 'description', 'sourceModule', 'approvedBy', 'isManual', 'amount'];
test('a prescreen rule the backend would reject is disabled with its reason and adds nothing when pressed', () => {
  const f = buildFixture({ accountMapping: { hasAnyCategory: true, hasRevenue: false, hasCounterpart: true }, availableGlFields: WITHOUT_CREATED_BY,
    project: { projectId: 'filter-build-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31', prepStartDate: null } });
  f.click('[data-condition-source="prescreen"]');
  // 這個夾具沒有配對傳票建立人員，後端會擋「非授權編製人員」。
  // 傳票建立人員未配對的原因由下面非授權編製人員那一句核對。
  assert.deepEqual(f.$$('[data-add-prescreen]').filter(button => button.disabled).map(button => button.getAttribute('data-add-prescreen')),
    ['prepPeriodApproval', 'unexpectedAccountPair', 'nonAuthorizedPreparer']);
  const reason = key => f.$('#' + f.$('[data-add-prescreen="' + key + '"]').getAttribute('aria-describedby')).textContent;
  // 這句的用字已定案。
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

// 沒有上傳國定假日清單時，第五步的假日條件比照未配對的欄位停用並寫原因，和第四步同一句；
// 「非營業日」也看週末，沒有清單時照常可用。上傳清單之後全部恢復可用。
// 這裡只缺國定假日清單。範例「財報準備期間的人工假日分錄」也排除補班日，還要補班日清單，
// 所以補班日清單照常帶著，範例的原因才只寫國定假日這一句；只缺補班日清單的情況見下一個測試。
test('without a holiday list every holiday condition is disabled with the step-2 reason while non-business-day stays available', () => {
  const holidayReason = '尚未上傳國定假日清單，請先在第二步「國定假日與補班日」上傳';
  const f = buildFixture({ ...EXAMPLE_READY, calendar: { makeupDayCount: 1 } });
  const describedText = node => f.$('#' + node.getAttribute('aria-describedby')).textContent;
  f.click('[data-condition-source="field"]');
  for (const key of ['glDateHoliday', 'approvalDateHoliday']) {
    const item = f.$('[data-add-shortcut="' + key + '"]');
    assert.equal(item.disabled, true, key + ' is disabled');
    assert.equal(describedText(item), holidayReason, key + ' says why');
  }
  for (const key of ['glDateWeekend', 'glDateNonBusinessDay', 'approvalDateNonBusinessDay'])
    assert.equal(f.$('[data-add-shortcut="' + key + '"]').disabled, false, key + ' stays available');
  // 已有的日期條件改篩選方式時，「國定假日」那一項停用，名稱後面寫同一句原因。
  f.$('[data-add-shortcut="glDateWeekend"]').fire('click');
  const holidayOption = () => f.$('.rule-row[data-ri="0"] [data-value-kind]').querySelectorAll('option')
    .find(option => option.getAttribute('value') === 'holiday');
  assert.notEqual(holidayOption().getAttribute('disabled'), null, 'the holiday choice is disabled');
  assert.equal(holidayOption().textContent, '國定假日（' + holidayReason + '）');
  f.$('.rule-row[data-ri="0"] [data-action="remove-rule"]').fire('click');
  // 預篩選分頁的假日過帳與假日核准停用並寫同一句，按了也不加入。
  f.click('[data-condition-source="prescreen"]');
  for (const key of ['holidayPosting', 'holidayApproval']) {
    const button = f.$('[data-add-prescreen="' + key + '"]');
    assert.equal(button.disabled, true, key + ' is disabled');
    assert.equal(describedText(button), holidayReason, key + ' says why');
    button.fire('click');
  }
  assert.equal(f.draft().groups.flatMap(group => group.conditions).length, 0, 'a disabled condition adds nothing');
  // 情境範例「財報準備期間的人工假日分錄」也要國定假日清單。
  assert.equal(f.$('[data-template-key="preparationManualHoliday"]').disabled, true);
  assert.equal(f.$('#filter-example-note-preparationManualHoliday').textContent, holidayReason);
  f.store.setCalendarState({ holidayCount: 3, makeupDayCount: 1 });
  for (const key of ['holidayPosting', 'holidayApproval']) assert.equal(f.$('[data-add-prescreen="' + key + '"]').disabled, false, key + ' after upload');
  assert.equal(f.$('[data-template-key="preparationManualHoliday"]').disabled, false, 'the example after upload');
  f.click('[data-condition-source="field"]');
  for (const key of ['glDateHoliday', 'approvalDateHoliday']) assert.equal(f.$('[data-add-shortcut="' + key + '"]').disabled, false, key + ' after upload');
});

// 沒有補班日清單時，「補班日」比照國定假日停用並在名稱後寫原因，用到補班日的範例也一起停用；
// 兩份清單各自判斷。
test('without a make-up day list the make-up choice is disabled with its own reason while holidays stay available', () => {
  const makeupReason = '尚未上傳補班日清單，請先在第二步「國定假日與補班日」上傳';
  const f = buildFixture({ ...EXAMPLE_READY, calendar: { holidayCount: 3 } });
  // 範例「財報準備期間的人工假日分錄」排除補班日，所以也要補班日清單；只缺這份清單時原因只寫這一句。
  const example = () => f.$('[data-template-key="preparationManualHoliday"]');
  assert.equal(example().disabled, true, 'the example needs the make-up list');
  assert.equal(f.$('#filter-example-note-preparationManualHoliday').textContent, makeupReason);
  f.click('[data-condition-source="field"]');
  f.$('[data-add-shortcut="glDateWeekend"]').fire('click');
  const option = key => f.$('.rule-row[data-ri="0"] [data-value-kind]').querySelectorAll('option')
    .find(node => node.getAttribute('value') === key);
  assert.notEqual(option('makeup').getAttribute('disabled'), null, 'the make-up choice is disabled');
  assert.equal(option('makeup').textContent, '補班日（' + makeupReason + '）');
  assert.equal(option('holiday').getAttribute('disabled'), null, 'the holiday choice stays available');
  f.store.setCalendarState({ holidayCount: 3, makeupDayCount: 1 });
  assert.equal(option('makeup').getAttribute('disabled'), null, 'the make-up choice after upload');
  assert.equal(option('makeup').textContent, '補班日');
  assert.equal(example().disabled, false, 'the example after upload');
});

// 從預篩選分頁一次加入「未預期借貸組合」，再查看符合的傳票。
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
  // 口徑說明只在這種情形出現，寫成一句，免得常駐的說明太多。
  assert.match(pane, /分錄筆數包含整張傳票的分錄/);
  assert.doesNotMatch(pane, /標示為參考的分錄不計入/);
  assert.match(pane, /SYN-1/);
});

// 值清單一開始就是固定的多行輸入框，免得輸入時框突然變高、顯得突兀；
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
// 第五步不能用 GL 沒有配對的欄位
// ------------------------------------------------------------------------------------------------

// GL 沒有配對的欄位停用並寫原因：欄位按鈕照樣列出但不能按，下方寫出原因以及可以回欄位配對補上，
// 常用查核條件與範例用同一套判斷。
// 可用欄位照後端在「傳票核准日、分錄來源模組、傳票建立人員、傳票核准人員與人工/自動分錄都沒有選來源欄」時送的清單寫死；
// 傳票文件項次沒有來源欄時系統自動編號，算已配對，所以在清單裡。
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

// 欄位條件分成日期、數字、文字、類別四組，類別組放借貸別、人工/自動分錄與科目分類，三個入口也用 data-add-field；
// 人工/自動分錄要 GL 的人工/自動分錄欄位，這個夾具沒有配對，跟著停用並寫進同一句。原因那一段放在欄位條件分頁的最後。
// 這裡核對四組依序列出的十四個入口、五個停用，原因是欄位條件分頁的最後一段。
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

// 常用查法掛在各欄位底下：欄位沒有配對時，底下的常用查法跟著停用，原因是分頁最後那一段，寫出每個沒有配對的欄位。
// 這裡核對傳票核准日底下五項與編製分錄較少的人員停用、都連到分頁最後那一段；人工/自動分錄是類別組的欄位，核對它的「加入」。
// 範例在右側托盤，需要未配對欄位的範例也停用並寫出原因。
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
  // 第四步照舊版不含編製分錄較少的人員與使用較少的科目，預篩選分頁沒有這兩條。
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
  // 子條件組類型選單的「人工/自動分錄」和第三步的欄位同名。
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

// 範例的前置條件由條件內容查和後端同一張需求表。沒有科目分類時兩個現金範例停用，原因和固定資產與費用那張卡同一句；
// 不停用的話，套用後要到預覽才被後端擋下。
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
