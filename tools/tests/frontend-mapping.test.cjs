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
    queryDataPreview() { return Promise.resolve({ columns: [], rows: [] }); },
    mappingValueProfile(payload) {
      return new Promise((resolve, reject) => pending.push({ payload, reject, resolve(data) {
        // Default fake returns only exact identity groups. Unicode equivalence cases below
        // supply fixed backend groups explicitly; no JS case/trim implementation acts as oracle.
        if (!data.comparisonGroups) data = { ...data, comparisonGroups:
          [...(data.values || []).map(item => item.value), ...(payload.comparisonValues || [])].map(value => [value]) };
        if (payload.checkSourceValues && !Object.hasOwn(data, 'missingComparisonValues')) data = { ...data, missingComparisonValues: [] };
        resolve(data);
      } }));
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
    const delegated = new Map();
    const controls = new Map();
    function control(selector) {
      if (!controls.has(selector)) {
        const attribute = selector.match(/^\[([^\]]+)\]/)?.[1] || '';
        const literal = attribute.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        const tag = container.innerHTML.match(new RegExp('<input[^>]*' + literal + '[^>]*>'));
        controls.set(selector, { value: tag && (tag[0].match(/value="([^"]*)"/) || [])[1] || '',
          closest: () => null,
          addEventListener(event, fn) { events.set(selector + ':' + event, fn); } });
      }
      return controls.get(selector);
    }
    const action = /^\[data-action="([a-z-]+)"\]$/;
    const section = {
      addEventListener(event, handler) { delegated.set(event, (delegated.get(event) || []).concat(handler)); },
      querySelector(selector) {
        if (selector === '[data-bind="gl-options"]') return {};
        if (selector === '[data-approval-source]' && container.innerHTML.includes('data-approval-source')) return control(selector);
        if (/^\[data-bind="(manual-code-add|automatic-code-add|posting-value-add)"\]$/.test(selector)
            && container.innerHTML.includes(selector.slice(1, -1))) return control(selector);
        if (/^\[data-manual-(mode|blank)\]$/.test(selector) && container.innerHTML.includes(selector.slice(1, -1))) return control(selector);
        if (action.test(selector) && container.innerHTML.includes(selector.slice(1, -1))) return control(selector);
        return null;
      },
      querySelectorAll(selector) {
        if (selector === '[data-option-bind="approvalDateMode"]') {
          return ['unmapped', 'mapped', 'sameAsPostDate'].map(mode => {
            const target = control(selector + '[value="' + mode + '"]');
            target.value = mode; target.checked = store.getState().mapping.gl.options.approvalDateMode === mode;
            return target;
          });
        }
        if (['[data-manual-assign]', '[data-posting-value]', '[data-manual-include]', '[data-rde-column]', '[data-rde-type]'].includes(selector)) {
          const attribute = selector.slice(1, -1);
          return Array.from(container.innerHTML.matchAll(/<(?:input|select)\b[^>]*>/g)).filter(match => match[0].includes(attribute + '=')).map(match => {
            const raw = match[0].match(new RegExp(attribute + '="([^"]*)"'))[1];
            const kind = match[0].match(/value="([^"]*)"/)?.[1];
            const identity = '[' + attribute + '="' + raw + '"]' + (attribute === 'data-manual-assign' ? '[value="' + kind + '"]' : '');
            const target = control(identity);
            target.value = kind; target.checked = / checked/.test(match[0]); target.disabled = / disabled/.test(match[0]);
            target.getAttribute = name => name === attribute ? raw : (match[0].match(new RegExp(name + '="([^"]*)"')) || [])[1] || null;
            return target;
          });
        }
        if (selector === '[data-action="add-code"]') {
          return ['manual', 'automatic'].map(group => {
            const target = control('[data-action="add-code"][data-code-group="' + group + '"]');
            target.getAttribute = name => name === 'data-code-group' ? group : null;
            return target;
          });
        }
        if (selector === '[data-retry-comparison]' || selector === '[data-retry-source-check]') {
          const attribute = selector.slice(1, -1);
          return Array.from(container.innerHTML.matchAll(new RegExp(attribute + '="([^"]*)"', 'g')), match => {
            const target = control('[' + attribute + '="' + match[1] + '"]');
            target.getAttribute = () => match[1]; return target;
          });
        }
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
      find(selector) { return controls.get(selector) || controls.get([...controls.keys()].find(key => key.startsWith(selector))); },
      toggle(selector, checked) {
        const target = controls.get(selector); assert.ok(target, selector + ' rendered'); target.checked = checked;
        for (const fn of delegated.get('change') || []) fn({ target, currentTarget: section });
        const handler = events.get(selector + ':change'); assert.ok(handler, selector + ' bound'); handler();
      },
      input(selector, value) {
        const target = controls.get(selector);
        assert.ok(target, 'Rendered input must exist: ' + selector);
        target.value = value;
        const handler = events.get(selector + ':input');
        assert.ok(handler, 'Rendered input must preserve its draft: ' + selector);
        handler();
      },
      value(selector) { return controls.get(selector)?.value; },
      addCode(group) { events.get('[data-action="add-code"][data-code-group="' + group + '"]:click')(); },
      retryComparison(column) { events.get('[data-retry-comparison="' + column + '"]:click')(); },
      retrySource(column) { events.get('[data-retry-source-check="' + column + '"]:click')(); },
      chooseManual(value, kind) {
        const key = '[data-manual-assign="' + value + '"][value="' + kind + '"]';
        controls.get(key).checked = true; events.get(key + ':change')();
      },
      togglePosting(value, checked) {
        const key = '[data-posting-value="' + value + '"]';
        controls.get(key).checked = checked; events.get(key + ':change')();
      },
      change(selector, value) {
        const target = controls.get(selector);
        assert.ok(target, 'Rendered selector must exist: ' + selector);
        target.value = value;
        const handler = events.get(selector + ':change');
        assert.ok(handler, 'Rendered selector must be bound: ' + selector);
        for (const fn of delegated.get('change') || []) fn({ target, currentTarget: section });
        handler();
      },
      approval(mode) {
        const key = '[data-option-bind="approvalDateMode"][value="' + mode + '"]';
        controls.get(key).checked = true; events.get(key + ':change')();
      },
      click(name) {
        const fn = events.get('[data-action="' + name + '"]:click');
        assert.ok(fn, 'Rendered control must be bound: ' + name);
        fn();
      }
    };
  }
  return { store, pending, render, window, document };
}
const profile = value => ({ blankCount: 0, distinctCount: 1, values: [{ value, count: 2 }], truncated: false });

// Batch9 high5 adds the explicit credit literal; first failure: 20261004-095907202-301e794a887548aba0f6956b27416aad.
// Preserve the exact schema equality: only side/flag gain this approved field, not TB or other GL modes.
for (const [kind, mode, amountKeys] of [
  ['gl', 'signed', ['amount']], ['gl', 'side', ['amount', 'dcField', 'dcDebitCode', 'dcCreditCode']],
  ['gl', 'flag', ['amount', 'dcField', 'dcDebitCode', 'dcCreditCode']], ['gl', 'dual', ['debitAmount', 'creditAmount']],
  ['tb', 'direct', ['amount']], ['tb', 'debitCredit', ['debitAmt', 'creditAmt']],
  ['tb', 'openClose', ['openingBalance', 'closingBalance']],
  ['tb', 'openCloseBySide', ['openingDebit', 'openingCredit', 'closingDebit', 'closingCredit']]
]) {
  test('both mapping views expose the full fixed schema for ' + kind + '/' + mode, () => {
    const f = fixture();
    f.store.setImportResult('tb', { batchId: 'tb-schema', columns: ['Account', 'Value'], rowCount: 2 });
    f.store.setMappingMode(kind, mode);
    const expected = (kind === 'gl' ? ['docNum', 'lineID', 'postDate', 'docDate', 'voucherDate', 'accNum', 'accName',
      'description', 'jeSource', 'createBy', 'approveBy', 'manual', 'postingStatus'] : ['accNum', 'accName']).concat(amountKeys).sort();
    for (const view of ['classic', 'grid']) {
      f.store.setMappingUiMode(view);
      const html = f.render().html.split('data-bind="mapping-' + kind + '"')[1].split('data-bind="mapping-tb"')[0];
      const shown = view === 'classic'
        ? [...html.matchAll(/data-mapping-(?:key|field)="([^"]+)"/g)].map(m => m[1])
        : [...html.matchAll(/<select\b[^>]*data-map-col=[\s\S]*?<\/select>/g)]
          .flatMap(m => [...m[0].matchAll(/<option value="([^"]+)"/g)].map(option => option[1]))
          .concat([...html.matchAll(/data-mapping-key="([^"]+)"/g)].map(m => m[1]));
      assert.deepEqual([...new Set(shown)].sort(), expected, view + ' must neither omit nor invent a field');
    }
  });
}

test('approval source is present only for source-provided mode in both mapping views', () => {
  const f = fixture();
  for (const view of ['classic', 'grid']) for (const mode of ['unmapped', 'mapped', 'sameAsPostDate']) {
    f.store.setMappingUiMode(view);
    f.store.patchGlMappingOptions({ approvalDateMode: mode });
    const html = f.render().html;
    assert.equal((html.match(/data-approval-source/g) || []).length, mode === 'mapped' ? 1 : 0, view + '/' + mode);
    assert.equal((html.match(/name="gl-approval-mode"/g) || []).length, 3, 'one shared three-mode control, not duplicate widgets');
    assert.match(html, /傳票核准日/);
  }
});

test('switching approval mode and view preserves a shared source until the user detaches it', () => {
  const f = fixture();
  f.store.setMappingDraft('gl', 'postDate', 'Other');
  f.render().approval('mapped');
  f.render().change('[data-approval-source]', 'Other');
  assert.equal(f.store.getState().mapping.gl.draft.docDate, 'Other');
  f.store.setMappingUiMode('grid');
  assert.match(f.render().html, /data-approval-source/);
  assert.equal(f.store.getState().mapping.gl.draft.postDate, 'Other');
  f.render().approval('sameAsPostDate');
  assert.equal(f.store.getState().mapping.gl.draft.docDate, undefined);
  assert.equal(f.store.getState().mapping.gl.draft.postDate, 'Other');
  assert.doesNotMatch(f.render().html, /data-approval-source/);
  f.store.setMappingUiMode('classic');
  f.render().approval('unmapped');
  assert.doesNotMatch(f.render().html, /data-approval-source/);
  f.render().approval('mapped'); f.render().change('[data-approval-source]', 'Manual');
  assert.equal(f.store.getState().mapping.gl.options.approvalDateMode, 'mapped');
  assert.equal(f.store.getState().mapping.gl.draft.docDate, 'Manual');
});

for (const [manual, automatic, overlap] of [
  ['ß', 'SS', false], ['ı', 'I', false], ['ſ', 'S', false], ['K', 'K', false],
  ['ΐ', 'ΐ', false], ['ß', 'ẞ', false], ['Σ', 'ς', true], ['é', 'É', true], ['ᾀ', 'ᾈ', true],
  ['\u0085M\u0085', 'm', true], ['\uFEFFM\uFEFF', 'M', false]
]) {
  test('manual code UI matches fixed .NET ordinal case oracle for ' + JSON.stringify([manual, automatic]), async () => {
    const f = fixture();
    f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: [manual], automaticValues: [automatic] } });
    const validation = f.render().html;
    assert.equal(validation.includes('人工與自動代碼不得重複'), false, 'only the backend may reject overlapping policies');
    f.render().click('load-manual-profile');
    f.pending[0].resolve({ ...profile(automatic), comparisonGroups: overlap ? [[manual, automatic]] : [[manual], [automatic]] });
    await settle();
    const row = f.render().html.match(/<li class="value-assign">([\s\S]*?)<\/li>/)?.[1];
    assert.ok(row);
    if (overlap) {
      assert.match(row, /人工與自動重複/);
      assert.doesNotMatch(row, /value="(?:manual|automatic)" checked/);
      return;
    }
    assert.match(row, /value="automatic" checked/, 'the automatic code must not be shown as manual');
    assert.doesNotMatch(row, /value="manual" checked/);
  });
}

