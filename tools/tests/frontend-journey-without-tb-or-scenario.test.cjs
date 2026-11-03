'use strict';
require('./support/resource-guard.cjs');
// 沒有 TB、沒有已儲存情境時的流程（裁定 2026-10-29-16 與 2026-10-30-15）。
//
// 審計員只匯入 GL，確認 GL 欄位配對後直接執行資料驗證；之後不儲存任何篩選情境就到第六步產生工作底稿。
// 另一位審計員已儲存情境，但匯出時一個都沒勾選，照樣產生工作底稿。
// 步驟關卡不再因為沒有 TB 或沒有情境而鎖住；資料驗證、目前結果與規則無法套用的情境仍照舊擋下。
// 夾具是共用的 usageJourneyFixture，後端回應都是寫死的合成值。
// 取代的舊斷言：frontend-workflow.test.cjs 的「TB import is still required by the user」與
// 「TB mapping is still required by the user」，以及 frontend-journey-completion.test.cjs 情境全刪時不能匯出的部分。
// 第一次失敗：FrontendMapping 收據 20261030-232349902-dbb29d8564bb41c586f73ee8fa863f3b。
const test = require('node:test');
const assert = require('node:assert/strict');
const F = require('./support/frontend-fixtures.cjs');
const { plain, settle } = F;

test('without TB: importing and confirming only GL opens field mapping and data validation', () => {
  const f = F.usageJourneyFixture();
  f.store.resetWorkflow();
  f.store.setProject({ projectId: 'without-tb' });
  f.store.setImportResult('gl', { batchId: 'gl' });
  assert.equal(f.window.JetUi.stepGate(f.store.getState(), 2).ok, true, 'GL alone opens field mapping');
  f.store.getState().mapping.gl.committed = {};
  assert.equal(f.window.JetUi.stepGate(f.store.getState(), 3).ok, true, 'a confirmed GL mapping alone opens data validation');
  assert.equal(f.window.JetUi.isStepReachable(f.store.getState(), 3), true);
  // 資料驗證仍是後面步驟的前置條件。
  assert.equal(f.window.JetUi.stepGate(f.store.getState(), 4).ok, false);
});

test('without saved scenarios: step six opens and exports a working paper with steps three and four left blank', async () => {
  const f = F.usageJourneyFixture();
  f.store.setSavedScenarios([]);
  f.store.setFilterResultRef(null);
  assert.equal(f.window.JetUi.stepGate(f.store.getState(), 5).ok, true, 'no saved scenario no longer locks step six');
  const step = f.draw('export');
  assert.equal(step.querySelector('[data-bind="export-no-scenario"]'), null, 'no notice asks the auditor to save a scenario first');
  assert.equal(step.querySelector('[data-scenario-position]'), null);
  f.change('[data-bind="include-prescreen-report"]', false); f.draw();
  f.click('[data-action="export-working-paper"]'); await settle();
  assert.deepEqual(plain(f.request('exportWorkingPaperStream').payload),
    { validationResultId: 'v1', scenarioRevision: null, scenarioPositions: [] });
});

test('with saved scenarios but none selected: the export button stays enabled and sends no position', async () => {
  const f = F.usageJourneyFixture();
  f.draw('export');
  f.change('[data-bind="include-prescreen-report"]', false); f.draw();
  f.click('[data-action="clear-scenario-selection"]'); f.draw();
  assert.equal(f.$('[data-action="export-working-paper"]').disabled, false);
  f.click('[data-action="export-working-paper"]'); await settle();
  assert.deepEqual(plain(f.request('exportWorkingPaperStream').payload),
    { validationResultId: 'v1', scenarioRevision: 'r1', scenarioPositions: [] });
});

test('scenarios that cannot use the current rules still lock step six until they are fixed', () => {
  const f = F.usageJourneyFixture();
  f.store.setFilterResultRef(null);
  f.store.setFilterScenarioCheck({ status: 'inconsistent', problems: [{ position: 1, name: '第一個', messages: ['合成欄位已移除'] }] });
  assert.deepEqual(plain(f.window.JetUi.stepGate(f.store.getState(), 5).missing), ['修改無法套用目前規則的篩選情境']);
});
