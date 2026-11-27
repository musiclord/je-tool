'use strict';
require('./support/resource-guard.cjs');
const test = require('node:test');
const assert = require('node:assert/strict');
const { usageJourneyFixture, plain, settle } = require('./support/frontend-fixtures.cjs');
const { filterOperationFixture } = require('./support/frontend-fixtures.cjs');
const fs = require('node:fs');
const path = require('node:path');

function dateFixture(rule) {
  const f = usageJourneyFixture('filter', { redraw: true });
  f.window.JetFocus = { defer(target) { const node = target(); if (node) node.focus(); } };
  f.store.setFilterDraft({ name: '合成日期', rationale: '合成查核', __nameDirty: true, __rationaleDirty: true,
    groups: [{ join: 'AND', matchScope: 'row', conditions: [rule] }] });
  return f;
}

test('case creation and metadata editing use the shared native date input', () => {
  const f = usageJourneyFixture('create', { redraw: true });
  assert.equal(typeof f.window.JetUi.dateInputHtml, 'function');
  f.click('[data-action="edit-project-metadata"]');
  const inputs = f.dom.content.querySelectorAll('input[type="date"]');
  assert.equal(inputs.length, 1, 'metadata editing only changes the preparation date; the audit period stays read-only');
  for (const input of inputs) assert.ok(input.classList.contains('date-input') && input.classList.contains('form__input'));
  f.store.setProject(null);
  assert.equal(f.dom.content.querySelectorAll('input.date-input[type="date"]').length, 3);
});

