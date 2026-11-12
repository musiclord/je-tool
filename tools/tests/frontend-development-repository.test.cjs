'use strict';
// 要讀 src/JET 以外檔案的前端測試：需求文件 docs/kct-gl-workflow.md，以及 tools/harness/gui-driver 裡 Gui 情境的腳本。
// 這五個測試原本在 frontend-workflow.test.cjs；前端測試搬到 src/JET/tests/frontend 時拆到這裡，因為只有 src/JET 時沒有這些檔案。
// 卡片常數、scenarioLifecycleFixture 與 remapAppFixture 照 src/JET/tests/frontend/frontend-workflow.test.cjs 的同名定義複製，
// 改其中一邊時另一邊一起改。註解照搬沒有改寫，提到的裁定見 docs/rulings.md。
// 全部使用程式生成的合成資料。
require('../../src/JET/tests/frontend/support/resource-guard.cjs');
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { root, plain, treeDom, usageJourneyFixture, filterOperationFixture, FILTER_STEP_PARTS, filterStepExposure } =
  require('../../src/JET/tests/frontend/support/frontend-fixtures.cjs');
const { contractJetApi } = require('../../src/JET/tests/frontend/support/action-contract.cjs');

const BLANK = 'blankDescription', TRAILING = 'specificTrailingDigits', NON_BUSINESS = 'nonBusinessDay';
const FIXED_ASSET = 'fixedAssetDebitExpenseCredit', KEYWORDS = 'specificKeywords', REVENUE = 'revenueDebitNearQuarterEnd';
const cardSelector = key => '[data-common-card="' + key + '"]';

function scenarioLifecycleFixture() {
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const document = { querySelector: () => null, createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  // 第五步拆成五個檔案：前四個先載入，主檔的內部函式從 FilterStep 取。第一次失敗：FrontendMapping 收據 20261025-053146514-770b4770cad34a55a1f25bee3a62b08f。
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js', ...FILTER_STEP_PARTS]) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const source = fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8');
  vm.runInContext(source.replace("  Ui.registerStep('filter', render);",
    filterStepExposure('workflow', ['completeScenarioSave', 'savedFeedbackText', 'filterCommitSnapshot', 'syncViewState', 'toWireDraft', 'addCommonToDraft', 'removeCommonFromActiveGroup', 'refreshAutomaticMetadata', 'commonPickerHtml', 'draftRuleProblems', 'ruleSummaryLabel', 'commonDescriptionHtml', 'bind'], 'view: () => FilterStep.viewState')), context);
  const store = window.JetStore, api = window.workflow;
  store.setProject({ projectId: 'sequence-fixture' }); api.syncViewState(store.getState());
  // 卡片以代號識別（2026-10-29 拿掉卡片字母，函式與常數也改名，原本依字母找卡片）。
  const item = key => window.JetUi.FILTER_COMMON_CONDITIONS.find(x => x.key === key);
  function toggle(key, remove = false) {
    const draft = store.getState().filter.draft;
    api[remove ? 'removeCommonFromActiveGroup' : 'addCommonToDraft'](draft, item(key));
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

// 2026-10-29：測試名稱原本以字母範圍稱呼這五張卡。需求文件的表格仍以當時的項次欄列出這五列，順序和卡片清單的前五張相同，
// 原本依項次欄找列，改成取表格的前五列依序對到前五張卡。動機的期望值原本由卡片資料組出、每行以字母開頭，
// 改成需求文件的五句評估說明依序以換行相接（裁定 2026-10-29-11：動機不再加字母開頭）。
test('the five cards with a default rationale reproduce the approved assessment text verbatim', () => {
 const f = scenarioLifecycleFixture();
 const doc = fs.readFileSync(path.resolve(__dirname, '../../docs/kct-gl-workflow.md'), 'utf8').split('\n');
 const header = doc.findIndex(line => line.startsWith('| 項次 | 高風險分錄篩選條件 | 評估說明 |'));
 assert.ok(header >= 0, 'the requirement table is found');
 const docRationales = doc.slice(header + 2, header + 7).map(line => line.split('|')[3].trim());
 const keys = [REVENUE, FIXED_ASSET, 'revenueWithoutNormalCounterpart', 'manualRevenueEntry', 'specificPreparer'];
 keys.forEach((key, index) => {
  const card = f.window.JetUi.FILTER_COMMON_CONDITIONS.find(x => x.key === key);
  assert.equal(card.defaultRationale, docRationales[index], key); f.toggle(key);
 });
 const draft=f.store.getState().filter.draft;
 assert.equal(draft.rationale, docRationales.join('\n'));
 draft.__rationaleDirty=true; draft.rationale='手寫保留'; f.toggle(KEYWORDS); assert.equal(draft.rationale,'手寫保留');
});

// Run the existing GUI readback JavaScript against production-rendered mapping UI without opening a desktop window.
function oct9GuiMappingFixture() {
  const dom = treeDom();
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
  store.setMappingDraft('gl', 'isManual', 'Manual');
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

test('GUI approval readback inspects the actual classic and grid controls in all three states', () => {
  const f = oct9GuiMappingFixture();
  for (const view of ['classic', 'grid']) {
    f.store.setMappingUiMode(view);
    for (const [mode, source, choice] of [['mapped', 'Approval', 'column:Approval'], ['unmapped', '', 'mode:unmapped'], ['sameAsGlDate', '', 'mode:sameAsGlDate']]) {
      f.store.setMappingDraft('gl', 'approvalDate', source);
      f.store.patchGlMappingOptions({ approvalDateMode: mode }); f.draw();
      const probe = f.read();
      assert.equal(probe.approvalChoice, choice, view + ' ' + mode);
      assert.equal(probe.approvalFieldPresent, true, view + ' ' + mode);
      assert.equal(probe.mode, mode); assert.equal(probe.source, source);
    }
  }
});

test('GUI options-only edit targets a rendered editable code and preserves mapping fields', () => {
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

// 「同一傳票」併入整張傳票條件後，更新 Gui 腳本的入口；這裡只檢查腳本，不執行 Gui。
// 舊選單斷言首次失敗：20261014-135637069-066eb7e965824f89bf2b8671156595f1。
test('GUI fixture uses visible compound actions instead of a retired scope selector', () => {
  const f = filterOperationFixture(); f.click(cardSelector(BLANK)); f.click(cardSelector(TRAILING));
  const source = fs.readFileSync(path.join(__dirname, '../harness/gui-driver/GuiSideMonthWorkflow.cs'), 'utf8');
  assert.doesNotMatch(source, /data-group-bind=matchScope|data-value-key=drCr|data-action=open-save|data-action=new-scenario/);
  assert.match(source, /data-add-compound/);
  assert.equal(!!f.$('[data-add-compound="voucher"]'), true);
  f.click('[data-add-compound="voucher"]');
  assert.equal(f.draft().groups[0].matchScope, 'row');
  assert.equal(f.draft().groups[0].conditions.length, 3);
  assert.equal(f.draft().groups[0].conditions[2].type, 'voucher');
});

test('layout keeps the visible current step in the navigation and GUI readback', () => {
  const f = remapAppFixture();
  f.store.resetWorkflow(); f.store.setProject({ projectId: 'created-synthetic', projectName: '合成建案' });
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