test('free text code drafts preserve raw Unicode and do not guess equivalence before backend comparison', () => {
  const f = fixture();
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['ß'], automaticValues: ['SS'] } });
  const form = f.render();
  form.input('[data-bind="manual-code-add"]', '\uFEFF M\u0085');
  form.addCode('manual');
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.manualValues), ['ß', '\uFEFF M\u0085']);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.automaticValues), ['SS']);
  // 第 6 批改成手輸後立即做純文字 metadata 比較，仍不得因為加碼讀取母體。
  // 首次失敗：20261004-083227172-9246e4b0d721484684d6488f54c9bd5d；保留上方原始 Unicode 與未自猜等價的斷言。
  assert.equal(f.pending.length, 1, 'adding once schedules one bounded metadata request');
  assert.equal(f.pending[0].payload.comparisonOnly, true);
  assert.notEqual(f.pending[0].payload.checkSourceValues, true);
  assert.equal(f.pending[0].payload.limit, undefined, 'no source-profile sample query');
  assert.deepEqual(copy(f.pending[0].payload.comparisonValues), ['ß', '\uFEFF M\u0085', 'SS']);
  assert.equal(f.pending.filter(request => request.payload.comparisonOnly !== true || request.payload.checkSourceValues === true).length, 0,
    'there are no full-source or source-presence reads');
});

test('added policy codes use metadata-only comparison and unknown or failed correspondence is not classified', async () => {
  const f = fixture();
  const mapping = { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo',
    debitAmount: 'Debit', creditAmount: 'Credit', manual: 'Manual' };
  f.store.setImportResult('gl', { batchId: 'ready', columns: Object.values(mapping), rowCount: 1 });
  f.store.replaceMappingDraft('gl', mapping);
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M'], automaticValues: ['A'] } });
  f.render().click('load-manual-profile');
  f.pending[0].resolve(profile('ß'));
  await settle(); f.render();
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M'], automaticValues: ['A', 'SS'] } });
  let html = f.render().html;
  assert.equal(f.pending.length, 2);
  assert.equal(f.pending[1].payload.comparisonOnly, true);
  assert.deepEqual(copy(f.pending[1].payload.comparisonValues), ['ß', 'SS']);
  assert.match(html, /正在確認來源值對應/);
  assert.doesNotMatch(html.match(/<li class="value-assign">([\s\S]*?)<\/li>/)[1], /value="(?:manual|automatic|none)" checked/);
  f.pending[1].reject(new Error('synthetic comparison failure')); await settle();
  html = f.render().html;
  assert.match(html, /來源值對應尚未確認/);
  assert.ok(html.includes('data-action="commit-gl">'), 'comparison failure must not disable confirmation');
  assert.doesNotMatch(html, /mapping-option-problems[^>]*>[^<]*人工與自動代碼不得重複/);
  assert.equal(f.pending.length, 2, 'a failure does not create an automatic retry loop');
  f.render().retryComparison('Manual');
  f.pending[2].resolve({ sourceColumn: 'Manual', comparisonGroups: [['ß'], ['SS']] });
  await settle();
  html = f.render().html;
  assert.match(html.match(/<li class="value-assign">([\s\S]*?)<\/li>/)[1], /value="none" checked/);
  assert.doesNotMatch(html, /正在確認來源值對應|來源值對應尚未確認/);
});

test('comparison metadata pages do not cap the number of saved policy codes', async () => {
  const f = fixture();
  const codes = Array.from({ length: 205 }, (_, i) => 'M-' + i);
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: codes, automaticValues: ['A'] } });
  f.render().click('load-manual-profile');
  assert.equal(f.pending[0].payload.comparisonValues.length, 100);
  f.pending[0].resolve(profile('SOURCE')); await settle(); f.render();
  for (let i = 1; i < 3; i++) {
    assert.equal(f.pending[i].payload.comparisonOnly, true);
    assert.ok(f.pending[i].payload.comparisonValues.length <= 100);
    f.pending[i].resolve({ sourceColumn: 'Manual' }); await settle(); f.render();
  }
  // 第 6 批另查完整來源的代碼存在性，不能把比較文字的 metadata 回應當成來源證據。
  // 首次失敗：20261004-081326275-60bc24fa144349f6bc2cba5c8f6cafa8。
  // 原本的初次概況及兩次 comparisonOnly 仍是三次；其後另外完成剩下 106 個代碼的兩批來源核對。
  assert.deepEqual(f.pending.slice(0, 3).map(request => request.payload.comparisonOnly === true), [false, true, true]);
  assert.equal(f.pending.filter(request => request.payload.comparisonOnly === true).length, 2);
  assert.equal(f.pending.length, 4);
  assert.equal(f.pending[3].payload.checkSourceValues, true);
  assert.notEqual(f.pending[3].payload.comparisonOnly, true);
  assert.deepEqual(copy(f.pending[3].payload.comparisonValues), codes.slice(100, 200));
  f.pending[3].resolve({ missingComparisonValues: [], sourceValueCheckStatus: 'checked' }); await settle(); f.render();
  assert.equal(f.pending.length, 5);
  assert.equal(f.pending[4].payload.checkSourceValues, true);
  assert.notEqual(f.pending[4].payload.comparisonOnly, true);
  assert.deepEqual(copy(f.pending[4].payload.comparisonValues), codes.slice(200).concat('A'));
  f.pending[4].resolve({ missingComparisonValues: [], sourceValueCheckStatus: 'checked' }); await settle(); f.render();
  assert.equal(f.pending.length, 5, 'all policy values have bounded source evidence; no repeated query');
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.manualValues), codes);
  assert.doesNotMatch(f.render().html, /正在確認來源值對應/);
});

test('pending equality never half-applies a reverse manual assignment', async () => {
  const f = fixture();
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M'], automaticValues: ['Z'] } });
  f.render().click('load-manual-profile'); f.pending[0].resolve(profile('A')); await settle(); f.render();
  const form = f.render(); form.input('[data-bind="automatic-code-add"]', ' A '); form.addCode('automatic');
  const waiting = f.render();
  assert.match(waiting.html.match(/<li class="value-assign">([\s\S]*?)<\/li>/)[1], /value="manual" disabled/);
  waiting.chooseManual('A', 'manual');
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy), { manualValues: ['M'], automaticValues: ['Z', ' A '] });
  f.pending[1].resolve({ comparisonGroups: [['A', ' A ']] }); await settle();
  f.render().chooseManual('A', 'manual');
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy), { manualValues: ['M', 'A'], automaticValues: ['Z'] });
});

test('pending posting comparison cannot leave an equivalent accepted value behind on deselection', async () => {
  const f = fixture();
  f.store.patchGlMappingOptions({ postingStatusPolicy: { acceptedValues: ['P'], includeBlank: false } });
  f.render().click('load-posting-profile'); f.pending[0].resolve(profile('Q')); await settle(); f.render();
  f.store.patchGlMappingOptions({ postingStatusPolicy: { acceptedValues: ['P', ' q '], includeBlank: false } });
  const waiting = f.render();
  assert.match(waiting.html, /data-posting-value="Q"[^>]*disabled/);
  waiting.togglePosting('Q', false);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.postingStatusPolicy.acceptedValues), ['P', ' q ']);
  f.pending[1].resolve({ comparisonGroups: [['Q', ' q ']] }); await settle();
  f.render().togglePosting('Q', false);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.postingStatusPolicy.acceptedValues), ['P']);
});

