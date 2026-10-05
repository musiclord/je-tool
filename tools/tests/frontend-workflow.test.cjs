'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '../../src/JET/JET/wwwroot/js');
const settle = () => new Promise(resolve => setImmediate(resolve));

function scenarioLifecycleFixture() {
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const document = { querySelector: () => null, createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js', 'filter-legacy.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const source = fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8');
  vm.runInContext(source.replace("  Ui.registerStep('filter', render);",
    "  window.workflow = { completeScenarioSave, savedFeedbackText, filterCommitSnapshot, syncViewState, toWireDraft, addKctToDraft, removeKctFromActiveGroup, applyKctNaming, bind, view: () => viewState };"), context);
  const store = window.JetStore, api = window.workflow;
  store.setProject({ projectId: 'sequence-fixture' }); api.syncViewState(store.getState());
  const item = letter => window.JetUi.FILTER_KCT_CHECKLIST.find(x => x.letter === letter);
  function toggle(letter, remove = false) {
    const draft = store.getState().filter.draft;
    api[remove ? 'removeKctFromActiveGroup' : 'addKctToDraft'](draft, item(letter));
    api.applyKctNaming(draft); store.setFilterDraft(draft);
  }
  function bindRemoval(index, action = 'confirm-remove-scenario') {
    const pending = [], nodes = new Map(), actions = [];
    window.JetApi = Object.fromEntries(['filterCommit', 'filterPreview', 'queryFilterVoucherPage'].map(method =>
      [method, payload => new Promise((resolve, reject) => pending.push({method, payload, resolve, reject}))]));
    window.JetUi.run = (label, fn, options) => {
      const promise = Promise.resolve().then(fn).catch(error => { if (options?.onError) options.onError(error); });
      actions.push(promise); return promise;
    };
    function node(selector) {
      if (!nodes.has(selector)) nodes.set(selector, { dataset: { index: String(index), action }, value: '', hidden: false, innerHTML: '',
        handlers: {}, classList: { add() {}, remove() {}, toggle() {} },
        addEventListener(k, fn) { this.handlers[k] = fn; },
        getAttribute(k) { return k === 'data-index' ? String(index) : null; },
        setAttribute() {}, removeAttribute() {}, focus() {}, querySelector: node, querySelectorAll: () => [] });
      return nodes.get(selector);
    }
    const container = { querySelector: node, querySelectorAll: selector => selector.includes('[data-action="' + action + '"]') ? [node('remove')] : [] };
    api.bind(container);
    return { pending, actions, click() { node('remove').handlers.click(); }, clickSelector(selector) { node(selector).handlers.click(); } };
  }
  return { window, store, api, toggle, bindRemoval };
}

test('removing the saved scenario clears its linked editor and preview only after success', async () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const draft = f.store.getState().filter.draft, saved = f.api.toWireDraft(draft);
  const list = [saved]; f.store.setSavedScenarios(list);
  f.store.patchFilterDraftMeta({ __editingIndex: 0, __editingSavedRef: list });
  f.store.setFilterPreview({ count: 3 });
  const op = f.bindRemoval(0); op.click(); await settle();
  assert.equal(f.store.getState().filter.draft, draft, 'do not erase before the backend succeeds');
  op.pending[0].resolve({ scenarios: [], resultRef: null, savedCount: 0 });
  await Promise.all(op.actions); await settle();
  assert.equal(f.store.getState().filter.draft.groups.length, 0);
  assert.equal(f.store.getState().filter.draft.name, '');
  assert.equal(f.store.getState().filter.preview, null);
});

test('automatic KCT names follow the latest G H selection even after saving', () => {
  const f = scenarioLifecycleFixture();
  f.toggle('G');
  const draft = f.store.getState().filter.draft;
  const saved = f.api.toWireDraft(draft);
  f.api.completeScenarioSave(f.api.filterCommitSnapshot(f.store.getState()),
    { scenarios: [saved], resultRef: { revision: 'r1' }, savedCount: 1 }, 0, saved);
  f.toggle('H'); assert.equal(draft.name, 'G+H');
  f.toggle('G', true); assert.equal(draft.name, 'H');
  f.toggle('G'); assert.equal(draft.name, 'G+H');
  f.toggle('H', true); assert.equal(draft.name, 'G');
  f.toggle('G', true); assert.equal(draft.name, '');
});

test('late save does not overwrite a later name or rationale edit', () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const draft = f.store.getState().filter.draft, saved = f.api.toWireDraft(draft);
  const snapshot = f.api.filterCommitSnapshot(f.store.getState());
  f.store.patchFilterDraftMeta({ name: '手動新名稱', rationale: '後來補上的動機', __nameDirty: true, __rationaleDirty: true });
  f.api.completeScenarioSave(snapshot, { scenarios: [saved], resultRef: { revision: 'r1' }, savedCount: 1 }, 0, saved);
  assert.equal(draft.name, '手動新名稱');
  assert.equal(draft.rationale, '後來補上的動機');
  assert.equal(draft.__editingIndex, 0, 'the next save updates the just-saved record instead of appending');
});

test('retyping the same name during save still transfers naming ownership to the user', () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const snapshot = f.api.filterCommitSnapshot(f.store.getState()), saved = f.api.toWireDraft(snapshot.draft);
  f.store.patchFilterDraftMeta({ name: 'G', __nameDirty: true });
  f.api.completeScenarioSave(snapshot, { scenarios: [saved], resultRef: { revision: 'r1' }, savedCount: 1 }, 0, saved);
  f.toggle('H'); assert.equal(snapshot.draft.name, 'G');
});

test('saved feedback compares JSON content rather than backend property order', () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const snapshot = f.api.filterCommitSnapshot(f.store.getState()), authored = f.api.toWireDraft(snapshot.draft);
  const saved = JSON.parse(JSON.stringify(authored, (_, value) => value && typeof value === 'object' && !Array.isArray(value)
    ? Object.fromEntries(Object.keys(value).sort().map(key => [key, value[key]])) : value));
  f.api.completeScenarioSave(snapshot, { scenarios: [saved], resultRef: { revision: 'r1' }, savedCount: 1 }, 0, authored);
  // 2026-10-03 用語統一 W10：使用者裁定以「已儲存」為準（第一次失敗：收據 20261003-024908090-6beccb9968d64a549df8acb53f0c5ff6）。
  assert.match(f.api.savedFeedbackText(snapshot.draft), /已儲存/);
  assert.doesNotMatch(f.api.savedFeedbackText(snapshot.draft), /尚未儲存|保存/);
});

for (const removed of [0, 2]) test('removing another scenario rebases the linked editor: ' + removed, async () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const draft = f.store.getState().filter.draft, saved = f.api.toWireDraft(draft);
  const list = ['first', 'edited', 'last'].map(name => ({ ...saved, name }));
  f.store.setSavedScenarios(list); f.store.patchFilterDraftMeta({ __editingIndex: 1, __editingSavedRef: list });
  const preview = { count: 2 }; f.store.setFilterPreview(preview);
  const op = f.bindRemoval(removed); op.click(); await settle();
  const remaining = list.filter((_, i) => i !== removed);
  op.pending[0].resolve({ scenarios: remaining, resultRef: { revision: 'next' }, savedCount: 2 });
  await Promise.all(op.actions);
  assert.equal(f.store.getState().filter.draft, draft);
  assert.equal(draft.__editingIndex, removed === 0 ? 0 : 1);
  assert.equal(draft.__editingSavedRef, remaining);
  assert.equal(f.store.getState().filter.preview, preview);
});

test('deleting a saved scenario preserves an independent new draft', async () => {
  const f = scenarioLifecycleFixture(); f.toggle('H');
  const draft = f.store.getState().filter.draft;
  f.store.setSavedScenarios([{ ...f.api.toWireDraft(draft), name: 'saved' }]);
  const op = f.bindRemoval(0); op.click(); await settle();
  op.pending[0].resolve({ scenarios: [], savedCount: 0 }); await Promise.all(op.actions);
  assert.equal(f.store.getState().filter.draft, draft);
  assert.equal(draft.name, 'H');
  assert.equal(draft.__editingIndex, undefined);
});

test('failed removal preserves the saved definition, linked draft and preview', async () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const draft = f.store.getState().filter.draft, list = [f.api.toWireDraft(draft)];
  f.store.setSavedScenarios(list); f.store.patchFilterDraftMeta({ __editingIndex: 0, __editingSavedRef: list });
  const preview = { count: 4 }; f.store.setFilterPreview(preview);
  const op = f.bindRemoval(0); op.click(); await settle();
  op.pending[0].reject(new Error('synthetic failure')); await Promise.all(op.actions);
  assert.equal(f.store.getState().filter.savedScenarios, list);
  assert.equal(f.store.getState().filter.draft, draft);
  assert.equal(f.store.getState().filter.preview, preview);
});

for (const automatic of [true, false, undefined]) test('reopening respects recorded naming ownership: ' + automatic, () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const saved = f.api.toWireDraft(f.store.getState().filter.draft);
  if (automatic === undefined) { delete saved.editorOrigins.nameIsAutomatic; delete saved.editorOrigins.rationaleIsAutomatic; }
  else { saved.editorOrigins.nameIsAutomatic = automatic; saved.editorOrigins.rationaleIsAutomatic = automatic; }
  f.store.setSavedScenarios([saved]);
  f.bindRemoval(0, 'edit-scenario').click();
  f.toggle('H');
  assert.equal(f.store.getState().filter.draft.name, automatic === true ? 'G+H' : 'G');
  assert.equal(f.store.getState().filter.draft.__editingIndex, 0);
});

test('copying a saved scenario preserves its copy name without linking the original', () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  f.store.setSavedScenarios([f.api.toWireDraft(f.store.getState().filter.draft)]);
  f.bindRemoval(0, 'copy-scenario').click(); f.toggle('H');
  const draft = f.store.getState().filter.draft;
  assert.equal(draft.name, 'G（副本）'); assert.equal(draft.__editingIndex, undefined);
  assert.equal(f.api.toWireDraft(draft).editorOrigins.nameIsAutomatic, false);
});

test('manual names survive repeated G H toggles and saving', () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  f.store.patchFilterDraftMeta({ name: '人工命名', rationale: '人工動機', __nameDirty: true, __rationaleDirty: true });
  const snapshot = f.api.filterCommitSnapshot(f.store.getState()), saved = f.api.toWireDraft(snapshot.draft);
  f.api.completeScenarioSave(snapshot, { scenarios: [saved], resultRef: { revision: 'r1' }, savedCount: 1 }, 0, saved);
  f.toggle('H'); f.toggle('G', true); f.toggle('H', true); f.toggle('G');
  assert.equal(snapshot.draft.name, '人工命名'); assert.equal(snapshot.draft.rationale, '人工動機');
});

test('late save binds but does not replace newer conditions or their automatic name', () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const snapshot = f.api.filterCommitSnapshot(f.store.getState()), saved = f.api.toWireDraft(snapshot.draft);
  f.toggle('H');
  f.api.completeScenarioSave(snapshot, { scenarios: [saved], resultRef: { revision: 'r1' }, savedCount: 1 }, 0, saved);
  assert.equal(snapshot.draft.name, 'G+H'); assert.equal(snapshot.draft.groups[0].rules.length, 2);
  assert.equal(snapshot.draft.__editingIndex, 0);
  // 2026-10-03 用語統一 W10：使用者裁定以「已儲存」為準（第一次失敗：收據 20261003-024908090-6beccb9968d64a549df8acb53f0c5ff6）。
  assert.match(f.api.savedFeedbackText(snapshot.draft), /尚未儲存/, 'feedback must show newer unsaved edits');
});

for (const mutation of ['same-project-reopen', 'source', 'taxonomy', 'saved-list']) test('late save ignores replaced context: ' + mutation, () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const snapshot = f.api.filterCommitSnapshot(f.store.getState()), saved = f.api.toWireDraft(snapshot.draft);
  if (mutation === 'same-project-reopen') f.store.setProject({ projectId: 'sequence-fixture' });
  if (mutation === 'source') f.store.setImportResult('gl', { batchId: 'next', columns: [], rowCount: 0 });
  if (mutation === 'taxonomy') f.store.setTaxonomyAfterSave({ revision: 2, categories: [] });
  if (mutation === 'saved-list') f.store.setSavedScenarios([]);
  const before = f.store.getState().filter.savedScenarios;
  assert.equal(f.api.completeScenarioSave(snapshot, { scenarios: [saved], resultRef: { revision: 'late' }, savedCount: 1 }, 0, saved), false);
  assert.equal(f.store.getState().filter.savedScenarios, before);
});

test('commit observers see the new list and linked draft in the same notification', () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const snapshot = f.api.filterCommitSnapshot(f.store.getState()), saved = f.api.toWireDraft(snapshot.draft), list = [saved];
  let committed = 0;
  f.store.subscribe(state => {
    if (state.filter.savedScenarios === list) {
      committed++;
      assert.equal(state.filter.draft.__editingSavedRef, list);
      assert.equal(state.filter.draft.__editingIndex, 0);
      assert.equal(state.filterResultRef.revision, 'one');
    }
  });
  f.api.completeScenarioSave(snapshot, { scenarios: list, resultRef: { revision: 'one' }, savedCount: 1 }, 0, saved);
  assert.ok(committed > 0);
});

for (const phase of ['summary', 'vouchers']) test('preview ignores taxonomy changes during ' + phase, async () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const op = f.bindRemoval(0);
  op.clickSelector('[data-action="preview-scenario"]'); await settle();
  assert.equal(op.pending[0].method, 'filterPreview');
  if (phase === 'vouchers') {
    op.pending[0].resolve({ scenario: { count: 1, voucherCount: 1, populationScope: 'auditPeriod' } });
    await settle(); assert.equal(op.pending[1].method, 'queryFilterVoucherPage');
  }
  f.store.setTaxonomyAfterSave({ revision: 2, categories: [] });
  if (phase === 'summary') op.pending[0].resolve({ scenario: { count: 1, populationScope: 'auditPeriod' } });
  else op.pending[1].resolve({ rows: [] });
  await Promise.all(op.actions);
  assert.equal(f.store.getState().filter.preview, null);
  assert.equal(op.pending.length, phase === 'summary' ? 1 : 2, 'do not fetch more data for an invalidated summary');
});

test('a published report survives unavailable catalog refresh without losing other reports', () => {
  const window = { setTimeout, clearTimeout }, document = { createElement() {
    return { textContent: '', get innerHTML() { return String(this.textContent).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); } };
  } }, context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const store = window.JetStore;
  store.setProject({ projectId: 'published' });
  store.setReportArtifacts([{ artifactId: 'prior-paper', kind: 'workingPaper' }, { artifactId: 'validation', kind: 'validationReport' }]);
  store.setStaleState({ filter: true });
  const artifact = { artifactId: 'selected-paper', kind: 'workingPaper', stale: false };
  store.applyReportExport({ artifact, reportArtifacts: null, filterResultsCurrent: false, reportArtifactWarning: '<catalog unavailable>' });
  assert.equal(store.getState().staleState.filter, true, 'a selected export does not prove all saved scenarios are current');
  assert.deepEqual(Array.from(store.getState().reportArtifacts, a => a.artifactId), ['prior-paper', 'validation', 'selected-paper']);
  const published = store.getState().reportArtifacts.find(a => a.artifactId === 'selected-paper');
  assert.equal(published.catalogWarning, '<catalog unavailable>');
  assert.equal(artifact.catalogWarning, undefined, 'response objects are not mutated');
  const html = window.JetUi.reportArtifactListHtml([published]);
  assert.match(html, /role="status"/);
  assert.match(html, /&lt;catalog unavailable&gt;/);
  assert.doesNotMatch(html, /<catalog unavailable>/);
  store.applyReportExport({ artifact, reportArtifacts: [artifact], filterResultsCurrent: true });
  assert.equal(store.getState().staleState.filter, false);
  assert.equal(store.getState().reportArtifacts[0].catalogWarning, undefined, 'successful catalog refresh clears the transient warning');
  const second = { artifactId: 'second-paper', kind: 'workingPaper', stale: false };
  store.applyReportExport({ artifact: second, reportArtifacts: [], filterResultsCurrent: false });
  assert.deepEqual(Array.from(store.getState().reportArtifacts, a => a.artifactId), ['selected-paper', 'second-paper'],
    'an incomplete catalog cannot erase a confirmed publication or the retained list');
  assert.match(store.getState().reportArtifacts[1].catalogWarning, /無須重新產生/);
});

test('successful operations clear only their own stale state and workpaper does not require a criteria file', () => {
  const window = { setTimeout, clearTimeout }, context = vm.createContext({ window });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const store = window.JetStore;
  store.setProject({ projectId: 'synthetic-stale-review' });
  store.setStaleState({ validation: true, prescreen: true, filter: true });
  store.setLastRun('validate', { resultRef: { runId: 'validation-new' }, completenessTest: { eligibility: { isEligible: true } } });
  assert.deepEqual(JSON.parse(JSON.stringify(store.getState().staleState)), { validation: false, prescreen: true, filter: true });
  store.setLastRun('prescreen', { resultRef: { runId: 'prescreen-new' } });
  assert.deepEqual(JSON.parse(JSON.stringify(store.getState().staleState)), { validation: false, prescreen: false, filter: true });
  store.setFilterResultRef({ revision: 'saved-1', populationScope: 'auditPeriod' });
  store.setSavedScenarios([{ name: 'Synthetic', groups: [] }]);
  assert.equal(store.getState().staleState.filter, false);
  store.setStaleState({ filter: true });
  assert.equal(window.JetUi.stepGate(store.getState(), 5).ok, true, 'current definitions can be recalculated without a report file');
  store.applyReportExport({ artifact: { artifactId: 'paper-new', kind: 'workingPaper', stale: false } });
  assert.equal(store.getState().staleState.filter, false, 'workpaper publication has already recalculated filter hits');
  store.setStaleState({ filter: true });
  store.setReportArtifacts([{ artifactId: 'old', kind: 'workingPaper', stale: false }]);
  assert.equal(store.getState().staleState.filter, true, 'loading a catalog is not successful recalculation');
  store.resetWorkflow();
  store.setProject({ projectId: 'never-run' });
  store.setTaxonomyAfterSave({ revision: 1, categories: [] });
  // Batch9 首敗095907202：setter不再複製後端依賴矩陣；固定回應明示未計算結果不算過期。
  store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true },
    staleState: { validation: false, prescreen: false, filter: false }, reportArtifacts: [] });
  assert.deepEqual(JSON.parse(JSON.stringify(store.getState().staleState)), { validation: false, prescreen: false, filter: false },
    'never-run results must not be announced as stale');
  store.setLastRun('validate', { resultRef: { runId: 'v' } });
  store.setLastRun('prescreen', { resultRef: { runId: 'p' } });
  store.setFilterResultRef({ revision: 'r', populationScope: 'auditPeriod' });
  store.setCalendarState({});
  store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true },
    staleState: { validation: false, prescreen: true, filter: true }, reportArtifacts: [] });
  assert.deepEqual(JSON.parse(JSON.stringify(store.getState().staleState)), { validation: false, prescreen: true, filter: true });
  assert.equal(store.getState().lastRuns.validate.resultRef.runId, 'v');
  assert.equal(store.getState().lastRuns.prescreen, null);
  window.setInterval = () => 0; window.clearInterval = () => {};
  window.JetUi.applyLoadedProject({ project: { projectId: 'synthetic-resume' },
    latestRuns: { validate: { resultRef: { runId: 'retained' } }, prescreen: null },
    staleState: { validation: true, prescreen: true, filter: true } }, { stayOnCurrentStep: true });
  assert.deepEqual(JSON.parse(JSON.stringify(store.getState().staleState)), { validation: true, prescreen: true, filter: true },
    'loading historical summaries must preserve the authoritative backend stale flags');
  store.resetWorkflow(); store.setProject({ projectId: 'required-tb' });
  // 2026-10-03 用語統一 W10：使用者裁定以「已儲存」為準（第一次失敗：收據 20261003-024908090-6beccb9968d64a549df8acb53f0c5ff6）。
  assert.deepEqual(JSON.parse(JSON.stringify(window.JetUi.stepGate(store.getState(), 5).missing)),
    ['儲存至少一個篩選情境'], 'a missing saved scenario needs one instruction, not duplicate prerequisites');
  store.setImportResult('gl', { batchId: 'gl' });
  assert.equal(window.JetUi.stepGate(store.getState(), 2).ok, false, 'TB import is still required by the user');
  store.setImportResult('tb', { batchId: 'tb' });
  assert.equal(window.JetUi.stepGate(store.getState(), 2).ok, true);
  store.getState().mapping.gl.committed = {};
  assert.equal(window.JetUi.stepGate(store.getState(), 3).ok, false, 'TB mapping is still required by the user');
  store.getState().mapping.tb.committed = {};
  assert.equal(window.JetUi.stepGate(store.getState(), 3).ok, true);
  for (const file of ['steps/validate-step.js', 'steps/filter-step.js']) {
    assert.doesNotMatch(fs.readFileSync(path.join(root, file), 'utf8'), /Ui\.staleNoticeHtml/);
  }
});

test('both account mapping methods depend on mapped accounts, not a validation result', () => {
  const f = fixture();
  f.store.getState().mapping.gl.committed = { committedUtc: 'synthetic' };
  f.store.getState().lastRuns.validate = null;
  let page = f.render();
  assert.match(page.container.innerHTML, /data-account-save/);
  page.click('toggle-account-excel'); page = f.render();
  assert.match(page.container.innerHTML, /data-action="ensure-account-mapping-template"/);
  assert.match(page.container.innerHTML, /data-action="import-account-mapping"/);
  assert.doesNotMatch(page.container.innerHTML, /請先執行「資料驗證」後，再匯入科目配對/);
});

test('account range selection drags both ways, reverses, cancels and keeps changes separate', () => {
  const frames = new Map(); let frameId = 0, touches = 0;
  function element(attrs = {}) {
    const handlers = {}, classes = new Set();
    return { dataset: {}, isConnected: true, checked: false, scrollTop: 0, scrollHeight: 600, clientHeight: 200,
      classList: { toggle(c, on) { on ? classes.add(c) : classes.delete(c); }, contains: c => classes.has(c) },
      getAttribute: k => attrs[k], addEventListener(k, fn) { handlers[k] = fn; },
      fire(k, args = {}) { const e = { target: this, currentTarget: this, preventDefault() {}, stopPropagation() {}, ...args }; handlers[k]?.(e); },
      focus() {}, setPointerCapture() {}, releasePointerCapture() {}, hasPointerCapture() { return true; }
    };
  }
  const m = { rows: Array.from({ length: 8 }, (_, i) => ({ accountCode: i === 0 ? '__proto__' : 'A' + i })),
    selected: Object.create(null), changes: Object.create(null), category: 'cash', anchor: null, loading: false };
  const table = element(), page = element(), count = element(), apply = element();
  table.getBoundingClientRect = () => ({ top: 0, bottom: 200, left: 0, right: 500 });
  const boxes = m.rows.map((r, i) => {
    const box = element({ 'data-account-select': String(i) });
    box.row = element(); box.row.getBoundingClientRect = () => ({ top: 30 + i * 30 - table.scrollTop, bottom: 60 + i * 30 - table.scrollTop });
    box.closest = s => s === 'tr' ? box.row : s === '[data-account-select-cell]' ? { querySelector: () => box } : null;
    return box;
  });
  table.querySelector = s => s === 'thead' ? { getBoundingClientRect: () => ({ bottom: 30 }) } : null;
  const card = { querySelectorAll: () => boxes, querySelector: s => ({ '.account-editor__table': table,
    '[data-account-select-page]': page, '[data-account-selection-count]': count, '[data-account-apply]': apply })[s] };
  const window = { JetUi: {}, JetStore: { touch() { touches++; } },
    requestAnimationFrame(fn) { const id = ++frameId; frames.set(id, fn); return id; }, cancelAnimationFrame(id) { frames.delete(id); } };
  const source = fs.readFileSync(path.join(root, 'account-mapping-editor.js'), 'utf8');
  window.JetUi.registerWorkflowReset = () => {};
  vm.runInNewContext(source.replace('Ui.accountMappingEditor =', 'global.bindSelectionTest = bindAccountSelection; Ui.accountMappingEditor ='), { window });
  window.bindSelectionTest(card, m);
  const selected = () => boxes.flatMap((b, i) => b.checked ? [i] : []);
  function start(i, shiftKey = false) { table.fire('pointerdown', { target: boxes[i], pointerId: 1, pointerType: 'mouse', button: 0, isPrimary: true, clientX: 20, clientY: 45 + i * 30, shiftKey }); }
  function move(i) { table.fire('pointermove', { pointerId: 1, buttons: 1, clientX: 200, clientY: 45 + i * 30 }); }
  function end() { table.fire('pointerup', { pointerId: 1 }); table.fire('click', { detail: 1 }); }
  start(1); move(4); assert.deepEqual(selected(), [1,2,3,4]);
  move(2); assert.deepEqual(selected(), [1,2], 'shrinking a drag restores rows outside the current range'); end();
  assert.equal(page.indeterminate, true); assert.equal(count.textContent, '已選 2 個科目'); assert.equal(apply.disabled, false);
  start(4); move(3); end(); assert.deepEqual(selected(), [1,2,3,4], 'reverse drag preserves prior selection');
  start(2); move(3); end(); assert.deepEqual(selected(), [1,4], 'starting on a checked row clears that range');
  start(0); move(3); table.fire('keydown', { key: 'Escape' }); assert.deepEqual(selected(), [1,4]);
  start(0); move(2); table.fire('pointercancel', { pointerId: 1 }); assert.deepEqual(selected(), [1,4]);
  start(0); move(2); table.fire('lostpointercapture', { pointerId: 1 }); assert.deepEqual(selected(), [1,4]);
  start(0); end(); assert.deepEqual(selected(), [0,1,4]);
  start(3, true); end(); assert.deepEqual(selected(), [0,1,2,3,4], 'shift uses the preceding anchor');
  // Keyboard/assistive clicks still use the checkbox's native change event.
  boxes[5].fire('click', { shiftKey: true }); boxes[5].checked = true; boxes[5].fire('change');
  assert.deepEqual(selected(), [0,1,2,3,4,5]);
  assert.equal(Object.keys(m.changes).length, 0); assert.equal(touches, 0, 'no rerender or persistence during selection');
  page.checked = false; page.fire('change'); assert.deepEqual(selected(), []); assert.equal(m.anchor, null);
  start(0); move(7);
  const frame = frames.values().next().value; frames.clear(); frame();
  assert.ok(table.scrollTop > 0, 'dragging below the list scrolls the bounded page');
  table.isConnected = false; const detached = frames.values().next().value; frames.clear(); detached();
  assert.equal(frames.size, 0); assert.equal(Object.keys(m.selected).length, 0, 'detached drag cannot commit');
  table.isConnected = true; start(0); m.rows = null; m.loading = true; end();
  assert.equal(Object.keys(m.selected).length, 0, 'a page reload cannot commit an old drag or dereference replaced rows');
});

test('condition group selection handles the outer group but not nested rule events', () => {
  const window = { setTimeout, clearTimeout }, calls = [];
  const context = vm.createContext({ window });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const source = fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8');
  vm.runInContext(source.replace("  Ui.registerStep('filter', render);", '  window.bindGroupTest = bindGroupSelection;'), context);
  const handlers = {}, well = { isConnected: true, dataset: { groupIndex: '1' }, addEventListener(k, fn) { handlers[k] = fn; } };
  const outer = { closest: s => s === '[data-group-index]' ? well : null };
  well.closest = outer.closest;
  window.bindGroupTest({ querySelectorAll: () => [well] }, (...args) => calls.push(args));
  handlers.click({ target: outer }); assert.deepEqual(calls, [[1, true]]);
  handlers.click({ target: { closest: s => s === '.rule-row' ? {} : well } }); assert.equal(calls.length, 1);
  handlers.keydown({ target: well, key: 'Enter', preventDefault() {} });
  handlers.keydown({ target: well, key: ' ', preventDefault() {} });
  handlers.keydown({ target: outer, key: ' ', preventDefault() { throw Error('must not intercept controls'); } });
  assert.equal(calls.length, 3);
  well.isConnected = false; handlers.click({ target: outer }); assert.equal(calls.length, 3);
});

test('auditor wording identifies the actual workbook field and the result being viewed', () => {
  const validate = fs.readFileSync(path.join(root, 'steps/validate-step.js'), 'utf8');
  assert.match(validate, /「Standardized Account Name\*」欄選擇科目分類/);
  assert.match(validate, /<span>篩選結果<\/span>/);
  assert.match(fs.readFileSync(path.join(root, '../index.html'), 'utf8'), /<h1 class="app-header__title">JE Tool<\/h1>/);
  for (const file of ['ui-core.js', 'overview-bi.js', 'steps/validate-step.js']) {
    assert.doesNotMatch(fs.readFileSync(path.join(root, file), 'utf8'), /低頻/, file);
  }
  // Batch9 constants 首敗105924159：文句已由同一門檻組成，不能再只比source的第一段literal。
  // 直接呼叫正式Ui函式核對原固定全文，活頁簿名稱與禁止簡稱的其他斷言全部保留。
  const ui = fixture().window.JetUi;
  assert.equal(ui.prescreenConditionLabel('lowFrequencyPreparer'), '編製分錄較少的人員（11 筆以下）');
  assert.equal(ui.prescreenConditionLabel('lowFrequencyAccount'), '使用較少的科目（11 筆以下）');
  const product = path.resolve(root, '../..');
  const template = fs.readFileSync(path.join(product, 'Infrastructure/Export/AccountMappingTemplateWriter.cs'), 'utf8');
  assert.match(template, /"GL_Number", "GL_Name", "Standardized Account Name\*"/);
  const domain = fs.readFileSync(path.join(product, 'Domain/Rules/FilterConditionLabels.cs'), 'utf8');
  const rules = fs.readFileSync(path.join(product, 'Domain/Rules/PrescreenRules.cs'), 'utf8');
  function domainLabel(key) {
    const declaration = domain.match(new RegExp('\\[PrescreenRuleKeys\\.' + key + '\\]\\s*=\\s*\\$?"([^"\\r\\n]*)"'));
    assert.ok(declaration, 'Domain label declaration: ' + key);
    const value = declaration[1].replace(/\{(PreparerFrequency|AccountFrequency)\.DefaultMaxEntries\}/g, (_, className) => {
      const body = rules.match(new RegExp('class\\s+' + className + '\\b[\\s\\S]*?\\{([^}]+)\\}'));
      assert.ok(body, className + ' definition');
      const constant = body[1].match(/const\s+int\s+DefaultMaxEntries\s*=\s*(\d+)\s*;/);
      assert.ok(constant, className + ' fixed integer'); return constant[1];
    });
    assert.doesNotMatch(value, /[{}]/, 'only the two declared frequency constants may be interpolated');
    return value;
  }
  assert.equal(domainLabel('LowFrequencyPreparer'), '編製分錄較少的人員（11 筆以下）');
  assert.equal(domainLabel('LowFrequencyAccount'), '使用較少的科目（11 筆以下）');
  const catalog = fs.readFileSync(path.join(product, 'AuditCore/RuleCatalog.cs'), 'utf8');
  assert.match(catalog, /"編製分錄較少的人員（11 筆以下）"/);
  assert.match(catalog, /"使用較少的科目（11 筆以下）"/);
});

