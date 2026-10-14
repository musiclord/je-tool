'use strict';
// 情境 15 的讀回部分：舊情境裡的舊式條件讀回成句子（收尾四項第一項第 4 批，2026-10-13）。
//
// 審計員重開案件，到第五步「已儲存情境與矩陣」看每個已存情境的條件句子。舊情境可能含文字值清單（textSet）、
// 同一傳票比對範圍、組內 AND 與 OR 混用、攸關資料元素條件（typed）；這些現在的畫面已經做不出來，但重開時要照樣讀得懂，
// 而且讀回不能改動已存的條件。使用者 2026-10-13 對第一項選「照建議做」，選項說明寫明
// 「舊情境裡的舊式條件維持現在能顯示、能編輯，一樣補測試。」編輯部分屬第 9 批，這個檔只測讀回。
//
// 預期句子寫死在 tools/tests/fixtures/batch7-filter-readback.json，前後端共用同一份：
//   - 後端：src/JET/tests/JET.Tests/Application/Batch7FilterReadbackTests.cs 用 FilterConditionRenderer.Render 核對；
//   - 前端：這個檔用正式第五步畫面畫出已存情境清單，讀畫面上的句子核對（wf 既有的讀回迴圈也會讀到同一份）。
// 第 4 批新增的七筆（文字值清單兩筆、同一傳票三條、組內混用兩筆、第一條的連接方式不算一筆、typed 一筆）的句子由第 4 批代理逐字寫下，
// 第一次執行時人工核對，不由程式算出。這是前後端一致的契約，所以逐字比對；不是使用者裁定過的畫面用語。
//
// 取代的 C# 方法（src/JET/tests/JET.Tests/Architecture/）：
//   FilterAstFrontendContractTests.BackendRendererLabels_AreMirroredExactlyByTheRuntimeFrontend
//   FilterFrontendParityTests.MixedRuleJoinReadback_MirrorsNonMutatingExactLeftFoldBoundary
//   TypedFilterFrontendTests.ReadBack_UsesCurrentFieldMetadataAndBackendOperatorLabels
// 不在這裡斷言：條件無法套用時的原因句子（第四項正在改寫）、儲存、移除與重新檢查（第三項正在改）。
// 全部資料都是程式生成的合成資料。
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { plain, usageJourneyFixture } = require('./support/frontend-fixtures.cjs');

const entries = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/batch7-filter-readback.json'), 'utf8'));

// 畫出第五步，回傳每個已存情境卡片裡的條件句子（拿掉句首「篩選條件：」）。
function savedReadbacks(f) {
  f.draw('filter');
  return f.dom.content.querySelectorAll('.saved-scenario').map(card => {
    const expr = card.querySelector('.scenario-pills .scenario-readback__expr');
    assert.ok(expr, 'saved scenario card shows a readback sentence');
    return expr.textContent;
  });
}

function reopenedCase(rdeFields, scenarios, retiredGlFieldLabels) {
  const f = usageJourneyFixture('filter');
  f.state.mapping.gl.committed = { options: { rdeFields: plain(rdeFields) } };
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-03：已移除的 GL 欄位名稱由 project.load 帶回；夾具寫出同一份名稱。
  f.store.setRetiredGlFieldLabels(retiredGlFieldLabels || null);
  f.store.setSavedScenarios(scenarios);
  return f;
}

for (const entry of entries) {
  test('readback: a reopened saved scenario reads back the shared literal sentence without changing it: ' + entry.id, () => {
    // 已存情境由後端讀回，不帶前端的 __preserveJoins 等暫存鍵；名稱與說明是合成的。
    const scenario = { name: '合成-' + entry.id, rationale: '合成讀回', ...plain(entry.scenario) };
    const f = reopenedCase(entry.rdeFields, [scenario], entry.retiredGlFieldLabels);
    const before = plain(f.state.filter.savedScenarios);
    assert.deepEqual(savedReadbacks(f), [entry.expected]);
    // 畫兩次：讀回不改動已存條件，第二次畫出同一句。
    assert.deepEqual(savedReadbacks(f), [entry.expected]);
    assert.deepEqual(plain(f.state.filter.savedScenarios), before, 'rendering the readback does not rewrite the saved conditions');
    assert.ok(!Object.keys(f.state.filter.savedScenarios[0]).some(key => key.startsWith('__')), 'no UI-only key is written into the saved scenario');
  });
}

test('readback: the shared fixture covers every legacy shape this batch is responsible for', () => {
  // 防止有人刪掉共用夾具裡的新筆後，這個檔仍然全部通過。
  const ids = new Set(entries.map(entry => entry.id));
  for (const id of ['text-set-contains-preserve-spaces', 'text-set-exact-remove-spaces', 'same-voucher-three-rules',
    'mixed-joins-in-one-group-fold-left', 'mixed-joins-ignore-case-and-spaces-before-next-group', 'first-rule-join-has-no-left-side',
    'typed-money-between-absolute', 'retired-gl-field-non-business-day', 'unknown-gl-field-list', 'rde-field-not-in-mapping-equals',
    'typed-field-not-in-mapping-has-no-basis', 'unknown-prescreen-key'])
    assert.ok(ids.has(id), 'fixture entry ' + id);
});

test('readback: several reopened scenarios each keep their own sentence and order', () => {
  const picked = ['same-voucher-three-rules', 'mixed-joins-in-one-group-fold-left', 'text-set-exact-remove-spaces']
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
  f.state.mapping.gl.committed = { options: { rdeFields: [{ fieldId, sourceColumn: 'SyntheticAmount', label: '改名後金額', valueType: 'money' }] } };
  f.store.setSavedScenarios(f.state.filter.savedScenarios.slice());
  assert.deepEqual(savedReadbacks(f), ['改名後金額 介於「10」～「20.5」（金額絕對值）']);

  // 使用者 2026-10-14 第三輪勾選頁確認 R3-03：欄位已不在配對裡時寫「已不在欄位配對中的攸關資料元素欄位」，
  // 不印識別碼，也不接金額比較基準；前後端同一句（第一次失敗：FrontendMapping 收據
  // 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
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
    { fieldId: 'rde.synthetic-money', sourceColumn: 'SynMoney', label: '合成金額', valueType: 'money' }
  ];
  const typed = rule => ({ name: '合成 ' + rule.operator + ' ' + rule.fieldId, rationale: '合成',
    groups: [{ join: 'AND', rules: [{ join: 'AND', type: 'typed', ...rule }] }] });
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
