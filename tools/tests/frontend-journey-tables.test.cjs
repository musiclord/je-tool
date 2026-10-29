'use strict';
// 2026-10-16：依使用者要求將資料明細改為每頁 200 筆及前後換頁。首次失敗保留於
// artifacts/harness/runs/20261016-093720476-14d8f06e13d34b44ac2b371b7a26ad3f/contract-specialized.stdout.log。
// 同步舊的頁量與累加預期，保留排序、錯誤、重試及來源失效斷言。
// 審計員情境測試：明細表與右側資料預覽（收尾四項第一項第 2 批，2026-10-13）。
//
// 依據：使用者 2026-10-13 對第一項選「照建議做」，把只比對原始碼字串的 C# 測試改成執行正式前端腳本的行為測試。
// 本檔分兩部分，每個測試都載入正式的 wwwroot 腳本，操作畫面後看送出的請求、畫出的節點與 Store 狀態。
//
// 第一部分　情境 20「明細表：翻頁、排序、查號碼與金額」
//   審計員在第四步與第五步的各種明細表點表頭排序、輸入號碼查看、按清除；讀取中又按了兩次查看；讀取失敗；
//   看各處的金額；看各處一頁讀幾筆。
//   取代：PagedTableFrontendTests.SortAndSearch_ReloadFromTheBackendInsteadOfSortingInTheBrowser、
//         ScreenBehaviorReviewFrontendTests.Money_ShowsTwoDecimalsWithThousandsSeparatorThroughOneSharedFormatter
//         （兩條整檔禁令改成小原始碼檢查，不在本檔）、
//         ScreenBehaviorReviewFrontendTests.PagedTable_SearchDuringLoadRunsAfterTheCurrentLoadKeepingOnlyTheLast、
//         Batch9FrontendConstantsTests.PagingConsumers_UseTheMatchingSharedUiConstant（開發者面板那一行搬到保留的 PagingFifties_…）。
//
// 第二部分　情境 21「右側資料預覽」
//   審計員切換資料表；讀取失敗後重試；讀取途中切到別的資料表；重新匯入或換案件時舊請求晚到；
//   視窗縮窄再拉寬，期間自己收合或展開；用鍵盤操作「其他資料」選單。
//   取代：ApprovedUxFrontendTests.DataPreview_ReplacesStaleContentWithNamedLoadingAndInlineRetryableFailure、
//         FrontendUsabilityContractTests.Preview_Uses1360CompactBoundaryAndPreservesManualChoiceUntilViewportCrossesAgain
//         （JS 與 app.css 斷點一致改成小原始碼檢查，不在本檔）、
//         FrontendUsabilityContractTests.OtherDataMenu_UsesPersistentNativeControlsWithKeyboardAndAria；
//   另提供 FrontendHardeningContractTests.LatestResponseGuard_IsSharedAcrossPreviewMappingAndFilterConsumers 的資料預覽部分
//   （各部分都已補齊，2026-10-14 第二、三波整併已刪除）。
//
// 斷言原則（計畫第 1 節）：只有使用者裁定過的文字逐字比對，出處寫在該斷言旁；其他畫面文字只比對意思。
// 預期值一律寫字面值（例如明細每頁 200 筆），不讀正式常數；action payload 精確比對。
// 全部用合成資料。這一批不碰訊息面板的句子、後端原因句子與第五步的儲存、移除、重新檢查流程。
// 測試程序的記憶體與時間上限，見 support/resource-guard.cjs（2026-10-13）。
require('./support/resource-guard.cjs');
const { test } = require('node:test');
const assert = require('node:assert/strict');
const F = require('./support/frontend-fixtures.cjs');
const { unchecked } = require('./support/action-contract.cjs');

const { settle, plain } = F;

// ---------------------------------------------------------------------------
// 共用的小工具（只服務本檔；計畫第 7 節第 5 點，第 11 批再決定要不要收進支援模組）
// ---------------------------------------------------------------------------