test('step navigation focus and selected filter conditions have different visual meanings', () => {
  const css = fs.readFileSync(path.join(root, '../css/app.css'), 'utf8');
  assert.doesNotMatch(css, /\.stepflow-item__title:focus\s*\{/);
  assert.match(css, /\.stepflow-item__title:focus-visible/);
  assert.match(css, /\.stepflow-item__title:focus:not\(:focus-visible\)/);
  assert.match(css, /\.filter-workspace \.rule-row--selected/);
  assert.match(css, /\.filter-builder-heading--composition/);
});

test('saving keeps the current scenario and preview, binds repeat saves to the saved slot, and ignores other projects', () => {
  const window = { setTimeout: () => 0, clearTimeout() {} };
  const document = { querySelector: () => null, createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const source = fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8');
  assert.match(source, /function completeScenarioSave\(/);
  vm.runInContext(source.replace("  Ui.registerStep('filter', render);",
    "  window.testSave = { completeScenarioSave, savedFeedbackText, filterCommitSnapshot, syncViewState, toWireDraft, pane: function () { return viewState.workspacePane; } };"), context);
  const store = window.JetStore;
  store.setProject({ projectId: 'synthetic-save' });
  window.testSave.syncViewState(store.getState());
  const draft = { name: '月底分錄', rationale: '查核期末', groups: [{ rules: [{ type: 'numRange', from: '200', to: '800' }] }] };
  draft.groups[0].join = 'AND';
  const definitionBefore = JSON.stringify(draft);
  const wire = window.testSave.toWireDraft(draft);
  assert.equal(wire.groups[0].join, 'OR', 'the existing wire normalization stays unchanged');
  assert.equal(JSON.stringify(draft), definitionBefore, 'wire normalization must not rewrite visible editor state');
  store.setFilterDraft(draft);
  const preview = { count: 2, voucherCount: 1, previewRows: [] };
  store.setFilterPreview(preview);
  let snapshot = window.testSave.filterCommitSnapshot(store.getState());
  const saved = { name: '月底分錄', rationale: '查核期末', groups: draft.groups };
  const response = { resultRef: { revision: 1 }, scenarios: [saved], savedCount: 1 };
  window.testSave.completeScenarioSave(snapshot, response, 0, saved);
  assert.equal(window.testSave.pane(), 'filter');
  assert.equal(store.getState().filter.draft, draft, 'do not destroy the current editor');
  assert.equal(store.getState().filter.preview, preview, 'saving the same definition does not invalidate its preview');
  assert.equal(draft.__editingIndex, 0, 'a repeated save updates instead of appending a duplicate');
  assert.equal(draft.__editingSavedRef, store.getState().filter.savedScenarios);
  assert.equal(store.getFilterDraftRev(), snapshot.revision, 'metadata only, no changed predicate');
  snapshot = window.testSave.filterCommitSnapshot(store.getState());
  window.testSave.completeScenarioSave(snapshot, { ...response, scenarios: [saved], resultRef: { revision: 2 } }, 0, saved);
  assert.equal(draft.__editingSavedRef, store.getState().filter.savedScenarios);
  snapshot = window.testSave.filterCommitSnapshot(store.getState());
  const changedDraft = { name: '另一草稿', rationale: '', groups: [] };
  store.setFilterDraft(changedDraft);
  window.testSave.completeScenarioSave(snapshot, response, 0, saved);
  assert.equal(store.getState().filter.draft, changedDraft, 'a later draft cannot be replaced by an earlier save');
  assert.equal(changedDraft.__editingIndex, undefined);
  store.setProject({ projectId: 'different-project' });
  const before = store.getState().filter.savedScenarios;
  window.testSave.completeScenarioSave(snapshot, response, 0, saved);
  assert.equal(store.getState().filter.savedScenarios, before, 'late save must not change another project');
});

test('account editor keeps drafts on failure, saves only changed accounts, pages and cancels', async () => {
  const reads = [], writes = [], messages = [];
  const window = { setTimeout, clearTimeout, JetApi: {
    queryAccountMappingPage: payload => new Promise((resolve, reject) => reads.push({ payload, resolve, reject })),
    accountMappingSave: payload => new Promise((resolve, reject) => writes.push({ payload, resolve, reject }))
  } };
  const document = { createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'account-mapping-editor.js'])
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context, { filename: file });
  const store = window.JetStore, ui = window.JetUi;
  store.setProject({ projectId: 'synthetic' });
  store.setTaxonomy({ revision: 1, categories: [
    { categoryId: 'builtin.cash', label: 'Cash' }, { categoryId: 'builtin.others', label: 'Others' },
    { categoryId: 'builtin.revenue', label: 'Revenue' }
  ] });
  ui.run = (name, work) => Promise.resolve().then(work).catch(error => messages.push(error.message));
  function render() {
    const html = ui.accountMappingEditor.html(store.getState(), true), controls = new Map();
    function control(selector) {
      if (!controls.has(selector)) controls.set(selector, { value: '', handlers: {},
        getAttribute: () => '0', addEventListener(name, fn) { this.handlers[name] = fn; },
        fire(name) { return this.handlers[name]({ preventDefault() {}, currentTarget: this }); },
        querySelector: () => ({ value: '' }) });
      return controls.get(selector);
    }
    const card = { querySelector: selector => selector === '.account-editor__table' ? null : control(selector),
      querySelectorAll: () => html.includes('data-account-select="') ? [control('selection')] : [] };
    control('selection').closest = () => ({ classList: { toggle() {} } });
    ui.accountMappingEditor.bind({ querySelector: () => card });
    return { html, control };
  }
  render();
  assert.equal(reads.length, 1);
  assert.equal(reads[0].payload.pageSize, 100);
  reads[0].reject(new Error('load failed')); await settle();
  assert.match(render().html, /load failed/);
  render().control('[data-account-retry]').fire('click');
  reads[1].resolve({ rows: [{ accountCode: '__proto__', accountName: 'Synthetic', categoryId: null },
    { accountCode: 'untouched', accountName: 'Existing', categoryId: 'builtin.revenue' }], nextCursor: 'next' });
  await settle();
  function applyCategory(category) {
    let page = render(); page.control('selection').checked = true; page.control('selection').fire('change');
    page = render(); page.control('[data-account-bulk-category]').value = category;
    page.control('[data-account-bulk-category]').fire('change');
    render().control('[data-account-apply]').fire('click');
  }
  let page = render();
  assert.doesNotMatch(page.html, /data-account-category=/);
  applyCategory('builtin.cash');
  assert.match(render().html, /1 個科目待儲存/);
  render().control('[data-account-save]').fire('click'); await settle();
  assert.deepEqual(JSON.parse(JSON.stringify(writes[0].payload)), { changes: [{ accountCode: '__proto__', categoryId: 'builtin.cash' }] });
  writes[0].reject(new Error('save failed')); await settle();
  assert.match(render().html, /1 個科目待儲存/);
  render().control('[data-account-save]').fire('click'); await settle();
  writes[1].resolve({ batchId: 'saved', rowCount: 1, hasAnyCategory: true }); await settle();
  assert.match(render().html, /科目分類已儲存/);
  assert.match(render().html, /data-account-current="1" data-category-id="builtin.revenue"/);
  // All-page selection is explicit and applies once, not a dropdown per account.
  page = render(); page.control('[data-account-select-page]').checked = true;
  page.control('[data-account-select-page]').fire('change');
  page = render(); page.control('[data-account-bulk-category]').value = 'builtin.others';
  page.control('[data-account-bulk-category]').fire('change');
  render().control('[data-account-apply]').fire('click');
  assert.match(render().html, /2 個科目待儲存/);
  assert.equal((render().html.match(/data-category-id="builtin.others"/g) || []).length, 2);
  render().control('[data-account-cancel]').fire('click');
  assert.match(render().html, /data-account-current="0" data-category-id="builtin.cash"/);
  assert.match(render().html, /data-account-current="1" data-category-id="builtin.revenue"/);
  render().control('[data-account-next]').fire('click');
  assert.equal(reads[2].payload.cursor, 'next');
  reads[2].resolve({ rows: [{ accountCode: 'r', accountName: 'Revenue', categoryId: 'builtin.revenue' }], nextCursor: null });
  await settle(); page = render();
  assert.match(page.html, /第 2 頁/);
  applyCategory('builtin.others');
  render().control('[data-account-cancel]').fire('click');
  assert.doesNotMatch(render().html, /個科目待儲存/);
  assert.equal(writes.length, 2);
  render().control('[data-account-search]').fire('submit');
  assert.doesNotMatch(render().html, /data-account-current=/);
  reads[3].reject(new Error('search failed')); await settle();
  assert.match(render().html, /search failed/);
  assert.doesNotMatch(render().html, /data-account-current=/);
  assert.doesNotMatch(render().html, /正在載入科目/);
  store.setProject({ projectId: 'another' }); render();
  assert.equal(reads.length, 5);
  assert.equal(reads[4].payload.cursor, null);
});

// Execute the complete production renderer and its event handlers. Only DOM plumbing and
// asynchronous action responses are doubles; WebView focus/scroll has separate GUI coverage.
function fixture() {
  const requests = [], saves = [];
  const window = { setTimeout, clearTimeout, JetApi: {
    queryPrescreenPage(payload) {
      return new Promise((resolve, reject) => requests.push({ payload, resolve, reject }));
    },
    accountTaxonomySave(payload) {
      return new Promise((resolve, reject) => saves.push({ payload, resolve, reject }));
    }
  }};
  const document = { querySelector: () => null, createElement() {
    return { textContent: '', get innerHTML() {
      return String(this.textContent).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }};
  }};
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'account-mapping-editor.js', 'steps/validate-step.js']) {
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context, { filename: file });
  }
  const store = window.JetStore;
  store.setProject({ projectId: 'synthetic' });
  store.setTaxonomy({ revision: 1, categories: [
    { categoryId: 'builtin.cash', label: 'Cash', semanticRole: 'cash', isBuiltIn: true }
  ] });
  store.getState().lastRuns.prescreen = {
    resultRef: { runId: 'run-1', generatedUtc: '2026-01-01T00:00:00Z' },
    creatorSummary: { creators: [] }, rareAccounts: { distinctAccountCount: 0, accounts: [] },
    postPeriodApproval: { count: 0 }, suspiciousKeywords: { count: 0 },
    unexpectedAccountPair: { count: 0 }, trailingZeros: { count: 0, zerosThreshold: 6 },
    weekendActivity: { postingCount: 1, approvalCount: 1 }, holidayActivity: { postingCount: 0, approvalCount: 0 },
    backdatedPosting: { count: 0 }, blankDescription: { count: 0 }, nonAuthorizedPreparer: { count: 0 },
    lowFrequencyPreparer: { count: 0 }, lowFrequencyAccount: { count: 0 }
  };
  function element(attributes = {}) {
    const events = {};
    let html = '', plain = '', retry;
    return { value: '', hidden: false, disabled: false, isConnected: true, parentElement: null,
      get innerHTML() { return html; }, set innerHTML(value) { html = value; plain = value.replace(/<[^>]*>/g, ''); retry = null; },
      get textContent() { return plain; }, set textContent(value) { plain = value; html = value; retry = null; },
      classList: { toggle() {} },
      getAttribute: key => attributes[key] || null,
      setAttribute: (key, value) => { attributes[key] = value; },
      // Batch9 中低12重繪分頁以真DOM父鏈定位同一明細；舊替身原本未提供closest。
      // 首次失敗104255226：只補DOM能力，原首頁、失敗重試與晚回斷言不變。
      closest(selector) {
        for (let current = this; current; current = current.parentElement) {
          if (selector.startsWith('.') && (current.getAttribute('class') || '').split(/\s+/).includes(selector.slice(1))) return current;
          const attr = selector.match(/^\[([^\]=]+)(?:="([^"]*)")?\]$/);
          if (attr && current.getAttribute(attr[1]) !== null && (attr[2] === undefined || current.getAttribute(attr[1]) === attr[2])) return current;
        }
        return null;
      },
      addEventListener: (name, handler) => { events[name] = handler; },
      fire(name) { assert.ok(events[name], 'bound event: ' + name); return events[name]({ target: this }); },
      querySelector(selector) {
        if (selector === '[data-detail-retry]' && html.includes('data-detail-retry')) return retry || (retry = element());
        return null;
      }, querySelectorAll() { return []; }
    };
  }
  function render() {
    const controls = new Map();
    for (const name of ['run-validate', 'run-prescreen', 'toggle-taxonomy', 'toggle-taxonomy-advanced', 'toggle-account-excel', 'select-account-jet', 'add-taxonomy-category', 'reset-taxonomy', 'save-taxonomy',
      'retry-account-template-in-excel']) {
      controls.set('[data-action="' + name + '"]', element());
    }
    for (const name of ['taxonomy-problems', 'taxonomy-dirty-notice']) {
      controls.set('[data-bind="' + name + '"]', element());
    }
    const label = element({ 'data-taxonomy-label': 'draft-1' });
    const card = element();
    card.querySelector = selector => controls.get(selector) || null;
    card.querySelectorAll = selector => selector === '[data-taxonomy-label]' ? [label] : [];
    const blocks = ['weekendPosting', 'weekendApproval'].map(key => {
      const block = element({ 'data-prescreen-key': key, class: 'rule-detail__preview-block' });
      block.body = element({ class: 'rule-detail__preview-body' });
      block.body.parentElement = block;
      block.querySelector = selector => selector === '.rule-detail__preview-body' ? block.body : null;
      return block;
    });
    const panel = element({ class: 'rule-detail', 'data-bind': 'rule-detail-p-5' }); panel.hidden = true;
    blocks.forEach(block => { block.parentElement = panel; });
    panel.querySelectorAll = selector => selector === '[data-prescreen-key]' ? blocks : [];
    const toggle = element({ 'data-scope': 'p', 'data-idx': '5' });
    const container = element();
    panel.parentElement = container;
    container.querySelector = selector => selector === '[data-bind="account-taxonomy-card"]' ? card
      : selector === '[data-bind="rule-detail-p-5"]' ? panel : controls.get(selector) || null;
    container.querySelectorAll = selector => selector === '[data-action="toggle-detail"]' ? [toggle] : [];
    window.JetUi.renderStep('validate', container, store.getState());
    return { label, blocks, panel, container,
      control: name => controls.get('[data-action="' + name + '"]'),
      click: name => controls.get('[data-action="' + name + '"]').fire('click'),
      toggle: () => toggle.fire('click') };
  }
  return { store, requests, saves, render, window };
}

function validationFixture() {
  return {
    completenessTest: { diffAccountCount: 1, diffAccounts: [],
      partA: { rowCountMatch: true, amountMatch: true }, naReason: null,
      eligibility: { isEligible: true, reason: null, warning: 'GL 與 TB 有 1 個科目差異，可說明原因並繼續。' } },
    docBalanceTest: { unbalancedDocumentCount: 0 }, infSamplingTest: { sampleSize: 0 },
    nullRecordsTest: { nullDocumentCount: 2, nullAccountCount: 2, nullDescriptionCount: 0, outOfRangeDateCount: 0,
      nullRows: [
        { documentNumber: '', accountCode: '1101', description: 'document only', issues: ['document'] },
        { documentNumber: 'JV-2', accountCode: '', description: 'account only', issues: ['account'] },
        { documentNumber: '', accountCode: '', description: 'both missing', issues: ['document', 'account'] }
      ] }
  };
}

test('account work has exclusive JET and Excel views; settings return to the chosen method', () => {
  const f = fixture();
  f.store.getState().mapping.gl.committed = { committedUtc: 'synthetic' };
  f.store.getState().lastRuns.validate = validationFixture();
  function view() { return f.render().container.innerHTML; }
  assert.match(view(), /data-bind="account-editor"/);
  assert.doesNotMatch(view(), /data-bind="account-mapping-card"/);
  f.render().click('toggle-account-excel');
  assert.match(view(), /data-bind="account-mapping-card"/);
  assert.doesNotMatch(view(), /data-bind="account-editor"/);
  f.render().click('toggle-account-excel');
  assert.match(view(), /data-bind="account-mapping-card"/);
  f.render().click('toggle-taxonomy');
  assert.match(view(), /data-bind="account-taxonomy-card"/);
  assert.doesNotMatch(view(), /data-bind="account-editor"|data-bind="account-mapping-card"/);
  f.render().click('toggle-taxonomy');
  assert.match(view(), /data-bind="account-mapping-card"/);
  f.render().click('select-account-jet');
  assert.match(view(), /data-bind="account-editor"/);
  assert.doesNotMatch(view(), /data-bind="account-mapping-card"/);
  assert.match(view(), /role="tablist" aria-label="科目配對方式"/);
  assert.match(view(), /role="tabpanel" id="account-method-panel"/);
});

// 2026-10-02 W8：科目配對檔準備中或失敗時，「在 JET 配對」分頁也看得到一行狀態；成功時只在 Excel 分頁呈現。
test('account mapping file status reaches the JET tab only while preparing or after a failure', () => {
  const f = fixture();
  f.store.getState().mapping.gl.committed = { committedUtc: 'synthetic' };
  f.store.getState().lastRuns.validate = validationFixture();
  const jet = () => f.render().container.innerHTML;
  function excel() {
    f.render().click('toggle-account-excel');
    const html = f.render().container.innerHTML;
    f.render().click('select-account-jet');
    return html;
  }
  assert.doesNotMatch(jet(), /data-account-template-status/);

  f.store.setValidationOutput('template', { runId: 'run-1', status: 'pending', message: '正在準備科目配對範本…' });
  assert.match(jet(), /data-account-template-status="pending" role="status">正在準備科目配對範本…<\/p>/);
  assert.match(jet(), /data-bind="account-editor"/, 'the status line does not replace the JET editor');
  assert.doesNotMatch(jet(), /retry-account-template-in-excel/);
  let html = excel();
  assert.match(html, /data-validation-output="template" role="status">正在準備科目配對範本…<\/p>/);
  assert.doesNotMatch(html, /data-account-template-status/);

  f.store.setValidationOutput('template', { runId: 'run-1', status: 'failed', reason: '檔案被其他程式使用 <synthetic>',
    message: '範本尚未產生：檔案被其他程式使用 <synthetic> 可使用此區按鈕重試。' });
  html = jet();
  assert.match(html, /<p class="form-notice" data-account-template-status="failed" role="status">科目配對檔尚未準備好：檔案被其他程式使用 &lt;synthetic&gt; <button[^>]*data-action="retry-account-template-in-excel">到「用 Excel 配對」重試<\/button><\/p>/);
  assert.doesNotMatch(html, /<synthetic>|可使用此區按鈕重試/);
  assert.doesNotMatch(html, /data-validation-output="template"/);
  html = excel();
  assert.match(html, /data-validation-output="template" role="status">範本尚未產生：檔案被其他程式使用 &lt;synthetic&gt; 可使用此區按鈕重試。<\/p>/);
  assert.doesNotMatch(html, /data-account-template-status/);
  f.render().click('retry-account-template-in-excel');
  assert.match(f.render().container.innerHTML, /data-bind="account-mapping-card"/, 'retry opens the Excel tab');
  assert.match(f.render().container.innerHTML, /id="account-method-excel"[^>]*aria-selected="true"/);
  f.render().click('select-account-jet');

  for (const message of ['已建立 AccountMapping.xlsx（3 個科目）。填妥科目分類並存檔後，即可匯入。',
    '已保留案件資料夾的 AccountMapping.xlsx，不改動其中已填寫的分類。']) {
    f.store.setValidationOutput('template', { runId: 'run-1', status: 'ready', message });
    assert.doesNotMatch(jet(), /data-account-template-status|AccountMapping\.xlsx/);
    assert.ok(excel().includes('data-validation-output="template" role="status">' + message + '</p>'));
  }
});