test('comparison response from a cancelled source selection cannot relabel a restored source', async () => {
  const f = fixture();
  f.render().click('load-manual-profile'); f.pending[0].resolve(profile('SOURCE')); await settle(); f.render();
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['1', 'NEW'], automaticValues: ['0'] } });
  f.render();
  const obsolete = f.pending[1];
  f.store.setMappingDraft('gl', 'manual', 'Other'); f.render();
  f.store.setMappingDraft('gl', 'manual', 'Manual'); f.render();
  obsolete.resolve({ comparisonGroups: [['SOURCE', '1', 'NEW']] }); await settle();
  const row = f.render().html.match(/<li class="value-assign">([\s\S]*?)<\/li>/)[1];
  assert.match(row, /value="none" checked/);
  assert.doesNotMatch(row, /value="manual" checked/);
});

for (const boundary of ['project', 'import']) {
  test('late code comparison cannot cross a replacement ' + boundary + ' with reused IDs', async () => {
    const f = fixture();
    f.render().click('load-manual-profile'); f.pending[0].resolve(profile('SOURCE')); await settle(); f.render();
    f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['1', 'NEW'], automaticValues: ['0'] } });
    f.render(); const obsolete = f.pending[1];
    if (boundary === 'project') f.store.setProject({ projectId: 'synthetic' });
    else f.store.setImportResult('gl', copy(f.store.getState().importState.gl));
    f.render().click('load-manual-profile');
    f.pending[2].resolve(profile('SOURCE')); await settle();
    obsolete.resolve({ comparisonGroups: [['SOURCE', '1', 'NEW']] }); await settle();
    const row = f.render().html.match(/<li class="value-assign">([\s\S]*?)<\/li>/)[1];
    assert.match(row, /value="none" checked/);
    assert.doesNotMatch(row, /value="manual" checked/);
  });
}