// 第四步：用正式的 validate 步驟畫出驗證與預篩選結果，事件會冒泡，點表頭就走正式的分頁綁定。
function validationJourney(mutate = () => {}) {
  const f = F.usageJourneyFixture('validate', { bubbles: true });
  const data = F.batch8ValidationData();
  data.completenessTest.diffAccountCount = 1;
  data.completenessTest.diffAccounts = [{ accountCode: '1001', accountName: 'Synthetic cash', tbAmount: 1234.5, glAmount: 1000, diff: 234.5 }];
  data.completenessTest.eligibility = { isEligible: true, reason: null, warning: null };
  data.docBalanceTest = { unbalancedDocumentCount: 1, unbalancedDocuments: [{ documentNumber: 'JV-1', debit: 1234.5, credit: 1000, diff: 234.5 }] };
  data.infSamplingTest = { sampleSize: 3 };
  data.nullRecordsTest.nullDescriptionCount = 1; data.nullRecordsTest.outOfRangeDateCount = 1;
  data.nullRecordsTest.nullRows.push({ documentNumber: 'JV-9', accountCode: '1001', description: '', issues: ['description', 'date'] });
  mutate(data);
  f.store.setLastRun('validate', data);
  f.store.setLastRun('prescreen', Object.assign(F.remapPrescreen('p1'), { weekendActivity: { postingCount: 2, approvalCount: 0 } }));
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

// 第五步：remapFilterPreviewFixture 再加上矩陣分頁的請求記錄。
function filterJourney({ saved = [] } = {}) {
  const f = F.remapFilterPreviewFixture();
  for (const action of ['queryTagMatrixScenarios', 'queryTagMatrixVoucherPage', 'queryTagMatrixRowPage'])
    f.window.JetApi[action] = payload => new Promise((resolve, reject) => f.requests.push({ action, payload, resolve, reject }));
  if (saved.length) {
    f.store.setLastRun('validate', { resultRef: { runId: 'v1' }, completenessTest: { eligibility: { isEligible: true } } });
    f.store.setSavedScenarios(saved);
    f.store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' });
    f.click('[data-filter-pane-select="saved"]');
  }
  return { ...f, requestsOf: action => f.requests.filter(request => request.action === action) };
}

// 本檔點的「空白摘要」卡 2026-10-29 改用代號識別（原本以當時的卡片字母找卡片）。第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
async function draftVoucherList(f, rows = [{ documentNumber: 'SYN-B', postDate: '2025-01-02', hitRowCount: 1, totalRowCount: 2, voucherTotal: 1234.5 },
  { documentNumber: 'SYN-A', postDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: -10 }]) {
  f.click('[data-common-card="blankDescription"]'); f.click('[data-action="preview-scenario"]');
  f.request('filterPreview').resolve({ scenario: { count: 2, voucherCount: 2, summaryRowCount: 2, populationScope: 'auditPeriod' } }); await settle();
  const first = f.request('queryFilterVoucherPage');
  first.resolve({ queryRevision: 'q1', rows, nextCursor: 'v-next' }); await settle();
  return first;
}

// ===========================================================================
// 第一部分　情境 20：明細表
// ===========================================================================

// 第四步七張分頁明細表：點表頭排序、輸入號碼查看，都把排序與搜尋送給後端並從第一頁重讀。
// 每一列：明細識別標記、後端 action、排序鍵（點第一個可排序表頭）、payload 固定帶的其他鍵。
// 七張表共用同一組合成列，但每張表的後端資料列只有自己的欄位（tools/tests/fixtures/action-shapes.json）。
// 2026-10-15 前後端契合時改成只留該表後端會送的欄位；排序鍵的值仍決定先後，斷言不變。
const BACKEND_ROW_FIELDS = {
  queryCompletenessDiffPage: ['accountCode'],
  queryDocBalancePage: ['documentNumber'],
  queryNullRecordsPage: ['accountCode', 'documentNumber', 'description'],
  querySourceQualityPage: ['accountCode', 'documentNumber', 'sourceRowNumber', 'description']
};
function backendRow(action, row) {
  return Object.fromEntries(Object.entries(row).filter(([key]) => BACKEND_ROW_FIELDS[action].includes(key)));
}

const VALIDATION_TABLES = [
  ['completeness', 'queryCompletenessDiffPage', 'accountCode', {}],
  ['docBalance', 'queryDocBalancePage', 'documentNumber', {}],
  ['null-outOfRangeDate', 'queryNullRecordsPage', 'documentNumber', { category: 'outOfRangeDate' }],
  // 預篩選收斂計畫階段九：第四步的空白摘要只留預篩選訊號那一處，資料缺漏輔助檢視不再有它的明細頁；其餘三張空值明細照舊。
  // 第一次失敗：FrontendMapping 收據 20261024-195429066-9dbfcc75dada4db6b7c3bf147795f7e2。
  ['null-nullDocument', 'queryNullRecordsPage', 'documentNumber', { category: 'nullDocument' }],
  ['null-nullAccount', 'queryNullRecordsPage', 'documentNumber', { category: 'nullAccount' }],
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
      backendRow(action, { accountCode: '2002', documentNumber: 'JV-B', sourceRowNumber: 9, description: 'later' }),
      backendRow(action, { accountCode: '1001', documentNumber: 'JV-A', sourceRowNumber: 1, description: 'earlier' })], nextCursor: 'next-1' });
    await settle();
    const text = detail.querySelector('[data-page-query]').textContent;
    const later = text.indexOf(key === 'accountCode' ? '2002' : key === 'sourceRowNumber' ? '9' : 'JV-B');
    const earlier = text.indexOf(key === 'accountCode' ? '1001' : key === 'sourceRowNumber' ? '1' : 'JV-A');
    assert.ok(later >= 0 && earlier > later, 'rows stay in the backend order: ' + text);
    assert.equal(table.querySelector('th[data-sort-key="' + key + '"]').getAttribute('aria-sort'), 'ascending');

    // 輸入號碼查看：保留目前排序，從第一頁重讀。
    submitSearch(detail, '  JV-1  ');
    assert.equal(f.requestsOf(action).length, 2);
    assert.deepEqual(plain(f.requestsOf(action)[1].payload), { ...extra, cursor: null, pageSize: 200, sort: { key, direction: 'asc' }, search: 'JV-1' });
    f.requestsOf(action)[1].resolve({ rows: [backendRow(action, { accountCode: '1001', documentNumber: 'JV-1' })], nextCursor: 'next-2' }); await settle();

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
  const f = validationJourney(data => { data.nullRecordsTest.outOfRangeDateCount = 7053; data.nullRecordsTest.nullRows = []; });
  const detail = f.detailOf('null-outOfRangeDate');
  assert.equal(f.requestsOf('queryNullRecordsPage').length, 0, 'folded details do not read rows');
  f.toggleFor('[data-page-query="null-outOfRangeDate"]').fire('click');
  assert.deepEqual(plain(f.requestsOf('queryNullRecordsPage')[0].payload),
    { category: 'outOfRangeDate', cursor: null, pageSize: 200, sort: null, search: null });
  f.requestsOf('queryNullRecordsPage')[0].resolve({
    rows: Array.from({ length: 200 }, (_, i) => ({ documentNumber: 'APPROVAL-' + (i + 1), accountCode: '1001', description: 'Synthetic' })),
    nextCursor: 'after-200', totalCount: 7053
  }); await settle();
  assert.equal(detail.querySelectorAll('tbody tr').length, 200);
  assert.match(detail.querySelector('tbody').textContent, /APPROVAL-1/);
  assert.equal(detail.querySelector('[data-page-position]').textContent, '1/36');
  assert.equal(detail.querySelector('[data-page-previous]').disabled, true);
  assert.equal(detail.querySelector('[data-page-next]').disabled, false);
  assert.equal(detail.querySelector('.table-pager').hidden, false, 'more than 200 rows shows the pager');
});

// 2026-10-16：總筆數只在第一頁計算，帶游標的續頁回 totalCount null（docs/action-contract-manifest.md）。
// 換到第二頁後分頁列仍用第一頁的總筆數算頁數，下一頁照樣可按。
test('a continuation page without a total keeps the first page count on a step four detail table', async () => {
  const f = validationJourney(data => { data.nullRecordsTest.outOfRangeDateCount = 7053; data.nullRecordsTest.nullRows = []; });
  const detail = f.detailOf('null-outOfRangeDate');
  f.toggleFor('[data-page-query="null-outOfRangeDate"]').fire('click');
  const page = start => Array.from({ length: 200 }, (_, i) => ({ documentNumber: 'APPROVAL-' + (start + i), accountCode: '1001', description: 'Synthetic' }));
  f.requestsOf('queryNullRecordsPage')[0].resolve({ rows: page(1), nextCursor: 'after-200', totalCount: 7053 }); await settle();
  detail.querySelector('[data-page-next]').fire('click');
  assert.equal(f.requestsOf('queryNullRecordsPage')[1].payload.cursor, 'after-200');
  f.requestsOf('queryNullRecordsPage')[1].resolve({ rows: page(201), nextCursor: 'after-400', totalCount: null }); await settle();
  assert.match(detail.querySelector('tbody').textContent, /APPROVAL-201/);
  assert.equal(detail.querySelector('[data-page-position]').textContent, '2/36');
  assert.equal(detail.querySelector('[data-page-next]').disabled, false);
  assert.equal(detail.querySelector('.table-pager').hidden, false);
});

