'use strict';
// 前端情境測試共用夾具的自我測試（收尾四項第一項第 0 批，2026-10-13）。
// tools/tests/support/frontend-fixtures.cjs 是後面各批情境測試的基礎；這裡確認第 0 批補上的假 DOM 能力、
// 新夾具與改壞檢查工具本身照設計運作。夾具壞了，情境測試的通過或失敗都不可信，所以這些測試和情境測試走同一條 FrontendMapping 路線。
// 全部使用程式生成的合成資料。
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { spawnSync } = require('node:child_process');
const support = require('./support/frontend-fixtures.cjs');
const mutation = require('./support/mutate-frontend.cjs');
const { batch9TreeDom, exposeAt, settle, plain } = support;

test('fake DOM matches unquoted, single-quoted and double-quoted attribute selectors without matching everything', () => {
  const dom = batch9TreeDom();
  dom.content.innerHTML = '<div data-bind="busy-label">處理中</div><span data-bind="busy-elapsed" hidden></span><p data-kind=\'note\'>合成</p>';
  const elapsed = dom.content.querySelector('[data-bind=busy-elapsed]');
  assert.ok(elapsed, 'unquoted selector finds its element');
  assert.equal(elapsed.tagName, 'SPAN');
  assert.ok(dom.content.querySelector('[data-bind="busy-elapsed"]') === elapsed, 'quoted and unquoted forms find the same node');
  assert.ok(dom.content.querySelector("[data-kind='note']") === dom.content.querySelector('p'));
  // wf 的版本把不帶引號的選擇器當成沒有條件，會回傳第一個元素；這裡必須找不到。
  assert.equal(dom.content.querySelector('[data-bind=missing]'), null);
  assert.equal(dom.document.querySelector('[data-bind=busy-label]').textContent, '處理中');
  // :not(...) 不必開 dialogFocus 也照規則判斷，「>」只認直接子元素。
  dom.content.innerHTML = '<section class="outer"><div class="row"><input type="hidden" name="h"><input type="text" name="t"></div></section>';
  assert.deepEqual(dom.content.querySelectorAll('input:not([type="hidden"])').map(node => node.getAttribute('name')), ['t']);
  assert.equal(dom.content.querySelectorAll('.outer > input').length, 0);
  assert.equal(dom.content.querySelectorAll('.outer > .row > input').length, 2);
  assert.equal(dom.content.querySelectorAll('.outer input').length, 2);
});

test('fake DOM exposes attributes, id and select() the way app.js reads them', () => {
  const dom = batch9TreeDom();
  dom.content.innerHTML = '<button type="button" id="synthetic-id" data-focus-mapping-field="postDate" aria-label="合成">合成</button><textarea>abc</textarea>';
  const button = dom.content.querySelector('button');
  assert.deepEqual(plain(button.attributes), [
    { name: 'type', value: 'button' }, { name: 'id', value: 'synthetic-id' },
    { name: 'data-focus-mapping-field', value: 'postDate' }, { name: 'aria-label', value: '合成' }]);
  assert.equal(button.id, 'synthetic-id');
  button.id = 'renamed';
  assert.equal(button.getAttribute('id'), 'renamed');
  const area = dom.content.querySelector('textarea');
  area.select();
  assert.deepEqual([area.selectionStart, area.selectionEnd], [0, 3]);
});

