'use strict';
// 第九節 F：頁內表單的主要動作放在最後，取消緊鄰左側；預覽保留在最前面。
// 修改前失敗證據：20261014-122748461-68cb6c817bad43309f1310bdd915f4dd，案件表單與兩種配對介面的順序不符。
const test = require('node:test');
const assert = require('node:assert/strict');
const { usageJourneyFixture } = require('./support/frontend-fixtures.cjs');

test('case metadata places cancel before saving', () => {
  const f = usageJourneyFixture('create', { redraw: true });
  f.click('[data-action="edit-project-metadata"]');
  assert.deepEqual(f.$('[data-bind="project-update-form"] .panel__actions').querySelectorAll('button').map(button => button.textContent), ['取消', '儲存案件資料']);
});

for (const mode of ['classic', 'grid']) test('mapping ' + mode + ' places preview first and confirmation last', () => {
  const f = usageJourneyFixture('mapping', { redraw: true });
  f.window.JetUi.applyLoadedProject({ project: { projectId: 'order', currentStep: 2 },
    importState: { gl: { batchId: 'synthetic', columns: ['Doc', 'Date', 'Code', 'Name', 'Memo', 'Amount'], rowCount: 2 } },
    mapping: { gl: { mapping: { docNum: 'Doc', postDate: 'Date', accNum: 'Code', accName: 'Name', description: 'Memo', amount: 'Amount' },
      amountMode: 'signed', approvalDateMode: 'unmapped', committedUtc: '2025-12-31T00:00:00Z' } } }, { stayOnCurrentStep: true });
  f.click('[data-action="remap-gl"]');
  f.store.setMappingUiMode(mode);
  const actions = f.$('[data-bind="mapping-gl"] .panel__actions').querySelectorAll('button').map(button => button.textContent);
  assert.deepEqual(actions, ['預覽', '取消變更', '重新確認配對']);
});