test('left-side examples and actions use normal page flow without sticky overlays or letter-width columns', () => {
  const css = fs.readFileSync(path.join(root, '../css/app.css'), 'utf8');
  for (const rule of css.matchAll(/[^{}]*\.filter-(?:primary-actions|entry|entry-scroll)[^{}]*\{([^{}]*)\}/g)) {
    assert.doesNotMatch(rule[1], /position:\s*(?:sticky|fixed)|overflow-y:\s*auto/);
  }
  assert.match(css, /\.filter-entry \.picker-card:not\(\.picker-card--kct\)[^{]*\{[^}]*grid-template-columns: minmax\(0, 1fr\)/);
  const source = fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8');
  assert.match(source, /example\.key === 'example2'/);
  // 2026-10-03 用語統一 W10：使用者裁定以「已儲存」為準（第一次失敗：收據 20261003-024908090-6beccb9968d64a549df8acb53f0c5ff6）。
  assert.match(source, /套用範例會替換目前條件，已儲存情境不變/);
  assert.doesNotMatch(source, /沿用 2024 JE 篩選表的代號/);
});

test('completed validation with a difference stays visible and allows the next step; stale results do not', () => {
  const f = fixture();
  f.store.getState().lastRuns.validate = validationFixture();
  assert.equal(f.window.JetUi.stepGate(f.store.getState(), 4).ok, true);
  const html = f.render().container.innerHTML;
  assert.match(html, /1 個科目不符/);
  assert.match(html, /GL 與 TB 有 1 個科目差異，可說明原因並繼續/);
  const firstCard = html.slice(html.indexOf('完整性測試'), html.indexOf('借貸不平測試'));
  assert.doesNotMatch(firstCard, />通過</);
  const completeness = f.store.getState().lastRuns.validate.completenessTest;
  completeness.partA = { rowCountMatch: null, amountMatch: null };
  completeness.diffAccountCount = 0;
  completeness.eligibility.warning = '缺少匯入控制總數，可繼續篩選。';
  assert.equal(f.window.JetUi.stepGate(f.store.getState(), 4).ok, true);
  // 2026-10-05 V2 裁定：狀態照實寫比對對象，不再寫「匯入總數」。
  assert.match(f.render().container.innerHTML, /存下的分錄無法核對/);
  f.store.getState().lastRuns.validate = null;
  assert.equal(f.window.JetUi.stepGate(f.store.getState(), 4).ok, false);
});

test('missing document and account details have independent searchable sorted tables and keep overlapping rows', () => {
  const f = fixture();
  f.store.getState().lastRuns.validate = validationFixture();
  const html = f.render().container.innerHTML;
  const documentTable = html.match(/<tbody data-load-target="null-nullDocument">([\s\S]*?)<\/tbody>/)[1];
  const accountTable = html.match(/<tbody data-load-target="null-nullAccount">([\s\S]*?)<\/tbody>/)[1];
  assert.match(documentTable, /document only/); assert.doesNotMatch(documentTable, /account only/);
  assert.match(accountTable, /account only/); assert.doesNotMatch(accountTable, /document only/);
  assert.match(documentTable, /both missing/); assert.match(accountTable, /both missing/);
  assert.doesNotMatch(html, /空白傳票號碼／科目/);
  for (const idx of [1, 2]) {
    const detail = html.match(new RegExp('data-bind="rule-detail-pn-' + idx + '"[^>]*>([\\s\\S]*?)(?=<li|$)'));
    assert.ok(detail, 'separate detail panel ' + idx);
    assert.match(detail[1], /data-page-search/);
    assert.match(detail[1], /data-sort/);
  }
});

test('taxonomy typing preserves the rendered input and updates save eligibility, cancel and retry', async () => {
  const f = fixture();
  f.render().click('toggle-taxonomy');
  f.render().click('add-taxonomy-category');
  const page = f.render();
  const version = f.store.getState().contentVersion;
  for (const value of ['銀', '銀行', '銀行存款', '銀行款', 'Bank']) {
    page.label.value = value;
    page.label.fire('input');
    assert.equal(f.store.getState().contentVersion, version, 'typing must not destroy the input node or IME composition');
    assert.equal(page.control('save-taxonomy').disabled, false);
  }
  page.label.value = 'Cash'; page.label.fire('input');
  assert.equal(page.control('save-taxonomy').disabled, true, 'duplicate name is still rejected');
  page.label.value = ''; page.label.fire('input');
  assert.equal(page.control('save-taxonomy').disabled, true);
  page.label.value = '銀行存款'; page.label.fire('input');
  page.click('save-taxonomy');
  assert.equal(f.saves[0].payload.categories[1].label, '銀行存款');
  f.saves[0].reject(new Error('synthetic save failure')); await settle();
  assert.match(f.render().container.innerHTML, /銀行存款/);
  f.render().click('save-taxonomy');
  // Batch9 首敗095907202：模擬真save回應的權威影響，不再依靠setter內的第二份依賴表。
  f.saves[1].resolve({ revision: 2, categories: f.saves[1].payload.categories.map((c, i) => ({ ...c, categoryId: i ? 'saved-bank' : 'builtin.cash' })),
    invalidatedResults: { validation: false, prescreen: true, filter: true },
    staleState: { validation: false, prescreen: true, filter: false }, reportArtifacts: [] });
  await settle();
  assert.equal(f.store.getState().taxonomy.categories[1].label, '銀行存款');
  assert.equal(f.store.getState().lastRuns.prescreen, null, 'saved category changes invalidate dependent results');
  f.render().click('add-taxonomy-category');
  f.render().click('reset-taxonomy');
  assert.doesNotMatch(f.render().container.innerHTML, /data-taxonomy-label="draft-/);
});

test('both sides of a prescreen detail load; a failed side can retry without reloading the successful side', async () => {
  const f = fixture(); const page = f.render();
  page.toggle();
  f.requests[0].resolve({ rows: [], nextCursor: null }); await settle();
  assert.equal(f.requests.length, 2, 'opening two sides must actually issue both queries');
  f.requests[1].reject(new Error('synthetic query failure')); await settle();
  assert.match(page.blocks[0].body.innerHTML, /0/);
  assert.match(page.blocks[1].body.textContent, /重試/);
  page.toggle(); page.toggle();
  assert.equal(f.requests.length, 3);
  f.requests[2].resolve({ rows: [], nextCursor: null }); await settle();
  assert.match(page.blocks[1].body.innerHTML, /0/);
  assert.equal(f.store.getState().busy, false);
});

test('detail errors provide a direct retry and accessible waiting state without collapsing the panel', async () => {
  const f = fixture(), page = f.render(); page.toggle();
  assert.equal(page.blocks[0].body.getAttribute('aria-busy'), 'true');
  assert.match(page.blocks[0].body.innerHTML, /role="status"/);
  f.requests[0].resolve({ rows: [], nextCursor: null });
  f.requests[1].reject(new Error('<untrusted>')); await settle();
  const body = page.blocks[1].body;
  assert.equal(body.getAttribute('aria-busy'), 'false');
  assert.match(body.innerHTML, /&lt;untrusted&gt;/);
  assert.ok(body.querySelector('[data-detail-retry]'));
  body.querySelector('[data-detail-retry]').fire('click');
  assert.equal(f.requests.length, 3, 'only the failed side is retried');
  assert.equal(f.store.getState().busy, false);
  f.requests[2].resolve({ rows: [], nextCursor: null }); await settle();
  assert.equal(body.getAttribute('aria-busy'), 'false');
  assert.equal(body.querySelector('[data-detail-retry]'), null);
});

function pagedReadFixture(options = {}) {
  function el() {
    const handlers = {}, attributes = {}, children = [];
    const node = { isConnected: true, hidden: false, disabled: false, textContent: '載入更多', value: '',
      handlers, children, innerHTML: '', className: '',
      setAttribute(k, v) { attributes[k] = v; }, getAttribute(k) { return attributes[k] || null; },
      addEventListener(k, fn) { handlers[k] = fn; },
      fire(k, event = {}) { return handlers[k]({ preventDefault() {}, target: node, ...event }); },
      appendChild(child) { children.push(child); child.parentElement = node; return child; },
      querySelectorAll() { return []; },
      querySelector(selector) { return children.find(c => c.getAttribute('data-page-status') !== null && selector === '[data-page-status]') || null; }
    }; return node;
  }
  const document = { createElement: () => el() }, window = { setTimeout, clearTimeout };
  const context = vm.createContext({ document, window });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const store = window.JetStore; store.setProject({ projectId: 'paged-read' });
  store.setLastRun('validate', { resultRef: { runId: 'v1' } });
  const wrapper = el(), table = el(), more = el(), form = el(), input = el(), clear = el(), sort = el(), requests = [], rows = ['existing'];
  sort.setAttribute('data-sort-key', 'amount'); sort.closest = selector => selector === '.th-sort' ? sort : null;
  table.contains = node => node === sort;
  form.querySelector = selector => selector === '[data-page-search-input]' ? input : selector === '[data-page-search-clear]' ? clear : null;
  wrapper.appendChild(table); wrapper.appendChild(more);
  window.JetUi.bindPagedTable(wrapper, { sourceKind: 'validate', table, search: form, loadMore: more,
    fetchPage(cursor, sort, search) {
      if (options.throwSync) throw new Error('synchronous failure');
      return new Promise((resolve, reject) => requests.push({ cursor, sort, search, resolve, reject })); },
    clearRows() { rows.length = 0; }, appendRows(data) { rows.push(...data); } });
  return { store, wrapper, table, more, form, input, clear, sort, requests, rows };
}

test('paged read ignores late data after a source change and never acquires the global busy state', async () => {
  const f = pagedReadFixture(); f.store.setBusy(true, 'unrelated operation'); f.more.fire('click');
  assert.equal(f.requests.length, 1);
  assert.equal(f.table.getAttribute('aria-busy'), 'true');
  f.store.setProject({ projectId: 'replacement' });
  f.requests[0].resolve({ rows: ['wrong project'], nextCursor: null }); await settle();
  assert.deepEqual(f.rows, ['existing']);
  assert.equal(f.store.getState().busy, true, 'a local read must not release someone else\'s operation');
});

test('paged read preserves rows on failure and reports an actionable local status', async () => {
  const f = pagedReadFixture(); f.more.fire('click');
  f.requests[0].reject(new Error('synthetic failed page')); await settle();
  assert.deepEqual(f.rows, ['existing']);
  assert.equal(f.more.disabled, false);
  const status = f.wrapper.children.find(c => c.getAttribute('data-page-status') !== null);
  assert.ok(status, 'failure belongs next to the affected table');
  assert.match(status.textContent, /重試/);
  assert.equal(status.getAttribute('role'), 'status');
  f.more.fire('click'); f.requests[1].resolve({ rows: ['recovered'], nextCursor: null }); await settle();
  assert.deepEqual(f.rows, ['recovered']);
  assert.equal(f.table.getAttribute('aria-busy'), 'false');
  assert.equal(f.store.getState().busy, false);
  assert.doesNotMatch(status.textContent, /分錄/, 'the shared table also displays accounts and vouchers');
});

for (const change of ['data', 'run', 'detached']) test('paged read rejects a response after ' + change + ' changes', async () => {
  const f = pagedReadFixture(); f.more.fire('click');
  if (change === 'data') f.store.setImportResult('gl', { batchId: 'new' });
  else if (change === 'run') f.store.setLastRun('validate', { resultRef: { runId: 'v1' } });
  else f.wrapper.isConnected = false;
  f.requests[0].resolve({ rows: ['stale'], nextCursor: 'stale-cursor' }); await settle();
  assert.deepEqual(f.rows, ['existing']);
});

test('paged sorting and search retry the exact failed request without discarding the previous result', async () => {
  const f = pagedReadFixture(); f.more.fire('click');
  f.requests[0].resolve({ rows: ['initial'], nextCursor: 'cursor-1' }); await settle();
  f.table.fire('click', { target: f.sort });
  assert.deepEqual(JSON.parse(JSON.stringify(f.requests[1].sort)), { key: 'amount', direction: 'asc' });
  assert.equal(f.requests[1].cursor, null);
  f.requests[1].resolve({ rows: ['sorted'], nextCursor: 'sorted-cursor' }); await settle();
  f.input.value = 'JV'; f.form.fire('submit');
  f.requests[2].reject(new Error('search failed')); await settle();
  assert.deepEqual(f.rows, ['sorted']); assert.equal(f.input.value, '');
  const retry = f.wrapper.children.find(node => node.textContent === '重試');
  retry.fire('click');
  assert.equal(f.requests[3].search, 'JV'); assert.equal(f.requests[3].cursor, null);
  assert.deepEqual(JSON.parse(JSON.stringify(f.requests[3].sort)), { key: 'amount', direction: 'asc' });
  f.requests[3].resolve({ rows: ['matched'], nextCursor: null }); await settle();
  assert.deepEqual(f.rows, ['matched']); assert.equal(f.input.value, 'JV'); assert.equal(f.more.hidden, true);
});

test('synchronous page failure restores controls and no legacy whole-page read helper remains', async () => {
  const f = pagedReadFixture({ throwSync: true }); f.more.fire('click'); await settle();
  assert.equal(f.more.disabled, false); assert.deepEqual(f.rows, ['existing']);
  assert.equal(f.table.getAttribute('aria-busy'), 'false');
  assert.doesNotMatch(fs.readFileSync(path.join(root, 'steps/validate-step.js'), 'utf8'), /Ui\.bindLoadMore/);
});

test('late detail response cannot populate a replacement project with the same run ID', async () => {
  const f = fixture(); const old = f.render(); old.toggle();
  f.store.setProject({ projectId: 'replacement' });
  f.requests[0].resolve({ rows: [], nextCursor: null }); await settle();
  const next = f.render(); next.toggle();
  assert.ok(f.requests.filter(r => r.payload.ruleKey === 'weekendPosting').length === 2,
    'old response cannot become a cache hit in another project');
});

test('collapsing an outstanding detail deduplicates requests and a new run never reuses its page', async () => {
  const f = fixture(); const page = f.render();
  page.toggle(); page.toggle(); page.toggle();
  assert.equal(f.requests.length, 2);
  f.requests[0].resolve({ rows: [], nextCursor: null });
  f.requests[1].resolve({ rows: [], nextCursor: null }); await settle();
  page.blocks[0].body.innerHTML = 'PAGE-AFTER-SORT';
  page.toggle(); page.toggle();
  assert.equal(page.blocks[0].body.innerHTML, 'PAGE-AFTER-SORT', 'reopening must retain the current paged table');
  f.store.getState().lastRuns.prescreen = { ...f.store.getState().lastRuns.prescreen };
  const next = f.render(); next.toggle();
  assert.equal(f.requests.length, 4, 'a replacement run discards cached pages even if its ID is reused');
});


// User comment 1 supersedes the previous full-path presentation. Preserve escaping and missing-file checks.
test('report metadata shows generation time and decimal MB without paths, retaining escaped names and file status', () => {
  const f = fixture();
  const html = f.window.JetUi.reportArtifactListHtml([{
    artifactId: 'synthetic', fileName: '<case>.xlsx', fullPath: 'C:\\Synthetic\\<case>\\case.xlsx',
    generatedUtc: '2026-09-17T00:00:00Z', bytes: 184074, fileState: 'missing'
  }], '', { reveal: true });
  assert.doesNotMatch(html, /儲存位置|C:\\|report-artifact__path|位元組/);
  assert.match(html, /&lt;case&gt;/);
  assert.match(html, /建立時間：/);
  assert.doesNotMatch(html, /（JET 產生）/);
  assert.match(html, /0\.18 MB/);
  assert.match(html, /檔案不存在，重新匯出即可/);
  assert.match(html, /disabled/);
});

test('report actions across steps identify the artifact and preserve retry after a folder failure', async () => {
  const f = fixture(), calls = [];
  const api = f.window.JetApi;
  let fail = true;
  api.hostOpenFolder = payload => {
    calls.push(JSON.parse(JSON.stringify(payload)));
    return fail ? Promise.reject(new Error('Synthetic folder failure')) : Promise.resolve({ ok: true });
  };
  const expected = [
    ['validationReport', '資料驗證報告'], ['infReport', '資料可靠性抽樣清單'],
    ['prescreenReport', '預篩選報告'], ['criteriaSelectionReport', '條件篩選報告'], ['workingPaper', '工作底稿']
  ];
  for (const [kind, label] of expected) {
    const html = f.window.JetUi.reportArtifactListHtml([{
      kind, artifactId: kind, fileName: 'synthetic-' + kind + '.xlsx',
      generatedUtc: '2026-01-01T00:00:00Z', bytes: 4
    }], '', { reveal: true });
    assert.ok(html.includes(label));
    assert.ok(html.includes('synthetic-' + kind + '.xlsx'));
    const events = {};
    f.window.JetUi.bindReportArtifacts({ querySelectorAll: () => [{
      getAttribute: key => { assert.equal(key, 'data-open-artifact'); return kind; },
      addEventListener: (key, fn) => { events[key] = fn; }
    }] });
    events.click(); await settle(); await settle();
    assert.equal(f.store.getState().busy, false);
    assert.deepEqual(calls.at(-1), { artifactId: kind });
    fail = false;
    events.click(); await settle(); await settle();
    assert.equal(f.store.getState().busy, false);
    assert.deepEqual(calls.at(-1), { artifactId: kind });
    fail = true;
  }
  assert.equal(calls.length, 10);
});

test('validation and prescreen folder controls use the same existing artifact action', () => {
  const f = fixture(), ui = f.window.JetUi;
  assert.equal(ui.reportFolderButtonHtml([]), '');
  assert.equal(ui.reportFolderButtonHtml([{ artifactId: 'missing', fileState: 'missing' }]), '');
  for (const kind of ['validationReport', 'prescreenReport']) {
    const html = ui.reportFolderButtonHtml([{ kind, artifactId: kind, fileState: 'available' }]);
    assert.match(html, /開啟資料夾/);
    assert.ok(html.includes('data-open-artifact="' + kind + '"'));
    assert.match(html, /class="btn btn--ghost"/);
  }
});

test('all report kinds use the same path-free metadata and preserve unavailable size and external edits', () => {
  const ui = fixture().window.JetUi;
  for (const kind of ['validationReport', 'infReport', 'prescreenReport', 'criteriaSelectionReport', 'workingPaper']) {
    const html = ui.reportArtifactListHtml([{ kind, artifactId: kind, fileName: kind + '.xlsx',
      fullPath: 'C:\\Synthetic\\hidden.xlsx', generatedUtc: '2026-09-22T00:00:00Z', bytes: 1250000,
      fileState: 'modifiedOutside', stale: true }], '', { history: true, reveal: true });
    assert.match(html, /1\.25 MB/);
    assert.match(html, /建立時間：/);
    assert.match(html, /已在 JET 之外修改/);
    assert.match(html, /先前資料或條件的版本/);
    assert.doesNotMatch(html, /Synthetic|位元組|儲存位置/);
  }
  assert.match(ui.reportArtifactListHtml([{ generatedUtc: 'bad', bytes: null }]), /時間未知.*大小未知/);
});

test('explanations stay visible and shared typography and focus rules cover the workflow and overview', () => {
  const read = file => fs.readFileSync(path.join(root, file), 'utf8');
  const filter = read('steps/filter-step.js'), vouchers = read('filter-vouchers.js');
  assert.doesNotMatch(filter, /<details class="filter-(readback|rule-notes)"/);
  assert.doesNotMatch(vouchers, /<details/);
  assert.match(vouchers, /標示為參考的分錄不計入上方筆數/);
  const css = read('../css/app.css');
  for (const selector of ['stat-card__value', 'population-cell__value', 'overview-pop__kpi-value']) {
    const rule = css.match(new RegExp('\\.' + selector + ' \\{([^}]+)\\}'))[1];
    assert.match(rule, /font-size: var\(--font-stat-size\)/);
    assert.match(rule, /font-weight: var\(--font-stat-weight\)/);
    assert.match(rule, /line-height: var\(--font-stat-leading\)/);
    assert.match(rule, /font-variant-numeric: tabular-nums/);
  }
  assert.match(css, /outline-offset: -2px/);
  assert.match(css, /prefers-reduced-motion: reduce/);
  const index = read('../index.html');
  assert.doesNotMatch(index, /分錄測試自動化工具|>目錄</);
  assert.match(index, /JE Tool/);
});

test('failed or cancelled full imports retain selection and old data, retry alone applies new data', async () => {
  const f = fixture(), calls = [], listeners = new Set();
  const api = f.window.JetApi;
  api.hostSelectFiles = async () => ({ files: [{ filePath: 'synthetic.csv', fileName: 'synthetic.csv' }] });
  api.importInspectFile = async () => ({ fileType: 'csv', columns: ['doc','amount'], encoding: 'utf-8', delimiter: ',' });
  api.importGlFromFile = payload => new Promise((resolve, reject) => calls.push({payload,resolve,reject}));
  api.on = (name, listener) => listeners.add(listener);
  api.off = (name, listener) => listeners.delete(listener);
  const context = vm.createContext({ window: f.window, document: { querySelector: () => null }, setTimeout: () => 0, clearTimeout() {} });
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/import-step.js'), 'utf8'), context);
  f.store.setImportResult('gl', { batchId: 'old', rowCount: 1, columns: ['doc','amount'], sources: [] });
  function render() {
    const actions = new Map();
    function control(name, attributes = {}) {
      const events = {}; const el = { getAttribute: key => attributes[key] || null,
        addEventListener: (event, callback) => events[event] = callback,
        click: () => { assert.ok(events.click, name + ' bound'); events.click(); }};
      actions.set(name, el); return el;
    }
    const toggle = control('toggle', { 'data-task-toggle': 'gl' });
    for (const name of ['wizard-replace-gl','wizard-pick','wizard-confirm','wizard-cancel']) control(name);
    const card = { querySelector: selector => actions.get((selector.match(/data-action="([^"]+)"/) || [])[1]) || null,
      querySelectorAll: () => [] };
    const container = { innerHTML: '',
      querySelector: selector => selector === '[data-bind="import-card-gl"]' ? card : null,
      querySelectorAll: selector => selector === '[data-task-toggle]' ? [toggle] : [] };
    f.window.JetUi.renderStep('import', container, f.store.getState());
    return { html: () => container.innerHTML, click: name => actions.get(name).click() };
  }
  render().click('toggle'); render().click('wizard-replace-gl');
  render().click('wizard-pick'); await settle();
  assert.match(render().html(), /synthetic.csv/);
  for (const code of ['file_read_error', 'operation_cancelled']) {
    render().click('wizard-confirm');
    assert.equal(listeners.size, 1);
    calls.at(-1).reject({code, message:'synthetic failure'}); await settle();
    assert.equal(f.store.getState().busy, false);
    assert.equal(listeners.size, 0);
    assert.equal(f.store.getState().importState.gl.batchId, 'old');
    assert.match(render().html(), /synthetic.csv/);
    assert.match(render().html(), /開始匯入/);
  }
  render().click('wizard-confirm');
  assert.deepEqual(JSON.parse(JSON.stringify(calls[0].payload)), JSON.parse(JSON.stringify(calls[2].payload)));
  calls[2].resolve({batchId:'new',rowCount:2,addedRowCount:2,columns:['doc','amount'],sources:[]}); await settle();
  assert.equal(f.store.getState().importState.gl.batchId,'new');
  assert.equal(listeners.size,0);
  assert.doesNotMatch(render().html(), /synthetic.csv/);
  render().click('wizard-replace-gl'); render().click('wizard-pick'); await settle();
  render().click('wizard-cancel');
  assert.doesNotMatch(render().html(), /synthetic.csv/);
  assert.equal(f.store.getState().importState.gl.batchId,'new');
});

// 2026-10-03 主線在瀏覽器主機重測 S9 時發現：匯入成功後寫入新資料的那一次重繪，匯入精靈還在「匯入中」；之後收起精靈
// 不會再重繪，卡片多停約 4 秒「匯入中…」，「剛剛重新匯入（時間）」徽章也從來沒有出現。這裡在資料更新的那一次通知就重畫並檢查。
test('a finished import shows the result and the just-imported badge in the same update', async () => {
  const f = fixture(), calls = [];
  const api = f.window.JetApi;
  api.hostSelectFiles = async () => ({ files: [{ filePath: 'synthetic.csv', fileName: 'synthetic.csv' }] });
  api.importInspectFile = async () => ({ fileType: 'csv', columns: ['doc','amount'], encoding: 'utf-8', delimiter: ',' });
  api.importGlFromFile = payload => new Promise((resolve, reject) => calls.push({payload,resolve,reject}));
  api.on = () => {}; api.off = () => {};
  const context = vm.createContext({ window: f.window, document: { querySelector: () => null }, setTimeout: () => 0, clearTimeout() {} });
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/import-step.js'), 'utf8'), context);
  f.store.setImportResult('gl', { batchId: 'old', rowCount: 1, columns: ['doc','amount'], sources: [] });
  function render() {
    const actions = new Map();
    function control(name, attributes = {}) {
      const events = {}; const el = { getAttribute: key => attributes[key] || null,
        addEventListener: (event, callback) => events[event] = callback,
        click: () => { assert.ok(events.click, name + ' bound'); events.click(); }};
      actions.set(name, el); return el;
    }
    const toggle = control('toggle', { 'data-task-toggle': 'gl' });
    for (const name of ['wizard-replace-gl','wizard-pick','wizard-confirm','wizard-cancel']) control(name);
    const card = { querySelector: selector => actions.get((selector.match(/data-action="([^"]+)"/) || [])[1]) || null,
      querySelectorAll: () => [] };
    const container = { innerHTML: '',
      querySelector: selector => selector === '[data-bind="import-card-gl"]' ? card : null,
      querySelectorAll: selector => selector === '[data-task-toggle]' ? [toggle] : [] };
    f.window.JetUi.renderStep('import', container, f.store.getState());
    return { html: () => container.innerHTML, click: name => actions.get(name).click() };
  }
  render().click('toggle'); render().click('wizard-replace-gl');
  render().click('wizard-pick'); await settle();
  render().click('wizard-confirm');
  let atUpdate = null;
  f.store.subscribe(state => {
    if (atUpdate === null && state.importState.gl && state.importState.gl.batchId === 'new') atUpdate = render().html();
  });
  calls[0].resolve({batchId:'new',rowCount:2,addedRowCount:2,columns:['doc','amount'],sources:[]}); await settle();
  assert.ok(atUpdate, 'writing the new data notifies the screen');
  assert.doesNotMatch(atUpdate, /匯入中/);
  assert.match(atUpdate, /剛剛重新匯入（/);
  assert.match(atUpdate, /已匯入 2 列/);
});


function authorizedPreparerReviewFixture() {
  const f = fixture(), imports = [], clears = [];
  const api = f.window.JetApi;
  api.hostSelectFile = async () => ({ filePath: 'synthetic.xlsx', fileName: 'synthetic.xlsx' });
  api.importInspectFile = async () => ({ fileType: 'xlsx', worksheets: [{ name: 'List', columns: ['Name', 'Employee ID'] }] });
  api.importPreviewFile = async () => ({ columns: ['Name', 'Employee ID'], sampleRows: [['Example', 'E01']] });
  api.importAuthorizedPreparerFromFile = payload => new Promise((resolve, reject) => imports.push({ payload, resolve, reject }));
  api.importAuthorizedPreparerClear = payload => new Promise((resolve, reject) => clears.push({ payload, resolve, reject }));
  const context = vm.createContext({ window: f.window, document: { querySelector: () => null }, setTimeout: () => 0, clearTimeout() {} });
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/import-step.js'), 'utf8'), context);
  function render() {
    const controls = new Map();
    function control(name, attributes = {}) {
      const events = {};
      const element = { value: '', disabled: false, getAttribute: key => attributes[key] || null,
        addEventListener: (event, callback) => { events[event] = callback; },
        fire(event) { assert.ok(events[event], name + ' ' + event + ' bound'); events[event](); } };
      controls.set(name, element); return element;
    }
    const toggle = control('toggle', { 'data-task-toggle': 'authorizedPreparer' });
    for (const name of ['import-authorized-preparer', 'commit-authorized-preparer', 'clear-authorized-preparer']) control(name);
    control('column'); control('sheet');
    const container = { innerHTML: '', querySelector(selector) {
      if (selector === '[data-ap-column]') return controls.get('column');
      if (selector === '[data-ap-sheet]') return controls.get('sheet');
      return controls.get((selector.match(/data-action="([^"]+)"/) || [])[1]) || null;
    }, querySelectorAll: selector => selector === '[data-task-toggle]' ? [toggle] : [] };
    f.window.JetUi.renderStep('import', container, f.store.getState());
    return { html: () => container.innerHTML, control: name => controls.get(name), click: name => controls.get(name).fire('click') };
  }
  function openCard() { render().click('toggle'); }
  async function importList(result) {
    render().click('import-authorized-preparer'); await settle();
    const page = render(); page.control('column').value = 'Employee ID'; page.control('column').fire('change');
    page.click('commit-authorized-preparer'); imports.at(-1).resolve(result); await settle();
  }
  return { ...f, imports, clears, render, openCard, importList, context };
}

// 第二遍第 3 批：斷言實際渲染與操作結果，不只比對 Store 裡的數字。
test('authorized list explains counted rows and preserves all counts after project reload', async () => {
  const f = authorizedPreparerReviewFixture(); f.openCard();
  const result = { rowCount: 35, sourceColumn: 'Employee ID', sourceRowCount: 54,
    blankRowCount: 4, duplicateRowCount: 15, matchedPreparerCount: 2 };
  await f.importList(result);
  function assertCounts(html) {
    assert.match(html, /原始資料 54 列/);
    assert.match(html, /略過空白 4 列/);
    assert.match(html, /重複識別值 15 列/);
    assert.match(html, /標頭.*完全空白.*不計入/);
    assert.match(html, /有效名單 35 位中，有 2 位出現在 GL 傳票建立人員/);
  }
  assertCounts(f.render().html());
  f.store.resetWorkflow();
  f.window.JetUi.applyLoadedProject({ project: { projectId: 'synthetic' },
    importState: { authorizedPreparer: JSON.parse(JSON.stringify(result)) } }, { stayOnCurrentStep: true });
  f.openCard(); assertCounts(f.render().html());
});

test('authorized list zero matches warn without rejecting the successful import and unknown GL mapping is not zero', async () => {
  const f = authorizedPreparerReviewFixture(); f.openCard();
  await f.importList({ rowCount: 35, sourceColumn: 'Employee ID', matchedPreparerCount: 0 });
  assert.equal(f.store.getState().importState.authorizedPreparer.rowCount, 35);
  assert.equal(f.store.getState().busy, false);
  assert.match(f.render().html(), /有效名單 35 位中，有 0 位出現在 GL 傳票建立人員/);
  assert.match(f.render().html(), /可能選錯識別欄/);
  f.store.setAuthorizedPreparerState({ rowCount: 35, sourceColumn: 'Employee ID', matchedPreparerCount: null });
  assert.match(f.render().html(), /確認.*傳票建立人員.*配對後.*比對/);
  assert.doesNotMatch(f.render().html(), /有 0 位|可能選錯識別欄/);
  f.store.setAuthorizedPreparerState({ rowCount: 35, sourceColumn: 'Employee ID', matchedPreparerCount: 1 });
  assert.match(f.render().html(), /有 1 位出現在 GL/);
  assert.doesNotMatch(f.render().html(), /可能選錯識別欄/, 'one match is enough; no unapproved ratio threshold');
});

test('authorized list old metadata is unknown and GL replacement invalidates only the comparison', () => {
  const f = authorizedPreparerReviewFixture(); f.openCard();
  f.store.setAuthorizedPreparerState({ rowCount: 35, sourceRowCount: null, blankRowCount: null,
    duplicateRowCount: null, matchedPreparerCount: 2 });
  assert.doesNotMatch(f.render().html(), /原始資料 0|略過空白 0|重複識別值 0/);
  f.store.setImportResult('gl', { rowCount: 1, columns: ['author'] });
  assert.equal(f.store.getState().importState.authorizedPreparer.matchedPreparerCount, null);
  assert.equal(f.store.getState().importState.authorizedPreparer.rowCount, 35);
  assert.match(f.render().html(), /確認.*傳票建立人員.*配對後.*比對/);
});

test('authorized list preview explains Excel title rows and removal describes the required next actions', async () => {
  const f = authorizedPreparerReviewFixture(); f.openCard();
  f.store.setAuthorizedPreparerState({ rowCount: 2 });
  f.render().click('import-authorized-preparer'); await settle();
  assert.match(f.render().html(), /Excel.*刪除.*標題列.*重新匯入/);
  f.render().click('clear-authorized-preparer'); f.clears[0].resolve({ cleared: true }); await settle();
  const message = f.store.getState().messages.map(item => item.text).join('\n');
  assert.match(message, /非授權編製人員/);
  assert.match(message, /重新匯入名單後才能計算/);
  assert.match(message, /預篩選.*重新執行/);
  assert.doesNotMatch(message, /依賴清單的結果會重新計算/);
});

test('GL mapping success refreshes the authorized list comparison without another invalidation', async () => {
  const f = authorizedPreparerReviewFixture();
  f.store.setImportResult('gl', { rowCount: 1, columns: ['author'] });
  f.store.replaceMappingDraft('gl', { createBy: 'author' });
  f.store.setAuthorizedPreparerState({ rowCount: 35, matchedPreparerCount: null });
  const refreshed = { rowCount: 35, sourceRowCount: 54, blankRowCount: 4, duplicateRowCount: 15, matchedPreparerCount: 2 };
  let committedPayload;
  f.window.JetApi.mappingCommitGl = async payload => {
    committedPayload = payload;
    return { projectedRowCount: 1, authorizedPreparerState: refreshed };
  };
  const source = fs.readFileSync(path.join(root, 'steps/mapping-step.js'), 'utf8');
  vm.runInContext(source.replace("  Ui.registerStep('mapping', render);",
    '  window.bindReviewedGlMapping = function (container) { bindMappingSection(container, "gl", Ui.GL_FIELDS); };'), f.context);
  let click;
  const section = { addEventListener() {}, querySelectorAll: () => [], querySelector: selector =>
    selector === '[data-action="commit-gl"]' ? { addEventListener: (_, handler) => { click = handler; } } : null };
  f.window.bindReviewedGlMapping({ querySelector: () => section });
  click(); await settle();
  assert.equal(committedPayload.mapping.createBy, 'author');
  assert.equal(Object.hasOwn(committedPayload.mapping, 'createdBy'), false);
  assert.equal(f.store.getState().importState.authorizedPreparer.matchedPreparerCount, 2);
  assert.equal(f.store.getState().importState.authorizedPreparer.sourceRowCount, 54);
  f.store.setLastRun('prescreen', { synthetic: true });
  f.store.refreshAuthorizedPreparerState({ ...refreshed, matchedPreparerCount: 3 });
  assert.equal(f.store.getState().lastRuns.prescreen.synthetic, true, 'read-only status refresh keeps computed results');
});

test('authorized list selects an identifier, keeps the draft on failure, cancels and removes only after success', async () => {
  const f = fixture(), imports = [], clears = [];
  const api = f.window.JetApi;
  api.hostSelectFile = async () => ({filePath:'synthetic.xlsx',fileName:'synthetic.xlsx'});
  api.importInspectFile = async () => ({fileType:'xlsx',worksheets:[{name:'List',columns:['Name','Employee ID']}]});
  api.importPreviewFile = async () => ({columns:['Name','Employee ID'],sampleRows:[['Same name','E01'],['Same name','E02']]});
  api.importAuthorizedPreparerFromFile = payload => new Promise((resolve,reject) => imports.push({payload,resolve,reject}));
  api.importAuthorizedPreparerClear = payload => new Promise((resolve,reject) => clears.push({payload,resolve,reject}));
  const context = vm.createContext({window:f.window,document:{querySelector:()=>null},setTimeout:()=>0,clearTimeout(){}});
  vm.runInContext(fs.readFileSync(path.join(root,'steps/import-step.js'),'utf8'),context);
  f.store.setAuthorizedPreparerState({rowCount:1,sourceColumn:'Old'});
  function render() {
    const controls = new Map();
    function control(name,attributes={}) {
      const events={}; const el={value:'',disabled:false,
        getAttribute:key=>attributes[key]||null,
        addEventListener:(event,callback)=>events[event]=callback,
        fire(event){assert.ok(events[event],name+' '+event+' bound'); events[event]();}};
      controls.set(name,el); return el;
    }
    const toggle=control('toggle',{'data-task-toggle':'authorizedPreparer'});
    for(const name of ['import-authorized-preparer','commit-authorized-preparer','cancel-authorized-preparer',
      'clear-authorized-preparer','retry-authorized-preparer-preview'])control(name);
    control('column'); control('sheet');
    const container={innerHTML:'',querySelector(selector){
      if(selector==='[data-ap-column]')return controls.get('column');
      if(selector==='[data-ap-sheet]')return controls.get('sheet');
      return controls.get((selector.match(/data-action="([^"]+)"/)||[])[1])||null;
    },querySelectorAll:selector=>selector==='[data-task-toggle]'?[toggle]:[]};
    f.window.JetUi.renderStep('import',container,f.store.getState());
    return {html:()=>container.innerHTML,control:name=>controls.get(name),click:name=>controls.get(name).fire('click')};
  }
  render().click('toggle');
  render().click('import-authorized-preparer'); await settle();
  let page=render();
  assert.match(page.html(),/data-ap-column/);
  assert.match(page.html(),/Same name/);
  assert.match(page.html(),/commit-authorized-preparer" disabled/);
  const version=f.store.getState().contentVersion;
  page.control('column').value='Employee ID'; page.control('column').fire('change');
  assert.equal(f.store.getState().contentVersion,version,'choosing an identifier keeps focus without rebuilding');
  page.click('commit-authorized-preparer');
  assert.equal(imports[0].payload.sourceColumn,'Employee ID');
  assert.equal(imports[0].payload.sheetName,'List');
  imports[0].reject(new Error('synthetic read failure')); await settle();
  assert.equal(f.store.getState().importState.authorizedPreparer.sourceColumn,'Old');
  assert.match(render().html(),/Employee ID" selected/);
  render().click('commit-authorized-preparer');
  assert.deepEqual(JSON.parse(JSON.stringify(imports[0].payload)),JSON.parse(JSON.stringify(imports[1].payload)));
  // Batch9 首敗095907202：mutation回應提供依賴失效欄位；失敗/取消保留的斷言仍不變。
  imports[1].resolve({rowCount:2,sourceColumn:'Employee ID',sourceRowCount:4,blankRowCount:1,duplicateRowCount:1,
    invalidatedResults:{validation:false,prescreen:true,filter:true},
    staleState:{validation:false,prescreen:true,filter:false},reportArtifacts:[]}); await settle();
  assert.equal(f.store.getState().importState.authorizedPreparer.rowCount,2);
  assert.doesNotMatch(render().html(),/data-ap-column/);
  render().click('import-authorized-preparer'); await settle();
  render().click('cancel-authorized-preparer');
  assert.doesNotMatch(render().html(),/data-ap-column/);
  assert.equal(imports.length,2);
  render().click('clear-authorized-preparer'); clears[0].reject(new Error('synthetic remove failure')); await settle();
  assert.equal(f.store.getState().importState.authorizedPreparer.rowCount,2);
  render().click('clear-authorized-preparer'); clears[1].resolve({cleared:true,
    invalidatedResults:{validation:false,prescreen:true,filter:true},
    staleState:{validation:false,prescreen:true,filter:false},reportArtifacts:[]}); await settle();
  assert.equal(f.store.getState().importState.authorizedPreparer,null);
  assert.equal(f.store.getState().lastRuns.prescreen,null);
  assert.doesNotMatch(render().html(),/data-action="clear-authorized-preparer"/);
});

// 2026-10-02 使用者裁定移除「情境層級」（W12）：KCT I 是加入目前條件組的一條條件括號，內含週末或假日。
// 2026-10-02 範例缺值時要說明第幾組第幾條（W20）。以下直接執行正式前端腳本核對草稿形狀、送出形狀、舊情境重開與提示文字。
const plain = value => JSON.parse(JSON.stringify(value));
function kctFixture() {
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer(fn) { window.deferred = fn; } } };
  const document = { querySelector: () => null, createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js', 'filter-legacy.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const source = fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8');
  vm.runInContext(source.replace("  Ui.registerStep('filter', render);",
    "  window.kct = { addKctToDraft, removeKctFromActiveGroup, applyKctNaming, toWireDraft, kctCardState, readBackHtml, applyTemplate, scenarioGate, setActiveGroup, syncViewState, bind, kctPickerHtml };"), context);
  const store = window.JetStore, api = window.kct;
  store.setProject({ projectId: 'kct-i-fixture' }); api.syncViewState(store.getState());
  const item = letter => window.JetUi.FILTER_KCT_CHECKLIST.find(x => x.letter === letter);
  function toggle(letter, remove = false) {
    const draft = store.getState().filter.draft;
    api[remove ? 'removeKctFromActiveGroup' : 'addKctToDraft'](draft, item(letter));
    api.applyKctNaming(draft); store.setFilterDraft(draft);
    return draft;
  }
  const text = html => html.replace(/<[^>]+>/g, '');
  function editSaved(index) {
    const nodes = new Map();
    function node(selector) {
      if (!nodes.has(selector)) nodes.set(selector, { dataset: { index: String(index), action: 'edit-scenario' }, value: '', hidden: false, innerHTML: '',
        handlers: {}, classList: { add() {}, remove() {}, toggle() {} }, addEventListener(k, fn) { this.handlers[k] = fn; },
        getAttribute(k) { return k === 'data-index' ? String(index) : null; }, setAttribute() {}, removeAttribute() {}, focus() {},
        querySelector: node, querySelectorAll: () => [] });
      return nodes.get(selector);
    }
    api.bind({ querySelector: node, querySelectorAll: selector => selector.includes('[data-action="edit-scenario"]') ? [node('edit')] : [] });
    node('edit').handlers.click();
    return store.getState().filter.draft;
  }
  function gate() {
    const stub = () => ({ classList: { add() {}, remove() {} }, setAttribute() {}, removeAttribute() {}, focus() {}, parentNode: null, hidden: true, textContent: '' });
    const notice = stub(), nodes = { '[data-bind="scenario-name"]': stub(), '[data-bind="scenario-rationale"]': stub(), '[data-bind="scenario-notice"]': notice };
    const ok = api.scenarioGate({ querySelector: selector => nodes[selector] || null }, false);
    return { ok, notice: notice.hidden ? '' : notice.textContent };
  }
  return { window, store, api, item, toggle, text, editSaved, gate };
}

// R5 now excludes makeup days but retains the existing bracket, group position and KCT identity.
// First post-change failures: 20261004-085250643-d905022f18374b579c7dabd630a8f646.
test('KCT I joins the active group as one condition bracket of nonbusiness days excluding makeup days', () => {
  const f = kctFixture();
  const draft = f.toggle('G'); f.toggle('I');
  assert.equal(draft.groups.length, 1);
  const [blank, bracket] = draft.groups[0].rules;
  assert.equal(blank.__kctLetter, 'G');
  assert.equal(bracket.type, 'group');
  assert.equal(bracket.__kctLetter, 'I');
  assert.equal(bracket.join, 'AND');
  assert.deepEqual(plain(bracket.rules), [
    { type: 'fieldValue', field: 'postDate', operator: 'isNonBusinessDay', join: 'AND' }]);
  assert.ok(bracket.rules.every(child => !child.__kctLetter));
  assert.equal(f.api.kctCardState(draft, f.item('I')), 'selected');
  assert.equal(draft.name, 'G+I');
  assert.equal(draft.rationale, 'G：' + f.item('G').label + '\nI：' + f.item('I').label);
  assert.match(f.text(f.api.readBackHtml(draft)), /預篩選：空白摘要 且 非營業日（排除補班日）/);
  const picker = f.api.kctPickerHtml(draft);
  assert.doesNotMatch(picker, /情境層級|適用整個情境|picker-card--preset/);
  assert.match(picker, /作用中組已選 2 項/);
});

test('adding KCT I twice never shares the bracket, its children or the preset definition', () => {
  const f = kctFixture();
  const draft = f.toggle('I');
  const second = { join: 'AND', matchScope: 'row', rules: [] };
  draft.groups.push(second); f.api.setActiveGroup(draft, second); f.toggle('I');
  const one = draft.groups[0].rules[0], two = draft.groups[1].rules[0];
  const preset = f.window.JetUi.FILTER_KCT_PRESETS.find(p => p.key === 'kctNonBusinessDay').overrides;
  assert.notEqual(one, two);
  assert.notEqual(one.rules, two.rules);
  assert.notEqual(one.rules[0], two.rules[0]);
  assert.notEqual(one.rules, preset.rules);
  assert.notEqual(one.rules[0], preset.rules[0]);
  // Mutate the sole R5 date child, still proving both child identity and deep-copy isolation.
  one.rules[0].operator = 'isWeekend';
  assert.equal(two.rules[0].operator, 'isNonBusinessDay');
  assert.equal(preset.rules[0].operator, 'isNonBusinessDay');
  one.rules[0].join = 'OR';
  assert.equal(two.rules[0].join, 'AND');
  assert.equal(preset.rules[0].join, 'AND');
  assert.equal(draft.name, 'I｜I');
});

test('removing KCT I only touches the active group and keeps the other group bracket', () => {
  const f = kctFixture();
  const draft = f.toggle('I'); f.toggle('G');
  const second = { join: 'OR', matchScope: 'row', rules: [] };
  draft.groups.push(second); f.api.setActiveGroup(draft, second); f.toggle('I');
  assert.equal(f.api.kctCardState(draft, f.item('I')), 'selected');
  f.toggle('I', true);
  assert.equal(second.rules.length, 0);
  assert.deepEqual(plain(draft.groups[0].rules.map(rule => rule.__kctLetter)), ['I', 'G']);
  assert.equal(f.api.kctCardState(draft, f.item('I')), 'elsewhere');
  assert.equal(draft.name, 'G+I');
});

test('the wire keeps the group order, strips UI keys and records no new preset group origin', () => {
  const f = kctFixture();
  const draft = f.toggle('I'); f.toggle('G');
  const wire = f.api.toWireDraft(draft);
  assert.equal(wire.source, 'kct');
  assert.equal(wire.groups.length, 1);
  assert.deepEqual(plain(wire.groups[0].rules.map(rule => rule.type)), ['group', 'prescreen']);
  assert.equal(JSON.stringify(wire).includes('__'), false);
  assert.deepEqual(plain(wire.groups[0].rules[0].rules), [{ type: 'fieldValue', join: 'AND', field: 'postDate', operator: 'isNonBusinessDay', includeBlank: false }]);
  assert.deepEqual(plain(wire.editorOrigins.groups), [{ letters: ['I', 'G'] }]);
  assert.equal(JSON.stringify(wire).includes('presetGroup'), false);
});

test('a scenario saved with the old separate non-business-day group reopens as ordinary conditions with the same tree', () => {
  const f = kctFixture();
  const old = {
    name: 'G｜I', rationale: 'old', source: 'kct',
    editorOrigins: { version: 1, legacyKctSource: false, nameIsAutomatic: true, rationaleIsAutomatic: true,
      groups: [{ presetGroup: false, letters: ['G'] }, { presetGroup: true, letters: ['I', 'I'] }] },
    groups: [
      { join: 'OR', matchScope: 'row', rules: [{ join: 'AND', type: 'prescreen', prescreenKey: 'blankDescription' }] },
      { join: 'AND', matchScope: 'row', rules: [
        { join: 'OR', type: 'prescreen', prescreenKey: 'weekendPosting' },
        { join: 'OR', type: 'prescreen', prescreenKey: 'holidayPosting' }] }]
  };
  f.store.setSavedScenarios([old]);
  const draft = f.editSaved(0);
  assert.equal(draft.groups.length, 2);
  assert.ok(draft.groups.every(group => !('__kctPresetGroup' in group)));
  assert.equal(draft.groups[0].__active, true);
  assert.equal(f.api.kctCardState(draft, f.item('I')), 'elsewhere');
  assert.equal(f.text(f.api.readBackHtml(draft)), '篩選條件：預篩選：空白摘要 且 （預篩選：週末過帳 或 預篩選：假日過帳）');
  const wire = f.api.toWireDraft(draft);
  assert.deepEqual(plain(wire.groups), old.groups, 'reopening and saving keeps the calculation tree');
  assert.deepEqual(plain(wire.editorOrigins.groups), [{ letters: ['G'] }, { letters: ['I', 'I'] }]);
  assert.equal(f.gate().ok, true);
});

test('the non-business-day example puts both conditions in group 1 and names the missing value position', () => {
  const f = kctFixture();
  f.api.applyTemplate('nonBusinessDayExcludingDates');
  const draft = f.store.getState().filter.draft;
  assert.equal(draft.groups.length, 1);
  assert.deepEqual(plain(draft.groups[0].rules.map(rule => rule.type)), ['group', 'fieldValue']);
  assert.equal(draft.groups[0].rules[0].__kctLetter, 'I');
  assert.equal(draft.groups[0].rules[1].join, 'AND');
  assert.deepEqual(plain(draft.groups[0].rules[1].values), [], 'the example never fills dates for the auditor');
  assert.equal(typeof f.window.deferred, 'function', 'focus moves to the value that still needs input');
  assert.deepEqual(plain(f.gate()), { ok: false, notice: '尚需補齊：第 1 組第 2 條「總帳入帳日」請填入 1 到 100 個不同值' });
  draft.groups[0].rules[1].values = ['2025-01-01'];
  assert.equal(f.gate().ok, true);
});

test('missing values inside a condition bracket name the item within the condition', () => {
  const f = kctFixture();
  const draft = f.toggle('G');
  draft.groups[0].rules.push({ type: 'group', join: 'AND', rules: [
    { type: 'prescreen', prescreenKey: 'weekendPosting', join: 'AND' },
    { type: 'fieldValue', join: 'OR', field: 'createBy', operator: 'in', values: [], includeBlank: false }] });
  draft.groups.push({ join: 'AND', matchScope: 'row', rules: [{ type: 'dateRange', field: 'postDate', from: '2025-02-01', to: '2025-01-01' }] });
  f.store.setFilterDraft(draft);
  assert.deepEqual(plain(f.gate()), { ok: false, notice: '尚需補齊：第 1 組第 2 條的第 2 項「' +
    f.window.JetUi.glFieldLabel('createBy') + '」請填入 1 到 100 個不同值、第 2 組第 1 條「日期區間」的起點晚於終點，請先修正' });
});

// 2026-10-03 主線審查加的防護：範例併進 KCT 組只在語意不變時做。組內有「或」時併進去會把「I 且 (a 或 b)」
// 變成「(I 且 a) 或 b」，所以另開一組。目前的正式範例都不是這種形狀，這裡用合成範例鎖住。
test('an example whose first group uses OR stays a separate group after the KCT group', () => {
  const f = kctFixture();
  f.window.JetUi.FILTER_SCENARIO_TEMPLATES.push({ key: 'syntheticOrExample', label: '合成範例', kct: 'I', rationale: 'synthetic',
    groups: [{ join: 'AND', matchScope: 'row', rules: [
      { type: 'prescreen', prescreenKey: 'blankDescription' },
      { type: 'prescreen', prescreenKey: 'weekendPosting', join: 'OR' }] }] });
  f.api.applyTemplate('syntheticOrExample');
  const draft = f.store.getState().filter.draft;
  assert.equal(draft.groups.length, 2);
  assert.deepEqual(plain(draft.groups[0].rules.map(rule => rule.type)), ['group']);
  assert.equal(draft.groups[0].rules[0].__kctLetter, 'I');
  assert.deepEqual(plain(draft.groups[1].rules.map(rule => rule.prescreenKey)), ['blankDescription', 'weekendPosting']);
  assert.equal(draft.groups[1].join, 'AND');
  assert.equal(draft.groups[1].rules[1].join, 'OR');
});

// 2026-10-04 第二遍回饋審閱第 1 批（C2）：匯入精靈選檔後的檢視失敗、就地改選編碼重試、同一來源不重複加入（L10），
// 多工作表各成一個來源（L05），以及單一欄的報表格式提示。這些路徑以前沒有 Node 行為測試。
function importWizardFixture(setup) {
  const f = fixture();
  const api = f.window.JetApi;
  api.on = () => {}; api.off = () => {};
  setup(api);
  const context = vm.createContext({ window: f.window, document: { querySelector: () => null }, setTimeout: () => 0, clearTimeout() {} });
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/import-step.js'), 'utf8'), context);
  function render() {
    const controls = new Map();
    const container = { innerHTML: '' };
    function control(name, attributes = {}) {
      const events = {};
      const el = { value: '', checked: false, disabled: false,
        getAttribute: key => attributes[key] || null,
        addEventListener: (event, callback) => events[event] = callback,
        fire(event) { assert.ok(events[event], name + ' ' + event + ' bound'); events[event](); } };
      controls.set(name, el); return el;
    }
    function matches(pattern) {
      const found = []; let m;
      while ((m = pattern.exec(container.innerHTML))) found.push(m);
      return found;
    }
    const toggle = control('toggle', { 'data-task-toggle': 'gl' });
    for (const name of ['wizard-replace-gl', 'wizard-pick', 'wizard-confirm', 'wizard-cancel']) control(name);
    const card = {
      querySelector: selector => controls.get((selector.match(/data-action="([^"]+)"/) || [])[1]) || null,
      querySelectorAll(selector) {
        if (selector === '[data-pending-bind]') {
          return matches(/data-pending-bind="([^"]+)" data-index="(\d+)"/g)
            .map(m => control('bind:' + m[1] + ':' + m[2], { 'data-pending-bind': m[1], 'data-index': m[2] }));
        }
        const action = (selector.match(/data-action="([^"]+)"/) || [])[1];
        if (!action) return [];
        return matches(new RegExp('data-action="' + action + '" data-index="(\\d+)"', 'g'))
          .map(m => control(action + ':' + m[1], { 'data-index': m[1] }));
      }
    };
    container.querySelector = selector => selector === '[data-bind="import-card-gl"]' ? card : null;
    container.querySelectorAll = selector => selector === '[data-task-toggle]' ? [toggle] : [];
    f.window.JetUi.renderStep('import', container, f.store.getState());
    return { html: () => container.innerHTML, click: name => controls.get(name).fire('click'), control: name => controls.get(name) };
  }
  function open() { render().click('toggle'); render().click('wizard-replace-gl'); }
  return { f, api, render, open };
}

test('picking files continues after one fails, offers an encoding retry in place, and never adds the same source twice', async () => {
  const previews = [];
  let badFails = true;
  const w = importWizardFixture(api => {
    api.hostSelectFiles = async () => ({ files: [
      { filePath: 'C:\\in\\bad.txt', fileName: 'bad.txt' }, { filePath: 'C:\\in\\good.csv', fileName: 'good.csv' }] });
    api.importInspectFile = async ({ filePath }) => {
      if (/bad\.txt$/.test(filePath) && badFails) { const e = new Error('synthetic decode failure'); e.code = 'file_read_error'; throw e; }
      return { fileType: 'csv', columns: ['doc', 'amount'], encoding: 'utf-8', delimiter: ',' };
    };
    api.importPreviewFile = async payload => { previews.push(payload); return { columns: ['doc', 'amount'], sampleRows: [['a', '1']] }; };
  });
  w.open();
  w.render().click('wizard-pick'); await settle();
  let page = w.render();
  assert.match(page.html(), /bad\.txt/, 'the failed source stays in the list');
  assert.match(page.html(), /good\.csv/, 'the later source is still inspected');
  assert.match(page.html(), /synthetic decode failure/);
  assert.match(page.html(), /開始匯入（1 個來源）/);
  assert.match(page.html(), /data-pending-bind="include" data-index="0"[^>]*disabled/, 'a source that could not be read cannot be sent');
  const encoding = page.control('bind:encoding:0');
  assert.ok(encoding, 'the failed text source still offers the encoding choice');
  encoding.value = 'big5'; encoding.fire('change'); await settle();
  assert.deepEqual(plain(previews.at(-1)), { filePath: 'C:\\in\\bad.txt', limit: 10, encoding: 'big5' });
  page = w.render();
  assert.doesNotMatch(page.html(), /synthetic decode failure/);
  assert.match(page.html(), /開始匯入（2 個來源）/);
  badFails = false;
  w.render().click('wizard-pick'); await settle();
  page = w.render();
  assert.equal((page.html().match(/class="pending-row"/g) || []).length, 2, 'the same file is not added twice');
  assert.ok(w.f.store.getState().messages.some(m => /已在清單/.test(m.text)), 'the auditor is told the file was already listed');
});

test('a workbook with several sheets becomes one pending source per sheet and each is sent with its sheet name', async () => {
  const calls = [];
  const w = importWizardFixture(api => {
    api.hostSelectFiles = async () => ({ files: [{ filePath: 'book.xlsx', fileName: 'book.xlsx' }] });
    api.importInspectFile = async () => ({ fileType: 'xlsx', worksheets: [
      { name: '上半年', columns: ['a', 'b'], rowCountEstimate: 10 },
      { name: '下半年', columns: ['a', 'b'], rowCountEstimate: null },
      { name: '空表', columns: [] }] });
    api.importGlFromFile = payload => new Promise(resolve => calls.push({ payload, resolve }));
  });
  w.open();
  w.render().click('wizard-pick'); await settle();
  const html = w.render().html();
  assert.match(html, /上半年/); assert.match(html, /下半年/); assert.doesNotMatch(html, /空表/);
  assert.match(html, /開始匯入（2 個來源）/);
  w.render().click('wizard-confirm');
  assert.deepEqual(plain(calls[0].payload), { mode: 'replace', sources: [
    { filePath: 'book.xlsx', fileName: 'book.xlsx', sheetName: '上半年' },
    { filePath: 'book.xlsx', fileName: 'book.xlsx', sheetName: '下半年' }] });
  calls[0].resolve({ batchId: 'n', rowCount: 4, addedRowCount: 4, columns: ['a', 'b'], sources: [] }); await settle();
  assert.equal(w.f.store.getState().importState.gl.batchId, 'n');
});

test('a single-column text file shows the report-layout notice next to the source', async () => {
  const w = importWizardFixture(api => {
    api.hostSelectFiles = async () => ({ files: [{ filePath: 'report.txt', fileName: 'report.txt' }] });
    api.importInspectFile = async () => ({ fileType: 'csv', columns: ['COL_1'], encoding: 'utf-8', delimiter: null,
      notices: ['synthetic notice about 報表格式'] });
  });
  w.open();
  w.render().click('wizard-pick'); await settle();
  assert.match(w.render().html(), /synthetic notice about 報表格式/);
});

function projectMetadataFixture(project = null) {
  const f = fixture(), creates = [], updates = [];
  f.store.resetWorkflow(); if (project) f.store.setProject(project);
  f.window.JetApi.projectCreate = payload => new Promise((resolve, reject) => creates.push({ payload, resolve, reject }));
  f.window.JetApi.projectUpdate = payload => new Promise((resolve, reject) => updates.push({ payload, resolve, reject }));
  const context = vm.createContext({ window: f.window, document: { querySelector: () => null } });
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/create-step.js'), 'utf8'), context);
  function render(values = {}) {
    const controls = new Map();
    function node(key) {
      if (!controls.has(key)) controls.set(key, { value: '', hidden: false, textContent: '', attributes: {}, events: {},
        setAttribute(name, value) { this.attributes[name] = value; }, removeAttribute(name) { delete this.attributes[name]; },
        addEventListener(event, callback) { this.events[event] = callback; }, matches: () => true, focus() {} });
      return controls.get(key);
    }
    const form = node('form'); form.elements = {};
    form.querySelector = selector => node(selector);
    form.querySelectorAll = selector => selector === '[aria-invalid="true"]'
      ? Object.values(form.elements).filter(field => field.attributes['aria-invalid'] === 'true') : [];
    const container = { _html: '', get innerHTML() { return this._html; }, set innerHTML(value) {
      this._html = value;
      for (const input of value.matchAll(/<(?:input|select)\b[^>]*\bname="([^"]+)"[^>]*>/g)) {
        const name = input[1], field = node(name);
        const encoded = (input[0].match(/\bvalue="([^"]*)"/) || [])[1] || '';
        field.value = Object.hasOwn(values, name) ? values[name] : encoded.replace(/&quot;/g, '"').replace(/&amp;/g, '&');
        form.elements[name] = form[name] = field;
      }
    }, querySelector(selector) {
      if (selector === '[data-bind="create-form"]') return this._html.includes('data-bind="create-form"') ? form : null;
      if (selector === '[data-bind="project-update-form"]') return this._html.includes('data-bind="project-update-form"') ? form : null;
      const action = (selector.match(/data-action="([^"]+)"/) || [])[1];
      return action && this._html.includes('data-action="' + action + '"') ? node(action) : null;
    }, querySelectorAll: () => [] };
    f.window.JetUi.renderStep('create', container, f.store.getState());
    return { html: () => container.innerHTML, form, node,
      click(action) { const button = node(action); assert.ok(button.events.click, action + ' bound'); button.events.click(); },
      input(name, value) { const target = form.elements[name]; assert.ok(target, name + ' visible'); target.value = value;
        form.events.input?.({ target, currentTarget: form }); },
      submit() { assert.ok(form.events.submit, 'form submit bound'); form.events.submit({ preventDefault() {}, target: form }); } };
  }
  return { ...f, creates, updates, render, context };
}

const syntheticMetadata = () => ({ projectId: 'Synthetic case', entityName: 'Original client', projectCode: 'CASE-01',
  operatorId: 'Synthetic user', periodStart: '2024-01-01', periodEnd: '2024-02-29', lastPeriodStart: null, databaseProvider: 'sqlite' });
const validCreateValues = () => ({ caseName: 'Synthetic case', entityName: '', projectCode: '', periodStart: '2024-01-01',
  periodEnd: '2024-02-29', lastPeriodStart: '', databaseProvider: 'sqlite' });

test('project creation blocks whitespace names and reversed periods before sending an action', () => {
  for (const name of ['   ', '\u3000\u3000', ' \u3000\t ']) {
    const f = projectMetadataFixture(), page = f.render({ ...validCreateValues(), caseName: name });
    page.submit(); assert.equal(f.creates.length, 0);
    assert.match(page.node('[data-bind="create-error"]').textContent, /案件名稱.*空白/);
    assert.equal(page.form.elements.caseName.attributes['aria-invalid'], 'true');
  }
  const f = projectMetadataFixture();
  const page = f.render({ ...validCreateValues(), periodStart: '2025-01-01', periodEnd: '2024-12-31' });
  page.submit(); assert.equal(f.creates.length, 0);
  assert.match(page.node('[data-bind="create-error"]').textContent, /查核起始日.*晚於.*截止日/);
  assert.equal(page.form.elements.periodStart.attributes['aria-invalid'], 'true');
  const sameDay = f.render({ ...validCreateValues(), periodStart: '2024-02-29' });
  sameDay.submit(); assert.equal(f.creates.length, 1, 'same-day periods remain valid');
});

test('project preparation-date warnings use exact calendar-year limits including leap years and never block create', () => {
  for (const [value, warns] of [['2023-12-31', true], ['2024-01-01', false], ['2025-02-28', false], ['2025-03-01', true], ['', false]]) {
    const f = projectMetadataFixture(), page = f.render(validCreateValues());
    page.input('lastPeriodStart', value);
    const warning = page.node('[data-bind="preparation-date-warning"]');
    assert.equal(!warning.hidden && /確認年份/.test(warning.textContent), warns, value || 'unset');
    page.submit(); assert.equal(f.creates.length, 1, 'a date warning is not a gate');
  }
});

test('project creation shows exact backend invalid characters and preserves the entered form', async () => {
  const f = projectMetadataFixture(), page = f.render({ ...validCreateValues(), caseName: 'A&B' });
  page.submit(); assert.equal(f.creates.length, 1, 'the backend owns the permitted-character rule');
  f.creates[0].reject({ field: 'caseName', message: '案件名稱不能使用「&」。請把完整公司名稱填在客戶名稱。' }); await settle();
  assert.match(page.node('[data-bind="create-error"]').textContent, /「&」.*客戶名稱/);
  assert.equal(page.form.elements.caseName.value, 'A&B');
  assert.equal(page.form.elements.caseName.attributes['aria-invalid'], 'true');
});

test('project metadata editor sends only editable fields and failed saves and cancellation retain persisted data', async () => {
  const original = syntheticMetadata(), f = projectMetadataFixture(original);
  assert.match(f.render().html(), /修改案件資料/);
  f.render().click('edit-project-metadata'); let page = f.render();
  assert.deepEqual(Object.keys(page.form.elements).sort(), ['entityName', 'lastPeriodStart', 'projectCode']);
  assert.equal(page.form.entityName.value, 'Original client');
  assert.match(page.html(), /案件名稱.*查核期間.*不能修改/);
  assert.match(page.html(), /已匯出.*不.*改寫/);
  page.input('entityName', ' New client '); page.input('projectCode', ' NEW-02 '); page.input('lastPeriodStart', '2025-03-01');
  assert.match(page.node('[data-bind="preparation-date-warning"]').textContent, /確認年份/);
  page.submit();
  assert.deepEqual(plain(f.updates[0].payload), { entityName: 'New client', projectCode: 'NEW-02', lastPeriodStart: '2025-03-01' });
  f.updates[0].reject(new Error('synthetic metadata save failure')); await settle();
  assert.equal(f.store.getState().project, original);
  assert.match(page.node('[data-bind="project-update-error"]').textContent, /synthetic metadata save failure/);
  f.store.touch(); page = f.render();
  assert.equal(page.form.entityName.value.trim(), 'New client', 'unrelated rendering keeps the unsaved draft');
  page.click('cancel-project-metadata');
  assert.doesNotMatch(f.render().html(), /data-bind="project-update-form"/);
  assert.equal(f.store.getState().project.entityName, 'Original client');
  f.render().click('edit-project-metadata');
  assert.equal(f.render().form.entityName.value, 'Original client', 'reopening after cancellation starts from persisted values');
});

test('project metadata successful save updates visible metadata without navigating or discarding the filter draft', async () => {
  const original = syntheticMetadata(), f = projectMetadataFixture(original);
  f.store.setFilterDraft({ name: 'Unsaved synthetic scenario', rationale: 'keep this', groups: [] });
  const draft = f.store.getState().filter.draft, preview = { count: 2 };
  f.store.setFilterPreview(preview); f.store.setLastRun('validate', { syntheticValidation: true });
  f.store.setLastRun('prescreen', { syntheticPrescreen: true });
  const artifacts = [{ artifactId: 'original-workpaper', kind: 'workingPaper', stale: true }];
  const index = f.store.getState().currentStepIndex;
  f.render().click('edit-project-metadata'); const page = f.render();
  page.input('entityName', 'Updated client'); page.submit();
  f.updates[0].resolve({ project: { ...original, entityName: 'Updated client' }, warnings: ['Synthetic backend warning'],
    staleState: { validation: false, prescreen: false, filter: false }, artifacts }); await settle();
  assert.equal(f.store.getState().caseClient, 'Updated client');
  assert.equal(f.store.getState().caseId, original.projectId);
  assert.equal(f.store.getState().currentStepIndex, index);
  assert.equal(f.store.getState().filter.draft, draft);
  assert.equal(f.store.getState().filter.preview, preview);
  assert.equal(f.store.getState().lastRuns.validate.syntheticValidation, true);
  assert.equal(f.store.getState().lastRuns.prescreen.syntheticPrescreen, true);
  assert.equal(f.store.getState().reportArtifacts[0].artifactId, 'original-workpaper');
  assert.equal(f.store.getState().reportArtifacts[0].stale, true);
  assert.match(f.render().html(), /Updated client/);
  assert.doesNotMatch(f.render().html(), /data-bind="project-update-form"/);
  assert.ok(f.store.getState().messages.some(message => message.text === 'Synthetic backend warning'));
});

test('metadata result invalidation follows backend flags rather than frontend date guesses', () => {
  for (const flags of [{ validation: false, prescreen: true, filter: true }, { validation: false, prescreen: false, filter: false }]) {
    const f = projectMetadataFixture(syntheticMetadata());
    f.store.setLastRun('validate', { id: 'validation' }); f.store.setLastRun('prescreen', { id: 'prescreen' });
    f.store.setFilterDraft({ name: 'Keep editor', rationale: 'Synthetic', groups: [] });
    const draft = f.store.getState().filter.draft, preview = { count: 3 };
    f.store.setFilterPreview(preview);
    const saved = [{ name: 'Keep saved definition', groups: [] }]; f.store.setSavedScenarios(saved);
    // 第 4 批契約分開「修改影響」與「持久化結果過期」；保留原有清除與保留的斷言。
    f.store.updateProjectMetadata({ project: { ...syntheticMetadata(), lastPeriodStart: '2024-02-01' },
      invalidatedResults: flags, staleState: flags,
      artifacts: [{ artifactId: 'existing', stale: flags.filter }] });
    assert.equal(f.store.getState().lastRuns.validate.id, 'validation');
    assert.equal(f.store.getState().lastRuns.prescreen?.id || null, flags.prescreen ? null : 'prescreen');
    assert.equal(f.store.getState().filter.preview, flags.filter ? null : preview);
    assert.equal(f.store.getState().filter.draft, draft);
    assert.equal(f.store.getState().filter.savedScenarios, saved);
    assert.deepEqual(plain(f.store.getState().staleState), flags);
  }
});

test('metadata impact clears an unsaved preview even when no persisted result can be stale', () => {
  const f = projectMetadataFixture(syntheticMetadata());
  f.store.setFilterDraft({ name: 'Unsaved only', rationale: 'Keep this work', groups: [] });
  const draft = f.store.getState().filter.draft;
  f.store.setFilterPreview({ count: 3 });
  f.store.setLastRun('validate', { id: 'independent-validation' });
  f.store.setLastRun('prescreen', { id: 'cached-prescreen' });
  const generation = f.store.getState().dataGeneration;
  const draftRevision = f.store.getFilterDraftRev();
  f.store.updateProjectMetadata({ project: { ...syntheticMetadata(), lastPeriodStart: '2024-02-01' },
    invalidatedResults: { validation: false, prescreen: true, filter: true },
    staleState: { validation: false, prescreen: false, filter: false }, artifacts: [] });
  assert.equal(f.store.getState().filter.preview, null);
  assert.equal(f.store.getState().lastRuns.prescreen, null);
  assert.equal(f.store.getState().lastRuns.validate.id, 'independent-validation');
  assert.equal(f.store.getState().filter.draft, draft);
  assert.ok(f.store.getState().dataGeneration > generation, 'dependent read caches must reload');
  assert.ok(f.store.getFilterDraftRev() > draftRevision, 'pending previews cannot restore the old result');
  assert.deepEqual(plain(f.store.getState().staleState), { validation: false, prescreen: false, filter: false });
});

test('project metadata draft is discarded on a project switch and a late response cannot alter another project', async () => {
  const f = projectMetadataFixture(syntheticMetadata());
  f.render().click('edit-project-metadata'); let page = f.render();
  page.input('entityName', 'Unsaved old case'); page.submit();
  const next = { ...syntheticMetadata(), projectId: 'Other case', entityName: 'Other client' };
  f.store.resetWorkflow(); f.store.setProject(next);
  assert.doesNotMatch(f.render().html(), /data-bind="project-update-form"|Unsaved old case/);
  f.updates[0].resolve({ project: { ...syntheticMetadata(), entityName: 'Old case reply' }, warnings: [],
    staleState: { validation: false, prescreen: false, filter: false }, artifacts: [] }); await settle();
  assert.equal(f.store.getState().project, next);
  assert.equal(f.store.getState().caseClient, 'Other client');
});

test('project update has a JetApi method and missing preparation dates point to the actual metadata editor', () => {
  const apiWindow = {};
  vm.runInNewContext(fs.readFileSync(path.join(root, 'jet-api.js'), 'utf8'), { window: apiWindow });
  assert.equal(typeof apiWindow.JetApi.projectUpdate, 'function');
  const f = scenarioLifecycleFixture();
  const source = fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8');
  const context = vm.createContext({ window: f.window, document: { querySelector: () => null } });
  vm.runInContext(source.replace("  Ui.registerStep('filter', render);", '  window.reviewMetadataRule = ruleControlsHtml;'), context);
  const rule = { type: 'prescreen', prescreenKey: 'postPeriodApproval' };
  const html = f.window.reviewMetadataRule(rule, 0, 0);
  assert.match(html, /期末財報準備日.*建立案件.*修改案件資料/);
  assert.doesNotMatch(html, /lastPeriodStart|重新建立案件/);
  f.store.setProject({ projectId: 'synthetic', lastPeriodStart: '2024-02-01' });
  assert.doesNotMatch(f.window.reviewMetadataRule(rule, 0, 0), /修改案件資料/);
});

function taxonomyReviewDom(html) {
  const nodes = [];
  for (const tag of html.matchAll(/<(input|select|button|p|div|li|section|details|summary)\b([^>]*)>/g)) {
    const attributes = {};
    for (const attr of tag[2].matchAll(/([\w-]+)(?:="([^"]*)")?/g)) attributes[attr[1]] = attr[2] ?? '';
    const node = { attributes, tagName: tag[1].toUpperCase(), type: attributes.type || '', isConnected: true,
      open: Object.hasOwn(attributes, 'open'), value: attributes.value || '', hidden: Object.hasOwn(attributes, 'hidden'),
      disabled: Object.hasOwn(attributes, 'disabled'), checked: Object.hasOwn(attributes, 'checked'), events: {}, textContent: '',
      getAttribute: name => attributes[name] ?? null,
      setAttribute(name, value) { attributes[name] = String(value); }, removeAttribute(name) { delete attributes[name]; },
      addEventListener(event, fn) { this.events[event] = fn; },
      fire(event = 'click') { assert.ok(this.events[event], JSON.stringify(attributes) + ' ' + event + ' bound'); this.events[event]({ target: this, currentTarget: this, preventDefault() {} }); },
      focus() { this.focused = true; }, scrollIntoView() { this.scrolled = true; }, classList: { add() {}, remove() {}, toggle() {} } };
    nodes.push(node);
  }
  function matches(node, selector) {
    const attr = /^\[([^=\]]+)(?:="([^"]*)")?\]$/.exec(selector);
    return !!attr && Object.hasOwn(node.attributes, attr[1]) && (attr[2] === undefined || node.attributes[attr[1]] === attr[2]);
  }
  const dom = { innerHTML: html, querySelector: selector => nodes.find(node => matches(node, selector)) || null,
    querySelectorAll: selector => nodes.filter(node => matches(node, selector)) };
  nodes.forEach(node => { node.querySelector = dom.querySelector; node.querySelectorAll = dom.querySelectorAll; });
  return dom;
}

function taxonomyReviewFixture() {
  const f = fixture(), focuses = [];
  let currentDom;
  f.window.JetFocus = { defer(target) { focuses.push(target); } };
  const context = vm.createContext({ window: f.window, document: { querySelector: selector => currentDom?.querySelector(selector) || null } });
  const source = fs.readFileSync(path.join(root, 'steps/validate-step.js'), 'utf8');
  vm.runInContext(source.replace("  Ui.registerStep('validate', render);",
    '  window.taxonomyReview = { html: taxonomyCardHtml, rows: taxonomyRows, bind: bindTaxonomyCard, bindAll: bind };'), context);
  for (const name of ['filter-values.js', 'filter-legacy.js']) vm.runInContext(fs.readFileSync(path.join(root, name), 'utf8'), context);
  const filter = fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8');
  vm.runInContext(filter.replace("  Ui.registerStep('filter', render);",
    '  window.taxonomyFilterReview = { summary: ruleSummaryLabel, controls: ruleControlsHtml, subject: ruleSubjectHtml, wire: toWireDraft };'), context);
  function render() {
    const html = f.window.taxonomyReview.html(f.store.getState()); currentDom = taxonomyReviewDom(html);
    f.window.taxonomyReview.bind(currentDom);
    return { html, dom: currentDom,
      click(selector) { const node = currentDom.querySelector(selector); assert.ok(node, selector + ' rendered'); node.fire(); },
      change(selector, value, event = 'change') { const node = currentDom.querySelector(selector); assert.ok(node, selector + ' rendered'); node.value = value; node.fire(event); } };
  }
  return { ...f, api: f.window.taxonomyReview, filterApi: f.window.taxonomyFilterReview, render, focuses,
    focusNow() { for (const target of focuses.splice(0)) { const element = typeof target === 'function' ? target() : target; if (element) { element.focus(); element.scrollIntoView(); } } } };
}

function classificationTree() {
  return [
    { categoryId: 'builtin.cash', label: '現金', semanticRole: 'cash', isBuiltIn: true },
    { categoryId: 'bank', label: '銀行存款', semanticRole: 'cash', parentCategoryId: 'builtin.cash' },
    { categoryId: 'petty', label: '庫存現金', semanticRole: 'others', parentCategoryId: 'builtin.cash' },
    { categoryId: 'unrelated', label: '其他同用途', semanticRole: 'cash' }
  ];
}

test('taxonomy can submit a new parent and child together and inherits purpose only until manually changed', async () => {
  const f = taxonomyReviewFixture();
  f.render().click('[data-action="add-taxonomy-category"]');
  f.render().change('[data-taxonomy-label="draft-1"]', '新上層', 'input');
  f.render().change('[data-taxonomy-role="draft-1"]', 'cash');
  f.render().click('[data-action="add-taxonomy-category"]');
  let page = f.render();
  assert.match(page.html, /option value="draft-1"/);
  page.change('[data-taxonomy-label="draft-2"]', '新下層', 'input');
  page.change('[data-taxonomy-parent="draft-2"]', 'draft-1');
  assert.equal(f.api.rows(f.store.getState()).find(row => row.rowId === 'draft-2').semanticRole, 'cash');
  f.render().change('[data-taxonomy-role="draft-2"]', 'receivables');
  f.store.touch(); f.render();
  assert.equal(f.api.rows(f.store.getState()).find(row => row.rowId === 'draft-2').semanticRole, 'receivables');
  f.render().click('[data-action="save-taxonomy"]');
  const categories = f.saves[0].payload.categories;
  assert.equal(categories.find(row => row.label === '新上層').categoryId, 'draft-1');
  assert.equal(categories.find(row => row.label === '新下層').parentCategoryId, 'draft-1');
  assert.equal(categories.find(row => row.label === '新下層').semanticRole, 'receivables');
  assert.match(page.html, /沿用上層.*可以.*改/);
});

test('taxonomy parent removal names direct children, waits for confirmation, reparents and can be cancelled before saving', () => {
  const f = taxonomyReviewFixture();
  f.store.setTaxonomy({ revision: 2, categories: [
    { categoryId: 'g', label: '祖父', semanticRole: 'others' },
    { categoryId: 'p', label: '父分類', semanticRole: 'cash', parentCategoryId: 'g' },
    { categoryId: 'c', label: '受影響子分類', semanticRole: 'cash', parentCategoryId: 'p' },
    { categoryId: 'gc', label: '孫分類', semanticRole: 'cash', parentCategoryId: 'c' }
  ] });
  f.render().click('[data-taxonomy-remove="p"]');
  assert.ok(f.api.rows(f.store.getState()).some(row => row.categoryId === 'p'), 'first click is not deletion');
  assert.match(f.render().html, /受影響子分類.*祖父/);
  f.render().click('[data-action="cancel-taxonomy-remove"]');
  assert.ok(f.api.rows(f.store.getState()).some(row => row.categoryId === 'p'));
  f.render().click('[data-taxonomy-remove="p"]');
  f.render().click('[data-action="confirm-taxonomy-remove"]');
  const rows = f.api.rows(f.store.getState());
  assert.equal(rows.find(row => row.categoryId === 'c').parentCategoryId, 'g');
  assert.equal(rows.find(row => row.categoryId === 'gc').parentCategoryId, 'c');
  assert.ok(!rows.some(row => row.categoryId === 'p'));
  f.render().click('[data-action="reset-taxonomy"]');
  assert.equal(f.api.rows(f.store.getState()).find(row => row.categoryId === 'c').parentCategoryId, 'p');
});

test('taxonomy editor and apply dropdown show hierarchy and newly added labels receive focus', () => {
  const f = taxonomyReviewFixture(); f.store.setTaxonomy({ revision: 2, categories: classificationTree() });
  const html = f.render().html;
  assert.match(html, /data-taxonomy-depth="1"/);
  assert.match(html, /toggle-taxonomy-advanced"[^>]*data-focus-key=/);
  assert.match(html, /add-taxonomy-category"[^>]*data-focus-key=/);
  const dropdown = f.window.JetUi.accountMappingEditor.html(f.store.getState(), true);
  assert.match(dropdown, /option value="bank"[^>]*>[^<]*└[^<]*銀行存款/);
  f.render().click('[data-action="add-taxonomy-category"]');
  const page = f.render(); f.focusNow();
  assert.equal(page.dom.querySelector('[data-taxonomy-label="draft-1"]').focused, true);
});

test('new classification cards and legacy P default to subtree while older saved rules remain role based', () => {
  const f = taxonomyReviewFixture(), ui = f.window.JetUi;
  for (const type of ['accountSide', 'accountPair', 'specialAccountCategoryPair']) assert.equal(ui.newFilterRule(type).categorySelection, 'subtree');
  const presets = f.window.JetLegacyFilters.catalogue.conditions;
  assert.equal(presets.find(item => item.letter === 'P').categoryRules[0].categorySelection, 'subtree');
  f.store.setTaxonomy({ revision: 2, categories: classificationTree() });
  const old = { type: 'accountSide', drCr: 'debit', categoryMode: 'is', categoryIds: ['builtin.cash'] };
  const wire = f.filterApi.wire({ name: 'Old rule', rationale: 'Synthetic', groups: [{ rules: [old] }] });
  assert.equal(wire.groups[0].rules[0].categorySelection, undefined);
  assert.match(f.filterApi.summary(old, 0), /相同分類用途/);
});

test('classification summaries show actual expanded categories, any direction and deleted-category guidance', () => {
  const f = taxonomyReviewFixture(); f.store.setTaxonomy({ revision: 2, categories: classificationTree() });
  for (const [mode, includes, excludes] of [['subtree', ['現金', '銀行存款', '庫存現金'], ['其他同用途']],
    ['node', ['現金'], ['銀行存款', '庫存現金', '其他同用途']], ['role', ['現金', '銀行存款', '其他同用途'], ['庫存現金']]]) {
    const rule = { type: 'accountSide', drCr: 'any', categoryMode: 'is', categorySelection: mode, categoryIds: ['builtin.cash'] };
    const summary = f.filterApi.summary(rule, 0);
    assert.match(summary, /不限借貸.*實際納入分類：/);
    const actual = summary.split('實際納入分類：')[1];
    includes.forEach(label => assert.ok(actual.includes(label), mode + ' includes ' + label));
    excludes.forEach(label => assert.ok(!actual.includes(label), mode + ' excludes ' + label));
    assert.match(f.filterApi.subject(rule, 'account'), /value="any" selected/);
  }
  const removed = { type: 'accountSide', drCr: 'any', categoryMode: 'absent', categorySelection: 'subtree', categoryIds: ['custom.deleted-id'] };
  const summary = f.filterApi.summary(removed, 0);
  assert.match(summary, /已刪除的分類.*重新選擇/); assert.doesNotMatch(summary, /custom.deleted-id/);
  assert.match(f.filterApi.controls(removed, 0, 0), /已刪除的分類.*重新選擇/);
});

test('classification tree searches in place and restores collapsed branches after clearing search', () => {
  const f = taxonomyReviewFixture(), ui = f.window.JetUi;
  f.store.setTaxonomy({ revision: 2, categories: classificationTree() });
  const view = { search: '', collapsed: {} };
  assert.equal(typeof ui.taxonomyPickerHtml, 'function');
  function render() {
    const html = ui.taxonomyPickerHtml(f.store.getState(), [], 'categoryIds', '指定分類', view);
    const dom = taxonomyReviewDom(html);
    ui.bindTaxonomyPicker(dom, f.store.getState(), view);
    return dom;
  }
  let dom = render(); dom.querySelector('[data-category-toggle="builtin.cash"]').fire();
  assert.equal(dom.querySelector('[data-category-node="bank"]').hidden, true);
  const search = dom.querySelector('[data-category-search]'); search.value = '銀行'; search.fire('input');
  assert.equal(dom.querySelector('[data-category-node="bank"]').hidden, false);
  assert.equal(dom.querySelector('[data-category-node="unrelated"]').hidden, true);
  search.value = ''; search.fire('input');
  assert.equal(dom.querySelector('[data-category-node="bank"]').hidden, true);
  dom = render(); assert.equal(dom.querySelector('[data-category-node="bank"]').hidden, true);
});

function accountTreeReviewFixture(initialValues = ['0099']) {
  const f = taxonomyReviewFixture(), requests = [];
  f.store.setTaxonomy({ revision: 2, categories: classificationTree() });
  f.store.setImportResult('gl', { batchId: 'gl-1', columns: ['account'], rowCount: 3 });
  const rule = { type: 'fieldValue', field: 'accNum', operator: 'in', values: initialValues.slice() };
  f.store.setFilterDraft({ name: 'Synthetic accounts', rationale: 'Synthetic', groups: [{ rules: [rule] }] });
  f.window.JetApi.queryAccountMappingPage = payload => new Promise((resolve, reject) => requests.push({ payload, resolve, reject }));
  function render() {
    const html = f.window.JetUi.FilterValues.render(rule, f.store.getState(), 'account-rule');
    const dom = taxonomyReviewDom(html);
    f.window.JetUi.FilterValues.bind(dom, rule, f.store.getState(), () => f.store.setFilterDraft(f.store.getState().filter.draft));
    return { html, dom, click(selector) { const control = dom.querySelector(selector); assert.ok(control, selector + ' rendered'); control.fire(); },
      check(selector, checked = true) { const control = dom.querySelector(selector); assert.ok(control, selector + ' rendered'); control.checked = checked; control.fire('change'); },
      search(value) { const input = dom.querySelector('[data-account-tree-search]'); assert.ok(input); input.value = value; input.fire('input'); } };
  }
  return { ...f, rule, requests, render };
}

test('account-number tree keeps existing exact-list semantics and selects actual codes without losing leading zeros', async () => {
  const f = accountTreeReviewFixture();
  assert.match(f.render().html, /從科目樹選取/);
  f.render().click('[data-account-tree-category="builtin.cash"]');
  assert.equal(f.requests[0].payload.categoryId, 'builtin.cash'); assert.equal(f.requests[0].payload.pageSize, 100);
  f.requests[0].resolve({ rows: [{ accountCode: '0010', accountName: 'Synthetic bank', categoryId: 'bank' }], nextCursor: null }); await settle();
  f.render().check('[data-account-tree-code="0010"]');
  assert.deepEqual(plain(f.rule.values), ['0099', '0010']); assert.equal(f.rule.operator, 'in');
  f.render().check('[data-account-tree-code="0010"]');
  assert.deepEqual(plain(f.rule.values), ['0099', '0010']);
  f.render().check('[data-account-tree-code="0010"]', false);
  assert.deepEqual(plain(f.rule.values), ['0099']);
  f.rule.operator = 'notIn'; assert.match(f.render().html, /從科目樹選取/);
  f.rule.operator = 'contains'; assert.doesNotMatch(f.render().html, /data-account-tree-code|data-account-tree-category/);
});

test('account-tree category selection is atomic for server truncation and the existing combined value limit', async () => {
  for (const mode of ['truncated', 'merged-limit']) {
    const initial = mode === 'merged-limit' ? Array.from({ length: 98 }, (_, i) => 'OLD-' + i) : ['0099'];
    const f = accountTreeReviewFixture(initial);
    f.render().check('[data-account-tree-select-category="builtin.cash"]');
    assert.equal(f.requests.length, 1);
    f.requests[0].resolve({ rows: Array.from({ length: mode === 'truncated' ? 100 : 5 }, (_, i) =>
      ({ accountCode: 'NEW-' + i, accountName: 'Synthetic', categoryId: 'bank' })),
      nextCursor: mode === 'truncated' ? 'more' : null }); await settle();
    assert.deepEqual(plain(f.rule.values), initial, 'do not partially select a category');
    assert.match(f.render().html, /縮小.*範圍.*科目分類條件/);
  }
});

test('account-tree search remains category-scoped, failures are retryable, and later search wins', async () => {
  const f = accountTreeReviewFixture(); f.render().click('[data-account-tree-category="builtin.cash"]');
  f.requests[0].reject(new Error('Synthetic page failure')); await settle();
  assert.match(f.render().html, /Synthetic page failure/);
  f.render().click('[data-account-tree-retry]');
  assert.deepEqual(plain(f.requests[1].payload), plain(f.requests[0].payload));
  f.requests[1].resolve({ rows: [], nextCursor: null }); await settle();
  f.render().search('old'); f.render().search('new');
  assert.equal(f.requests[2].payload.search, 'old'); assert.equal(f.requests[3].payload.search, 'new');
  assert.equal(f.requests[3].payload.categoryId, 'builtin.cash');
  f.requests[3].resolve({ rows: [{ accountCode: 'NEW', accountName: 'new result', categoryId: 'bank' }], nextCursor: null }); await settle();
  f.requests[2].resolve({ rows: [{ accountCode: 'OLD', accountName: 'old result', categoryId: 'bank' }], nextCursor: null }); await settle();
  assert.match(f.render().html, /data-account-tree-code="NEW"/); assert.doesNotMatch(f.render().html, /data-account-tree-code="OLD"/);
});

test('late account-tree category selections cannot mutate a different project, changed source, or removed rule', async () => {
  for (const change of ['project', 'source', 'rule']) {
    const f = accountTreeReviewFixture(); f.render().check('[data-account-tree-select-category="builtin.cash"]');
    if (change === 'project') f.store.setProject({ projectId: 'another synthetic project' });
    if (change === 'source') f.store.setImportResult('gl', { batchId: 'gl-2', columns: ['account'], rowCount: 2 });
    if (change === 'rule') f.store.setFilterDraft({ name: 'Changed draft', groups: [] });
    f.requests[0].resolve({ rows: [{ accountCode: 'LATE', accountName: 'Synthetic', categoryId: 'bank' }], nextCursor: null }); await settle();
    assert.deepEqual(plain(f.rule.values), ['0099'], change);
  }
});

// Batch 7: exercise the actual editors and their change handlers; fixture values are synthetic.
function batch7Fixture() {
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const document = { querySelector: () => null, createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js', 'filter-legacy.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8').replace("  Ui.registerStep('filter', render);",
    '  window.batch7 = { bind, bindLegacyPicker, legacyPickerHtml, bindCompoundRule, compoundRuleHtml, ruleRowHtml, ruleSubjectHtml, ruleProblem, readBackHtml, toWireDraft, syncViewState, getView: () => viewState };'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'filter-vouchers.js'), 'utf8').replace('Ui.FilterVouchers = { summary: summary, mount: mount };',
    'Ui.FilterVouchers = { summary: summary, mount: mount, matchStatusHtml: matchStatusHtml };'), context);
  const store = window.JetStore, ui = window.JetUi, api = window.batch7;
  store.setProject({ projectId: 'batch7-synthetic', rocDateEnabled: true, periodStart: '2025-01-01', periodEnd: '2025-12-31' });
  api.syncViewState(store.getState());
  function editor(rule) {
    const state = store.getState(), html = ui.FilterValues.render(rule, state, 'batch7');
    const dom = batch7Dom(html); let changes = 0;
    ui.FilterValues.bind(dom, rule, state, () => changes++);
    return { html, dom, input(key, value) { const node = dom.querySelector('[data-value-key="' + key + '"]'); assert.ok(node, key + ' rendered'); node.value = value; node.fire(node.tagName === 'SELECT' || node.type === 'checkbox' ? 'change' : 'input'); }, changes: () => changes };
  }
  return { window, store, ui, api, editor, text: html => html.replace(/<[^>]+>/g, '') };
}

function batch7Dom(html) {
  const nodes = [];
  for (const tag of html.matchAll(/<(input|textarea|select|button|p|div|details|summary)\b([^>]*)>/g)) {
    const attrs = {};
    for (const attr of tag[2].matchAll(/([\w-]+)(?:="([^"]*)")?/g)) attrs[attr[1]] = attr[2] ?? '';
    const node = { tagName: tag[1].toUpperCase(), type: attrs.type || '', value: attrs.value || '', checked: Object.hasOwn(attrs, 'checked'),
      disabled: Object.hasOwn(attrs, 'disabled'), isConnected: true, hidden: false, innerHTML: '', textContent: '', dataset: {}, events: {},
      getAttribute: key => attrs[key] ?? null, hasAttribute: key => Object.hasOwn(attrs, key), setAttribute(key, val) { attrs[key] = val; }, removeAttribute(key) { delete attrs[key]; },
      addEventListener(name, fn) { this.events[name] = fn; }, fire(name = 'click') { assert.ok(this.events[name], JSON.stringify(attrs) + ' ' + name + ' bound'); this.events[name]({ target: this, currentTarget: this, preventDefault() {} }); },
      classList: { add() {}, remove() {}, toggle() {} }, focus() {}, getClientRects: () => [], style: {}, closest: () => dom };
    for (const [key, val] of Object.entries(attrs)) if (key.startsWith('data-')) node.dataset[key.slice(5).replace(/-([a-z])/g, (_, c) => c.toUpperCase())] = val;
    nodes.push(node);
  }
  function match(node, selector) {
    const attribute = /^\[([^=\]]+)(?:="?([^"\]]+)"?)?\]$/.exec(selector);
    return attribute ? node.hasAttribute(attribute[1]) && (attribute[2] === undefined || node.getAttribute(attribute[1]) === attribute[2]) : false;
  }
  const dom = { querySelector: selector => nodes.find(node => match(node, selector)) || null, querySelectorAll: selector => nodes.filter(node => match(node, selector)), innerHTML: html };
  // These parsed controls start with no element children; selected-date chips are populated after bind.
  nodes.forEach(node => { node.querySelector = () => null; node.querySelectorAll = () => []; });
  return dom;
}

test('batch7 list editors split the approved separators without splitting literal text lists or money grouping commas', () => {
  const f = batch7Fixture(), values = f.ui.FilterValues;
  for (const field of ['description', 'createBy', 'approveBy', 'accNum']) {
    const rule = { type: 'fieldValue', field, operator: field === 'description' ? 'contains' : 'in', values: [] };
    const page = f.editor(rule); page.input('values', '001,002，003、004\t005\n006');
    assert.deepEqual(plain(values.wire(rule, f.store.getState()).values), ['001', '002', '003', '004', '005', '006'], field);
    assert.ok(page.changes() > 0); assert.match(page.html, /換行.*逗號.*頓號.*Tab/);
  }
  for (const [field, text, expected] of [['description', 'Alpha,Beta\nGamma，Delta', ['Alpha,Beta', 'Gamma，Delta']], ['amount', '1,234.00\n5,678.00', ['1,234.00', '5,678.00']]]) {
    const rule = { type: 'fieldValue', field, operator: 'in', values: [] };
    const page = f.editor(rule); page.input('values', text);
    assert.deepEqual(plain(values.wire(rule, f.store.getState()).values), expected);
    assert.match(page.html, /每行一個/);
  }
});

test('batch7 trailing digit and month-day editors accept the same separators and preserve zero prefixes', () => {
  const f = batch7Fixture(), state = f.store.getState();
  const tail = { type: 'fieldValue', field: 'amount', operator: 'endsWithDigits', value: '' };
  f.editor(tail).input('value', '001，002、003\t004\n005');
  assert.equal(f.ui.FilterValues.wire(tail, state).value, '001,002,003,004,005');
  assert.equal(f.ui.FilterValues.problem(tail, state), '');
  const days = { type: 'fieldValue', field: 'postDate', operator: 'dayOfMonthIn', values: [] };
  f.editor(days).input('daysOfMonth', '1，2、3\t4\n31');
  assert.deepEqual(plain(f.ui.FilterValues.wire(days, state).values), ['1', '2', '3', '4', '31']);
});

test('batch7 existing keyword and tail cards normalize approved separators on wire without modifying literal text sets', () => {
  const f = batch7Fixture(), draft = { name: 'Older cards', rationale: 'Synthetic', groups: [{ rules: [
    { type: 'customKeywords', keywords: 'Alpha，Beta、Gamma\tDelta\nEpsilon' },
    { type: 'trailingDigits', keywords: '001，002、003\t004\n005' },
    { type: 'textSet', field: 'description', mode: 'exact', normalization: 'trim', values: ['Alpha,Beta', 'Gamma，Delta'] }
  ] }] };
  const rules = f.api.toWireDraft(draft).groups[0].rules;
  assert.equal(rules[0].keywords, 'Alpha,Beta,Gamma,Delta,Epsilon');
  assert.equal(rules[1].keywords, '001,002,003,004,005');
  assert.deepEqual(plain(rules[2].values), ['Alpha,Beta', 'Gamma，Delta']);
});

test('batch7 single-value comparisons retain their literal operand and warn about multiline input', () => {
  const f = batch7Fixture();
  for (const operator of ['startsWith', 'notStartsWith', 'endsWith', 'notEndsWith']) {
    const rule = { type: 'fieldValue', field: 'description', operator, value: 'Alpha\nBeta' };
    assert.equal(f.ui.FilterValues.wire(rule, f.store.getState()).value, 'Alpha\nBeta');
    assert.match(f.editor(rule).html, /只.*一個值.*多行|多行.*只.*一個值/);
    assert.equal(f.ui.FilterValues.problem(rule, f.store.getState()), '', 'reminder does not invent a new blocker');
  }
});

test('batch7 filter dates use GL formats and the project ROC setting without changing month-day numbers', () => {
  const f = batch7Fixture(), state = f.store.getState(), value = f.ui.FilterValues;
  const rule = { type: 'fieldValue', field: 'postDate', operator: 'in', values: ['2025/6/1', '2025.6.2', '20250603', '114/6/4', '1140605', '45814'] };
  assert.deepEqual(plain(value.wire(rule, state).values), ['2025-06-01', '2025-06-02', '2025-06-03', '2025-06-04', '2025-06-05', '2025-06-06']);
  assert.equal(value.problem(rule, state), '');
  state.project.rocDateEnabled = false;
  const disabled = { ...rule, values: ['114/6/4'] };
  assert.match(value.problem(disabled, state), /日期/);
  assert.equal(value.wire(disabled, state).values[0], '114/6/4');
  assert.deepEqual(plain(value.wire({ ...rule, operator: 'dayOfMonthIn', values: ['1', '31'] }, state).values), ['1', '31']);
  assert.match(f.editor({ ...rule, values: [] }).html, /yyyy\/M\/d|2025\/8\/1/);
});

test('batch7 credit voucher children inherit credit and personnel/account bracket fields default to exact lists', () => {
  const f = batch7Fixture();
  for (const [type, choice, expected] of [['voucher', 'type:accountSide', { drCr: 'credit' }], ['group', 'field:approveBy', { operator: 'in', field: 'approveBy' }], ['group', 'field:createBy', { operator: 'in', field: 'createBy' }], ['group', 'field:accNum', { operator: 'in', field: 'accNum' }]]) {
    const rule = { type, side: 'credit', quantifier: 'any', rules: [] }, draft = { groups: [{ rules: [rule] }] };
    f.store.setFilterDraft(draft);
    const dom = batch7Dom(f.api.compoundRuleHtml(rule, 0, 0, false, draft.groups[0]));
    f.api.bindCompoundRule(dom, draft.groups[0].rules, 0, 0);
    dom.querySelector('[data-child-kind]').value = choice; dom.querySelector('[data-child-add]').fire();
    for (const [key, value] of Object.entries(expected)) assert.equal(rule.rules[0][key], value, type + ' ' + choice);
  }
});

test('batch7 same-row bracket and voucher child menus do not offer voucher-wide absent classification', () => {
  const f = batch7Fixture(), child = { type: 'accountSide', drCr: 'credit', categoryMode: 'is', categoryIds: [] };
  for (const type of ['group', 'voucher']) {
    const rule = { type, side: 'credit', quantifier: 'any', rules: [child] };
    assert.doesNotMatch(f.api.compoundRuleHtml(rule, 0, 0, false, { rules: [rule] }), /option value="absent"/);
  }
  assert.match(f.api.ruleRowHtml(child, { rules: [child] }, 0, 0, false), /option value="absent"/);
});

test('batch7 unavailable saved risk and deleted-field selections show selected explanatory placeholders', () => {
  const f = batch7Fixture();
  for (const key of ['nonAuthorizedPreparer', 'unexpectedAccountPair']) {
    const html = f.api.ruleSubjectHtml({ type: 'prescreen', prescreenKey: key }, 'pattern');
    assert.match(html, new RegExp('<option[^>]*value="prescreen:' + key + '"[^>]*selected[^>]*>[^<]*(?:名單|科目配對)'));
  }
  assert.match(f.ui.FilterValues.fieldOptionsHtml(f.store.getState(), 'deleted-rde', []), /<option[^>]*value="deleted-rde"[^>]*selected[^>]*>[^<]*(?:未配對|重新選擇)/);
});

test('batch7 negative absolute money warns without blocking while invalid amount formats are caught before sending', () => {
  const f = batch7Fixture(), values = f.ui.FilterValues, state = f.store.getState();
  const negative = { type: 'fieldValue', field: 'amount', operator: 'lessThan', amountBasis: 'absolute', value: '-10.50' };
  assert.equal(values.problem(negative, state), '');
  assert.match(f.editor(negative).html, /絕對值.*負數|負數.*絕對值/);
  for (const value of ['not-money', '1e3', '$12', '12.3.4']) assert.match(values.problem({ ...negative, value }, state), /金額/);
  for (const value of ['-', '1,234.50', '12,34', '12-', '+12.50']) assert.equal(values.problem({ ...negative, value }, state), '', value);
  assert.match(values.problem({ ...negative, operator: 'between', from: '2', to: '1' }, state), /起點|上限|區間/);
});

test('batch7 frequency thresholds are nonnegative integers with a required ordered upper bound', () => {
  const f = batch7Fixture(), base = { type: 'entityFrequency', field: 'approveBy', countUnit: 'vouchers', countOperator: 'between', countFrom: '1', countTo: '2' };
  assert.equal(f.api.ruleProblem(base), null);
  for (const patch of [{ countFrom: '' }, { countFrom: '-1' }, { countFrom: '1.5' }, { countFrom: '2147483648' }, { countTo: '' }, { countTo: '0' }]) {
    assert.ok(f.api.ruleProblem({ ...base, ...patch }), JSON.stringify(patch));
  }
  for (const countOperator of ['lessThanOrEqual', 'greaterThanOrEqual']) assert.equal(f.api.ruleProblem({ ...base, countOperator, countFrom: '0', countTo: '' }), null);
});

test('batch7 contains keyword limit counts precisely the distinct values that the wire sends', () => {
  const f = batch7Fixture(), values = f.ui.FilterValues, state = f.store.getState();
  const list = Array.from({ length: 50 }, (_, i) => ['Key' + i, 'key' + i]).flat();
  const rule = { type: 'fieldValue', field: 'description', operator: 'contains', values: [...list, 'EXTRA'] };
  assert.equal(values.wire(rule, state).values.length, 101);
  assert.match(values.problem(rule, state), /100/);
  rule.values = [...list, ' Key0 ', 'Key0'];
  assert.equal(values.wire(rule, state).values.length, 100); assert.equal(values.problem(rule, state), '');
});

test('batch7 voucher match label changes without altering hit flags, hit count or reference labels', () => {
  const f = batch7Fixture(), ui = f.ui.FilterVouchers;
  const row = { isHit: true, matchDescription: '第 1 組：傳票條件成立' }, before = plain(row);
  assert.match(ui.matchStatusHtml(row), /<strong>傳票條件成立<\/strong>/); assert.deepEqual(plain(row), before);
  assert.match(ui.matchStatusHtml({ isHit: true, matchDescription: '第 1 組第 1 條：符合欄位條件' }), /<strong>符合條件的分錄<\/strong>/);
  assert.match(ui.matchStatusHtml({ isHit: false, matchDescription: '同張傳票的其他分錄' }), /<strong>參考分錄<\/strong>/);
  assert.match(ui.summary({ count: 3, voucherCount: 1 }), /符合條件：3 筆分錄（1 張傳票）/);
});

test('batch7 KCT I adds the shared nonbusiness predicate while old weekend-or-holiday AST remains untouched and honestly named', () => {
  const f = kctFixture(), draft = f.toggle('I');
  const wire = plain(f.api.toWireDraft(draft));
  assert.equal(wire.groups[0].rules[0].type, 'group');
  assert.deepEqual(wire.groups[0].rules[0].rules.map(rule => [rule.type, rule.field, rule.operator]), [['fieldValue', 'postDate', 'isNonBusinessDay']]);
  assert.match(f.text(f.api.readBackHtml(draft)), /非營業日（排除補班日）/);
  const older = { name: 'Older synthetic', rationale: 'Preserve saved meaning', __preserveJoins: true, groups: [{ rules: [{ type: 'group', join: 'AND', rules: [
    { type: 'prescreen', prescreenKey: 'weekendPosting', join: 'AND' }, { type: 'prescreen', prescreenKey: 'holidayPosting', join: 'OR' } ] }] }] };
  const before = plain(older); const readback = f.text(f.api.readBackHtml(older));
  assert.match(readback, /週末過帳.*或.*假日過帳/); assert.doesNotMatch(readback, /非營業日/);
  assert.deepEqual(plain(older), before);
  assert.deepEqual(plain(f.api.toWireDraft(older)).groups[0].rules[0].rules, before.groups[0].rules[0].rules);
});

test('batch7 saved readback uses the surviving group join when an empty middle group is omitted from the wire', () => {
  const f = batch7Fixture(), draft = { name: 'Join fixture', rationale: 'Synthetic', __preserveJoins: true, groups: [
    { join: 'AND', rules: [{ type: 'manualAuto', isManual: 'true' }] }, { join: 'OR', rules: [] },
    { join: 'AND', rules: [{ type: 'drCrOnly', drCr: 'credit' }] } ] };
  assert.equal(f.api.toWireDraft(draft).groups.length, 2);
  assert.equal(f.api.toWireDraft(draft).groups[1].join, 'AND');
  assert.match(f.text(f.api.readBackHtml(draft)), /人工分錄 且 僅貸方/);
  assert.doesNotMatch(f.text(f.api.readBackHtml(draft)), / 或 /);
});

function batch7ControlDom(lists = {}) {
  const nodes = new Map();
  function node(selector) {
    if (!nodes.has(selector)) nodes.set(selector, { value: '', dataset: {}, hidden: true, innerHTML: '', textContent: '', open: false, events: {}, parentNode: null,
      getAttribute(name) { return name === 'data-gi' ? this.dataset.gi : null; }, setAttribute() {}, removeAttribute() {}, classList: { add() {}, remove() {}, toggle() {} }, focus() {},
      addEventListener(name, fn) { this.events[name] = fn; }, fire(name = 'change') { assert.ok(this.events[name], selector + ' ' + name + ' bound'); this.events[name]({ target: this, currentTarget: this, preventDefault() {} }); },
      querySelector: node, querySelectorAll: () => [] });
    return nodes.get(selector);
  }
  return { node, querySelector: node, querySelectorAll: selector => lists[selector] || [] };
}

test('batch7 legacy U personnel choice is applied by the real picker and approver values reach the wire unchanged', () => {
  const f = batch7Fixture(); f.api.getView().legacyLetter = 'U';
  const dom = batch7ControlDom(); f.api.bindLegacyPicker(dom);
  for (const field of ['createBy', 'approveBy']) {
    dom.node('[data-legacy-person]').value = field;
    dom.node('[data-action="add-legacy-rule"]').fire('click');
    const draft = f.store.getState().filter.draft, rule = draft.groups[0].rules.at(-1);
    assert.equal(rule.field, field); assert.equal(rule.operator, 'in');
    f.editor(rule).input('values', 'PERSON-01，PERSON-02');
    assert.deepEqual(plain(f.api.toWireDraft(draft).groups[0].rules.at(-1).values), ['PERSON-01', 'PERSON-02']);
  }
});

test('batch7 group and between-group dropdown changes update only the intended saved joins', () => {
  const f = batch7Fixture(), draft = { name: 'Join controls', rationale: 'Synthetic', __preserveJoins: true, groups: [
    { join: 'AND', rules: [{ type: 'manualAuto', isManual: 'true', join: 'AND' }, { type: 'drCrOnly', drCr: 'debit', join: 'AND' }] },
    { join: 'OR', rules: [{ type: 'drCrOnly', drCr: 'credit', join: 'AND' }] },
    { join: 'AND', rules: [{ type: 'manualAuto', isManual: 'false', join: 'AND' }] } ] };
  f.store.setFilterDraft(draft);
  const dom = batch7ControlDom(), combinator = dom.node('combinator'), connector = dom.node('connector');
  combinator.dataset.gi = '0'; connector.dataset.setJoinIndex = '1';
  dom.querySelectorAll = selector => selector === '[data-set-combinator]' ? [combinator] : selector === '[data-set-join]' ? [connector] : [];
  f.api.bind(dom);
  combinator.value = 'OR'; combinator.fire();
  assert.deepEqual(plain(draft.groups[0].rules.map(rule => rule.join)), ['OR', 'OR']);
  assert.equal(draft.groups[1].rules[0].join, 'AND');
  connector.value = 'AND'; connector.fire();
  assert.deepEqual(plain(draft.groups.map(group => group.join)), ['AND', 'AND', 'AND']);
  connector.value = 'OR'; connector.fire();
  assert.deepEqual(plain(f.api.toWireDraft(draft).groups.map(group => group.join)), ['AND', 'OR', 'AND']);
  assert.match(f.text(f.api.readBackHtml(draft)), / 或 /); assert.match(f.text(f.api.readBackHtml(draft)), / 且 /);
});

test('batch7 unexpected account pair uses the same revenue and any-counterpart prerequisites as step four', () => {
  const f = batch7Fixture(), rule = { type: 'prescreen', prescreenKey: 'unexpectedAccountPair' };
  for (const [mapping, reason] of [
    [{ hasRevenue: false, hasCounterpart: true }, /收入|Revenue/],
    [{ hasRevenue: true, hasCounterpart: false }, /應收.*現金.*預收/] ]) {
    f.store.setAccountMappingState(mapping);
    assert.match(f.api.ruleProblem(rule)?.message || '', reason);
    assert.match(f.api.ruleSubjectHtml(rule, 'pattern'), /<option[^>]*value="prescreen:unexpectedAccountPair"[^>]*selected/);
    f.api.getView().legacyLetter = 'C';
    assert.match(f.api.legacyPickerHtml(), /data-action="add-legacy-rule"[^>]*disabled/);
  }
  // One of the three counterpart roles is sufficient; no new all-three rule is invented.
  f.store.setAccountMappingState({ hasRevenue: true, hasCounterpart: true });
  assert.equal(f.api.ruleProblem(rule), null);
  assert.doesNotMatch(f.api.legacyPickerHtml(), /data-action="add-legacy-rule"[^>]*disabled/);
});

// The backend consumes this same hand-written JSON; neither side derives expected text from the other renderer.
for (const entry of JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/batch7-filter-readback.json'), 'utf8'))) {
  test('batch7 shared full condition readback: ' + entry.id, () => {
    const f = batch7Fixture(), scenario = { name: entry.id, rationale: 'Synthetic shared readback', __preserveJoins: true, ...plain(entry.scenario) };
    f.store.getState().mapping.gl.committed = { options: { rdeFields: plain(entry.rdeFields) } };
    f.store.setSavedScenarios([scenario]);
    assert.equal(f.text(f.api.readBackHtml(f.store.getState().filter.savedScenarios[0], false)).replace(/^篩選條件：/, ''), entry.expected);
  });
}

function batch8Fixture() {
  const requests = [], elements = new Map();
  const document = { readyState: 'loading', addEventListener() {}, querySelector: selector => elements.get(selector) || null,
    createElement() { return { textContent: '', get innerHTML() { return String(this.textContent).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); } }; } };
  const window = { setTimeout, clearTimeout, addEventListener() {}, JetFocus: { defer() {} }, JetApi: {} };
  for (const action of ['validateRun', 'exportValidationArtifacts', 'exportAccountMappingTemplate', 'exportPrescreenReport', 'hostOpenFolder', 'queryDataPreview', 'querySourceQualityPage', 'prescreenRun']) {
    window.JetApi[action] = payload => new Promise((resolve, reject) => requests.push({ action, payload, resolve, reject }));
  }
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'account-mapping-editor.js', 'filter-values.js', 'filter-legacy.js', 'overview-bi.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const modules = [
    ['validate', 'window.batch8Validation = { bind, detailView, VALIDATION_ITEMS, PRESCREEN_ITEMS, NULL_PRESCREEN_ITEMS, LOAD_MORE_SPECS, detailHtml, validationCardHtml, validationArtifactsHtml, validationOutputStatusHtml, prescreenArtifactHtml, prescreenCardHtml, populationSummaryHtml, statsBarHtml, pairCountStatus, hitCountStatus };'],
    ['filter', 'window.batch8Filter = { ruleControlsHtml, ruleSummaryLabel, readBackHtml, toWireDraft, compoundRuleHtml, scenarioBuilderHtml };'],
    ['mapping', 'window.batch8Mapping = { classicEditSection, summarySection, mappingPreflightHtml };'],
    ['import', 'window.batch8Import = { emptyFaceHtml, wizardBannerHtml, previewTableHtml, datasetCard, getWizard: () => wizard };'],
    ['create', 'window.batch8Create = { render, operatorNoticeHtml };']
  ];
  for (const [step, exposure] of modules) {
    const source = fs.readFileSync(path.join(root, 'steps/' + step + '-step.js'), 'utf8');
    const marker = "  Ui.registerStep('" + step + "', render);";
    assert.ok(source.includes(marker), step + ' registration seam');
    vm.runInContext(source.replace(marker, '  ' + exposure + '\n' + marker), context);
  }
  vm.runInContext(fs.readFileSync(path.join(root, 'app.js'), 'utf8').replace("  if (document.readyState === 'loading') {",
    "  window.batch8App = { stepSummary, renderIdentityBadge, overviewStepFactsHtml, overviewPopulationHtml };\n  if (document.readyState === 'loading') {"), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'data-preview.js'), 'utf8').replace('  Ui.initDataPreview = initDataPreview;',
    '  window.batch8Preview = { displayCell, renderNote, setup: function (aside, body, note) { elAside = aside; elBody = body; elNote = note; } };\n  Ui.initDataPreview = initDataPreview;'), context);
  const store = window.JetStore, ui = window.JetUi;
  store.setProject({ projectId: 'batch8-synthetic', entityName: 'Synthetic', operatorId: 'CREATOR', lastPeriodStart: '2025-12-31', periodStart: '2025-01-01', periodEnd: '2025-12-31' });
  store.getState().view = 'workflow';
  const previewDom = batch7ControlDom();
  window.batch8Preview.setup(previewDom.node('aside'), previewDom.node('body'), previewDom.node('note'));
  function controls(html = '') {
    const dom = batch7Dom(html + '<button data-action="run-validate"></button><button data-action="run-prescreen"></button>');
    window.batch8Validation.bind(dom, true);
    return { dom, click(selector) { const control = dom.querySelector(selector); assert.ok(control, selector + ' rendered'); control.fire(); } };
  }
  return { store, ui, window, requests, elements, controls, previewDom, v: window.batch8Validation, filter: window.batch8Filter,
    mapping: window.batch8Mapping, importer: window.batch8Import, app: window.batch8App, text: html => html.replace(/<[^>]+>/g, '') };
}

function batch8ValidationData(runId = 'v8') {
  const data = validationFixture();
  data.resultRef = { runId, generatedUtc: '2026-01-01T00:00:00Z' };
  data.stats = { glRowCount: 80, voucherCount: 12, totalDebit: 1250, totalCredit: 1250, net: 0, periodStart: '2025-01-01', periodEnd: '2025-12-31' };
  data.populationSummary = { raw: { rowCount: 100 }, effective: { rowCount: 80 }, excluded: { byPeriodCount: 15, byPostingStatusCount: 5 } };
  data.sourceQuality = { findingCount: 2, sampleRows: [{ category: 'nullPostDate', sourceLabel: 'Synthetic', sourceRowNumber: 7, documentNumber: 'JV-7', accountCode: '1001', description: 'Synthetic row' }] };
  return data;
}

function batch8Artifacts(runId = 'v8', stamp = '') {
  return ['validationReport', 'infReport'].map((kind, i) => ({ artifactId: 'b8-' + kind + stamp, kind, fileName: kind + '.xlsx', fileState: 'present', stale: false,
    generatedUtc: '2026-01-01T00:00:00Z', sizeBytes: 2000 + i, sourceRef: { validationRunId: runId } }));
}

test('batch8 validation interpretation restores the two user sentences and RDE guidance does not add an execution gate', async () => {
  const f = batch8Fixture(), items = f.v.VALIDATION_ITEMS;
  assert.match(items.find(item => item.title === '完整性測試').desc, /金額不符代表總帳母體可能缺漏/);
  // 2026-10-05 W1 裁定：照使用者原件用「、」。
  assert.match(items.find(item => item.title === '借貸不平測試').desc, /借貸不平代表傳票編號、金額欄位可能有誤/);
  assert.match(items.find(item => item.title === '資料可靠性測試').desc, /先確認.*(?:RDE|攸關資料元素).*可靠.*(?:再|後).*高風險/);
  f.store.setLastRun('validate', batch8ValidationData());
  assert.doesNotMatch(f.v.prescreenCardHtml(null, f.store.getState().lastRuns.validate, true, f.store.getState(), { isEligible: true }), /data-action="run-prescreen"[^>]*disabled/);
  f.controls().click('[data-action="run-prescreen"]');
  assert.equal(f.requests[0].action, 'prescreenRun', 'no new reliability-confirmation blocker');
  f.requests[0].reject(new Error('Synthetic end of request')); await settle();
});

test('batch8 completeness detail displays both authoritative control totals even when they agree', () => {
  const f = batch8Fixture(), data = batch8ValidationData();
  data.completenessTest = { diffAccountCount: 0, diffAccounts: [], eligibility: { isEligible: true, reason: null, warning: null }, partA: {
    eligibleSource: { rowCount: 123, totalDebit: 4567.89, totalCredit: 4567.89 },
    effectiveTarget: { rowCount: 123, totalDebit: 4567.89, totalCredit: 4567.89 }, rowCountMatch: true, amountMatch: true } };
  const item = f.v.VALIDATION_ITEMS.find(entry => entry.title === '完整性測試');
  const html = f.v.detailHtml(item.detail(data));
  // 2026-10-05 V2 裁定：照實說明兩欄都是 JET 算的數字，不比對來源檔本身；欄名逐字核對。
  assert.match(html, /確認 JET 存下的分錄沒有少，也沒有重複。/); assert.match(html, /這一項不和來源檔自己的合計比對。/);
  assert.match(html, /<th>確認配對時算出<\/th>/); assert.match(html, /<th>存下後重新計算<\/th>/);
  assert.ok((html.match(/123/g) || []).length >= 2); assert.ok((html.match(/4,567\.89/g) || []).length >= 4);
  data.completenessTest.partA = { eligibleSource: null, effectiveTarget: null, rowCountMatch: null, amountMatch: null };
  const missing = f.v.detailHtml(item.detail(data));
  assert.match(missing, /尚無|無法核對|—/); assert.doesNotMatch(missing, /NaN/);
});

test('batch8 an older case without import control totals remains eligible and does not ask for a futile validation rerun', () => {
  const f = batch8Fixture(), data = batch8ValidationData();
  data.completenessTest = { diffAccountCount: 0, diffAccounts: [],
    eligibility: { isEligible: true, reason: null, warning: '舊案缺少匯入控制總數，可繼續操作。' },
    partA: { eligibleSource: null, effectiveTarget: null, rowCountMatch: null, amountMatch: null } };
  f.store.setLastRun('validate', data);
  const item = f.v.VALIDATION_ITEMS.find(entry => entry.title === '完整性測試');
  const html = f.v.detailHtml(item.detail(data));
  assert.match(html, /—/); assert.match(html, /不代表 0/);
  assert.doesNotMatch(html, /請重新執行|重新匯入/);
  const eligibility = f.ui.completenessEligibility(data);
  assert.equal(eligibility.isEligible, true); assert.equal(eligibility.warning, data.completenessTest.eligibility.warning);
  assert.doesNotMatch(f.v.prescreenCardHtml(null, data, true, f.store.getState(), eligibility), /data-action="run-prescreen"[^>]*disabled/);
});

test('batch8 tiny nonzero differences stay visible on summaries, first pages and subsequent pages without changing ordinary money', () => {
  const f = batch8Fixture(), data = batch8ValidationData();
  assert.equal(f.ui.money(1234.5), '1,234.50'); assert.equal(f.ui.money(0), '0.00');
  data.stats.net = -0.003;
  data.completenessTest.diffAccounts = [{ accountCode: '1001', tbAmount: 1, glAmount: 0.997, diff: 0.003 }];
  data.docBalanceTest = { unbalancedDocumentCount: 1, unbalancedDocuments: [{ documentNumber: 'JV-1', debit: 0.997, credit: 1, diff: -0.003 }] };
  for (const title of ['完整性測試', '借貸不平測試']) {
    const html = f.v.detailHtml(f.v.VALIDATION_ITEMS.find(item => item.title === title).detail(data));
    assert.match(html, /不足 0\.01/); assert.match(html, /完整.*驗證報告|驗證報告.*完整/);
  }
  assert.match(f.v.statsBarHtml(data), /不足 0\.01/);
  assert.equal(f.v.LOAD_MORE_SPECS.completeness.columns[4]({ diff: 0.003 }), '不足 0.01');
  assert.equal(f.v.LOAD_MORE_SPECS.docBalance.columns[3]({ diff: -0.003 }), '負值，不足 0.01');
  assert.equal(f.v.LOAD_MORE_SPECS.docBalance.columns[3]({ diff: 0 }), '0.00');
});

test('batch8 overview uses the same tiny net display while ordinary debit and credit totals retain two decimals', () => {
  const f = batch8Fixture(), data = batch8ValidationData(); data.stats.net = -0.003;
  f.store.setLastRun('validate', data);
  const html = f.app.overviewPopulationHtml(f.store.getState());
  assert.match(html, /借貸淨額 負值，不足 0\.01/);
  assert.equal((html.match(/1,250\.00/g) || []).length, 2);
  assert.doesNotMatch(html, /借貸淨額 -?0\.00/);
  assert.equal(data.stats.net, -0.003);
});

test('batch8 empty remedies are omitted and all blank-source category labels are consistent', () => {
  const f = batch8Fixture();
  assert.doesNotMatch(f.v.detailHtml({ kind: 'reason', text: 'Synthetic reason', remedy: null }), /rule-detail__remedy/);
  assert.match(f.v.detailHtml({ kind: 'reason', text: 'Synthetic reason', remedy: 'Synthetic next step' }), /Synthetic next step/);
  const data = batch8ValidationData(), item = f.v.VALIDATION_ITEMS.find(item => item.title === '總帳入帳日空白');
  assert.equal(item.detail(data).rows[0][0], '總帳入帳日空白');
  assert.equal(f.v.LOAD_MORE_SPECS.sourceQuality.columns[0]({ category: 'nullPostDate' }), '總帳入帳日空白');
  for (const [key, label] of [['nullDescription', '空白摘要'], ['nullDocument', '空白傳票號碼'], ['nullAccount', '空白科目編號']]) {
    assert.equal(f.v.LOAD_MORE_SPECS['null-' + key].columns[4]({}), label);
  }
  assert.deepEqual(plain(item.detail(data).columns.slice(3, 5)), ['傳票號碼', '科目編號']);
});

test('batch8 blank preparers retain separate rows while showing a readable label and low-frequency status uses only its new count', () => {
  const f = batch8Fixture();
  const blank = { createdBy: '', entryCount: 1, debitTotal: 1, creditTotal: 0, manualCount: 1 };
  const spaces = { ...blank, createdBy: '　 ' }, literal = { ...blank, createdBy: '（空白）' };
  const p = { creatorSummary: { creators: [blank, spaces, literal] }, rareAccounts: { distinctAccountCount: 500, lowFrequencyAccountCount: 3, accounts: [{ accountCode: '101', entryCount: 2 }] } };
  const creators = f.v.PRESCREEN_ITEMS.find(item => item.title === '依分錄編製者彙總').detail(p).rows;
  assert.equal(creators.length, 3); assert.deepEqual(plain(creators.map(row => row[0])), ['（空白）', '（空白）', '（空白）']);
  assert.equal(blank.createdBy, ''); assert.equal(spaces.createdBy, '　 '); assert.equal(literal.createdBy, '（空白）');
  const rare = f.v.PRESCREEN_ITEMS.find(item => item.title === '較少使用之科目');
  assert.match(rare.status(p).text, /3.*(?:低頻|較少使用)/); assert.doesNotMatch(rare.status(p).text, /500/);
  delete p.rareAccounts.lowFrequencyAccountCount;
  assert.match(rare.status(p).text, /未知|尚無|重新執行/); assert.doesNotMatch(rare.status(p).text, /0 個|500/);
  p.rareAccounts.lowFrequencyAccountCount = 0; assert.match(rare.status(p).text, /0/);
});

test('batch8 concentration chart labels blank preparers without merging them with the same literal person name', () => {
  const f = batch8Fixture(), top = [
    { createdBy: '', entryCount: 2, manualCount: 1, cumulativePct: 20 },
    { createdBy: '　 ', entryCount: 3, manualCount: 1, cumulativePct: 50 },
    { createdBy: '（空白）', entryCount: 5, manualCount: 2, cumulativePct: 100 }
  ];
  const before = plain(top), option = f.window.JetOverviewBi.options.preparers({ top, othersEntryCount: 0 });
  assert.deepEqual(plain(option.xAxis.data), ['（空白）', '（空白）', '（空白）']);
  assert.deepEqual(plain(option.series[0].data), [2, 3, 5]);
  assert.deepEqual(plain(top), before, 'display only, preserve raw grouping keys');
});

test('batch8 null posting and hit counts never claim a measured zero or an all-clear result', () => {
  const f = batch8Fixture();
  assert.doesNotMatch(JSON.stringify(f.v.hitCountStatus({ count: null })), /未發現/);
  const pair = f.v.pairCountStatus({ postingCount: null, approvalCount: 2 });
  assert.match(pair.text, /過帳.*(?:—|未知|未執行)/); assert.match(pair.text, /核准 2/);
  assert.doesNotMatch(pair.text, /過帳 0/);
  assert.equal(f.v.hitCountStatus({ count: 0 }).text, '未發現');
});

test('batch8 population summary keeps the backend counts and the real excluded button sends the bounded query action', async () => {
  const f = batch8Fixture(), data = batch8ValidationData();
  const html = f.v.populationSummaryHtml(data), page = f.controls(html);
  for (const count of [100, 80, 15, 5]) assert.match(html, new RegExp('>' + count + '<'));
  assert.match(html, /不在查核期間|日期不符/); assert.match(html, /過帳狀態不符/);
  page.click('[data-action="preview-excluded-entries"]');
  assert.deepEqual(plain(f.requests[0].payload), { dataset: 'glExcludedEntries' });
  f.requests[0].resolve({ columns: [], rows: [], totalCount: 0, stats: { excludedByPeriodCount: 15, excludedByPostingStatusCount: 5 } }); await settle();
  assert.match(f.previewDom.node('note').innerHTML, /過帳狀態不符/);
  assert.equal(f.window.batch8Preview.displayCell('glExcludedEntries', 'exclusionReason', 'postingStatus'), '過帳狀態不符');
  const request = f.v.LOAD_MORE_SPECS.sourceQuality.fetchPage('cursor-2', { key: 'sourceRowNumber', direction: 'asc' }, 'JV');
  assert.deepEqual(plain(f.requests[1].payload), { cursor: 'cursor-2', pageSize: 200, sort: { key: 'sourceRowNumber', direction: 'asc' }, search: 'JV' });
  f.requests[1].resolve({ rows: [], nextCursor: null }); await request;
});

test('batch8 report buttons execute export, preserve failure, retry, show the catalog and open the actual artifact folder', async () => {
  const f = batch8Fixture(), data = batch8ValidationData(); f.store.setLastRun('validate', data);
  let page = f.controls(f.v.validationArtifactsHtml(f.store.getState(), data));
  page.click('[data-action="export-validation-artifacts"]');
  assert.equal(f.requests[0].action, 'exportValidationArtifacts'); assert.deepEqual(plain(f.requests[0].payload), { runId: 'v8' });
  f.requests[0].reject(new Error('Synthetic locked output')); await settle();
  assert.equal(f.store.getState().lastRuns.validate, data);
  assert.match(f.v.validationOutputStatusHtml('reports'), /Synthetic locked output.*重試/);
  assert.equal(f.store.getState().reportArtifacts.length, 0);
  page = f.controls(f.v.validationArtifactsHtml(f.store.getState(), data)); page.click('[data-action="export-validation-artifacts"]');
  const artifacts = batch8Artifacts(); f.requests[1].resolve({ artifacts, reportArtifacts: artifacts }); await settle();
  assert.deepEqual(plain(f.store.getState().reportArtifacts), artifacts);
  const html = f.v.validationArtifactsHtml(f.store.getState(), data);
  assert.match(html, /重新產生兩份報告/); assert.match(html, /validationReport\.xlsx/); assert.match(html, /infReport\.xlsx/);
  assert.match(f.store.getState().messages.map(message => message.text).join('\n'), /案件資料夾/);
  page = f.controls(html); page.click('[data-open-artifact="b8-validationReport"]');
  assert.equal(f.requests[2].action, 'hostOpenFolder'); assert.deepEqual(plain(f.requests[2].payload), { artifactId: 'b8-validationReport' });
  f.requests[2].resolve({ opened: true }); await settle();
});

test('batch8 report callbacks cannot overwrite a reopened case or a newer validation output', async () => {
  for (const change of ['other-project', 'reload-same-project', 'new-validation']) {
    const f = batch8Fixture(), old = batch8ValidationData(); f.store.setLastRun('validate', old);
    f.controls(f.v.validationArtifactsHtml(f.store.getState(), old)).click('[data-action="export-validation-artifacts"]');
    if (change !== 'new-validation') f.store.setProject({ projectId: change === 'other-project' ? 'another-synthetic' : 'batch8-synthetic' });
    const fresh = batch8ValidationData('fresh-run'); f.store.setLastRun('validate', fresh);
    const artifacts = batch8Artifacts('fresh-run', '-fresh'); f.store.setReportArtifacts(artifacts);
    f.store.setValidationOutput('reports', { runId: 'fresh-run', status: 'ready', message: 'Fresh output' });
    f.requests[0].resolve({ artifacts: batch8Artifacts(), reportArtifacts: batch8Artifacts() }); await settle();
    assert.deepEqual(plain(f.store.getState().reportArtifacts), artifacts, change);
    assert.equal(f.store.getState().validationOutput.reports.message, 'Fresh output', change);
  }
});

test('batch8 automatic report output never starts the following template action after changing the project scope', async () => {
  for (const id of ['another-synthetic', 'batch8-synthetic']) {
    const f = batch8Fixture();
    f.controls().click('[data-action="run-validate"]');
    assert.equal(f.requests[0].action, 'validateRun');
    f.requests[0].resolve(batch8ValidationData()); await settle();
    assert.equal(f.requests[1].action, 'exportValidationArtifacts');
    f.store.setProject({ projectId: id });
    f.requests[1].resolve({ artifacts: batch8Artifacts(), reportArtifacts: batch8Artifacts() }); await settle();
    assert.equal(f.requests.length, 2, id + ': old validation must not start a template in a new project scope');
    assert.equal(f.store.getState().reportArtifacts.length, 0);
  }
});

test('batch8 sidebar reports GL and TB confirmation independently and uses the same confirmed state label', () => {
  const f = batch8Fixture(), state = f.store.getState();
  f.store.setImportResult('gl', { batchId: 'g', columns: ['Date', 'Account'], rowCount: 5 });
  f.store.setImportResult('tb', { batchId: 't', columns: ['Account'], rowCount: 3 });
  f.store.setMappingCommitted('gl', { mapping: {}, mode: 'dual', projectedRowCount: 5 });
  const partial = f.app.stepSummary(state, 2, 'current');
  assert.match(partial, /GL.*已確認配對/); assert.match(partial, /TB.*待/); assert.doesNotMatch(partial, /GL[^；]*待配對/);
  f.store.setMappingCommitted('tb', { mapping: {}, mode: 'change', projectedRowCount: 3 });
  assert.match(f.app.stepSummary(state, 2, 'done'), /已確認配對/);
  assert.doesNotMatch(f.app.stepSummary(state, 2, 'done'), /已確認來源欄位的用途/);
});

test('batch8 operator labels distinguish the current account from the project creator', () => {
  const f = batch8Fixture(), badge = batch7ControlDom().node('badge'); f.elements.set('[data-bind="user-identity"]', badge);
  f.store.setCurrentUser({ shortName: 'CURRENT', principalName: 'SYNTHETIC\\CURRENT' }); f.app.renderIdentityBadge(f.store.getState());
  assert.match(badge.textContent, /目前帳號.*CURRENT/);
  const dom = batch7Dom(''); f.window.batch8Create.render(dom, f.store.getState());
  assert.match(dom.innerHTML, /建案者.*CREATOR/); assert.doesNotMatch(dom.innerHTML, /操作人員/);
  assert.match(f.app.overviewStepFactsHtml(f.store.getState(), 0), /建案者.*CREATOR/);
});

test('batch8 source preview states its ten-row boundary and TB merge guidance does not imply period-file reconciliation', () => {
  const f = batch8Fixture();
  const preview = f.importer.previewTableHtml({ previewData: { columns: ['Synthetic'], sampleRows: [['value']] } });
  assert.match(preview, /前 10 列/);
  assert.doesNotMatch(f.importer.emptyFaceHtml('tb'), /可合併欄位相同/);
  assert.match(f.importer.emptyFaceHtml('gl'), /可合併欄位相同/);
  f.importer.getWizard().kind = 'tb'; f.importer.getWizard().mode = 'replace';
  assert.doesNotMatch(f.importer.wizardBannerHtml(null), /可合併工作表或資料表/);
});

test('batch8 mapping date fields explain their separate purposes and approval-source warnings reuse the selectable label', () => {
  const f = batch8Fixture(), state = f.store.getState();
  f.store.setImportResult('gl', { batchId: 'g', columns: ['Post', 'Voucher', 'Approval'], rowCount: 1 });
  const html = f.mapping.classicEditSection('gl', 'GL', f.ui.GL_FIELDS, f.ui.GL_MODES, state.importState.gl, state.mapping.gl, 'dual', null, false);
  assert.match(html, /總帳入帳日[\s\S]*決定查核期間/);
  assert.match(html, /傳票日期[\s\S]*回溯過帳/);
  assert.match(html, /傳票核准日[\s\S]*財報準備日起核准/);
  f.store.setMappingDraft('gl', 'docDate', 'Approval'); f.store.patchGlMappingOptions({ approvalDateMode: 'unmapped' });
  assert.equal(state.mapping.gl.draft.docDate, undefined, 'the ordinary mode switch correctly clears the conflicting source');
  // Simulate an older inconsistent draft loaded from storage; ordinary setters deliberately prevent creating it.
  state.mapping.gl.draft.docDate = 'Approval';
  // Approval-source errors are rendered by optionProblemsHtml, not the manual-code-only preflight.
  const invalidApproval = f.mapping.classicEditSection('gl', 'GL', f.ui.GL_FIELDS, f.ui.GL_MODES, state.importState.gl, state.mapping.gl, 'dual', null, false);
  assert.match(invalidApproval, /核准日選「沒有核准日」時，不可同時指派/);
  assert.doesNotMatch(invalidApproval, /核准日選「不提供」/);
});

test('batch8 prescreen condition controls repeat the fourth-step logic and six-zero threshold while nested category hints remain row-scoped', () => {
  const f = batch8Fixture();
  for (const [key, index] of [['postPeriodApproval', 0], ['suspiciousKeywords', 1], ['unexpectedAccountPair', 2]]) {
    assert.ok(f.filter.ruleControlsHtml({ type: 'prescreen', prescreenKey: key }, 0, 0).includes(f.ui.esc(f.v.PRESCREEN_ITEMS[index].desc)), key);
  }
  assert.match(f.filter.ruleSummaryLabel({ type: 'prescreen', prescreenKey: 'trailingZeros' }, 0), /6/);
  const rule = { type: 'voucher', side: 'credit', quantifier: 'any', rules: [{ type: 'accountSide', drCr: 'credit', categoryMode: 'is', categoryIds: ['builtin.cash'] }] };
  const html = f.filter.compoundRuleHtml(rule, 0, 0, false, { rules: [rule] });
  assert.doesNotMatch(html, /「整張傳票」模式輸出整張傳票的分錄/);
  assert.match(html, /同一筆分錄/);
});

test('batch8 a single preparation-date condition visibly renders the actual date inside its card without changing the AST', () => {
  const f = batch8Fixture(); f.store.getState().project.lastPeriodStart = '2025-12-01';
  const rule = { type: 'prescreen', prescreenKey: 'postPeriodApproval', join: 'AND' };
  const draft = { name: 'One synthetic condition', rationale: 'Synthetic', groups: [{ join: 'AND', rules: [rule] }] };
  f.store.setFilterDraft(draft);
  const html = f.filter.scenarioBuilderHtml(draft);
  assert.ok(html.includes(f.ui.esc(f.v.PRESCREEN_ITEMS[0].desc)), 'the fourth-step explanation remains visible');
  assert.match(f.text(html), /財報準備日起核准（2025-12-01 起，含當日）/);
  assert.doesNotMatch(html, /class="filter-readback"/, 'single-rule layout does not gain a redundant full-expression section');
  assert.deepEqual(plain(f.filter.toWireDraft(draft).groups[0].rules[0]), plain(rule));
});

test('batch8 prescreen report handler sends the selected run and ignores callbacks after switching projects', async () => {
  const f = batch8Fixture(), run = { resultRef: { runId: 'p8', generatedUtc: '2026-01-01T00:00:00Z' } };
  f.store.setLastRun('prescreen', run);
  const html = f.v.prescreenArtifactHtml(f.store.getState(), run, { reportTitle: '預篩選報告' });
  f.controls(html).click('[data-action="export-prescreen-report"]');
  assert.equal(f.requests[0].action, 'exportPrescreenReport'); assert.deepEqual(plain(f.requests[0].payload), { runId: 'p8' });
  f.store.setProject({ projectId: 'another-synthetic' });
  f.requests[0].resolve({ artifacts: [{ artifactId: 'old-p8', kind: 'prescreenReport', stale: false, sourceRef: { prescreenRunId: 'p8' } }] }); await settle();
  assert.equal(f.store.getState().reportArtifacts.length, 0);
});

for (const entry of JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/batch8-filter-readback.json'), 'utf8'))) {
  test('batch8 shared full condition readback: ' + entry.id, () => {
    const f = batch8Fixture(); f.store.getState().project.lastPeriodStart = entry.preparationDate;
    const scenario = { name: entry.id, rationale: 'Synthetic preparation-date readback', __preserveJoins: true, ...plain(entry.scenario) };
    assert.equal(f.text(f.filter.readBackHtml(scenario, false)).replace(/^篩選條件：/, ''), entry.expected);
    assert.equal(f.filter.toWireDraft(scenario).groups[0].rules[0].preparationDate, undefined, 'project date is not frozen into the AST');
    if (entry.ruleTypeLabel) assert.equal(f.ui.FILTER_RULE_TYPES.find(item => item.value === scenario.groups[0].rules[0].type).label, entry.ruleTypeLabel);
    if (entry.preparationDate === null) assert.match(f.filter.ruleControlsHtml(scenario.groups[0].rules[0], 0, 0), /修改案件資料/);
  });
}

function batch9Fixture() {
  const requests = [], window = { setTimeout, clearTimeout, addEventListener() {}, JetFocus: { defer() {}, move() {}, trapDialogTab() {} }, JetApi: {} };
  const document = { readyState: 'loading', addEventListener() {}, querySelector: () => null,
    createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  for (const action of ['projectDeletePreview', 'projectDelete', 'mappingValueProfile', 'mappingCommitGl', 'mappingCommitTb']) {
    window.JetApi[action] = payload => new Promise((resolve, reject) => requests.push({ action, payload, resolve, reject }));
  }
  const context = vm.createContext({ window, document, CSS: { escape: value => value } });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/mapping-step.js'), 'utf8').replace("  Ui.registerStep('mapping', render);",
    '  window.batch9Mapping = { render, gridEditSection, classicEditSection, mappingCommitEligibility, policyCodes, ensureCodeComparisons, sameCode, bindMappingSection, glOptionsHtml, bindGlOptions };'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/import-step.js'), 'utf8').replace("  Ui.registerStep('import', render);",
    '  window.batch9Import = { applyResponse };'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'app.js'), 'utf8').replace("  if (document.readyState === 'loading') {",
    "  window.batch9App = { bindPicker, renderPicker };\n  if (document.readyState === 'loading') {"), context);
  const store = window.JetStore, ui = window.JetUi;
  store.setProject({ projectId: 'b9-synthetic', entityName: 'Synthetic' });
  ui.run = (label, fn, options = {}) => Promise.resolve().then(fn).catch(error => { if (options.onError) options.onError(error.message, error); });
  ui.loadProjects = () => Promise.resolve();
  return { window, store, ui, context, requests, mapping: window.batch9Mapping, importer: window.batch9Import, app: window.batch9App };
}

