'use strict';
require('./support/resource-guard.cjs');
const test = require('node:test');
const assert = require('node:assert/strict');
const { usageJourneyFixture, filterOperationFixture, plain } = require('./support/frontend-fixtures.cjs');
// 第九節 G1、G2：修改前七項失敗，收據 20261014-132237474-091e773983ba47caa3479a82f666d8db。

function draftFixture(rule) {
  const f = usageJourneyFixture('filter', { redraw: true });
  f.store.setFilterDraft({ name: '合成條件', rationale: '固定答案', __nameDirty: true, __rationaleDirty: true,
    groups: [{ join: 'AND', rules: [rule] }] });
  return f;
}

test('section 9 G1: account fields no longer offer an embedded entry side', () => {
  for (const field of ['accNum', 'accName']) {
    const f = draftFixture({ type: 'fieldValue', field, operator: 'in', values: ['A'] });
    assert.equal(f.$('[data-value-key="drCr"]'), null);
    assert.equal(f.window.JetUi.FilterValues.wire(f.store.getState().filter.draft.groups[0].rules[0], f.store.getState()).drCr, undefined);
  }
});

test('section 9 G1: classification has two modes and no entry-side menu', () => {
  const f = draftFixture({ type: 'accountSide', categoryMode: 'is', categoryIds: ['builtin.cash'], categorySelection: 'subtree' });
  assert.equal(f.$('[data-account-subject]'), null);
  assert.deepEqual(plain(f.window.JetUi.ACCOUNT_SIDE_MODE_OPTIONS.map(item => item.value)), ['is', 'isNot']);
  assert.equal(f.window.JetUi.newFilterRule('accountSide').drCr, undefined);
});

// 低嚴重度事項 F9（使用者 2026-10-19 要求處理）：「包含下層分類」只有一種寫法，下拉選項和讀回、報告用同一個詞。
test('the subtree selection option reads 包含下層分類', () => {
  const f = draftFixture({ type: 'accountSide', categoryMode: 'is', categoryIds: ['builtin.cash'], categorySelection: 'subtree' });
  const options = Array.from(f.$('[data-rule-bind="categorySelection"]').querySelectorAll('option'));
  assert.deepEqual(options.map(option => option.textContent), ['相同分類用途', '僅分類本身', '包含下層分類']);
});

test('section 9 G1: voucher scope is expressed by child rules', () => {
  const f = filterOperationFixture();
  f.click('[data-add-compound="voucher"]');
  assert.equal(f.$('[data-compound-key="side"]'), null);
  assert.equal(f.draft().groups[0].rules[0].side, undefined);
  assert.equal(f.draft().groups[0].rules[0].quantifier, 'any');
  assert.ok(f.$('[data-child-kind] option[value="type:drCrOnly"]'), 'the child menu offers the standalone entry-side condition');
});

test('section 9 G1: only additional money fields offer a signed comparison', () => {
  const core = draftFixture({ type: 'fieldValue', field: 'amount', operator: 'greaterThan', value: '10', amountBasis: 'absolute' });
  assert.equal(core.$('[data-value-key="amountBasis"]'), null);
  const extra = draftFixture({ type: 'fieldValue', fieldId: 'rde.money', operator: 'greaterThan', value: '-10', amountBasis: 'signed' });
  extra.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed', options: { rdeFields: [
    { fieldId: 'rde.money', sourceColumn: 'Amount', label: '合成數值', valueType: 'money' }] } });
  assert.equal(extra.$('[data-value-key="amountBasis"]').value, 'signed');
  assert.equal(extra.window.JetUi.FilterValues.wire(extra.store.getState().filter.draft.groups[0].rules[0], extra.store.getState()).amountBasis, 'signed');
});

test('section 9 G1: common account combinations use explicit side children and keep their original defaults', () => {
  const f = filterOperationFixture();
  f.click('[data-add-check="accountCodePair"]');
  const rules = f.draft().groups[0].rules[0].rules;
  assert.deepEqual(plain(rules.map(rule => [rule.side, rule.quantifier, rule.rules.map(child => [child.type, child.drCr, child.field])])), [
    [null, 'any', [['drCrOnly', 'debit', null], ['fieldValue', null, 'accNum']]],
    [null, 'any', [['drCrOnly', 'credit', null], ['fieldValue', null, 'accNum']]]]);
});

