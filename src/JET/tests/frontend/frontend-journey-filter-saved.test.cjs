'use strict';
require('./support/resource-guard.cjs');
// 第五步預覽、儲存與已存情境的情境測試。
//
// 情境 12「第五步預覽與儲存新情境」：審計員點「空白摘要」卡，不填名稱與動機，預覽後儲存；拿掉這張卡改用欄位條件；
// 預覽回來前又改了條件；後端回帶 details 的錯誤。
// 情境 13「第五步已存情境」：展開已存情境與高風險條件彙總（成功、失敗後重試、換案件後才回來）；其他作業進行中展開與儲存；
// 沒有可用版本時看彙總、報告與命中明細；編輯一個情境改名後儲存；移除一個情境（取消、換案件、再確認）；
// 按「重新檢查並儲存情境」；產生條件篩選報告；後端只讓篩選命中失效、情境保留。
// 情境 14「開案時有待修正的舊情境」：開案回應帶「規則升級已重算」或待修正清單，看第五、六步，一個一個修改。
// 情境 15 的編輯部分「舊情境裡的舊式條件」：編輯 typed 條件與文字值清單，新增條件時找不到舊式型別。讀回部分在 frontend-journey-readback.test.cjs。
//
// 每個測試都執行正式的 state.js、ui-core.js 與第一到第六步的步驟腳本（共用夾具 usageJourneyFixture），
// 在假 DOM 上畫出第五步或第六步，用點擊、選單與輸入操作；合成資料只控制邊界，後端回應都是寫死的合成值。
// 儲存、另存、移除與重新檢查只送 filter.scenario.save、filter.scenario.remove、filter.scenario.recheck，
// 一次只帶正在處理的那一個情境。
//
// 斷言規則：已定案的文字逐字比對，其他文字只比對意思。
// 預期值寫死，不由被測程式算出；送出的 payload 精確比對，而且不得帶 `__` 開頭的鍵。
// 第五步的就地檢查、讀回與已存情境畫面的句子，都是已定案的文字，逐字比對。
const test = require('node:test');
const assert = require('node:assert/strict');
const F = require('./support/frontend-fixtures.cjs');
const { unchecked } = require('./support/action-contract.cjs');
const { plain, settle } = F;

const REF = { revision: 'r1', populationScope: 'auditPeriod' };
const VALIDATED = { resultRef: { resultId: 'v1' }, completenessTest: { eligibility: { isEligible: true } } };

// 合成的已存情境。savedBlankDescription 是共用夾具裡空白摘要卡存成的情境（有第 2 版 editorOrigins）。
// 本檔用卡片代號點卡片。第五步條件總覽的「欄位條件」分頁，程式裡的識別是 field。
// 常用查法掛在各欄位底下；情境範例放在右側條件組合上方的托盤；非營業日之分錄那張卡直接加入一條條件。
const authored = name => ({ name, rationale: '合成自擬動機', groups: [{ join: 'AND', matchScope: 'row',
  conditions: [{ type: 'fieldValue', join: 'AND', field: 'description', operator: 'contains', values: ['合成'] }] }] });
const savedBlankNamed = name => ({ ...F.savedBlankDescription(), name });
// 對應不回卡片的已存情境：第 2 版編輯來源，卡片記 null；名稱與動機是審計員手寫的，條件是空白摘要的預篩選條件。
const noCardScenario = name => ({ name, rationale: '合成舊版動機', source: null, groups: [{ join: 'AND', matchScope: 'row',
  conditions: [{ type: 'prescreen', join: 'AND', prescreenKey: 'blankDescription' }] }],
  editorOrigins: { version: 2, nameIsAutomatic: false, rationaleIsAutomatic: false, groups: [{ cards: [null] }] } });

// 本檔的夾具：共用的 usageJourneyFixture 載入第一到第六步的正式腳本、訂閱重繪，JetApi 記下每個請求。
// 這裡另外把 JetFocus 改成立即聚焦（情境 13 的移除流程要看焦點），補上 getComputedStyle，
// 並依選項換掉夾具預設的已存情境、版本、驗證結果與待修正清單。只服務這個檔，所以留在這裡。
function savedFixture(options = {}) {
  const { saved = [], resultRef = saved.length ? REF : null, check = null, stale = null, reports = [], rdeFields = null,
    validated = true, step = 'filter', pane = null } = options;
  const f = F.usageJourneyFixture(step, { redraw: true });
  const { window, store, dom } = f;
  window.getComputedStyle = () => ({ gridTemplateColumns: 'none' });
  window.JetFocus = {
    defer(target) { const node = target(); if (node && node.focus) node.focus(); },
    deferFirst(selectors) { for (const selector of selectors) { const node = dom.document.querySelector(selector); if (node) { node.focus(); return; } } }
  };
  store.setResult('validate', validated ? VALIDATED : null);
  store.setAccountMappingState({ hasAnyCategory: true, hasRevenue: true, hasCounterpart: true });
  if (rdeFields) {
    store.setMappingCommitted('gl', { mapping: { voucherNumber: 'Doc', glDate: 'Date', accountNumber: 'Code', description: 'Memo', amount: 'Amount' },
      mode: 'signed', options: { rdeFields } });
  }
  store.setSavedScenarios(saved);
  store.setFilterResultRef(resultRef);
  store.setFilterScenarioCheck(check);
  if (stale) store.setStaleState(stale);
  store.setReports(reports);
  store.setFilterDraft(null);
  if (pane) f.click('[data-filter-pane-select="' + pane + '"]');
  const $$ = selector => dom.content.querySelectorAll(selector);
  // 不檢查按鈕是否停用就觸發 click：用來確認「硬按」停用的按鈕不會送出請求。
  function press(selector) { const node = f.$(selector); assert.ok(node, 'missing ' + selector); node.fire('click'); }
  function type(selector, value) { const node = f.$(selector); assert.ok(node, 'missing ' + selector); node.value = value; node.fire('input'); }
  const sent = action => f.requests.filter(request => request.action === action);
  const scenarioWrites = () => f.requests.filter(request => /^filterScenario|^filterCommit$/.test(request.action));
  const messages = () => plain(store.getState().messages.map(message => ({ text: message.text, level: message.level })));
  const notice = () => { const node = f.$('[data-bind="scenario-notice"]'); return node && !node.hidden ? node.textContent : ''; };
  const draft = () => store.getState().filter.draft;
  const text = () => dom.content.textContent;
  return { ...f, $$, press, type, sent, scenarioWrites, messages, notice, draft, text };
}

// 送出的 payload 不得帶任何 `__` 開頭的鍵（畫面專用標記不屬於前後端契約，預覽、查詢與儲存都不送）。
function assertNoScreenKeys(payload, label) {
  assert.ok(!JSON.stringify(payload).includes('"__'), label + ' carries no screen-only key');
}

// 後端成功儲存一個情境的合成回應：清單照後端形狀整批帶回，position 是這個情境的位置（從 1 起算）。
function saveResponse(scenarios, position, extra = {}) {
  return { ok: true, position, savedCount: scenarios.length, scenarios, resultRef: { revision: 'r2', populationScope: 'auditPeriod' },
    filterScenarioCheck: { status: 'current', recalculatedCount: scenarios.length, problems: [] },
    staleState: { validation: false, prescreen: false, filter: false }, ...extra };
}

// 一次按下就存好，名稱與動機常駐，成功後開始下一份空白草稿。
test('metadata stays visible and one save action completes a new scenario', async () => {
  const f = savedFixture();
  f.click('[data-common-card="blankDescription"]');
  const name = f.$('[data-bind="scenario-name"]');
  assert.ok(name && !name.closest('[hidden]'), 'name is visible without opening another form');
  assert.equal(f.$('[data-action="open-save"]'), null);
  assert.equal(f.$('[data-action="close-save"]'), null);
  assert.equal(f.$('[data-action="save-scenario"]').textContent, '儲存情境');
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 1);
});

for (const operation of ['new', 'edit', 'copy']) test('successful ' + operation + ' clears only the saved draft and returns focus to the builder', async () => {
  const initial = operation === 'new' ? [] : [savedBlankNamed('原情境')];
  const f = savedFixture({ saved: initial });
  if (operation === 'new') f.click('[data-common-card="blankDescription"]');
  else f.click('[data-action="edit-scenario"][data-index="0"]');
  f.type('[data-bind="scenario-name"]', '新情境');
  f.store.setFilterPreview({ count: 0, voucherCount: 0, summaryRowCount: 0, previewRows: [], populationScope: 'auditPeriod' });
  f.click(operation === 'copy' ? '[data-action="save-scenario-copy"]' : '[data-action="save-scenario"]');
  await settle();
  const savedName = operation === 'copy' ? '新情境（副本）' : '新情境';
  const saved = operation === 'copy' ? initial.concat(savedBlankNamed(savedName)) : [savedBlankNamed(savedName)];
  f.request('filterScenarioSave').resolve(saveResponse(saved, saved.length));
  await settle(); await settle();
  assert.equal(f.draft().__editingIndex, undefined);
  assert.equal(f.draft().groups.flatMap(group => group.conditions).length, 0);
  assert.equal(f.draft().name, '');
  assert.equal(f.draft().rationale, '');
  assert.equal(f.store.getState().filter.preview, null);
  assert.equal(f.$('[data-action="new-scenario"]'), null);
  assert.equal(f.$('[data-bind="save-feedback"]').getAttribute('role'), 'status');
  // 頁籤只寫「已儲存情境」，提示指向這個頁籤。
  assert.equal(f.$('[data-bind="save-feedback"]').textContent, '已儲存「' + savedName + '」。可在「已儲存情境」查看或修改。');
  assert.ok(f.dom.document.activeElement === f.$('[data-builder-title]'));
  assert.deepEqual(plain(f.store.getState().filter.savedScenarios.map(item => item.name)), saved.map(item => item.name));
});

// 「從情境範例開始」托盤在同一次開案內記住開合；條件列的「查找說明」跟著草稿收起。
// 儲存、取消編輯與移除編輯中的情境時，托盤維持開著，查找說明收起。
// 查找說明的鍵以 rule-help- 開頭，這裡直接放一個合成的鍵代表。
test('the example tray stays open for the whole case opening while condition help folds with each draft', async () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲'), savedBlankNamed('乙')] });
  const view = () => f.window.JetUi.FilterStep.viewState;
  const tray = () => f.$('details.filter-template-tray');
  assert.equal(tray().hasAttribute('open'), false, 'the tray starts folded');
  tray().open = true; tray().fire('toggle');

  view().disclosures['rule-help-synthetic'] = true;
  f.click('[data-common-card="blankDescription"]');
  f.type('[data-bind="scenario-name"]', '新情境');
  f.store.setFilterPreview({ count: 0, voucherCount: 0, summaryRowCount: 0, previewRows: [], populationScope: 'auditPeriod' });
  f.click('[data-action="save-scenario"]'); await settle();
  f.request('filterScenarioSave').resolve(saveResponse([savedBlankNamed('甲'), savedBlankNamed('乙'), savedBlankNamed('新情境')], 3));
  await settle(); await settle();
  assert.equal(f.draft().groups.flatMap(group => group.conditions).length, 0, 'the saved draft is cleared');
  assert.equal(tray().hasAttribute('open'), true, 'saving keeps the tray open');
  assert.deepEqual(plain(view().disclosures), { templates: true }, 'saving folds the help of the saved draft');

  view().disclosures['rule-help-synthetic'] = true;
  f.click('[data-action="edit-scenario"][data-index="0"]');
  f.click('[data-action="cancel-edit-scenario"]');
  assert.equal(tray().hasAttribute('open'), true, 'cancelling an edit keeps the tray open');
  assert.deepEqual(plain(view().disclosures), { templates: true }, 'cancelling an edit folds the help');

  view().disclosures['rule-help-synthetic'] = true;
  f.click('[data-action="edit-scenario"][data-index="0"]');
  f.click('[data-action="remove-scenario"][data-index="0"]');
  f.click('[data-action="confirm-remove-scenario"][data-index="0"]'); await settle();
  f.request('filterScenarioRemove').resolve({ ok: true, position: null, savedCount: 2, scenarios: [savedBlankNamed('乙'), savedBlankNamed('新情境')],
    resultRef: { revision: 'r3', populationScope: 'auditPeriod' }, filterScenarioCheck: { status: 'current', recalculatedCount: 2, problems: [] },
    staleState: { validation: false, prescreen: false, filter: false } });
  await settle(); await settle();
  assert.equal(f.draft().__editingIndex, undefined, 'removing the edited scenario clears the draft');
  assert.equal(tray().hasAttribute('open'), true, 'removing the edited scenario keeps the tray open');
  assert.deepEqual(plain(view().disclosures), { templates: true }, 'removing the edited scenario folds the help');
});

// 上游修改清掉已存情境後，第五步不顯示剛才「已儲存」的提示。
test('the saved notice disappears when an upstream change clears the saved scenarios', async () => {
  const f = savedFixture();
  f.click('[data-common-card="blankDescription"]');
  f.type('[data-bind="scenario-name"]', '新情境');
  f.store.setFilterPreview({ count: 0, voucherCount: 0, summaryRowCount: 0, previewRows: [], populationScope: 'auditPeriod' });
  f.click('[data-action="save-scenario"]'); await settle();
  f.request('filterScenarioSave').resolve(saveResponse([savedBlankNamed('新情境')], 1));
  await settle(); await settle();
  // 頁籤寫「已儲存情境」。
  assert.equal(f.$('[data-bind="save-feedback"]').textContent, '已儲存「新情境」。可在「已儲存情境」查看或修改。');
  f.store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true } });
  await settle();
  assert.equal(f.store.getState().filter.savedScenarios.length, 0);
  const feedback = f.$('[data-bind="save-feedback"]');
  assert.ok(!feedback || feedback.hidden, 'no saved notice once the scenarios are gone: ' + (feedback && feedback.textContent));
});

// 後端存檔時只保留每種條件用得到的欄位，前端送出的格式不變。
// 人工分錄條件以 newFilterCondition 為底，送出時帶著季末前天數、科目配對方式等用不到的欄位，回應裡的條件只剩人工或自動。
// 儲存途中審計員加了一條條件又移除，內容和送出時相同，提示仍要寫「已儲存」。
test('a save answered with only the fields each condition uses still counts an unchanged draft as saved', async () => {
  const f = savedFixture();
  // 人工分錄條件從欄位條件分頁類別組的「人工/自動分錄」欄位加入。
  f.click('[data-condition-source="field"]');
  f.click('[data-add-field="manualAuto"]');
  f.type('[data-bind="scenario-name"]', '合成人工分錄');
  f.type('[data-bind="scenario-rationale"]', '合成動機');
  f.store.setFilterPreview({ count: 0, voucherCount: 0, summaryRowCount: 0, previewRows: [], populationScope: 'auditPeriod' });
  f.click('[data-action="save-scenario"]'); await settle();
  const sent = plain(f.request('filterScenarioSave').payload.scenario);
  // 前提：送出的人工分錄條件確實帶著用不到的欄位，這裡看季末前天數 windowDays。
  assert.equal(sent.groups[0].conditions[0].type, 'manualAuto');
  assert.ok(Object.hasOwn(sent.groups[0].conditions[0], 'windowDays'));
  f.click('[data-add-field="voucherNumber"]');
  assert.equal(f.draft().groups[0].conditions.length, 2);
  f.click('.rule-row[data-ri="1"] [data-action="remove-rule"]');
  assert.equal(f.draft().groups[0].conditions.length, 1);
  const stored = { source: null, name: '合成人工分錄', rationale: '合成動機',
    groups: [{ join: sent.groups[0].join, matchScope: sent.groups[0].matchScope, conditions: [{ join: 'AND', type: 'manualAuto', isManual: 'true' }] }],
    populationScope: 'auditPeriod', savedUtc: '2026-01-01T00:00:00Z' };
  if (sent.editorOrigins) stored.editorOrigins = sent.editorOrigins;
  f.request('filterScenarioSave').resolve(saveResponse([stored], 1));
  await settle(); await settle();
  assert.equal(f.draft().__editingIndex, 0, 'the draft changed during the request, so it stays open on the saved scenario');
  assert.equal(f.$('[data-bind="save-feedback"]').textContent, '已儲存「合成人工分錄」。');
});

