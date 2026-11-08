'use strict';
// 第二次輸入發生在交叉淡化尚未結束時，更新仍須落在正在使用的元素。
// 使用正式步驟的輸入事件；只用替身保留動畫複本，不把假 DOM 當成真實版面驗收。
// 第一次失敗：FrontendMapping 20261014-121127883-79684ee8468f4def8216ac6eb6115077，提示累積四份、必填清單三份。
const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { usageJourneyFixture, mappingRenderFixture, readWwwroot, exposeAt } = require('./support/frontend-fixtures.cjs');

const live = (root, selector) => root.querySelectorAll(selector).filter(node => !node.closest('[data-motion-leaving]'));

function holdCrossfade(f, selector, nested = false) {
  // 模擬 morph 留在新元素前面的淡出複本。複本本身及複本內的子元素都不能成為下一次更新目標。
  f.window.JetMotion.morph = (root, render) => {
    const before = live(root, selector)[0];
    const attrs = before ? before.attributes.map(({ name, value }) => ' ' + name + '="' + value + '"').join('') : '';
    const html = before ? '<' + before.tagName + attrs + '>' + before.innerHTML + '</' + before.tagName + '>' : '';
    render();
    const after = live(root, selector)[0];
    if (!after || !html) return;
    const ghost = nested ? '<div data-motion-leaving="">' + html + '</div>' : html.replace('>', ' data-motion-leaving="">');
    after.insertAdjacentHTML('beforebegin', ghost);
  };
}

// 橫排項目改高度時仍須填滿剩餘寬度；直向 flex 清單保留防止壓扁的限制。
// 第一次失敗：FrontendMapping 20261014-121355398-92ecd98aa6804f5f8b33f54ca6b104ab，橫排、grid 與 block 都被凍結。
for (const [display, direction, frozen] of [['flex', 'row', false], ['flex', 'column', true], ['grid', 'row', false], ['block', 'column', false]]) {
  for (const operation of ['resize', 'enter']) test('motion ' + operation + ' only freezes vertical flex items: ' + display + ' ' + direction, () => {
    const f = usageJourneyFixture();
    f.window.addEventListener = () => {};
    f.window.getComputedStyle = () => ({ display, flexDirection: direction });
    vm.runInContext(exposeAt(readWwwroot('js/app.js'), '    return {\n      findLive: findLiveElement,',
      '    global.testMotionFrames = { run: run, heightFrames: heightFrames };\n'), f.context);
    let frames;
    const element = { parentElement: f.dom.content, animate(value) { frames = value; return {}; } };
    if (operation === 'resize') {
      frames = f.window.testMotionFrames.heightFrames({ start: 0, at: () => [50, 0] }, 100, null, element);
    } else {
      f.window.testMotionFrames.run(f.dom.content, 'synthetic', element, { axis: ['height'], natural: { height: 50 } }, 0, 1, {});
    }
    assert.ok(frames.length > 1, 'animation has multiple frames');
    for (const frame of frames) {
      assert.equal(frame.flexGrow, frozen ? '0' : undefined, 'growth follows the parent layout');
      assert.equal(frame.flexShrink, frozen ? '0' : undefined, 'shrink follows the parent layout');
      assert.equal(frame.minHeight, '0px', 'height can still shrink below content');
    }
  });
}

