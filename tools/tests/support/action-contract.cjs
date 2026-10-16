'use strict';
// 前端測試的假後端用正式後端的資料形狀核對自己（docs/history/specs/2026-10-15-motion-and-verification-plan.md「前後端契合」）。
//
// tools/tests/fixtures/action-shapes.json 由 C# 的 ActionShapeContractTests 產生：正式的 dispatcher 照審計員的順序走一遍
// 合成案件，SQLite 與 DuckDB 各一次，記下每個 action 收到的資料與回應的欄位和型別。這裡用它核對兩件事：
//   checkResponse(action, data)：假回應的每個欄位都要在正式回應裡出現過，型別也要出現過。欄位可以少，不可以多。
//   checkRequest(action, payload)：前端送出的每個欄位都要是正式後端收過的，型別相同；後端每次都收到的欄位不能少。
// 對不上就丟出錯誤，所以前端測試不能假造後端不會回的欄位，也不能送後端沒收過的欄位。
// 刻意假造的失敗或極端狀態用 unchecked('原因', data) 包起來，就不核對；原因寫在呼叫的地方。
// 核對的是形狀，不是值；審計結果仍由各自算獨立答案的測試判定。
//
// 對不上的錯誤可能在前端的請求流程裡丟出，被畫面當成後端失敗處理掉，測試本身看不到。
// 所以每次對不上也記在這個程序裡，由 node --test 在每個測試結束時判定那個測試失敗。
const fs = require('node:fs');
const path = require('node:path');

const CONTRACT_PATH = path.join(__dirname, '..', 'fixtures', 'action-shapes.json');
const contract = JSON.parse(fs.readFileSync(CONTRACT_PATH, 'utf8'));
const UNCHECKED = Symbol('unchecked fake response');
const mismatches = [];

function reportMismatches(context) {
  if (!mismatches.length) return;
  const found = mismatches.splice(0);
  const message = '這個測試的假後端和正式後端對不上，共 ' + found.length + ' 處：\n' + found.join('\n');
  // 測試先因為別的原因失敗時，node --test 不再顯示 afterEach 的錯誤；同時寫到 stderr 並標明測試名稱，原因一定看得到。
  process.stderr.write('「' + (context && context.name ? context.name : '測試檔收尾') + '」' + message + '\n');
  throw new Error(message);
}
// 只在主程式是測試檔時註冊：node --test 為每個測試檔另開程序並以測試檔為主程式，直接用 node 執行測試檔也一樣。
// 測試自己再開的子程序（node -e）會繼承 NODE_TEST_CONTEXT，主程式卻不是測試檔；在那裡註冊會讓測試框架把資料寫進子程序的輸出。
if (require.main && /\.test\.cjs$/.test(require.main.filename)) {
  const { afterEach, after } = require('node:test');
  afterEach(reportMismatches);
  after(reportMismatches);
}

// jet-api.js 的 toMethodName：'query.filterVoucherPage' → 'queryFilterVoucherPage'。
function toMethodName(action) {
  const parts = action.split('.');
  return parts[0] + parts.slice(1).map(part => part.charAt(0).toUpperCase() + part.slice(1)).join('');
}
const actionsByMethod = new Map(Object.keys(contract.actions).map(action => [toMethodName(action), action]));
// jet-api.js 裡不是 action 的成員，假後端照常提供，不核對。
const NOT_ACTIONS = new Set(['on', 'off', 'isReady', 'invoke', 'onRequestStarted', 'then']);

function kindOf(value) {
  if (value === null) return 'null';
  if (Array.isArray(value)) return 'array';
  return typeof value;
}

// 契約檔裡只會是純量的位置寫成型別字串（'string|null'），可能是物件或陣列的位置寫成 { kind, fields, optional, items, values }。
function kindsOf(node) {
  return (typeof node === 'string' ? node : node.kind).split('|');
}

