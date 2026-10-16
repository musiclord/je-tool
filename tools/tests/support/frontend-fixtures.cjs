'use strict';
// 前端情境測試的共用夾具（收尾四項第一項第 0 批，2026-10-13）。
//
// 用途：tools/tests/frontend-journey-*.test.cjs 依審計員的使用情境載入正式前端腳本、操作畫面並斷言結果。
// 兩個以上情境檔都會用到的夾具與小工具放在這裡；只服務一個情境檔的夾具留在那個檔裡。
// 檔名不含 .test.，Node 不會把它當成測試執行；夾具本身的測試在 tools/tests/frontend-support.test.cjs。
//
// 來源：下面標示「copied from」的區段，是第 0 批從 frontend-workflow.test.cjs（wf）與 frontend-mapping.test.cjs（mp）
// 原樣複製的副本（標示的行號是複製當時的位置）。複製後 wf 的 remapAppFixture 多掛出
// renderContent、renderMessagesPanel 與 buildMessage，這裡已同步。
// 第 11 批（2026-10-14）讓 wf、mp 改用本模組並刪掉自己的副本；搬之前逐一比對過，wf 在複製之後改過的 filterOperationFixture 已照 wf 同步。
// 只有 remapAppFixture 沒有換：wf 的根節點本身就是中欄，f.draw() 會清掉頁首與案件清單，wf 的測試依賴這個寫法，所以 wf 保留自己那一份。
// 和 wf、mp 原本那份不同的地方，都在該函式內用「第 0 批補：」的註解標出，主要是：
//   - batch9TreeDom：屬性選擇器接受不帶引號的值（[data-bind=busy-elapsed]）；:not(...) 不必開 dialogFocus 也照規則判斷；
//     「>」照子元素判斷；元素有 attributes 清單、id 與 select()；dispatch() 會冒泡到 document，fire() 在 bubbles 選項下也會冒泡；
//     document.addEventListener 會記下監聽；serialization: 'browser' 讓 innerHTML 和瀏覽器一樣不把文字裡的引號轉成 &quot;；
//     rootAttributes 可換掉根節點的屬性。
//   - usageJourneyFixture：可訂閱重繪（redraw）、把 setTimeout 收進佇列（queueTimers）、使用可控的 performance.now（clock）；
//     setInterval 與 clearInterval 一律記錄在 f.intervals。
//   - remapAppFixture：第一段切片延伸到 `var cancelOperationBtn = `，畫面放入 index.html 頁首的正式按鈕，另掛出 render；
//     中欄改成根節點裡的 <section data-bind="content">，f.draw() 畫進中欄，render() 重建中欄時不會清掉頁首。
//   - overviewDialogFixture：可選擇裝上假的 ECharts 與 ResizeObserver，並收集 overview-bi.js 排定的 setTimeout。
//   - mappingFixture 就是 mp 的 fixture()，改名以免和其他夾具混淆。
//
// 改壞檢查：環境變數 JET_FRONTEND_TEST_WWWROOT 可把讀取的 wwwroot 換成副本，只給 tools/tests/support/mutate-frontend.cjs 使用。
// 設定時會在 stderr 印一行提醒；FrontendMapping 路線執行前會清掉這個變數。
//
// 載入夾具的測試程序都受 resource-guard.cjs 的記憶體與時間上限保護（2026-10-13）。
require('./resource-guard.cjs');
// 假後端都經過 action-contract.cjs：假回應與送出的資料要符合正式後端記下的形狀（2026-10-15 前後端契合）。
const { contractJetApi, checkRequest, checkResponse, unwrap } = require('./action-contract.cjs');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const DEFAULT_WWWROOT = path.resolve(__dirname, '../../../src/JET/JET/wwwroot');
const wwwroot = resolveWwwroot();
const root = path.join(wwwroot, 'js');

function resolveWwwroot() {
  const override = process.env.JET_FRONTEND_TEST_WWWROOT;
  if (!override) return DEFAULT_WWWROOT;
  const resolved = path.resolve(override);
  assert.ok(fs.existsSync(path.join(resolved, 'js', 'app.js')), 'JET_FRONTEND_TEST_WWWROOT must point at a copied wwwroot: ' + resolved);
  process.stderr.write('[frontend-fixtures] JET_FRONTEND_TEST_WWWROOT=' + resolved + '：這次讀的是改壞檢查用的副本，不是工作樹的正式前端。\n');
  return resolved;
}

const settle = () => new Promise(resolve => setImmediate(resolve));
const copy = value => JSON.parse(JSON.stringify(value));

// 讀 wwwroot 底下的正式檔案（例如 'index.html'、'js/app.js'）。
function readWwwroot(relative) {
  return fs.readFileSync(path.join(wwwroot, relative), 'utf8');
}

// 從正式原始碼切出 start 到 end 之前的一段；兩個錨點都必須存在，而且 end 在 start 之後。
function sliceBetween(source, start, end) {
  const a = source.indexOf(start);
  assert.ok(a >= 0, 'sliceBetween: start anchor not found: ' + start);
  const b = source.indexOf(end, a + start.length);
  assert.ok(b > a, 'sliceBetween: end anchor not found after start: ' + end);
  return source.slice(a, b);
}

// 在正式原始碼的錨點前（或 after: true 時在錨點後）插入一段測試用程式，例如把 IIFE 內部函式掛到 window。
// 錨點找不到或出現不只一次就直接失敗：產品改了寫法時，測試要明確壞掉，而不是默默掛不上。
function exposeAt(source, anchor, code, options = {}) {
  assert.equal(typeof source, 'string', 'exposeAt: source must be a string');
  assert.ok(anchor, 'exposeAt: anchor must not be empty');
  const first = source.indexOf(anchor);
  assert.ok(first >= 0, 'exposeAt: anchor not found: ' + anchor);
  assert.equal(source.indexOf(anchor, first + 1), -1, 'exposeAt: anchor must appear exactly once: ' + anchor);
  const at = options.after ? first + anchor.length : first;
  return source.slice(0, at) + code + source.slice(at);
}

// 依 CSSOM 規格的 CSS.escape，給沒有瀏覽器 CSS 物件的 vm 使用（app.js 用它組焦點與捲動的選擇器）。
function cssEscape(value) {
  const text = String(value);
  let result = '';
  for (let index = 0; index < text.length; index++) {
    const code = text.charCodeAt(index);
    if (code === 0) { result += '�'; continue; }
    if ((code >= 0x1 && code <= 0x1f) || code === 0x7f || (index === 0 && code >= 0x30 && code <= 0x39) ||
        (index === 1 && code >= 0x30 && code <= 0x39 && text.charCodeAt(0) === 0x2d)) {
      result += '\\' + code.toString(16) + ' '; continue;
    }
    if (index === 0 && text.length === 1 && code === 0x2d) { result += '\\' + text[index]; continue; }
    if (code >= 0x80 || code === 0x2d || code === 0x5f || (code >= 0x30 && code <= 0x39) ||
        (code >= 0x41 && code <= 0x5a) || (code >= 0x61 && code <= 0x7a)) { result += text[index]; continue; }
    result += '\\' + text[index];
  }
  return result;
}

// 由測試決定何時執行的 setTimeout：排定的工作先收進佇列，run() 依延遲先後、同延遲依排定順序執行。
function timerQueue() {
  let nextId = 1;
  const pending = [];
  function take() {
    let best = 0;
    for (let i = 1; i < pending.length; i++) if (pending[i].delay < pending[best].delay) best = i;
    return pending.splice(best, 1)[0];
  }
  return {
    setTimeout(fn, delay = 0, ...args) { const id = nextId++; pending.push({ id, fn, delay: Number(delay) || 0, args }); return id; },
    clearTimeout(id) { const index = pending.findIndex(entry => entry.id === id); if (index >= 0) pending.splice(index, 1); },
    pending: () => pending.map(entry => ({ id: entry.id, delay: entry.delay })),
    // 執行目前與執行中新排定的工作，直到佇列清空；超過 limit 次視為無限排程而失敗。
    run(limit = 100) {
      let count = 0;
      while (pending.length) {
        assert.ok(count < limit, 'timerQueue.run: more than ' + limit + ' timers; the code keeps rescheduling');
        const entry = take(); count++;
        if (typeof entry.fn === 'function') entry.fn(...entry.args);
      }
      return count;
    }
  };
}

