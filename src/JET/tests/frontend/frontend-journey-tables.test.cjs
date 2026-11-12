'use strict';
// 審計員情境測試：明細表與右側資料預覽。
// 資料明細每頁 200 筆，可往前或往後換頁。
//
// 本檔分兩部分，每個測試都載入正式的 wwwroot 腳本，操作畫面後看送出的請求、畫出的節點與 Store 狀態。
//
// 第一部分　情境 20「明細表：翻頁、排序、查號碼與金額」
//   審計員在第四步與第五步的各種明細表點表頭排序、輸入號碼查看、按清除；讀取中又按了兩次查看；讀取失敗；
//   看各處的金額；看各處一頁讀幾筆。
//
// 第二部分　情境 21「右側資料預覽」
//   審計員切換資料表；讀取失敗後重試；讀取途中切到別的資料表；重新匯入或換案件時舊請求晚到；
//   視窗縮窄再拉寬，期間自己收合或展開；用鍵盤操作「其他資料」選單。
//
// 斷言原則：只有定案的畫面用字逐字比對；其他畫面文字只比對意思。
// 預期值一律寫字面值（例如明細每頁 200 筆），不讀正式常數；action payload 精確比對。
// 全部用合成資料。這個檔不碰訊息面板的句子、後端原因句子與第五步的儲存、移除、重新檢查流程。
// 測試程序的記憶體與時間上限，見 support/resource-guard.cjs。
require('./support/resource-guard.cjs');
const { test } = require('node:test');
const assert = require('node:assert/strict');
const F = require('./support/frontend-fixtures.cjs');
const { unchecked } = require('./support/action-contract.cjs');

const { settle, plain } = F;

// ---------------------------------------------------------------------------
// 共用的小工具（只服務本檔，沒有收進支援模組）
// ---------------------------------------------------------------------------

// 第四步：用正式的 validate 步驟畫出驗證與預篩選結果，事件會冒泡，點表頭就走正式的分頁綁定。
function validationJourney(mutate = () => {}) {
  const f = F.usageJourneyFixture('validate', { bubbles: true });
  const data = F.validationResultData();
  data.completenessTest.diffAccountCount = 1;
  data.completenessTest.diffAccounts = [{ accountNumber: '1001', accountName: 'Synthetic cash', tbAmount: 1234.5, glAmount: 1000, diff: 234.5 }];
  data.completenessTest.eligibility = { isEligible: true, reason: null, warning: null };
  data.voucherBalanceTest = { unbalancedVoucherCount: 1, unbalancedVouchers: [{ voucherNumber: 'JV-1', debit: 1234.5, credit: 1000, diff: 234.5 }] };
  data.infSamplingTest = { sampleSize: 3 };
  data.blankRecordsTest.blankDescriptionCount = 1; data.blankRecordsTest.approvalDateNotInPeriodCount = 1;
  data.blankRecordsTest.blankRows.push({ voucherNumber: 'JV-9', accountNumber: '1001', description: '', issues: ['description', 'approvalDate'] });
  mutate(data);
  f.store.setResult('validate', data);
  f.store.setResult('prescreen', Object.assign(F.remapPrescreen('p1'), { weekendActivity: { postingCount: 2, approvalCount: 0 } }));
  const root = f.draw();
  const detailOf = id => { const body = root.querySelector('[data-page-query="' + id + '"]'); assert.ok(body, id + ' paged table rendered'); return body.closest('.rule-detail'); };
  // 找出內含某個節點的明細面板，再找控制它的「檢視」按鈕。
  function toggleFor(selector) {
    const inner = root.querySelector(selector); assert.ok(inner, selector + ' rendered');
    const panel = inner.closest('.rule-detail'); assert.ok(panel, selector + ' sits in a detail panel');
    const toggle = root.querySelectorAll('[data-action="toggle-detail"]').find(button =>
      panel.getAttribute('data-bind') === 'rule-detail-' + button.getAttribute('data-scope') + '-' + button.getAttribute('data-idx'));
    assert.ok(toggle, selector + ' has a toggle'); return toggle;
  }
  const requestsOf = action => f.requests.filter(request => request.action === action);
  return { ...f, data, root, detailOf, toggleFor, requestsOf };
}

// 送出搜尋表單（正式的「查看」按鈕是 submit）。
function submitSearch(scope, text) {
  const form = scope.querySelector('[data-page-search]'); assert.ok(form, 'search form rendered');
  form.querySelector('[data-page-search-input]').value = text;
  form.fire('submit');
}

// 第五步：remapFilterPreviewFixture 再加上高風險條件彙總分頁的請求記錄。
function filterJourney({ saved = [] } = {}) {
  const f = F.remapFilterPreviewFixture();
  for (const action of ['queryHighRiskSummaryScenarios', 'queryHighRiskSummaryVoucherPage', 'queryHighRiskSummaryRowPage'])
    f.window.JetApi[action] = payload => new Promise((resolve, reject) => f.requests.push({ action, payload, resolve, reject }));
  if (saved.length) {
    f.store.setResult('validate', { resultRef: { resultId: 'v1' }, completenessTest: { eligibility: { isEligible: true } } });
    f.store.setSavedScenarios(saved);
    f.store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' });
    f.click('[data-filter-pane-select="saved"]');
  }
  return { ...f, requestsOf: action => f.requests.filter(request => request.action === action) };
}

// columns 是傳票頁帶的欄位資訊；傳票與分錄在同一張表，攸關資料元素的自訂欄由這一頁決定。
async function draftVoucherList(f, rows = [{ voucherNumber: 'SYN-B', glDate: '2025-01-02', hitRowCount: 1, totalRowCount: 2, voucherTotal: 1234.5 },
  { voucherNumber: 'SYN-A', glDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: -10 }], columns = null) {
  f.click('[data-common-card="blankDescription"]'); f.click('[data-action="preview-scenario"]');
  f.request('filterPreview').resolve({ scenario: { count: 2, voucherCount: 2, summaryRowCount: 2, populationScope: 'auditPeriod' } }); await settle();
  const first = f.request('queryFilterVoucherPage');
  first.resolve({ queryRevision: 'q1', rows, nextCursor: 'v-next', ...(columns ? { columns } : {}) }); await settle();
  return first;
}

// ===========================================================================
// 第一部分　情境 20：明細表
// ===========================================================================

// 第四步的分頁明細表：點表頭排序、輸入號碼查看，都把排序與搜尋送給後端並從第一頁重讀。
// 每一列：明細識別標記、後端 action、排序鍵（點第一個可排序表頭）、payload 固定帶的其他鍵。
// 這些表共用同一組合成列，但每張表的後端資料列只有自己的欄位（tests/JET.Tests/Fixtures/action-shapes.json）。
const BACKEND_ROW_FIELDS = {
  queryCompletenessDiffPage: ['accountNumber'],
  queryVoucherBalancePage: ['voucherNumber'],
  queryBlankRecordsPage: ['accountNumber', 'voucherNumber', 'description'],
  querySourceQualityPage: ['accountNumber', 'voucherNumber', 'sourceRowNumber', 'description']
};
function backendRow(action, row) {
  return Object.fromEntries(Object.entries(row).filter(([key]) => BACKEND_ROW_FIELDS[action].includes(key)));
}

const VALIDATION_TABLES = [
  ['completeness', 'queryCompletenessDiffPage', 'accountNumber', {}],
  ['voucherBalance', 'queryVoucherBalancePage', 'voucherNumber', {}],
  ['null-approvalDateNotInPeriod', 'queryBlankRecordsPage', 'voucherNumber', { category: 'approvalDateNotInPeriod' }],
  // 一條規則一個定義：第四步的空白摘要只在預篩選訊號那一處，資料缺漏輔助檢視沒有它的明細頁。
  ['null-blankVoucherNumber', 'queryBlankRecordsPage', 'voucherNumber', { category: 'blankVoucherNumber' }],
  ['null-blankAccountNumber', 'queryBlankRecordsPage', 'voucherNumber', { category: 'blankAccountNumber' }],
  ['sourceQuality', 'querySourceQualityPage', 'sourceRowNumber', {}]
];

for (const [id, action, key, extra] of VALIDATION_TABLES) {
  test('step four ' + id + ' table sorts and searches on the backend from the first page and ignores clicks while reading', async () => {
    const f = validationJourney(), detail = f.detailOf(id), table = detail.querySelector('[data-page-query]').closest('table');
    const sortButton = table.querySelector('.th-sort'); assert.ok(sortButton, 'a sortable header is rendered');
    assert.equal(sortButton.getAttribute('data-sort-key'), key);

    sortButton.fire('click');
    assert.equal(f.requestsOf(action).length, 1);
    assert.deepEqual(plain(f.requestsOf(action)[0].payload), { ...extra, cursor: null, pageSize: 200, sort: { key, direction: 'asc' }, search: null });

    // 讀取中再點表頭、再按查看、再按下一頁，都不送第二個請求（查看會在讀完後才接著做，見下一組測試）。
    sortButton.fire('click'); detail.querySelector('[data-page-next]').fire('click');
    assert.equal(f.requests.length, 1, 'nothing else is sent while the table is reading');

    // 後端回傳的順序原樣顯示，畫面不自己排序。
    f.requestsOf(action)[0].resolve({ rows: [
      backendRow(action, { accountNumber: '2002', voucherNumber: 'JV-B', sourceRowNumber: 9, description: 'later' }),
      backendRow(action, { accountNumber: '1001', voucherNumber: 'JV-A', sourceRowNumber: 1, description: 'earlier' })], nextCursor: 'next-1' });
    await settle();
    const text = detail.querySelector('[data-page-query]').textContent;
    const later = text.indexOf(key === 'accountNumber' ? '2002' : key === 'sourceRowNumber' ? '9' : 'JV-B');
    const earlier = text.indexOf(key === 'accountNumber' ? '1001' : key === 'sourceRowNumber' ? '1' : 'JV-A');
    assert.ok(later >= 0 && earlier > later, 'rows stay in the backend order: ' + text);
    assert.equal(table.querySelector('th[data-sort-key="' + key + '"]').getAttribute('aria-sort'), 'ascending');

    // 輸入號碼查看：保留目前排序，從第一頁重讀。
    submitSearch(detail, '  JV-1  ');
    assert.equal(f.requestsOf(action).length, 2);
    assert.deepEqual(plain(f.requestsOf(action)[1].payload), { ...extra, cursor: null, pageSize: 200, sort: { key, direction: 'asc' }, search: 'JV-1' });
    f.requestsOf(action)[1].resolve({ rows: [backendRow(action, { accountNumber: '1001', voucherNumber: 'JV-1' })], nextCursor: 'next-2' }); await settle();

    // 下一頁沿用同一個排序與搜尋，接著上一頁的游標。
    detail.querySelector('[data-page-next]').fire('click');
    assert.deepEqual(plain(f.requestsOf(action)[2].payload), { ...extra, cursor: 'next-2', pageSize: 200, sort: { key, direction: 'asc' }, search: 'JV-1' });
    f.requestsOf(action)[2].resolve({ rows: [], nextCursor: null }); await settle();

    // 再點同一個表頭改成反向，仍從第一頁重讀。
    sortButton.fire('click');
    assert.deepEqual(plain(f.requestsOf(action)[3].payload), { ...extra, cursor: null, pageSize: 200, sort: { key, direction: 'desc' }, search: 'JV-1' });
    assert.equal(f.store.getState().busy, false, 'reading a page never takes over the whole screen');
  });
}

