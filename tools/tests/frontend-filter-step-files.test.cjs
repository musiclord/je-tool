'use strict';
require('./support/resource-guard.cjs');
// 第五步 filter-step.js 的拆檔（預篩選收斂計畫階段十，2026-10-25）。
//
// 第五步的程式分成五個檔案：filter-step-view.js（檢視狀態與資料世代）、filter-step-sources.js（條件來源的挑選）、
// filter-step-rules.js（條件列與摘要文字）、filter-step-saved.js（已存情境、矩陣與條件篩選報告）與 filter-step.js
// （送出形狀、送出前檢查與畫面組裝）。各檔把自己的最外層函式掛在 JetUi.FilterStep，其他檔案透過它轉呼叫。
// 這個檔固定拆檔後的結構：五個檔案都存在、index.html 依序載入、每個檔案都是獨立的 IIFE 並掛在 JetUi.FilterStep、
// 同一個函式只在一個檔案定義、轉呼叫與常數別名都有人定義，而且依序載入後第五步照常註冊。
// 函式內容逐字搬、行為不變由既有的第五步情境測試核對，這裡不重寫那些斷言。
// 第一次失敗：FrontendMapping 收據 20261025-052305304-d05c7b2ff80e4b63b84a74059c59d78d（四個新檔案還不存在）。
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { root, readWwwroot } = require('./support/frontend-fixtures.cjs');

// 載入順序：前四個檔案只掛函式與共用狀態，主檔最後載入並註冊第五步。
const FILTER_STEP_FILES = ['steps/filter-step-view.js', 'steps/filter-step-sources.js', 'steps/filter-step-rules.js',
  'steps/filter-step-saved.js', 'steps/filter-step.js'];

const readSources = () => FILTER_STEP_FILES.map(file => ({ file, source: fs.readFileSync(path.join(root, file), 'utf8') }));

test('第五步的五個檔案都存在，index.html 在 account-mapping-editor.js 之後、export-step.js 之前依序載入', () => {
  for (const file of FILTER_STEP_FILES) assert.ok(fs.existsSync(path.join(root, file)), file + ' 存在');
  const scripts = [...readWwwroot('index.html').matchAll(/<script defer src="\.\/js\/([^"]+)"><\/script>/g)].map(match => match[1]);
  const start = scripts.indexOf('account-mapping-editor.js');
  assert.ok(start >= 0, 'index.html 載入 account-mapping-editor.js');
  assert.deepEqual(scripts.slice(start, start + FILTER_STEP_FILES.length + 2),
    ['account-mapping-editor.js', ...FILTER_STEP_FILES, 'steps/export-step.js']);
});

test('每個檔案都是獨立的 IIFE 並掛在 JetUi.FilterStep；同一個函式只在一個檔案定義，轉呼叫與常數別名都有人定義', () => {
  const owners = new Map(), forwards = [], aliases = [];
  for (const { file, source } of readSources()) {
    assert.match(source, /^\/\*\n  第五步：進階條件篩選/, file + ' 開頭說明這是第五步的哪一塊');
    assert.ok(source.includes("(function (global) {\n  'use strict';\n"), file + ' 是獨立的 IIFE');
    assert.ok(source.includes('  var FilterStep = Ui.FilterStep = Ui.FilterStep || {};\n'), file + ' 掛在 JetUi.FilterStep');
    assert.ok(source.endsWith('})(window);\n'), file + ' 以 })(window); 結尾');
    const registrations = source.match(/^  Ui\.registerStep\('filter', render\);$/gm) || [];
    assert.equal(registrations.length, file === 'steps/filter-step.js' ? 1 : 0, file + ' 只有主檔註冊第五步');
    for (const match of source.matchAll(/^  function ([\w$]+)\(/gm)) {
      const name = match[1];
      if (name === 'forward') continue;
      assert.equal(owners.has(name), false, `${name} 只在一個檔案定義（${owners.get(name)} 與 ${file}）`);
      owners.set(name, file);
      assert.ok(source.includes(`  FilterStep.${name} = ${name};\n`), `${file} 把 ${name} 掛到 FilterStep`);
    }
    for (const match of source.matchAll(/^  var ([\w$]+) = forward\('([\w$]+)'\);$/gm)) {
      assert.equal(match[1], match[2], file + ' 轉呼叫的名稱一致');
      forwards.push({ file, name: match[1] });
    }
    for (const match of source.matchAll(/^  var ([\w$]+) = FilterStep\.([\w$]+);$/gm)) {
      assert.equal(match[1], match[2], file + ' 常數別名的名稱一致');
      aliases.push({ file, name: match[1] });
    }
  }
  for (const { file, name } of forwards) {
    assert.ok(owners.has(name), `${file} 轉呼叫的 ${name} 在某個檔案定義`);
    assert.notEqual(owners.get(name), file, `${file} 不轉呼叫自己定義的 ${name}`);
  }
  const exportedConstants = new Map();
  for (const { file, source } of readSources()) {
    for (const match of source.matchAll(/^  FilterStep\.([\w$]+) = \1;$/gm)) { if (!owners.has(match[1])) exportedConstants.set(match[1], file); }
  }
  for (const { file, name } of aliases) {
    assert.ok(exportedConstants.has(name), `${file} 用到的常數 ${name} 由某個檔案掛出`);
    assert.ok(FILTER_STEP_FILES.indexOf(exportedConstants.get(name)) < FILTER_STEP_FILES.indexOf(file),
      `${file} 用到的常數 ${name} 由較早載入的 ${exportedConstants.get(name)} 定義`);
  }
});

test('依序載入五個檔案後第五步照常註冊，共用的檢視狀態、送出形狀與事件綁定都從 JetUi.FilterStep 取得', () => {
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const document = { querySelector: () => null, createElement: () => ({ textContent: '', get innerHTML() { return this.textContent; } }) };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context, { filename: file });
  const registered = {}, originalRegister = window.JetUi.registerStep;
  window.JetUi.registerStep = (stepId, renderer) => { registered[stepId] = renderer; return originalRegister(stepId, renderer); };
  for (const { file, source } of readSources()) vm.runInContext(source, context, { filename: file });
  const step = window.JetUi.FilterStep;
  assert.equal(typeof step, 'object');
  assert.equal(registered.filter, step.render, '主檔把 FilterStep.render 註冊成第五步');
  // 常用篩選條件分頁的函式 2026-10-29 改名為 commonPickerHtml（舊名以當時的條件分頁名稱命名）。第一次失敗：FrontendMapping 收據 20261029-055947833-f591ce3960d6446c9dcd4785f98ac3c2。
  for (const name of ['render', 'bind', 'toWireDraft', 'syncViewState', 'commonPickerHtml', 'ruleSummaryLabel', 'readBackHtml', 'scenarioBuilderHtml'])
    assert.equal(typeof step[name], 'function', 'FilterStep.' + name + ' 是函式');
  assert.equal(typeof step.viewState, 'object', '共用的檢視狀態放在 FilterStep.viewState');
  assert.equal(step.viewState.workspacePane, 'filter', '檢視狀態的起始分頁');
  assert.equal(step.selectedRule, null, '選取中的條件起始為空');
});