// 全站按鈕列的規則是取消緊鄰主要按鈕左邊，所以編輯時依序是另存為新情境、取消編輯、儲存變更。
test('editing places save a new scenario, cancel and save changes at the bottom', () => {
  const f = savedFixture({ saved: [authored('原情境')] });
  f.click('[data-action="edit-scenario"][data-index="0"]');
  assert.equal(f.$('[data-bind="editing-scenario-name"]').textContent, '編輯情境：原情境');
  const actions = f.$('.filter-primary-actions');
  assert.deepEqual(actions.querySelectorAll('button').map(button => button.textContent), ['查看符合的傳票', '另存為新情境', '取消編輯', '儲存變更']);
  assert.equal(f.$('.filter-builder-heading').querySelectorAll('button').length, 0);
});

// ================================================================================================
// 第一部分　情境 12：預覽與儲存新情境
// ================================================================================================

// 草稿有常用篩選條件的卡片才帶 source。
// 名稱與動機留白時，JET 會依條件自動補上建議文字（refreshAutomaticMetadata），所以審計員清空欄位後按儲存，送出的是建議名稱。
// 不論有沒有卡片，名稱與動機都能留白，由後端依條件補上：兩欄都沒有標示，留白照樣送出；條件缺值時仍擋下，提示不列名稱與動機。
// 建議文字也是空白的草稿，畫面操作做不出來，所以這一段直接把草稿的名稱與動機設成手寫的空白（__nameDirty），確認照樣送出。
test('name and rationale may be left blank with or without a common card; only a condition missing its value stops the save', async () => {
  const f = savedFixture();
  f.click('[data-common-card="blankDescription"]');

  const labels = () => f.$$('[data-save-panel] .form__label');
  // 名稱與動機都不標選填或必填。
  for (const label of labels()) {
    assert.doesNotMatch(label.textContent, /可選填/);
    assert.ok(label.querySelector('.form__req') === null, 'no required mark on name or rationale');
  }
  // 審計員清空兩欄後按儲存：照樣送出，名稱與動機是空白摘要卡的建議文字。
  f.type('[data-bind="scenario-name"]', '');
  f.type('[data-bind="scenario-rationale"]', '');
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 1);
  // 空白摘要卡用欄位條件，只送欄位空白判斷。
  assert.deepEqual(plain(f.request('filterScenarioSave').payload), {
    populationScope: 'auditPeriod', position: null,
    scenario: { name: '空白摘要', rationale: '空白摘要',
      groups: [{ join: 'OR', matchScope: 'row', conditions: [{ join: 'AND', type: 'fieldValue', field: 'description', operator: 'isBlank', includeBlank: false }] }],
      editorOrigins: { version: 2, nameIsAutomatic: true, rationaleIsAutomatic: true, groups: [{ cards: ['blankDescription'] }] },
      source: 'common' } });
  assertNoScreenKeys(f.request('filterScenarioSave').payload, 'the common card save');
  f.request('filterScenarioSave').reject(new Error('合成儲存失敗。')); await settle(); await settle();

  // 手寫成空白（畫面做不出、只在建議也空白時發生）：有卡片仍送出，名稱與動機是空字串，由後端依條件補上文字。
  f.store.patchFilterDraftMeta({ name: '', rationale: '', __nameDirty: true, __rationaleDirty: true });
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 2);
  assert.equal(f.request('filterScenarioSave').payload.scenario.name, '');
  assert.equal(f.request('filterScenarioSave').payload.scenario.rationale, '');
  assert.equal(f.request('filterScenarioSave').payload.scenario.source, 'common');
  f.request('filterScenarioSave').reject(new Error('合成儲存失敗。')); await settle(); await settle();

  // 拿掉最後一張卡，改用欄位條件：兩欄仍沒有標示，送出的草稿不再帶 source。
  f.click('[data-common-card="blankDescription"]');
  f.click('[data-condition-source="field"]');
  f.click('[data-add-field="description"]');

  for (const label of labels()) {
    assert.doesNotMatch(label.textContent, /可選填/);
    assert.ok(label.querySelector('.form__req') === null, 'still no required mark without a common card');
  }
  // 欄位條件還沒填值：按儲存不送出，提示說出是哪一條、缺什麼。
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 2, 'nothing is sent while a condition still lacks its value');
  // 位置字首與「尚需補齊：」是已定案的句子，逐字比對。
  // 「包含」是一行一個值的清單，沒填值的原因是已定案的「請填 1 到 100 個值」；名稱與動機沒有列出。
  assert.equal(f.notice(), '尚需補齊：第 1 組第 1 條「傳票摘要」請填 1 到 100 個值');
  // 填好值之後，名稱與動機手寫成空白：照樣送出空字串，由後端依條件補上；兩欄都不標錯，也沒有欄位旁的提示。
  f.type('[data-value-key="values"]', '合成');
  f.store.patchFilterDraftMeta({ name: '', rationale: '', __nameDirty: true, __rationaleDirty: true });
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 3, 'a blank name and rationale are sent for the backend to fill in');
  assert.equal(f.request('filterScenarioSave').payload.scenario.name, '');
  assert.equal(f.request('filterScenarioSave').payload.scenario.rationale, '');
  assert.equal(f.notice(), '');
  const nameInput = f.$('[data-bind="scenario-name"]'), rationaleInput = f.$('[data-bind="scenario-rationale"]');
  assert.notEqual(nameInput.getAttribute('aria-invalid'), 'true');
  assert.notEqual(rationaleInput.getAttribute('aria-invalid'), 'true');
  const fieldNote = input => { const note = input.parentNode === null ? null : input.parentNode.querySelector('.field-error'); return note === null ? null : note.textContent; };
  assert.equal(fieldNote(nameInput), null);
  assert.equal(fieldNote(rationaleInput), null);
  f.request('filterScenarioSave').reject(new Error('合成儲存失敗。')); await settle(); await settle();
  // 手寫名稱與動機後按儲存送出，而且沒有 source。
  f.type('[data-bind="scenario-rationale"]', '合成動機');
  f.type('[data-bind="scenario-name"]', '合成情境');
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 4);
  const authoredSave = f.request('filterScenarioSave').payload;
  assert.equal(authoredSave.scenario.name, '合成情境');
  assert.equal(authoredSave.scenario.rationale, '合成動機');
  assert.equal(Object.hasOwn(authoredSave.scenario, 'source'), false, 'a self-authored draft has no source key');
  assertNoScreenKeys(authoredSave, 'the self-authored save');
});

// 每個預覽與儲存請求都帶查核期間、和草稿脫鉤的條件副本，沒有畫面專用的鍵。
// 「同一傳票」的比對併在整張傳票條件裡，這裡加一條獨立的整張傳票條件，確認它照樣送出。
// 含卡片的草稿帶 source，值是 'common'。
test('preview and save send the audit period and detached explicit voucher conditions', async () => {
  const f = savedFixture();
  f.click('[data-common-card="blankDescription"]');
  f.click('[data-condition-source="field"]');
  f.click('[data-add-compound="voucher"]');
  f.change('[data-child-kind]', 'field:description');
  f.click('[data-child-add]');
  f.type('[data-value-key="values"]', 'Alpha\nBeta');
  assert.equal(f.draft().groups[0].matchScope, 'row');
  const draftValues = f.draft().groups[0].conditions[1].conditions[0].values;
  assert.deepEqual(plain(draftValues), ['Alpha', 'Beta']);

  f.click('[data-action="preview-scenario"]'); await settle();
  const preview = f.request('filterPreview');
  
  // 預覽回來後再存。
  preview.resolve({ scenario: { count: 2, voucherCount: 1, summaryRowCount: 2, populationScope: 'auditPeriod' } }); await settle();
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [], nextCursor: null }); await settle(); await settle();
  f.click('[data-action="save-scenario"]'); await settle();
  const save = f.request('filterScenarioSave');
  for (const [label, payload] of [['preview', preview.payload], ['save', save.payload]]) {
    assert.equal(payload.populationScope, 'auditPeriod', label);
    const group = payload.scenario.groups[0];
    assert.equal(group.matchScope, 'row', label + ' uses ordinary groups');
    // 空白摘要卡用欄位條件。
    assert.deepEqual(plain(group.conditions.map(rule => rule.type)), ['fieldValue', 'voucher'], label);
    assert.equal(group.conditions[1].quantifier, 'any');
    assert.deepEqual(plain(group.conditions[1].conditions[0].values), ['Alpha', 'Beta'], label);
    assert.ok(group.conditions[1].conditions[0].values !== draftValues, label + ' sends a copy, not the draft array');
    // 來源代碼是 common。
    assert.equal(payload.scenario.source, 'common', label + ' a draft with a common card is sent with the common source');
    assertNoScreenKeys(payload, label);
  }
  assert.deepEqual(Object.keys(preview.payload).sort(), ['populationScope', 'scenario']);
  assert.deepEqual(Object.keys(save.payload).sort(), ['populationScope', 'position', 'scenario']);
  assert.equal(save.payload.position, null, 'a new scenario has no position');
  // 送出的投影沒有改動草稿：標記仍在草稿上。
  assert.equal(f.draft().groups[0].conditions[0].__commonCard, 'blankDescription');
});

// 預覽走 Ui.run，同一時間只會有一個草稿預覽在途；draftResponseGuard.issue 傳入的判斷式比對草稿版本、案件與資料版本。
// 拿掉判斷式裡的草稿版本比對時，這則測試會失敗。
test('a late preview never replaces "conditions changed", and a preview for another population or case is ignored', async () => {
  const f = savedFixture();
  f.click('[data-common-card="blankDescription"]');
  f.click('[data-action="preview-scenario"]'); await settle();
  f.request('filterPreview').resolve({ scenario: { count: 2, voucherCount: 1, summaryRowCount: 2, populationScope: 'auditPeriod' } }); await settle();
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [], nextCursor: null }); await settle(); await settle();
  const pane = () => f.$('[data-bind="preview-pane-body"]').textContent;
  // 預覽分開顯示傳票數量與分錄筆數。
  assert.match(pane(), /1符合條件的傳票數量2符合條件的分錄筆數/);

  // 再按一次預覽，回來之前改了條件：畫面說條件已變更，晚到的舊數字不採用，也不再讀傳票清單。
  f.click('[data-action="preview-scenario"]'); await settle();
  const late = f.request('filterPreview');
  f.click('[data-common-card="specificTrailingDigits"]');
  assert.match(pane(), /條件已變更/);
  late.resolve({ scenario: { count: 9, voucherCount: 9, summaryRowCount: 9, populationScope: 'auditPeriod' } }); await settle(); await settle();
  assert.equal(f.sent('queryFilterVoucherPage').length, 1, 'no voucher list is read for a late preview');
  assert.match(pane(), /條件已變更/);
  // 照統計列的寫法比對，數字緊接著標籤。
  assert.doesNotMatch(pane(), /9符合條件的傳票數量|9符合條件的分錄筆數/);
  assert.ok(f.store.getState().filter.preview === null, 'the late preview is not stored');

  // 回應的範圍和請求不同：不採用。
  f.click('[data-action="preview-scenario"]'); await settle();
  f.request('filterPreview').resolve({ scenario: { count: 7, voucherCount: 7, summaryRowCount: 7, populationScope: 'allProjected' } }); await settle();
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q2', rows: [], nextCursor: null }); await settle(); await settle();
  assert.ok(f.store.getState().filter.preview === null, 'a preview for another population is not stored');
  assert.doesNotMatch(pane(), /7符合條件的傳票數量|7符合條件的分錄筆數/);

  // 預覽在途時換開另一個案件：回應不採用，也不再讀傳票清單。
  f.click('[data-action="preview-scenario"]'); await settle();
  const before = f.sent('queryFilterVoucherPage').length;
  f.store.setProject({ projectId: 'another-synthetic-case' });
  f.request('filterPreview').resolve({ scenario: { count: 5, voucherCount: 5, summaryRowCount: 5, populationScope: 'auditPeriod' } }); await settle(); await settle();
  assert.equal(f.sent('queryFilterVoucherPage').length, before);
  assert.ok(f.store.getState().filter.preview === null);
});

// 錯誤細節的形狀：details 帶 group、rule、message，以及 scenarioPosition、scenarioName。
// jet-api.js 把 details 帶到 Error 上這件事，由 frontend-journey-transport.test.cjs 負責。
test('a rejected save marks only the current draft and names another scenario that caused it', async () => {
  const f = savedFixture({ saved: [savedBlankNamed('情境甲'), savedBlankNamed('情境乙'), savedBlankNamed('情境丙')], pane: 'saved' });
  f.click('[data-action="edit-scenario"][data-index="0"]');
  
  const invalid = (message, position, name) => Object.assign(new Error('第 1 組第 1 條：' + message), { code: 'invalid_scenario',
    details: [{ group: 1, rule: 1, message, scenarioPosition: position, scenarioName: name }] });
  // 錯誤屬於別的情境：不標在目前草稿，儲存鈕上方寫出是哪一個情境。
  f.click('[data-action="save-scenario"]'); await settle();
  f.request('filterScenarioSave').reject(invalid('合成原因', 2, '情境乙')); await settle(); await settle();
  assert.equal(f.$$('.rule-row--invalid').length, 0);
  // 用字照已定案的句式「儲存失敗：情境 〈N〉「〈名稱〉」：〈原因〉」；原因是後端的訊息。
  assert.equal(f.notice(), '儲存失敗：情境 2「情境乙」：第 1 組第 1 條：合成原因');
  // 錯誤屬於正在儲存的這一個：標在條件列，說明寫在那一列下面，儲存鈕上方不另寫。
  f.click('[data-action="save-scenario"]'); await settle();
  f.request('filterScenarioSave').reject(invalid('合成原因', 1, '情境甲')); await settle(); await settle();
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row.classList.contains('rule-row--invalid'));
  assert.equal(row.querySelector('[data-rule-error]').textContent, '合成原因');
  assert.equal(f.notice(), '');
  // 錯誤回來之前已改了條件：不標在新的條件上，也不寫在儲存鈕上方。
  f.click('[data-action="save-scenario"]'); await settle();
  const pending = f.request('filterScenarioSave');
  f.click('[data-common-card="specificTrailingDigits"]');
  pending.reject(invalid('另一個原因', 1, '情境甲')); await settle(); await settle();
  assert.equal(f.$$('.rule-row--invalid').length, 0);
  assert.equal(f.notice(), '');
  assert.ok(!f.text().includes('另一個原因'));
});

// 審計員在這裡的目的是設定篩選情境，不是查看符合條件的傳票，所以儲存是主要按鈕，「查看符合的傳票」是次要按鈕。
// vm 沒有版面，只能看 class。
test('saving is the primary action and looking at matching vouchers stays available as the secondary one', async () => {
  const f = savedFixture();
  f.click('[data-common-card="blankDescription"]');
  const classes = selector => f.$(selector).className.split(/\s+/);
  assert.equal(f.$('[data-save-panel]').hidden, false);
  assert.equal(f.$('.filter-primary-actions').hidden, false);
  assert.ok(classes('[data-action="preview-scenario"]').includes('btn--ghost'), 'preview is a secondary button');
  
  assert.ok(classes('[data-action="save-scenario"]').includes('btn'));
  assert.ok(!classes('[data-action="save-scenario"]').includes('btn--ghost'), 'the save button in the panel is primary');
  // 次要按鈕仍可用：按了照樣送出預覽。
  
  f.click('[data-action="preview-scenario"]'); await settle();
  assert.equal(f.sent('filterPreview').length, 1);
});

// ================================================================================================
// 第二部分　情境 13：已存情境的展開、高風險條件彙總、編輯、移除、重新檢查、報告與上游失效
// ================================================================================================