test('approval dates outside the period load the first 200 rows on opening even when all 7053 findings are absent from the summary sample', async () => {
  const f = validationJourney(data => { data.blankRecordsTest.approvalDateNotInPeriodCount = 7053; data.blankRecordsTest.blankRows = []; });
  const detail = f.detailOf('null-approvalDateNotInPeriod');
  assert.equal(f.requestsOf('queryBlankRecordsPage').length, 0, 'folded details do not read rows');
  f.toggleFor('[data-page-query="null-approvalDateNotInPeriod"]').fire('click');
  assert.deepEqual(plain(f.requestsOf('queryBlankRecordsPage')[0].payload),
    { category: 'approvalDateNotInPeriod', cursor: null, pageSize: 200, sort: null, search: null });
  f.requestsOf('queryBlankRecordsPage')[0].resolve({
    rows: Array.from({ length: 200 }, (_, i) => ({ voucherNumber: 'APPROVAL-' + (i + 1), accountNumber: '1001', description: 'Synthetic' })),
    nextCursor: 'after-200', totalCount: 7053
  }); await settle();
  assert.equal(detail.querySelectorAll('tbody tr').length, 200);
  assert.match(detail.querySelector('tbody').textContent, /APPROVAL-1/);
  assert.equal(detail.querySelector('[data-page-position]').textContent, '1/36');
  assert.equal(detail.querySelector('[data-page-previous]').disabled, true);
  assert.equal(detail.querySelector('[data-page-next]').disabled, false);
  assert.equal(detail.querySelector('.table-pager').hidden, false, 'more than 200 rows shows the pager');
});

// 總筆數只在第一頁計算，帶游標的續頁回 totalCount null。
// 換到第二頁後分頁列仍用第一頁的總筆數算頁數，下一頁照樣可按。
test('a continuation page without a total keeps the first page count on a step four detail table', async () => {
  const f = validationJourney(data => { data.blankRecordsTest.approvalDateNotInPeriodCount = 7053; data.blankRecordsTest.blankRows = []; });
  const detail = f.detailOf('null-approvalDateNotInPeriod');
  f.toggleFor('[data-page-query="null-approvalDateNotInPeriod"]').fire('click');
  const page = start => Array.from({ length: 200 }, (_, i) => ({ voucherNumber: 'APPROVAL-' + (start + i), accountNumber: '1001', description: 'Synthetic' }));
  f.requestsOf('queryBlankRecordsPage')[0].resolve({ rows: page(1), nextCursor: 'after-200', totalCount: 7053 }); await settle();
  detail.querySelector('[data-page-next]').fire('click');
  assert.equal(f.requestsOf('queryBlankRecordsPage')[1].payload.cursor, 'after-200');
  f.requestsOf('queryBlankRecordsPage')[1].resolve({ rows: page(201), nextCursor: 'after-400', totalCount: null }); await settle();
  assert.match(detail.querySelector('tbody').textContent, /APPROVAL-201/);
  assert.equal(detail.querySelector('[data-page-position]').textContent, '2/36');
  assert.equal(detail.querySelector('[data-page-next]').disabled, false);
  assert.equal(detail.querySelector('.table-pager').hidden, false);
});

// 一頁最多 200 筆，超過 200 筆才分頁。只有一頁時不顯示分頁列；讀取完成前也不先顯示空的頁碼。
test('a detail table with at most 200 rows shows no pager, and the pager stays hidden until the first page arrives', async () => {
  const f = validationJourney(data => { data.blankRecordsTest.approvalDateNotInPeriodCount = 150; data.blankRecordsTest.blankRows = []; });
  const detail = f.detailOf('null-approvalDateNotInPeriod');
  f.toggleFor('[data-page-query="null-approvalDateNotInPeriod"]').fire('click');
  assert.equal(detail.querySelector('.table-pager').hidden, true, 'no page numbers before the first page is read');
  f.requestsOf('queryBlankRecordsPage')[0].resolve({
    rows: Array.from({ length: 150 }, (_, i) => ({ voucherNumber: 'SINGLE-' + (i + 1), accountNumber: '1001', description: 'Synthetic' })),
    nextCursor: null, totalCount: 150
  }); await settle();
  assert.equal(detail.querySelectorAll('tbody tr').length, 150);
  assert.equal(detail.querySelector('.table-pager').hidden, true, 'a single page has no pager');
});

test('step four sample details and prescreen hits sort and search on the backend from the first page', async () => {
  const f = validationJourney();

  // 資料可靠性測試的抽樣明細：展開時先讀第一頁，欄位與可否排序由後端決定。
  f.toggleFor('[data-dynamic-page="inf"]').fire('click');
  assert.deepEqual(plain(f.requestsOf('queryInfSamplePage')[0].payload), { cursor: null, pageSize: 200, sort: null, search: null });
  // 正式後端的抽樣明細沒有「金額」欄，借方與貸方分開。
  f.requestsOf('queryInfSamplePage')[0].resolve({ columns: [
    { key: 'voucherNumber', label: '傳票號碼', valueType: 'text', sortable: true },
    { key: 'debit', label: '借方金額', valueType: 'amount', sortable: true }], rows: [{ voucherNumber: 'JV-S', debit: 1 }], nextCursor: 'inf-2' });
  await settle();
  const sample = f.root.querySelector('[data-dynamic-page="inf"]');
  sample.querySelector('.th-sort[data-sort-key="debit"]').fire('click');
  assert.deepEqual(plain(f.requestsOf('queryInfSamplePage')[1].payload), { cursor: null, pageSize: 200, sort: { key: 'debit', direction: 'asc' }, search: null });
  f.requestsOf('queryInfSamplePage')[1].resolve({ columns: [], rows: [], nextCursor: null }); await settle();
  submitSearch(sample, 'JV-S');
  assert.deepEqual(plain(f.requestsOf('queryInfSamplePage')[2].payload), { cursor: null, pageSize: 200, sort: { key: 'debit', direction: 'asc' }, search: 'JV-S' });

  // 預篩選命中：展開時讀前 200 筆（字面值），之後排序與查看由後端從第一頁重讀，一頁 200 筆。
  f.toggleFor('[data-prescreen-key="weekendPosting"]').fire('click');
  assert.deepEqual(plain(f.requestsOf('queryPrescreenPage')[0].payload), { ruleKey: 'weekendPosting', pageSize: 200 });
  f.requestsOf('queryPrescreenPage')[0].resolve({ rows: [{ voucherNumber: 'JV-P', lineItem: '1', glDate: '2025-01-04', accountNumber: '1001',
    accountName: 'Synthetic cash', description: 'weekend', amount: 1234.5, drCr: 'DEBIT' }], nextCursor: 'p-2' });
  await settle();
  const hits = f.root.querySelector('[data-prescreen-key="weekendPosting"]');
  hits.querySelector('.th-sort[data-sort-key="voucherNumber"]').fire('click');
  assert.deepEqual(plain(f.requestsOf('queryPrescreenPage')[1].payload),
    { ruleKey: 'weekendPosting', cursor: null, pageSize: 200, sort: { key: 'voucherNumber', direction: 'asc' }, search: null });
  f.requestsOf('queryPrescreenPage')[1].resolve({ rows: [], nextCursor: null }); await settle();
  submitSearch(hits, 'JV-P');
  assert.deepEqual(plain(f.requestsOf('queryPrescreenPage')[2].payload),
    { ruleKey: 'weekendPosting', cursor: null, pageSize: 200, sort: { key: 'voucherNumber', direction: 'asc' }, search: 'JV-P' });
});

test('step five voucher list sorts, searches and clears on the backend from the first page, two hundred vouchers per page', async () => {
  const f = filterJourney();
  const first = await draftVoucherList(f);
  // 第五步預覽的傳票清單一頁 200 張（字面值）。
  assert.equal(first.payload.pageSize, 200);
  assert.deepEqual(Object.keys(first.payload).sort(), ['pageSize', 'populationScope', 'scenario']);
  const host = f.$('.filter-voucher-host'); assert.ok(host);
  const rowsText = host.querySelector('tbody').textContent;
  assert.ok(rowsText.indexOf('SYN-B') < rowsText.indexOf('SYN-A'), 'vouchers stay in the backend order');

  const base = { populationScope: 'auditPeriod', scenario: plain(first.payload.scenario) };
  host.querySelector('th[data-sort-key="voucherTotal"] .th-sort').onclick();
  assert.deepEqual(plain(f.requestsOf('queryFilterVoucherPage')[1].payload),
    { ...base, cursor: null, pageSize: 200, queryRevision: 'q1', sort: { key: 'voucherTotal', direction: 'asc' }, search: null });
  f.requestsOf('queryFilterVoucherPage')[1].resolve({ queryRevision: 'q1', rows: [{ voucherNumber: 'SYN-A', glDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: -10 }], nextCursor: null });
  await settle();
  assert.equal(host.querySelector('th[data-sort-key="voucherTotal"]').getAttribute('aria-sort'), 'ascending');

  const form = host.querySelector('[data-page-search]');
  form.querySelector('[data-page-search-input]').value = ' SYN ';
  form.onsubmit({ preventDefault() {} });
  assert.deepEqual(plain(f.requestsOf('queryFilterVoucherPage')[2].payload),
    { ...base, cursor: null, pageSize: 200, queryRevision: 'q1', sort: { key: 'voucherTotal', direction: 'asc' }, search: 'SYN' });
  f.requestsOf('queryFilterVoucherPage')[2].resolve({ queryRevision: 'q1', rows: [{ voucherNumber: 'SYN-A', glDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: -10 }], nextCursor: null });
  await settle();

  host.querySelector('[data-page-search-clear]').onclick();
  assert.deepEqual(plain(f.requestsOf('queryFilterVoucherPage')[3].payload),
    { ...base, cursor: null, pageSize: 200, queryRevision: 'q1', sort: { key: 'voucherTotal', direction: 'asc' }, search: null });
});

