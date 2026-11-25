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
  // 科目清單只回事先準備好的那一頁。照準備時的請求（每頁 100 個、不帶分類）一定回它；
  // 這一頁已列出全部科目（沒有下一頁）時，第四步與第五步每頁 200 個以內的請求也回它。指定分類時只列出這一頁裡分類相同的科目；
  // 這個分類有下層分類時，預覽不推算下層包含哪些科目，照樣拒絕。帶搜尋字或游標的請求一律拒絕。
  function accountMappingPage(bundle, p) {
    const page = bundle.accountMappingPage;
    if (!page || Object.keys(p).some(key => !['cursor', 'pageSize', 'search', 'categoryId'].includes(key)) || p.cursor != null || p.search) return null;
    if (p.categoryId == null && (p.pageSize == null || p.pageSize === 100)) return structuredClone(page);
    const rows = page.rows || [];
    if (page.nextCursor != null || !Number.isInteger(p.pageSize) || p.pageSize < rows.length || p.pageSize > 200) return null;
    if (p.categoryId == null) return structuredClone(page);
    const categories = (bundle.loaded && bundle.loaded.accountCategory && bundle.loaded.accountCategory.categories) || [];
    if (categories.some(category => category.parentCategoryId === p.categoryId)) return null;
    const matched = rows.filter(row => row.categoryId === p.categoryId);
    return structuredClone({ ...page, rows: matched, nextCursor: null, totalCount: matched.length });
  }
  function dispatch(bundle, action, payload) {
    const p = payload || {};
    if (action === 'system.ping') return { devToolsEnabled: false };
    // 啟動時原本也查 SQL Server 設定、開案後送心跳，身分也帶使用者編號；都隨 SQL Server 移除（裁定 2026-11-06-08）。
    if (action === 'system.whoAmI') return { principal: 'PREVIEW\\auditor', shortName: 'auditor（範例）' };
    if (action === 'project.listLocal') return { projects: [] };
    if (action === 'mapping.valueProfile') {
      const prepared = (bundle.mappingValueProfiles || []).find(item => canonical(item.payload) === canonical(p));
      if (!prepared) throw new Error('此來源值查詢尚未準備合成回應；預覽不會自行判定代碼是否相同。');
      return structuredClone(prepared.response);
    }
    if (action === 'query.accountMappingPage') {
      const accounts = accountMappingPage(bundle, p);
      if (accounts) return accounts;
    }
    const allowed = ['filter.preview', 'query.filterVoucherPage', 'query.filterVoucherRowsPage'];
    if (!allowed.includes(action)) throw new Error('此操作需在 JET 桌面程式執行；目前頁面僅供畫面預覽。');
    const keys = ['populationScope', 'scenario'];
    if (action !== 'filter.preview') keys.push('pageSize', 'cursor', 'sort', 'search', 'queryRevision');
    if (action === 'query.filterVoucherRowsPage') keys.push('voucherNumber');
    if (Object.keys(p).some(key => !keys.includes(key)) || p.populationScope !== 'auditPeriod' ||
        p.cursor != null || p.sort != null || (p.search != null && p.search !== '') ||
        (p.pageSize != null && p.pageSize !== 50)) throw new Error('此查詢尚未準備合成回應；預覽不會自行計算或排序。');
    const fixture = bundle.fixtures.find(item => scenarioKey(item.scenario) === scenarioKey(p.scenario));
    if (!fixture) throw new Error('條件已改變，沒有對應的合成答案。可繼續調整畫面，或切回上方的固定情境。');
    if (p.queryRevision != null && p.queryRevision !== fixture.page.queryRevision) throw new Error('合成結果版本不符，請重新載入固定情境。');
    if (action === 'filter.preview') return structuredClone(fixture.preview);
    if (action === 'query.filterVoucherPage') return structuredClone(fixture.page);
    if (!fixture.detail || p.voucherNumber !== 'DEMO-B' || p.queryRevision !== fixture.page.queryRevision)
      throw new Error('此傳票尚未準備合成明細。');
    return structuredClone(fixture.detail);
  }
  const api = { dispatch, scenarioKey };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.JetPreviewReplay = api;
})(typeof window === 'undefined' ? globalThis : window);