test('fake DOM events bubble to ancestors and document, stop where asked, and fire() keeps the old behaviour unless bubbles is set', () => {
  const dom = batch9TreeDom();
  dom.content.innerHTML = '<section class="outer"><div class="inner"><button type="button">合成</button></div></section>';
  const button = dom.content.querySelector('button'), inner = dom.content.querySelector('.inner'), outer = dom.content.querySelector('.outer');
  const seen = [];
  const capture = event => seen.push('document-capture:' + event.target.tagName);
  dom.document.addEventListener('click', capture, true);
  dom.document.addEventListener('click', event => seen.push('document:' + (event.currentTarget === dom.document)));
  inner.addEventListener('click', event => seen.push('inner:' + event.target.tagName + ':' + (event.currentTarget === inner)));
  outer.addEventListener('click', () => seen.push('outer'));
  button.dispatch('click');
  assert.deepEqual(seen, ['document-capture:BUTTON', 'inner:BUTTON:true', 'outer', 'document:true']);

  seen.length = 0;
  inner.addEventListener('click', event => event.stopPropagation());
  button.dispatch('click');
  assert.deepEqual(seen, ['document-capture:BUTTON', 'inner:BUTTON:true'], 'stopPropagation finishes the current node and stops there');

  seen.length = 0;
  dom.document.removeEventListener('click', capture, true);
  button.fire('click');
  assert.deepEqual(seen, [], 'without the bubbles option fire() only calls the target listeners, as in wf');

  const bubbling = batch9TreeDom({ bubbles: true });
  bubbling.content.innerHTML = '<div class="inner"><button type="button">合成</button></div>';
  const calls = [];
  bubbling.content.querySelector('.inner').addEventListener('click', event => calls.push(event.target.tagName));
  bubbling.document.addEventListener('keydown', event => calls.push('key:' + event.key));
  bubbling.content.querySelector('button').fire('click');
  bubbling.content.querySelector('button').focus();
  bubbling.document.dispatch('keydown', { key: 'Escape' });
  assert.deepEqual(calls, ['BUTTON', 'key:Escape']);
  assert.equal(bubbling.document.listenerCount('keydown'), 1);

  // 沒有掛在畫面根節點下的元素不會傳到 document。
  const detached = bubbling.element('button');
  detached.dispatch('keydown', { key: 'Enter' });
  assert.deepEqual(calls, ['BUTTON', 'key:Escape']);
});

test('browser serialization keeps quotes in text so Ui.esc must escape them itself', () => {
  function esc(options) {
    const dom = batch9TreeDom(options);
    const context = vm.createContext({ window: { setTimeout() {}, clearTimeout() {} }, document: dom.document });
    for (const file of ['state.js', 'ui-core.js']) vm.runInContext(fs.readFileSync(path.join(support.root, file), 'utf8'), context, { filename: file });
    return { esc: context.window.JetUi.esc, dom };
  }
  const browser = esc({ serialization: 'browser' });
  const text = browser.dom.element('div');
  text.textContent = 'a"b\'c<d>&';
  assert.equal(text.innerHTML, 'a"b\'c&lt;d&gt;&amp;', 'text keeps both quote kinds, like a browser');
  assert.equal(browser.esc('a"b\'c<d>&'), 'a&quot;b&#39;c&lt;d&gt;&amp;');
  const legacy = esc({});
  const legacyText = legacy.dom.element('div');
  legacyText.textContent = 'a"b';
  assert.equal(legacyText.innerHTML, 'a&quot;b', 'the default keeps the wf serialization');
  // 兩種模式的屬性值都會跳脫引號。
  const holder = browser.dom.element('div');
  holder.innerHTML = '<span title="x&quot;y"></span>';
  assert.equal(holder.innerHTML, '<span title="x&quot;y"></span>');
});

test('exposeAt inserts test code at a unique production anchor and fails loudly when the anchor moves', () => {
  const source = 'start();\n  if (ready) {\n  }\n';
  assert.equal(exposeAt(source, '  if (ready) {', '  window.seam = 1;\n'), 'start();\n  window.seam = 1;\n  if (ready) {\n  }\n');
  assert.equal(exposeAt(source, 'start();', ' window.after = 1;', { after: true }), 'start(); window.after = 1;\n  if (ready) {\n  }\n');
  assert.throws(() => exposeAt(source, '  if (missing) {', 'x'), /anchor not found/);
  assert.throws(() => exposeAt('a();a();', 'a();', 'x'), /exactly once/);
  assert.throws(() => exposeAt(source, '', 'x'), /must not be empty/);
  assert.throws(() => support.sliceBetween(source, 'start();', 'never'), /end anchor not found/);
});