test('step five saved scenario lists read two hundred vouchers and two hundred entries per page', async () => {
  const f = filterJourney({ saved: [F.savedBlankDescription()] });
  f.click('[data-action="toggle-scenario"][data-index="0"]');
  // 有可用版本時展開只讀一次傳票頁，不先送即時預覽；第一頁帶符合的傳票數與分錄筆數。
  // 傳票與分錄在同一張表，點傳票那一列展開分錄。
  assert.equal(f.requestsOf('filterPreview').length, 0);
  // 已儲存情境的傳票清單一頁 200 張（字面值），帶情境位置與版本。
  assert.deepEqual(plain(f.request('queryFilterVoucherPage').payload), { scenarioPosition: 1, scenarioRevision: 'r1', populationScope: 'auditPeriod', pageSize: 200 });
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [{ voucherNumber: 'SAVED-1', glDate: '2025-01-01', hitRowCount: 1, totalRowCount: 2, voucherTotal: 5 }], nextCursor: null,
    voucherCount: 1, summaryRowCount: 2, countsWholeVoucherRows: false });
  await settle();
  f.$('[data-voucher-row="0"]').onclick();
  // 展開傳票的分錄一頁 200 筆（字面值）。
  assert.deepEqual(plain(f.request('queryFilterVoucherRowsPage').payload),
    { scenarioPosition: 1, scenarioRevision: 'r1', populationScope: 'auditPeriod', voucherNumber: 'SAVED-1', cursor: null, pageSize: 200, queryRevision: 'q1' });
});

// 分錄的傳票文件項次自成一欄，表頭寫名詞表的短表頭「項次」；分錄列只寫數字，傳票列這一格留白。
test('step five voucher table shows each entry line item as a bare number in its own column', async () => {
  const f = filterJourney({ saved: [F.savedBlankDescription()] });
  f.click('[data-action="toggle-scenario"][data-index="0"]');
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [{ voucherNumber: 'SAVED-1', glDate: '2025-01-01', hitRowCount: 1, totalRowCount: 2, voucherTotal: 5 }], nextCursor: null,
    voucherCount: 1, summaryRowCount: 2, countsWholeVoucherRows: false });
  await settle();
  f.$('[data-voucher-row="0"]').onclick();
  f.request('queryFilterVoucherRowsPage').resolve({ queryRevision: 'q1', nextCursor: null,
    rows: [{ lineItem: '1', isHit: true, amount: 5 }, { lineItem: '2', isHit: false, amount: -5 }] });
  await settle();
  const table = f.$('.filter-voucher-host table.voucher-table');
  const head = table.querySelectorAll('thead th').map(th => th.textContent);
  assert.deepEqual(head.slice(1, 4), ['傳票號碼', '項次', '總帳日期']);
  const group = table.querySelectorAll('tr.voucher-table__group td').map(td => td.textContent);
  assert.equal(group[1], 'SAVED-1'); assert.equal(group[2], '');
  const entries = table.querySelectorAll('tr.voucher-table__entry').map(row => row.querySelectorAll('td').map(td => td.textContent));
  assert.equal(entries.length, 2);
  assert.deepEqual(entries.map(cells => cells.slice(1, 3)), [['', '1'], ['', '2']]);
  assert.ok(entries.every(cells => cells.length === head.length), 'entry rows have one cell per header column');
  assert.doesNotMatch(table.textContent, /傳票文件項次/);
});

test('voucher pages replace 200 rows, restore the previous page and retry a failed whole-result sort', async () => {
  const f = filterJourney();
  f.click('[data-common-card="blankDescription"]'); f.click('[data-action="preview-scenario"]');
  f.request('filterPreview').resolve({ scenario: { count: 201, voucherCount: 201, summaryRowCount: 201, populationScope: 'auditPeriod' } }); await settle();
  const rows = Array.from({ length: 200 }, (_, i) => ({ voucherNumber: 'V-' + (i + 1), glDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: i + 1 }));
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows, nextCursor: 'after-200', totalCount: 201 }); await settle();
  const host = f.$('.filter-voucher-host');
  assert.equal(host.querySelectorAll('tbody tr').length, 200); assert.equal(host.querySelector('[data-page-position]').textContent, '1/2');
  host.querySelector('[data-voucher-next]').onclick();
  assert.equal(f.request('queryFilterVoucherPage').payload.cursor, 'after-200');
  assert.equal(host.querySelector('[data-voucher-next]').disabled, true);
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [{ ...rows[0], voucherNumber: 'V-201' }], nextCursor: null, totalCount: 201 }); await settle();
  assert.equal(host.querySelectorAll('tbody tr').length, 1); assert.match(host.querySelector('tbody').textContent, /V-201/);
  assert.equal(host.querySelector('[data-page-position]').textContent, '2/2'); assert.equal(host.querySelector('[data-voucher-next]').disabled, true);
  host.querySelector('th[data-sort-key="voucherTotal"] .th-sort').onclick();
  const sorted = f.request('queryFilterVoucherPage');
  assert.equal(sorted.payload.cursor, null); assert.deepEqual(plain(sorted.payload.sort), { key: 'voucherTotal', direction: 'asc' });
  sorted.reject(new Error('synthetic whole-result sort failed')); await settle();
  assert.match(host.querySelector('tbody').textContent, /V-201/); assert.equal(host.querySelector('[data-page-position]').textContent, '2/2');
  assert.equal(host.querySelector('th[data-sort-key="voucherTotal"]').getAttribute('aria-sort'), 'none');
  host.querySelector('[data-voucher-retry]').onclick();
  assert.deepEqual(plain(f.request('queryFilterVoucherPage').payload), plain(sorted.payload));
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows, nextCursor: 'sorted-200', totalCount: 201 }); await settle();
  assert.equal(host.querySelector('[data-page-position]').textContent, '1/2');
  host.querySelector('[data-voucher-next]').onclick();
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [{ ...rows[0], voucherNumber: 'V-201' }], nextCursor: null, totalCount: 201 }); await settle();
  host.querySelector('[data-voucher-previous]').onclick(); assert.equal(f.request('queryFilterVoucherPage').payload.cursor, null);
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows, nextCursor: 'sorted-200', totalCount: 201 }); await settle();
  assert.equal(host.querySelectorAll('tbody tr').length, 200); assert.equal(host.querySelector('[data-page-position]').textContent, '1/2');
});

// 第五步傳票清單的續頁同樣不回總筆數；換頁後仍顯示第一頁算出的總頁數。
test('a voucher continuation page without a total keeps the first page count', async () => {
  const f = filterJourney();
  f.click('[data-common-card="blankDescription"]'); f.click('[data-action="preview-scenario"]');
  f.request('filterPreview').resolve({ scenario: { count: 401, voucherCount: 401, summaryRowCount: 401, populationScope: 'auditPeriod' } }); await settle();
  const rows = start => Array.from({ length: 200 }, (_, i) => ({ voucherNumber: 'V-' + (start + i), glDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: 1 }));
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: rows(1), nextCursor: 'after-200', totalCount: 401 }); await settle();
  const host = f.$('.filter-voucher-host');
  assert.equal(host.querySelector('[data-page-position]').textContent, '1/3');
  host.querySelector('[data-voucher-next]').onclick();
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: rows(201), nextCursor: 'after-400', totalCount: null }); await settle();
  assert.match(host.querySelector('tbody').textContent, /V-201/);
  assert.equal(host.querySelector('[data-page-position]').textContent, '2/3');
  assert.equal(host.querySelector('[data-voucher-next]').disabled, false);
});

for (const outcome of ['success', 'failure']) test('voucher in-flight ' + outcome + ' survives a step redraw and updates the newly mounted table', async () => {
  const f = filterJourney(); await draftVoucherList(f);
  const oldHost = f.$('.filter-voucher-host'); oldHost.querySelector('[data-voucher-next]').onclick();
  const pending = f.request('queryFilterVoucherPage');
  f.store.touch();
  const host = f.$('.filter-voucher-host');
  assert.ok(host !== oldHost, 'the step redraw actually replaced the table host');
  assert.equal(oldHost.isConnected, false); assert.equal(host.querySelector('[data-voucher-next]').disabled, true);
  if (outcome === 'failure') {
    pending.reject(new Error('synthetic redraw page failure')); await settle();
    assert.match(host.querySelector('tbody').textContent, /SYN-B/);
    const retry = host.querySelector('[data-voucher-retry]'); assert.ok(retry);
    retry.onclick();
    assert.deepEqual(plain(f.request('queryFilterVoucherPage').payload), plain(pending.payload));
    assert.equal(host.querySelector('[data-voucher-next]').disabled, true, 'retry marks the current host as reading');
  }
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [{ voucherNumber: 'AFTER-REDRAW', glDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: 1 }], nextCursor: null, totalCount: 201 });
  await settle();
  assert.match(host.querySelector('tbody').textContent, /AFTER-REDRAW/);
  assert.doesNotMatch(host.querySelector('tbody').textContent, /SYN-B/);
  assert.equal(host.querySelector('table').getAttribute('aria-busy'), 'false');
  assert.equal(host.querySelector('[data-voucher-previous]').disabled, false);
  assert.equal(host.querySelector('[data-voucher-next]').disabled, true);
});