// 可控的 performance.now：advance(ms) 前進，時間不會自己走。
function fakeClock(start = 1000) {
  let now = start;
  return { performance: { now: () => now }, advance(ms) { now += ms; return now; }, now: () => now };
}

// 假的 ECharts 與 ResizeObserver：記錄 init 的參數、每次 setOption、resize 與 dispose，供總覽圖表的生命週期斷言。
// 要在載入 overview-bi.js 之前用 install(window) 放進 window，overview-bi.js 載入時就會建立 ResizeObserver。
function fakeCharts() {
  const charts = [], observers = [];
  class ResizeObserver {
    constructor(callback) { this.callback = callback; this.observed = []; this.disconnectCount = 0; observers.push(this); }
    observe(element) { if (!this.observed.includes(element)) this.observed.push(element); }
    unobserve(element) { this.observed = this.observed.filter(entry => entry !== element); }
    disconnect() { this.observed = []; this.disconnectCount++; }
    // 模擬瀏覽器回報大小改變。
    trigger() { this.callback(this.observed.map(target => ({ target }))); }
  }
  const echarts = {
    init(element, theme, initOptions) {
      const chart = { element, theme, initOptions, options: [], resizeCount: 0, disposed: false,
        setOption(option, notMerge) { this.options.push({ option, notMerge }); },
        resize() { this.resizeCount++; },
        dispose() { this.disposed = true; },
        isDisposed() { return this.disposed; } };
      charts.push(chart);
      return chart;
    }
  };
  return { echarts, ResizeObserver, charts, observers,
    live: () => charts.filter(chart => !chart.disposed),
    install(window) { window.echarts = echarts; window.ResizeObserver = ResizeObserver; } };
}

// 假的 matchMedia：change(matches) 模擬視窗跨過斷點，queries 記下每次查詢的字串。
function fakeMatchMedia(initial = false) {
  const lists = [];
  function matchMedia(query) {
    const listeners = [];
    const list = { media: query, matches: typeof initial === 'function' ? !!initial(query) : !!initial,
      addEventListener(type, fn) { if (type === 'change') listeners.push(fn); },
      removeEventListener(type, fn) { const index = listeners.indexOf(fn); if (index >= 0) listeners.splice(index, 1); },
      addListener(fn) { listeners.push(fn); }, removeListener(fn) { const index = listeners.indexOf(fn); if (index >= 0) listeners.splice(index, 1); },
      change(matches) { this.matches = !!matches; for (const fn of listeners.slice()) fn({ matches: this.matches, media: query }); } };
    lists.push(list);
    return list;
  }
  return { matchMedia, lists, queries: () => lists.map(list => list.media) };
}

// ---- copied from tools/tests/frontend-workflow.test.cjs:1675 (plain) ----
const plain = value => JSON.parse(JSON.stringify(value));