// 使用者 2026-10-16：「一頁至多200筆資料，如果超過200筆就分頁數」。只有一頁時不顯示分頁列；讀取完成前也不先顯示空的頁碼。
test('a detail table with at most 200 rows shows no pager, and the pager stays hidden until the first page arrives', async () => {
  const f = validationJourney(data => { data.nullRecordsTest.outOfRangeDateCount = 150; data.nullRecordsTest.nullRows = []; });
  const detail = f.detailOf('null-outOfRangeDate');
  f.toggleFor('[data-page-query="null-outOfRangeDate"]').fire('click');
  assert.equal(detail.querySelector('.table-pager').hidden, true, 'no page numbers before the first page is read');
  f.requestsOf('queryNullRecordsPage')[0].resolve({
    rows: Array.from({ length: 150 }, (_, i) => ({ documentNumber: 'SINGLE-' + (i + 1), accountCode: '1001', description: 'Synthetic' })),
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
  // 正式後端的抽樣明細沒有「金額」欄，借方與貸方分開；2026-10-15 前後端契合時把假欄位改成借方欄，預期的排序鍵跟著改。
  f.requestsOf('queryInfSamplePage')[0].resolve({ columns: [
    { key: 'documentNumber', label: '傳票號碼', valueType: 'text', sortable: true },
    { key: 'debit', label: '借方金額', valueType: 'money', sortable: true }], rows: [{ documentNumber: 'JV-S', debit: 1 }], nextCursor: 'inf-2' });
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
  f.requestsOf('queryPrescreenPage')[0].resolve({ rows: [{ documentNumber: 'JV-P', lineItem: '1', postDate: '2025-01-04', accountCode: '1001',
    accountName: 'Synthetic cash', documentDescription: 'weekend', amount: 1234.5, drCr: 'DEBIT' }], nextCursor: 'p-2' });
  await settle();
  const hits = f.root.querySelector('[data-prescreen-key="weekendPosting"]');
  hits.querySelector('.th-sort[data-sort-key="documentNumber"]').fire('click');
  assert.deepEqual(plain(f.requestsOf('queryPrescreenPage')[1].payload),
    { ruleKey: 'weekendPosting', cursor: null, pageSize: 200, sort: { key: 'documentNumber', direction: 'asc' }, search: null });
  f.requestsOf('queryPrescreenPage')[1].resolve({ rows: [], nextCursor: null }); await settle();
  submitSearch(hits, 'JV-P');
  assert.deepEqual(plain(f.requestsOf('queryPrescreenPage')[2].payload),
    { ruleKey: 'weekendPosting', cursor: null, pageSize: 200, sort: { key: 'documentNumber', direction: 'asc' }, search: 'JV-P' });
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
  f.requestsOf('queryFilterVoucherPage')[1].resolve({ queryRevision: 'q1', rows: [{ documentNumber: 'SYN-A', postDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: -10 }], nextCursor: null });
  await settle();
  assert.equal(host.querySelector('th[data-sort-key="voucherTotal"]').getAttribute('aria-sort'), 'ascending');

  const form = host.querySelector('[data-page-search]');
  form.querySelector('[data-page-search-input]').value = ' SYN ';
  form.onsubmit({ preventDefault() {} });
  assert.deepEqual(plain(f.requestsOf('queryFilterVoucherPage')[2].payload),
    { ...base, cursor: null, pageSize: 200, queryRevision: 'q1', sort: { key: 'voucherTotal', direction: 'asc' }, search: 'SYN' });
  f.requestsOf('queryFilterVoucherPage')[2].resolve({ queryRevision: 'q1', rows: [{ documentNumber: 'SYN-A', postDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: -10 }], nextCursor: null });
  await settle();

  host.querySelector('[data-page-search-clear]').onclick();
  assert.deepEqual(plain(f.requestsOf('queryFilterVoucherPage')[3].payload),
    { ...base, cursor: null, pageSize: 200, queryRevision: 'q1', sort: { key: 'voucherTotal', direction: 'asc' }, search: null });
});

test('step five saved scenario lists read two hundred vouchers and two hundred entries per page', async () => {
  const f = filterJourney({ saved: [F.savedBlankDescription()] });
  f.click('[data-action="toggle-scenario"][data-index="0"]');
  f.request('filterPreview').resolve({ scenario: { count: 2, voucherCount: 1, summaryRowCount: 2, populationScope: 'auditPeriod' } }); await settle();
  // 已儲存情境的傳票清單一頁 200 張（字面值），帶情境位置與版本。
  assert.deepEqual(plain(f.request('queryFilterVoucherPage').payload), { scenarioPosition: 1, scenarioRevision: 'r1', populationScope: 'auditPeriod', pageSize: 200 });
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [{ documentNumber: 'SAVED-1', postDate: '2025-01-01', hitRowCount: 1, totalRowCount: 2, voucherTotal: 5 }], nextCursor: null });
  await settle();
  f.$('[data-voucher-open="0"]').onclick();
  // 展開傳票的分錄一頁 200 筆（字面值）。
  assert.deepEqual(plain(f.request('queryFilterVoucherRowsPage').payload),
    { scenarioPosition: 1, scenarioRevision: 'r1', populationScope: 'auditPeriod', documentNumber: 'SAVED-1', cursor: null, pageSize: 200, queryRevision: 'q1' });
});

test('voucher pages replace 200 rows, restore the previous page and retry a failed whole-result sort', async () => {
  const f = filterJourney();
  f.click('[data-common-card="blankDescription"]'); f.click('[data-action="preview-scenario"]');
  f.request('filterPreview').resolve({ scenario: { count: 201, voucherCount: 201, summaryRowCount: 201, populationScope: 'auditPeriod' } }); await settle();
  const rows = Array.from({ length: 200 }, (_, i) => ({ documentNumber: 'V-' + (i + 1), postDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: i + 1 }));
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows, nextCursor: 'after-200', totalCount: 201 }); await settle();
  const host = f.$('.filter-voucher-host');
  assert.equal(host.querySelectorAll('tbody tr').length, 200); assert.equal(host.querySelector('[data-page-position]').textContent, '1/2');
  host.querySelector('[data-voucher-next]').onclick();
  assert.equal(f.request('queryFilterVoucherPage').payload.cursor, 'after-200');
  assert.equal(host.querySelector('[data-voucher-next]').disabled, true);
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [{ ...rows[0], documentNumber: 'V-201' }], nextCursor: null, totalCount: 201 }); await settle();
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
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [{ ...rows[0], documentNumber: 'V-201' }], nextCursor: null, totalCount: 201 }); await settle();
  host.querySelector('[data-voucher-previous]').onclick(); assert.equal(f.request('queryFilterVoucherPage').payload.cursor, null);
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows, nextCursor: 'sorted-200', totalCount: 201 }); await settle();
  assert.equal(host.querySelectorAll('tbody tr').length, 200); assert.equal(host.querySelector('[data-page-position]').textContent, '1/2');
});

// 2026-10-16：第五步傳票清單的續頁同樣不回總筆數；換頁後仍顯示第一頁算出的總頁數。
test('a voucher continuation page without a total keeps the first page count', async () => {
  const f = filterJourney();
  f.click('[data-common-card="blankDescription"]'); f.click('[data-action="preview-scenario"]');
  f.request('filterPreview').resolve({ scenario: { count: 401, voucherCount: 401, summaryRowCount: 401, populationScope: 'auditPeriod' } }); await settle();
  const rows = start => Array.from({ length: 200 }, (_, i) => ({ documentNumber: 'V-' + (start + i), postDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: 1 }));
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
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [{ documentNumber: 'AFTER-REDRAW', postDate: '2025-01-01', hitRowCount: 1, totalRowCount: 1, voucherTotal: 1 }], nextCursor: null, totalCount: 201 });
  await settle();
  assert.match(host.querySelector('tbody').textContent, /AFTER-REDRAW/);
  assert.doesNotMatch(host.querySelector('tbody').textContent, /SYN-B/);
  assert.equal(host.querySelector('table').getAttribute('aria-busy'), 'false');
  assert.equal(host.querySelector('[data-voucher-previous]').disabled, false);
  assert.equal(host.querySelector('[data-voucher-next]').disabled, true);
});

