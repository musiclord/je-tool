'use strict';
// 頁內表單的主要動作放在最後，取消緊鄰左側；預覽保留在最前面。
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
    mapping: { gl: { mapping: { voucherNumber: 'Doc', glDate: 'Date', accountNumber: 'Code', accountName: 'Name', description: 'Memo', amount: 'Amount' },
      amountMode: 'signed', approvalDateMode: 'unmapped', committedUtc: '2025-12-31T00:00:00Z' } } }, { stayOnCurrentStep: true });
  f.click('[data-action="remap-gl"]');
  f.store.setMappingUiMode(mode);
  const actions = f.$('[data-bind="mapping-gl"] .panel__actions').querySelectorAll('button').map(button => button.textContent);
  // 打開右側資料預覽的檢視一律寫「資料預覽」，順序不變。
  assert.deepEqual(actions, ['資料預覽', '取消變更', '重新確認配對']);
});
