'use strict';
// 情境 24：連線層與共用原語（收尾四項第一項第 4 批，2026-10-13）。
//
// 審計員不會直接操作這一層，但每個步驟都會經過它：畫面重新載入時還在等的動作要明確失敗，不能一直轉圈；
// 後端的錯誤要帶著錯誤碼、追蹤編號、欄位與條件列位置回到畫面；客戶資料插進畫面時不能跳出 HTML 屬性；
// 晚到的舊回應不能蓋過新的結果；還原欄位配對草稿時兩份草稿一次換掉，已確認的配對不動。
//
// 這個檔在 vm 載入正式的 jet-api.js、ui-core.js、state.js 與第五步畫面，斷言送出的請求、Promise 的結果、
// Store 狀態與畫出來的節點，不比對原始碼字串。依據是使用者 2026-10-13 對第一項選的「照建議做」。
//
// 取代的 C# 方法（src/JET/tests/JET.Tests/Architecture/）：
//   FrontendEscapingTests.Esc_EscapesBothQuoteKinds_ForHtmlAttributeInterpolation（雙引號由第 7 批補齊，2026-10-14 第二、三波整併連同整檔刪除）
//   MappingMetadataMirrorTests.RestoreState_AtomicallyReplacesBothDraftsAndPreservesCommittedSnapshots（2026-10-14 第一波整併已刪除）
// 另外提供下列跨情境方法的連線層部分（其餘部分由別的情境補齊）：
//   FrontendHardeningContractTests.JetApi_RejectsPendingOnUnload_AndProjectLoadIsCancellable（取消屬情境 22；2026-10-14 第二、三波整併已刪除）
//   FrontendHardeningContractTests.LatestResponseGuard_IsSharedAcrossPreviewMappingAndFilterConsumers（各畫面的使用屬情境 6、13、21，
//     都已補齊，2026-10-14 第二、三波整併已刪除）
//   ScreenBehaviorReviewFrontendTests.FrontendTransportErrors_UseChineseInsteadOfEnglishOriginals（訊息面板屬情境 22、23；各部分已齊全，
//     原方法註解的第一次失敗收據在第 11 批補進 frontend-journey-operations，2026-10-14 第二、三波整併連同整檔刪除）
// ApprovedUxFrontendTests.CreateProjectFailure_UsesStructuredBackendFieldWithoutGuessingFromCodeOrMessage 的連線層部分在這裡，
// 畫面標欄位的部分由 frontend-journey-case-setup.test.cjs（情境 2）補齊，2026-10-14 第一波整併已刪除該方法。
// FilterTemplatesFrontendTests.InvalidScenarioDetails_AreMarkedOnTheRuleRow 已由第五步修正刪除，取代測試見
// artifacts/review/closing-four-items-20261013/impl-scenario-save.md 第 4 節；這裡的 details 測試只是額外守住連線層。
//
// 刻意不逐字比對的句子：jet-api.js 在後端沒附訊息、畫面重新載入、連線還沒就緒時的固定說明。
// 這些句子正在依 2026-10-13「一起整理」與拿掉「輸出支援日誌」的裁定改寫，這裡只比對意思：是中文、不含英文代碼。
// 全部資料都是程式生成的合成資料。
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { readWwwroot, plain, settle, usageJourneyFixture, savedG } = require('./support/frontend-fixtures.cjs');

// 已退場的英文原文（ScreenBehaviorReviewFrontendTests.FrontendTransportErrors_… 的 W19 清單，原樣搬來）。
const RETIRED_ENGLISH = ['JET bridge request failed.', 'JET host bridge is not available.', 'JET host bridge was unloaded'];
const HAN = /[一-鿿]/;