// 原本前半段核對「完整命中分錄」表：只在後端沒有回傳傳票清單時才出現，正式後端一律回傳，所以走不到。
// 使用者 2026-10-19 裁定刪除這段畫面，這裡的檢查一併移除。第一次失敗：20261019-090901211-9e9ec7ffcbf34596ae2d8476cf613b45。
test('step five risk matrix tables sort and search on the backend from the first page', async () => {
  const f = filterJourney({ saved: [F.savedBlankDescription()] });

  // 高風險條件矩陣的傳票表：一打開就讀第一頁，排序與查看都從第一頁重讀。
  f.click('[data-action="toggle-matrix"]');
  f.request('queryTagMatrixScenarios').resolve({ scenarios: [{ position: 1, name: 'G', voucherHitCount: 1, rowHitCount: 2, summaryRowCount: 2 }] }); await settle();
  for (const [view, action, key] of [['vouchers', 'queryTagMatrixVoucherPage', 'voucherTotal']]) {
    f.click('[data-matrix-view="' + view + '"]'); await settle();
    assert.deepEqual(plain(f.requestsOf(action)[0].payload), { cursor: null, pageSize: 200, sort: null, search: null }, view);
    f.requestsOf(action)[0].resolve({ rows: [], nextCursor: null }); await settle();
    const box = f.$('[data-bind="matrix-' + view + '"]'), boxTable = box.querySelector('.preview-table');
    boxTable.fire('click', { target: boxTable.querySelector('.th-sort[data-sort-key="' + key + '"]') });
    assert.deepEqual(plain(f.requestsOf(action)[1].payload), { cursor: null, pageSize: 200, sort: { key, direction: 'asc' }, search: null }, view);
    f.requestsOf(action)[1].resolve({ rows: [], nextCursor: null }); await settle();
    submitSearch(box, 'M-1');
    assert.deepEqual(plain(f.requestsOf(action)[2].payload), { cursor: null, pageSize: 200, sort: { key, direction: 'asc' }, search: 'M-1' }, view);
  }
});

test('risk matrix retains the full long scenario title and has no entry-detail tab or row query', async () => {
  const name = '合成的長條件名稱用來確認標題仍可完整閱讀與安全轉義 <b>條件</b> ' + '跨期間傳票篩選條件'.repeat(12);
  const f = filterJourney({ saved: [F.savedBlankDescription(name)] });
  f.click('[data-action="toggle-matrix"]');
  f.request('queryTagMatrixScenarios').resolve({ scenarios: [{ position: 1, name, voucherHitCount: 201, rowHitCount: 401, summaryRowCount: 401 }] });
  await settle();
  assert.equal(f.$('[data-matrix-view="rows"]'), null);
  assert.equal(f.$('[data-bind="matrix-rows"]'), null);
  f.click('[data-matrix-view="vouchers"]'); await settle();
  f.request('queryTagMatrixVoucherPage').resolve({ rows: [], nextCursor: null, totalCount: 0 }); await settle();
  const header = f.$('[data-bind="matrix-vouchers"] th[title]');
  assert.ok(header); assert.equal(header.getAttribute('title'), name);
  assert.equal(header.querySelector('.filter-matrix-name').textContent, name);
  assert.equal(header.querySelector('b'), null, 'title text never becomes markup');
  // 使用者 2026-10-16 選定交叉表表頭只顯示代號：名稱只留給停留提示與輔助技術，不佔欄寬。
  assert.match(header.getAttribute('class'), /preview-table__tag/);
  assert.match(header.querySelector('.filter-matrix-name').getAttribute('class'), /visually-hidden/);
  assert.equal(header.textContent.slice(0, header.textContent.length - name.length), 'C1');
  assert.equal(f.requestsOf('queryTagMatrixRowPage').length, 0);
});

// 讀取中按「查看」或「清除」：2026-10-02 整體複審 W18 改成不再忽略，只記住最後一次，讀完後接著做。
// 狀態列的說明句沒有找到使用者逐字裁定，只比對意思（讀完後接著查看或清除）。
test('searching while a table is reading waits for the current read and keeps only the last text, even after a failure', async () => {
  const f = validationJourney(), detail = f.detailOf('docBalance');
  const status = () => detail.querySelector('[data-page-status]');
  f.toggleFor('[data-page-query="docBalance"]').fire('click');
  assert.equal(f.requestsOf('queryDocBalancePage').length, 1);

  submitSearch(detail, 'A'); submitSearch(detail, 'B');
  assert.equal(f.requestsOf('queryDocBalancePage').length, 1, 'the second and third clicks wait for the current read');
  assert.match(status().textContent, /讀完後/); assert.match(status().textContent, /查看/);
  assert.equal(status().hidden, false);

  f.requestsOf('queryDocBalancePage')[0].reject(new Error('synthetic read failure')); await settle();
  assert.equal(f.requestsOf('queryDocBalancePage').length, 2, 'the remembered search runs after the failed read');
  assert.deepEqual(plain(f.requestsOf('queryDocBalancePage')[1].payload), { cursor: null, pageSize: 200, sort: null, search: 'B' });
  assert.equal(f.requests.some(request => request.payload && request.payload.search === 'A'), false, 'only the last text is kept');

  // 清除的版本：讀取中按清除，讀完後接著用空白條件從第一頁重讀。
  f.requestsOf('queryDocBalancePage')[1].resolve({ rows: [{ documentNumber: 'B-1' }], nextCursor: 'b-2' }); await settle();
  detail.querySelector('[data-page-next]').fire('click');
  assert.deepEqual(plain(f.requestsOf('queryDocBalancePage')[2].payload), { cursor: 'b-2', pageSize: 200, sort: null, search: 'B' });
  detail.querySelector('[data-page-search-clear]').fire('click');
  assert.equal(f.requestsOf('queryDocBalancePage').length, 3);
  assert.match(status().textContent, /讀完後/); assert.match(status().textContent, /清除/);
  f.requestsOf('queryDocBalancePage')[2].resolve({ rows: [], nextCursor: null }); await settle();
  assert.equal(f.requestsOf('queryDocBalancePage').length, 4);
  assert.deepEqual(plain(f.requestsOf('queryDocBalancePage')[3].payload), { cursor: null, pageSize: 200, sort: null, search: null });
});