// 換步驟時，原本那一步的內容收回它的那一列（JetMotion.leave）。那一列是可換行的橫排，展開箭頭用 order 排在最右邊。
// 收回的內容要排在箭頭之後；否則箭頭被擠到下一行，收完拿掉的那一格，下面各列一次往上跳約 40 像素
// （裁定 2026-10-15-10 錄影指出切換尾段的抖動）。收的期間這一列不用列距、由內容的上外距帶著同樣的距離，
// 是 app.css 的規則，假 DOM 算不出版面，改在瀏覽器主機逐格量；這裡只核對外距與高度都收到 0。
// 第一次失敗：FrontendMapping 20261015-152203001-148afcf80d8241d993b54efb23313c6c，收回的內容排在箭頭前面。
test('step body folding back into its row ends flush with the row', () => {
  const f = usageJourneyFixture();
  f.window.addEventListener = () => {};
  f.window.requestAnimationFrame = () => 0;
  f.window.Element = function Element() {};
  f.window.Element.prototype.animate = () => ({});
  const styles = new Map();
  f.window.getComputedStyle = node => styles.get(node) || { position: 'static', order: '0' };
  vm.runInContext(exposeAt(readWwwroot('js/app.js'), '    return {\n      findLive: findLiveElement,',
    '    global.testMotionLeave = leave;\n'), f.context);
  const row = { children: [], getClientRects: () => [1],
    get firstElementChild() { return this.children[0] || null; },
    appendChild(child) { this.children = this.children.filter(node => node !== child).concat(child); child.parentElement = this; return child; } };
  styles.set(row, { display: 'flex', flexDirection: 'row', flexWrap: 'wrap', rowGap: '12px', columnGap: '12px', position: 'static', order: '0' });
  function child(style, extra = {}) {
    const node = Object.assign({ style: {}, getClientRects: () => [1] }, extra);
    Object.defineProperty(node, 'nextElementSibling', { get() { return row.children[row.children.indexOf(node) + 1] || null; } });
    styles.set(node, Object.assign({ position: 'static', order: '0' }, style));
    return node;
  }
  row.appendChild(child({ position: 'absolute' }));   // 軸線上的節點
  row.appendChild(child({}));                         // 步驟名稱
  row.appendChild(child({ order: '9' }));             // 展開箭頭
  let frames = null;
  const body = child({ flexBasis: '100%', marginTop: '32px' }, {
    attributes: [], hasAttribute: () => false, setAttribute() {}, removeAttribute() {}, getAttribute: () => null,
    querySelectorAll: () => [], getBoundingClientRect: () => ({ height: 300, width: 900 }),
    animate(value) { frames = value; return {}; } });
  assert.equal(f.window.testMotionLeave(body, row, null), true, 'the old body folds back into its row');
  assert.equal(row.children.at(-1), body, 'the folding body is the last child of the row');
  assert.ok(Number(body.style.order) > 9, 'the folding body is ordered after the caret, so the caret stays on the heading line');
  assert.equal(frames[0].marginTop, '32.00px', 'the fold starts from the body margin');
  assert.equal(frames.at(-1).marginTop, '0.00px', 'the fold takes the margin above the body back to zero');
  assert.equal(frames.at(-1).height, '0.00px', 'the fold ends at zero height');
});

// 直接呈現三個有設定的小區塊，不再顯示沒有獨立設定的總標題。
// 第一次失敗：FrontendMapping 20261014-121504367-4ac5f55c567347ad96ee86204d7e37d7。
test('mapping options show their three sections without an empty overall heading', () => {
  const f = usageJourneyFixture('mapping');
  f.store.setImportResult('gl', { batchId: 'gl-synthetic', columns: ['Doc', 'Date', 'Account', 'Name', 'Memo', 'Amount'], rowCount: 3 });
  f.draw();
  const options = f.$('[data-bind="gl-options"]');
  assert.ok(options, 'GL options exist');
  assert.equal(options.querySelectorAll('.map-options__title').length, 0, 'empty overall heading is removed');
  assert.deepEqual(options.querySelectorAll('.map-options__legend').map(node => node.textContent), ['過帳狀態', '人工/自動分錄代碼', '攸關資料元素欄位']);
  assert.equal(options.getAttribute('aria-label'), '總帳配對設定');
});