// 在 vm 載入正式 jet-api.js。假的 chrome.webview 記下送出的訊息並收下 message 處理函式；
// window.addEventListener 記下 pagehide 等監聽。ready: false 時沒有 chrome.webview。
function bridgeFixture({ ready = true } = {}) {
  const sent = [], windowListeners = {}, messageHandlers = [];
  let counter = 0;
  const window = {
    crypto: { randomUUID: () => 'req-' + (++counter) },
    addEventListener(type, fn) { (windowListeners[type] ||= []).push(fn); },
    console: { error() {} }
  };
  if (ready) {
    window.chrome = { webview: {
      postMessage: message => sent.push(message),
      addEventListener(type, fn) { if (type === 'message') messageHandlers.push(fn); }
    } };
  }
  vm.runInContext(readWwwroot('js/jet-api.js'), vm.createContext({ window }), { filename: 'jet-api.js' });
  const api = window.JetApi;
  function reply(message, asString = false) {
    assert.ok(messageHandlers.length > 0, 'jet-api.js subscribed to chrome.webview messages');
    for (const fn of messageHandlers) fn({ data: asString ? JSON.stringify(message) : message });
  }
  function pagehide() {
    assert.ok((windowListeners.pagehide || []).length > 0, 'jet-api.js listens for pagehide');
    for (const fn of windowListeners.pagehide) fn({ type: 'pagehide' });
  }
  // 讓 Promise 的結果可以同步讀：pending、fulfilled 或 rejected。
  function track(promise) {
    const box = { state: 'pending', value: undefined, requestId: promise.requestId };
    promise.then(value => { box.state = 'fulfilled'; box.value = value; }, error => { box.state = 'rejected'; box.value = error; });
    return box;
  }
  return { window, api, sent, reply, pagehide, track, messageHandlers, windowListeners };
}

test('transport: a reload while project.load is pending rejects it with bridge_unloaded and ignores the late reply', async () => {
  const f = bridgeFixture();
  const load = f.track(f.api.projectLoad({ projectId: 'synthetic-case' }));
  const list = f.track(f.api.projectList({}));
  // 送出形狀精確：requestId、action 與 payload，沒有別的鍵。
  assert.deepEqual(plain(f.sent), [
    { requestId: 'req-1', action: 'project.load', payload: { projectId: 'synthetic-case' } },
    { requestId: 'req-2', action: 'project.list', payload: {} }
  ]);
  assert.equal(load.requestId, 'req-1', 'the promise exposes its requestId for operation.cancel');
  await settle();
  assert.equal(load.state, 'pending');

  f.pagehide(); await settle();
  for (const box of [load, list]) {
    assert.equal(box.state, 'rejected', 'every pending request fails instead of spinning forever');
    assert.equal(box.value.code, 'bridge_unloaded');
    assert.match(box.value.message, HAN, 'the reload explanation is Chinese');
    for (const english of RETIRED_ENGLISH) assert.ok(!box.value.message.includes(english));
  }

  // 同一個 requestId 晚到的回應被忽略：不改變已經失敗的結果，也不丟例外。
  f.reply({ requestId: 'req-1', ok: true, data: { project: { projectId: 'synthetic-case' } } });
  f.reply({ requestId: 'req-2', ok: false, error: { code: 'late', message: '晚到' } });
  await settle();
  assert.equal(load.state, 'rejected'); assert.equal(load.value.code, 'bridge_unloaded');
  assert.equal(list.value.code, 'bridge_unloaded');

  // 重新載入後新送的請求照常運作。
  const next = f.track(f.api.projectListLocal());
  assert.equal(f.sent.at(-1).action, 'project.listLocal');
  f.reply({ requestId: next.requestId, ok: true, data: { projects: [] } }); await settle();
  assert.equal(next.state, 'fulfilled'); assert.deepEqual(plain(next.value), { projects: [] });
});