// 每次回應都是新物件：正式程式會把傳票清單與畫面狀態掛在回應物件上。
const scenarioPreview = () => ({ scenario: { count: 3, voucherCount: 2, summaryRowCount: 3, populationScope: 'auditPeriod' } });
const voucherPage = (voucherNumber, queryRevision = 'q1') => ({ queryRevision, nextCursor: null,
  rows: [{ voucherNumber, glDate: '2025-01-01', hitRowCount: 1, totalRowCount: 2, voucherTotal: 10 }] });
// 儲存情境時已算好結果。有可用版本時，展開已存情境只讀一次傳票頁，
// 第一頁另帶符合的傳票數與分錄筆數，不先即時預覽；沒有可用版本時才即時預覽。
const savedFirstPage = (voucherNumber, counts = { voucherCount: 2, summaryRowCount: 3 }) => ({ ...voucherPage(voucherNumber),
  ...counts, countsWholeVoucherRows: false });
const HIGH_RISK_SUMMARY = { scenarios: [{ position: 1, name: '甲', voucherHitCount: 2, rowHitCount: 3, summaryRowCount: 3 }, { position: 2, name: '乙', voucherHitCount: 0, rowHitCount: 0, summaryRowCount: 0 }] };

// 「載入失敗」「重試讀取結果」「請收合後再展開以重試」不是已定案的文字，只比對意思。
test('a saved scenario and the high-risk summary fail in place and can be retried; the failure also reaches the message panel once', async () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲'), savedBlankNamed('乙')], pane: 'saved' });
  const body = () => f.$('[data-bind="scenario-preview-0"]');
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  // 有可用版本時只讀存好的結果，送的是傳票頁，不是即時預覽。
  assert.equal(f.sent('filterPreview').length, 0);
  assert.equal(f.sent('queryFilterVoucherPage').length, 1);
  assert.match(body().textContent, /載入中/);
  const before = f.messages().length;
  f.request('queryFilterVoucherPage').reject(new Error('合成讀取失敗')); await settle(); await settle();
  assert.match(body().textContent, /載入失敗/);
  assert.match(body().textContent, /合成讀取失敗/);
  const added = f.messages().slice(0, f.messages().length - before);
  assert.equal(added.length, 1, 'the current failure is reported once in the message panel');
  assert.equal(added[0].level, 'error');
  assert.match(added[0].text, /合成讀取失敗/);
  const retry = body().querySelector('[data-scenario-retry]');
  assert.ok(retry !== null, 'a retry button is offered in place');
  retry.fire('click'); await settle();
  assert.equal(f.sent('queryFilterVoucherPage').length, 2, 'retrying asks again');
  // 有可用版本時，傳票清單用已存情境的位置與版本讀，不送條件內容。
  assert.deepEqual(plain(f.request('queryFilterVoucherPage').payload),
    { scenarioPosition: 1, scenarioRevision: 'r1', populationScope: 'auditPeriod', pageSize: 200 });
  f.request('queryFilterVoucherPage').resolve(savedFirstPage('JV-1')); await settle(); await settle();
  // 分開顯示傳票數量與分錄筆數，兩個數字由傳票頁第一頁帶回。
  assert.match(body().textContent, /2符合條件的傳票數量3符合條件的分錄筆數/);
  assert.match(body().textContent, /JV-1/);
  assert.equal(f.sent('filterPreview').length, 0, 'a saved result is not computed again');

  // 高風險條件彙總讀取失敗：就地說明，收合後再展開會重新讀取。
  f.click('[data-action="toggle-high-risk-summary"]'); await settle();
  assert.equal(f.sent('queryHighRiskSummaryScenarios').length, 1);
  f.request('queryHighRiskSummaryScenarios').reject(new Error('合成彙總失敗')); await settle(); await settle();
  assert.match(f.$('[data-bind="high-risk-summary-scenarios"]').textContent, /載入失敗/);
  f.click('[data-action="toggle-high-risk-summary"]');
  f.click('[data-action="toggle-high-risk-summary"]'); await settle();
  assert.equal(f.sent('queryHighRiskSummaryScenarios').length, 2, 'collapsing and expanding retries the high-risk summary');
  f.request('queryHighRiskSummaryScenarios').resolve(HIGH_RISK_SUMMARY); await settle(); await settle();
  assert.match(f.$('[data-bind="high-risk-summary-scenarios"]').textContent, /甲/);
});

test('answers that arrive after the case, the saved list or the version changed are dropped without any message', async () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲'), savedBlankNamed('乙')], pane: 'saved' });
  // 展開第一個情境與高風險條件彙總，兩個請求都在途時換開另一個案件。
  // 有可用版本的情境展開時只送傳票頁，所以本則在途的請求看的是傳票頁。
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  f.click('[data-action="toggle-high-risk-summary"]'); await settle();
  const oldPreview = f.request('queryFilterVoucherPage'), oldHighRiskSummary = f.request('queryHighRiskSummaryScenarios');
  const before = f.messages().length;
  f.store.setProject({ projectId: 'another-synthetic-case' });
  oldPreview.reject(new Error('舊案件的讀取失敗')); await settle(); await settle();
  oldHighRiskSummary.resolve({ scenarios: [{ position: 1, name: '舊案件的彙總', voucherHitCount: 9, rowHitCount: 9, summaryRowCount: 9 }] }); await settle(); await settle();
  assert.equal(f.messages().length, before, 'a failure from the previous case leaves no message');
  assert.ok(!f.text().includes('舊案件的讀取失敗'));
  // 新案件裡展開：重新讀取，不沿用舊案件晚到的結果。
  f.click('[data-filter-pane-select="saved"]');
  f.click('[data-action="toggle-high-risk-summary"]'); await settle();
  assert.equal(f.sent('queryHighRiskSummaryScenarios').length, 2, 'the new case asks for its own high-risk summary');
  assert.ok(!f.text().includes('舊案件的彙總'));
  f.request('queryHighRiskSummaryScenarios').resolve(HIGH_RISK_SUMMARY); await settle(); await settle();
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  assert.equal(f.sent('queryFilterVoucherPage').length, 2, 'the new case asks for its own scenario result');

  // 回應前已存清單被換掉（例如另一個情境剛存好）：晚到的第一頁不顯示，畫面不出現舊數字。
  const pendingForOldList = f.request('queryFilterVoucherPage');
  f.store.setSavedScenarios([savedBlankNamed('甲'), savedBlankNamed('乙'), savedBlankNamed('丙')]);
  pendingForOldList.resolve(savedFirstPage('OLD-LIST-1', { voucherCount: 8, summaryRowCount: 8 })); await settle(); await settle();
  // 同上，照統計列的寫法比對。
  assert.doesNotMatch(f.text(), /8符合條件的傳票數量|8符合條件的分錄筆數/);
  assert.ok(!f.text().includes('OLD-LIST-1'));

  // 回應前已存情境的版本換了：同樣不採用，改用新版本重新讀取。
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  const pendingForOldVersion = f.request('queryFilterVoucherPage');
  assert.equal(pendingForOldVersion.payload.scenarioRevision, 'r1');
  f.store.setFilterResultRef({ revision: 'r2', populationScope: 'auditPeriod' }); await settle();
  pendingForOldVersion.resolve(savedFirstPage('OLD-VERSION-1', { voucherCount: 6, summaryRowCount: 6 })); await settle(); await settle();
  assert.doesNotMatch(f.text(), /6符合條件的傳票數量|6符合條件的分錄筆數/);
  assert.ok(!f.text().includes('OLD-VERSION-1'));
  assert.equal(f.request('queryFilterVoucherPage').payload.scenarioRevision, 'r2', 'the open scenario is read again with the new version');

  // 同一個情境收起再展開時又送了一次：先回來的新結果留著，晚到的舊結果不蓋掉它。
  const g = savedFixture({ saved: [savedBlankNamed('甲')], pane: 'saved' });
  g.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  const first = g.request('queryFilterVoucherPage');
  g.click('[data-action="toggle-scenario"][data-index="0"]');
  g.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  assert.equal(g.sent('queryFilterVoucherPage').length, 2);
  g.request('queryFilterVoucherPage').resolve(savedFirstPage('NEW-1', { voucherCount: 4, summaryRowCount: 4 })); await settle(); await settle();
  first.resolve(savedFirstPage('OLD-1', { voucherCount: 1, summaryRowCount: 1 })); await settle(); await settle();
  // 分開顯示傳票數量與分錄筆數。
  assert.match(g.$('[data-bind="scenario-preview-0"]').textContent, /4符合條件的傳票數量4符合條件的分錄筆數/);
  assert.match(g.$('[data-bind="scenario-preview-0"]').textContent, /NEW-1/);
  assert.ok(!g.text().includes('OLD-1'), 'the superseded answer does not replace the newer one');

  // 高風險條件彙總在途時換開另一個案件，舊的彙總讀取失敗：不留訊息，新案件的畫面也不寫失敗。
  const h = savedFixture({ saved: [savedBlankNamed('甲')], pane: 'saved' });
  h.click('[data-action="toggle-high-risk-summary"]'); await settle();
  const staleHighRiskSummary = h.request('queryHighRiskSummaryScenarios');
  const quiet = h.messages().length;
  h.store.setProject({ projectId: 'another-synthetic-case' });
  staleHighRiskSummary.reject(new Error('舊案件的彙總失敗')); await settle(); await settle();
  assert.equal(h.messages().length, quiet, 'a high-risk summary failure from the previous case leaves no message');
  assert.ok(!h.text().includes('舊案件的彙總失敗'));

  // 還沒有可用版本時仍是兩段：命中數已回來、傳票清單還在途時，已存清單被換掉：晚到的傳票清單不顯示，
  // 也不留作快取；再展開時重新讀取。有可用版本時只有一段，同樣的情況由上面「已存清單被換掉」那一段涵蓋。
  const k = savedFixture({ saved: [savedBlankNamed('甲')], resultRef: null, pane: 'saved' });
  k.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  k.request('filterPreview').resolve(scenarioPreview()); await settle();
  const lateVouchers = k.request('queryFilterVoucherPage');
  k.store.setSavedScenarios([savedBlankNamed('甲')]);
  lateVouchers.resolve(voucherPage('LATE-1')); await settle(); await settle();
  assert.ok(!k.text().includes('LATE-1'), 'the late voucher list is not shown');
  k.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  assert.equal(k.sent('filterPreview').length, 2, 'the replaced list is read again instead of reusing the late answer');
  assert.ok(!k.text().includes('LATE-1'));
});

test('while another operation runs, saved results still load and pressing save explains that work is still running', async () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲')], pane: 'saved' });
  f.store.setBusy(true, '執行資料驗證');
  f.click('[data-action="toggle-high-risk-summary"]'); await settle();
  assert.equal(f.sent('queryHighRiskSummaryScenarios').length, 1, 'the high-risk summary loads while another operation runs');
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  // 有可用版本時展開只讀存好的結果。
  assert.equal(f.sent('queryFilterVoucherPage').length, 1, 'a saved scenario result loads while another operation runs');
  assert.equal(f.store.getState().busy, true);
  assert.equal(f.store.getState().busyLabel, '執行資料驗證', 'reading saved results does not take over the busy area');

  f.click('[data-action="edit-scenario"][data-index="0"]');
  
  const before = f.messages().length;
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 0, 'nothing is saved while another operation runs');
  const added = f.messages().slice(0, f.messages().length - before);
  assert.equal(added.length, 1, 'pressing save is answered instead of doing nothing');
  // 用字照已定案的句式「「〈作業名稱〉」還在進行，請等它完成後再試。」
  assert.equal(added[0].text, '「執行資料驗證」還在進行，請等它完成後再試。');
  assert.equal(f.store.getState().busyLabel, '執行資料驗證');
});

// 「重新檢查並儲存情境」是已定案的按鈕名稱，逐字比對。
test('without a usable version the high-risk summary and the criteria report stay locked and say why', async () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲')], resultRef: null, pane: 'saved',
    check: { status: 'needsEdit', recalculatedCount: 0, problems: [{ position: 1, name: '甲', messages: ['合成原因'] }] } });
  const resave = f.$('[data-action="resave-scenarios"]');
  assert.ok(resave !== null);
  assert.equal(resave.textContent, '重新檢查並儲存情境');
  assert.ok(f.$('[data-action="toggle-high-risk-summary"]') === null, 'the high-risk summary cannot be opened');
  // 「尚無可用版本」的說法用已定案的句子：GL 區塊、報告區、高風險條件彙總與第六步各一句，逐字比對。
  // 清單下方的說明也是已定案的句子，逐字比對。
  const highRiskSummaryCard = f.$$('.rule-card').find(card => { const title = card.querySelector('.rule-card__title'); return title && title.textContent === '高風險條件彙總'; });
  assert.equal(highRiskSummaryCard.querySelector('.empty-state').textContent, '已儲存的篩選情境尚未套用目前規則，彙總暫不顯示。');
  const report = f.$('[data-action="export-criteria-report"]');
  assert.equal(report.disabled, true);
  assert.equal(report.closest('.report-output').querySelector('.report-list__empty').textContent, '已儲存的篩選情境尚未套用目前規則，暫時無法產生報告。');
  assert.equal(f.$('.population-scope__status > span').textContent, '已儲存的篩選情境尚未套用目前規則。');
  assert.equal(f.$('.population-scope__notice').textContent,
    '上列情境全部修改並儲存或移除後，才會顯示高風險條件彙總與全部符合條件的分錄，也才能產生條件篩選報告與工作底稿。');
  // 硬按停用的報告鈕：不送出、也不留訊息。
  const before = f.messages().length;
  f.press('[data-action="export-criteria-report"]'); await settle();
  assert.equal(f.sent('exportCriteriaSelectionReport').length, 0);
  assert.equal(f.messages().length, before);

  // 展開情境仍可看前幾筆：沒有版本時用條件內容即時讀，不用已存位置。
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  // 空白摘要卡用欄位空白條件。
  // 共用夾具用第 2 版編輯來源與 common 來源。
  const wire = { name: '甲', rationale: '空白摘要', groups: [{ join: 'AND', matchScope: 'row', conditions: [{ type: 'fieldValue', join: 'AND', field: 'description', operator: 'isBlank', includeBlank: false }] }],
    editorOrigins: { version: 2, nameIsAutomatic: true, rationaleIsAutomatic: true, groups: [{ cards: ['blankDescription'] }] }, source: 'common' };
  assert.deepEqual(plain(f.request('filterPreview').payload), { populationScope: 'auditPeriod', scenario: wire });
  f.request('filterPreview').resolve({ scenario: { count: 3, voucherCount: 2, summaryRowCount: 3, populationScope: 'auditPeriod',
    previewRows: [{ voucherNumber: 'JV-P', glDate: '2025-01-01', accountNumber: '1001', amount: 5 }] } }); await settle();
  assert.deepEqual(plain(f.request('queryFilterVoucherPage').payload), { scenario: wire, populationScope: 'auditPeriod', pageSize: 200 });
  // 正式後端一律回傳傳票清單；沒有版本時，照條件內容讀出的傳票清單直接列出。
  f.request('queryFilterVoucherPage').resolve(voucherPage('JV-P')); await settle(); await settle();
  const body = f.$('[data-bind="scenario-preview-0"]');
  assert.match(body.textContent, /JV-P/);
  assert.ok(body.querySelector('.filter-voucher-host table') !== null, 'the voucher list is shown');

  // 整個第五、六步都沒有範圍選擇器，也沒有舊按鈕名稱與內部用語。
  const retired = ['以查核期間重新保存', '較早的版本', '版本參照', '測試母體區', '有界預覽', '尚無 CriteriaSelectionReport', '凍結為 CriteriaSelectionReport', '匯出時會依目前資料重新計算'];
  for (const step of ['filter', 'export']) {
    f.draw(step);
    assert.equal(f.$$('[data-population-scope]').length, 0, step + ' has no population scope selector');
    for (const word of retired) assert.ok(!f.text().includes(word), step + ' does not say ' + word);
  }
  // 第六步的條件儲存時間寫上面那組句子的第四句「條件儲存時間：尚未套用目前規則。」。
  assert.ok(f.$$('.scenario-export-selection__hint').some(hint => hint.textContent.startsWith('條件儲存時間：尚未套用目前規則。')),
    'step six says the saved scenarios do not follow the current rules yet');
});