test('step five high-risk summary tables sort and search on the backend from the first page', async () => {
  const f = filterJourney({ saved: [F.savedBlankDescription()] });

  // 高風險條件彙總的傳票表：一打開就讀第一頁，排序與查看都從第一頁重讀。
  f.click('[data-action="toggle-high-risk-summary"]');
  f.request('queryHighRiskSummaryScenarios').resolve({ scenarios: [{ position: 1, name: 'G', voucherHitCount: 1, rowHitCount: 2, summaryRowCount: 2 }] }); await settle();
  for (const [view, action, key] of [['vouchers', 'queryHighRiskSummaryVoucherPage', 'voucherTotal']]) {
    f.click('[data-high-risk-summary-view="' + view + '"]'); await settle();
    assert.deepEqual(plain(f.requestsOf(action)[0].payload), { cursor: null, pageSize: 200, sort: null, search: null }, view);
    f.requestsOf(action)[0].resolve({ rows: [], nextCursor: null }); await settle();
    const box = f.$('[data-bind="high-risk-summary-' + view + '"]'), boxTable = box.querySelector('.preview-table');
    boxTable.fire('click', { target: boxTable.querySelector('.th-sort[data-sort-key="' + key + '"]') });
    assert.deepEqual(plain(f.requestsOf(action)[1].payload), { cursor: null, pageSize: 200, sort: { key, direction: 'asc' }, search: null }, view);
    f.requestsOf(action)[1].resolve({ rows: [], nextCursor: null }); await settle();
    submitSearch(box, 'M-1');
    assert.deepEqual(plain(f.requestsOf(action)[2].payload), { cursor: null, pageSize: 200, sort: { key, direction: 'asc' }, search: 'M-1' }, view);
  }
});

test('the high-risk summary retains the full long scenario title and has no entry-detail tab or row query', async () => {
  const name = '合成的長條件名稱用來確認標題仍可完整閱讀與安全轉義 <b>條件</b> ' + '跨期間傳票篩選條件'.repeat(12);
  const f = filterJourney({ saved: [F.savedBlankDescription(name)] });
  f.click('[data-action="toggle-high-risk-summary"]');
  f.request('queryHighRiskSummaryScenarios').resolve({ scenarios: [{ position: 1, name, voucherHitCount: 201, rowHitCount: 401, summaryRowCount: 401 }] });
  await settle();
  assert.equal(f.$('[data-high-risk-summary-view="rows"]'), null);
  assert.equal(f.$('[data-bind="high-risk-summary-rows"]'), null);
  f.click('[data-high-risk-summary-view="vouchers"]'); await settle();
  f.request('queryHighRiskSummaryVoucherPage').resolve({ rows: [], nextCursor: null, totalCount: 0 }); await settle();
  const header = f.$('[data-bind="high-risk-summary-vouchers"] th[title]');
  assert.ok(header); assert.equal(header.getAttribute('title'), name);
  assert.equal(header.querySelector('.filter-high-risk-summary-name').textContent, name);
  assert.equal(header.querySelector('b'), null, 'title text never becomes markup');
  // 交叉表表頭只顯示代號：名稱只留給停留提示與輔助技術，不佔欄寬。
  assert.match(header.getAttribute('class'), /preview-table__tag/);
  assert.match(header.querySelector('.filter-high-risk-summary-name').getAttribute('class'), /visually-hidden/);
  assert.equal(header.textContent.slice(0, header.textContent.length - name.length), 'C1');
  assert.equal(f.requestsOf('queryHighRiskSummaryRowPage').length, 0);
});

// 讀取中按「查看」或「清除」不會被忽略：只記住最後一次，讀完後接著做。
// 狀態列的說明句用字沒有定案，只比對意思（讀完後接著查看或清除）。
test('searching while a table is reading waits for the current read and keeps only the last text, even after a failure', async () => {
  const f = validationJourney(), detail = f.detailOf('voucherBalance');
  const status = () => detail.querySelector('[data-page-status]');
  f.toggleFor('[data-page-query="voucherBalance"]').fire('click');
  assert.equal(f.requestsOf('queryVoucherBalancePage').length, 1);

  submitSearch(detail, 'A'); submitSearch(detail, 'B');
  assert.equal(f.requestsOf('queryVoucherBalancePage').length, 1, 'the second and third clicks wait for the current read');
  assert.match(status().textContent, /讀完後/); assert.match(status().textContent, /查看/);
  assert.equal(status().hidden, false);

  f.requestsOf('queryVoucherBalancePage')[0].reject(new Error('synthetic read failure')); await settle();
  assert.equal(f.requestsOf('queryVoucherBalancePage').length, 2, 'the remembered search runs after the failed read');
  assert.deepEqual(plain(f.requestsOf('queryVoucherBalancePage')[1].payload), { cursor: null, pageSize: 200, sort: null, search: 'B' });
  assert.equal(f.requests.some(request => request.payload && request.payload.search === 'A'), false, 'only the last text is kept');

  // 清除的版本：讀取中按清除，讀完後接著用空白條件從第一頁重讀。
  f.requestsOf('queryVoucherBalancePage')[1].resolve({ rows: [{ voucherNumber: 'B-1' }], nextCursor: 'b-2' }); await settle();
  detail.querySelector('[data-page-next]').fire('click');
  assert.deepEqual(plain(f.requestsOf('queryVoucherBalancePage')[2].payload), { cursor: 'b-2', pageSize: 200, sort: null, search: 'B' });
  detail.querySelector('[data-page-search-clear]').fire('click');
  assert.equal(f.requestsOf('queryVoucherBalancePage').length, 3);
  assert.match(status().textContent, /讀完後/); assert.match(status().textContent, /清除/);
  f.requestsOf('queryVoucherBalancePage')[2].resolve({ rows: [], nextCursor: null }); await settle();
  assert.equal(f.requestsOf('queryVoucherBalancePage').length, 4);
  assert.deepEqual(plain(f.requestsOf('queryVoucherBalancePage')[3].payload), { cursor: null, pageSize: 200, sort: null, search: null });
});

// 重試保留原請求，不受失敗後退回的畫面狀態影響。排序或查看失敗時表頭與輸入框退回上一次成功的狀態，
// 表格裡仍是舊列；換了驗證結果後晚到的列不顯示，讀取中記住的查看也不送出。
test('a failed sort or search keeps the old rows, restores the header and box, and retry resends the exact request', async () => {
  const f = validationJourney(), detail = f.detailOf('voucherBalance');
  const table = detail.querySelector('[data-page-query]').closest('table'), input = detail.querySelector('[data-page-search-input]');
  const sortButton = table.querySelector('.th-sort[data-sort-key="debit"]');
  const before = detail.querySelector('[data-page-query]').textContent;
  sortButton.fire('click');
  f.requestsOf('queryVoucherBalancePage')[0].reject(new Error('synthetic sort failure')); await settle();
  assert.equal(detail.querySelector('[data-page-query]').textContent, before, 'the old rows stay');
  assert.equal(table.querySelector('th[data-sort-key="debit"]').getAttribute('aria-sort'), 'none', 'the header no longer claims the failed sort');
  submitSearch(detail, 'JV-404');
  assert.deepEqual(plain(f.requestsOf('queryVoucherBalancePage')[1].payload), { cursor: null, pageSize: 200, sort: null, search: 'JV-404' });
  f.requestsOf('queryVoucherBalancePage')[1].reject(new Error('synthetic search failure')); await settle();
  assert.equal(input.value, '', 'the search box returns to the last successful search');
  const status = detail.querySelector('[data-page-status]');
  assert.match(status.textContent, /失敗/); assert.match(status.textContent, /重試/);
  const retry = detail.querySelectorAll('button').find(button => button.textContent === '重試' && !button.hidden);
  assert.ok(retry, 'a retry button is shown next to the table');
  retry.fire('click');
  assert.deepEqual(plain(f.requestsOf('queryVoucherBalancePage')[2].payload), { cursor: null, pageSize: 200, sort: null, search: 'JV-404' });
  assert.equal(input.value, 'JV-404');
  f.requestsOf('queryVoucherBalancePage')[2].resolve({ rows: [{ voucherNumber: 'JV-404' }], nextCursor: null }); await settle();
  assert.match(detail.querySelector('[data-page-query]').textContent, /JV-404/);
  assert.equal(f.store.getState().busy, false);

  // 讀取中換了驗證結果：晚到的列不顯示，讀取中按下的查看也不送出。
  sortButton.fire('click'); submitSearch(detail, 'LATER');
  const count = f.requests.length, shown = detail.querySelector('[data-page-query]').textContent;
  f.store.setResult('validate', F.validationResultData('v-new'));
  f.requestsOf('queryVoucherBalancePage').at(-1).resolve({ rows: [{ voucherNumber: 'STALE-ROW' }], nextCursor: null }); await settle();
  assert.equal(detail.querySelector('[data-page-query]').textContent, shown);
  assert.doesNotMatch(detail.querySelector('[data-page-query]').textContent, /STALE-ROW/);
  assert.equal(f.requests.length, count, 'the remembered search is dropped with the old result');
});