test('transport: a reload with nothing pending is harmless and replies are matched only by their own requestId', async () => {
  const f = bridgeFixture();
  f.pagehide();
  const a = f.track(f.api.projectLoad({ projectId: 'a' })), b = f.track(f.api.projectLoad({ projectId: 'b' }));
  // 不認得的 requestId、沒有 requestId、壞掉的 JSON 字串都不影響等待中的請求。
  f.reply({ requestId: 'unknown', ok: true, data: {} });
  f.reply({ ok: true, data: {} });
  for (const fn of f.messageHandlers) fn({ data: '{not json' });
  await settle();
  assert.equal(a.state, 'pending'); assert.equal(b.state, 'pending');
  f.reply({ requestId: b.requestId, ok: true, data: { which: 'b' } }, true); await settle();
  assert.equal(b.state, 'fulfilled'); assert.deepEqual(plain(b.value), { which: 'b' });
  assert.equal(a.state, 'pending', 'a reply for b does not settle a');
  // 回應只處理一次：同一個 requestId 再回一次不會改變結果。
  f.reply({ requestId: a.requestId, ok: true, data: { which: 'a' } });
  f.reply({ requestId: a.requestId, ok: false, error: { code: 'again', message: '重複' } }); await settle();
  assert.equal(a.state, 'fulfilled'); assert.deepEqual(plain(a.value), { which: 'a' });
});

test('transport: a backend error carries code, correlationId, field and details onto the Error unchanged', async () => {
  const f = bridgeFixture();
  const details = [{ group: 1, rule: 2, message: '合成條件原因' }, { group: 2, rule: 1, message: '第二個合成原因' }];
  const box = f.track(f.api.filterPreview({ scenario: { groups: [] } }));
  f.reply({ requestId: box.requestId, ok: false, correlationId: 'corr-synthetic-1',
    error: { code: 'invalid_scenario', message: '合成的後端訊息', field: 'caseName', details } });
  await settle();
  assert.equal(box.state, 'rejected');
  const err = box.value;
  assert.ok(err instanceof Error || typeof err.message === 'string');
  assert.equal(err.message, '合成的後端訊息', 'a backend message is shown as sent');
  assert.equal(err.code, 'invalid_scenario');
  assert.equal(err.correlationId, 'corr-synthetic-1');
  assert.equal(err.field, 'caseName');
  assert.deepEqual(plain(err.details), details, 'details reach the step unchanged so the rule row can be marked');
});

test('transport: missing or malformed error metadata becomes null instead of a guess', async () => {
  const f = bridgeFixture();
  const cases = [
    [{ code: 'invalid_payload', message: '合成訊息' }, undefined],
    [{ code: 'invalid_payload', message: '合成訊息', field: 42, details: 'not-an-array' }, 12345],
    [{ code: 'invalid_payload', message: '合成訊息', field: null, details: { group: 1 } }, null]
  ];
  for (const [error, correlationId] of cases) {
    const box = f.track(f.api.projectCreate({ caseName: '合成' }));
    const message = { requestId: box.requestId, ok: false, error };
    if (correlationId !== undefined) message.correlationId = correlationId;
    f.reply(message); await settle();
    assert.equal(box.state, 'rejected');
    assert.equal(box.value.code, 'invalid_payload');
    assert.equal(box.value.field, null, 'a non-string field is not used to mark an input');
    assert.equal(box.value.details, null, 'non-array details are dropped');
    assert.equal(box.value.correlationId, null, 'a non-string correlationId is dropped');
  }
});

test('transport: without a backend message the Error explains in Chinese and never shows the code or English originals', async () => {
  const f = bridgeFixture();
  for (const error of [{ code: 'synthetic_internal_code' }, undefined, { code: 'synthetic_internal_code', message: '' }]) {
    const box = f.track(f.api.projectLoad({ projectId: 'x' }));
    const message = { requestId: box.requestId, ok: false };
    if (error) message.error = error;
    f.reply(message); await settle();
    assert.equal(box.state, 'rejected');
    // 只比對意思：句子是中文，不含錯誤碼與已退場的英文原文。句子本身正在依 2026-10-13 的裁定改寫，不逐字鎖住。
    assert.match(box.value.message, HAN);
    assert.ok(!box.value.message.includes('synthetic_internal_code'), 'the wire code is not shown as the message');
    for (const english of RETIRED_ENGLISH) assert.ok(!box.value.message.includes(english));
    assert.equal(box.value.code, error && error.code ? error.code : null);
  }
});