test('unsubmitted code text survives unrelated profile completion and stays bound to its source', async () => {
  const f = fixture();
  f.render().click('load-posting-profile');
  f.pending[0].resolve({ ...profile('POSTED'), truncated: true });
  await settle();
  const version = f.store.getState().contentVersion;
  const form = f.render();
  form.input('[data-bind="manual-code-add"]', 'MANUAL-DRAFT');
  form.input('[data-bind="automatic-code-add"]', 'AUTO-DRAFT');
  form.input('[data-bind="posting-value-add"]', 'POSTING-DRAFT');
  assert.equal(f.store.getState().contentVersion, version, 'typing must not rebuild the editor');
  f.render().click('load-manual-profile');
  f.pending[1].resolve(profile('SOURCE-VALUE'));
  await settle();
  const refreshed = f.render();
  assert.equal(refreshed.value('[data-bind="manual-code-add"]'), 'MANUAL-DRAFT');
  assert.equal(refreshed.value('[data-bind="automatic-code-add"]'), 'AUTO-DRAFT');
  assert.equal(refreshed.value('[data-bind="posting-value-add"]'), 'POSTING-DRAFT');
  assert.match(refreshed.html, /data-focus-key="manual-code-add-/);
  assert.match(refreshed.html, /data-focus-key="automatic-code-add-/);
  assert.match(refreshed.html, /data-focus-key="posting-value-add-/);
  f.store.setMappingDraft('gl', 'manual', 'Other');
  assert.equal(f.render().value('[data-bind="manual-code-add"]'), '');
  assert.equal(f.render().value('[data-bind="posting-value-add"]'), 'POSTING-DRAFT', 'unrelated source draft stays');
  f.store.setMappingDraft('gl', 'manual', 'Manual');
  assert.equal(f.render().value('[data-bind="manual-code-add"]'), '', 'detached source draft must not return');
});

for (const transition of ['import', 'project']) {
  test('unsubmitted code text cannot cross ' + transition + ' boundary with reused identifiers', () => {
    const f = fixture();
    f.render().input('[data-bind="manual-code-add"]', 'OLD-CODE');
    if (transition === 'import') f.store.setImportResult('gl', copy(f.store.getState().importState.gl));
    else f.store.setProject({ projectId: 'synthetic' });
    assert.equal(f.render().value('[data-bind="manual-code-add"]'), '');
    assert.doesNotMatch(f.render().html, /OLD-CODE/);
  });
}

for (const method of ['set', 'assign', 'replace']) {
  test('manual policy changes only with its source through ' + method + ' and cancel restores every choice', () => {
    const f = fixture();
    const policy = { manualValues: ['M'], automaticValues: [], unlistedValueKind: 'automatic', blankValueKind: 'unclassified' };
    const posting = { acceptedValues: ['P'], includeBlank: true };
    f.store.patchGlMappingOptions({ manualAutoPolicy: policy, postingStatusPolicy: posting });
    const gl = f.store.getState().mapping.gl;
    f.store.setMappingCommitted('gl', { mapping: gl.draft, mode: gl.amountMode, options: gl.options });
    function change(column) {
      if (method === 'set') f.store.setMappingDraft('gl', 'manual', column);
      if (method === 'assign') f.store.assignColumnToField('gl', column || 'Other', column ? 'manual' : '', ['dcDebitCode']);
      if (method === 'replace') f.store.replaceMappingDraft('gl', { postingStatus: 'Posting', ...(column ? { manual: column } : {}) });
    }
    change('Manual');
    assert.deepEqual(copy(gl.options.manualAutoPolicy), policy);
    change('Other');
    assert.deepEqual(copy(gl.options.manualAutoPolicy), { manualValues: ['1'], automaticValues: ['0'] });
    assert.deepEqual(copy(gl.options.postingStatusPolicy), posting);
    f.store.patchGlMappingOptions({ manualAutoPolicy: policy });
    change('');
    assert.deepEqual(copy(gl.options.manualAutoPolicy), { manualValues: ['1'], automaticValues: ['0'] });
    assert.deepEqual(copy(gl.committed.options.manualAutoPolicy), policy, 'saved source interpretation remains immutable');
    f.store.restoreCommittedMapping('gl');
    assert.equal(gl.draft.manual, 'Manual');
    assert.deepEqual(copy(gl.options.manualAutoPolicy), policy);
    assert.deepEqual(copy(gl.options.postingStatusPolicy), posting);
  });
}

for (const transition of ['import', 'project']) {
  test('late mapping success cannot publish into replacement ' + transition, async () => {
    const f = fixture();
    const mapping = { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', debitAmount: 'Debit', creditAmount: 'Credit' };
    f.store.setImportResult('gl', { batchId: 'ready', columns: Object.values(mapping), rowCount: 1 });
    f.store.replaceMappingDraft('gl', mapping);
    let complete;
    f.window.JetApi.mappingCommitGl = () => new Promise(resolve => { complete = resolve; });
    f.render().click('commit-gl');
    assert.equal(f.store.getState().busyLabel, '確認 GL 欄位配對');
    if (transition === 'import') f.store.setImportResult('gl', copy(f.store.getState().importState.gl));
    else f.store.setProject({ projectId: 'synthetic' });
    const replacementOptions = copy(f.store.getState().mapping.gl.options);
    complete({ batchId: 'ready', projectedRowCount: 1, approvalDateMode: 'unmapped',
      manualAutoPolicy: { manualValues: ['OLD'], automaticValues: ['0'] }, rdeFields: [] });
    await settle();
    assert.equal(f.store.getState().mapping.gl.committed, null);
    assert.deepEqual(copy(f.store.getState().mapping.gl.options), replacementOptions);
    assert.ok(!f.store.getState().messages.some(message => message.text.includes('已確認欄位配對')));
  });
}

test('late mapping failure does not accuse the replacement project', async () => {
  const f = fixture();
  const mapping = { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', debitAmount: 'Debit', creditAmount: 'Credit' };
  f.store.setImportResult('gl', { batchId: 'ready', columns: Object.values(mapping), rowCount: 1 });
  f.store.replaceMappingDraft('gl', mapping);
  let fail;
  f.window.JetApi.mappingCommitGl = () => new Promise((resolve, reject) => { fail = reject; });
  f.render().click('commit-gl');
  f.store.setProject({ projectId: 'synthetic' });
  fail(new Error('OLD-SOURCE-ERROR'));
  await settle();
  assert.doesNotMatch(f.render().html, /OLD-SOURCE-ERROR/);
  assert.ok(!f.store.getState().messages.some(message => message.text.includes('OLD-SOURCE-ERROR')));
});

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

// 2026-10-03 O1、O2：同一種問題只說一次，依值列列號；能對到設定位置的組附「前往設定」。
test('grouped projection failure shows the lead once and one item per problem with a jump to its setting', async () => {
  const f = fixture();
  const mapping = { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', debitAmount: 'Debit', creditAmount: 'Credit', manual: 'Manual' };
  f.store.setImportResult('gl', { batchId: 'ready', columns: Object.values(mapping).concat(['Gone']), rowCount: 1 });
  f.store.replaceMappingDraft('gl', mapping);
  const lead = '4 列無法轉換，系統沒有儲存這次配對結果。';
  const details = [
    { group: null, rule: null, sourceColumn: 'Manual', message: '欄位「Manual」有 2 列未歸類為人工或自動：「Adjust」在第 2、3 列。請回到欄位配對把這些值歸類。' },
    { group: null, rule: null, sourceColumn: 'Debit', message: '欄位「Debit」有 1 列不是有效金額：「<x>」在第 4 列。' },
    { group: null, rule: null, sourceColumn: 'Gone', message: '欄位「Gone」有 1 列無法解析為日期：第 5 列。' }
  ];
  f.window.JetApi.mappingCommitGl = () => Promise.reject(Object.assign(
    new Error(lead + details.map(detail => detail.message).join('')), { code: 'projection_failed', details }));
  f.render().click('commit-gl');
  await settle();
  const notice = f.render().html.split('data-bind="mapping-commit-error-gl"')[1].split('</div>')[0];
  assert.equal(notice.split(lead).length - 1, 1, 'the lead sentence appears once');
  assert.equal((notice.match(/mapping-error-groups__item/g) || []).length, 3);
  assert.equal((notice.match(/請回到欄位配對把這些值歸類/g) || []).length, 1, 'instruction is not repeated per row');
  assert.ok(notice.includes('data-focus-mapping-option="manual-mode"'), 'manual/auto problems jump to the decision setting');
  assert.ok(notice.includes('data-focus-mapping-field="debitAmount"'), 'core fields jump to their source selector');
  assert.equal((notice.match(/>前往設定</g) || []).length, 2, 'a column without a setting on screen gets no button');
  assert.ok(notice.includes('&lt;x&gt;') && !notice.includes('<x>'), 'values are escaped');
  const logged = f.store.getState().messages.find(message => message.text.includes('4 列無法轉換'));
  assert.ok(logged && !logged.text.includes('。，'), 'message panel does not join a full stop and a comma');
});

// 2026-10-03 O3：重新匯入後草稿保留上次確認的配對，新資料沒有的來源欄拿掉並逐一寫出。
test('reimport keeps the current draft, drops missing columns and names them', () => {
  const f = fixture();
  const mapping = { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', debitAmount: 'Debit', creditAmount: 'Credit', manual: 'Manual' };
  f.store.setImportResult('gl', { batchId: 'b1', columns: Object.values(mapping).concat(['Dept']), rowCount: 1 });
  f.store.replaceMappingDraft('gl', mapping);
  f.store.patchGlMappingOptions({
    manualAutoPolicy: { manualValues: ['M'], automaticValues: ['A'] },
    rdeFields: [{ fieldId: 'rde.keep', sourceColumn: 'Dept', label: '部門', valueType: 'text' }]
  });
  const before = f.store.getState().mapping.gl;
  f.store.setMappingCommitted('gl', { mapping: before.draft, mode: before.amountMode, options: before.options });
  f.store.setImportResult('gl', { batchId: 'b2', columns: ['Doc', 'Account', 'Name', 'Memo', 'Debit', 'Credit', 'Dept'], rowCount: 1 });
  const gl = f.store.getState().mapping.gl;
  assert.equal(gl.committed, null);
  assert.equal(gl.invalidatedByImport, true);
  assert.deepEqual(copy(gl.draft), { docNum: 'Doc', accNum: 'Account', accName: 'Name', description: 'Memo', debitAmount: 'Debit', creditAmount: 'Credit' });
  assert.deepEqual(copy(gl.options.manualAutoPolicy), { manualValues: ['1'], automaticValues: ['0'] }, 'codes of a dropped source are cleared');
  assert.deepEqual(copy(gl.options.rdeFields), [{ fieldId: 'rde.keep', sourceColumn: 'Dept', label: '部門', valueType: 'text' }]);
  const html = f.render().html;
  // 第 6 批 L26：同次開啟保留目前草稿，只有重開 previousMapping 才是上次確認配對。
  // 首次失敗：20261004-081326275-60bc24fa144349f6bc2cba5c8f6cafa8；其餘資料與缺欄斷言保留。
  assert.equal(gl.reimportDraftOrigin, 'current');
  assert.ok(html.includes('來源資料已變更，已保留目前草稿；請檢查後按「確認配對」。'));
  assert.ok(html.includes('新資料沒有下列來源欄，請重新選擇：總帳入帳日（原為「Date」）、人工/自動分錄（原為「Manual」）。'));
  f.store.setMappingCommitted('gl', { mapping: gl.draft, mode: gl.amountMode, options: gl.options });
  assert.deepEqual(copy(f.store.getState().mapping.gl.droppedFields), [], 'a confirmed mapping clears the notice');
});

test('reopening after reimport restores the last confirmed mapping as an unconfirmed draft', () => {
  const f = fixture();
  f.window.JetUi.applyLoadedProject({
    project: { projectId: 'synthetic', currentStep: 2 },
    importState: {
      gl: { batchId: 'b2', columns: ['Doc', 'Date', 'Account', 'Name', 'Memo', 'Debit', 'Credit'], rowCount: 1 },
      tb: { batchId: 't2', columns: ['Account', 'Name', 'Change'], rowCount: 1 }
    },
    mapping: { gl: null, tb: null },
    previousMapping: {
      gl: {
        mapping: { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', debitAmount: 'Debit', creditAmount: 'Credit', manual: 'Manual' },
        amountMode: 'dual', approvalDateMode: 'unmapped', postingStatusPolicy: null,
        manualAutoPolicy: { manualValues: ['M'], automaticValues: ['A'] },
        rdeFields: [{ fieldId: 'rde.gone', sourceColumn: 'Dept', label: '部門', valueType: 'text' }]
      },
      tb: { mapping: { accNum: 'Account', accName: 'Name', amount: 'Change' }, changeMode: 'direct' }
    }
  });
  const gl = f.store.getState().mapping.gl;
  assert.equal(gl.committed, null, 'the previous mapping is never treated as confirmed');
  assert.equal(gl.invalidatedByImport, true);
  assert.equal(gl.draft.postDate, 'Date');
  assert.equal(gl.draft.manual, undefined);
  assert.deepEqual(copy(gl.options.rdeFields), []);
  const tb = f.store.getState().mapping.tb;
  assert.equal(tb.committed, null);
  assert.equal(tb.invalidatedByImport, true);
  assert.equal(tb.changeMode, 'direct');
  assert.deepEqual(copy(tb.draft), { accNum: 'Account', accName: 'Name', amount: 'Change' });
  const html = f.render().html;
  assert.ok(html.includes('人工/自動分錄（原為「Manual」）、攸關資料元素「部門」（原為「Dept」）'));
});

test('without a previous mapping the reopened draft stays empty', () => {
  const f = fixture();
  f.window.JetUi.applyLoadedProject({
    project: { projectId: 'synthetic', currentStep: 2 },
    importState: { gl: { batchId: 'b2', columns: ['Doc'], rowCount: 1 } },
    mapping: { gl: null, tb: null },
    previousMapping: { gl: null, tb: null }
  });
  const gl = f.store.getState().mapping.gl;
  assert.deepEqual(copy(gl.draft), {});
  assert.equal(gl.invalidatedByImport, false);
  assert.deepEqual(copy(gl.droppedFields), []);
});

test('classic mapping lists only active amount fields in all eight modes', () => {
  const f = fixture();
  f.store.setImportResult('tb', { batchId: 'tb', columns: ['Balance'], rowCount: 1 });
  const cases = {
    gl: { dual: ['debitAmount', 'creditAmount'], signed: ['amount'], side: ['amount', 'dcField', 'dcDebitCode'], flag: ['amount', 'dcField', 'dcDebitCode'] },
    tb: { direct: ['amount'], debitCredit: ['debitAmt', 'creditAmt'], openClose: ['openingBalance', 'closingBalance'], openCloseBySide: ['openingDebit', 'openingCredit', 'closingDebit', 'closingCredit'] }
  };
  for (const [kind, modes] of Object.entries(cases)) for (const [mode, expected] of Object.entries(modes)) {
    f.store.setMappingMode(kind, mode);
    const section = f.render().html.split('data-bind="mapping-' + kind + '"')[1].split('</section>')[0];
    const fields = Array.from(section.matchAll(/data-mapping-key="([^"]+)"/g), match => match[1]);
    const amountKeys = [...new Set(Object.values(modes).flat())];
    assert.deepEqual(fields.filter(key => amountKeys.includes(key)).sort(), [...expected].sort(), kind + ' ' + mode);
  }
});

test('grid displays shared source uses without clearing either mapping', () => {
  const f = fixture();
  f.store.setMappingDraft('gl', 'createBy', 'Other');
  f.store.setMappingDraft('gl', 'approveBy', 'Other');
  f.store.setMappingUiMode('grid');
  const html = f.render().html;
  assert.match(html, /傳票建立人員、傳票核准人員/);
  assert.match(html, /共用此來源欄/);
  assert.equal(f.store.getState().mapping.gl.draft.createBy, 'Other');
  assert.equal(f.store.getState().mapping.gl.draft.approveBy, 'Other');
});

// Execute the production content-rendering functions with a small DOM model. The model
// deliberately resets caret position and scroll when replacing nodes, as browsers do.
function contentFixture(type = 'text', stepId = 'mapping') {
  const source = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
  const begin = source.indexOf('  function captureContentFocusSelector(');
  const end = source.indexOf('  /* ---- 訊息區', begin);
  assert.ok(begin >= 0 && end > begin);
  const listeners = new Map(), deferred = [];
  const document = { activeElement: null };
  const state = { project: stepId === 'create' ? null : {}, currentStepIndex: stepId === 'create' ? 0 : 2, view: 'workflow', contentVersion: 1, stepFlowCollapsed: false };
  const current = { id: stepId };
  const operator = { outerHTML: '' };
  let replacements = 0, selectionCalls = 0;
  function input() {
    return { type, value: 'abcdef', isConnected: true,
      selectionStart: type === 'text' ? 1 : null, selectionEnd: type === 'text' ? 4 : null, selectionDirection: 'backward',
      getAttribute: key => key === 'data-focus-key' ? 'synthetic-input' : null,
      matches: () => false,
      setSelectionRange(start, end, direction) {
        assert.equal(type, 'text', 'non-text inputs must not use the selection API');
        selectionCalls++; this.selectionStart = start; this.selectionEnd = end; this.selectionDirection = direction;
      }
    };
  }
  let activeInput = input();
  const container = {
    scrollTop: 140, scrollLeft: 20,
    contains: target => target === activeInput,
    addEventListener: (event, callback) => listeners.set(event, callback),
    querySelector: selector => selector === '[data-bind="create-operator"]' ? operator : selector.startsWith('[data-focus-key=') ? activeInput : {},
    querySelectorAll: () => [],
    set innerHTML(value) {
      replacements++; activeInput.isConnected = false; activeInput = input();
      activeInput.selectionStart = activeInput.selectionEnd = type === 'text' ? 6 : null;
      activeInput.selectionDirection = 'none'; this.scrollTop = 0; this.scrollLeft = 0;
    }
  };
  const context = vm.createContext({
    document, global: { CSS: { escape: value => value }, setTimeout: callback => deferred.push(callback) },
    Store: { getState: () => state, STEPS: [current] }, Ui: { $: () => container, renderStep() {} },
    contentComposition: null, lastContentKey: null, lastStepScrollKey: 'workflow|' + stepId,
    stepSectionHtml: () => '', bindStepFlow() {}, focusCurrentStepHeading() {},
    focusElement(element) { document.activeElement = element; return true; }
  });
  vm.runInContext(source.slice(begin, end), context, { filename: 'app.js content renderer' });
  context.render = () => context.renderContent(state, current);
  document.activeElement = activeInput;
  return { state, container, document, context, operator, render: context.render, get replacements() { return replacements; },
    get selectionCalls() { return selectionCalls; },
    emit(event) { listeners.get(event)?.({ target: activeInput }); },
    flush() { while (deferred.length) deferred.shift()(); }
  };
}

test('content refresh preserves text selection direction and outer scrolling', () => {
  const f = contentFixture();
  f.render();
  assert.equal(f.document.activeElement.selectionStart, 1);
  assert.equal(f.document.activeElement.selectionEnd, 4);
  assert.equal(f.document.activeElement.selectionDirection, 'backward');
  assert.equal(f.selectionCalls, 1);
  assert.equal(f.container.scrollTop, 140);
  assert.equal(f.container.scrollLeft, 20);
});

test('content refresh never calls text selection APIs for date inputs', () => {
  const f = contentFixture('date');
  f.render();
  assert.equal(f.replacements, 1);
  assert.equal(f.selectionCalls, 0);
});

test('content refresh waits for IME completion without losing a pending update', () => {
  const f = contentFixture();
  f.render();
  f.emit('compositionstart');
  f.state.contentVersion++;
  f.render();
  assert.equal(f.replacements, 1, 'background results must not replace an active IME input');
  f.emit('compositionend');
  assert.equal(f.replacements, 1, 'the final input event must run before rebuilding');
  f.flush();
  assert.equal(f.replacements, 2, 'queued state is rendered after composition ends');
});

test('IME state from an old project cannot delay a newly loaded project', () => {
  const f = contentFixture();
  f.render();
  f.emit('compositionstart');
  f.state.project = {};
  f.state.contentVersion++;
  f.render();
  assert.equal(f.replacements, 2);
});

test('a lost IME focus without compositionend cannot freeze later content updates', () => {
  const f = contentFixture();
  f.render();
  f.emit('compositionstart');
  f.document.activeElement = null;
  f.state.contentVersion++;
  f.render();
  assert.equal(f.replacements, 2);
});

// 2026-10-02 W6：第三步的下拉、借方代碼、逐值選項與兩個「加入」按鈕都要有可分辨的名稱。
test('mapping controls carry names from the field catalog and source values in both views', async () => {
  const f = fixture();
  f.store.setImportResult('tb', { batchId: 'tb-names', columns: ['Account', 'Value'], rowCount: 2 });
  f.store.setMappingMode('gl', 'flag');
  const glLabel = key => f.window.JetUi.GL_FIELDS.find(field => field.key === key).label;
  const tbLabel = key => f.window.JetUi.TB_FIELDS.find(field => field.key === key).label;
  let html = f.render().html;
  assert.ok(html.includes('aria-label="' + glLabel('postDate') + '的來源欄"'));
  assert.ok(html.includes('aria-label="總帳入帳日的來源欄"'));
  assert.ok(html.includes('<input class="form__input mapping-table__input" type="text" data-mapping-key="dcDebitCode" aria-label="借方代碼"'));
  assert.ok(html.includes('aria-label="' + tbLabel('accNum') + '的來源欄"'), 'TB uses the same naming');
  assert.equal((html.match(/<select class="mapping-table__select"(?![^>]*aria-label=)/g) || []).length, 0, 'every classic source select is named');
  assert.ok(html.includes('>預覽</button>'));
  assert.ok(!html.includes('預覽來源資料'));
  assert.ok(html.includes('data-code-group="manual" aria-label="加入人工代碼"'));
  assert.ok(html.includes('data-code-group="automatic" aria-label="加入自動代碼"'));

  f.store.setMappingUiMode('grid');
  html = f.render().html;
  assert.match(html, /<input class="form__input map-literal__input" type="text" id="mapping-gl-dcDebitCode-literal" data-mapping-key="dcDebitCode" aria-label="借方代碼" aria-describedby="mapping-gl-dcDebitCode-literal-hint"/);
  assert.ok(html.includes('id="mapping-gl-dcDebitCode-literal-hint">輸入代表借方的代碼，例如 D 或 1</span>'));
  assert.ok(!html.includes('字面值（不是欄位名稱）'));
  assert.ok(html.includes('aria-label="來源欄「Manual」對應的分錄測試欄位"'));
  assert.equal((html.match(/<select class="map-grid__select"(?![^>]*aria-label=)/g) || []).length, 0, 'every grid select is named');

  f.store.setMappingUiMode('classic');
  f.render().click('load-manual-profile');
  f.pending[0].resolve({ blankCount: 0, distinctCount: 2, truncated: false, values: [{ value: '0', count: 2 }, { value: 'M<1>', count: 1 }] });
  await settle();
  const rows = [...f.render().html.matchAll(/<li class="value-assign">([\s\S]*?)<\/li>/g)].map(match => match[1]);
  assert.equal(rows.length, 2);
  assert.match(rows[0], /<span class="value-assign__modes" role="radiogroup" aria-label="來源值「0」">/);
  assert.match(rows[1], /<span class="value-assign__modes" role="radiogroup" aria-label="來源值「M&lt;1&gt;」">/);
  // 單選鈕本身的 name、value 與資料屬性維持原樣。
  assert.match(rows[0], /name="manual-code-0" value="manual"[^>]* data-manual-assign="0"/);
  f.store.patchGlMappingOptions({ manualAutoPolicy: { ...f.store.getState().mapping.gl.options.manualAutoPolicy, unlistedValueKind: 'automatic', automaticValues: [] } });
  assert.match(f.render().html, /<span class="value-assign__modes" role="group" aria-label="來源值「0」">/);
});

function readyBatch6Fixture() {
  const f = fixture();
  const mapping = { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo',
    debitAmount: 'Debit', creditAmount: 'Credit', manual: 'Manual' };
  f.store.setImportResult('gl', { batchId: 'batch6', columns: Object.values(mapping).concat(['Other', 'Department']), rowCount: 10 });
  f.store.replaceMappingDraft('gl', mapping);
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M'], automaticValues: ['A'],
    blankValueKind: 'reject', unlistedValueKind: 'reject' } });
  return { ...f, mapping };
}

test('batch6 source-existence warnings use full-source evidence rather than the truncated display', async () => {
  const f = readyBatch6Fixture();
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['TYPO', 'TAIL'], automaticValues: [], unlistedValueKind: 'automatic' } });
  f.render().click('load-manual-profile');
  assert.equal(f.pending[0].payload.checkSourceValues, true);
  f.pending[0].resolve({ blankCount: 0, distinctCount: 80, truncated: true, values: [{ value: 'HEAD', count: 10 }],
    comparisonGroups: [['HEAD'], ['TYPO'], ['TAIL']], missingComparisonValues: ['TYPO'] }); await settle();
  const html = f.render().html;
  const chip = code => html.match(new RegExp('data-source-code="' + code + '"[^>]*>[\\s\\S]*?</span>'))?.[0] || '';
  assert.match(chip('TYPO'), /來源裡沒有這個值/);
  assert.doesNotMatch(chip('TAIL'), /來源裡沒有這個值/, 'not in the first 50 does not mean absent');
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.manualValues), ['TYPO', 'TAIL']);
  const warning = html.match(/data-bind="mapping-preflight-gl"[\s\S]*?<\/div>/)?.[0] || '';
  assert.match(warning, /TYPO/); assert.match(warning, /來源裡沒有這個值/);
  assert.ok(html.includes('data-action="commit-gl">'), 'warnings do not add a frontend gate');
});