test('timer queue, clock, chart and media doubles run only when the test says so', () => {
  const timers = support.timerQueue(), order = [];
  timers.setTimeout(() => order.push('late'), 30);
  const cancelled = timers.setTimeout(() => order.push('cancelled'), 0);
  timers.setTimeout(() => { order.push('first'); timers.setTimeout(() => order.push('chained'), 0); }, 0);
  timers.clearTimeout(cancelled);
  assert.deepEqual(order, []);
  assert.equal(timers.run(), 3);
  assert.deepEqual(order, ['first', 'chained', 'late']);
  assert.throws(() => { const loop = support.timerQueue(); const again = () => loop.setTimeout(again, 0); again(); loop.run(5); }, /keeps rescheduling/);

  const clock = support.fakeClock(100);
  assert.equal(clock.performance.now(), 100);
  clock.advance(2500);
  assert.equal(clock.performance.now(), 2600);

  const charts = support.fakeCharts(), window = {};
  charts.install(window);
  const observer = new window.ResizeObserver(() => {});
  const chart = window.echarts.init({ id: 'synthetic' }, null, { renderer: 'svg' });
  chart.setOption({ animation: false }, true);
  observer.observe(chart.element);
  chart.dispose();
  assert.deepEqual(plain(chart.options), [{ option: { animation: false }, notMerge: true }]);
  assert.equal(chart.isDisposed(), true);
  assert.equal(charts.live().length, 0);
  assert.equal(charts.observers[0].observed.length, 1);

  const media = support.fakeMatchMedia(false), list = media.matchMedia('(max-width: 1360px)'), changes = [];
  list.addEventListener('change', event => changes.push(event.matches));
  list.change(true);
  assert.deepEqual(changes, [true]);
  assert.deepEqual(media.queries(), ['(max-width: 1360px)']);
  assert.equal(support.cssEscape('a"b c'), 'a\\"b\\ c');
  assert.equal(support.cssEscape('1x'), '\\31 x');
});

test('usage journey options queue timers, record intervals and redraw only when contentVersion changes', () => {
  const f = support.usageJourneyFixture('filter', { redraw: true, queueTimers: true, clock: true });
  assert.ok(f.timers && f.clock, 'timer queue and clock are exposed');
  assert.equal(f.window.performance.now(), f.clock.now());
  const drawn = f.drawCount();
  f.store.addMessage('合成訊息', 'info');
  assert.equal(f.drawCount(), drawn, 'a message alone does not change contentVersion and does not redraw');
  f.store.setSavedScenarios([support.savedG('合成情境')]);
  assert.equal(f.drawCount(), drawn + 1, 'a data change redraws the step once');
  const id = f.window.setInterval(() => {}, 45000);
  f.window.clearInterval(id);
  assert.deepEqual(f.intervals.map(entry => [entry.ms, entry.cleared]), [[45000, true]]);
  const plainFixture = support.usageJourneyFixture('filter');
  assert.equal(plainFixture.timers, null);
  assert.equal(plainFixture.window.performance, undefined, 'without the clock option the window has no performance, as in wf');
});

test('remap app fixture renders the real header buttons and binds the project folder button from app.js', async () => {
  const f = support.remapAppFixture();
  const folder = f.$('[data-action="open-project-folder"]');
  assert.ok(folder, 'the header button comes from index.html');
  assert.equal(folder.hidden, true, 'index.html starts it hidden');
  f.store.setView('picker'); f.app.render();
  assert.equal(folder.hidden, true);
  f.store.setView('workflow'); f.app.render();
  assert.equal(folder.hidden, false);
  assert.ok(f.$('[data-action="app-exit"]') && f.$('[data-action="app-back-picker"]'), 'the header still has exit and back buttons after render()');
  f.click('[data-action="open-project-folder"]');
  await settle();
  assert.equal(f.requests.filter(request => request.action === 'hostOpenFolder').length, 1, 'the extended slice binds the real click handler');
  f.request('hostOpenFolder').resolve({}); await settle();
});

test('overview fixture can install chart doubles before overview-bi.js loads', () => {
  const plainOverview = support.overviewDialogFixture();
  assert.equal(plainOverview.charts, undefined, 'the default stays identical to wf');
  const f = support.overviewDialogFixture({ charts: true });
  assert.equal(f.charts.observers.length, 1, 'overview-bi.js created its ResizeObserver from the installed double');
  assert.deepEqual(f.windowListeners.map(entry => entry.type), ['resize']);
  assert.ok(f.timers, 'setTimeout calls are queued for the test');
});

