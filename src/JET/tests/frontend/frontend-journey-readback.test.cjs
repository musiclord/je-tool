'use strict';
// 情境 15 的讀回部分：舊情境裡的舊式條件讀回成句子。
//
// 審計員重開案件，到第五步「已儲存情境」看每個已存情境的條件句子。舊情境可能含文字值清單（textSet）、
// 組內 AND 與 OR 混用、攸關資料元素條件（typed）；這些現在的畫面已經做不出來，但重開時要照樣讀得懂，
// 而且讀回不能改動已存的條件。舊情境裡的舊式條件要能顯示，也要能編輯；
// 編輯部分在 frontend-journey-filter-saved.test.cjs，這個檔只測讀回。
//
// 預期句子寫死在 tests/JET.Tests/Fixtures/filter-readback-sentences.json，前後端共用同一份：
//   - 後端：tests/JET.Tests/Application/FilterReadbackSentenceTests.cs 用 FilterConditionRenderer.Render 核對；
//   - 前端：這個檔用正式第五步畫面畫出已存情境清單，讀畫面上的句子核對（frontend-workflow.test.cjs 的讀回迴圈也讀同一份）。
// 預期句子是逐字寫下、經人工核對的，不由程式算出。這是前後端一致的契約，所以逐字比對。
//
// 不在這裡斷言：條件無法套用時的原因句子、儲存、移除與重新檢查。
// 全部資料都是程式生成的合成資料。
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { plain, usageJourneyFixture } = require('./support/frontend-fixtures.cjs');

const entries = JSON.parse(fs.readFileSync(path.join(__dirname, '../JET.Tests/Fixtures/filter-readback-sentences.json'), 'utf8'));

// 畫出第五步，回傳每個已存情境卡片裡的條件句子（拿掉句首「篩選條件：」）。
function savedReadbacks(f) {
  f.draw('filter');
  return f.dom.content.querySelectorAll('.saved-scenario').map(card => {
    const expr = card.querySelector('.scenario-pills .scenario-readback__expr');
    assert.ok(expr, 'saved scenario card shows a readback sentence');
    return expr.textContent;
  });
}

function reopenedCase(rdeFields, scenarios) {
  const f = usageJourneyFixture('filter');
  f.state.mapping.gl.committed = { options: { rdeFields: plain(rdeFields) } };
  // 開案時不帶回已移除欄位的名稱。
  f.store.setSavedScenarios(scenarios);
  return f;
}

for (const entry of entries) {
  test('readback: a reopened saved scenario reads back the shared literal sentence without changing it: ' + entry.id, () => {
    // 已存情境由後端讀回，不帶前端的 __preserveJoins 等暫存鍵；名稱與說明是合成的。
    const scenario = { name: '合成-' + entry.id, rationale: '合成讀回', ...plain(entry.scenario) };
    const f = reopenedCase(entry.rdeFields, [scenario]);
    const before = plain(f.state.filter.savedScenarios);
    assert.deepEqual(savedReadbacks(f), [entry.expected]);
    // 畫兩次：讀回不改動已存條件，第二次畫出同一句。
    assert.deepEqual(savedReadbacks(f), [entry.expected]);
    assert.deepEqual(plain(f.state.filter.savedScenarios), before, 'rendering the readback does not rewrite the saved conditions');
    assert.ok(!Object.keys(f.state.filter.savedScenarios[0]).some(key => key.startsWith('__')), 'no UI-only key is written into the saved scenario');
  });
}

test('readback: the shared fixture covers every legacy shape this file reads back', () => {
  // 防止有人刪掉共用夾具裡的這幾筆後，這個檔仍然全部通過。
  const ids = new Set(entries.map(entry => entry.id));
  for (const id of ['text-set-contains-preserve-spaces', 'text-set-exact-remove-spaces',
    'mixed-joins-in-one-group-fold-left', 'mixed-joins-ignore-case-and-spaces-before-next-group', 'first-rule-join-has-no-left-side',
    'typed-money-between-absolute', 'retired-gl-field-non-business-day', 'unknown-gl-field-list', 'rde-field-not-in-mapping-equals',
    'typed-field-not-in-mapping-has-no-basis', 'unknown-prescreen-key'])
    assert.ok(ids.has(id), 'fixture entry ' + id);
});