test('batch6 unsupported-provider source checking remains explicitly unknown and does not block confirmation', async () => {
  const f = readyBatch6Fixture();
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['TAIL'], automaticValues: [], unlistedValueKind: 'automatic' } });
  f.render().click('load-manual-profile');
  f.pending[0].resolve({ blankCount: 0, distinctCount: 80, truncated: true, values: [{ value: 'HEAD', count: 10 }],
    comparisonGroups: [['HEAD'], ['TAIL']], missingComparisonValues: null, sourceValueCheckStatus: 'unsupportedProvider' }); await settle();
  const html = f.render().html;
  assert.match(html, /尚未核對/);
  assert.doesNotMatch(html, /來源裡沒有這個值/);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.manualValues), ['TAIL']);
  assert.ok(html.includes('data-action="commit-gl">'));
});

test('batch6 preflight names conflicts outside the display plus known blank and unclassified values without blocking', async () => {
  const f = readyBatch6Fixture();
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['Ghost'], automaticValues: ['ghost'],
    blankValueKind: 'reject', unlistedValueKind: 'reject' } });
  f.render().click('load-manual-profile');
  f.pending[0].resolve({ blankCount: 3, distinctCount: 90, truncated: true, values: [{ value: 'Unassigned', count: 7 }],
    comparisonGroups: [['Ghost', 'ghost'], ['Unassigned']], missingComparisonValues: ['Ghost', 'ghost'] }); await settle();
  const html = f.render().html;
  const warning = html.match(/data-bind="mapping-preflight-gl"[\s\S]*?<\/div>/)?.[0] || '';
  assert.match(warning, /Ghost/); assert.match(warning, /人工與自動.*重複/);
  assert.match(warning, /空白.*3.*來源空白時/);
  assert.match(warning, /Unassigned.*未歸類|未歸類.*Unassigned/);
  assert.match(html, /只列一側/);
  assert.ok(html.includes('data-action="commit-gl">'));
});

