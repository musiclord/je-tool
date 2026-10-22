'use strict';
require('./support/resource-guard.cjs');
// 第三步欄位配對的情境測試（收尾四項第一項第 7 批，2026-10-13）。
//
// 情境 5「第三步配對內容與確認」：審計員在簡易清單指派過帳狀態欄、讀來源值、沒選值時想確認、選值後確認；
// 設定人工與自動代碼（只列一側、兩側都列）；勾新的攸關資料元素欄、確認、拿到後端發的識別碼、改名再確認；
// 取消指派過帳狀態欄後再確認；在簡易清單與對照表格比較能不能確認；切換金額格式。
// 另外補第 0 批留下的兩段：傳票核准日只有一個來源選擇（清掉來源欄、舊草稿互相矛盾、取消變更），
// 以及確認失敗後在文字框打字要標成「設定已修改」。
// 情境 6「第三步重繪、焦點、捲動與連續輸入」：全新草稿用下拉選欄；表格捲到一半後選欄；改借方代碼與攸關資料元素名稱，
// 不離開輸入框直接按確認；確認後按重新配對再改一次；欄名含引號時選欄；重新匯入時舊的表格預覽晚到。
//
// 每個測試都經過 app.js 的 renderContent 畫出正式第三步（共用夾具 mappingRenderFixture）：
// 正式的 state.js、ui-core.js、steps/mapping-step.js 與 app.js，Store 每次通知都走正式的重繪判斷，
// 事件在假 DOM 上冒泡，setTimeout 收進佇列由測試執行。後端請求記在 f.requests，由測試 resolve 或 reject。
//
// 斷言規則照 artifacts/review/closing-four-items-20261013/plan-item1-tests.md 第 1 節：
// 只有使用者裁定過的文字逐字比對，出處寫在該斷言旁；其他文字只比對意思；預期值寫死，不由被測程式算出；
// 送出的 payload 精確比對，而且不得帶 `__` 開頭的鍵。訊息面板的句子留給第 10 批，這個檔不寫。
//
// 取代的 C# 方法（src/JET/tests/JET.Tests/Architecture/，2026-10-14 第二、三波整併已刪除，紀錄見 impl-item1-wave23-consolidation.md）：
//   情境 5：FrontendHardeningContractTests.MappingEditors_ShareCommitEligibility_AndClassicShowsMissingRail、
//     MappingNamesAndTemplateStatusFrontendTests.MappingControls_… 與 MappingTexts_…、PhaseFiveFollowUpFrontendTests.ManualAutoLegend_…、
//     MappingProjectionPolicyFrontendTests 的 PostingStatusAndManualCodes_…、ManualAutoCodes_…、PostingStatusPolicy_…、
//     RelevantDataElementFields_… 與 ApprovalDateModes_AreMutuallyExclusive…、
//     StoreNotificationConventionFrontendTests.MappingModeSwitch_…、UserFeedbackStepsOneToFourFrontendTests.MappingUi_ShowsCommitFailure…。
//   情境 6：FrontendEscapingTests.MappingColumnAttributes_…、StoreNotificationConventionFrontendTests 其餘六個方法。
//   另補 FrontendHardeningContractTests.LatestResponseGuard_… 與 WorkflowControlsAccessibilityFrontendTests.DisabledControlsExplainWhy…
//   的第三步部分；這兩個方法的其他步驟由別批補齊後，也已在 2026-10-14 第二、三波整併刪除。
// 第三步的重繪慣例（原本寫在已刪除的 StoreNotificationConventionFrontendTests 說明裡）：會改變衍生畫面的寫入一律重繪；借方代碼與攸關資料元素名稱
// 是例外，輸入時就地更新衍生畫面、失焦不重建，避免確認鈕在滑鼠按下與放開之間被換掉（2026-10-02 W3）。重建後的焦點與捲動
// 由 app.js 的 renderContent 統一還原。歷史根因與收斂裁定見 docs/history/specs/2026-09-01-frontend-sync-devlog-mutation-plan.md。
// 對照表與改壞檢查紀錄見 artifacts/review/closing-four-items-20261013/impl-item1-batch7.md。
const test = require('node:test');
const assert = require('node:assert/strict');
const { mappingRenderFixture, plain, settle, readWwwroot } = require('./support/frontend-fixtures.cjs');