test('readback: several reopened scenarios each keep their own sentence and order', () => {
  const picked = ['text-set-contains-preserve-spaces', 'mixed-joins-in-one-group-fold-left', 'text-set-exact-remove-spaces']
    .map(id => entries.find(entry => entry.id === id));
  const f = reopenedCase([], picked.map((entry, i) => ({ name: '合成 ' + (i + 1), rationale: '合成', ...plain(entry.scenario) })));
  assert.deepEqual(savedReadbacks(f), picked.map(entry => entry.expected));
});

test('readback: a typed condition uses the current field label, follows a rename, and names a removed field without its identifier', () => {
  const entry = entries.find(item => item.id === 'typed-money-between-absolute');
  const fieldId = 'rde.b7000000000000000000000000000003';
  const scenario = { name: '合成 typed', rationale: '合成', ...plain(entry.scenario) };
  const f = reopenedCase(entry.rdeFields, [scenario]);
  assert.deepEqual(savedReadbacks(f), ['合成金額 介於「10」～「20.5」（金額絕對值）']);

  // 第三步把欄位改名後重新確認：讀回跟著換成新名稱，條件本身不變。
  f.state.mapping.gl.committed = { options: { rdeFields: [{ fieldId, sourceColumn: 'SyntheticAmount', label: '改名後金額', valueType: 'amount' }] } };
  f.store.setSavedScenarios(f.state.filter.savedScenarios.slice());
  assert.deepEqual(savedReadbacks(f), ['改名後金額 介於「10」～「20.5」（金額絕對值）']);

  // 欄位已不在配對裡時寫「已不在欄位配對中的攸關資料元素欄位」，
  // 不印識別碼，也不接金額比較基準；前後端同一句。
  f.state.mapping.gl.committed = { options: { rdeFields: [] } };
  f.store.setSavedScenarios(f.state.filter.savedScenarios.slice());
  const [removed] = savedReadbacks(f);
  assert.equal(removed, '已不在欄位配對中的攸關資料元素欄位 介於「10」～「20.5」');
  assert.ok(!removed.includes(fieldId), 'the identifier is not shown');
  assert.ok(!removed.includes('合成金額') && !removed.includes('改名後金額'), 'no stale label is shown for a removed field');
  assert.deepEqual(plain(f.state.filter.savedScenarios[0].groups), plain(entry.scenario.groups), 'the stored typed rule is untouched');
});

test('readback: typed operators read back with the operator wording and the value each operator uses', () => {
  const fields = [
    { fieldId: 'rde.synthetic-text', sourceColumn: 'SynText', label: '合成文字', valueType: 'text' },
    { fieldId: 'rde.synthetic-date', sourceColumn: 'SynDate', label: '合成日期', valueType: 'date' },
    { fieldId: 'rde.synthetic-money', sourceColumn: 'SynMoney', label: '合成金額', valueType: 'amount' }
  ];
  const typed = rule => ({ name: '合成 ' + rule.operator + ' ' + rule.fieldId, rationale: '合成',
    groups: [{ join: 'AND', conditions: [{ join: 'AND', type: 'typed', ...rule }] }] });
  const cases = [
    [{ fieldId: 'rde.synthetic-text', operator: 'in', values: ['甲', '乙,丙'] }, '合成文字 屬於任一值「甲、乙,丙」'],
    [{ fieldId: 'rde.synthetic-text', operator: 'notContains', value: '測試' }, '合成文字 不包含（排除）「測試」'],
    [{ fieldId: 'rde.synthetic-date', operator: 'onOrAfter', value: '2025-03-01' }, '合成日期 不早於「2025-03-01」'],
    [{ fieldId: 'rde.synthetic-date', operator: 'isBlank' }, '合成日期 為空白'],
    [{ fieldId: 'rde.synthetic-money', operator: 'greaterThan', value: '100', amountBasis: 'signed' }, '合成金額 大於「100」（帶正負號金額）'],
    [{ fieldId: 'rde.synthetic-money', operator: 'isNotBlank' }, '合成金額 非空白']
  ];
  const f = reopenedCase(fields, cases.map(([rule]) => typed(rule)));
  const before = plain(f.state.filter.savedScenarios);
  assert.deepEqual(savedReadbacks(f), cases.map(([, sentence]) => sentence));
  assert.deepEqual(plain(f.state.filter.savedScenarios), before);
});