// 來源由草稿的卡片標記推導；已存情境在自己被預覽或編輯儲存時保留來源；自擬情境不帶來源。
// 儲存時只送正在編輯的那一個情境。
// 這裡的「舊版情境」是對應不回卡片的第 2 版情境：預覽照原樣送回；編輯時對應不回卡片，名稱與動機和一般情境一樣沒有必填標示，
// 重新儲存送第 2 版、不帶來源；重存後就是一般情境，加過的卡片標得回來。
test('previewing or editing one saved scenario keeps its common source, an old-version one maps back to no card, a self-authored one has none, and only that scenario is sent', async () => {
  const f = savedFixture({ saved: [authored('自擬'), savedBlankNamed('空白摘要情境'), noCardScenario('舊版情境')], pane: 'saved' });
  const prescreenBlank = [{ join: 'AND', matchScope: 'row', conditions: [{ type: 'prescreen', join: 'AND', prescreenKey: 'blankDescription' }] }];
  // 空白摘要卡使用欄位空白條件；舊情境的條件照原文保留。
  const fieldBlank = [{ join: 'AND', matchScope: 'row', conditions: [{ type: 'fieldValue', join: 'AND', field: 'description', operator: 'isBlank', includeBlank: false }] }];
  const blankOrigins = { version: 2, nameIsAutomatic: true, rationaleIsAutomatic: true, groups: [{ cards: ['blankDescription'] }] };
  const authoredOrigins = { version: 2, nameIsAutomatic: false, rationaleIsAutomatic: false, groups: [{ cards: [null] }] };
  // 有可用版本時展開只讀存好的結果，送位置與版本，不送條件內容；
  // 條件內容只在還沒有可用版本時隨即時預覽送出，所以三種情境的送出形狀在沒有可用版本的夾具核對。
  const unversioned = savedFixture({ saved: [authored('自擬'), savedBlankNamed('空白摘要情境'), noCardScenario('舊版情境')], resultRef: null, pane: 'saved' });
  for (const index of [0, 1, 2]) unversioned.click('[data-action="toggle-scenario"][data-index="' + index + '"]');
  await settle();
  // 展開預覽：三種情境照各自的來源送出；對應不回卡片的情境照存下的第 2 版編輯來源送回，沒有來源代碼。
  assert.deepEqual(plain(unversioned.sent('filterPreview').map(request => request.payload)), [
    { populationScope: 'auditPeriod', scenario: { name: '自擬', rationale: '合成自擬動機', groups: [{ join: 'AND', matchScope: 'row',
      conditions: [{ type: 'fieldValue', join: 'AND', operator: 'contains', includeBlank: false, field: 'description', values: ['合成'] }] }] } },
    { populationScope: 'auditPeriod', scenario: { name: '空白摘要情境', rationale: '空白摘要', groups: fieldBlank, editorOrigins: blankOrigins, source: 'common' } },
    { populationScope: 'auditPeriod', scenario: { name: '舊版情境', rationale: '合成舊版動機', groups: prescreenBlank, editorOrigins: authoredOrigins } }]);
  for (const index of [0, 1, 2]) f.click('[data-action="toggle-scenario"][data-index="' + index + '"]');
  await settle();
  assert.equal(f.sent('filterPreview').length, 0);
  assert.deepEqual(plain(f.sent('queryFilterVoucherPage').map(request => request.payload)), [1, 2, 3].map(scenarioPosition =>
    ({ scenarioPosition, scenarioRevision: 'r1', populationScope: 'auditPeriod', pageSize: 200 })));

  // 編輯對應不回卡片的情境：沒有卡片被標成已選，名稱與動機和一般情境一樣不標選填或必填；改名儲存時送第 2 版、不帶來源，只送這一個。
  f.click('[data-action="edit-scenario"][data-index="2"]');
  assert.deepEqual(f.$$('[data-common-card][aria-pressed="true"]').length, 0, 'no card is marked as chosen');
  for (const label of f.$$('[data-save-panel] .form__label')) {
    assert.doesNotMatch(label.textContent, /可選填/);
    // 名稱與動機不是必填，沒有必填星號。
    assert.ok(label.querySelector('.form__req') === null, 'an old-version scenario has no required mark like any other');
  }
  f.type('[data-bind="scenario-name"]', '舊版情境（改名）');
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 1);
  assert.deepEqual(plain(f.request('filterScenarioSave').payload), { populationScope: 'auditPeriod', position: 3, expectedName: '舊版情境',
    scenario: { name: '舊版情境（改名）', rationale: '合成舊版動機', groups: prescreenBlank, editorOrigins: authoredOrigins } });
  assertNoScreenKeys(f.request('filterScenarioSave').payload, 'the old-version scenario saved again');
  const renamed = { name: '舊版情境（改名）', rationale: '合成舊版動機', source: null, groups: prescreenBlank, editorOrigins: authoredOrigins };
  f.request('filterScenarioSave').resolve(saveResponse([authored('自擬'), savedBlankNamed('空白摘要情境'), renamed], 3)); await settle(); await settle();
  // 句子是已定案的「已儲存篩選情境（〈N〉 / 10）。」；Ui.run 在句尾接上耗時。
  // 耗時的寫法：不到 0.1 秒時寫「耗時不到 0.1 秒」，本檔各完成句都照這兩種寫法比對。
  assert.match(f.messages()[0].text, /^已儲存篩選情境（3 \/ 10），耗時(?: \d+(?:\.\d)? 秒|不到 0\.1 秒)。$/);
  // 編輯自擬情境：送出時沒有 source。
  f.click('[data-filter-pane-select="saved"]');
  f.click('[data-action="edit-scenario"][data-index="0"]');

  f.click('[data-action="save-scenario"]'); await settle();
  assert.deepEqual(plain(f.request('filterScenarioSave').payload), { populationScope: 'auditPeriod', position: 1, expectedName: '自擬',
    scenario: { name: '自擬', rationale: '合成自擬動機', groups: [{ join: 'AND', matchScope: 'row',
      conditions: [{ type: 'fieldValue', join: 'AND', operator: 'contains', includeBlank: false, field: 'description', values: ['合成'] }] }],
    editorOrigins: authoredOrigins } });
  assertNoScreenKeys(f.request('filterScenarioSave').payload, 'the self-authored edit');

  // 改成和另一個已存情境同名：不送出並說明；另存副本不受同名限制，名稱加上「（副本）」。
  // 重名不得影響唯一性，儲存時才檢查重名，另存副本略過。
  f.request('filterScenarioSave').reject(new Error('合成儲存失敗。')); await settle(); await settle();
  f.type('[data-bind="scenario-name"]', '空白摘要情境');
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 2, 'a duplicate name is not sent');
  assert.match(f.notice(), /同名/);
  f.click('[data-action="save-scenario-copy"]'); await settle();
  assert.equal(f.scenarioWrites().length, 3);
  const copy = f.request('filterScenarioSave').payload;
  assert.equal(copy.position, null);
  assert.equal(Object.hasOwn(copy, 'expectedName'), false);
  assert.equal(copy.scenario.name, '空白摘要情境（副本）');
  assert.equal(Object.hasOwn(copy.scenario, 'source'), false);

  // 編輯標得回卡片的空白摘要情境，拿掉那張卡改用欄位條件：已存的 common 來源不沿用，儲存時沒有 source。
  const g = savedFixture({ saved: [authored('自擬'), savedBlankNamed('空白摘要情境')], pane: 'saved' });
  g.click('[data-action="edit-scenario"][data-index="1"]');
  assert.equal(g.$('[data-common-card="blankDescription"]').getAttribute('aria-pressed'), 'true', 'the card maps back from the saved origin');
  g.click('[data-common-card="blankDescription"]');
  g.click('[data-condition-source="field"]');
  g.click('[data-add-field="description"]');
  g.type('[data-value-key="values"]', '合成');

  for (const label of g.$$('[data-save-panel] .form__label')) assert.doesNotMatch(label.textContent, /可選填/);
  g.type('[data-bind="scenario-name"]', '空白摘要情境改成自擬');
  g.type('[data-bind="scenario-rationale"]', '合成改寫動機');
  g.click('[data-action="save-scenario"]'); await settle();
  assert.equal(g.scenarioWrites().length, 1);
  const rewritten = g.request('filterScenarioSave').payload;
  assert.equal(rewritten.position, 2);
  assert.equal(Object.hasOwn(rewritten.scenario, 'source'), false, 'a restored common scenario without its card is no longer sent with the common source');
  assertNoScreenKeys(rewritten, 'the rewritten scenario');

  // 對應不回卡片的情境加上一張卡存過一次後，送出的是第 2 版，那張卡帶 common 來源；之後再編輯，卡片標得回來；
  // 把卡拿掉後就是一般情境，不帶來源。
  const h = savedFixture({ saved: [noCardScenario('舊版情境'), noCardScenario('另一個舊版情境')], pane: 'saved' });
  h.click('[data-action="edit-scenario"][data-index="0"]');
  h.click('[data-common-card="specificTrailingDigits"]');

  h.click('[data-action="save-scenario"]'); await settle();
  const withCard = h.request('filterScenarioSave').payload.scenario;
  assert.equal(withCard.source, 'common');
  assert.deepEqual(plain(withCard.editorOrigins), { version: 2, nameIsAutomatic: false, rationaleIsAutomatic: false,
    groups: [{ cards: [null, 'specificTrailingDigits'] }] }, 'no old-version origin is recorded any more');
  h.request('filterScenarioSave').resolve(saveResponse([plain(withCard), noCardScenario('另一個舊版情境')], 1)); await settle(); await settle();
  h.click('[data-filter-pane-select="saved"]');
  h.click('[data-action="edit-scenario"][data-index="0"]');
  assert.equal(h.$('[data-common-card="specificTrailingDigits"]').getAttribute('aria-pressed'), 'true', 'the added card maps back');
  // 名稱與動機沒有選填標示。
  for (const label of h.$$('[data-save-panel] .form__label')) assert.doesNotMatch(label.textContent, /可選填/);
  h.click('[data-common-card="specificTrailingDigits"]');

  for (const label of h.$$('[data-save-panel] .form__label')) assert.doesNotMatch(label.textContent, /可選填/);
  h.click('[data-action="save-scenario"]'); await settle();
  const withoutCard = h.request('filterScenarioSave').payload;
  assert.equal(h.scenarioWrites().length, 2);
  assert.deepEqual(plain(withoutCard.scenario.groups), [{ join: 'AND', matchScope: 'row', conditions: [{ type: 'prescreen', join: 'AND', prescreenKey: 'blankDescription' }] }]);
  assert.equal(Object.hasOwn(withoutCard.scenario, 'source'), false, 'without a card the scenario is an ordinary one');
  assert.deepEqual(plain(withoutCard.scenario.editorOrigins), authoredOrigins);
  assertNoScreenKeys(withoutCard, 'the scenario without its card saved again');
});

// 「非營業日之分錄」直接加入一條條件；已儲存情境裡也可能是只包一條條件的子條件組，照樣能讀，也認得是這張卡。
// 審計員編輯這種情境時，卡片標成已選、條件照原樣留著；重新儲存時條件照原樣送回、編輯來源記這張卡、
// 來源代碼是 common。另一次編輯時再按一次卡片，拿掉的就是那個子條件組。
test('a scenario saved with the earlier one-condition bracket of the non-business-day card is still that card when edited and saved again', async () => {
  const bracket = [{ join: 'AND', matchScope: 'row', conditions: [{ type: 'group', join: 'AND', conditions: [
    { type: 'fieldValue', join: 'AND', field: 'glDate', operator: 'isNonBusinessDay', includeBlank: false }] }] }];
  const saved = () => ({ name: '非營業日之分錄', rationale: '非營業日之分錄', source: 'common', groups: bracket,
    editorOrigins: { version: 2, nameIsAutomatic: true, rationaleIsAutomatic: true, groups: [{ cards: ['nonBusinessDay'] }] } });
  const f = savedFixture({ saved: [saved()], pane: 'saved' });
  f.click('[data-action="edit-scenario"][data-index="0"]');
  assert.equal(f.$('[data-common-card="nonBusinessDay"]').getAttribute('aria-pressed'), 'true', 'the saved bracket maps back to the card');
  assert.deepEqual(plain(f.draft().groups[0].conditions.map(rule => rule.type)), ['group'], 'the saved bracket stays as it was');
  f.type('[data-bind="scenario-name"]', '非營業日之分錄（改名）');
  f.click('[data-action="save-scenario"]'); await settle();
  const sent = plain(f.request('filterScenarioSave').payload.scenario);
  assert.equal(sent.name, '非營業日之分錄（改名）');
  assert.deepEqual(sent.groups, bracket);
  assert.deepEqual(sent.editorOrigins.groups, [{ cards: ['nonBusinessDay'] }]);
  assert.equal(sent.source, 'common');
  assertNoScreenKeys(f.request('filterScenarioSave').payload, 'the earlier bracket saved again');

  const g = savedFixture({ saved: [saved()], pane: 'saved' });
  g.click('[data-action="edit-scenario"][data-index="0"]');
  g.click('[data-common-card="nonBusinessDay"]');
  assert.deepEqual(plain(g.draft().groups.flatMap(group => group.conditions)), [], 'pressing the card again removes the saved bracket');
  assert.equal(g.$('[data-common-card="nonBusinessDay"]').getAttribute('aria-pressed'), 'false');
});

// 移除的確認問題出現在該情境那一列，說明移除的影響；移除只送那一個情境。
test('removing a saved scenario asks in its own row, cancel returns focus, a new list or case clears the question, and only confirming sends it', async () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲'), savedBlankNamed('乙'), savedBlankNamed('丙')], pane: 'saved' });
  const active = () => f.dom.document.activeElement;
  const confirmArea = () => f.$('.saved-scenario__confirm');
  f.click('[data-action="remove-scenario"][data-index="1"]');
  const area = confirmArea();
  assert.ok(area !== null, 'the question appears in place');
  assert.equal(area.closest('.saved-scenario').querySelector('.saved-scenario__name').textContent, '2. 乙', 'the question sits in the row of the scenario being removed');
  const warning = area.querySelector('.form-notice');
  assert.ok(warning.classList.contains('form-notice--warning'), 'the impact is an amber reminder');
  assert.match(warning.textContent, /移除/);
  // 黃色提示不過度解釋，用已定案的短句，不提已匯出的底稿；這裡核對整句。
  assert.equal(warning.textContent, '移除這個情境；若正在編輯它，目前的條件也會清空。');
  assert.match(warning.textContent, /編輯/, 'it says what happens to the conditions being edited');
  assert.equal(f.$('[data-action="remove-scenario"][data-index="1"]').hidden, true);
  assert.ok(active() === f.$('[data-action="cancel-remove-scenario"][data-index="1"]'), 'focus moves to cancel');
  assert.equal(f.scenarioWrites().length, 0, 'pressing remove alone sends nothing');

  // 取消：問題收起，焦點回到同一列的情境標題。
  f.click('[data-action="cancel-remove-scenario"][data-index="1"]');
  assert.ok(confirmArea() === null);
  assert.ok(active() === f.$('[data-action="toggle-scenario"][data-index="1"]'), 'focus returns to the same row');

  // 已存清單被換掉（例如另一個動作存好了）：問題收起。
  f.click('[data-action="remove-scenario"][data-index="1"]');
  f.store.setSavedScenarios([savedBlankNamed('甲'), savedBlankNamed('乙'), savedBlankNamed('丙')]);
  assert.ok(confirmArea() === null, 'a replaced list clears the question');
  // 換開另一個案件：問題也收起。
  f.click('[data-action="remove-scenario"][data-index="1"]');
  f.store.setProject({ projectId: 'another-synthetic-case' });
  f.click('[data-filter-pane-select="saved"]');
  assert.ok(confirmArea() === null, 'another case clears the question');
  assert.equal(f.scenarioWrites().length, 0);

  // 確認後才送出，只帶那一個情境的位置與畫面上的名稱。
  f.click('[data-action="remove-scenario"][data-index="1"]');
  f.click('[data-action="confirm-remove-scenario"][data-index="1"]'); await settle();
  assert.equal(f.scenarioWrites().length, 1);
  assert.deepEqual(plain(f.request('filterScenarioRemove').payload), { populationScope: 'auditPeriod', position: 2, expectedName: '乙' });
  f.request('filterScenarioRemove').resolve({ ok: true, position: null, savedCount: 2, scenarios: [savedBlankNamed('甲'), savedBlankNamed('丙')],
    resultRef: { revision: 'r2', populationScope: 'auditPeriod' }, filterScenarioCheck: { status: 'current', recalculatedCount: 2, problems: [] },
    staleState: { validation: false, prescreen: false, filter: false } });
  await settle(); await settle();
  assert.deepEqual(f.store.getState().filter.savedScenarios.map(scenario => scenario.name), ['甲', '丙']);
  // 句子是已定案的「已移除情境（剩餘 〈N〉 / 10）。」
  assert.deepEqual(f.messages()[0], { text: '已移除情境（剩餘 2 / 10）。', level: 'info' });
  assert.ok(active() === f.$('[data-action="toggle-scenario"][data-index="1"]'), 'focus moves to the row that took its place');
  assert.equal(f.$('[data-action="toggle-scenario"][data-index="1"]').closest('.saved-scenario').querySelector('.saved-scenario__name').textContent, '2. 丙');
});