// 金額顯示：畫面統一兩位小數加千分位；這個格式不套用到報表與底稿。
// 1,234.50、-10.00 這類顯示結果是這個格式的直接例子，逐字比對。
test('money shows two decimals with thousands separators on step four tables, the overview and step four queried pages', async () => {
  const f = validationJourney();
  assert.match(f.detailOf('completeness').querySelector('[data-page-query]').textContent, /1,234\.50/);
  assert.match(f.detailOf('voucherBalance').querySelector('[data-page-query]').textContent, /1,234\.50/);

  // 展開後讀取的第一頁同樣格式。
  const detail = f.detailOf('completeness');
  f.toggleFor('[data-page-query="completeness"]').fire('click');
  f.requestsOf('queryCompletenessDiffPage')[0].resolve({ rows: [{ accountNumber: '3003', accountName: 'x', tbAmount: 1234567.891, glAmount: -10, diff: 0.003 }], nextCursor: null });
  await settle();
  const loaded = detail.querySelector('[data-page-query]').textContent;
  assert.match(loaded, /1,234,567\.89/); assert.match(loaded, /-10\.00/);
  // 極小的非零差額不顯示成 0.00（只比對意思）。
  // 摘要列與第一頁的差額另由 frontend-workflow.test.cjs「tiny nonzero differences stay visible ...」測試。
  assert.match(loaded, /不足 0\.01/);

  // 第一頁的差額欄同樣不顯示成 0.00；負的極小差額說明是負值（只比對意思，不看表格上方的說明句）。
  const tiny = validationJourney(data => {
    data.completenessTest.diffAccounts[0].diff = 0.003; data.voucherBalanceTest.unbalancedVouchers[0].diff = -0.003;
  });
  assert.match(tiny.detailOf('completeness').querySelector('[data-page-query]').textContent, /不足 0\.01/);
  assert.match(tiny.detailOf('voucherBalance').querySelector('[data-page-query]').textContent, /負值.*不足 0\.01/);

  // 預篩選命中的第一頁與後續頁。
  f.toggleFor('[data-prescreen-key="weekendPosting"]').fire('click');
  f.requestsOf('queryPrescreenPage')[0].resolve({ rows: [{ voucherNumber: 'JV-P', glDate: '2025-01-04', accountNumber: '1001', amount: 1234.5, drCr: 'DEBIT' }], nextCursor: 'p-2' });
  await settle();
  const hits = f.root.querySelector('[data-prescreen-key="weekendPosting"]');
  assert.match(hits.querySelector('tbody').textContent, /1,234\.50/);
  hits.querySelector('[data-page-next]').fire('click');
  f.requestsOf('queryPrescreenPage')[1].resolve({ rows: [{ voucherNumber: 'JV-Q', glDate: '2025-01-05', accountNumber: '1001', amount: -10, drCr: 'CREDIT' }], nextCursor: null });
  await settle();
  assert.match(hits.querySelector('tbody').textContent, /-10\.00/);

  // 抽樣明細的金額欄與金額型攸關資料元素欄位（後端欄位定義標 money）。
  f.toggleFor('[data-dynamic-page="inf"]').fire('click');
  // 抽樣明細的金額欄是借方與貸方（正式後端的欄位）。
  f.requestsOf('queryInfSamplePage')[0].resolve({ columns: [
    { key: 'voucherNumber', label: 'Doc', valueType: 'text' }, { key: 'debit', label: 'Debit', valueType: 'amount' },
    { key: 'rde.extra', label: 'Extra', valueType: 'amount', isCustom: true }],
  rows: [{ voucherNumber: 'JV-S', debit: 1234.5, customValues: { 'rde.extra': -10 } }], nextCursor: null });
  await settle();
  const sample = f.root.querySelector('[data-dynamic-page="inf"]').textContent;
  assert.match(sample, /1,234\.50/); assert.match(sample, /-10\.00/);

  // 流程總覽的借方與貸方合計。
  const overview = F.stepScreensFixture(), data = F.validationResultData();
  data.stats.totalDebit = 1234.5; data.stats.totalCredit = 1234.5;
  overview.store.setResult('validate', data);
  assert.equal((overview.app.overviewPopulationHtml(overview.store.getState()).match(/1,234\.50/g) || []).length, 2);
});

test('money shows two decimals on step five voucher lists, entry details and the high-risk summary while condition values stay as typed', async () => {
  const f = filterJourney();
  // 傳票與分錄在同一張表：攸關資料元素的自訂欄取自傳票頁的欄位資訊，點傳票那一列在下方展開分錄。
  const columns = [{ key: 'rde.extra', label: 'Extra', valueType: 'amount', isCustom: true }];
  await draftVoucherList(f, undefined, columns);
  const host = f.$('.filter-voucher-host');
  const totals = host.querySelector('tbody').textContent;
  assert.match(totals, /1,234\.50/); assert.match(totals, /-10\.00/);

  host.querySelector('[data-voucher-row="0"]').onclick();
  // 正式後端的分錄金額一定有值；第二列刻意給 null，確認畫面寫「—」而不是 0.00，所以這筆不核對。
  f.request('queryFilterVoucherRowsPage').resolve(unchecked('分錄金額刻意給 null，確認缺值的寫法', { columns,
    rows: [{ lineItem: '1', isHit: true, amount: -10, customValues: { 'rde.extra': 1234.5 } }, { lineItem: '2', isHit: false, amount: null, customValues: { 'rde.extra': null } }], nextCursor: null }));
  await settle();
  const rows = host.querySelectorAll('tr.voucher-table__entry');
  assert.equal(rows.length, 2);
  assert.match(rows[0].textContent, /-10\.00/); assert.match(rows[0].textContent, /1,234\.50/);
  assert.match(rows[1].textContent, /—/, 'a missing amount shows a dash instead of 0.00');
  assert.doesNotMatch(rows[1].textContent, /0\.00/);

  // 已儲存情境：條件讀回保留審計員輸入的值，不加千分位、不補小數；高風險條件彙總的金額照畫面格式。
  const s = F.savedBlankDescription('合成金額'); s.source = 'custom'; delete s.editorOrigins;
  s.groups = [{ join: 'AND', matchScope: 'row', conditions: [{ type: 'numRange', join: 'AND', from: '1234.5', to: '' }] }];
  const g = filterJourney({ saved: [s] });
  const readback = g.dom.content.textContent;
  assert.match(readback, /≥ 1234\.5/); assert.doesNotMatch(readback, /1,234\.5/);
  g.click('[data-action="toggle-high-risk-summary"]');
  g.request('queryHighRiskSummaryScenarios').resolve({ scenarios: [{ position: 1, name: '合成金額', voucherHitCount: 1, rowHitCount: 1, summaryRowCount: 1 }] }); await settle();
  g.click('[data-high-risk-summary-view="vouchers"]'); await settle();
  g.request('queryHighRiskSummaryVoucherPage').resolve({ rows: [{ voucherNumber: 'M-1', glDate: '2025-01-01', createdBy: 'U', voucherTotal: 1234.5 }], nextCursor: null }); await settle();
  assert.match(g.$('[data-bind="high-risk-summary-vouchers"] tbody').textContent, /1,234\.50/);
  assert.equal(g.$('[data-high-risk-summary-view="rows"]'), null, 'the removed entry-detail tab stays absent');
  assert.equal(g.requestsOf('queryHighRiskSummaryRowPage').length, 0);
});

test('report history shows fifty files per page and the mapping value sample reads fifty source values', () => {
  // 第六步版本紀錄一頁 50 份（字面值）。
  const f = F.usageJourneyFixture('export');
  f.store.setReports(Array.from({ length: 51 }, (_, i) => ({ reportId: 'paper-' + i, kind: 'workingPaper', fileName: 'Synthetic-' + i + '.xlsx',
    generatedUtc: '2026-01-01T00:00:00Z', stale: false, fileState: 'asPublished', sourceRef: {} })));
  const root = f.draw();
  assert.equal(root.querySelectorAll('[data-open-report]').length, 50);
  // 換頁設計統一，版本紀錄也用左箭頭、目前頁數/總頁數、右箭頭。
  assert.equal(root.querySelector('.table-pager [data-page-position]').textContent, '1/2');
  root.querySelector('[data-history-page="1"]').fire('click');
  const second = f.draw();
  assert.equal(second.querySelectorAll('[data-open-report]').length, 1);
  assert.equal(second.querySelector('.table-pager [data-page-position]').textContent, '2/2');
  assert.equal(second.querySelector('[data-history-page="1"]').disabled, true);

  // 第三步「人工/自動」來源值：一次讀 50 個（字面值）。
  const m = F.mappingFixture(), page = m.render();
  page.click('load-manual-profile');
  assert.equal(m.pending.length, 1);
  assert.deepEqual(plain(m.pending[0].payload), { dataset: 'gl', sourceColumn: 'Manual', limit: 50, comparisonValues: ['1', '0'], checkSourceValues: true });
});

// ===========================================================================
// 第二部分　情境 21：右側資料預覽
// ===========================================================================

async function previewOpened(options) {
  const f = F.dataPreviewFixture(options);
  f.enterWorkflow(); await settle();
  return f;
}
const tab = (f, dataset) => f.aside().querySelector('[data-ds="' + dataset + '"]');
const previewRequests = f => f.requests.filter(request => request.action === 'queryDataPreview');

