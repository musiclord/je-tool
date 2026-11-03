'use strict';
// 畫面上的數量、主畫面步驟標題旁的狀態字與情境範例的動機（2026-10-31）。
//
// 審計員的操作：
//   匯入一萬多列的 GL 後看匯入卡寫的列數，以及來源清單上的列數與「欄名在第幾列」；
//   在六個步驟之間移動，看目前步驟標題旁的狀態字，收合目前步驟後看那一列，再打開流程總覽對照；
//   在第五步看兩個情境範例帶入的動機。
// 夾具：tools/tests/support/frontend-fixtures.cjs 的 usageJourneyFixture（只畫中欄的步驟）與 remapAppFixture（步驟文件流與流程總覽）。
// 依據：裁定 2026-10-30-19 畫面上的數量加千分位，Excel 列號這類位置不加；裁定 2026-10-30-20 照確認頁的表改寫「可能」；
// 裁定 2026-10-30-21 步驟標題旁的狀態字和流程總覽用同一套。預期值一律寫死，不由被測程式算出；資料都是程式生成的合成資料。
// 第一次執行在改程式之前，五則都依預期失敗：FrontendMapping 收據 20261031-005738048-537d55bb503744e797c25b2b6ef9f583。
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { usageJourneyFixture, remapAppFixture: baseRemapAppFixture, batch8ValidationData, savedBlankDescription, readWwwroot } =
  require('./support/frontend-fixtures.cjs');

// 流程總覽要 overview-bi.js；vm 沒有版面，計算後樣式回傳不影響結果的固定值（和 frontend-journey-completion.test.cjs 相同）。
function remapAppFixture() {
  const f = baseRemapAppFixture();
  f.window.getComputedStyle = () => ({ gridTemplateColumns: 'none', display: 'block', position: 'static' });
  vm.runInContext(readWwwroot('js/overview-bi.js'), f.context, { filename: 'overview-bi.js' });
  return f;
}

