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
    groups: [{ join: 'AND', matchScope: 'row', rules: [rule] }] });
  return f;
}

// 修改前失敗：20261014-124404664-c8b6779cfddc4f07b17b81219a8d3470，原生日期框尚未共用，單日與區間仍是文字框。
test('section 9 E: case creation and metadata editing use the shared native date input', () => {
  const f = usageJourneyFixture('create', { redraw: true });
  assert.equal(typeof f.window.JetUi.dateInputHtml, 'function');
  f.click('[data-action="edit-project-metadata"]');
  const inputs = f.dom.content.querySelectorAll('input[type="date"]');
  assert.equal(inputs.length, 1, 'metadata editing only changes the preparation date; the audit period stays read-only');
  for (const input of inputs) assert.ok(input.classList.contains('date-input') && input.classList.contains('form__input'));
  f.store.setProject(null);
  assert.equal(f.dom.content.querySelectorAll('input.date-input[type="date"]').length, 3);
});

for (const operator of ['on', 'before', 'between']) test('section 9 E: ' + operator + ' uses native date controls and keeps the date payload', () => {
  const rule = { type: 'fieldValue', field: 'postDate', operator, value: '2025/1/2', from: '2025/1/2', to: '2025-01-31' };
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
  assert.deepEqual(wire, operator === 'between' ? { type: 'fieldValue', join: 'AND', operator, includeBlank: false, field: 'postDate', from: '2025-02-01', to: '2025-02-28' } :
    { type: 'fieldValue', join: 'AND', operator, includeBlank: false, field: 'postDate', value: '2025-02-01' });
});

// 獨立複審補查：原生日期框仍顯示文字解析提示。第一次失敗：20261014-144520616-dd86cdcee65e4b7bb255fd893737a94e。
test('section 9 E: native date hints do not promise pasted formats outside the list', () => {
  for (const operator of ['on', 'before', 'between']) {
    const f = dateFixture({ type: 'fieldValue', field: 'postDate', operator,
      value: '2025-01-01', from: '2025-01-01', to: '2025-01-31' });
    assert.doesNotMatch(f.$('.value-editor').textContent, /Excel 序列值|民國年/);
  }
  const list = dateFixture({ type: 'fieldValue', field: 'postDate', operator: 'in', values: ['2025-01-01'] });
  // 預篩選收斂計畫階段七（使用者 2026-10-22 裁定）：日期清單的說明不再承諾 Excel 序列值，改寫成純數字不當成日期。
  // 原本斷言這裡要有「Excel 序列值」；第一次失敗：FrontendMapping 收據 20261024-045131921-2a00c186343c4fde945aa8eaaaea8da7。
  const listText = list.$('.value-editor').textContent;
  assert.match(listText, /20250801.*民國年依案件設定.*純數字不當成日期/);
  assert.doesNotMatch(listText, /Excel 序列值/);
});

// 低嚴重度事項 F09（使用者 2026-10-19 要求處理）時，日期清單的 Excel 序列值和後端一樣只在 1900 到 2100 年換算，範圍外保留原文。
// 預篩選收斂計畫階段七（使用者 2026-10-22 裁定）：純數字不再當成序列值，六個值全部保留原文送出並寫出無法辨識；
// 前三個以前換成 2024-01-01、1900-01-01、2100-12-31。第一次失敗：FrontendMapping 收據 20261024-045131921-2a00c186343c4fde945aa8eaaaea8da7。
test('date lists keep plain numbers as typed instead of converting Excel serials', () => {
  const rule = { type: 'fieldValue', field: 'postDate', operator: 'in', values: ['45292', '2', '73415', '1', '73416', '240115'] };
  const f = dateFixture(rule);
  assert.deepEqual(plain(f.window.JetUi.FilterValues.wire(rule, f.store.getState()).values),
    ['45292', '2', '73415', '1', '73416', '240115']);
  assert.match(f.window.JetUi.FilterValues.problem(rule, f.store.getState()), /日期「45292」無法辨識/);
});

