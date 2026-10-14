'use strict';
// 計畫第九節 A：第二次輸入發生在交叉淡化尚未結束時，更新仍須落在正在使用的元素。
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

// 第九節 B：橫排項目改高度時仍須填滿剩餘寬度；直向 flex 清單保留防止壓扁的限制。
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

// 第九節 C：直接呈現三個有設定的小區塊，不再顯示沒有獨立設定的總標題。
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
    f.store.setFilterDraft({ name: '合成條件', rationale: '合成', groups: [{ join: 'AND', matchScope: 'row', rules: [
      { type: 'drCrOnly', join: 'AND', drCr: 'debit' },
      { type: 'fieldValue', join: 'AND', field: 'accNum', operator: 'in', values: [] }
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
    f.store.replaceMappingDraft('gl', { docNum: 'Doc', postDate: 'Date', accNum: 'Account', accName: 'Name', description: 'Memo', amount: 'Amount', dcField: 'Side' });
    f.timers.run();
    holdCrossfade(f, '.map-rail', nested);
    const input = f.body().querySelector('[data-mapping-key="dcDebitCode"]');
    assert.ok(input, 'debit code editor is present');
    input.focus();
    for (const value of ['D', 'DR', 'DEBIT']) { input.value = value; input.dispatch('input'); }
    assert.equal(live(f.body(), '[data-bind="mapping-gl"] .map-rail').length, 1);
    assert.equal(f.dom.document.activeElement === input, true);
    assert.equal(f.store.getState().mapping.gl.draft.dcDebitCode, 'DEBIT');
  });
}