test('batch6 blank-reason error jumps to the blank policy instead of the general manual mode', async () => {
  const f = readyBatch6Fixture();
  f.window.JetApi.mappingCommitGl = () => Promise.reject({ message: 'Synthetic grouped failure', details: [
    { sourceColumn: 'Manual', reasonCode: 'manual_blank', message: 'Synthetic blank group' },
    { sourceColumn: 'Manual', reasonCode: 'manual_unlisted', message: 'Synthetic unlisted group' }
  ] });
  f.render().click('commit-gl'); await settle();
  const html = f.render().html;
  assert.match(html, /Synthetic blank group[\s\S]*?data-focus-mapping-option="manual-blank"/);
  assert.match(html, /Synthetic unlisted group[\s\S]*?data-focus-mapping-option="manual-mode"/);
});

test('batch6 editing one setting retains grouped failures and the other groups navigation until reconfirmed', async () => {
  const f = readyBatch6Fixture();
  f.window.JetApi.mappingCommitGl = () => Promise.reject({ message: 'Synthetic failure', details: [
    { sourceColumn: 'Manual', reasonCode: 'manual_blank', message: 'Synthetic blank group' },
    { sourceColumn: 'Debit', reasonCode: 'money_invalid', message: 'Synthetic amount group' }
  ] });
  f.render().click('commit-gl'); await settle();
  f.render().change('[data-manual-blank]', 'manual');
  const html = f.render().html;
  assert.match(html, /設定已修改，請重新確認/);
  assert.match(html, /Synthetic blank group/); assert.match(html, /Synthetic amount group/);
  assert.match(html, /data-focus-mapping-field="debitAmount"/);
  f.window.JetApi.mappingCommitGl = async () => ({ projectedRowCount: 10 });
  f.render().click('commit-gl'); await settle();
  assert.doesNotMatch(f.render().html, /Synthetic blank group|Synthetic amount group|設定已修改，請重新確認/);
});

test('batch6 RDE selection and type changes preserve actual marked inner scrolling', () => {
  const f = readyBatch6Fixture();
  const source = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
  const begin = source.indexOf('  function captureContentScrollPositions(');
  const end = source.indexOf('  function bindContentComposition(', begin);
  const context = vm.createContext({ global: { CSS: { escape: value => value } } });
  vm.runInContext(source.slice(begin, end), context);
  function container(html, scrollTop) {
    const key = html.match(/<ul class="rde-field-list"[^>]*data-preserve-scroll="([^"]+)"/)?.[1];
    const element = { scrollTop, scrollLeft: 0, getAttribute: () => key };
    return { element, querySelectorAll: () => key ? [element] : [],
      querySelector: selector => key && selector === '[data-preserve-scroll="' + key + '"]' ? element : null };
  }
  let before = container(f.render().html, 220);
  const firstPosition = context.captureContentScrollPositions(before);
  f.render().toggle('[data-rde-column="Other"]', true);
  let after = container(f.render().html, 0); context.restoreContentScrollPositions(after, firstPosition);
  assert.equal(after.element.scrollTop, 220, 'selection must retain the RDE list position');
  const secondPosition = context.captureContentScrollPositions(after);
  f.render().change('[data-rde-type="Other"]', 'date');
  after = container(f.render().html, 0); context.restoreContentScrollPositions(after, secondPosition);
  assert.equal(after.element.scrollTop, 220, 'changing a field type must retain the same list position');
});

test('batch6 selecting all RDE fields explicitly restores focus to a usable control', () => {
  const f = readyBatch6Fixture(), deferred = [];
  f.window.JetFocus = { defer(target) { deferred.push(target); } };
  f.render().click('select-all-rde'); const page = f.render();
  const first = page.find('[data-rde-column="Other"]');
  f.document.querySelector = () => first;
  assert.ok(deferred.length, 'the now-disabled select-all button cannot retain focus itself');
  const target = typeof deferred.at(-1) === 'function' ? deferred.at(-1)() : deferred.at(-1);
  assert.equal(target, first); assert.notEqual(target.disabled, true);
});

test('batch6 a committed mapping reserves its edit-banner position before the first setting change', () => {
  const f = readyBatch6Fixture();
  const before = f.store.getState().mapping.gl;
  f.store.setMappingCommitted('gl', { mapping: before.draft, mode: before.amountMode, options: before.options });
  f.render().click('remap-gl');
  assert.match(f.render().html, /data-bind="mapping-banner-gl"/, 'an empty banner still reserves layout space');
  f.render().toggle('[data-rde-column="Other"]', true);
  assert.equal((f.render().html.match(/data-bind="mapping-banner-gl"/g) || []).length, 1);
  assert.match(f.render().html, /配對已修改/);
});

test('batch6 reimport distinguishes the current in-session draft from a reloaded previous mapping', () => {
  const f = readyBatch6Fixture();
  const gl = f.store.getState().mapping.gl;
  f.store.setMappingCommitted('gl', { mapping: gl.draft, mode: gl.amountMode, options: gl.options });
  f.store.setMappingDraft('gl', 'description', 'Other');
  f.store.setImportResult('gl', { batchId: 'new-import', columns: Object.values(f.mapping).concat(['Other']), rowCount: 1 });
  assert.equal(f.store.getState().mapping.gl.draft.description, 'Other');
  assert.match(f.render().html, /已保留目前草稿/);
  f.store.restorePreviousMapping('gl', { mapping: f.mapping, mode: 'dual' }, Object.values(f.mapping));
  assert.equal(f.store.getState().mapping.gl.draft.description, 'Memo');
  assert.match(f.render().html, /已保留上次確認的配對/);
});

test('batch6 one-sided checkboxes update only the explicit list and keep stable focus and scroll keys', async () => {
  const f = readyBatch6Fixture();
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M'], automaticValues: [], unlistedValueKind: 'automatic' } });
  f.render().click('load-manual-profile');
  f.pending[0].resolve({ blankCount: 0, distinctCount: 2, truncated: false,
    values: [{ value: 'M', count: 4 }, { value: 'A', count: 6 }], missingComparisonValues: [] }); await settle();
  const before = f.render(); const key = before.find('[data-manual-include="A"]').getAttribute('data-focus-key');
  before.toggle('[data-manual-include="A"]', true);
  const after = f.render();
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.manualValues), ['M', 'A']);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.automaticValues), []);
  assert.equal(after.find('[data-manual-include="A"]').getAttribute('data-focus-key'), key);
  assert.match(after.html, /data-preserve-scroll="manual-values-Manual"/);
  after.toggle('[data-manual-include="A"]', false);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.manualValues), ['M']);
});

test('batch6 confirming after removing the manual column omits manual settings from the actual summary', async () => {
  const f = readyBatch6Fixture(); let payload;
  f.store.setMappingDraft('gl', 'manual', '');
  f.window.JetApi.mappingCommitGl = async request => { payload = request; return { projectedRowCount: 10 }; };
  f.render().click('commit-gl'); await settle();
  assert.equal(payload.mapping.manual, undefined);
  const summary = f.render().html.match(/data-bind="gl-committed-options"[\s\S]*?<\/ul>/)?.[0] || '';
  assert.match(summary, /核准日/);
  assert.doesNotMatch(summary, /人工|自動|空白：/);
});

async function batch6PendingPresenceFixture() {
  const f = readyBatch6Fixture();
  f.render().click('load-manual-profile');
  f.pending[0].resolve({ blankCount: 0, distinctCount: 3, truncated: false,
    values: ['M', 'A', 'TAIL'].map(value => ({ value, count: 1 })), missingComparisonValues: [] }); await settle();
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M', 'TAIL'], automaticValues: ['A'] } });
  f.render();
  assert.equal(f.pending.length, 2);
  assert.equal(f.pending[1].payload.checkSourceValues, true);
  assert.notEqual(f.pending[1].payload.comparisonOnly, true);
  return f;
}

test('batch6 a stale pending presence check cannot prevent a fresh check after the comparison scope changes', async () => {
  for (const change of ['posting-source', 'manual-away-and-back']) {
    const f = await batch6PendingPresenceFixture();
    if (change === 'posting-source') f.store.setMappingDraft('gl', 'postingStatus', 'Other');
    else {
      f.store.setMappingDraft('gl', 'manual', ''); f.render();
      f.store.setMappingDraft('gl', 'manual', 'Manual');
      f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M', 'TAIL'], automaticValues: ['A'] } });
    }
    f.render();
    assert.equal(f.pending.length, 3, change + ' requires a new current-scope check, not perpetual waiting');
    f.pending[2].resolve({ missingComparisonValues: [], sourceValueCheckStatus: 'checked' }); await settle();
    f.pending[1].resolve({ missingComparisonValues: ['TAIL'], sourceValueCheckStatus: 'checked' }); await settle();
    const html = f.render().html;
    assert.doesNotMatch(html, /來源裡沒有這個值|正在核對清單代碼/);
  }
});

