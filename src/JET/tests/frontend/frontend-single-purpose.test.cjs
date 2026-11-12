'use strict';
require('./support/resource-guard.cjs');
const test = require('node:test');
const assert = require('node:assert/strict');
const { usageJourneyFixture, filterOperationFixture, plain } = require('./support/frontend-fixtures.cjs');
// 每條條件只表達一件事。

function draftFixture(rule) {
  const f = usageJourneyFixture('filter', { redraw: true });
  f.store.setFilterDraft({ name: '合成條件', rationale: '固定答案', __nameDirty: true, __rationaleDirty: true,
    groups: [{ join: 'AND', conditions: [rule] }] });
  return f;
}

test('account fields offer no embedded entry side', () => {
  for (const field of ['accountNumber', 'accountName']) {
    const f = draftFixture({ type: 'fieldValue', field, operator: 'in', values: ['A'] });
    assert.equal(f.$('[data-value-key="drCr"]'), null);
    assert.equal(f.window.JetUi.FilterValues.wire(f.store.getState().filter.draft.groups[0].conditions[0], f.store.getState()).drCr, undefined);
  }
});

test('classification has two modes and no entry-side menu', () => {
  const f = draftFixture({ type: 'accountSide', categoryMode: 'is', categoryIds: ['builtin.cash'], categorySelection: 'subtree' });
  assert.equal(f.$('[data-account-subject]'), null);
  assert.deepEqual(plain(f.window.JetUi.ACCOUNT_SIDE_MODE_OPTIONS.map(item => item.value)), ['is', 'isNot']);
  assert.equal(f.window.JetUi.newFilterCondition('accountSide').drCr, undefined);
});

// 「包含下層分類」只有一種寫法，下拉選項和讀回、報告用同一個詞。
// 「選取依據」暫時隱藏，開關 Ui.FEATURES.categoryHierarchy 預設關著；先核對開關關著時沒有這個下拉，再打開開關重畫。
test('the subtree selection option reads 包含下層分類', () => {
  const f = draftFixture({ type: 'accountSide', categoryMode: 'is', categoryIds: ['builtin.cash'], categorySelection: 'subtree' });
  assert.equal(f.$('[data-rule-bind="categorySelection"]'), null, 'with the switch off there is no selection menu');
  f.window.JetUi.FEATURES.categoryHierarchy = true;
  f.store.touch();
  const options = Array.from(f.$('[data-rule-bind="categorySelection"]').querySelectorAll('option'));
  assert.deepEqual(options.map(option => option.textContent), ['相同分類用途', '僅分類本身', '包含下層分類']);
});

test('voucher scope is expressed by child rules', () => {
  const f = filterOperationFixture();
  f.click('[data-add-compound="voucher"]');
  assert.equal(f.$('[data-compound-key="side"]'), null);
  assert.equal(f.draft().groups[0].conditions[0].side, undefined);
  assert.equal(f.draft().groups[0].conditions[0].quantifier, 'any');
  assert.ok(f.$('[data-child-kind] option[value="type:drCrOnly"]'), 'the child menu offers the standalone entry-side condition');
});

test('only additional money fields offer a signed comparison', () => {
  const core = draftFixture({ type: 'fieldValue', field: 'amount', operator: 'greaterThan', value: '10', amountBasis: 'absolute' });
  assert.equal(core.$('[data-value-key="amountBasis"]'), null);
  const extra = draftFixture({ type: 'fieldValue', fieldId: 'rde.money', operator: 'greaterThan', value: '-10', amountBasis: 'signed' });
  extra.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed', options: { rdeFields: [
    { fieldId: 'rde.money', sourceColumn: 'Amount', label: '合成數值', valueType: 'amount' }] } });
  assert.equal(extra.$('[data-value-key="amountBasis"]').value, 'signed');
  assert.equal(extra.window.JetUi.FilterValues.wire(extra.store.getState().filter.draft.groups[0].conditions[0], extra.store.getState()).amountBasis, 'signed');
});

// 「借方與貸方科目編號組合」是情境範例，套用後替換整份條件；借方與貸方各一個明示方向的子條件。
test('common account combinations use explicit side children and keep their original defaults', () => {
  const f = filterOperationFixture();
  f.click('[data-template-key="accountNumberPair"]');
  const rules = f.draft().groups[0].conditions[0].conditions;
  assert.deepEqual(plain(rules.map(rule => [rule.side, rule.quantifier, rule.conditions.map(child => [child.type, child.drCr, child.field])])), [
    [null, 'any', [['drCrOnly', 'debit', null], ['fieldValue', null, 'accountNumber']]],
    [null, 'any', [['drCrOnly', 'credit', null], ['fieldValue', null, 'accountNumber']]]]);
});