// 2026-10-29 拿掉卡片字母：測試名稱、選擇器與身分標記改用卡片代號（原名用當時的四個卡片字母稱呼這四張卡，身分標記原本記字母）。
// 第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
const FIELD_CARDS = ['specificPreparer', 'specificKeywords', 'blankDescription', 'specificTrailingDigits'];
test('section 9 G2: the preparer, keyword, blank description and trailing digit cards share field conditions and keep their card identity', () => {
  const f = filterOperationFixture();
  f.store.setMappingCommitted('gl', { mapping: { createBy: 'Maker' }, mode: 'signed', options: {} });
  for (const card of FIELD_CARDS) f.click('[data-common-card="' + card + '"]');
  const rules = f.draft().groups[0].rules;
  assert.deepEqual(plain(rules.map(rule => [rule.__commonCard, rule.type, rule.field, rule.operator])), [
    ['specificPreparer', 'fieldValue', 'createBy', 'in'], ['specificKeywords', 'fieldValue', 'description', 'contains'],
    ['blankDescription', 'fieldValue', 'description', 'isBlank'], ['specificTrailingDigits', 'fieldValue', 'amount', 'endsWithDigits']]);
  assert.equal(rules[3].value, '000000');
  f.click('[data-common-card="specificKeywords"]');
  assert.deepEqual(plain(f.draft().groups[0].rules.map(rule => rule.__commonCard)), ['specificPreparer', 'blankDescription', 'specificTrailingDigits']);
});

test('section 9 G2: the trailing-zero preset visibly excludes absolute amounts below one', () => {
  const f = filterOperationFixture();
  f.click('[data-add-check="trailingZeros"]');
  const group = f.draft().groups[0].rules[0];
  assert.equal(group.type, 'group');
  assert.deepEqual(plain(group.rules.map(rule => [rule.type, rule.field, rule.operator, rule.value, rule.amountBasis])), [
    ['fieldValue', 'amount', 'endsWithDigits', '000000', 'absolute'],
    ['fieldValue', 'amount', 'greaterThanOrEqual', '1', 'absolute']]);
});

test('section 9 G2: oversized field lists explain how to split conditions into an OR subgroup', () => {
  const f = filterOperationFixture(), values = Array.from({ length: 101 }, (_, i) => String(i));
  for (const rule of [
    { type: 'fieldValue', field: 'createBy', operator: 'in', values },
    { type: 'fieldValue', field: 'description', operator: 'contains', values },
    { type: 'fieldValue', field: 'amount', operator: 'endsWithDigits', value: values.join(','), amountBasis: 'absolute' }
  ]) assert.match(f.window.JetUi.FilterValues.problem(rule, f.store.getState()), /100.*拆.*任一符合.*子條件組/);
});

test('section 9 G3: group headings no longer offer a same-voucher scope or anchor controls', () => {
  const f = filterOperationFixture();
  f.store.setFilterDraft({ name:'舊情境', rationale:'原文保留', groups:[{join:'AND',matchScope:'sameVoucher',rules:[
    {type:'drCrOnly',drCr:'debit'},{type:'fieldValue',field:'accNum',operator:'in',values:['A']}]}] });
  assert.equal(f.$('[data-group-bind="matchScope"]'),null);
  assert.equal(f.$('[data-action="make-primary"]'),null);
  assert.doesNotMatch(f.$('.scenario-set').textContent, /設為主要條件|主要條件|比對範圍/);
  assert.ok(f.$('[data-set-combinator]'), 'legacy groups do not hide the ordinary join control');
  assert.equal(f.$('[data-set-combinator]').disabled,false);
  assert.equal(f.draft().groups[0].matchScope, 'sameVoucher', 'opening a saved definition must not silently convert it');
});