function seedBatch9Results(f) {
  const state = f.store.getState();
  state.lastRuns = { validate: { id: 'v9' }, prescreen: { id: 'p9' } };
  state.filter.preview = { count: 7 }; state.filterResultRef = { revision: 'f9' };
  state.filter.draft = { name: 'Unsaved draft', rationale: 'Keep this', groups: [] };
  state.staleState = { validation: true, prescreen: true, filter: true };
  state.reportArtifacts = [{ artifactId: 'criteria', kind: 'criteriaSelectionReport', stale: false, sourceRef: { scenarioRevision: 'f9' } }];
  return state;
}

test('batch9 mutation effects clear only backend-invalidated caches and overwrite stale flags without deriving report states', () => {
  const f = batch9Fixture(), state = seedBatch9Results(f), draft = state.filter.draft;
  assert.equal(typeof f.store.applyMutationEffects, 'function', 'one backend-authoritative mutation entry point');
  f.store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true },
    staleState: { validation: false, prescreen: false, filter: false },
    reportArtifacts: [{ artifactId: 'criteria', kind: 'criteriaSelectionReport', stale: true }] });
  assert.equal(state.lastRuns.validate.id, 'v9'); assert.equal(state.lastRuns.prescreen, null); assert.equal(state.filter.preview, null);
  assert.equal(state.filter.draft, draft, 'keep unsaved condition editing');
  assert.deepEqual(plain(state.staleState), { validation: false, prescreen: false, filter: false });
  assert.deepEqual(plain(state.reportArtifacts), [{ artifactId: 'criteria', kind: 'criteriaSelectionReport', stale: true }]);
});

