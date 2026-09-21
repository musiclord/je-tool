'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '../../src/JET/JET/wwwroot/js');
const copy = value => JSON.parse(JSON.stringify(value));
const settle = () => new Promise(resolve => setImmediate(resolve));

// Load whole production scripts unchanged. Only DOM plumbing and backend timing are test doubles;
// rendering, event binding, cache lifetime and state transitions all execute production code.
function fixture() {
  const pending = [];
  const window = { setTimeout, clearTimeout, setInterval: () => 1, clearInterval() {}, JetApi: {
    mappingValueProfile(payload) {
      return new Promise((resolve, reject) => pending.push({ payload, resolve, reject }));
    }
  } };
  const document = { querySelector: () => null, createElement() {
    return { textContent: '', get innerHTML() {
      return String(this.textContent).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    } };
  } };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'steps/mapping-step.js']) {
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context, { filename: file });
  }
  const store = window.JetStore;
  store.setProject({ projectId: 'synthetic' });
  store.setImportResult('gl', { batchId: 'batch-1', columns: ['Manual', 'Posting', 'Other', '__proto__'], rowCount: 2 });
  store.setMappingDraft('gl', 'manual', 'Manual');
  store.setMappingDraft('gl', 'postingStatus', 'Posting');

  function render() {
    const events = new Map();
    const controls = new Map();
    function control(selector) {
      if (!controls.has(selector)) controls.set(selector, { value: '', addEventListener(event, fn) { events.set(selector + ':' + event, fn); } });
      return controls.get(selector);
    }
    const action = /^\[data-action="([a-z-]+)"\]$/;
    const section = {
      addEventListener() {},
      querySelector(selector) {
        if (selector === '[data-bind="gl-options"]') return {};
        if (/^\[data-manual-(mode|blank)\]$/.test(selector) && container.innerHTML.includes(selector.slice(1, -1))) return control(selector);
        if (action.test(selector) && container.innerHTML.includes(selector.slice(1, -1))) return control(selector);
        return null;
      },
      querySelectorAll(selector) {
        const item = this.querySelector(selector);
        return item ? [item] : [];
      }
    };
    const container = {
      innerHTML: '',
      querySelector(selector) { return selector === '[data-bind="mapping-gl"]' ? section : null; },
      querySelectorAll() { return []; }
    };
    window.JetUi.renderStep('mapping', container, store.getState());
    return {
      html: container.innerHTML,
      change(selector, value) {
        const target = controls.get(selector);
        assert.ok(target, 'Rendered selector must exist: ' + selector);
        target.value = value;
        const handler = events.get(selector + ':change');
        assert.ok(handler, 'Rendered selector must be bound: ' + selector);
        handler();
      },
      click(name) {
        const fn = events.get('[data-action="' + name + '"]:click');
        assert.ok(fn, 'Rendered control must be bound: ' + name);
        fn();
      }
    };
  }
  return { store, pending, render, window };
}
const profile = value => ({ blankCount: 0, distinctCount: 1, values: [{ value, count: 2 }], truncated: false });

test('one-sided manual policy and explicit blank choice survive editing and loaded metadata', () => {
  const f = fixture();
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M'], automaticValues: ['A'] } });
  f.render().change('[data-manual-mode]', 'automatic');
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy),
    { manualValues: ['M'], automaticValues: [], unlistedValueKind: 'automatic' });
  f.render().change('[data-manual-blank]', 'unclassified');
  const saved = copy(f.store.getState().mapping.gl.options);
  f.window.JetUi.applyLoadedProject({ project: { projectId: 'synthetic', currentStep: 1 },
    importState: { gl: { batchId: 'batch-1', columns: ['Manual', 'Posting', 'Other', '__proto__'], rowCount: 2 } },
    mapping: { gl: { ...saved, mapping: { manual: 'Manual' }, amountMode: 'dual', formatVersion: 2 } } });
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy), saved.manualAutoPolicy);
  f.render().click('remap-gl');
  assert.match(f.render().html, /只列人工，其餘非空白值視為自動/);
  f.render().change('[data-manual-mode]', 'reject');
  assert.equal(f.store.getState().mapping.gl.options.manualAutoPolicy.blankValueKind, 'unclassified');
  f.store.setMappingDraft('gl', 'manual', '');
  f.store.setMappingDraft('gl', 'manual', 'Manual');
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy), { manualValues: ['1'], automaticValues: ['0'] });
});

for (const order of [[0, 1], [1, 0]]) {
  test('independent value profiles finish in order ' + order.join(','), async () => {
    const f = fixture();
    f.render().click('load-manual-profile');
    f.render().click('load-posting-profile');
    assert.equal(f.pending.length, 2);
    for (const index of order) f.pending[index].resolve(profile(index === 0 ? 'MANUAL-RESULT' : 'POSTING-RESULT'));
    await settle();
    const html = f.render().html;
    assert.match(html, /MANUAL-RESULT/);
    assert.match(html, /POSTING-RESULT/);
    assert.doesNotMatch(html, /讀取來源值中/);
  });
}

test('failed profile can be retried and duplicate pending requests are suppressed', async () => {
  const f = fixture();
  const first = f.render();
  first.click('load-manual-profile');
  first.click('load-manual-profile');
  assert.equal(f.pending.length, 1);
  f.pending[0].reject(new Error('synthetic read failure'));
  await settle();
  assert.match(f.render().html, /再試一次/);
  f.render().click('load-manual-profile');
  assert.equal(f.pending.length, 2);
  f.pending[1].resolve(profile('RETRIED'));
  await settle();
  assert.match(f.render().html, /RETRIED/);
});