// 使用者 2026-10-19 裁定：帶千分位的數字視為無法辨識的日期，和後端相同不當序列值換算，保留原文送出。
// 預篩選收斂計畫階段七（使用者 2026-10-22 裁定）：沒有千分位的 45292 也不再換成 2024-01-01，同樣保留原文。
// 第一次失敗：FrontendMapping 收據 20261024-045131921-2a00c186343c4fde945aa8eaaaea8da7。
test('date lists keep numbers with thousands separators as typed', () => {
  const rule = { type: 'fieldValue', field: 'postDate', operator: 'in', values: ['1,234', '45,292', '45292'] };
  const f = dateFixture(rule);
  assert.deepEqual(plain(f.window.JetUi.FilterValues.wire(rule, f.store.getState()).values),
    ['1,234', '45,292', '45292']);
});

// 預篩選收斂計畫階段七（使用者 2026-10-22 裁定「共用固定答案」）：前後端讀同一份日期固定答案 tools/tests/fixtures/date-normalization.json，
// C# 的 DateNormalizerTests 讀同一份；一邊改了規則另一邊就失敗。無法辨識的值前端保留原文，由後端寫出格式錯誤；
// 標成 passThrough 的寬鬆寫法前端不解析，原文交給後端。第一次失敗：FrontendMapping 收據 20261024-045131921-2a00c186343c4fde945aa8eaaaea8da7（純數字那一組當時仍換成日期）。
test('date lists follow the shared date fixture under both ROC settings', () => {
  const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/date-normalization.json'), 'utf8'));
  assert.ok(fixture.cases.length > 50, 'the shared fixture lists every date case from both sides');
  const rule = { type: 'fieldValue', field: 'postDate', operator: 'in', values: [] };
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

// 使用者 2026-10-19 裁定「視為無法辨識的日期」：沒有四位數年份或沒有日的寫法，前端不自行補齊，保留原文送出，
// 由後端逐值寫出無法辨識；寫出年份與日的英文寫法也原樣交給後端判斷。
test('date lists keep incomplete dates as typed for the backend to reject', () => {
  const rule = { type: 'fieldValue', field: 'postDate', operator: 'in', values: ['12/31', 'Dec 2025', '2025-12', 'June 11, 2025'] };
  const f = dateFixture(rule);
  assert.deepEqual(plain(f.window.JetUi.FilterValues.wire(rule, f.store.getState()).values),
    ['12/31', 'Dec 2025', '2025-12', 'June 11, 2025']);
  assert.equal(f.window.JetUi.FilterValues.problem(rule, f.store.getState()), '');
});

test('section 9 E: the legacy date range shares the same controls', () => {
  const f = dateFixture({ type: 'dateRange', field: 'postDate', from: '2025-01-01', to: '2025-01-31' });
  for (const key of ['from', 'to']) assert.ok(f.$('[data-rule-bind="' + key + '"]').classList.contains('date-input'));
});

test('section 9 E: legacy typed dates share the native control too', () => {
  const f = dateFixture({ type: 'typed', fieldId: 'rde.date', operator: 'between', from: '2025-01-01', to: '2025-01-31' });
  f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed', options: { rdeFields: [
    { fieldId: 'rde.date', sourceColumn: 'Date', label: '合成日期', valueType: 'date' }] } });
  for (const key of ['from', 'to']) assert.ok(f.$('[data-rule-bind="' + key + '"]').classList.contains('date-input'));
});

test('section 9 E: button and Enter add dates once, keep pasted dates, and allow chip removal', () => {
  const rule = { type: 'fieldValue', field: 'postDate', operator: 'in', values: ['2025/1/2'] };
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
  // 預篩選收斂計畫階段七（使用者 2026-10-22 裁定）：貼上的 45693 原本換成 2025-02-05，現在純數字保留原文。
  // 第一次失敗：FrontendMapping 收據 20261024-045131921-2a00c186343c4fde945aa8eaaaea8da7。
  assert.deepEqual(plain(f.window.JetUi.FilterValues.wire(rule, f.store.getState()).values), ['2025-02-03', '2025-02-04', '45693']);
});