test('data preview fixture runs the real panel: loading on entry and the other-data menu closing from a document click', async () => {
  const f = support.dataPreviewFixture();
  assert.equal(f.requests.length, 0, 'nothing loads before a case is open');
  f.enterWorkflow(); await settle();
  // 2026-10-16：所有來源預覽改為 200 筆有界分頁，首次請求明確送出 offset 0。
  assert.deepEqual(plain(f.requests.map(request => [request.action, request.payload])), [['queryDataPreview', { dataset: 'pbcGl', limit: 200, offset: 0 }]]);
  assert.equal(f.body().getAttribute('aria-busy'), 'true');
  f.request('queryDataPreview').resolve({ columns: ['Doc'], rows: [['JV-1']], totalCount: 1 }); await settle();
  assert.notEqual(f.body().getAttribute('aria-busy'), 'true');
  assert.match(f.body().textContent, /JV-1/);
  const toggle = () => f.$('[data-more-toggle]');
  assert.ok(toggle(), 'the other-data toggle is rendered');
  toggle().fire('click');
  assert.equal(toggle().getAttribute('aria-expanded'), 'true', 'the toggle click bubbles to the delegated tab handler');
  f.$('.data-preview__head').fire('click');
  assert.equal(toggle().getAttribute('aria-expanded'), 'false', 'a click elsewhere reaches the document listener and closes the menu');
});

test('mapping render fixture goes through app.js renderContent and restores focus on the rebuilt control', () => {
  const f = support.mappingRenderFixture();
  f.store.setImportResult('gl', { batchId: 'gl-synthetic', columns: ['Doc', 'Date', 'Code', 'Name', 'Memo', 'Amount'], rowCount: 2 });
  f.store.setMappingUiMode('classic');
  assert.ok(f.body(), 'the mapping step is drawn inside the current step body');
  const select = f.body().querySelector('select[data-mapping-key="docNum"]');
  assert.ok(select);
  select.focus(); select.value = 'Doc';
  const before = f.contentVersion(), renders = f.renderCount();
  select.dispatch('change');
  assert.equal(f.contentVersion(), before + 1);
  assert.ok(f.renderCount() > renders);
  const rebuilt = f.body().querySelector('select[data-mapping-key="docNum"]');
  assert.ok(rebuilt !== select, 'the step was rebuilt');
  assert.ok(f.dom.document.activeElement === rebuilt, 'focus moved to the rebuilt select');
  assert.equal(f.store.getState().mapping.gl.draft.docNum, 'Doc');
});

test('message panel fixture binds the real copy button and falls back to execCommand when the clipboard refuses', async () => {
  for (const clipboard of ['accept', 'reject']) {
    const f = support.messagePanelFixture({ clipboard });
    assert.ok(f.copyButton(), 'the copy button comes from index.html');
    f.copyButton().fire('click'); await settle();
    f.request('logRecent').resolve({ messages: [{ occurredUtc: '2026-01-01T00:00:00Z', level: 'info', text: '合成訊息' }] }); await settle(); await settle();
    const copied = clipboard === 'accept' ? f.clipboardWrites : f.copyCommands.map(entry => entry.value);
    assert.equal(copied.length, 1, clipboard + ': copied once');
    assert.match(copied[0], /合成訊息/);
    if (clipboard === 'reject') assert.equal(f.copyCommands[0].command, 'copy');
  }
});

test('JET_FRONTEND_TEST_WWWROOT switches the fixtures to a copy and announces it on stderr', () => {
  const script = "process.stdout.write(require(" + JSON.stringify(path.join(__dirname, 'support', 'frontend-fixtures.cjs')) + ").wwwroot)";
  const env = { ...process.env };
  delete env.JET_FRONTEND_TEST_WWWROOT;
  const normal = spawnSync(process.execPath, ['-e', script], { env, encoding: 'utf8' });
  assert.equal(normal.status, 0, normal.stderr);
  assert.equal(normal.stdout, path.resolve(__dirname, '../../src/JET/JET/wwwroot'));
  assert.equal(normal.stderr, '');
  const target = path.resolve(__dirname, '../../src/JET/JET/wwwroot');
  const switched = spawnSync(process.execPath, ['-e', script], { env: { ...env, JET_FRONTEND_TEST_WWWROOT: target }, encoding: 'utf8' });
  assert.equal(switched.status, 0, switched.stderr);
  assert.equal(switched.stdout, target);
  assert.match(switched.stderr, /JET_FRONTEND_TEST_WWWROOT=/);
  const missing = spawnSync(process.execPath, ['-e', script], { env: { ...env, JET_FRONTEND_TEST_WWWROOT: path.join(__dirname, 'no-such-wwwroot') }, encoding: 'utf8' });
  assert.notEqual(missing.status, 0, 'a wrong copy path stops the run instead of silently using the working tree');
});

