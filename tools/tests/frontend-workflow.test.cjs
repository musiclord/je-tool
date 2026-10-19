'use strict';
// 2026-10-16：依使用者要求將資料明細改為每頁 200 筆及前後換頁。首次失敗保留於
// artifacts/harness/runs/20261016-093720476-14d8f06e13d34b44ac2b371b7a26ad3f/contract-specialized.stdout.log。
// 同步舊的頁量與累加預期，保留排序、錯誤、重試及來源失效斷言。
// 第九節 D 改為名稱與動機常駐、單次儲存後清空草稿；移除舊表單開關操作並改驗證新流程。
// 原測試首次失敗：20261014-123035603-d123badefd7941119a02c58b1b431fae；保留請求、錯誤、失效與並行修改斷言。
// 測試程序的記憶體與時間上限，見 support/resource-guard.cjs（2026-10-13）。
require('./support/resource-guard.cjs');
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
// 共用夾具（假 DOM、usageJourneyFixture、第四步與第五步的夾具與合成資料）在 support/frontend-fixtures.cjs。
// 第 11 批（2026-10-14）刪掉本檔的副本改用模組；remapAppFixture 的根節點結構和模組不同，本檔保留自己那一份。
const { root, settle, plain, batch9TreeDom, batch7Dom, batch7ControlDom, validationFixture, batch8Fixture, batch8ValidationData, batch8Artifacts,
  savedG, usageJourneyFixture, filterOperationFixture, remapFilterPreviewFixture, overviewDialogFixture, remapPaper, remapPrescreen } = require('./support/frontend-fixtures.cjs');
// 本檔自己組的假後端也包進 contractJetApi，送出的資料與假回應照樣和正式後端的形狀核對（2026-10-15）。
const { contractJetApi, unchecked } = require('./support/action-contract.cjs');
// 上游修改清除篩選情境時的訊息。使用者 2026-10-13 勾選頁確認 12，原本是「前面的資料已更改，請重新設定篩選情境。」
// （第一次失敗：FrontendMapping 收據 20261013-141147763-5dc8acde89a2431d9568ff02be8bd79e）。
const UPSTREAM_RESET_MESSAGE = '前面步驟的資料已更改，已存的篩選情境已清除，請重新設定。';
// 已存滿 10 個情境時的提醒。使用者 2026-10-13 勾選頁確認 60，原本是「最多儲存 10 個篩選情境；請先移除既有情境。」（第一次失敗：FrontendMapping 收據 20261013-141147763-5dc8acde89a2431d9568ff02be8bd79e）。
const SCENARIO_LIMIT_MESSAGE = '最多可儲存 10 個篩選情境，請先移除不需要的情境。';

// 第九節 F 改用分類樹與共用搜尋框。舊夾具首次失敗：20261014-125419175-08e0cec4d9cf4360ac4af81713f531b3。
// 解析正式標記取代手寫控制項，保留原有分頁、失效、未儲存提醒與儲存 payload 斷言。
function accountEditorControls(ui, store) {
  const html = ui.accountMappingEditor.html(store.getState(), true), dom = batch9TreeDom();
  dom.content.innerHTML = html;
  ui.accountMappingEditor.bind(dom.content);
  for (const node of dom.content.querySelectorAll('button, input, form')) {
    const fire = node.fire.bind(node); node.fire = (name = 'click') => fire(name);
  }
  return { html, control(selector) {
    const node = dom.content.querySelector(selector === 'selection' ? '[data-account-select="0"]' : selector);
    assert.ok(node, selector + ' rendered'); return node;
  } };
}

function chooseAccountCategory(f, category) {
  f.change('[data-account-bulk-category]', category);
}

function scenarioLifecycleFixture() {
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const document = { querySelector: () => null, createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const source = fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8');
  vm.runInContext(source.replace("  Ui.registerStep('filter', render);",
    "  window.workflow = { completeScenarioSave, savedFeedbackText, filterCommitSnapshot, syncViewState, toWireDraft, addKctToDraft, removeKctFromActiveGroup, refreshAutomaticMetadata, kctPickerHtml, draftRuleProblems, ruleSummaryLabel, kctDescriptionHtml, bind, view: () => viewState };"), context);
  const store = window.JetStore, api = window.workflow;
  store.setProject({ projectId: 'sequence-fixture' }); api.syncViewState(store.getState());
  const item = letter => window.JetUi.FILTER_KCT_CHECKLIST.find(x => x.letter === letter);
  function toggle(letter, remove = false) {
    const draft = store.getState().filter.draft;
    api[remove ? 'removeKctFromActiveGroup' : 'addKctToDraft'](draft, item(letter));
    api.refreshAutomaticMetadata(draft, true); store.setFilterDraft(draft);
  }
  function bindRemoval(index, action = 'confirm-remove-scenario') {
    const pending = [], nodes = new Map(), actions = [];
    // 2026-10-13 第五步一次只存或刪一個情境：畫面改送 filterScenarioSave／Remove／Recheck，只帶正在處理的那一個情境；
    // 移除改送 filterScenarioRemove，假物件補上這個方法，四支移除測試的斷言不變。第一次失敗：FrontendMapping 收據 20261013-103553346-54ebed91711946dd8ff77c3dd1be7b23。
    window.JetApi = contractJetApi(Object.fromEntries(['filterCommit', 'filterScenarioRemove', 'filterPreview', 'queryFilterVoucherPage'].map(method =>
      [method, payload => new Promise((resolve, reject) => pending.push({method, payload, resolve, reject}))])));
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

test('automatic KCT names follow the next draft after saving without changing the saved G', () => {
  const f = scenarioLifecycleFixture();
  f.toggle('G');
  const saved = f.api.toWireDraft(f.store.getState().filter.draft);
  f.api.completeScenarioSave(f.api.filterCommitSnapshot(f.store.getState()),
    { scenarios: [saved], resultRef: { revision: 'r1' }, savedCount: 1 }, 0, saved);
  const draft = f.store.getState().filter.draft;
  assert.equal(draft.name, '');
  f.toggle('H'); assert.equal(draft.name, 'H');
  f.toggle('G', true); assert.equal(draft.name, 'H');
  f.toggle('G'); assert.equal(draft.name, 'G+H');
  f.toggle('H', true); assert.equal(draft.name, 'G 2', 'the next draft avoids the saved name');
  f.toggle('G', true); assert.equal(draft.name, '');
  assert.equal(f.store.getState().filter.savedScenarios[0].name, 'G');
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
  assert.match(f.api.savedFeedbackText(f.store.getState().filter.draft), /已儲存/);
  assert.doesNotMatch(f.api.savedFeedbackText(f.store.getState().filter.draft), /尚未儲存|保存/);
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

test('manual names survive repeated G H toggles before saving and remain in the saved scenario', () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  f.store.patchFilterDraftMeta({ name: '人工命名', rationale: '人工動機', __nameDirty: true, __rationaleDirty: true });
  f.toggle('H'); f.toggle('G', true); f.toggle('H', true); f.toggle('G');
  const snapshot = f.api.filterCommitSnapshot(f.store.getState()), saved = f.api.toWireDraft(snapshot.draft);
  assert.equal(snapshot.draft.name, '人工命名'); assert.equal(snapshot.draft.rationale, '人工動機');
  f.api.completeScenarioSave(snapshot, { scenarios: [saved], resultRef: { revision: 'r1' }, savedCount: 1 }, 0, saved);
  assert.equal(f.store.getState().filter.savedScenarios[0].name, '人工命名');
  assert.equal(f.store.getState().filter.savedScenarios[0].rationale, '人工動機');
  assert.equal(f.store.getState().filter.draft.name, '');
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

test('commit observers see the new list and cleared draft in the same notification', () => {
  const f = scenarioLifecycleFixture(); f.toggle('G');
  const snapshot = f.api.filterCommitSnapshot(f.store.getState()), saved = f.api.toWireDraft(snapshot.draft), list = [saved];
  let committed = 0;
  f.store.subscribe(state => {
    if (state.filter.savedScenarios === list) {
      committed++;
      assert.equal(state.filter.draft.__editingSavedRef, undefined);
      assert.equal(state.filter.draft.__editingIndex, undefined);
      assert.equal(state.filter.draft.groups.length, 0);
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
    op.pending[0].resolve({ scenario: { count: 1, voucherCount: 1, summaryRowCount: 1, populationScope: 'auditPeriod' } });
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
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-30（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  assert.equal(store.getState().reportArtifacts[1].catalogWarning, '檔案已產生，但報告清單沒有更新；重新開啟案件就會更新，不必重新產生。');
});

// 使用者 2026-10-19 裁定「回報成功並提醒」：報告檔已產生、卻寫不進操作紀錄時，回應的 warnings 帶提醒。
// 四種產生報告都經過 applyReportExport，提醒在這裡顯示；報告清單照常更新。
test('report exports show the audit-log reminder carried in the response warnings', () => {
  const window = { setTimeout, clearTimeout }, context = vm.createContext({ window });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const store = window.JetStore;
  store.setProject({ projectId: 'published' });
  const warning = '檔案已產生，但這次產生的檔案沒有寫入操作紀錄。';
  const artifact = { artifactId: 'prescreen-1', kind: 'prescreenReport', stale: false };
  store.applyReportExport({ artifact, reportArtifacts: [artifact], warnings: [warning] });
  assert.deepEqual(plain(store.getState().messages.filter(m => m.text === warning).map(m => m.level)), ['warn']);
  assert.deepEqual(plain(store.getState().reportArtifacts.map(a => a.artifactId)), ['prescreen-1']);
  store.applyReportExport({ artifact, reportArtifacts: [artifact], warnings: [] });
  assert.equal(store.getState().messages.filter(m => m.text === warning).length, 1, 'an export without the reminder adds nothing');
});

test('successful operations clear only their own stale state and workpaper does not require a criteria file', () => {
  const window = { setTimeout, clearTimeout }, context = vm.createContext({ window });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const store = window.JetStore;
  store.setProject({ projectId: 'synthetic-stale-review' });
  assert.deepEqual(JSON.parse(JSON.stringify(store.getState().staleState)), { validation: false, prescreen: false, filter: false },
    'a new case starts with nothing stale');
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
  // 2026-10-07 起後端回應多帶 filterScenarios，且一律和 filter 同值；以下模擬回應照實帶上。
  store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: false, filter: false }, reportArtifacts: [] });
  assert.deepEqual(JSON.parse(JSON.stringify(store.getState().staleState)), { validation: false, prescreen: false, filter: false },
    'never-run results must not be announced as stale');
  store.setLastRun('validate', { resultRef: { runId: 'v' } });
  store.setLastRun('prescreen', { resultRef: { runId: 'p' } });
  store.setFilterResultRef({ revision: 'r', populationScope: 'auditPeriod' });
  store.setCalendarState({});
  store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
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
  window.JetUi.applyLoadedProject({ project: { projectId: 'synthetic-no-stale' } }, { stayOnCurrentStep: true });
  assert.deepEqual(JSON.parse(JSON.stringify(store.getState().staleState)), { validation: false, prescreen: false, filter: false },
    'a load without stale flags announces nothing as stale');
  assert.equal(window.JetUi.staleNoticeHtml, undefined, 'no page-wide stale notice is offered');
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
  const table = element(), page = element(), count = element(), apply = element(), clear = element();
  clear.hidden = true;
  table.getBoundingClientRect = () => ({ top: 0, bottom: 200, left: 0, right: 500 });
  const boxes = m.rows.map((r, i) => {
    const box = element({ 'data-account-select': String(i) });
    box.row = element(); box.row.getBoundingClientRect = () => ({ top: 30 + i * 30 - table.scrollTop, bottom: 60 + i * 30 - table.scrollTop });
    box.closest = s => s === 'tr' ? box.row : s === '[data-account-select-cell]' ? { querySelector: () => box } : null;
    return box;
  });
  table.querySelector = s => s === 'thead' ? { getBoundingClientRect: () => ({ bottom: 30 }) } : null;
  const card = { querySelectorAll: () => boxes, querySelector: s => ({ '.account-editor__table': table,
    '[data-account-select-page]': page, '[data-account-selection-count]': count, '[data-account-apply]': apply,
    '[data-account-clear-selection]': clear })[s] };
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
  start(1);
  // 2026-10-14 Gui：「清除勾選」在拖曳途中展開會把表格往下推，游標下的列跟著跑掉。拖曳中先不出現，放開後才展開。
  assert.equal(clear.hidden, true, 'the clear button stays hidden while dragging');
  move(4); assert.deepEqual(selected(), [1,2,3,4]);
  assert.equal(clear.hidden, true, 'the clear button stays hidden while dragging');
  move(2); assert.deepEqual(selected(), [1,2], 'shrinking a drag restores rows outside the current range'); end();
  assert.equal(clear.hidden, false, 'the clear button appears once the drag is released');
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
  // 2026-10-10 使用者選擇精簡 Excel 配對說明：仍寫實際欄名，不再舉 Cash、Revenue 為例。
  assert.match(validate, /「Standardized Account Name\*」欄填入分類後存檔/);
  assert.match(validate, /<span>篩選結果<\/span>/);
  // 2026-10-11 第三次調整把名稱裡的 JE 放進字標方框，標記多了一層，畫面與報讀的名稱仍是「JE Tool」。
  // 原本比對整段標記的斷言首敗：20261011-141858880-ac28cf188b664288a5663fe76a40dd84；改比對標題的文字，名稱本身照舊鎖住。
  const title = fs.readFileSync(path.join(root, '../index.html'), 'utf8').match(/<h1 class="app-header__title">([\s\S]*?)<\/h1>/);
  assert.ok(title, 'index.html 缺少頂部標題');
  assert.equal(title[1].replace(/<[^>]*>/g, ''), 'JE Tool');
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

test('saving clears the submitted draft and preview, preserves later drafts, and ignores other projects', () => {
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
  const preview = { count: 2, voucherCount: 1, summaryRowCount: 2, previewRows: [] };
  store.setFilterPreview(preview);
  let snapshot = window.testSave.filterCommitSnapshot(store.getState());
  const saved = { name: '月底分錄', rationale: '查核期末', groups: draft.groups };
  const response = { resultRef: { revision: 1 }, scenarios: [saved], savedCount: 1 };
  window.testSave.completeScenarioSave(snapshot, response, 0, saved);
  assert.equal(window.testSave.pane(), 'filter');
  assert.notEqual(store.getState().filter.draft, draft);
  assert.deepEqual(plain(store.getState().filter.draft), { name: '', rationale: '', groups: [] });
  assert.equal(store.getState().filter.preview, null);
  assert.equal(store.getState().filter.savedScenarios[0], saved);
  assert.equal(store.getFilterDraftRev(), snapshot.revision + 1, 'clearing invalidates the submitted conditions');
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
  const window = { setTimeout, clearTimeout, JetApi: contractJetApi({
    queryAccountMappingPage: payload => new Promise((resolve, reject) => reads.push({ payload, resolve, reject })),
    accountMappingSave: payload => new Promise((resolve, reject) => writes.push({ payload, resolve, reject }))
  }) };
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
  function render() { return accountEditorControls(ui, store); }
  render();
  assert.equal(reads.length, 1);
  assert.equal(reads[0].payload.pageSize, 200); // 2026-10-16 統一明細頁量，取代 2026-10-09 的 50 筆；其餘斷言保留。
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
  reads[2].resolve({ rows: [{ accountCode: 'r', accountName: 'Revenue', categoryId: 'builtin.revenue' }], nextCursor: null, totalCount: 201 });
  await settle(); page = render();
  assert.match(page.html, /data-page-position[^>]*>2\/2</);
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

// 使用者 2026-10-19 裁定「回報成功並提醒」：存檔後讀不回科目配對摘要時，後端回應不帶摘要欄位，只帶提醒與失效結果。
// 畫面保留原本的摘要與比對用的批次，套用失效結果並顯示提醒；已存的分類不再待儲存，之後改分類照常可以再存。
test('account editor keeps the previous summary when the saved summary cannot be read back', async () => {
  const reads = [], writes = [];
  const window = { setTimeout, clearTimeout, JetApi: contractJetApi({
    queryAccountMappingPage: payload => new Promise((resolve, reject) => reads.push({ payload, resolve, reject })),
    accountMappingSave: payload => new Promise((resolve, reject) => writes.push({ payload, resolve, reject }))
  }) };
  const document = { createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'account-mapping-editor.js'])
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context, { filename: file });
  const store = window.JetStore, ui = window.JetUi;
  store.setProject({ projectId: 'synthetic' });
  store.setTaxonomy({ revision: 1, categories: [{ categoryId: 'builtin.cash', label: 'Cash' }, { categoryId: 'builtin.revenue', label: 'Revenue' }] });
  const summary = { batchId: 'before', rowCount: 2, hasAnyCategory: true, hasRevenue: false, hasCounterpart: true, blankCategoryCount: 1 };
  store.setAccountMappingState(summary);
  ui.run = (name, work) => Promise.resolve().then(work);
  function render() { return accountEditorControls(ui, store); }
  function applyRevenue() {
    let page = render(); page.control('selection').checked = true; page.control('selection').fire('change');
    page = render(); page.control('[data-account-bulk-category]').value = 'builtin.revenue';
    page.control('[data-account-bulk-category]').fire('change');
    render().control('[data-account-apply]').fire('click');
  }
  render();
  reads[0].resolve({ rows: [{ accountCode: 'A1', accountName: 'One', categoryId: 'builtin.cash' }], nextCursor: null });
  await settle();
  applyRevenue();
  render().control('[data-account-save]').fire('click'); await settle();
  const warning = '變更已儲存，但畫面上的科目配對摘要沒有更新；重新開啟案件就會更新，不必重做變更。';
  writes[0].resolve(unchecked('存檔後讀不回科目配對摘要時的回應；正式旅程讀得到摘要，走不到這裡', { warnings: [warning],
    invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true, accountMapping: false },
    staleState: { validation: false, prescreen: true, filter: false }, reportArtifacts: [], reportArtifactWarning: null }));
  await settle();
  assert.equal(store.getState().importState.accountMapping, summary, 'the previous summary stays on screen');
  assert.deepEqual(plain(store.getState().messages.filter(m => m.text === warning).map(m => m.level)), ['warn']);
  assert.equal(store.getState().staleState.prescreen, true);
  assert.equal(ui.accountMappingEditor.pendingCount(), 0);
  assert.match(render().html, /data-account-current="0" data-category-id="builtin.revenue"/);
  assert.equal(reads.length, 1, 'the editor keeps its loaded page instead of starting over');
});

// 使用者 2026-10-08 裁定「跨頁保留，一次儲存」：勾選與已套用的分類在換頁後都保留，最後按一次儲存；
// 超過 500 筆時分批送出，失敗的那一批之後仍待儲存。
test('account editor keeps selections and pending categories across pages and saves them together', async () => {
  const reads = [], writes = [], messages = [];
  const window = { setTimeout, clearTimeout, JetApi: contractJetApi({
    queryAccountMappingPage: payload => new Promise((resolve, reject) => reads.push({ payload, resolve, reject })),
    accountMappingSave: payload => new Promise((resolve, reject) => writes.push({ payload, resolve, reject }))
  }) };
  const document = { createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'account-mapping-editor.js'])
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context, { filename: file });
  const store = window.JetStore, ui = window.JetUi;
  store.setProject({ projectId: 'synthetic' });
  store.setTaxonomy({ revision: 1, categories: [
    { categoryId: 'builtin.cash', label: 'Cash' }, { categoryId: 'builtin.revenue', label: 'Revenue' }
  ] });
  ui.run = (name, work) => Promise.resolve().then(work).catch(error => messages.push(error.message));
  function render() { return accountEditorControls(ui, store); }
  function selectFirstRow() { const page = render(); page.control('selection').checked = true; page.control('selection').fire('change'); }
  render();
  reads[0].resolve({ rows: [{ accountCode: 'A1', accountName: 'One', categoryId: null },
    { accountCode: 'A2', accountName: 'Two', categoryId: 'builtin.cash' }], nextCursor: 'page-2' });
  await settle();
  selectFirstRow();
  render().control('[data-account-next]').fire('click');
  reads[1].resolve({ rows: [{ accountCode: 'B1', accountName: 'Three', categoryId: 'builtin.cash' }], nextCursor: null });
  await settle();
  assert.match(render().html, /已選 1 個科目（其中 1 個在其他頁）/);
  selectFirstRow();
  let page = render(); page.control('[data-account-bulk-category]').value = 'builtin.revenue';
    page.control('[data-account-bulk-category]').fire('change');
  render().control('[data-account-apply]').fire('click');
  assert.match(render().html, /共 2 個科目待儲存/);
  assert.doesNotMatch(render().html, /data-account-prev disabled/, 'pending categories do not block paging');
  render().control('[data-account-prev]').fire('click');
  assert.equal(reads[2].payload.cursor, null);
  reads[2].resolve({ rows: [{ accountCode: 'A1', accountName: 'One', categoryId: null },
    { accountCode: 'A2', accountName: 'Two', categoryId: 'builtin.cash' }], nextCursor: 'page-2' });
  await settle();
  assert.match(render().html, /data-account-current="0" data-category-id="builtin.revenue">Revenue<span class="account-editor__pending">/);
  assert.equal(ui.accountMappingEditor.pendingCount(), 2);
  render().control('[data-account-save]').fire('click'); await settle();
  assert.deepEqual(JSON.parse(JSON.stringify(writes[0].payload.changes)).sort((a, b) => a.accountCode.localeCompare(b.accountCode)),
    [{ accountCode: 'A1', categoryId: 'builtin.revenue' }, { accountCode: 'B1', categoryId: 'builtin.revenue' }]);
  writes[0].resolve({ batchId: 'saved-1', rowCount: 3, hasAnyCategory: true }); await settle();
  assert.match(render().html, /科目分類已儲存/);
  assert.equal(ui.accountMappingEditor.pendingCount(), 0);

  // 501 個改動分成 500 與 1 兩批；第二批失敗時只剩那 1 筆待儲存。
  render().control('[data-account-search]').fire('submit');
  reads[3].resolve({ rows: Array.from({ length: 501 }, (_, i) => ({ accountCode: 'C' + i, accountName: 'Bulk', categoryId: null })), nextCursor: null });
  await settle();
  page = render(); page.control('[data-account-select-page]').checked = true; page.control('[data-account-select-page]').fire('change');
  page = render(); page.control('[data-account-bulk-category]').value = 'builtin.cash';
    page.control('[data-account-bulk-category]').fire('change');
  render().control('[data-account-apply]').fire('click');
  render().control('[data-account-save]').fire('click'); await settle();
  assert.equal(writes[1].payload.changes.length, 500);
  writes[1].resolve({ batchId: 'saved-2', rowCount: 501, hasAnyCategory: true }); await settle();
  assert.equal(writes[2].payload.changes.length, 1);
  writes[2].reject(new Error('second batch failed')); await settle();
  assert.deepEqual(messages, ['second batch failed']);
  assert.equal(ui.accountMappingEditor.pendingCount(), 1);
  assert.match(render().html, /共 1 個科目待儲存/);
});

// 使用者 2026-10-08 裁定「跨頁保留，一次儲存」：離開第四步前，如果還有沒儲存的科目分類就先詢問；
// 留在這裡、先不儲存仍可離開、儲存並繼續三種選擇都要照字面發生。帳務配對儲存只清除預篩選與篩選，不清除驗證。
test('pending account categories ask before leaving the validation step and honour each choice', async () => {
  const f = usageJourneyFixture('validate'), eligibleValidation = f.state.lastRuns.validate;
  f.dom.document.body = f.dom.element('body'); f.dom.document.removeEventListener = () => {};
  // 前面幾步都完成，第五步才可以進入。匯入或配對改變會讓科目清單重新載入，所以先準備好再改分類。
  f.store.setImportResult('gl', { batchId: 'g', columns: ['Doc'], rowCount: 1 });
  f.store.setImportResult('tb', { batchId: 't', columns: ['Acc'], rowCount: 1 });
  f.store.setMappingCommitted('tb', { mapping: {}, mode: 'closing' });
  f.state.lastRuns.validate = null; f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed' }); f.store.setStepIndex(3); f.draw();
  f.request('queryAccountMappingPage').resolve({ rows: [{ accountCode: '1000', accountName: '合成科目', categoryId: 'builtin.cash' }], nextCursor: null });
  await settle(); f.draw();
  f.change('[data-account-select-page]', true); chooseAccountCategory(f, 'builtin.revenue'); f.draw();
  f.click('[data-account-apply]'); f.draw();
  f.state.lastRuns.validate = eligibleValidation;
  assert.equal(f.window.JetUi.stepGate(f.state, 4).ok, true);
  const editor = f.window.JetUi.accountMappingEditor;
  const modal = () => f.dom.document.body.querySelector('[data-bind="account-pending-modal"]');
  function choose(choice) {
    const dialog = modal(); assert.ok(dialog, 'the confirmation is shown');
    dialog.fire('click', { target: dialog.querySelector('[data-pending-choice="' + choice + '"]') });
  }
  assert.equal(editor.pendingCount(), 1);
  f.window.JetUi.gotoStep(4);
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-41（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  assert.match(modal().textContent, /^科目分類尚未儲存有 1 個科目的分類尚未儲存。後續步驟只會使用已儲存的分類/);
  assert.equal(f.state.currentStepIndex, 3, 'nothing moves before the auditor chooses');
  choose('stay'); await settle();
  assert.equal(modal(), null); assert.equal(f.state.currentStepIndex, 3);
  f.window.JetUi.gotoStep(4); choose('continue'); await settle();
  assert.equal(f.state.currentStepIndex, 4, 'continuing without saving still leaves the step');
  assert.equal(editor.pendingCount(), 1, 'the unsaved categories stay on the screen for later');
  assert.equal(f.requests.some(r => r.action === 'accountMappingSave'), false);
  f.window.JetUi.gotoStep(3);
  assert.equal(f.state.currentStepIndex, 3, 'returning to the validation step does not ask');
  f.window.JetUi.gotoStep(4); choose('save');
  assert.deepEqual(plain(f.request('accountMappingSave').payload.changes), [{ accountCode: '1000', categoryId: 'builtin.revenue' }]);
  assert.equal(f.state.currentStepIndex, 3, 'the step changes only after the save succeeds');
  f.request('accountMappingSave').resolve({ batchId: 'saved', rowCount: 1, hasAnyCategory: true,
    invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: true, filter: false }, reportArtifacts: [] });
  await settle(); await settle();
  assert.equal(editor.pendingCount(), 0);
  assert.equal(f.state.currentStepIndex, 4);
  assert.equal(f.state.filter.savedScenarios.length, 0, 'saving categories clears saved scenarios as ruled on 2026-10-07');
});

// 回首頁或結束時選「不儲存」，只詢問一次就繼續離開；改動不保留是對話框寫明的結果。
test('leaving the case with unsaved account categories asks once and then departs', async () => {
  const f = usageJourneyFixture('validate');
  f.dom.document.body = f.dom.element('body'); f.dom.document.removeEventListener = () => {};
  f.state.lastRuns.validate = null; f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed' }); f.store.setStepIndex(3); f.draw();
  f.request('queryAccountMappingPage').resolve({ rows: [{ accountCode: '1000', accountName: '合成科目', categoryId: 'builtin.cash' }], nextCursor: null });
  await settle(); f.draw();
  f.change('[data-account-select-page]', true); chooseAccountCategory(f, 'builtin.revenue'); f.draw();
  f.click('[data-account-apply]'); f.draw();
  const modals = () => f.dom.document.body.querySelectorAll('[data-bind="account-pending-modal"]');
  f.window.JetUi.backToPickerFromHeader();
  assert.equal(modals().length, 1);
  assert.match(modals()[0].textContent, /離開案件後，這些改動不會保留。/);
  const dialog = modals()[0];
  dialog.fire('click', { target: dialog.querySelector('[data-pending-choice="continue"]') }); await settle();
  assert.equal(modals().length, 0, 'choosing not to save does not ask again');
  assert.ok(f.requests.some(r => r.action === 'projectReleaseLock' || r.action === 'projectSaveProgress'), 'departure continues');
  assert.equal(f.requests.some(r => r.action === 'accountMappingSave'), false);
});

// 在第四步留下一個未儲存的科目分類；前面幾步都完成，旅程夾具原本就有 3 個已存情境，第五、六步都可以進入。
async function pendingCategoryJourney() {
  const f = usageJourneyFixture('validate'), eligibleValidation = f.state.lastRuns.validate;
  f.dom.document.body = f.dom.element('body'); f.dom.document.removeEventListener = () => {};
  f.store.setImportResult('gl', { batchId: 'g', columns: ['Doc'], rowCount: 1 });
  f.store.setImportResult('tb', { batchId: 't', columns: ['Acc'], rowCount: 1 });
  f.store.setMappingCommitted('tb', { mapping: {}, mode: 'closing' });
  f.state.lastRuns.validate = null; f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed' }); f.store.setStepIndex(3); f.draw();
  f.request('queryAccountMappingPage').resolve({ rows: [{ accountCode: '1000', accountName: '合成科目', categoryId: 'builtin.cash' }], nextCursor: null });
  await settle(); f.draw();
  applyPendingCategory(f, 'builtin.revenue');
  f.state.lastRuns.validate = eligibleValidation;
  return f;
}

function applyPendingCategory(f, category) {
  f.change('[data-account-select-page]', true); chooseAccountCategory(f, category); f.draw();
  f.click('[data-account-apply]'); f.draw();
  assert.equal(f.window.JetUi.accountMappingEditor.pendingCount(), 1);
}

function resolveCategorySave(f) {
  f.request('accountMappingSave').resolve({ batchId: 'saved-' + f.requests.length, rowCount: 1, hasAnyCategory: true,
    invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: true, filter: false }, reportArtifacts: [] });
}

// 儲存科目分類會清除預篩選結果與篩選情境（2026-10-07 裁定），對話框要先寫明；會清掉情境時預設停在「留在這裡」。
// 存完後原本要去的第六步因為沒有情境而不能進入，改到最遠還能進入的第五步。
test('the pending-categories dialog says what saving clears and lands on the furthest open step', async () => {
  const f = await pendingCategoryJourney();
  f.store.setLastRun('prescreen', { resultRef: { runId: 'p1' } });
  assert.equal(f.window.JetUi.stepGate(f.state, 5).ok, true, 'saved scenarios make the export step reachable');
  const modal = () => f.dom.document.body.querySelector('[data-bind="account-pending-modal"]');
  f.window.JetUi.gotoStep(5);
  assert.match(modal().textContent, /後續步驟只會使用已儲存的分類；改動會留在這個畫面，回來仍可儲存。儲存會清除預篩選結果和第五步的篩選情境。/);
  assert.equal(f.dom.document.activeElement.getAttribute('data-pending-choice'), 'stay', 'Enter does not clear the saved scenarios');
  modal().fire('click', { target: modal().querySelector('.modal__actions [data-pending-choice="save"]') });
  resolveCategorySave(f); await settle(); await settle();
  assert.equal(f.state.filter.savedScenarios.length, 0);
  assert.equal(f.window.JetUi.stepGate(f.state, 5).ok, false);
  assert.equal(f.state.currentStepIndex, 4, 'the save moves on to the filter step instead of staying behind');
  assert.ok(f.state.messages.some(m => m.text === UPSTREAM_RESET_MESSAGE));

  // 沒有情境、沒有預篩選結果時，對話框不提清除，預設按鈕回到「儲存並繼續」。
  // 夾具的驗證摘要只有放行需要的欄位，和 pendingCategoryJourney 一樣先拿掉再畫第四步。
  // 存完後科目清單沿用原本那一頁，這一列已是 builtin.revenue，改回 builtin.cash 才有未儲存的改動。
  f.window.JetUi.gotoStep(3);
  const validation = f.state.lastRuns.validate; f.state.lastRuns.validate = null; f.draw('validate');
  applyPendingCategory(f, 'builtin.cash');
  f.state.lastRuns.validate = validation;
  f.window.JetUi.gotoStep(4);
  assert.doesNotMatch(modal().textContent, /儲存會清除/);
  assert.equal(f.dom.document.activeElement.getAttribute('data-pending-choice'), 'save');
});

// 原生視窗每按一次關閉就呼叫一次 exitApp；對話框開著時不再疊第二個。結束時的三種選擇都照字面發生。
for (const finalChoice of ['continue', 'save']) test('closing twice with unsaved categories opens one dialog, staying stays and "' + finalChoice + '" leaves', async () => {
  const f = await pendingCategoryJourney();
  f.store.setView('workflow');
  const modals = () => f.dom.document.body.querySelectorAll('[data-bind="account-pending-modal"]');
  const choose = choice => { const dialog = modals()[0]; dialog.fire('click', { target: dialog.querySelector('.modal__actions [data-pending-choice="' + choice + '"]') }); };
  const leaving = () => f.requests.some(r => r.action === 'projectSaveProgress');
  f.window.JetUi.exitApp(); f.window.JetUi.exitApp();
  assert.equal(modals().length, 1, 'a second close request does not stack another dialog');
  assert.match(modals()[0].textContent, /結束後，這些改動不會保留。儲存會清除第五步的篩選情境。/);
  choose('stay'); await settle();
  assert.equal(modals().length, 0);
  assert.equal(leaving(), false, 'staying does not start leaving');
  f.window.JetUi.exitApp();
  assert.equal(modals().length, 1, 'closing again asks again');
  choose(finalChoice);
  if (finalChoice === 'save') {
    assert.equal(leaving(), false, 'leaving waits for the save');
    resolveCategorySave(f);
  }
  await settle(); await settle();
  assert.equal(modals().length, 0, 'the dialog is not asked a second time');
  assert.ok(leaving(), 'leaving continues');
  assert.equal(f.state.busyLabel, '儲存並結束');
  assert.equal(f.requests.some(r => r.action === 'accountMappingSave'), finalChoice === 'save');
  assert.equal(f.window.JetUi.accountMappingEditor.pendingCount(), finalChoice === 'save' ? 0 : 1);
});

test('running prescreen asks first only when account categories are unsaved', async () => {
  const f = batch8Fixture(); f.store.setLastRun('validate', batch8ValidationData('v-guard'));
  const calls = []; let answer = false;
  f.ui.accountMappingEditor.pendingCount = () => 2;
  f.ui.accountMappingEditor.confirmPending = (consequence, label) => { calls.push([consequence, label]); return Promise.resolve(answer); };
  f.controls().click('[data-action="run-prescreen"]'); await settle();
  assert.deepEqual(calls, [['預篩選只會使用已儲存的分類。', '先不儲存，直接執行']]);
  assert.equal(f.requests.filter(r => r.action === 'prescreenRun').length, 0, 'staying keeps prescreen from running');
  answer = true;
  f.controls().click('[data-action="run-prescreen"]'); await settle();
  assert.equal(f.requests.filter(r => r.action === 'prescreenRun').length, 1);
  f.requests.filter(r => r.action === 'prescreenRun').at(-1).resolve({ resultRef: { runId: 'p-guard' } }); await settle();
  f.ui.accountMappingEditor.pendingCount = () => 0;
  f.controls().click('[data-action="run-prescreen"]');
  assert.equal(f.requests.filter(r => r.action === 'prescreenRun').length, 2, 'without pending categories the request is sent at once');
  assert.equal(calls.length, 2);
});

// 使用者 2026-10-15 在待決事項勾選頁裁定（U2）：重新匯入、確認配對或用 Excel 匯入科目配對，都會丟掉第四步還沒儲存的科目分類，
// 送出前先用同一個「尚未儲存的分類」對話框詢問。選「留在這裡」不送出；選繼續才送出。沒有改動時照舊直接送出，
// 由下方既有的匯入、配對與科目配對檔測試涵蓋。
// 這幾個動作本身就會清除或取代已儲存的分類，先存也留不住，所以對話框不提供「儲存並繼續」
// （實作時發現，使用者 2026-10-15 選擇「拿掉儲存，只留兩個按鈕」）。第三欄記錄呼叫端傳的 saveable。
// 第一次失敗：FrontendMapping 收據 20261015-072925964-d0a95b04d18f4c7dafaebb322576c444，當時預期三個按鈕的文字；
// 這些測試是本次新增的，依使用者的新選擇改預期。
function stubPendingCategories(f) {
  const editor = f.window.JetUi.accountMappingEditor, calls = [];
  let answer = false;
  editor.pendingCount = () => 1;
  editor.confirmPending = (consequence, label, options) => { calls.push([consequence, label, options ? options.saveable : undefined]); return Promise.resolve(answer); };
  return { calls, proceed() { answer = true; } };
}

for (const kind of ['gl', 'tb']) test(kind + ' import asks about unsaved account categories before sending', async () => {
  const f = usageJourneyFixture('import');
  const action = kind === 'gl' ? 'importGlFromFile' : 'importTbFromFile';
  f.draw(); f.click('[data-task-toggle="' + kind + '"]'); f.draw();
  f.click('[data-action="wizard-replace-' + kind + '"]');
  f.request('hostSelectFiles').resolve({ files: [{ filePath: 'synthetic.csv', fileName: 'synthetic.csv' }] }); await settle();
  f.request('importInspectFile').resolve({ fileType: 'csv', columns: ['Code', 'Amount'], encoding: 'utf-8', delimiter: ',' }); await settle();
  f.draw();
  const pending = stubPendingCategories(f);
  f.click('[data-action="wizard-confirm"]'); await settle();
  assert.deepEqual(pending.calls, [['匯入後，第四步已儲存和未儲存的科目分類都會清除。', '繼續匯入', false]]);
  assert.equal(f.requests.some(r => r.action === action), false, 'staying keeps the import from being sent');
  pending.proceed(); f.draw();
  f.click('[data-action="wizard-confirm"]'); await settle();
  assert.equal(f.requests.filter(r => r.action === action).length, 1);
});

for (const kind of ['gl', 'tb']) test(kind + ' mapping commit asks about unsaved account categories before sending', async () => {
  const f = usageJourneyFixture('mapping');
  const mapping = prepareUsageMapping(f, kind), mode = kind === 'gl' ? 'signed' : 'direct';
  f.store.setMappingCommitted(kind, { mapping, mode, options: kind === 'gl' ? plain(f.state.mapping.gl.options) : null });
  f.draw(); f.click('[data-action="remap-' + kind + '"]'); f.draw();
  const action = kind === 'gl' ? 'mappingCommitGl' : 'mappingCommitTb';
  const pending = stubPendingCategories(f);
  f.click('[data-action="commit-' + kind + '"]'); await settle();
  assert.deepEqual(pending.calls, [['確認配對後，第四步已儲存和未儲存的科目分類都會清除。', '繼續確認', false]]);
  assert.equal(f.requests.some(r => r.action === action), false, 'staying keeps the mapping from being committed');
  pending.proceed();
  f.click('[data-action="commit-' + kind + '"]'); await settle();
  assert.equal(f.requests.filter(r => r.action === action).length, 1);
});

test('importing an account mapping file asks about unsaved account categories before choosing the file', async () => {
  const f = usageJourneyFixture('validate');
  f.state.lastRuns.validate = null; f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed' });
  f.draw(); f.click('[data-action="toggle-account-excel"]'); f.draw(); f.click('[data-action="toggle-account-excel"]'); f.draw();
  const pending = stubPendingCategories(f);
  f.click('[data-action="import-account-mapping"]'); await settle();
  assert.deepEqual(pending.calls, [['匯入科目配對檔後，檔案內容會取代第四步的科目分類，這些改動不會保留。', '繼續匯入', false]]);
  assert.equal(f.requests.some(r => r.action === 'hostSelectFile'), false, 'staying does not open the file dialog');
  pending.proceed();
  f.click('[data-action="import-account-mapping"]'); await settle();
  assert.equal(f.requests.filter(r => r.action === 'hostSelectFile').length, 1);
});

// 同一個裁定的實際對話框：只有「留在這裡」和繼續兩個按鈕，不寫儲存會清除什麼，預設停在「留在這裡」。
test('the dialog before importing an account mapping file offers only staying or continuing', async () => {
  const f = await pendingCategoryJourney();
  // 夾具的驗證摘要只有放行需要的欄位，畫第四步前先拿掉（同 pendingCategoryJourney）。
  f.state.lastRuns.validate = null;
  f.click('[data-action="toggle-account-excel"]'); f.draw();
  const modal = () => f.dom.document.body.querySelector('[data-bind="account-pending-modal"]');
  f.click('[data-action="import-account-mapping"]');
  assert.equal(modal().querySelector('.modal__body').textContent,
    '有 1 個科目的分類尚未儲存。匯入科目配對檔後，檔案內容會取代第四步的科目分類，這些改動不會保留。');
  assert.deepEqual(modal().querySelectorAll('.modal__actions button').map(button => button.textContent), ['留在這裡', '繼續匯入']);
  assert.equal(f.dom.document.activeElement.getAttribute('data-pending-choice'), 'stay');
  modal().fire('click', { target: modal().querySelector('[data-pending-choice="continue"]') }); await settle();
  assert.equal(f.requests.filter(r => r.action === 'hostSelectFile').length, 1, 'continuing opens the file dialog');
  assert.equal(f.requests.some(r => r.action === 'accountMappingSave'), false);
});

// 使用者 2026-10-15 在待決事項勾選頁裁定（U3）：分類設定儲存後，要套用到已刪分類的待儲存改動拿掉，
// 在科目表上方寫出取消了幾個科目；其他待儲存改動保留，「儲存科目分類」只送留下來的改動。
// 修改前，已刪分類的改動一直留著，每次儲存都被後端以「科目或分類已變更」拒絕。
test('deleting a category drops only the pending account changes that used it and says how many', async () => {
  const f = usageJourneyFixture('validate');
  const builtIns = plain(f.window.JetUi.taxonomyCategories({}));
  f.store.setTaxonomy({ revision: 2, categories: builtIns.concat([
    { categoryId: 'custom.synthetic', label: '合成分類', ordinal: 99, semanticRole: 'others', isBuiltIn: false }]) });
  f.state.lastRuns.validate = null; f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed' }); f.draw();
  f.request('queryAccountMappingPage').resolve({ rows: [
    { accountCode: '1000', accountName: '合成科目一', categoryId: 'builtin.cash' },
    { accountCode: '2000', accountName: '合成科目二', categoryId: 'builtin.cash' }], nextCursor: null });
  await settle(); f.draw();
  f.change('[data-account-select="0"]', true); chooseAccountCategory(f, 'custom.synthetic'); f.draw(); f.click('[data-account-apply]'); f.draw();
  f.change('[data-account-select="1"]', true); chooseAccountCategory(f, 'builtin.revenue'); f.draw(); f.click('[data-account-apply]'); f.draw();
  const editor = f.window.JetUi.accountMappingEditor;
  assert.equal(editor.pendingCount(), 2);
  f.store.setTaxonomyAfterSave({ revision: 3, categories: builtIns });
  f.draw();
  assert.equal(editor.pendingCount(), 1, 'only the change that used the deleted category is dropped');
  assert.match(f.dom.content.textContent, /有 1 個科目要套用的分類已在分類設定中刪除，這些科目的改動已取消。/);
  const note = f.$('[data-account-dropped]'), editorSection = note.parentElement;
  assert.ok(editorSection.children.indexOf(note) < editorSection.children.indexOf(f.$('.account-editor__table')), 'the note sits above the account list');
  f.click('[data-account-save]');
  assert.deepEqual(plain(f.request('accountMappingSave').payload.changes), [{ accountCode: '2000', categoryId: 'builtin.revenue' }]);
});

// Execute the complete production renderer and its event handlers. Only DOM plumbing and
// asynchronous action responses are doubles; WebView focus/scroll has separate GUI coverage.
function fixture() {
  const requests = [], saves = [];
  const window = { setTimeout, clearTimeout, JetApi: contractJetApi({
    queryPrescreenPage(payload) {
      return new Promise((resolve, reject) => requests.push({ payload, resolve, reject }));
    },
    accountTaxonomySave(payload) {
      return new Promise((resolve, reject) => saves.push({ payload, resolve, reject }));
    }
  })};
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
    // 使用者 2026-10-08 裁定移除傳票日期與回溯過帳，合成結果不再帶 backdatedPosting。
    blankDescription: { count: 0 }, nonAuthorizedPreparer: { count: 0 },
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
  // 使用者 2026-10-08 要求重新設計自訂篩選條件：範例移到右側「從範例開始」，需要部門欄位的範例就地選欄位，不再有範例專用下拉。
  assert.match(source, /template\.pickField === 'text'/);
  assert.doesNotMatch(source, /data-legacy-example-field/);
  // 2026-10-03 用語統一 W10：使用者裁定以「已儲存」為準（第一次失敗：收據 20261003-024908090-6beccb9968d64a549df8acb53f0c5ff6）。
  assert.match(source, /套用後會替換目前的條件，已儲存的情境不受影響。/);
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
  const documentTable = html.match(/<tbody data-page-query="null-nullDocument">([\s\S]*?)<\/tbody>/)[1];
  const accountTable = html.match(/<tbody data-page-query="null-nullAccount">([\s\S]*?)<\/tbody>/)[1];
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
    invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
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
  // 新頁碼導覽有真正的子節點，沿用樹狀 DOM 解析，不為分頁另寫一份假的實作。
  const dom = batch9TreeDom(), el = dom.element;
  const document = dom.document, window = { setTimeout, clearTimeout };
  const context = vm.createContext({ document, window });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const store = window.JetStore; store.setProject({ projectId: 'paged-read' });
  store.setLastRun('validate', { resultRef: { runId: 'v1' } });
  const wrapper = el(), table = el(), more = el(), form = el(), input = el(), clear = el(), sort = el(), requests = [], rows = ['existing'];
  sort.setAttribute('data-sort-key', 'amount'); sort.closest = selector => selector === '.th-sort' ? sort : null;
  table.contains = node => node === sort;
  form.querySelector = selector => ['[data-page-search-input]', '[data-search-input]'].includes(selector) ? input :
    ['[data-page-search-clear]', '[data-search-clear]'].includes(selector) ? clear : null;
  wrapper.appendChild(table); wrapper.appendChild(more);
  const controller = window.JetUi.bindPagedTable(wrapper, { sourceKind: 'validate', table, search: form,
    fetchPage(cursor, sort, search) {
      if (options.throwSync) throw new Error('synchronous failure');
      return new Promise((resolve, reject) => requests.push({ cursor, sort, search, resolve, reject })); },
    clearRows() { rows.length = 0; }, appendRows(data) { rows.push(...data); } });
  return { store, wrapper, table, more: wrapper.querySelector('[data-page-next]'), form, input, clear, sort, requests, rows,
    start: () => controller.ensureLoaded(), previous: wrapper.querySelector('[data-page-previous]'),
    position: wrapper.querySelector('[data-page-position]'), retry: wrapper.children.find(node => node.textContent === '重試') };
}

test('paged read ignores late data after a source change and never acquires the global busy state', async () => {
  const f = pagedReadFixture(); f.store.setBusy(true, 'unrelated operation'); f.start();
  assert.equal(f.requests.length, 1);
  assert.equal(f.table.getAttribute('aria-busy'), 'true');
  f.store.setProject({ projectId: 'replacement' });
  f.requests[0].resolve({ rows: ['wrong project'], nextCursor: null }); await settle();
  assert.deepEqual(f.rows, ['existing']);
  assert.equal(f.store.getState().busy, true, 'a local read must not release someone else\'s operation');
});

test('paged read preserves rows on failure and reports an actionable local status', async () => {
  const f = pagedReadFixture(); f.start();
  f.requests[0].reject(new Error('synthetic failed page')); await settle();
  assert.deepEqual(f.rows, ['existing']);
  assert.equal(f.retry.disabled, false);
  const status = f.wrapper.children.find(c => c.getAttribute('data-page-status') !== null);
  assert.ok(status, 'failure belongs next to the affected table');
  assert.match(status.textContent, /重試/);
  assert.equal(status.getAttribute('role'), 'status');
  f.retry.fire('click'); f.requests[1].resolve({ rows: ['recovered'], nextCursor: null }); await settle();
  assert.deepEqual(f.rows, ['recovered']);
  assert.equal(f.table.getAttribute('aria-busy'), 'false');
  assert.equal(f.store.getState().busy, false);
  assert.doesNotMatch(status.textContent, /分錄/, 'the shared table also displays accounts and vouchers');
});

for (const change of ['data', 'run', 'detached']) test('paged read rejects a response after ' + change + ' changes', async () => {
  const f = pagedReadFixture(); f.start();
  if (change === 'data') f.store.setImportResult('gl', { batchId: 'new' });
  else if (change === 'run') f.store.setLastRun('validate', { resultRef: { runId: 'v1' } });
  else f.wrapper.isConnected = false;
  f.requests[0].resolve({ rows: ['stale'], nextCursor: 'stale-cursor' }); await settle();
  assert.deepEqual(f.rows, ['existing']);
});

test('paged sorting and search retry the exact failed request without discarding the previous result', async () => {
  const f = pagedReadFixture(); f.start();
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
  assert.deepEqual(f.rows, ['matched']); assert.equal(f.input.value, 'JV'); assert.equal(f.more.disabled, true);
});

test('synchronous page failure restores controls and no legacy whole-page read helper remains', async () => {
  const f = pagedReadFixture({ throwSync: true }); f.start(); await settle();
  assert.equal(f.retry.disabled, false); assert.deepEqual(f.rows, ['existing']);
  assert.equal(f.table.getAttribute('aria-busy'), 'false');
  assert.doesNotMatch(fs.readFileSync(path.join(root, 'steps/validate-step.js'), 'utf8'), /Ui\.bindLoadMore/);
});

test('200-row pager replaces each page across 401 rows, goes back, retains failed pages and resets search totals', async () => {
  const f = pagedReadFixture();
  const rows = start => Array.from({ length: 200 }, (_, i) => 'ROW-' + (start + i));
  f.start();
  f.requests[0].resolve({ rows: rows(1), nextCursor: 'after-200', totalCount: 401 }); await settle();
  assert.equal(f.rows.length, 200); assert.equal(f.position.textContent, '1/3');
  assert.equal(f.previous.disabled, true); assert.equal(f.more.disabled, false);
  f.more.fire('click'); assert.equal(f.requests[1].cursor, 'after-200');
  f.requests[1].resolve({ rows: rows(201), nextCursor: 'after-400', totalCount: 401 }); await settle();
  assert.equal(f.rows.length, 200); assert.equal(f.rows[0], 'ROW-201'); assert.equal(f.rows.at(-1), 'ROW-400');
  assert.equal(f.position.textContent, '2/3'); assert.equal(f.rows.includes('ROW-1'), false);
  f.more.fire('click'); f.requests[2].reject(new Error('synthetic third-page failure')); await settle();
  assert.equal(f.position.textContent, '2/3'); assert.equal(f.rows[0], 'ROW-201'); assert.equal(f.retry.hidden, false);
  f.retry.fire('click'); assert.equal(f.requests[3].cursor, 'after-400');
  f.requests[3].resolve({ rows: ['ROW-401'], nextCursor: null, totalCount: 401 }); await settle();
  assert.deepEqual(f.rows, ['ROW-401']); assert.equal(f.position.textContent, '3/3'); assert.equal(f.more.disabled, true);
  f.previous.fire('click'); assert.equal(f.requests[4].cursor, 'after-200');
  f.requests[4].resolve({ rows: rows(201), nextCursor: 'after-400', totalCount: 401 }); await settle();
  assert.equal(f.position.textContent, '2/3'); assert.equal(f.rows[0], 'ROW-201');
  f.previous.fire('click'); assert.equal(f.requests[5].cursor, null);
  f.requests[5].resolve({ rows: rows(1), nextCursor: 'after-200', totalCount: 401 }); await settle();
  f.input.value = 'MATCH'; f.form.fire('submit');
  assert.equal(f.requests[6].cursor, null); assert.equal(f.requests[6].search, 'MATCH');
  f.requests[6].resolve({ rows: ['MATCH-1'], nextCursor: null, totalCount: 1 }); await settle();
  assert.deepEqual(f.rows, ['MATCH-1']); assert.equal(f.position.textContent, '1/1');
  assert.equal(f.previous.disabled, true); assert.equal(f.more.disabled, true);
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
  // 上方改成兩個數字後，「上方筆數」指不清楚，改寫成「分錄筆數」（使用者 2026-10-16 要求分開顯示）；
  // 第一次失敗：原始碼不符 /標示為參考的分錄不計入上方筆數/。
  assert.match(vouchers, /標示為參考的分錄不計入分錄筆數/);
  const css = read('../css/app.css');
  for (const selector of ['stat-card__value', 'overview-pop__kpi-value']) {
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
// 使用者 2026-10-19 同意按鈕改名「加入」「更換」後，加入的徽章一併改成「剛剛加入新檔（時間）」，不再寫「已加入來源」。
for (const [mode, badge, other] of [['replace', /剛剛重新匯入（/, /剛剛加入新檔/], ['append', /剛剛加入新檔（/, /剛剛重新匯入|已加入來源/]])
test('a finished ' + mode + ' import shows the result and the just-imported badge in the same update', async () => {
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
    for (const name of ['wizard-replace-gl','wizard-append-gl','wizard-pick','wizard-confirm','wizard-cancel']) control(name);
    const card = { querySelector: selector => actions.get((selector.match(/data-action="([^"]+)"/) || [])[1]) || null,
      querySelectorAll: () => [] };
    const container = { innerHTML: '',
      querySelector: selector => selector === '[data-bind="import-card-gl"]' ? card : null,
      querySelectorAll: selector => selector === '[data-task-toggle]' ? [toggle] : [] };
    f.window.JetUi.renderStep('import', container, f.store.getState());
    return { html: () => container.innerHTML, click: name => actions.get(name).click() };
  }
  render().click('toggle'); render().click('wizard-' + mode + '-gl');
  render().click('wizard-pick'); await settle();
  render().click('wizard-confirm');
  let atUpdate = null;
  f.store.subscribe(state => {
    if (atUpdate === null && state.importState.gl && state.importState.gl.batchId === 'new') atUpdate = render().html();
  });
  calls[0].resolve({batchId:'new',rowCount:2,addedRowCount:2,columns:['doc','amount'],sources:[]}); await settle();
  assert.ok(atUpdate, 'writing the new data notifies the screen');
  assert.doesNotMatch(atUpdate, /匯入中/);
  assert.match(atUpdate, badge);
  assert.doesNotMatch(atUpdate, other);
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

// 2026-10-09 使用者要求提示避免專業術語、不得過長：「可能選錯識別欄」改成「可能選錯了名單的欄位」，
// 並依分類改成需要注意（琥珀）。第一次失敗收據 20261008-173522118-1569597102f24a35ab54a3cc800403e0。
test('authorized list zero matches warn without rejecting the successful import and unknown GL mapping is not zero', async () => {
  const f = authorizedPreparerReviewFixture(); f.openCard();
  await f.importList({ rowCount: 35, sourceColumn: 'Employee ID', matchedPreparerCount: 0 });
  assert.equal(f.store.getState().importState.authorizedPreparer.rowCount, 35);
  assert.equal(f.store.getState().busy, false);
  assert.match(f.render().html(), /有效名單 35 位中，有 0 位出現在 GL 傳票建立人員/);
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-38（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  // 使用者 2026-10-14 要求黃色提示不過度解釋，句子依同日確認的清單縮短（第一次失敗：FrontendMapping 收據 20261014-085644389-9bd9b20c579e4d95926d6ad0c4881504）。
  assert.ok(f.render().html().includes('class="form-notice form-notice--warning">清單中的人員都不在 GL 的傳票建立人員裡，' +
    '請確認所選欄位是人員代號或姓名。</p>'),
    'the zero-match reminder does not block, so it is amber rather than red');
  f.store.setAuthorizedPreparerState({ rowCount: 35, sourceColumn: 'Employee ID', matchedPreparerCount: null });
  assert.match(f.render().html(), /確認.*傳票建立人員.*配對後.*比對/);
  assert.doesNotMatch(f.render().html(), /有 0 位|請確認所選的清單欄位/);
  f.store.setAuthorizedPreparerState({ rowCount: 35, sourceColumn: 'Employee ID', matchedPreparerCount: 1 });
  assert.match(f.render().html(), /有 1 位出現在 GL/);
  assert.doesNotMatch(f.render().html(), /請確認所選的清單欄位/, 'one match is enough; no unapproved ratio threshold');
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
  // 使用者 2026-10-07 裁定上游修改清除下游：移除名單會清掉已存情境，不再說明「含非授權編製人員條件的情境要重新匯入名單」（第一次失敗紀錄 artifacts/review/kct-filter-ops/frontend-workflow-before-assertion-update-upstream-shape.txt）。
  assert.match(message, /已移除授權清單/);
  assert.doesNotMatch(message, /情境設定仍保留/);
  assert.match(message, /預篩選.*重新執行/);
  assert.doesNotMatch(message, /依賴清單的結果會重新計算/);
});

test('GL mapping success refreshes the authorized list comparison without another invalidation', async () => {
  const f = authorizedPreparerReviewFixture();
  // 草稿照正式畫面能確認的樣子補齊必填欄位（2026-10-15 前後端契合時補）；斷言只看建立人員。
  f.store.setImportResult('gl', { rowCount: 1, columns: ['doc', 'date', 'acc', 'name', 'desc', 'amt', 'author'] });
  f.store.replaceMappingDraft('gl', { docNum: 'doc', postDate: 'date', accNum: 'acc', accName: 'name', description: 'desc', amount: 'amt', createBy: 'author' });
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

// 使用者 2026-10-19 裁定：確認配對已存檔、但讀不到授權清單比對狀態時，回應不帶這個欄位，畫面保留原本的比對結果。
// 一併附上的提醒句由後端測試核對；這裡的假回應只能用正式後端記錄過的形狀，所以只省略欄位。
test('GL mapping success without the authorized list comparison keeps the shown comparison', async () => {
  const f = authorizedPreparerReviewFixture();
  f.store.setImportResult('gl', { rowCount: 1, columns: ['doc', 'date', 'acc', 'name', 'desc', 'amt', 'author'] });
  f.store.replaceMappingDraft('gl', { docNum: 'doc', postDate: 'date', accNum: 'acc', accName: 'name', description: 'desc', amount: 'amt', createBy: 'author' });
  const shown = { rowCount: 35, sourceRowCount: 54, blankRowCount: 4, duplicateRowCount: 15, matchedPreparerCount: 2 };
  f.store.setAuthorizedPreparerState(shown);
  f.window.JetApi.mappingCommitGl = async () => ({ projectedRowCount: 1 });
  const source = fs.readFileSync(path.join(root, 'steps/mapping-step.js'), 'utf8');
  vm.runInContext(source.replace("  Ui.registerStep('mapping', render);",
    '  window.bindReviewedGlMapping = function (container) { bindMappingSection(container, "gl", Ui.GL_FIELDS); };'), f.context);
  let click;
  const section = { addEventListener() {}, querySelectorAll: () => [], querySelector: selector =>
    selector === '[data-action="commit-gl"]' ? { addEventListener: (_, handler) => { click = handler; } } : null };
  f.window.bindReviewedGlMapping({ querySelector: () => section });
  click(); await settle();
  assert.equal(f.store.getState().mapping.gl.committed.projectedRowCount, 1, 'the commit itself succeeded');
  assert.deepEqual(plain(f.store.getState().importState.authorizedPreparer), shown);
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
    invalidatedResults:{validation:false,prescreen:true,filter:true, filterScenarios: true},
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
    invalidatedResults:{validation:false,prescreen:true,filter:true, filterScenarios: true},
    staleState:{validation:false,prescreen:true,filter:false},reportArtifacts:[]}); await settle();
  assert.equal(f.store.getState().importState.authorizedPreparer,null);
  assert.equal(f.store.getState().lastRuns.prescreen,null);
  assert.doesNotMatch(render().html(),/data-action="clear-authorized-preparer"/);
});

// 2026-10-02 使用者裁定移除「情境層級」（W12）：KCT I 是加入目前條件組的一條條件括號，內含週末或假日。
// 2026-10-02 範例缺值時要說明第幾組第幾條（W20）。以下直接執行正式前端腳本核對草稿形狀、送出形狀、舊情境重開與提示文字。
function kctFixture() {
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer(fn) { window.deferred = fn; } } };
  const document = { querySelector: () => null, createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const source = fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8');
  vm.runInContext(source.replace("  Ui.registerStep('filter', render);",
    "  window.kct = { addKctToDraft, removeKctFromActiveGroup, refreshAutomaticMetadata, toWireDraft, kctCardState, readBackHtml, applyTemplate, scenarioGate, setActiveGroup, syncViewState, bind, kctPickerHtml };"), context);
  const store = window.JetStore, api = window.kct;
  store.setProject({ projectId: 'kct-i-fixture' }); api.syncViewState(store.getState());
  const item = letter => window.JetUi.FILTER_KCT_CHECKLIST.find(x => x.letter === letter);
  function toggle(letter, remove = false) {
    const draft = store.getState().filter.draft;
    api[remove ? 'removeKctFromActiveGroup' : 'addKctToDraft'](draft, item(letter));
    api.refreshAutomaticMetadata(draft, true); store.setFilterDraft(draft);
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
  // 第九節 G2 的 G 卡使用欄位文字；首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  assert.match(f.text(f.api.readBackHtml(draft)), /傳票摘要 空白 且 非營業日（排除補班日）/);
  const picker = f.api.kctPickerHtml(draft);
  assert.doesNotMatch(picker, /情境層級|適用整個情境|picker-card--preset/);
  assert.match(picker, /作用中組已選 2 項/);
});

// 2026-10-02 使用者裁定移除「情境層級」：整個第五步畫面與樣式表都不再有這個區塊。
test('KCT I leaves no scenario-level block on the rendered filter step or in the stylesheet', () => {
  const f = filterOperationFixture();
  f.click('[data-kct-letter="G"]'); f.click('[data-kct-letter="I"]');
  assert.equal(f.draft().groups.length, 1);
  assert.deepEqual(plain(f.draft().groups[0].rules.map(rule => rule.__kctLetter)), ['G', 'I']);
  assert.ok(f.draft().groups.every(group => !('__kctPresetGroup' in group)));
  assert.ok(f.window.JetUi.FILTER_KCT_PRESETS.every(preset => !('newGroup' in preset)), 'no preset opens a separate group');
  assert.doesNotMatch(f.dom.content.innerHTML, /情境層級|適用整個情境|remove-preset-group|scenario-preset|picker-card--preset/);
  const css = fs.readFileSync(path.join(root, '../css/app.css'), 'utf8');
  for (const removed of ['情境層級', '適用整個情境', '__kctPresetGroup', 'remove-preset-group', 'scenario-preset', 'picker-card--preset', 'isPresetNewGroup'])
    assert.equal(css.includes(removed), false, removed);
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
  // 第九節 G2 保留身分與順序，只改 G 的型別。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  assert.deepEqual(plain(wire.groups[0].rules.map(rule => rule.type)), ['group', 'fieldValue']);
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
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-08（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。 位置字首照 R3-01 不變，只換原因。
  assert.deepEqual(plain(f.gate()), { ok: false, notice: '尚需補齊：第 1 組第 2 條「總帳日期」請填 1 到 100 個值' });
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
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-08、R3-12（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。 位置字首照 R3-01 不變，只換原因。
  assert.deepEqual(plain(f.gate()), { ok: false, notice: '尚需補齊：第 1 組第 2 條的第 2 項「' +
    f.window.JetUi.glFieldLabel('createBy') + '」請填 1 到 100 個值、第 2 組第 1 條「日期區間」起日晚於迄日' });
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

// 低嚴重度事項 A2（使用者 2026-10-19 要求處理）：文字檔改選分隔符或編碼後重新讀前 10 列，
// 「只讀到一欄」的提醒跟著出現或消失，不必先展開預覽；勾掉的來源也不會因為改選被重新勾回。
test('changing a text source delimiter refreshes the single-column notice without opening the preview', async () => {
  const SINGLE = '只讀到一欄。若檔案上方有公司名稱或報表標題列';
  const previews = [];
  const w = importWizardFixture(api => {
    api.hostSelectFiles = async () => ({ files: [{ filePath: 'C:\\in\\tabbed.txt', fileName: 'tabbed.txt' }] });
    api.importInspectFile = async () => ({ fileType: 'csv', columns: ['doc\tamount'], encoding: 'utf-8', delimiter: ',',
      notices: [SINGLE + '……'] });
    api.importPreviewFile = async payload => {
      previews.push(payload);
      return payload.delimiter === '\t'
        ? { columns: ['doc', 'amount'], sampleRows: [['a', '1']] }
        : { columns: ['doc\tamount'], sampleRows: [['a\t1']], notices: [SINGLE + '……'] };
    };
  });
  w.open();
  w.render().click('wizard-pick'); await settle();
  assert.ok(w.render().html().includes(SINGLE), 'the inspection notice is shown');
  const include = w.render().control('bind:include:0');
  include.checked = false; include.fire('change');
  const delimiter = w.render().control('bind:delimiter:0');
  delimiter.value = '\t'; delimiter.fire('change'); await settle();
  assert.deepEqual(plain(previews.at(-1)), { filePath: 'C:\\in\\tabbed.txt', limit: 10, encoding: 'utf-8', delimiter: '\t' });
  assert.ok(!w.render().html().includes(SINGLE), 'choosing the tab delimiter clears the notice');
  assert.match(w.render().html(), /data-pending-bind="include" data-index="0">/, 'a source the auditor unticked stays unticked');
  delimiter.value = ','; delimiter.fire('change'); await settle();
  assert.ok(w.render().html().includes(SINGLE), 'choosing the comma again brings the notice back');
});

// 使用者 2026-10-19 要求前端不露出系統用詞：編碼選項寫 UTF-8、Big5（繁體中文）、UTF-16，送出的值仍是後端接受的名稱。
test('text source encoding options read as common names while keeping the backend values', async () => {
  const w = importWizardFixture(api => {
    api.hostSelectFiles = async () => ({ files: [{ filePath: 'plain.csv', fileName: 'plain.csv' }] });
    api.importInspectFile = async () => ({ fileType: 'csv', columns: ['doc', 'amount'], encoding: 'big5', delimiter: ',' });
  });
  w.open();
  w.render().click('wizard-pick'); await settle();
  const html = w.render().html();
  assert.match(html, /<option value="utf-8">UTF-8<\/option>/);
  assert.match(html, /<option value="big5" selected>Big5（繁體中文）<\/option>/);
  assert.match(html, /<option value="utf-16">UTF-16<\/option>/);
  assert.doesNotMatch(html, />(utf-8|big5|utf-16)</);
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
  assert.equal(calls.length, 1, 'every source goes in one request, without a follow-up per source');
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
      invalidatedResults: { ...flags, filterScenarios: flags.filter }, staleState: flags,
      artifacts: [{ artifactId: 'existing', stale: flags.filter }] });
    assert.equal(f.store.getState().lastRuns.validate.id, 'validation');
    assert.equal(f.store.getState().lastRuns.prescreen?.id || null, flags.prescreen ? null : 'prescreen');
    assert.equal(f.store.getState().filter.preview, flags.filter ? null : preview);
    // 使用者 2026-10-07 裁定上游修改清除下游：篩選命中失效時，已存情境與編輯中的條件一起回到預設（第一次失敗紀錄 artifacts/review/kct-filter-ops/frontend-workflow-before-assertion-update-upstream-shape.txt）。
    if (flags.filter) {
      assert.deepEqual(plain(f.store.getState().filter.draft), { name: '', rationale: '', groups: [] });
      assert.deepEqual(plain(f.store.getState().filter.savedScenarios), []);
    } else {
      assert.equal(f.store.getState().filter.draft, draft);
      assert.equal(f.store.getState().filter.savedScenarios, saved);
    }
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
    invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: false, filter: false }, artifacts: [] });
  assert.equal(f.store.getState().filter.preview, null);
  assert.equal(f.store.getState().lastRuns.prescreen, null);
  assert.equal(f.store.getState().lastRuns.validate.id, 'independent-validation');
  // 使用者 2026-10-07 裁定上游修改清除下游：沒儲存的條件也回到預設，不另外提示（第一次失敗紀錄 artifacts/review/kct-filter-ops/frontend-workflow-before-assertion-update-upstream-shape.txt）。
  assert.notEqual(f.store.getState().filter.draft, draft);
  assert.deepEqual(plain(f.store.getState().filter.draft), { name: '', rationale: '', groups: [] });
  assert.equal(f.store.getState().messages.some(m => m.text === UPSTREAM_RESET_MESSAGE), false,
    'an empty draft with no saved scenario has nothing to report');
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
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-17（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  assert.match(html, /<p class="rule-field__hint">尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫<\/p>/);
  assert.doesNotMatch(html, /lastPeriodStart|重新建立案件/);
  f.store.setProject({ projectId: 'synthetic', lastPeriodStart: '2024-02-01' });
  assert.doesNotMatch(f.window.reviewMetadataRule(rule, 0, 0), /修改案件資料/);
});

function taxonomyReviewDom(html) {
  const nodes = [];
  for (const tag of html.matchAll(/<(input|select|button|p|div|li|section|details|summary|form)\b([^>]*)>/g)) {
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
  for (const name of ['filter-values.js']) vm.runInContext(fs.readFileSync(path.join(root, name), 'utf8'), context);
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

test('scenario-used taxonomy can be removed, cancelled, retried and saved without losing scenarios before success', async () => {
  const f = taxonomyReviewFixture();
  f.store.setTaxonomy({ revision: 2, categories: classificationTree() });
  const saved = [{ name: '分類情境', groups: [{ rules: [{ type: 'accountCategory', categoryIds: ['bank'] }] }] }];
  f.store.setSavedScenarios(saved);
  let page = f.render();
  assert.equal(page.dom.querySelector('[data-taxonomy-remove="bank"]').disabled, false);
  assert.equal(page.dom.querySelector('[data-taxonomy-remove="builtin.cash"]'), null);
  assert.doesNotMatch(page.html, /已被篩選情境使用，不可刪除/);
  page.click('[data-taxonomy-remove="bank"]');
  assert.equal(f.api.rows(f.store.getState()).some(row => row.categoryId === 'bank'), false);
  assert.equal(f.store.getState().filter.savedScenarios, saved);
  f.render().click('[data-action="reset-taxonomy"]');
  assert.ok(f.api.rows(f.store.getState()).some(row => row.categoryId === 'bank'));
  assert.equal(f.saves.length, 0);
  f.render().click('[data-taxonomy-remove="bank"]');
  f.render().click('[data-action="save-taxonomy"]');
  assert.equal(f.saves[0].payload.categories.some(row => row.categoryId === 'bank'), false);
  f.saves[0].reject({ code: 'CategoryInUse', message: '科目配對仍使用此分類' });
  await settle();
  assert.equal(f.store.getState().filter.savedScenarios, saved);
  assert.ok(f.store.getState().taxonomy.categories.some(row => row.categoryId === 'bank'));
  assert.equal(f.api.rows(f.store.getState()).some(row => row.categoryId === 'bank'), false);
  f.render().click('[data-action="save-taxonomy"]');
  f.saves[1].resolve({ revision: 3, categories: f.saves[1].payload.categories,
    invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: true, filter: false }, reportArtifacts: [] });
  await settle();
  assert.equal(f.store.getState().filter.savedScenarios.length, 0);
  assert.equal(f.store.getState().taxonomy.categories.some(row => row.categoryId === 'bank'), false);
  assert.equal(f.store.getState().messages.filter(m => m.text === UPSTREAM_RESET_MESSAGE).length, 1);
});

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

// 使用者 2026-10-15 要求第四步「套用分類」改回下拉選單，改回檢查下拉的縮排。修改前失敗：20261015-011855792-4174f66464c84ffe990d516f57676fae。
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

test('new classification cards and the category pair check default to subtree while older saved rules remain role based', () => {
  const f = taxonomyReviewFixture(), ui = f.window.JetUi;
  for (const type of ['accountSide', 'accountPair', 'specialAccountCategoryPair']) assert.equal(ui.newFilterRule(type).categorySelection, 'subtree');
  // 使用者 2026-10-08 要求重新設計自訂篩選條件：原本由目錄 P 帶入的分類組合，改由常用查核條件「借方與貸方分類組合」加入，同樣包含下層分類。
  const checks = ui.FILTER_COMMON_CHECK_GROUPS.flatMap(group => group.checks);
  for (const key of ['categoryPair', 'accountCategory']) {
    // 第九節 G1 把借方與分類分為兩條，仍核對分類預設包含下層。首次失敗：20261014-132501058-ef503fecc9824f3b9e6f009a7b154327。
    const root = checks.find(check => check.key === key).rules[0];
    const spec = key === 'accountCategory' ? root.rules[1] : root;
    assert.equal(spec.categorySelection ?? ui.newFilterRule(spec.type).categorySelection, 'subtree', key);
  }
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
    // 第九節 G1 移除方向選單；分類展開與舊條件文字仍照原斷言檢查。首次失敗：20261014-132501058-ef503fecc9824f3b9e6f009a7b154327。
    assert.equal(f.filterApi.subject(rule, 'account'), '');
  }
  const removed = { type: 'accountSide', drCr: 'any', categoryMode: 'absent', categorySelection: 'subtree', categoryIds: ['custom.deleted-id'] };
  const summary = f.filterApi.summary(removed, 0);
  assert.match(summary, /已刪除的分類.*重新選擇/); assert.doesNotMatch(summary, /custom.deleted-id/);
  assert.match(f.filterApi.controls(removed, 0, 0), /已刪除的分類.*重新選擇/);
});

test('classification tree has no search and preserves explicit collapsed branches', () => {
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
  // 第 13 則移除搜尋；收合、展開與重繪後狀態仍由真實事件驗證。
  assert.equal(!!dom.querySelector('[data-category-search]'), false);
  dom.querySelector('[data-category-toggle="builtin.cash"]').fire();
  assert.equal(dom.querySelector('[data-category-node="bank"]').hidden, false);
  assert.equal(dom.querySelector('[data-category-node="unrelated"]').hidden, false);
  dom.querySelector('[data-category-toggle="builtin.cash"]').fire();
  assert.equal(dom.querySelector('[data-category-node="bank"]').hidden, true);
  dom = render(); assert.equal(dom.querySelector('[data-category-node="bank"]').hidden, true);
});

// 2026-10-19 使用者要求同功能的元件只留一套：分類樹的展開改成共用的展開箭頭按鈕，名稱說出它控制哪一個分類的下層，
// 開關只寫在 aria-expanded，按鈕不再顯示「展開」「收合」；沒有下層的分類留同樣寬的空位，名稱才對齊。
test('classification tree toggles are shared chevron buttons named after their category and report their state through aria-expanded', () => {
  const f = taxonomyReviewFixture(), ui = f.window.JetUi;
  f.store.setTaxonomy({ revision: 2, categories: classificationTree() });
  const view = { search: '', collapsed: {} };
  const html = ui.taxonomyPickerHtml(f.store.getState(), [], 'categoryIds', '指定分類', view);
  assert.doesNotMatch(html, />(展開|收合)</, 'the toggle shows no 展開 or 收合 text');
  assert.equal((html.match(/<span class="category-tree__toggle" aria-hidden="true"><\/span>/g) || []).length, 3, 'the three leaf categories keep an empty slot');
  const dom = taxonomyReviewDom(html);
  ui.bindTaxonomyPicker(dom, f.store.getState(), view);
  const toggle = dom.querySelector('[data-category-toggle="builtin.cash"]');
  assert.match(toggle.getAttribute('class'), /\bjet-disclosure\b/);
  assert.equal(toggle.getAttribute('aria-label'), '現金的下層分類');
  assert.equal(toggle.getAttribute('aria-expanded'), 'true');
  toggle.fire();
  assert.equal(toggle.getAttribute('aria-expanded'), 'false');
  assert.equal(dom.querySelector('[data-category-node="bank"]').hidden, true);
  toggle.fire();
  assert.equal(toggle.getAttribute('aria-expanded'), 'true');
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
      search(value) { const input = dom.querySelector('[data-account-tree-search]'); assert.ok(input); input.value = value; input.fire('input');
        dom.querySelector('[data-account-tree-search-form]').fire('submit'); } };
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

// 使用者 2026-10-19 裁定：有搜尋字時勾選分類，仍選入整個分類與下層分類的科目，和勾選框的文字一致；
// 清單改顯示整個分類，搜尋框清空。
test('account-tree category checkbox selects the whole category even while searching', async () => {
  const f = accountTreeReviewFixture();
  f.render().search('現金');
  assert.equal(f.requests[0].payload.search, '現金');
  f.requests[0].resolve({ rows: [{ accountCode: '1101', accountName: '現金', categoryId: 'bank' }], nextCursor: null }); await settle();
  f.render().check('[data-account-tree-select-category="builtin.cash"]');
  assert.equal(f.requests[1].payload.categoryId, 'builtin.cash');
  assert.equal(f.requests[1].payload.search, '');
  f.requests[1].resolve({ rows: [
    { accountCode: '1101', accountName: '現金', categoryId: 'bank' },
    { accountCode: '1102', accountName: '銀行存款', categoryId: 'bank' }], nextCursor: null }); await settle();
  assert.deepEqual(plain(f.rule.values), ['0099', '1101', '1102']);
  const html = f.render().html;
  assert.match(html, /data-account-tree-code="1102"/);
  assert.match(html, /value="" data-account-tree-search /);
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
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8').replace("  Ui.registerStep('filter', render);",
    '  window.batch7 = { bind, customPickerHtml, addFieldCondition, addCommonCheck, conditionFromSpec, findCommonCheck, bindCompoundRule, compoundRuleHtml, ruleRowHtml, ruleSubjectHtml, ruleProblem, readBackHtml, toWireDraft, syncViewState, getView: () => viewState };'), context);
  // 替換字串跟著正式匯出改：2026-10-16 加了 countText，2026-10-19 刪掉只給備用畫面用的 countText（第一次失敗：
  // 20261019-090901211-9e9ec7ffcbf34596ae2d8476cf613b45）。只多匯出 matchStatusHtml 供下面的測試使用。
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

// 第九節 G1 的分類子條件不再繼承方向。首次失敗：20261014-132501058-ef503fecc9824f3b9e6f009a7b154327。
test('batch7 classification children have no hidden direction and personnel/account bracket fields default to exact lists', () => {
  const f = batch7Fixture();
  for (const [type, choice, expected] of [['voucher', 'type:accountSide', { drCr: undefined }], ['group', 'field:approveBy', { operator: 'in', field: 'approveBy' }], ['group', 'field:createBy', { operator: 'in', field: 'createBy' }], ['group', 'field:accNum', { operator: 'in', field: 'accNum' }]]) {
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
  // 第九節 G1 連外層也不再提供 absent，舊模式保留為停用提示。首次失敗：20261014-132501058-ef503fecc9824f3b9e6f009a7b154327。
  assert.doesNotMatch(f.api.ruleRowHtml(child, { rules: [child] }, 0, 0, false), /option value="absent"/);
  assert.match(f.api.ruleRowHtml({ ...child, categoryMode: 'absent' }, { rules: [child] }, 0, 0, false), /option value="absent" selected disabled/);
});

test('batch7 unavailable saved risk and deleted-field selections show selected explanatory placeholders', () => {
  const f = batch7Fixture();
  for (const key of ['nonAuthorizedPreparer', 'unexpectedAccountPair']) {
    const html = f.api.ruleSubjectHtml({ type: 'prescreen', prescreenKey: key }, 'pattern');
    // 使用者 2026-10-14 第三輪勾選頁確認 R3-15（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。 授權編製人員改稱「清單」。
    assert.match(html, new RegExp('<option[^>]*value="prescreen:' + key + '"[^>]*selected[^>]*>[^<]*(?:清單|科目配對)'));
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
  // 使用者 2026-10-16 依過往底稿要求分開顯示「符合條件的傳票數量」與「符合條件的分錄筆數」，舊句式不再使用。
  // 第一次失敗：輸入不符 /符合條件：3 筆分錄（1 張傳票）/。分錄筆數照後端的 summaryRowCount，命中旗標與參考標籤不變。
  const counts = ui.summary({ count: 3, voucherCount: 1, summaryRowCount: 3 });
  assert.match(counts, /<span class="stat-card__value">1<\/span><span class="stat-card__label">符合條件的傳票數量<\/span>/);
  assert.match(counts, /<span class="stat-card__value">3<\/span><span class="stat-card__label">符合條件的分錄筆數<\/span>/);
  assert.match(counts, /標示為參考的分錄不計入分錄筆數/);
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

test('batch7 personnel field buttons are applied by the real picker and approver values reach the wire unchanged', () => {
  // 使用者 2026-10-08 要求重新設計自訂篩選條件：原本目錄 U 的「指定建立或核准人員」改由「文字」列的人員欄位按鈕加入。
  const f = batch7Fixture(), dom = batch7ControlDom();
  const chips = Object.fromEntries(['createBy', 'approveBy'].map(id => {
    const node = dom.node('chip-' + id); node.getAttribute = name => name === 'data-add-field' ? id : null; return [id, node];
  }));
  dom.querySelectorAll = selector => selector === '[data-add-field]' ? Object.values(chips) : [];
  f.api.bind(dom);
  for (const field of ['createBy', 'approveBy']) {
    chips[field].fire('click');
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
    // 使用者 2026-10-14 第三輪勾選頁確認 R3-15（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。 原因寫出缺的分類名稱；
    // 同一個樣式也套進常用查核條件的說明，所以不加行首行尾錨點。
    [{ hasRevenue: false, hasCounterpart: true }, /科目配對中尚無歸類為「Revenue」的科目，請先在第四步設定/],
    [{ hasRevenue: true, hasCounterpart: false }, /科目配對中尚無歸類為「Receivables」「Cash」或「Receipt in advance」的科目，請先在第四步設定/] ]) {
    f.store.setAccountMappingState(mapping);
    assert.match(f.api.ruleProblem(rule)?.message || '', reason);
    assert.match(f.api.ruleSubjectHtml(rule, 'pattern'), /<option[^>]*value="prescreen:unexpectedAccountPair"[^>]*selected/);
    // 使用者 2026-10-08 要求重新設計自訂篩選條件：停用改看常用查核條件「未預期的借貸組合」，原因寫在該項下方。
    assert.match(f.api.customPickerHtml(), /data-add-check="unexpectedAccountPair"[^>]*disabled/);
    assert.match(f.api.customPickerHtml(), new RegExp('id="filter-check-note-unexpectedAccountPair">[^<]*(?:' + reason.source + ')'));
  }
  // One of the three counterpart roles is sufficient; no new all-three rule is invented.
  f.store.setAccountMappingState({ hasRevenue: true, hasCounterpart: true });
  assert.equal(f.api.ruleProblem(rule), null);
  assert.doesNotMatch(f.api.customPickerHtml(), /data-add-check="unexpectedAccountPair"[^>]*disabled/);
});

// The backend consumes this same hand-written JSON; neither side derives expected text from the other renderer.
for (const entry of JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures/batch7-filter-readback.json'), 'utf8'))) {
  test('batch7 shared full condition readback: ' + entry.id, () => {
    const f = batch7Fixture(), scenario = { name: entry.id, rationale: 'Synthetic shared readback', __preserveJoins: true, ...plain(entry.scenario) };
    f.store.getState().mapping.gl.committed = { options: { rdeFields: plain(entry.rdeFields) } };
    // 使用者 2026-10-14 第三輪勾選頁確認 R3-03：已移除的 GL 欄位名稱由 project.load 帶回；夾具寫出同一份名稱。
    f.store.setRetiredGlFieldLabels(entry.retiredGlFieldLabels || null);
    f.store.setSavedScenarios([scenario]);
    assert.equal(f.text(f.api.readBackHtml(f.store.getState().filter.savedScenarios[0], false)).replace(/^篩選條件：/, ''), entry.expected);
  });
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
  // 使用者 2026-10-13 選擇讓第四步明細表的筆數與金額欄連同表頭靠右，這兩欄表頭多了 preview-table__amount；
  // 原本鎖住沒有 class 的 <th>，改字前的失敗收據：20261013-033220756-0bcbdbb6d2ad4869a5beedc5647669b9。欄名仍逐字核對。
  // 使用者 2026-10-16 移除這張核對表上方的說明，數字與原有欄名仍須完整呈現。
  assert.doesNotMatch(html, /確認 JET 存下的分錄沒有少，也沒有重複。|這一項不和來源檔自己的合計比對。/);
  const dom = batch9TreeDom(); dom.content.innerHTML = html;
  assert.deepEqual(dom.content.querySelectorAll('th').map(th => th.textContent), ['核對項目', '確認配對時算出', '存下後重新計算']);
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
  assert.match(html, /—/); assert.doesNotMatch(html, />0(?:\.00)?<|NaN/);
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
  // 使用者 2026-10-13 選擇讓金額欄靠右，明細分頁表的金額欄改成 { cell, className } 欄位設定，和第五步的欄位設定同一種寫法；
  // 原本直接呼叫欄位函式，改寫前的失敗收據：20261013-033220756-0bcbdbb6d2ad4869a5beedc5647669b9。顯示的字仍逐字核對。
  for (const column of [f.v.DETAIL_PAGE_SPECS.completeness.columns[4], f.v.DETAIL_PAGE_SPECS.docBalance.columns[3]]) {
    assert.equal(column.className, 'preview-table__amount');
  }
  assert.equal(f.v.DETAIL_PAGE_SPECS.completeness.columns[4].cell({ diff: 0.003 }), '不足 0.01');
  assert.equal(f.v.DETAIL_PAGE_SPECS.docBalance.columns[3].cell({ diff: -0.003 }), '負值，不足 0.01');
  assert.equal(f.v.DETAIL_PAGE_SPECS.docBalance.columns[3].cell({ diff: 0 }), '0.00');
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
  const data = batch8ValidationData(), item = f.v.VALIDATION_ITEMS.find(item => item.title === '總帳日期空白');
  assert.equal(item.detail(data).rows[0][0], '總帳日期空白');
  assert.equal(f.v.DETAIL_PAGE_SPECS.sourceQuality.columns[0]({ category: 'nullPostDate' }), '總帳日期空白');
  for (const [key, label] of [['nullDescription', '空白摘要'], ['nullDocument', '空白傳票號碼'], ['nullAccount', '空白科目編號']]) {
    assert.equal(f.v.DETAIL_PAGE_SPECS['null-' + key].columns[4]({}), label);
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

// 第 11、12 則移除摘要與捷徑；改由仍存在的右側預覽入口驗證相同查詢與排除原因。
test('excluded data stays available through the sidebar without a duplicate population summary', async () => {
  const f = batch8Fixture();
  assert.doesNotMatch(fs.readFileSync(path.join(root, 'steps/validate-step.js'), 'utf8'), /populationSummaryHtml|preview-excluded-entries/);
  f.ui.openDataPreview('glExcluded');
  assert.deepEqual(plain(f.requests[0].payload), { dataset: 'glExcluded', offset: 0, limit: 200 }); // 使用者 2026-10-08 裁定資料名稱全系統統一
  f.requests[0].resolve({ columns: [], rows: [], totalCount: 0, stats: { excludedByPeriodCount: 15, excludedByPostingStatusCount: 5 } }); await settle();
  assert.match(f.previewDom.node('note').innerHTML, /過帳狀態不符/);
  assert.equal(f.window.batch8Preview.displayCell('glExcluded', 'exclusionReason', 'postingStatus'), '過帳狀態不符');
  const request = f.v.DETAIL_PAGE_SPECS.sourceQuality.fetchPage('cursor-2', { key: 'sourceRowNumber', direction: 'asc' }, 'JV');
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
  // 正式的 host.openFolder 只回 { ok: true }（HostOpenFolderHandler）；2026-10-15 前後端契合時改掉自創的 opened。
  f.requests[2].resolve({ ok: true }); await settle();
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

test('acceptance feedback overview labels legacy flag like side without changing the saved mode or adding a choice', () => {
  const f = batch8Fixture(), state = f.store.getState();
  f.store.setMappingCommitted('tb', { mapping: {}, mode: 'direct', projectedRowCount: 3 });
  for (const mode of ['flag', 'side']) {
    f.store.setMappingCommitted('gl', { mapping: {}, mode, projectedRowCount: 5 });
    const html = f.app.overviewStepFactsHtml(state, 2);
    assert.match(html, /金額與借貸別/);
    assert.match(html, /直接變動金額欄/);
    assert.doesNotMatch(html, /尚未設定|尚未完成/);
    assert.equal(state.mapping.gl.committed.mode, mode);
  }
  assert.deepEqual(plain(f.ui.GL_MODES.map(mode => mode.value)), ['dual', 'signed', 'side']);
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

test('mapping date fields omit permanent explanations while invalid approval settings still show a warning', () => {
  const f = batch8Fixture(), state = f.store.getState();
  f.store.setImportResult('gl', { batchId: 'g', columns: ['Post', 'Voucher', 'Approval'], rowCount: 1 });
  const html = f.mapping.classicEditSection('gl', 'GL', f.ui.GL_FIELDS, f.ui.GL_MODES, state.importState.gl, state.mapping.gl, 'dual', null, false);
  assert.match(html, /總帳日期/);
  assert.doesNotMatch(html, /用來決定查核期間內/);
  // 使用者 2026-10-08 裁定移除傳票日期與回溯過帳：配對畫面不再列出傳票日期或回溯過帳。
  assert.doesNotMatch(html, /回溯過帳/);
  assert.match(html, /傳票核准日/);
  assert.doesNotMatch(html, /用於核准日期條件/);
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
  // 預篩選報告的回應是單一 artifact，和正式後端相同（2026-10-15 前後端契合前寫成 artifacts 陣列；畫面兩種都接受）。
  f.requests[0].resolve({ artifact: { artifactId: 'old-p8', kind: 'prescreenReport', stale: false, sourceRef: { prescreenRunId: 'p8' } } }); await settle();
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
  const requests = [], window = { setTimeout, clearTimeout, addEventListener() {}, JetFocus: { defer() {}, move() {}, trapDialogTab() {} }, JetApi: contractJetApi({}) };
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
  f.store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
    staleState: { validation: false, prescreen: false, filter: false },
    reportArtifacts: [{ artifactId: 'criteria', kind: 'criteriaSelectionReport', stale: true }] });
  assert.equal(state.lastRuns.validate.id, 'v9'); assert.equal(state.lastRuns.prescreen, null); assert.equal(state.filter.preview, null);
  // 使用者 2026-10-07 裁定上游修改清除下游：編輯中的條件不再保留（第一次失敗紀錄 artifacts/review/kct-filter-ops/frontend-workflow-before-assertion-update-upstream-shape.txt）。
  assert.notEqual(state.filter.draft, draft);
  assert.deepEqual(plain(state.filter.draft), { name: '', rationale: '', groups: [] }, 'unsaved condition editing returns to the default');
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
    invalidatedResults: { validation: false, prescreen: false, filter: true, filterScenarios: true },
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

for (const mode of ['side', 'flag']) test('batch9 GL ' + mode + ' accepts one literal code without inventing a legacy opposite value', () => {
  const f = batch9Fixture(), state = f.store.getState(), info = { batchId: 'g', columns: ['Amount', 'Side'], rowCount: 2 };
  f.store.setImportResult('gl', info); f.store.setMappingMode('gl', mode);
  const credit = f.ui.GL_FIELDS.find(field => field.key === 'dcCreditCode');
  assert.ok(credit && credit.literal && credit.req.includes(mode), 'credit code remains an applicable literal');
  assert.equal(f.ui.TB_FIELDS.some(field => field.key === 'dcCreditCode'), false);
  for (const field of f.ui.GL_FIELDS.filter(field => f.ui.isRequired(field, mode))) f.store.setMappingDraft('gl', field.key, field.literal ? (field.key === 'dcDebitCode' ? 'D' : '') : 'Amount');
  const eligibility = f.mapping.mappingCommitEligibility(f.ui.GL_FIELDS, mode, state.mapping.gl, 'gl');
  // 第 6 則允許單側，兩個都沒填時仍不可確認。
  assert.equal(eligibility.canCommit, true);
  assert.equal(eligibility.missing.length, 0);
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
  // 使用者 2026-10-19 裁定「來源檔用列，分錄用筆」：受影響的是 GL 分錄，改寫「筆分錄」。
  // 第一次失敗：FrontendMapping 收據 20261019-122433278-c327738e178a4e6e829f666f3c1bd5c1。
  assert.match(dom.innerHTML, /3\s*個傳票號碼/); assert.match(dom.innerHTML, /17\s*筆分錄/);
  assert.match(dom.innerHTML, /不同總帳日期/);
  // 使用者 2026-10-14 要求黃色提示不過度解釋，句子依同日確認的清單縮短（第一次失敗：FrontendMapping 收據 20261014-085644389-9bd9b20c579e4d95926d6ad0c4881504）；提示不再說明比對鍵，傳票比對鍵沒有被改由下面的 project 比對核對。
  assert.ok(dom.innerHTML.includes('GL 有 3 個傳票號碼出現在不同總帳日期（共 17 筆分錄），請確認來源系統是否重複使用號碼。'));
  assert.doesNotMatch(dom.innerHTML, /跨月.*3\s*張傳票/);
  assert.deepEqual(plain(state.project), before);
  assert.equal(f.ui.stepGate(state, 1).ok, true, 'this is a review reminder, not a new blocking gate');
});

test('batch9 first step distinguishes a measured zero from unavailable legacy or stale date-reuse summaries', () => {
  const f = batch8Fixture(), state = f.store.getState();
  function html() { const dom = batch7Dom(''); f.window.batch8Create.render(dom, state); return dom.innerHTML; }
  f.store.setLastRun('validate', { documentDateReuse: { documentNumberCount: 0, entryCount: 0 } });
  assert.match(html(), /未發現.*不同總帳日期/);
  for (const summary of [null, {}, { documentDateReuse: null }]) {
    f.store.setLastRun('validate', summary);
    assert.match(html(), /尚未確認/); assert.match(html(), /資料驗證/);
    assert.doesNotMatch(html(), /0\s*個傳票號碼|未發現.*不同總帳日期/);
  }
  f.store.setLastRun('validate', { documentDateReuse: { documentNumberCount: 3, entryCount: 17 } });
  f.store.setStaleState({ validation: true });
  assert.match(html(), /尚未確認/); assert.doesNotMatch(html(), /3\s*個傳票號碼|17\s*筆分錄/);
});

test('batch9 reopening uses persisted date-reuse summary without an extra source scan', () => {
  const f = batch8Fixture(); f.store.resetWorkflow();
  f.window.setInterval = () => 0; f.window.clearInterval = () => {};
  f.ui.applyLoadedProject({ project: { projectId: 'reopened-date-reuse' },
    latestRuns: { validate: { documentDateReuse: { documentNumberCount: 1, entryCount: 4 } } },
    staleState: { validation: false, prescreen: false, filter: false } }, { stayOnCurrentStep: true });
  const dom = batch7Dom(''); f.window.batch8Create.render(dom, f.store.getState());
  // 「筆分錄」的改寫與第一次失敗收據同上一個建案頁測試。
  assert.match(dom.innerHTML, /1\s*個傳票號碼/); assert.match(dom.innerHTML, /4\s*筆分錄/);
  assert.equal(f.requests.length, 0, 'summary rendering does not fetch GL rows');
});

function batch9PagerLifecycleFixture(kind) {
  const dom = batch9TreeDom(), requests = [], window = { setTimeout: fn => { fn(); return 0; }, clearTimeout() {}, CSS: { escape: value => value },
    addEventListener() {}, JetFocus: { defer() {} }, JetApi: contractJetApi({}) };
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
    pageQuery: 'docBalance', sortAction: 'query.docBalancePage', sortKeys: ['documentNumber', 'debit', 'credit', 'diff'], searchLabel: '依傳票號碼查看' },
    dynamic: { kind: 'dynamicPage', pageKey: 'inf' }, prescreen: { kind: 'prescreenPreview', previews: [{ key: 'weekendPosting', label: '週末過帳' }] } };
  ui.registerStep('validate', (container, current) => { container.innerHTML = window.batch9Pages.ruleListHtml([
    { title: 'Synthetic detail', desc: 'Synthetic', status: () => ({ tone: 'hit', text: '3 筆' }), detail: () => details[kind] }
  ], current.lastRuns[runKind], scope); window.batch9Pages.bind(container, false); });
  function draw() { window.batch9PagerApp.renderContent(store.getState(), store.STEPS[3]); return view(); }
  function view() { return { root: dom.content, panel: dom.content.querySelector('.rule-detail'), toggle: dom.content.querySelector('[data-action="toggle-detail"]'),
    get table() { return dom.content.querySelector('.preview-table'); }, get more() { return dom.content.querySelector('[data-page-next]'); },
    get search() { return dom.content.querySelector('[data-page-search]'); }, get input() { return dom.content.querySelector('[data-page-search-input]'); },
    get wrap() { return dom.content.querySelector('.preview-table__wrap'); }, get tbody() { return dom.content.querySelector('.preview-table tbody'); } }; }
  // 每種清單只放正式後端那一頁真的有的欄位（tools/tests/fixtures/action-shapes.json）；2026-10-15 前後端契合時改掉三種清單共用的假列。
  const rowOf = { ordinary: name => ({ documentNumber: name, debit: 10, credit: 0, diff: 10 }),
    dynamic: name => ({ documentNumber: name, accountCode: '1001', debit: 10, credit: 0 }),
    prescreen: name => ({ documentNumber: name, accountCode: '1001', amount: 10 }) }[kind];
  function response(names, nextCursor) {
    const page = { rows: names.map(rowOf), nextCursor };
    if (kind === 'dynamic') page.columns = [{ key: 'documentNumber', label: '傳票號碼', valueType: 'text', sortable: true }];
    return page;
  }
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
  assert.match(page.tbody.textContent, /JV-B/); assert.doesNotMatch(page.tbody.textContent, /JV-A/, 'next page replaces earlier rows');
  page.wrap.scrollTop = 73; page.wrap.scrollLeft = 41; page.root.scrollTop = 257;
  const before = f.requests.length; f.store.touch(); page = f.draw();
  assert.equal(page.panel.hidden, false, 'redraw preserves the expanded detail');
  assert.match(page.tbody?.textContent || '', /JV-B/, 'the loaded rows are restored rather than just the summary preview');
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
  assert.match(f.view().tbody.textContent, /SECOND/); assert.doesNotMatch(f.view().tbody.textContent, /FIRST/); assert.equal(f.view().more.disabled, true);
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
    assert.equal(f.requests.at(-1).payload.cursor ?? null, null, 'new scope starts at the first query page');
    f.requests.at(-1).resolve(f.response(['NEW-PREVIEW'], 'new-preview-next')); await settle();
    page = f.view(); page.more.fire('click'); assert.equal(f.requests.at(-1).payload.cursor, 'new-preview-next');
  });
}

function batch9CancellationFixture() {
  const dom = batch9TreeDom(), pending = [], requestHandlers = [], window = { setTimeout, clearTimeout, JetApi: contractJetApi({
    onRequestStarted(fn) { requestHandlers.push(fn); }, operationCancel: payload => new Promise((resolve, reject) => pending.push({ payload, resolve, reject })) }) };
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
    invalidatedResults: { validation: true, prescreen: true, filter: true, filterScenarios: true }, staleState: { validation: false, prescreen: false, filter: false }, reportArtifacts: [] });
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

test('batch9 detail first page and later pages use two hundred while value profiles stay fifty', async () => {
  const f = batch9PagerLifecycleFixture('prescreen');
  assert.equal(f.ui.TABLE_PAGE_SIZE, 200); assert.equal(f.ui.VALUE_PROFILE_LIMIT, 50);
  const page = await f.open(); assert.equal(f.requests[0].payload.pageSize, 200);
  page.more.fire('click'); assert.equal(f.requests.at(-1).payload.pageSize, 200);
});

test('batch9 report history uses its own fifty-file page rather than a query or audit limit', () => {
  const f = batch9Fixture(), dom = batch9TreeDom();
  assert.equal(f.ui.REPORT_HISTORY_PAGE_SIZE, 50); assert.equal(f.ui.TABLE_PAGE_SIZE, 200);
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
    CSS: { escape: value => value }, addEventListener() {}, JetApi: contractJetApi({
      projectListLocal: payload => new Promise((resolve, reject) => requests.push({ payload, resolve, reject })),
      systemDatabaseInfo: payload => new Promise((resolve, reject) => databaseRequests.push({ payload, resolve, reject }))
    }) };
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
  assert.equal(store.getState().sqlServerConfigured, null, 'availability is unknown until system.databaseInfo answers');
  assert.equal((appSource.match(/systemDatabaseInfo\(/g) || []).length, 1, 'startup reuses the single system.databaseInfo call');
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
  assert.equal(hint.textContent, '這台電腦沒有設定線上資料庫，請改選 SQLite 或 DuckDB。');
  assert.equal(f.form.querySelector('option[value="sqlServer"]').textContent, 'SQL Server（共用實例）');
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

// 2026-10-09 使用者要求提示只顯示系統狀態、不重複也不列系統日誌：失聯提示改用前端自己的一句，
// 不再接上後端同義的 message，所以改用提示本身確認線上狀態已更新。
// 第一次失敗收據 20261008-173522118-1569597102f24a35ab54a3cc800403e0。
test('batch9 project picker still refreshes both project rows and online status after startup-only notifications', () => {
  const f = batch9CreateStartupFixture(), state = f.store.getState();
  f.app.renderPicker(state); assert.match(f.picker.textContent, /尚無本地案件/);
  f.store.subscribe(current => f.app.renderPicker(current));
  f.store.setProjects([{ projectId: 'picker-new-local', databaseProvider: 'duckdb' }], { reachable: false, message: 'Synthetic offline' });
  assert.match(f.picker.textContent, /picker-new-local/); assert.match(f.picker.textContent, /目前連不上線上案件。/);
  assert.doesNotMatch(f.picker.textContent, /Synthetic offline/, 'the backend message repeats the notice and is not appended');
  f.store.setProjects([{ projectId: 'picker-next-online', databaseProvider: 'sqlServer' }], { reachable: true, principal: 'SYNTHETIC-ONLINE' });
  assert.match(f.picker.textContent, /picker-next-online/); assert.match(f.picker.textContent, /SYNTHETIC-ONLINE/);
  assert.doesNotMatch(f.picker.textContent, /picker-new-local|目前連不上線上案件/);
  f.store.setProjects(state.projects, { reachable: false, message: 'Synthetic disconnected' });
  assert.match(f.picker.textContent, /picker-next-online/);
  assert.match(f.picker.textContent, /目前連不上線上案件，下面先列出這台電腦上次記下的清單。/);
  assert.match(f.picker.innerHTML, /class="form-notice form-notice--warning picker-section__alert"/, 'offline is a non-blocking amber notice');
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
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-48（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  assert.equal(item.status({ creatorSummary: { creators: fifty } }).text, '預篩選結果未包含人員數，請重新執行預篩選');
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

  // 準備日改變時，後端讓預篩選與篩選命中失效，並清除全部已存情境（使用者 2026-10-07 裁定上游修改清除下游）。
  // 原本斷言展開的情境保留；情境已不存在，改成確認展開狀態與已載入的命中都清掉（第一次失敗紀錄 artifacts/review/kct-filter-ops/frontend-workflow-before-assertion-update-upstream-shape.txt）。
  store.updateProjectMetadata({ project: { projectId: 'sequence-fixture', entityName: 'Renamed client', lastPeriodStart: '2025-06-30' },
    invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true }, staleState: none, reportArtifacts: [] });
  assert.equal(store.getState().project, project);
  assert.ok(store.getState().dataGeneration > generation);
  f.api.syncViewState(store.getState());
  assert.deepEqual(plain(store.getState().filter.savedScenarios), []);
  assert.equal(f.api.view().openScenarios[0], undefined);
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


test('K1 readback shows the merged quarter and audit period end windows', () => {
  const f = scenarioLifecycleFixture();
  f.store.setProject({ projectId: 'sequence-fixture', periodStart: '2025-09-01', periodEnd: '2025-10-03' });
  assert.equal(f.api.ruleSummaryLabel({type:'revenueDebitNearQuarterEnd',windowDays:5},0), '季底或查核期末前 5 天借記收入；日期區間：2025-09-26～2025-10-03');
});

for(const row of JSON.parse(fs.readFileSync(path.join(__dirname,'fixtures/quarter-end-windows.json'),'utf8'))) {
 test('quarter end shared boundary table: '+row.case,()=>{
  const f=scenarioLifecycleFixture();f.store.setProject({projectId:'shared',periodStart:row.start,periodEnd:row.end});
  const expected='季底或查核期末前 '+row.days+' 天借記收入；日期區間：'+row.expected.map(w=>w.replace('..','～')).join('、');
  assert.equal(f.api.ruleSummaryLabel({type:'revenueDebitNearQuarterEnd',windowDays:row.days},0),expected);
 });
}

test('K2 B card creates empty subtree selections and blocks preview with positioned guidance', () => {
  const f = scenarioLifecycleFixture(), item = f.window.JetUi.FILTER_KCT_CHECKLIST.find(x => x.letter === 'B');
  assert.ok(!item.disabled);
  f.toggle('B');
  const draft = f.store.getState().filter.draft, wire = f.api.toWireDraft(draft), rule = wire.groups[0].rules[0];
  assert.equal(wire.source, 'kct'); assert.equal(rule.type, 'specialAccountCategoryPair');
  assert.equal(rule.pairMode, 'drAndCr'); assert.equal(rule.categorySelection, 'subtree');
  assert.deepEqual(Array.from(rule.debitCategoryIds), []); assert.deepEqual(Array.from(rule.creditCategoryIds), []);
  assert.match(f.api.draftRuleProblems(draft)[0].text, /第 1 組第 1 條.*借方分類/);
  assert.match(f.api.kctPickerHtml(draft), /尚不可用/);
  f.store.getState().importState.accountMapping = { hasAnyCategory: false };
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-15（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  assert.match(f.api.kctPickerHtml(draft), /科目配對中尚無任何科目設定分類，請先在第四步設定/);
  f.store.getState().importState.accountMapping = { hasAnyCategory: true };
  const b = f.api.kctPickerHtml(draft).match(/<button[^>]*data-kct-letter="B"[^>]*>/)[0];
  assert.doesNotMatch(b, /disabled/);
  draft.groups[0].rules[0].debitCategoryIds = ['ppe'];
  assert.match(f.api.draftRuleProblems(draft)[0].text, /貸方分類/);
  draft.groups[0].rules[0].creditCategoryIds = ['expense'];
  assert.equal(f.api.draftRuleProblems(draft).length, 0);
});


test('K3 approved C explanation is rendered on both card and editor without changing readback', () => {
 const f = scenarioLifecycleFixture(), card = f.window.JetUi.FILTER_KCT_CHECKLIST.find(x => x.letter === 'C');
 assert.match(f.api.kctPickerHtml(f.store.getState().filter.draft), /現金不算一般對方科目/);
 assert.match(f.api.kctDescriptionHtml({type:card.ref}), /現金不算一般對方科目/);
 assert.doesNotMatch(f.api.ruleSummaryLabel({type:card.ref},0), /現金/);
});


test('K10 A to E automatic rationales reproduce the approved assessment text verbatim', () => {
 const f = scenarioLifecycleFixture();
 const doc = fs.readFileSync(path.resolve(__dirname, '../../docs/kct-gl-workflow.md'), 'utf8');
 for (const letter of ['A','B','C','D','E']) {
  const card = f.window.JetUi.FILTER_KCT_CHECKLIST.find(x=>x.letter===letter);
  const expected = doc.split('\n').find(line=>line.startsWith('| '+letter+' |')).split('|')[3].trim();
  assert.equal(card.defaultRationale, expected); f.toggle(letter);
 }
 const draft=f.store.getState().filter.draft;
 assert.equal(draft.rationale, f.window.JetUi.FILTER_KCT_CHECKLIST.filter(c=>'ABCDE'.includes(c.letter)).map(c=>c.letter+'：'+c.defaultRationale).join('\n'));
 draft.__rationaleDirty=true; draft.rationale='手寫保留'; f.toggle('F'); assert.equal(draft.rationale,'手寫保留');
});


test('K9 list counts use the submitted distinct values and update without replacing the editor', () => {
 const f=batch7Fixture(), rule={type:'fieldValue',field:'description',operator:'contains',values:[]};
 const editor=f.editor(rule), counter=editor.dom.querySelector('[data-value-count]');
 assert.ok(counter, 'render a live count');
 editor.input('values',' A，B、A\nC\t B ');
 assert.equal(counter.textContent,'已輸入 3 個值，上限 100');
 assert.equal(f.ui.FilterValues.wire(rule,f.store.getState()).values.length,3);
 assert.equal(editor.dom.querySelector('[data-value-count]'),counter);
 editor.input('values', Array.from({length:101},(_,i)=>'V'+i).join('\n'));
 assert.equal(counter.textContent,'已輸入 101 個值，上限 100');
 assert.match(f.ui.FilterValues.problem(rule,f.store.getState()), /100/);
 const tail={type:'fieldValue',field:'amount',operator:'endsWithDigits',value:''};
 const te=f.editor(tail); te.input('value','001，002、001\n003');
 assert.equal(te.dom.querySelector('[data-value-count]').textContent,'已輸入 3 個值，上限 100');
 assert.equal(f.ui.FilterValues.wire(tail,f.store.getState()).value,'001,002,003');
});

test('K5 only effective-entry manual status is displayed in Chinese', () => {
 const f=batch8Fixture(), show=f.window.batch8Preview.displayCell;
 assert.equal(show('gl','manualAuto','manual'),'人工');
 assert.equal(show('gl','manualAuto','automatic'),'自動');
 assert.equal(show('gl','manualAuto',null),null);
 assert.equal(show('pbcGl','manualAuto','manual'),'manual');
});


// 使用者 2026-10-14 要求拋棄第五步的日期選擇器樣式；移除自製月曆的年月與方向鍵案例。
// 首次失敗：20261014-124620770-eb3019eaf3104b769e0a7e040903dd65；原生日期、Enter、去重與上限改由 frontend-shared-controls.test.cjs 驗證。

test('step five field lists show the source column of each relevant data element field and keep its id', () => {
  const f = batch8Fixture();
  f.store.setMappingCommitted('gl', { mapping: {}, mode: 'dual', options: { rdeFields: [
    { fieldId: 'rde.created-a', sourceColumn: 'CreateDate', label: '傳票建立日', valueType: 'date' },
    { fieldId: 'rde.created-b', sourceColumn: 'EntryDate', label: '傳票建立日', valueType: 'date' }] } });
  const general = f.ui.FilterValues.fieldOptionsHtml(f.store.getState(), 'rde.created-b', []);
  // 第五步選單不再寫「攸關資料元素欄位」，只加註來源欄。使用者 2026-10-13 裁定改名（計畫「收尾四項：調查與裁定」）；同批改名的 C# 測試第一次失敗收據是 20261013-092449164-3847782f6c50488b86a0ac3856e606ca，那次在 C# 測試步驟停止，Node 前端測試尚未執行。
  assert.match(general, /<option value="rde\.created-a">傳票建立日（來源欄：CreateDate）<\/option>/);
  assert.match(general, /<option value="rde\.created-b" selected>傳票建立日（來源欄：EntryDate）<\/option>/);
  const typed = f.filter.ruleControlsHtml({ type: 'typed', fieldId: 'rde.created-a', operator: 'between', from: '', to: '' }, 0, 0);
  assert.match(typed, /<option value="rde\.created-a" selected>傳票建立日（來源欄：CreateDate）<\/option>/);
  assert.match(typed, /<option value="rde\.created-b">傳票建立日（來源欄：EntryDate）<\/option>/);
});

test('all five field picker forms share source-aware labels without changing saved field ids', () => {
 const f=batch8Fixture();
 f.store.setMappingCommitted('gl',{mapping:{},mode:'dual',options:{rdeFields:[
  {fieldId:'rde.a',sourceColumn:'Create<Date>',label:'傳票建立日',valueType:'date'},
  {fieldId:'rde.b',sourceColumn:'EntryDate',label:'傳票建立日',valueType:'date'},
  {fieldId:'rde.t1',sourceColumn:'Dept&A',label:'部門',valueType:'text'},
  {fieldId:'rde.t2',sourceColumn:'DeptB',label:'部門',valueType:'text'}]}});
 const check=(html,selector,ids,prefix='')=>{
  const container=selector ? [...html.matchAll(/<select\b[^>]*>[\s\S]*?<\/select>/g)].map(m=>m[0]).find(select=>select.slice(0,select.indexOf('>')).includes(selector.slice(1,-1))) : html;
  assert.ok(container,selector);
  for(const [id,source] of ids){
   const option=[...container.matchAll(/<option value="([^"]*)"[^>]*>([^<]*)<\/option>/g)].find(m=>m[1]===prefix+id);
   assert.ok(option,id); assert.match(option[2],/來源欄：/); assert.ok(option[2].includes(source),source);
  }
 };
 const dates=[['rde.a','Create&lt;Date&gt;'],['rde.b','EntryDate']];
 const texts=[['rde.t1','Dept&amp;A'],['rde.t2','DeptB']];
 check(f.filter.compoundRuleHtml({type:'group',rules:[]},0,0,false,{matchScope:'row'}),'[data-child-kind]',dates,'field:');
 check(f.ui.FilterValues.fieldOptionsHtml(f.store.getState(),'rde.b',[]),null,dates);
 check(f.filter.ruleControlsHtml({type:'typed',fieldId:'rde.a',operator:'between'},0,0),'[data-rule-bind="fieldId"]',dates);
 // 使用者 2026-10-08 要求重新設計自訂篩選條件：欄位下拉改成欄位按鈕與範例的就地選欄位。按鈕只寫欄位名稱，
 // 和別的欄位同名時才加來源欄；完整說明（含「來源欄：」）放在 title，保存的仍是欄位識別。
 const buttons=(html,attribute,ids)=>{
  for(const [id,source] of ids){
   const button=[...html.matchAll(/<button\b([^>]*)>([^<]*)<\/button>/g)].find(m=>m[1].includes(attribute+'="'+id+'"'));
   assert.ok(button,id); assert.match(button[1],/title="[^"]*來源欄：/); assert.ok(button[1].includes(source),source);
   assert.ok(button[2].includes('（'+source+'）'),'duplicate names show the source column');
  }
 };
 buttons(f.filter.customPickerHtml(),'data-add-field',dates);
 buttons(f.filter.customPickerHtml(),'data-add-field',texts);
 f.filter.viewState.examplePick='manualDepartment';
 buttons(f.filter.exampleListHtml(),'data-example-field',texts);
 f.filter.viewState.examplePick=null;
 const rule={type:'fieldValue',fieldId:'rde.b',operator:'on',value:'2025-01-01'};
 assert.equal(f.ui.FilterValues.wire(rule,f.store.getState()).fieldId,'rde.b');
 assert.doesNotMatch(f.ui.FilterValues.summary(rule,f.store.getState()),/來源欄/,'read-back wording is unchanged');
});

// 使用者 2026-10-13 裁定訊息分三級：error 成為合法等級，其他值照舊不加入。
// 第一次失敗收據 20261013-095454658-c6425ce2422c45fb8480a7ac4834522e（舊斷言把 error 當成不合法）。
test('message entry accepts only info, warn and error and ignores blank or non-text messages',()=>{
 const f=batch8Fixture(),before=f.store.getState().messages.length;
 for(const level of ['ERROR','warning','WARN','',null,12])f.store.addMessage('invalid level',level);
 for(const text of ['', '  \t\n',null,{},12])f.store.addMessage(text,'info');
 assert.equal(f.store.getState().messages.length,before);
 f.store.addMessage('default');f.store.addMessage('reminder','warn');f.store.addMessage('failure','error');
 assert.deepEqual(plain(f.store.getState().messages.slice(0,3).map(m=>[m.text,m.level])),[['failure','error'],['reminder','warn'],['default','info']]);
});

test('message persistence skips a rejected payload, keeps transient failures for retry, and never writes old-project messages',async()=>{
 const f=batch8Fixture(),api=f.window.batch8App,calls=[];
 f.store.setBridgeReady(true);
 let transient=true;
 f.window.JetApi.logAppend=async payload=>{
  calls.push(payload.text);
  if(payload.text==='rejected')throw Object.assign(new Error('synthetic invalid format'),{code:'invalid_payload'});
  if(payload.text==='retry'&&transient)throw Object.assign(new Error('synthetic busy'),{code:'operation_in_progress'});
 };
 for(const text of ['rejected','saved','retry','after'])f.store.addMessage(text,'info');
 api.persistNewMessages(f.store.getState());await settle();
 assert.deepEqual(calls,['rejected','saved','retry']);
 transient=false; await api.flushMessageWrites();
 assert.deepEqual(calls,['rejected','saved','retry','retry','after']);
 await api.flushMessageWrites();assert.equal(calls.length,5);
 f.store.addMessage('old-project','info');api.persistNewMessages(f.store.getState());
 f.store.setProject({projectId:'another'});await api.flushMessageWrites();
 assert.ok(!calls.includes('old-project'));
 f.store.addMessage('new-project','warn');api.persistNewMessages(f.store.getState());await api.flushMessageWrites();
 assert.equal(calls.at(-1),'new-project');
});

// 改動原因：使用者 2026-10-08 畫面註解要求移除假日與補班日卡片的範本入口與資料夾按鈕，
// 原本測範本與資料夾按鈕的 K15 測試，改成確認卡片不再有這兩顆按鈕且保留上傳入口。
test('calendar card has no template or folder button and keeps the upload buttons', () => {
 const f=scenarioLifecycleFixture();
 const context=vm.createContext({window:f.window,document:{querySelector:()=>null},setTimeout:()=>0,clearTimeout(){}});
 vm.runInContext(fs.readFileSync(path.join(root,'steps/import-step.js'),'utf8').replace("  Ui.registerStep('import', render);",'  window.calendarTest={calendarCard,bindCalendarCard,resetWizard};'),context);
 const api=f.window.calendarTest;
 const html=api.calendarCard(null);
 assert.doesNotMatch(html,/export-calendar-templates/);
 assert.doesNotMatch(html,/open-calendar-folder/);
 assert.match(html,/data-action="import-holiday"/);
 assert.match(html,/data-action="import-makeup"/);
 // 使用者 2026-10-14 第三輪勾選頁確認 R3-43（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
 assert.match(html,/假日檔讀 Date_of_Holiday 欄，補班檔讀 Date_of_MakeUpday 欄，日期寫成 2026-12-31 的格式。/);
 assert.match(html,/未調整時預設週六、週日/);
 assert.doesNotMatch(html,/2025 年資料/);
 const dom=batch7Dom(html);api.bindCalendarCard(dom);
 assert.ok(dom.querySelector('[data-action="import-holiday"]'));
});


function k6MappingDifferenceFixture() {
 const dom=batch9TreeDom(), pending=[], actions=[];
 const window={setTimeout,clearTimeout,JetFocus:{defer(){}},JetApi:contractJetApi({
  hostSelectFile:async()=>({filePath:'synthetic.csv',fileName:'synthetic.csv'}),
  importAccountMappingFromFile:async()=>({batchId:'b2',rowCount:4,hasAnyCategory:true,blankCategoryCount:1,mappingOnlyCount:2,unmappedCount:3}),
  queryAccountMappingDifferencePage:payload=>new Promise((resolve,reject)=>pending.push({payload,resolve,reject}))})};
 const context=vm.createContext({window,document:dom.document});
 for(const file of ['state.js','ui-core.js'])vm.runInContext(fs.readFileSync(path.join(root,file),'utf8'),context);
 vm.runInContext(fs.readFileSync(path.join(root,'steps/validate-step.js'),'utf8').replace("  Ui.registerStep('validate', render);",'  window.k6={accountMappingCardHtml,bindAccountMappingCard};'),context);
 const store=window.JetStore;store.setProject({projectId:'k6'});store.setAccountMappingState({batchId:'b1',rowCount:1});
 // 使用者 2026-10-13 裁定成功時只留一則完成訊息，由 Ui.run 接上耗時記下；替身改成包住正式的 Ui.run，只多記下每次作業。
 // 第一次失敗收據 20261013-095454658-c6425ce2422c45fb8480a7ac4834522e（舊替身不經 Ui.run，完成句因此沒有記下）。
 const realRun=window.JetUi.run;
 window.JetUi.run=(label,fn,options)=>{const promise=realRun(label,fn,options);actions.push(promise);return promise;};
 function render(){dom.content.innerHTML=window.k6.accountMappingCardHtml(store.getState().importState.accountMapping,true);window.k6.bindAccountMappingCard(dom.content);return dom.content;}
 return{window,store,dom,pending,actions,render};
}

test('K6 mapping differences load only on demand, preserve paging and retry, and ignore another project',async()=>{
 const f=k6MappingDifferenceFixture();let page=f.render();
 assert.ok(page.querySelector('[data-mapping-difference="mappingOnly"]'),'render both list entries after reopening');
 assert.equal(f.pending.length,0,'rendering does not scan GL');
 page.querySelector('[data-action="import-account-mapping"]').fire('click');await settle();await Promise.all(f.actions);
 page=f.render();assert.match(page.textContent,/本案沒有的科目.*2/);assert.match(page.textContent,/配對檔未列的科目.*3/);
 // 2026-10-10 使用者選擇兩份清單只留名稱與筆數，不再附說明句。
 // 使用者 2026-10-13 勾選頁確認 33：完成訊息只留第一行，「也可匯入前一個案件的配對檔」移到「1. 取得配對檔」的說明
 // （第一次失敗：FrontendMapping 收據 20261013-141147763-5dc8acde89a2431d9568ff02be8bd79e）。
 assert.doesNotMatch(page.textContent,/不會出現在篩選結果/);assert.match(f.store.getState().messages.at(-1).text,/^科目配對匯入完成，共 4 個科目，耗時(?: \d+\.\d 秒|不到 0\.1 秒)。$/);
 // 使用者 2026-10-14 第三輪勾選頁確認 R3-26（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。 很快完成時寫「耗時不到 0.1 秒」。
 assert.match(page.textContent,/在「Standardized Account Name\*」欄填入分類後存檔；也可匯入前一個案件的配對檔。/);
 page.querySelector('[data-mapping-difference="mappingOnly"]').fire('click');assert.equal(f.pending.length,1);
 assert.deepEqual(plain(f.pending[0].payload),{kind:'mappingOnly',cursor:null,pageSize:200});
 f.pending[0].resolve({rows:[{accountCode:'E',accountName:'Extra'}],nextCursor:'next',totalCount:2});await settle();
 page=f.render();assert.match(page.textContent,/E.*Extra/);assert.equal(f.pending.length,1,'redraw does not scan again');
 page.querySelector('[data-difference-list="mappingOnly"] [data-page-next]').fire('click');assert.equal(f.pending[1].payload.cursor,'next');
 f.pending[1].resolve({rows:[{accountCode:'X',accountName:'Outside'}],nextCursor:null,totalCount:2});await settle();
 assert.match(page.textContent,/X.*Outside/);
 page.querySelector('[data-mapping-difference="unmapped"]').fire('click');f.pending[2].reject(new Error('synthetic retry'));await settle();
 const list=page.querySelector('[data-difference-list="unmapped"]');assert.match(list.textContent,/讀取失敗/);
 list.querySelectorAll('button').find(b=>b.textContent==='重試').fire('click');assert.equal(f.pending[3].payload.cursor,null);
 f.store.setProject({projectId:'other'});f.store.setAccountMappingState({batchId:'other',rowCount:1});page=f.render();
 f.pending[3].resolve({rows:[{accountCode:'OLD',accountName:'old result'}],nextCursor:null,totalCount:9});await settle();
 assert.doesNotMatch(page.textContent,/OLD|old result/);assert.match(page.textContent,/尚未核對/);
 assert.equal(f.pending.length,4,'changing projects does not start a source scan');
});

test('mapping import shows positional warning and lets unknown difference counts be retried',async()=>{
 const f=k6MappingDifferenceFixture();
 f.window.JetApi.importAccountMappingFromFile=async()=>({batchId:'saved',rowCount:1,hasAnyCategory:true,
  mappingOnlyCount:null,unmappedCount:null,columnMappingWarning:'依欄位順序將「c1」當成科目編號。'});
 f.render().querySelector('[data-action="import-account-mapping"]').fire('click');await settle();await Promise.all(f.actions);
 assert.ok(f.store.getState().messages.some(m=>m.text==='依欄位順序將「c1」當成科目編號。'));
 const page=f.render();assert.match(page.textContent,/尚未核對/);
 page.querySelector('[data-mapping-difference="unmapped"]').fire('click');
 f.pending[0].resolve({rows:[],nextCursor:null,totalCount:0});await settle();
 assert.equal(f.pending.length,1);assert.match(f.render().textContent,/配對檔未列的科目.*0/);
});

// 低嚴重度事項（使用者 2026-10-19 要求處理）：科目配對檔的欄位內含換行提醒，和 GL、TB 一樣以需要注意的等級顯示在訊息裡。
test('mapping import shows the quoted line-break reminder like GL and TB imports',async()=>{
 const f=k6MappingDifferenceFixture();
 const reminder='「合成科目配對.csv」有 1 列的欄位內含換行，例如第 2 列。引號位置錯誤時，後續資料會併入同一格而使列數減少，請以 Excel 開啟原檔核對。';
 f.window.JetApi.importAccountMappingFromFile=async()=>({batchId:'saved',rowCount:1,hasAnyCategory:true,
  mappingOnlyCount:0,unmappedCount:0,warnings:[reminder]});
 f.render().querySelector('[data-action="import-account-mapping"]').fire('click');await settle();await Promise.all(f.actions);
 const shown=f.store.getState().messages.find(m=>m.text===reminder);
 assert.ok(shown,'the reminder reaches the message panel');
 assert.equal(shown.level,'warn');
});

// 訊息等級只有後端 log.append 接受的那幾種；其他等級不能送進訊息佇列。
function messageLogAllowedLevels(){
 const handler=fs.readFileSync(path.resolve(root,'../../Application/Handlers/MessageLogHandlers.cs'),'utf8');
 const declared=/AllowedLevels\s*=\s*\[([^\]]*)\]/.exec(handler);
 assert.ok(declared,'MessageLogHandlers.cs must declare AllowedLevels');
 return [...declared[1].matchAll(/"([a-z]+)"/g)].map(m=>m[1]);
}

test('mapping import positional warning is logged at a level the message log accepts',async()=>{
 const allowed=messageLogAllowedLevels();
 const f=k6MappingDifferenceFixture();
 f.window.JetApi.importAccountMappingFromFile=async()=>({batchId:'saved',rowCount:1,hasAnyCategory:true,
  mappingOnlyCount:0,unmappedCount:0,columnMappingWarning:'依欄位順序將「c1」當成科目編號。'});
 f.render().querySelector('[data-action="import-account-mapping"]').fire('click');await settle();await Promise.all(f.actions);
 const warning=f.store.getState().messages.find(m=>m.text==='依欄位順序將「c1」當成科目編號。');
 assert.ok(warning,'the positional warning reaches the message panel');
 assert.equal(warning.level,'warn','a column-order guess is a reminder, shown with the warning style');
 assert.ok(allowed.includes(warning.level),'log.append accepts '+allowed.join(', '));
});

test('every Store.addMessage level literal is one the message log accepts',()=>{
 const allowed=messageLogAllowedLevels();
 const files=[];
 (function walk(dir){for(const entry of fs.readdirSync(dir,{withFileTypes:true})){
  const full=path.join(dir,entry.name);
  if(entry.isDirectory())walk(full);else if(entry.name.endsWith('.js'))files.push(full);}})(root);
 const offenders=[];let checked=0;
 for(const file of files){
  const source=fs.readFileSync(file,'utf8');
  for(const match of source.matchAll(/addMessage\(/g)){
   // 依括號與字串邊界切出整個呼叫的頂層引數，第二個引數才是等級。
   let depth=1,i=match.index+match[0].length,quote=null,start=i;const args=[];
   for(;i<source.length&&depth>0;i++){
    const ch=source[i];
    if(quote){if(ch==='\\')i++;else if(ch===quote)quote=null;continue;}
    if(ch==="'"||ch==='"'||ch==='`')quote=ch;
    else if('([{'.includes(ch))depth++;
    else if(')]}'.includes(ch)){depth--;if(depth===0)args.push(source.slice(start,i));}
    else if(ch===','&&depth===1){args.push(source.slice(start,i));start=i+1;}
   }
   if(args.length<2)continue;
   const levels=[...args[args.length-1].matchAll(/'([^']*)'|"([^"]*)"/g)].map(m=>m[1]??m[2]);
   for(const level of levels){checked++;if(!allowed.includes(level))
    offenders.push(path.relative(root,file)+':'+source.slice(0,match.index).split('\n').length+' '+level);}
  }
 }
 assert.ok(checked>40,'the scan must actually see the level literals ('+checked+')');
 assert.deepEqual(offenders,[],'message levels outside '+allowed.join(', '));
});

// 第九節 G2 改用共用欄位條件，沿用清單去重、即時數量及焦點斷言；使用者同意每條 100 值上限。
// 首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
for (const [letter, input, expected] of [
  ['E', ' A，B、A\nC\t B ', 'A,B,C'],
  ['F', ' Alpha，Beta、Alpha\nGamma\t Beta ', 'Alpha,Beta,Gamma'],
  ['H', ' 001，002、001\n003\t002 ', '001,002,003']
]) test('K9 KCT ' + letter + ' displays and updates the actual keyword list count in place', () => {
  const dom = batch9TreeDom(), window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const context = vm.createContext({ window, document: dom.document });
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js'])
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8').replace("  Ui.registerStep('filter', render);",
    '  window.k9 = { addKctToDraft, syncViewState, ruleControlsHtml, render, toWireDraft };'), context);
  const store = window.JetStore, api = window.k9;
  store.setProject({ projectId: 'k9-entry-synthetic' }); api.syncViewState(store.getState());
  const draft = store.getState().filter.draft;
  api.addKctToDraft(draft, window.JetUi.FILTER_KCT_CHECKLIST.find(card => card.letter === letter));
  const rule = draft.groups[0].rules[0]; assert.equal(rule.type, 'fieldValue');
  assert.match(api.ruleControlsHtml(rule, 0, 0), /data-value-count/, 'KCT entry must render its count');
  api.render(dom.content, store.getState());
  const selector = '[data-value-key="' + (letter === 'H' ? 'value' : 'values') + '"]';
  const control = dom.content.querySelector(selector);
  const counter = dom.content.querySelector('[data-value-count]');
  const submitted = () => { const wire = api.toWireDraft(draft).groups[0].rules[0]; return letter === 'H' ? wire.value : wire.values.join(','); };
  const initial = submitted();
  assert.equal(counter.textContent, '已輸入 ' + (initial ? initial.split(',').length : 0) + ' 個值，上限 100');
  control.focus(); control.value = input; control.fire('input');
  assert.equal(submitted(), expected);
  assert.equal(control.value, input, 'keep the raw editor text while submitting distinct values');
  assert.equal(counter.textContent, '已輸入 3 個值，上限 100');
  assert.equal(dom.content.querySelector(selector), control);
  assert.equal(dom.content.querySelector('[data-value-count]'), counter);
  assert.equal(dom.document.activeElement, control);
  control.value = Array.from({length:101}, (_, i) => String(i).padStart(3,'0')).join('\n'); control.fire('input');
  assert.equal(counter.textContent, '已輸入 101 個值，上限 100');
  assert.equal(submitted().split(',').length, 101);
  assert.match(window.JetUi.FilterValues.problem(rule, store.getState()), /100.*拆.*任一符合.*子條件組/);
  control.value = '  \n\t '; control.fire('input');
  assert.equal(counter.textContent, '已輸入 0 個值，上限 100');
  assert.equal(submitted(), '');
  if (letter !== 'H') {
    control.value = 'Alpha,alpha,Alpha'; control.fire('input');
    assert.equal(counter.textContent, '已輸入 2 個值，上限 100');
    assert.equal(submitted(), 'Alpha,alpha');
  }
});

const KCT_A_RATIONALE = 'A：期末前 X天內收入科目有迴轉分錄，表示收入認列過程中可能存在不適當的調整或更正(案件團隊須提供天數)';
const KCT_B_RATIONALE = 'B：借記固定資產科目(如不動產、廠房和設備(PPE))但貸記營業費用或維修費用等費用科目之分錄組合不符合公司營業流程的瞭解，因此屬於為非預期的過帳，可能為舞弊類型分錄及其他調整的特質。';
// C、D、E 的動機逐字照 docs/kct-gl-workflow.md，和 Gui 的 filter-kct-editing 情境原本寫死的文字相同（2026-10-15 S5 搬到這裡）。
const KCT_C_RATIONALE = 'C：查核團隊已發現與收入科目相關的未預期或不尋常過帳(貸方為收入，但借方科目非為應收票據/帳款、預收貨款/合約負債)';
const KCT_D_RATIONALE = 'D：根據查核核團隊的瞭解，收入科目的過帳一般是自動產生，如為人工分錄非屬正常營業流程的會計分錄';
const KCT_E_RATIONALE = 'E：執行長、財務長及高階主管通常不預期會經手會計分錄編製或過帳。且因高階主管所處的位置常有能力能直接或間接操縱會計記錄來進行財務報表舞弊。誘因或壓力愈大，則愈有可能合理化其舞弊行為。';
test('acceptance comments remove duplicate period and population cards but preserve validation results', () => {
  const f = usageJourneyFixture('validate'), data = batch8ValidationData();
  f.store.setLastRun('validate', data); f.draw();
  assert.equal(!!f.$('.stat-card--period'), false);
  assert.equal(!!f.$('.population-summary'), false);
  assert.equal(!!f.$('[data-action="preview-excluded-entries"]'), false);
  assert.match(f.dom.content.textContent, /GL 分錄數|借貸淨額/);
  assert.equal(f.state.lastRuns.validate, data);
  f.draw('filter');
  assert.equal(!!f.$('[data-action="preview-population"]'), false);
  assert.match(f.dom.content.textContent, /GL：查核期間內的分錄/);
  f.draw('import');
  assert.match(f.dom.content.textContent, /GL \(總帳明細\)/);
  assert.match(f.dom.content.textContent, /TB \(試算表\)/);
  const preview = fs.readFileSync(path.join(root, 'data-preview.js'), 'utf8');
  assert.match(preview, /label: 'PBC-GL'/); assert.match(preview, /label: 'PBC-TB'/);
});

test('acceptance category choices keep their checkboxes and hierarchy without a search input', () => {
  const f = usageJourneyFixture('filter');
  f.store.setTaxonomy({ revision: 1, categories: [
    { categoryId: 'parent', label: 'Parent', semanticRole: 'Cash' },
    { categoryId: 'child', label: 'Child', parentCategoryId: 'parent', semanticRole: 'Cash' }
  ] });
  const html = f.window.JetUi.taxonomyPickerHtml(f.state, ['child'], 'debit', '借方分類', {});
  assert.doesNotMatch(html, /搜尋分類|data-category-search/);
  assert.match(html, /value="child" checked/);
  assert.match(html, /data-category-toggle="parent"/);
});

test('acceptance account editor uses 200 rows and drops pending classifications on backend source invalidation', async () => {
  const f = usageJourneyFixture('validate'); f.state.lastRuns.validate = null;
  f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed' });
  f.store.setAccountMappingState({ batchId: 'old-categories', hasAnyCategory: true });
  f.draw();
  assert.equal(f.request('queryAccountMappingPage').payload.pageSize, 200);
  f.request('queryAccountMappingPage').resolve({ rows: [{ accountCode: 'OLD', accountName: 'Old account', categoryId: 'builtin.cash' }], nextCursor: null });
  await settle(); f.draw();
  f.change('[data-account-select-page]', true); chooseAccountCategory(f, 'builtin.revenue');
  f.draw(); f.click('[data-account-apply]'); f.draw();
  assert.match(f.dom.content.textContent, /1 個科目待儲存/);
  f.store.applyMutationEffects({ invalidatedResults: { accountMapping: true, validation: true, prescreen: true, filter: true, filterScenarios: true } });
  assert.equal(f.state.importState.accountMapping, null);
  f.draw(); assert.doesNotMatch(f.dom.content.textContent, /Old account|1 個科目待儲存/);
  f.request('queryAccountMappingPage').resolve({ rows: [{ accountCode: 'NEW', accountName: 'New account', categoryId: null }], nextCursor: null });
  await settle(); f.draw();
  assert.match(f.dom.content.textContent, /New account/);
  assert.doesNotMatch(f.dom.content.textContent, /Old account|1 個科目待儲存/);
});

test('export selection follows actual checked controls, subset, clear, all and a new saved revision', async () => {
  const f = usageJourneyFixture(); f.draw();
  assert.equal(f.$('[data-bind="selected-scenario-count"]').textContent, '已選 3 / 3 個情境');
  f.change('[data-bind="include-prescreen-report"]', false); f.draw();
  f.change('[data-scenario-position="2"]', false); f.draw();
  f.click('[data-action="export-workpaper"]');
  assert.deepEqual(plain(f.request('exportWorkpaperStream').payload), { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 3] });
  f.request('exportWorkpaperStream').resolve({ sheetStats: [], reportArtifacts: [] }); await settle();
  f.click('[data-action="clear-scenario-selection"]'); f.draw();
  assert.equal(f.$('[data-action="export-workpaper"]').disabled, true);
  assert.throws(() => f.click('[data-action="export-workpaper"]'), /enabled/);
  assert.equal(f.requests.filter(r => r.action === 'exportWorkpaperStream').length, 1);
  f.click('[data-action="select-all-scenarios"]'); f.draw();
  assert.equal(f.$('[data-bind="selected-scenario-count"]').textContent, '已選 3 / 3 個情境');
  f.change('[data-scenario-position="1"]', false);
  f.store.setFilterResultRef({ revision: 'r2', populationScope: 'auditPeriod' }); f.draw();
  assert.equal(f.$('[data-bind="selected-scenario-count"]').textContent, '已選 3 / 3 個情境');
  f.click('[data-action="export-workpaper"]');
  assert.deepEqual(plain(f.request('exportWorkpaperStream').payload.scenarioPositions), [1, 2, 3]);
});

for (const invalid of [false, true]) test('export history remains viewable but cannot export after ' + (invalid ? 'rule incompatibility' : 'scenario clearing'), () => {
  const f = usageJourneyFixture();
  f.state.filterResultRef = null;
  if (!invalid) f.store.setSavedScenarios([]);
  else f.state.filterScenarioCheck = { status: 'inconsistent', problems: [{ position: 1, name: '第一個', messages: ['合成欄位已移除'] }] };
  assert.deepEqual(plain(f.window.JetUi.stepGate(f.state, 5).missing), [invalid ? '修改無法套用目前規則的篩選情境' : '儲存至少一個篩選情境']);
  f.store.setReportArtifacts([{ artifactId: 'prior-paper', kind: 'workingPaper', stale: true, fileName: 'Synthetic.xlsx' }]);
  assert.equal(f.window.JetUi.stepGate(f.state, 5).ok, true); f.draw();
  assert.equal(f.$('[data-action="export-workpaper"]').disabled, true);
  // 使用者 2026-10-14 要求黃色提示不過度解釋，句子依同日確認的清單縮短（第一次失敗：FrontendMapping 收據 20261014-085644389-9bd9b20c579e4d95926d6ad0c4881504）。
  assert.match(f.dom.content.textContent, invalid ? /第一個.*合成欄位已移除/ : /還沒有已儲存的篩選情境，請先到「進階條件篩選」儲存，再匯出工作底稿。/);
  assert.match(f.dom.content.textContent, /Synthetic.xlsx/);
});

// 2026-10-09 使用者要求提示避免專業術語、不得過長：先說狀態，逐筆列出情境與原因，下一步只在清單後說一次。
test('scenario problems name each scenario with its reasons and give the next step once', () => {
  const f = usageJourneyFixture();
  const html = f.window.JetUi.filterScenarioProblemsHtml({ filterScenarioCheck: { status: 'needsEdit', problems: [
    { position: 1, name: '第一個', messages: ['合成欄位已移除。'] },
    { position: 2, name: '第二個', messages: ['合成條件無效；', '合成分類已刪除'] }] } });
  // 2026-10-11 使用者裁定前置條件還沒完成的提示一律用琥珀色；舊斷言（紅色）第一次失敗：20261010-171353992-339f59bf533749bea822e143c14c1c0f。
  assert.match(html, /^<div class="form-notice form-notice--warning filter-scenario-problems"><strong>下列情境無法套用目前規則：<\/strong><ul>/);
  assert.match(html, /<li>情境 1「第一個」：合成欄位已移除。<\/li><li>情境 2「第二個」：合成條件無效；合成分類已刪除。<\/li><\/ul>/);
  // 使用者 2026-10-13 勾選頁確認 N1（第一次失敗：FrontendMapping 收據 20261013-141147763-5dc8acde89a2431d9568ff02be8bd79e）。
  // 使用者 2026-10-14 要求黃色提示不過度解釋，句子依同日確認的清單縮短（第一次失敗：FrontendMapping 收據 20261014-085644389-9bd9b20c579e4d95926d6ad0c4881504）。
  assert.equal((html.match(/請到「進階條件篩選」修改後儲存，或移除不需要的情境。/g) || []).length, 1, 'the next step is said once, after the list');
});

for (const [action, method, countKey] of [['import-holiday', 'importHolidayFromFile', 'holidayCount'], ['import-makeup', 'importMakeupDayFromFile', 'makeupDayCount']]) {
  for (const outcome of ['success', 'failure', 'cancel']) test('calendar journey ' + action + ' ' + outcome + ' preserves or clears saved scenarios at the response boundary', async () => {
    const f = usageJourneyFixture('import'), saved = f.state.filter.savedScenarios;
    f.draw(); f.click('[data-task-toggle="calendar"]'); f.draw();
    f.click('[data-action="' + action + '"]');
    assert.deepEqual(plain(f.request('hostSelectFile').payload.extensions), ['.xlsx']);
    f.request('hostSelectFile').resolve(outcome === 'cancel' ? {} : { filePath: 'synthetic.xlsx', fileName: 'synthetic.xlsx' }); await settle();
    if (outcome !== 'cancel') {
      assert.deepEqual(plain(f.request(method).payload), { filePath: 'synthetic.xlsx', fileName: 'synthetic.xlsx' });
      if (outcome === 'failure') f.request(method).reject(new Error('synthetic calendar failure'));
      else f.request(method).resolve({ count: 2, invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true },
        staleState: { validation: false, prescreen: true, filter: false }, reportArtifacts: [] });
      await settle();
    }
    if (outcome === 'success') {
      assert.equal(f.state.filter.savedScenarios.length, 0); assert.equal(f.state.importState.calendar[countKey], 2);
      assert.deepEqual(plain(f.state.filter.draft), { name: '', rationale: '', groups: [] });
    } else { assert.equal(f.state.filter.savedScenarios, saved); if (outcome === 'cancel') assert.equal(f.requests.length, 1); }
    assert.equal(f.state.messages.filter(m => m.text === UPSTREAM_RESET_MESSAGE).length, outcome === 'success' ? 1 : 0);
  });
}

for (const changed of [true, false]) test('weekly calendar consumes the authoritative mutation response: changed=' + changed, async () => {
  const f = usageJourneyFixture('import'), saved = f.state.filter.savedScenarios;
  f.store.setCalendarState({ nonWorkingDays: [0, 6], nonWorkingDaysConfigured: true });
  f.draw(); f.click('[data-task-toggle="calendar"]'); f.draw();
  f.change('[data-weekday="6"]', !changed);
  assert.deepEqual(plain(f.request('calendarSetNonWorkingDays').payload.days), changed ? [0] : [6, 0]);
  f.request('calendarSetNonWorkingDays').resolve({ nonWorkingDays: changed ? [0] : [0, 6],
    invalidatedResults: { validation: false, prescreen: changed, filter: changed, filterScenarios: changed }, reportArtifacts: [] }); await settle();
  assert.equal(changed ? f.state.filter.savedScenarios.length : f.state.filter.savedScenarios, changed ? 0 : saved);
});

for (const step of ['create', 'import', 'mapping', 'validate']) test('upstream notice is rendered in ' + step + ' and disappears after scenarios clear', () => {
  const f = usageJourneyFixture(step); if (step === 'validate') f.state.lastRuns.validate = null; f.draw();
  if (step === 'create') { assert.doesNotMatch(f.dom.content.textContent, /更改已設定好的資料，會清除後面步驟的設定/); f.click('[data-action="edit-project-metadata"]'); f.draw(); }
  assert.match(f.dom.content.textContent, /更改已設定好的資料，會清除後面步驟的設定/);
  f.store.applyMutationEffects({ invalidatedResults: { filter: true, filterScenarios: true } }); f.draw();
  assert.doesNotMatch(f.dom.content.textContent, /更改已設定好的資料，會清除後面步驟的設定/);
});

test('rule upgrade lists each affected scenario and rechecks the current saved list through the button', async () => {
  const f = usageJourneyFixture('filter'); f.state.filterResultRef = null;
  f.state.filterScenarioCheck = { status: 'inconsistent', problems: [
    { position: 1, name: '第一個', messages: ['合成欄位已移除'] }, { position: 2, name: '第二個', messages: ['合成分類已移除'] }] };
  f.draw(); assert.match(f.dom.content.textContent, /第一個.*合成欄位已移除/); assert.match(f.dom.content.textContent, /第二個.*合成分類已移除/);
  f.click('[data-action="resave-scenarios"]'); await settle();
  // 2026-10-13 第五步一次只存或刪一個情境：畫面改送 filterScenarioSave／Remove／Recheck，只帶正在處理的那一個情境：重新檢查不再送出清單，
  // 由後端從資料庫讀出全部情境。原斷言要求送出全部情境的名稱與條件；第一次失敗：FrontendMapping 收據 20261013-103553346-54ebed91711946dd8ff77c3dd1be7b23。
  assert.deepEqual(plain(f.request('filterScenarioRecheck').payload), { populationScope: 'auditPeriod' });
  assert.equal(f.requests.some(r => r.action === 'filterCommit'), false);
});

function prepareUsageMapping(f, kind) {
  const mapping = kind === 'gl' ? { docNum: 'Doc', postDate: 'Date', accNum: 'Code', accName: 'Name', description: 'Memo', amount: 'Amount' }
    : { accNum: 'Code', accName: 'Name', amount: 'Amount' };
  f.store.setImportResult(kind, { batchId: kind + '-source', columns: Object.values(mapping), rowCount: 2 });
  f.store.replaceMappingDraft(kind, mapping); f.store.setMappingMode(kind, kind === 'gl' ? 'signed' : 'direct');
  f.store.setMappingUiMode('classic');
  return mapping;
}
function usageEffects(clear) { return { invalidatedResults: { validation: true, prescreen: clear, filter: clear, filterScenarios: clear },
  staleState: { validation: true, prescreen: clear, filter: false }, reportArtifacts: [] }; }

for (const kind of ['gl', 'tb']) for (const outcome of ['success', 'failure', 'cancel'])
test(kind + ' mapping journey ' + outcome + ' keeps saved scenarios until a successful clearing response', async () => {
  const f = usageJourneyFixture('mapping'), saved = f.state.filter.savedScenarios;
  const mapping = prepareUsageMapping(f, kind), mode = kind === 'gl' ? 'signed' : 'direct';
  const options = plain(f.state.mapping.gl.options);
  f.store.setMappingCommitted(kind, { mapping, mode, options: kind === 'gl' ? options : null });
  f.draw(); f.click('[data-action="remap-' + kind + '"]'); f.draw();
  if (outcome === 'cancel') {
    f.store.setMappingMode(kind, kind === 'gl' ? 'dual' : 'openClose');
    f.store.setMappingDraft(kind, 'accName', 'Code');
    if (kind === 'gl') f.store.replaceGlMappingOptions({ ...options, approvalDateMode: 'sameAsPostDate',
      manualAutoPolicy: { manualValues: ['M'], automaticValues: [], unlistedValueKind: 'automatic', blankValueKind: 'automatic' },
      rdeFields: [{ fieldId: 'rde.fake', sourceColumn: 'Memo', label: '合成欄', valueType: 'text' }] });
    f.draw(); f.click('[data-action="restore-' + kind + '"]');
    assert.deepEqual(plain(f.state.mapping[kind].draft), mapping);
    assert.equal(f.state.mapping[kind][kind === 'gl' ? 'amountMode' : 'changeMode'], mode);
    if (kind === 'gl') assert.deepEqual(plain(f.state.mapping.gl.options), options);
    assert.equal(f.requests.some(r => /^mappingCommit/.test(r.action)), false);
  } else {
    f.click('[data-action="commit-' + kind + '"]');
    const request = f.request(kind === 'gl' ? 'mappingCommitGl' : 'mappingCommitTb');
    assert.deepEqual(plain(request.payload.mapping), mapping);
    if (outcome === 'failure') request.reject(new Error('synthetic mapping failure'));
    else request.resolve({ projectedRowCount: 2, ...usageEffects(true) });
    await settle();
  }
  const clears = outcome === 'success';
  assert.equal(clears ? f.state.filter.savedScenarios.length : f.state.filter.savedScenarios, clears ? 0 : saved);
  assert.equal(f.state.messages.filter(m => m.text === UPSTREAM_RESET_MESSAGE).length, clears ? 1 : 0);
});

for (const kind of ['gl', 'tb']) for (const outcome of ['success', 'failure', 'cancel'])
test(kind + ' import wizard ' + outcome + ' preserves or clears the saved scenario list', async () => {
  const f = usageJourneyFixture('import'), saved = f.state.filter.savedScenarios;
  f.draw(); f.click('[data-task-toggle="' + kind + '"]'); f.draw();
  f.click('[data-action="wizard-replace-' + kind + '"]');
  f.request('hostSelectFiles').resolve({ files: [{ filePath: 'synthetic.csv', fileName: 'synthetic.csv' }] }); await settle();
  f.request('importInspectFile').resolve({ fileType: 'csv', columns: ['Code', 'Amount'], encoding: 'utf-8', delimiter: ',' }); await settle();
  f.draw(); f.click('[data-action="pending-preview"][data-index="0"]');
  f.request('importPreviewFile').resolve({ columns: ['Code', 'Amount'], sampleRows: [['1000', '10']] }); await settle(); f.draw();
  if (outcome === 'cancel') { f.click('[data-action="wizard-cancel"]'); }
  else {
    f.click('[data-action="wizard-confirm"]'); const request = f.request(kind === 'gl' ? 'importGlFromFile' : 'importTbFromFile');
    assert.equal(request.payload.mode, 'replace'); assert.equal(request.payload.sources[0].filePath, 'synthetic.csv');
    if (outcome === 'failure') request.reject(new Error('synthetic import failure'));
    else request.resolve({ batchId: 'next', rowCount: 1, addedRowCount: 1, columns: ['Code', 'Amount'], sources: [], ...usageEffects(true) });
    await settle();
  }
  const clears = outcome === 'success';
  assert.equal(clears ? f.state.filter.savedScenarios.length : f.state.filter.savedScenarios, clears ? 0 : saved);
  if (outcome === 'cancel') assert.equal(f.requests.some(r => /^import(?:Gl|Tb)FromFile$/.test(r.action)), false);
});

for (const outcome of ['success', 'failure', 'cancel']) test('account mapping file ' + outcome + ' consumes mutation effects only on success', async () => {
  const f = usageJourneyFixture('validate'), saved = f.state.filter.savedScenarios;
  f.state.lastRuns.validate = null; f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed' });
  f.draw(); f.click('[data-action="toggle-account-excel"]'); f.draw(); f.click('[data-action="toggle-account-excel"]'); f.draw(); f.click('[data-action="import-account-mapping"]');
  f.request('hostSelectFile').resolve(outcome === 'cancel' ? {} : { filePath: 'synthetic.csv', fileName: 'synthetic.csv' }); await settle();
  if (outcome !== 'cancel') {
    const request = f.request('importAccountMappingFromFile');
    assert.equal(request.payload.filePath, 'synthetic.csv');
    if (outcome === 'failure') request.reject(new Error('synthetic classification failure'));
    else request.resolve({ batchId: 'mapping-next', rowCount: 2, hasAnyCategory: true, mappingOnlyCount: 0, unmappedCount: 0, ...usageEffects(true) });
    await settle();
  }
  assert.equal(outcome === 'success' ? f.state.filter.savedScenarios.length : f.state.filter.savedScenarios, outcome === 'success' ? 0 : saved);
});

test('account editor cancel keeps scenarios; save clears them only after successful response', async () => {
  const f = usageJourneyFixture('validate'), saved = f.state.filter.savedScenarios;
  f.state.lastRuns.validate = null; f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed' }); f.draw();
  f.request('queryAccountMappingPage').resolve({ rows: [{ accountCode: '1000', accountName: '合成科目', categoryId: 'builtin.cash' }], nextCursor: null }); await settle(); f.draw();
  function apply() { f.change('[data-account-select-page]', true); chooseAccountCategory(f, 'builtin.revenue'); f.draw(); f.click('[data-account-apply]'); f.draw(); }
  apply(); f.click('[data-account-cancel]'); f.draw();
  assert.equal(f.state.filter.savedScenarios, saved); assert.equal(f.requests.some(r => r.action === 'accountMappingSave'), false);
  apply(); f.click('[data-account-save]');
  assert.deepEqual(plain(f.request('accountMappingSave').payload.changes), [{ accountCode: '1000', categoryId: 'builtin.revenue' }]);
  f.request('accountMappingSave').reject(new Error('synthetic save failure')); await settle();
  assert.equal(f.state.filter.savedScenarios, saved); f.draw(); f.click('[data-account-save]');
  f.request('accountMappingSave').resolve({ batchId: 'saved', rowCount: 1, hasAnyCategory: true, ...usageEffects(true) }); await settle();
  assert.equal(f.state.filter.savedScenarios.length, 0);
});

test('cancelling project metadata editing sends no write and preserves saved scenarios', () => {
  const f = usageJourneyFixture('create'), saved = f.state.filter.savedScenarios;
  f.draw(); f.click('[data-action="edit-project-metadata"]'); f.draw();
  f.$('[name="lastPeriodStart"]').value = '2025-11-21'; f.$('[data-bind="project-update-form"]').fire('input');
  f.click('[data-action="cancel-project-metadata"]'); f.draw();
  assert.equal(f.state.filter.savedScenarios, saved); assert.equal(f.state.project.lastPeriodStart, '2025-11-20');
  assert.equal(f.requests.some(r => r.action === 'projectUpdate'), false);
});

test('editing then applying an example creates a new scenario, keeps metadata visible and preserves the original', async () => {
  const f = filterOperationFixture({ saved: [savedG()] });
  f.click('[data-action="edit-scenario"][data-index="0"]');
  assert.equal(f.draft().__editingIndex, 0);
  assert.equal(f.$('[data-save-panel]').hidden, false, 'metadata is always visible');
  f.click('[data-action="apply-template"]');
  assert.equal(f.draft().__editingIndex, undefined);
  assert.equal(f.$('[data-save-panel]').hidden, false);
  f.click('[data-action="save-scenario"]'); await settle();
  // 2026-10-13 第五步一次只存或刪一個情境：畫面改送 filterScenarioSave／Remove／Recheck，只帶正在處理的那一個情境。原本看整批第 1 個仍是原情境；
  // 現在看這次是新增（position 為 null）、請求不帶原情境，原情境在畫面清單裡不變。第一次失敗：FrontendMapping 收據 20261013-103553346-54ebed91711946dd8ff77c3dd1be7b23。
  assert.equal(f.commits.length, 1); assert.equal(f.commits[0].method, 'filterScenarioSave');
  assert.equal(f.commits[0].payload.position, null); assert.equal('scenarios' in f.commits[0].payload, false);
  assert.equal(f.store.getState().filter.savedScenarios[0].name, 'G');
  assert.deepEqual(plain(f.store.getState().filter.savedScenarios[0].groups), plain(savedG().groups));
});

test('copying identical conditions and removing the copy resets and reloads matrix columns without changing the original', async () => {
  const f = filterOperationFixture({ saved: [savedG()] }), matrix = [];
  f.store.setLastRun('validate', { resultRef: { runId: 'v1' }, completenessTest: { eligibility: { isEligible: true } } });
  f.store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' });
  f.window.JetApi.queryTagMatrixScenarios = payload => new Promise(resolve => matrix.push({ payload, resolve }));
  f.window.JetApi.queryTagMatrixVoucherPage = async () => ({ rows: [], nextCursor: null });
  f.window.JetApi.queryTagMatrixRowPage = async () => ({ rows: [], nextCursor: null });
  const answer = async names => { matrix.at(-1).resolve({ scenarios: names.map((name, i) => ({ position: i + 1, name, voucherHitCount: 1, rowHitCount: 2, summaryRowCount: 2 })) }); await settle(); };
  f.click('[data-action="toggle-matrix"]'); await answer(['G']);
  assert.equal(f.$('[data-bind="matrix-body"]').getAttribute('data-loaded'), '1');
  f.click('[data-action="copy-scenario"][data-index="0"]'); f.click('[data-action="save-scenario"]'); await settle();
  // 2026-10-13 第五步一次只存或刪一個情境：畫面改送 filterScenarioSave／Remove／Recheck，只帶正在處理的那一個情境。原本看整批的名稱與條件；
  // 現在看另存只送副本（加在最後），條件和原情境相同；合成回應照後端的形狀帶回整批。第一次失敗：FrontendMapping 收據 20261013-103553346-54ebed91711946dd8ff77c3dd1be7b23。
  assert.equal(f.commits[0].method, 'filterScenarioSave'); assert.equal(f.commits[0].payload.position, null);
  assert.equal(f.commits[0].payload.scenario.name, 'G（副本）');
  assert.deepEqual(plain(f.commits[0].payload.scenario.groups), plain(savedG().groups));
  f.commits[0].resolve({ position: 2, savedCount: 2, scenarios: [savedG(), f.commits[0].payload.scenario],
    resultRef: { revision: 'r2', populationScope: 'auditPeriod' } }); await settle();
  assert.equal(f.$('[data-bind="matrix-body"]').hidden, true);
  assert.equal(f.$('[data-bind="matrix-body"]').getAttribute('data-loaded'), null);
  assert.match(f.dom.content.textContent, /尚未產生目前版本的報告/);
  f.click('[data-action="toggle-matrix"]'); await answer(['G', 'G（副本）']);
  f.click('[data-matrix-view="vouchers"]'); await settle();
  assert.equal(f.dom.content.querySelectorAll('[data-bind="matrix-vouchers"] .filter-matrix-name').length, 2);
  f.click('[data-action="remove-scenario"][data-index="1"]'); f.click('[data-action="confirm-remove-scenario"][data-index="1"]'); await settle();
  assert.equal(f.commits[1].method, 'filterScenarioRemove'); assert.equal(f.commits[1].payload.position, 2);
  f.commits[1].resolve({ position: null, savedCount: 1, scenarios: [savedG()], resultRef: { revision: 'r3', populationScope: 'auditPeriod' } }); await settle();
  assert.equal(f.$('[data-bind="matrix-body"]').hidden, true);
  assert.equal(f.$('[data-bind="matrix-body"]').getAttribute('data-loaded'), null);
  f.click('[data-action="toggle-matrix"]'); await answer(['G']);
  f.click('[data-matrix-view="vouchers"]'); await settle();
  assert.equal(f.dom.content.querySelectorAll('[data-bind="matrix-vouchers"] .filter-matrix-name').length, 1);
  assert.deepEqual(plain(f.store.getState().filter.savedScenarios[0].groups), plain(savedG().groups));
  assert.equal(matrix.length, 3);
});

test('removing the active condition group keeps the other group and leaves saved scenarios unchanged', () => {
  const f = filterOperationFixture({ saved: [savedG()] });
  f.click('[data-kct-letter="G"]'); f.click('[data-action="add-set"]'); f.click('[data-kct-letter="H"]');
  assert.equal(f.draft().groups.length, 2);
  f.click('[data-action="remove-set"][data-gi="1"]');
  // 第九節 G2：用欄位與比較方式辨識 G。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  assert.equal(f.draft().groups.length, 1); assert.equal(f.draft().groups[0].rules[0].field, 'description');
  assert.equal(f.draft().groups[0].rules[0].operator, 'isBlank');
  assert.equal(f.dom.content.querySelectorAll('[data-action="remove-set"]').length, 0);
  assert.equal(f.store.getState().filter.savedScenarios.length, 1); assert.equal(f.commits.length, 0);
});

test('clicking A off, B on, B off and A on keeps the card, name and rationale inputs in step', () => {
  const f = filterOperationFixture();
  f.click('[data-kct-letter="A"]'); 
  // 合成 DOM 的文字區塊以內文呈現，重繪後尚未被程式改寫時讀內文。
  const shown = node => node.value || node.textContent;
  const read = () => [f.pressed('A'), f.pressed('B'), f.$('[data-bind="scenario-name"]').value, shown(f.$('[data-bind="scenario-rationale"]'))];
  assert.deepEqual(read(), [true, false, 'A', KCT_A_RATIONALE]);
  f.click('[data-kct-letter="A"]'); assert.deepEqual(read(), [false, false, '', '']);
  f.click('[data-kct-letter="B"]'); assert.deepEqual(read(), [false, true, 'B', KCT_B_RATIONALE]);
  f.click('[data-kct-letter="B"]'); assert.deepEqual(read(), [false, false, '', '']);
  f.click('[data-kct-letter="A"]'); assert.deepEqual(read(), [true, false, 'A', KCT_A_RATIONALE]);
  f.click('[data-kct-letter="B"]'); assert.deepEqual(read(), [true, true, 'A+B', KCT_A_RATIONALE + '\n' + KCT_B_RATIONALE]);
});

// 使用者 2026-10-15 在待決事項勾選頁選 S5：Gui 的 filter-kct-editing 情境原本逐張點 A 到 E 核對單張動機，B 卡另外核對加入的條件，
// 再點一次取消，共 10 次操作。改在這裡逐張點擊核對同一件事，之後刪掉那 10 次 Gui 操作。
test('clicking each KCT card A to E alone fills its own rationale, B adds its account category pair, and clicking again removes it', () => {
  const f = filterOperationFixture();
  const expected = { A: KCT_A_RATIONALE, B: KCT_B_RATIONALE, C: KCT_C_RATIONALE, D: KCT_D_RATIONALE, E: KCT_E_RATIONALE };
  for (const letter of ['A', 'B', 'C', 'D', 'E']) {
    f.click('[data-kct-letter="' + letter + '"]');
    assert.equal(f.draft().rationale, expected[letter], letter);
    assert.equal(f.pressed(letter), true, letter + ' is marked as added');
    if (letter === 'B') {
      const rule = f.draft().groups[0].rules[0];
      assert.deepEqual([rule.__kctLetter, rule.type], ['B', 'specialAccountCategoryPair']);
    }
    f.click('[data-kct-letter="' + letter + '"]');
    assert.equal(f.draft().groups.flatMap(group => group.rules).length, 0, letter + ' is removed by the second click');
    assert.equal(f.pressed(letter), false, letter);
  }
});

test('F1 an automatic KCT name already used by a saved scenario gets a number and saves', async () => {
  const f = filterOperationFixture({ saved: [savedG()] });
  f.click('[data-kct-letter="G"]'); 
  assert.equal(f.$('[data-bind="scenario-name"]').value, 'G 2');
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.commits.length, 1);
  // 2026-10-13 第五步一次只存或刪一個情境：畫面改送 filterScenarioSave／Remove／Recheck，只帶正在處理的那一個情境。原本看整批名稱是 G 與 G 2；
  // 現在看新增的這一個叫 G 2、加在最後。第一次失敗：FrontendMapping 收據 20261013-103553346-54ebed91711946dd8ff77c3dd1be7b23。
  assert.equal(f.commits[0].method, 'filterScenarioSave'); assert.equal(f.commits[0].payload.position, null);
  assert.equal(f.commits[0].payload.scenario.name, 'G 2');
});

test('F2 a duplicate name is explained beside the save button and nothing is sent', async () => {
  const f = filterOperationFixture({ saved: [savedG('季底檢查')] });
  f.click('[data-kct-letter="G"]'); 
  f.type('[data-bind="scenario-name"]', ' 季底檢查 ');
  f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.commits.length, 0);
  assert.match(f.notice(), /已有同名情境「季底檢查」，請改用其他名稱。/);
});

test('F2 the ten-scenario limit is shown beside the save button without a completed message', async () => {
  const f = filterOperationFixture({ saved: Array.from({ length: 10 }, (_, i) => savedG('S' + (i + 1))) });
  f.click('[data-kct-letter="G"]'); 
  f.click('[data-action="save-scenario"]'); await settle(); await settle();
  assert.equal(f.commits.length, 0);
  assert.equal(f.notice(), SCENARIO_LIMIT_MESSAGE);
  assert.ok(!f.messages().some(text => text.startsWith('儲存篩選情境完成')), f.messages().join(' / '));
});

test('F2 a backend failure without a row position is shown beside the save button', async () => {
  const f = filterOperationFixture();
  f.click('[data-kct-letter="G"]'); 
  f.click('[data-action="save-scenario"]'); await settle();
  f.commits[0].reject(Object.assign(new Error('情境名稱重複：「G」。'), { code: 'invalid_scenario', details: null }));
  await settle(); await settle();
  assert.equal(f.notice(), '儲存失敗：情境名稱重複：「G」。');
});

// 第九節 G2：換比較方式後撤銷 G 身分，原本的身分與命名斷言不變。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
test('F3 changing the G row to another field comparison stops counting it as card G', () => {
  const f = filterOperationFixture();
  f.click('[data-kct-letter="G"]'); 
  f.choose('.rule-row[data-gi="0"][data-ri="0"] [data-value-kind]', 'contains');
  assert.equal(f.pressed('G'), false);
  assert.equal(f.draft().groups[0].rules[0].__kctLetter, undefined);
  assert.notEqual(f.$('[data-bind="scenario-name"]').value, 'G');
  const rationale = f.$('[data-bind="scenario-rationale"]');
  assert.doesNotMatch(rationale.value || rationale.textContent, /空白摘要/);
});

test('F6 a backend error after an empty group is marked on the row the auditor sees', async () => {
  const f = filterOperationFixture();
  f.click('[data-action="add-set"]');
  f.click('[data-kct-letter="G"]');
  assert.equal(f.draft().groups.length, 2); assert.equal(f.draft().groups[0].rules.length, 0);
  f.click('[data-action="save-scenario"]'); await settle();
  f.commits[0].reject(Object.assign(new Error('第 1 組第 1 條：合成錯誤。'), { code: 'invalid_scenario',
    details: [{ group: 1, rule: 1, message: '合成錯誤。' }] }));
  await settle(); await settle();
  const row = f.$('.rule-row[data-gi="1"][data-ri="0"]');
  assert.ok(row.classList.contains('rule-row--invalid'));
  assert.equal(row.querySelector('[data-rule-error]').textContent, '合成錯誤。');
  assert.equal(f.notice(), '');
});

// 2026-10-13 第五步一次只存或刪一個情境（使用者對「情境卡住」選「本計畫內修好」）：儲存、移除與重新檢查只送正在處理的
// 那一個情境，其他情境由後端照資料庫原樣保留；錯誤只標在它所屬的情境。這幾支記錄每個送出的請求與方法名稱。
function oneAtATimeFixture() {
  const f = filterOperationFixture({ saved: [savedG('情境甲'), savedG('情境乙'), savedG('情境丙')] });
  const sent = [];
  f.window.JetApi = contractJetApi(new Proxy({}, { get: (_, method) => payload =>
    new Promise((resolve, reject) => sent.push({ method, payload, resolve, reject })) }));
  f.store.getState().filterScenarioCheck = { status: 'needsEdit', problems: [
    { position: 1, name: '情境甲', messages: ['合成原因一'] }, { position: 2, name: '情境乙', messages: ['合成原因二'] }] };
  f.store.setFilterDraft({ name: '', rationale: '', groups: [] });
  const scenarioRequests = () => sent.filter(request => /^filter/.test(request.method));
  return { ...f, sent, scenarioRequests };
}

test('one at a time: saving an edited scenario sends only that scenario with its position and shown name', async () => {
  const f = oneAtATimeFixture();
  f.click('[data-action="edit-scenario"][data-index="0"]'); 
  f.type('[data-bind="scenario-name"]', '情境甲（改名）');
  f.click('[data-action="save-scenario"]'); await settle();
  const requests = f.scenarioRequests();
  assert.equal(requests.length, 1);
  assert.equal(requests[0].method, 'filterScenarioSave');
  const payload = requests[0].payload;
  assert.equal(payload.position, 1);
  assert.equal(payload.expectedName, '情境甲');
  assert.equal(payload.scenario.name, '情境甲（改名）');
  assert.equal(payload.populationScope, 'auditPeriod');
  assert.equal('scenarios' in payload, false);
  assert.equal(JSON.stringify(payload).includes('"__'), false);
});

test('one at a time: rechecking sends no scenario list', async () => {
  const f = oneAtATimeFixture();
  f.click('[data-action="resave-scenarios"]'); await settle();
  const requests = f.scenarioRequests();
  assert.equal(requests.length, 1);
  assert.equal(requests[0].method, 'filterScenarioRecheck');
  assert.equal('scenarios' in requests[0].payload, false);
  assert.equal(requests[0].payload.populationScope, 'auditPeriod');
});

test('one at a time: confirming a removal sends only that position and shown name', async () => {
  const f = oneAtATimeFixture();
  f.click('[data-action="remove-scenario"][data-index="1"]'); f.click('[data-action="confirm-remove-scenario"][data-index="1"]'); await settle();
  const requests = f.scenarioRequests();
  assert.equal(requests.length, 1);
  assert.equal(requests[0].method, 'filterScenarioRemove');
  assert.equal(requests[0].payload.position, 2);
  assert.equal(requests[0].payload.expectedName, '情境乙');
  assert.equal('scenarios' in requests[0].payload, false);
  assert.equal(requests[0].payload.populationScope, 'auditPeriod');
});

test('one at a time: an error that belongs to another scenario is not marked on the draft and names that scenario', async () => {
  const f = oneAtATimeFixture();
  f.click('[data-action="edit-scenario"][data-index="0"]'); 
  f.click('[data-action="save-scenario"]'); await settle();
  f.scenarioRequests().at(-1).reject(Object.assign(new Error('第 1 組第 1 條：合成錯誤。'), { code: 'invalid_scenario',
    details: [{ group: 1, rule: 1, message: '合成錯誤。', scenarioPosition: 2, scenarioName: '情境乙' }] }));
  await settle(); await settle();
  assert.equal(f.dom.content.querySelectorAll('.rule-row--invalid').length, 0);
  assert.equal(f.$('.rule-row[data-gi="0"][data-ri="0"]').querySelector('[data-rule-error]'), null);
  assert.match(f.notice(), /情境 2「情境乙」/);
  assert.match(f.notice(), /合成錯誤/);
});

test('one at a time: an error for the scenario being saved is still marked on its rule row', async () => {
  const f = oneAtATimeFixture();
  f.click('[data-action="edit-scenario"][data-index="0"]'); 
  f.click('[data-action="save-scenario"]'); await settle();
  f.scenarioRequests().at(-1).reject(Object.assign(new Error('第 1 組第 1 條：合成錯誤。'), { code: 'invalid_scenario',
    details: [{ group: 1, rule: 1, message: '合成錯誤。', scenarioPosition: 1, scenarioName: '情境甲' }] }));
  await settle(); await settle();
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]');
  assert.ok(row.classList.contains('rule-row--invalid'));
  assert.equal(row.querySelector('[data-rule-error]').textContent, '合成錯誤。');
  assert.equal(f.notice(), '');
});

test('one at a time: a save that leaves other scenarios to fix shows the new list and keeps results locked', async () => {
  const f = oneAtATimeFixture();
  f.click('[data-action="edit-scenario"][data-index="0"]'); 
  f.click('[data-action="save-scenario"]'); await settle();
  const request = f.scenarioRequests()[0], saved = f.store.getState().filter.savedScenarios;
  request.resolve({ ok: true, position: 1, savedCount: 3, scenarios: [request.payload.scenario, saved[1], saved[2]], resultRef: null,
    filterScenarioCheck: { status: 'needsEdit', recalculatedCount: 0, problems: [{ position: 2, name: '情境乙', messages: ['合成原因二'] }] },
    staleState: { validation: false, prescreen: false, filter: true } });
  await settle(); await settle();
  const state = f.store.getState();
  assert.equal(state.filterResultRef, null);
  assert.equal(state.staleState.filter, true);
  assert.deepEqual(plain(state.filterScenarioCheck.problems.map(problem => problem.name)), ['情境乙']);
  const list = f.$('.filter-scenario-problems');
  assert.ok(list, 'the list of scenarios to fix stays on screen');
  assert.equal(list.querySelectorAll('li').length, 1);
  assert.match(list.textContent, /情境 2「情境乙」/);
  assert.doesNotMatch(list.textContent, /情境甲/);
  assert.ok(f.$('[data-action="resave-scenarios"]'));
  assert.equal(f.window.JetUi.filterScenarioMissing(state), '修改無法套用目前規則的篩選情境');
  assert.equal(f.draft().__editingIndex, undefined);
  assert.equal(f.draft().groups.length, 0);
});

test('one at a time: the save that fixes the last scenario unlocks the report and step six', async () => {
  const f = oneAtATimeFixture();
  f.store.setLastRun('validate', { resultRef: { runId: 'v1' }, completenessTest: { eligibility: { isEligible: true } } });
  f.click('[data-action="edit-scenario"][data-index="1"]'); 
  assert.ok(f.$('.filter-scenario-problems'));
  assert.equal(f.$('[data-action="export-criteria-report"]').disabled, true);
  f.click('[data-action="save-scenario"]'); await settle();
  const request = f.scenarioRequests()[0], saved = f.store.getState().filter.savedScenarios;
  request.resolve({ ok: true, position: 2, savedCount: 3, scenarios: [saved[0], request.payload.scenario, saved[2]],
    resultRef: { revision: 'r-fixed', generatedUtc: 'r-fixed', populationScope: 'auditPeriod' },
    filterScenarioCheck: { status: 'current', recalculatedCount: 2, problems: [] },
    staleState: { validation: false, prescreen: false, filter: false } });
  await settle(); await settle();
  const state = f.store.getState();
  assert.equal(state.filterResultRef.revision, 'r-fixed');
  assert.equal(state.staleState.filter, false);
  assert.equal(f.$('.filter-scenario-problems'), null);
  assert.equal(f.$('[data-action="resave-scenarios"]'), null);
  assert.equal(f.window.JetUi.filterScenarioMissing(state), null);
  assert.equal(f.$('[data-action="export-criteria-report"]').disabled, false);
  assert.equal(f.draft().__editingIndex, undefined);
  assert.equal(f.draft().groups.length, 0);
});

test('one at a time: saving a copy appends it and clears the submitted draft', async () => {
  const f = oneAtATimeFixture();
  f.click('[data-action="copy-scenario"][data-index="0"]'); 
  f.click('[data-action="save-scenario"]'); await settle();
  const request = f.scenarioRequests()[0], saved = f.store.getState().filter.savedScenarios;
  assert.equal(request.method, 'filterScenarioSave');
  assert.equal(request.payload.position, null);
  assert.equal('expectedName' in request.payload, false);
  assert.equal(request.payload.scenario.name, '情境甲（副本）');
  request.resolve({ ok: true, position: 4, savedCount: 4, scenarios: [...saved, request.payload.scenario], resultRef: null,
    filterScenarioCheck: { status: 'needsEdit', recalculatedCount: 0, problems: [{ position: 1, name: '情境甲', messages: ['合成原因一'] }] },
    staleState: { validation: false, prescreen: false, filter: true } });
  await settle(); await settle();
  assert.equal(f.draft().__editingIndex, undefined);
  assert.equal(f.draft().__editingSavedRef, undefined);
  assert.equal(f.draft().groups.length, 0);
  assert.equal(f.store.getState().filter.savedScenarios.length, 4);
});

test('one at a time: a screen list that no longer matches is explained beside the save button and changes nothing', async () => {
  const f = oneAtATimeFixture(), before = f.store.getState().filter.savedScenarios;
  f.click('[data-action="edit-scenario"][data-index="0"]'); 
  f.click('[data-action="save-scenario"]'); await settle();
  f.scenarioRequests()[0].reject(Object.assign(new Error('合成清單不一致。'), { code: 'stale_result' }));
  await settle(); await settle();
  assert.equal(f.notice(), '儲存失敗：合成清單不一致。');
  assert.equal(f.store.getState().filter.savedScenarios, before);
});

// 以下取代 2026-10-13 刪除的四個原始碼比對測試（依第一項測試計畫第 3.2 節：產品改動弄壞已排定行為化的字串測試時，
// 當場寫行為測試再刪除）：FilterAstFrontendContractTests.SharedWireProjection_PreservesMatchScopeAndValuesArrayWhileRemovingUiMetadata、
// FilterKctMetadataFrontendTests.PreviewCommitResaveAndRemove_AllUseTheSharedScenarioProjection、
// FilterPopulationScopeMirrorTests.FilterActions_AlwaysSendAuditScopeAndRejectLatePreviewAcrossDraftChanges、
// FilterTemplatesFrontendTests.InvalidScenarioDetails_AreMarkedOnTheRuleRow。四個方法的第一次失敗：Public 收據
// 20261013-103826650-632ded11dde247d0b97d4e5ea6dd63bf。儲存、移除與重新檢查的送出形狀見上面幾支 one at a time 測試。
function projectionDraft(values) {
  return { name: '投影檢查', rationale: '合成動機', __nameDirty: true, __rationaleDirty: true,
    groups: [{ join: 'AND', matchScope: 'sameVoucher', rules: [
      { type: 'fieldValue', join: 'AND', field: 'description', operator: 'contains', values, __probe: 'screen only' }] }] };
}

test('shared projection: draft preview, saving and saved-scenario preview keep match scope and value arrays, drop screen-only keys and send the audit period', async () => {
  const f = oneAtATimeFixture(), values = ['Alpha', 'Beta'];
  f.store.setFilterDraft(projectionDraft(values));
  f.click('[data-action="preview-scenario"]'); await settle();
  const preview = f.sent.find(request => request.method === 'filterPreview');
  assert.ok(preview, 'the draft preview is sent');
  preview.reject(new Error('合成預覽失敗。')); await settle(); await settle();
  f.click('[data-action="save-scenario"]'); await settle();
  const save = f.sent.find(request => request.method === 'filterScenarioSave');
  assert.ok(save, 'the save is sent');
  for (const request of [preview, save]) {
    const group = request.payload.scenario.groups[0], rule = group.rules[0];
    assert.equal(request.payload.populationScope, 'auditPeriod');
    assert.equal(group.matchScope, 'sameVoucher');
    assert.deepEqual(plain(rule.values), ['Alpha', 'Beta']);
    assert.notEqual(rule.values, values, 'the request carries a copy, not the draft array');
    assert.equal(JSON.stringify(request.payload).includes('"__'), false);
    assert.equal(JSON.stringify(request.payload).includes('allProjected'), false);
  }
  save.reject(new Error('合成儲存失敗。')); await settle(); await settle();
  f.click('[data-action="toggle-scenario"][data-index="0"]'); await settle();
  const savedPreview = f.sent.filter(request => request.method === 'filterPreview').at(-1);
  assert.notEqual(savedPreview, preview, 'opening a saved scenario previews it');
  assert.equal(savedPreview.payload.populationScope, 'auditPeriod');
  assert.equal(savedPreview.payload.scenario.name, '情境甲');
  assert.equal(savedPreview.payload.scenario.source, 'kct');
  assert.deepEqual(plain(savedPreview.payload.scenario.groups), plain(savedG().groups));
  assert.equal(JSON.stringify(savedPreview.payload).includes('"__'), false);
});

test('late preview: a preview answered after the conditions changed is not shown', async () => {
  const f = oneAtATimeFixture();
  f.store.setFilterDraft(projectionDraft(['Alpha']));
  f.click('[data-action="preview-scenario"]'); await settle();
  const draft = f.draft(); draft.groups[0].rules[0].values = ['Alpha', 'Gamma']; f.store.setFilterDraft(draft);
  f.sent.find(request => request.method === 'filterPreview').resolve({ scenario: { name: '投影檢查', populationScope: 'auditPeriod',
    count: 9, voucherCount: 3, summaryRowCount: 9, previewRows: [] } });
  await settle(); await settle();
  assert.equal(f.sent.some(request => request.method === 'queryFilterVoucherPage'), false);
  assert.equal(f.store.getState().filter.preview, null);
});

test('late preview: a preview whose population differs from the request is not shown', async () => {
  const f = oneAtATimeFixture();
  f.store.setFilterDraft(projectionDraft(['Alpha']));
  f.click('[data-action="preview-scenario"]'); await settle();
  f.sent.find(request => request.method === 'filterPreview').resolve({ scenario: { name: '投影檢查', populationScope: 'allProjected',
    count: 9, voucherCount: 3, summaryRowCount: 9, previewRows: [] } });
  await settle(); await settle();
  const page = f.sent.find(request => request.method === 'queryFilterVoucherPage');
  if (page) { page.resolve({ rows: [], nextCursor: null }); await settle(); await settle(); }
  assert.equal(f.store.getState().filter.preview, null);
});

test('rule errors: the bridge keeps error details, a current preview error is marked and late errors are not', async () => {
  // 連線層把 details（含情境位置）原樣交給畫面。
  const posted = [], listeners = [];
  const apiWindow = { chrome: { webview: { postMessage: message => posted.push(message), addEventListener: (_, fn) => listeners.push(fn) } } };
  vm.runInContext(fs.readFileSync(path.join(root, 'jet-api.js'), 'utf8'), vm.createContext({ window: apiWindow }));
  const pending = apiWindow.JetApi.filterScenarioSave({ populationScope: 'auditPeriod', position: null, scenario: { name: 'x' } });
  assert.equal(posted[0].action, 'filter.scenario.save');
  const details = [{ group: 1, rule: 1, message: '合成錯誤。', scenarioPosition: 2, scenarioName: '情境乙' }];
  listeners.forEach(fn => fn({ data: { requestId: posted[0].requestId, ok: false,
    error: { code: 'invalid_scenario', message: '第 1 組第 1 條：合成錯誤。', details } } }));
  const error = await pending.then(() => null, rejected => rejected);
  assert.equal(error.code, 'invalid_scenario');
  assert.deepEqual(plain(error.details), details);

  // 目前的預覽錯誤標在條件列；條件改過之後才回來的錯誤不標。
  const f = oneAtATimeFixture();
  f.store.setFilterDraft(projectionDraft(['Alpha']));
  f.click('[data-action="preview-scenario"]'); await settle();
  f.sent.find(request => request.method === 'filterPreview').reject(Object.assign(new Error('第 1 組第 1 條：合成錯誤。'),
    { code: 'invalid_scenario', details: [{ group: 1, rule: 1, message: '合成錯誤。' }] }));
  await settle(); await settle();
  assert.ok(f.$('.rule-row[data-gi="0"][data-ri="0"]').classList.contains('rule-row--invalid'));
  f.click('[data-action="preview-scenario"]'); await settle();
  const draft = f.draft(); draft.groups[0].rules[0].values = ['Alpha', 'Gamma']; f.store.setFilterDraft(draft);
  f.sent.filter(request => request.method === 'filterPreview').at(-1).reject(Object.assign(new Error('第 1 組第 1 條：另一個錯誤。'),
    { code: 'invalid_scenario', details: [{ group: 1, rule: 1, message: '另一個錯誤。' }] }));
  await settle(); await settle();
  assert.equal(f.dom.content.textContent.includes('另一個錯誤。'), false);
});

test('rule errors: a save error that returns after the conditions changed is neither marked nor shown beside the save button', async () => {
  const f = oneAtATimeFixture();
  f.store.setFilterDraft(projectionDraft(['Alpha']));
  f.click('[data-action="save-scenario"]'); await settle();
  const draft = f.draft(); draft.groups[0].rules[0].values = ['Alpha', 'Gamma']; f.store.setFilterDraft(draft);
  f.scenarioRequests().at(-1).reject(Object.assign(new Error('第 1 組第 1 條：合成錯誤。'), { code: 'invalid_scenario',
    details: [{ group: 1, rule: 1, message: '合成錯誤。', scenarioPosition: 4, scenarioName: '投影檢查' }] }));
  await settle(); await settle();
  assert.equal(f.dom.content.querySelectorAll('.rule-row--invalid').length, 0);
  assert.equal(f.notice(), '');
});

for (const remaining of [2, 0]) test('one at a time: rechecking reports the result without failing when ' + remaining + ' scenarios still need edits', async () => {
  const f = oneAtATimeFixture();
  f.click('[data-action="resave-scenarios"]'); await settle();
  const saved = f.store.getState().filter.savedScenarios;
  const problems = remaining ? [{ position: 1, name: '情境甲', messages: ['合成原因一'] }, { position: 2, name: '情境乙', messages: ['合成原因二'] }] : [];
  f.scenarioRequests()[0].resolve({ ok: true, position: null, savedCount: 3, scenarios: saved.slice(),
    resultRef: remaining ? null : { revision: 'r-recheck', populationScope: 'auditPeriod' },
    filterScenarioCheck: { status: remaining ? 'needsEdit' : 'current', recalculatedCount: remaining ? 1 : 3, problems },
    staleState: { validation: false, prescreen: false, filter: !!remaining } });
  await settle(); await settle();
  const messages = f.store.getState().messages.filter(message => /重新檢查/.test(message.text));
  assert.equal(messages.length, 1, f.messages().join(' / '));
  assert.equal(messages[0].level, 'info');
  assert.doesNotMatch(messages[0].text, /失敗/);
  // 句子是 2026-10-13 的暫定文字，只比對意思：更新與仍需修改的數目。
  if (remaining) { assert.match(messages[0].text, /1 個已更新/); assert.match(messages[0].text, /2 個仍需修改/); }
  else assert.match(messages[0].text, /3 個情境/);
  assert.equal(!!f.$('.filter-scenario-problems'), !!remaining);
});

function namingFixture() {
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const document = { querySelector: () => null, createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8').replace("  Ui.registerStep('filter', render);",
    '  window.naming = { refreshAutomaticMetadata, addKctToDraft, removeKctFromActiveGroup, applyTemplate, toWireScenario, toWireDraft, syncViewState };'), context);
  const store = window.JetStore, api = window.naming;
  store.setProject({ projectId: 'naming-synthetic' }); api.syncViewState(store.getState());
  const card = letter => window.JetUi.FILTER_KCT_CHECKLIST.find(x => x.letter === letter);
  function toggle(letter, remove = false) {
    const draft = store.getState().filter.draft;
    api[remove ? 'removeKctFromActiveGroup' : 'addKctToDraft'](draft, card(letter));
    api.refreshAutomaticMetadata(draft, true); store.setFilterDraft(draft);
    return draft;
  }
  function amountAbove(value) {
    const rule = window.JetUi.FilterValues.create('money');
    delete rule.fieldId; rule.field = 'amount'; rule.operator = 'greaterThan'; rule.value = value; rule.join = 'AND';
    return rule;
  }
  return { window, store, api, toggle, amountAbove };
}

test('a KCT card and a custom condition in one group are both named (2026-10-07 ruling)', () => {
  const f = namingFixture(), draft = f.toggle('G');
  draft.groups[0].rules.push(f.amountAbove('40000')); f.api.refreshAutomaticMetadata(draft, true);
  assert.equal(draft.name, 'G+自訂');
  assert.equal(draft.rationale, 'G：空白摘要\n自訂：金額絕對值 大於「40000」；空白不列入');
  f.toggle('H');
  assert.equal(draft.name, 'G+H+自訂');
});

test('F3 changing the I bracket child stops counting it as card I, filled values keep A and H', () => {
  const f = namingFixture(), draft = f.toggle('I');
  draft.groups[0].rules[0].rules[0].operator = 'isWeekend'; f.api.refreshAutomaticMetadata(draft, false);
  assert.equal(draft.groups[0].rules[0].__kctLetter, undefined);
  assert.equal(f.api.toWireDraft(draft).editorOrigins.groups[0].letters[0], null);
  const kept = namingFixture(), values = kept.toggle('A'); kept.toggle('H');
  values.groups[0].rules[0].windowDays = '10'; values.groups[0].rules[1].keywords = '999999';
  kept.api.refreshAutomaticMetadata(values, false);
  assert.deepEqual(plain(values.groups[0].rules.map(r => r.__kctLetter)), ['A', 'H']);
  assert.equal(values.name, 'A+H');
});

test('example text stays while values are filled and turns automatic once conditions are added or removed', () => {
  const f = namingFixture();
  f.api.applyTemplate('nonBusinessDayExcludingDates');
  const draft = f.store.getState().filter.draft;
  assert.equal(draft.name, '非營業日且排除指定日期');
  draft.groups[0].rules[1].values = ['2025-12-31']; f.api.refreshAutomaticMetadata(draft, false);
  assert.equal(draft.name, '非營業日且排除指定日期');
  assert.equal(draft.rationale, '非營業日過帳的分錄，另排除個案已知的例外日期。');
  f.toggle('I', true);
  assert.notEqual(draft.name, '非營業日且排除指定日期');
  assert.notEqual(draft.rationale, '非營業日過帳的分錄，另排除個案已知的例外日期。');
  f.api.applyTemplate('nonBusinessDayExcludingDates');
  const edited = f.store.getState().filter.draft;
  f.store.patchFilterDraftMeta({ name: '審計員自訂名稱', __nameDirty: true, __templateName: false });
  f.toggle('G');
  assert.equal(edited.name, '審計員自訂名稱');
  assert.notEqual(edited.rationale, '非營業日過帳的分錄，另排除個案已知的例外日期。');
});

test('F1 an example name already used by a saved scenario gets a number', () => {
  const f = namingFixture();
  f.store.setSavedScenarios([{ name: '非營業日且排除指定日期', rationale: 'x', groups: [] }]);
  f.api.applyTemplate('nonBusinessDayExcludingDates');
  assert.equal(f.store.getState().filter.draft.name, '非營業日且排除指定日期 2');
});

test('F4 an old saved scenario without naming origins is sent back without inventing them', () => {
  const f = namingFixture();
  const legacy = { name: '審計員原本的名稱', rationale: '審計員原本的動機',
    groups: [{ join: 'AND', matchScope: 'row', rules: [{ type: 'prescreen', join: 'AND', prescreenKey: 'weekendPosting' }] }] };
  assert.equal(f.api.toWireScenario(legacy).editorOrigins, undefined);
});

test('F7 a replaced draft starts without the changed-conditions hint', () => {
  const f = namingFixture(), draft = f.toggle('G');
  f.store.setFilterPreview({ count: 1 });
  f.store.setFilterDraft(draft);
  assert.equal(f.store.getState().filter.previewExpired, true, 'the same draft changed after a preview');
  f.store.setFilterDraft({ name: '', rationale: '', groups: [] });
  assert.equal(f.store.getState().filter.previewExpired, false);
});

// 使用者 2026-10-07 裁定：會讓篩選結果過期的上游修改清除後面步驟的篩選情境，前面步驟只用一句話提醒。
test('an upstream change that clears filter scenarios resets step 5 and logs one short message', () => {
  const f = namingFixture(), draft = f.toggle('G');
  f.store.setSavedScenarios([savedG()]);
  f.store.applyFilterCommit({ scenarios: [savedG()], resultRef: { revision: 'r1' } }, null, false);
  f.store.setFilterPreview({ count: 2 });
  assert.equal(draft.groups.length, 1);
  f.store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true } });
  const state = f.store.getState();
  assert.equal(state.filter.savedScenarios.length, 0);
  assert.equal(state.filter.draft.groups.length, 0);
  assert.equal(state.filter.draft.name, '');
  assert.equal(state.filter.preview, null);
  assert.equal(state.filterResultRef, null);
  // 使用者 2026-10-13 裁定訊息分三級：要回第五步重做，屬於需要注意。第一次失敗收據 20261013-095454658-c6425ce2422c45fb8480a7ac4834522e。
  assert.deepEqual(plain(state.messages.filter(m => m.text === UPSTREAM_RESET_MESSAGE).map(m => m.level)), ['warn']);
  f.store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true } });
  assert.equal(f.store.getState().messages.filter(m => m.text === UPSTREAM_RESET_MESSAGE).length, 1,
    'nothing left to clear, so no second message');
});

// TB now follows GL by the 2026-10-07 ruling; initial policy failure: 20261007-082053783-19b83716af8c44478af22300b8a3d95c.
test('a trial-balance change clears the saved filter scenarios and draft', () => {
  const f = namingFixture(); f.toggle('G');
  f.store.setSavedScenarios([savedG()]);
  f.store.applyMutationEffects({ invalidatedResults: { validation: true, prescreen: true, filter: true, filterScenarios: true } });
  assert.equal(f.store.getState().filter.savedScenarios.length, 0);
  assert.equal(f.store.getState().filter.draft.groups.length, 0);
});

test('earlier steps show one plain sentence only while filter scenarios exist', () => {
  const f = namingFixture();
  assert.equal(f.window.JetUi.downstreamResetNoticeHtml(f.store.getState()), '');
  f.store.setSavedScenarios([savedG()]);
  assert.match(f.window.JetUi.downstreamResetNoticeHtml(f.store.getState()), />更改已設定好的資料，會清除後面步驟的設定。</);
  for (const file of ['steps/import-step.js', 'steps/mapping-step.js', 'steps/validate-step.js', 'steps/create-step.js'])
    assert.match(fs.readFileSync(path.join(root, file), 'utf8'), /Ui\.downstreamResetNoticeHtml\(/, file);
});

// 使用者 2026-10-15 在待決事項勾選頁裁定（U6）：只有未儲存的第五步草稿、沒有已存情境時，上游修改一樣會清掉草稿，所以也要先提醒。
// 判斷和清除後的訊息用同一條：有已存情境，或草稿已有條件組。修改前只看已存情境。
test('earlier steps also show the sentence when only an unsaved filter draft exists', () => {
  const f = namingFixture();
  f.store.setFilterDraft({ name: '', rationale: '', groups: [{ join: 'and', rules: [{ type: 'drCrOnly', drCr: 'debit' }] }] });
  assert.equal(f.store.getState().filter.savedScenarios.length, 0);
  assert.match(f.window.JetUi.downstreamResetNoticeHtml(f.store.getState()), />更改已設定好的資料，會清除後面步驟的設定。</);
});

// 使用者 2026-10-15 在待決事項勾選頁裁定（U8）：GL 或 TB 重新匯入、重新確認配對讓資料驗證失效時，
// 第四步「用 Excel 配對」不再顯示舊資料的科目配對檔狀態；下次驗證後會重新寫出這一句。修改前這一句一直留到換案件。
test('the account mapping file status is cleared when an upstream change clears the validation', () => {
  const f = usageJourneyFixture('validate');
  f.store.setValidationOutput('template', { status: 'done', runId: 'v1',
    message: '已建立 合成AccountMapping.xlsx（2 個科目）。填妥科目分類並存檔後，即可匯入。' });
  f.store.applyMutationEffects({ invalidatedResults: { validation: false, prescreen: true, filter: true, filterScenarios: true } });
  assert.ok(f.store.getState().validationOutput.template, 'a change that keeps the validation keeps the status');
  f.store.applyMutationEffects({ invalidatedResults: { validation: true, prescreen: true, filter: true, filterScenarios: true, accountMapping: true } });
  assert.equal(f.store.getState().validationOutput.template, null);
});

// 2026-10-07 remap: expected behavior comes from docs/jet-frontend-description.md and the action contract.
test('remap S1-01 creation renders browser-required audit dates', () => {
  const f = projectMetadataFixture(), page = f.render(validCreateValues());
  for (const field of ['periodStart', 'periodEnd']) {
    const tag = page.html().match(new RegExp('<input[^>]*name="' + field + '"[^>]*>'));
    assert.ok(tag, field + ' rendered'); assert.match(tag[0], /\brequired(?:\s|>|=)/);
  }
});

for (const kind of ['gl', 'tb']) test('remap S3-02 ' + kind + ' confirmation shows every source warning without blocking and clears scenarios', async () => {
  const f = usageJourneyFixture('mapping'); prepareUsageMapping(f, kind); f.draw();
  const warnings = ['來源欄「Code」整欄空白，請檢查配對。', '來源欄「Name」整欄空白，請檢查配對。'];
  // 核准日與人工代碼設定只在 GL 的確認配對回應裡（MappingHandlers）；2026-10-15 前後端契合時改成依種類給。
  const glOptions = kind === 'gl' ? { approvalDateMode: 'unmapped', manualAutoPolicy: { manualValues: ['1'], automaticValues: ['0'] } } : {};
  f.click('[data-action="commit-' + kind + '"]');
  f.request(kind === 'gl' ? 'mappingCommitGl' : 'mappingCommitTb').resolve({ projectedRowCount: 2, ...glOptions, warnings, ...usageEffects(true) }); await settle();
  assert.equal(f.state.mapping[kind].committed.projectedRowCount, 2);
  assert.equal(f.state.filter.savedScenarios.length, 0); assert.equal(f.state.filter.draft.groups.length, 0); assert.equal(f.state.filter.draft.name, '');
  for (const warning of warnings) assert.equal(f.state.messages.filter(m => m.level === 'warn' && m.text.includes(warning)).length, 1);
  assert.equal(f.state.messages.filter(m => m.text === UPSTREAM_RESET_MESSAGE).length, 1);
  assert.ok(f.state.messages.some(m => m.level === 'info' && /配對/.test(m.text)));
  f.draw(); f.click('[data-action="remap-' + kind + '"]'); f.draw(); f.click('[data-action="commit-' + kind + '"]');
  f.request(kind === 'gl' ? 'mappingCommitGl' : 'mappingCommitTb').resolve({ projectedRowCount: 2, ...glOptions, ...usageEffects(true) }); await settle();
  assert.equal(f.state.messages.filter(m => m.level === 'warn' && warnings.some(w => m.text.includes(w))).length, 2);
  assert.equal(f.state.messages.filter(m => m.text === UPSTREAM_RESET_MESSAGE).length, 1);
});

test('remap S2-02 TB card and both import modes explain the same-period boundary positively', async () => {
  const f = usageJourneyFixture('import'); f.draw(); f.click('[data-task-toggle="tb"]'); f.draw();
  const tbCard = f.$('[data-task-toggle="tb"]').closest('.import-task').querySelector('.import-task__panel');
  assert.match(tbCard.textContent, /同一查核期間/); assert.match(tbCard.textContent, /不會自動.*期初與期末.*期間變動/);
  for (const mode of ['replace', 'append']) {
    if (mode === 'append') { f.store.setImportResult('tb', { batchId: 'same-period', rowCount: 2, columns: ['Code', 'Amount'] }); f.draw(); }
    f.click('[data-action="wizard-' + mode + '-tb"]');
    f.request('hostSelectFiles').resolve({ files: [{ filePath: 'synthetic.csv', fileName: 'synthetic.csv' }] }); await settle();
    f.request('importInspectFile').resolve({ fileType: 'csv', columns: ['Code', 'Amount'], encoding: 'utf-8', delimiter: ',' }); await settle(); f.draw();
    assert.match(f.$('.wizard-pane__hint').textContent, /同一查核期間/); assert.match(f.$('.wizard-pane__hint').textContent, /不會自動配成期初與期末/);
    // 使用者 2026-10-19：說明開頭跟著按鈕改成「加入：」。
    if (mode === 'append') assert.match(f.$('.wizard-pane__hint').textContent, /^加入：新檔的資料會附加到現有 2 列/);
    f.click('[data-action="wizard-cancel"]'); f.draw();
  }
  f.click('[data-task-toggle="gl"]'); f.draw(); assert.match(f.dom.content.textContent, /可合併欄位相同/);
});

test('remap X-05 mutation handlers do not duplicate the reset message or log one for an empty step five', async () => {
  const f = usageJourneyFixture('mapping'); f.store.setSavedScenarios([]); prepareUsageMapping(f, 'tb');
  for (let pass = 0; pass < 3; pass++) {
    if (pass === 1) f.store.setSavedScenarios([savedG()]);
    f.draw(); if (f.$('[data-action="remap-tb"]')) { f.click('[data-action="remap-tb"]'); f.draw(); }
    f.click('[data-action="commit-tb"]'); f.request('mappingCommitTb').resolve({ projectedRowCount: 2, ...usageEffects(true) }); await settle();
    assert.equal(f.state.filter.savedScenarios.length, 0); assert.equal(f.state.filter.draft.groups.length, 0);
    assert.equal(f.state.messages.filter(m => m.text === UPSTREAM_RESET_MESSAGE).length, pass === 0 ? 0 : 1);
  }
});

test('remap X-06 preparation date form consumes the authoritative clearing response', async () => {
  const f = projectMetadataFixture(syntheticMetadata()); f.store.setSavedScenarios([savedG()]);
  let page = f.render(); page.click('edit-project-metadata'); page = f.render();
  page.input('lastPeriodStart', '2024-02-20'); page.submit(); assert.equal(f.updates.length, 1);
  assert.equal(f.updates[0].payload.lastPeriodStart, '2024-02-20');
  f.updates[0].resolve({ project: { ...syntheticMetadata(), lastPeriodStart: '2024-02-20' }, ...usageEffects(true) }); await settle();
  const state = f.store.getState(); assert.equal(state.filter.savedScenarios.length, 0); assert.equal(state.filter.draft.groups.length, 0);
  assert.equal(state.messages.filter(m => m.text === UPSTREAM_RESET_MESSAGE).length, 1);
  assert.doesNotMatch(f.render().html(), /更改已設定好的資料，會清除後面步驟的設定/);
});

test('remap S4-02 template buttons distinguish preserving existing work from explicit rebuild', async () => {
  const f = usageJourneyFixture('validate'); f.store.setLastRun('validate', batch8ValidationData('v1')); prepareUsageMapping(f, 'gl'); f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed' });
  f.draw(); f.click('[data-action="toggle-account-excel"]'); f.draw();
  // Both controls belong to the same Excel account-mapping area; the rebuild warning is rendered next to them.
  assert.match(f.dom.content.textContent, /清空.*分類/);
  for (const [action, onlyIfMissing, disposition] of [['download-account-mapping-template', false, 'created'], ['ensure-account-mapping-template', true, 'kept']]) {
    f.click('[data-action="' + action + '"]'); const req = f.request('exportAccountMappingTemplate'); assert.equal(req.payload.onlyIfMissing, onlyIfMissing);
    req.resolve({ filePath: 'synthetic/AccountMapping.xlsx', disposition }); await settle(); f.draw();
    assert.match(f.$('[data-validation-output="template"]').textContent, disposition === 'kept' ? /保留/ : /建立|產生/);
  }
});

for (const catalog of [null, []]) test('remap S4-06 successful reports survive an unavailable or incomplete catalog: ' + JSON.stringify(catalog), async () => {
  const f = batch8Fixture(), data = batch8ValidationData(), old = { artifactId: 'old-prescreen', kind: 'prescreenReport', fileName: 'old.xlsx', stale: true };
  f.store.setLastRun('validate', data); f.store.setReportArtifacts([old]);
  f.controls(f.v.validationArtifactsHtml(f.store.getState(), data)).click('[data-action="export-validation-artifacts"]');
  const artifacts = batch8Artifacts(); f.requests[0].resolve({ artifacts, reportArtifacts: catalog, ...(catalog === null ? { reportArtifactWarning: '合成清單無法更新' } : {}) }); await settle();
  assert.deepEqual(plain(f.store.getState().reportArtifacts.map(a => a.artifactId).sort()), [old, ...artifacts].map(a => a.artifactId).sort());
  const html = f.v.validationArtifactsHtml(f.store.getState(), data); assert.match(html, /role="status"/); assert.match(html, catalog === null ? /合成清單無法更新/ : /重新開啟案件就會更新，不必重新產生/);
  assert.doesNotMatch(f.v.validationOutputStatusHtml('reports'), /失敗|重試/);
});

async function completeRemapValidation(f, runId) {
  f.requests.filter(r => r.action === 'validateRun').at(-1).resolve(batch8ValidationData(runId)); await settle();
  f.requests.filter(r => r.action === 'exportValidationArtifacts').at(-1).resolve({ artifacts: batch8Artifacts(runId), reportArtifacts: batch8Artifacts(runId) }); await settle();
  f.requests.filter(r => r.action === 'exportAccountMappingTemplate').at(-1).resolve({ disposition: 'kept', filePath: 'synthetic/AccountMapping.xlsx' }); await settle();
}

test('remap S4-12 validation and prescreen buttons clear only their own stale flag', async () => {
  const f = batch8Fixture(); f.store.setLastRun('validate', batch8ValidationData('old')); f.store.setLastRun('prescreen', remapPrescreen('p-old'));
  f.store.setStaleState({ validation: true, prescreen: true, filter: false }); f.controls().click('[data-action="run-validate"]');
  await completeRemapValidation(f, 'new');
  assert.deepEqual(plain(f.store.getState().staleState), { validation: false, prescreen: true, filter: false });
  assert.match(f.v.prescreenCardHtml(f.store.getState().lastRuns.prescreen, f.store.getState().lastRuns.validate, true, f.store.getState(), { isEligible: true }), /重新|過期/);
  f.controls().click('[data-action="run-prescreen"]'); f.requests.filter(r => r.action === 'prescreenRun').at(-1).resolve({ resultRef: { runId: 'p-new' } }); await settle();
  assert.deepEqual(plain(f.store.getState().staleState), { validation: false, prescreen: false, filter: false });
});

for (const kind of ['validate', 'prescreen']) test('remap X-16 ' + kind + ' failure retains the previous result and can be retried from its button', async () => {
  const f = batch8Fixture(), old = kind === 'validate' ? batch8ValidationData('old') : { resultRef: { runId: 'p-old' } }, artifacts = batch8Artifacts('old');
  if (kind === 'prescreen') f.store.setLastRun('validate', batch8ValidationData('v1'));
  f.store.setLastRun(kind, old); f.store.setReportArtifacts(artifacts); f.controls().click('[data-action="run-' + kind + '"]');
  f.requests[0].reject(new Error('synthetic transient failure')); await settle();
  assert.equal(f.store.getState().busy, false); assert.equal(f.store.getState().lastRuns[kind], old); assert.deepEqual(plain(f.store.getState().reportArtifacts), artifacts);
  f.controls().click('[data-action="run-' + kind + '"]');
  if (kind === 'validate') await completeRemapValidation(f, 'retry');
  else { f.requests.at(-1).resolve({ resultRef: { runId: 'p-retry' } }); await settle(); }
  assert.equal(f.store.getState().busy, false); assert.equal(f.store.getState().lastRuns[kind].resultRef.runId, kind === 'validate' ? 'retry' : 'p-retry');
});

test('remap X-16 workpaper failure retains history and retries the same selected scenarios', async () => {
  const f = usageJourneyFixture(), old = remapPaper('prior', true); f.store.setReportArtifacts([old]); f.draw(); f.change('[data-bind="include-prescreen-report"]', false); f.click('[data-action="export-workpaper"]');
  const first = f.request('exportWorkpaperStream'); first.reject(new Error('synthetic output failure')); await settle(); f.draw();
  assert.equal(f.state.busy, false); assert.deepEqual(plain(f.state.reportArtifacts), [old]); assert.equal(f.$('[data-action="export-workpaper"]').disabled, false);
  f.click('[data-action="export-workpaper"]'); const retry = f.request('exportWorkpaperStream'); assert.deepEqual(plain(retry.payload), plain(first.payload));
  const published = remapPaper('retry'); retry.resolve({ artifact: published, reportArtifacts: [old, published], sheetStats: [] }); await settle();
  assert.equal(f.state.busy, false); assert.deepEqual(plain(f.state.reportArtifacts.map(a => a.artifactId)), ['prior', 'retry']);
});

test('remap S6-09 workpaper handler replaces history with the pruned complete catalog', async () => {
  const f = usageJourneyFixture(), old = [remapPaper('kept', true), remapPaper('missing-a', true), remapPaper('missing-b', true)];
  f.store.setReportArtifacts(old); f.draw(); f.change('[data-bind="include-prescreen-report"]', false); f.click('[data-action="export-workpaper"]');
  const published = remapPaper('new'); f.request('exportWorkpaperStream').resolve({ artifact: published, reportArtifacts: [old[0], published], sheetStats: [] }); await settle(); f.draw();
  assert.deepEqual(plain(f.state.reportArtifacts.map(a => a.artifactId)), ['kept', 'new']);
  assert.doesNotMatch(f.dom.content.textContent, /missing-a|missing-b/);
  assert.equal(f.dom.content.querySelectorAll('[data-open-artifact]').length, 2);
});

function remapAppFixture() {
  const f = usageJourneyFixture(); f.window.addEventListener = () => {}; f.window.JetApi.isReady = () => true;
  f.dom.content.innerHTML = '<section data-bind="project-picker"></section><nav data-bind="step-nav"></nav><button data-action="app-exit"></button><button data-action="app-back-picker"></button><button data-action="cancel-operation"></button>';
  const source = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
  vm.runInContext(source.replace("  if (document.readyState === 'loading') {",
    '  window.remapApp = { renderPicker, overviewHtml, renderStepNav, renderContent, renderMessagesPanel, buildMessage, formatExportProgress };\n  if (document.readyState === \'loading\') {'), f.context);
  function slice(start, end) { const a = source.indexOf(start), b = source.indexOf(end, a + start.length); assert.ok(a >= 0 && b > a); return source.slice(a, b); }
  vm.runInContext('var Ui = window.JetUi;\n' + slice('    var exitBtn = ', '    var openProjectFolderBtn = '), f.context);
  const started = []; f.window.JetApi.onRequestStarted = fn => started.push(fn);
  vm.runInContext('var global = window, Store = window.JetStore, busyActiveAction = null;\n' +
    slice('  var CANCELLABLE_ACTIONS = {', '  // 增量渲染狀態') +
    slice("    if (global.JetApi && typeof global.JetApi.onRequestStarted === 'function') {", '    // 長作業進度只更新') +
    slice("    var cancelOperationBtn = document.querySelector('[data-action=\"cancel-operation\"]');", "    document.querySelectorAll('[data-action=\"messages-toggle\"]')"), f.context);
  return { ...f, app: f.window.remapApp, started(action, requestId) { started.forEach(fn => fn({ action, requestId })); } };
}

test('remap S1-15 failed project loading exports the same error correlation from the picker', async () => {
  const f = remapAppFixture(); f.store.resetWorkflow(); f.store.setView('picker');
  const opening = f.window.JetUi.openProject('failed-synthetic');
  f.request('projectLoad').reject({ code: 'file_read_error', message: '合成載入失敗', correlationId: 'picker-load-x' }); await opening;
  f.app.renderPicker(f.state);
  assert.equal(f.state.project, null); assert.equal(f.state.pickerFeedbackContext.projectId, 'failed-synthetic'); assert.equal(f.state.pickerFeedbackContext.correlationId, 'picker-load-x');
  // 使用者 2026-10-13 選「從錯誤框拿掉，改成一般工具」：錯誤框不再有「複製錯誤」，「輸出支援日誌」移到操作列、不帶 projectId，
  // 匯出全部紀錄。原本斷言錯誤框有兩個按鈕、payload 帶這次的 projectId 與 correlationId
  // （第一次失敗：FrontendMapping 收據 20261013-141147763-5dc8acde89a2431d9568ff02be8bd79e）。這次的 correlation 仍留給 Debug 組建的 DEV 按鈕。
  assert.match(f.dom.content.textContent, /合成載入失敗/); assert.ok(f.$('[data-action="picker-copy-error"]') === null, 'no copy button');
  assert.ok(f.$('.picker-panel__actions [data-action="picker-support-export"]') !== null, 'support log button in the action row');
  f.click('[data-action="picker-support-export"]');
  assert.deepEqual(plain(f.request('supportLogExport').payload), {});
  f.request('supportLogExport').resolve({ filePath: 'synthetic/support.ndjson' }); await settle(); f.app.renderPicker(f.state);
  assert.match(f.dom.content.textContent, /support.ndjson/); assert.match(f.dom.content.textContent, /合成載入失敗/); assert.equal(f.state.project, null);
});

for (const action of ['app-back-picker', 'app-exit']) {
  test('remap S1-16 X-17 ' + action + ' waits for saving and lock release before departure', async () => {
    const f = remapAppFixture(), project = f.state.project; f.store.setView('workflow'); f.click('[data-action="' + action + '"]'); await settle();
    assert.deepEqual(f.requests.map(r => r.action), ['projectSaveProgress']); assert.equal(f.state.view, 'workflow'); assert.equal(f.state.busy, true);
    f.request('projectSaveProgress').resolve({}); await settle(); assert.equal(f.request('projectReleaseLock').action, 'projectReleaseLock');
    assert.equal(f.state.project, project); assert.equal(f.state.view, 'workflow'); assert.equal(f.requests.some(r => r.action === 'hostExitApp'), false);
    f.request('projectReleaseLock').resolve({}); await settle();
    if (action === 'app-exit') { assert.ok(f.request('hostExitApp')); f.request('hostExitApp').resolve({}); }
    else { assert.equal(f.state.view, 'picker'); assert.equal(f.state.project, null); f.request('projectListLocal').resolve({ projects: [] }); }
    await settle();
  });
  test('remap X-17 ' + action + ' blocks departure while busy and preserves the project when lock release fails', async () => {
    const f = remapAppFixture(), project = f.state.project; f.store.setView('workflow'); f.store.setBusy(true, 'synthetic in flight');
    f.click('[data-action="' + action + '"]'); assert.equal(f.requests.length, 0); assert.equal(f.state.project, project); assert.equal(f.state.view, 'workflow');
    assert.ok(f.state.messages.some(m => m.level === 'warn'));
    f.store.setBusy(false); f.click('[data-action="' + action + '"]'); await settle(); f.request('projectSaveProgress').resolve({}); await settle();
    f.request('projectReleaseLock').reject(new Error('synthetic lock failure')); await settle();
    assert.equal(f.state.project, project); assert.equal(f.state.view, 'workflow'); assert.equal(f.state.busy, false); assert.equal(f.requests.some(r => r.action === 'hostExitApp'), false);
    // 使用者 2026-10-13 裁定訊息分三級：離開案件失敗是錯誤。第一次失敗收據 20261013-095454658-c6425ce2422c45fb8480a7ac4834522e。
    assert.ok(f.state.messages.some(m => m.level === 'error' && m.text.includes('synthetic lock failure')));
  });
}

for (const status of ['recalculated', 'current']) test('remap S5-35 project loading reports a completed rule upgrade only for ' + status, async () => {
  const f = usageJourneyFixture(), opening = f.window.JetUi.openProject('recalculated-synthetic');
  f.request('projectLoad').resolve({ project: { projectId: 'recalculated-synthetic' }, filterScenarioCheck: { status, recalculatedCount: 2, problems: [] } }); await settle();
  f.request('logRecent').resolve({ messages: [] }); await opening;
  // 使用者 2026-10-14 要求黃色提示不過度解釋，句子依同日確認的清單縮短（第一次失敗：FrontendMapping 收據 20261014-085644389-9bd9b20c579e4d95926d6ad0c4881504）；句子不再有「新規則」與「過期」，改用開頭與「需要重新產生」辨認。
  const messages = f.state.messages.filter(m => /篩選規則已更新/.test(m.text)); assert.equal(messages.length, status === 'recalculated' ? 1 : 0);
  // 使用者 2026-10-13 裁定訊息分三級：報告已過期、需要時要重新產生，屬於需要注意。第一次失敗收據 20261013-095454658-c6425ce2422c45fb8480a7ac4834522e。
  if (messages.length) { assert.equal(messages[0].level, 'warn'); assert.match(messages[0].text, /2.*情境/); assert.match(messages[0].text, /條件篩選報告.*底稿.*需要重新產生/); }
});

test('remap overview uses the same completed progress as the sidebar and does not repeat a fallback case title', () => {
  const f = remapAppFixture(); f.store.setProject({ projectId: 'Synthetic case', caseName: 'Synthetic case', entityName: '', periodStart: '2025-01-01', periodEnd: '2025-11-30' });
  for (const kind of ['gl', 'tb']) { f.store.setImportResult(kind, { batchId: kind, rowCount: 2, columns: ['Account'] }); f.store.setMappingCommitted(kind, { mapping: {}, mode: kind === 'gl' ? 'signed' : 'direct' }); }
  f.store.setLastRun('validate', { resultRef: { runId: 'v1' }, completenessTest: { eligibility: { isEligible: true } } });
  f.store.setSavedScenarios([savedG()]); f.store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' });
  for (const stale of [false, true]) {
    f.store.setReportArtifacts([remapPaper('current', stale)]); const overview = f.dom.element(); overview.innerHTML = f.app.overviewHtml(f.state); f.app.renderStepNav(f.state, f.store.STEPS);
    const completed = stale ? 5 : 6; assert.match(f.$('.toc-progress').textContent, new RegExp('進度 ' + completed + '/6'));
    assert.equal(overview.querySelector('[role="progressbar"]').getAttribute('aria-valuenow'), String(completed));
    assert.equal(overview.querySelector('.overview__title').textContent, 'Synthetic case');
    assert.match(overview.querySelector('.overview__baseline').textContent, /查核期間/); assert.doesNotMatch(overview.querySelector('.overview__baseline').textContent, /Synthetic case/);
  }
});

test('overview opened from the header button takes keyboard focus on its close button, closes on Escape and returns focus', () => {
  const f = overviewDialogFixture(), opener = f.$('[data-action="overview-open"]'), modal = f.$('.overview-modal');
  for (const round of ['first open', 'reopen']) {
    opener.focus(); opener.fire('click');
    assert.equal(f.state.overviewOpen, true, round); assert.equal(modal.hidden, false, round);
    const active = f.dom.document.activeElement;
    assert.ok(modal.contains(active), round + ': focus moves into the overview');
    assert.equal(active.tagName, 'BUTTON', round + ': focus lands on a button, not the backdrop');
    assert.ok(active.classList.contains('overview__close'), round + ': focus lands on the card close button');
    // 2026-10-13 修改既有測試的原因：使用者選定總覽改用共用的焦點圈限，Escape 改由 document 捕獲階段的監聽處理，
    // 直接對 modal 送 keydown 不再經過它而依預期失敗，第一次失敗收據 20261013-072254030-1d559fa4970d468eb505a5e111b2ce01。
    // 斷言不變，只改成照瀏覽器的順序送鍵。
    f.key('Escape');
    assert.equal(f.state.overviewOpen, false, round); assert.equal(modal.hidden, true, round);
    assert.equal(f.dom.document.activeElement, opener, round + ': focus returns to the overview button');
  }
});

// 總覽開著時背景資料更新會整張重畫卡片：審計員正在用的頁籤或分析區標題要留住焦點，Escape 才接得住。
test('overview keeps keyboard focus on the same control when its data redraws while open', () => {
  const f = overviewDialogFixture(); const opener = f.$('[data-action="overview-open"]');
  f.store.setLastRun('validate', { resultRef: { runId: 'v1' }, stats: { glRowCount: 4, voucherCount: 2, totalDebit: 100, totalCredit: 100 },
    completenessTest: { eligibility: { isEligible: true } } });
  opener.focus(); opener.fire('click');
  const oldTab = f.$('#overview-stage-tab-2'); oldTab.focus(); oldTab.fire('click');
  f.store.touch();
  const tab = f.$('#overview-stage-tab-2');
  assert.ok(tab !== oldTab, 'the card was rebuilt');
  assert.ok(f.dom.document.activeElement === tab, 'focus stays on the same stage tab');
  assert.equal(tab.getAttribute('aria-selected'), 'true', 'the chosen stage stays selected');
  const summaryOf = pattern => f.dom.content.querySelectorAll('.overview-modal__card summary').find(s => pattern.test(s.textContent));
  const oldSummary = summaryOf(/預篩選結果分布/); assert.ok(oldSummary, 'the distribution section is shown'); oldSummary.focus(); f.store.touch();
  assert.ok(summaryOf(/預篩選結果分布/) !== oldSummary, 'the section heading was rebuilt');
  assert.ok(f.dom.document.activeElement === summaryOf(/預篩選結果分布/), 'focus stays on the same section heading');
  f.store.setLastRun('validate', { resultRef: { runId: 'v2' }, completenessTest: { eligibility: { isEligible: true } } });
  assert.ok(summaryOf(/預篩選結果分布/) === undefined, 'the section disappears without statistics');
  assert.ok(f.dom.document.activeElement === f.$('.overview-modal__card button[data-action="overview-close"]'), 'falls back to the close button');
  f.dom.document.activeElement = f.dom.document.body; f.store.touch();
  assert.ok(f.dom.document.activeElement === f.dom.document.body, 'a redraw does not grab focus that was outside the overview');
});

// 焦點跑到總覽外面時 Tab 拉回、Escape 照樣關；作業進行中鍵盤留給作業遮罩；點背景照樣關閉，背景本身不是控制項。
test('overview Escape and Tab use the shared document trap while open and yield while busy', () => {
  const f = overviewDialogFixture(), opener = f.$('[data-action="overview-open"]'), before = f.keyListeners();
  opener.focus(); opener.fire('click');
  assert.equal(f.keyListeners(), before + 1, 'listens on the document while open');
  const close = f.$('.overview-modal__card button[data-action="overview-close"]');
  f.dom.document.activeElement = f.dom.document.body;
  const tab = f.key('Tab'); assert.equal(tab.defaultPrevented, true, 'Tab is handled by the overview'); assert.ok(f.dom.document.activeElement === close, 'Tab pulls escaped focus back');
  f.store.setBusy(true, '儲存並結束'); f.dom.document.activeElement = f.dom.document.body;
  const busyTab = f.key('Tab'); assert.equal(busyTab.defaultPrevented, false, 'Tab is left to the operation overlay');
  assert.ok(f.dom.document.activeElement === f.dom.document.body, 'focus is not moved into the inert overview');
  f.key('Escape'); assert.equal(f.state.overviewOpen, true, 'Escape does not close the overview during an operation');
  f.store.setBusy(false);
  // 原生對話框（第五步的日期多選用 showModal 開在最上層）開著時，Escape 與 Tab 交給它。
  const nativeDialog = f.dom.document.body.appendChild(f.dom.element('dialog', { open: '' }));
  const dialogTab = f.key('Tab', { target: nativeDialog }); assert.equal(dialogTab.defaultPrevented, false, 'Tab is left to the native dialog on top');
  f.key('Escape', { target: nativeDialog }); assert.equal(f.state.overviewOpen, true, 'Escape is left to the native dialog on top');
  nativeDialog.removeAttribute('open');
  f.key('Escape'); assert.equal(f.state.overviewOpen, false, 'Escape closes even when focus left the overview');
  assert.ok(f.dom.document.activeElement === opener, 'focus returns to the overview button');
  assert.equal(f.keyListeners(), before, 'stops listening once closed');
  const backdrop = f.$('.overview-modal__backdrop');
  assert.equal(backdrop.hasAttribute('data-action'), false, 'the backdrop is not a control');
  opener.focus(); opener.fire('click'); backdrop.fire('click');
  assert.equal(f.state.overviewOpen, false, 'a backdrop click still closes the overview');
  assert.ok(f.dom.document.activeElement === opener, 'backdrop click returns focus'); assert.equal(f.keyListeners(), before, 'backdrop close stops listening');
});

// 第四步有沒儲存的分類時按視窗的關閉鈕，待確認對話框疊在總覽上面：第一次 Escape 只關它，第二次才關總覽。
test('overview yields Escape to the unsaved-category dialog that the window close button opens above it', async () => {
  const f = overviewDialogFixture(), opener = f.$('[data-action="overview-open"]');
  const host = f.dom.content.appendChild(f.dom.element('section'));
  const draw = () => f.window.JetUi.renderStep('validate', host, f.store.getState());
  f.state.lastRuns.validate = null;
  f.store.setMappingCommitted('gl', { mapping: {}, mode: 'signed' });
  f.store.setAccountMappingState({ batchId: 'categories', hasAnyCategory: true });
  draw();
  f.request('queryAccountMappingPage').resolve({ rows: [{ accountCode: 'A1', accountName: 'Synthetic account', categoryId: 'builtin.cash' }], nextCursor: null });
  await settle(); draw();
  f.change('[data-account-select-page]', true); chooseAccountCategory(f, 'builtin.revenue');
  draw(); f.click('[data-account-apply]'); draw();
  assert.equal(f.window.JetUi.accountMappingEditor.pendingCount(), 1, 'one category change is unsaved');
  opener.focus(); opener.fire('click');
  const close = f.$('.overview-modal__card button[data-action="overview-close"]'); assert.ok(f.dom.document.activeElement === close, 'the overview has focus');
  f.window.JetUi.exitApp();
  const dialog = f.$('[data-bind="account-pending-modal"]'); assert.ok(dialog, 'the unsaved-category dialog opens');
  assert.ok(dialog.contains(f.dom.document.activeElement), 'focus moves into the dialog on top');
  f.key('Escape'); await settle();
  assert.ok(f.$('[data-bind="account-pending-modal"]') === null, 'Escape closes the dialog on top');
  assert.equal(f.state.overviewOpen, true, 'the overview below stays open');
  assert.ok(f.dom.document.activeElement === close, 'focus returns to the overview');
  f.key('Escape'); assert.equal(f.state.overviewOpen, false, 'the second Escape closes the overview');
  assert.ok(f.dom.document.activeElement === opener, 'focus returns to the overview button');
});

test('remap S6-06 cancellation through the actual export button retains a completed prescreen report and stops the workpaper', async () => {
  const f = remapAppFixture(), cancel = f.$('[data-action="cancel-operation"]');
  f.store.setLastRun('prescreen', { resultRef: { runId: 'p1' } }); f.draw();
  f.change('[data-bind="include-prescreen-report"]', true); f.click('[data-action="export-workpaper"]'); await settle();
  assert.ok(f.request('exportPrescreenReport')); f.started('export.prescreenReport', 'export-stage-one'); cancel.fire('click');
  f.request('operationCancel').resolve({ requested: false }); await settle();
  const report = { artifactId: 'finished-prescreen', kind: 'prescreenReport', fileName: 'prescreen.xlsx', stale: false, sourceRef: { prescreenRunId: 'p1' } };
  f.request('exportPrescreenReport').resolve({ artifact: report, reportArtifacts: [report] }); await settle();
  assert.equal(f.requests.some(r => r.action === 'exportWorkpaperStream'), false); assert.ok(f.state.reportArtifacts.some(a => a.artifactId === report.artifactId));
  const messages = f.state.messages.map(m => m.text).join('\n'); assert.match(messages, /後續|尚未開始/); assert.match(messages, /已完成.*保留/); assert.doesNotMatch(messages, /回滾|無需取消/);
});

test('remap S5-18 applying an example replaces all existing conditions and focuses the first missing value', () => {
  const f = filterOperationFixture(), deferred = []; f.window.JetFocus.defer = fn => deferred.push(fn);
  f.click('[data-kct-letter="G"]'); f.click('[data-action="add-set"]'); f.click('[data-kct-letter="H"]'); assert.equal(f.draft().groups.length, 2); const oldRationale = f.draft().rationale;
  // 使用者 2026-10-11 裁定：還沒儲存的條件被範例換掉前先詢問。這裡選「不儲存，直接套用」，其餘核對不變。
  // 第一次失敗：20261011-032026332-6d4b2b2cebce4d8eaf343a490c8b8c46。
  f.dom.document.body = f.dom.element('body'); f.dom.document.removeEventListener = () => {};
  f.click('[data-action="apply-template"][data-template-key="nonBusinessDayExcludingDates"]');
  const dialog = f.dom.document.body.querySelector('[data-bind="filter-pending-modal"]');
  dialog.fire('click', { target: dialog.querySelector('.modal__actions [data-pending-choice="continue"]') });
  assert.equal(f.draft().groups.length, 1); assert.deepEqual(plain(f.draft().groups[0].rules.map(r => r.type)), ['group', 'fieldValue']);
  assert.doesNotMatch(JSON.stringify(f.draft().groups), /blankDescription|roundAmount/); assert.equal(f.draft().name, '非營業日且排除指定日期');
  assert.ok(f.draft().rationale); assert.notEqual(f.draft().rationale, oldRationale); assert.equal(f.$('[data-save-panel]').hidden, false);
  assert.deepEqual(plain(f.draft().groups[0].rules[1].values), []);
  const target = deferred.at(-1)(); assert.ok(target); assert.ok(f.$('.rule-row[data-gi="0"][data-ri="1"]').contains(target));
  // 第九節 E 改用原生日期框；首次失敗：20261014-124620770-eb3019eaf3104b769e0a7e040903dd65。
  assert.ok(target.matches('[data-date-input], [data-value-key="values"]'));
});

test('remap S5-22 an unavailable department example preserves a populated draft and explains the missing field locally', () => {
  // 使用者 2026-10-08 要求重新設計自訂篩選條件：範例移到「從範例開始」；沒有文字型攸關資料元素欄位時直接停用並在該項下方寫原因。
  const f = filterOperationFixture(); f.click('[data-kct-letter="G"]'); const before = f.draft(), json = JSON.stringify(before);
  f.click('[data-action="toggle-examples"]');
  const example = f.$('[data-template-key="manualDepartment"]');
  assert.equal(example.disabled, true); assert.equal(example.getAttribute('data-action'), null);
  example.fire('click');
  assert.equal(f.draft(), before); assert.equal(JSON.stringify(f.draft()), json); assert.equal(f.commits.length, 0);
  assert.equal(f.$('#filter-example-note-manualDepartment').textContent, '需要先在欄位配對加入部門等文字欄位');
});

for (const action of ['copy-scenario', 'edit-scenario']) test('remap S5-27 ten saved scenarios ' + (action === 'copy-scenario' ? 'block a copy locally' : 'still allow updating one existing scenario'), async () => {
  const f = filterOperationFixture({ saved: Array.from({ length: 10 }, (_, i) => savedG('S' + (i + 1))) });
  f.click('[data-action="' + action + '"][data-index="0"]'); 
  if (action === 'edit-scenario') f.type('[data-bind="scenario-name"]', 'Renamed existing');
  f.click('[data-action="save-scenario"]'); await settle();
  if (action === 'copy-scenario') { assert.equal(f.commits.length, 0); assert.ok(f.notice().trim()); assert.ok(!f.messages().some(m => /儲存.*完成|已儲存/.test(m))); }
  // 2026-10-13 第五步一次只存或刪一個情境：畫面改送 filterScenarioSave／Remove／Recheck，只帶正在處理的那一個情境。原本看整批仍是 10 個、第 1 個改了名；
  // 現在看只送第 1 個（position 1、原名 S1）並帶新名稱。第一次失敗：FrontendMapping 收據 20261013-103553346-54ebed91711946dd8ff77c3dd1be7b23。
  else {
    assert.equal(f.commits.length, 1); assert.equal(f.commits[0].method, 'filterScenarioSave');
    assert.equal(f.commits[0].payload.position, 1); assert.equal(f.commits[0].payload.expectedName, 'S1');
    assert.equal(f.commits[0].payload.scenario.name, 'Renamed existing');
  }
});

async function remapSuccessfulPreview(f) {
  f.click('[data-kct-letter="G"]'); f.click('[data-action="preview-scenario"]');
  f.request('filterPreview').resolve({ scenario: { count: 123, voucherCount: 1, summaryRowCount: 123, populationScope: 'auditPeriod' } }); await settle();
  f.request('queryFilterVoucherPage').resolve({ queryRevision: 'q1', rows: [{ documentNumber: 'SYNTHETIC-JV', postDate: '2025-01-01', hitRowCount: 123, totalRowCount: 124, voucherTotal: 0 }], nextCursor: null }); await settle();
}

test('remap S5-23 changed conditions replace old preview counts with an update reminder', async () => {
  const f = remapFilterPreviewFixture(); await remapSuccessfulPreview(f); assert.match(f.dom.content.textContent, /123/);
  f.click('[data-kct-letter="H"]'); assert.match(f.dom.content.textContent, /條件已變更.*更新結果/); assert.doesNotMatch(f.$('[data-bind="preview-pane-body"]').textContent, /123/);
});

test('remap S5-23 a rejected preview marks the backend identified rule and reason', async () => {
  const f = remapFilterPreviewFixture(); f.click('[data-kct-letter="G"]'); f.click('[data-action="preview-scenario"]');
  f.request('filterPreview').reject({ code: 'invalid_scenario', message: '合成預覽拒絕', details: [{ group: 1, rule: 1, message: '合成錯誤。' }] }); await settle();
  const row = f.$('.rule-row[data-gi="0"][data-ri="0"]'); assert.equal(row.classList.contains('rule-row--invalid'), true); assert.equal(row.querySelector('[data-rule-error]').textContent, '合成錯誤。');
});

test('remap S5-23 expanding a voucher requests two hundred rows and labels hits separately from reference entries', async () => {
  const f = remapFilterPreviewFixture(); await remapSuccessfulPreview(f);
  const open = f.$('[data-voucher-open="0"]'); assert.ok(open); assert.equal(typeof open.onclick, 'function'); open.onclick();
  const req = f.request('queryFilterVoucherRowsPage'); assert.equal(req.payload.documentNumber, 'SYNTHETIC-JV'); assert.equal(req.payload.pageSize, 200);
  req.resolve({ rows: [{ lineItem: '1', isHit: true, amount: 10 }, { lineItem: '2', isHit: false, amount: -10 }], nextCursor: null }); await settle();
  const rows = f.dom.content.querySelectorAll('.voucher-details tbody tr'); assert.equal(rows.length, 2);
  assert.match(rows[0].textContent, /符合條件的分錄/); assert.match(rows[1].textContent, /參考分錄/);
});

test('remap S5-36 switching between filter and saved panes preserves the existing preview and draft without requests', async () => {
  const f = remapFilterPreviewFixture(); await remapSuccessfulPreview(f);
  const state = f.store.getState(), draft = state.filter.draft, preview = state.filter.preview, requests = f.requests.length;
  const shown = () => [f.$('[data-filter-pane="filter"]').hidden, f.$('[data-filter-pane="saved"]').hidden];
  assert.deepEqual(shown(), [false, true], 'only the filter pane is shown at first');
  f.click('[data-filter-pane-select="saved"]');
  assert.deepEqual(shown(), [true, false], 'the saved pane replaces the filter pane');
  f.click('[data-filter-pane-select="filter"]');
  assert.deepEqual(shown(), [false, true]);
  assert.equal(state.filter.draft, draft); assert.equal(state.filter.preview, preview); assert.equal(state.filter.previewExpired, false); assert.equal(f.requests.length, requests);
  assert.doesNotMatch(f.dom.content.textContent, /條件已變更/); assert.match(f.dom.content.textContent, /123/);
});

test('remap S5-24 long custom metadata respects the documented limits and a cleared name can be regenerated', () => {
  const f = filterOperationFixture(); f.click('[data-condition-source="custom"]');
  for (const [subject, value] of [['description', '合成摘要'.repeat(80)], ['accName', '合成科目名稱'.repeat(80)], ['docNum', '合成傳票號碼'.repeat(80)]]) {
    // 使用者 2026-10-08 要求重新設計自訂篩選條件：欄位改由欄位按鈕直接加入。
    f.click('[data-add-field="' + subject + '"]');
    const last = f.dom.content.querySelectorAll('[data-value-key="values"]').at(-1); assert.ok(last); last.value = value; last.fire('input');
  }
  const name = f.$('[data-bind="scenario-name"]').value, rationale = f.$('[data-bind="scenario-rationale"]').value;
  assert.ok(name.length <= 64); assert.match(name, /摘要/); assert.match(name, /科目名稱/); assert.match(name, /3/); assert.doesNotMatch(name, /傳票號碼/);
  assert.ok(rationale.length <= 180); assert.match(rationale, /摘要/); assert.match(rationale, /科目名稱/); assert.match(rationale, /傳票號碼/);
  f.type('[data-bind="scenario-name"]', ''); const input = f.dom.content.querySelectorAll('[data-value-key="values"]')[0]; input.value = '合成新摘要'; input.fire('input');
  assert.ok(f.$('[data-bind="scenario-name"]').value.trim()); assert.ok(f.$('[data-bind="scenario-name"]').value.length <= 64);
});

for (const key of ['dcDebitCode', 'dcCreditCode']) test('remap S3-13 ' + key + ' input updates required status and confirmation without replacing its node', async () => {
  const f = usageJourneyFixture('mapping'), mapping = prepareUsageMapping(f, 'gl');
  f.store.setImportResult('gl', { batchId: 'side', columns: [...Object.values(mapping), 'Side'], rowCount: 2 });
  f.store.replaceMappingDraft('gl', { ...mapping, dcField: 'Side', dcDebitCode: 'D', dcCreditCode: 'C' }); f.store.setMappingMode('gl', 'side');
  f.store.setMappingCommitted('gl', { mapping: { ...mapping, dcField: 'Side', dcDebitCode: 'D', dcCreditCode: 'C' }, mode: 'side', options: plain(f.state.mapping.gl.options) });
  f.draw(); f.click('[data-action="remap-gl"]'); f.draw();
  const input = f.$('[data-mapping-key="' + key + '"]'), commit = f.$('[data-action="commit-gl"]'); assert.ok(input); assert.equal(commit.disabled, false);
  // 第 6 則：刪除一側仍可確認，刪除最後一側才提示；編輯中的 DOM 與焦點不重建。
  input.value = ''; input.fire('input'); assert.equal(commit.disabled, false);
  const other = f.$('[data-mapping-key="' + (key === 'dcDebitCode' ? 'dcCreditCode' : 'dcDebitCode') + '"]');
  other.value = ''; other.fire('input'); assert.equal(commit.disabled, true);
  assert.match(f.$('[data-bind="mapping-option-problems"]').textContent, /至少填寫一個/);
  input.value = 'X'; input.fire('input'); assert.equal(commit.disabled, false);
  assert.equal(!!f.$('[data-bind="mapping-option-problems"]'), false);
  assert.equal(f.$('[data-mapping-key="' + key + '"]'), input); assert.equal(f.$('[data-action="commit-gl"]'), commit);
  assert.match(f.$('[data-bind="mapping-banner-gl"]').textContent, /修改|變更/); f.click('[data-action="commit-gl"]');
  assert.equal(f.requests.filter(r => r.action === 'mappingCommitGl').length, 1); assert.equal(f.request('mappingCommitGl').payload.mapping[key], 'X');
});

test('remap S3-13 RDE label input updates the missing-name notice and confirmation without replacing its node', () => {
  const f = usageJourneyFixture('mapping'), mapping = prepareUsageMapping(f, 'gl');
  f.store.setImportResult('gl', { batchId: 'rde', columns: [...Object.values(mapping), 'Dept'], rowCount: 2 });
  f.store.patchGlMappingOptions({ rdeFields: [{ fieldId: 'rde.keep', sourceColumn: 'Dept', label: '部門', valueType: 'text' }] }); f.draw();
  const input = f.$('[data-rde-label="Dept"]'), commit = f.$('[data-action="commit-gl"]'); assert.ok(input); assert.equal(commit.disabled, false);
  input.value = ''; input.fire('input'); assert.equal(commit.disabled, true); assert.match(f.$('[data-bind="mapping-option-problems"]').textContent, /名稱/);
  input.value = '新部門'; input.fire('input'); assert.equal(commit.disabled, false); assert.equal(f.$('[data-rde-label="Dept"]'), input);
  f.click('[data-action="commit-gl"]'); assert.equal(f.requests.filter(r => r.action === 'mappingCommitGl').length, 1); assert.equal(f.request('mappingCommitGl').payload.rdeFields[0].label, '新部門');
});

for (const catalog of [null, []]) test('remap S4-06 prescreen export preserves its new file when the catalog is incomplete: ' + JSON.stringify(catalog), async () => {
  const f = batch8Fixture(), run = { resultRef: { runId: 'p1' } }, old = batch8Artifacts()[0];
  f.store.setLastRun('prescreen', run); f.store.setReportArtifacts([old]);
  f.controls(f.v.prescreenArtifactHtml(f.store.getState(), run, f.ui.prescreenPositioningCopy(run))).click('[data-action="export-prescreen-report"]');
  const artifact = { artifactId: 'new-prescreen', kind: 'prescreenReport', fileName: 'prescreen.xlsx', stale: false, fileState: 'asPublished', sourceRef: { prescreenRunId: 'p1' } };
  f.requests[0].resolve({ artifact, reportArtifacts: catalog, ...(catalog === null ? { reportArtifactWarning: '合成清單無法更新' } : {}) }); await settle();
  assert.deepEqual(plain(f.store.getState().reportArtifacts.map(a => a.artifactId).sort()), [old.artifactId, artifact.artifactId].sort());
  const html = f.v.prescreenArtifactHtml(f.store.getState(), run, f.ui.prescreenPositioningCopy(run)); assert.match(html, /role="status"/); assert.match(html, catalog === null ? /合成清單無法更新/ : /重新開啟案件就會更新，不必重新產生/);
  assert.ok(f.store.getState().messages.some(m => m.level === 'info' && /已.*產生預篩選報告/.test(m.text)));
});

for (const action of ['app-back-picker', 'app-exit']) test('remap S1-16 X-17 ' + action + ' retains the case when release is rejected for an in-flight operation', async () => {
  const f = remapAppFixture(), project = f.state.project; f.store.setView('workflow'); f.click('[data-action="' + action + '"]'); await settle();
  f.request('projectSaveProgress').resolve({}); await settle(); f.request('projectReleaseLock').reject({ code: 'operation_in_progress' }); await settle();
  assert.equal(f.state.project, project); assert.equal(f.state.view, 'workflow'); assert.equal(f.state.busy, false); assert.equal(f.requests.some(r => r.action === 'hostExitApp'), false);
  // 使用者 2026-10-13 勾選頁確認 23：句子不再寫按鈕名稱，原本比對「等待…完成…重試」（第一次失敗：FrontendMapping 收據 20261013-141147763-5dc8acde89a2431d9568ff02be8bd79e）。
  assert.ok(f.state.messages.some(m => m.level === 'warn' && m.text === '作業還在進行，請等它完成後再試一次。'));
});

// 使用者 2026-10-08 要求重新設計自訂篩選條件：左欄只剩「依欄位設定」與「常用查核條件」，範例移到右側「從範例開始」，
// 每組多一個「⋯」選單。以下以正式第五步畫面與函式操作；名稱、規則與原因逐字寫死，不由被測程式算出。
const CUSTOM_CHECK_LABELS = {
  '日期與時點': ['總帳日期在週末', '總帳日期在國定假日', '總帳日期在非營業日', '傳票核准日在非營業日', '財報準備日起核准的分錄', '指定日期區間', '查核期末最後幾天'],
  '金額': ['大額分錄', '金額尾數連續多個 0'],
  '摘要': ['摘要含特定字詞', '空白摘要'],
  '科目與借貸': ['只看借方或貸方', '科目屬於指定分類', '借方與貸方分類組合', '借方與貸方科目編號組合', '未預期的借貸組合', '較少使用的科目'],
  '人員與分錄性質': ['人工分錄', '編製分錄較少的人員']
  // 「預篩選結果：沿用第四步預篩選規則」在使用者 2026-10-16 要求縮短套用預篩選條件的操作後，改成條件總覽的「預篩選」分頁，
  // 由 frontend-journey-filter-build.test.cjs 的分頁測試涵蓋；第一次失敗：常用查核條件少了「預篩選結果」一類。
};
const CUSTOM_EXAMPLE_NAMES = ['借現金、貸非現金', '貸現金、借非現金', '非營業日且排除指定日期', '期末最後 7 天的人工分錄', '空白摘要或空白核准人員',
  '特定人員的大額分錄', '週末人工分錄的特定摘要', '人工分錄的特定部門', '指定借貸科目組合的大額分錄', '財報準備期間的人工假日分錄'];
const DEFAULT_KEYWORDS = ['ADJ', 'REV', 'RECLASS', 'SUSPENSE', 'ERROR', 'WRONG', '調整', '迴轉', '沖銷', '重分類', '避險', '重編', '錯誤', '計畫外', '預算外', '帳外',
  '调整', '回转', '冲销', '重分类', '避险', '重编', '错误', '计画外', '预算外'];

// 只取預期值裡寫到的鍵比較；陣列逐項比較。
function pickShape(actual, expected) {
  if (Array.isArray(expected)) return expected.map((item, i) => pickShape((actual || [])[i], item));
  if (expected && typeof expected === 'object') return Object.fromEntries(Object.keys(expected).map(key => [key, pickShape((actual || {})[key], expected[key])]));
  return actual;
}

function customPaneFixture({ rdeFields = [], mapping = {} } = {}) {
  const f = filterOperationFixture();
  f.store.setMappingCommitted('gl', { mapping, mode: 'signed', options: { rdeFields } });
  f.click('[data-condition-source="custom"]');
  return f;
}

test('custom pane lists every core field by type, marks relevant data element fields and adds the default rule to the active group', () => {
  const f = customPaneFixture({ rdeFields: [{ fieldId: 'rde.d', sourceColumn: 'DocDay', label: '單據日', valueType: 'date' },
    { fieldId: 'rde.m', sourceColumn: 'Tax', label: '稅額', valueType: 'money' }, { fieldId: 'rde.t', sourceColumn: 'Dept', label: '部門', valueType: 'text' }] });
  const pane = f.$('[data-condition-pane="custom"]');
  assert.deepEqual(pane.querySelectorAll('.filter-custom-block__title').map(node => node.textContent), ['依欄位設定', '常用查核條件']);
  assert.doesNotMatch(pane.innerHTML, /<h3|data-custom-subject|data-legacy|data-template-key|舊表|範例/);
  const rows = pane.querySelectorAll('.filter-chip-row');
  assert.deepEqual(rows.map(row => row.querySelector('.filter-chip-row__label').textContent), ['日期', '數字', '文字']);
  // 核心欄位不依配對狀態篩選（此案件沒有配對任何核心欄位，仍全部列出）。
  assert.deepEqual(rows.map(row => row.querySelectorAll('[data-add-field]').map(button => button.textContent)), [
    ['總帳日期', '傳票核准日', '單據日'], ['金額', '稅額'],
    ['傳票摘要', '會計科目編號', '會計科目名稱', '傳票建立人員', '傳票核准人員', '傳票號碼', '分錄來源模組', '傳票文件項次', '部門']]);
  assert.equal(f.$('[data-add-field="rde.t"]').classList.contains('filter-chip--extra'), true);
  assert.equal(f.$('[data-add-field="postDate"]').classList.contains('filter-chip--extra'), false);
  // 同上，欄位按鈕的提示與報讀名稱只加註來源欄。使用者 2026-10-13 裁定改名（計畫「收尾四項：調查與裁定」）；同批改名的 C# 測試第一次失敗收據是 20261013-092449164-3847782f6c50488b86a0ac3856e606ca，那次在 C# 測試步驟停止，Node 前端測試尚未執行。
  assert.equal(f.$('[data-add-field="rde.t"]').getAttribute('title'), '部門（來源欄：Dept）');
  assert.equal(f.$('[data-add-field="rde.t"]').getAttribute('aria-label'), '加入條件：部門（來源欄：Dept）');

  const deferred = []; f.window.JetFocus.defer = fn => deferred.push(fn);
  f.click('[data-add-field="createBy"]'); f.click('[data-action="add-set"]'); f.click('[data-add-field="amount"]');
  const groups = f.draft().groups;
  assert.deepEqual(plain(groups.map(group => group.rules.map(rule => [rule.type, rule.field, rule.operator]))),
    [[['fieldValue', 'createBy', 'in']], [['fieldValue', 'amount', 'equals']]]);
  assert.equal(f.$('.rule-row[data-gi="1"][data-ri="0"]').classList.contains('rule-row--selected'), true, 'the new condition is selected');
  f.click('[data-check-category="amounts"]'); f.click('[data-add-check="largeAmount"]');
  assert.equal(deferred.at(-1)(), f.$('.rule-row[data-gi="1"][data-ri="1"]').querySelector('[data-value-key="value"]'), 'an empty threshold receives focus');
  f.click('[data-add-field="rde.t"]');
  assert.deepEqual(plain([groups[1].rules[2].fieldId, groups[1].rules[2].field ?? null, groups[1].rules[2].operator]), ['rde.t', null, 'contains']);
  assert.doesNotMatch(fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8'), /[✦★]/, 'no decorative star markers');
});

test('field buttons follow the active group join rules: same voucher always AND, row groups keep their combinator', () => {
  const f = customPaneFixture();
  f.click('[data-add-field="description"]'); f.click('[data-add-field="accName"]');
  assert.deepEqual(plain(f.draft().groups[0].rules.map(rule => rule.join)), ['AND', 'AND'], 'a new row group starts with AND');
  const draft = f.draft(); draft.groups[0].rules.forEach(rule => { rule.join = 'OR'; }); f.store.setFilterDraft(draft);
  f.click('[data-add-field="docNum"]');
  assert.equal(f.draft().groups[0].rules[2].join, 'OR');
  const same = f.draft(); same.groups[0].matchScope = 'sameVoucher'; same.groups[0].rules.forEach(rule => { rule.join = 'AND'; }); f.store.setFilterDraft(same);
  f.click('[data-add-field="jeSource"]');
  assert.equal(f.draft().groups[0].rules[3].join, 'AND');
});

test('common checks open one category at a time, start with the first, remember the choice and show only a name and an add button', () => {
  const f = customPaneFixture();
  const open = () => f.dom.content.querySelectorAll('[data-check-category]').filter(button => button.getAttribute('aria-expanded') === 'true').map(button => button.textContent);
  assert.deepEqual(open(), ['日期與時點']);
  assert.equal(f.$('#filter-checks-dates').hidden, false); assert.equal(f.$('#filter-checks-amounts').hidden, true);
  f.click('[data-check-category="amounts"]');
  assert.deepEqual(open(), ['金額']); assert.equal(f.$('#filter-checks-dates').hidden, true); assert.equal(f.$('#filter-checks-amounts').hidden, false);
  f.store.touch();
  assert.deepEqual(open(), ['金額'], 'redrawing keeps the open category');
  f.click('[data-check-category="amounts"]');
  assert.deepEqual(open(), [], 'the open category can be closed');
  const labels = Object.fromEntries(f.dom.content.querySelectorAll('[data-check-category]').map(button =>
    [button.textContent, f.$('#' + button.getAttribute('aria-controls')).querySelectorAll('.filter-check__label').map(node => node.textContent)]));
  assert.deepEqual(labels, CUSTOM_CHECK_LABELS);
  // 2026-10-16 新增的「預篩選」分頁沿用同一種列樣式，這裡只檢查常用查核條件裡的列；第一次失敗：TypeError，
  // 預篩選分頁的列沒有 [data-add-check]。分頁的列由 frontend-journey-filter-build.test.cjs 檢查。
  for (const item of f.dom.content.querySelectorAll('.filter-check').filter(node => node.closest('.filter-check-group'))) {
    assert.equal(item.querySelector('[data-add-check]').textContent, '加入');
    const extra = item.children.filter(child => !child.matches('.filter-check__label') && !child.matches('[data-add-check]') && !child.matches('.filter-check__note'));
    assert.deepEqual(extra, [], 'no extra explanation beside a check');
  }
});

test('every common check adds its fixed rule shape to the active group', () => {
  const f = batch7Fixture();
  f.store.getState().project.lastPeriodStart = '2025-12-01';
  f.store.setAccountMappingState({ hasAnyCategory: true, hasRevenue: true, hasCounterpart: true });
  f.store.setMappingCommitted('gl', { mapping: { createBy: 'Maker', approveBy: 'Checker' }, mode: 'signed', options: {} });
  const expected = {
    postDateWeekend: { type: 'fieldValue', field: 'postDate', operator: 'isWeekend' },
    postDateHoliday: { type: 'fieldValue', field: 'postDate', operator: 'isHoliday' },
    postDateNonBusinessDay: { type: 'fieldValue', field: 'postDate', operator: 'isNonBusinessDay' },
    docDateNonBusinessDay: { type: 'fieldValue', field: 'docDate', operator: 'isNonBusinessDay' },
    postPeriodApproval: { type: 'prescreen', prescreenKey: 'postPeriodApproval' },
    postDateRange: { type: 'fieldValue', field: 'postDate', operator: 'between', from: '', to: '' },
    periodEndDays: { type: 'fieldValue', field: 'postDate', operator: 'between', from: '2025-12-25', to: '2025-12-31' },
    largeAmount: { type: 'fieldValue', field: 'amount', operator: 'greaterThan', value: '', amountBasis: 'absolute' },
    // 第九節 G2 改用欄位條件；首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
    trailingZeros: { type: 'group', rules: [{ type: 'fieldValue', field: 'amount', operator: 'endsWithDigits', value: '000000', amountBasis: 'absolute' },
      { type: 'fieldValue', field: 'amount', operator: 'greaterThanOrEqual', value: '1', amountBasis: 'absolute' }] },
    descriptionKeywords: { type: 'fieldValue', field: 'description', operator: 'contains', values: DEFAULT_KEYWORDS },
    blankDescription: { type: 'fieldValue', field: 'description', operator: 'isBlank' },
    debitOrCreditOnly: { type: 'drCrOnly', drCr: 'debit' },
    // 第九節 G1 保留借方預設，方向改放明示子條件。首次失敗：20261014-132501058-ef503fecc9824f3b9e6f009a7b154327。
    accountCategory: { type: 'group', rules: [{ type: 'drCrOnly', drCr: 'debit' }, { type: 'accountSide', categoryMode: 'is', categorySelection: 'subtree' }] },
    categoryPair: { type: 'specialAccountCategoryPair', pairMode: 'drAndCr', categorySelection: 'subtree' },
    accountCodePair: { type: 'group', rules: [
      { type: 'voucher', quantifier: 'any', rules: [{ type: 'drCrOnly', drCr: 'debit' }, { type: 'fieldValue', field: 'accNum', operator: 'in', values: [] }] },
      { type: 'voucher', quantifier: 'any', rules: [{ type: 'drCrOnly', drCr: 'credit' }, { type: 'fieldValue', field: 'accNum', operator: 'in', values: [] }] }] },
    unexpectedAccountPair: { type: 'prescreen', prescreenKey: 'unexpectedAccountPair' },
    rareAccounts: { type: 'entityFrequency', field: 'accNum', countUnit: 'entries', countOperator: 'lessThanOrEqual', countFrom: '11' },
    manualEntries: { type: 'manualAuto', isManual: 'true' },
    rarePreparers: { type: 'entityFrequency', field: 'createBy', countUnit: 'entries', countOperator: 'lessThanOrEqual', countFrom: '11' }
    // prescreenResult 已由「預篩選」分頁取代（使用者 2026-10-16 要求）；第一次失敗：常用查核條件的鍵少了 prescreenResult。
  };
  const keys = f.ui.FILTER_COMMON_CHECK_GROUPS.flatMap(group => group.checks.map(check => check.key));
  assert.deepEqual(plain(keys), Object.keys(expected));
  for (const [key, shape] of Object.entries(expected)) {
    f.store.setFilterDraft({ name: '', rationale: '', groups: [] });
    f.api.addCommonCheck(key);
    const rules = f.store.getState().filter.draft.groups[0].rules;
    assert.equal(rules.length, 1, key);
    assert.deepEqual(plain(pickShape(rules[0], shape)), shape, key);
    assert.ok(f.api.toWireDraft(f.store.getState().filter.draft).groups[0].rules[0].type, key);
  }
  // 原本這裡檢查沒有期末財報準備日時，「沿用第四步預篩選規則」改帶下一個可用的規則。這個入口已由「預篩選」分頁取代，
  // 分頁直接停用「財報準備日起核准」並寫出原因，同樣不會帶入後端一定會擋的條件；改由 frontend-journey-filter-build.test.cjs
  // 的停用測試涵蓋（使用者 2026-10-16 要求）。
});

test('common checks are disabled only when the backend would reject them and say why under the item', () => {
  const f = batch7Fixture();
  const state = () => {
    const html = f.api.customPickerHtml(), result = {};
    for (const match of html.matchAll(/data-add-check="([^"]+)"([^>]*)>/g)) result[match[1]] = /\bdisabled\b/.test(match[2]);
    return { html, disabled: Object.keys(result).filter(key => result[key]) };
  };
  const note = (html, key) => (new RegExp('id="filter-check-note-' + key + '">([^<]*)<').exec(html) || [])[1];
  let page = state();
  assert.deepEqual(page.disabled, ['postPeriodApproval', 'accountCategory', 'categoryPair', 'unexpectedAccountPair', 'rarePreparers']);
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-15、R3-17（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  assert.equal(note(page.html, 'postPeriodApproval'), '尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫');
  assert.equal(note(page.html, 'accountCategory'), '科目配對中尚無任何科目設定分類，請先在第四步設定');
  assert.equal(note(page.html, 'categoryPair'), '科目配對中尚無任何科目設定分類，請先在第四步設定');
  assert.equal(note(page.html, 'unexpectedAccountPair'), '科目配對中尚無任何科目設定分類，請先在第四步設定');
  assert.equal(note(page.html, 'rarePreparers'), '「傳票建立人員」尚未選擇來源欄');
  assert.equal(note(page.html, 'postDateHoliday'), undefined, 'a missing holiday list does not disable the check');
  const before = JSON.stringify(f.store.getState().filter.draft);
  f.api.addCommonCheck('rarePreparers');
  assert.equal(JSON.stringify(f.store.getState().filter.draft), before, 'a disabled check never changes the draft');
  // 使用者 2026-10-14 第三輪勾選頁同意刪除「案件沒有查核截止日」這個停用分支：建立案件必填查核截止日
  // （create-step.js），畫面走不到這個狀態。原本「沒有截止日就停用」的核對改成鎖住刪除後的行為：
  // 不再因截止日停用，也不再寫這句（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4，
  // 本測試先在上面 R3-17 的句子失敗）。
  f.store.getState().project.periodEnd = null;
  page = state();
  assert.ok(!page.disabled.includes('periodEndDays'));
  assert.equal(note(page.html, 'periodEndDays'), undefined);
  assert.doesNotMatch(page.html, /案件沒有查核截止日/);
  f.store.getState().project.periodEnd = '2025-12-31'; f.store.getState().project.lastPeriodStart = '2025-12-01';
  f.store.setAccountMappingState({ hasAnyCategory: true, hasRevenue: true, hasCounterpart: true });
  f.store.setMappingCommitted('gl', { mapping: { createBy: 'Maker' }, mode: 'signed', options: {} });
  assert.deepEqual(state().disabled, []);
});

// 第九節 G4 將組合按鈕移出選單；首次失敗：20261014-131029712-8f2df24aa4ae432f9ea153f221584af6。
// 保留加入指定組、預設量詞與不影響其他組的斷言。
test('the group heading adds a bracket or a voucher condition to its own group even when there is only one group', () => {
  const f = filterOperationFixture();
  assert.equal(f.$('.filter-group-menu'), null);
  assert.deepEqual(f.dom.content.querySelectorAll('[data-add-compound]').map(button => button.textContent), ['加入子條件組', '加入整張傳票條件']);
  f.click('[data-add-compound="group"]');
  assert.deepEqual(plain(f.draft().groups.map(group => group.rules.map(rule => rule.type))), [['group']]);
  f.click('[data-action="add-set"]');
  assert.equal(f.dom.content.querySelectorAll('.filter-group-heading [data-add-compound="group"]').length, 2);
  f.click('[data-add-compound="voucher"][data-gi="0"]');
  const groups = f.draft().groups;
  assert.deepEqual(plain(groups.map(group => group.rules.map(rule => rule.type))), [['group', 'voucher'], []]);
  // 第九節 G1 的傳票條件不再攜帶 side。首次失敗：20261014-132501058-ef503fecc9824f3b9e6f009a7b154327。
  assert.deepEqual(plain([groups[0].rules[1].side, groups[0].rules[1].quantifier]), [null, 'any']);
});

test('examples start hidden, list ten plain names with a one-line notice and apply under the example name', () => {
  const f = filterOperationFixture(), list = () => f.$('[data-example-list]');
  assert.equal(list().hidden, true);
  f.click('[data-action="toggle-examples"]');
  assert.equal(list().hidden, false); assert.equal(f.$('[data-action="toggle-examples"]').getAttribute('aria-expanded'), 'true');
  assert.equal(f.$('.filter-example-list__hint').textContent, '套用後會替換目前的條件，已儲存的情境不受影響。');
  const buttons = list().querySelectorAll('[data-template-key]');
  assert.deepEqual(buttons.map(button => button.textContent), CUSTOM_EXAMPLE_NAMES);
  for (const button of buttons) {
    assert.ok(button.getAttribute('title').length > 0, 'the audit purpose is in the title');
    assert.doesNotMatch(button.getAttribute('title'), /工作簿範例|請依案件修改理由/);
  }
  // 原本以目錄代號、範例編號命名的情境，現在直接用範例名稱。
  assert.doesNotMatch(list().textContent, /舊表|A 到 U|範例 \d|[A-U](?:\+[A-U])+/);
  f.click('[data-action="apply-template"][data-template-key="weekendManualDescription"]');
  assert.equal(f.draft().name, '週末人工分錄的特定摘要');
  assert.equal(list().hidden, true, 'applying closes the example list');
  assert.deepEqual(plain(f.draft().groups[0].rules.map(rule => [rule.type, rule.field, rule.operator, rule.values])), [
    ['manualAuto', null, null, null], ['fieldValue', 'postDate', 'isWeekend', []], ['fieldValue', 'description', 'contains', ['迴轉']]]);
  f.click('[data-add-field="docNum"]');
  assert.notEqual(f.draft().name, '週末人工分錄的特定摘要', 'adding a condition returns to the automatic name');
});

test('examples disable for missing approver mapping, preparation date or period end and leave the draft untouched', () => {
  const f = filterOperationFixture();
  f.store.setProject({ projectId: 'example-requirements', periodEnd: '' });
  f.click('[data-action="toggle-examples"]');
  const note = key => f.$('#filter-example-note-' + key)?.textContent;
  assert.equal(f.$('[data-template-key="blankDescriptionOrApprover"]').disabled, true);
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-17（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  assert.equal(note('blankDescriptionOrApprover'), '「傳票核准人員」尚未選擇來源欄');
  assert.equal(note('preparationManualHoliday'), '尚未填寫期末財報準備日，請先在第一步按「修改案件資料」填寫');
  // 使用者 2026-10-14 第三輪勾選頁同意刪除「案件沒有查核截止日」分支：建立案件必填查核截止日，範例不再因此停用。
  assert.equal(note('periodEndManual'), undefined);
  assert.equal(f.$('[data-template-key="periodEndManual"]').disabled, false);
  assert.equal(note('weekendManualDescription'), undefined);
  f.store.setMappingCommitted('gl', { mapping: { approveBy: 'Checker' }, mode: 'signed', options: {} });
  assert.equal(f.$('[data-template-key="blankDescriptionOrApprover"]').disabled, false);
});

for (const count of [1, 2]) test('the department example with ' + count + ' text field(s) ' + (count === 1 ? 'fills it automatically' : 'asks in place and cancelling or Esc keeps the draft'), () => {
  const rdeFields = [{ fieldId: 'rde.dept', sourceColumn: 'Dept', label: '部門', valueType: 'text' },
    { fieldId: 'rde.unit', sourceColumn: 'Unit', label: '部門', valueType: 'text' }].slice(0, count);
  const f = customPaneFixture({ rdeFields });
  f.dom.document.body = f.dom.element('body'); f.dom.document.removeEventListener = () => {};
  f.click('[data-add-field="description"]');
  const before = f.draft(), json = JSON.stringify(before);
  f.click('[data-action="toggle-examples"]');
  f.click('[data-action="apply-template"][data-template-key="manualDepartment"]');
  if (count === 2) {
    const pick = f.$('[data-example-pick="manualDepartment"]');
    assert.ok(pick, 'field choices appear in place');
    assert.deepEqual(pick.querySelectorAll('[data-example-field]').map(button => button.textContent), ['部門（Dept）', '部門（Unit）']);
    assert.equal(f.draft(), before); assert.equal(JSON.stringify(f.draft()), json);
    f.click('[data-action="cancel-example-pick"]');
    assert.equal(f.$('[data-example-pick]'), null); assert.equal(JSON.stringify(f.draft()), json);
    f.click('[data-action="apply-template"][data-template-key="manualDepartment"]');
    f.$('[data-example-list]').fire('keydown', { key: 'Escape' });
    assert.equal(f.$('[data-example-pick]'), null); assert.equal(f.$('[data-example-list]').hidden, false, 'the first Esc only cancels the field choice');
    assert.equal(JSON.stringify(f.draft()), json);
    f.click('[data-action="apply-template"][data-template-key="manualDepartment"]');
    f.click('[data-example-field="rde.unit"]');
  }
  // 使用者 2026-10-11 裁定：草稿已有條件，範例真的要換掉它時先詢問。這裡選「不儲存，直接套用」，其餘核對不變。
  // 第一次失敗：20261011-032026332-6d4b2b2cebce4d8eaf343a490c8b8c46。
  const dialog = f.dom.document.body.querySelector('[data-bind="filter-pending-modal"]');
  dialog.fire('click', { target: dialog.querySelector('.modal__actions [data-pending-choice="continue"]') });
  const rules = f.draft().groups[0].rules;
  assert.equal(f.draft().name, '人工分錄的特定部門');
  assert.deepEqual(plain(rules.map(rule => [rule.type, rule.fieldId, rule.operator, rule.values])), [
    ['manualAuto', null, null, null], ['fieldValue', count === 1 ? 'rde.dept' : 'rde.unit', 'contains', ['總經理室']]]);
  assert.equal(rules[1].field, undefined);
});

test('saved scenarios with retired count rule types still display their label, value and readback', () => {
  const f = batch7Fixture();
  for (const [type, label] of [['customPreparerEntryCount', 'GL 中編製人員分錄筆數 ≤'], ['customAccountEntryCount', 'GL 中科目分錄筆數 ≤']]) {
    const rule = { type, maxEntries: '11', join: 'AND' };
    const subject = f.api.ruleSubjectHtml(rule, 'pattern');
    assert.ok(subject.includes('<option value="' + type + '" selected>' + label + '</option>'), type);
    assert.doesNotMatch(subject, /無法使用/);
    const row = f.api.ruleRowHtml(rule, { rules: [rule] }, 0, 0);
    assert.ok(row.includes('<strong>' + label + '</strong>'), type);
    assert.match(row, /data-rule-bind="maxEntries"[^>]*value="11"/);
    assert.equal(f.text(f.api.readBackHtml({ groups: [{ rules: [rule] }] })).replace(/^篩選條件：/, ''), label + ' 11');
  }
  // 新條件不再提供這兩種型別。
  assert.doesNotMatch(f.api.ruleSubjectHtml({ type: 'customTrailingZeros', digits: '6' }, 'pattern'), /customPreparerEntryCount|customAccountEntryCount/);
  assert.doesNotMatch(f.api.customPickerHtml(), /customPreparerEntryCount|customAccountEntryCount/);
});

// 舊目錄（只留在測試夾具）代號與新選單的對照。keys 列出比較的鍵；null 表示整條規則結構相同（只比較舊目錄寫到的鍵）。
// equivalence 表示有意改用別的規則，等價案例寫在 LegacyFormWorkflowTests，以既有固定答案的集合運算驗證。
const LEGACY_MENU_MAP = [
  { code: 'A', kind: 'check', key: 'postPeriodApproval', keys: null },
  { code: 'B', kind: 'check', key: 'descriptionKeywords', equivalence: 'B_customKeywords' },
  { code: 'C', kind: 'check', key: 'unexpectedAccountPair', keys: null },
  { code: 'D', kind: 'check', key: 'trailingZeros', equivalence: 'D_customTrailingZeros' },
  { code: 'E', kind: 'check', key: 'manualEntries', keys: null },
  { code: 'F', kind: 'check', key: 'blankDescription', equivalence: 'F_fieldBlank' },
  { code: 'G', kind: 'check', key: 'postDateWeekend', keys: null },
  { code: 'H', kind: 'check', key: 'postDateHoliday', keys: null },
  { code: 'I', kind: 'check', key: 'postDateNonBusinessDay', equivalence: 'GH_nonBusinessDay' },
  { code: 'J', kind: 'field', key: 'docDate', keys: ['type', 'field'] },
  { code: 'K', kind: 'field', key: 'docDate', keys: ['type', 'field'] },
  { code: 'L', kind: 'check', key: 'docDateNonBusinessDay', equivalence: 'JK_nonBusinessDay' },
  { code: 'M', kind: 'check', key: 'debitOrCreditOnly', keys: null },
  { code: 'N', kind: 'check', key: 'debitOrCreditOnly', keys: ['type'] },
  { code: 'O', kind: 'field', key: 'description', keys: ['type', 'field', 'operator'] },
  { code: 'P', kind: 'check', key: 'accountCodePair', equivalence: 'P_explicitSides' },
  { code: 'P', kind: 'check', key: 'categoryPair', keys: ['type', 'pairMode', 'categorySelection'], source: 'categoryRules' },
  { code: 'Q', kind: 'field', key: 'amount', keys: ['type', 'field'] },
  { code: 'R', kind: 'check', key: 'postDateRange', keys: null },
  { code: 'S', kind: 'check', key: 'largeAmount', keys: ['type', 'field', 'operator', 'amountBasis'] },
  { code: 'T', kind: 'check', key: 'rareAccounts', keys: ['type', 'field', 'countUnit'] },
  { code: 'U', kind: 'check', key: 'rarePreparers', keys: ['type', 'field'] },
  { code: 'example1', kind: 'template', key: 'weekendManualDescription', keys: null },
  { code: 'example2', kind: 'template', key: 'manualDepartment', keys: null },
  { code: 'example3', kind: 'template', key: 'accountPairLargeAmount', equivalence: 'example3_explicitSides' },
  { code: 'example5', kind: 'template', key: 'preparationManualHoliday', keys: null }
];

test('common checks and examples keep the rule structure of the matching retired catalogue entries', () => {
  const f = batch7Fixture(), ui = f.ui;
  const catalogue = JSON.parse(fs.readFileSync(path.join(__dirname, '../../src/JET/tests/JET.Tests/Fixtures/legacy-form-2024-1210.json'), 'utf8'));
  const workflowTests = fs.readFileSync(path.join(__dirname, '../../src/JET/tests/JET.Tests/Application/LegacyFormWorkflowTests.cs'), 'utf8');
  const checks = ui.FILTER_COMMON_CHECK_GROUPS.flatMap(group => group.checks);
  function legacyRules(entry) {
    const item = catalogue.conditions.find(c => c.letter === entry.code) || catalogue.examples.find(e => e.key === entry.code);
    assert.ok(item, entry.code); return item[entry.source || 'rules'];
  }
  function menuRules(entry) {
    if (entry.kind === 'check') return checks.find(c => c.key === entry.key).rules.map(spec => f.api.conditionFromSpec(spec));
    if (entry.kind === 'template') return ui.FILTER_SCENARIO_TEMPLATES.find(t => t.key === entry.key).groups[0].rules;
    f.api.addFieldCondition(entry.key); const draft = f.store.getState().filter.draft; return [draft.groups.at(-1).rules.at(-1)];
  }
  const strip = rule => JSON.parse(JSON.stringify(rule, (key, value) => key === 'join' ? undefined : value));
  for (const entry of LEGACY_MENU_MAP) {
    const legacy = legacyRules(entry), current = menuRules(entry).map(strip);
    if (entry.equivalence) { assert.ok(workflowTests.includes('"' + entry.equivalence + '"'), entry.code + ' needs an equivalence case'); continue; }
    assert.equal(current.length, legacy.length, entry.code);
    legacy.forEach((rule, i) => {
      const expected = entry.keys ? Object.fromEntries(entry.keys.map(key => [key, rule[key]])) : rule;
      assert.deepEqual(plain(pickShape(current[i], expected)), expected, entry.code + ' → ' + entry.key);
    });
  }
  // 範例 4 和「金額尾數連續多個 0」重複，不另列範例。
  assert.equal(ui.FILTER_SCENARIO_TEMPLATES.some(t => t.groups.some(g => g.rules.some(r => r.prescreenKey === 'trailingZeros'))), false);
});

test('common checks and examples use only rule types, operators and requirements the screen already knows', () => {
  const f = batch7Fixture(), ui = f.ui;
  const types = new Set(ui.FILTER_RULE_TYPES.map(type => type.value));
  const operators = new Set(['in', 'notIn', 'contains', 'between', 'greaterThan', 'greaterThanOrEqual', 'isBlank', 'isWeekend', 'isHoliday', 'isNonBusinessDay', 'isNotMakeupDay', 'dayOfMonthNotIn', 'endsWithDigits']);
  const prescreenKeys = new Set(ui.PRESCREEN_KEY_OPTIONS.map(option => option.value));
  function visit(rule, where) {
    assert.ok(types.has(rule.type), where + ' type ' + rule.type);
    if (rule.operator) assert.ok(operators.has(rule.operator), where + ' operator ' + rule.operator);
    if (rule.prescreenKey) assert.ok(prescreenKeys.has(rule.prescreenKey), where + ' prescreen ' + rule.prescreenKey);
    (rule.rules || []).forEach(child => visit(child, where));
  }
  const requirements = new Set(ui.FILTER_CONDITION_REQUIREMENTS);
  for (const check of ui.FILTER_COMMON_CHECK_GROUPS.flatMap(group => group.checks)) {
    if (check.requires) assert.ok(requirements.has(check.requires), check.key);
    check.rules.forEach(rule => visit(rule, check.key));
  }
  assert.equal(ui.FILTER_SCENARIO_TEMPLATES.length, 10);
  for (const template of ui.FILTER_SCENARIO_TEMPLATES) {
    if (template.requires) assert.ok(requirements.has(template.requires), template.key);
    if (template.pickField) assert.equal(template.pickField, 'text', template.key);
    template.groups.forEach(group => group.rules.forEach(rule => visit(rule, template.key)));
  }
});


test('oct9 examples belong to the condition catalog and still replace only the draft', () => {
  const f = filterOperationFixture({ saved: [savedG()] });
  assert.ok(f.$('.filter-entry [data-action="toggle-examples"]'));
  assert.equal(!!f.$('.scenario-builder [data-action="toggle-examples"]'), false);
  f.click('[data-action="toggle-examples"]');
  assert.equal(f.$('.filter-entry [data-example-list]').hidden, false);
  f.click('[data-action="apply-template"][data-template-key="weekendManualDescription"]');
  assert.equal(f.draft().name, '週末人工分錄的特定摘要');
  assert.deepEqual(plain(f.store.getState().filter.savedScenarios), [savedG()]);
  assert.equal(f.commits.length, 0);
});

// 第九節 G3 移除比對範圍；首次失敗：20261014-135637069-066eb7e965824f89bf2b8671156595f1。
test('oct9 group actions stay visible without a scope menu and allow either join', () => {
  const f = filterOperationFixture(); f.click('[data-kct-letter="G"]'); f.click('[data-kct-letter="H"]');
  assert.equal(!!f.$('.filter-relations'), false);
  assert.equal(f.$('.filter-group-menu'), null);
  f.choose('[data-set-combinator]', 'OR');
  assert.equal(f.draft().groups[0].matchScope, 'row');
  assert.deepEqual(plain(f.draft().groups[0].rules.map(rule => rule.join)), ['OR', 'OR']);
  assert.equal(f.$('[data-set-combinator]').disabled, false);
  assert.doesNotMatch(f.$('.filter-group-heading').textContent, /同一傳票/);
});

test('oct9 clear group leaves an empty reusable group, preserves other groups and never changes saved scenarios', () => {
  const f = filterOperationFixture({ saved: [savedG()] });
  f.click('[data-kct-letter="G"]'); f.click('[data-action="add-set"]'); f.click('[data-kct-letter="H"]');
  f.store.patchFilterDraftMeta({ name: '手寫情境', rationale: '手寫動機', __nameDirty: true, __rationaleDirty: true });
  const first = plain(f.draft().groups[0]);
  f.click('[data-action="clear-set"][data-gi="1"]');
  assert.equal(f.draft().groups.length, 2);
  assert.deepEqual(plain(f.draft().groups[0]), first);
  assert.equal(f.draft().groups[1].rules.length, 0);
  assert.equal(f.draft().groups[1].matchScope, 'row');
  assert.equal(f.draft().name, '手寫情境'); assert.equal(f.draft().rationale, '手寫動機');
  f.click('[data-kct-letter="H"]');
  // 第九節 G2 的 H 卡改用欄位尾數；首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  assert.equal(f.draft().groups[1].rules[0].type, 'fieldValue');
  assert.equal(f.draft().groups[1].rules[0].operator, 'endsWithDigits');
  assert.equal(f.draft().groups[1].rules[0].value, '000000');
  f.click('[data-action="remove-set"][data-gi="0"]');
  assert.equal(!!f.$('[data-action="remove-set"]'), false);
  f.click('[data-action="clear-set"][data-gi="0"]');
  assert.equal(f.draft().groups.length, 1); assert.equal(f.draft().groups[0].rules.length, 0);
  assert.equal(f.$('[data-action="clear-set"]').disabled, true);
  assert.deepEqual(plain(f.store.getState().filter.savedScenarios), [savedG()]);
  assert.equal(f.commits.length, 0);
});

test('oct9 account-pair notes are available on demand and nested conditions have a purpose-based title', () => {
  const f = filterOperationFixture(); f.click('[data-kct-letter="B"]');
  const help = f.$('.rule-row--account [data-rule-help]');
  assert.ok(help); assert.equal(help.hasAttribute('open'), false);
  assert.match(help.textContent, /結果列出同傳票/); assert.match(help.textContent, /未在配對檔/);
  assert.equal(!!f.$('.rule-row--account > .rule-row__controls > .rule-explanation'), false);
  f.click('[data-add-compound="group"][data-gi="0"]');
  assert.match(f.$('.rule-row--compound').textContent, /子條件組/);
  assert.match(f.$('.rule-row--compound').textContent, /同一筆分錄/);
  assert.equal(f.draft().groups[0].rules[1].type, 'group');
});

test('oct9 account paging across 201 accounts never saves implicitly and identifies the last page', async () => {
  const reads = [], writes = [];
  const window = { setTimeout, clearTimeout, JetApi: contractJetApi({
    queryAccountMappingPage: payload => new Promise(resolve => reads.push({ payload, resolve })),
    accountMappingSave: payload => { writes.push(plain(payload)); return Promise.resolve({ batchId: 'saved', rowCount: 201 }); }
  }) };
  const document = { createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'account-mapping-editor.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const store = window.JetStore, ui = window.JetUi;
  store.setProject({ projectId: 'paging-201' }); store.setTaxonomy({ revision: 1, categories: [{ categoryId: 'cash', label: 'Cash' }] });
  ui.run = (_, work) => work();
  function render() { return accountEditorControls(ui, store); }
  const all = Array.from({ length: 200 }, (_, i) => ({ accountCode: 'A' + String(i).padStart(3, '0'), accountName: 'Synthetic', categoryId: null }))
    .concat([{ accountCode: 'B001', accountName: 'Synthetic', categoryId: null }]);
  async function reply(index) {
    const request = reads[index]; assert.equal(request.payload.pageSize, 200);
    const offset = request.payload.cursor ? Number(request.payload.cursor) : 0;
    request.resolve({ rows: all.slice(offset, offset + 200), nextCursor: offset + 200 < all.length ? String(offset + 200) : null, totalCount: 201 });
    await settle();
  }
  render(); await reply(0);
  for (let index = 0; index < 2; index++) {
    const page = render(); page.control('[data-account-select-page]').checked = true; page.control('[data-account-select-page]').fire('change');
    if (index < 1) { assert.doesNotMatch(page.html, /data-account-next[^>]*disabled/); page.control('[data-account-next]').fire(); await reply(index + 1); }
  }
  assert.match(render().html, /已選 201 個科目/);
  let page = render(); page.control('[data-account-bulk-category]').value = 'cash';
    page.control('[data-account-bulk-category]').fire('change');
  render().control('[data-account-apply]').fire(); assert.equal(ui.accountMappingEditor.pendingCount(), 201);
  render().control('[data-account-prev]').fire(); await reply(2);
  assert.doesNotMatch(render().html, /data-account-next disabled/);
  render().control('[data-account-next]').fire(); await reply(3);
  assert.equal(writes.length, 0, 'paging has not sent a save request');
  assert.match(render().html, /data-page-position[^>]*>2\/2</, 'the pager identifies the last page');
  assert.match(render().html, /data-account-next[^>]*disabled/);
  render().control('[data-account-save]').fire(); await settle();
  assert.equal(writes.length, 1); assert.equal(writes[0].changes.length, 201);
  assert.deepEqual(writes[0].changes[0], { accountCode: 'A000', categoryId: 'cash' });
  assert.deepEqual(writes[0].changes[200], { accountCode: 'B001', categoryId: 'cash' });
});


// Run the existing GUI readback JavaScript against production-rendered mapping UI without opening a desktop window.
function oct9GuiMappingFixture() {
  const dom = batch9TreeDom();
  const window = { setTimeout: () => 0, clearTimeout() {}, JetApi: contractJetApi({
    queryDataPreview: async () => ({ columns: ['Approval', 'Manual'], rows: [['2025-01-02', '1']] }),
    mappingValueProfile: async () => ({ blankCount: 0, values: [], comparisonGroups: [], missingComparisonValues: [] })
  }) };
  const context = vm.createContext({ window, document: dom.document });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/mapping-step.js'), 'utf8').replace("  Ui.registerStep('mapping', render);", '  window.drawMapping = render;'), context);
  const store = window.JetStore;
  store.setProject({ projectId: 'gui-selector-synthetic' });
  store.setImportResult('gl', { batchId: 'gui-selectors', columns: ['Approval', 'Manual'], rowCount: 1 });
  store.setMappingDraft('gl', 'manual', 'Manual');
  const source = fs.readFileSync(path.join(__dirname, '../harness/gui-driver/GuiScenarios.cs'), 'utf8');
  const script = source.match(/MappingPolicyProbeScript = Probe\("""([\s\S]*?)"""\);/)[1]
    .replace('/* shared GUI helpers */', 'function visible(el) { return !!el && !el.hidden; }');
  function draw() {
    window.drawMapping(dom.content, store.getState());
    // The lightweight DOM parser does not implement native select.value initialization.
    for (const select of dom.content.querySelectorAll('select')) {
      const selected = select.querySelectorAll('option').find(option => option.hasAttribute('selected')) || select.querySelector('option');
      select.value = selected ? selected.getAttribute('value') : '';
    }
  }
  return { source, store, dom, draw, read: () => vm.runInContext(script, context) };
}

test('oct9 GUI approval readback inspects the actual classic and grid controls in all three states', () => {
  const f = oct9GuiMappingFixture();
  for (const view of ['classic', 'grid']) {
    f.store.setMappingUiMode(view);
    for (const [mode, source, choice] of [['mapped', 'Approval', 'column:Approval'], ['unmapped', '', 'mode:unmapped'], ['sameAsPostDate', '', 'mode:sameAsPostDate']]) {
      f.store.setMappingDraft('gl', 'docDate', source);
      f.store.patchGlMappingOptions({ approvalDateMode: mode }); f.draw();
      const probe = f.read();
      assert.equal(probe.approvalChoice, choice, view + ' ' + mode);
      assert.equal(probe.approvalFieldPresent, true, view + ' ' + mode);
      assert.equal(probe.mode, mode); assert.equal(probe.source, source);
    }
  }
});

test('oct9 GUI options-only edit targets a rendered editable code and preserves mapping fields', () => {
  const f = oct9GuiMappingFixture(); f.draw();
  const match = f.source.match(/RecordStage\("options-only-dirty-warning"\);[\s\S]*?ClickControlAsync\(cdp, process, GlSection \+ "((?:\\.|[^"\\])*)"/);
  assert.ok(match);
  const selector = JSON.parse('"' + match[1] + '"');
  const input = f.dom.content.querySelector(selector);
  assert.equal(!!input, true, selector + ' must exist');
  assert.equal(input.tagName, 'INPUT');
  const before = plain(f.store.getState().mapping.gl.draft);
  input.value = ''; input.fire('input');
  assert.deepEqual(plain(f.store.getState().mapping.gl.options.manualAutoPolicy.manualValues), []);
  assert.deepEqual(plain(f.store.getState().mapping.gl.options.manualAutoPolicy.automaticValues), ['0']);
  assert.deepEqual(plain(f.store.getState().mapping.gl.draft), before);
});

// 第九節 G3 更新腳本的入口，但本輪依使用者指示不執行 Gui。
// 舊選單斷言首次失敗：20261014-135637069-066eb7e965824f89bf2b8671156595f1。
test('oct9 GUI fixture uses visible compound actions instead of a retired scope selector', () => {
  const f = filterOperationFixture(); f.click('[data-kct-letter="G"]'); f.click('[data-kct-letter="H"]');
  const source = fs.readFileSync(path.join(__dirname, '../harness/gui-driver/GuiSideMonthWorkflow.cs'), 'utf8');
  assert.doesNotMatch(source, /data-group-bind=matchScope|data-value-key=drCr|data-action=open-save|data-action=new-scenario/);
  assert.match(source, /data-add-compound/);
  assert.equal(!!f.$('[data-add-compound="voucher"]'), true);
  f.click('[data-add-compound="voucher"]');
  assert.equal(f.draft().groups[0].matchScope, 'row');
  assert.equal(f.draft().groups[0].rules.length, 3);
  assert.equal(f.draft().groups[0].rules[2].type, 'voucher');
});


test('oct9 followup group buttons use short visible names and distinguish their targets for assistive reading', () => {
  const f = filterOperationFixture(); f.click('[data-kct-letter="G"]'); f.click('[data-action="add-set"]');
  for (const [action, text, label] of [['clear-set', '清除', '清除第 1 組的條件'], ['remove-set', '移除', '移除第 1 組']]) {
    const button = f.$('[data-action="' + action + '"][data-gi="0"]');
    assert.equal(button.textContent, text); assert.equal(button.getAttribute('aria-label'), label);
  }
  f.click('[data-action="clear-set"][data-gi="0"]');
  assert.equal(f.draft().groups.length, 2); assert.equal(f.draft().groups[0].rules.length, 0);
});

test('oct9 followup case summary retains separated case details without repeating the navigation position', () => {
  const dom = batch9TreeDom();
  const index = fs.readFileSync(path.join(root, '../index.html'), 'utf8');
  dom.content.innerHTML = index.match(/<section class="case-summary"[\s\S]*?<\/section>/)[0];
  const info = dom.content.querySelector('.case-summary__details');
  const position = dom.content.querySelector('.case-summary__position');
  assert.equal(!!info, true); assert.equal(!!position, false);
  for (const name of ['case-id', 'case-client', 'case-period']) assert.equal(!!info.querySelector('[data-bind="' + name + '"]'), true);
  assert.deepEqual(info.querySelectorAll('.case-summary__label').map(el => el.textContent), ['案件名稱', '客戶名稱', '查核期間']);
  for (const name of ['case-step', 'case-progress']) assert.equal(!!dom.content.querySelector('[data-bind="' + name + '"]'), false);
});

test('oct9 layout followup keeps the visible current step in the navigation and GUI readback', () => {
  const f = remapAppFixture();
  f.store.resetWorkflow(); f.store.setProject({ projectId: 'created-synthetic', caseName: '合成建案' });
  f.store.setStepIndex(1);
  f.dom.content.innerHTML += '<span data-bind="case-id">合成建案</span>';
  f.app.renderStepNav(f.state, f.store.STEPS);
  const nav = f.$('[data-bind="step-nav"]'), active = nav.querySelector('[aria-current="step"]');
  assert.equal(active.getAttribute('data-step-index'), '1');
  assert.equal(active.querySelector('[data-bind="case-step"]')?.textContent, '匯入資料');
  assert.match(nav.querySelector('.toc-progress').textContent, /進度 1\/6/);
  const source = fs.readFileSync(path.join(__dirname, '../harness/gui-driver/GuiScenarios.cs'), 'utf8');
  const script = source.match(/CreatedProjectProbeScript = Probe\("""([\s\S]*?)"""\);/)[1]
    .replace('/* shared GUI helpers */', 'function visible(el) { return !!el && !el.hidden; }');
  f.$('[data-action="app-exit"]').getBoundingClientRect = () => ({ left: 0, top: 0, width: 36, height: 36 });
  const probe = vm.runInContext(script, f.context);
  assert.equal(probe.caseStepVisible, true); assert.equal(probe.caseStepText, '匯入資料');
  assert.equal(probe.importStepVisible, true); assert.equal(probe.caseIdText, '合成建案');
  assert.equal(probe.formAbsent, true);
  // Going back changes the current position, not the completed-step count.
  f.store.setStepIndex(0); f.app.renderStepNav(f.state, f.store.STEPS);
  assert.equal(nav.querySelectorAll('[data-bind="case-step"]').length, 1);
  assert.equal(nav.querySelector('[data-bind="case-step"]').textContent, '建立案件');
  assert.match(nav.querySelector('.toc-progress').textContent, /進度 1\/6/);
});

// 使用者 2026-10-11 裁定：第五步目前的情境有修改還沒儲存時，編輯或複製已存情境、新增情境與套用範例都先詢問，
// 按鈕比照第四步科目分類；沒有修改時照舊直接換，取消編輯不問。對話框文字與按鈕逐字寫死，不由被測程式算出。
const savedManual = name => ({ ...savedG(name), rationale: name + '：合成動機',
  editorOrigins: { ...savedG(name).editorOrigins, nameIsAutomatic: false, rationaleIsAutomatic: false } });

function pendingScenarioFixture(saved = [savedManual('甲情境'), savedManual('乙情境')], fixture = filterOperationFixture) {
  const f = fixture({ saved });
  f.dom.document.body = f.dom.element('body'); f.dom.document.removeEventListener = () => {};
  const modals = () => f.dom.document.body.querySelectorAll('[data-bind="filter-pending-modal"]');
  const modal = () => modals()[0] || null;
  function choose(choice) {
    const dialog = modal(); assert.ok(dialog, 'the confirmation is shown');
    dialog.fire('click', { target: dialog.querySelector('.modal__actions [data-pending-choice="' + choice + '"]') });
  }
  const body = () => modal().querySelector('.modal__body').textContent;
  const buttons = () => modal().querySelectorAll('.modal__actions button').map(button => button.textContent);
  // 2026-10-13 第五步一次只存或刪一個情境：畫面改送 filterScenarioSave／Remove／Recheck，只帶正在處理的那一個情境；合成回應照後端的形狀，
  // 把這一個放進畫面清單的位置（新增時加在最後）再帶回整批。第一次失敗：FrontendMapping 收據 20261013-103553346-54ebed91711946dd8ff77c3dd1be7b23。
  const resolveCommit = index => {
    const { payload } = f.commits[index], scenarios = f.store.getState().filter.savedScenarios.slice();
    const position = payload.position === null ? scenarios.length + 1 : payload.position;
    scenarios[position - 1] = payload.scenario;
    f.commits[index].resolve({ position, scenarios, savedCount: scenarios.length,
      resultRef: { revision: 'oct11-' + index, populationScope: 'auditPeriod' } });
  };
  return { ...f, modals, modal, choose, body, buttons, resolveCommit };
}

test('oct11 drafts without changes switch at once from every entry point', () => {
  const f = pendingScenarioFixture();
  f.click('[data-action="edit-scenario"][data-index="0"]');
  assert.equal(f.modal(), null); assert.equal(f.draft().__editingIndex, 0); assert.equal(f.draft().name, '甲情境');
  
  f.click('[data-action="edit-scenario"][data-index="1"]');
  assert.equal(f.modal(), null, 'opening and closing the save panel is not a change');
  assert.equal(f.draft().__editingIndex, 1); assert.equal(f.draft().name, '乙情境');
  f.click('[data-action="copy-scenario"][data-index="0"]');
  assert.equal(f.modal(), null); assert.equal(f.draft().name, '甲情境（副本）'); assert.equal(f.draft().__editingIndex, undefined);
  f.click('[data-action="apply-template"][data-template-key="weekendManualDescription"]');
  assert.equal(f.modal(), null); assert.equal(f.draft().name, '週末人工分錄的特定摘要');
  f.click('[data-action="apply-template"][data-template-key="nonBusinessDayExcludingDates"]');
  assert.equal(f.modal(), null, 'an example that was only applied is not a change'); assert.equal(f.draft().name, '非營業日且排除指定日期');
  assert.equal(f.commits.length, 0);
});

test('oct11 opening a saved scenario over unsaved conditions asks first and staying keeps everything', () => {
  const f = pendingScenarioFixture();
  f.click('[data-kct-letter="G"]');
  const draft = f.draft(), json = JSON.stringify(draft);
  f.click('[data-action="edit-scenario"][data-index="1"]');
  assert.equal(f.modals().length, 1);
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-41（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  assert.equal(f.modal().querySelector('.modal__title').textContent, '篩選情境尚未儲存');
  assert.equal(f.body(), '目前的條件尚未儲存為情境。開啟「乙情境」會換掉這些條件。');
  assert.deepEqual(f.buttons(), ['留在這裡', '不儲存，直接開啟', '儲存並開啟']);
  assert.equal(f.dom.document.activeElement.getAttribute('data-pending-choice'), 'save', 'saving a scenario clears nothing, so it is the default');
  assert.equal(f.draft(), draft, 'nothing changes before the auditor chooses');
  f.click('[data-action="edit-scenario"][data-index="0"]');
  assert.equal(f.modals().length, 1, 'a second request does not stack another dialog');
  f.choose('stay');
  assert.equal(f.modal(), null); assert.equal(f.draft(), draft); assert.equal(JSON.stringify(f.draft()), json);
  f.click('[data-action="edit-scenario"][data-index="1"]');
  f.modal().fire('click', { target: f.modal().querySelector('.modal__backdrop') });
  assert.equal(f.modal(), null, 'clicking outside the dialog also stays'); assert.equal(JSON.stringify(f.draft()), json);
  f.click('[data-action="edit-scenario"][data-index="1"]'); f.choose('continue');
  assert.equal(f.modal(), null); assert.equal(f.draft().__editingIndex, 1); assert.equal(f.draft().name, '乙情境');
  assert.equal(f.commits.length, 0, 'not saving sends nothing');
});

test('oct11 save and open updates the edited scenario first and opens the next one only after the save succeeds', async () => {
  const f = pendingScenarioFixture();
  f.click('[data-action="edit-scenario"][data-index="0"]');
  f.type('[data-bind="scenario-name"]', '甲情境（修改）');
  f.click('[data-action="edit-scenario"][data-index="1"]');
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-41（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  assert.equal(f.body(), '情境「甲情境」的修改尚未儲存。開啟「乙情境」會換掉這些修改。');
  f.choose('save');
  assert.equal(f.modal(), null, 'the dialog closes while saving');
  assert.equal(f.commits.length, 1);
  // 2026-10-13 第五步一次只存或刪一個情境：畫面改送 filterScenarioSave／Remove／Recheck，只帶正在處理的那一個情境。原本看整批名稱；
  // 現在看只送第 1 個（原名甲情境）並帶新名稱。第一次失敗：FrontendMapping 收據 20261013-103553346-54ebed91711946dd8ff77c3dd1be7b23。
  assert.equal(f.commits[0].method, 'filterScenarioSave'); assert.equal(f.commits[0].payload.position, 1);
  assert.equal(f.commits[0].payload.expectedName, '甲情境'); assert.equal(f.commits[0].payload.scenario.name, '甲情境（修改）');
  assert.equal(f.draft().__editingIndex, 0, 'the next scenario waits for the save');
  f.commits[0].reject(new Error('合成儲存失敗')); await settle(); await settle();
  assert.equal(f.modal(), null); assert.equal(f.draft().__editingIndex, 0); assert.equal(f.draft().name, '甲情境（修改）');
  assert.ok(f.messages().some(m => m.startsWith('儲存篩選情境失敗：合成儲存失敗')));
  f.click('[data-action="edit-scenario"][data-index="1"]'); f.choose('save');
  assert.equal(f.commits.length, 2);
  f.resolveCommit(1); await settle(); await settle();
  assert.equal(f.draft().__editingIndex, 1); assert.equal(f.draft().name, '乙情境');
  assert.deepEqual(plain(f.store.getState().filter.savedScenarios.map(s => s.name)), ['甲情境（修改）', '乙情境']);
});

test('oct11 save and open stays when the save is blocked by the ten scenario limit', async () => {
  const f = pendingScenarioFixture(Array.from({ length: 10 }, (_, i) => savedManual('情境' + (i + 1))));
  f.click('[data-kct-letter="G"]');
  f.click('[data-action="edit-scenario"][data-index="0"]'); f.choose('save'); await settle(); await settle();
  assert.equal(f.commits.length, 0); assert.equal(f.modal(), null);
  assert.equal(f.draft().__editingIndex, undefined, 'the new draft stays on screen');
  assert.ok(f.messages().includes(SCENARIO_LIMIT_MESSAGE));
});

test('oct11 copy and examples ask with their own wording; saving clears the draft', async () => {
  const f = pendingScenarioFixture();
  f.click('[data-action="edit-scenario"][data-index="0"]');
  f.type('[data-bind="scenario-name"]', '甲情境（修改）');
  f.click('[data-action="copy-scenario"][data-index="1"]');
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-41（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  assert.equal(f.body(), '情境「甲情境」的修改尚未儲存。複製「乙情境」會換掉這些修改。');
  assert.deepEqual(f.buttons(), ['留在這裡', '不儲存，直接複製', '儲存並複製']);
  f.choose('stay');
  f.click('[data-action="apply-template"][data-template-key="weekendManualDescription"]');
  assert.equal(f.body(), '情境「甲情境」的修改尚未儲存。套用範例「週末人工分錄的特定摘要」會換掉這些修改。');
  assert.deepEqual(f.buttons(), ['留在這裡', '不儲存，直接套用', '儲存並套用']);
  f.choose('stay'); assert.equal(f.draft().name, '甲情境（修改）');
  // 第九節 D：存好就清空，後續不再提供「新增情境」入口。
  f.click('[data-action="save-scenario"]'); f.resolveCommit(0); await settle(); await settle();
  assert.equal(f.$('[data-action="new-scenario"]'), null);
  assert.equal(f.modal(), null); assert.equal(f.draft().groups.length, 0); assert.equal(f.draft().name, '');
  assert.equal(f.commits.length, 1, 'clearing the saved draft sends nothing more');
});

test('oct11 a saved scenario starts a new one at once until it changes again', async () => {
  const f = pendingScenarioFixture([]);
  f.click('[data-kct-letter="G"]'); f.click('[data-action="save-scenario"]');
  f.resolveCommit(0); await settle(); await settle();
  assert.equal(f.$('[data-action="new-scenario"]'), null);
  assert.equal(f.modal(), null); assert.equal(f.draft().groups.length, 0);
});

test('oct11 cancelling an edit discards the change without asking', () => {
  const f = pendingScenarioFixture();
  f.click('[data-action="edit-scenario"][data-index="0"]');
  f.type('[data-bind="scenario-name"]', '甲情境（修改）');
  f.click('[data-action="cancel-edit-scenario"]');
  assert.equal(f.modal(), null); assert.equal(f.draft().groups.length, 0); assert.equal(f.draft().__editingIndex, undefined);
  assert.deepEqual(plain(f.store.getState().filter.savedScenarios.map(s => s.name)), ['甲情境', '乙情境']);
});

test('oct11 an example that needs a field asks only when the chosen field is about to replace the draft', () => {
  const rdeFields = [{ fieldId: 'rde.dept', sourceColumn: 'Dept', label: '部門', valueType: 'text' },
    { fieldId: 'rde.unit', sourceColumn: 'Unit', label: '部門', valueType: 'text' }];
  const f = pendingScenarioFixture([], () => customPaneFixture({ rdeFields }));
  f.click('[data-add-field="description"]');
  const before = f.draft(), json = JSON.stringify(before);
  f.click('[data-action="toggle-examples"]');
  f.click('[data-action="apply-template"][data-template-key="manualDepartment"]');
  assert.ok(f.$('[data-example-pick="manualDepartment"]')); assert.equal(f.modal(), null, 'choosing the field comes first');
  f.click('[data-example-field="rde.unit"]');
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-41（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。
  assert.equal(f.body(), '目前的條件尚未儲存為情境。套用範例「人工分錄的特定部門」會換掉這些條件。');
  f.choose('stay'); assert.equal(f.draft(), before); assert.equal(JSON.stringify(f.draft()), json);
  f.click('[data-example-field="rde.unit"]'); f.choose('continue');
  assert.equal(f.draft().name, '人工分錄的特定部門');
  assert.deepEqual(plain(f.draft().groups[0].rules.map(rule => [rule.type, rule.fieldId])), [['manualAuto', null], ['fieldValue', 'rde.unit']]);
});

// 使用者 2026-10-13 裁定訊息分三級（錯誤、需要注意、一般），操作失敗時在目前步驟顯示提示框，耗時改成秒並合併。
// 以下從審計員的操作寫：用正式的 app.js 畫出流程畫面與訊息區，按正式步驟的按鈕，後端回應用合成資料。
// 字句只比對這次裁定定下的結構（動作、原因、等級文字與耗時寫法）；其他訊息的字等勾選頁確認後另外改。
function stepFailureFixture(stepIndex = 5) {
  const f = remapAppFixture();
  f.window.getComputedStyle = () => ({ gridTemplateColumns: 'none' });
  f.store.setView('workflow'); f.store.setStepIndex(stepIndex);
  const show = () => { f.app.renderContent(f.state, f.store.STEPS[f.state.currentStepIndex]); return f.dom.content; };
  const failureNotice = () => f.dom.content.querySelector('.stepflow-item__failure');
  // 訊息區用正式 index.html 的標記，只換掉中欄內容來檢查開關上的數量標示。
  const html = fs.readFileSync(path.join(root, '..', 'index.html'), 'utf8');
  const asideStart = html.indexOf('<aside class="messages'), asideEnd = html.indexOf('</aside>', asideStart) + '</aside>'.length;
  assert.ok(asideStart >= 0 && asideEnd > asideStart, 'messages markup seam');
  function badge() {
    const holder = f.dom.element(); holder.innerHTML = html.slice(asideStart, asideEnd);
    const saved = f.dom.content.children; f.dom.content.children = [holder]; holder.parentElement = f.dom.content;
    try { f.app.renderMessagesPanel(f.state); return f.dom.content.querySelector('[data-bind="message-badge"]'); }
    finally { f.dom.content.children = saved; }
  }
  function messageItem(message) { const holder = f.dom.element('ul'); holder.appendChild(f.app.buildMessage(message)); return holder.children[0]; }
  function exportOnce(outcome) {
    show(); f.click('[data-action="export-workpaper"]');
    const request = f.requests.filter(r => r.action === 'exportWorkpaperStream').at(-1); assert.ok(request, 'exportWorkpaperStream requested');
    if (outcome instanceof Error || (outcome && outcome.code)) request.reject(outcome); else request.resolve(outcome);
  }
  return { ...f, show, failureNotice, badge, messageItem, exportOnce };
}

test('items2 a failed workpaper export shows a red notice under the step title until the auditor acts again', async () => {
  const f = stepFailureFixture(); f.show(); f.change('[data-bind="include-prescreen-report"]', false);
  f.exportOnce(new Error('合成輸出失敗。')); await settle();
  const notice = f.show() && f.failureNotice();
  assert.ok(notice, 'the current step shows the failure');
  assert.equal(notice.textContent, '產生工作底稿失敗：合成輸出失敗。');
  assert.match(notice.className, /\bform-notice\b/); assert.doesNotMatch(notice.className, /form-notice--warning/);
  assert.equal(notice.getAttribute('role'), 'alert');
  const current = notice.parentElement, order = current.children.map(child => child.className);
  assert.ok(order.indexOf('stepflow-item__head') < order.indexOf(notice.className) && order.indexOf(notice.className) < order.indexOf('stepflow-item__body'),
    'the notice sits between the step title and its content');
  // 訊息面板留一則錯誤：有符號、讀屏軟體念「錯誤：」，耗時與最後進度只在這裡。
  const logged = f.state.messages[0];
  // 使用者 2026-10-13 勾選頁確認 19：耗時收進括號（第一次失敗：FrontendMapping 收據 20261013-141147763-5dc8acde89a2431d9568ff02be8bd79e）。
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-26、R3-27（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。 括號後加句號。
  assert.equal(logged.level, 'error'); assert.match(logged.text, /^產生工作底稿失敗：合成輸出失敗（耗時(?: \d+\.\d 秒|不到 0\.1 秒)）。$/);
  const item = f.messageItem(logged);
  assert.match(item.className, /messages__item--error/);
  assert.equal(item.querySelector('.messages__mark').getAttribute('aria-hidden'), 'true');
  assert.equal(item.querySelector('.visually-hidden').textContent, '錯誤：');
  assert.equal(item.querySelector('.messages__text').textContent, logged.text);
  const closed = f.badge();
  assert.equal(closed.hidden, false); assert.match(closed.className, /messages__badge--error/);
  assert.equal(closed.getAttribute('aria-label'), f.state.messagesUnseen + ' 則新訊息，含錯誤');
  // 再按一次就拿掉；這次成功，不再出現。
  f.exportOnce({ sheetStats: [], reportArtifacts: [] }); await settle();
  assert.equal(f.state.operationFailure, null); f.show(); assert.ok(f.failureNotice() === null, 'no step notice');
  // 再失敗一次後換到別的步驟，也拿掉。
  f.exportOnce(new Error('合成輸出失敗。')); await settle(); f.show(); assert.ok(f.failureNotice());
  f.store.setStepIndex(4); assert.equal(f.state.operationFailure, null);
  f.store.setStepIndex(5); f.show(); assert.ok(f.failureNotice() === null, 'no step notice');
});

test('items2 an operation with its own error box does not add a step notice', async () => {
  // 確認欄位配對失敗時，原因已寫在配對區的錯誤框。
  const f = stepFailureFixture(2); prepareUsageMapping(f, 'gl'); f.show();
  f.click('[data-action="commit-gl"]'); f.request('mappingCommitGl').reject(new Error('合成配對失敗。')); await settle();
  assert.equal(f.state.messages[0].level, 'error'); assert.equal(f.state.operationFailure, null);
  f.show(); assert.ok(f.failureNotice() === null, 'no step notice');
  // 儲存情境失敗時，原因寫在儲存按鈕上方。
  const g = filterOperationFixture(); g.store.setView('workflow'); g.store.setStepIndex(4);
  g.click('[data-kct-letter="G"]'); g.click('[data-action="save-scenario"]'); await settle();
  g.commits[0].reject(new Error('合成儲存失敗。')); await settle(); await settle();
  assert.equal(g.notice(), '儲存失敗：合成儲存失敗。');
  assert.equal(g.store.getState().messages[0].level, 'error'); assert.equal(g.store.getState().operationFailure, null);
});

// 文字批次 B（使用者 2026-10-13 裁定「已經有就地錯誤框的操作維持原樣」）：步驟提示框只看操作是否真的把錯誤寫在畫面上。
// 只做焦點的「移除篩選情境」失敗時要有提示框；預覽只有把原因標到條件列時才算就地顯示。
test('text-b removing a saved scenario that fails shows the step notice', async () => {
  const f = oneAtATimeFixture(); f.store.setView('workflow'); f.store.setStepIndex(4);
  f.click('[data-action="remove-scenario"][data-index="1"]'); f.click('[data-action="confirm-remove-scenario"][data-index="1"]'); await settle();
  f.scenarioRequests().at(-1).reject(new Error('合成移除失敗。')); await settle(); await settle();
  const failure = f.store.getState().operationFailure;
  assert.ok(failure !== null, 'the remove failure is shown in the step');
  assert.equal(failure.message, '移除篩選情境失敗：合成移除失敗。');
  assert.equal(failure.level, 'error');
});

for (const marked of [true, false]) test('text-b a failed preview ' + (marked ? 'marked on its rule row has no step notice' : 'without rule reasons shows the step notice'), async () => {
  const f = oneAtATimeFixture(); f.store.setView('workflow'); f.store.setStepIndex(4);
  f.store.setFilterDraft(projectionDraft(['Alpha']));
  f.click('[data-action="preview-scenario"]'); await settle();
  const error = marked
    ? Object.assign(new Error('第 1 組第 1 條：合成錯誤。'), { code: 'invalid_scenario', details: [{ group: 1, rule: 1, message: '合成錯誤。' }] })
    : new Error('合成預覽失敗。');
  f.sent.find(request => request.method === 'filterPreview').reject(error); await settle(); await settle();
  const failure = f.store.getState().operationFailure;
  if (marked) {
    assert.ok(f.$('.rule-row[data-gi="0"][data-ri="0"]').classList.contains('rule-row--invalid'), 'the reason is on the rule row');
    assert.ok(failure === null, 'no step notice when the rule row already shows the reason');
  } else {
    assert.ok(failure !== null, 'the preview failure is shown in the step');
    assert.equal(failure.message, '預覽篩選情境失敗：合成預覽失敗。');
  }
});

test('text-b a failed or cancelled operation puts elapsed time and the last progress in brackets without repeating the elapsed time', async () => {
  const f = stepFailureFixture(); f.show(); f.change('[data-bind="include-prescreen-report"]', false);
  f.show(); f.click('[data-action="export-workpaper"]');
  f.store.setBusyDetail('工作底稿：正在寫入「合成表」，已完成 1 張工作表、寫入 10 列，經過 3.0 秒');
  f.request('exportWorkpaperStream').reject(new Error('合成輸出失敗。')); await settle();
  const failed = f.state.messages[0].text;
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-26、R3-27、R3-28（第一次失敗：FrontendMapping 收據 20261014-012331883-35a6efc8b8a145ad969793d34c2094a4）。 括號裡的進度不再有第二個冒號，括號後加句號。
  assert.match(failed, /^產生工作底稿失敗：合成輸出失敗（耗時(?: \d+\.\d 秒|不到 0\.1 秒)，最後進度：工作底稿正在寫入「合成表」，已完成 1 張工作表、寫入 10 列）。$/);
  assert.equal(failed.includes('經過'), false);
  f.show(); assert.equal(f.failureNotice().textContent, '產生工作底稿失敗：合成輸出失敗。');
  f.exportOnce(new Error('合成輸出失敗。')); await settle();
  assert.match(f.state.messages[0].text, /^產生工作底稿失敗：合成輸出失敗（耗時(?: \d+\.\d 秒|不到 0\.1 秒)）。$/);
  f.exportOnce({ code: 'operation_cancelled', message: '合成取消' }); await settle();
  assert.match(f.state.messages[0].text, /^產生工作底稿已取消（耗時(?: \d+\.\d 秒|不到 0\.1 秒)）。$/);
  f.exportOnce({ code: 'operation_cancelled', message: '合成取消', completedResultsPreserved: true }); await settle();
  assert.match(f.state.messages[0].text, /^產生工作底稿已取消，已完成的部分仍保留（耗時(?: \d+\.\d 秒|不到 0\.1 秒)）。$/);
  f.exportOnce({ code: 'completeness_prerequisite_failed', message: '合成完整性檢查未通過。' }); await settle();
  assert.equal(f.state.messages[0].text, '產生工作底稿尚無法執行：合成完整性檢查未通過。');
  f.show(); assert.equal(f.failureNotice().textContent, '產生工作底稿尚無法執行：合成完整性檢查未通過。');
  // 使用者 2026-10-14 第三輪勾選頁確認 R3-34：沒有已儲存的情境和前置條件不足同一種寫法，不附耗時，屬提醒而非錯誤。
  f.exportOnce({ code: 'no_saved_scenario', message: '尚未儲存任何篩選情境，請先在「進階條件篩選」儲存情境' }); await settle();
  assert.equal(f.state.messages[0].text, '產生工作底稿尚無法執行：尚未儲存任何篩選情境，請先在「進階條件篩選」儲存情境。');
  assert.equal(f.state.messages[0].level, 'warn');
  assert.equal(f.state.messages[0].text.includes('耗時'), false);
  f.exportOnce({ code: 'operation_in_progress', message: '合成忙碌' }); await settle();
  assert.equal(f.state.messages[0].text, '另一項作業還在進行，請等它完成後再試。');
  f.store.setBusy(true, '產生工作底稿'); f.window.JetUi.run('匯入 PBC-GL', () => Promise.resolve()); f.store.setBusy(false);
  assert.equal(f.state.messages[0].text, '「產生工作底稿」還在進行，請等它完成後再試。');
});

test('text-b report progress reads as one sentence per phase with the elapsed time', () => {
  const f = remapAppFixture(), format = f.app.formatExportProgress;
  const progress = (phase, sheetName, sheetsCompleted, rowsWritten) => ({ artifactKind: 'workingPaper', phase, sheetName,
    sheetsCompleted, rowsWritten, elapsedMilliseconds: 4200 });
  assert.equal(format(progress('preparingData', null, 0, 0)), '工作底稿：正在準備資料，經過 4.2 秒');
  assert.equal(format(progress('writingSheet', '合成表', 2, 1500)), '工作底稿：正在寫入「合成表」，已完成 2 張工作表、寫入 1,500 列，經過 4.2 秒');
  assert.equal(format(progress('finalizingWorkbook', null, 3, 1500)), '工作底稿：正在完成檔案，已完成 3 張工作表、寫入 1,500 列，經過 4.2 秒');
  assert.equal(format(progress('publishingArtifact', null, 3, 1500)), '工作底稿：正在存進案件資料夾，已完成 3 張工作表、寫入 1,500 列，經過 4.2 秒');
  assert.equal(format({ ...progress('preparingData', null, 0, 0), artifactKind: 'prescreenReport' }), '預篩選報告：正在準備資料，經過 4.2 秒');
});

for (const devTools of [false, true]) test('text-b the case list error box has no buttons and the support log is a general tool in the action row' + (devTools ? ' (debug build)' : ''), async () => {
  const f = remapAppFixture(); f.store.resetWorkflow(); f.store.setView('picker'); f.store.setDevToolsEnabled(devTools);
  const opening = f.window.JetUi.openProject('failed-synthetic');
  f.request('projectLoad').reject({ code: 'file_read_error', message: '合成載入失敗', correlationId: 'picker-load-x' }); await opening;
  f.app.renderPicker(f.state);
  const box = f.$('.picker-feedback');
  assert.ok(box !== null, 'the error box is shown');
  assert.match(box.textContent, /合成載入失敗/);
  assert.ok(box.querySelector('button') === null, 'the error box has no buttons');
  const actions = f.$('.picker-panel__actions');
  assert.ok(actions !== null, 'the action row is shown');
  const exportButton = actions.querySelector('[data-action="picker-support-export"]');
  assert.ok(exportButton !== null, 'the support log button is in the action row');
  assert.equal(exportButton.textContent, '輸出支援日誌');
  assert.equal(actions.children.at(-1 - (devTools ? 1 : 0)), exportButton, 'the support log button follows the refresh and sync buttons');
  assert.equal(actions.querySelector('[data-action="picker-dev-log-export"]') !== null, devTools);
  f.click('[data-action="picker-support-export"]');
  const sent = f.request('supportLogExport');
  assert.equal('projectId' in sent.payload, false); assert.equal('correlationId' in sent.payload, false);
  sent.resolve({ filePath: 'synthetic/logs/JET-support.txt', lineCount: 3, scope: 'all' }); await settle(); f.app.renderPicker(f.state);
  const status = f.$('.picker-panel__actions [role="status"]');
  assert.ok(status !== null, 'the result is announced beside the button');
  assert.equal(status.textContent, '支援日誌已輸出：synthetic/logs/JET-support.txt');
  assert.match(f.$('.picker-feedback').textContent, /合成載入失敗/);
});

for (const [name, error, level, notice] of [
  // 使用者 2026-10-13 勾選頁確認 18、16：「已阻擋」改「尚無法執行」，「請稍候，另一項作業進行中」改成統一的句子（第一次失敗：FrontendMapping 收據 20261013-141147763-5dc8acde89a2431d9568ff02be8bd79e）。
  ['blocked', { code: 'completeness_prerequisite_failed', message: '合成完整性檢查未通過。' }, 'warn', '產生工作底稿尚無法執行：合成完整性檢查未通過。'],
  ['another operation in progress', { code: 'operation_in_progress', message: '合成忙碌' }, 'warn', null],
  ['cancelled by the auditor', { code: 'operation_cancelled', message: '合成取消' }, 'info', null]
]) test('items2 a workpaper export that is ' + name + ' is shown as ' + (notice ? 'an amber step notice' : 'a message only'), async () => {
  const f = stepFailureFixture(); f.show(); f.change('[data-bind="include-prescreen-report"]', false);
  f.exportOnce(error); await settle(); f.show();
  assert.equal(f.state.messages[0].level, level);
  if (notice) {
    assert.equal(f.failureNotice().textContent, notice);
    assert.match(f.failureNotice().className, /form-notice form-notice--warning/);
  } else assert.ok(f.failureNotice() === null, 'no step notice');
  // 另一項作業進行中時這次操作沒有開始，不寫耗時。
  if (error.code === 'operation_in_progress') assert.equal(f.state.messages[0].text, '另一項作業還在進行，請等它完成後再試。');
});

test('items2 message levels show an amber mark for attention, none for general notes, and the switch takes the worst unseen level', () => {
  const f = stepFailureFixture();
  f.store.addMessage('合成一般說明。', 'info');
  let item = f.messageItem(f.state.messages[0]);
  assert.match(item.className, /messages__item--info/); assert.equal(item.querySelector('.messages__mark'), null); assert.equal(item.querySelector('.visually-hidden'), null);
  assert.doesNotMatch(f.badge().className, /messages__badge--(error|warn)/);
  assert.equal(f.badge().getAttribute('aria-label'), f.state.messagesUnseen + ' 則新訊息');
  f.store.addMessage('合成需要注意。', 'warn');
  item = f.messageItem(f.state.messages[0]);
  assert.match(item.className, /messages__item--warn/); assert.ok(item.querySelector('.messages__mark'));
  assert.equal(item.querySelector('.visually-hidden').textContent, '需要注意：');
  assert.match(f.badge().className, /messages__badge--warn/);
  assert.equal(f.badge().getAttribute('aria-label'), f.state.messagesUnseen + ' 則新訊息，含需要注意');
  f.store.addMessage('合成失敗。', 'error'); f.store.addMessage('合成之後的一般說明。', 'info');
  const badge = f.badge();
  assert.match(badge.className, /messages__badge--error/); assert.doesNotMatch(badge.className, /messages__badge--warn/);
  assert.equal(badge.getAttribute('aria-label'), '4 則新訊息，含錯誤');
  f.store.toggleMessagesPanel(); assert.equal(f.badge().hidden, true, 'opening the panel clears the unseen count');
});

test('items2 a successful long operation leaves one completion message with seconds, or minutes after a minute', async () => {
  const f = stepFailureFixture(); let clock = 0;
  f.window.performance = { now: () => clock };
  f.show(); f.change('[data-bind="include-prescreen-report"]', false);
  f.show(); f.click('[data-action="export-workpaper"]'); clock = 4200;
  f.request('exportWorkpaperStream').resolve({ sheetStats: [{}, {}], reportArtifacts: [] }); await settle();
  const workpaper = f.state.messages.filter(m => /工作底稿/.test(m.text));
  assert.deepEqual(plain(workpaper.map(m => [m.level, m.text])), [['info', '已在案件資料夾產生工作底稿（2 張工作表），耗時 4.2 秒。']]);
  assert.equal(f.state.messages.some(m => /完成，耗時|毫秒/.test(m.text)), false);
  clock = 0; f.show(); f.click('[data-action="export-workpaper"]'); clock = 171000;
  f.request('exportWorkpaperStream').resolve({ sheetStats: [{}], reportArtifacts: [] }); await settle();
  assert.equal(f.state.messages[0].text, '已在案件資料夾產生工作底稿（1 張工作表），耗時 2 分 51 秒。');
  assert.equal(f.window.JetUi.formatElapsed(3780000), '1 小時 3 分');
  assert.equal(f.window.JetUi.formatElapsed(59940), '59.9 秒');
  assert.equal(f.window.JetUi.formatElapsed(59960), '1 分 0 秒');
});

test('items2 the save-button reminders for a changed saved list and the ten-scenario limit are amber', async () => {
  const saved = Array.from({ length: 10 }, (_, i) => savedG('合成情境' + (i + 1)));
  const f = filterOperationFixture({ saved });
  f.click('[data-kct-letter="G"]'); f.click('[data-action="save-scenario"]'); await settle();
  assert.equal(f.notice(), SCENARIO_LIMIT_MESSAGE);
  assert.match(f.$('[data-bind="scenario-notice"]').className, /form-notice--warning/);
  assert.equal(f.store.getState().messages[0].level, 'warn');
  assert.equal(f.commits.length, 0);
});

test('items2 deleting a case whose folder could not be removed shows an amber reminder on the case list', async () => {
  const f = remapAppFixture(); f.store.resetWorkflow(); f.store.setView('picker');
  f.store.setProjects([{ projectId: '合成案件', databaseProvider: 'sqlite' }]); f.app.renderPicker(f.state);
  f.click('[data-action="picker-delete"]');
  f.request('projectDeletePreview').resolve({ projectId: '合成案件', reportCount: 0, workpaperCount: 0 }); await settle();
  f.click('[data-action="modal-confirm"]');
  f.request('projectDelete').resolve({ ok: true, message: '合成資料夾沒有刪乾淨。' }); await settle();
  f.request('projectListLocal').resolve({ projects: [] }); await settle(); await settle();
  f.app.renderPicker(f.state);
  const reminder = f.$('.picker-feedback--warning');
  assert.ok(reminder, 'the case list shows the reminder');
  assert.equal(reminder.textContent, '合成資料夾沒有刪乾淨。');
  assert.match(reminder.className, /form-notice form-notice--warning/);
  assert.doesNotMatch(f.dom.content.textContent, /無法完成操作/);
  assert.ok(f.state.messages.some(m => m.level === 'warn' && m.text === '合成資料夾沒有刪乾淨。'));
});

test('items2 reopening an older case shows unknown or missing message levels as general notes', async () => {
  const f = stepFailureFixture(), opening = f.window.JetUi.openProject('older-synthetic');
  f.request('projectLoad').resolve({ project: { projectId: 'older-synthetic' } }); await settle();
  f.request('logRecent').resolve({ messages: [
    { text: '合成未知等級。', level: 'critical', occurredUtc: '2026-10-01T01:03:00Z' },
    { text: '合成缺少等級。', occurredUtc: '2026-10-01T01:02:00Z' },
    { text: '合成舊提醒。', level: 'warn', occurredUtc: '2026-10-01T01:01:00Z' }] }); await opening;
  const restored = f.state.messages.filter(m => /^合成/.test(m.text));
  assert.deepEqual(plain(restored.map(m => [m.text, m.level])), [['合成未知等級。', 'info'], ['合成缺少等級。', 'info'], ['合成舊提醒。', 'warn']]);
  const unknown = f.messageItem(restored[0]);
  assert.equal(unknown.className, 'messages__item messages__item--info'); assert.equal(unknown.querySelector('.messages__mark'), null);
});

// 使用者 2026-10-14 第三輪勾選頁確認 R3-03：條件標題與讀回不印程式代號。已移除的 GL 欄位寫出原名並標明已移除，
// 名稱由 project.load 的 retiredGlFieldLabels 帶回；認不得的欄位與不在欄位配對中的攸關資料元素欄位各有固定寫法。
test('text-d a removed GL field, an unknown field and a field no longer in the mapping are titled without program codes', () => {
  const f = batch7Fixture();
  const retired = { type: 'fieldValue', field: 'voucherDate', operator: 'isNonBusinessDay', values: [] };
  const unknown = { type: 'fieldValue', field: 'syntheticGoneField', operator: 'in', values: ['X1'] };
  const rdeGone = { type: 'fieldValue', fieldId: 'rde.text-d-gone', operator: 'equals', value: 'Alpha' };
  assert.equal(f.ui.FilterValues.fieldTitle(retired, f.store.getState()), '欄位已不存在', 'without the catalog name the field is only known to be gone');
  f.store.setRetiredGlFieldLabels({ voucherDate: '傳票日期' });
  const state = f.store.getState();
  assert.equal(f.ui.FilterValues.fieldTitle(retired, state), '傳票日期（已移除）');
  assert.equal(f.ui.FilterValues.fieldTitle(unknown, state), '欄位已不存在');
  assert.equal(f.ui.FilterValues.fieldTitle(rdeGone, state), '已不在欄位配對中的攸關資料元素欄位');
  for (const [rule, title, code] of [[retired, '傳票日期（已移除）', 'voucherDate'], [unknown, '欄位已不存在', 'syntheticGoneField'],
    [rdeGone, '已不在欄位配對中的攸關資料元素欄位', 'rde.text-d-gone']]) {
    const text = f.text(f.api.ruleRowHtml(rule, { rules: [rule] }, 0, 0));
    assert.ok(text.includes(title), title);
    assert.ok(!text.includes(code), code + ' is not shown as text');
  }
  assert.equal(f.ui.glFieldReadback(state, 'voucherDate'), '已移除的「傳票日期」');
  assert.equal(f.ui.glFieldReadback(state, 'syntheticGoneField'), '已不存在的欄位');
  assert.equal(f.ui.glFieldReadback(state, 'postDate'), '總帳日期');
});

// 使用者 2026-10-14 第三輪勾選頁確認 R3-03：retiredGlFieldLabels 隨開案存進畫面狀態，換開沒有帶名稱的案件時清空。
// 同一批刪除了開案時還原篩選母體範圍的呼叫：母體只有查核期間一種，開案後仍是 auditPeriod。
test('text-d opening a case keeps the removed field names from project.load and the audit-period population', () => {
  const f = batch8Fixture(); f.store.resetWorkflow();
  f.window.setInterval = () => 0; f.window.clearInterval = () => {};
  const stale = { validation: false, prescreen: false, filter: false };
  f.ui.applyLoadedProject({ project: { projectId: 'text-d-retired' }, retiredGlFieldLabels: { voucherDate: '傳票日期' }, staleState: stale },
    { stayOnCurrentStep: true });
  assert.deepEqual(plain(f.store.getState().retiredGlFieldLabels), { voucherDate: '傳票日期' });
  assert.equal(f.store.getState().filter.populationScope, 'auditPeriod');
  assert.equal(typeof f.store.restoreFilterPopulationScope, 'undefined', 'the unused restore entry point stays deleted');
  f.ui.applyLoadedProject({ project: { projectId: 'text-d-other' }, staleState: stale }, { stayOnCurrentStep: true });
  assert.deepEqual(plain(f.store.getState().retiredGlFieldLabels), {});
  assert.equal(f.store.getState().filter.populationScope, 'auditPeriod');
});

// 使用者 2026-10-14 第三輪勾選頁確認 R3-08：清單超過上限時提示框逐條寫出位置，原因和欄位條件同一句，
// 不再另列「文字值清單每組最多…」「攸關資料元素欄位的值清單每組最多…」。
test('text-d an oversized value list is named at its own position with the shared reason', () => {
  const f = kctFixture();
  const draft = f.store.getState().filter.draft;
  const many = Array.from({ length: 101 }, (_, i) => 'V' + i);
  draft.groups = [{ join: 'AND', matchScope: 'row', rules: [{ type: 'fieldValue', field: 'description', operator: 'in', values: ['A'] }] },
    { join: 'AND', matchScope: 'row', rules: [{ type: 'fieldValue', field: 'description', operator: 'in', values: ['A'] },
      { type: 'textSet', join: 'AND', field: 'description', mode: 'contains', values: many }] }];
  f.store.setFilterDraft(draft);
  const result = plain(f.gate());
  assert.deepEqual(result, { ok: false, notice: '尚需補齊：第 2 組第 2 條「文字值清單」請填 1 到 100 個值' });
  assert.doesNotMatch(result.notice, /每組最多/);
});

// 使用者 2026-10-14 第三輪勾選頁確認 R3-26：四捨五入是 0.0 秒時寫「不到 0.1 秒」，接在「耗時」「經過」後面不空格。
test('text-d elapsed time under a tenth of a second reads as less than 0.1 seconds', () => {
  const f = batch7Fixture();
  assert.equal(f.ui.elapsedPhrase('耗時', 0), '耗時不到 0.1 秒');
  assert.equal(f.ui.elapsedPhrase('耗時', 49), '耗時不到 0.1 秒');
  assert.equal(f.ui.elapsedPhrase('經過', 0), '經過不到 0.1 秒');
  assert.equal(f.ui.elapsedPhrase('耗時', 50), '耗時 0.1 秒');
  assert.equal(f.ui.elapsedPhrase('耗時', 4200), '耗時 4.2 秒');
  assert.equal(f.ui.formatElapsed(0), '不到 0.1 秒');
});

// 使用者 2026-10-14 第三輪勾選頁同意刪除欄位條件裡的「分錄性質」選項與切換程式：畫面早已不提供，
// 欄位條件的標題只寫欄位名稱。
test('text-d field conditions no longer offer the entry-nature pseudo fields', () => {
  const f = batch7Fixture();
  const rule = { type: 'fieldValue', field: 'description', operator: 'in', values: ['A'] };
  assert.equal(f.api.ruleSubjectHtml(rule, 'field'), '');
  const row = f.api.ruleRowHtml(rule, { rules: [rule] }, 0, 0);
  assert.doesNotMatch(row, /分錄性質|data-field-subject/);
  assert.doesNotMatch(f.ui.FilterValues.render(rule, f.store.getState(), 'text-d'), /分錄性質/);
});

// 使用者 2026-10-14 第三輪勾選頁確認 R3-28：進度接進訊息時拿掉「工作底稿：」的冒號，一句只留一個冒號；
// 取消時「當時進度」用同一個寫法（app.js），忙碌畫面上的進度照舊。
test('text-d progress quoted inside a message drops the second colon', () => {
  const f = batch7Fixture();
  assert.equal(f.ui.progressInMessage('工作底稿：正在寫入「合成表」，已完成 1 張工作表、寫入 10 列，經過 3.0 秒'),
    '工作底稿正在寫入「合成表」，已完成 1 張工作表、寫入 10 列，經過 3.0 秒');
  assert.equal(f.ui.progressInMessage('已讀取 1,000 列'), '已讀取 1,000 列');
  const app = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
  assert.ok(app.includes("'，當時進度：' + Ui.progressInMessage(latest.busyDetail)"), 'the cancel snapshot uses the same wording');
});