// ---- copied from tools/tests/frontend-workflow.test.cjs:3367 (batch9TreeDom) ----
// Minimal tree DOM for real rule-list render/bind and app renderContent lifecycles (not a second pager implementation).
// 第 0 批補的選項：bubbles（fire() 也照瀏覽器冒泡）、serialization: 'browser'（innerHTML 文字不把引號轉成 &quot;）。
function batch9TreeDom(options = {}) {
  // 第 0 批補：預設維持 wf 的寫法，文字也把 " 轉成 &quot;；serialization: 'browser' 時和瀏覽器一樣，文字只跳脫 & < >。
  // 用 wf 的寫法測不出 Ui.esc 漏掉雙引號那一行，因為假 DOM 自己就轉好了。屬性值兩種模式都跳脫 & " < >。
  const browserText = options.serialization === 'browser';
  const escape = value => { const text = String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); return browserText ? text : text.replace(/"/g, '&quot;'); };
  const escapeAttribute = value => String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  const decode = value => value.replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
  let document;
  const documentListeners = [];
  const captureOf = listenerOptions => listenerOptions === true || !!(listenerOptions && listenerOptions.capture);
  // 第 0 批補：照瀏覽器的順序送事件：document 的捕獲監聽、目標、各層父元素，最後是 document 的一般監聽；
  // stopPropagation 停在目前這一層，stopImmediatePropagation 連同一層後面的監聽也不跑。只有掛在畫面根節點下的元素會傳到 document。
  function dispatchEvent(target, type, extra = {}) {
    const event = { type, target, bubbles: true, defaultPrevented: false, propagationStopped: false, immediateStopped: false,
      preventDefault() { this.defaultPrevented = true; }, stopPropagation() { this.propagationStopped = true; },
      stopImmediatePropagation() { this.propagationStopped = true; this.immediateStopped = true; }, ...extra };
    const pathNodes = [];
    for (let node = target; node; node = node.parentElement) pathNodes.push(node);
    const reachesDocument = pathNodes.at(-1) === content || (document.body && pathNodes.at(-1) === document.body);
    const runDocument = capture => {
      event.currentTarget = document;
      for (const entry of documentListeners.slice()) {
        if (entry.type !== type || entry.capture !== capture || !documentListeners.includes(entry)) continue;
        entry.fn(event); if (event.immediateStopped) break;
      }
    };
    if (reachesDocument) runDocument(true);
    for (const node of pathNodes) {
      if (event.propagationStopped) return event;
      event.currentTarget = node;
      for (const fn of (node.handlers[type] || []).slice()) { fn(event); if (event.immediateStopped) break; }
    }
    if (reachesDocument && !event.propagationStopped) runDocument(false);
    return event;
  }
  // 第 0 批補：支援 [a="b"]、[a='b'] 與不帶引號的 [a=b]，回傳 [名稱, 值或 undefined]。
  const attributeSelectors = selector => [...selector.matchAll(/\[([^\]=\s]+)(?:=(?:"([^"]*)"|'([^']*)'|([^\]"'\s]+)))?\]/g)]
    .map(match => [match[1], match[2] ?? match[3] ?? match[4]]);
  function element(tag = 'div', attrs = {}) {
    const node = { tagName: tag.toUpperCase(), attrs: { ...attrs }, children: [], parentElement: null, handlers: {}, isConnected: true,
      style: {}, scrollHeight: 36, scrollTop: 0, scrollLeft: 0, disabled: Object.hasOwn(attrs, 'disabled'), hidden: Object.hasOwn(attrs, 'hidden'), value: attrs.value || '',
      checked: Object.hasOwn(attrs, 'checked'),
      getAttribute(name) { return Object.hasOwn(this.attrs, name) ? this.attrs[name] : null; },
      hasAttribute(name) { return Object.hasOwn(this.attrs, name); },
      setAttribute(name, value) { this.attrs[name] = String(value); }, removeAttribute(name) { delete this.attrs[name]; },
      addEventListener(name, fn) { (this.handlers[name] ||= []).push(fn); },
      // 第 0 批補：bubbles 選項開啟時 fire() 等於 dispatch()；沒開時維持 wf 的寫法，只呼叫目標自己的監聽。
      fire(name, extra = {}) { if (options.bubbles) return dispatchEvent(this, name, extra); for (const fn of this.handlers[name] || []) fn({ target: this, currentTarget: this, preventDefault() {}, stopPropagation() {}, ...extra }); },
      dispatch(name, extra = {}) { return dispatchEvent(this, name, extra); },
      // 第 0 批補：app.js 的焦點辨識讀 element.attributes 與 element.id；剪貼簿備援會呼叫 select()。
      get attributes() { return Object.entries(this.attrs).map(([name, value]) => ({ name, value })); },
      get id() { return this.attrs.id || ''; }, set id(value) { this.attrs.id = String(value); },
      select() { this.selectionStart = 0; this.selectionEnd = String(this.value ?? '').length; },
      appendChild(child) { this._text = ''; this.children.push(child); child.parentElement = this; child.isConnected = this.isConnected; return child; },
      contains(child) { for (let p = child; p; p = p.parentElement) if (p === this) return true; return false; },
      matches(selector) {
        if (selector.includes(',')) return selector.split(',').some(part => this.matches(part.trim()));
        // 第 0 批補：:not(...) 一律照規則判斷；wf 只在 dialogFocus 時判斷，否則會把括號裡的屬性當成必須符合。
        if (/:not\([^()]*\)/.test(selector)) {
          const negated = [...selector.matchAll(/:not\(([^()]*)\)/g)].map(match => match[1]);
          return this.matches(selector.replace(/:not\([^()]*\)/g, '')) && !negated.some(part => this.matches(part));
        }
        const attributes = attributeSelectors(selector);
        if (attributes.some(([name, value]) => this.getAttribute(name) === null || value !== undefined && this.getAttribute(name) !== value)) return false;
        const base = selector.replace(/\[[^\]]+\]/g, ''), tagMatch = base.match(/^[a-z][\w-]*/i);
        if (tagMatch && this.tagName !== tagMatch[0].toUpperCase()) return false;
        for (const part of base.matchAll(/\.([\w-]+)/g)) if (!(this.attrs.class || '').split(/\s+/).includes(part[1])) return false;
        const id = base.match(/#([\w-]+)/); return !id || this.attrs.id === id[1];
      },
      closest(selector) { for (let node = this; node; node = node.parentElement) if (node.matches(selector)) return node; return null; },
      querySelectorAll(selector) {
        const selectors = selector.split(',').map(part => part.trim()), found = [];
        // 第 0 批補：「>」照子元素判斷；wf 把它當成任何祖先都符合。
        function matchesPath(node, parts) { if (!node.matches(parts.at(-1))) return false; let p = node.parentElement;
          for (let i = parts.length - 2; i >= 0; i--) {
            if (parts[i] === '>') { i--; if (i < 0 || !p || !p.matches(parts[i])) return false; p = p.parentElement; continue; }
            while (p && !p.matches(parts[i])) p = p.parentElement; if (!p) return false; p = p.parentElement; } return true; }
        function walk(parent) { for (const child of parent.children) { if (selectors.some(sel => matchesPath(child, sel.split(/\s+(?=(?:[^"]*"[^"]*")*[^"]*$)/)))) found.push(child); walk(child); } }
        walk(this); return found;
      },
      querySelector(selector) { return this.querySelectorAll(selector)[0] || null; },
      insertAdjacentHTML(position, html) { const fragment = element(); fragment.innerHTML = html;
        if (position === 'beforeend') for (const child of fragment.children.slice()) this.appendChild(child);
        else if (['beforebegin', 'afterend'].includes(position)) { const parent = this.parentElement; assert.ok(parent); const index = parent.children.indexOf(this) + (position === 'afterend' ? 1 : 0);
          parent.children.splice(index, 0, ...fragment.children); fragment.children.forEach(child => { child.parentElement = parent; }); }
        else throw new Error('Unsupported synthetic DOM insertion: ' + position); },
      focus(focusOptions) { this.lastFocusOptions = focusOptions || null; if (document) document.activeElement = this; }, scrollIntoView() {},
      getClientRects() { return this.hidden ? [] : [{ width: 400, height: 30 }]; },
      remove() { if (!this.parentElement) return; const parent = this.parentElement; parent.children.splice(parent.children.indexOf(this), 1); this.parentElement = null; this.isConnected = false; },
      set outerHTML(html) { const parent = this.parentElement; if (!parent) return; const index = parent.children.indexOf(this), fragment = element(); fragment.innerHTML = html;
        parent.children.splice(index, 1, ...fragment.children); fragment.children.forEach(child => { child.parentElement = parent; }); this.isConnected = false; },
      get rows() { return this.querySelectorAll('tr'); },
      get innerHTML() { return this._text ? escape(this._text) : this.children.map(child => '<' + child.tagName.toLowerCase() + Object.entries(child.attrs).map(([k, v]) => ' ' + k + '="' + escapeAttribute(v) + '"').join('') + '>' + child.innerHTML + '</' + child.tagName.toLowerCase() + '>').join(''); },
      set innerHTML(html) {
        function detach(node) { node.isConnected = false; node.children.forEach(detach); } this.children.forEach(detach); this.children = []; this._text = '';
        const stack = [this], voids = new Set(['input', 'br', 'hr', 'img', 'meta', 'link']);
        for (const token of String(html).matchAll(/<\/?([a-z][\w-]*)\b([^>]*?)>|([^<]+)/gi)) {
          if (token[3]) { const text = element('jet-text'); text._text = decode(token[3]); stack.at(-1).appendChild(text); continue; }
          const name = token[1].toLowerCase(); if (token[0].startsWith('</')) { while (stack.length > 1) { if (stack.pop().tagName === name.toUpperCase()) break; } continue; }
          const attributes = {}; for (const attr of token[2].matchAll(/([^\s=/>]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?/g)) attributes[attr[1]] = decode(attr[2] ?? attr[3] ?? attr[4] ?? '');
          const child = element(name, attributes); stack.at(-1).appendChild(child); if (!voids.has(name) && !token[0].endsWith('/>')) stack.push(child);
        }
        for (const select of this.querySelectorAll('select')) {
          const options = select.querySelectorAll('option');
          const selected = options.find(option => option.hasAttribute('selected')) || options[0];
          select.value = selected ? selected.getAttribute('value') ?? selected.textContent : '';
        }
        for (const textarea of this.querySelectorAll('textarea')) textarea.value = textarea.textContent;
      },
      get content() { return this; },
      get elements() { const entries = this.querySelectorAll('input,select,textarea,button');
        for (const node of entries) { const name = node.getAttribute('name'); if (name) entries[name] = node; } return entries; },
      get textContent() { return this._text || this.children.map(child => child.textContent).join(''); },
      set textContent(value) { this.children = []; this._text = String(value); },
      get className() { return this.attrs.class || ''; }, set className(value) { this.attrs.class = value; },
      // data-* 屬性的 dataset 讀寫，供第五步多條件組的綁定使用。
      get dataset() {
        const owner = this, attr = key => 'data-' + String(key).replace(/[A-Z]/g, c => '-' + c.toLowerCase());
        return new Proxy({}, { get: (_, key) => owner.getAttribute(attr(key)) ?? undefined,
          set: (_, key, value) => { owner.setAttribute(attr(key), value); return true; } });
      }
    };
    // 2026-10-13：父子連結、事件監聽與每次讀取都重新計算的屬性改成不可列舉，照樣能讀寫。
    // 測試失敗時，Node 的 TAP 與 spec 報表會逐項展開失敗值的可列舉內容；失敗值是元件時，原本會沿著這些連結展開整個畫面而且停不下來，
    // 當天一個改壞檢查因此讓單一測試程序占用 73 GB 記憶體。改成不可列舉後，同一個失敗只印出幾 KB 的說明。
    for (const [key, descriptor] of Object.entries(Object.getOwnPropertyDescriptors(node))) {
      if (descriptor.get || descriptor.set || ['parentElement', 'children', 'handlers'].includes(key)) Object.defineProperty(node, key, { ...descriptor, enumerable: false });
    }
    // 對話框淡出後用 parentNode.removeChild 拿掉（JetMotion.dismiss），第五步的欄位提示用 input.parentNode 找到欄位那一列再加上說明。
    // 第 11 批（2026-10-14）：原本只在 dialogFocus 時才有，改成一律提供，和瀏覽器一樣；第 9 批因為沒有 parentNode 看不到第五步名稱欄與動機欄旁的提示。
    // parentNode 和 parentElement 一樣不可列舉（原因見上一段）。
    Object.defineProperty(node, 'parentNode', { get() { return this.parentElement; }, enumerable: false });
    node.removeChild = child => { child.remove(); return child; };
    node.classList = { add(name) { const values = new Set(node.className.split(/\s+/).filter(Boolean)); values.add(name); node.className = [...values].join(' '); },
      remove(name) { node.className = node.className.split(/\s+/).filter(value => value !== name).join(' '); },
      contains(name) { return node.className.split(/\s+/).includes(name); }, toggle(name, force) { const on = force ?? !this.contains(name); this[on ? 'add' : 'remove'](name); } };
    return node;
  }
  // 第 0 批補：rootAttributes 可換掉根節點的屬性。預設和 wf 一樣是 data-bind="content"，整個畫面就是中欄；
  // remapAppFixture 改用 data-jet-root，把中欄放成根節點裡的一個區塊，app.js 的 render() 重建中欄時才不會清掉頁首。
  const content = element('main', options.rootAttributes || { 'data-bind': 'content' });
  document = { readyState: 'loading', activeElement: null, createElement: element,
    // 第 0 批補：記下 document 的監聽（wf 的版本什麼都不做），冒泡到畫面根節點後會呼叫；capture 監聽在目標之前執行。
    addEventListener(type, fn, listenerOptions) {
      const capture = captureOf(listenerOptions);
      if (!documentListeners.some(entry => entry.type === type && entry.fn === fn && entry.capture === capture)) documentListeners.push({ type, fn, capture });
    },
    removeEventListener(type, fn, listenerOptions) {
      const capture = captureOf(listenerOptions);
      const index = documentListeners.findIndex(entry => entry.type === type && entry.fn === fn && entry.capture === capture);
      if (index >= 0) documentListeners.splice(index, 1);
    },
    listenerCount: type => documentListeners.filter(entry => entry.type === type).length,
    // 從目前焦點（沒有就從畫面根節點）送出事件，例如鍵盤事件；extra.target 可指定目標。
    dispatch(type, extra = {}) { return dispatchEvent(extra.target || document.activeElement || content, type, extra); },
    querySelector: selector => content.matches(selector) ? content : content.querySelector(selector), querySelectorAll: selector => content.querySelectorAll(selector) };
  // 2026-10-15：app.js 的 bindWindowFocus 在 <html> 上加減 window-inactive，所以補一個不在畫面樹裡的根元素。
  document.documentElement = element('html', {});
  return { element, content, document };
}

// ---- copied from tools/tests/frontend-workflow.test.cjs:4310 (savedG) ----
const savedG = (name = 'G') => ({ name, rationale: 'G：空白摘要', source: 'kct',
  // 第九節 G2 的現行 G 卡；舊情境測試另用原始 JSON，不由這個夾具轉換。首次失敗：20261014-133406774-8241f3894ca54d84a987e8fa13814cea。
  groups: [{ join: 'AND', matchScope: 'row', rules: [{ type: 'fieldValue', join: 'AND', field: 'description', operator: 'isBlank', includeBlank: false }] }],
  editorOrigins: { version: 1, legacyKctSource: false, nameIsAutomatic: true, rationaleIsAutomatic: true, groups: [{ letters: ['G'] }] } });

// ---- copied from tools/tests/frontend-workflow.test.cjs:4315 (usageJourneyFixture) ----
// 合成回應只控制邊界；步驟繪製、事件處理與 Store 均執行正式腳本。
// 第 0 批補的選項（和假 DOM 的選項放在同一個物件，其餘照舊交給 batch9TreeDom）：
//   redraw: true      訂閱 Store，contentVersion 改變時重繪目前步驟（和 app.js 的重繪條件相同）；f.drawCount() 是重繪次數。
//   queueTimers: true setTimeout 收進 f.timers，由測試呼叫 f.timers.run() 執行；沒開時和 wf 一樣直接丟掉。
//   clock: true       window.performance.now 改用 f.clock，f.clock.advance(ms) 才會前進；沒開時和 wf 一樣沒有 performance。
// setInterval 與 clearInterval 一律記在 f.intervals（wf 的版本直接丟掉），心跳與忙碌計時的測試可以讀它。
function usageJourneyFixture(step = 'export', fixtureOptions = {}) {
  const { redraw = false, queueTimers = false, clock: useClock = false, ...domOptions } = fixtureOptions;
  const dom = batch9TreeDom(domOptions), requests = [];
  const timers = queueTimers ? timerQueue() : null, clock = useClock ? fakeClock() : null, intervals = [];
  const setTimer = timers ? timers.setTimeout : () => 0, clearTimer = timers ? timers.clearTimeout : () => {};
  const window = { setTimeout: setTimer, clearTimeout: clearTimer,
    setInterval(fn, ms) { intervals.push({ id: intervals.length + 1, fn, ms, cleared: false }); return intervals.length; },
    clearInterval(id) { const entry = intervals.find(item => item.id === id); if (entry) entry.cleared = true; },
    JetFocus: { defer() {} }, JetApi: recordingJetApi(requests) };
  if (clock) window.performance = clock.performance;
  const context = vm.createContext({ window, document: dom.document, setTimeout: setTimer, clearTimeout: clearTimer });
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js', 'filter-vouchers.js',
    'steps/create-step.js', 'steps/import-step.js', 'steps/mapping-step.js', 'steps/validate-step.js',
    'account-mapping-editor.js', 'steps/filter-step.js', 'steps/export-step.js'])
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context, { filename: file });
  const store = window.JetStore, state = store.getState();
  store.setProject({ projectId: 'usage-synthetic', periodStart: '2025-01-01', periodEnd: '2025-11-30', lastPeriodStart: '2025-11-20' });
  store.setLastRun('validate', { resultRef: { runId: 'v1' }, completenessTest: { eligibility: { isEligible: true } } });
  store.setSavedScenarios([savedG('第一個'), savedG('第二個'), savedG('第三個')]);
  store.setFilterResultRef({ revision: 'r1', populationScope: 'auditPeriod' });
  const $ = selector => dom.content.querySelector(selector);
  function draw(next = step) { step = next; window.JetUi.renderStep(step, dom.content, store.getState()); return dom.content; }
  function click(selector) { const node = $(selector); assert.ok(node, selector + ' rendered'); assert.equal(node.disabled, false, selector + ' enabled'); node.fire('click'); }
  function change(selector, value) { const node = $(selector); assert.ok(node, selector + ' rendered');
    if (typeof value === 'boolean') node.checked = value; else node.value = value; node.fire('change'); }
  function request(action) { const found = requests.filter(r => r.action === action); assert.ok(found.length, action + ' requested'); return found.at(-1); }
  let drawn = 0, drawnVersion = null;
  if (redraw) {
    store.subscribe(next => { if (next.contentVersion === drawnVersion) return; drawnVersion = next.contentVersion; drawn++; draw(); });
    drawnVersion = state.contentVersion; draw(); drawn++;
  }
  return { window, store, state, dom, context, requests, $, draw, click, change, request, timers, clock, intervals, drawCount: () => drawn };
}

// ---- copied from tools/tests/frontend-workflow.test.cjs:4278 (filterOperationFixture) ----
// 2026-10-07 進階篩選操作測試（計畫第八節「操作測試找到的問題」F1 到 F9）。前半用合成 DOM 繪出正式第五步並以真實點擊操作；
// 後半直接呼叫正式函式核對命名、送出形狀與預覽提示。名稱與動機的預期值逐字寫死，不由被測程式算出。
function filterOperationFixture({ saved = [] } = {}) {
  const dom = batch9TreeDom(), commits = [];
  const window = { setTimeout: () => 0, clearTimeout() {}, JetFocus: { defer() {} } };
  const context = vm.createContext({ window, document: dom.document });
  for (const file of ['state.js', 'ui-core.js', 'filter-values.js'])
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'steps/filter-step.js'), 'utf8').replace("  Ui.registerStep('filter', render);",
    '  window.ops = { syncViewState, render, view: () => viewState };'), context);
  const store = window.JetStore, api = window.ops;
  // 2026-10-13 第五步一次只存或刪一個情境：畫面改送 filterScenarioSave／Remove／Recheck，只帶正在處理的那一個情境。
  // 三個新方法和舊的 filterCommit 都記進 commits 並帶方法名稱。第一次失敗：FrontendMapping 收據 20261013-103553346-54ebed91711946dd8ff77c3dd1be7b23。
  // 第 11 批：wf 在第 0 批複製之後改成這個寫法，這裡照 wf 同步，wf 改用本模組。
  window.JetApi = contractJetApi(Object.fromEntries(['filterCommit', 'filterScenarioSave', 'filterScenarioRemove', 'filterScenarioRecheck'].map(method =>
    [method, payload => new Promise((resolve, reject) => commits.push({ method, payload, resolve, reject }))])));
  store.setProject({ projectId: 'filter-operation-synthetic' });
  store.setAccountMappingState({ hasAnyCategory: true, hasRevenue: true, hasCounterpart: true });
  store.setSavedScenarios(saved);
  let version = -1;
  function draw() {
    const state = store.getState();
    if (state.contentVersion === version) return;
    version = state.contentVersion; api.syncViewState(state); api.render(dom.content, state);
  }
  store.subscribe(draw); draw();
  const $ = selector => dom.content.querySelector(selector);
  function click(selector) { const node = $(selector); assert.ok(node, 'missing ' + selector); node.fire('click'); }
  function choose(selector, value) { const node = $(selector); assert.ok(node, 'missing ' + selector); node.value = value; node.fire('change'); }
  function type(selector, value) { const node = $(selector); assert.ok(node, 'missing ' + selector); node.value = value; node.fire('input'); }
  const pressed = letter => $('[data-kct-letter="' + letter + '"]').getAttribute('aria-pressed') === 'true';
  const messages = () => store.getState().messages.map(m => m.text);
  const notice = () => { const node = $('[data-bind="scenario-notice"]'); return node && !node.hidden ? node.textContent : ''; };
  return { window, store, dom, context, commits, $, click, choose, type, pressed, messages, notice, draft: () => store.getState().filter.draft };
}

// ---- copied from tools/tests/frontend-workflow.test.cjs:5239 (remapFilterPreviewFixture) ----
function remapFilterPreviewFixture() {
  const f = filterOperationFixture(), requests = [];
  for (const action of ['filterPreview', 'queryFilterVoucherPage', 'queryFilterVoucherRowsPage'])
    f.window.JetApi[action] = payload => new Promise((resolve, reject) => requests.push({ action, payload, resolve, reject }));
  vm.runInContext(fs.readFileSync(path.join(root, 'filter-vouchers.js'), 'utf8'), f.context);
  return { ...f, requests, request(action) { const result = requests.filter(r => r.action === action).at(-1); assert.ok(result, action + ' requested'); return result; } };
}

// ---- copied from tools/tests/frontend-workflow.test.cjs:4981 (remapAppFixture；wf 保留自己那一份，原因見檔頭) ----
// 第 0 批補：options 原樣交給 usageJourneyFixture（例如 queueTimers、clock）。畫面放入 index.html 頁首的正式按鈕
// （總覽、開啟案件資料夾、回首頁、退出，初始照 index.html 的 hidden），取代 wf 自己寫的兩個空按鈕；
// 第一段切片延伸到 `var cancelOperationBtn = `，「開啟案件資料夾」也照正式程式綁定；另掛出 render，
// 並補上 window.CSS.escape 與 document.body，讓整個 render() 可以在 vm 執行。
// 根節點改成 data-jet-root，中欄是裡面的 <section data-bind="content">；f.draw() 也畫進這個中欄，不再清掉頁首與案件清單。
function remapAppFixture(options = {}) {
  const f = usageJourneyFixture('export', { ...options, rootAttributes: { 'data-jet-root': '' } });
  f.window.addEventListener = () => {}; f.window.JetApi.isReady = () => true;
  if (!f.window.CSS) f.window.CSS = { escape: cssEscape };
  if (!f.dom.document.body) f.dom.document.body = f.dom.element('body');
  const header = sliceBetween(readWwwroot('index.html'), '<div class="app-header__status">', '</header>');
  f.dom.content.innerHTML = '<section data-bind="project-picker"></section><nav data-bind="step-nav"></nav>' + header +
    '<section class="content" data-bind="content"></section><button data-action="cancel-operation"></button>';
  const middle = f.dom.content.querySelector('[data-bind="content"]');
  f.draw = (next = 'export') => { f.window.JetUi.renderStep(next, middle, f.store.getState()); return middle; };
  const source = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
  vm.runInContext(exposeAt(source, "  if (document.readyState === 'loading') {",
    '  window.remapApp = { renderPicker, overviewHtml, renderStepNav, renderContent, renderMessagesPanel, buildMessage, render };\n'), f.context, { filename: 'app.js' });
  function slice(start, end) { const a = source.indexOf(start), b = source.indexOf(end, a + start.length); assert.ok(a >= 0 && b > a); return source.slice(a, b); }
  vm.runInContext('var Ui = window.JetUi;\n' + slice('    var exitBtn = ', '    var cancelOperationBtn = '), f.context);
  const started = []; f.window.JetApi.onRequestStarted = fn => started.push(fn);
  vm.runInContext('var global = window, Store = window.JetStore, busyActiveAction = null;\n' +
    slice('  var CANCELLABLE_ACTIONS = {', '  // 增量渲染狀態') +
    slice("    if (global.JetApi && typeof global.JetApi.onRequestStarted === 'function') {", '    // 長作業進度只更新') +
    slice("    var cancelOperationBtn = document.querySelector('[data-action=\"cancel-operation\"]');", "    document.querySelectorAll('[data-action=\"messages-toggle\"]')"), f.context);
  return { ...f, app: f.window.remapApp, started(action, requestId) { started.forEach(fn => fn({ action, requestId })); } };
}

// ---- copied from tools/tests/frontend-workflow.test.cjs:5058 (overviewDialogFixture) ----
// 審計員用鍵盤開關流程總覽：用正式 index.html 的總覽標記與 app.js 正式的開啟按鈕程式。
// f.key 照瀏覽器的順序送鍵：先跑 document 捕獲階段的監聽，再從目標往上冒泡。
// 假 DOM 的 focus() 不分元素能否取得焦點，實機焦點另由瀏覽器主機確認。
// 比對兩個假 DOM 節點一律用 assert.ok(a === b)：assert.equal 失敗時會序列化整棵循環的假 DOM，記憶體用盡而看似卡住。
// 第 0 批補：overviewDialogFixture({ charts: true }) 在載入 overview-bi.js 之前裝上假的 ECharts 與 ResizeObserver（f.charts），
// setTimeout 收進 f.timers，window 的監聽記在 f.windowListeners，f.resizeWindow() 送出 resize。沒開時和 wf 完全相同。
function overviewDialogFixture({ charts = false } = {}) {
  const f = usageJourneyFixture('validate', charts ? { dialogFocus: true, queueTimers: true } : { dialogFocus: true });
  f.window.getComputedStyle = () => ({ gridTemplateColumns: 'none' });
  f.window.addEventListener = () => {};
  if (charts) {
    f.charts = fakeCharts(); f.charts.install(f.window); f.windowListeners = [];
    f.window.addEventListener = (type, fn) => { f.windowListeners.push({ type, fn }); };
    f.resizeWindow = () => { for (const entry of f.windowListeners.filter(item => item.type === 'resize')) entry.fn({ type: 'resize' }); };
  }
  f.dom.document.contains = node => f.dom.content.contains(node);
  vm.runInContext(fs.readFileSync(path.join(root, 'overview-bi.js'), 'utf8'), f.context, { filename: 'overview-bi.js' });
  const html = fs.readFileSync(path.join(root, '..', 'index.html'), 'utf8');
  const markupStart = html.indexOf('<div class="overview-modal" hidden>'), markupEnd = html.indexOf('<!-- 開發工具', markupStart);
  assert.ok(markupStart >= 0 && markupEnd > markupStart, 'overview markup seam');
  f.dom.content.innerHTML = '<header><button type="button" data-action="overview-open">總覽</button></header>' + html.slice(markupStart, markupEnd);
  const listeners = [], captureOf = options => options === true || !!(options && options.capture);
  const same = (type, fn, capture) => entry => entry.type === type && entry.fn === fn && entry.capture === capture;
  f.dom.document.addEventListener = (type, fn, options) => { if (!listeners.some(same(type, fn, captureOf(options)))) listeners.push({ type, fn, capture: captureOf(options) }); };
  f.dom.document.removeEventListener = (type, fn, options) => { const index = listeners.findIndex(same(type, fn, captureOf(options))); if (index >= 0) listeners.splice(index, 1); };
  f.dom.document.body = f.dom.content.appendChild(f.dom.element('body'));
  f.keyListeners = () => listeners.filter(entry => entry.type === 'keydown' && entry.capture).length;
  f.key = (key, extra = {}) => {
    const target = extra.target || f.dom.document.activeElement || f.dom.document.body;
    const event = { type: 'keydown', key, shiftKey: !!extra.shiftKey, target, defaultPrevented: false, stopped: false,
      preventDefault() { this.defaultPrevented = true; }, stopPropagation() { this.stopped = true; } };
    for (const entry of listeners.slice()) if (!event.stopped && entry.type === 'keydown' && entry.capture && listeners.includes(entry)) entry.fn(event);
    for (let node = target; node && !event.stopped; node = node.parentElement) { event.currentTarget = node; for (const fn of (node.handlers.keydown || []).slice()) fn(event); }
    return event;
  };
  const source = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
  const openerStart = source.indexOf('    var overviewOpenBtn = document.querySelector(\'[data-action="overview-open"]\');');
  const openerEnd = source.indexOf('    bindOverviewDialog();', openerStart);
  assert.ok(openerStart >= 0 && openerEnd > openerStart, 'overview opener seam');
  vm.runInContext(source.replace("  if (document.readyState === 'loading') {",
    '  window.overviewApp = { renderOverview: renderOverview, bindOpen: function () {\n' + source.slice(openerStart, openerEnd) +
    '    bindOverviewDialog();\n  } };\n  if (document.readyState === \'loading\') {'), f.context, { filename: 'app.js' });
  f.window.overviewApp.bindOpen();
  f.store.subscribe(s => f.window.overviewApp.renderOverview(s));
  return f;
}

// ---- copied from tools/tests/frontend-workflow.test.cjs:956 (validationFixture) ----
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

// ---- copied from tools/tests/frontend-workflow.test.cjs:2504 (batch7Dom) ----
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

// ---- copied from tools/tests/frontend-workflow.test.cjs:2681 (batch7ControlDom) ----
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

// ---- copied from tools/tests/frontend-workflow.test.cjs:2758 (batch8Fixture) ----
function batch8Fixture() {
  const requests = [], elements = new Map();
  const document = { readyState: 'loading', addEventListener() {}, querySelector: selector => elements.get(selector) || null,
    createElement() { return { textContent: '', get innerHTML() { return String(this.textContent).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); } }; } };
  const window = { setTimeout, clearTimeout, addEventListener() {}, JetFocus: { defer() {} }, JetApi: contractJetApi({}) };
  for (const action of ['validateRun', 'exportValidationArtifacts', 'exportAccountMappingTemplate', 'exportPrescreenReport', 'hostOpenFolder', 'queryDataPreview', 'querySourceQualityPage', 'prescreenRun']) {
    window.JetApi[action] = payload => new Promise((resolve, reject) => requests.push({ action, payload, resolve, reject }));
  }
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'account-mapping-editor.js', 'filter-values.js', 'overview-bi.js']) vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context);
  const modules = [
    ['validate', 'window.batch8Validation = { bind, detailView, VALIDATION_ITEMS, PRESCREEN_ITEMS, NULL_PRESCREEN_ITEMS, LOAD_MORE_SPECS, detailHtml, validationCardHtml, validationArtifactsHtml, validationOutputStatusHtml, prescreenArtifactHtml, prescreenCardHtml, statsBarHtml, pairCountStatus, hitCountStatus };'],
    ['filter', 'window.batch8Filter = { customPickerHtml, exampleListHtml, viewState, ruleControlsHtml, ruleSummaryLabel, readBackHtml, toWireDraft, compoundRuleHtml, scenarioBuilderHtml };'],
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
    "  window.batch8App = { flushMessageWrites, persistNewMessages, stepSummary, renderIdentityBadge, overviewStepFactsHtml, overviewPopulationHtml, overviewHtml, renderStepNav };\n  if (document.readyState === 'loading') {"), context);
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

// ---- copied from tools/tests/frontend-workflow.test.cjs:2799 (batch8ValidationData) ----
function batch8ValidationData(runId = 'v8') {
  const data = validationFixture();
  data.resultRef = { runId, generatedUtc: '2026-01-01T00:00:00Z' };
  data.stats = { glRowCount: 80, voucherCount: 12, totalDebit: 1250, totalCredit: 1250, net: 0, periodStart: '2025-01-01', periodEnd: '2025-12-31' };
  data.populationSummary = { raw: { rowCount: 100 }, effective: { rowCount: 80 }, excluded: { rowCount: 20, byPeriodCount: 15, byPostingStatusCount: 5 } };
  data.sourceQuality = { findingCount: 2, sampleRows: [{ category: 'nullPostDate', sourceLabel: 'Synthetic', sourceRowNumber: 7, documentNumber: 'JV-7', accountCode: '1001', description: 'Synthetic row' }] };
  return data;
}

// ---- copied from tools/tests/frontend-workflow.test.cjs:2808 (batch8Artifacts) ----
// 2026-10-15 前後端契合：正式後端的檔案大小欄位叫 bytes，畫面也只讀 bytes；原本的 sizeBytes 沒有人讀，拿掉後畫面不變。
function batch8Artifacts(runId = 'v8', stamp = '') {
  return ['validationReport', 'infReport'].map(kind => ({ artifactId: 'b8-' + kind + stamp, kind, fileName: kind + '.xlsx', fileState: 'present', stale: false,
    generatedUtc: '2026-01-01T00:00:00Z', sourceRef: { validationRunId: runId } }));
}

// ---- copied from tools/tests/frontend-workflow.test.cjs:4960 (remapPaper) ----
function remapPaper(id, stale = false) { return { artifactId: id, kind: 'workingPaper', fileName: id + '.xlsx', fileState: 'asPublished', stale,
  generatedUtc: '2026-01-01T00:00:00Z', sourceRef: { validationRunId: 'v1', scenarioRevision: 'r1', scenarioPositions: [1, 2, 3] } }; }

// ---- copied from tools/tests/frontend-workflow.test.cjs:5339 (remapPrescreen) ----
function remapPrescreen(runId) {
  return { resultRef: { runId }, creatorSummary: { creators: [], totalPreparerCount: 0 }, rareAccounts: { distinctAccountCount: 0, lowFrequencyAccountCount: 0, accounts: [] },
    postPeriodApproval: { count: 0 }, suspiciousKeywords: { count: 0 }, unexpectedAccountPair: { count: 0 }, trailingZeros: { count: 0, zerosThreshold: 6 },
    weekendActivity: { postingCount: 0, approvalCount: 0 }, holidayActivity: { postingCount: 0, approvalCount: 0 },
    // 使用者 2026-10-08 裁定移除傳票日期與回溯過帳，合成結果不再帶 backdatedPosting。
    blankDescription: { count: 0 }, nonAuthorizedPreparer: { count: 0 }, lowFrequencyPreparer: { count: 0 }, lowFrequencyAccount: { count: 0 } };
}

// ---- copied from tools/tests/frontend-mapping.test.cjs:13 (fixture，本模組改名為 mappingFixture) ----
// Load whole production scripts unchanged. Only DOM plumbing and backend timing are test doubles;
// rendering, event binding, cache lifetime and state transitions all execute production code.
function mappingFixture() {
  const pending = [];
  const window = { setTimeout, clearTimeout, setInterval: () => 1, clearInterval() {}, JetApi: contractJetApi({
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
  }) };
  const document = { querySelector: () => null, createElement() {
    return { textContent: '', get innerHTML() {
      return String(this.textContent).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    } };
  } };
  const context = vm.createContext({ window, document });
  for (const file of ['state.js', 'ui-core.js', 'steps/mapping-step.js']) {
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8').replace("  Ui.registerStep('mapping', render);", "  window.mappingWire = glCommitPayload; window.mappingEligibility = mappingCommitEligibility; Ui.registerStep('mapping', render);"), context, { filename: file });
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
        if (selector === '[data-rde-created-date]' && container.innerHTML.includes('data-rde-created-date')) return control(selector);
        if (selector === '[data-approval-same-date]' && container.innerHTML.includes('data-approval-same-date')) return control(selector);
        if (selector === '[data-approval-choice]' && container.innerHTML.includes('data-approval-choice')) return control(selector);
        if (/^\[data-bind="(manual-code-add|automatic-code-add|posting-value-add)"\]$/.test(selector)
            && container.innerHTML.includes(selector.slice(1, -1))) return control(selector);
        if (/^\[data-manual-(mode|blank)\]$/.test(selector) && container.innerHTML.includes(selector.slice(1, -1))) return control(selector);
        if (action.test(selector) && container.innerHTML.includes(selector.slice(1, -1))) return control(selector);
        return null;
      },
      querySelectorAll(selector) {
        if (selector === 'select[data-mapping-key]') {
          return Array.from(container.innerHTML.matchAll(/<select\b[^>]*data-mapping-key="([^"]+)"[^>]*>/g)).map(match => {
            const target = control('[data-mapping-key="' + match[1] + '"]');
            target.getAttribute = name => name === 'data-mapping-key' ? match[1] : null;
            target.hasAttribute = name => match[0].includes(name);
            return target;
          });
        }
        if (['[data-manual-assign]', '[data-posting-value]', '[data-manual-include]', '[data-rde-column]', '[data-rde-type]', '[data-manual-code-edit]'].includes(selector)) {
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
      approval(value) {
        // 傳票核准日只有一個下拉：值是 mode:unmapped、mode:sameAsPostDate 或 column:來源欄。
        const target = controls.get('[data-approval-choice]');
        assert.ok(target, 'Rendered approval choice must exist');
        target.value = value; events.get('[data-approval-choice]:change')();
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

// ---- copied from tools/tests/frontend-mapping.test.cjs:169 (profile) ----
const profile = value => ({ blankCount: 0, distinctCount: 1, values: [{ value, count: 2 }], truncated: false });

// ---- 第 0 批新增的夾具 ----

// 切出 index.html 的一段正式標記並拿掉 HTML 註解（假 DOM 不認得註解，會把註解文字當成畫面文字）。
// end 不包含在結果裡；需要結尾標籤時由呼叫端自己補。
function indexMarkup(start, end) {
  return sliceBetween(readWwwroot('index.html'), start, end).replace(/<!--[\s\S]*?-->/g, '');
}

// isReady 永遠回 true：接上核對以前，任何名稱都會變成記錄請求的函式，isReady() 因此碰巧是真值；這裡明確寫出來。
function recordingJetApi(requests, extra = {}) {
  return contractJetApi({ on() {}, off() {}, isReady: () => true, ...extra }, requests);
}

// 右側資料預覽（情境 21，第 2 批）：切出 index.html 的 aside.data-preview，載入 state、ui-core、data-preview，
// 呼叫正式的 Ui.initDataPreview。假 DOM 會冒泡、document 的監聽有作用（點面板外關閉「其他資料」選單）。
// compact：一開始視窗是否在 1360px 以下，f.media.lists[0].change(true/false) 模擬跨過斷點。
// queryDataPreview 等請求記在 f.requests，由測試 resolve 或 reject；setTimeout 收進 f.timers。
function dataPreviewFixture({ compact = false } = {}) {
  const dom = batch9TreeDom({ dialogFocus: true, bubbles: true }), requests = [], timers = timerQueue(), media = fakeMatchMedia(compact);
  const window = { setTimeout: timers.setTimeout, clearTimeout: timers.clearTimeout, matchMedia: media.matchMedia,
    innerWidth: compact ? 1280 : 1600, addEventListener() {}, removeEventListener() {}, JetFocus: { defer() {} },
    CSS: { escape: cssEscape }, JetApi: recordingJetApi(requests) };
  const context = vm.createContext({ window, document: dom.document, setTimeout: timers.setTimeout, clearTimeout: timers.clearTimeout });
  dom.document.body = dom.element('body');
  for (const file of ['state.js', 'ui-core.js', 'data-preview.js'])
    vm.runInContext(fs.readFileSync(path.join(root, file), 'utf8'), context, { filename: file });
  dom.content.innerHTML = indexMarkup('<aside class="data-preview"', '</aside>') + '</aside>';
  const store = window.JetStore, ui = window.JetUi;
  ui.initDataPreview();
  const $ = selector => dom.content.querySelector(selector);
  function request(action) { const found = requests.filter(r => r.action === action); assert.ok(found.length, action + ' requested'); return found.at(-1); }
  // 進入案件畫面：資料預覽第一次進入 workflow 時會自動讀目前的資料表。
  function enterWorkflow(project = { projectId: 'preview-synthetic', periodStart: '2025-01-01', periodEnd: '2025-12-31' }) {
    store.setProject(project); store.setView('workflow');
  }
  return { window, context, dom, store, ui, requests, timers, media, $, request, enterWorkflow,
    aside: () => $('[data-bind="data-preview"]'), body: () => $('[data-bind="data-preview-body"]') };
}

// 第三步經過 app.js 的 renderContent 重繪（情境 6，第 7 批）：建立在 usageJourneyFixture('mapping') 上，
// 載入整個 app.js 並用 exposeAt 掛出 render 與 renderContent，訂閱 Store 後每次通知都呼叫正式的 renderContent
// （它自己依 contentVersion 決定要不要重建）。setTimeout 收進 f.timers、事件會冒泡、元素有 attributes。
// 步驟內容畫在 .stepflow-item--current .stepflow-item__body 裡；f.renderCount() 是呼叫 renderContent 的次數，
// f.contentVersion() 讀目前的 contentVersion。options 原樣交給 usageJourneyFixture。
function mappingRenderFixture(options = {}) {
  const f = usageJourneyFixture('mapping', { dialogFocus: true, bubbles: true, queueTimers: true, ...options });
  f.window.addEventListener = () => {};
  if (!f.window.CSS) f.window.CSS = { escape: cssEscape };
  if (!f.dom.document.body) f.dom.document.body = f.dom.element('body');
  vm.runInContext(exposeAt(readWwwroot('js/app.js'), "  if (document.readyState === 'loading') {",
    '  window.mappingRenderApp = { render: render, renderContent: renderContent };\n'), f.context, { filename: 'app.js' });
  const app = f.window.mappingRenderApp, steps = f.store.STEPS;
  const mappingIndex = steps.findIndex(step => step.id === 'mapping');
  assert.ok(mappingIndex >= 0, 'mapping step exists');
  f.store.setView('workflow'); f.store.setStepIndex(mappingIndex);
  let renders = 0;
  function renderNow() { const state = f.store.getState(); renders++; app.renderContent(state, steps[state.currentStepIndex]); }
  f.store.subscribe(renderNow); renderNow();
  return { ...f, app, renderNow, renderCount: () => renders, contentVersion: () => f.store.getState().contentVersion,
    body: () => f.dom.content.querySelector('.stepflow-item--current .stepflow-item__body') };
}

// 訊息面板的「複製紀錄」（情境 23，第 10 批）：切出 index.html 的訊息面板標記，載入 app.js 並用 exposeAt 掛出
// copyRecentMessages 等函式，bindCopy() 在 app.js 內部執行正式的按鈕綁定，之後點按鈕就走正式流程。
// clipboard: 'accept' 時剪貼簿收下文字（f.clipboardWrites），'reject' 時拒絕，正式程式改走 execCommand('copy')（f.copyCommands
// 記下當時焦點元素的值）。logRecent 等請求記在 f.requests；setTimeout 收進 f.timers（按鈕文字 2.2 秒後還原）。
function messagePanelFixture({ clipboard = 'accept' } = {}) {
  const f = usageJourneyFixture('export', { queueTimers: true });
  f.window.addEventListener = () => {};
  f.window.document = f.dom.document;
  if (!f.dom.document.body) f.dom.document.body = f.dom.element('body');
  const clipboardWrites = [], copyCommands = [];
  f.window.navigator = { clipboard: { writeText(value) {
    if (clipboard === 'reject') return Promise.reject(new Error('synthetic clipboard denied'));
    clipboardWrites.push(value); return Promise.resolve();
  } } };
  f.dom.document.execCommand = command => {
    const active = f.dom.document.activeElement;
    copyCommands.push({ command, value: active ? active.value : null }); return true;
  };
  f.dom.content.innerHTML = indexMarkup('<aside class="messages', '</aside>') + '</aside>';
  const source = readWwwroot('js/app.js');
  const binding = sliceBetween(source, '    var copyMessagesButton = ', '    var supportLogButton = ');
  vm.runInContext(exposeAt(source, "  if (document.readyState === 'loading') {",
    '  window.messagePanelApp = { copyRecentMessages: copyRecentMessages, flushMessageWrites: flushMessageWrites,\n' +
    '    persistNewMessages: persistNewMessages, renderMessages: renderMessages, bindCopy: function () {\n' + binding + '    } };\n'),
  f.context, { filename: 'app.js' });
  const app = f.window.messagePanelApp;
  app.bindCopy();
  return { ...f, app, clipboardWrites, copyCommands, copyButton: () => f.$('[data-action="messages-copy"]') };
}

// ---- 第 11 批從情境檔收進來的通用能力（2026-10-14） ----

// 等 Promise 與 setImmediate 排隊的工作跑完：預設 6 輪。frontend-journey-picker、-export、-operations 原本各有一份相同的寫法。
// frontend-journey-step4 的 flush 預設 4 輪，和這裡不同，留在那個檔。
async function flush(times = 6) { for (let i = 0; i < times; i++) await settle(); }

// 送出的 payload 不帶 `__` 開頭的鍵（前端內部用的暫存欄位不能送到後端）。frontend-journey-case-setup、-step4、-picker、-export 原本各有一份。
const noDunderKeys = payload => !JSON.stringify(payload).includes('"__');

// 整個 JET 畫面（第 10 批建立，原本在 frontend-journey-picker、-export、-operations 各有一份相同的副本，第 11 批收進這裡）：
// 整個 index.html 的 body 標記與它列出的全部前端腳本（不含 ECharts 函式庫），正式的 jet-api.js 經假的 chrome.webview 收發，
// app.js 照瀏覽器在 DOMContentLoaded 執行正式的 init()。所以案件清單第一次讀取與 jet-api.js 的請求都看得到，
// 忙碌畫面的作業名稱也由 jet-api.js 真的送出請求時的 onRequestStarted 事件帶入。
// remapAppFixture 把 app.js 切成幾段載入、不跑 init()，看不到這些，所以第 10 批改用整個畫面。
// 送出的請求記在 f.posted（{ requestId, action, payload }），由測試用 f.reply 或 f.fail 回應，
// 回應走 jet-api.js 正式的 receive。autoReply 列出的 action 由夾具自動回成功（預設只有寫入訊息紀錄的 log.append）。
// 送出的資料、f.reply 與 autoReply 的回應都經過 action-contract.cjs 核對，要和正式後端的資料形狀一致。
// 計時器：setTimeout 收進 f.timers；setInterval 記在 f.intervals；performance.now 是 f.clock，advance(ms) 才會前進。
function fullAppFixture({ autoReply = { 'log.append': {} } } = {}) {
  const dom = batch9TreeDom({ bubbles: true, serialization: 'browser', dialogFocus: true, rootAttributes: { 'data-test-body': '' } });
  const timers = timerQueue(), clock = fakeClock(), intervals = [], posted = [], hostListeners = [], windowListeners = [];
  let nextRequest = 0;
  function deliver(message) { for (const fn of hostListeners.slice()) fn({ data: message }); }
  const window = {
    setTimeout: timers.setTimeout, clearTimeout: timers.clearTimeout,
    setInterval(fn, ms) { intervals.push({ id: intervals.length + 1, fn, ms, cleared: false }); return intervals.length; },
    clearInterval(id) { const entry = intervals.find(item => item.id === id); if (entry) entry.cleared = true; },
    performance: clock.performance,
    addEventListener(type, fn) { windowListeners.push({ type, fn }); }, removeEventListener() {},
    CSS: { escape: cssEscape },
    // vm 沒有版面：計算後樣式一律是一般定位、沒有格線。
    getComputedStyle: () => ({ gridTemplateColumns: 'none', position: 'static', display: 'block', getPropertyValue: () => '' }),
    crypto: { randomUUID: () => 'request-' + (++nextRequest) },
    chrome: { webview: {
      postMessage(request) {
        checkRequest(request.action, request.payload);
        posted.push(request);
        if (Object.hasOwn(autoReply, request.action)) {
          checkResponse(request.action, autoReply[request.action]);
          Promise.resolve().then(() => deliver({ requestId: request.requestId, ok: true, data: unwrap(autoReply[request.action]) }));
        }
      },
      addEventListener(type, fn) { if (type === 'message') hostListeners.push(fn); }
    } },
    console
  };
  window.document = dom.document;
  const context = vm.createContext({ window, document: dom.document, setTimeout: timers.setTimeout, clearTimeout: timers.clearTimeout, CSS: window.CSS, console });
  dom.document.body = dom.content;
  const html = readWwwroot('index.html');
  dom.content.innerHTML = html.slice(html.indexOf('<body>') + 6, html.indexOf('<noscript>')).replace(/<!--[\s\S]*?-->/g, '');
  // 假 DOM 沒有版面：標題列高度與訊息欄底線這兩個純呈現的量測，給固定的零大小。
  const box = () => ({ top: 0, bottom: 0, left: 0, right: 0, width: 0, height: 0 });
  for (const node of dom.content.querySelectorAll('.app-shell, .app-header, .case-summary, .toc-col, .messages')) {
    node.getBoundingClientRect = box;
    node.toggleAttribute = function (name, force) { const on = force === undefined ? !this.hasAttribute(name) : !!force; if (on) this.setAttribute(name, ''); else this.removeAttribute(name); return on; };
    node.style.setProperty = function (name, value) { this[name] = value; };
    node.clientHeight = 0;
  }
  // 訊息清單用 insertBefore、firstChild、lastChild 與 removeChild 逐則加入與修剪，假 DOM 沒有這幾個，照瀏覽器的意思補在這個節點上。
  const messageList = dom.content.querySelector('[data-bind="message-list"]');
  Object.defineProperty(messageList, 'firstChild', { get() { return this.children[0] || null; } });
  Object.defineProperty(messageList, 'lastChild', { get() { return this.children.at(-1) || null; } });
  messageList.insertBefore = function (node, reference) {
    const index = reference ? this.children.indexOf(reference) : -1;
    this.appendChild(node);
    if (index >= 0) { this.children.pop(); this.children.splice(index, 0, node); }
    return node;
  };
  messageList.removeChild = function (node) { node.remove(); return node; };
  const scripts = [...html.matchAll(/<script defer src="\.\/(js\/[^"]+)"><\/script>/g)].map(match => match[1]);
  assert.ok(scripts.length >= 15 && scripts.at(-1) === 'js/app.js', 'index.html lists the frontend scripts with app.js last');
  for (const file of scripts) vm.runInContext(fs.readFileSync(path.join(root, '..', file), 'utf8'), context, { filename: file });
  dom.document.dispatch('DOMContentLoaded', { target: dom.content });
  const store = window.JetStore;
  const $ = selector => dom.content.querySelector(selector);
  function sent(action) { return posted.filter(request => request.action === action); }
  function request(action) { const found = sent(action); assert.ok(found.length > 0, action + ' was sent'); return found.at(-1); }
  function target(requestOrAction) { return typeof requestOrAction === 'string' ? request(requestOrAction) : requestOrAction; }
  function reply(requestOrAction, data = {}) {
    const request = target(requestOrAction);
    checkResponse(request.action, data);
    deliver({ requestId: request.requestId, ok: true, data: unwrap(data) });
  }
  function fail(requestOrAction, error = {}, correlationId) { deliver({ requestId: target(requestOrAction).requestId, ok: false, error, correlationId }); }
  function emit(event, data) { deliver({ event, data }); }
  function click(selector) { const node = $(selector); assert.ok(node !== null, selector + ' rendered'); assert.equal(node.disabled, false, selector + ' enabled'); node.dispatch('click'); }
  const overlay = {
    shown: () => !$('[data-bind="busy-overlay"]').hidden,
    label: () => $('[data-bind="busy-label"]').textContent,
    elapsed: () => { const node = $('[data-bind="busy-elapsed"]'); return node.hidden ? null : node.textContent; },
    guidance: () => { const node = $('[data-bind="busy-guidance"]'); return node.hidden ? null : node.textContent; },
    cancel: () => { const node = $('[data-action="cancel-operation"]'); return node.hidden ? null : { text: node.textContent, disabled: node.disabled }; }
  };
  return { window, context, dom, store, timers, clock, intervals, posted, windowListeners, overlay, $, sent, request, reply, fail, emit, click,
    ui: window.JetUi, api: window.JetApi, state: () => store.getState(),
    messages: () => plain(store.getState().messages.map(m => ({ text: m.text, level: m.level }))),
    liveIntervals: () => intervals.filter(entry => !entry.cleared) };
}

// 開啟 JET：連線檢查、SQL Server 設定、目前使用者與本機案件清單都回來，畫面停在案件清單。
async function bootFullApp(projects = [], options) {
  const f = fullAppFixture(options);
  f.reply('system.ping', { devToolsEnabled: false }); await flush();
  f.reply('system.databaseInfo', { sqlServer: { configured: false, reachable: false, summary: '' } });
  // 正式後端一定回身分物件；本機案件沒有線上使用者目錄時編號是 null（2026-10-15 前後端契合前這裡回 null）。
  f.reply('system.whoAmI', { principal: 'synthetic.auditor', shortName: '合成審計員', userNumber: null, numberSource: 'unavailable' });
  f.reply('project.listLocal', { projects }); await flush();
  return f;
}

module.exports = {
  // 共用常數與小工具
  wwwroot, root, settle, plain, copy, readWwwroot, sliceBetween, exposeAt, indexMarkup, cssEscape,
  timerQueue, fakeClock, fakeCharts, fakeMatchMedia, recordingJetApi,
  // 假 DOM
  batch9TreeDom, batch7Dom, batch7ControlDom,
  // 合成資料
  savedG, validationFixture, batch8ValidationData, batch8Artifacts, remapPaper, remapPrescreen, profile,
  // 從 wf、mp 複製的夾具
  usageJourneyFixture, filterOperationFixture, remapFilterPreviewFixture, remapAppFixture, overviewDialogFixture, batch8Fixture, mappingFixture,
  // 第 0 批新增的夾具
  dataPreviewFixture, mappingRenderFixture, messagePanelFixture,
  // 第 11 批從情境檔收進來的通用能力
  flush, noDunderKeys, fullAppFixture, bootFullApp
};