// 2026-09-23：重試保留原請求，不受失敗後退回的畫面狀態影響。排序或查看失敗時表頭與輸入框退回上一次成功的狀態，
// 表格裡仍是舊列；換了驗證結果後晚到的列不顯示，讀取中記住的查看也不再送出。
test('a failed sort or search keeps the old rows, restores the header and box, and retry resends the exact request', async () => {
  const f = validationJourney(), detail = f.detailOf('docBalance');
  const table = detail.querySelector('[data-page-query]').closest('table'), input = detail.querySelector('[data-page-search-input]');
  const sortButton = table.querySelector('.th-sort[data-sort-key="debit"]');
  const before = detail.querySelector('[data-page-query]').textContent;
  sortButton.fire('click');
  f.requestsOf('queryDocBalancePage')[0].reject(new Error('synthetic sort failure')); await settle();
  assert.equal(detail.querySelector('[data-page-query]').textContent, before, 'the old rows stay');
  assert.equal(table.querySelector('th[data-sort-key="debit"]').getAttribute('aria-sort'), 'none', 'the header no longer claims the failed sort');
  submitSearch(detail, 'JV-404');
  assert.deepEqual(plain(f.requestsOf('queryDocBalancePage')[1].payload), { cursor: null, pageSize: 200, sort: null, search: 'JV-404' });
  f.requestsOf('queryDocBalancePage')[1].reject(new Error('synthetic search failure')); await settle();
  assert.equal(input.value, '', 'the search box returns to the last successful search');
  const status = detail.querySelector('[data-page-status]');
  assert.match(status.textContent, /失敗/); assert.match(status.textContent, /重試/);
  const retry = detail.querySelectorAll('button').find(button => button.textContent === '重試' && !button.hidden);
  assert.ok(retry, 'a retry button is shown next to the table');
  retry.fire('click');
  assert.deepEqual(plain(f.requestsOf('queryDocBalancePage')[2].payload), { cursor: null, pageSize: 200, sort: null, search: 'JV-404' });
  assert.equal(input.value, 'JV-404');
  f.requestsOf('queryDocBalancePage')[2].resolve({ rows: [{ documentNumber: 'JV-404' }], nextCursor: null }); await settle();
  assert.match(detail.querySelector('[data-page-query]').textContent, /JV-404/);
  assert.equal(f.store.getState().busy, false);

  // 讀取中換了驗證結果：晚到的列不顯示，讀取中按下的查看也不送出。
  sortButton.fire('click'); submitSearch(detail, 'LATER');
  const count = f.requests.length, shown = detail.querySelector('[data-page-query]').textContent;
  f.store.setLastRun('validate', F.batch8ValidationData('v-new'));
  f.requestsOf('queryDocBalancePage').at(-1).resolve({ rows: [{ documentNumber: 'STALE-ROW' }], nextCursor: null }); await settle();
  assert.equal(detail.querySelector('[data-page-query]').textContent, shown);
  assert.doesNotMatch(detail.querySelector('[data-page-query]').textContent, /STALE-ROW/);
  assert.equal(f.requests.length, count, 'the remembered search is dropped with the old result');
});

// 金額顯示：使用者 2026-10-02 裁定 P3-4「畫面統一兩位小數加千分位；報表與底稿不變」
// （出處：src/JET/JET/wwwroot/js/ui-core.js 的 money 函式註解，以及原 C# 測試 ScreenBehaviorReviewFrontendTests 的註解）。
// 1,234.50、-10.00 這類顯示結果是裁定格式的直接例子，逐字比對。
test('money shows two decimals with thousands separators on step four tables, the overview and step four queried pages', async () => {
  const f = validationJourney();
  assert.match(f.detailOf('completeness').querySelector('[data-page-query]').textContent, /1,234\.50/);
  assert.match(f.detailOf('docBalance').querySelector('[data-page-query]').textContent, /1,234\.50/);

  // 展開後讀取的第一頁同樣格式。
  const detail = f.detailOf('completeness');
  f.toggleFor('[data-page-query="completeness"]').fire('click');
  f.requestsOf('queryCompletenessDiffPage')[0].resolve({ rows: [{ accountCode: '3003', accountName: 'x', tbAmount: 1234567.891, glAmount: -10, diff: 0.003 }], nextCursor: null });
  await settle();
  const loaded = detail.querySelector('[data-page-query]').textContent;
  assert.match(loaded, /1,234,567\.89/); assert.match(loaded, /-10\.00/);
  // 極小的非零差額不顯示成 0.00（只比對意思）。原 C# 測試標為 L46，第一次失敗收據 20261004-091831760-a762b9ef96db4d61a4bb2bff7cdb0022；
  // 摘要列與第一頁的差額另由 frontend-workflow.test.cjs「batch8 tiny nonzero differences stay visible ...」測試。
  assert.match(loaded, /不足 0\.01/);

  // 第一頁的差額欄同樣不顯示成 0.00；負的極小差額說明是負值（只比對意思，不看表格上方的說明句）。
  const tiny = validationJourney(data => {
    data.completenessTest.diffAccounts[0].diff = 0.003; data.docBalanceTest.unbalancedDocuments[0].diff = -0.003;
  });
  assert.match(tiny.detailOf('completeness').querySelector('[data-page-query]').textContent, /不足 0\.01/);
  assert.match(tiny.detailOf('docBalance').querySelector('[data-page-query]').textContent, /負值.*不足 0\.01/);

  // 預篩選命中的第一頁與後續頁。
  f.toggleFor('[data-prescreen-key="weekendPosting"]').fire('click');
  f.requestsOf('queryPrescreenPage')[0].resolve({ rows: [{ documentNumber: 'JV-P', postDate: '2025-01-04', accountCode: '1001', amount: 1234.5, drCr: 'DEBIT' }], nextCursor: 'p-2' });
  await settle();
  const hits = f.root.querySelector('[data-prescreen-key="weekendPosting"]');
  assert.match(hits.querySelector('tbody').textContent, /1,234\.50/);
  hits.querySelector('[data-page-next]').fire('click');
  f.requestsOf('queryPrescreenPage')[1].resolve({ rows: [{ documentNumber: 'JV-Q', postDate: '2025-01-05', accountCode: '1001', amount: -10, drCr: 'CREDIT' }], nextCursor: null });
  await settle();
  assert.match(hits.querySelector('tbody').textContent, /-10\.00/);

  // 抽樣明細的金額欄與金額型額外欄位（後端欄位定義標 money）。
  f.toggleFor('[data-dynamic-page="inf"]').fire('click');
  // 抽樣明細的金額欄是借方與貸方（正式後端的欄位；2026-10-15 前後端契合前寫成後端沒有的 amount）。
  f.requestsOf('queryInfSamplePage')[0].resolve({ columns: [
    { key: 'documentNumber', label: 'Doc', valueType: 'text' }, { key: 'debit', label: 'Debit', valueType: 'money' },
    { key: 'rde.extra', label: 'Extra', valueType: 'money', isCustom: true }],
  rows: [{ documentNumber: 'JV-S', debit: 1234.5, customValues: { 'rde.extra': -10 } }], nextCursor: null });
  await settle();
  const sample = f.root.querySelector('[data-dynamic-page="inf"]').textContent;
  assert.match(sample, /1,234\.50/); assert.match(sample, /-10\.00/);

  // 流程總覽的借方與貸方合計。
  const overview = F.batch8Fixture(), data = F.batch8ValidationData();
  data.stats.totalDebit = 1234.5; data.stats.totalCredit = 1234.5;
  overview.store.setLastRun('validate', data);
  assert.equal((overview.app.overviewPopulationHtml(overview.store.getState()).match(/1,234\.50/g) || []).length, 2);
});