test('section 9 E: a full date list keeps the pending date and explains the existing limit', () => {
  const values = Array.from({ length: 100 }, (_, i) => new Date(Date.UTC(2025, 0, i + 1)).toISOString().slice(0, 10));
  const rule = { type: 'fieldValue', field: 'postDate', operator: 'in', values };
  const f = dateFixture(rule);
  const input = f.$('[data-date-input]'); assert.ok(input); input.value = '2025-12-31';
  f.click('[data-date-add]');
  assert.deepEqual(plain(rule.values), values);
  assert.equal(f.$('[data-date-input]').value, '2025-12-31');
  assert.match(f.$('[data-date-error]').textContent, /100.*移除/);
});

// 修改前失敗：20261014-124845687-c1409ef46f0e4be099412d4659210ff4，搜尋與分類樹尚未共用。
test('section 9 F: account search waits for submission and shares load errors and category layout', async () => {
  const rule = { type: 'fieldValue', field: 'accNum', operator: 'in', values: ['0010'] };
  const f = dateFixture(rule);
  const tree = f.$('[data-account-tree] .category-tree'); assert.ok(tree);
  const form = f.$('[data-account-tree-search-form]'); assert.ok(form.classList.contains('page-search'));
  const input = form.querySelector('input'); input.value = '1103'; input.fire('input');
  assert.equal(f.requests.length, 0);
  form.fire('submit');
  const request = f.request('queryAccountMappingPage');
  assert.deepEqual(plain(request.payload), { categoryId: null, search: '1103', pageSize: 100, cursor: null });
  request.reject(new Error('合成來源失敗')); await settle(); await settle();
  const retry = f.$('[data-account-tree-retry]');
  assert.equal(retry.textContent, '重新載入');
  assert.ok(retry.closest('[data-load-failure].form-notice[role="alert"]'));
  f.click('[data-account-tree-retry]');
  assert.deepEqual(plain(f.request('queryAccountMappingPage').payload), plain(request.payload));
  assert.deepEqual(plain(rule.values), ['0010']);
});

test('section 9 F: table search uses the same search box and action wording', () => {
  const f = usageJourneyFixture('filter');
  f.dom.content.innerHTML = f.window.JetUi.pageSearchHtml('依傳票號碼搜尋');
  assert.ok(f.$('[data-page-search][data-search-box]'));
  assert.equal(f.$('[data-page-search] button[type="submit"]').textContent, '搜尋');
});

// 修改前失敗：20261014-125713987-b465b10fd9e74712beba823dbd855b05，人員說明仍使用資訊框。
test('section 9 F: person-list instructions use ordinary shared help text', () => {
  const f = dateFixture({ type: 'fieldValue', field: 'createBy', operator: 'in', values: [] });
  const note = f.dom.content.querySelectorAll('.help-text').find(node => /姓名或員工代碼/.test(node.textContent));
  assert.ok(note);
  assert.equal(note.classList.contains('form-notice'), false);
});

// 修改前失敗：20261014-130902298-e0231ac9b9224a4a9c5a0fa3a83091ce，按鈕仍在收合選單內。
test('section 9 G4: compound actions are directly visible in the group heading', () => {
  const f = filterOperationFixture();
  for (const kind of ['group', 'voucher']) {
    const button = f.$('[data-add-compound="' + kind + '"]');
    assert.ok(button.closest('.filter-group-heading'));
    assert.equal(button.closest('details'), null, 'the compound action is not hidden in a menu');
  }
  assert.equal(f.$('.filter-group-menu'), null, 'an empty group has no unused menu');
  f.click('[data-add-compound="group"]');
  f.click('[data-add-compound="voucher"]');
  assert.deepEqual(plain(f.draft().groups[0].rules.map(rule => rule.type)), ['group', 'voucher']);
});