// 重新檢查不整批失敗。
test('rechecking updates what it can, lists what still needs editing and never fails as a whole', async () => {
  const problems = [{ position: 1, name: '甲', messages: ['合成原因一'] }, { position: 2, name: '乙', messages: ['合成原因二'] }];
  const saved = [savedBlankNamed('甲'), savedBlankNamed('乙'), savedBlankNamed('丙')];
  const f = savedFixture({ saved, resultRef: null, check: { status: 'needsEdit', recalculatedCount: 0, problems } });
  const list = () => f.$('.filter-scenario-problems');
  assert.equal(list().querySelectorAll('li').length, 2);
  // 連線失敗：說明是這個按鈕的動作失敗，清單不變。
  f.click('[data-action="resave-scenarios"]'); await settle();
  assert.deepEqual(plain(f.request('filterScenarioRecheck').payload), { populationScope: 'auditPeriod' }, 'no scenario list is sent');
  f.request('filterScenarioRecheck').reject(new Error('合成連線失敗')); await settle(); await settle();
  assert.equal(f.messages()[0].level, 'error');
  assert.match(f.messages()[0].text, /重新檢查並儲存情境/);
  assert.match(f.messages()[0].text, /失敗/);
  assert.equal(list().querySelectorAll('li').length, 2);

  // 一個仍需修改：其他的更新，清單只剩那一個，訊息是一般說明，不是失敗。
  f.click('[data-action="resave-scenarios"]'); await settle();
  assert.equal(f.sent('filterScenarioRecheck').length, 2);
  f.request('filterScenarioRecheck').resolve({ ok: true, position: null, savedCount: 3, scenarios: saved, resultRef: null,
    filterScenarioCheck: { status: 'needsEdit', recalculatedCount: 2, problems: [problems[1]] }, staleState: { validation: false, prescreen: false, filter: true } });
  await settle(); await settle();
  // 句子是已定案的「已依目前規則重新檢查情境：〈N〉 個已更新，〈M〉 個仍需修改，請見上方清單。」；Ui.run 在句尾接上耗時。
  // 等級是一般。
  assert.equal(f.messages()[0].level, 'info');
  assert.match(f.messages()[0].text, /^已依目前規則重新檢查情境：2 個已更新，1 個仍需修改，請見上方清單，耗時(?: \d+(?:\.\d)? 秒|不到 0\.1 秒)。$/);
  assert.deepEqual(list().querySelectorAll('li').map(item => item.textContent), ['情境 2「乙」：合成原因二。']);
  assert.ok(f.$('[data-action="resave-scenarios"]') !== null, 'the button stays while a scenario still needs editing');
  assert.equal(f.sent('filterCommit').length, 0);

  // 全部可以套用：清單與按鈕都收起，結果解鎖。
  f.click('[data-action="resave-scenarios"]'); await settle();
  f.request('filterScenarioRecheck').resolve({ ok: true, position: null, savedCount: 3, scenarios: saved, resultRef: { revision: 'r3', populationScope: 'auditPeriod' },
    filterScenarioCheck: { status: 'current', recalculatedCount: 3, problems: [] }, staleState: { validation: false, prescreen: false, filter: false } });
  await settle(); await settle();
  // 句子是已定案的「已依目前規則重新檢查 〈N〉 個情境，全部已更新。」
  assert.match(f.messages()[0].text, /^已依目前規則重新檢查 3 個情境，全部已更新，耗時(?: \d+(?:\.\d)? 秒|不到 0\.1 秒)。$/);
  assert.ok(list() === null);
  assert.ok(f.$('[data-action="resave-scenarios"]') === null);
  assert.equal(f.window.JetUi.stepGate(f.store.getState(), 5).ok, true);
});

const criteriaReport = (revision, stale = false) => ({ reportId: 'criteria-' + revision, kind: 'criteriaSelectionReport', fileName: 'criteria-' + revision + '.xlsx',
  fileState: 'asPublished', stale, generatedUtc: '2026-01-01T00:00:00Z', sourceRef: { validationResultId: 'v1', scenarioRevision: revision, scenarioPositions: [1] } });

// 產生條件篩選報告時送出的資料綁定目前的驗證結果與情境版本。
// 產生報告的按鈕名稱用字沒有定案，只比對意思：有歷史報告或上游已變更時，表示是重新產生。
test('the criteria report is bound to the current validation and scenario version, and says regenerate when one exists or upstream changed', async () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲')], pane: 'saved' });
  const button = () => f.$('[data-action="export-criteria-report"]');
  assert.doesNotMatch(button().textContent, /重新/, 'the first report is not called a regeneration');
  f.click('[data-action="export-criteria-report"]'); await settle();
  assert.deepEqual(plain(f.request('exportCriteriaSelectionReport').payload), { validationResultId: 'v1', revision: 'r1' });
  f.request('exportCriteriaSelectionReport').resolve({ publishedReport: criteriaReport('r1') }); await settle(); await settle();
  // 句子是已定案的「已在案件資料夾產生條件篩選報告。」
  assert.match(f.messages()[0].text, /^已在案件資料夾產生條件篩選報告，耗時(?: \d+(?:\.\d)? 秒|不到 0\.1 秒)。$/);
  assert.match(button().textContent, /重新/, 'after a report exists the button regenerates it');

  for (const [label, options] of [['an older report in history', { reports: [criteriaReport('r0')] }],
    ['upstream data changed', { stale: { validation: false, prescreen: false, filter: true } }]]) {
    const g = savedFixture({ saved: [savedBlankNamed('甲')], pane: 'saved', ...options });
    assert.match(g.$('[data-action="export-criteria-report"]').textContent, /重新/, label);
  }
});

// 失效範圍與過期旗標只照後端的回應。
// 試算表修改時後端清掉全部情境，由 frontend-workflow.test.cjs 的「a trial-balance change clears the saved filter scenarios and draft」核對。
test('when the backend invalidates only filter hits, expanded scenarios stay open, read their hits again and the high-risk summary asks again', async () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲'), savedBlankNamed('乙')], pane: 'saved' });
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  // 有可用版本時展開只讀一次傳票頁。
  f.request('queryFilterVoucherPage').resolve(savedFirstPage('OLD-1')); await settle(); await settle();
  f.click('[data-action="toggle-high-risk-summary"]'); await settle();
  f.request('queryHighRiskSummaryScenarios').resolve(HIGH_RISK_SUMMARY); await settle(); await settle();
  assert.match(f.$('[data-bind="scenario-preview-0"]').textContent, /OLD-1/);
  const validate = f.store.getState().results.validate;

  f.store.applyMutationEffects({ invalidatedResults: { filter: true }, staleState: { validation: false, prescreen: false, filter: true },
    reports: [criteriaReport('r1', true)] });
  await settle();
  const toggle = f.$('[data-action="toggle-scenario"][data-index="0"]');
  assert.equal(f.$('[data-bind="scenario-body-0"]').hidden, false, 'the expanded scenario stays open');
  // 同功能的元件只留一套：展開的字固定，開著由 aria-expanded 與箭頭表示。
  // 情境的整列標題就是開關，字是情境名稱。
  assert.equal(toggle.textContent, '1. 甲');
  assert.equal(toggle.getAttribute('aria-expanded'), 'true');
  assert.equal(f.sent('queryFilterVoucherPage').length, 2, 'its hits are read again');
  assert.equal(f.sent('filterPreview').length, 0);
  assert.doesNotMatch(f.$('[data-bind="scenario-preview-0"]').textContent, /OLD-1/, 'the outdated rows are gone');
  assert.equal(f.$('[data-bind="high-risk-summary-body"]').hidden, false, 'the high-risk summary stays open');
  assert.equal(f.sent('queryHighRiskSummaryScenarios').length, 2, 'the high-risk summary is asked again');
  // 過期旗標與報告清單照後端的回應；沒有被宣告失效的驗證結果保留。
  assert.deepEqual(plain(f.store.getState().staleState), { validation: false, prescreen: false, filter: true });
  assert.deepEqual(plain(f.store.getState().reports), [criteriaReport('r1', true)]);
  assert.ok(f.store.getState().results.validate === validate, 'validation results that were not invalidated stay');
  // 後端沒有回傳報告清單時，清單不變。
  f.store.applyMutationEffects({ invalidatedResults: { filter: true }, staleState: { validation: false, prescreen: false, filter: true }, reports: null });
  assert.deepEqual(plain(f.store.getState().reports), [criteriaReport('r1', true)]);
});

// 命中明細的欄位取自後端的欄位資訊；抽樣明細的部分在 frontend-journey-step4.test.cjs。
// 攸關資料元素欄的值只從 customValues 取，同名的頂層鍵 999 是干擾值。
const RDE_COLUMN = { key: 'rde.amount1', label: '合成金額欄', valueType: 'amount', isCustom: true };
test('hit details show the backend RDE amount column with values from customValues', async () => {
  // 實際使用的路：展開已存情境，點傳票那一列展開分錄。
  // 傳票與分錄在同一張表，傳票是分組列，點整列就在下方列出分錄，分錄共用傳票表的欄位。
  // 表頭取自傳票頁，傳票頁與分錄頁的欄位資訊來自後端同一份欄位設定。
  // 有可用版本時展開只讀一次傳票頁。
  const columns = [{ key: 'voucherNumber', label: '傳票號碼', valueType: 'text', isCustom: false }, RDE_COLUMN];
  const f = savedFixture({ saved: [savedBlankNamed('甲')], pane: 'saved' });
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  f.request('queryFilterVoucherPage').resolve({ ...savedFirstPage('JV-1'), columns }); await settle(); await settle();
  const host = f.$('[data-bind="scenario-preview-0"] .filter-voucher-host');
  host.querySelector('[data-voucher-row="0"]').onclick(); await settle();
  assert.deepEqual(plain(f.request('queryFilterVoucherRowsPage').payload), { scenarioPosition: 1, scenarioRevision: 'r1', populationScope: 'auditPeriod',
    voucherNumber: 'JV-1', cursor: null, pageSize: 200, queryRevision: 'q1' });
  f.request('queryFilterVoucherRowsPage').resolve(unchecked('列的頂層刻意放一個和攸關資料元素同名的干擾值；正式後端不會送這個鍵', { queryRevision: 'q1', nextCursor: null,
    columns, rows: [{ lineItem: '1', isHit: true, amount: -10, 'rde.amount1': 999, customValues: { 'rde.amount1': 1234.5 } }] }));
  await settle(); await settle();
  const table = host.querySelector('table.voucher-table');
  assert.ok(table.querySelectorAll('thead th').map(th => th.textContent).includes('合成金額欄'));
  const cells = table.querySelectorAll('tr.voucher-table__entry td').map(td => td.textContent);
  assert.ok(cells.includes('1,234.50'), 'the RDE amount comes from customValues');
  assert.ok(!cells.includes('999'));
});

// ================================================================================================
// 第三部分　情境 14：開案時有待修正的舊情境
// ================================================================================================

// 開案：用正式的 JetUi.openProject 送 projectLoad，回應後讀訊息紀錄（logRecent），兩個都由測試回應。
async function openCase(f, load) {
  const opening = f.window.JetUi.openProject('合成案件');
  assert.deepEqual(plain(f.request('projectLoad').payload), { projectId: '合成案件' });
  f.request('projectLoad').resolve({ project: { projectId: '合成案件', currentStep: 4 }, validationResult: VALIDATED, reports: [], ...load });
  await settle();
  f.request('logRecent').resolve({ messages: [] });
  await opening; await settle();
}

// 「篩選規則已更新…」是已定案的句子。
// 等級是需要注意（warn）：訊息分三級，這句列為 warn。
test('opening a case after a rule upgrade logs the confirmed sentence once, and only then', async () => {
  const recalculated = savedFixture();
  await openCase(recalculated, { filterScenarios: [savedBlankNamed('甲'), savedBlankNamed('乙')], filterResultRef: { revision: 'r9', populationScope: 'auditPeriod' },
    filterScenarioCheck: { status: 'recalculated', recalculatedCount: 2, problems: [] } });
  const upgrade = recalculated.messages().filter(message => /篩選規則已更新/.test(message.text));
  // 黃色提示不過度解釋，用已定案的短句。
  assert.deepEqual(upgrade, [{ text: '篩選規則已更新，已重算 2 個情境；之前的條件篩選報告與底稿需要重新產生。', level: 'warn' }]);
  // 已是目前規則：不記這一句。
  const current = savedFixture();
  await openCase(current, { filterScenarios: [savedBlankNamed('甲')], filterResultRef: { revision: 'r9', populationScope: 'auditPeriod' },
    filterScenarioCheck: { status: 'current', recalculatedCount: 0, problems: [] } });
  assert.equal(current.messages().filter(message => /篩選規則已更新/.test(message.text)).length, 0);
});

// 逐字比對的句子：N1、「條件內容無法讀取」與「尚未選擇比較方式」。黃色提示不過度解釋，N1 用已定案的短句。
// 原因句由後端產生，這裡確認畫面照樣逐筆列出、不改字；清單最後的下一步只寫一次。
// 提示避免專業術語、不得過長，下一步不逐筆重複。
// 甲用到已移除的 GL 欄位 voucherDate：條件標題與讀回不印程式代號，寫法和其他認不得的欄位相同。
const N1 = '請到「進階條件篩選」修改後儲存，或移除不需要的情境。';
const DESCRIPTION_RULE = { type: 'fieldValue', join: 'AND', field: 'description', operator: 'contains', values: ['合成'] };
const retiredFieldScenario = name => ({ name, rationale: '合成舊情境動機', groups: [{ join: 'AND', matchScope: 'row', conditions: [
  { type: 'fieldValue', join: 'AND', field: 'voucherDate', operator: 'isNonBusinessDay' }, DESCRIPTION_RULE] }] });
