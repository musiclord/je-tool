(function (root) {
  'use strict';

  // 合成顯示資料只存在開發預覽。數字、日期、條件與查詢答案仍取自原本的固定素材。
  const projectName = 'FY2025_GroupConsolidatedFinancialStatements_SubstantiveJournalEntryTesting_FinalReview';
  const scenarioName = '合成集團年度財務報表查核：期末調整分錄、跨部門往來及集中入帳交易之日期與金額檢視（供查核小組交叉核對之完整情境名稱，含海外營運單位及個別公司補充說明）';
  const rationale = '合成情境說明：依查核期間的日期與金額條件檢視分錄，並記錄共同檢視事項。此段長文字用於確認換行、操作按鈕與完整內容的閱讀；案例名稱包含王小明、Đặng Thị Ngọc Hân 及 👩🏽‍💻 合成覆核者。';
  const conditionValue = 'ERP20251231ConsolidationAdjustmentIntercompanyReconciliationYearEndAccrualRegionalFinanceReviewReference00001284';

  function modeFrom(search) {
    const mode = new URLSearchParams(search || '').get('data');
    return ['worst', 'worst-input'].includes(mode) ? mode : 'demo';
  }

  function prepareBundle(source, mode) {
    const bundle = structuredClone(source);
    if (mode === 'demo') return bundle;
    const loaded = bundle.loaded || {};
    if (loaded.project) {
      loaded.project.projectId = projectName;
      loaded.project.clientName = '合成國際精密製造與數位服務股份有限公司台灣營運總部暨海外投資控股事業群';
      loaded.project.projectCode = 'AUDIT2025GROUPCONSOLIDATEDFINANCIALSTATEMENTSFINALREVIEW001';
      loaded.project.operatorId = 'PREVIEW\\alexandrawisniewskakowalczyk';
    }
    (loaded.reports || []).forEach((report, index) => {
      report.fileName = projectName + '_20250101-20251231_' + report.kind + '_ReviewCopy_v' + (index + 1) + '.xlsx';
    });
    (bundle.fixtures || []).forEach(fixture => {
      fixture.scenario.name = scenarioName;
      fixture.scenario.rationale = rationale;
      if (fixture.preview && fixture.preview.scenario) fixture.preview.scenario.name = scenarioName;
    });
    // 重用已有固定答案的條件，只改顯示名稱；開啟這兩筆的查詢仍須通過 replay 的完整比對。
    if (bundle.fixtures && bundle.fixtures[0]) {
      const saved = structuredClone(bundle.fixtures[0].scenario);
      loaded.filterScenarios = [saved, { ...structuredClone(saved), name: 'J', rationale: '' }];
    }
    return bundle;
  }

  function draftFor(scenario, mode) {
    const draft = structuredClone(scenario);
    if (mode !== 'worst-input') return draft;
    draft.groups[0].conditions.push({ type: 'fieldValue', field: 'description', operator: 'contains',
      values: [conditionValue], join: 'AND', includeBlank: false });
    // 加入條件後不再匹配 prepared response，bootstrap 不查詢也不呈現舊答案。
    delete draft.editorOrigins;
    return draft;
  }

  const api = { modeFrom, prepareBundle, draftFor };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.JetPreviewData = api;
})(typeof window === 'undefined' ? globalThis : window);