test('transport: before the host bridge is ready nothing is sent and the rejection is Chinese', async () => {
  const f = bridgeFixture({ ready: false });
  assert.equal(f.api.isReady(), false);
  const box = f.track(f.api.projectListLocal()); await settle();
  assert.equal(box.state, 'rejected');
  assert.match(box.value.message, HAN);
  for (const english of RETIRED_ENGLISH) assert.ok(!box.value.message.includes(english));
  assert.equal(f.sent.length, 0);
});

// ---- 共用原語：Ui.esc、Ui.createLatestResponseGuard、Store.restoreMappingDrafts ----

function coreFixture() {
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  // 和瀏覽器一樣：textContent 寫入後 innerHTML 只跳脫 & < >，不跳脫引號，所以引號一定要由 Ui.esc 自己處理。
  const document = { createElement() { return { textContent: '', get innerHTML() {
    return String(this.textContent).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); } }; } };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js']) vm.runInContext(readWwwroot('js/' + file), context, { filename: file });
  return { window, ui: window.JetUi, store: window.JetStore };
}

test('primitives: Ui.esc escapes both quote kinds and the HTML specials, and null becomes empty', () => {
  const { ui } = coreFixture();
  assert.equal(ui.esc('a"b\'c<d>&'), 'a&quot;b&#39;c&lt;d&gt;&amp;');
  assert.equal(ui.esc(null), '');
  assert.equal(ui.esc(undefined), '');
  assert.equal(ui.esc(0), '0');
  assert.equal(ui.esc('&quot;'), '&amp;quot;', 'already-escaped text is escaped again, not passed through');
});

test('primitives: a saved scenario name with quotes stays inside its HTML attribute on the real step five screen', () => {
  // 假 DOM 用瀏覽器的序列化（文字不把引號轉成 &quot;），屬性值照 HTML 解析；Ui.esc 漏掉雙引號時屬性會被截斷。
  const name = '合成"情境\'<甲>&乙';
  const f = usageJourneyFixture('filter', { serialization: 'browser' });
  f.store.setSavedScenarios([savedG(name)]);
  f.draw('filter');
  const summary = f.$('details.filter-saved-actions summary');
  assert.ok(summary, 'saved scenario actions rendered');
  assert.equal(summary.getAttribute('aria-label'), '操作：' + name, 'the attribute holds the whole name');
  assert.equal(f.$('.saved-scenario__name').textContent, '1. ' + name);
  assert.deepEqual(Object.keys(summary.attrs), ['aria-label'], 'no attribute was injected by the name');
});

test('primitives: a latest-response guard accepts only the newest issue and nothing after invalidate', () => {
  const { ui } = coreFixture();
  const guard = ui.createLatestResponseGuard(), other = ui.createLatestResponseGuard();
  const first = guard.issue();
  assert.equal(first(), true, 'the only request issued is current');
  const second = guard.issue();
  assert.equal(first(), false, 'a newer issue replaces the older one');
  assert.equal(second(), true);
  assert.equal(other.issue()(), true, 'guards do not share a revision');
  assert.equal(second(), true, 'another guard does not invalidate this one');
  guard.invalidate();
  assert.equal(second(), false, 'invalidate (case switch, reimport) rejects every outstanding response');
  let projectId = 'case-a';
  const scoped = guard.issue(() => projectId === 'case-a');
  assert.equal(scoped(), true);
  projectId = 'case-b';
  assert.equal(scoped(), false, 'stillCurrent can veto a response even when it is the newest issue');
  // 每次呼叫都重新判斷，不快取。
  projectId = 'case-a';
  assert.equal(scoped(), true);
});