test('money shows two decimals on step five voucher lists, entry details and risk matrix while condition values stay as typed', async () => {
  const f = filterJourney();
  await draftVoucherList(f);
  const host = f.$('.filter-voucher-host');
  const totals = host.querySelector('tbody').textContent;
  assert.match(totals, /1,234\.50/); assert.match(totals, /-10\.00/);

  host.querySelector('[data-voucher-open="0"]').onclick();
  // 正式後端的分錄金額一定有值；第二列刻意給 null，確認畫面寫「—」而不是 0.00，所以這筆不核對。
  f.request('queryFilterVoucherRowsPage').resolve(unchecked('分錄金額刻意給 null，確認缺值的寫法', { columns: [{ key: 'rde.extra', label: 'Extra', valueType: 'money', isCustom: true }],
    rows: [{ lineItem: '1', isHit: true, amount: -10, customValues: { 'rde.extra': 1234.5 } }, { lineItem: '2', isHit: false, amount: null, customValues: { 'rde.extra': null } }], nextCursor: null }));
  await settle();
  const rows = host.querySelectorAll('.voucher-details tbody tr');
  assert.match(rows[0].textContent, /-10\.00/); assert.match(rows[0].textContent, /1,234\.50/);
  assert.match(rows[1].textContent, /—/, 'a missing amount shows a dash instead of 0.00');
  assert.doesNotMatch(rows[1].textContent, /0\.00/);

  // 已儲存情境：條件讀回保留審計員輸入的值，不加千分位、不補小數；矩陣金額照畫面格式。
  const s = F.savedBlankDescription('合成金額'); s.source = 'custom'; delete s.editorOrigins;
  s.groups = [{ join: 'AND', matchScope: 'row', rules: [{ type: 'numRange', join: 'AND', from: '1234.5', to: '' }] }];
  const g = filterJourney({ saved: [s] });
  const readback = g.dom.content.textContent;
  assert.match(readback, /≥ 1234\.5/); assert.doesNotMatch(readback, /1,234\.5/);
  g.click('[data-action="toggle-matrix"]');
  g.request('queryTagMatrixScenarios').resolve({ scenarios: [{ position: 1, name: '合成金額', voucherHitCount: 1, rowHitCount: 1, summaryRowCount: 1 }] }); await settle();
  g.click('[data-matrix-view="vouchers"]'); await settle();
  g.request('queryTagMatrixVoucherPage').resolve({ rows: [{ documentNumber: 'M-1', postDate: '2025-01-01', createdBy: 'U', voucherTotal: 1234.5 }], nextCursor: null }); await settle();
  assert.match(g.$('[data-bind="matrix-vouchers"] tbody').textContent, /1,234\.50/);
  assert.equal(g.$('[data-matrix-view="rows"]'), null, 'the removed entry-detail tab stays absent');
  assert.equal(g.requestsOf('queryTagMatrixRowPage').length, 0);
});