// 後端只接受四位數年份的日期，所以每一個日期框都帶 9999-12-31 的上限，年份欄打不出五位數以上。
test('every date input caps the year at four digits', () => {
  const capped = (inputs, expected, label) => {
    assert.equal(inputs.length, expected, label);
    for (const input of inputs) assert.equal(input.getAttribute('max'), '9999-12-31', label);
  };
  const create = usageJourneyFixture('create', { redraw: true });
  assert.equal((create.window.JetUi.dateInputHtml({ name: 'synthetic' }).match(/ max="9999-12-31"/g) || []).length, 1);
  assert.equal((create.window.JetUi.dateInputHtml({ name: 'synthetic', max: '2100-12-31' }).match(/ max="[^"]*"/g) || []).join(''),
    ' max="9999-12-31"', 'callers cannot replace the shared limit');
  create.click('[data-action="edit-project-metadata"]');
  capped(create.dom.content.querySelectorAll('input[type="date"]'), 1, 'metadata editing');
  create.store.setProject(null);
  capped(create.dom.content.querySelectorAll('input[type="date"]'), 3, 'case creation');

  for (const operator of ['on', 'before', 'after', 'between']) {
    const f = dateFixture({ type: 'fieldValue', field: 'glDate', operator, value: '2025-01-02', from: '2025-01-02', to: '2025-01-31' });
    capped((operator === 'between' ? ['from', 'to'] : ['value']).map(key => f.$('[data-value-key="' + key + '"]')),
      operator === 'between' ? 2 : 1, 'field condition ' + operator);
  }
  const list = dateFixture({ type: 'fieldValue', field: 'glDate', operator: 'in', values: ['2025-01-02'] });
  capped([list.$('[data-date-input]')], 1, 'date list');
});

for (const operator of ['on', 'before', 'between']) test('' + operator + ' uses native date controls and keeps the date payload', () => {
  const rule = { type: 'fieldValue', field: 'glDate', operator, value: '2025/1/2', from: '2025/1/2', to: '2025-01-31' };
  const f = dateFixture(rule);
  const keys = operator === 'between' ? ['from', 'to'] : ['value'];
  for (const key of keys) {
    const input = f.$('[data-value-key="' + key + '"]');
    assert.equal(input.getAttribute('type'), 'date');
    assert.ok(input.classList.contains('date-input') && input.classList.contains('form__input'));
    assert.equal(input.value, key === 'to' ? '2025-01-31' : '2025-01-02');
    input.value = key === 'to' ? '2025-02-28' : '2025-02-01'; input.fire('input');
  }
  const wire = plain(f.window.JetUi.FilterValues.wire(rule, f.store.getState()));
  assert.deepEqual(wire, operator === 'between' ? { type: 'fieldValue', join: 'AND', operator, includeBlank: false, field: 'glDate', from: '2025-02-01', to: '2025-02-28' } :
    { type: 'fieldValue', join: 'AND', operator, includeBlank: false, field: 'glDate', value: '2025-02-01' });
});

test('native date hints do not promise pasted formats outside the list', () => {
  for (const operator of ['on', 'before', 'between']) {
    const f = dateFixture({ type: 'fieldValue', field: 'glDate', operator,
      value: '2025-01-01', from: '2025-01-01', to: '2025-01-31' });
    assert.doesNotMatch(f.$('.value-editor').textContent, /Excel 序列值|民國年/);
  }
  const list = dateFixture({ type: 'fieldValue', field: 'glDate', operator: 'in', values: ['2025-01-01'] });
  // 日期清單的說明寫純數字不當成日期，沒有提到 Excel 序列值。
  const listText = list.$('.value-editor').textContent;
  assert.match(listText, /20250801.*民國年依案件設定.*純數字不當成日期/);
  assert.doesNotMatch(listText, /Excel 序列值/);
});

// 純數字不當成 Excel 序列值，六個值全部保留原文送出，並寫出無法辨識。
test('date lists keep plain numbers as typed instead of converting Excel serials', () => {
  const rule = { type: 'fieldValue', field: 'glDate', operator: 'in', values: ['45292', '2', '73415', '1', '73416', '240115'] };
  const f = dateFixture(rule);
  assert.deepEqual(plain(f.window.JetUi.FilterValues.wire(rule, f.store.getState()).values),
    ['45292', '2', '73415', '1', '73416', '240115']);
  assert.match(f.window.JetUi.FilterValues.problem(rule, f.store.getState()), /日期「45292」無法辨識/);
});

// 帶千分位的數字視為無法辨識的日期，和後端相同不當序列值換算，保留原文送出。
// 沒有千分位的 45292 也不換算，同樣保留原文。
test('date lists keep numbers with thousands separators as typed', () => {
  const rule = { type: 'fieldValue', field: 'glDate', operator: 'in', values: ['1,234', '45,292', '45292'] };
  const f = dateFixture(rule);
  assert.deepEqual(plain(f.window.JetUi.FilterValues.wire(rule, f.store.getState()).values),
    ['1,234', '45,292', '45292']);
});

// 前後端讀同一份日期固定答案 tests/JET.Tests/Fixtures/date-normalization.json，
// C# 的 DateNormalizerTests 讀同一份；一邊改了規則另一邊就失敗。無法辨識的值前端保留原文，由後端寫出格式錯誤；
// 標成 passThrough 的寬鬆寫法前端不解析，原文交給後端。
test('date lists follow the shared date fixture under both ROC settings', () => {
  const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, '../JET.Tests/Fixtures/date-normalization.json'), 'utf8'));
  assert.ok(fixture.cases.length > 50, 'the shared fixture lists every date case from both sides');
  const rule = { type: 'fieldValue', field: 'glDate', operator: 'in', values: [] };
  const f = dateFixture(rule);
  const state = f.store.getState();
  const mismatches = [];
  for (const item of fixture.cases) {
    for (const roc of [true, false]) {
      state.project.rocDateEnabled = roc;
      const backend = roc ? item.rocEnabled : item.rocDisabled;
      const expected = backend === null || item.frontend === 'passThrough' ? item.raw : backend;
      const actual = plain(f.window.JetUi.FilterValues.wire({ ...rule, values: [item.raw] }, state).values);
      if (actual.length !== 1 || actual[0] !== expected) mismatches.push(item.raw + '（民國年' + (roc ? '開' : '關') + '）：預期 ' + expected + '，實際 ' + actual.join('|'));
    }
  }
  assert.deepEqual(mismatches, []);
});

