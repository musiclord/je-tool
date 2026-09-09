(function (root) {
  'use strict';
  // This module replays exact prepared requests. It never evaluates audit conditions.
  function canonical(value) {
    if (Array.isArray(value)) return '[' + value.map(canonical).join(',') + ']';
    if (value && typeof value === 'object') return '{' + Object.keys(value).sort().map(key => JSON.stringify(key) + ':' + canonical(value[key])).join(',') + '}';
    return JSON.stringify(value);
  }
  function scenarioKey(scenario) {
    // Name/rationale do not affect matching. Everything else remains part of the contract.
    const copy = JSON.parse(JSON.stringify(scenario || {}));
    delete copy.name; delete copy.rationale;
    return canonical(copy);
  }
  function dispatch(bundle, action, payload) {
    const p = payload || {};
    if (action === 'system.ping') return { devToolsEnabled: false };
    if (action === 'system.databaseInfo') return {};
    if (action === 'system.whoAmI') return { principal: 'PREVIEW\\auditor', shortName: 'auditor（範例）', userNumber: null, numberSource: 'unavailable' };
    if (action === 'project.listLocal') return { projects: [], online: null };
    if (action === 'project.heartbeat') return {};
    const allowed = ['filter.preview', 'query.filterVoucherPage', 'query.filterVoucherRowsPage'];
    if (!allowed.includes(action)) throw new Error('設計預覽未提供此操作：' + action + '。正式功能請在 JET 驗證。');
    const keys = ['populationScope', 'scenario'];
    if (action !== 'filter.preview') keys.push('pageSize', 'cursor', 'sort', 'search', 'queryRevision');
    if (action === 'query.filterVoucherRowsPage') keys.push('documentNumber');
    if (Object.keys(p).some(key => !keys.includes(key)) || p.populationScope !== 'auditPeriod' ||
        p.cursor != null || p.sort != null || (p.search != null && p.search !== '') ||
        (p.pageSize != null && p.pageSize !== 50)) throw new Error('此查詢尚未準備合成回應；預覽不會自行計算或排序。');
    const fixture = bundle.fixtures.find(item => scenarioKey(item.scenario) === scenarioKey(p.scenario));
    if (!fixture) throw new Error('條件已改變，沒有對應的合成答案。可繼續調整畫面，或切回上方的固定情境。');
    if (p.queryRevision != null && p.queryRevision !== fixture.page.queryRevision) throw new Error('合成結果版本不符，請重新載入固定情境。');
    if (action === 'filter.preview') return structuredClone(fixture.preview);
    if (action === 'query.filterVoucherPage') return structuredClone(fixture.page);
    if (!fixture.detail || p.documentNumber !== 'DEMO-B' || p.queryRevision !== fixture.page.queryRevision)
      throw new Error('此傳票尚未準備合成明細。');
    return structuredClone(fixture.detail);
  }
  const api = { dispatch, scenarioKey };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.JetPreviewReplay = api;
})(typeof window === 'undefined' ? globalThis : window);
