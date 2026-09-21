'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '../../src/JET/JET/wwwroot/js');
const settle = () => new Promise(resolve => setImmediate(resolve));

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
  for (const file of ['state.js', 'ui-core.js', 'steps/validate-step.js']) {
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
    return { value: '', hidden: false, disabled: false, innerHTML: '', textContent: '', isConnected: true,
      classList: { toggle() {} },
      getAttribute: key => attributes[key] || null,
      addEventListener: (name, handler) => { events[name] = handler; },
      fire(name) { assert.ok(events[name], 'bound event: ' + name); return events[name]({ target: this }); },
      querySelector() { return null; }, querySelectorAll() { return []; }
    };
  }
  function render() {
    const controls = new Map();
    for (const name of ['add-taxonomy-category', 'reset-taxonomy', 'save-taxonomy']) {
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
      const block = element({ 'data-prescreen-key': key });
      block.body = element();
      block.querySelector = selector => selector === '.rule-detail__preview-body' ? block.body : null;
      return block;
    });
    const panel = element(); panel.hidden = true;
    panel.querySelectorAll = selector => selector === '[data-prescreen-key]' ? blocks : [];
    const toggle = element({ 'data-scope': 'p', 'data-idx': '5' });
    const container = element();
    container.querySelector = selector => selector === '[data-bind="account-taxonomy-card"]' ? card
      : selector === '[data-bind="rule-detail-p-5"]' ? panel : null;
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
  assert.match(f.render().container.innerHTML, /匯入總數無法核對/);
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
  f.saves[1].resolve({ revision: 2, categories: f.saves[1].payload.categories.map((c, i) => ({ ...c, categoryId: i ? 'saved-bank' : 'builtin.cash' })) });
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


test('report locations are selectable, escaped and retain missing-file status', () => {
  const f = fixture();
  const html = f.window.JetUi.reportArtifactListHtml([{
    artifactId: 'synthetic', fileName: 'case.xlsx', fullPath: 'C:\\Synthetic\\<case>\\case.xlsx',
    generatedUtc: '2026-09-17T00:00:00Z', bytes: 4, fileState: 'missing'
  }], '', { reveal: true });
  assert.match(html, /儲存位置：C:/);
  assert.match(html, /&lt;case&gt;/);
  assert.match(html, /overflow-wrap:anywhere;user-select:text/);
  assert.match(html, /檔案不存在，重新匯出即可/);
  assert.match(html, /disabled/);
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
  imports[1].resolve({rowCount:2,sourceColumn:'Employee ID',sourceRowCount:4,blankRowCount:1,duplicateRowCount:1}); await settle();
  assert.equal(f.store.getState().importState.authorizedPreparer.rowCount,2);
  assert.doesNotMatch(render().html(),/data-ap-column/);
  render().click('import-authorized-preparer'); await settle();
  render().click('cancel-authorized-preparer');
  assert.doesNotMatch(render().html(),/data-ap-column/);
  assert.equal(imports.length,2);
  render().click('clear-authorized-preparer'); clears[0].reject(new Error('synthetic remove failure')); await settle();
  assert.equal(f.store.getState().importState.authorizedPreparer.rowCount,2);
  render().click('clear-authorized-preparer'); clears[1].resolve({cleared:true}); await settle();
  assert.equal(f.store.getState().importState.authorizedPreparer,null);
  assert.equal(f.store.getState().lastRuns.prescreen,null);
  assert.doesNotMatch(render().html(),/data-action="clear-authorized-preparer"/);
});