// 前後端讀同一份金額固定答案 tests/JET.Tests/Fixtures/amount-parsing.json，
// C# 的 MoneyScalingTests 與 FieldValueOperandValidationTests 讀同一份；一邊改了規則另一邊就失敗。
// 畫面照案件的金額存放倍率把值讀成放大後的整數：讀不成時寫出不是數字，兩端都讀得成才比上下限，負數提醒看放大後的值。
test('amount conditions follow the shared amount fixture', () => {
  const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, '../JET.Tests/Fixtures/amount-parsing.json'), 'utf8'));
  assert.ok(fixture.cases.length > 50 && fixture.ranges.length > 5, 'the shared fixture lists every amount case from both sides');
  const rule = { type: 'fieldValue', field: 'amount', operator: 'equals', amountBasis: 'absolute', value: '' };
  const f = dateFixture(rule);
  const state = f.store.getState();
  state.project.moneyScale = fixture.moneyScale;
  const values = f.window.JetUi.FilterValues;
  const mismatches = [];
  for (const item of fixture.cases) {
    const expected = item.scaled === null ? '金額「' + item.raw + '」不是數字' : '';
    const actual = values.problem({ ...rule, value: item.raw }, state);
    if (actual !== expected) mismatches.push(JSON.stringify(item.raw) + '：預期「' + expected + '」，實際「' + actual + '」');
    if (item.scaled === null) continue;
    const warned = values.render({ ...rule, value: item.raw }, state, 'value-editor').includes('填了負數');
    if (warned !== item.scaled.startsWith('-')) mismatches.push(JSON.stringify(item.raw) + '：負數提醒預期 ' + item.scaled.startsWith('-') + '，實際 ' + warned);
  }
  const rangeAnswers = { invalidFrom: range => '金額「' + range.from + '」不是數字', invalidBoth: range => '金額「' + range.from + '」不是數字',
    invalidTo: range => '金額「' + range.to + '」不是數字', reversed: () => '下限金額大於上限金額', ok: () => '' };
  for (const range of fixture.ranges) {
    assert.ok(rangeAnswers[range.result], range.result);
    const expected = rangeAnswers[range.result](range);
    const actual = values.problem({ ...rule, operator: 'between', from: range.from, to: range.to }, state);
    if (actual !== expected) mismatches.push(range.from + ' 至 ' + range.to + '：預期「' + expected + '」，實際「' + actual + '」');
  }
  assert.deepEqual(mismatches, []);
});

// 「指定金額」清單的 100 個值上限和後端同一種數法：讀得成的金額看依案件金額存放倍率放大取整後的值，
// 所以 1,000 與 1000 算一個值，倍率 100 時 2000.004 與 2000 也算一個值；讀不成金額的值照原文各算一個。
// 送出的清單仍是使用者輸入的原文，換算交給後端。攸關資料元素的金額欄位也一樣。
test('amount lists count the value limit by scaled amounts like the backend', () => {
  const hundred = Array.from({ length: 100 }, (_, i) => String((i + 1) * 1000));
  const rule = { type: 'fieldValue', field: 'amount', operator: 'in', amountBasis: 'absolute', values: [] };
  const f = dateFixture(rule);
  const state = f.store.getState();
  state.project.moneyScale = 100;
  const values = f.window.JetUi.FilterValues;
  const limit = { in: '每條最多 100 個值。請拆成多條條件，放進子條件組，用 OR 連接。',
    notIn: '每條最多 100 個值。請拆成多條條件，放進子條件組，用 AND 連接。' };
  for (const operator of ['in', 'notIn']) {
    const typed = hundred.concat(['1,000', '2000.004']);
    const merged = { ...rule, operator, values: typed };
    assert.equal(values.problem(merged, state), '', operator + '：102 個原文只有 100 個不同金額');
    assert.deepEqual(plain(values.wire(merged, state).values), typed, operator + '：送出審計員輸入的原文');
    assert.match(values.render(merged, state, 'value-editor'), /已輸入 100 個值，上限 100/, operator);
    assert.equal(values.problem({ ...merged, values: typed.concat(['101,000.00']) }, state), limit[operator], operator + '：第 101 個不同金額');
    // 讀不成金額的值照原文數，大小寫不同算兩個；超過上限時先寫上限，和後端相同。
    const invalid = hundred.slice(0, 99);
    assert.equal(values.problem({ ...merged, values: invalid.concat(['abc', 'ABC']) }, state), limit[operator], operator + '：讀不成的值各算一個');
    assert.equal(values.problem({ ...merged, values: invalid.concat(['abc']) }, state), '金額「abc」不是數字', operator);
  }

  // 在畫面上貼上清單時，計數也照放大後的值更新。
  rule.values = hundred.slice(0, 1); f.draw();
  const list = f.$('[data-value-key="values"]');
  list.value = hundred.concat(['1,000', '2000.004']).join('\n'); list.fire('input');
  assert.equal(f.$('[data-value-count]').textContent, '已輸入 100 個值，上限 100');
  list.value = hundred.concat(['1,000', '101000']).join('\n'); list.fire('input');
  assert.equal(f.$('[data-value-count]').textContent, '已輸入 101 個值，上限 100');

  f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed', options: { rdeFields: [
    { fieldId: 'rde.amount', sourceColumn: 'Amount', label: '合成金額', valueType: 'amount' }] } });
  const rde = { type: 'fieldValue', fieldId: 'rde.amount', operator: 'in', amountBasis: 'absolute', values: hundred.concat(['1,000']) };
  assert.equal(values.problem(rde, f.store.getState()), '');
  assert.equal(values.problem({ ...rde, values: rde.values.concat(['101000']) }, f.store.getState()), limit.in);
});