test('batch9 unavailable artifact refresh preserves the exact catalog and exposes its warning rather than inventing empty or stale files', () => {
  const f = batch9Fixture(), state = seedBatch9Results(f), catalog = state.reportArtifacts;
  assert.equal(typeof f.store.applyMutationEffects, 'function');
  f.store.applyMutationEffects({ invalidatedResults: { validation: true, prescreen: false, filter: false },
    staleState: { validation: true, prescreen: false, filter: false }, reportArtifacts: null,
    reportArtifactWarning: '變更已儲存，報告清單暫時無法更新。' });
  assert.equal(state.reportArtifacts, catalog); assert.equal(catalog[0].stale, false);
  assert.match(state.messages.map(item => item.text).join('\n'), /變更已儲存.*報告清單暫時無法更新/);
  assert.equal(state.lastRuns.validate, null); assert.equal(state.lastRuns.prescreen.id, 'p9');
  assert.equal(state.filter.preview.count, 7);
});

test('batch9 state hydration setters do not maintain a second mutation dependency matrix', () => {
  const operations = [f => f.store.setImportResult('gl', { batchId: 'g', columns: [] }),
    f => f.store.setImportResult('tb', { batchId: 't', columns: [] }),
    f => f.store.setMappingCommitted('gl', { mapping: {}, mode: 'dual' }),
    f => f.store.setMappingCommitted('tb', { mapping: {}, mode: 'change' }),
    f => f.store.setCalendarState({ nonWorkingDays: [0, 6] }), f => f.store.setAccountMappingState({ rowCount: 3 }),
    f => f.store.setAuthorizedPreparerState({ rowCount: 2 }), f => f.store.setTaxonomyAfterSave({ revision: 2, categories: [] })];
  for (const [index, operation] of operations.entries()) {
    const f = batch9Fixture(), state = seedBatch9Results(f), catalog = plain(state.reportArtifacts);
    operation(f);
    assert.equal(state.lastRuns.validate?.id, 'v9', 'setter ' + index + ' cannot decide audit dependencies');
    assert.equal(state.lastRuns.prescreen?.id, 'p9', 'setter ' + index + ' cannot decide audit dependencies');
    assert.equal(state.filter.preview?.count, 7, 'setter ' + index + ' cannot decide audit dependencies');
    assert.deepEqual(plain(state.reportArtifacts), catalog);
  }
});

