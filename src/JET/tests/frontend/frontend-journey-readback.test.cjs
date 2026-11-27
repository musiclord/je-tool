'use strict';
// 已存情境的條件讀回成句子的情境測試。
//
// 使用者重開案件，到第五步「已儲存情境」看每個已存情境的條件句子。已存情境可能含組內 AND 與 OR 混用、
// 已不存在的欄位、已不在欄位配對中的攸關資料元素欄位與已停用的預篩選條件；重開時要照樣讀得懂，
// 而且讀回不能改動已存的條件。這個檔只測讀回。
//
// 預期句子寫死在 tests/JET.Tests/Fixtures/filter-readback-sentences.json，前後端共用同一份：
//   - 後端：tests/JET.Tests/Application/FilterReadbackSentenceTests.cs 用 FilterConditionRenderer.Render 核對；
//   - 前端：這個檔用正式第五步畫面畫出已存情境清單，讀畫面上的句子核對（frontend-workflow.test.cjs 的讀回迴圈也讀同一份）。
// 預期句子逐字寫死，不由程式算出；這是前後端一致的契約，所以逐字比對。
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

function reopenedCase(rdeFields, scenarios, neverBlankGlFields) {
  const f = usageJourneyFixture('filter');
  // 有些句子要帶案件裡一定有值的欄位才寫得出來；夾具沒有這一項的句子就不帶。
  f.state.mapping.gl.committed = { options: { rdeFields: plain(rdeFields) }, ...(neverBlankGlFields ? { neverBlankGlFields: plain(neverBlankGlFields) } : {}) };
  // 開案回應不帶已經不在配對裡的欄位名稱，這裡也不帶。
  f.store.setSavedScenarios(scenarios);
  return f;
}

for (const entry of entries) {
  test('readback: a reopened saved scenario reads back the shared literal sentence without changing it: ' + entry.id, () => {
    // 已存情境由後端讀回，不帶前端的 __preserveJoins 等暫存鍵；名稱與說明是合成的。
    const scenario = { name: '合成-' + entry.id, rationale: '合成讀回', ...plain(entry.scenario) };
    const f = reopenedCase(entry.rdeFields, [scenario], entry.neverBlankGlFields);
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
  for (const id of ['mixed-joins-in-one-group-fold-left', 'mixed-joins-ignore-case-and-spaces-before-next-group', 'first-rule-join-has-no-left-side',
    'retired-gl-field-non-business-day', 'unknown-gl-field-list', 'rde-field-not-in-mapping-equals',
    'relevant-data-element-amount-keeps-the-blank-note', 'unknown-prescreen-key'])
    assert.ok(ids.has(id), 'fixture entry ' + id);
});

test('readback: several reopened scenarios each keep their own sentence and order', () => {
  const picked = ['unknown-gl-field-list', 'mixed-joins-in-one-group-fold-left', 'retired-gl-field-non-business-day']
    .map(id => entries.find(entry => entry.id === id));
  const f = reopenedCase([], picked.map((entry, i) => ({ name: '合成 ' + (i + 1), rationale: '合成', ...plain(entry.scenario) })));
  assert.deepEqual(savedReadbacks(f), picked.map(entry => entry.expected));
});

test('readback: a typed condition uses the current field label, follows a rename, and names a removed field without its identifier', () => {
  const entry = entries.find(item => item.id === 'relevant-data-element-amount-keeps-the-blank-note');
  const fieldId = 'rde.b7000000000000000000000000000003';
  const scenario = { name: '合成攸關資料元素欄位', rationale: '合成', ...plain(entry.scenario) };
  const f = reopenedCase(entry.rdeFields, [scenario]);
  assert.deepEqual(savedReadbacks(f), ['合成金額絕對值 大於「1000」；空白不列入']);

  // 第三步把欄位改名後重新確認：讀回跟著換成新名稱，條件本身不變。
  f.state.mapping.gl.committed = { options: { rdeFields: [{ fieldId, sourceColumn: 'Synthetic', label: '改名後金額', valueType: 'amount' }] } };
  f.store.setSavedScenarios(f.state.filter.savedScenarios.slice());
  assert.deepEqual(savedReadbacks(f), ['改名後金額絕對值 大於「1000」；空白不列入']);

  // 欄位已不在配對裡時寫「已不在欄位配對中的攸關資料元素欄位」，
  // 不印識別碼，也不接金額比較基準；前後端同一句。
  f.state.mapping.gl.committed = { options: { rdeFields: [] } };
  f.store.setSavedScenarios(f.state.filter.savedScenarios.slice());
  const [removed] = savedReadbacks(f);
  assert.equal(removed, '已不在欄位配對中的攸關資料元素欄位 大於「1000」；空白不列入');
  assert.ok(!removed.includes(fieldId), 'the identifier is not shown');
  assert.ok(!removed.includes('合成金額') && !removed.includes('改名後金額'), 'no stale label is shown for a removed field');
  assert.deepEqual(plain(f.state.filter.savedScenarios[0].groups), plain(entry.scenario.groups), 'the stored rule is untouched');
});