// 日期的純數字判斷和金額讀法分開：帶千分位的數字即使千分位位置不對，仍是無法辨識的日期，和後端 DateNormalizer 相同。
test('plain numbers with commas stay unrecognizable dates', () => {
  const rule = { type: 'fieldValue', field: 'glDate', operator: 'on', value: '' };
  const f = dateFixture(rule);
  const state = f.store.getState();
  for (const value of ['12,31', '1,234']) {
    assert.equal(f.window.JetUi.FilterValues.problem({ ...rule, value }, state), '日期「' + value + '」無法辨識，請以 2026-12-31 的格式輸入', value);
  }
});

// 用「-」分隔的日期和用「/」分隔的一樣：四位數年份照寫出來的年份讀，不限 1900 到 2100 年；
// 民國年開啟時三位數民國年換成西元。關閉時三位數年份讀不成日期，照原文送出並寫出無法辨識。
test('date lists read hyphenated dates and ROC years like slashed ones', () => {
  const rule = { type: 'fieldValue', field: 'glDate', operator: 'in', values: ['1899-6-11', '2101-6-11', '114-6-11', '114-06-11'] };
  const f = dateFixture(rule);
  const state = f.store.getState();
  state.project.rocDateEnabled = true;
  assert.deepEqual(plain(f.window.JetUi.FilterValues.wire(rule, state).values),
    ['1899-06-11', '2101-06-11', '2025-06-11']);
  assert.equal(f.window.JetUi.FilterValues.problem(rule, state), '');

  state.project.rocDateEnabled = false;
  const off = { ...rule, values: ['114-6-11'] };
  assert.deepEqual(plain(f.window.JetUi.FilterValues.wire(off, state).values), ['114-6-11']);
  assert.equal(f.window.JetUi.FilterValues.problem(off, state), '日期「114-6-11」無法辨識，請以 2026-12-31 的格式輸入');
});

// 沒有四位數年份或沒有日的寫法視為無法辨識的日期：前端不自行補齊，保留原文送出，
// 由後端逐值寫出無法辨識；寫出年份與日的英文寫法也原樣交給後端判斷。
test('date lists keep incomplete dates as typed for the backend to reject', () => {
  const rule = { type: 'fieldValue', field: 'glDate', operator: 'in', values: ['12/31', 'Dec 2025', '2025-12', 'June 11, 2025'] };
  const f = dateFixture(rule);
  assert.deepEqual(plain(f.window.JetUi.FilterValues.wire(rule, f.store.getState()).values),
    ['12/31', 'Dec 2025', '2025-12', 'June 11, 2025']);
  assert.equal(f.window.JetUi.FilterValues.problem(rule, f.store.getState()), '');
});