test('batch9 GL import response consumes authoritative effects including an unsaved preview with no persisted stale results', () => {
  const f = batch9Fixture(), state = seedBatch9Results(f);
  f.importer.applyResponse('gl', { batchId: 'new', columns: ['Synthetic'], rowCount: 1,
    invalidatedResults: { validation: false, prescreen: false, filter: true },
    staleState: { validation: false, prescreen: false, filter: false }, reportArtifacts: [] });
  assert.equal(state.lastRuns.validate?.id, 'v9'); assert.equal(state.lastRuns.prescreen?.id, 'p9');
  assert.equal(state.filter.preview, null); assert.deepEqual(plain(state.reportArtifacts), []);
  assert.deepEqual(plain(state.staleState), { validation: false, prescreen: false, filter: false });
});

test('batch9 metadata updates prefer reportArtifacts and preserve a null catalog instead of replacing it with the compatibility alias', () => {
  const f = batch9Fixture(), state = seedBatch9Results(f), project = state.project, catalog = state.reportArtifacts;
  const result = { project, invalidatedResults: { validation: false, prescreen: false, filter: false },
    staleState: { validation: false, prescreen: false, filter: false }, reportArtifacts: null, artifacts: [] };
  f.store.updateProjectMetadata(result); assert.equal(state.reportArtifacts, catalog);
  f.store.updateProjectMetadata({ ...result, reportArtifacts: [{ artifactId: 'authoritative', stale: true }], artifacts: [] });
  assert.equal(state.reportArtifacts[0]?.artifactId, 'authoritative');
  assert.equal(state.lastRuns.validate?.id, 'v9'); assert.equal(state.filter.preview?.count, 7);
});