const FIELD_CARDS = ['specificPreparer', 'specificKeywords', 'blankDescription', 'specificTrailingDigits'];
test('the preparer, keyword, blank description and trailing digit cards share field conditions and keep their card identity', () => {
  const f = filterOperationFixture();
  f.store.setMappingCommitted('gl', { mapping: { createdBy: 'Maker' }, mode: 'signed', options: {} });
  for (const card of FIELD_CARDS) f.click('[data-common-card="' + card + '"]');
  const rules = f.draft().groups[0].conditions;
  assert.deepEqual(plain(rules.map(rule => [rule.__commonCard, rule.type, rule.field, rule.operator])), [
    ['specificPreparer', 'fieldValue', 'createdBy', 'in'], ['specificKeywords', 'fieldValue', 'description', 'contains'],
    ['blankDescription', 'fieldValue', 'description', 'isBlank'], ['specificTrailingDigits', 'fieldValue', 'amount', 'endsWithDigits']]);
  assert.equal(rules[3].value, '000000');
  f.click('[data-common-card="specificKeywords"]');
  assert.deepEqual(plain(f.draft().groups[0].conditions.map(rule => rule.__commonCard)), ['specificPreparer', 'blankDescription', 'specificTrailingDigits']);
});

// 「金額尾數連續多個 0」是一條條件，比較方式是尾數連續幾個 0，0 元不選入；條件列寫出整數部分為 0 的金額不符合。
// 0 的個數照 IDEA 版由 GL 算出，跟著已確認的 GL 配對帶回；加入時帶這個案件的個數，這裡帶 5，確認用的是案件的個數。
test('the trailing-zero preset visibly excludes absolute amounts below one', () => {
  const f = filterOperationFixture();
  f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed', options: {}, zerosThreshold: 5 });
  f.click('[data-condition-source="field"]');
  f.click('[data-add-shortcut="trailingZeros"]');
  const rules = f.draft().groups[0].conditions;
  assert.deepEqual(plain(rules.map(rule => [rule.type, rule.field, rule.operator, rule.value, rule.amountBasis])), [
    ['fieldValue', 'amount', 'endsWithZeros', '5', 'absolute']]);
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row.querySelectorAll('.rule-explanation').some(node => /整數部分為 0 的金額不符合/.test(node.textContent)),
    'the row says amounts whose whole part is 0 do not match');
});

// 子條件組的連接在畫面上只寫 AND 與 OR，提示寫「放進子條件組，用 OR 連接」。
test('oversized field lists explain how to split conditions into an OR subgroup', () => {
  const f = filterOperationFixture(), values = Array.from({ length: 101 }, (_, i) => String(i));
  for (const rule of [
    { type: 'fieldValue', field: 'createdBy', operator: 'in', values },
    { type: 'fieldValue', field: 'description', operator: 'contains', values },
    { type: 'fieldValue', field: 'amount', operator: 'endsWithDigits', value: values.join(','), amountBasis: 'absolute' }
  ]) assert.match(f.window.JetUi.FilterValues.problem(rule, f.store.getState()), /100.*拆.*子條件組.*用 OR 連接/);
});

test('group headings offer no same-voucher scope or anchor controls', () => {
  const f = filterOperationFixture();
  f.store.setFilterDraft({ name:'舊情境', rationale:'原文保留', groups:[{join:'AND',matchScope:'sameVoucher',conditions:[
    {type:'drCrOnly',drCr:'debit'},{type:'fieldValue',field:'accountNumber',operator:'in',values:['A']}]}] });
  assert.equal(f.$('[data-group-bind="matchScope"]'),null);
  assert.equal(f.$('[data-action="make-primary"]'),null);
  assert.doesNotMatch(f.$('.scenario-set').textContent, /設為主要條件|主要條件|比對範圍/);
  assert.ok(f.$('[data-set-combinator]'), 'legacy groups do not hide the ordinary join control');
  assert.equal(f.$('[data-set-combinator]').disabled,false);
  assert.equal(f.draft().groups[0].matchScope, 'sameVoucher', 'opening a saved definition must not silently convert it');
});