test('button and Enter add dates once, keep pasted dates, and allow chip removal', () => {
  const rule = { type: 'fieldValue', field: 'glDate', operator: 'in', values: ['2025/1/2'] };
  const f = dateFixture(rule);
  assert.equal(f.$('[data-calendar-open]'), null);
  const add = (value, enter = false) => {
    const input = f.$('[data-date-input]'); assert.ok(input); assert.equal(input.getAttribute('type'), 'date');
    input.value = value;
    if (enter) input.fire('keydown', { key: 'Enter' }); else f.click('[data-date-add]');
  };
  add('2025-01-02'); assert.deepEqual(plain(f.window.JetUi.FilterValues.values(rule, f.store.getState())), ['2025-01-02']);
  add('2025-02-03', true);
  assert.deepEqual(plain(rule.values), ['2025-01-02', '2025-02-03']);
  assert.equal(f.$('[data-date-input]').value, '');
  assert.equal(f.dom.document.activeElement, f.$('[data-date-input]'));
  f.click('[data-date-remove="2025-01-02"]');
  assert.deepEqual(plain(rule.values), ['2025-02-03']);
  const list = f.$('[data-value-key="values"]'); list.value = '2025/2/3\n114.2.4\n45693'; list.fire('input');
  // 貼上的純數字 45693 保留原文。
  assert.deepEqual(plain(f.window.JetUi.FilterValues.wire(rule, f.store.getState()).values), ['2025-02-03', '2025-02-04', '45693']);
});

test('a full date list keeps the pending date and explains the existing limit', () => {
  const values = Array.from({ length: 100 }, (_, i) => new Date(Date.UTC(2025, 0, i + 1)).toISOString().slice(0, 10));
  const rule = { type: 'fieldValue', field: 'glDate', operator: 'in', values };
  const f = dateFixture(rule);
  const input = f.$('[data-date-input]'); assert.ok(input); input.value = '2025-12-31';
  f.click('[data-date-add]');
  assert.deepEqual(plain(rule.values), values);
  assert.equal(f.$('[data-date-input]').value, '2025-12-31');
  assert.match(f.$('[data-date-error]').textContent, /100.*移除/);
});

test('account search waits for submission and shares load errors and category layout', async () => {
  const rule = { type: 'fieldValue', field: 'accountNumber', operator: 'in', values: ['0010'] };
  const f = dateFixture(rule);
  const tree = f.$('[data-account-tree] .category-tree'); assert.ok(tree);
  const form = f.$('[data-account-tree-search-form]'); assert.ok(form.classList.contains('page-search'));
  const input = form.querySelector('input'); input.value = '1103'; input.fire('input');
  assert.equal(f.requests.length, 0);
  form.fire('submit');
  const request = f.request('queryAccountMappingPage');
  assert.deepEqual(plain(request.payload), { categoryId: null, search: '1103', pageSize: 200, cursor: null });
  request.reject(new Error('合成來源失敗')); await settle(); await settle();
  const retry = f.$('[data-account-tree-retry]');
  assert.equal(retry.textContent, '重新載入');
  assert.ok(retry.closest('[data-load-failure].form-notice[role="alert"]'));
  f.click('[data-account-tree-retry]');
  assert.deepEqual(plain(f.request('queryAccountMappingPage').payload), plain(request.payload));
  assert.deepEqual(plain(rule.values), ['0010']);
});

test('table search uses the same search box and action wording', () => {
  const f = usageJourneyFixture('filter');
  f.dom.content.innerHTML = f.window.JetUi.pageSearchHtml('依傳票號碼搜尋');
  assert.ok(f.$('[data-page-search][data-search-box]'));
  assert.equal(f.$('[data-page-search] button[type="submit"]').textContent, '搜尋');
});

test('person-list instructions use ordinary shared help text', () => {
  const f = dateFixture({ type: 'fieldValue', field: 'createdBy', operator: 'in', values: [] });
  const note = f.dom.content.querySelectorAll('.help-text').find(node => /姓名或員工代碼/.test(node.textContent));
  assert.ok(note);
  assert.equal(note.classList.contains('form-notice'), false);
});

test('compound actions are directly visible in the group heading', () => {
  const f = filterOperationFixture();
  for (const kind of ['group', 'voucher']) {
    const button = f.$('[data-add-compound="' + kind + '"]');
    assert.ok(button.closest('.filter-group-heading'));
    assert.equal(button.closest('details'), null, 'the compound action is not hidden in a menu');
  }
  assert.equal(f.$('.filter-group-menu'), null, 'an empty group has no unused menu');
  f.click('[data-add-compound="group"]');
  f.click('[data-add-compound="voucher"]');
  assert.deepEqual(plain(f.draft().groups[0].conditions.map(rule => rule.type)), ['group', 'voucher']);
});