test('fake DOM elements keep links and computed properties out of what a failure report walks through', () => {
  // 2026-10-13：測試失敗時，Node 的報表會逐項展開失敗值的可列舉內容。元件的父子連結、事件監聽與每次讀取都重新計算的屬性
  // 必須不可列舉，否則失敗值是元件時會沿著整個畫面展開而停不下來；這些屬性照樣能讀寫。
  const dom = batch9TreeDom();
  dom.content.innerHTML = '<section class="scenario-set"><div class="rule-row"><select data-rule-bind="field"><option value="a">A</option></select><button type="button">移除</button></div></section>';
  const created = dom.document.createElement('div');
  for (const node of [dom.content, dom.content.querySelector('.rule-row'), dom.content.querySelector('select'), created]) {
    const listed = Object.keys(node);
    for (const hidden of ['parentElement', 'children', 'handlers', 'innerHTML', 'textContent', 'attributes', 'dataset', 'rows', 'elements', 'content']) {
      assert.ok(!listed.includes(hidden), node.tagName + ' lists ' + hidden);
    }
  }
  const row = dom.content.querySelector('.rule-row');
  assert.equal(row.parentElement.className, 'scenario-set', 'links still read');
  assert.equal(row.children.length, 2);
  row.addEventListener('click', () => { row.clicked = true; });
  row.fire('click');
  assert.equal(row.clicked, true, 'listeners still run');
  row.textContent = '已改';
  assert.equal(row.textContent, '已改', 'computed properties still write and read');
});

// 第 11 批（2026-10-14）：parentNode 與 removeChild 改成一律提供，第五步欄位提示（setFieldError 經 input.parentNode）才看得到。
test('fake DOM elements have parentNode and removeChild like the browser, without listing the link', () => {
  const dom = batch9TreeDom();
  dom.content.innerHTML = '<div class="form__row"><input name="scenario-name"></div>';
  const row = dom.content.querySelector('.form__row'), input = dom.content.querySelector('input');
  assert.ok(input.parentNode === row, 'parentNode is the containing row');
  assert.ok(row.parentNode === dom.content);
  assert.equal(dom.content.parentNode, null, 'the screen root has no parent');
  assert.equal(dom.document.createElement('span').parentNode, null, 'a new element has no parent until appended');
  assert.ok(!Object.keys(input).includes('parentNode'), 'parentNode stays out of failure reports');
  const note = dom.document.createElement('span');
  row.appendChild(note);
  assert.ok(note.parentNode === row);
  assert.ok(row.removeChild(note) === note, 'removeChild returns the removed node');
  assert.equal(note.parentNode, null);
  assert.equal(row.children.length, 1);
});

test('flush waits several settle rounds and noDunderKeys rejects payloads carrying internal keys', async () => {
  let rounds = 0;
  const tick = () => { rounds++; if (rounds < 10) setImmediate(tick); };
  setImmediate(tick);
  await support.flush();
  assert.ok(rounds >= 5 && rounds <= 7, 'six settle rounds by default: ' + rounds);
  assert.equal(support.noDunderKeys({ name: '合成', nested: [{ value: 1 }] }), true);
  assert.equal(support.noDunderKeys({ name: '合成', nested: [{ __draftId: 'x' }] }), false);
});

// 第 11 批（2026-10-14）：fullAppFixture 與 bootFullApp 從 frontend-journey-picker、-export、-operations 收進模組。
test('full app fixture runs init() through the real jet-api.js and boot stops at the local case list', async () => {
  const f = await support.bootFullApp([{ projectId: '合成甲案', entityName: '合成客戶甲', databaseProvider: 'sqlite' }]);
  assert.deepEqual(f.posted.slice(0, 4).map(request => request.action), ['system.ping', 'system.databaseInfo', 'system.whoAmI', 'project.listLocal']);
  assert.equal(f.sent('project.listLocal').length, 1, 'the first list read is the local one');
  assert.equal(f.sent('project.listOnline').length, 0);
  assert.equal(f.state().view, 'picker');
  assert.deepEqual(f.dom.content.querySelectorAll('[data-motion-list="local"] .project-row__title').map(node => node.textContent), ['合成甲案']);
  assert.ok(f.posted.every(request => typeof request.requestId === 'string'), 'requests go through jet-api.js with request ids');
});