test('report history shows fifty files per page and the mapping value sample reads fifty source values', () => {
  // 第六步版本紀錄一頁 50 份（字面值）。
  const f = F.usageJourneyFixture('export');
  f.store.setReportArtifacts(Array.from({ length: 51 }, (_, i) => ({ artifactId: 'paper-' + i, kind: 'workingPaper', fileName: 'Synthetic-' + i + '.xlsx',
    generatedUtc: '2026-01-01T00:00:00Z', stale: false, fileState: 'asPublished', sourceRef: {} })));
  const root = f.draw();
  assert.equal(root.querySelectorAll('[data-open-artifact]').length, 50);
  // 使用者 2026-10-16 要求換頁設計統一：版本紀錄也用左箭頭、目前頁數/總頁數、右箭頭。
  assert.equal(root.querySelector('.table-pager [data-page-position]').textContent, '1/2');
  root.querySelector('[data-history-page="1"]').fire('click');
  const second = f.draw();
  assert.equal(second.querySelectorAll('[data-open-artifact]').length, 1);
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
  f.request('queryDataPreview').resolve({ columns: ['documentNumber'], rows: [['OLD-ROW']], totalCount: 1 }); await settle();
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

  f.request('queryDataPreview').resolve({ columns: ['documentNumber'], rows: [['NEW-ROW']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /NEW-ROW/);
  assert.equal(f.body().getAttribute('aria-busy'), null);
});

// 使用者 2026-10-19 要求前端不露出系統用詞：GL 與未納入測試的分錄，借貸欄和結果表格一樣寫「借」「貸」。
test('the GL preview shows debit and credit as 借 and 貸 instead of the stored English values', async () => {
  const f = await previewOpened();
  f.request('queryDataPreview').resolve({ columns: ['documentNumber'], rows: [['FIRST']], totalCount: 1 }); await settle();
  for (const dataset of ['gl', 'glExcluded']) {
    tab(f, dataset).fire('click');
    previewRequests(f).at(-1).resolve({ columns: ['documentNumber', 'drCr'], rows: [['JV-1', 'DEBIT'], ['JV-1', 'CREDIT']], totalCount: 2 });
    await settle();
    const cells = f.body().querySelectorAll('td').map(cell => cell.textContent);
    assert.deepEqual(cells, ['JV-1', '借', 'JV-1', '貸'], dataset);
    assert.doesNotMatch(f.body().textContent, /DEBIT|CREDIT/, dataset);
  }
});

// 使用者 2026-10-22 裁定資料預覽依配對列欄位、欄名和欄位配對畫面相同，金額固定顯示四位小數（預篩選收斂計畫階段五）。
// GL 與 GL(未納入測試) 的欄名與型別讀回應的 columnDetails：金額欄靠右並補成四位小數，傳票文件項次、科目編號這類
// 看起來像數字的文字照樣靠左；人工或自動分錄兩頁都寫中文。第一次失敗：FrontendMapping 收據 20261023-113338743-9460c2825756428082a55c788b6229eb。
test('the GL pages take column names and alignment from the column details and show amounts with four decimals', async () => {
  const f = await previewOpened();
  f.request('queryDataPreview').resolve({ columns: ['documentNumber'], rows: [['FIRST']], totalCount: 1 }); await settle();
  const columnDetails = [
    { key: 'documentNumber', label: '傳票號碼', valueType: 'text' },
    { key: 'lineItem', label: '傳票文件項次', valueType: 'text' },
    { key: 'postDate', label: '總帳日期', valueType: 'date' },
    { key: 'accountCode', label: '會計科目編號', valueType: 'text' },
    { key: 'amount', label: '金額', valueType: 'money' },
    { key: 'manualAuto', label: '人工/自動分錄', valueType: 'text' },
    { key: 'rde.0123456789abcdef0123456789abcdef', label: '合成外幣金額', valueType: 'money' }
  ];
  const rows = [
    ['JV-1', '1', '2025-03-01', '1101', '1234.5678', 'manual', '12.34'],
    ['JV-1', '2', '2025-03-01', '4101', '-1000', 'automatic', null]
  ];
  const stats = { amountAbsMin: '1000', amountAbsMax: '1234.5678', postDateMin: '2025-03-01', postDateMax: '2025-03-01', voucherCount: 1 };
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

// 2026-10-28 複審：前端說明寫人工或自動分錄沒有判定時顯示「—」，這裡鎖住畫面實際的寫法。程式本來就這樣顯示，第一次就通過，沒有第一次失敗的收據。
test('the GL pages show an undetermined manual or automatic entry as a dash', async () => {
  const f = await previewOpened();
  f.request('queryDataPreview').resolve({ columns: ['documentNumber'], rows: [['FIRST']], totalCount: 1 }); await settle();
  const columnDetails = [
    { key: 'documentNumber', label: '傳票號碼', valueType: 'text' },
    { key: 'manualAuto', label: '人工/自動分錄', valueType: 'text' }
  ];
  for (const dataset of ['gl', 'glExcluded']) {
    tab(f, dataset).fire('click');
    previewRequests(f).at(-1).resolve({ dataset, columns: ['documentNumber', 'manualAuto'], columnDetails,
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
  previewRequests(f)[2].resolve({ columns: ['accountCode'], rows: [['1001']], totalCount: 1 }); await settle();
  assert.ok(body.querySelector('[data-action="retry-data-preview"]') === null, 'the retry button goes away after success');
  assert.match(body.textContent, /1001/);
  assert.equal(f.store.getState().busy, false);
});

test('source preview uses 200-row offset pages for 201 rows and previous returns to the first page', async () => {
  const f = await previewOpened();
  const firstRows = Array.from({ length: 200 }, (_, i) => ['SOURCE-' + (i + 1)]);
  previewRequests(f)[0].resolve({ columns: ['documentNumber'], rows: firstRows, totalCount: 201 }); await settle();
  assert.equal(f.body().querySelectorAll('tbody tr').length, 200);
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '1/2');
  assert.equal(f.aside().querySelector('[data-page-previous]').disabled, true);
  f.aside().querySelector('[data-page-next]').fire('click');
  assert.deepEqual(plain(previewRequests(f)[1].payload), { dataset: 'pbcGl', limit: 200, offset: 200 });
  previewRequests(f)[1].resolve({ columns: ['documentNumber'], rows: [['SOURCE-201']], totalCount: 201 }); await settle();
  assert.equal(f.body().querySelectorAll('tbody tr').length, 1);
  assert.equal(f.body().querySelector('tbody').textContent, 'SOURCE-201');
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '2/2');
  assert.equal(f.aside().querySelector('[data-page-next]').disabled, true);
  f.aside().querySelector('[data-page-previous]').fire('click');
  assert.deepEqual(plain(previewRequests(f)[2].payload), { dataset: 'pbcGl', limit: 200, offset: 0 });
  previewRequests(f)[2].resolve({ columns: ['documentNumber'], rows: firstRows, totalCount: 201 }); await settle();
  assert.equal(f.body().querySelectorAll('tbody tr').length, 200);
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '1/2');
  assert.equal(f.body().querySelector('.th-sort'), null, 'the preview does not offer sorting only the current page');
});

// 2026-10-16：資料預覽的總筆數與統計只在 offset 0 計算，續頁兩者都是 null；畫面沿用第一頁的頁數、列數與統計。
test('a preview continuation page without a total or stats keeps the first page count and summary', async () => {
  const f = await previewOpened();
  f.request('queryDataPreview').resolve({ columns: ['documentNumber'], rows: [['PBC-ROW']], totalCount: 1 }); await settle();
  tab(f, 'gl').fire('click');
  // 使用者 2026-10-22 裁定資料預覽的金額固定四位小數，統計列的金額改用字串照存下的值傳（預篩選收斂計畫階段五）；
  // 原本這裡給數字，第一次失敗是假回應和正式後端的形狀對不上：FrontendMapping 收據 20261023-113338743-9460c2825756428082a55c788b6229eb。
  const stats = { amountAbsMin: '1', amountAbsMax: '9', postDateMin: '2025-01-01', postDateMax: '2025-12-31', voucherCount: 3 };
  previewRequests(f)[1].resolve({ columns: ['documentNumber'], rows: Array.from({ length: 200 }, (_, i) => ['GL-' + (i + 1)]), totalCount: 401, stats }); await settle();
  const note = () => f.aside().querySelector('.data-preview__note');
  const firstNote = note().textContent;
  // 使用者 2026-10-19 裁定「來源檔用列，分錄用筆」：GL 的筆數單位改成「筆」。
  // 第一次失敗：FrontendMapping 收據 20261019-122433278-c327738e178a4e6e829f666f3c1bd5c1。
  assert.match(firstNote, /共 401 筆/); assert.match(firstNote, /傳票 3 張/);
  assert.equal(note().querySelector('[data-page-position]').textContent, '1/3');
  note().querySelector('[data-page-next]').fire('click');
  assert.deepEqual(plain(previewRequests(f)[2].payload), { dataset: 'gl', limit: 200, offset: 200 });
  previewRequests(f)[2].resolve({ columns: ['documentNumber'], rows: [['GL-201']], totalCount: null, stats: null }); await settle();
  assert.equal(f.body().querySelector('tbody').textContent, 'GL-201');
  assert.equal(note().querySelector('[data-page-position]').textContent, '2/3');
  assert.match(note().textContent, /共 401 筆/); assert.match(note().textContent, /傳票 3 張/);
  assert.equal(note().querySelector('[data-page-next]').disabled, false);
});

test('source preview next-page failure retains rows and page number while retry keeps the failed offset', async () => {
  const f = await previewOpened();
  previewRequests(f)[0].resolve({ columns: ['documentNumber'], rows: [['KEPT-PREVIEW']], totalCount: 201 }); await settle();
  f.aside().querySelector('[data-page-next]').fire('click');
  assert.equal(f.body().querySelector('tbody').textContent, 'KEPT-PREVIEW');
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '1/2');
  assert.equal(f.aside().querySelector('[data-page-next]').disabled, true);
  previewRequests(f)[1].reject(new Error('synthetic second-page failure')); await settle();
  assert.equal(f.body().querySelector('tbody').textContent, 'KEPT-PREVIEW');
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '1/2');
  f.body().querySelector('[data-action="retry-data-preview"]').fire('click');
  assert.deepEqual(plain(previewRequests(f)[2].payload), { dataset: 'pbcGl', limit: 200, offset: 200 });
  previewRequests(f)[2].resolve({ columns: ['documentNumber'], rows: [['RETRIED-PREVIEW']], totalCount: 201 }); await settle();
  assert.equal(f.body().querySelector('tbody').textContent, 'RETRIED-PREVIEW');
  assert.equal(f.aside().querySelector('[data-page-position]').textContent, '2/2');
  assert.equal(f.body().querySelector('[data-action="retry-data-preview"]'), null);
});

test('a late preview response is dropped after switching dataset, re-importing or switching case', async () => {
  // 切到別的資料表：A 還沒回來就切到 B，A 晚到的結果（成功或失敗）都不顯示。
  const f = await previewOpened();
  tab(f, 'gl').fire('click'); tab(f, 'tb').fire('click');
  const [, a, b] = previewRequests(f);
  a.resolve({ columns: ['documentNumber'], rows: [['FROM-GL']], totalCount: 1 }); await settle();
  assert.doesNotMatch(f.body().textContent, /FROM-GL/);
  assert.equal(f.body().getAttribute('aria-busy'), 'true', 'still waiting for TB');
  b.resolve({ columns: ['accountCode'], rows: [['FROM-TB']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /FROM-TB/);

  // 重新匯入：舊請求晚到不顯示，畫面自動重讀新資料。
  tab(f, 'gl').fire('click');
  const beforeImport = previewRequests(f).at(-1);
  f.store.setImportResult('gl', { batchId: 'reimported', columns: [], rowCount: 1 }); await settle();
  const afterImport = previewRequests(f).at(-1);
  assert.ok(afterImport !== beforeImport, 'the new data is read again');
  assert.deepEqual(plain(afterImport.payload), { dataset: 'gl', offset: 0, limit: 200 });
  beforeImport.resolve({ columns: ['documentNumber'], rows: [['BEFORE-IMPORT']], totalCount: 1 }); await settle();
  assert.doesNotMatch(f.body().textContent, /BEFORE-IMPORT/);
  afterImport.resolve({ columns: ['documentNumber'], rows: [['AFTER-IMPORT']], totalCount: 1 }); await settle();
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
  newCase.resolve({ columns: ['documentNumber'], rows: [['SECOND-CASE']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /SECOND-CASE/);
});

test('asking twice for the same dataset shows only the latest answer, and a re-import or case switch while folded drops the old answer', async () => {
  // 同一份資料表連按兩次：後一次的結果先回來，前一次晚到也不覆蓋。
  const f = await previewOpened();
  tab(f, 'gl').fire('click'); tab(f, 'gl').fire('click');
  const [, first, second] = previewRequests(f);
  second.resolve({ columns: ['documentNumber'], rows: [['LATEST']], totalCount: 1 }); await settle();
  first.resolve({ columns: ['documentNumber'], rows: [['EARLIER']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /LATEST/);
  assert.doesNotMatch(f.body().textContent, /EARLIER/);

  // 讀取途中收合面板，接著重新匯入：收合時不重讀，舊請求晚到也不顯示；展開後才讀新資料。
  tab(f, 'tb').fire('click');
  const beforeImport = previewRequests(f).at(-1), count = previewRequests(f).length;
  f.$('.data-preview__collapse').fire('click');
  f.store.setImportResult('tb', { batchId: 'reimported-tb', columns: [], rowCount: 1 }); await settle();
  assert.equal(previewRequests(f).length, count, 'nothing is read while the preview is folded');
  beforeImport.resolve({ columns: ['accountCode'], rows: [['OLD-TB']], totalCount: 1 }); await settle();
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
  oldCase.resolve({ columns: ['documentNumber'], rows: [['OLD-CASE-ROW']], totalCount: 1 }); await settle();
  assert.doesNotMatch(f.body().textContent, /OLD-CASE-ROW/);
  f.$('.data-preview__rail').fire('click'); await settle();
  assert.deepEqual(plain(previewRequests(f).at(-1).payload), { dataset: 'pbcGl', offset: 0, limit: 200 }, 'expanding reads the new case from the first tab');
});

// 使用者 2026-10-22 裁定資料預覽的金額固定顯示四位小數，例如 1,234.5000，只改資料預覽（預篩選收斂計畫階段五）。
// 原本這裡斷言兩位小數，出處是使用者 2026-10-02 裁定 P3-4「畫面統一兩位小數加千分位」，其他畫面維持兩位。
// 後端照存下的值傳精確的字串，超大金額補零時不經過浮點數。第一次失敗：FrontendMapping 收據 20261023-113338743-9460c2825756428082a55c788b6229eb。
test('preview amounts on standardized data show four decimals exactly as stored while PBC source values stay as imported', async () => {
  const f = await previewOpened();
  // 資料預覽的每一格都是字串，和正式後端相同（2026-10-15 前後端契合前這裡給數字）。
  f.request('queryDataPreview').resolve({ columns: ['documentNumber', 'amount'], rows: [['JV-1', '1234.5']], totalCount: 1 }); await settle();
  assert.match(f.body().textContent, /1234\.5/); assert.doesNotMatch(f.body().textContent, /1,234\.5/);
  const columnDetails = [{ key: 'documentNumber', label: '傳票號碼', valueType: 'text' }, { key: 'amount', label: '金額', valueType: 'money' }];
  for (const [dataset, column] of [['gl', 'amount'], ['glExcluded', 'amount'], ['tb', 'changeAmount']]) {
    f.ui.openDataPreview(dataset);
    f.request('queryDataPreview').resolve({ columns: ['documentNumber', column], ...(dataset === 'tb' ? {} : { columnDetails }),
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
  assert.deepEqual(items().map(item => item.getAttribute('data-ds')), ['glExcluded', 'authorizedPreparers', 'dateDimension', 'schemaOverview']);
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
  const chosen = active(); assert.equal(chosen.getAttribute('data-ds'), 'authorizedPreparers');
  chosen.dispatch('click'); // 原生按鈕按 Enter 時瀏覽器送出 click。
  assert.deepEqual(plain(previewRequests(f).at(-1).payload), { dataset: 'authorizedPreparers', offset: 0, limit: 200 });
  assert.equal(toggle.getAttribute('aria-expanded'), 'false'); assert.equal(menu.hidden, true);
  assert.ok(active() === toggle, 'choosing from the menu returns focus to the toggle');
  assert.equal(toggle.classList.contains('is-active'), true);
  assert.ok(tabs.querySelector('[data-more-toggle]') === toggle, 'the toggle is never rebuilt');
  for (const main of tabs.querySelectorAll('[role="tab"]')) assert.equal(main.getAttribute('aria-selected'), 'false');
});

// 2026-10-19 使用者要求同功能的元件只留一套：資料預覽的主要資料表改成共用頁籤，外層是 role="tablist"，
// 只有選中的頁籤留在 Tab 鍵順序裡，方向鍵左右、Home、End 直接換資料表；「其他資料」是頁籤後面的共用選單，不在方向鍵裡。
test('data preview tabs switch datasets with arrow keys, Home and End, keep one tab in the Tab order, and leave the other-data menu out of the arrows', async () => {
  const f = await previewOpened();
  const list = f.aside().querySelector('.data-preview__tabs [role="tablist"]');
  assert.ok(list, 'the main datasets sit in a tablist');
  assert.equal(list.getAttribute('aria-label'), '資料預覽資料表');
  const tabs = () => list.querySelectorAll('[role="tab"]');
  assert.deepEqual(tabs().map(item => item.getAttribute('data-ds')), ['pbcGl', 'gl', 'pbcTb', 'tb', 'accountMappings']);
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
  for (const [key, dataset] of [['ArrowRight', 'gl'], ['End', 'accountMappings'], ['ArrowRight', 'pbcGl'], ['ArrowLeft', 'accountMappings'], ['Home', 'pbcGl']]) {
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