function compare(value, node, where, problems, requireAll) {
  const kind = kindOf(value), kinds = kindsOf(node);
  if (!kinds.includes(kind)) {
    problems.push(where + ' 是 ' + kind + '；正式後端在這裡只出現過 ' + kinds.join('、'));
    return;
  }
  if (kind === 'object') {
    if (Object.hasOwn(node, 'values')) {
      for (const [key, item] of Object.entries(value)) {
        if (node.values) compare(item, node.values, where + '.' + key, problems, requireAll);
        else problems.push(where + '.' + key + '：正式後端在這裡只送過空物件');
      }
    } else {
      for (const [key, item] of Object.entries(value)) {
        if (!Object.hasOwn(node.fields, key)) problems.push(where + '.' + key + ' 不在正式後端的資料裡');
        else compare(item, node.fields[key], where + '.' + key, problems, requireAll);
      }
      if (requireAll) {
        const optional = new Set(node.optional || []);
        for (const key of Object.keys(node.fields)) {
          if (!optional.has(key) && !Object.hasOwn(value, key)) problems.push(where + '.' + key + ' 是正式後端每次都收到的欄位，這裡沒有送');
        }
      }
    }
  }
  if (kind === 'array') {
    value.forEach((item, index) => {
      if (node.items) compare(item, node.items, where + '[' + index + ']', problems, requireAll);
      else problems.push(where + '[' + index + ']：正式後端在這裡只出現過空陣列');
    });
  }
}

function resolveAction(name) {
  if (Object.hasOwn(contract.actions, name)) return name;
  return actionsByMethod.get(name) || null;
}

// 送到後端的資料先經過 JSON（WebView2 的 postMessage）；undefined 欄位不會送出。
function wireCopy(value) {
  return value === undefined ? {} : JSON.parse(JSON.stringify(value));
}

function fail(action, side, problems) {
  const error = new Error('前端測試的' + side + '和正式後端對不上（' + action + '）：\n  ' + problems.slice(0, 12).join('\n  ') +
    (problems.length > 12 ? '\n  …另有 ' + (problems.length - 12) + ' 項' : '') +
    '\n正式後端的形狀在 tools/tests/fixtures/action-shapes.json，由 ActionShapeContractTests 產生。');
  error.code = 'ACTION_CONTRACT_MISMATCH';
  mismatches.push(error.message);
  return error;
}

function checkRequest(name, payload) {
  const action = resolveAction(name);
  if (!action) throw fail(name, '送出的資料', ['正式後端沒有這個 action 的紀錄；在 ActionShapeJourney 補上審計員會走到的那一步']);
  const problems = [];
  compare(wireCopy(payload), contract.actions[action].request, 'payload', problems, true);
  if (problems.length) throw fail(action, '送出的資料', problems);
}

function checkResponse(name, data) {
  if (isUnchecked(data)) return;
  const action = resolveAction(name);
  if (!action) throw fail(name, '假回應', ['正式後端沒有這個 action 的紀錄；在 ActionShapeJourney 補上審計員會走到的那一步']);
  const problems = [];
  compare(wireCopy(data), contract.actions[action].response, 'data', problems, false);
  if (problems.length) throw fail(action, '假回應', problems);
}

// 刻意不符合正式回應的假資料（失敗、損毀或極端狀態），寫明原因才可以不核對。
function unchecked(reason, data) {
  if (typeof reason !== 'string' || reason.trim().length < 4) throw new Error('unchecked() 要寫明這筆假資料為什麼不核對');
  return { [UNCHECKED]: reason, data };
}
function isUnchecked(value) { return value !== null && typeof value === 'object' && Object.hasOwn(value, UNCHECKED); }
function unwrap(value) { return isUnchecked(value) ? value.data : value; }

// 包住假的 JetApi：每次呼叫先核對送出的資料，回應核對後才交給前端。
// 沒有寫好的方法時，和 recordingJetApi 一樣把請求記進 requests，由測試呼叫 resolve 或 reject。
// 測試之後才指定的方法（f.window.JetApi.x = ...）也經過同一層核對。
function contractJetApi(target = {}, requests = null) {
  return new Proxy(target, {
    get(object, name) {
      // 繼承來的成員（toString、constructor 等）不是 action，照原樣交出。
      if (typeof name !== 'string' || NOT_ACTIONS.has(name) || (!Object.hasOwn(object, name) && name in object)) return object[name];
      const own = object[name];
      if (own !== undefined && typeof own !== 'function') return own;
      if (!own && !requests) return undefined;
      return function (payload) {
        checkRequest(name, payload);
        if (!own) {
          return new Promise((resolve, reject) => requests.push({
            action: name, payload, reject,
            resolve(data) { checkResponse(name, data); resolve(unwrap(data)); }
          }));
        }
        const result = own.apply(this, arguments);
        if (!result || typeof result.then !== 'function') { checkResponse(name, result); return unwrap(result); }
        return result.then(data => { checkResponse(name, data); return unwrap(data); });
      };
    }
  });
}

module.exports = { contract, checkRequest, checkResponse, unchecked, unwrap, isUnchecked, contractJetApi, toMethodName, CONTRACT_PATH };