for (const mode of ['side', 'flag']) test('batch9 GL ' + mode + ' requires and renders both literal codes without inventing a legacy credit value', () => {
  const f = batch9Fixture(), state = f.store.getState(), info = { batchId: 'g', columns: ['Amount', 'Side'], rowCount: 2 };
  f.store.setImportResult('gl', info); f.store.setMappingMode('gl', mode);
  const credit = f.ui.GL_FIELDS.find(field => field.key === 'dcCreditCode');
  assert.ok(credit && credit.literal && credit.req.includes(mode), 'credit code is a required literal');
  assert.equal(f.ui.TB_FIELDS.some(field => field.key === 'dcCreditCode'), false);
  for (const field of f.ui.GL_FIELDS.filter(field => field.req.includes(mode))) f.store.setMappingDraft('gl', field.key, field.literal ? (field.key === 'dcDebitCode' ? 'D' : '') : 'Amount');
  const eligibility = f.mapping.mappingCommitEligibility(f.ui.GL_FIELDS, mode, state.mapping.gl, 'gl');
  assert.equal(eligibility.canCommit, false);
  assert.ok(eligibility.missing.some(field => field.key === 'dcCreditCode'));
  const grid = f.mapping.gridEditSection('gl', 'GL', f.ui.GL_FIELDS, f.ui.GL_MODES, info, state.mapping.gl, mode, null, false);
  const classic = f.mapping.classicEditSection('gl', 'GL', f.ui.GL_FIELDS, f.ui.GL_MODES, info, state.mapping.gl, mode, null, false);
  for (const html of [grid, classic]) { assert.match(html, /dcDebitCode/); assert.match(html, /dcCreditCode/); assert.match(html, /貸方代碼/); }
  // setMappingDraft has always removed empty values. A missing key, not a new empty-string convention, is the legacy state.
  assert.equal(Object.hasOwn(state.mapping.gl.draft, 'dcCreditCode'), false, 'older debit-only mappings must not gain an assumed other-side code');
  for (const html of [grid, classic]) assert.match(html, /<input[^>]*data-mapping-key="dcCreditCode"[^>]*value=""/);
});

test('batch9 literal credit survives import reconciliation and does not reserve an RDE source column with the same name', () => {
  const f = batch9Fixture(); f.store.setImportResult('gl', { batchId: 'old', columns: ['Side', 'C'], rowCount: 1 });
  f.store.replaceMappingDraft('gl', { dcField: 'Side', dcDebitCode: 'D', dcCreditCode: 'C' });
  f.store.setImportResult('gl', { batchId: 'new', columns: ['Side', 'C'], rowCount: 2 });
  assert.equal(f.store.getState().mapping.gl.draft.dcCreditCode, 'C');
  assert.ok(f.store.availableGlRdeColumns().includes('C'), 'literal does not consume an RDE source');
  f.store.setImportResult('gl', { batchId: 'newer', columns: ['Side'], rowCount: 3 });
  assert.equal(f.store.getState().mapping.gl.draft.dcCreditCode, 'C', 'literal survives even when no source shares its text');
});

test('batch9 two literal codes use backend canonical comparisons, including distinct sharp-S and SS', async () => {
  const f = batch9Fixture(); f.store.setImportResult('gl', { batchId: 'g', columns: ['Side'], rowCount: 2 });
  f.store.setMappingMode('gl', 'flag'); f.store.replaceMappingDraft('gl', { dcField: 'Side', dcDebitCode: ' D ', dcCreditCode: 'd' });
  assert.deepEqual(plain(f.mapping.policyCodes('Side')), [' D ', 'd']);
  f.mapping.ensureCodeComparisons('Side', false, true);
  assert.equal(f.requests.length, 1); assert.equal(f.requests[0].payload.comparisonOnly, true);
  f.requests[0].resolve({ comparisonKeys: [{ value: ' D ', key: 'opaque-D' }, { value: 'd', key: 'opaque-D' }] }); await settle();
  assert.equal(f.mapping.sameCode(' D ', 'd', 'Side'), true);
  assert.match(f.mapping.mappingCommitEligibility(f.ui.GL_FIELDS, 'flag', f.store.getState().mapping.gl, 'gl').problems.join('\n'), /借方.*貸方.*不同/);
  f.store.replaceMappingDraft('gl', { dcField: 'Side', dcDebitCode: 'ß', dcCreditCode: 'SS' });
  f.mapping.ensureCodeComparisons('Side', false, true);
  f.requests[1].resolve({ comparisonKeys: [{ value: 'ß', key: 'opaque-sharp' }, { value: 'SS', key: 'opaque-SS' }] }); await settle();
  assert.equal(f.mapping.sameCode('ß', 'SS', 'Side'), false);
  assert.doesNotMatch(f.mapping.mappingCommitEligibility(f.ui.GL_FIELDS, 'flag', f.store.getState().mapping.gl, 'gl').problems.join('\n'), /借方.*貸方.*不同/);
});

test('batch9 failed debit-credit metadata comparison can retry visibly without reading the GL population', async () => {
  const f = batch9Fixture(), state = f.store.getState();
  f.store.setImportResult('gl', { batchId: 'g', columns: ['Side'], rowCount: 2 });
  f.store.setMappingMode('gl', 'flag'); f.store.replaceMappingDraft('gl', { dcField: 'Side', dcDebitCode: ' D ', dcCreditCode: 'd' });
  function render() { const dom = batch7Dom(f.mapping.glOptionsHtml(state.importState.gl, state.mapping.gl)); f.mapping.bindGlOptions(dom); return dom; }
  render(); assert.equal(f.requests.length, 1); assert.equal(f.requests[0].payload.comparisonOnly, true);
  f.requests[0].reject(new Error('Synthetic metadata unavailable')); await settle();
  const dom = render(); assert.match(dom.innerHTML, /來源值對應尚未確認/);
  const retry = dom.querySelector('[data-retry-comparison="Side"]'); assert.ok(retry, 'visible retry for debit-credit metadata'); retry.fire();
  assert.equal(f.requests.length, 2);
  assert.deepEqual(plain(f.requests[1].payload), { dataset: 'gl', sourceColumn: 'Side', comparisonOnly: true, comparisonValues: [' D ', 'd'] });
  f.requests[1].resolve({ comparisonKeys: [{ value: ' D ', key: 'opaque-D' }, { value: 'd', key: 'opaque-D' }] }); await settle();
  assert.match(f.mapping.mappingCommitEligibility(f.ui.GL_FIELDS, 'flag', state.mapping.gl, 'gl').problems.join('\n'), /借方.*貸方.*不同/);
  assert.equal(state.mapping.gl.draft.dcDebitCode, ' D '); assert.equal(state.mapping.gl.draft.dcCreditCode, 'd');
});

function batch9PickerFixture() {
  const f = batch9Fixture(), nodes = new Map();
  function node(key, attributes = {}) {
    if (!nodes.has(key)) nodes.set(key, { attributes, hidden: false, disabled: false, innerHTML: '', textContent: '', isConnected: true, handlers: {},
      getAttribute(name) { return this.attributes[name] ?? null; }, addEventListener(name, handler) { this.handlers[name] = handler; },
      querySelector: selector => node(selector), querySelectorAll: selector => selector === '[data-action="modal-cancel"]' ? [node('button[data-action="modal-cancel"]')] : [],
      focus() {}, setAttribute() {}, removeAttribute() {},
      click() { assert.equal(typeof this.handlers.click, 'function', key + ' click binding'); this.handlers.click({ stopPropagation() {}, preventDefault() {} }); } });
    return nodes.get(key);
  }
  const rows = [node('delete-A', { 'data-project-id': 'a', 'data-project-name': 'Synthetic A', 'data-project-provider': 'sqlite' }),
    node('delete-B', { 'data-project-id': 'b', 'data-project-name': 'Synthetic B', 'data-project-provider': 'sqlServer' })];
  const container = { querySelector: selector => /picker-copy-error|picker-support-export|picker-dev-log-export/.test(selector) ? null : node(selector),
    querySelectorAll: selector => selector === '[data-action="picker-delete"]' ? rows : [] };
  f.app.bindPicker(container);
  return { ...f, node, rows, detail: () => node('[data-bind="delete-modal-detail"]').innerHTML + node('[data-bind="delete-modal-detail"]').textContent };
}

test('batch9 delete confirmation loads exact report and workpaper counts and states that the entire folder is removed', async () => {
  const f = batch9PickerFixture(); f.rows[0].click(); await settle();
  assert.equal(f.requests[0]?.action, 'projectDeletePreview'); assert.deepEqual(plain(f.requests[0].payload), { projectId: 'a' });
  assert.doesNotMatch(f.detail(), /報告\s*0\s*份|底稿\s*0\s*份/);
  f.requests[0].resolve({ projectId: 'a', databaseProvider: 'sqlite', reportCount: 3, workpaperCount: 2 }); await settle();
  assert.match(f.detail(), /報告\s*3\s*份/); assert.match(f.detail(), /工作底稿\s*2\s*份/);
  assert.match(f.detail(), /案件資料夾/); assert.match(f.detail(), /其他檔案/); assert.match(f.detail(), /無法復原/);
  f.node('[data-action="modal-confirm"]').click(); await settle();
  assert.equal(f.requests[1].action, 'projectDelete'); assert.deepEqual(plain(f.requests[1].payload), { projectId: 'a' });
});

test('batch9 delete count failures allow retry without a fake zero or an additional deletion gate', async () => {
  const f = batch9PickerFixture(); f.rows[0].click(); await settle();
  assert.equal(f.requests[0]?.action, 'projectDeletePreview');
  f.requests[0].reject(new Error('Synthetic catalog unavailable')); await settle();
  assert.match(f.detail(), /數量暫時無法確認/); assert.doesNotMatch(f.detail(), /報告\s*0\s*份/);
  f.node('[data-action="delete-preview-retry"]').click(); await settle();
  assert.equal(f.requests[1].action, 'projectDeletePreview');
  f.requests[1].reject(new Error('Synthetic catalog unavailable again')); await settle();
  assert.equal(f.node('[data-action="modal-confirm"]').disabled, false);
  f.node('[data-action="modal-confirm"]').click(); await settle(); assert.equal(f.requests[2].action, 'projectDelete');
});

test('batch9 cancelled or replaced delete preview responses cannot overwrite the new target and SQL Server remains explicit', async () => {
  const f = batch9PickerFixture(); f.rows[0].click(); await settle();
  assert.equal(f.requests[0]?.action, 'projectDeletePreview');
  f.node('button[data-action="modal-cancel"]').click(); f.rows[1].click(); await settle();
  f.requests[1].resolve({ projectId: 'b', databaseProvider: 'sqlServer', reportCount: 1, workpaperCount: 4 }); await settle();
  const expected = f.detail(); assert.match(expected, /所有使用者/); assert.match(expected, /工作底稿\s*4\s*份/);
  f.requests[0].resolve({ projectId: 'a', databaseProvider: 'sqlite', reportCount: 99, workpaperCount: 99 }); await settle();
  assert.equal(f.detail(), expected);
});

test('batch9 project.deletePreview is available only through the existing JetApi request channel', () => {
  const sent = [], window = { chrome: { webview: { postMessage: message => sent.push(message), addEventListener() {} } } };
  vm.runInContext(fs.readFileSync(path.join(root, 'jet-api.js'), 'utf8'), vm.createContext({ window }));
  assert.equal(typeof window.JetApi.projectDeletePreview, 'function');
  window.JetApi.projectDeletePreview({ projectId: 'synthetic' });
  assert.equal(sent[0].action, 'project.deletePreview'); assert.deepEqual(plain(sent[0].payload), { projectId: 'synthetic' });
});

test('batch9 first step shows distinct reused document numbers separately from affected entries and does not change the voucher key', () => {
  const f = batch8Fixture(), state = f.store.getState();
  f.store.setLastRun('validate', { documentDateReuse: { documentNumberCount: 3, entryCount: 17 } });
  const before = plain(state.project), dom = batch7Dom(''); f.window.batch8Create.render(dom, state);
  assert.match(dom.innerHTML, /role="status"/);
  assert.match(dom.innerHTML, /3\s*個傳票號碼/); assert.match(dom.innerHTML, /17\s*列/);
  assert.match(dom.innerHTML, /不同總帳入帳日/);
  assert.match(dom.innerHTML, /不會.*(?:更改|改變).*比對鍵/);
  assert.doesNotMatch(dom.innerHTML, /跨月.*3\s*張傳票/);
  assert.deepEqual(plain(state.project), before);
  assert.equal(f.ui.stepGate(state, 1).ok, true, 'this is a review reminder, not a new blocking gate');
});

test('batch9 first step distinguishes a measured zero from unavailable legacy or stale date-reuse summaries', () => {
  const f = batch8Fixture(), state = f.store.getState();
  function html() { const dom = batch7Dom(''); f.window.batch8Create.render(dom, state); return dom.innerHTML; }
  f.store.setLastRun('validate', { documentDateReuse: { documentNumberCount: 0, entryCount: 0 } });
  assert.match(html(), /未發現.*不同總帳入帳日/);
  for (const summary of [null, {}, { documentDateReuse: null }]) {
    f.store.setLastRun('validate', summary);
    assert.match(html(), /尚未確認/); assert.match(html(), /資料驗證/);
    assert.doesNotMatch(html(), /0\s*個傳票號碼|未發現.*不同總帳入帳日/);
  }
  f.store.setLastRun('validate', { documentDateReuse: { documentNumberCount: 3, entryCount: 17 } });
  f.store.setStaleState({ validation: true });
  assert.match(html(), /尚未確認/); assert.doesNotMatch(html(), /3\s*個傳票號碼|17\s*列/);
});

test('batch9 reopening uses persisted date-reuse summary without an extra source scan', () => {
  const f = batch8Fixture(); f.store.resetWorkflow();
  f.window.setInterval = () => 0; f.window.clearInterval = () => {};
  f.ui.applyLoadedProject({ project: { projectId: 'reopened-date-reuse' },
    latestRuns: { validate: { documentDateReuse: { documentNumberCount: 1, entryCount: 4 } } },
    staleState: { validation: false, prescreen: false, filter: false } }, { stayOnCurrentStep: true });
  const dom = batch7Dom(''); f.window.batch8Create.render(dom, f.store.getState());
  assert.match(dom.innerHTML, /1\s*個傳票號碼/); assert.match(dom.innerHTML, /4\s*列/);
  assert.equal(f.requests.length, 0, 'summary rendering does not fetch GL rows');
});