test('switching the preview dataset replaces the old table with a named loading state without locking the screen', async () => {
  const f = await previewOpened();
  // 第一次進入案件畫面就讀目前的資料表。
  assert.deepEqual(plain(previewRequests(f)[0].payload), { dataset: 'pbcGl', offset: 0, limit: 200 });
  f.request('queryDataPreview').resolve({ columns: ['voucherNumber'], rows: [['OLD-ROW']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /OLD-ROW/);
  assert.equal(f.body().getAttribute('aria-busy'), null);

  tab(f, 'gl').fire('click');
  assert.deepEqual(plain(previewRequests(f)[1].payload), { dataset: 'gl', offset: 0, limit: 200 });
  // 載入中立刻換掉舊表格，寫出資料名稱（只比對意思）。
  assert.doesNotMatch(f.body().textContent, /OLD-ROW/);
  assert.ok(f.body().querySelector('table') === null, 'the old table is gone while loading');
  assert.match(f.body().textContent, /載入/); assert.match(f.body().textContent, /GL/);
  assert.equal(f.body().getAttribute('aria-busy'), 'true');
  assert.equal(tab(f, 'gl').getAttribute('aria-selected'), 'true');
  assert.equal(tab(f, 'pbcGl').getAttribute('aria-selected'), 'false');
  assert.equal(f.store.getState().busy, false, 'the preview never takes over the whole screen');

  f.request('queryDataPreview').resolve({ columns: ['voucherNumber'], rows: [['NEW-ROW']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /NEW-ROW/);
  assert.equal(f.body().getAttribute('aria-busy'), null);
});

// 前端不露出系統用詞，GL 與未納入測試的分錄，借貸欄和結果表格一樣寫「借」「貸」。
test('the GL preview shows debit and credit as 借 and 貸 instead of the stored English values', async () => {
  const f = await previewOpened();
  f.request('queryDataPreview').resolve({ columns: ['voucherNumber'], rows: [['FIRST']], totalCount: 1 }); await settle();
  for (const dataset of ['gl', 'glExcluded']) {
    tab(f, dataset).fire('click');
    previewRequests(f).at(-1).resolve({ columns: ['voucherNumber', 'drCr'], rows: [['JV-1', 'DEBIT'], ['JV-1', 'CREDIT']], totalCount: 2 });
    await settle();
    const cells = f.body().querySelectorAll('td').map(cell => cell.textContent);
    assert.deepEqual(cells, ['JV-1', '借', 'JV-1', '貸'], dataset);
    assert.doesNotMatch(f.body().textContent, /DEBIT|CREDIT/, dataset);
  }
});

// 資料預覽依配對列欄位、欄名和欄位配對畫面相同，金額固定顯示四位小數。
// GL 與 GL(未納入測試) 的欄名與型別讀回應的 columnDetails：金額欄靠右並補成四位小數，傳票文件項次、科目編號這類
// 看起來像數字的文字照樣靠左；人工或自動分錄兩頁都寫中文。
test('the GL pages take column names and alignment from the column details and show amounts with four decimals', async () => {
  const f = await previewOpened();
  f.request('queryDataPreview').resolve({ columns: ['voucherNumber'], rows: [['FIRST']], totalCount: 1 }); await settle();
  const columnDetails = [
    { key: 'voucherNumber', label: '傳票號碼', valueType: 'text' },
    { key: 'lineItem', label: '傳票文件項次', valueType: 'text' },
    { key: 'glDate', label: '總帳日期', valueType: 'date' },
    { key: 'accountNumber', label: '會計科目編號', valueType: 'text' },
    { key: 'amount', label: '金額', valueType: 'amount' },
    { key: 'manualAuto', label: '人工/自動分錄', valueType: 'text' },
    { key: 'rde.0123456789abcdef0123456789abcdef', label: '合成外幣金額', valueType: 'amount' }
  ];
  const rows = [
    ['JV-1', '1', '2025-03-01', '1101', '1234.5678', 'manual', '12.34'],
    ['JV-1', '2', '2025-03-01', '4101', '-1000', 'automatic', null]
  ];
  const stats = { amountAbsMin: '1000', amountAbsMax: '1234.5678', glDateMin: '2025-03-01', glDateMax: '2025-03-01', voucherCount: 1 };
  for (const dataset of ['gl', 'glExcluded']) {
    tab(f, dataset).fire('click');
    previewRequests(f).at(-1).resolve({ dataset, columns: columnDetails.map(column => column.key), columnDetails, rows, totalCount: 2,
      stats: dataset === 'gl' ? stats : null });
    await settle();
    const headers = f.body().querySelectorAll('th');
    assert.deepEqual(headers.map(header => header.textContent),
      ['傳票號碼', '傳票文件項次', '總帳日期', '會計科目編號', '金額', '人工/自動分錄', '合成外幣金額'], dataset);
    const alignedRight = [false, false, false, false, true, false, true];
    assert.deepEqual(headers.map(header => header.classList.contains('num')), alignedRight, dataset);
    const bodyRows = f.body().querySelectorAll('tbody tr');
    assert.deepEqual(bodyRows.map(row => row.querySelectorAll('td').map(cell => cell.textContent)), [
      ['JV-1', '1', '2025-03-01', '1101', '1,234.5678', '人工', '12.3400'],
      ['JV-1', '2', '2025-03-01', '4101', '-1,000.0000', '自動', '—']
    ], dataset);
    assert.deepEqual(bodyRows[0].querySelectorAll('td').map(cell => cell.classList.contains('num')), alignedRight, dataset);
    if (dataset === 'gl') {
      // 統計列的金額也固定四位小數。
      assert.match(f.aside().querySelector('.data-preview__note').textContent, /金額（絕對值）1,000\.0000 ～ 1,234\.5678/);
    }
  }
});

// TB、科目配對與 PBC 兩頁也讀回應的 columnDetails。科目編號這類看起來像數字的文字靠左，只有金額靠右；
// PBC 兩頁的金額欄只靠右，數值照來源文字；沒有欄位型別的回應全部靠左，不逐格判斷是不是數字。
test('TB, account mapping and both PBC pages take names and alignment from the column details and never guess numbers', async () => {
  const f = await previewOpened();
  const text = (key, label) => ({ key, label: label || key, valueType: 'text' });
  const amount = (key, label) => ({ key, label: label || key, valueType: 'amount' });
  const check = (dataset, details, rows, headers, cells, alignedRight) => {
    const ths = f.body().querySelectorAll('th');
    assert.deepEqual(ths.map(header => header.textContent), headers, dataset);
    assert.deepEqual(ths.map(header => header.classList.contains('num')), alignedRight, dataset);
    const tds = f.body().querySelectorAll('tbody tr')[0].querySelectorAll('td');
    assert.deepEqual(tds.map(cell => cell.textContent), cells, dataset);
    assert.deepEqual(tds.map(cell => cell.classList.contains('num')), alignedRight, dataset);
  };
  // 打開資料預覽先停在 PBC-GL：配到借方金額的來源欄靠右，數值照原樣；會計科目、分錄序號整欄都是數字也靠左。
  const pbcGl = [text('傳票編號'), text('分錄序號'), text('會計科目'), amount('借方金額')];
  f.request('queryDataPreview').resolve({ dataset: 'pbcGl', columns: pbcGl.map(column => column.key), columnDetails: pbcGl,
    rows: [['GL-1', '1', '6206', '8640']], totalCount: 1 }); await settle();
  check('pbcGl', pbcGl, null, ['傳票編號', '分錄序號', '會計科目', '借方金額'], ['GL-1', '1', '6206', '8640'], [false, false, false, true]);
  const cases = [
    ['tb', [text('accountNumber', '會計科目編號'), text('accountName', '會計科目名稱'), amount('changeAmount', '年度變動金額')],
      [['1111', '庫存現金', '-29926']], ['會計科目編號', '會計科目名稱', '年度變動金額'], ['1111', '庫存現金', '-29,926.0000'], [false, false, true]],
    ['accountMapping', [text('accountNumber', '會計科目編號'), text('accountName', '會計科目名稱'), text('standardizedCategory', '科目分類')],
      [['111301', '銀行存款', 'Cash']], ['會計科目編號', '會計科目名稱', '科目分類'], ['111301', '銀行存款', 'Cash'], [false, false, false]],
    ['pbcTb', [text('科目代號'), amount('期末借方餘額')],
      [['1111', '55074']], ['科目代號', '期末借方餘額'], ['1111', '55074'], [false, true]]
  ];
  for (const [dataset, details, rows, headers, cells, alignedRight] of cases) {
    tab(f, dataset).fire('click');
    previewRequests(f).at(-1).resolve({ dataset, columns: details.map(column => column.key), columnDetails: details, rows, totalCount: 1 });
    await settle();
    check(dataset, details, rows, headers, cells, alignedRight);
  }
  // 沒有欄位型別的回應：全部靠左，整欄都是數字也一樣。
  tab(f, 'pbcGl').fire('click');
  previewRequests(f).at(-1).resolve({ dataset: 'pbcGl', columns: ['分錄序號', '借方金額'], rows: [['1', '8640'], ['2', '0']], totalCount: 2 });
  await settle();
  assert.deepEqual(f.body().querySelectorAll('tbody tr')[0].querySelectorAll('td').map(cell => cell.classList.contains('num')), [false, false]);
});

// 人工或自動分錄沒有判定時顯示「—」，這裡鎖住畫面實際的寫法。
test('the GL pages show an undetermined manual or automatic entry as a dash', async () => {
  const f = await previewOpened();
  f.request('queryDataPreview').resolve({ columns: ['voucherNumber'], rows: [['FIRST']], totalCount: 1 }); await settle();
  const columnDetails = [
    { key: 'voucherNumber', label: '傳票號碼', valueType: 'text' },
    { key: 'manualAuto', label: '人工/自動分錄', valueType: 'text' }
  ];
  for (const dataset of ['gl', 'glExcluded']) {
    tab(f, dataset).fire('click');
    previewRequests(f).at(-1).resolve({ dataset, columns: ['voucherNumber', 'manualAuto'], columnDetails,
      rows: [['JV-1', 'manual'], ['JV-2', null]], totalCount: 2, stats: null });
    await settle();
    const cells = f.body().querySelectorAll('tbody tr').map(row => row.querySelectorAll('td').map(cell => cell.textContent));
    assert.deepEqual(cells, [['JV-1', '人工'], ['JV-2', '—']], dataset);
  }
});

test('a failed preview shows the escaped reason in place and retry asks again for the same dataset', async () => {
  const f = await previewOpened();
  tab(f, 'tb').fire('click');
  previewRequests(f)[1].reject({ message: '<b>synthetic</b> failure' }); await settle();
  const body = f.body();
  assert.equal(body.getAttribute('aria-busy'), null);
  assert.match(body.textContent, /失敗/);
  assert.match(body.textContent, /<b>synthetic<\/b> failure/, 'the reason is shown as text');
  assert.ok(body.querySelector('b') === null, 'the reason is escaped, not rendered as markup');
  const retry = body.querySelector('[data-action="retry-data-preview"]'); assert.ok(retry);
  retry.fire('click');
  assert.equal(previewRequests(f).length, 3);
  assert.deepEqual(plain(previewRequests(f)[2].payload), { dataset: 'tb', offset: 0, limit: 200 });
  assert.equal(body.getAttribute('aria-busy'), 'true');
  previewRequests(f)[2].resolve({ columns: ['accountNumber'], rows: [['1001']], totalCount: 1 }); await settle();
  assert.ok(body.querySelector('[data-action="retry-data-preview"]') === null, 'the retry button goes away after success');
  assert.match(body.textContent, /1001/);
  assert.equal(f.store.getState().busy, false);
});

test('source preview uses 200-row offset pages for 201 rows and previous returns to the first page', async () => {
  const f = await previewOpened();
  const firstRows = Array.from({ length: 200 }, (_, i) => ['SOURCE-' + (i + 1)]);
  previewRequests(f)[0].resolve({ columns: ['voucherNumber'], rows: firstRows, totalCount: 201 }); await settle();
  assert.equal(f.body().querySelectorAll('tbody tr').length, 200);
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '1/2');
  assert.equal(f.aside().querySelector('[data-page-previous]').disabled, true);
  f.aside().querySelector('[data-page-next]').fire('click');
  assert.deepEqual(plain(previewRequests(f)[1].payload), { dataset: 'pbcGl', limit: 200, offset: 200 });
  previewRequests(f)[1].resolve({ columns: ['voucherNumber'], rows: [['SOURCE-201']], totalCount: 201 }); await settle();
  assert.equal(f.body().querySelectorAll('tbody tr').length, 1);
  assert.equal(f.body().querySelector('tbody').textContent, 'SOURCE-201');
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '2/2');
  assert.equal(f.aside().querySelector('[data-page-next]').disabled, true);
  f.aside().querySelector('[data-page-previous]').fire('click');
  assert.deepEqual(plain(previewRequests(f)[2].payload), { dataset: 'pbcGl', limit: 200, offset: 0 });
  previewRequests(f)[2].resolve({ columns: ['voucherNumber'], rows: firstRows, totalCount: 201 }); await settle();
  assert.equal(f.body().querySelectorAll('tbody tr').length, 200);
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '1/2');
  assert.equal(f.body().querySelector('.th-sort'), null, 'the preview does not offer sorting only the current page');
});

// 資料預覽的總筆數與統計只在 offset 0 計算，續頁兩者都是 null；畫面沿用第一頁的頁數、列數與統計。
test('a preview continuation page without a total or stats keeps the first page count and summary', async () => {
  const f = await previewOpened();
  f.request('queryDataPreview').resolve({ columns: ['voucherNumber'], rows: [['PBC-ROW']], totalCount: 1 }); await settle();
  tab(f, 'gl').fire('click');
  // 資料預覽的金額固定四位小數，統計列的金額用字串照存下的值傳，和正式後端的形狀一致。
  const stats = { amountAbsMin: '1', amountAbsMax: '9', glDateMin: '2025-01-01', glDateMax: '2025-12-31', voucherCount: 3 };
  previewRequests(f)[1].resolve({ columns: ['voucherNumber'], rows: Array.from({ length: 200 }, (_, i) => ['GL-' + (i + 1)]), totalCount: 401, stats }); await settle();
  const note = () => f.aside().querySelector('.data-preview__note');
  const firstNote = note().textContent;
  // 來源檔用「列」，分錄用「筆」：GL 的筆數單位是「筆」。
  assert.match(firstNote, /共 401 筆/); assert.match(firstNote, /傳票 3 張/);
  assert.equal(note().querySelector('[data-page-position]').textContent, '1/3');
  note().querySelector('[data-page-next]').fire('click');
  assert.deepEqual(plain(previewRequests(f)[2].payload), { dataset: 'gl', limit: 200, offset: 200 });
  previewRequests(f)[2].resolve({ columns: ['voucherNumber'], rows: [['GL-201']], totalCount: null, stats: null }); await settle();
  assert.equal(f.body().querySelector('tbody').textContent, 'GL-201');
  assert.equal(note().querySelector('[data-page-position]').textContent, '2/3');
  assert.match(note().textContent, /共 401 筆/); assert.match(note().textContent, /傳票 3 張/);
  assert.equal(note().querySelector('[data-page-next]').disabled, false);
});

test('source preview next-page failure retains rows and page number while retry keeps the failed offset', async () => {
  const f = await previewOpened();
  previewRequests(f)[0].resolve({ columns: ['voucherNumber'], rows: [['KEPT-PREVIEW']], totalCount: 201 }); await settle();
  f.aside().querySelector('[data-page-next]').fire('click');
  assert.equal(f.body().querySelector('tbody').textContent, 'KEPT-PREVIEW');
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '1/2');
  assert.equal(f.aside().querySelector('[data-page-next]').disabled, true);
  previewRequests(f)[1].reject(new Error('synthetic second-page failure')); await settle();
  assert.equal(f.body().querySelector('tbody').textContent, 'KEPT-PREVIEW');
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '1/2');
  f.body().querySelector('[data-action="retry-data-preview"]').fire('click');
  assert.deepEqual(plain(previewRequests(f)[2].payload), { dataset: 'pbcGl', limit: 200, offset: 200 });
  previewRequests(f)[2].resolve({ columns: ['voucherNumber'], rows: [['RETRIED-PREVIEW']], totalCount: 201 }); await settle();
  assert.equal(f.body().querySelector('tbody').textContent, 'RETRIED-PREVIEW');
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '2/2');
  assert.equal(f.body().querySelector('[data-action="retry-data-preview"]'), null);
});