for (const transition of ['import', 'project']) {
  test('late profile cannot cross ' + transition + ' boundary even when IDs are reused', async () => {
    const f = fixture();
    f.render().click('load-manual-profile');
    if (transition === 'import') f.store.setImportResult('gl', copy(f.store.getState().importState.gl));
    else f.store.setProject({ projectId: 'synthetic' });
    f.render().click('load-manual-profile');
    assert.equal(f.pending.length, 2);
    f.pending[1].resolve(profile('NEW-RESULT'));
    f.pending[0].resolve(profile('OLD-RESULT'));
    await settle();
    const html = f.render().html;
    assert.ok(html.includes('NEW-RESULT'), 'new profile must be rendered');
    assert.ok(!html.includes('OLD-RESULT'), 'old response must be discarded');
  });
}

test('completed profile is not shown even once after a replacement import', async () => {
  const f = fixture();
  f.store.setMappingDraft('gl', 'postingStatus', '');
  f.render().click('load-manual-profile');
  f.pending[0].resolve(profile('OLD-RESULT'));
  await settle();
  assert.ok(f.render().html.includes('OLD-RESULT'));
  f.store.setImportResult('gl', { batchId: 'batch-2', columns: ['Manual'], rowCount: 2 });
  assert.ok(!f.render().html.includes('OLD-RESULT'), 'first render must discard the old source values');
});

test('detaching and reattaching a manual source resets codes but retains valid value profile', async () => {
  const f = fixture();
  f.render().click('load-manual-profile');
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M'], automaticValues: ['A'] } });
  f.store.setMappingDraft('gl', 'manual', '');
  f.pending[0].resolve(profile('SOURCE-VALUE'));
  await settle();
  assert.ok(!f.render().html.includes('SOURCE-VALUE'));
  f.store.setMappingDraft('gl', 'manual', 'Manual');
  assert.ok(f.render().html.includes('SOURCE-VALUE'));
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy), { manualValues: ['1'], automaticValues: ['0'] });
});

for (const method of ['set', 'assign', 'replace']) {
  test('posting policy follows its source through ' + method + ' and restores committed snapshot', () => {
    const f = fixture();
    const initial = { acceptedValues: ['POSTED'], includeBlank: true };
    f.store.patchGlMappingOptions({ postingStatusPolicy: initial });
    const gl = f.store.getState().mapping.gl;
    f.store.setMappingCommitted('gl', { mapping: gl.draft, mode: gl.amountMode, options: gl.options });
    function change(column) {
      if (method === 'set') f.store.setMappingDraft('gl', 'postingStatus', column);
      if (method === 'assign') f.store.assignColumnToField('gl', column || 'Other', column ? 'postingStatus' : '', ['dcDebitCode']);
      if (method === 'replace') f.store.replaceMappingDraft('gl', { manual: 'Manual', ...(column ? { postingStatus: column } : {}) });
    }
    change('Posting');
    assert.deepEqual(copy(gl.options.postingStatusPolicy), initial);
    change('Other');
    assert.equal(gl.options.postingStatusPolicy, null);
    f.store.patchGlMappingOptions({ postingStatusPolicy: initial });
    change('');
    assert.equal(gl.options.postingStatusPolicy, null);
    assert.deepEqual(copy(gl.committed.options.postingStatusPolicy), initial);
    f.store.restoreCommittedMapping('gl');
    assert.equal(gl.draft.postingStatus, 'Posting');
    assert.deepEqual(copy(gl.options.postingStatusPolicy), initial);
  });
}

test('RDE select all preserves edits and excludes core columns and literal values', () => {
  const f = fixture();
  f.store.setMappingDraft('gl', 'dcDebitCode', 'Other');
  f.store.patchGlMappingOptions({ rdeFields: [{ fieldId: 'stable-id', sourceColumn: 'Other', label: 'Edited', valueType: 'date' }] });
  f.render().click('select-all-rde');
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.rdeFields), [
    { fieldId: 'stable-id', sourceColumn: 'Other', label: 'Edited', valueType: 'date' },
    { sourceColumn: '__proto__', label: '__proto__', valueType: 'text' }
  ]);
  f.store.setMappingDraft('gl', 'description', 'Other');
  assert.equal(f.store.getState().mapping.gl.options.rdeFields.length, 1);
  f.render().click('clear-all-rde');
  assert.equal(f.store.getState().mapping.gl.options.rdeFields.length, 0);
});

test('projection error is escaped, shown locally and discarded after replacing the source', async () => {
  const f = fixture();
  const mapping = { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', debitAmount: 'Debit', creditAmount: 'Credit' };
  f.store.setImportResult('gl', { batchId: 'ready', columns: Object.values(mapping), rowCount: 1 });
  f.store.replaceMappingDraft('gl', mapping);
  f.window.JetApi.mappingCommitGl = () => Promise.reject(new Error('空白來源 <synthetic>'));
  const ready = f.render();
  assert.ok(ready.html.includes('data-action="commit-gl">'), 'complete mapping must allow commit');
  ready.click('commit-gl');
  await settle();
  const failed = f.render().html;
  assert.ok(failed.includes('data-bind="mapping-commit-error-gl"'));
  assert.ok(failed.includes('空白來源 &lt;synthetic&gt;'));
  assert.ok(!failed.includes('<synthetic>'));
  f.store.setImportResult('gl', { batchId: 'replacement', columns: Object.values(mapping), rowCount: 1 });
  assert.ok(!f.render().html.includes('data-bind="mapping-commit-error-gl"'), 'previous source error must not accuse replacement data');
});