test('scenarios to fix are listed one by one on steps five and six, and fixing them one at a time unlocks the export', async () => {
  const f = savedFixture();
  const saved = [retiredFieldScenario('甲'), savedBlankNamed('乙'), savedBlankNamed('丙')];
  await openCase(f, { filterScenarios: saved, filterResultRef: null, staleState: { validation: false, prescreen: false, filter: true },
    filterScenarioCheck: { status: 'needsEdit', recalculatedCount: 0, problems: [
      { position: 1, name: '甲', messages: ['此條件使用的欄位已不存在'] },
      { position: 2, name: '乙', messages: ['條件內容無法讀取'] },
      { position: 3, name: '丙', messages: ['尚未選擇比較方式'] }] } });
  const items = () => f.$('.filter-scenario-problems').querySelectorAll('li').map(item => item.textContent);
  const count = (text, sentence) => text.split(sentence).length - 1;
  const expected = ['情境 1「甲」：此條件使用的欄位已不存在。', '情境 2「乙」：條件內容無法讀取。', '情境 3「丙」：尚未選擇比較方式。'];
  for (const step of ['filter', 'export']) {
    f.draw(step);
    assert.match(f.$('.filter-scenario-problems').textContent, /無法套用目前規則/, step);
    assert.deepEqual(items(), expected, step + ' lists each scenario with its position, name and reason');
    assert.equal(count(f.text(), N1), 1, step + ' gives the next step once');
    assert.equal(f.$('.filter-scenario-problems').querySelector('p').textContent, N1, step + ' puts the next step after the list');
  }
  // 第六步不能匯出，並說明原因。
  assert.equal(f.$('[data-action="export-working-paper"]').disabled, true);
  assert.match(f.window.JetUi.stepGate(f.store.getState(), 5).missing.join(''), /無法套用目前規則/);

  // 第五步清單下方的說明是已定案的句子，逐字比對，只寫一次。
  f.draw('filter');
  const R3_23 = '上列情境全部修改並儲存或移除後，才會顯示高風險條件彙總與全部符合條件的分錄，也才能產生條件篩選報告與工作底稿。';
  assert.equal(count(f.text(), R3_23), 1);

  // 一個一個修改：先開甲。已存清單與編輯畫面都不出現程式代號 voucherDate。
  f.click('[data-filter-pane-select="saved"]');
  f.click('[data-action="toggle-scenario"][data-index="0"]');
  assert.match(f.$('[data-bind="scenario-body-0"]').textContent, /已不存在的欄位 在非營業日/);
  f.click('[data-action="edit-scenario"][data-index="0"]');
  const retiredRow = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.equal(retiredRow.querySelector('.filter-rule-heading strong').textContent, '欄位已不存在');
  // 條件卡片用已定案的句子說明此欄位目前未配對。
  assert.ok(retiredRow.textContent.includes('此欄位目前未配對，請回第三步確認，或移除後重新加入。'));
  // 讀回的已移除欄位部分照確認的最終文字逐字比對；後面傳票摘要那一條不是這則測試的重點，只看有接上。
  const readback = f.$('.filter-readback .scenario-readback').textContent;
  assert.ok(readback.startsWith('篩選條件：已不存在的欄位 在非營業日；空白不列入 且 '), readback);
  assert.match(readback, /傳票摘要/);
  assert.ok(!f.text().includes('voucherDate'), 'no program code is shown for the removed field');
  // 照原樣按儲存：畫面先擋下，說明那一條要重新選擇欄位（位置字首、提示框原因與「尚需補齊：」都照確認的句子）。
  
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 0, 'a scenario that still uses the removed field is not sent');
  assert.equal(f.notice(), '尚需補齊：第 1 組第 1 條：重新選擇欄位，或回到第三步確認配對');
  // 刪掉這一條後儲存：只送甲，送出的條件只剩傳票摘要那一條。
  retiredRow.querySelector('[data-action="remove-rule"]').fire('click');
  
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 1);
  assert.deepEqual(plain(f.request('filterScenarioSave').payload).position, 1);
  const fixedFirst = f.request('filterScenarioSave').payload.scenario;
  assert.deepEqual(plain(fixedFirst.groups), [{ join: 'AND', matchScope: 'row',
    conditions: [{ type: 'fieldValue', join: 'AND', operator: 'contains', includeBlank: false, field: 'description', values: ['合成'] }] }]);
  f.request('filterScenarioSave').resolve(saveResponse([fixedFirst, saved[1], saved[2]], 1, { resultRef: null, staleState: { validation: false, prescreen: false, filter: true },
    filterScenarioCheck: { status: 'needsEdit', recalculatedCount: 0, problems: [{ position: 2, name: '乙', messages: ['條件內容無法讀取'] },
      { position: 3, name: '丙', messages: ['尚未選擇比較方式'] }] } }));
  await settle(); await settle();
  assert.deepEqual(items(), expected.slice(1), 'the fixed scenario leaves the list');
  // 讀不出來的乙只能移除。
  f.click('[data-filter-pane-select="saved"]');
  f.click('[data-action="remove-scenario"][data-index="1"]');
  f.click('[data-action="confirm-remove-scenario"][data-index="1"]'); await settle();
  assert.deepEqual(plain(f.request('filterScenarioRemove').payload), { populationScope: 'auditPeriod', position: 2, expectedName: '乙' });
  const remaining = [fixedFirst, saved[2]];
  f.request('filterScenarioRemove').resolve({ ok: true, position: null, savedCount: 2, scenarios: remaining, resultRef: null,
    filterScenarioCheck: { status: 'needsEdit', recalculatedCount: 0, problems: [{ position: 2, name: '丙', messages: ['尚未選擇比較方式'] }] },
    staleState: { validation: false, prescreen: false, filter: true } });
  await settle(); await settle();
  assert.deepEqual(items(), ['情境 2「丙」：尚未選擇比較方式。']);
  f.draw('export');
  assert.equal(f.$('[data-action="export-working-paper"]').disabled, true, 'step six stays locked while one scenario still needs editing');
  // 最後改好丙：清單消失，第六步可以匯出。
  f.draw('filter');
  f.click('[data-filter-pane-select="saved"]');
  f.click('[data-action="edit-scenario"][data-index="1"]');
  
  f.click('[data-action="save-scenario"]'); await settle();
  assert.deepEqual(plain(f.request('filterScenarioSave').payload).position, 2);
  f.request('filterScenarioSave').resolve(saveResponse([fixedFirst, f.request('filterScenarioSave').payload.scenario], 2, { resultRef: { revision: 'r10', populationScope: 'auditPeriod' } }));
  await settle(); await settle();
  assert.ok(f.$('.filter-scenario-problems') === null);
  assert.equal(f.window.JetUi.stepGate(f.store.getState(), 5).ok, true);
  f.draw('export');
  assert.ok(f.$('.filter-scenario-problems') === null);
  assert.equal(f.$('[data-action="export-working-paper"]').disabled, false, 'step six can export once every scenario is fixed');
});

// 沒有逐筆清單時（資料不一致，或後端只知道要重新檢查）：第五、六步都寫 N2 一句，請審計員按「重新檢查並儲存情境」。
// N2 是已定案的句子。
test('without a per-scenario list both steps say once that rechecking will list what to edit', async () => {
  const f = savedFixture();
  await openCase(f, { filterScenarios: [savedBlankNamed('甲')], filterResultRef: null,
    filterScenarioCheck: { status: 'inconsistent', recalculatedCount: 0, problems: [] } });
  // 黃色提示不過度解釋，用已定案的短句。
  const N2 = '已儲存的篩選情境無法套用目前規則，請到「進階條件篩選」按「重新檢查並儲存情境」。';
  for (const step of ['filter', 'export']) {
    f.draw(step);
    assert.ok(f.$('.filter-scenario-problems') === null, step);
    assert.equal(f.text().split(N2).length - 1, 1, step + ' explains once');
  }
  f.draw('filter');
  assert.equal(f.$('[data-action="resave-scenarios"]').textContent, '重新檢查並儲存情境');
});

// ================================================================================================
// 第四部分　情境 15 的編輯部分：舊情境裡的舊式條件
// ================================================================================================

// 合成的攸關資料元素欄位：日期、金額、文字三種型別，加上一個後端不認得型別的欄位。
const RDE_FIELDS = [
  { fieldId: 'rde.date', label: '合成日期欄', valueType: 'date', sourceColumn: 'SynDate' },
  { fieldId: 'rde.amt', label: '合成金額欄', valueType: 'amount', sourceColumn: 'SynAmount' },
  { fieldId: 'rde.txt', label: '合成文字欄', valueType: 'text', sourceColumn: 'SynText' },
  { fieldId: 'rde.flag', label: '合成未知型別欄', valueType: 'boolean', sourceColumn: 'SynFlag' }];
// 舊情境裡的 typed 條件：有幾條多帶了比較方式用不到的值（舊資料常見），重存時只能送用得到的那一種。
const typedScenario = () => ({ name: '舊式欄位條件', rationale: '合成舊式動機', groups: [{ join: 'AND', matchScope: 'row', conditions: [
  { type: 'typed', join: 'AND', fieldId: 'rde.date', operator: 'between', from: '2025-01-01', to: '2025-03-31', value: '2025-02-01' },
  { type: 'typed', join: 'AND', fieldId: 'rde.amt', operator: 'greaterThan', value: '1000', amountBasis: 'absolute' },
  { type: 'typed', join: 'AND', fieldId: 'rde.txt', operator: 'isBlank', value: 'x', amountBasis: 'signed' },
  { type: 'typed', join: 'AND', fieldId: 'rde.txt', operator: 'in', values: ['A,B', 'C'] },
  { type: 'typed', join: 'AND', fieldId: 'rde.flag', operator: 'equals', value: '1' },
  { type: 'group', join: 'AND', conditions: [{ type: 'typed', join: 'AND', fieldId: 'rde.amt', operator: 'between', from: '1', to: '2', value: '9', amountBasis: 'signed' }] }] }] });
const typedRow = (f, ri) => f.$('.rule-row[data-gi="0"][data-ri="' + ri + '"]');
const ownControls = (row, selector) => row.querySelectorAll(selector).filter(node => node.closest('.rule-row') === row);

// 比較方式的選項與名稱鏡像後端，由 C# 的 OperatorMenus_… 核對。
test('an old field condition reopens with inputs shaped by the field type and its comparison', () => {
  const f = savedFixture({ saved: [typedScenario()], rdeFields: RDE_FIELDS, pane: 'saved' });
  f.click('[data-action="edit-scenario"][data-index="0"]');
  const inputs = (ri, bind) => ownControls(typedRow(f, ri), '[data-rule-bind="' + bind + '"]');
  // 日期欄的「介於」：兩個日期輸入框，沒有單一值，也沒有比較基準。
  assert.deepEqual(['from', 'to'].map(bind => inputs(0, bind).map(node => node.getAttribute('type'))), [['date'], ['date']]);
  assert.deepEqual(['from', 'to'].map(bind => inputs(0, bind)[0].value), ['2025-01-01', '2025-03-31']);
  assert.equal(inputs(0, 'value').length, 0);
  assert.equal(inputs(0, 'amountBasis').length, 0, 'only money fields compare on an amount basis');
  // 金額欄的「大於」：一個數字輸入框，另有比較基準兩個選項。
  assert.deepEqual(inputs(1, 'value').map(node => node.getAttribute('type')), ['number']);
  assert.equal(inputs(1, 'value')[0].value, '1000');
  assert.deepEqual(inputs(1, 'amountBasis')[0].querySelectorAll('option').map(option => option.getAttribute('value')), ['signed', 'absolute']);
  assert.equal(inputs(1, 'amountBasis')[0].value, 'absolute');
  // 「為空白」：沒有任何值的輸入框。
  for (const bind of ['value', 'from', 'to', 'values', 'amountBasis']) assert.equal(inputs(2, bind).length, 0, 'isBlank has no ' + bind);
  // 「屬於任一值」：一行一個值的文字框，說明經 aria-describedby 連到，說明寫出一行一個與上限。
  const set = inputs(3, 'values');
  assert.equal(set.length, 1);
  assert.equal(set[0].tagName, 'TEXTAREA');
  assert.equal(set[0].value, 'A,B\nC');
  const help = f.$('#' + set[0].getAttribute('aria-describedby'));
  assert.ok(help !== null, 'the value list is described');
  assert.match(help.textContent, /每行一個值/);
  assert.match(help.textContent, /100/);
  // 不認得型別的欄位：比較方式選單只有「請選擇」，沒有任何比較方式。
  const unknown = inputs(4, 'operator');
  assert.equal(unknown.length, 1);
  assert.deepEqual(unknown[0].querySelectorAll('option').map(option => option.getAttribute('value')), ['']);
  // 欄位清單只來自已確認的攸關資料元素欄位。
  assert.deepEqual(inputs(0, 'fieldId')[0].querySelectorAll('option').map(option => option.getAttribute('value')), ['', 'rde.date', 'rde.amt', 'rde.txt', 'rde.flag']);
  // 欄位已不在配對裡：不提供比較方式，請先選欄位。
  f.store.setMappingCommitted('gl', { mapping: { voucherNumber: 'Doc', glDate: 'Date', accountNumber: 'Code', description: 'Memo', amount: 'Amount' }, mode: 'signed',
    options: { rdeFields: RDE_FIELDS.filter(field => field.fieldId !== 'rde.date') } });
  assert.equal(inputs(0, 'operator').length, 0);
  assert.equal(inputs(0, 'from').length, 0);
});

// 「攸關資料元素欄位」是統一的用語。
test('clearing a comparison or listing too many values blocks saving; the completed condition sends only what its comparison uses', async () => {
  const f = savedFixture({ saved: [typedScenario()], rdeFields: RDE_FIELDS, pane: 'saved' });
  f.click('[data-action="edit-scenario"][data-index="0"]');
  
  const setOperator = value => { const select = ownControls(typedRow(f, 0), '[data-rule-bind="operator"]')[0]; select.value = value; select.fire('input'); };
  // 清掉比較方式：按儲存不送出並說明。
  setOperator('');
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 0);
  // 「攸關資料元素欄位條件需選定欄位與比較方式」是已定案的句子，逐字比對。
  assert.equal(f.notice(), '尚需補齊：攸關資料元素欄位條件需選定欄位與比較方式');
  // 值清單超過上限：同樣不送出。
  setOperator('between');
  const list = () => ownControls(typedRow(f, 3), '[data-rule-bind="values"]')[0];
  list().value = Array.from({ length: 101 }, (_, i) => 'V' + i).join('\n'); list().fire('input');
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 0, 'a list over the limit is not sent');
  // 用字照已定案的句子：逐條寫出是哪一條條件，「請填 1 到 100 個值」。
  assert.equal(f.notice(), '尚需補齊：第 1 組第 4 條「合成文字欄」請填 1 到 100 個值');
  // 清單仍超過上限時改了動機：提示留著，不會先收起。
  f.type('[data-bind="scenario-rationale"]', '合成舊式動機（補充）');
  assert.equal(f.notice(), '尚需補齊：第 1 組第 4 條「合成文字欄」請填 1 到 100 個值', 'the notice stays while the list is still over the limit');
  list().value = 'A,B\nC'; list().fire('input');
  // 補好之後儲存：只送這一個情境，每條 typed 條件只帶比較方式用得到的值；比較基準只有金額欄才帶，條件組內的也一樣。
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 1);
  const payload = plain(f.request('filterScenarioSave').payload);
  assert.deepEqual({ position: payload.position, expectedName: payload.expectedName, populationScope: payload.populationScope },
    { position: 1, expectedName: '舊式欄位條件', populationScope: 'auditPeriod' });
  assert.deepEqual(payload.scenario.groups, [{ join: 'AND', matchScope: 'row', conditions: [
    { join: 'AND', type: 'typed', fieldId: 'rde.date', operator: 'between', from: '2025-01-01', to: '2025-03-31' },
    { join: 'AND', type: 'typed', fieldId: 'rde.amt', operator: 'greaterThan', value: '1000', amountBasis: 'absolute' },
    { join: 'AND', type: 'typed', fieldId: 'rde.txt', operator: 'isBlank' },
    { join: 'AND', type: 'typed', fieldId: 'rde.txt', operator: 'in', values: ['A,B', 'C'] },
    { join: 'AND', type: 'typed', fieldId: 'rde.flag', operator: 'equals', value: '1' },
    { type: 'group', join: 'AND', conditions: [{ join: 'AND', type: 'typed', fieldId: 'rde.amt', operator: 'between', from: '1', to: '2', amountBasis: 'signed' }] }] }]);
  assertNoScreenKeys(payload, 'the old typed scenario');
});