test('a late preview response is dropped after switching dataset, re-importing or switching case', async () => {
  // 切到別的資料表：A 還沒回來就切到 B，A 晚到的結果（成功或失敗）都不顯示。
  const f = await previewOpened();
  tab(f, 'gl').fire('click'); tab(f, 'tb').fire('click');
  const [, a, b] = previewRequests(f);
  a.resolve({ columns: ['voucherNumber'], rows: [['FROM-GL']], totalCount: 1 }); await settle();
  assert.doesNotMatch(f.body().textContent, /FROM-GL/);
  assert.equal(f.body().getAttribute('aria-busy'), 'true', 'still waiting for TB');
  b.resolve({ columns: ['accountNumber'], rows: [['FROM-TB']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /FROM-TB/);

  // 重新匯入：舊請求晚到不顯示，畫面自動重讀新資料。
  tab(f, 'gl').fire('click');
  const beforeImport = previewRequests(f).at(-1);
  f.store.setImportResult('gl', { batchId: 'reimported', columns: [], rowCount: 1 }); await settle();
  const afterImport = previewRequests(f).at(-1);
  assert.ok(afterImport !== beforeImport, 'the new data is read again');
  assert.deepEqual(plain(afterImport.payload), { dataset: 'gl', offset: 0, limit: 200 });
  beforeImport.resolve({ columns: ['voucherNumber'], rows: [['BEFORE-IMPORT']], totalCount: 1 }); await settle();
  assert.doesNotMatch(f.body().textContent, /BEFORE-IMPORT/);
  afterImport.resolve({ columns: ['voucherNumber'], rows: [['AFTER-IMPORT']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /AFTER-IMPORT/);

  // 換案件：回到案件清單時清掉舊內容與底部說明，舊案件的請求晚到（成功或失敗）都不顯示在新案件。
  const note = () => f.aside().querySelector('.data-preview__note');
  assert.notEqual(note().textContent, '', 'the shown table has a row-count note');
  f.ui.goBackToPicker();
  assert.doesNotMatch(f.body().textContent, /AFTER-IMPORT/);
  assert.equal(note().textContent, '');
  f.enterWorkflow({ projectId: 'preview-between', periodStart: '2025-01-01', periodEnd: '2025-12-31' }); await settle();
  tab(f, 'tb').fire('click');
  const oldCase = previewRequests(f).at(-1), messages = f.store.getState().messages.length;
  f.ui.goBackToPicker();
  f.enterWorkflow({ projectId: 'preview-second', periodStart: '2025-01-01', periodEnd: '2025-12-31' }); await settle();
  const newCase = previewRequests(f).at(-1);
  assert.deepEqual(plain(newCase.payload), { dataset: 'pbcGl', offset: 0, limit: 200 }, 'the new case starts from the first tab');
  oldCase.reject(new Error('old case failure')); await settle();
  // 回到案件清單會讀案件清單（合成環境不回應），所以只看和資料預覽有關的訊息。
  assert.equal(f.store.getState().messages.slice(messages).some(message => /資料預覽|old case failure/.test(message.text)), false,
    'an old case failure is not reported in the new case');
  assert.doesNotMatch(f.body().textContent, /old case failure/);
  newCase.resolve({ columns: ['voucherNumber'], rows: [['SECOND-CASE']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /SECOND-CASE/);
});

test('asking twice for the same dataset shows only the latest answer, and a re-import or case switch while folded drops the old answer', async () => {
  // 同一份資料表連按兩次：後一次的結果先回來，前一次晚到也不覆蓋。
  const f = await previewOpened();
  tab(f, 'gl').fire('click'); tab(f, 'gl').fire('click');
  const [, first, second] = previewRequests(f);
  second.resolve({ columns: ['voucherNumber'], rows: [['LATEST']], totalCount: 1 }); await settle();
  first.resolve({ columns: ['voucherNumber'], rows: [['EARLIER']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /LATEST/);
  assert.doesNotMatch(f.body().textContent, /EARLIER/);

  // 讀取途中收合面板，接著重新匯入：收合時不重讀，舊請求晚到也不顯示；展開後才讀新資料。
  tab(f, 'tb').fire('click');
  const beforeImport = previewRequests(f).at(-1), count = previewRequests(f).length;
  f.$('.data-preview__collapse').fire('click');
  f.store.setImportResult('tb', { batchId: 'reimported-tb', columns: [], rowCount: 1 }); await settle();
  assert.equal(previewRequests(f).length, count, 'nothing is read while the preview is folded');
  beforeImport.resolve({ columns: ['accountNumber'], rows: [['OLD-TB']], totalCount: 1 }); await settle();
  assert.doesNotMatch(f.body().textContent, /OLD-TB/);
  f.$('.data-preview__rail').fire('click'); await settle();
  assert.equal(previewRequests(f).length, count + 1, 'expanding reads the new data once');
  assert.deepEqual(plain(previewRequests(f).at(-1).payload), { dataset: 'tb', offset: 0, limit: 200 });

  // 讀取途中收合面板，接著換案件：新案件收合時不讀，舊案件的結果晚到也不顯示在新案件。
  tab(f, 'gl').fire('click');
  const oldCase = previewRequests(f).at(-1), before = previewRequests(f).length;
  f.$('.data-preview__collapse').fire('click');
  f.ui.goBackToPicker();
  f.enterWorkflow({ projectId: 'preview-folded-next', periodStart: '2025-01-01', periodEnd: '2025-12-31' }); await settle();
  assert.equal(previewRequests(f).length, before, 'the folded preview does not read the new case yet');
  oldCase.resolve({ columns: ['voucherNumber'], rows: [['OLD-CASE-ROW']], totalCount: 1 }); await settle();
  assert.doesNotMatch(f.body().textContent, /OLD-CASE-ROW/);
  f.$('.data-preview__rail').fire('click'); await settle();
  assert.deepEqual(plain(previewRequests(f).at(-1).payload), { dataset: 'pbcGl', offset: 0, limit: 200 }, 'expanding reads the new case from the first tab');
});

// 資料預覽的金額固定顯示四位小數，例如 1,234.5000；其他畫面是兩位小數。
// 後端照存下的值傳精確的字串，超大金額補零時不經過浮點數。
// TB 也由後端帶 columnDetails，畫面不依欄位 id 認變動金額；這裡的 TB 回應和正式後端一樣帶欄位型別。
test('preview amounts on standardized data show four decimals exactly as stored while PBC source values stay as imported', async () => {
  const f = await previewOpened();
  // 資料預覽的每一格都是字串，和正式後端相同。
  f.request('queryDataPreview').resolve({ columns: ['voucherNumber', 'amount'], rows: [['JV-1', '1234.5']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /1234\.5/); assert.doesNotMatch(f.body().textContent, /1,234\.5/);
  const columnDetails = [{ key: 'voucherNumber', label: '傳票號碼', valueType: 'text' }, { key: 'amount', label: '金額', valueType: 'amount' }];
  const tbColumnDetails = [{ key: 'voucherNumber', label: '傳票號碼', valueType: 'text' }, { key: 'changeAmount', label: '年度變動金額', valueType: 'amount' }];
  for (const [dataset, column] of [['gl', 'amount'], ['glExcluded', 'amount'], ['tb', 'changeAmount']]) {
    f.ui.openDataPreview(dataset);
    f.request('queryDataPreview').resolve({ columns: ['voucherNumber', column], columnDetails: dataset === 'tb' ? tbColumnDetails : columnDetails,
      rows: [['JV-1', '1234.5'], ['JV-2', '-10'], ['JV-3', '922337203685477.5807']], totalCount: 3 }); await settle();
    const amounts = f.body().querySelectorAll('tbody tr').map(row => row.querySelectorAll('td')[1].textContent);
    assert.deepEqual(amounts, ['1,234.5000', '-10.0000', '922,337,203,685,477.5807'], dataset);
  }
});

test('the preview folds at 1360 pixels and keeps the auditor\'s own choice until the window crosses the boundary again', async () => {
  // 視窗本來就窄：自動收合；拉寬就自動展開，再縮窄又自動收合。
  const narrow = F.dataPreviewFixture({ compact: true });
  assert.deepEqual(narrow.media.queries(), ['(max-width: 1360px)']);
  const media = narrow.media.lists[0], collapsed = () => narrow.store.getState().dataPreviewCollapsed;
  assert.equal(collapsed(), true);
  media.change(false); assert.equal(collapsed(), false);
  media.change(true); assert.equal(collapsed(), true);

  // 自動收合後自己展開：留在展開；拉寬仍展開；再縮窄才自動收合。
  narrow.$('.data-preview__rail').fire('click'); assert.equal(collapsed(), false);
  // 自己展開後又自己收合：這是審計員的選擇，拉寬也不自動展開。
  narrow.$('.data-preview__collapse').fire('click'); assert.equal(collapsed(), true);
  media.change(false); assert.equal(collapsed(), true, "the auditor's own fold survives widening");
  narrow.$('.data-preview__rail').fire('click'); assert.equal(collapsed(), false);
  media.change(true); assert.equal(collapsed(), true);
  narrow.$('.data-preview__rail').fire('click'); assert.equal(collapsed(), false);
  media.change(false); assert.equal(collapsed(), false);
  media.change(true); assert.equal(collapsed(), true);

  // 寬畫面自己收合：縮窄、再拉寬都維持收合，直到自己展開。
  const wide = F.dataPreviewFixture({ compact: false }), wideMedia = wide.media.lists[0];
  const wideCollapsed = () => wide.store.getState().dataPreviewCollapsed;
  assert.equal(wideCollapsed(), false);
  wide.$('.data-preview__collapse').fire('click'); assert.equal(wideCollapsed(), true);
  wideMedia.change(true); assert.equal(wideCollapsed(), true);
  wideMedia.change(false); assert.equal(wideCollapsed(), true, 'the auditor\'s own fold survives widening');
  wide.$('.data-preview__rail').fire('keydown', { key: 'Enter' }); assert.equal(wideCollapsed(), false);
});

test('the other-data menu works from the keyboard on the same toggle with matching aria state', async () => {
  const f = await previewOpened();
  const tabs = f.aside().querySelector('.data-preview__tabs'), toggle = tabs.querySelector('[data-more-toggle]');
  const menu = tabs.querySelector('[role="menu"]'), items = () => tabs.querySelectorAll('[role="menuitem"]');
  const active = () => f.dom.document.activeElement;
  // 逐條 aria 標記：toggle 是開選單的按鈕，指向同一個選單；選單裡是 menuitem。
  assert.equal(toggle.tagName, 'BUTTON');
  assert.match(toggle.textContent, /其他資料/);
  assert.equal(toggle.getAttribute('aria-haspopup'), 'menu');
  assert.equal(toggle.getAttribute('aria-controls'), 'data-preview-other-menu');
  assert.equal(menu.getAttribute('id'), 'data-preview-other-menu');
  assert.equal(toggle.getAttribute('aria-expanded'), 'false');
  assert.equal(menu.hidden, true);
  assert.deepEqual(items().map(item => item.getAttribute('data-ds')), ['glExcluded', 'authorizedPreparer', 'calendar', 'schemaOverview']);
  for (const item of items()) assert.equal(item.tagName, 'BUTTON');

  toggle.focus(); toggle.dispatch('keydown', { key: 'ArrowDown' });
  assert.equal(toggle.getAttribute('aria-expanded'), 'true'); assert.equal(menu.hidden, false);
  assert.ok(active() === items()[0], 'ArrowDown opens the menu on the first item');
  assert.ok(tabs.querySelector('[data-more-toggle]') === toggle, 'opening keeps the same toggle node');
  active().dispatch('keydown', { key: 'ArrowUp' }); assert.ok(active() === items()[3], 'ArrowUp wraps to the last item');
  active().dispatch('keydown', { key: 'ArrowDown' }); assert.ok(active() === items()[0], 'ArrowDown wraps to the first item');
  active().dispatch('keydown', { key: 'End' }); assert.ok(active() === items()[3]);
  active().dispatch('keydown', { key: 'Home' }); assert.ok(active() === items()[0]);
  active().dispatch('keydown', { key: 'Escape' });
  assert.equal(toggle.getAttribute('aria-expanded'), 'false'); assert.equal(menu.hidden, true);
  assert.ok(active() === toggle, 'Escape returns focus to the toggle');

  toggle.dispatch('keydown', { key: 'ArrowUp' });
  assert.ok(active() === items()[3], 'ArrowUp opens the menu on the last item');
  active().dispatch('keydown', { key: 'Tab' });
  assert.equal(toggle.getAttribute('aria-expanded'), 'false', 'Tab closes the menu'); assert.equal(menu.hidden, true);

  // 點按鈕開選單、點畫面其他地方關閉。
  toggle.dispatch('click'); assert.equal(toggle.getAttribute('aria-expanded'), 'true');
  f.body().dispatch('click'); assert.equal(toggle.getAttribute('aria-expanded'), 'false');

  // 用 Enter 選一項：送出讀取、選單關閉、焦點回到 toggle，toggle 標示目前資料在選單裡。
  toggle.dispatch('keydown', { key: 'ArrowDown' });
  active().dispatch('keydown', { key: 'ArrowDown' });
  const chosen = active(); assert.equal(chosen.getAttribute('data-ds'), 'authorizedPreparer');
  chosen.dispatch('click'); // 原生按鈕按 Enter 時瀏覽器送出 click。
  assert.deepEqual(plain(previewRequests(f).at(-1).payload), { dataset: 'authorizedPreparer', offset: 0, limit: 200 });
  assert.equal(toggle.getAttribute('aria-expanded'), 'false'); assert.equal(menu.hidden, true);
  assert.ok(active() === toggle, 'choosing from the menu returns focus to the toggle');
  assert.equal(toggle.classList.contains('is-active'), true);
  assert.ok(tabs.querySelector('[data-more-toggle]') === toggle, 'the toggle is never rebuilt');
  for (const main of tabs.querySelectorAll('[role="tab"]')) assert.equal(main.getAttribute('aria-selected'), 'false');
});

// 同功能的元件只留一套，資料預覽的主要資料表用共用頁籤，外層是 role="tablist"，
// 只有選中的頁籤留在 Tab 鍵順序裡，方向鍵左右、Home、End 直接換資料表；「其他資料」是頁籤後面的共用選單，不在方向鍵裡。
test('data preview tabs switch datasets with arrow keys, Home and End, keep one tab in the Tab order, and leave the other-data menu out of the arrows', async () => {
  const f = await previewOpened();
  const list = f.aside().querySelector('.data-preview__tabs [role="tablist"]');
  assert.ok(list, 'the main datasets sit in a tablist');
  assert.equal(list.getAttribute('aria-label'), '資料預覽資料表');
  const tabs = () => list.querySelectorAll('[role="tab"]');
  assert.deepEqual(tabs().map(item => item.getAttribute('data-ds')), ['pbcGl', 'gl', 'pbcTb', 'tb', 'accountMapping']);
  assert.ok(list.querySelector('[data-more-toggle]') === null, 'the other-data button is not one of the tabs');
  const active = () => f.dom.document.activeElement;
  function expectSelected(dataset, label) {
    for (const item of tabs()) {
      const on = item.getAttribute('data-ds') === dataset;
      assert.equal(item.getAttribute('aria-selected'), on ? 'true' : 'false', label + ': aria-selected ' + item.getAttribute('data-ds'));
      assert.equal(item.getAttribute('tabindex'), on ? '0' : '-1', label + ': tabindex ' + item.getAttribute('data-ds'));
    }
    assert.ok(active() === tab(f, dataset), label + ': focus is on the selected tab');
    assert.deepEqual(plain(previewRequests(f).at(-1).payload), { dataset, offset: 0, limit: 200 }, label + ': the dataset is read');
  }
  tab(f, 'pbcGl').focus();
  for (const [key, dataset] of [['ArrowRight', 'gl'], ['End', 'accountMapping'], ['ArrowRight', 'pbcGl'], ['ArrowLeft', 'accountMapping'], ['Home', 'pbcGl']]) {
    assert.equal(active().dispatch('keydown', { key }).defaultPrevented, true, key + ' is handled');
    expectSelected(dataset, key);
  }
  // 正在看「其他資料」裡的資料表時，沒有頁籤是選中的，第一個頁籤留在 Tab 鍵順序裡。
  const toggle = f.aside().querySelector('[data-more-toggle]');
  toggle.dispatch('click');
  f.aside().querySelector('[role="menuitem"][data-ds="schemaOverview"]').dispatch('click');
  assert.deepEqual(plain(previewRequests(f).at(-1).payload), { dataset: 'schemaOverview', offset: 0, limit: 200 });
  for (const item of tabs()) {
    assert.equal(item.getAttribute('aria-selected'), 'false');
    assert.equal(item.getAttribute('tabindex'), item.getAttribute('data-ds') === 'pbcGl' ? '0' : '-1');
  }
  assert.equal(toggle.classList.contains('is-active'), true, 'the other-data button carries the underline');
});