test('batch6 source-presence failures retain codes and expose an exact retry without an automatic loop', async () => {
  const f = await batch6PendingPresenceFixture();
  f.pending[1].reject(new Error('Synthetic source check failure')); await settle();
  assert.match(f.render().html, /尚未核對/); f.render(); assert.equal(f.pending.length, 2);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.manualValues), ['M', 'TAIL']);
  f.render().retrySource('Manual');
  assert.deepEqual(copy(f.pending[2].payload), copy(f.pending[1].payload));
  f.pending[2].resolve({ missingComparisonValues: ['TAIL'], sourceValueCheckStatus: 'checked' }); await settle();
  const html = f.render().html;
  assert.match(html, /data-source-code="TAIL"[^>]*>[\s\S]*?來源裡沒有這個值/);
  assert.doesNotMatch(html, /尚未核對|正在核對清單代碼/);
  assert.equal(f.pending.length, 3);
});

test('batch6 a late full-source presence response cannot mark codes missing in a newly imported batch', async () => {
  const f = await batch6PendingPresenceFixture();
  f.store.setImportResult('gl', { batchId: 'replacement-batch', columns: Object.values(f.mapping), rowCount: 3 });
  f.store.replaceMappingDraft('gl', f.mapping);
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M', 'TAIL'], automaticValues: ['A'] } });
  f.render().click('load-manual-profile');
  f.pending[2].resolve({ blankCount: 0, distinctCount: 3, truncated: false,
    values: ['M', 'A', 'TAIL'].map(value => ({ value, count: 1 })), missingComparisonValues: [] }); await settle();
  f.pending[1].resolve({ missingComparisonValues: ['TAIL'], sourceValueCheckStatus: 'checked' }); await settle();
  assert.equal(f.store.getState().importState.gl.batchId, 'replacement-batch');
  assert.doesNotMatch(f.render().html, /來源裡沒有這個值|正在核對清單代碼/);
});

test('batch6 an old comparison-only request cannot block source checking after its pending code was removed', async () => {
  const f = readyBatch6Fixture();
  f.render().click('load-manual-profile');
  f.pending[0].resolve({ blankCount: 0, distinctCount: 3, truncated: false,
    values: ['M', 'A', 'TAIL'].map(value => ({ value, count: 1 })), missingComparisonValues: [] }); await settle();
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M', 'TAIL', 'PendingCode'], automaticValues: ['A'] } });
  f.render();
  assert.equal(f.pending.length, 2); assert.equal(f.pending[1].payload.comparisonOnly, true);
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M', 'TAIL'], automaticValues: ['A'] } });
  f.store.setMappingDraft('gl', 'postingStatus', 'Other');
  f.render();
  assert.equal(f.pending.length, 3, 'old-scope comparison must not prevent an independent current-scope presence query');
  assert.equal(f.pending[2].payload.checkSourceValues, true);
  assert.notEqual(f.pending[2].payload.comparisonOnly, true);
  assert.deepEqual(copy(f.pending[2].payload.comparisonValues), ['TAIL']);
  f.pending[2].resolve({ missingComparisonValues: [], sourceValueCheckStatus: 'checked' }); await settle();
  f.pending[1].resolve({ comparisonGroups: [['M'], ['A'], ['TAIL'], ['PendingCode']] }); await settle();
  const html = f.render().html;
  assert.doesNotMatch(html, /來源裡沒有這個值|正在核對清單代碼|正在確認來源值對應/);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.manualValues), ['M', 'TAIL']);
});

function fixedComparisonKeyResponse(request, keys) {
  const requested = request.payload.comparisonValues || [];
  const all = [...new Set(['0', '1'].concat(requested))];
  return { blankCount: 0, distinctCount: 2, truncated: false,
    values: [{ value: '0', count: 5 }, { value: '1', count: 5 }],
    // Each response deliberately has separate groups. Only opaque backend keys can join equivalent values seen in different requests.
    comparisonGroups: all.map(value => [value]),
    comparisonKeys: all.map(value => { assert.ok(keys.has(value), 'fixed oracle covers ' + value); return { value, key: keys.get(value) }; }),
    missingComparisonValues: requested.filter(value => value !== '0' && value !== '1'), sourceValueCheckStatus: 'checked' };
}

test('batch6 separately added missing manual and automatic codes share backend keys across distinct completed requests', async () => {
  const f = readyBatch6Fixture();
  const keys = new Map([['0', 'opaque-zero'], ['1', 'opaque-one'], ['B6-MISSING', 'opaque-person-27'], ['b6-missing', 'opaque-person-27']]);
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['1'], automaticValues: ['0'] } });
  f.render().click('load-manual-profile'); f.pending[0].resolve(fixedComparisonKeyResponse(f.pending[0], keys)); await settle();
  let page = f.render(); page.input('[data-bind="manual-code-add"]', 'B6-MISSING'); page.addCode('manual'); f.render();
  assert.equal(f.pending[1].payload.comparisonOnly, true);
  f.pending[1].resolve(fixedComparisonKeyResponse(f.pending[1], keys)); await settle(); f.render();
  assert.equal(f.pending[2].payload.checkSourceValues, true);
  f.pending[2].resolve(fixedComparisonKeyResponse(f.pending[2], keys)); await settle();
  assert.match(f.render().html, /data-source-code="B6-MISSING"[^>]*>[\s\S]*?來源裡沒有這個值/);
  page = f.render(); page.input('[data-bind="automatic-code-add"]', 'b6-missing'); page.addCode('automatic'); f.render();
  assert.deepEqual(copy(f.pending[3].payload.comparisonValues), ['0', '1', 'b6-missing']);
  f.pending[3].resolve(fixedComparisonKeyResponse(f.pending[3], keys)); await settle(); f.render();
  f.pending[4].resolve(fixedComparisonKeyResponse(f.pending[4], keys)); await settle();
  const html = f.render().html;
  const warning = html.match(/data-bind="mapping-preflight-gl"[\s\S]*?<\/div>/)?.[0] || '';
  assert.match(warning, /人工與自動.*重複.*B6-MISSING/);
  assert.match(html, /data-source-code="b6-missing"[^>]*>[\s\S]*?來源裡沒有這個值/);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.manualValues), ['1', 'B6-MISSING']);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.automaticValues), ['0', 'b6-missing']);
});

test('batch6 equivalent policy codes split across the first 100 and a later page retain one backend identity', async () => {
  const f = readyBatch6Fixture();
  const manual = ['CROSS-FIRST'].concat(Array.from({ length: 99 }, (_, index) => 'CODE-' + index));
  const keys = new Map([['0', 'opaque-zero'], ['1', 'opaque-one'], ['CROSS-FIRST', 'opaque-cross-41'], ['cross-first', 'opaque-cross-41'],
    ...manual.slice(1).map((value, index) => [value, 'opaque-filler-' + index])]);
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: manual, automaticValues: ['cross-first'] } });
  f.render().click('load-manual-profile');
  assert.equal(f.pending[0].payload.comparisonValues.length, 100);
  assert.ok(f.pending[0].payload.comparisonValues.includes('CROSS-FIRST'));
  assert.ok(!f.pending[0].payload.comparisonValues.includes('cross-first'));
  f.pending[0].resolve(fixedComparisonKeyResponse(f.pending[0], keys)); await settle(); f.render();
  assert.equal(f.pending[1].payload.comparisonOnly, true);
  assert.deepEqual(copy(f.pending[1].payload.comparisonValues), ['0', '1', 'cross-first']);
  f.pending[1].resolve(fixedComparisonKeyResponse(f.pending[1], keys)); await settle(); f.render();
  f.pending[2].resolve(fixedComparisonKeyResponse(f.pending[2], keys)); await settle();
  const warning = f.render().html.match(/data-bind="mapping-preflight-gl"[\s\S]*?<\/div>/)?.[0] || '';
  assert.match(warning, /人工與自動.*重複.*CROSS-FIRST/);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.manualValues), manual);
  assert.deepEqual(copy(f.store.getState().mapping.gl.options.manualAutoPolicy.automaticValues), ['cross-first']);
  assert.ok(f.pending.every(request => request.payload.comparisonValues.length <= 100));
});

test('batch6 opaque comparison keys do not apply JavaScript uppercase equivalence to missing Unicode codes', async () => {
  const f = readyBatch6Fixture();
  const keys = new Map([['0', 'opaque-zero'], ['1', 'opaque-one'], ['ß', 'opaque-distinct-6'], ['SS', 'opaque-distinct-9']]);
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['ß'], automaticValues: ['SS'] } });
  f.render().click('load-manual-profile'); f.pending[0].resolve(fixedComparisonKeyResponse(f.pending[0], keys)); await settle();
  const warning = f.render().html.match(/data-bind="mapping-preflight-gl"[\s\S]*?<\/div>/)?.[0] || '';
  assert.doesNotMatch(warning, /人工與自動.*重複/);
  assert.match(warning, /來源裡沒有這個值/);
});