for (const nested of [false, true]) {
  test('readback stays singular during repeated input with a ' + (nested ? 'parent' : 'direct') + ' fading copy', () => {
    const f = usageJourneyFixture('filter');
    f.window.addEventListener = () => {};
    vm.runInContext(readWwwroot('js/app.js'), f.context);
    f.store.setFilterDraft({ name: '合成條件', rationale: '合成', groups: [{ join: 'AND', matchScope: 'row', conditions: [
      { type: 'drCrOnly', join: 'AND', drCr: 'debit' },
      { type: 'fieldValue', join: 'AND', field: 'accountNumber', operator: 'in', values: [] }
    ] }] });
    f.draw();
    holdCrossfade(f, '.scenario-readback', nested);
    const input = f.$('[data-value-key="values"]');
    assert.ok(input, 'account number editor is present');
    input.focus();
    for (const value of ['1', '11', '110', '1103']) {
      input.value = value;
      input.fire('input');
    }
    const sentences = live(f.dom.content, '.filter-readback .scenario-readback');
    assert.equal(sentences.length, 1, 'only the current readback is active');
    assert.match(sentences[0].textContent, /1103/);
    assert.equal(f.dom.document.activeElement === input, true, 'input focus is preserved');
    for (const ghost of f.dom.content.querySelectorAll('[data-motion-leaving]')) ghost.remove();
    assert.equal(f.dom.content.querySelectorAll('.filter-readback .scenario-readback').length, 1, 'one sentence remains after fading');
  });

  test('mapping required fields stay singular during repeated debit code input with a ' + (nested ? 'parent' : 'direct') + ' fading copy', () => {
    const f = mappingRenderFixture();
    f.store.setImportResult('gl', { batchId: 'gl-synthetic', columns: ['Doc', 'Date', 'Account', 'Name', 'Memo', 'Amount', 'Side'], rowCount: 3 });
    f.store.setMappingMode('gl', 'side');
    f.store.replaceMappingDraft('gl', { voucherNumber: 'Doc', glDate: 'Date', accountNumber: 'Account', accountName: 'Name', description: 'Memo', amount: 'Amount', drCr: 'Side' });
    f.timers.run();
    // 原本盯住右側必填清單；裁定 2026-11-05-24 拿掉它之後，打字時就地換掉的是表格上方那一行（第一次失敗：FrontendMapping 收據 20261105-075417209-aad34b4a8aee4ac2834cb54522eeb8cc）。
    holdCrossfade(f, '.map-required', nested);
    const input = f.body().querySelector('[data-mapping-key="drCrDebitCode"]');
    assert.ok(input, 'debit code editor is present');
    input.focus();
    for (const value of ['D', 'DR', 'DEBIT']) { input.value = value; input.dispatch('input'); }
    assert.equal(live(f.body(), '[data-bind="mapping-gl"] .map-required').length, 1);
    assert.equal(f.dom.document.activeElement === input, true);
    assert.equal(f.store.getState().mapping.gl.draft.drCrDebitCode, 'DEBIT');
  });
}