// 選項的值是送給後端的封閉值，精確比對；比對方式的名稱只比對意思；空白處理的名稱是已定案的文字，逐字比對。
// TEXT_SET_MAX_VALUES 與後端 FilterScenarioLimits.MaxTextSetValuesPerRule 的鏡像只能看原始碼，由 FilterBackendRuleMirrorFrontendTests 核對。
const textSetScenario = () => ({ name: '舊式文字清單', rationale: '合成文字清單動機', groups: [{ join: 'AND', matchScope: 'row', conditions: [
  { type: 'textSet', join: 'AND', field: 'description', values: ['Alpha,Beta', 'Gamma'], mode: 'exact', normalization: 'removeAsciiSpaces' }] }] });
test('an old text value list edits one value per line, keeps commas, blocks 101 values for preview and save, and sends the lines as typed', async () => {
  const f = savedFixture({ saved: [textSetScenario()], pane: 'saved' });
  f.click('[data-action="edit-scenario"][data-index="0"]');
  const row = typedRow(f, 0);
  const textarea = ownControls(row, '[data-rule-bind="values"]')[0];
  assert.equal(textarea.tagName, 'TEXTAREA');
  assert.equal(textarea.value, 'Alpha,Beta\nGamma', 'one value per line, the comma stays inside its value');
  const help = f.$('#' + textarea.getAttribute('aria-describedby'));
  assert.ok(help !== null);
  assert.match(help.textContent, /每行一個值/);
  assert.match(help.textContent, /100/, 'the hint states the limit');
  const mode = ownControls(row, '[data-rule-bind="mode"]')[0], normalization = ownControls(row, '[data-rule-bind="normalization"]')[0];
  assert.deepEqual(mode.querySelectorAll('option').map(option => option.getAttribute('value')), ['contains', 'exact']);
  assert.equal(mode.value, 'exact');
  assert.match(mode.querySelectorAll('option')[0].textContent, /包含/);
  assert.match(mode.querySelectorAll('option')[1].textContent, /完全符合/);
  assert.deepEqual(normalization.querySelectorAll('option').map(option => option.getAttribute('value')), ['preserve', 'removeAsciiSpaces']);
  assert.equal(normalization.value, 'removeAsciiSpaces');
  // 「保留半形空白」「移除半形空白」是已定案的文字，逐字比對。
  assert.deepEqual(normalization.querySelectorAll('option').map(option => option.textContent), ['保留半形空白', '移除半形空白']);

  // 101 個值：預覽與儲存都不送出，畫面寫出上限。
  const values = () => ownControls(typedRow(f, 0), '[data-rule-bind="values"]')[0];
  values().value = Array.from({ length: 101 }, (_, i) => 'T' + i).join('\n'); values().fire('input');
  f.click('[data-action="preview-scenario"]'); await settle();
  assert.equal(f.sent('filterPreview').length, 0);
  // 提示框用已定案的句子逐條寫出是哪一條條件，逐字比對。
  assert.equal(f.notice(), '尚需補齊：第 1 組第 1 條「文字值清單」請填 1 到 100 個值');
  
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.scenarioWrites().length, 0);
  // 清單仍超過上限時改了動機：提示留著，不會先收起。
  f.type('[data-bind="scenario-rationale"]', '合成文字清單動機（補充）');
  assert.equal(f.notice(), '尚需補齊：第 1 組第 1 條「文字值清單」請填 1 到 100 個值', 'the notice stays while the list is still over the limit');
  f.type('[data-bind="scenario-rationale"]', '合成文字清單動機');
  // 改成兩行：提示收起，預覽送出的就是這兩行，逗號不拆開。
  values().value = 'A,B\nC'; values().fire('input');
  assert.equal(f.notice(), '');
  
  f.click('[data-action="preview-scenario"]'); await settle();
  const rule = { type: 'textSet', join: 'AND', field: 'description', values: ['A,B', 'C'], mode: 'exact', normalization: 'removeAsciiSpaces' };
  // 草稿送出的編輯來源是第 2 版，逐條記卡片代號。
  const scenario = { name: '舊式文字清單', rationale: '合成文字清單動機', groups: [{ join: 'AND', matchScope: 'row', conditions: [rule] }],
    editorOrigins: { version: 2, nameIsAutomatic: false, rationaleIsAutomatic: false, groups: [{ cards: [null] }] } };
  assert.deepEqual(plain(f.request('filterPreview').payload), { populationScope: 'auditPeriod', scenario });
  f.request('filterPreview').reject(new Error('合成預覽失敗。')); await settle(); await settle();
  
  f.click('[data-action="save-scenario"]'); await settle();
  assert.deepEqual(plain(f.request('filterScenarioSave').payload), { populationScope: 'auditPeriod', position: 1, expectedName: '舊式文字清單', scenario });
});

// 新增條件的入口不提供舊式型別；舊情境裡的文字清單、日期區間等歷史型別能顯示與編輯，標成「舊式」，但不能新建。
// 案件已確認攸關資料元素欄位、科目配對完整、有授權清單，舊的可用條件都成立，仍不得出現這些型別。
// 特定人員、特定摘要、空白摘要與特定尾數四張卡用欄位條件；舊式型別只用來讀取舊情境。
// 兩種舊式筆數條件也停用，歸在舊式清單。
const LEGACY_TYPES = ['text', 'textSet', 'dateRange', 'numRange', 'typed', 'customKeywords', 'customTrailingZeros', 'trailingDigits',
  'customPreparerEntryCount', 'customAccountEntryCount'];
const CURRENT_TYPES = ['fieldValue', 'drCrOnly', 'manualAuto', 'accountSide', 'specialAccountCategoryPair', 'accountPair', 'group', 'voucher',
  'prescreen', 'entityFrequency',
  'revenueDebitNearQuarterEnd', 'revenueWithoutNormalCounterpart', 'manualRevenueEntry', 'preparerEqualsApprover'];
function ruleTypes(draft) {
  const found = [];
  (function walk(rules) { for (const rule of rules || []) { found.push(rule.type); walk(rule.conditions); } })(draft.groups.flatMap(group => group.conditions));
  return found;
}
const assertCurrentTypes = (types, label) => {
  assert.ok(types.length > 0, label + ' adds conditions');
  for (const type of types) assert.ok(CURRENT_TYPES.includes(type) && !LEGACY_TYPES.includes(type), label + ' adds the current type ' + type);
};
test('no entry for adding a condition offers an old-style condition type', () => {
  const f = savedFixture({ rdeFields: RDE_FIELDS });
  f.store.setAuthorizedPreparerState({ rowCount: 2 });
  // 每一段從空白草稿開始；空白草稿換掉時不會詢問是否儲存。
  const reset = () => f.store.setFilterDraft(null);
  // 常用篩選條件的卡片：每張卡帶入的型別寫死，來源身分另外保留。卡片用代號識別。
  // 非營業日之分錄直接加入一條欄位條件，沒有包一層只有一條的子條件組。
  const expectedCards = { revenueDebitNearQuarterEnd: ['revenueDebitNearQuarterEnd'], fixedAssetDebitExpenseCredit: ['specialAccountCategoryPair'],
    revenueWithoutNormalCounterpart: ['revenueWithoutNormalCounterpart'], manualRevenueEntry: ['manualRevenueEntry'], specificPreparer: ['fieldValue'],
    specificKeywords: ['fieldValue'], blankDescription: ['fieldValue'], specificTrailingDigits: ['fieldValue'], nonBusinessDay: ['fieldValue'],
    preparerEqualsApprover: ['preparerEqualsApprover'] };
  const cards = {};
  for (const key of Object.keys(expectedCards)) {
    f.click('[data-common-card="' + key + '"]');
    cards[key] = ruleTypes(f.draft());
    reset();
  }
  assert.deepEqual(cards, expectedCards);

  // 欄位條件分頁的欄位按鈕（含三種型別的攸關資料元素欄位）。類別組的借貸別、人工/自動分錄與科目分類各帶入一條既有型別的條件，
  // 其他欄位帶入欄位條件。
  f.click('[data-condition-source="field"]');
  const fieldKeys = f.$$('[data-add-field]').map(button => button.getAttribute('data-add-field'));
  for (const id of ['rde.date', 'rde.amt', 'rde.txt']) assert.ok(fieldKeys.includes(id), id + ' has a field button');
  const choiceTypes = { drCr: ['drCrOnly'], manualAuto: ['manualAuto'], accountCategory: ['accountSide'] };
  for (const key of Object.keys(choiceTypes)) assert.ok(fieldKeys.includes(key), key + ' has a field button');
  for (const key of fieldKeys) { f.click('[data-add-field="' + key + '"]'); assert.deepEqual(ruleTypes(f.draft()), choiceTypes[key] || ['fieldValue'], key); reset(); }

  // 欄位底下的常用查法：逐一按下其中可用的。
  const checkKeys = f.$$('[data-add-shortcut]').filter(node => !node.disabled).map(node => node.getAttribute('data-add-shortcut'));
  assert.ok(checkKeys.length > 10, 'the field shortcuts are listed');
  for (const key of checkKeys) {
    f.click('[data-add-shortcut="' + key + '"]');
    assertCurrentTypes(ruleTypes(f.draft()), 'common check ' + key);
    reset();
  }

  // 預篩選分頁直接列出每一條預篩選規則：逐一按下其中可用的條件。
  f.click('[data-condition-source="prescreen"]');
  for (const key of f.$$('[data-add-prescreen]').filter(node => !node.disabled).map(node => node.getAttribute('data-add-prescreen'))) {
    f.click('[data-add-prescreen="' + key + '"]');
    assertCurrentTypes(ruleTypes(f.draft()), 'prescreen condition ' + key);
    reset();
  }

  // 樣態下拉的每個選項。
  // 從預篩選分頁加入一條條件，檢查它的樣態選單。
  f.click('[data-add-prescreen="suspiciousKeywords"]');
  const patternValues = f.$('[data-pattern-subject]').querySelectorAll('option').map(option => option.getAttribute('value'));
  assert.ok(patternValues.length > 5);
  for (const value of patternValues) assert.ok(!LEGACY_TYPES.includes(value), 'the pattern menu offers no ' + value);
  reset();

  // 條件組合，以及組合裡「加入子條件」的每個選項。
  f.click('[data-add-compound="group"]');
  const childKinds = f.$('[data-child-kind]').querySelectorAll('option').map(option => option.getAttribute('value'));
  for (const legacy of LEGACY_TYPES) assert.ok(!childKinds.includes('type:' + legacy), 'the child condition menu offers no ' + legacy);
  for (const value of childKinds) {
    const select = f.$('[data-child-kind]'); select.value = value;
    f.$('[data-child-add]').fire('click');
  }
  f.click('[data-add-compound="voucher"]');
  assertCurrentTypes(ruleTypes(f.draft()), 'condition brackets');
  reset();

  // 情境範例的每個範例各自套用一次。
  // 範例在右側條件組合上方的「從情境範例開始」托盤，每次套用前確認清單仍在托盤裡。
  const keys = f.$$('[data-action="apply-template"]').map(button => button.getAttribute('data-template-key'));
  assert.ok(keys.length > 3 && keys.every(Boolean), 'examples are listed');
  for (const key of keys) {
    assert.ok(f.$('.filter-template-tray [data-example-list]'), 'the examples stay listed in the tray');
    f.click('[data-action="apply-template"][data-template-key="' + key + '"]');
    assertCurrentTypes(ruleTypes(f.draft()), 'example ' + key);
    reset();
  }
});

// 同功能的元件只留一套：已儲存情境的「操作」和資料預覽的「其他資料」用同一個共用選單（ui-core.js 的 bindMenus）。
// 這裡照審計員的操作走一遍：用滑鼠與鍵盤打開、一次只開一個、Esc 與 Tab 與點其他地方關閉，按下動作後選單關閉、焦點去該去的地方。
test('each saved scenario\'s actions menu opens from its button or the keyboard, only one at a time, and closes on Esc, Tab, an outside click or a chosen action', () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲'), savedBlankNamed('乙')], pane: 'saved' });
  const active = () => f.dom.document.activeElement;
  const toggle = index => f.$$('.saved-scenario .jet-menu__toggle')[index];
  const menu = index => f.$('#saved-actions-' + index);
  const items = index => menu(index).querySelectorAll('[role="menuitem"]');
  // 標記：按鈕指向自己那一列的選單；選單是 role="menu"，三個動作只能用方向鍵到達。
  assert.equal(toggle(0).getAttribute('aria-haspopup'), 'menu');
  assert.equal(toggle(0).getAttribute('aria-controls'), 'saved-actions-0');
  assert.equal(toggle(0).getAttribute('aria-expanded'), 'false');
  assert.equal(toggle(0).textContent, '操作');
  assert.equal(menu(0).getAttribute('role'), 'menu');
  assert.equal(menu(0).hidden, true);
  assert.deepEqual(items(0).map(item => item.textContent), ['編輯', '另存副本', '移除']);
  for (const item of items(0)) assert.equal(item.getAttribute('tabindex'), '-1');

  // 鍵盤：方向鍵下打開並停在第一項，上下循環，Home、End 到頭尾，Esc 關閉並回到按鈕；方向鍵上打開時停在最後一項，Tab 關閉。
  toggle(0).focus();
  assert.equal(toggle(0).dispatch('keydown', { key: 'ArrowDown' }).defaultPrevented, true);
  assert.equal(toggle(0).getAttribute('aria-expanded'), 'true'); assert.equal(menu(0).hidden, false);
  assert.ok(active() === items(0)[0], 'ArrowDown opens the menu on the first action');
  active().dispatch('keydown', { key: 'ArrowUp' }); assert.ok(active() === items(0)[2], 'ArrowUp wraps to the last action');
  active().dispatch('keydown', { key: 'ArrowDown' }); assert.ok(active() === items(0)[0], 'ArrowDown wraps to the first action');
  active().dispatch('keydown', { key: 'End' }); assert.ok(active() === items(0)[2]);
  active().dispatch('keydown', { key: 'Home' }); assert.ok(active() === items(0)[0]);
  active().dispatch('keydown', { key: 'Escape' });
  assert.equal(toggle(0).getAttribute('aria-expanded'), 'false'); assert.equal(menu(0).hidden, true);
  assert.ok(active() === toggle(0), 'Esc returns focus to the button');
  toggle(0).dispatch('keydown', { key: 'ArrowUp' });
  assert.ok(active() === items(0)[2], 'ArrowUp opens the menu on the last action');
  active().dispatch('keydown', { key: 'Tab' });
  assert.equal(menu(0).hidden, true, 'Tab closes the menu');

  // 滑鼠：再按一次同一個按鈕關閉；打開另一列的選單時，前一個關閉；點畫面其他地方關閉。
  toggle(0).dispatch('click'); assert.equal(menu(0).hidden, false);
  toggle(0).dispatch('click'); assert.equal(menu(0).hidden, true, 'the same button closes it again');
  toggle(0).dispatch('click');
  toggle(1).dispatch('click');
  assert.equal(menu(0).hidden, true, 'opening another row closes the first menu');
  assert.equal(toggle(0).getAttribute('aria-expanded'), 'false');
  assert.equal(menu(1).hidden, false);
  // 情境名稱在整列標題的開關裡，點它會展開情境，所以「畫面其他地方」點的是區塊標題。
  f.$('[data-bind="saved-scenarios-title"]').dispatch('click');
  assert.equal(menu(1).hidden, true, 'a click elsewhere closes the menu');

  // 按下「移除」：選單關閉，確認問題在同一列出現，焦點在「取消」，選單裡的「移除」先藏起來。
  toggle(1).dispatch('click');
  items(1)[2].focus(); items(1)[2].dispatch('click');
  assert.ok(f.$('.saved-scenario__confirm') !== null, 'the removal question appears');
  assert.equal(toggle(1).getAttribute('aria-expanded'), 'false', 'choosing an action closes the menu');
  assert.equal(menu(1).hidden, true);
  assert.ok(active() === f.$('[data-action="cancel-remove-scenario"][data-index="1"]'), 'focus moves to cancel, not back to the menu');
  assert.equal(f.$('#saved-actions-1 [data-action="remove-scenario"]').hidden, true);
  assert.equal(f.scenarioWrites().length, 0, 'opening and closing the menu sends nothing');
});