test('batch6 a present null canonical key is known metadata and differs from the literal word null', async () => {
  const f = readyBatch6Fixture();
  const keys = new Map([['0', 'opaque-zero'], ['1', 'opaque-one'], ['\u3000', null], ['', null], ['null', 'opaque-literal-null']]);
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['\u3000'], automaticValues: ['null'] } });
  f.render().click('load-manual-profile'); f.pending[0].resolve(fixedComparisonKeyResponse(f.pending[0], keys)); await settle();
  assert.doesNotMatch(f.render().html, /人工與自動代碼重複/);
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['\u3000'], automaticValues: [''] } });
  f.render();
  assert.equal(f.pending[1].payload.comparisonOnly, true);
  assert.ok(f.pending[1].payload.comparisonValues.includes(''));
  f.pending[1].resolve(fixedComparisonKeyResponse(f.pending[1], keys)); await settle();
  const warning = f.render().html.match(/data-bind="mapping-preflight-gl"[\s\S]*?<\/div>/)?.[0] || '';
  assert.match(warning, /人工與自動代碼重複/);
  assert.doesNotMatch(f.render().html, /正在確認來源值對應/);
});

test('batch6 hand-entered policy overlaps are checked with metadata only before any source profile is read', async () => {
  const f = readyBatch6Fixture();
  const keys = new Map([['0', 'opaque-zero'], ['1', 'opaque-one'], ['B6-NO-PROFILE', 'opaque-same-73'], ['b6-no-profile', 'opaque-same-73']]);
  function metadata(request) {
    return { comparisonGroups: request.payload.comparisonValues.map(value => [value]),
      comparisonKeys: request.payload.comparisonValues.map(value => { assert.ok(keys.has(value)); return { value, key: keys.get(value) }; }) };
  }
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['1'], automaticValues: ['0'] } });
  let page = f.render(); page.input('[data-bind="manual-code-add"]', 'B6-NO-PROFILE'); page.addCode('manual');
  assert.equal(f.pending.length, 1, 'adding a code requests text metadata without requiring the source-reader button');
  assert.equal(f.pending[0].payload.comparisonOnly, true); assert.notEqual(f.pending[0].payload.checkSourceValues, true);
  f.pending[0].resolve(metadata(f.pending[0])); await settle();
  page = f.render(); page.input('[data-bind="automatic-code-add"]', 'b6-no-profile'); page.addCode('automatic'); f.render();
  assert.equal(f.pending[1].payload.comparisonOnly, true); assert.notEqual(f.pending[1].payload.checkSourceValues, true);
  f.pending[1].resolve(metadata(f.pending[1])); await settle();
  let html = f.render().html;
  assert.match(html, /人工與自動代碼重複.*B6-NO-PROFILE/);
  assert.match(html, /data-action="load-manual-profile"/);
  assert.match(html, /尚未核對/);
  assert.doesNotMatch(html, /NaN|共 0 種值|正在核對清單代碼|來源裡沒有這個值/);
  assert.equal(f.pending.length, 2, 'text comparison must not silently trigger a full-source existence scan');
  f.render().click('load-manual-profile');
  assert.equal(f.pending[2].payload.checkSourceValues, true); assert.notEqual(f.pending[2].payload.comparisonOnly, true);
  f.pending[2].resolve(fixedComparisonKeyResponse(f.pending[2], keys)); await settle();
  html = f.render().html;
  assert.match(html, /人工與自動代碼重複.*B6-NO-PROFILE/);
  assert.match(html, /來源裡沒有這個值/);
  assert.doesNotMatch(html, /NaN/);
});

test('batch7 late whoAmI updates only the create operator line and preserves the same draft input and focus', () => {
  const f = contentFixture('text', 'create');
  // Load the real create-step identity updater; DOM identity, input and caret remain observable.
  f.context.Ui.registerWorkflowReset = () => {};
  f.context.Ui.registerStep = () => {};
  f.context.Ui.esc = value => String(value);
  f.context.window = { JetStore: f.context.Store, JetUi: f.context.Ui };
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/create-step.js'), 'utf8'), f.context);
  f.render();
  const input = f.document.activeElement;
  input.value = 'B7 unsaved case'; input.selectionStart = 3; input.selectionEnd = 8;
  f.state.currentUser = { shortName: 'B7-auditor', userNumber: 7, principal: 'SYNTHETIC\\B7-auditor' };
  f.render();
  assert.equal(f.replacements, 1, 'identity-only notification must not rebuild the create form');
  assert.equal(f.document.activeElement, input, 'focus remains on the exact same live input');
  assert.equal(input.isConnected, true);
  assert.equal(input.value, 'B7 unsaved case');
  assert.equal(input.selectionStart, 3); assert.equal(input.selectionEnd, 8);
  // Batch 8 distinguishes the current Windows identity from the stored project creator.
  assert.match(f.operator.outerHTML, /目前帳號：<strong>B7-auditor<\/strong>/);
});

test('batch7 late whoAmI does not rebuild other workflow steps or overwrite the recorded case creator', () => {
  const f = contentFixture();
  f.render(); const input = f.document.activeElement; input.value = 'mapping draft';
  f.state.currentUser = { shortName: 'late-auditor', userNumber: 9 };
  f.render();
  assert.equal(f.replacements, 1);
  assert.equal(f.document.activeElement, input);
  assert.equal(input.value, 'mapping draft');
  const create = contentFixture('text', 'create');
  create.context.Ui.registerWorkflowReset = () => {};
  create.context.Ui.registerStep = () => {};
  create.context.Ui.esc = value => String(value);
  create.context.window = { JetStore: create.context.Store, JetUi: create.context.Ui };
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/create-step.js'), 'utf8'), create.context);
  create.state.project = { operatorId: 'original-creator' };
  create.operator.outerHTML = 'recorded original-creator';
  create.render();
  create.state.currentUser = { shortName: 'different-auditor' };
  create.render();
  assert.equal(create.replacements, 1);
  assert.equal(create.operator.outerHTML, 'recorded original-creator');
});

// 2026-10-05 最後獨立複審 V3、Q5「確認配對前也提醒」：只列一側時，清單代碼還沒和來源核對就要在確認前提醒，
// 而且不擋確認。讀取來源值後，改由既有的「來源裡沒有這個值」接手。
test('V3 single-side codes not yet checked against the source are named before confirming without blocking', async () => {
  const f = readyBatch6Fixture();
  const preflight = html => html.match(/data-bind="mapping-preflight-gl"[\s\S]*?<\/div>/)?.[0] || '';
  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['1'], automaticValues: [],
    unlistedValueKind: 'automatic', blankValueKind: 'automatic' } });
  let html = f.render().html;
  assert.match(preflight(html),
    /人工分錄代碼「1」還沒和來源核對。來源裡沒有這個代碼時，其他非空白的值都會算成自動分錄。可以先按「讀取來源值」確認。/);
  assert.ok(html.includes('data-action="commit-gl">'), 'the reminder does not add a frontend gate');

  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: [], automaticValues: ['0', 'N'],
    unlistedValueKind: 'manual', blankValueKind: 'manual' } });
  assert.match(preflight(f.render().html),
    /自動分錄代碼「0、N」還沒和來源核對。來源裡沒有這些代碼時，其他非空白的值都會算成人工分錄。可以先按「讀取來源值」確認。/);

  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['M'], automaticValues: ['A'],
    unlistedValueKind: 'reject', blankValueKind: 'reject' } });
  assert.doesNotMatch(f.render().html, /還沒和來源核對/, 'per-value mode classifies every value, so no single-side reminder');

  f.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['1'], automaticValues: [],
    unlistedValueKind: 'automatic', blankValueKind: 'automatic' } });
  f.render().click('load-manual-profile');
  const request = f.pending.find(item => item.payload.checkSourceValues);
  request.resolve({ blankCount: 0, distinctCount: 2, truncated: false,
    values: [{ value: 'M', count: 1 }, { value: 'A', count: 1 }], missingComparisonValues: ['1'] });
  await settle();
  html = f.render().html;
  assert.doesNotMatch(html, /還沒和來源核對/);
  assert.match(preflight(html), /「1」：來源裡沒有這個值/);

  // 獨立複審補充：讀取來源值失敗時，代碼同樣沒有核對，確認前仍要提醒。
  const failed = readyBatch6Fixture();
  failed.store.patchGlMappingOptions({ manualAutoPolicy: { manualValues: ['1'], automaticValues: [],
    unlistedValueKind: 'automatic', blankValueKind: 'automatic' } });
  failed.render().click('load-manual-profile');
  failed.pending.find(item => item.payload.checkSourceValues).reject(new Error('Synthetic read failure'));
  await settle();
  html = failed.render().html;
  assert.match(html, /讀取來源值失敗/);
  assert.match(preflight(html), /人工分錄代碼「1」還沒和來源核對。/);
  assert.ok(html.includes('data-action="commit-gl">'));
});

// 2026-10-05 最後獨立複審 V7：只改案件文字資料時，已讀取的來源值摘要保留，不重新查詢。
test('V7 loaded source values survive a text-only project metadata edit', async () => {
  const f = readyBatch6Fixture();
  f.render().click('load-manual-profile');
  f.pending.find(item => item.payload.checkSourceValues).resolve({ blankCount: 0, distinctCount: 2, truncated: false,
    values: [{ value: 'M', count: 1 }, { value: 'A', count: 1 }] });
  await settle();
  const requests = f.pending.length, none = { validation: false, prescreen: false, filter: false };
  f.store.updateProjectMetadata({ project: { ...f.store.getState().project, entityName: 'Edited only' },
    invalidatedResults: none, staleState: none, reportArtifacts: [] });
  const html = f.render().html;
  assert.match(html, /來源欄「Manual」共 2 種值/);
  assert.doesNotMatch(html, /data-action="load-manual-profile"/);
  assert.equal(f.pending.length, requests, 'no new source query');
});