test('primitives: restoreMappingDrafts swaps both drafts in one change and keeps the committed snapshots', () => {
  // Store.restoreMappingDrafts 目前沒有呼叫端（2026-09-22 撤下還原畫面，寫的是「直到報告還原流程談定」）；
  // 函式保留期間由這個 Store 層級的行為測試守住，刪不刪函式由使用者決定（plan-item1-tests.md 第 4 節、第 10 節第 1 點）。
  const { store } = coreFixture();
  const state = store.getState();
  const glCommitted = { mapping: { docNum: 'SynDoc' }, amountMode: 'dual', options: { rdeFields: [] } };
  const tbCommitted = { mapping: { account: 'SynAcct' }, changeMode: 'debitCredit' };
  state.mapping.gl.committed = glCommitted; state.mapping.tb.committed = tbCommitted;
  state.mapping.gl.draft = { docNum: 'OldDoc' }; state.mapping.tb.draft = { account: 'OldAcct' };
  const committedBefore = plain({ gl: glCommitted, tb: tbCommitted });
  const versionBefore = state.contentVersion;
  const seen = [];
  store.subscribe(next => seen.push({ gl: plain(next.mapping.gl.draft), tb: plain(next.mapping.tb.draft), version: next.contentVersion }));

  const rde = [{ fieldId: 'rde.synthetic1', sourceColumn: 'SynNote', label: '合成附註', valueType: 'text' }];
  const bundle = {
    gl: { mapping: { docNum: 'NewDoc', amount: 'SynAmount' }, amountMode: 'signed', approvalDateMode: 'column',
      postingStatusPolicy: { column: 'SynStatus', postedValues: ['P'] }, manualAutoPolicy: { manualValues: ['M'], automaticValues: ['A'] }, rdeFields: rde },
    tb: { mapping: { account: 'NewAcct', change: 'SynChange' }, changeMode: 'netChange' }
  };
  store.restoreMappingDrafts(bundle);

  assert.equal(seen.length, 1, 'one notification: the screen never sees one draft restored and the other old');
  assert.deepEqual(seen[0].gl, { docNum: 'NewDoc', amount: 'SynAmount' });
  assert.deepEqual(seen[0].tb, { account: 'NewAcct', change: 'SynChange' });
  assert.equal(seen[0].version, versionBefore + 1);
  assert.equal(state.mapping.gl.amountMode, 'signed');
  assert.equal(state.mapping.tb.changeMode, 'netChange');
  assert.deepEqual(plain(state.mapping.gl.options), {
    approvalDateMode: 'column', postingStatusPolicy: { column: 'SynStatus', postedValues: ['P'] },
    manualAutoPolicy: { manualValues: ['M'], automaticValues: ['A'] }, rdeFields: rde });
  assert.equal(state.mapping.gl.committed, glCommitted, 'the committed GL snapshot object is kept');
  assert.equal(state.mapping.tb.committed, tbCommitted, 'the committed TB snapshot object is kept');
  assert.deepEqual(plain({ gl: state.mapping.gl.committed, tb: state.mapping.tb.committed }), committedBefore,
    'a restored draft does not pose as the effective mapping');
  // 草稿是複本：之後改 bundle 不會改到畫面的草稿。
  bundle.gl.mapping.docNum = 'Mutated'; bundle.tb.mapping.account = 'Mutated'; rde.push({ fieldId: 'rde.extra' });
  assert.equal(state.mapping.gl.draft.docNum, 'NewDoc');
  assert.equal(state.mapping.tb.draft.account, 'NewAcct');
  assert.equal(state.mapping.gl.options.rdeFields.length, 1);

  // 舊版報告沒有 v2 選項時，用固定的預設值，不沿用目前草稿的設定。
  store.restoreMappingDrafts({ gl: { mapping: {}, amountMode: 'dual' }, tb: { mapping: {}, changeMode: 'debitCredit' } });
  assert.deepEqual(plain(state.mapping.gl.options), {
    approvalDateMode: 'unmapped', postingStatusPolicy: null,
    manualAutoPolicy: { manualValues: ['1'], automaticValues: ['0'] }, rdeFields: [] });
  assert.equal(seen.length, 2);
});