// 依流程逐步建立狀態：1 建立案件、2 匯入 GL 與 TB、3 兩邊配對都確認、4 資料驗證可繼續、5 已儲存情境。
function buildState(f, level) {
  const store = f.store;
  store.resetWorkflow();
  if (level >= 1) store.setProject({ projectId: 'wording-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31' });
  if (level >= 2) for (const kind of ['gl', 'tb']) store.setImportResult(kind, { batchId: kind, rowCount: 2, columns: ['Account'] });
  if (level >= 3) for (const kind of ['gl', 'tb']) store.setMappingCommitted(kind, { mapping: {}, mode: kind === 'gl' ? 'dual' : 'debitCredit' });
  if (level >= 4) store.setResult('validate', batch8ValidationData('v1'));
  if (level >= 5) { store.setSavedScenarios([savedBlankDescription('第一個')]); store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' }); }
  store.setView('workflow');
}

test('the shared count format adds thousands separators to quantities', () => {
  const ui = usageJourneyFixture('import').window.JetUi;
  assert.equal(typeof ui.countText, 'function', 'one shared count format');
  assert.equal(ui.countText(1234567), '1,234,567');
  assert.equal(ui.countText(12345), '12,345');
  assert.equal(ui.countText('12345'), '12,345', 'a count that arrives as text is formatted the same way');
  assert.equal(ui.countText(-1234), '-1,234');
  assert.equal(ui.countText(999), '999');
  assert.equal(ui.countText(0), '0');
  assert.equal(ui.countText(null), '—', 'a missing count stays a dash');
});

test('the import card writes row counts with thousands separators and leaves the header row number as it is', () => {
  const f = usageJourneyFixture('import', { redraw: true });
  f.store.setImportResult('gl', { batchId: 'gl', rowCount: 12345, columns: ['Account', 'Amount'],
    sources: [{ sourceNo: 1, fileName: '合成總帳.xlsx', sheetName: 'GL', headerRow: 1200, rowCount: 12345, importedUtc: '2026-01-01T00:00:00Z' }] });
  f.draw('import');
  f.click('[data-task-toggle="gl"]');
  const card = f.$('[data-bind="import-card-gl"]');
  assert.ok(card !== null, 'the GL import card is drawn');
  assert.equal(card.querySelector('.import-card__status--ok').textContent, '已匯入 12,345 列、2 欄（1 個來源）。');
  assert.equal(card.querySelector('td.source-list__rows').textContent, '12,345');
  // 欄名所在的列是 Excel 列號，不是數量。
  assert.match(card.querySelector('.source-list__detail').textContent, /欄名在第 1200 列/);
});

test('the two scenario examples ask what to check instead of saying what the entries may be', () => {
  const ui = usageJourneyFixture('filter').window.JetUi;
  const rationale = key => ui.FILTER_SCENARIO_TEMPLATES.find(template => template.key === key).rationale;
  assert.equal(rationale('weekendManualDescription'), '檢視週末以人工方式入帳、且摘要含特定字詞的分錄，是否為非例行的調整。');
  assert.equal(rationale('preparationManualHoliday'),
    '檢視財報準備日起核准、在假日以人工入帳的分錄，是否為期末的不當調整；每月 1 到 3 日的分錄不列入。');
});

// 標題旁的狀態字和流程總覽同一階段的狀態字逐字相同；「尚缺」照舊接在後面。
const HEADINGS = [
  { name: 'case created, standing on import', level: 1, step: 1, status: '可處理', missing: '尚缺：匯入 PBC-GL' },
  { name: 'GL imported, standing on import', level: 2, step: 1, status: '✓ 完成', missing: null },
  { name: 'GL imported, standing on validation before mapping', level: 2, step: 3, status: '等待前置步驟', missing: '尚缺：執行資料驗證' },
  { name: 'validation eligible, standing on step five without scenarios', level: 4, step: 4, status: '可處理', missing: null },
  { name: 'scenarios saved, standing on step five', level: 5, step: 4, status: '✓ 完成', missing: null }
];

test('the current step heading uses the overview status words instead of a separate in-progress label', () => {
  const f = remapAppFixture();
  for (const entry of HEADINGS) {
    buildState(f, entry.level); f.store.setStepIndex(entry.step);
    f.app.renderContent(f.state, f.store.STEPS[entry.step]);
    const head = f.$('.stepflow-item--current .stepflow-item__status');
    assert.ok(head !== null, entry.name + ': heading status drawn');
    assert.equal(head.querySelector('.stepflow-item__flag').textContent, entry.status, entry.name + ': heading status');
    const missing = head.querySelector('.stepflow-item__cond');
    assert.equal(missing === null ? null : missing.textContent, entry.missing, entry.name + ': missing items');
    const overview = f.dom.element(); overview.innerHTML = f.app.overviewHtml(f.state);
    assert.equal(overview.querySelectorAll('.overview-stage__status')[entry.step].textContent, entry.status, entry.name + ': same words as the overview');
    assert.doesNotMatch(f.$('[data-bind="content"]').textContent, /進行中/, entry.name + ': no in-progress label');
  }
});

test('the collapsed current step shows the same status words', () => {
  const f = remapAppFixture();
  buildState(f, 2); f.store.setStepIndex(1); f.store.setStepFlowCollapsed(true);
  f.app.renderContent(f.state, f.store.STEPS[1]);
  const row = f.$('.stepflow-item--collapsed');
  assert.ok(row !== null, 'the collapsed row is drawn');
  assert.equal(row.querySelector('.stepflow-item__flag').textContent, '✓ 完成');
  buildState(f, 1); f.store.setStepIndex(1); f.store.setStepFlowCollapsed(true);
  f.app.renderContent(f.state, f.store.STEPS[1]);
  assert.equal(f.$('.stepflow-item--collapsed .stepflow-item__flag').textContent, '可處理');
  assert.doesNotMatch(f.$('[data-bind="content"]').textContent, /進行中/);
});