// Minimal tree DOM for real rule-list render/bind and app renderContent lifecycles (not a second pager implementation).
function batch9TreeDom() {
  const escape = value => String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  const decode = value => value.replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
  let document;
  function element(tag = 'div', attrs = {}) {
    const node = { tagName: tag.toUpperCase(), attrs: { ...attrs }, children: [], parentElement: null, handlers: {}, isConnected: true,
      scrollTop: 0, scrollLeft: 0, disabled: Object.hasOwn(attrs, 'disabled'), hidden: Object.hasOwn(attrs, 'hidden'), value: attrs.value || '',
      getAttribute(name) { return Object.hasOwn(this.attrs, name) ? this.attrs[name] : null; },
      setAttribute(name, value) { this.attrs[name] = String(value); }, removeAttribute(name) { delete this.attrs[name]; },
      addEventListener(name, fn) { (this.handlers[name] ||= []).push(fn); },
      fire(name, extra = {}) { for (const fn of this.handlers[name] || []) fn({ target: this, currentTarget: this, preventDefault() {}, stopPropagation() {}, ...extra }); },
      appendChild(child) { this._text = ''; this.children.push(child); child.parentElement = this; child.isConnected = this.isConnected; return child; },
      contains(child) { for (let p = child; p; p = p.parentElement) if (p === this) return true; return false; },
      matches(selector) {
        if (selector.includes(',')) return selector.split(',').some(part => this.matches(part.trim()));
        const attributes = [...selector.matchAll(/\[([^\]=]+)(?:="([^"]*)")?\]/g)];
        if (attributes.some(match => this.getAttribute(match[1]) === null || match[2] !== undefined && this.getAttribute(match[1]) !== match[2])) return false;
        const base = selector.replace(/\[[^\]]+\]/g, ''), tagMatch = base.match(/^[a-z][\w-]*/i);
        if (tagMatch && this.tagName !== tagMatch[0].toUpperCase()) return false;
        for (const part of base.matchAll(/\.([\w-]+)/g)) if (!(this.attrs.class || '').split(/\s+/).includes(part[1])) return false;
        const id = base.match(/#([\w-]+)/); return !id || this.attrs.id === id[1];
      },
      closest(selector) { for (let node = this; node; node = node.parentElement) if (node.matches(selector)) return node; return null; },
      querySelectorAll(selector) {
        const selectors = selector.split(',').map(part => part.trim()), found = [];
        function matchesPath(node, parts) { if (!node.matches(parts.at(-1))) return false; let p = node.parentElement;
          for (let i = parts.length - 2; i >= 0; i--) { while (p && !p.matches(parts[i])) p = p.parentElement; if (!p) return false; p = p.parentElement; } return true; }
        function walk(parent) { for (const child of parent.children) { if (selectors.some(sel => matchesPath(child, sel.split(/\s+(?=(?:[^"]*"[^"]*")*[^"]*$)/)))) found.push(child); walk(child); } }
        walk(this); return found;
      },
      querySelector(selector) { return this.querySelectorAll(selector)[0] || null; },
      insertAdjacentHTML(position, html) { const fragment = element(); fragment.innerHTML = html;
        if (position === 'beforeend') for (const child of fragment.children.slice()) this.appendChild(child);
        else throw new Error('Unsupported synthetic DOM insertion: ' + position); },
      focus() { if (document) document.activeElement = this; }, scrollIntoView() {},
      get rows() { return this.querySelectorAll('tr'); },
      get innerHTML() { return this._text ? escape(this._text) : this.children.map(child => '<' + child.tagName.toLowerCase() + Object.entries(child.attrs).map(([k, v]) => ' ' + k + '="' + escape(v) + '"').join('') + '>' + child.innerHTML + '</' + child.tagName.toLowerCase() + '>').join(''); },
      set innerHTML(html) {
        function detach(node) { node.isConnected = false; node.children.forEach(detach); } this.children.forEach(detach); this.children = []; this._text = '';
        const stack = [this], voids = new Set(['input', 'br', 'hr', 'img', 'meta', 'link']);
        for (const token of String(html).matchAll(/<\/?([a-z][\w-]*)\b([^>]*?)>|([^<]+)/gi)) {
          if (token[3]) { const text = element('jet-text'); text._text = decode(token[3]); stack.at(-1).appendChild(text); continue; }
          const name = token[1].toLowerCase(); if (token[0].startsWith('</')) { while (stack.length > 1) { if (stack.pop().tagName === name.toUpperCase()) break; } continue; }
          const attributes = {}; for (const attr of token[2].matchAll(/([^\s=/>]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/g)) attributes[attr[1]] = decode(attr[2] ?? attr[3] ?? attr[4] ?? '');
          const child = element(name, attributes); stack.at(-1).appendChild(child); if (!voids.has(name) && !token[0].endsWith('/>')) stack.push(child);
        }
      },
      get textContent() { return this._text || this.children.map(child => child.textContent).join(''); },
      set textContent(value) { this.children = []; this._text = String(value); },
      get className() { return this.attrs.class || ''; }, set className(value) { this.attrs.class = value; }
    };
    node.classList = { add(name) { const values = new Set(node.className.split(/\s+/).filter(Boolean)); values.add(name); node.className = [...values].join(' '); },
      remove(name) { node.className = node.className.split(/\s+/).filter(value => value !== name).join(' '); },
      contains(name) { return node.className.split(/\s+/).includes(name); }, toggle(name, force) { const on = force ?? !this.contains(name); this[on ? 'add' : 'remove'](name); } };
    return node;
  }
  const content = element('main', { 'data-bind': 'content' });
  document = { readyState: 'loading', activeElement: null, createElement: element, addEventListener() {},
    querySelector: selector => content.matches(selector) ? content : content.querySelector(selector), querySelectorAll: selector => content.querySelectorAll(selector) };
  return { element, content, document };
}

function batch9PagerLifecycleFixture(kind) {
  const dom = batch9TreeDom(), requests = [], window = { setTimeout: fn => { fn(); return 0; }, clearTimeout() {}, CSS: { escape: value => value },
    addEventListener() {}, JetFocus: { defer() {} }, JetApi: {} };
  for (const action of ['queryDocBalancePage', 'queryInfSamplePage', 'queryPrescreenPage']) window.JetApi[action] = payload =>
    new Promise((resolve, reject) => requests.push({ action, payload, resolve, reject }));
  const context = vm.createContext({ window, document: dom.document });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  window.JetUi.accountMappingEditor = { bind() {} };
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/validate-step.js'), 'utf8').replace("  Ui.registerStep('validate', render);",
    '  window.batch9Pages = { bind, ruleListHtml };'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'app.js'), 'utf8').replace("  if (document.readyState === 'loading') {",
    "  window.batch9PagerApp = { renderContent };\n  if (document.readyState === 'loading') {"), context);
  const store = window.JetStore, ui = window.JetUi;
  store.setProject({ projectId: 'b9-pager', entityName: 'Synthetic' });
  store.setLastRun('validate', { resultRef: { runId: 'v1' } }); store.setLastRun('prescreen', { resultRef: { runId: 'p1' } });
  const state = store.getState(); state.view = 'workflow'; state.currentStepIndex = 3;
  const runKind = kind === 'prescreen' ? 'prescreen' : 'validate', scope = kind === 'prescreen' ? 'p' : 'v';
  const details = { ordinary: { kind: 'table', columns: ['傳票號碼', '借方', '貸方', '差額'], rows: [['PREVIEW', '1', '0', '1']],
    loadMore: 'docBalance', sortAction: 'query.docBalancePage', sortKeys: ['documentNumber', 'debit', 'credit', 'diff'], searchLabel: '依傳票號碼查看' },
    dynamic: { kind: 'dynamicPage', pageKey: 'inf' }, prescreen: { kind: 'prescreenPreview', previews: [{ key: 'weekendPosting', label: '週末過帳' }] } };
  ui.registerStep('validate', (container, current) => { container.innerHTML = window.batch9Pages.ruleListHtml([
    { title: 'Synthetic detail', desc: 'Synthetic', status: () => ({ tone: 'hit', text: '3 筆' }), detail: () => details[kind] }
  ], current.lastRuns[runKind], scope); window.batch9Pages.bind(container, false); });
  function draw() { window.batch9PagerApp.renderContent(store.getState(), store.STEPS[3]); return view(); }
  function view() { return { root: dom.content, panel: dom.content.querySelector('.rule-detail'), toggle: dom.content.querySelector('[data-action="toggle-detail"]'),
    get table() { return dom.content.querySelector('.preview-table'); }, get more() { return dom.content.querySelector('[data-load-more], [data-dynamic-load-more], [data-prescreen-load-more]'); },
    get search() { return dom.content.querySelector('[data-page-search]'); }, get input() { return dom.content.querySelector('[data-page-search-input]'); },
    get wrap() { return dom.content.querySelector('.preview-table__wrap'); }, get tbody() { return dom.content.querySelector('.preview-table tbody'); } }; }
  function response(names, nextCursor) { return { rows: names.map(name => ({ documentNumber: name, debit: 10, credit: 0, diff: 10, accountCode: '1001', amount: 10 })), nextCursor,
    columns: [{ key: 'documentNumber', label: '傳票號碼', valueType: 'text', sortable: true }] }; }
  async function open() { let page = draw(); page.toggle.fire('click'); if (requests.length) { requests.at(-1).resolve(response(['PREVIEW'], 'preview-next')); await settle(); } return view(); }
  return { store, ui, window, requests, draw, view, open, response, runKind };
}

for (const kind of ['ordinary', 'dynamic', 'prescreen']) test('batch9 medium pager survives redraw with rows cursor sort search expansion and non-edge scroll: ' + kind, async () => {
  const f = batch9PagerLifecycleFixture(kind); let page = await f.open();
  const sort = page.table.querySelector('.th-sort'); page.table.fire('click', { target: sort });
  f.requests.at(-1).resolve(f.response(['SORTED'], 'sorted-next')); await settle();
  page.input.value = 'JV'; page.search.fire('submit'); f.requests.at(-1).resolve(f.response(['JV-A'], 'page-2')); await settle();
  page.more.fire('click'); assert.equal(f.requests.at(-1).payload.cursor, 'page-2');
  f.requests.at(-1).resolve(f.response(['JV-B'], 'page-3')); await settle();
  assert.match(page.tbody.textContent, /JV-A.*JV-B/);
  page.wrap.scrollTop = 73; page.wrap.scrollLeft = 41; page.root.scrollTop = 257;
  const before = f.requests.length; f.store.touch(); page = f.draw();
  assert.equal(page.panel.hidden, false, 'redraw preserves the expanded detail');
  assert.match(page.tbody?.textContent || '', /JV-A.*JV-B/, 'the loaded rows are restored rather than just the summary preview');
  assert.equal(page.input.value, 'JV'); assert.equal(page.table.querySelector('th[data-sort-key]').getAttribute('aria-sort'), 'ascending');
  assert.equal(page.wrap.scrollTop, 73); assert.equal(page.wrap.scrollLeft, 41); assert.equal(page.root.scrollTop, 257);
  assert.equal(f.requests.length, before, 'redraw must not silently restart the query');
  page.more.fire('click'); assert.equal(f.requests.at(-1).payload.cursor, 'page-3'); assert.equal(f.requests.at(-1).payload.search, 'JV');
  assert.deepEqual(plain(f.requests.at(-1).payload.sort), { key: 'documentNumber', direction: 'asc' });
});

for (const kind of ['ordinary', 'dynamic', 'prescreen']) test('batch9 medium pager failure survives redraw and retries its next cursor without erasing earlier rows: ' + kind, async () => {
  const f = batch9PagerLifecycleFixture(kind); let page = await f.open(); page.more.fire('click');
  f.requests.at(-1).resolve(f.response(['FIRST'], 'next')); await settle(); page.more.fire('click');
  f.requests.at(-1).reject(new Error('Synthetic next page failure')); await settle();
  f.store.touch(); page = f.draw(); assert.match(page.tbody?.textContent || '', /FIRST/);
  const retry = [...page.panel.querySelectorAll('button')].find(button => button.textContent === '重試' && !button.hidden);
  assert.ok(retry, 'the retry action remains next to the retained table'); retry.fire('click');
  assert.equal(f.requests.at(-1).payload.cursor, 'next'); f.requests.at(-1).resolve(f.response(['SECOND'], null)); await settle();
  assert.match(f.view().tbody.textContent, /FIRST.*SECOND/); assert.equal(f.view().more.hidden, true);
});

for (const kind of ['ordinary', 'dynamic', 'prescreen']) for (const change of ['project', 'reopen', 'run', 'generation']) {
  test('batch9 medium pager drops scoped state and late data after ' + change + ': ' + kind, async () => {
    const f = batch9PagerLifecycleFixture(kind); let page = await f.open(); page.more.fire('click');
    f.requests.at(-1).resolve(f.response(['OLD-FIRST'], 'old-next')); await settle(); page.more.fire('click'); const late = f.requests.at(-1);
    if (change === 'project' || change === 'reopen') f.store.setProject({ projectId: change === 'project' ? 'replacement' : 'b9-pager' });
    if (change === 'run') f.store.setLastRun(f.runKind, { resultRef: { runId: 'new-run' } });
    if (change === 'generation') f.store.setImportResult('gl', { batchId: 'replacement', columns: [] });
    page = f.draw(); assert.doesNotMatch(page.root.textContent, /OLD-FIRST/);
    late.resolve(f.response(['WRONG-LATE'], 'wrong-next')); await settle();
    assert.doesNotMatch(page.root.textContent, /WRONG-LATE/);
    if (page.panel.hidden) page.toggle.fire('click');
    if (kind !== 'ordinary') { f.requests.at(-1).resolve(f.response(['NEW-PREVIEW'], 'new-preview-next')); await settle(); }
    page = f.view(); page.more.fire('click'); assert.equal(f.requests.at(-1).payload.cursor, null, 'new scope starts at the first query page');
  });
}

function batch9CancellationFixture() {
  const dom = batch9TreeDom(), pending = [], requestHandlers = [], window = { setTimeout, clearTimeout, JetApi: {
    onRequestStarted(fn) { requestHandlers.push(fn); }, operationCancel: payload => new Promise((resolve, reject) => pending.push({ payload, resolve, reject })) } };
  dom.content.innerHTML = '<button data-action="cancel-operation">取消作業</button>';
  const context = vm.createContext({ window, document: dom.document });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const source = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
  function slice(start, end) { const from = source.indexOf(start), to = source.indexOf(end, from + start.length); assert.ok(from >= 0 && to > from); return source.slice(from, to); }
  // Execute the actual app registration and click-handler blocks; no duplicate implementation of cancellation policy.
  vm.runInContext('var global = window, Store = window.JetStore, busyActiveAction = null;\n' +
    slice('  var CANCELLABLE_ACTIONS = {', '  // 增量渲染狀態') +
    slice("    if (global.JetApi && typeof global.JetApi.onRequestStarted === 'function') {", '    // 長作業進度只更新') +
    slice("    var cancelOperationBtn = document.querySelector('[data-action=\"cancel-operation\"]');", "    document.querySelectorAll('[data-action=\"messages-toggle\"]')"), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/export-step.js'), 'utf8').replace("  Ui.registerStep('export', render);",
    '  window.batch9ExportChain = { requireExportActive, exportPrescreenReportForRun, exportWorkpaper };'), context);
  return { window, store: window.JetStore, pending, cancel: dom.content.querySelector('button'),
    started(action, requestId) { requestHandlers.forEach(fn => fn({ action, requestId })); } };
}

test('batch9 medium every existing import action is cancellable and background requests cannot steal its request ID', () => {
  const f = batch9CancellationFixture();
  const actions = ['import.inspectFile', 'import.previewFile', 'import.gl.fromFile', 'import.tb.fromFile', 'import.accountMapping.fromFile',
    'import.authorizedPreparer.fromFile', 'import.authorizedPreparer.clear', 'import.holiday', 'import.makeupDay', 'import.holiday.fromFile', 'import.makeupDay.fromFile'];
  for (const [index, action] of actions.entries()) {
    f.store.setBusy(true, 'Synthetic import'); f.started(action, 'request-' + index); f.started('log.append', 'log');
    f.started('query.dataPreview', 'background'); assert.equal(f.store.getState().activeRequestId, 'request-' + index, action);
    f.cancel.fire('click'); assert.equal(f.pending.at(-1).payload.requestId, 'request-' + index);
  }
});

test('batch9 medium cancellation after a stage committed truthfully stops only later stages and preserves the completed result', async () => {
  const f = batch9CancellationFixture(), stages = [], completed = { artifactId: 'already-published', kind: 'prescreenReport', stale: false };
  f.store.setProject({ projectId: 'chain' });
  f.store.setLastRun('validate', { resultRef: { runId: 'v1' } }); f.store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' });
  const project = f.store.getState().project, payload = { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: [0] };
  let finishStage;
  f.window.JetApi.exportPrescreenReport = () => new Promise(resolve => { finishStage = () => { stages.push('first-committed'); resolve({ artifact: completed, reportArtifacts: [completed] }); }; });
  f.window.JetApi.exportWorkpaperStream = async () => { stages.push('second-started'); return { sheetStats: [] }; };
  const task = f.window.JetUi.run('產生工作底稿與預篩選報告', () => f.window.batch9ExportChain.exportPrescreenReportForRun('p1', project, payload).then(() => {
    f.window.batch9ExportChain.requireExportActive(project, payload);
    return f.window.batch9ExportChain.exportWorkpaper(payload, project);
  }));
  f.started('export.prescreenReport', 'committed-request'); f.cancel.fire('click');
  f.pending[0].resolve({ requested: false }); await settle(); finishStage(); await task;
  assert.deepEqual(stages, ['first-committed']); assert.equal(f.store.getState().reportArtifacts[0].artifactId, completed.artifactId);
  const messages = f.store.getState().messages.map(item => item.text).join('\n');
  assert.match(messages, /後續|尚未開始/); assert.doesNotMatch(messages, /無需取消/);
});

test('batch9 medium corrupt local project remains visible with honest unknown provider and actionable load error', () => {
  const dom = batch9TreeDom(), window = { setTimeout, clearTimeout, addEventListener() {}, JetFocus: { defer() {}, move() {} } };
  dom.content.setAttribute('data-bind', 'project-picker');
  const context = vm.createContext({ window, document: dom.document });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'app.js'), 'utf8').replace("  if (document.readyState === 'loading') {",
    "  window.batch9CorruptPicker = { renderPicker };\n  if (document.readyState === 'loading') {"), context);
  const corrupt = { projectId: 'unreadable-synthetic', databaseProvider: null,
    loadError: { code: 'project_document_invalid', message: '案件資料無法讀取，請還原備份，或另建案件重新匯入。' } };
  window.JetStore.setProjects([corrupt, { projectId: 'healthy-synthetic', databaseProvider: 'sqlite', entityName: 'Synthetic' }]);
  window.batch9CorruptPicker.renderPicker(window.JetStore.getState());
  const row = dom.content.querySelector('[data-project-id="unreadable-synthetic"]'); assert.ok(row);
  assert.match(row.textContent, /案件資料無法讀取/); assert.match(row.textContent, /資料庫種類尚未確認/);
  assert.match(row.textContent, /還原備份.*另建案件重新匯入/); assert.doesNotMatch(row.textContent, /SQLite/);
  assert.match(dom.content.querySelector('[data-project-id="healthy-synthetic"]').textContent, /SQLite/);
});

test('batch9 medium source inspection cancellation stops the remaining files instead of recording a read failure and continuing', async () => {
  const reads = [];
  const w = importWizardFixture(api => {
    api.hostSelectFiles = async () => ({ files: [{ filePath: 'synthetic-a.csv', fileName: 'a.csv' }, { filePath: 'synthetic-b.csv', fileName: 'b.csv' }] });
    api.importInspectFile = payload => new Promise((resolve, reject) => reads.push({ payload, resolve, reject }));
  });
  w.open(); w.render().click('wizard-pick'); await settle();
  const error = new Error('Synthetic cancellation'); error.code = 'operation_cancelled'; reads[0].reject(error); await settle();
  assert.equal(reads.length, 1, 'do not start inspecting the next file after cancellation');
  assert.doesNotMatch(w.render().html(), /讀不出這個檔案/);
  assert.match(w.f.store.getState().messages.map(item => item.text).join('\n'), /已取消/);
});

test('batch9 medium successful import shows backend duplicate-source warnings without blocking or undoing the import', async () => {
  const imports = [];
  const w = importWizardFixture(api => {
    api.hostSelectFiles = async () => ({ files: [{ filePath: 'synthetic.csv', fileName: 'synthetic.csv' }] });
    api.importInspectFile = async () => ({ fileType: 'csv', columns: ['doc', 'amount'], encoding: 'utf-8', delimiter: ',' });
    api.importGlFromFile = payload => new Promise((resolve, reject) => imports.push({ payload, resolve, reject }));
  });
  w.open(); w.render().click('wizard-pick'); await settle(); w.render().click('wizard-confirm');
  assert.equal(imports.length, 1); imports[0].resolve({ batchId: 'committed', rowCount: 6, addedRowCount: 3, columns: ['doc', 'amount'], sources: [],
    warnings: ['檔名、工作表與列數都與既有來源相同，可能已匯入過，請確認。'],
    invalidatedResults: { validation: true, prescreen: true, filter: true }, staleState: { validation: false, prescreen: false, filter: false }, reportArtifacts: [] });
  await settle(); assert.equal(w.f.store.getState().importState.gl.batchId, 'committed');
  assert.match(w.f.store.getState().messages.map(item => item.text).join('\n'), /可能已匯入過/);
  assert.match(w.f.store.getState().messages.map(item => item.text).join('\n'), /匯入完成/);
});

test('batch9 constants keep separate fixed audit thresholds and unchanged full labels and custom defaults', () => {
  const f = batch8Fixture();
  assert.deepEqual(f.ui.PRESCREEN_DEFAULTS && plain(f.ui.PRESCREEN_DEFAULTS), { preparerMaxEntries: 11, accountMaxEntries: 11, trailingZeroDigits: 6 });
  const labels = Object.fromEntries(f.ui.PRESCREEN_KEY_OPTIONS.map(item => [item.value, item.label]));
  assert.equal(labels.trailingZeros, '金額尾數連續 6 個 0');
  assert.equal(labels.lowFrequencyPreparer, '編製分錄較少的人員（11 筆以下）');
  assert.equal(labels.lowFrequencyAccount, '使用較少的科目（11 筆以下）');
  assert.equal(f.ui.prescreenConditionDescription('trailingZeros'), '依本次測試的分錄，找出金額整數部分尾數連續 6 個 0 的分錄；不含小數，整數部分為 0 不列入。');
  assert.equal(f.ui.newFilterRule('customPreparerEntryCount').maxEntries, '11');
  assert.equal(f.ui.newFilterRule('customAccountEntryCount').maxEntries, '11');
  assert.equal(f.ui.newFilterRule('customTrailingZeros').digits, '3', 'the custom-card default is not the fixed six-zero rule');
  assert.equal(f.ui.newFilterRule('trailingDigits').keywords, '000000', 'KCT H is an exact literal pattern, not an audit threshold alias');
});

test('batch9 shared preview size stays fifty while fourth-step load-more remains two hundred', async () => {
  const f = batch9PagerLifecycleFixture('prescreen');
  assert.equal(f.ui.RESULT_PREVIEW_PAGE_SIZE, 50); assert.equal(f.ui.VALUE_PROFILE_LIMIT, 50);
  const page = await f.open(); assert.equal(f.requests[0].payload.pageSize, 50);
  page.more.fire('click'); assert.equal(f.requests.at(-1).payload.pageSize, 200);
});

test('batch9 report history uses its own fifty-file page rather than a query or audit limit', () => {
  const f = batch9Fixture(), dom = batch9TreeDom();
  assert.equal(f.ui.REPORT_HISTORY_PAGE_SIZE, 50); assert.equal(f.ui.DEV_TABLE_PAGE_SIZE, 50);
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/export-step.js'), 'utf8'), f.context);
  f.store.setReportArtifacts(Array.from({ length: 51 }, (_, i) => ({ artifactId: 'paper-' + i, kind: 'workingPaper', fileName: 'Synthetic-' + i + '.xlsx',
    generatedUtc: '2026-01-01T00:00:00Z', stale: false, fileState: 'asPublished', sourceRef: {} })));
  f.ui.renderStep('export', dom.content, f.store.getState());
  assert.equal(dom.content.querySelectorAll('[data-open-artifact]').length, 50);
  dom.content.querySelector('[data-history-page="1"]').fire('click');
  f.ui.renderStep('export', dom.content, f.store.getState());
  assert.equal(dom.content.querySelectorAll('[data-open-artifact]').length, 1);
});

// Full-validation GUI 112031834-83ece06749ab4a74aece1989cd8d4246 lost the first
// create-field value. Exercise real state notifications and both real renderers,
// rather than manually changing contentVersion or reproducing their memo logic.
function batch9CreateStartupFixture(configured = null) {
  const dom = batch9TreeDom(), requests = [], databaseRequests = [], window = { setTimeout: fn => { fn(); return 0; }, clearTimeout() {},
    CSS: { escape: value => value }, addEventListener() {}, JetApi: {
      projectListLocal: payload => new Promise((resolve, reject) => requests.push({ payload, resolve, reject })),
      systemDatabaseInfo: payload => new Promise((resolve, reject) => databaseRequests.push({ payload, resolve, reject }))
    } };
  dom.content.setAttribute('data-bind', 'synthetic-shell');
  const content = dom.element('main', { 'data-bind': 'content' });
  const picker = dom.element('section', { 'data-bind': 'project-picker' });
  dom.content.appendChild(content); dom.content.appendChild(picker);
  const context = vm.createContext({ window, document: dom.document });
  for (const file of ['state.js', 'ui-core.js', 'steps/create-step.js']) {
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context, { filename: file });
  }
  const appSource = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
  const databaseBegin = appSource.indexOf('        global.JetApi.systemDatabaseInfo({}).then(');
  const databaseEnd = appSource.indexOf('        // 與 databaseInfo 並行', databaseBegin);
  assert.ok(databaseBegin >= 0 && databaseEnd > databaseBegin);
  // Execute the actual bootstrap continuation with deferred JetApi responses, not
  // a duplicate implementation of when SQL configuration becomes authoritative.
  vm.runInContext(appSource.replace("  if (document.readyState === 'loading') {",
    '  window.batch9CreateStartup = { renderContent, renderPicker, beginDatabaseInfo: function () { return ' +
    appSource.slice(databaseBegin, databaseEnd).trim() +
    " } };\n  if (document.readyState === 'loading') {"), context);
  const store = window.JetStore, app = window.batch9CreateStartup;
  store.setSqlServerConfigured(configured); store.setView('workflow');
  function draw() {
    const state = store.getState();
    if (state.view === 'workflow') app.renderContent(state, store.STEPS[0]);
    else app.renderPicker(state);
  }
  draw(); store.subscribe(draw);
  let form, fields;
  const values = { caseName: 'B9 synthetic unsaved', projectCode: 'B9-code', entityName: 'B9 synthetic customer',
    periodStart: '2025-01-01', periodEnd: '2025-12-31', lastPeriodStart: '2025-12-01', databaseProvider: 'duckdb' };
  function enterDraft() {
    form = content.querySelector('[data-bind="create-form"]'); assert.ok(form);
    fields = Object.fromEntries(form.querySelectorAll('input, select').map(node => [node.getAttribute('name'), node]));
    for (const [name, value] of Object.entries(values)) { assert.ok(fields[name], name); fields[name].value = value; }
    fields.caseName.selectionStart = 3; fields.caseName.selectionEnd = 12; fields.caseName.selectionDirection = 'backward';
    fields.caseName.focus();
  }
  enterDraft();
  function assertDraftRetained() {
    assert.ok(content.querySelector('[data-bind="create-form"]') === form, 'a startup-only response must not replace the form node');
    for (const [name, node] of Object.entries(fields)) {
      assert.ok(form.querySelector('[name="' + name + '"]') === node, name + ' retains the same DOM node');
      assert.equal(node.isConnected, true, name + ' remains attached');
      assert.equal(node.value, values[name], name + ' keeps the entered value');
    }
    assert.ok(dom.document.activeElement === fields.caseName, 'focus stays on the same live input');
    assert.equal(fields.caseName.selectionStart, 3); assert.equal(fields.caseName.selectionEnd, 12);
    assert.equal(fields.caseName.selectionDirection, 'backward');
  }
  return { store, app, ui: window.JetUi, content, picker, requests, databaseRequests, get form() { return form; }, get fields() { return fields; },
    values, enterDraft, assertDraftRetained };
}

for (const result of ['success', 'failure']) test('batch9 actual late project-list response is ignored after opening create: ' + result, async () => {
  const f = batch9CreateStartupFixture();
  f.store.setView('picker');
  const task = f.ui.loadProjects(); assert.equal(f.requests.length, 1);
  f.picker.querySelector('[data-action="picker-new"]').fire('click');
  assert.equal(f.store.getState().view, 'workflow'); f.enterDraft();
  if (result === 'success') f.requests[0].resolve({ projects: [{ projectId: 'wrong-late-local', databaseProvider: 'sqlite' }] });
  else f.requests[0].reject(new Error('Synthetic late list error'));
  await task;
  f.assertDraftRetained();
  assert.equal(f.store.getState().projects.some(project => project.projectId === 'wrong-late-local'), false);
  assert.equal(f.store.getState().pickerFeedback, null);
  assert.doesNotMatch(f.store.getState().messages.map(message => message.text).join('\n'), /Synthetic late list error/);
});

test('batch9 late unconfigured SQL disables its option and explains it without resetting a selected provider or the create draft', async () => {
  const f = batch9CreateStartupFixture();
  f.fields.databaseProvider.value = f.values.databaseProvider = 'sqlServer';
  assert.equal(f.form.querySelector('option[value="sqlServer"]').disabled, false);
  const task = f.app.beginDatabaseInfo(); assert.equal(f.databaseRequests.length, 1);
  f.databaseRequests[0].resolve({ sqlServer: { configured: false, reachable: false } }); await task;
  f.assertDraftRetained();
  assert.equal(f.form.querySelector('option[value="sqlServer"]').disabled, true, 'configuration absence remains visible, not suppressed to preserve the draft');
  const hint = f.form.querySelector('[data-bind="databaseProvider-hint"]');
  assert.ok(hint && !hint.hidden); assert.match(hint.textContent, /沒有設定線上資料庫.*SQLite.*DuckDB/);
});

test('batch9 a later configured SQL response refreshes only option and hint while preserving the create form', async () => {
  const f = batch9CreateStartupFixture(false);
  assert.equal(f.form.querySelector('option[value="sqlServer"]').disabled, true);
  const task = f.app.beginDatabaseInfo(); f.databaseRequests[0].resolve({ sqlServer: { configured: true, reachable: false } }); await task;
  f.assertDraftRetained();
  assert.equal(f.form.querySelector('option[value="sqlServer"]').disabled, false);
  const hint = f.form.querySelector('[data-bind="databaseProvider-hint"]');
  assert.ok(!hint || hint.hidden || !hint.textContent.trim(), 'known-unconfigured hint must disappear when that fact is no longer current');
});

test('batch9 an initially unknown then configured SQL response does not rebuild the create draft', async () => {
  const f = batch9CreateStartupFixture(), task = f.app.beginDatabaseInfo();
  f.databaseRequests[0].resolve({ sqlServer: { configured: true, reachable: false } }); await task; f.assertDraftRetained();
  assert.equal(f.form.querySelector('option[value="sqlServer"]').disabled, false);
});

test('batch9 a failed SQL configuration query leaves unknown availability and the create draft unchanged', async () => {
  const f = batch9CreateStartupFixture(), task = f.app.beginDatabaseInfo();
  f.databaseRequests[0].reject(new Error('Synthetic configuration query failure')); await task; f.assertDraftRetained();
  assert.equal(f.store.getState().sqlServerConfigured, null);
  assert.equal(f.form.querySelector('option[value="sqlServer"]').disabled, false);
});

test('batch9 project picker still refreshes both project rows and online status after startup-only notifications', () => {
  const f = batch9CreateStartupFixture(), state = f.store.getState();
  f.app.renderPicker(state); assert.match(f.picker.textContent, /尚無本地案件/);
  f.store.subscribe(current => f.app.renderPicker(current));
  f.store.setProjects([{ projectId: 'picker-new-local', databaseProvider: 'duckdb' }], { reachable: false, message: 'Synthetic offline' });
  assert.match(f.picker.textContent, /picker-new-local/); assert.match(f.picker.textContent, /Synthetic offline/);
  f.store.setProjects([{ projectId: 'picker-next-online', databaseProvider: 'sqlServer' }], { reachable: true, principal: 'SYNTHETIC-ONLINE' });
  assert.match(f.picker.textContent, /picker-next-online/); assert.match(f.picker.textContent, /SYNTHETIC-ONLINE/);
  assert.doesNotMatch(f.picker.textContent, /picker-new-local|Synthetic offline/);
  f.store.setProjects(state.projects, { reachable: false, message: 'Synthetic disconnected' });
  assert.match(f.picker.textContent, /picker-next-online/); assert.match(f.picker.textContent, /Synthetic disconnected/);
});

// 2026-10-05 最後獨立複審 V9：人數用後端的完整人數，不用最多 50 列的清單長度；表格只列一部分時說清楚。
// 舊的預篩選結果沒有完整人數，照「較少使用之科目」的前例寫「未知，請重新執行預篩選」，不拿清單長度冒充。
test('V9 preparer count uses the full backend total, explains a partial table and marks old runs unknown', () => {
  const f = batch8Fixture();
  const item = f.v.PRESCREEN_ITEMS.find(item => item.title === '依分錄編製者彙總');
  const row = name => ({ createdBy: name, entryCount: 1, debitTotal: 1, creditTotal: 0, manualCount: 0 });
  const fifty = Array.from({ length: 50 }, (_, i) => row('U' + i));
  const many = { creatorSummary: { creators: fifty, totalPreparerCount: 61 } };
  assert.equal(item.status(many).text, '61 位人員');
  assert.equal(item.detail(many).prefix, '共 61 位人員，這裡列出分錄筆數最多的 50 位。');
  assert.equal(item.detail(many).rows.length, 50);
  const few = { creatorSummary: { creators: fifty.slice(0, 3), totalPreparerCount: 3 } };
  assert.equal(item.status(few).text, '3 位人員');
  assert.ok(!item.detail(few).prefix);
  assert.equal(item.status({ creatorSummary: { creators: fifty } }).text, '人員數未知，請重新執行預篩選');
});

// 2026-10-05 最後獨立複審 V7：修改案件資料只更新原本的 project 物件。各步驟以物件身分判斷「是否換了案件」，
// 換掉物件會把第三步的值摘要、第四步的明細與第五步展開的結果都當成舊案件清掉。
// 該清的結果只依後端回傳的失效清單處理；真的換案件時照原本全部重設。
test('V7 project metadata edits keep view state, clear only what the backend invalidates, and a project switch still resets', () => {
  const f = scenarioLifecycleFixture(), store = f.store, state = store.getState(), project = state.project;
  const saved = [{ name: 'Kept scenario', rationale: 'Synthetic', groups: [] }];
  store.setSavedScenarios(saved); f.api.syncViewState(state);
  f.api.view().openScenarios[0] = true; f.api.view().scenarioPreviews[0] = { rows: ['synthetic hit'] };
  const generation = state.dataGeneration;
  const none = { validation: false, prescreen: false, filter: false };

  store.updateProjectMetadata({ project: { projectId: 'sequence-fixture', entityName: 'Renamed client', projectCode: null },
    invalidatedResults: none, staleState: none, reportArtifacts: [] });
  assert.equal(store.getState().project, project, 'same case keeps the same object');
  assert.equal(project.entityName, 'Renamed client'); assert.equal(project.projectCode, null);
  assert.equal(store.getState().caseClient, 'Renamed client');
  assert.equal(store.getState().dataGeneration, generation);
  f.api.syncViewState(store.getState());
  assert.equal(f.api.view().openScenarios[0], true);
  assert.deepEqual(plain(f.api.view().scenarioPreviews[0]), { rows: ['synthetic hit'] });

  // 準備日改變時，後端讓預篩選與篩選命中失效：展開的情境保留，已載入的命中清掉，之後重新讀取。
  store.updateProjectMetadata({ project: { projectId: 'sequence-fixture', entityName: 'Renamed client', lastPeriodStart: '2025-06-30' },
    invalidatedResults: { validation: false, prescreen: true, filter: true }, staleState: none, reportArtifacts: [] });
  assert.equal(store.getState().project, project);
  assert.ok(store.getState().dataGeneration > generation);
  f.api.syncViewState(store.getState());
  assert.equal(f.api.view().openScenarios[0], true);
  assert.equal(f.api.view().scenarioPreviews[0], undefined, 'stale hits must reload');

  store.setProject({ projectId: 'another-case' }); f.api.syncViewState(store.getState());
  assert.deepEqual(plain(f.api.view().openScenarios), {});
});

test('V7 loaded validation detail pages survive a text-only metadata edit', () => {
  const f = batch8Fixture(), store = f.store, none = { validation: false, prescreen: false, filter: false };
  store.setLastRun('validate', batch8ValidationData());
  const view = f.v.detailView('validate'); view.expanded.completeness = true; view.pages['null:records'] = { rows: [1, 2] };
  store.updateProjectMetadata({ project: { ...store.getState().project, entityName: 'Edited only' },
    invalidatedResults: none, staleState: none, reportArtifacts: [] });
  assert.equal(f.v.detailView('validate'), view);
  assert.equal(f.v.detailView('validate').expanded.completeness, true);
  store.setProject({ projectId: 'another-case' });
  assert.notEqual(f.v.detailView('validate'), view);
});

// 2026-10-05 W2 裁定：兩個現金常用範本和新建條件一樣用「包含下層分類」（Q4），套用後的草稿與讀回都照實寫出。
test('W2 both cash templates select the cash category including its subcategories', () => {
  const f = kctFixture();
  for (const [key, mode] of [['cashDebitNonCashCredit', 'drNotCr'], ['cashCreditNonCashDebit', 'notDrCr']]) {
    f.api.applyTemplate(key);
    const rule = f.store.getState().filter.draft.groups[0].rules[0];
    assert.equal(rule.pairMode, mode);
    assert.equal(rule.categorySelection, 'subtree');
    assert.match(f.api.readBackHtml(f.store.getState().filter.draft), /包含下層分類/);
  }
});