// 目前步驟的標題連按兩次應回到展開，不能被前一次收合的完成回呼再關掉。
// 合成預覽的背景 Edge 首次重現記錄：artifacts/frontend-preview/motion-before.json。
// 這裡控制時間與 WAAPI 完成事件；實際高度連續性另在背景瀏覽器量測。
for (const reduced of [false, true]) {
  for (const presses of [2, 3]) test('step fold responds to ' + presses + ' rapid presses with reduced motion ' + reduced, () => {
    const f = usageJourneyFixture('filter', { clock: true });
    f.window.addEventListener = () => {};
    f.window.requestAnimationFrame = () => 0;
    f.window.matchMedia = query => ({ matches: reduced && query.includes('prefers-reduced-motion') });
    f.window.Element = function Element() {};
    f.window.Element.prototype.animate = () => ({});
    f.window.getComputedStyle = () => ({ display: 'block', flexDirection: 'column' });
    vm.runInContext(exposeAt(readWwwroot('js/app.js'), '  function focusCurrentStepHeading(',
      '  global.testBindStepFlow = bindStepFlow;\n'), f.context);
    f.dom.content.innerHTML = '<div class="stepflow-item--current"><button data-step-toggle="">目前步驟</button><div class="stepflow-item__body">合成內容</div></div>';
    const toggle = f.$('[data-step-toggle]');
    const body = f.$('.stepflow-item__body');
    body.isConnected = true;
    let presentationHeight = 300;
    body.getBoundingClientRect = () => ({ height: presentationHeight, width: 900 });
    const animations = [];
    body.animate = frames => {
      const started = f.clock.now();
      const animation = { frames, cancelled: false, get currentTime() { return f.clock.now() - started; },
        cancel() { this.cancelled = true; presentationHeight = 300; if (this.oncancel) this.oncancel(); } };
      presentationHeight = Number.parseFloat(frames[0].height);
      animations.push(animation);
      return animation;
    };
    f.window.testBindStepFlow(f.dom.content);
    for (let index = 0; index < presses; index += 1) {
      toggle.fire('click');
      if (!reduced && index > 0) {
        assert.equal(Number.parseFloat(animations.at(-1).frames[0].height), index === 1 ? 180 : 120,
          'each reversal starts at the supplied browser presentation height');
      }
      f.clock.advance(60);
      // 瀏覽器目前呈現的高度獨立給定，不用產品的彈簧公式產生預期值。
      presentationHeight = index === 0 ? 180 : 120;
    }
    if (!reduced) {
      assert.equal(animations.length, presses, 'each press redirects the current motion');
      assert.ok(animations.slice(0, -1).every(animation => animation.cancelled), 'earlier completion callbacks are cancelled');
      const height = Number.parseFloat(animations[1].frames[0].height);
      assert.ok(height > 0 && height < 300, 'the reversal begins partway through the fold');
      assert.equal(body.inert, presses % 2 === 1, 'reopening immediately restores interaction');
      f.clock.advance(1000);
      animations.filter(animation => !animation.cancelled).forEach(animation => animation.onfinish());
    } else {
      assert.equal(animations.length, 0, 'reduced motion changes state directly');
    }
    assert.equal(f.store.getState().stepFlowCollapsed, presses % 2 === 1, 'the final state follows the last press');
  });
}

test('step collapse interrupts an opening body at its presented height', () => {
  const f = usageJourneyFixture('filter', { clock: true });
  f.window.addEventListener = () => {};
  f.window.requestAnimationFrame = () => 0;
  f.window.Element = function Element() {};
  f.window.Element.prototype.animate = () => ({});
  f.window.getComputedStyle = () => ({ display: 'block', flexDirection: 'column' });
  vm.runInContext(exposeAt(readWwwroot('js/app.js'), '  function focusCurrentStepHeading(',
    '  global.testBindStepFlow = bindStepFlow;\n'), f.context);
  f.dom.content.innerHTML = '<div class="stepflow-item--current"><button data-step-toggle="">目前步驟</button><div class="stepflow-item__body">合成內容</div></div>';
  const body = f.$('.stepflow-item__body');
  body.isConnected = true;
  body.getClientRects = () => [1];
  let presentationHeight = 300;
  body.getBoundingClientRect = () => ({ height: presentationHeight, width: 900 });
  const animations = [];
  body.animate = frames => {
    const started = f.clock.now();
    const animation = { frames, cancelled: false, get currentTime() { return f.clock.now() - started; },
      cancel() { this.cancelled = true; presentationHeight = 300; if (this.oncancel) this.oncancel(); } };
    presentationHeight = Number.parseFloat(frames[0].height);
    animations.push(animation);
    return animation;
  };
  f.window.testBindStepFlow(f.dom.content);
  f.window.JetMotion.enter(body);
  assert.equal(animations.length, 1, 'the body starts opening');
  f.clock.advance(60);
  presentationHeight = 120;
  f.$('[data-step-toggle]').fire('click');
  assert.equal(animations.length, 2, 'collapse redirects the opening animation');
  assert.equal(animations[0].cancelled, true, 'the old opening animation is cancelled');
  assert.equal(Number.parseFloat(animations[1].frames[0].height), 120, 'collapse starts at the browser presentation height');
  assert.equal(body.inert, true, 'the folding body no longer accepts input');
  animations[1].onfinish();
  assert.equal(f.store.getState().stepFlowCollapsed, true, 'the last request finishes collapsed');
});