test('resource guard stops a test process that keeps growing or keeps running, and leaves normal runs alone', () => {
  const guardPath = path.join(__dirname, 'support', 'resource-guard.cjs');
  const { MARKER } = require('./support/resource-guard.cjs');
  const env = { ...process.env };
  delete env.JET_TEST_MAX_RSS_MB;
  delete env.JET_TEST_MAX_SECONDS;
  const run = (body, limits) => spawnSync(process.execPath, ['-e', 'require(' + JSON.stringify(guardPath) + ');\n' + body],
    { env: { ...env, ...limits }, encoding: 'utf8', timeout: 30000 });
  // 記憶體：每次配置 32 MB，到 768 MB 停住 3 秒。上限 256 MB 時必須在途中被停掉；合成程序自己有天花板，保護失效也不會失控。
  const growing = run("const keep = []; (function step() { if (keep.length * 32 >= 768) { setTimeout(() => console.log('survived'), 3000); return; }" +
    ' keep.push(Buffer.alloc(32 * 1024 * 1024, 1)); setImmediate(step); })();', { JET_TEST_MAX_RSS_MB: '256' });
  assert.notEqual(growing.status, 0);
  assert.ok(growing.stderr.includes(MARKER + ' memory'), growing.stderr);
  assert.doesNotMatch(growing.stdout, /survived/);
  // 時間：主執行緒卡在同步迴圈時，監看執行緒照樣把程序停掉，不必等外層的逾時。
  const stuck = run('while (true) {}', { JET_TEST_MAX_SECONDS: '1' });
  assert.equal(stuck.error, undefined, 'the guard, not the outer spawn timeout, stopped the process');
  assert.notEqual(stuck.status, 0);
  assert.ok(stuck.stderr.includes(MARKER + ' running for'), stuck.stderr);
  // 正常結束的程序不受影響，監看執行緒也不會拖住結束。
  const normal = run("console.log('done')", {});
  assert.equal(normal.status, 0, normal.stderr);
  assert.equal(normal.stdout.trim(), 'done');
  assert.equal(normal.stderr, '');
  // 上限寫錯時直接停下，不默默改用預設值。
  const wrong = run("console.log('ran')", { JET_TEST_MAX_RSS_MB: 'lots' });
  assert.notEqual(wrong.status, 0);
  assert.match(wrong.stderr, /JET_TEST_MAX_RSS_MB must be a positive number/);
  assert.doesNotMatch(wrong.stdout, /ran/);
});

test('mutation tool applies exactly the requested change, stays under its artifact folder and only counts new failures', () => {
  assert.equal(mutation.applyMutation('a();b();', 'b();', 'c();'), 'a();c();');
  assert.equal(mutation.applyMutation('x x', 'x', 'y', 2), 'y y');
  assert.throws(() => mutation.applyMutation('a();', 'missing', 'x'), /exactly 1 time/);
  assert.throws(() => mutation.applyMutation('x x', 'x', 'y'), /found 2/);
  assert.throws(() => mutation.applyMutation('a', 'a', 'a'), /must differ/);
  const base = path.join(__dirname, 'synthetic-mutation-root');
  assert.equal(mutation.mutationWorkDirectory('0', 'A01', base), path.join(base, '0', 'A01'));
  assert.throws(() => mutation.mutationWorkDirectory('0', '../escape', base), /safe name/);
  assert.throws(() => mutation.mutationWorkDirectory('..', 'A01', base), /safe name/);
  const tap = 'ok 1 - kept\nnot ok 2 - layout only\nnot ok 3 - caught\n# tests 3\n# pass 1\n# fail 2\n';
  const summary = mutation.parseTapSummary(tap);
  assert.deepEqual([summary.tests, summary.pass, summary.fail], [3, 1, 2]);
  const copy = mutation.parseTapSummary('ok 1 - kept\nnot ok 2 - layout only\nok 3 - caught\n# tests 3\n# pass 2\n# fail 1\n');
  const tree = mutation.parseTapSummary('ok 1 - kept\nok 2 - layout only\nok 3 - caught\n# tests 3\n# pass 3\n# fail 0\n');
  assert.deepEqual(mutation.newFailures(copy, summary), ['caught']);
  assert.equal(mutation.verdict(tree, copy, summary), 'killed');
  assert.equal(mutation.verdict(tree, copy, copy), 'survived', 'failures that already happen before mutating are not credited');
  assert.equal(mutation.verdict(summary, copy, summary), 'inconclusive-tree-failed');
  // 2026-10-13 起：還在撰寫的 *.wip.cjs 也能做改壞檢查，不必另寫腳本；碰到時間、輸出量或記憶體上限的執行不算抓到。
  assert.ok(mutation.isMutationTestPath('tools/tests/frontend-journey-tables.wip.cjs'));
  assert.ok(mutation.isMutationTestPath('tools\\tests\\frontend-workflow.test.cjs'));
  assert.ok(!mutation.isMutationTestPath('tools/tests/support/frontend-fixtures.cjs'));
  assert.ok(!mutation.isMutationTestPath('tools/tests/nested/frontend-x.test.cjs'));
  const { MARKER } = require('./support/resource-guard.cjs');
  assert.equal(mutation.resourceLimitOf({ limit: null, text: 'not ok 1 - x\n# ' + MARKER + ' memory 2100 MB exceeded the 2048 MB limit' }), 'resource-guard');
  assert.equal(mutation.resourceLimitOf({ limit: 'timeout', text: '' }), 'timeout');
  assert.equal(mutation.resourceLimitOf({ limit: null, text: summary.failed.join('\n') }), null);
  assert.equal(mutation.verdict(tree, copy, summary, 'fail', { mutant: 'resource-guard' }), 'inconclusive-resource-limit',
    'a mutant that only stops at the resource limit is investigated, not credited');
  assert.equal(mutation.verdict(tree, copy, summary, 'fail', { tree: 'timeout' }), 'inconclusive-resource-limit');
});