// 靠近捲動區底邊的那一列，「操作」選單往下長，最後一項會被底邊切掉，所以下方放不下時改往上展開。
// 假 DOM 沒有版面，這裡照預覽主機量到的位置給固定值：捲動區從 0 到 778，上方 111 被標題列蓋住，最後一列的按鈕在 676 到 704，選單高 100。
test('an actions menu without room below opens upward, and a row with room below still opens downward', () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲'), savedBlankNamed('乙')], pane: 'saved' });
  const toggle = index => f.$$('.saved-scenario .jet-menu__toggle')[index];
  const opensUp = index => toggle(index).closest('.jet-menu').classList.contains('jet-menu--up');
  const scroller = f.dom.content;
  f.window.innerHeight = 900;
  f.window.getComputedStyle = node => node === scroller
    ? { gridTemplateColumns: 'none', overflowY: 'auto', scrollPaddingTop: '111px', scrollPaddingBottom: 'auto' }
    : { gridTemplateColumns: 'none', overflowY: 'visible' };
  scroller.getBoundingClientRect = () => ({ top: 0, bottom: 778 });
  scroller.clientTop = 0;
  scroller.clientHeight = 778;
  function rowAt(index, top) {
    toggle(index).getBoundingClientRect = () => ({ top, bottom: top + 28 });
    const list = f.$('#saved-actions-' + index);
    list.getBoundingClientRect = () => ({ top: top + 32, bottom: top + 132 });
    list.offsetHeight = 100;
  }
  rowAt(0, 200);
  rowAt(1, 676);

  toggle(1).dispatch('click');
  assert.equal(f.$('#saved-actions-1').hidden, false);
  assert.equal(opensUp(1), true, 'the row near the bottom edge opens upward');
  toggle(1).dispatch('click');
  assert.equal(opensUp(1), true, 'closing keeps the direction, so the menu shrinks back toward the button');
  toggle(0).dispatch('click');
  assert.equal(opensUp(0), false, 'a row with room below opens downward');

  // 捲動後再打開會重新量：同一列移到上方時改回往下展開。
  rowAt(1, 300);
  toggle(1).dispatch('click');
  assert.equal(opensUp(1), false, 'reopening measures the space again');
  // 上方只剩標題列蓋住的那一段時，往上也放不下，維持往下。
  toggle(1).dispatch('click');
  rowAt(1, 120);
  scroller.clientHeight = 200;
  toggle(1).dispatch('click');
  assert.equal(opensUp(1), false, 'the strip under the title bar does not count as room above');
});

// 按下選單裡的動作後跳出「尚未儲存」對話框，選「留在這裡」時，焦點回到那一列的「操作」，不落在已經收起的選單項目上。
test('staying in the unsaved-changes dialog opened from a scenario\'s actions menu returns focus to that row\'s actions button', () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲'), savedBlankNamed('乙')], pane: 'saved' });
  f.dom.document.body = f.dom.element('body');
  const active = () => f.dom.document.activeElement;
  const toggle = index => f.$$('.saved-scenario .jet-menu__toggle')[index];
  f.click('[data-common-card="revenueDebitNearQuarterEnd"]');
  const draft = JSON.stringify(f.draft());
  toggle(1).dispatch('click');
  const edit = f.$('#saved-actions-1 [data-action="edit-scenario"]');
  edit.focus(); edit.dispatch('click');
  const dialog = f.dom.document.body.querySelector('[data-bind="filter-pending-modal"]');
  assert.ok(dialog, 'the unsaved-changes dialog opens');
  assert.equal(toggle(1).getAttribute('aria-expanded'), 'false', 'the menu closes behind the dialog');
  dialog.fire('click', { target: dialog.querySelector('.modal__actions [data-pending-choice="stay"]') });
  assert.equal(f.dom.document.body.querySelector('[data-bind="filter-pending-modal"]'), null);
  assert.ok(active() === toggle(1), 'focus returns to the actions button of the same row');
  assert.equal(JSON.stringify(f.draft()), draft, 'staying keeps the conditions');
});

// 同功能的元件只留一套：第五步的兩組頁籤和其他頁籤一樣，方向鍵左右、Home、End 直接換頁，
// 只有選中的頁籤留在 Tab 鍵順序裡。
test('step five work and condition-source tabs switch with arrow keys, Home and End, and keep only the selected tab in the Tab order', () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲')] });
  const active = () => f.dom.document.activeElement;
  const work = key => f.$('[data-filter-pane-select="' + key + '"]');
  const source = key => f.$('[data-condition-source="' + key + '"]');
  assert.equal(work('filter').closest('[role="tablist"]').getAttribute('aria-label'), '篩選工作區');
  // 第一個條件來源分頁的鍵是 common。
  assert.equal(source('common').closest('[role="tablist"]').getAttribute('aria-label'), '條件來源');
  function expectWork(key, label) {
    for (const other of ['filter', 'saved']) {
      assert.equal(work(other).getAttribute('aria-selected'), other === key ? 'true' : 'false', label + ': aria-selected ' + other);
      assert.equal(work(other).getAttribute('tabindex'), other === key ? '0' : '-1', label + ': tabindex ' + other);
      assert.equal(f.$('[data-filter-pane="' + other + '"]').hidden, other !== key, label + ': pane ' + other);
    }
    assert.equal(work(key).getAttribute('aria-controls'), 'filter-pane-' + key);
    assert.ok(active() === work(key), label + ': focus follows the selection');
  }
  work('filter').focus();
  for (const [key, expected] of [['ArrowRight', 'saved'], ['ArrowRight', 'filter'], ['End', 'saved'], ['Home', 'filter'], ['ArrowLeft', 'saved']]) {
    assert.equal(active().dispatch('keydown', { key }).defaultPrevented, true, key + ' is handled');
    expectWork(expected, key);
  }
  work('filter').dispatch('click');
  source('common').focus();
  source('common').dispatch('keydown', { key: 'ArrowRight' });
  assert.ok(active() === source('prescreen'));
  assert.equal(source('prescreen').getAttribute('aria-selected'), 'true');
  assert.equal(f.$('[data-condition-pane="prescreen"]').hidden, false);
  assert.equal(f.$('[data-condition-pane="common"]').hidden, true);
  active().dispatch('keydown', { key: 'End' });
  // 第三個分頁的識別是 field。
  assert.ok(active() === source('field'));
  assert.equal(f.$('[data-condition-pane="field"]').hidden, false);
});

// 同功能的元件只留一套：展開的字固定，開關寫在 aria-expanded，aria-controls 指向它打開的內容。
// 情境的整列標題也是開關：字是情境名稱，再點一次收起。
test('the scenario title row and 查看彙總 keep their labels and report what they open through aria-expanded and aria-controls', async () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲')], pane: 'saved' });
  const result = () => f.$('[data-action="toggle-scenario"][data-index="0"]');
  const highRiskSummary = () => f.$('[data-action="toggle-high-risk-summary"]');
  assert.equal(result().textContent, '1. 甲');
  assert.ok(result().classList.contains('saved-scenario__toggle'));
  assert.equal(result().getAttribute('aria-controls'), 'scenario-body-0');
  assert.ok(f.$('#scenario-body-0') === f.$('[data-bind="scenario-body-0"]'), 'the id names the scenario body');
  assert.equal(result().getAttribute('aria-expanded'), 'false');
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  assert.equal(result().getAttribute('aria-expanded'), 'true');
  assert.equal(result().textContent, '1. 甲');
  assert.equal(f.$('#scenario-body-0').hidden, false);
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  assert.equal(result().getAttribute('aria-expanded'), 'false', 'the same title closes it again');
  assert.equal(f.$('#scenario-body-0').hidden, true);
  // 高風險條件彙總的開關寫「查看彙總」。
  // 就地展開的檢視是淡底小標籤，箭頭在字前面，樣式是 jet-view--expand。
  assert.equal(highRiskSummary().textContent, '查看彙總');
  assert.ok(highRiskSummary().classList.contains('jet-view--expand'));
  assert.equal(highRiskSummary().getAttribute('aria-controls'), 'filter-high-risk-summary-body');
  assert.ok(f.$('#filter-high-risk-summary-body') === f.$('[data-bind="high-risk-summary-body"]'), 'the id names the high-risk summary body');
  f.click('[data-action="toggle-high-risk-summary"]'); await settle();
  assert.equal(highRiskSummary().getAttribute('aria-expanded'), 'true');
  assert.equal(highRiskSummary().textContent, '查看彙總');
  f.click('[data-action="toggle-high-risk-summary"]');
  assert.equal(highRiskSummary().getAttribute('aria-expanded'), 'false');
  assert.equal(f.$('#filter-high-risk-summary-body').hidden, true);
});


// TB 匯入回應帶後端失效清單時，第五步和 GL 匯入一樣清掉已存情境、草稿、預覽與命中快取，因為改動 TB 視同改動 GL。
// 走正式匯入步驟套用回應的 applyResponse，不直接呼叫 store。
test('a trial-balance import response that invalidates filter results clears the saved scenarios and the hit cache like a GL import', async () => {
  const f = savedFixture({ saved: [savedBlankNamed('甲'), savedBlankNamed('乙')], pane: 'saved' });
  const vm = require('node:vm'), fs = require('node:fs'), path = require('node:path');
  vm.runInContext(fs.readFileSync(path.join(F.root, 'steps/import-step.js'), 'utf8')
    .replace("  Ui.registerStep('import', render);", '  window.exposedImport = { applyResponse };'), f.context, { filename: 'steps/import-step.js' });
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  // 有可用版本時展開只讀一次傳票頁。
  f.request('queryFilterVoucherPage').resolve(savedFirstPage('OLD-1')); await settle(); await settle();
  assert.match(f.$('[data-bind="scenario-preview-0"]').textContent, /OLD-1/);
  const before = f.store.getState();
  const generation = before.filterResultGeneration;
  assert.equal(before.filter.savedScenarios.length, 2);

  // 後端對 TB 匯入回的失效清單：驗證、預篩選、篩選命中與情境定義都失效，科目配對清掉（AuditDependencyPolicy 的 TbImport）。
  f.window.exposedImport.applyResponse('tb', { batchId: 'tb-synthetic', rowCount: 3, columns: ['科目代號', '科目名稱', '本期變動'],
    sources: [{ fileName: 'synthetic-tb.xlsx' }],
    invalidatedResults: { validation: true, prescreen: true, filter: true, filterScenarios: true, accountMapping: true },
    staleState: { validation: false, prescreen: false, filter: false }, reports: [] });
  await settle();

  const after = f.store.getState();
  assert.equal(after.importState.tb.rowCount, 3, 'the TB import result is recorded');
  assert.equal(after.mapping.tb.committed, null, 'the TB mapping is no longer committed');
  assert.deepEqual(plain(after.filter.savedScenarios), [], 'saved scenarios are cleared like a GL import');
  assert.equal(after.filter.draft.name, '');
  assert.equal(after.filter.draft.rationale, '');
  assert.deepEqual(plain(after.filter.draft.groups), []);
  assert.equal(after.filter.preview, null);
  assert.equal(after.filterResultRef, null);
  assert.equal(after.filterResultGeneration, generation + 1, 'the hit cache generation moves on');
  assert.equal(after.results.validate, null);
  assert.equal(after.results.prescreen, null);
  assert.equal(after.importState.accountMapping, null);
  assert.equal(f.$('[data-action="toggle-scenario"][data-index="0"]'), null, 'no saved scenario is drawn on step 5');
  assert.doesNotMatch(f.dom.content.textContent, /OLD-1/, 'the outdated hit rows are gone');
});

// 有卡片時自動產生的動機只列前五張卡的評估說明，只寫卡片名稱或「自訂：」加條件的那幾行不列，
// 改寫過的動機照樣整段列出。下面兩則確認只寫卡片名稱的「空白摘要」不列出。
const generatedRationale = () => ({ ...authored('自訂自動'), rationale: '檢視符合以下條件的分錄：摘要包含 合成。' });

test('saved scenarios hide the rationale generated from custom conditions and the card-name lines, and keep handwritten rationales', () => {
  const f = savedFixture({ saved: [authored('自擬'), generatedRationale(), F.savedBlankDescription('卡片')], pane: 'saved' });
  assert.deepEqual(f.$$('.saved-scenario__rationale').map(node => node.textContent), ['合成自擬動機']);
});

test('step six shows handwritten rationales and lists the conditions instead of a generated rationale or a card name', () => {
  const f = savedFixture({ saved: [authored('自擬'), generatedRationale(), F.savedBlankDescription('卡片')], step: 'export' });
  f.store.setStepIndex(5);
  // 預篩選報告那一列也用同一種選項外觀，只取帶情境位置的選項。
  const meta = f.$$('.scenario-export-option').filter(option => option.querySelector('[data-scenario-position]'))
    .map(option => option.querySelector('.scenario-export-option__meta').textContent);
  assert.equal(meta.length, 3);
  assert.equal(meta[0], '合成自擬動機');
  assert.doesNotMatch(meta[1], /檢視符合以下條件的分錄/);
  assert.match(meta[1], /合成/);
  assert.notEqual(meta[2], '空白摘要');
  assert.match(meta[2], /摘要/, 'the conditions are listed instead of the card name');
});

// 有卡片時列出動機的三種情形。卡片的評估說明照 ui-core.js 的 FILTER_COMMON_CONDITIONS 逐字寫下；
// 情境只用特定人員與空白摘要兩張卡，或特定人員加一條自訂條件，以及審計員改寫過的動機。
const PREPARER_EVALUATION = '執行長、財務長及高階主管通常不預期會經手會計分錄編製或過帳。且因高階主管所處的位置常有能力能直接或間接操縱會計記錄來進行財務報表舞弊。誘因或壓力愈大，則愈有可能合理化其舞弊行為。';
const preparerRule = { type: 'fieldValue', join: 'AND', field: 'createdBy', operator: 'in', values: ['合成主管'], includeBlank: false };
const blankRule = { type: 'fieldValue', join: 'AND', field: 'description', operator: 'isBlank', includeBlank: false };
const creditRule = { type: 'drCrOnly', join: 'AND', drCr: 'credit' };
function cardScenario(name, rationale, rules, cards, rationaleIsAutomatic = true) {
  return { name, rationale, source: 'common', groups: [{ join: 'AND', matchScope: 'row', conditions: rules }],
    editorOrigins: { version: 2, nameIsAutomatic: true, rationaleIsAutomatic, groups: [{ cards }] } };
}
const RATIONALE_CASES = () => [
  cardScenario('只有卡片', PREPARER_EVALUATION + '\n空白摘要', [preparerRule, blankRule], ['specificPreparer', 'blankDescription']),
  cardScenario('卡片加自訂', PREPARER_EVALUATION + '\n自訂：僅貸方', [preparerRule, creditRule], ['specificPreparer', null]),
  cardScenario('改寫過', '合成改寫的動機\n自訂：僅貸方', [preparerRule, creditRule], ['specificPreparer', null], false)
];

test('with cards the saved list shows only the evaluation notes, and a rewritten rationale stays whole', () => {
  const f = savedFixture({ saved: RATIONALE_CASES(), pane: 'saved' });
  assert.deepEqual(f.$$('.saved-scenario__rationale').map(node => node.textContent),
    [PREPARER_EVALUATION, PREPARER_EVALUATION, '合成改寫的動機\n自訂：僅貸方']);
});

test('with cards the step-six options show only the evaluation notes, and a rewritten rationale stays whole', () => {
  const f = savedFixture({ saved: RATIONALE_CASES(), step: 'export' });
  f.store.setStepIndex(5);
  const meta = f.$$('.scenario-export-option').filter(option => option.querySelector('[data-scenario-position]'))
    .map(option => option.querySelector('.scenario-export-option__meta').textContent);
  assert.deepEqual(meta, [PREPARER_EVALUATION, PREPARER_EVALUATION, '合成改寫的動機\n自訂：僅貸方']);
});