const GL_COLUMNS = ['Doc', 'Date', 'Account', 'Name', 'Memo', 'Debit', 'Credit', 'Amount', 'Side', 'Manual', 'Posting', 'Approve', 'Dept', 'Region'];
const TB_COLUMNS = ['TbAccount', 'TbName', 'TbDebit', 'TbCredit'];
// 分別設定借貸金額格式的全部必填欄位。
const DUAL = { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', debitAmount: 'Debit', creditAmount: 'Credit' };
const DEFAULT_MANUAL = { manualValues: ['1'], automaticValues: ['0'] };
// 單欄金額加借貸別格式、而且借方代碼已填的完整草稿。
const SIDE_DONE = { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', amount: 'Amount', dcField: 'Side', dcDebitCode: 'D' };

const settleAll = async (times = 4) => { for (let i = 0; i < times; i++) await settle(); };

// 2026-10-16：使用者要求來源資料每頁 200 筆，取消對照表只列十筆的限制；既有失效及焦點檢查保留。
test('mapping grid pages 201 source rows as 200 and one while preserving every mapping choice', async () => {
  const f = journey({ ui: 'grid', columns: ['Doc'], draft: { docNum: 'Doc' } });
  const first = f.last('queryDataPreview');
  assert.deepEqual(plain(first.payload), { dataset: 'pbcGl', limit: 200, offset: 0 });
  const firstRows = Array.from({ length: 200 }, (_, i) => ['MAPPING-' + (i + 1)]);
  first.resolve({ columns: ['Doc'], rows: firstRows, totalCount: 201 }); await settleAll();
  const savedDraft = f.draftNow();
  assert.equal(f.$$('.map-grid tbody tr').length, 200);
  assert.equal(f.need('[data-page-position]').textContent, '1/2');
  assert.equal(f.need('[data-source-previous]').disabled, true);
  f.press('[data-source-next]');
  assert.deepEqual(plain(f.last('queryDataPreview').payload), { dataset: 'pbcGl', limit: 200, offset: 200 });
  f.last('queryDataPreview').resolve({ columns: ['Doc'], rows: [['MAPPING-201']], totalCount: 201 }); await settleAll();
  assert.equal(f.$$('.map-grid tbody tr').length, 1);
  assert.equal(f.need('.map-grid tbody').textContent, 'MAPPING-201');
  assert.equal(f.need('[data-page-position]').textContent, '2/2');
  assert.equal(f.need('[data-source-next]').disabled, true);
  assert.deepEqual(f.draftNow(), savedDraft);
  f.press('[data-source-previous]');
  assert.deepEqual(plain(f.last('queryDataPreview').payload), { dataset: 'pbcGl', limit: 200, offset: 0 });
  f.last('queryDataPreview').resolve({ columns: ['Doc'], rows: firstRows, totalCount: 201 }); await settleAll();
  assert.equal(f.$$('.map-grid tbody tr').length, 200);
  assert.deepEqual(f.draftNow(), savedDraft);
  assert.equal(f.$('.th-sort'), null, 'mapping does not sort only the visible source rows');
});

test('mapping next-page failure preserves the successful page and retries the failed offset', async () => {
  const f = journey({ ui: 'grid', columns: ['Doc'], draft: { docNum: 'Doc' } });
  f.last('queryDataPreview').resolve({ columns: ['Doc'], rows: [['KEPT-MAPPING']], totalCount: 201 }); await settleAll();
  f.press('[data-source-next]');
  assert.equal(f.need('.map-grid tbody').textContent, 'KEPT-MAPPING');
  assert.equal(f.need('[data-page-position]').textContent, '1/2');
  assert.equal(f.need('[data-source-next]').disabled, true);
  f.last('queryDataPreview').reject(new Error('synthetic next-page failure')); await settleAll();
  assert.equal(f.need('.map-grid tbody').textContent, 'KEPT-MAPPING');
  assert.equal(f.need('[data-page-position]').textContent, '1/2');
  f.press('[data-source-retry]');
  assert.deepEqual(plain(f.last('queryDataPreview').payload), { dataset: 'pbcGl', limit: 200, offset: 200 });
  f.last('queryDataPreview').resolve({ columns: ['Doc'], rows: [['RETRIED-MAPPING']], totalCount: 201 }); await settleAll();
  assert.equal(f.need('.map-grid tbody').textContent, 'RETRIED-MAPPING');
  assert.equal(f.need('[data-page-position]').textContent, '2/2');
  assert.equal(f.$('[data-source-retry]'), null);
  assert.deepEqual(f.draftNow(), { docNum: 'Doc' });
});

// 本檔的夾具：建立在共用的 mappingRenderFixture 上，先匯入合成的 GL（需要時加 TB），再依參數放好草稿。
// 草稿與格式只是開場的佈置；測試要驗的操作一律經畫面上的控制項觸發。
function journey({ ui = 'classic', columns = GL_COLUMNS, tb = null, mode = 'dual', draft = null, options = null, serialization } = {}) {
  const f = mappingRenderFixture(serialization ? { serialization } : {});
  f.store.setMappingUiMode(ui);
  f.store.setImportResult('gl', { batchId: 'gl-1', columns, rowCount: 3 });
  if (tb) f.store.setImportResult('tb', { batchId: 'tb-1', columns: tb, rowCount: 2 });
  if (mode !== 'dual') f.store.setMappingMode('gl', mode);
  if (draft) f.store.replaceMappingDraft('gl', draft);
  if (options) f.store.patchGlMappingOptions(options);
  f.timers.run();
  const section = (kind = 'gl') => f.body().querySelector('[data-bind="mapping-' + kind + '"]');
  const $ = (selector, kind = 'gl') => { const s = section(kind); return s ? s.querySelector(selector) : null; };
  const $$ = (selector, kind = 'gl') => { const s = section(kind); return s ? s.querySelectorAll(selector) : []; };
  const need = (selector, kind = 'gl') => { const node = $(selector, kind); assert.ok(node !== null, 'rendered: ' + selector); return node; };
  function choose(selector, value, kind = 'gl') { const node = need(selector, kind); node.focus(); node.value = value; node.dispatch('change'); return node; }
  function tick(selector, checked, kind = 'gl') { const node = need(selector, kind); node.checked = checked; node.dispatch('change'); return node; }
  function type(selector, value, kind = 'gl') { const node = need(selector, kind); node.focus(); node.value = value; node.dispatch('input'); return node; }
  function press(selector, kind = 'gl') { const node = need(selector, kind); assert.ok(node.disabled === false, 'enabled: ' + selector); node.dispatch('click'); return node; }
  const sent = action => f.requests.filter(request => request.action === action);
  const last = action => { const found = sent(action); assert.ok(found.length > 0, action + ' requested'); return found.at(-1); };
  const draftNow = () => plain(f.store.getState().mapping.gl.draft);
  const optionsNow = () => plain(f.store.getState().mapping.gl.options);
  const commitDisabled = (kind = 'gl') => need('[data-action="commit-' + kind + '"]', kind).disabled;
  const problems = (kind = 'gl') => { const node = $('[data-bind="mapping-option-problems"]', kind); return node === null ? '' : node.textContent; };
  const legendGroup = text => $$('fieldset').find(set => { const legend = set.querySelector('legend'); return legend !== null && legend.textContent === text; }) || null;
  const railItems = (kind = 'gl') => $$('.map-rail__item', kind).map(item => ({
    label: item.querySelector('.map-rail__jump').textContent, done: item.className.split(/\s+/).includes('is-done') }));
  return { ...f, section, $, $$, need, choose, tick, type, press, sent, last, draftNow, optionsNow, commitDisabled, problems, legendGroup, railItems };
}

// 記下之後每一次焦點落在哪個元素（假 DOM 的 focus() 會設定 document.activeElement）。
function focusHistory(f) {
  const seen = [];
  let current = f.dom.document.activeElement;
  Object.defineProperty(f.dom.document, 'activeElement', { configurable: true,
    get: () => current, set: value => { current = value; if (value) seen.push(value); } });
  return seen;
}

// 確認成功時後端的回應：只帶 canonical options 與筆數。
function commitReply(options = {}) {
  return { projectedRowCount: 3, approvalDateMode: 'unmapped', postingStatusPolicy: null,
    manualAutoPolicy: plain(DEFAULT_MANUAL), rdeFields: [], ...options };
}

// 送出的 payload 不得帶 __ 開頭的鍵（計畫第 1 節第 3 點）。
function assertNoInternalKeys(payload) {
  assert.ok(!JSON.stringify(payload).includes('"__'), 'payload has no __ keys: ' + JSON.stringify(payload));
}

/* ---- 情境 5：第三步配對內容與確認 ------------------------------------------------ */

for (const kind of ['posting', 'manual']) test('section 9 F: ' + kind + ' source read failures use the shared notice and retry wording', async () => {
  const f = journey({ draft: { ...DUAL, postingStatus: 'Posting', manual: 'Manual' } });
  f.press('[data-action="load-' + kind + '-profile"]');
  f.last('mappingValueProfile').reject(new Error('synthetic read failure'));
  await settleAll();
  const retry = f.need('[data-action="load-' + kind + '-profile"]');
  assert.equal(retry.textContent, '重新載入');
  assert.ok(retry.closest('[data-load-failure].form-notice[role="alert"]'));
  const before = f.sent('mappingValueProfile').length;
  retry.dispatch('click');
  assert.equal(f.sent('mappingValueProfile').length, before + 1);
});

test('mapping: every control has a distinguishable name, the preview button reads 預覽 and the list uses the ruled field names', async () => {
  const f = journey({ tb: TB_COLUMNS, mode: 'side', draft: { manual: 'Manual', postingStatus: 'Posting' } });

  // 2026-10-02 主線實測 W6：下拉、借方代碼、逐值選項與三個「加入」按鈕都要有可分辨的名稱，名稱取自欄位目錄或來源值。
  // 名稱的寫法沒有使用者逐字裁定，只比對意思（docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md「W6 只加無障礙名稱」）。
  // 對照表格下拉名稱改用「JET 欄位」時的第一次失敗收據：20261013-092449164-3847782f6c50488b86a0ac3856e606ca。
  // 簡易清單：每一列的控制項都以那一列的 JET 欄位命名，名稱彼此不同。
  const rows = f.$$('tr.mapping-table__row');
  assert.ok(rows.length > 10, 'the list shows every field of the side format');
  const names = [];
  for (const row of rows) {
    const label = row.querySelector('.mapping-table__label').textContent.replace('*', '').trim();
    const control = row.querySelector('select, input');
    assert.ok(control !== null, 'row has a control: ' + label);
    const name = control.getAttribute('aria-label');
    assert.ok(name && name.includes(label), 'the control of ' + label + ' is named after it: ' + name);
    names.push(name);
  }
  assert.equal(new Set(names).size, names.length, 'list control names are distinguishable');
  const tbNames = f.$$('select[data-mapping-key]', 'tb').map(select => select.getAttribute('aria-label'));
  assert.ok(tbNames.length > 0 && tbNames.every(Boolean), 'TB list selects are named too');

  // 使用者 2026-10-13 裁定改名（docs/history/specs/2026-10-06-kct-gl-requirements-plan.md「收尾四項：調查與裁定（2026-10-13）」
  // 選項「分錄金額（單欄）改成金額,借貸別欄位改成借貸別,分錄測試欄位改成 JET 欄位」），表頭與欄位名稱逐字比對。
  assert.equal(f.$('.mapping-table thead th').textContent, 'JET 欄位');
  const labels = rows.map(row => row.querySelector('.mapping-table__label').textContent.replace('*', '').trim());
  assert.ok(labels.includes('金額'), 'the single amount field is named 金額');
  assert.ok(labels.includes('借貸別'), 'the debit-credit field is named 借貸別');
  assert.ok(!labels.some(label => /分錄金額|借貸別欄位|分錄測試欄位/.test(label)), 'old names are gone');
  // 使用者 2026-09-21 第 8 則畫面註解：按鈕命名成「預覽」就好。紀錄見 docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md
  // 「兩處文字：簡易清單的「預覽來源資料」改「預覽」」與「09-21 十九則……第 8 則「預覽」是本批改的」。
  assert.equal(f.need('[data-action="preview-source-gl"]').textContent, '預覽');

  // 逐值選項：每個來源值的一組選項以那個值命名。
  f.press('[data-action="load-manual-profile"]');
  f.last('mappingValueProfile').resolve({ blankCount: 0, distinctCount: 2, truncated: false, values: [{ value: '1', count: 3 }, { value: 'M<1>', count: 1 }],
    comparisonGroups: [['1'], ['0'], ['M<1>']], missingComparisonValues: [] });
  await settleAll();
  const groups = f.$$('.value-assign__modes');
  assert.equal(groups.length, 2);
  assert.ok(groups[0].getAttribute('aria-label').includes('1') && groups[1].getAttribute('aria-label').includes('M<1>'), 'each value group is named after its source value');
  assert.notEqual(groups[0].getAttribute('aria-label'), groups[1].getAttribute('aria-label'));
  assert.equal(groups[0].getAttribute('role'), 'radiogroup', 'listing both sides is one choice per value');

  // 三個「加入」按鈕：畫面上都寫「加入」，報讀名稱分得出人工、自動與已過帳。
  f.press('[data-action="manual-entry-codes"]');
  f.press('[data-action="load-posting-profile"]');
  f.last('mappingValueProfile').resolve({ blankCount: 0, distinctCount: 60, truncated: true, values: [{ value: 'P', count: 9 }], comparisonGroups: [['P']] });
  await settleAll();
  const manualAdd = f.need('[data-action="add-code"][data-code-group="manual"]');
  const automaticAdd = f.need('[data-action="add-code"][data-code-group="automatic"]');
  const postingAdd = f.need('[data-action="add-posting-value"]');
  for (const button of [manualAdd, automaticAdd, postingAdd]) assert.equal(button.textContent, '加入');
  assert.match(manualAdd.getAttribute('aria-label'), /人工/);
  assert.match(automaticAdd.getAttribute('aria-label'), /自動/);
  assert.match(postingAdd.getAttribute('aria-label'), /已過帳/);
  assert.equal(new Set([manualAdd, automaticAdd, postingAdd].map(button => button.getAttribute('aria-label'))).size, 3);
  // 人工與自動代碼是可以直接改的輸入框，每一格有自己的名稱，沒有另外的刪除鈕。
  const codeNames = f.$$('[data-manual-code-edit]').map(input => input.getAttribute('aria-label'));
  assert.ok(codeNames.length >= 2 && new Set(codeNames).size === codeNames.length, 'code inputs are named one by one');
  assert.equal(f.$$('[data-remove-code]').length, 0);

  // 對照表格：每個表頭下拉以來源欄命名，說明它選的是 JET 欄位。
  f.store.setMappingUiMode('grid');
  const gridSelects = f.$$('select[data-map-col]');
  assert.equal(gridSelects.length, GL_COLUMNS.length, 'one select per source column');
  for (const select of gridSelects) {
    const name = select.getAttribute('aria-label');
    assert.ok(name.includes(select.getAttribute('data-map-col')) && name.includes('JET 欄位'), 'grid select named after its column: ' + name);
  }
  const literal = f.need('input[data-mapping-key="dcDebitCode"]');
  assert.match(literal.getAttribute('aria-label'), /借方代碼/);
  // 使用者 2026-09-22：「操作系統不需要繞口難讀的文言文」，提示不再寫「字面值（不是欄位名稱）」。提示句本身沒有使用者原話，只比對意思：
  // 說明是哪一方的代碼，並舉例。借、貸兩個代碼共用一段畫法時的第一次失敗收據：20261004-100752118-a195c091201e444d858ec7d7bee3d291。
  const hint = f.need('#' + literal.getAttribute('aria-describedby'));
  assert.match(hint.textContent, /借方/, 'the grid code hint explains which side the code stands for');
  assert.match(hint.textContent, /例如.*D/, 'and gives an example code');
  const creditHint = f.need('#' + f.need('input[data-mapping-key="dcCreditCode"]').getAttribute('aria-describedby'));
  assert.match(creditHint.textContent, /貸方/);
  assert.ok(!f.section().textContent.includes('字面值'), 'the grid hint uses plain words');
});

test('mapping: the manual and automatic block uses the same half-width slash as its field name', () => {
  // 使用者 2026-10-03 對 O7「「人工／自動分錄代碼」用全形斜線」選「要修」；處理方式是改成和欄位名稱「人工/自動分錄」同一種斜線，
  // 見 docs/history/specs/2026-09-17-user-feedback-and-workflow-review-plan.md「第三步人工/自動設定區的標題（O7）」。
  const f = journey();
  const label = f.$$('tr.mapping-table__row').map(row => row.querySelector('.mapping-table__label').textContent.trim())
    .find(text => text.startsWith('人工'));
  assert.equal(label, '人工/自動分錄');
  assert.ok(f.legendGroup('人工/自動分錄代碼') !== null, 'the block title matches before a source is chosen');
  f.choose('select[data-mapping-key="manual"]', 'Manual');
  assert.ok(f.legendGroup('人工/自動分錄代碼') !== null, 'and after a source is chosen');
  assert.ok(!f.$$('legend').some(legend => legend.textContent.includes('／')), 'no full-width slash in the block titles');
});

test('mapping: reading source values asks the backend for fifty values of the chosen column and shows reading, failure and retry', async () => {
  const f = journey({ draft: DUAL });
  f.choose('select[data-mapping-key="postingStatus"]', 'Posting');
  const posting = () => f.legendGroup('過帳狀態');
  f.press('[data-action="load-posting-profile"]');
  // 值、次數、空白數與是否截斷都由後端聚合，畫面只讀前 50 種。
  assert.deepEqual(plain(f.last('mappingValueProfile').payload),
    { dataset: 'gl', sourceColumn: 'Posting', limit: 50, comparisonValues: [], checkSourceValues: false });
  assert.match(posting().textContent, /讀取/, 'the block says it is reading until the reply');
  assert.ok(posting().querySelector('[data-posting-value]') === null);

  f.last('mappingValueProfile').reject(new Error('synthetic read failure'));
  await settleAll();
  assert.match(posting().textContent, /失敗/);
  const retry = posting().querySelector('[data-action="load-posting-profile"]');
  assert.ok(retry !== null, 'a failed read can be retried');
  retry.dispatch('click');
  assert.equal(f.sent('mappingValueProfile').length, 2, 'retry sends the read again');
  assert.deepEqual(plain(f.last('mappingValueProfile').payload),
    { dataset: 'gl', sourceColumn: 'Posting', limit: 50, comparisonValues: [], checkSourceValues: false });
  f.last('mappingValueProfile').resolve({ blankCount: 1, distinctCount: 2, truncated: false, values: [{ value: 'P', count: 5 }, { value: 'U', count: 2 }],
    comparisonGroups: [['P'], ['U']] });
  await settleAll();
  assert.deepEqual(posting().querySelectorAll('[data-posting-value]').map(box => box.getAttribute('data-posting-value')), ['P', 'U']);
  assert.ok(!/讀取來源值中/.test(posting().textContent));

  // 人工/自動分錄的來源值另外核對清單代碼是否出現在完整來源（不是只看畫面上的 50 種）。
  f.choose('select[data-mapping-key="manual"]', 'Manual');
  f.press('[data-action="load-manual-profile"]');
  assert.deepEqual(plain(f.last('mappingValueProfile').payload),
    { dataset: 'gl', sourceColumn: 'Manual', limit: 50, comparisonValues: ['1', '0'], checkSourceValues: true });
});

test('mapping: a code added by hand is compared by the backend and then checked against the whole source with the same fifty-value limit', async () => {
  const f = journey({ draft: { ...DUAL, manual: 'Manual' } });
  f.press('[data-action="load-manual-profile"]');
  f.last('mappingValueProfile').resolve({ blankCount: 0, distinctCount: 2, truncated: false, values: [{ value: '1', count: 3 }, { value: '0', count: 4 }],
    comparisonGroups: [['1'], ['0']], missingComparisonValues: [] });
  await settleAll();
  const before = f.sent('mappingValueProfile').length;
  f.press('[data-action="manual-entry-codes"]');
  f.type('[data-bind="automatic-code-add"]', 'X9');
  f.press('[data-action="add-code"][data-code-group="automatic"]');
  // 新代碼先只比對文字等價（不讀來源），送畫面上已有的值加上新代碼。
  const comparison = f.sent('mappingValueProfile').slice(before);
  assert.equal(comparison.length, 1, 'one comparison request for the new code');
  assert.deepEqual(plain(comparison[0].payload),
    { dataset: 'gl', sourceColumn: 'Manual', comparisonOnly: true, comparisonValues: ['1', '0', 'X9'] });
  // 比對回來之前，逐值選項不能選，也不先猜歸類。
  f.press('[data-action="manual-entry-values"]');
  const pendingRadios = f.$$('[data-manual-assign]');
  assert.ok(pendingRadios.length > 0 && pendingRadios.every(radio => radio.disabled && !radio.checked), 'pending values cannot be chosen');
  assert.equal(f.need('.value-assign-list').getAttribute('aria-busy'), 'true');

  comparison[0].resolve({ comparisonGroups: [['1'], ['0'], ['X9']] });
  await settleAll();
  const presence = f.sent('mappingValueProfile').slice(before + 1);
  assert.equal(presence.length, 1, 'then one full-source presence check');
  assert.deepEqual(plain(presence[0].payload),
    { dataset: 'gl', sourceColumn: 'Manual', limit: 50, comparisonValues: ['X9'], checkSourceValues: true });
  assert.match(f.legendGroup('人工/自動分錄代碼').textContent, /核對/, 'the block says the codes are being checked');
  presence[0].resolve({ blankCount: 0, distinctCount: 2, truncated: false, values: [], comparisonGroups: [['X9']], missingComparisonValues: ['X9'] });
  await settleAll();
  f.press('[data-action="manual-entry-codes"]');
  const added = f.$$('[data-manual-code-edit]').find(input => input.value === 'X9');
  assert.ok(added !== undefined && /來源裡沒有/.test(added.parentElement.textContent), 'the missing code is marked next to its input');
  assert.ok(f.commitDisabled() === false, 'a missing code is a reminder, not a block');
});

test('mapping: a mapped posting status needs an accepted value or blank before confirming, and only then is its policy sent', async () => {
  const f = journey({ draft: DUAL });
  assert.ok(f.commitDisabled() === false, 'all required fields are mapped');
  f.choose('select[data-mapping-key="postingStatus"]', 'Posting');
  assert.ok(f.commitDisabled() === true, 'a posting column without an accepted value blocks confirming');
  // 停用的確認鈕不默默鎖住：按鈕上方列出還要補什麼（WorkflowControlsAccessibilityFrontendTests.DisabledControlsExplainWhy… 的第三步部分）。
  // 句子沒有使用者逐字裁定，只比對意思。
  assert.match(f.problems(), /補/, 'the notice says something is still needed');
  assert.match(f.problems(), /過帳/);
  f.press('[data-action="load-posting-profile"]');
  f.last('mappingValueProfile').resolve({ blankCount: 2, distinctCount: 2, truncated: false, values: [{ value: 'P', count: 5 }, { value: 'U', count: 2 }],
    comparisonGroups: [['P'], ['U']] });
  await settleAll();
  f.tick('[data-posting-value="P"]', true);
  assert.ok(f.commitDisabled() === false);
  assert.equal(f.problems(), '');
  f.press('[data-action="commit-gl"]');
  const first = plain(f.last('mappingCommitGl').payload);
  assertNoInternalKeys(first);
  assert.deepEqual(first, { mapping: { ...DUAL, postingStatus: 'Posting' }, amountMode: 'dual', approvalDateMode: 'unmapped',
    manualAutoPolicy: DEFAULT_MANUAL, rdeFields: [], postingStatusPolicy: { acceptedValues: ['P'], includeBlank: false } });
  f.last('mappingCommitGl').reject(new Error('synthetic'));
  await settleAll();

  // 只勾「空白也視為已過帳」也算有接受的值。
  f.tick('[data-posting-value="P"]', false);
  assert.ok(f.commitDisabled() === true);
  f.tick('[data-option-bind="includeBlank"]', true);
  assert.ok(f.commitDisabled() === false);

  // 取消指派過帳狀態欄：不再要求接受值，送出時也沒有這個政策。
  f.choose('select[data-mapping-key="postingStatus"]', '');
  assert.ok(f.commitDisabled() === false);
  f.press('[data-action="commit-gl"]');
  const second = plain(f.last('mappingCommitGl').payload);
  assert.ok(!Object.hasOwn(second, 'postingStatusPolicy'), 'no posting policy without a posting column');
  assert.deepEqual(second, { mapping: DUAL, amountMode: 'dual', approvalDateMode: 'unmapped', manualAutoPolicy: DEFAULT_MANUAL, rdeFields: [] });
});

test('mapping: manual and automatic codes each need a value unless one side is listed alone', async () => {
  const f = journey({ draft: { ...DUAL, manual: 'Manual' } });
  assert.ok(f.commitDisabled() === false, 'the default codes 1 and 0 are enough');
  const commit = f.need('[data-action="commit-gl"]');
  const version = f.contentVersion();
  f.type('[data-manual-code-edit="automatic:0"]', '');
  assert.ok(commit.disabled === true, 'an empty automatic side blocks confirming at once');
  assert.match(f.problems(), /人工/);
  assert.match(f.problems(), /自動/);
  assert.equal(f.contentVersion(), version, 'typing a code does not rebuild the step');
  f.choose('[data-manual-mode]', 'automatic');
  assert.ok(f.commitDisabled() === false, 'listing only manual codes needs no automatic code');
  f.press('[data-action="commit-gl"]');
  assert.deepEqual(plain(f.last('mappingCommitGl').payload).manualAutoPolicy,
    { manualValues: ['1'], automaticValues: [], unlistedValueKind: 'automatic' });
  f.last('mappingCommitGl').reject(new Error('synthetic'));
  await settleAll();
  f.choose('[data-manual-mode]', 'manual');
  assert.ok(f.commitDisabled() === true, 'listing only automatic codes needs at least one automatic code');
  f.type('[data-bind="automatic-code-add"]', 'A');
  f.press('[data-action="add-code"][data-code-group="automatic"]');
  assert.ok(f.commitDisabled() === false);
});

test('mapping: whether two codes are the same comes from the backend comparison, not from JavaScript case rules', async () => {
  // 2026-09-23 固定的 Unicode 案例證明 JS 的大寫展開和 .NET OrdinalIgnoreCase 不等價：代碼是否相同由後端比對決定，
  // 人工與自動重疊也由後端確認時判定，前端不另擋。
  for (const [groups, expected] of [[[['m'], ['M'], ['0']], 'none'], [[['m', 'M'], ['0']], 'manual']]) {
    const f = journey({ draft: { ...DUAL, manual: 'Manual' } });
    f.type('[data-manual-code-edit="manual:0"]', 'm');
    f.press('[data-action="load-manual-profile"]');
    assert.deepEqual(plain(f.last('mappingValueProfile').payload).comparisonValues, ['m', '0']);
    f.last('mappingValueProfile').resolve({ blankCount: 0, distinctCount: 3, truncated: false,
      values: [{ value: 'm', count: 2 }, { value: 'M', count: 1 }, { value: '0', count: 4 }], comparisonGroups: groups, missingComparisonValues: [] });
    await settleAll();
    const chosen = f.$$('[data-manual-assign="M"]').find(radio => radio.checked);
    assert.ok(chosen !== undefined, 'the M row has a choice');
    assert.equal(chosen.value, expected, 'M follows the backend groups: ' + JSON.stringify(groups));
    // 同一個代碼同時列在人工與自動不會被前端擋下；後端確認時才判定。
    f.press('[data-action="manual-entry-codes"]');
    f.type('[data-manual-code-edit="automatic:0"]', 'm');
    assert.ok(f.commitDisabled() === false, 'no second overlap gate in the frontend');
    f.press('[data-action="commit-gl"]');
    assert.deepEqual(plain(f.last('mappingCommitGl').payload).manualAutoPolicy, { manualValues: ['m'], automaticValues: ['m'] }, 'raw codes are sent unchanged');
  }
});

test('mapping: a new relevant data element field is sent without an id and the id from the backend is reused after renaming', async () => {
  const f = journey({ draft: DUAL });
  // 攸關資料元素只收審計員勾選的來源欄；新欄一律不帶 fieldId，既有欄沿用後端發的識別碼，確認回應的設定覆寫草稿，
  // 下一次確認才會沿用同一個身分（第五步的條件靠它指到欄位）。
  // 核心欄位用掉的來源欄不列入攸關資料元素候選。
  const candidates = () => f.$$('[data-rde-column]').map(box => box.getAttribute('data-rde-column'));
  assert.ok(!candidates().includes('Doc') && !candidates().includes('Debit'), 'core columns are not offered');
  assert.ok(candidates().includes('Dept') && candidates().includes('Region'));
  f.tick('[data-rde-column="Dept"]', true);
  const typeSelect = f.need('[data-rde-type="Dept"]');
  assert.deepEqual(typeSelect.querySelectorAll('option').map(option => option.getAttribute('value')), ['text', 'date', 'money']);
  const typeLabels = typeSelect.querySelectorAll('option').map(option => option.textContent);
  assert.ok(/文字/.test(typeLabels[0]) && /日期/.test(typeLabels[1]) && /金額/.test(typeLabels[2]), 'type names: ' + typeLabels.join('、'));
  f.press('[data-action="commit-gl"]');
  const first = plain(f.last('mappingCommitGl').payload);
  assert.deepEqual(first.rdeFields, [{ sourceColumn: 'Dept', label: 'Dept', valueType: 'text' }], 'a new field never carries an id from the frontend');
  f.last('mappingCommitGl').resolve(commitReply({ rdeFields: [{ fieldId: 'rde.x', sourceColumn: 'Dept', label: 'Dept', valueType: 'text' }] }));
  await settleAll();
  f.timers.run();
  assert.ok(f.$('[data-action="remap-gl"]') !== null, 'the confirmed mapping folds into its summary');

  f.press('[data-action="remap-gl"]');
  f.type('[data-rde-label="Dept"]', '部門');
  f.press('[data-action="commit-gl"]');
  const second = plain(f.last('mappingCommitGl').payload);
  assertNoInternalKeys(second);
  assert.deepEqual(second.rdeFields, [{ sourceColumn: 'Dept', label: '部門', valueType: 'text', fieldId: 'rde.x' }], 'the backend id is reused');

  // 把攸關資料元素用的來源欄改配給核心欄位時，那個攸關資料元素欄位就拿掉，不會送出後端必定拒絕的草稿。
  f.last('mappingCommitGl').reject(new Error('synthetic'));
  await settleAll();
  f.choose('select[data-mapping-key="jeSource"]', 'Dept');
  assert.deepEqual(f.optionsNow().rdeFields, []);
  assert.ok(!candidates().includes('Dept'));
});

test('mapping: a debit or credit code is a typed value, not a source column, and does not hide that column from the extra fields', () => {
  // 第二遍第 9 批新增貸方代碼：兩個代碼都不佔用同名來源欄。第一次失敗收據：20261004-100752118-a195c091201e444d858ec7d7bee3d291。
  const f = journey({ mode: 'side', draft: { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', amount: 'Amount', dcField: 'Side' } });
  f.type('input[data-mapping-key="dcDebitCode"]', 'Dept');
  f.type('input[data-mapping-key="dcCreditCode"]', 'Region');
  assert.ok(f.$('[data-rde-column="Dept"]') !== null && f.$('[data-rde-column="Region"]') !== null, 'typed codes do not use up source columns');
  f.press('[data-action="select-all-rde"]');
  const selected = f.optionsNow().rdeFields.map(field => field.sourceColumn);
  assert.ok(selected.includes('Dept') && selected.includes('Region'));
});

test('mapping: source columns named like built-in object keys are still offered as extra fields', () => {
  // 「哪些來源欄已被核心配對用掉」用沒有原型的物件記錄；欄名剛好是 constructor 或 toString 時也不會被誤認成已用掉。
  const f = journey({ columns: [...GL_COLUMNS, 'constructor', 'toString', '__proto__'], draft: DUAL });
  const candidates = f.$$('[data-rde-column]').map(box => box.getAttribute('data-rde-column'));
  for (const column of ['constructor', 'toString', '__proto__']) assert.ok(candidates.includes(column), column + ' is offered');
  f.tick('[data-rde-column="constructor"]', true);
  f.choose('select[data-mapping-key="jeSource"]', 'Dept');
  assert.deepEqual(f.optionsNow().rdeFields.map(field => field.sourceColumn), ['constructor'], 'mapping another column keeps the field');
});

test('mapping: the list and the grid agree on the reason and on whether confirm can be pressed in three states', () => {
  const states = [
    { name: 'missing required', mode: 'dual', draft: { docNum: 'Doc' } },
    { name: 'settings incomplete', mode: 'side', draft: { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', amount: 'Amount', dcField: 'Side' } },
    { name: 'complete', mode: 'side', draft: { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', amount: 'Amount', dcField: 'Side', dcDebitCode: 'D' } }
  ];
  for (const state of states) {
    const seen = {};
    for (const ui of ['classic', 'grid']) {
      const f = journey({ ui, mode: state.mode, draft: state.draft });
      const rail = f.need('.map-rail');
      seen[ui] = { disabled: f.commitDisabled(), problems: f.problems(), rail: f.railItems(), title: rail.querySelector('.map-rail__title').textContent };
    }
    assert.deepEqual(seen.grid, seen.classic, state.name + ': both views agree');
    const view = seen.classic;
    if (state.name === 'missing required') {
      assert.ok(view.disabled === true);
      assert.deepEqual(view.rail.filter(item => !item.done).map(item => item.label), ['總帳日期', '會計科目編號', '會計科目名稱', '傳票摘要', '借方金額', '貸方金額'],
        'the list names every missing required field');
      assert.match(view.title, /6/);
    } else if (state.name === 'settings incomplete') {
      assert.ok(view.disabled === true);
      assert.ok(view.rail.every(item => item.done));
      assert.match(view.problems, /借方代碼|貸方代碼/, 'the reason points to the missing code');
    } else {
      assert.ok(view.disabled === false);
      assert.equal(view.problems, '');
    }
  }
});

test('mapping: switching the amount format prunes fields the new format does not use in a single redraw', () => {
  // 使用者 2026-10-12 指出第三步換金額欄位格式時選取一閃而過：原本同一次點擊先換草稿、再換格式，整段重繪兩次。
  // 清掉不適用的欄位與換格式是同一個操作，只重繪一次。
  const f = journey({ draft: { ...DUAL, manual: 'Manual' } });
  const version = f.contentVersion();
  const radio = f.need('input[name="mode-gl"][value="signed"]');
  radio.checked = true; radio.dispatch('change');
  assert.equal(f.contentVersion(), version + 1, 'one redraw for one click');
  assert.equal(f.store.getState().mapping.gl.amountMode, 'signed');
  const draft = f.draftNow();
  assert.ok(!('debitAmount' in draft) && !('creditAmount' in draft), 'debit and credit columns are pruned');
  assert.equal(draft.manual, 'Manual', 'fields shared by both formats stay');
  assert.deepEqual(f.railItems().filter(item => !item.done).map(item => item.label), ['金額']);
});

test('mapping: approval date has a single source choice; clearing the source means no approval date and old contradictions block confirming', async () => {
  // 2026-09-05 指定來源欄即切換模式，互斥由一次 Store 更新維持；2026-09-23 起兩種介面共用完整適用欄位；
  // 使用者 2026-10-08 畫面註解：核准日只留一個下拉，選來源欄即代表由來源欄提供。
  // 第 0 批核對時沒有行為測試的三件事在這裡補：清掉來源欄後回到「沒有核准日」、舊草稿互相矛盾時擋下確認，以及下一個測試的取消變更。
  const grid = journey({ ui: 'grid', draft: DUAL });
  grid.choose('select[data-map-col="Approve"]', 'docDate');
  assert.equal(grid.optionsNow().approvalDateMode, 'mapped');
  grid.choose('select[data-map-col="Approve"]', '');
  assert.equal(grid.optionsNow().approvalDateMode, 'unmapped', 'clearing the source column means no approval date');
  assert.ok(grid.commitDisabled() === false);
  grid.press('[data-action="commit-gl"]');
  const payload = plain(grid.last('mappingCommitGl').payload);
  assert.equal(payload.approvalDateMode, 'unmapped');
  assert.ok(!('docDate' in payload.mapping));

  // 簡易清單只有一個核准日下拉：選來源欄、改選與總帳日期相同、重新匯入後來源欄不見。
  const list = journey({ draft: DUAL });
  // 傳票核准日那一列只有一個控制項；選項是「沒有核准日」、與總帳日期相同，以及每一個來源欄。
  // 選項文字沒有使用者逐字裁定，只比對意思（GL_APPROVAL_DATE_MODES 的標籤）。
  const approvalRow = list.$$('tr.mapping-table__row').find(row => row.querySelector('.mapping-table__label').textContent.includes('傳票核准日'));
  assert.ok(approvalRow !== undefined, 'the list has an approval date row');
  assert.equal(approvalRow.querySelectorAll('select, input').length, 1, 'approval date is set through one control');
  assert.equal(list.$$('[data-mapping-key="docDate"]').length, 1, 'no second control writes the approval date source');
  const choiceLabels = list.need('[data-approval-choice]').querySelectorAll('option').map(option => option.textContent);
  assert.equal(choiceLabels.length, 2 + GL_COLUMNS.length);
  assert.ok(choiceLabels.slice(0, 2).some(label => /總帳日期/.test(label)), 'the dropdown offers the same date as the posting date: ' + choiceLabels.slice(0, 2).join('、'));
  assert.deepEqual(choiceLabels.slice(2), GL_COLUMNS, 'and every source column');
  list.choose('[data-approval-choice]', 'column:Approve');
  assert.deepEqual([list.optionsNow().approvalDateMode, list.draftNow().docDate], ['mapped', 'Approve']);
  assert.equal(list.need('[data-approval-choice]').value, 'column:Approve');
  list.choose('[data-approval-choice]', 'mode:sameAsPostDate');
  assert.deepEqual([list.optionsNow().approvalDateMode, list.draftNow().docDate], ['sameAsPostDate', undefined]);
  list.choose('[data-approval-choice]', 'column:Approve');
  list.store.setImportResult('gl', { batchId: 'gl-2', columns: GL_COLUMNS.filter(column => column !== 'Approve'), rowCount: 3 });
  assert.deepEqual([list.optionsNow().approvalDateMode, list.draftNow().docDate], ['unmapped', undefined], 'a dropped source column also means no approval date');
  assert.ok(list.commitDisabled() === false);

  // 舊草稿的核准日設定互相矛盾時，確認前就指出要改哪一項（後端仍會拒絕）。
  const contradictions = [
    { approvalDateMode: 'sameAsPostDate', mapping: { ...DUAL, docDate: 'Approve' } },
    { approvalDateMode: 'unmapped', mapping: { ...DUAL, docDate: 'Approve' } },
    { approvalDateMode: 'mapped', mapping: DUAL }
  ];
  for (const old of contradictions) {
    const f = journey();
    f.window.JetUi.applyLoadedProject({ project: { projectId: 'old-draft', currentStep: 2 },
      importState: { gl: { batchId: 'gl-old', columns: GL_COLUMNS, rowCount: 3 } },
      mapping: { gl: { mapping: old.mapping, amountMode: 'dual', approvalDateMode: old.approvalDateMode, committedUtc: '2025-12-31T00:00:00Z' } } },
    { stayOnCurrentStep: true });
    f.press('[data-action="remap-gl"]');
    assert.ok(f.commitDisabled() === true, old.approvalDateMode + ': the contradiction blocks confirming');
    assert.match(f.problems(), /核准日/, old.approvalDateMode + ': the reason names the approval date');
  }
});

// 低嚴重度事項 D18（使用者 2026-10-19 要求處理）：配對完成後一律寫「已確認配對」，時間寫「確認於」，不再寫「完成於」；
// 科目分類在 GL 確認配對前的提示也用同一個說法。
test('mapping: the confirmed summary and the classification gate both say the mapping was confirmed', () => {
  const f = journey();
  f.window.JetUi.applyLoadedProject({ project: { projectId: 'confirmed', currentStep: 2 },
    importState: { gl: { batchId: 'gl-c', columns: GL_COLUMNS, rowCount: 3 } },
    mapping: { gl: { mapping: DUAL, amountMode: 'dual', approvalDateMode: 'unmapped', committedUtc: '2025-12-31T00:00:00Z' } } },
  { stayOnCurrentStep: true });
  const status = f.need('.mapping-summary__status').textContent;
  assert.match(status, /^已確認配對.*確認於 /);
  assert.doesNotMatch(status, /完成於/);
  assert.equal(f.window.JetUi.accountMappingEditor.html(f.store.getState(), false),
    '<p class="rule-card__gate">GL 確認配對後，即可設定科目分類。</p>');
});

test('mapping: cancelling changes restores a separate copy of the confirmed settings', () => {
  const f = journey();
  f.window.JetUi.applyLoadedProject({ project: { projectId: 'restore', currentStep: 2 },
    importState: { gl: { batchId: 'gl-r', columns: GL_COLUMNS, rowCount: 3 } },
    mapping: { gl: { mapping: { ...DUAL, manual: 'Manual' }, amountMode: 'dual', approvalDateMode: 'unmapped',
      manualAutoPolicy: { manualValues: ['1'], automaticValues: ['0'], blankValueKind: 'reject' }, committedUtc: '2025-12-31T00:00:00Z' } } },
  { stayOnCurrentStep: true });
  const confirmed = plain(f.store.getState().mapping.gl.committed.options);
  const summary = () => f.need('[data-bind="gl-committed-options"]').textContent;
  assert.match(summary(), /先補齊/);
  for (const blank of ['automatic', 'manual']) {
    f.press('[data-action="remap-gl"]');
    f.choose('[data-manual-blank]', blank);
    assert.ok(f.$('[data-bind="mapping-banner-gl"]') !== null && /修改/.test(f.need('[data-bind="mapping-banner-gl"]').textContent),
      blank + ': the edit is shown as a change to the confirmed mapping');
    f.press('[data-action="restore-gl"]');
    assert.ok(f.$('[data-action="remap-gl"]') !== null, blank + ': cancelling returns to the summary');
    assert.match(summary(), /先補齊/, blank + ': the summary still shows the confirmed blank handling');
    assert.deepEqual(plain(f.store.getState().mapping.gl.committed.options), confirmed, blank + ': the confirmed settings are untouched');
    assert.deepEqual(f.optionsNow(), confirmed);
  }
});

test('mapping: a failed confirm is shown in place, and typing afterwards marks it as changed without rebuilding', async () => {
  const f = journey({ mode: 'side', draft: { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', amount: 'Amount', dcField: 'Side', dcDebitCode: 'D' } });
  f.press('[data-action="commit-gl"]');
  // 後端沒有附原因：使用者 2026-10-13 第二輪勾選頁確認的 M1（final-wording.json 編號 M1）。
  f.last('mappingCommitGl').reject(Object.assign(new Error(''), { code: 'mapping_failed' }));
  await settleAll();
  const error = () => f.need('[data-bind="mapping-commit-error-gl"]');
  assert.ok(error().textContent.includes('確認配對未完成，請再試一次。'));
  assert.equal(error().getAttribute('role'), 'alert');
  // 使用者 2026-10-13「失敗提示」選項說明：已經有就地錯誤框的操作，例如確認欄位配對，維持原樣，不另外顯示步驟提示框。
  assert.ok(f.store.getState().operationFailure === null, 'no extra step notice for a mapping failure');

  f.press('[data-action="commit-gl"]');
  f.last('mappingCommitGl').reject(Object.assign(new Error('有 2 列的值無法讀取，本次配對未儲存。 第 3 列：借貸別是空白。'), {
    details: [{ sourceColumn: 'Side', reasonCode: 'dc_blank', message: '第 3 列：借貸別是空白。' }] }));
  await settleAll();
  // 第二遍第 6 批裁定：修改設定後保留上次各組錯誤與「前往設定」，改標成需要重新確認，不整份清掉。
  // 第一次失敗收據：20261004-081808251-028419be00a44696b3d2d8e01e72927f。第 0 批核對時沒有測試在確認失敗後用打字修改設定，這裡補上。
  assert.ok(f.$('[data-bind="mapping-commit-error-gl"] [data-focus-mapping-field="dcField"]') !== null, 'the problem has a jump to its setting');
  assert.ok(!/已修改/.test(error().textContent));
  const commit = f.need('[data-action="commit-gl"]');
  const version = f.contentVersion();
  f.type('input[data-mapping-key="dcDebitCode"]', 'DR');
  assert.match(error().textContent, /已修改/, 'typing marks the shown problems as from before the change');
  assert.match(error().textContent, /重新確認/);
  assert.ok(error().textContent.includes('第 3 列'), 'the last problems stay visible');
  assert.equal(f.contentVersion(), version, 'typing does not rebuild the step');
  assert.ok(f.need('[data-action="commit-gl"]') === commit, 'the confirm button is the same node');
});

/* ---- 情境 6：第三步重繪、焦點、捲動與連續輸入 ------------------------------------ */

test('redraw: choosing a column redraws once, updates the required list and confirm, and keeps focus on the new select, also after remap', async () => {
  // 草稿餵給必填清單、「確認配對」能不能按與過帳等設定區，選欄後一定要重繪。舊補丁「輸入框或已確認才重繪」讓全新草稿的下拉選欄
  // 不重繪（右側必填檢查延遲），不得復發，所以全新草稿與按「重新配對」之後兩輪都測。
  const f = journey();
  const order = Object.entries(DUAL);
  for (const [index, [key, column]] of order.entries()) {
    const version = f.contentVersion();
    const old = f.choose('select[data-mapping-key="' + key + '"]', column);
    assert.equal(f.contentVersion(), version + 1, key + ': a fresh draft redraws on every choice');
    const fresh = f.need('select[data-mapping-key="' + key + '"]');
    assert.ok(fresh !== old, key + ': the step was rebuilt');
    assert.ok(f.dom.document.activeElement === fresh, key + ': focus moved to the rebuilt select');
    assert.equal(fresh.value, column);
    const pending = f.railItems().filter(item => !item.done).length;
    assert.equal(pending, order.length - index - 1, key + ': the required list follows the draft');
    assert.ok(f.commitDisabled() === (pending > 0), key + ': confirm follows the required list');
  }
  f.press('[data-action="commit-gl"]');
  f.last('mappingCommitGl').resolve(commitReply());
  await settleAll();
  f.timers.run();
  f.press('[data-action="remap-gl"]');
  f.timers.run();
  const version = f.contentVersion();
  f.choose('select[data-mapping-key="accName"]', '');
  assert.equal(f.contentVersion(), version + 1, 'after remap a choice still redraws');
  assert.ok(f.dom.document.activeElement === f.need('select[data-mapping-key="accName"]'));
  assert.deepEqual(f.railItems().filter(item => !item.done).map(item => item.label), ['會計科目名稱']);
  assert.ok(f.commitDisabled() === true);
  assert.match(f.need('[data-bind="mapping-banner-gl"]').textContent, /修改/);

  // 對照表格的表頭下拉也一樣。
  const grid = journey({ ui: 'grid' });
  const version2 = grid.contentVersion();
  const old = grid.choose('select[data-map-col="Doc"]', 'docNum');
  assert.equal(grid.contentVersion(), version2 + 1);
  const fresh = grid.need('select[data-map-col="Doc"]');
  assert.ok(fresh !== old && grid.dom.document.activeElement === fresh);
  assert.ok(grid.railItems().find(item => item.label === '傳票號碼').done);
});

test('redraw: the grid and list keep their scroll positions when a column is chosen', () => {
  // 捲動位置由 renderContent 依 data-preserve-scroll 統一還原，第三步不再自己暫存。
  const grid = journey({ ui: 'grid', tb: TB_COLUMNS });
  grid.need('.map-grid-wrap').scrollLeft = 300;
  grid.need('.map-grid-wrap', 'tb').scrollLeft = 80;
  const old = grid.need('.map-grid-wrap');
  grid.choose('select[data-map-col="Doc"]', 'docNum');
  assert.ok(grid.need('.map-grid-wrap') !== old, 'the grid was rebuilt');
  assert.equal(grid.need('.map-grid-wrap').scrollLeft, 300, 'GL grid keeps its horizontal position');
  assert.equal(grid.need('.map-grid-wrap', 'tb').scrollLeft, 80, 'TB grid keeps its horizontal position');

  const list = journey();
  list.need('.mapping-table-wrap').scrollTop = 120;
  const before = list.need('.mapping-table-wrap');
  list.choose('select[data-mapping-key="docNum"]', 'Doc');
  assert.ok(list.need('.mapping-table-wrap') !== before);
  assert.equal(list.need('.mapping-table-wrap').scrollTop, 120, 'the list keeps its vertical position');
});

test('redraw: focus goes back only to a control that is still the only one of its kind', async () => {
  // 2026-10-13 起重繪後也放回沒標 data-focus-key 的控制項（使用者 2026-10-13 交接的「重繪後鍵盤焦點與展開狀態沒有留住」），
  // 身分在重繪前後都只有一個符合才放回，同名的控制項不猜。原字串測試改版時的第一次失敗收據：
  // 20261013-023526095-82a54d8da59f4ae59b006538d70926fe。文字選取與輸入法組字的還原由 frontend-mapping.test.cjs 的
  // 「content refresh preserves text selection…」等既有測試守住；組字期間換步驟另見本檔的 input method 測試。
  const f = journey({ mode: 'side', draft: { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', amount: 'Amount', dcField: 'Side', dcDebitCode: 'D' } });
  // 沒有 data-focus-key 的確認鈕：畫面上只有一個，重繪後放回新的確認鈕。
  const commit = f.need('[data-action="commit-gl"]');
  commit.focus();
  f.store.touch();
  const fresh = f.need('[data-action="commit-gl"]');
  assert.ok(fresh !== commit && f.dom.document.activeElement === fresh, 'the only confirm button gets focus back');

  // 確認失敗、同一個來源欄有兩組問題時，有兩個一樣的「前往設定」；焦點在其中一個時，重繪後不猜是哪一個。
  const twoProblems = () => Object.assign(new Error('有 2 列的值無法讀取。 第 3 列：A 第 4 列：B'), {
    details: [{ sourceColumn: 'Amount', reasonCode: 'invalid_amount', message: '第 3 列：A' }, { sourceColumn: 'Amount', reasonCode: 'invalid_amount', message: '第 4 列：B' }] });
  f.press('[data-action="commit-gl"]');
  f.last('mappingCommitGl').reject(twoProblems());
  await settleAll();
  const jumps = f.$$('[data-bind="mapping-commit-error-gl"] [data-focus-mapping-field]');
  assert.equal(jumps.length, 2);
  jumps[0].focus();
  f.store.touch();
  const after = f.$$('[data-bind="mapping-commit-error-gl"] [data-focus-mapping-field]');
  assert.equal(after.length, 2);
  assert.ok(!after.includes(f.dom.document.activeElement), 'two identical buttons: focus is not guessed');

  // 問題只有一組時「前往設定」是唯一的；等待下一次確認時焦點停在它上面，回來兩組一樣的問題，也不猜。
  const g = journey({ mode: 'side', draft: { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', amount: 'Amount', dcField: 'Side', dcDebitCode: 'D' } });
  g.press('[data-action="commit-gl"]');
  g.last('mappingCommitGl').reject(Object.assign(new Error('有 1 列的值無法讀取。 第 3 列：A'), {
    details: [{ sourceColumn: 'Amount', reasonCode: 'invalid_amount', message: '第 3 列：A' }] }));
  await settleAll();
  g.press('[data-action="commit-gl"]');
  const single = g.need('[data-bind="mapping-commit-error-gl"] [data-focus-mapping-field]');
  single.focus();
  g.last('mappingCommitGl').reject(twoProblems());
  await settleAll();
  const now = g.$$('[data-bind="mapping-commit-error-gl"] [data-focus-mapping-field]');
  assert.equal(now.length, 2);
  assert.ok(!now.includes(g.dom.document.activeElement), 'a control that became one of two identical ones is not guessed');

  // 反過來：焦點在兩個一樣的「前往設定」其中一個，等待時回來只剩一組問題。重繪前就分不出是哪一個，重繪後也不交給剩下的那一個。
  const h = journey({ mode: 'side', draft: SIDE_DONE });
  h.press('[data-action="commit-gl"]');
  h.last('mappingCommitGl').reject(twoProblems());
  await settleAll();
  h.press('[data-action="commit-gl"]');
  const pair = h.$$('[data-bind="mapping-commit-error-gl"] [data-focus-mapping-field]');
  assert.equal(pair.length, 2);
  pair[0].focus();
  h.last('mappingCommitGl').reject(Object.assign(new Error('有 1 列的值無法讀取。 第 3 列：A'), {
    details: [{ sourceColumn: 'Amount', reasonCode: 'invalid_amount', message: '第 3 列：A' }] }));
  await settleAll();
  const remaining = h.$$('[data-bind="mapping-commit-error-gl"] [data-focus-mapping-field]');
  assert.equal(remaining.length, 1);
  assert.ok(h.dom.document.activeElement !== remaining[0], 'focus on one of two identical buttons is not handed to the one left');
});

test('redraw: switching between typing codes and choosing source values keeps focus on the switch button', async () => {
  // 切換按鈕的動作與文字會跟著換，焦點仍要回到同一個按鈕（重繪後由 data-focus-key 認出它）。
  const f = journey({ draft: { ...DUAL, manual: 'Manual' } });
  f.press('[data-action="load-manual-profile"]');
  f.last('mappingValueProfile').resolve({ blankCount: 0, distinctCount: 2, truncated: false, values: [{ value: '1', count: 3 }, { value: '0', count: 4 }],
    comparisonGroups: [['1'], ['0']], missingComparisonValues: [] });
  await settleAll();
  // 先跑完開場時排進佇列的焦點安排（步驟標題），之後的焦點變化才是切換造成的。
  f.timers.run();
  const switcher = () => f.need('.map-options__entry-tools button');
  const labels = [];
  for (let round = 0; round < 2; round++) {
    const before = switcher();
    labels.push(before.textContent);
    before.focus();
    before.dispatch('click');
    f.timers.run();
    const after = switcher();
    assert.ok(after !== before, 'round ' + round + ': the block was redrawn');
    assert.notEqual(after.textContent, before.textContent, 'round ' + round + ': the button now offers the other way');
    assert.ok(f.dom.document.activeElement === after, 'round ' + round + ': focus stays on the switch button');
  }
  assert.ok(labels.some(label => /輸入/.test(label)) && labels.some(label => /來源值/.test(label)), 'the two ways: ' + labels.join('、'));
});

test('redraw: clearing a field name blocks confirm with a reason in place, and naming it again unblocks it', () => {
  // 攸關資料元素名稱是連續輸入的例外：名稱是否空白會改變確認鈕與待補提示，輸入時就地更新，不重建整步。句子只比對意思。
  const f = journey({ mode: 'side', draft: SIDE_DONE, options: { rdeFields: [{ sourceColumn: 'Dept', label: 'Dept', valueType: 'text' }] } });
  const commit = f.need('[data-action="commit-gl"]');
  const input = f.need('[data-rde-label="Dept"]');
  const version = f.contentVersion();
  assert.ok(commit.disabled === false);
  f.type('[data-rde-label="Dept"]', '  ');
  assert.ok(commit.disabled === true, 'a blank name blocks confirming at once');
  assert.match(f.problems(), /名稱/, 'the notice says a name is missing');
  f.type('[data-rde-label="Dept"]', '部門');
  assert.ok(commit.disabled === false, 'a name unblocks confirming');
  assert.equal(f.problems(), '');
  assert.equal(f.contentVersion(), version, 'typing does not rebuild the step');
  assert.ok(f.need('[data-rde-label="Dept"]') === input && f.need('[data-action="commit-gl"]') === commit);
});

test('redraw: typing a debit code or a field name and pressing confirm without leaving the box sends exactly once', async () => {
  // 2026-10-02 W3：借方代碼輸入框改值後直接用滑鼠點確認鈕，第一次就要送出。原本失焦後延遲重建整步，會在按下與放開之間換掉確認鈕。
  // 攸關資料元素名稱改成輸入時就地更新的第一次失敗收據：20261003-020747009-ee4dd9fbeb694d50bc7947d8930b79df。
  // 就地更新必填清單與提示多帶第三步識別時的第一次失敗收據：20261012-120854522-9077f25bbd704735a207ea7f954e02b5、
  // 20261012-120943218-2a02481dd9254d92b981b7d8d67a6816。
  for (const target of ['literal', 'rde']) {
    const f = journey({ mode: 'side', draft: { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', amount: 'Amount', dcField: 'Side', dcDebitCode: 'D' },
      options: { rdeFields: [{ sourceColumn: 'Dept', label: 'Dept', valueType: 'text' }] } });
    const selector = target === 'literal' ? 'input[data-mapping-key="dcCreditCode"]' : '[data-rde-label="Dept"]';
    const commit = f.need('[data-action="commit-gl"]');
    const version = f.contentVersion();
    const input = f.need(selector);
    input.focus();
    for (const text of [' C', ' Cr', ' Cr ']) { input.value = text; input.dispatch('input'); }
    // 滑鼠按下確認鈕時輸入框先失焦，接著 change；中間不能重建整步。
    for (const type of ['blur', 'focusout', 'change']) input.dispatch(type);
    f.timers.run();
    assert.equal(f.contentVersion(), version, target + ': continuous typing and leaving the box do not rebuild the step');
    assert.ok(f.need('[data-action="commit-gl"]') === commit && commit.isConnected, target + ': the confirm button is the same node on screen');
    assert.ok(f.need(selector) === input, target + ': the input is the same node');
    if (target === 'literal') assert.equal(input.value, 'Cr', 'change trims the shown value to what the draft keeps');
    commit.dispatch('click');
    await settleAll();
    assert.equal(f.sent('mappingCommitGl').length, 1, target + ': one click sends one confirm');
    const payload = plain(f.last('mappingCommitGl').payload);
    if (target === 'literal') assert.equal(payload.mapping.dcCreditCode, 'Cr');
    else assert.deepEqual(payload.rdeFields, [{ sourceColumn: 'Dept', label: 'Cr', valueType: 'text' }]);
  }
});

test('redraw: typing a code after remap shows and hides the changed-mapping notice in place', async () => {
  const f = journey({ mode: 'side', draft: SIDE_DONE });
  f.press('[data-action="commit-gl"]');
  f.last('mappingCommitGl').resolve(commitReply());
  await settleAll();
  f.timers.run();
  f.press('[data-action="remap-gl"]');
  f.timers.run();
  assert.ok(f.$('[data-bind="mapping-banner-gl"]') === null, 'nothing has changed yet');
  const commit = f.need('[data-action="commit-gl"]');
  const input = f.need('input[data-mapping-key="dcDebitCode"]');
  const version = f.contentVersion();
  f.type('input[data-mapping-key="dcDebitCode"]', 'DR');
  assert.match(f.need('[data-bind="mapping-banner-gl"]').textContent, /修改/, 'the notice appears while typing');
  assert.ok(commit.disabled === false && f.need('[data-action="commit-gl"]') === commit, 'the confirm button stays the same node');
  f.type('input[data-mapping-key="dcDebitCode"]', 'D');
  assert.ok(f.$('[data-bind="mapping-banner-gl"]') === null, 'typing the confirmed code back removes the notice');
  assert.equal(f.contentVersion(), version, 'no rebuild while typing');
  assert.ok(f.need('input[data-mapping-key="dcDebitCode"]') === input);
});

test('redraw: while an input method is composing, background updates wait, but changing step does not', () => {
  // 組字期間的背景結果等組字結束才畫（frontend-mapping.test.cjs 另測換案件與失去焦點）；換步驟時不等，直接畫新的一步。
  const options = { rdeFields: [{ sourceColumn: 'Dept', label: 'Dept', valueType: 'text' }] };
  const f = journey({ mode: 'side', draft: SIDE_DONE, options });
  const input = f.need('[data-rde-label="Dept"]');
  input.focus();
  input.dispatch('compositionstart');
  f.store.touch();
  assert.ok(f.need('[data-rde-label="Dept"]') === input, 'a background update does not replace the composing input');
  input.dispatch('compositionend');
  assert.ok(f.need('[data-rde-label="Dept"]') === input, 'the last input event runs before rebuilding');
  f.timers.run();
  assert.ok(f.need('[data-rde-label="Dept"]') !== input, 'the waiting update is drawn after composition ends');

  const g = journey({ mode: 'side', draft: SIDE_DONE, options });
  const composing = g.need('[data-rde-label="Dept"]');
  composing.focus();
  composing.dispatch('compositionstart');
  // 第四步的統計列會量版面；vm 沒有版面，給一個沒有欄位設定的樣式。
  g.window.getComputedStyle = () => ({ gridTemplateColumns: 'none' });
  g.store.setLastRun('validate', null);
  g.store.setStepIndex(g.store.STEPS.findIndex(step => step.id === 'validate'));
  assert.ok(g.section() === null, 'the next step is drawn even while composing');
});

test('redraw: moving to the next step starts at its top and does not bring back step three focus', () => {
  const f = journey({ draft: { docNum: 'Doc' } });
  f.dom.content.scrollTop = 400;
  f.need('select[data-mapping-key="docNum"]').focus();
  f.window.getComputedStyle = () => ({ gridTemplateColumns: 'none' });
  // 清掉第四步結果本身會重繪第三步，這次焦點與捲動照常放回；之後換步驟才是要看的重繪。
  f.store.setLastRun('validate', null);
  assert.equal(f.dom.content.scrollTop, 400, 'a redraw inside step three keeps the page position');
  const focused = focusHistory(f);
  f.store.setStepIndex(f.store.STEPS.findIndex(step => step.id === 'validate'));
  f.timers.run();
  assert.equal(f.dom.content.scrollTop, 0, 'a new step starts at the top');
  assert.ok(!focused.some(node => node.getAttribute('data-mapping-key') !== null), 'no step three control gets focus back');
  const active = f.dom.document.activeElement;
  assert.ok(active !== null && active.getAttribute('data-bind') === 'current-step-title', 'the new step title takes focus');
});

// 使用者 2026-10-15 反映換步驟時「點下去的時候我會預期元素開始移動，但他會頓一下才開始移動」。
// 原因之一是中欄在第一格就從原本的捲動位置一下跳到頂部，接著才開始收放。可以播動態時，第一格停在原位，
// 之後每一格朝頂部捲，一路不回頭；新步驟的標題照樣拿到焦點。
// 第一次失敗：FrontendMapping 20261015-154041325-f772787e8a3140dd8bbf251f9e2cee27，第一格就跳到 0。
test('redraw: switching steps from a scrolled page glides back to the top instead of jumping', () => {
  const f = mappingRenderFixture({ clock: true });
  const frames = [];
  f.window.requestAnimationFrame = callback => { frames.push(callback); return frames.length; };
  f.window.cancelAnimationFrame = () => {};
  f.window.getComputedStyle = () => ({ gridTemplateColumns: 'none' });
  f.store.setLastRun('validate', null);
  f.dom.content.scrollTop = 400;
  f.store.setStepIndex(f.store.STEPS.findIndex(step => step.id === 'validate'));
  assert.equal(f.dom.content.scrollTop, 400, 'the first frame keeps the page where it was');
  f.timers.run();
  const active = f.dom.document.activeElement;
  assert.ok(active !== null && active.getAttribute('data-bind') === 'current-step-title', 'the new step title takes focus');
  const path = [];
  for (let i = 0; i < 60 && frames.length; i++) { f.clock.advance(16); frames.shift()(); path.push(f.dom.content.scrollTop); }
  assert.ok(path.length > 3, 'the page scrolls over several frames');
  assert.ok(path.every((value, i) => value <= (i ? path[i - 1] : 400)), 'the page only moves toward the top: ' + path.join(','));
  assert.equal(path.at(-1), 0, 'the new step ends at its top');
});

// 2026-10-16 提交前複審找到捲回頂部的兩個漏洞：途中同一步重繪時，移焦點沒有帶 preventScroll，標題在下方就會瞬間捲過去；
// 內容變短時瀏覽器把捲軸夾到新的底部，被當成使用者自己捲而停下，可能停在半途。兩種情況都要一路捲到頂部。
test('redraw: redrawing the same step or shortening it while gliding home keeps gliding to the top', () => {
  const f = mappingRenderFixture({ clock: true });
  const frames = [];
  f.window.requestAnimationFrame = callback => { frames.push(callback); return frames.length; };
  f.window.cancelAnimationFrame = () => {};
  f.window.getComputedStyle = () => ({ gridTemplateColumns: 'none' });
  f.store.setLastRun('validate', null);
  f.dom.content.scrollTop = 400;
  f.store.setStepIndex(f.store.STEPS.findIndex(step => step.id === 'validate'));
  f.timers.run();
  const path = [];
  for (let i = 0; i < 2; i++) { f.clock.advance(16); frames.shift()(); path.push(f.dom.content.scrollTop); }
  f.store.touch();
  f.timers.run();
  const title = f.dom.document.activeElement;
  assert.ok(title !== null && title.getAttribute('data-bind') === 'current-step-title', 'the title keeps focus after the redraw');
  assert.equal(title.lastFocusOptions && title.lastFocusOptions.preventScroll, true, 'focusing the title does not scroll the page by itself');
  // 內容變短：和瀏覽器一樣，捲動位置超過新的底部時夾到底部。
  const content = f.dom.content;
  let top = content.scrollTop;
  content.clientHeight = 300;
  content.scrollHeight = 300 + Math.floor(path.at(-1) / 2);
  Object.defineProperty(content, 'scrollTop', { configurable: true,
    get: () => top, set: value => { top = Math.max(0, Math.min(value, content.scrollHeight - content.clientHeight)); } });
  content.scrollTop = top;
  path.push(content.scrollTop);
  for (let i = 0; i < 60 && frames.length; i++) { f.clock.advance(16); frames.shift()(); path.push(f.dom.content.scrollTop); }
  assert.ok(path.every((value, i) => value <= (i ? path[i - 1] : 400)), 'the page only moves toward the top: ' + path.join(','));
  assert.equal(path.at(-1), 0, 'the glide still ends at the top');
});

test('redraw: after typing replaces the required list, its jump buttons still move focus to the matching select', () => {
  const f = journey({ ui: 'grid', mode: 'side', draft: { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', amount: 'Amount', dcField: 'Side' } });
  const oldRail = f.need('.map-rail');
  f.type('input[data-mapping-key="dcDebitCode"]', 'D');
  assert.ok(f.need('.map-rail') !== oldRail, 'typing replaced the required list in place');
  assert.match(f.need('.map-rail__title').textContent, /皆已/, 'the list title follows the draft');
  const jump = f.need('[data-focus-mapping-field="accName"]');
  jump.dispatch('click');
  f.timers.run();
  const select = f.need('select[data-mapping-key="accName"]');
  assert.equal(f.store.getState().mappingUiMode, 'classic', 'jumping opens the list where every field has its own select');
  assert.ok(f.dom.document.activeElement === select, 'focus lands on the matching select');

  // 按下後還沒定位就換了案件：不把焦點放到新案件的畫面。之後的步驟標題會接手焦點，所以記下每一次焦點落在哪裡。
  const g = journey({ draft: { docNum: 'Doc' } });
  g.need('[data-focus-mapping-field="postDate"]').dispatch('click');
  g.store.setProject({ projectId: 'another-case', periodStart: '2025-01-01', periodEnd: '2025-12-31' });
  g.store.setImportResult('gl', { batchId: 'gl-other', columns: GL_COLUMNS, rowCount: 3 });
  const focusedInG = focusHistory(g);
  g.timers.run();
  assert.ok(!focusedInG.some(node => node.getAttribute('data-mapping-key') === 'postDate'), 'focus does not move into another case');
  // 換到第四步之後也不定位。
  const h = journey({ draft: { docNum: 'Doc' } });
  h.need('[data-focus-mapping-field="postDate"]').dispatch('click');
  // 第四步的統計列會量版面；vm 沒有版面，給一個沒有欄位設定的樣式。
  h.window.getComputedStyle = () => ({ gridTemplateColumns: 'none' });
  h.store.setLastRun('validate', null);
  h.store.setStepIndex(h.store.STEPS.findIndex(step => step.id === 'validate'));
  const focusedInH = focusHistory(h);
  h.timers.run();
  assert.ok(!focusedInH.some(node => node.getAttribute('data-mapping-key') === 'postDate'), 'focus is not sent back to step three');
});

test('redraw: source columns with quotes stay whole in both views and are chosen as written', () => {
  const columns = ['Doc', 'Say "hi"', "It's", 'x" data-evil="1'];
  for (const ui of ['grid', 'classic']) {
    const f = journey({ ui, columns, serialization: 'browser' });
    assert.equal(f.dom.content.querySelectorAll('[data-evil]').length, 0, ui + ': no element picks up an injected attribute');
    if (ui === 'grid') {
      assert.deepEqual(f.$$('select[data-map-col]').map(select => select.getAttribute('data-map-col')), columns);
      for (const column of columns) {
        const select = f.$$('select[data-map-col]').find(node => node.getAttribute('data-map-col') === column);
        select.focus(); select.value = 'description'; select.dispatch('change');
        assert.equal(f.draftNow().description, column, ui + ': the draft keeps the column as written');      }
    } else {
      const options = f.need('select[data-mapping-key="description"]').querySelectorAll('option').map(option => option.getAttribute('value')).filter(Boolean);
      assert.deepEqual(options, columns);
      for (const column of columns) {
        f.choose('select[data-mapping-key="description"]', column);
        assert.equal(f.draftNow().description, column, ui + ': the draft keeps ' + column);
        assert.equal(f.need('select[data-mapping-key="description"]').value, column, ui + ': the rebuilt select shows ' + column);
      }
    }
    assert.equal(f.dom.content.querySelectorAll('[data-evil]').length, 0);
  }
});

test('redraw: after a new import, the old grid preview is never shown whether it succeeds or fails', async () => {
  // FrontendHardeningContractTests.LatestResponseGuard_… 的第三步部分：GL 與 TB 的表格預覽各自只收最新一次讀取的回應。
  // GL 與 TB 同時讀取時，一邊的讀取不讓另一邊的回應作廢：兩個表格都顯示自己的列。
  {
    const f = journey({ ui: 'grid', tb: TB_COLUMNS });
    const glRead = f.sent('queryDataPreview').filter(request => request.payload.dataset === 'pbcGl').at(-1);
    const tbRead = f.sent('queryDataPreview').filter(request => request.payload.dataset === 'pbcTb').at(-1);
    assert.ok(glRead !== undefined && tbRead !== undefined, 'both grids read their preview');
    glRead.resolve({ columns: GL_COLUMNS, rows: [GL_COLUMNS.map(() => 'GL-ROW')], totalCount: 1 });
    tbRead.resolve({ columns: TB_COLUMNS, rows: [TB_COLUMNS.map(() => 'TB-ROW')], totalCount: 1 });
    await settleAll();
    assert.ok(f.need('.map-grid').textContent.includes('GL-ROW'), 'the GL grid shows its rows');
    assert.ok(f.need('.map-grid', 'tb').textContent.includes('TB-ROW'), 'the TB grid shows its rows');
  }
  for (const kind of ['gl', 'tb']) {
    const columns = kind === 'gl' ? GL_COLUMNS : TB_COLUMNS;
    const dataset = kind === 'gl' ? 'pbcGl' : 'pbcTb';
    const reads = f => f.sent('queryDataPreview').filter(request => request.payload.dataset === dataset);
    for (const outcome of ['resolve', 'reject']) {
      const label = kind + ' ' + outcome;
      const f = journey({ ui: 'grid', tb: TB_COLUMNS });
      const old = reads(f).at(-1);
      assert.ok(old !== undefined, label + ': the grid reads its preview');
      assert.deepEqual(plain(old.payload), { dataset, limit: 200, offset: 0 });
      f.store.setImportResult(kind, { batchId: kind + '-2', columns, rowCount: 5 });
      const fresh = reads(f).at(-1);
      assert.ok(fresh !== old, label + ': the new import is read again');
      if (outcome === 'resolve') old.resolve({ columns, rows: [columns.map(() => 'OLD-ROW')], totalCount: 1 });
      else old.reject(new Error('synthetic old failure'));
      await settleAll();
      const grid = f.need('.map-grid', kind);
      assert.ok(!grid.textContent.includes('OLD-ROW'), label + ': old rows are not shown');
      assert.ok(!/失敗/.test(grid.textContent), label + ': an old failure is not shown');
      assert.match(grid.textContent, /載入/, label + ': the grid is still waiting for the new import');
      fresh.resolve({ columns, rows: [columns.map(() => 'NEW-ROW')], totalCount: 5 });
      await settleAll();
      assert.ok(f.need('.map-grid', kind).textContent.includes('NEW-ROW'));
    }
  }

  // 表格讀取中換到簡易清單，這時重新匯入（簡易清單不讀表格預覽），舊的回應才到：換回表格時要重新讀，不顯示舊匯入的列。
  for (const outcome of ['resolve', 'reject']) {
    const f = journey({ ui: 'grid' });
    const old = f.last('queryDataPreview');
    f.store.setMappingUiMode('classic');
    f.store.setImportResult('gl', { batchId: 'gl-2', columns: GL_COLUMNS, rowCount: 5 });
    if (outcome === 'resolve') old.resolve({ columns: GL_COLUMNS, rows: [GL_COLUMNS.map(() => 'OLD-IMPORT')], totalCount: 1 });
    else old.reject(new Error('synthetic old failure'));
    await settleAll();
    const before = f.sent('queryDataPreview').length;
    f.store.setMappingUiMode('grid');
    assert.equal(f.sent('queryDataPreview').length, before + 1, outcome + ': the new import is read when the grid comes back');
    assert.ok(!f.need('.map-grid').textContent.includes('OLD-IMPORT'), outcome + ': rows of the old import are not shown');
    assert.ok(!/失敗/.test(f.need('.map-grid').textContent), outcome + ': a failure of the old import is not shown');
  }

  // 重新開啟同一個案件、同一批匯入時，開案前發出的讀取也不算數：換到表格時重新讀，不顯示開案前的回應。
  const f = journey({ ui: 'grid' });
  const old = f.last('queryDataPreview');
  f.store.setMappingUiMode('classic');
  f.window.JetUi.applyLoadedProject({ project: { projectId: 'usage-synthetic', periodStart: '2025-01-01', periodEnd: '2025-11-30', currentStep: 2 },
    importState: { gl: { batchId: 'gl-1', columns: GL_COLUMNS, rowCount: 3 } } }, { stayOnCurrentStep: true });
  old.resolve({ columns: GL_COLUMNS, rows: [GL_COLUMNS.map(() => 'BEFORE-REOPEN')], totalCount: 1 });
  await settleAll();
  const before = f.sent('queryDataPreview').length;
  f.store.setMappingUiMode('grid');
  assert.equal(f.sent('queryDataPreview').length, before + 1, 'the reopened case reads its preview again');
  assert.ok(!f.need('.map-grid').textContent.includes('BEFORE-REOPEN'), 'a reply from before reopening is not shown');
});

/* ---- 第 10 批補：確認欄位配對後訊息面板的句子（收尾四項第一項第 10 批，2026-10-14） ---------------- */
// 第 7 批刻意不斷言訊息面板，留給第 10 批在文字確認並改進產品後補上。逐字比對的句子都寫出 final-wording.json 的編號；
// 後端提醒與原因一律用合成字串。R3-36（讀取來源值時來源欄已不在匯入資料中）是後端的句子，畫面只照原樣顯示，這裡不比對；
// R3-42 已由文字批次 D 在 frontend-mapping 的「V3 single-side codes…」逐字比對，R3-43 在 frontend-workflow，這裡不重寫；
// R3-47（預覽沒有資料列）列在盤點報告「碰不到的句子」，不另造情境。
const ELAPSED = /耗時(?: \d+\.\d 秒|不到 0\.1 秒|\d+ 分 \d+ 秒)/.source;
const messageList = f => plain(f.store.getState().messages.map(m => ({ text: m.text, level: m.level })));

test('mapping messages: a confirmed mapping logs the row count with the elapsed time, each backend reminder separately, and a failed confirm is an error line', async () => {
  const f = journey({ tb: TB_COLUMNS, mode: 'side', draft: SIDE_DONE });
  f.press('[data-action="commit-gl"]');
  f.last('mappingCommitGl').resolve(commitReply({ projectedRowCount: 12345, warnings: ['合成提醒一。', '合成提醒二。'] }));
  await settleAll();
  const messages = messageList(f);
  // 使用者 2026-10-13 勾選頁確認 38（完成句，維持原句的寫法）與 39（配對提醒，維持原句）（final-wording.json）；
  // 耗時的寫法是同日「保留，改成秒並合併」的裁定與 2026-10-14 第三輪 R3-26。
  assert.match(messages[0].text, new RegExp('^GL 已確認配對，共 12,345 筆，' + ELAPSED + '。$'));
  assert.equal(messages[0].level, 'info');
  assert.deepEqual(messages.slice(1, 3), [{ text: 'GL 配對提醒：合成提醒二。', level: 'warn' }, { text: 'GL 配對提醒：合成提醒一。', level: 'warn' }]);
  assert.equal(messages.filter(m => m.text.includes('已確認配對')).length, 1, 'one completion line');

  // TB 用自己的名稱。
  for (const [key, column] of [['accNum', 'TbAccount'], ['accName', 'TbName'], ['debitAmt', 'TbDebit'], ['creditAmt', 'TbCredit']]) {
    f.choose('select[data-mapping-key="' + key + '"]', column, 'tb');
  }
  f.timers.run();
  f.press('[data-action="commit-tb"]', 'tb');
  f.last('mappingCommitTb').resolve({ projectedRowCount: 7 });
  await settleAll();
  assert.match(messageList(f)[0].text, new RegExp('^TB 已確認配對，共 7 筆，' + ELAPSED + '。$'));

  // 失敗：原因寫在配對區的錯誤框（第 7 批已測），訊息面板留一則錯誤。使用者 2026-10-13 勾選頁確認 19，2026-10-14 第三輪 R3-27。
  f.press('[data-action="remap-gl"]'); f.timers.run();
  f.press('[data-action="commit-gl"]');
  f.last('mappingCommitGl').reject(Object.assign(new Error('合成：配對未通過檢查。'), { code: 'mapping_failed' }));
  await settleAll();
  // 作業名稱沒有使用者裁定，只確認提到 GL 與配對。
  assert.match(messageList(f)[0].text, new RegExp('^[^：]*GL[^：]*配對[^：]*失敗：合成：配對未通過檢查（' + ELAPSED + '）。$'));
  assert.equal(messageList(f)[0].level, 'error');
});

// 2026-10-16：總筆數只在第一頁計算，續頁回 null（docs/action-contract-manifest.md 的 query.dataPreview）；
// 對照表格換頁後仍沿用第一頁的總頁數，不會因為續頁沒有總數就失去頁碼。
test('mapping grid keeps the first page count when the next page arrives without a total', async () => {
  const f = journey({ ui: 'grid', columns: ['Doc'], draft: { docNum: 'Doc' } });
  f.last('queryDataPreview').resolve({ columns: ['Doc'], rows: Array.from({ length: 200 }, (_, i) => ['FIRST-' + (i + 1)]), totalCount: 401 });
  await settleAll();
  assert.equal(f.need('[data-page-position]').textContent, '1/3');
  f.press('[data-source-next]');
  assert.deepEqual(plain(f.last('queryDataPreview').payload), { dataset: 'pbcGl', limit: 200, offset: 200 });
  f.last('queryDataPreview').resolve({ columns: ['Doc'], rows: [['SECOND-1']], totalCount: null, stats: null }); await settleAll();
  assert.equal(f.need('.map-grid tbody').textContent, 'SECOND-1');
  assert.equal(f.need('[data-page-position]').textContent, '2/3');
  assert.equal(f.need('[data-source-next]').disabled, false, 'the third page is still reachable');
});

// 使用者 2026-10-16（圖一）：「GL和TB在欄位配對時的資料表高度至少要大於必填欄位的高度」。對照表格與必填清單放在同一列，
// 分頁列排在表格下方；表格至少 480px，清單較長時表格跟著加高，兩者等高。實際高度只有瀏覽器量得到，這裡核對結構與樣式規則。
test('mapping grid sits beside the required list in one row, with the pager below the table', async () => {
  const f = journey({ ui: 'grid', columns: ['Doc'], draft: { docNum: 'Doc' } });
  f.last('queryDataPreview').resolve({ columns: ['Doc'], rows: Array.from({ length: 200 }, (_, i) => ['ROW-' + (i + 1)]), totalCount: 401 });
  await settleAll();
  const layout = f.need('.map-layout--grid');
  const main = layout.querySelector('.map-layout__main');
  assert.ok(main.querySelector('.map-grid-wrap'), 'the table is in the layout row');
  assert.ok(layout.children.some(node => (node.getAttribute('class') || '').split(/\s+/).includes('map-rail')), 'the required list is beside it');
  const children = main.children.map(node => (node.getAttribute('class') || ''));
  assert.ok(children.indexOf('map-grid-wrap') < children.findIndex(name => name.split(/\s+/).includes('table-pager')), 'the pager follows the table');
  const css = readWwwroot('css/app.css');
  assert.match(css, /\.map-layout--grid > \.map-layout__main \{ display: contents; \}/);
  assert.match(css, /\.map-layout--grid > \.map-layout__main > \.map-grid-wrap \{\s*grid-row: 1;\s*contain: size;\s*min-height: 480px;/);
  assert.match(css, /\.map-layout--grid > \.map-rail \{ grid-column: 2; grid-row: 1; \}/);
});

// 使用者 2026-10-16（圖三）：「按鈕 `預覽` 和 `重新配對` 相隔太遠，短時間比較難察覺到這兩個按鈕的存在」。
test('the confirmed mapping summary keeps preview and remap side by side', async () => {
  const f = journey({ draft: DUAL });
  f.press('[data-action="commit-gl"]');
  f.last('mappingCommitGl').resolve(commitReply()); await settleAll(); f.timers.run();
  const remap = f.need('[data-action="remap-gl"]');
  const actions = remap.parentNode;
  assert.ok(actions.getAttribute('class').split(/\s+/).includes('panel__actions--start'), 'both buttons start from the left');
  assert.deepEqual(actions.children.map(node => node.textContent), ['預覽', '重新配對']);
  assert.equal(actions.querySelector('.panel__action--start'), null, 'no button pushes the other to the far side');
});