test('frontend test runs cap how many test files run at once, so the total stays within the verification step limit', () => {
  // 2026-10-14：每個測試程序各有上限，但 node --test 預設依 CPU 核心數同時執行，合計記憶體會隨核心數放大。
  const { MAX_PARALLEL_TEST_FILES } = require('./support/resource-guard-marker.cjs');
  const { DEFAULT_MAX_RSS_MB } = require('./support/resource-guard.cjs');
  const lanes = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'harness', 'lanes.json'), 'utf8'));
  assert.ok(Number.isInteger(MAX_PARALLEL_TEST_FILES) && MAX_PARALLEL_TEST_FILES >= 1);
  assert.ok(MAX_PARALLEL_TEST_FILES * DEFAULT_MAX_RSS_MB <= lanes.limits.maximumProcessTreeMemoryMegabytes,
    'test files running at once, each at its own limit, must fit inside the verification step limit');
  const mutation = require('./support/mutate-frontend.cjs');
  const args = mutation.nodeTestArgs(['tools/tests/frontend-a.test.cjs', 'tools/tests/frontend-b.test.cjs'], 'approval');
  assert.ok(args.includes('--test-concurrency=' + MAX_PARALLEL_TEST_FILES), args.join(' '));
  assert.deepEqual(args.slice(-2), ['tools/tests/frontend-a.test.cjs', 'tools/tests/frontend-b.test.cjs']);
});

test('mutation tool itself runs outside the test-process limits, so a long list is not stopped halfway', () => {
  // 2026-10-13 第 2 批一次跑整份改壞清單時，工具載入了測試程序的資源上限，在第 900 秒把自己停掉，只完成 37 個。
  // 每次執行 node --test 已有工具自己的逾時與子程序清理，測試程序也各自載入上限；工具程序本身只讀標記，不啟動上限。
  const toolPath = path.join(__dirname, 'support', 'mutate-frontend.cjs');
  const { MARKER } = require('./support/resource-guard.cjs');
  const env = { ...process.env };
  delete env.JET_TEST_MAX_RSS_MB;
  delete env.JET_TEST_MAX_SECONDS;
  const probe = spawnSync(process.execPath, ['-e', 'require(' + JSON.stringify(toolPath) + ');\n' +
    "setTimeout(() => console.log('still running', typeof globalThis.__jetTestResourceGuard), 2500);"],
    { env: { ...env, JET_TEST_MAX_SECONDS: '1' }, encoding: 'utf8', timeout: 30000 });
  assert.equal(probe.status, 0, probe.stderr);
  assert.ok(!probe.stderr.includes(MARKER), probe.stderr);
  assert.equal(probe.stdout.trim(), 'still running undefined');
});
