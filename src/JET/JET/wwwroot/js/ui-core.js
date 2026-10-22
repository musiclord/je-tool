/*
  前端共用核心（JetUi namespace）。
  持有：DOM 輔助、run() 包裝、流程閘門與導航、統一步驟頁尾、
  步驟渲染器註冊表與共用契約鏡像常數。
  各步驟渲染器以獨立檔案自行註冊（js/steps/*.js）；本檔不承載任何權威業務規則。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;

  // 步驟可以獨立載入；有動畫時由殼層排除淡出複本，沒有動畫時維持一般查找。
  function findLive(root, selector) {
    return global.JetMotion && global.JetMotion.findLive ? global.JetMotion.findLive(root, selector) :
      (root ? root.querySelector(selector) : null);
  }

  // 所有日期輸入沿用建立案件的原生日期框；條件清單的多種日期寫法仍由貼上清單處理。
  function dateInputHtml(attributes) {
    return '<input type="date" class="form__input date-input"' + controlAttributes(attributes) + '>';
  }

  function controlAttributes(attributes) {
    return Object.keys(attributes || {}).map(function (key) {
      var value = attributes[key];
      return value === false || value == null ? '' : ' ' + key + (value === true ? '' : '="' + esc(value) + '"');
    }).join('');
  }

  function loadFailureHtml(message, retryAttributes, attributes) {
    return '<div class="form-notice" data-load-failure role="alert"' + controlAttributes(attributes) + '><span>' + esc(message) + '</span>' +
      (retryAttributes ? ' <button type="button" class="btn btn--ghost btn--tiny"' + controlAttributes(retryAttributes) + '>重新載入</button>' : '') + '</div>';
  }

  function searchBoxHtml(label, options) {
    options = options || {};
    return '<form class="page-search' + (options.className ? ' ' + esc(options.className) : '') + '" data-search-box' + controlAttributes(options.attributes) + '>' +
      '<label class="page-search__label"><span>' + esc(label) + '</span>' +
      '<input type="search" class="form__input page-search__input" data-search-input maxlength="' + (options.maxLength || 200) +
      '" placeholder="輸入一部分即可" value="' + esc(options.value || '') + '"' + controlAttributes(options.inputAttributes) + '></label>' +
      '<button type="submit" class="btn btn--ghost btn--tiny"' + (options.disabled ? ' disabled' : '') + '>搜尋</button>' +
      '<button type="button" class="btn btn--ghost btn--tiny" data-search-clear data-collapsible-inline' + controlAttributes(options.clearAttributes) +
      (options.value ? '' : ' hidden') + (options.disabled ? ' disabled' : '') + '>清除</button></form>';
  }

  function bindSearchBox(form, submit, inputChanged) {
    if (!form) { return; }
    var input = form.querySelector('[data-search-input]'), clear = form.querySelector('[data-search-clear]');
    form.addEventListener('submit', function (event) {
      event.preventDefault(); submit(input.value.trim(), false);
    });
    input.addEventListener('input', function () {
      clear.hidden = !input.value;
      if (inputChanged) { inputChanged(input.value); }
    });
    clear.addEventListener('click', function () {
      if (global.JetMotion && typeof global.JetMotion.shake === 'function') { global.JetMotion.shake(input); }
      input.value = ''; clear.hidden = true;
      if (inputChanged) { inputChanged(''); }
      submit('', true);
    });
  }

  /* ---- 共用契約鏡像（key/label 對齊後端 action 契約） ------ */

  // GL 借方與貸方代碼都是字面值，不是來源欄位 select。
  var GL_FIELDS = [
    { key: 'docNum', label: '傳票號碼', req: 'always' },
    { key: 'lineID', label: '傳票文件項次', req: 'optional' },
    { key: 'postDate', label: '總帳日期', req: 'always' },
    { key: 'docDate', label: '傳票核准日', req: 'optional' },
    { key: 'accNum', label: '會計科目編號', req: 'always' },
    { key: 'accName', label: '會計科目名稱', req: 'always' },
    { key: 'description', label: '傳票摘要', req: 'always' },
    { key: 'jeSource', label: '分錄來源模組', req: 'optional' },
    { key: 'createBy', label: '傳票建立人員', req: 'optional' },
    { key: 'approveBy', label: '傳票核准人員', req: 'optional' },
    { key: 'manual', label: '人工/自動分錄', req: 'optional' },
    { key: 'amount', label: '金額', req: ['signed', 'side', 'flag'] },
    { key: 'debitAmount', label: '借方金額', req: ['dual'] },
    { key: 'creditAmount', label: '貸方金額', req: ['dual'] },
    { key: 'dcField', label: '借貸別', req: ['side', 'flag'] },
    { key: 'dcDebitCode', label: '借方代碼', req: ['side', 'flag'], literal: true },
    { key: 'dcCreditCode', label: '貸方代碼', req: ['side', 'flag'], literal: true },
    { key: 'postingStatus', label: '過帳狀態', req: 'optional' }
  ];

  var TB_FIELDS = [
    { key: 'accNum', label: '會計科目編號', req: 'always' },
    { key: 'accName', label: '會計科目名稱', req: 'always' },
    { key: 'amount', label: '年度變動金額', req: ['direct'] },
    { key: 'debitAmt', label: '借方金額', req: ['debitCredit'] },
    { key: 'creditAmt', label: '貸方金額', req: ['debitCredit'] },
    { key: 'openingBalance', label: '期初餘額', req: ['openClose'] },
    { key: 'closingBalance', label: '期末餘額', req: ['openClose'] },
    { key: 'openingDebit', label: '期初借方', req: ['openCloseBySide'] },
    { key: 'openingCredit', label: '期初貸方', req: ['openCloseBySide'] },
    { key: 'closingDebit', label: '期末借方', req: ['openCloseBySide'] },
    { key: 'closingCredit', label: '期末貸方', req: ['openCloseBySide'] }
  ];

  // GL 核准日三態（後端 mapping.commit.gl 接受的 approvalDateMode 固定值）。
  // mapped 必須且只可配「傳票核准日」來源欄；sameAsPostDate 直接沿用標準化後的總帳日期，
  // 兩者互斥由前端就近引導，後端 mapping.commit.gl 仍是權威。
  var GL_APPROVAL_DATE_MODES = [
    { value: 'unmapped', label: '—' },
    { value: 'mapped', label: '由來源欄提供' },
    { value: 'sameAsPostDate', label: '與總帳日期相同' }
  ];

  // 攸關資料元素（RDE）欄位型別（後端接受的 rdeFields.valueType 固定值）。
  var RDE_VALUE_TYPES = [
    { value: 'text', label: '文字' },
    { value: 'date', label: '日期' },
    { value: 'money', label: '金額' }
  ];

  // 後端 GlMappingOptionRules 的 UI 鏡像上限；後端仍是權威。
  var RDE_MAX_LABEL_LENGTH = 400;

  // 人工／自動代碼的後端預設（manualAutoPolicy 未提供時的值）。
  var MANUAL_AUTO_DEFAULTS = { manualValues: ['1'], automaticValues: ['0'] };

  // mapping.valueProfile 的有界取樣上限（action 的 limit 允許 1 到 100）。
  var VALUE_PROFILE_LIMIT = 50;
  // Domain各規則的既定門檻鏡像；只供預設值與文案使用，不在前端判斷命中。
  var PRESCREEN_DEFAULTS = { preparerMaxEntries: 11, accountMaxEntries: 11, trailingZeroDigits: 6 };
  // 三種展示用途各自命名，不改後端query.*Page預設200列，也不共用審計門檻。
  var RESULT_PREVIEW_PAGE_SIZE = 50;
  var TABLE_PAGE_SIZE = 200;
  var REPORT_HISTORY_PAGE_SIZE = 50;

  // 金額方式名稱不用「＋」連接，免得被讀成相加（使用者 2026-10-21）：TB 沿用 legacy 的按鈕名稱，GL 只有雙欄方式改用 legacy 名稱。
  // note 是選取後顯示的計算說明，只給要相減或依正負號判斷的方式；直接取用一欄的方式沒有說明。
  var GL_MODES = [
    { value: 'dual', label: '分別設定借貸金額', note: '配對借方與貸方金額欄位後，以借方減去貸方，計算金額。' },
    { value: 'signed', label: '單一帶號金額欄', note: '配對金額欄位後，正數視為借方，負數視為貸方。' },
    { value: 'side', label: '金額與借貸別', note: '配對金額與借貸別欄位後，依借貸別決定正負號，借方為正數，貸方為負數。' }
  ];

  var TB_MODES = [
    { value: 'debitCredit', label: '設定借方與貸方金額', note: '配對借方與貸方金額欄位後，以借方減去貸方，計算本期變動金額。' },
    { value: 'direct', label: '設定年度變動金額' },
    { value: 'openClose', label: '設定期初與期末金額', note: '配對期初與期末金額欄位後，以期末減去期初，計算本期變動金額。' },
    { value: 'openCloseBySide', label: '設定借方貸方之期初期末金額',
      note: '配對期初與期末的借方、貸方金額欄位後，各以借方減去貸方，再以期末減去期初，計算本期變動金額。' }
  ];

  // 進階篩選的條件型別。label 逐鍵鏡像 Domain FilterConditionLabels.RuleTypes；
  // quickLabel、group 與 accountMappingRequirement 是前端呈現／可用性 metadata。
  // accountMappingRequirement 逐條鏡像 target 內容資格，不以來源檔存在代替（權威驗證仍在後端）。
  // group 只剩 'kct' 有意義（風險樣態下拉的「KCT 專屬」分組）；其餘分組與 quickLabel 是舊挑選區的遺留 metadata，
  // 2026-09-07 改版後畫面改用三個家族（filter-step 的 RULE_FAMILIES）。value 是 AST 型別鍵（wire 契約），不隨標籤調整改動。
  var FILTER_RULE_TYPES = [
    { value: 'fieldValue', label: '欄位值比較', quickLabel: '指定值與排除', group: 'field' },
    { value: 'accountSide', label: '借貸科目分類', group: 'nature', accountMappingRequirement: 'any' },
    { value: 'prescreen', label: '預篩選', quickLabel: '選擇預篩選條件', group: 'risk' },
    { value: 'text', label: '文字條件', group: 'field' },
    { value: 'textSet', label: '文字值清單', group: 'field' },
    { value: 'numRange', label: '金額區間', group: 'field' },
    { value: 'dateRange', label: '日期區間', group: 'field' },
    { value: 'customKeywords', label: '自訂關鍵字', group: 'field' },
    { value: 'drCrOnly', label: '借貸限定', group: 'nature' },
    { value: 'manualAuto', label: '人工/自動', group: 'nature' },
    { value: 'customTrailingZeros', label: '金額尾數連續 0 的位數', group: 'pattern' },
    // 借貸科目組合：畫面上只有一張卡（specialAccountCategoryPair 為預設 wire 型別），五種白話模式跨兩個
    // wire 型別（ACCOUNT_COMBINATION_OPTIONS）；accountPair 只在型別下拉與舊情境讀回出現，不進快速加入。
    { value: 'accountPair', label: '借貸科目組合（看對方科目）', group: 'pattern', accountMappingRequirement: 'any', pickerHidden: true },
    { value: 'specialAccountCategoryPair', label: '借貸科目組合', group: 'pattern', accountMappingRequirement: 'any' },
    { value: 'customPreparerEntryCount', label: '自訂編製人員分錄筆數', group: 'pattern' },
    { value: 'customAccountEntryCount', label: '自訂科目分錄筆數', group: 'pattern' },
    { value: 'entityFrequency', label: '科目與人員統計', group: 'pattern' },
    { value: 'group', label: '條件括號', group: 'compound' },
    { value: 'voucher', label: '傳票分錄條件', group: 'compound' },
    // 攸關資料元素條件：舊式條件，只在舊情境讀回與編輯時出現，畫面不提供新增。
    { value: 'typed', label: '攸關資料元素條件', quickLabel: '攸關資料元素欄位條件', group: 'field' },
    // KCT 小組條件（清單 A/C/D/H/J；分組獨立於其他四組）。Revenue 型依內容事實逐條解鎖。
    { value: 'revenueDebitNearQuarterEnd', label: '季末前借記收入', group: 'kct', accountMappingRequirement: 'revenue' },
    { value: 'revenueWithoutNormalCounterpart', label: '收入無一般對方科目', group: 'kct', accountMappingRequirement: 'revenueAndCounterpart' },
    { value: 'manualRevenueEntry', label: '收入之人工分錄', group: 'kct', accountMappingRequirement: 'revenue' },
    { value: 'trailingDigits', label: '特定金額尾數', group: 'kct' },
    { value: 'preparerEqualsApprover', label: '編製與核准同一人', group: 'kct' }
  ];

  // 條件型別的分組（依審計意圖，非資料格式）。顯示順序即此陣列順序；
  // 每組約 3–4 項，讓使用者只看自己要的那一塊，不必一次面對全部選項。

  // KCT 小組「重用既有型別」的預設條件（清單 E/F/G/I）：點按鈕即帶入既有型別的預填規則，
  // 不另立 wire 型別（避免重複既有述詞，單一事實在後端述詞）。overrides 套在 newFilterRule 之上，
  // 加入時整份深拷貝。非營業日 I 是一條「條件括號」，括號內只有「總帳日期是非營業日」（排除補班日）；
  // 2026-10-02 使用者裁定 I 不再自成一組，和其他單規則 KCT 一樣加入目前條件組。
  // 鏡像 Domain SuspiciousKeywordDefaults；KCT 與常用查核共用同一份預填清單。
  var SUSPICIOUS_KEYWORD_DEFAULTS = ['ADJ', 'REV', 'RECLASS', 'SUSPENSE', 'ERROR', 'WRONG',
    '調整', '迴轉', '沖銷', '重分類', '避險', '重編', '錯誤', '計畫外', '預算外', '帳外',
    '调整', '回转', '冲销', '重分类', '避险', '重编', '错误', '计画外', '预算外'];

  var FILTER_KCT_PRESETS = [
    { key: 'kctSpecificPreparer', label: '特定人員建立之分錄',
      overrides: { type: 'fieldValue', field: 'createBy', operator: 'in', values: [] } },
    { key: 'kctSpecificKeywords', label: '特定摘要',
      overrides: { type: 'fieldValue', field: 'description', operator: 'contains', values: SUSPICIOUS_KEYWORD_DEFAULTS.slice() } },
    { key: 'kctBlankDescription', label: '空白摘要',
      overrides: { type: 'fieldValue', field: 'description', operator: 'isBlank' } },
    { key: 'kctTrailingDigits', label: '特定金額尾數',
      overrides: { type: 'fieldValue', field: 'amount', operator: 'endsWithDigits', value: '000000', amountBasis: 'absolute' } },
    { key: 'kctNonBusinessDay', label: '非營業日分錄',
      overrides: { type: 'group', rules: [
        { type: 'fieldValue', field: 'postDate', operator: 'isNonBusinessDay', join: 'AND' }
      ] } }
  ];

  // 新 KCT I 的條件括號直接使用日期「非營業日」判斷，排除補班日；舊 AST 不改寫。
  // 正本是 Domain FilterConditionLabels.NonBusinessDayAtom，後端 FilterConditionRenderer 以同一規則辨識。
  var FILTER_KCT_ATOM_LABELS = {
    kctNonBusinessDay: '非營業日（排除補班日）'
  };

  // KCT 小組方法學檢核清單：獨立顯著面板的「單一資料來源」，十顆按鈕由此一份資料驅動，
  // 而非十段重複 HTML，新增卡片只要加一筆資料。每筆只是「指向既有述詞」的標記：
  //   kind:'type'   → ref 是既有 FILTER_RULE_TYPES 的 value；點按帶入一條 newFilterRule(ref)。
  //   kind:'preset' → ref 是既有 FILTER_KCT_PRESETS 的 key；點按帶入該預設的一條規則（I 是條件括號）。
  //   kind:'spec' → 使用明確覆寫值，B 不預選分類。
  // label 為 KCT 清單用語（卡片顯示文字）。每張卡是可複選 toggle：選取即把其規格落地成 rule
  // 併入草稿、並在每條 rule 打上 __kctLetter 身分標記（UI-only，剝除後才送 wire）；取消即移除帶
  // 該字母標記的 rule。送出時由 marker 推導 source:'kct'；KCT 名稱／動機可沿用自動值、手改或留白，
  // 一般自訂情境仍必填。已存情境由 project.load 回傳 canonical source，重存不得遺失。
  // 不在此引入任何新 wire 型別／述詞；A/C/D/H/J 與 E/F/G/I 全部重用既有 type/preset。
  var FILTER_KCT_CHECKLIST = [
    { letter: 'A', defaultRationale: "期末前 X天內收入科目有迴轉分錄，表示收入認列過程中可能存在不適當的調整或更正(案件團隊須提供天數)", kind: 'type', ref: 'revenueDebitNearQuarterEnd', label: '在該季度前 X 天借記收入的會計分錄' },
    { letter: 'B', defaultRationale: "借記固定資產科目(如不動產、廠房和設備(PPE))但貸記營業費用或維修費用等費用科目之分錄組合不符合公司營業流程的瞭解，因此屬於為非預期的過帳，可能為舞弊類型分錄及其他調整的特質。", kind: 'spec', ref: 'specialAccountCategoryPair', overrides: { type: 'specialAccountCategoryPair', pairMode: 'drAndCr', categorySelection: 'subtree', debitCategoryIds: [], creditCategoryIds: [] },
      description: '借方選固定資產，貸方選營業費用；請先在第四步「分類設定」新增這兩類並配對科目。在建工程不要配到固定資產，也不要放在固定資產底下，因為預設包含下層分類。',
      label: '借記固定資產(PPE，不含在建)且貸記費用之分錄' },
    { letter: 'C', defaultRationale: "查核團隊已發現與收入科目相關的未預期或不尋常過帳(貸方為收入，但借方科目非為應收票據/帳款、預收貨款/合約負債)", description: '現金不算一般對方科目；要排除現金銷貨，改用預篩選條件『未預期借貸組合』。', kind: 'type', ref: 'revenueWithoutNormalCounterpart', label: '貸方為收入但借方非一般對方科目之分錄' },
    { letter: 'D', defaultRationale: "根據查核核團隊的瞭解，收入科目的過帳一般是自動產生，如為人工分錄非屬正常營業流程的會計分錄", kind: 'type', ref: 'manualRevenueEntry', label: '收入之人工分錄' },
    { letter: 'E', defaultRationale: "執行長、財務長及高階主管通常不預期會經手會計分錄編製或過帳。且因高階主管所處的位置常有能力能直接或間接操縱會計記錄來進行財務報表舞弊。誘因或壓力愈大，則愈有可能合理化其舞弊行為。", kind: 'preset', ref: 'kctSpecificPreparer', label: '特定人員(財務長/執行長/高階主管等)建立之分錄' },
    { letter: 'F', kind: 'preset', ref: 'kctSpecificKeywords', label: '特定摘要(如迴轉、調整等)' },
    { letter: 'G', kind: 'preset', ref: 'kctBlankDescription', label: '空白摘要' },
    { letter: 'H', kind: 'preset', ref: 'kctTrailingDigits', label: '特定尾數(如 999999/000000 結尾)' },
    { letter: 'I', kind: 'preset', ref: 'kctNonBusinessDay', label: '非營業日之分錄' },
    { letter: 'J', kind: 'type', ref: 'preparerEqualsApprover', label: '編製人員與核准人員相同' }
  ];

  // 預篩選 row-tag 鍵與中文名（代號已退役，一律用具體名稱）。
  // 本表的 value/label 鏡像 Domain FilterConditionLabels.PrescreenKeys（正本在 Domain），
  // 由 FilterConditionLabelMirrorTests 雙向守衛；新增鍵須同步 Domain 標籤表。
  var PRESCREEN_KEY_OPTIONS = [
    { value: 'postPeriodApproval', label: '財報準備日起核准' },
    { value: 'suspiciousKeywords', label: '摘要特定描述' },
    { value: 'unexpectedAccountPair', label: '未預期借貸組合', requiresAccountMapping: true },
    { value: 'trailingZeros', label: '金額尾數連續 ' + PRESCREEN_DEFAULTS.trailingZeroDigits + ' 個 0' },
    { value: 'weekendPosting', label: '週末過帳' },
    { value: 'weekendApproval', label: '週末核准' },
    { value: 'holidayPosting', label: '假日過帳' },
    { value: 'holidayApproval', label: '假日核准' },
    { value: 'blankDescription', label: '空白摘要' },
    // requiresCreateBy：後端要求「傳票建立人員」已配對來源欄（FilterScenario 的欄位前置檢查）。
    { value: 'nonAuthorizedPreparer', label: '非授權編製人員', requiresAuthorizedPreparers: true, requiresCreateBy: true },
    { value: 'lowFrequencyPreparer', label: '編製分錄較少的人員（' + PRESCREEN_DEFAULTS.preparerMaxEntries + ' 筆以下）', requiresCreateBy: true },
    { value: 'lowFrequencyAccount', label: '使用較少的科目（' + PRESCREEN_DEFAULTS.accountMaxEntries + ' 筆以下）' }
  ];

  // Shared presentation copy for the fourth-step cards and fifth-step condition controls.
  // The predicates and prerequisites remain backend-owned.
  var PRESCREEN_DESCRIPTIONS = {
    postPeriodApproval: '傳票核准日在案件設定的期末財報準備日當日或之後。檢查範圍為本次測試的分錄。',
    suspiciousKeywords: '摘要包含預設的 25 個中英文關鍵字之一，包含繁體與簡體文字。在進階篩選加入「摘要含特定字詞」可檢視及調整整份清單。',
    unexpectedAccountPair: '列出貸方為收入，但同張傳票沒有應收款項、現金或預收款借方的分錄。需先完成科目配對。',
    trailingZeros: '依本次測試的分錄，找出金額整數部分尾數連續 ' + PRESCREEN_DEFAULTS.trailingZeroDigits + ' 個 0 的分錄；不含小數，整數部分為 0 不列入。',
    weekendPosting: '總帳日期落在案件設定的週末日；補班日仍納入。',
    weekendApproval: '傳票核准日落在案件設定的週末日；補班日仍納入。',
    holidayPosting: '總帳日期落在已匯入假日清單的日期。',
    holidayApproval: '傳票核准日落在已匯入假日清單的日期。',
    blankDescription: '摘要欄位空白的分錄；可在下一步直接作為篩選條件。',
    nonAuthorizedPreparer: '傳票建立人員的識別值非空白，且未列在匯入的授權清單中。請使用與 GL 相同的姓名或員工代碼欄位；這個差異本身不代表已判定越權。',
    lowFrequencyPreparer: '依本次測試的分錄，按傳票建立人員計算分錄筆數，列出 ' + PRESCREEN_DEFAULTS.preparerMaxEntries + ' 筆以下的人員分錄。同張傳票有多筆分錄時每筆都計入；空白人員不列入。自訂條件可另設分錄筆數上限。',
    lowFrequencyAccount: '依本次測試的分錄，按科目編號計算分錄筆數，列出 ' + PRESCREEN_DEFAULTS.accountMaxEntries + ' 筆以下的科目分錄。同張傳票有多筆分錄時每筆都計入；空白科目不列入。自訂條件可另設分錄筆數上限。'
  };
  function prescreenConditionDescription(key) { return PRESCREEN_DESCRIPTIONS[key] || ''; }
  function prescreenConditionLabel(key) {
    var option = PRESCREEN_KEY_OPTIONS.find(function (item) { return item.value === key; });
    return option ? option.label : key;
  }

  // AuditCore PrescreenPositioningRenderer 的逐字鏡像。新 prescreen.run 會回 positioning；
  // 舊摘要沒有此加法欄位時才使用本 fallback。文字由 mirror 守衛逐欄比對，前端不得自行改寫審計定位。
  var PRESCREEN_POSITIONING_COPY = {
    aggregateGuidance: '查看編製人員與較少使用科目的分錄筆數及金額。',
    signalGuidance: '符合條件的分錄供初步查核；請在「進階條件篩選」設定本案的測試範圍。',
    reportGuidance: '可在此產生預篩選報告，或在匯出底稿時一併產生。不產生也可繼續篩選與匯出底稿。',
    overviewGuidance: '預篩選呈現查核期間分錄的分布與符合條件的分錄，是否需進一步查核由審計員判斷。',
    exportDefaultGuidance: '預設一併產出預篩選報告；取消勾選不影響其他報告及底稿內容。',
    exportPendingRunGuidance: '尚無預篩選結果，匯出時會先執行預篩選；資料量大時需要數分鐘到十餘分鐘。'
  };

  function prescreenPositioningCopy(p) {
    var wire = p && p.positioning && typeof p.positioning === 'object' ? p.positioning : {};
    return {
      aggregateGuidance: typeof wire.aggregateGuidance === 'string'
        ? wire.aggregateGuidance : PRESCREEN_POSITIONING_COPY.aggregateGuidance,
      signalGuidance: typeof wire.signalGuidance === 'string'
        ? wire.signalGuidance : PRESCREEN_POSITIONING_COPY.signalGuidance,
      reportGuidance: typeof wire.reportGuidance === 'string'
        ? wire.reportGuidance : PRESCREEN_POSITIONING_COPY.reportGuidance,
      overviewGuidance: typeof wire.overviewGuidance === 'string'
        ? wire.overviewGuidance : PRESCREEN_POSITIONING_COPY.overviewGuidance,
      exportDefaultGuidance: typeof wire.exportDefaultGuidance === 'string'
        ? wire.exportDefaultGuidance : PRESCREEN_POSITIONING_COPY.exportDefaultGuidance,
      exportPendingRunGuidance: typeof wire.exportPendingRunGuidance === 'string'
        ? wire.exportPendingRunGuidance : PRESCREEN_POSITIONING_COPY.exportPendingRunGuidance
    };
  }

  // 科目配對分析三模式（見 docs/jet-guide.md 第 6 節「進階條件篩選」）。
  var ACCOUNT_PAIR_MODE_OPTIONS = [
    { value: 'exact', label: '借方是 A 且貸方是 B' },
    { value: 'debitAnchor', label: '借方是 A，看它的對方科目' },
    { value: 'creditAnchor', label: '貸方是 B，看它的對方科目' }
  ];

  // 借貸科目組合卡的模式選單（前端呈現；wire 仍是 accountPair 與 specialAccountCategoryPair 兩型別）。
  // accountPair exact 與 specialAccountCategoryPair drAndCr 述詞相同，新情境只用後者；exact 只給舊情境讀回。
  var ACCOUNT_COMBINATION_OPTIONS = [
    { type: 'specialAccountCategoryPair', mode: 'drAndCr', label: '借方是 A 且貸方是 B', hint: '同一張傳票同時有 A 借方與 B 貸方；輸出 A 借列與 B 貸列。' },
    { type: 'specialAccountCategoryPair', mode: 'drNotCr', label: '借方是 A 且整張傳票沒有 B 貸方', hint: '輸出 A 借列。A、B 都選 Cash 就是「借現金、貸非現金」。' },
    { type: 'specialAccountCategoryPair', mode: 'notDrCr', label: '貸方是 B 且整張傳票沒有 A 借方', hint: '輸出 B 貸列。A、B 都選 Cash 就是「貸現金、借非現金」。' },
    { type: 'accountPair', mode: 'debitAnchor', label: '借方是 A，看它的對方科目', hint: '輸出 A 借列與同傳票所有貸方列。' },
    { type: 'accountPair', mode: 'creditAnchor', label: '貸方是 B，看它的對方科目', hint: '輸出 B 貸列與同傳票所有借方列。' },
    { type: 'accountPair', mode: 'exact', label: '借方是 A 且貸方是 B（舊格式）', hint: '同一張傳票同時有 A 借方與 B 貸方；結果列出符合的借方與貸方分錄。', legacy: true }
  ];

  // 第五步「從範例開始」的範例：一鍵把草稿換成常見的審計問題，審計員再改數值或直接預覽。
  // 純前端資料；套用時由 filter-step 的 applyTemplate 深拷貝規則，不打 KCT 標記。
  // requires 只用 FILTER_CONDITION_REQUIREMENTS 列出的幾種，停用原因由 filter-step 依案件狀態產生。
  // 查核截止日在建立案件時必填，不列為前置條件（使用者 2026-10-14 同意刪除沒有作用的「案件沒有查核截止日」分支）。
  // pickField: 'text' 表示條件裡 fieldId 空白的那一條，要換成一個文字型攸關資料元素欄位。
  // 兩個現金範本和新建條件一樣用「包含下層分類」。
  var FILTER_CONDITION_REQUIREMENTS = ['accountMapping', 'preparationDate', 'approveByMapped', 'createByMapped', 'unexpectedAccountPair'];
  var FILTER_SCENARIO_TEMPLATES = [
    { key: 'cashDebitNonCashCredit', label: '借現金、貸非現金', requires: 'accountMapping',
      rationale: '借方為現金，而整張傳票沒有貸方現金的分錄。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'specialAccountCategoryPair', pairMode: 'drNotCr', categorySelection: 'subtree', debitCategoryIds: ['builtin.cash'], creditCategoryIds: ['builtin.cash'] }] }] },
    { key: 'cashCreditNonCashDebit', label: '貸現金、借非現金', requires: 'accountMapping',
      rationale: '貸方為現金，而整張傳票沒有借方現金的分錄。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'specialAccountCategoryPair', pairMode: 'notDrCr', categorySelection: 'subtree', debitCategoryIds: ['builtin.cash'], creditCategoryIds: ['builtin.cash'] }] }] },
    { key: 'nonBusinessDayExcludingDates', label: '非營業日且排除指定日期', kct: 'I',
      rationale: '非營業日過帳的分錄，另排除個案已知的例外日期。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'fieldValue', field: 'postDate', operator: 'notIn', values: [], includeBlank: false, __valueType: 'date' }] }] },
    { key: 'periodEndManual', label: '期末最後 7 天的人工分錄',
      rationale: '期末最後幾天入帳的人工分錄，容易用來調整期末數字。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'fieldValue', field: 'postDate', operator: 'between', from: '', to: '', values: [], includeBlank: false, __valueType: 'date', __periodEndDays: 7 },
        { type: 'manualAuto', isManual: 'true' }] }] },
    { key: 'blankDescriptionOrApprover', label: '空白摘要或空白核准人員', requires: 'approveByMapped',
      rationale: '缺少說明或缺少核准人員的分錄。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'fieldValue', field: 'description', operator: 'isBlank', values: [], includeBlank: false, __valueType: 'text' },
        { type: 'fieldValue', join: 'OR', field: 'approveBy', operator: 'isBlank', values: [], includeBlank: false, __valueType: 'text' }] }] },
    { key: 'preparerLargeAmount', label: '特定人員的大額分錄',
      rationale: '指定建立人員且金額超過門檻的分錄；人員與門檻請自行填入。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'fieldValue', field: 'createBy', operator: 'in', values: [], includeBlank: false, __valueType: 'text' },
        { type: 'fieldValue', field: 'amount', operator: 'greaterThanOrEqual', value: '', values: [], includeBlank: false, amountBasis: 'absolute', __valueType: 'money' }] }] },
    { key: 'weekendManualDescription', label: '週末人工分錄的特定摘要',
      rationale: '週末以人工方式入帳且摘要含特定字詞的分錄，可能是非例行的調整。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'manualAuto', isManual: 'true' },
        { type: 'fieldValue', field: 'postDate', operator: 'isWeekend', values: [], includeBlank: false, __valueType: 'date' },
        { type: 'fieldValue', field: 'description', operator: 'contains', values: ['迴轉'], includeBlank: false, __valueType: 'text' }] }] },
    { key: 'manualDepartment', label: '人工分錄的特定部門', pickField: 'text',
      rationale: '特定部門的人工分錄，用來檢查高風險單位是否有非例行的入帳。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'manualAuto', isManual: 'true' },
        { type: 'fieldValue', fieldId: '', operator: 'contains', values: ['總經理室'], includeBlank: false, __valueType: 'text' }] }] },
    { key: 'accountPairLargeAmount', label: '指定借貸科目組合的大額分錄',
      rationale: '指定借方與貸方科目組合中的大額分錄，用來檢查不尋常的科目對應。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'group', rules: [
          { type: 'voucher', quantifier: 'any', rules: [
            { type: 'drCrOnly', drCr: 'debit' },
            { type: 'fieldValue', field: 'accNum', operator: 'in', values: ['1111', '1113', '1114', '1115', '1116', '2223'], includeBlank: false, __valueType: 'text' }] },
          { type: 'voucher', quantifier: 'any', rules: [
            { type: 'drCrOnly', drCr: 'credit' },
            { type: 'fieldValue', field: 'accNum', operator: 'in', values: ['5550', '5551', '6501'], includeBlank: false, __valueType: 'text' }] }] },
        { type: 'fieldValue', field: 'amount', operator: 'greaterThan', value: '300000', values: [], includeBlank: false, amountBasis: 'absolute', __valueType: 'money' }] }] },
    { key: 'preparationManualHoliday', label: '財報準備期間的人工假日分錄', requires: 'preparationDate',
      rationale: '財報準備日起核准、在假日以人工入帳的分錄，可能是期末的不當調整，並排除每月 1 到 3 日。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'prescreen', prescreenKey: 'postPeriodApproval' },
        { type: 'manualAuto', isManual: 'true' },
        { type: 'fieldValue', field: 'postDate', operator: 'isHoliday', values: [], includeBlank: false, __valueType: 'date' },
        { type: 'group', rules: [
          { type: 'fieldValue', field: 'postDate', operator: 'isNotMakeupDay', values: [], includeBlank: false, __valueType: 'date' },
          { type: 'fieldValue', field: 'postDate', operator: 'dayOfMonthNotIn', values: ['1', '2', '3'], includeBlank: false, __valueType: 'date' }] }] }] }
  ];

  // 第五步「常用查核條件」：依審計目的分類，按「加入」把 rules 加入目前作用中的條件組。
  // 型別與比較方式都是後端既有的；requires 同上，只有後端一定會擋的情況才標。
  // __periodEndDays 與範例相同，加入時由查核截止日往前算出起訖日期。
  var FILTER_COMMON_CHECK_GROUPS = [
    { key: 'dates', label: '日期與時點', checks: [
      { key: 'postDateWeekend', label: '總帳日期在週末', rules: [{ type: 'fieldValue', field: 'postDate', operator: 'isWeekend' }] },
      { key: 'postDateHoliday', label: '總帳日期在國定假日', rules: [{ type: 'fieldValue', field: 'postDate', operator: 'isHoliday' }] },
      { key: 'postDateNonBusinessDay', label: '總帳日期在非營業日', rules: [{ type: 'fieldValue', field: 'postDate', operator: 'isNonBusinessDay' }] },
      { key: 'docDateNonBusinessDay', label: '傳票核准日在非營業日', rules: [{ type: 'fieldValue', field: 'docDate', operator: 'isNonBusinessDay' }] },
      { key: 'postPeriodApproval', label: '財報準備日起核准的分錄', requires: 'preparationDate', rules: [{ type: 'prescreen', prescreenKey: 'postPeriodApproval' }] },
      { key: 'postDateRange', label: '指定日期區間', focusValue: true, rules: [{ type: 'fieldValue', field: 'postDate', operator: 'between', from: '', to: '' }] },
      { key: 'periodEndDays', label: '查核期末最後幾天', rules: [{ type: 'fieldValue', field: 'postDate', operator: 'between', from: '', to: '', __periodEndDays: 7 }] }
    ] },
    { key: 'amounts', label: '金額', checks: [
      { key: 'largeAmount', label: '大額分錄', focusValue: true, rules: [{ type: 'fieldValue', field: 'amount', operator: 'greaterThan', value: '', amountBasis: 'absolute' }] },
      { key: 'trailingZeros', label: '金額尾數連續多個 0', rules: [{ type: 'group', rules: [
        { type: 'fieldValue', field: 'amount', operator: 'endsWithDigits', value: '0'.repeat(PRESCREEN_DEFAULTS.trailingZeroDigits), amountBasis: 'absolute' },
        { type: 'fieldValue', field: 'amount', operator: 'greaterThanOrEqual', value: '1', amountBasis: 'absolute' }] }] }
    ] },
    { key: 'description', label: '摘要', checks: [
      { key: 'descriptionKeywords', label: '摘要含特定字詞', rules: [{ type: 'fieldValue', field: 'description', operator: 'contains', values: SUSPICIOUS_KEYWORD_DEFAULTS.slice() }] },
      { key: 'blankDescription', label: '空白摘要', rules: [{ type: 'fieldValue', field: 'description', operator: 'isBlank' }] }
    ] },
    { key: 'accounts', label: '科目與借貸', checks: [
      { key: 'debitOrCreditOnly', label: '只看借方或貸方', rules: [{ type: 'drCrOnly', drCr: 'debit' }] },
      { key: 'accountCategory', label: '科目屬於指定分類', requires: 'accountMapping', rules: [{ type: 'group', rules: [
        { type: 'drCrOnly', drCr: 'debit' }, { type: 'accountSide', categoryMode: 'is', categoryIds: ['builtin.cash'], categorySelection: 'subtree' }] }] },
      { key: 'categoryPair', label: '借方與貸方分類組合', requires: 'accountMapping', rules: [{ type: 'specialAccountCategoryPair' }] },
      { key: 'accountCodePair', label: '借方與貸方科目編號組合', rules: [{ type: 'group', rules: [
        { type: 'voucher', quantifier: 'any', rules: [{ type: 'drCrOnly', drCr: 'debit' }, { type: 'fieldValue', field: 'accNum', operator: 'in', values: [] }] },
        { type: 'voucher', quantifier: 'any', rules: [{ type: 'drCrOnly', drCr: 'credit' }, { type: 'fieldValue', field: 'accNum', operator: 'in', values: [] }] }] }] },
      { key: 'unexpectedAccountPair', label: '未預期的借貸組合', requires: 'unexpectedAccountPair', rules: [{ type: 'prescreen', prescreenKey: 'unexpectedAccountPair' }] },
      { key: 'rareAccounts', label: '較少使用的科目', rules: [{ type: 'entityFrequency', field: 'accNum', countUnit: 'entries', countOperator: 'lessThanOrEqual',
        countFrom: String(PRESCREEN_DEFAULTS.accountMaxEntries) }] }
    ] },
    { key: 'people', label: '人員與分錄性質', checks: [
      { key: 'manualEntries', label: '人工分錄', rules: [{ type: 'manualAuto', isManual: 'true' }] },
      { key: 'rarePreparers', label: '編製分錄較少的人員', requires: 'createByMapped', rules: [{ type: 'entityFrequency', field: 'createBy', countUnit: 'entries',
        countOperator: 'lessThanOrEqual', countFrom: String(PRESCREEN_DEFAULTS.preparerMaxEntries) }] }
    ] }
    // 原本最後一類「預篩選結果」只有「沿用第四步預篩選規則」，加入後要到條件卡片改選；2026-10-16 改成條件總覽的
    // 「預篩選」分頁，直接列出每一條預篩選規則（見 filter-step.js 的 prescreenPickerHtml）。
  ];

  // 特殊科目類別配對三模式（specialAccountCategoryPair；A=借方類別、B=貸方類別）。
  // 三模式皆需 A 與 B 皆填（否定模式同樣需要 B/A 才能判定「不存在」），與 accountPair 的
  // 錨定模式不同——故各 case 一律呈現借/貸兩個 categorySelect。標籤明確標示 Dr/Cr 與否定語意。
  var SPECIAL_PAIR_MODE_OPTIONS = [
    { value: 'drAndCr', label: '借方是 A 且貸方是 B' },
    { value: 'drNotCr', label: '借方是 A 且整張傳票沒有 B 貸方' },
    { value: 'notDrCr', label: '貸方是 B 且整張傳票沒有 A 借方' }
  ];

  // 借貸科目分類（accountSide）三模式（兩值語意，2026-09-07 裁定）。標籤鏡像 Domain FilterConditionLabels。
  var ACCOUNT_SIDE_MODE_OPTIONS = [
    { value: 'is', label: '科目屬於指定分類' },
    { value: 'isNot', label: '科目不屬於指定分類' }
  ];

  // 科目配對單側多選分類的讀回分隔字元。正本是 Domain FilterConditionLabels.CategoryListSeparator，
  // 後端 renderer 是唯一權威輸出；前端讀回只鏡射同一分隔字元，不自行決定呈現方式。
  var FILTER_CATEGORY_LIST_SEPARATOR = '、';

  var TEXT_MODE_OPTIONS = [
    { value: 'contains', label: '包含' },
    { value: 'exact', label: '完全符合' },
    { value: 'notContains', label: '不包含（排除）' },
    { value: 'notExact', label: '不等於（排除）' }
  ];

  // 進階篩選條件 AST 的封閉選項。顯示字串鏡像 FilterConditionRenderer；前端只負責編輯 wire，
  // matchScope 的命中語意與 textSet 的正規化／比對仍由後端權威執行。
  var FILTER_MATCH_SCOPE_OPTIONS = [
    { value: 'row', label: '同一分錄列' },
    { value: 'sameVoucher', label: '同一傳票' }
  ];

  var TEXT_SET_MODE_OPTIONS = [
    { value: 'contains', label: '包含任一值' },
    { value: 'exact', label: '完全符合任一值' }
  ];

  var TEXT_SET_NORMALIZATION_OPTIONS = [
    { value: 'preserve', label: '保留半形空白' },
    { value: 'removeAsciiSpaces', label: '移除半形空白' }
  ];

  // 後端 FilterScenarioLimits.MaxTextSetValuesPerRule 的 UI 鏡像；後端仍是權威。
  var TEXT_SET_MAX_VALUES = 100;

  // 後端 FilterScenarioLimits.MaxSavedScenarios 的 UI 鏡像（每個案件可保存的篩選情境數）；後端仍是權威。
  var FILTER_MAX_SAVED_SCENARIOS = 10;
  // 情境名稱上限，和後端 FilterScenarioLimits.MaxNameLength 相同（三種資料庫共用，使用者 2026-10-19）；輸入框直接限制字數。
  var FILTER_MAX_SCENARIO_NAME_LENGTH = 400;

  // typed（攸關資料元素）條件的 operator 封閉選單。value/label 逐鍵鏡像 Domain
  // FilterConditionLabels.TypedOperators 與 TypedFieldOperatorSets 的 per-type 集合；
  // 正本在 Domain，前端只呈現，比較語意與 blank 判定仍全由後端 SQL 執行。
  var TYPED_TEXT_OPERATORS = [
    { value: 'equals', label: '等於' },
    { value: 'notEquals', label: '不等於（排除）' },
    { value: 'contains', label: '包含' },
    { value: 'notContains', label: '不包含（排除）' },
    { value: 'in', label: '屬於任一值' },
    { value: 'notIn', label: '不屬於任何值（排除）' },
    { value: 'isBlank', label: '為空白' },
    { value: 'isNotBlank', label: '非空白' }
  ];

  var TYPED_DATE_OPERATORS = [
    { value: 'on', label: '等於日期' },
    { value: 'before', label: '早於' },
    { value: 'onOrBefore', label: '不晚於' },
    { value: 'after', label: '晚於' },
    { value: 'onOrAfter', label: '不早於' },
    { value: 'between', label: '介於' },
    { value: 'isBlank', label: '為空白' },
    { value: 'isNotBlank', label: '非空白' }
  ];

  var TYPED_MONEY_OPERATORS = [
    { value: 'equals', label: '等於' },
    { value: 'notEquals', label: '不等於（排除）' },
    { value: 'greaterThan', label: '大於' },
    { value: 'greaterThanOrEqual', label: '大於等於' },
    { value: 'lessThan', label: '小於' },
    { value: 'lessThanOrEqual', label: '小於等於' },
    { value: 'between', label: '介於' },
    { value: 'isBlank', label: '為空白' },
    { value: 'isNotBlank', label: '非空白' }
  ];

  // typed money 條件必填的比較基準（Domain TypedAmountBasisNames 與讀回標籤的鏡像）。
  var TYPED_AMOUNT_BASIS_OPTIONS = [
    { value: 'signed', label: '帶正負號金額' },
    { value: 'absolute', label: '金額絕對值' }
  ];

  // typed in／notIn 的值數上限（與後端相同，1 到 100；後端仍是權威）。
  var TYPED_SET_MAX_VALUES = 100;

  // 尾數比對每組的位數上限，也是尾數連續 0 的位數上限（與後端 TrailingZeroThreshold.MaxCustomDigits 相同；後端仍是權威）。
  var TAIL_DIGITS_MAX = 12;

  // RDE 型別 → 可用 operator 集合；未知型別回空陣列（fail closed，同後端 ForValueType）。
  function typedOperatorsForValueType(valueType) {
    if (valueType === 'text') { return TYPED_TEXT_OPERATORS; }
    if (valueType === 'date') { return TYPED_DATE_OPERATORS; }
    if (valueType === 'money') { return TYPED_MONEY_OPERATORS; }
    return [];
  }

  // 認不得的預篩選條件、比對方式或借貸組合方式不印程式代號（使用者 2026-10-14 第三輪勾選頁確認 R3-03）。
  var RETIRED_CONDITION_LABEL = '已停用的條件';

  function typedOperatorLabel(op) {
    var all = TYPED_TEXT_OPERATORS.concat(TYPED_DATE_OPERATORS, TYPED_MONEY_OPERATORS);
    var hit = all.filter(function (o) { return o.value === op; })[0];
    return hit ? hit.label : (op ? RETIRED_CONDITION_LABEL : '');
  }

  // operand carrier 判定（固定三種）：只決定要渲染哪一種輸入框，
  // 缺漏／型別錯誤的最終裁定仍由後端 invalid_scenario 負責。
  function typedOperatorCarrier(op) {
    if (op === 'isBlank' || op === 'isNotBlank') { return 'none'; }
    if (op === 'between') { return 'range'; }
    if (op === 'in' || op === 'notIn') { return 'set'; }
    return 'value';
  }

  // 科目分類的 semantic role 封閉選單（Domain AccountTaxonomyBuiltIns 的 role 鏡像）。
  // 商業判定只看 role，不看顯示名稱；自訂分類指定與內建同 role 即參與同一結果。
  var ACCOUNT_TAXONOMY_ROLES = [
    { value: 'revenue', label: '收入' },
    { value: 'receivables', label: '應收款項' },
    { value: 'cash', label: '現金' },
    { value: 'receipt_in_advance', label: '預收款項' },
    { value: 'others', label: '其他' }
  ];

  // 內建分類的不可變身分與顯示名（Domain AccountTaxonomyBuiltIns 的鏡像；顯示名可改、身分與 role 不可改）。
  var ACCOUNT_TAXONOMY_BUILT_INS = [
    { categoryId: 'builtin.revenue', label: 'Revenue', ordinal: 0, semanticRole: 'revenue', isBuiltIn: true },
    { categoryId: 'builtin.receivables', label: 'Receivables', ordinal: 1, semanticRole: 'receivables', isBuiltIn: true },
    { categoryId: 'builtin.cash', label: 'Cash', ordinal: 2, semanticRole: 'cash', isBuiltIn: true },
    { categoryId: 'builtin.receipt_in_advance', label: 'Receipt in advance', ordinal: 3, semanticRole: 'receipt_in_advance', isBuiltIn: true },
    { categoryId: 'builtin.others', label: 'Others', ordinal: 4, semanticRole: 'others', isBuiltIn: true }
  ];

  var TAXONOMY_MAX_LABEL_LENGTH = 400;

  // 可作文字／日期條件的邏輯欄位（白名單的前端鏡像；權威驗證在後端）。
  var FILTER_TEXT_FIELDS = ['docNum', 'lineID', 'accNum', 'accName', 'description', 'jeSource', 'createBy', 'approveBy'];
  var FILTER_DATE_FIELDS = ['postDate', 'docDate'];

  /* ---- DOM 輔助 ------------------------------------------------------------ */

  function $(bind) {
    return document.querySelector('[data-bind="' + bind + '"]');
  }

  function setText(bind, text) {
    var el = $(bind);
    if (el) { el.textContent = text; }
  }

  function esc(text) {
    var div = document.createElement('div');
    div.textContent = text == null ? '' : String(text);
    return div.innerHTML
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  // 畫面上所有金額的唯一顯示格式：兩位小數加千分位（使用者 2026-10-02 裁定 P3-4「畫面統一兩位小數加千分位；
  // 報表與底稿不變」）。純顯示格式化，非計算——前端不得計算金額（見 AGENTS.md 前端邊界）；
  // 輸入框、篩選條件值與送給後端的值不經過這裡。
  // 空值或無效值一律 '—'（含 null、undefined、空字串、NaN）；其餘一律兩位小數（0 → 0.00、35920.33 → 35,920.33）。
  function money(value) {
    if (value === null || value === undefined || value === '') { return '—'; }
    var n = Number(value);
    return isNaN(n)
      ? '—'
      : n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  }

  // Only differences/net use this display helper; the backend value and report precision stay unchanged.
  function moneyDifference(value) {
    var formatted = money(value), number = Number(value);
    if (number !== 0 && (formatted === '0.00' || formatted === '-0.00')) {
      return number < 0 ? '負值，不足 0.01' : '不足 0.01';
    }
    return formatted;
  }

  // 後端的 UTC ISO 時間轉成當地的年月日時分（24 小時制）。總覽與第六步共用這一個格式；
  // 沒有時間或無法解析時回傳固定說明，不把原始字串直接顯示給審計員。
  function formatDateTime(iso) {
    if (!iso) { return '尚無紀錄'; }
    var date = new Date(iso);
    if (isNaN(date.getTime())) { return '時間未提供'; }
    return date.toLocaleString('zh-Hant', {
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', hour12: false
    });
  }

  // 非同步讀取的統一最新回應守衛。每次 issue 使上一張 ticket 失效；消費者可再提供自己的狀態 predicate
  // （專案、資料世代、草稿 revision、savedScenarios 參照等）。invalidate 供 workflow reset／快取作廢使用。
  function createLatestResponseGuard() {
    var revision = 0;
    return {
      issue: function (stillCurrent) {
        var issuedRevision = ++revision;
        return function () {
          return issuedRevision === revision && (!stillCurrent || stillCurrent());
        };
      },
      invalidate: function () { revision++; }
    };
  }

  // 統一的 action 執行包裝：權威 single-flight（同一時間至多一項作業）＋ busy 狀態 ＋ 錯誤導入訊息區。
  // 進入時若已 busy：留下可見提示後回 resolved no-op（不執行 factory），把各步驟手寫的
  //   `if (busy) return` 一般化到唯一入口，杜絕鍵盤／頂列／目錄／預覽等破口併發第二項作業。
  // 錯誤在此吸收（已顯示給使用者），避免 unhandled rejection；後端 operation_in_progress 誠實呈現「另一項作業還在進行」。
  // 成功時只留一則完成訊息（使用者 2026-10-13 裁定：耗時保留，改成秒並合併）：
  // options.completionMessage(value) 回傳這個操作自己的完成句，Ui.run 記「完成句，耗時 X。」，操作本身不再另記完成句；
  // 回傳空字串表示這次其實沒有做事（例如沒有選檔），不記。沒有 completionMessage 而有 logCompletion 時，
  // 記「操作名稱完成，耗時 X。」。logCompletionWhen 可排除「檔案選擇已取消」這類 resolved no-op。
  // 失敗與取消在括號裡附總耗時，有 busyDetail 時再附最後進度；被擋下與「另一項作業還在進行」沒有開始做事，不附耗時。
  // 失敗與被擋下時一律在目前步驟顯示提示框（見 state.operationFailure），除非 options.inlineError 表示
  // 這個操作已把錯誤寫在自己的就地錯誤框；onError 只是收到錯誤後的處理，不影響提示框。
  // options.refresh 由呼叫點宣告完成後刷新；refreshWhen 可為 success 或 settled（預設 settled）。
  // refresh 一律等主作業解除 busy 後才執行，且 refresh 失敗只另記訊息，不改寫主作業結果。
  function monotonicNowMilliseconds() {
    if (global.performance && typeof global.performance.now === 'function') {
      return global.performance.now();
    }
    return Date.now();
  }

  // 耗時的唯一寫法（使用者 2026-10-13 裁定不再用毫秒）：不到 60 秒寫一位小數的秒，例如「4.2 秒」；
  // 60 秒以上寫「2 分 51 秒」；一小時以上寫「1 小時 3 分」。四捨五入是 0.0 秒時寫「不到 0.1 秒」
  // （使用者 2026-10-14 第三輪勾選頁確認 R3-26）。
  var UNDER_TENTH_SECOND = '不到 0.1 秒';
  function formatElapsed(milliseconds) {
    var value = Math.max(0, Number(milliseconds) || 0);
    var tenths = Math.round(value / 100);
    if (tenths === 0) { return UNDER_TENTH_SECOND; }
    if (tenths < 600) { return (tenths / 10).toFixed(1) + ' 秒'; }
    var seconds = Math.round(value / 1000);
    if (seconds < 3600) { return Math.floor(seconds / 60) + ' 分 ' + (seconds % 60) + ' 秒'; }
    var minutes = Math.floor(seconds / 60);
    return Math.floor(minutes / 60) + ' 小時 ' + (minutes % 60) + ' 分';
  }

  // 「耗時」「經過」接上時間的唯一寫法：「耗時 4.2 秒」；不到 0.1 秒時寫「耗時不到 0.1 秒」，中間不空格。
  function elapsedPhrase(prefix, milliseconds) {
    var text = formatElapsed(milliseconds);
    return prefix + (text === UNDER_TENTH_SECOND ? '' : ' ') + text;
  }

  // 成功時要記的那一句；不需要記時回傳空字串。
  function completionSentence(label, options, value) {
    if (typeof options.logCompletionWhen === 'function' && !options.logCompletionWhen(value)) { return ''; }
    if (typeof options.completionMessage === 'function') {
      return trimSentenceEnd(options.completionMessage(value));
    }
    return options.logCompletion ? label + '完成' : '';
  }

  // 「另一項作業還在進行」的唯一寫法（使用者 2026-10-13 勾選頁確認，14、16 統一成同一句）。
  var OTHER_OPERATION_RUNNING = '另一項作業還在進行，請等它完成後再試。';

  // JavaScript 內建的程式錯誤（TypeError 這類）是程式本身的問題，英文原文不給審計員看（使用者 2026-10-19 要求前端不露出系統用詞）。
  // 這時和錯誤沒有訊息時一樣，改寫呼叫端給的說明，沒有就寫後端非預期錯誤的同一句；後端回的錯誤與前端自己寫的說明照原句。
  var UNEXPECTED_ERROR_MESSAGE = '發生非預期的錯誤，此操作未完成。請再試一次。';

  // 以錯誤名稱判斷，iframe 或測試的另一個執行環境丟出的內建錯誤也認得出來。
  var PROGRAM_ERROR_NAMES = ['TypeError', 'ReferenceError', 'RangeError', 'SyntaxError', 'EvalError', 'URIError'];

  function isProgramError(error) {
    return !!error && PROGRAM_ERROR_NAMES.indexOf(error.name) >= 0;
  }

  function errorText(error, fallback) {
    if (error && error.message && !isProgramError(error)) { return error.message; }
    return fallback == null ? UNEXPECTED_ERROR_MESSAGE : fallback;
  }

  // 進度接進訊息時，開頭「工作底稿：正在…」的冒號拿掉，免得一句出現兩個冒號（使用者 2026-10-14 第三輪勾選頁確認 R3-28）。
  // 忙碌畫面上的進度照舊，只有訊息面板用這個寫法。
  function progressInMessage(detail) {
    return String(detail || '').replace(/^([^：，「」]+)：(?=正在)/, '$1');
  }

  // 最後進度不再重複耗時：進度文字結尾的「，經過 …」拿掉（使用者 2026-10-13 勾選頁確認 19）。
  function progressWithoutElapsed(detail) {
    return progressInMessage(detail).replace(/，經過[^，]*$/, '');
  }

  // 操作是否已把錯誤寫在自己的就地錯誤框（使用者 2026-10-13 裁定「已經有就地錯誤框的操作維持原樣」）。
  // inlineError 可以是布林值，或收到錯誤後回傳真假的函式；沒有標的操作一律顯示步驟提示框。
  function showsErrorInline(options, error) {
    if (typeof options.inlineError === 'function') { return !!options.inlineError(error); }
    return !!options.inlineError;
  }

  function run(label, promiseFactory, options) {
    if (Store.getState().busy) {
      var current = Store.getState();
      Store.addMessage(
        current.busyLabel ? '「' + current.busyLabel + '」還在進行，請等它完成後再試。' : OTHER_OPERATION_RUNNING,
        'info');
      return Promise.resolve();
    }
    options = options || {};
    var startedAt = monotonicNowMilliseconds();
    // 重試任何操作時，上一次的步驟失敗提示先拿掉。
    Store.clearOperationFailure();
    Store.setBusy(true, label);
    var operationSucceeded = false;
    var promise;
    try {
      promise = promiseFactory();
    } catch (error) {
      promise = Promise.reject(error);
    }
    // 取消目標只由 app.js 在請求送出時依可取消動作清單設定；這裡不再用 promise 上的 requestId 補設，
    // 否則開啟資料夾這類不能取消的動作也會出現取消鈕（低嚴重度事項 F4，使用者 2026-10-19 要求處理）。
    return Promise.resolve(promise)
      .then(function (value) {
        operationSucceeded = true;
        var sentence = completionSentence(label, options, value);
        if (sentence) {
          Store.addMessage(
            sentence + '，' + elapsedPhrase('耗時', monotonicNowMilliseconds() - startedAt) + '。',
            'info');
        }
        return value;
      })
      .catch(function (error) {
        var message;
        // 訊息分三級（使用者 2026-10-13 裁定）：操作失敗是錯誤；被阻擋與另一項作業進行中是需要注意；使用者自己取消是一般說明。
        var level = 'error';
        var showInStep = true;
        var lastProgress = progressWithoutElapsed(Store.getState().busyDetail);
        // 句子照使用者 2026-10-13 勾選頁確認的 16、17、18、19：
        // 另一項作業還在進行與尚無法執行都沒有開始做事，不附耗時；取消與失敗把耗時與最後進度收進括號。
        var timed = false;
        if (error && error.code === 'operation_in_progress') {
          message = trimSentenceEnd(OTHER_OPERATION_RUNNING);
          level = 'warn';
          showInStep = false;
        } else if (error && error.code === 'operation_cancelled') {
          message = label + (error.completedResultsPreserved ? '已取消，已完成的部分仍保留' : '已取消');
          level = 'info';
          showInStep = false;
          timed = true;
        } else if (error && (error.code === 'completeness_prerequisite_failed' || error.code === 'no_saved_scenario')) {
          // 沒有已儲存的情境也是前置條件不足（使用者 2026-10-14 第三輪勾選頁確認 R3-34，照 18 的寫法）。
          message = label + '尚無法執行：' + trimSentenceEnd(errorText(error));
          level = 'warn';
        } else {
          // 原因若以句號結尾，先拿掉，避免出現「。（」或「。。」。
          message = label + '失敗：' + trimSentenceEnd(errorText(error));
          timed = true;
        }
        // 步驟提示框只寫動作與原因；耗時與最後進度只進訊息面板。
        var stepNotice = message + '。';
        // 失敗與取消的括號後面也以句號結尾（使用者 2026-10-14 第三輪勾選頁確認 R3-27）。
        if (timed) {
          message += '（' + elapsedPhrase('耗時', monotonicNowMilliseconds() - startedAt) +
            (lastProgress ? '，最後進度：' + lastProgress : '') + '）。';
        } else {
          message += '。';
        }
        Store.addMessage(message, level);
        var current = Store.getState();
        if (showInStep && current.view === 'workflow' && !showsErrorInline(options, error)) {
          Store.setOperationFailure({ label: label, message: stepNotice, level: level, stepIndex: current.currentStepIndex });
        }
        if (typeof options.onError === 'function') {
          try {
            options.onError(message, error);
          } catch (callbackError) {
            if (global.console) { global.console.error('JET action error callback failed:', callbackError); }
          }
        }
      })
      .finally(function () {
        Store.setBusy(false);
      })
      .then(function (value) {
        if (!options.refresh || (options.refreshWhen === 'success' && !operationSucceeded)) {
          return value;
        }
        return Promise.resolve()
          .then(options.refresh)
          .catch(function (error) {
            Store.addMessage((options.refreshLabel || '重新整理') + '失敗：' + errorText(error), 'error');
          })
          .then(function () { return value; });
      });
  }

  // 背景（唯讀、可併行）執行：不佔 single-flight、不設 busy（不觸發整介面遮罩），僅吸收錯誤。
  // 專供「後端已放行併行、不應被進行中的作業遮罩阻擋」的唯讀讀取（如資料預覽的世代自動刷新——
  // 它會在某個變更型作業的 run 仍持有 busy 期間觸發，若走 run 會被 single-flight 吞掉而看到陳舊預覽）。
  function runBackground(label, promiseFactory) {
    return promiseFactory()
      .catch(function (error) {
        // 唯讀動作理論上不會被後端序列化閘回絕；若真回絕就靜默（背景刷新不打擾使用者）。
        if (error && error.code === 'operation_in_progress') { return; }
        Store.addMessage(label + '失敗：' + errorText(error), 'error');
      });
  }

  /* ---- 專案租約鎖：心跳保活 / 離場釋放 ------------------------ */
  // 鏡像後端：SQL Server 案件 project.load 成功後每 heartbeatSeconds 秒續租一次；實際離開專案時釋放。
  // 本機案件不送心跳。純鏡像、零商業邏輯。

  var heartbeatTimer = null;

  // 停心跳（清 interval，冪等）。切換專案、回選擇、結束前呼叫。
  function stopHeartbeat() {
    if (heartbeatTimer !== null) {
      global.clearInterval(heartbeatTimer);
      heartbeatTimer = null;
    }
  }

  // 起心跳：以伺服器驅動的 heartbeatSeconds 間隔，透過 runBackground 週期續租當前專案的租約鎖。
  // 走 runBackground（不設 busy／不觸發遮罩／不佔 single-flight）——背景保活絕不可被進行中的作業擋住。
  function startHeartbeat(seconds) {
    stopHeartbeat();
    var interval = (typeof seconds === 'number' && seconds > 0 ? seconds : 30) * 1000;
    heartbeatTimer = global.setInterval(function () {
      if (!global.JetApi.isReady()) { return; }
      runBackground('確認線上案件使用狀態', function () { return global.JetApi.projectHeartbeat({}); });
    }, interval);
  }

  // 離開專案時直接等後端釋放鎖；只有成功後才停心跳。不得走會吞掉
  // operation_in_progress 的 runBackground，否則長作業仍在寫入時可能誤離場。
  function releaseProjectLease() {
    if (global.JetApi.isReady()) {
      return global.JetApi.projectReleaseLock({}).then(function (result) {
        stopHeartbeat();
        return result;
      });
    }
    if (!Store.getState().project) {
      stopHeartbeat();
      return Promise.resolve({ ok: true });
    }
    var unavailable = new Error('應用程式連線尚未就緒，無法確認案件鎖已釋放。');
    unavailable.code = 'bridge_not_ready';
    return Promise.reject(unavailable);
  }

  // 句子不再寫按鈕名稱（使用者 2026-10-13 勾選頁確認 22、23，統一成第一輪的寫法）；departure 參數只標明是哪個離開動作。
  function departureRetryMessage(state) {
    return state.activeRequestId
      ? '作業還在進行。請先按「取消作業」，等取消完成後再試一次。'
      : '作業還在進行，請等它完成後再試一次。';
  }

  // busy 時主畫面與訊息 rail 會被 overlay 遮住；提示必須同步寫進可見的 busyDetail。
  function showBusyDepartureGuidance(departure) {
    var state = Store.getState();
    var message = departureRetryMessage(state);
    Store.setBusyDetail(message);
    Store.addMessage(message, 'warn');
  }

  function handleProjectDepartureFailure(error, departure) {
    if (error && error.code === 'operation_in_progress') {
      var state = Store.getState();
      var message = departureRetryMessage(state);
      if (state.activeRequestId) {
        Store.setBusyDetail(message);
      } else {
        Store.setBusy(false);
      }
      Store.addMessage(message, 'warn');
      return;
    }
    Store.setBusy(false);
    // 沒有原因時不寫「未知錯誤」（使用者 2026-10-14 第三輪勾選頁確認 R3-29）。
    Store.addMessage(
      errorText(error, '') ? '無法離開案件，已保留目前畫面：' + errorText(error, '') : '無法離開案件，已保留目前畫面。請再試一次。',
      'error');
  }

  function isRequired(field, mode) {
    if (field.req === 'always') { return true; }
    if (Array.isArray(field.req)) { return field.req.indexOf(mode) >= 0; }
    return false;
  }

  function glFieldLabel(key) {
    var hit = GL_FIELDS.filter(function (f) { return f.key === key; })[0];
    return hit ? hit.label : key;
  }

  // 條件用的欄位已不存在時，標題與讀回寫的名稱（使用者 2026-10-14 第三輪勾選頁確認 R3-03），不印程式代號。
  // 已移除欄位的名稱由 project.load 回應的 retiredGlFieldLabels 帶來，正本只在後端 JetFieldCatalog.RetiredGlFieldLabels。
  function missingConditionFieldName(state, rule, usage) {
    var readback = usage === 'readback';
    if (rule && rule.fieldId) { return '已不在欄位配對中的攸關資料元素欄位'; }
    var labels = state && state.retiredGlFieldLabels;
    var retired = labels && rule && rule.field && Object.prototype.hasOwnProperty.call(labels, rule.field) ? labels[rule.field] : '';
    if (retired) { return readback ? '已移除的「' + retired + '」' : retired + '（已移除）'; }
    return readback ? '已不存在的欄位' : '欄位已不存在';
  }

  // 舊式文字、日期條件讀回用的 GL 欄位名稱；欄位已不存在時同上，不印程式代號。
  function glFieldReadback(state, key) {
    var hit = GL_FIELDS.filter(function (f) { return f.key === key; })[0];
    return hit ? hit.label : missingConditionFieldName(state, { field: key }, 'readback');
  }

  /* ---- 專案層契約鏡像的讀取輔助（純讀取，不重算任何審計語意） -------------- */

  // 目前專案的科目分類；project.load 尚未回傳時退回內建五類，讓畫面有可讀初值。
  // 這只是顯示用退路：保存與判定一律以後端 taxonomy revision 為準。
  function taxonomyCategories(state) {
    var snapshot = state && state.taxonomy;
    var categories = snapshot && Array.isArray(snapshot.categories) ? snapshot.categories : [];
    return categories.length ? categories : ACCOUNT_TAXONOMY_BUILT_INS;
  }

  // 分類身分只用於資料交換；已刪除的分類不以內部代號冒充顯示名稱。
  function taxonomyCategoryLabel(state, categoryId) {
    var hit = taxonomyCategories(state).filter(function (c) { return c.categoryId === categoryId; })[0];
    if (hit) { return hit.label; }
    var builtIn = ACCOUNT_TAXONOMY_BUILT_INS.filter(function (c) {
      return c.categoryId === categoryId;
    })[0];
    return builtIn ? builtIn.label : '已刪除的分類，請重新選擇';
  }

  function taxonomyTree(state) {
    var categories = taxonomyCategories(state), result = [], visited = {};
    function add(parent, depth) {
      categories.filter(function (c) { return (c.parentCategoryId || null) === parent; }).forEach(function (c) {
        if (visited[c.categoryId]) { return; }
        visited[c.categoryId] = true;
        result.push(Object.assign({}, c, { depth: depth })); add(c.categoryId, depth + 1);
      });
    }
    add(null, 0);
    categories.forEach(function (c) { if (!visited[c.categoryId]) { result.push(Object.assign({}, c, { depth: 0 })); } });
    return result;
  }

  // 僅展開分類中繼資料，供條件說明呈現選取範圍；實際分錄判斷仍由後端處理。
  function taxonomySelectionCategories(state, ids, mode) {
    var categories = taxonomyCategories(state).slice().sort(function (a, b) {
      return (a.ordinal || 0) - (b.ordinal || 0) || (a.categoryId < b.categoryId ? -1 : a.categoryId > b.categoryId ? 1 : 0);
    }), chosen = ids || [];
    var selected = categories.filter(function (category) { return chosen.indexOf(category.categoryId) >= 0; });
    if (mode === 'node') { return selected; }
    if (!mode || mode === 'role') {
      return categories.filter(function (category) {
        return selected.some(function (item) { return item.semanticRole === category.semanticRole; });
      });
    }
    return categories.filter(function (category) {
      var current = category, visited = {};
      while (current && !visited[current.categoryId]) {
        if (chosen.indexOf(current.categoryId) >= 0) { return true; }
        visited[current.categoryId] = true;
        current = categories.find(function (item) { return item.categoryId === current.parentCategoryId; });
      }
      return false;
    });
  }

  // 分類節點是否被收合中的上層藏起來；畫面輸出與綁定後的 update 用同一套判斷，第一次綁定時不會重播收起。
  function taxonomyNodeHidden(categories, collapsed, id) {
    var seen = {};
    var category = categories.find(function (item) { return item.categoryId === id; });
    while (category && category.parentCategoryId && !seen[category.parentCategoryId]) {
      var parentId = category.parentCategoryId;
      seen[parentId] = true;
      // 上層已不在清單裡的節點畫在最外層，沒有收合鈕可以打開，所以只看真的找得到的上層。
      var parent = categories.find(function (item) { return item.categoryId === parentId; });
      if (!parent) { break; }
      if ((collapsed || {})[parentId]) { return true; }
      category = parent;
    }
    return false;
  }

  function taxonomyTreeHtml(state, view, rowHtml) {
    var tree = taxonomyTree(state);
    var prefix = view.focusKey || 'category';
    var all = taxonomyCategories(state);
    return '<div class="category-tree">' + tree.map(function (category) {
        var branch = tree.some(function (child) { return child.parentCategoryId === category.categoryId; });
        var open = !(view.collapsed || {})[category.categoryId];
        // 使用者 2026-10-13 要求展開與收合一致：分類節點跟著上層收放，標 data-collapsible；一開始就藏起來的節點直接輸出 hidden。
        var hidden = taxonomyNodeHidden(all, view.collapsed, category.categoryId);
        return '<div class="category-tree__row" data-category-node="' + esc(category.categoryId) + '" data-collapsible' + (hidden ? ' hidden' : '') +
          ' style="margin-inline-start:' + category.depth + 'em">' +
          // 有下層的分類前面是展開箭頭；沒有下層的分類留同樣寬的空位，名稱才對齊。
          (branch ? '<button type="button" class="category-tree__toggle jet-disclosure" data-category-toggle="' + esc(category.categoryId) +
            '" data-focus-key="' + esc(prefix + '-' + category.categoryId) + '" aria-label="' + esc(category.label) + '的下層分類"' +
            disclosureAttributes(open) + '></button>' : '<span class="category-tree__toggle" aria-hidden="true"></span>') +
          rowHtml(category) + '</div>';
      }).join('') + '</div>';
  }

  function taxonomyPickerHtml(state, selected, key, legend, view) {
    var missing = (selected || []).some(function (id) { return !taxonomyCategories(state).some(function (category) { return category.categoryId === id; }); });
    return '<fieldset class="category-select" data-category-picker="' + esc(key) + '"><legend class="category-select__legend">' + esc(legend) + '</legend>' +
      (missing ? '<p class="form-notice">已刪除的分類，請重新選擇。</p>' : '') +
      taxonomyTreeHtml(state, view, function (category) {
        return '<label class="category-option"><input type="checkbox" data-category-bind="' + esc(key) + '" value="' + esc(category.categoryId) + '"' +
          ((selected || []).indexOf(category.categoryId) >= 0 ? ' checked' : '') + '><span>' + esc(category.label) + '</span></label>';
      }) + '</fieldset>';
  }

  function bindTaxonomyPicker(root, state, view) {
    view.collapsed = view.collapsed || {};
    var categories = taxonomyCategories(state);
    function update() {
      root.querySelectorAll('[data-category-node]').forEach(function (node) {
        var hidden = taxonomyNodeHidden(categories, view.collapsed, node.getAttribute('data-category-node'));
        if (node.hidden !== hidden) { node.hidden = hidden; }
      });
      root.querySelectorAll('[data-category-toggle]').forEach(function (button) {
        var open = !view.collapsed[button.getAttribute('data-category-toggle')];
        button.setAttribute('aria-expanded', String(open));
      });
    }
    root.querySelectorAll('[data-category-toggle]').forEach(function (button) {
      button.addEventListener('click', function () {
        var id = button.getAttribute('data-category-toggle'); view.collapsed[id] = !view.collapsed[id]; update();
      });
    });
    update();
  }

  // 目前已提交 GL 配對的攸關資料元素欄位定義；未提交或舊版配對時為空陣列。
  function committedRdeFields(state) {
    var committed = state && state.mapping ? state.mapping.gl.committed : null;
    var options = committed ? committed.options : null;
    return options && Array.isArray(options.rdeFields) ? options.rdeFields : [];
  }

  function rdeFieldLabel(state, fieldId) {
    var hit = committedRdeFields(state).filter(function (f) { return f.fieldId === fieldId; })[0];
    return hit ? hit.label : missingConditionFieldName(state, { fieldId: fieldId || 'missing' }, 'readback');
  }

  // 第五步選欄位時在名稱後加註來源欄，兩個同名的攸關資料元素欄位也分得出來；只改顯示，選項值仍是 fieldId。
  // 回傳未跳脫的文字，由呼叫端跳脫。
  function rdeFieldOptionLabel(field) {
    return field.label + (field.sourceColumn ? '（來源欄：' + field.sourceColumn + '）' : '');
  }

  /* ---- 後端欄位 metadata 驅動的結果表（動態欄） ---------------------------- */

  // columns 精確來自後端（{ key, label, valueType, isCustom }）：前端不推導欄序、不補欄、不改名。
  function dynamicColumnHeadHtml(columns) {
    // 後端在欄位定義標 sortable 的固定欄可點排序；額外欄位只顯示。
    return '<tr>' + sortableHeadCellsHtml(null, (columns || []).map(function (col) {
      return { key: col.sortable ? col.key : null, label: col.label, className: col.isCustom ? 'preview-table__custom' : '' };
    })) + '</tr>';
  }

  // 明細表表頭：有 key 的欄變成可點排序的按鈕（aria-sort 標示目前方向）。呼叫端把 action 名稱與鍵名寫死在同一個
  // 呼叫裡，Architecture 守衛核對鍵名都在後端該查詢的白名單內；action 只供守衛對照，不參與渲染。
  function sortableHeadCellsHtml(action, columns) {
    return (columns || []).map(function (col) {
      var cls = col.className ? ' class="' + esc(col.className) + '"' : '';
      if (!col.key) { return '<th' + cls + '>' + esc(col.label) + '</th>'; }
      return '<th' + cls + ' data-sort-key="' + esc(col.key) + '" aria-sort="none">' +
        '<button type="button" class="th-sort" data-sort-key="' + esc(col.key) + '" title="依完整查詢結果排序，再按一下反向排序">' +
        '<span class="th-sort__label">' + esc(col.label) + '</span><span class="th-sort__mark" aria-hidden="true"></span></button></th>';
    }).join('');
  }

  // 「依傳票號碼查看」或「依科目編號查看」：送出後從第一頁重載，只在資料庫端比對，不在畫面過濾。
  function pageSearchHtml(label) {
    return searchBoxHtml(label, { attributes: { 'data-page-search': true }, inputAttributes: { 'data-page-search-input': true },
      clearAttributes: { 'data-page-search-clear': true } });
  }

  // 只有一頁時不顯示分頁列（使用者 2026-10-16：超過 200 筆才分頁）。總筆數未知時，以是否有前後頁判斷。
  function pagerNeeded(page, pages, canNext) {
    if (page > 1 || canNext) { return true; }
    return pages != null && pages > 1;
  }

  // 所有明細共用同一種頁碼。總筆數由後端依相同搜尋範圍計算，前端只保存當頁及已走過的游標。
  // pageSize 只給非明細清單（例如工作底稿版本紀錄）使用；明細一律每頁 TABLE_PAGE_SIZE 筆。
  function pageControlsHtml(page, totalCount, options) {
    var opts = options || {};
    var size = opts.pageSize || TABLE_PAGE_SIZE;
    var pages = totalCount == null ? null : Math.max(1, Math.ceil(Number(totalCount) / size));
    var previous = opts.previousAttribute || 'data-page-previous';
    var next = opts.nextAttribute || 'data-page-next';
    var canNext = opts.canNext !== undefined ? !!opts.canNext : pages != null && page < pages;
    return '<nav class="table-pager" aria-label="資料分頁"' + (pagerNeeded(page, pages, canNext) ? '' : ' hidden') +
      '><button type="button" class="btn btn--ghost btn--tiny table-pager__button" ' +
      previous + ' aria-label="上一頁"' + (page <= 1 || opts.busy ? ' disabled' : '') +
      '><span class="table-pager__arrow table-pager__arrow--previous" aria-hidden="true"></span></button>' +
      '<span class="table-pager__position" data-page-position aria-live="polite">' + page + '/' + (pages == null ? '—' : pages) + '</span>' +
      '<button type="button" class="btn btn--ghost btn--tiny table-pager__button" ' + next + ' aria-label="下一頁"' +
      (opts.busy || !canNext ? ' disabled' : '') +
      '><span class="table-pager__arrow table-pager__arrow--next" aria-hidden="true"></span></button></nav>';
  }

  // fetchPage(cursor, sort, search) 回 rows、nextCursor、totalCount。換搜尋或排序後回第一頁，失敗保留原頁。
  function bindPagedTable(root, opts) {
    var persistent = !!opts.viewState;
    var state = opts.viewState || {};
    if (!state.initialized) {
      Object.assign(state, { initialized: true, cursor: null, sort: null, search: '', started: false, busy: false,
        rows: [], pageIndex: 0, pageCursors: [null], totalCount: null, retryRequest: null, queuedSearch: null, statusText: '' });
      if (opts.initialPage) {
        Object.assign(state, { rows: opts.initialPage.rows || [], cursor: opts.initialPage.nextCursor,
          totalCount: opts.initialPage.totalCount, started: true });
      }
    }
    var pager = document.createElement('div');
    pager.innerHTML = pageControlsHtml(1, state.totalCount);
    root.appendChild(pager);
    var pagerNav = pager.querySelector('.table-pager');
    var previousPage = pager.querySelector('[data-page-previous]');
    var nextPage = pager.querySelector('[data-page-next]');
    var position = pager.querySelector('[data-page-position]');
    var table = opts.table || null;
    var form = opts.search || null;
    var captured = Store.getState();
    var project = captured.project;
    var generation = captured.dataGeneration;
    function resultSource(s) {
      return opts.sourceKind === 'filter' ? s.filterResultRef : (opts.sourceKind ? s.lastRuns[opts.sourceKind] : null);
    }
    var source = resultSource(captured);
    function current() {
      var s = Store.getState();
      return (persistent || root.isConnected !== false) && s.project === project && s.dataGeneration === generation && resultSource(s) === source;
    }
    // 使用者 2026-10-13 要求展開與收合一致：狀態列與重試按鈕原地收放，標 data-collapsible；收起時保留原來的字，
    // 才看得到它收回去，不會先變成空白再消失。
    var status = document.createElement('p');
    status.className = 'page-status';
    status.setAttribute('data-collapsible', '');
    status.setAttribute('data-page-status', 'true');
    status.setAttribute('role', 'status');
    status.hidden = true;
    root.appendChild(status);
    var retry = document.createElement('button');
    retry.type = 'button'; retry.className = 'btn btn--ghost btn--tiny';
    retry.textContent = '重試'; retry.hidden = true; retry.setAttribute('data-collapsible', '');
    // 行內按鈕收高度時，所在的匿名行框還留一行高，收到最後會跳一下；改成區塊排版，整行跟著收。
    // [hidden] 的 display:none 帶 !important，這裡的行內樣式不會讓它藏不起來。
    if (retry.style) { retry.style.display = 'block'; retry.style.width = 'fit-content'; }
    root.appendChild(retry);
    var scrollElement = opts.scrollElement || (root.querySelector && root.querySelector('.preview-table__wrap'));

    // 表頭可能在首擊後才由系統端欄位定義重建，所以排序標記每次載入後重新套，點擊用事件委派。
    function applySortMarks() {
      if (!table) { return; }
      table.querySelectorAll('th[data-sort-key]').forEach(function (th) {
        var key = th.getAttribute('data-sort-key');
        th.setAttribute('aria-sort', state.sort && state.sort.key === key
          ? (state.sort.direction === 'asc' ? 'ascending' : 'descending') : 'none');
      });
    }

    var input = form ? form.querySelector('[data-page-search-input]') : null;
    var clear = form ? form.querySelector('[data-page-search-clear]') : null;
    function applySearchBox() {
      if (input) { input.value = state.search || ''; }
      if (clear) { clear.hidden = !state.search; }
    }

    // pending回應更新同一份viewState，再交給最新的DOM綁定呈現；舊節點不接收晚到資料。
    function refresh(replayRows) {
      if (!current() || root.isConnected === false || (persistent && state.refresh !== refresh)) { return; }
      if (replayRows && state.started) {
        var top = scrollElement ? scrollElement.scrollTop : 0, left = scrollElement ? scrollElement.scrollLeft : 0;
        opts.clearRows(); opts.appendRows(state.rows);
        if (scrollElement) { scrollElement.scrollTop = top; scrollElement.scrollLeft = left; }
      }
      applySortMarks(); applySearchBox();
      var pages = state.totalCount == null ? null : Math.max(1, Math.ceil(state.totalCount / TABLE_PAGE_SIZE));
      previousPage.disabled = state.busy || !state.started || state.pageIndex === 0;
      nextPage.disabled = state.busy || !state.started || state.cursor == null;
      position.textContent = (state.pageIndex + 1) + '/' + (pages == null ? '—' : pages);
      pagerNav.hidden = !state.started || !pagerNeeded(state.pageIndex + 1, pages, state.cursor != null);
      if (table) { table.setAttribute('aria-busy', state.busy ? 'true' : 'false'); }
      if (state.statusText) { status.textContent = state.statusText; }
      status.hidden = !state.statusText;
      retry.hidden = !state.retryRequest || state.busy;
    }
    if (persistent) { state.refresh = refresh; }

    // 重載失敗時把排序與搜尋退回上一次成功的狀態：表格裡還是舊列，表頭與輸入框不能宣稱新的排序或搜尋。
    function fetch(reset, label, previous, targetPage) {
      if (state.busy || !current() || root.isConnected === false || (persistent && state.refresh !== refresh)) { return Promise.resolve(); }
      state.busy = true;
      var target = reset ? 0 : (targetPage == null ? state.pageIndex + 1 : targetPage);
      var cursor = reset ? null : (target === state.pageIndex + 1 ? state.cursor : state.pageCursors[target]);
      refresh(false);
      if (table) { table.setAttribute('aria-busy', 'true'); }
      status.hidden = false; status.textContent = '正在讀取資料…'; retry.hidden = true;
      state.statusText = status.textContent;
      var requested = { reset: reset, target: target, label: label, previous: previous, sort: state.sort, search: state.search };
      // These are bounded result reads, not mutations. Their status must not acquire the whole-page busy state.
      var pending;
      try { pending = opts.fetchPage(cursor, requested.sort, requested.search); }
      catch (error) { pending = Promise.reject(error); }
      return Promise.resolve(pending).then(function (data) {
          if (!current()) { return; }
          var rows = (data && data.rows) || [];
          if (reset) { state.pageCursors = [null]; }
          state.pageCursors[target] = cursor;
          state.rows = rows; state.started = true; state.pageIndex = target;
          if (data && data.totalCount != null) { state.totalCount = data.totalCount; }
          state.cursor = data ? data.nextCursor : null;
          state.statusText = ''; state.retryRequest = null;
          if (persistent) { state.refresh(true); } else { refresh(true); }
          if (scrollElement) { scrollElement.scrollTop = 0; }
        }).catch(function (error) {
          if (!current()) { return; }
          if (previous) { state.sort = previous.sort; state.search = previous.search; applySortMarks(); applySearchBox(); }
          state.retryRequest = requested; retry.hidden = false;
          status.textContent = (error && error.code === 'operation_cancelled' ? '讀取已取消。' : '讀取失敗。') +
            '目前資料未變更，可重試。' + (errorText(error, '') ? ' ' + errorText(error, '') : '');
          state.statusText = status.textContent;
          if (persistent) { state.refresh(false); } else { refresh(false); }
        }).finally(function () {
          state.busy = false;
          if (table) { table.setAttribute('aria-busy', 'false'); }
          if (persistent) { state.refresh(false); } else { refresh(false); }
          // 讀取中按下的「查看」或「清除」在這裡接著執行；成功或失敗都會走到這裡。
          if (persistent) { state.runQueuedSearch(); } else { runQueuedSearch(); }
        });
    }
    // 讀取中只記住最後一次要求的搜尋文字，不排隊；目前的讀取結束後再執行。
    function search(text, queuedHint) {
      if (state.busy) {
        state.queuedSearch = text;
        status.hidden = false;
        status.textContent = queuedHint;
        state.statusText = queuedHint;
        return;
      }
      state.queuedSearch = null;
      var previous = snapshot();
      state.search = text;
      applySearchBox();
      fetch(true, '依號碼查看明細', previous);
    }
    function runQueuedSearch() {
      if (state.queuedSearch === null) { return; }
      var text = state.queuedSearch;
      state.queuedSearch = null;
      if (!current()) { return; }
      search(text, '');
    }
    if (persistent) { state.runQueuedSearch = runQueuedSearch; }
    retry.addEventListener('click', function () {
      if (!state.retryRequest || state.busy || !current()) { return; }
      var request = state.retryRequest;
      state.sort = request.sort; state.search = request.search; applySortMarks(); applySearchBox();
      fetch(request.reset, request.label, request.previous, request.target);
    });
    function snapshot() { return { sort: state.sort, search: state.search }; }

    previousPage.addEventListener('click', function () { if (state.pageIndex > 0) { fetch(false, '上一頁', null, state.pageIndex - 1); } });
    nextPage.addEventListener('click', function () { if (state.cursor != null) { fetch(false, '下一頁'); } });
    if (table) {
      table.addEventListener('click', function (event) {
        var button = event.target && event.target.closest ? event.target.closest('.th-sort') : null;
        if (!button || !table.contains(button) || state.busy) { return; }
        var previous = snapshot();
        var key = button.getAttribute('data-sort-key');
        var direction = state.sort && state.sort.key === key && state.sort.direction === 'asc' ? 'desc' : 'asc';
        state.sort = { key: key, direction: direction };
        applySortMarks();
        fetch(true, '重新排序明細', previous);
      });
    }
    bindSearchBox(form, function (text, cleared) {
      search(text, cleared ? '正在讀取資料，讀完後接著清除查看條件。' : '正在讀取資料，讀完後接著查看這個號碼。');
    });
    refresh(true);
    if (opts.autoLoad && !state.started && !state.busy) { fetch(true, '載入明細'); }
    return { reload: function () { return fetch(true, '載入明細'); },
      ensureLoaded: function () { return !state.started && !state.busy ? fetch(true, '載入明細') : Promise.resolve(); } };
  }

  // 單格顯示：custom 欄一律讀 row.customValues[key]（key set 精確等於 custom columns，
  // 缺值明示 null）；money 沿用既有固定小數格式，其餘原樣呈現。前端不換算金額尺度。
  function dynamicCellText(col, row) {
    var raw = col.isCustom
      ? (row && row.customValues ? row.customValues[col.key] : null)
      : (row ? row[col.key] : null);
    if (raw == null || raw === '') { return ''; }
    if (!col.isCustom && col.key === 'drCr') { return raw === 'DEBIT' ? '借' : '貸'; }
    if (col.valueType === 'money') { return money(raw); }
    return String(raw);
  }

  function dynamicColumnCells(columns) {
    return (columns || []).map(function (col) {
      return {
        className: col.valueType === 'money' ? 'preview-table__amount' : null,
        cell: function (row) { return dynamicCellText(col, row); }
      };
    });
  }

  /* ---- 步驟渲染器 / workflow 重設 的註冊表 ------------------------------- */

  var stepRenderers = {};
  var workflowResets = [];

  function registerStep(stepId, renderer) {
    stepRenderers[stepId] = renderer;
  }

  // 步驟模組的區域狀態（如待匯入清單）在離開專案／載入專案時歸零。
  function registerWorkflowReset(fn) {
    workflowResets.push(fn);
  }

  function resetStepModules() {
    workflowResets.forEach(function (fn) { fn(); });
  }

  function renderStep(stepId, container, state) {
    var renderer = stepRenderers[stepId];
    if (renderer) { renderer(container, state); }
  }

  /* ---- 流程進入條件（state-oriented：每一步的進入條件與缺漏） ------------------- */

  function ruleRunId(run) {
    return run && run.resultRef ? run.resultRef.runId : null;
  }

  var COMPLETENESS_RERUN_REASON = '請重新執行資料驗證，以取得目前資料的結果';
  var COMPLETENESS_FIRST_RUN_REASON = '請先執行資料驗證，以取得目前資料的結果';

  // 「可否繼續後續步驟」只鏡射 validate.run 後端的判定（completenessTest.eligibility）。舊摘要缺少這個欄位
  // 時 fail closed 並要求重跑；不得在前端用控制總數或逐科目比對的原始結果重算第二份判定。
  // 從未執行資料驗證時沒有東西可以「重新」執行，改請審計員先執行。
  function completenessEligibility(validation) {
    if (!validation) {
      return { isEligible: false, reason: COMPLETENESS_FIRST_RUN_REASON };
    }
    var eligibility = validation && validation.completenessTest
      ? validation.completenessTest.eligibility
      : null;
    if (!eligibility || eligibility.isEligible !== true) {
      return {
        isEligible: false,
        reason: eligibility && eligibility.reason
          ? eligibility.reason
          : COMPLETENESS_RERUN_REASON
      };
    }
    return { isEligible: true, reason: null, warning: eligibility.warning || null };
  }

  // 步驟 index 的進入條件；missing 描述「還缺什麼」。
  // 對齊 docs/jet-frontend-description.md「六個步驟」。
  function stepGate(state, index) {
    var missing = [];
    switch (index) {
      case 1:
        if (!state.project) { missing.push('建立或載入案件'); }
        break;
      case 2:
        if (!state.importState.gl) { missing.push('匯入 PBC-GL'); }
        if (!state.importState.tb) { missing.push('匯入 PBC-TB'); }
        break;
      case 3:
        if (!state.mapping.gl.committed) { missing.push('確認 GL 欄位配對'); }
        if (!state.mapping.tb.committed) { missing.push('確認 TB 欄位配對'); }
        break;
      case 4:
        var completeness = completenessEligibility(state.lastRuns.validate);
        if (!state.lastRuns.validate) {
          missing.push('執行資料驗證');
        } else if (!completeness.isEligible) {
          missing.push(completeness.reason);
        }
        break;
      case 5:
        // 第六步的工作底稿版本紀錄不依賴情境：已經有版本紀錄時，即使情境全刪或無法套用也能進來查看；
        // 匯出按鈕仍要求可用的情境（見 export-step.js）。兩者都沒有時照舊鎖住並寫出情境的原因。
        var scenarioMissing = filterScenarioMissing(state);
        if (scenarioMissing && !hasWorkpaperHistory(state)) { missing.push(scenarioMissing); }
        break;
    }
    return { ok: missing.length === 0, missing: missing };
  }

  // 可以用來匯出工作底稿的已儲存情境是否齊備；齊備時回傳 null，否則回傳缺少的項目。
  // 這也是第五步「進階條件篩選」算完成的條件。
  function filterScenarioMissing(state) {
    if (state.filter.savedScenarios.length === 0) { return '儲存至少一個篩選情境'; }
    if (!state.filterResultRef || state.filterResultRef.populationScope !== 'auditPeriod') {
      return '修改無法套用目前規則的篩選情境';
    }
    return null;
  }

  function hasWorkpaperHistory(state) {
    return reportArtifactHistory(state, 'workingPaper').length > 0;
  }

  // 目前版本的工作底稿：綁定目前的資料驗證結果與目前可用的情境版本，且後端沒有標成過期。
  // 第六步「案件流程已完成」、左側進度與流程總覽都用這一個判斷，三處不得各算一份。
  function currentWorkpaperArtifact(state) {
    var validationRunId = ruleRunId(state.lastRuns.validate);
    var resultRef = state.filterResultRef;
    var revision = resultRef && resultRef.populationScope === 'auditPeriod' ? resultRef.revision || null : null;
    if (!validationRunId || !revision) { return null; }
    return findCurrentReportArtifact(state, 'workingPaper', {
      validationRunId: validationRunId,
      scenarioRevision: revision
    });
  }

  // 步驟 index 是否完成。前四步沿用下一步的進入條件；第五步看情境是否齊備（不因第六步可以只看
  // 版本紀錄而算完成）；最後一步看是否已有目前版本的工作底稿。
  function stepComplete(state, index) {
    var last = Store.STEPS.length - 1;
    if (index === last) { return !!currentWorkpaperArtifact(state); }
    if (index === last - 1) { return !filterScenarioMissing(state); }
    return stepGate(state, index + 1).ok;
  }

  // 步驟還沒完成時缺少什麼；最後一步由呼叫端依情境與底稿狀態另外說明。
  function stepCompletionMissing(state, index) {
    var last = Store.STEPS.length - 1;
    if (index === last - 1) {
      var scenarioMissing = filterScenarioMissing(state);
      return scenarioMissing ? [scenarioMissing] : [];
    }
    return index < last ? stepGate(state, index + 1).missing : [];
  }

  // 線性流程：進入 index 必須滿足沿路所有進入條件。
  function isStepReachable(state, index) {
    for (var i = 1; i <= index; i++) {
      if (!stepGate(state, i).ok) { return false; }
    }
    return true;
  }

  // 鎖定步驟的 tooltip：沿路所有缺漏條件（去重）。
  function lockedStepTip(state, index) {
    var all = [];
    for (var i = 1; i <= index; i++) {
      stepGate(state, i).missing.forEach(function (item) {
        if (all.indexOf(item) < 0) { all.push(item); }
      });
    }
    return all.length ? '需先完成：' + all.join('、') : '';
  }

  // 鎖定步驟對使用者顯示的簡短前置條件。
  // 這是純顯示文字，不決定能否進入：放行與否仍以 stepGate / isStepReachable 為準。
  var STEP_LOCKED_REASON = {
    1: '需先建立案件',
    2: '需先完成資料匯入',
    3: '需先確認配對',
    4: '需先完成資料驗證',
    5: '需先儲存篩選情境'
  };

  // 鎖定原因沿線找第一個未滿足的進入條件；完整性所在的 Step 4 使用
  // 後端「可否繼續」判定的動態原因，其餘步驟維持既有短句。
  function lockedStepReason(state, index) {
    for (var i = 1; i <= index; i++) {
      var gate = stepGate(state, i);
      if (!gate.ok) {
        return i === 4
          ? gate.missing[0]
          : (STEP_LOCKED_REASON[i] || gate.missing[0] || '');
      }
    }
    return STEP_LOCKED_REASON[index] || '';
  }

  // 文件流／目錄的「呈現狀態」彙整：完全由既有進入條件推導，不新增任何放行語意。
  // status = 'current'（=currentStepIndex）｜'done'（stepComplete 判定本步已完成）｜
  //          'available'（可達但未完成）｜'locked'（不可達）。
  // 「完成」一律由 stepComplete 判定；最後一步在有目前版本的工作底稿時算完成。
  function stepPresentation(state, index) {
    var status;
    if (index === state.currentStepIndex) {
      status = 'current';
    } else if (stepComplete(state, index)) {
      status = 'done';
    } else if (isStepReachable(state, index)) {
      status = 'available';
    } else {
      status = 'locked';
    }
    return {
      status: status,
      lockedReason: status === 'locked' ? lockedStepReason(state, index) : ''
    };
  }

  // 已完成步數（左側進度用）：六步都用 stepComplete 判定，與目錄勾選、流程總覽和第六步的完成摘要一致。
  function doneStepCount(state) {
    var steps = Store.STEPS;
    var count = 0;
    for (var i = 0; i < steps.length; i++) {
      if (stepComplete(state, i)) { count++; }
    }
    return count;
  }

  // 統一導航入口：切換步驟並把目前位置持久化到專案（resume 用）。
  function gotoStep(index, pendingConfirmed) {
    var state = Store.getState();
    if (index < 0 || index >= Store.STEPS.length || !isStepReachable(state, index)) {
      return;
    }
    var current = Store.STEPS[state.currentStepIndex];
    if (!pendingConfirmed && current && current.id === 'validate' && index !== state.currentStepIndex &&
        pendingAccountCategoryCount()) {
      // 第四步的科目分類還沒儲存時先問；選「先不儲存」也能離開，改動留在畫面上，回來仍可儲存。
      // 儲存會清除篩選情境，原本要去的步驟可能因此不能進入，這時改到最遠還能進入的一步。
      confirmPendingAccountCategories('後續步驟只會使用已儲存的分類；改動會留在這個畫面，回來仍可儲存。', '先不儲存')
        .then(function (proceed) {
          if (!proceed) { return; }
          var latest = Store.getState();
          var target = index;
          while (target > latest.currentStepIndex && !isStepReachable(latest, target)) { target--; }
          if (target !== latest.currentStepIndex) { gotoStep(target, true); }
        });
      return;
    }
    Store.setStepIndex(index);
    persistProgress(index);
  }

  // 科目分類的待儲存改動只存在畫面上；沒有改動時直接通過。
  function pendingAccountCategoryCount() {
    var editor = global.JetUi && global.JetUi.accountMappingEditor;
    return editor && typeof editor.pendingCount === 'function' ? editor.pendingCount() : 0;
  }

  function confirmPendingAccountCategories(consequence, continueLabel, options) {
    if (!pendingAccountCategoryCount()) { return Promise.resolve(true); }
    return global.JetUi.accountMappingEditor.confirmPending(consequence, continueLabel, options);
  }

  // 「尚未儲存」對話框：第四步科目分類與第五步篩選情境共用這一個（使用者 2026-10-19 要求同功能的元件只留一套）。
  // 按鈕由左到右是「留在這裡」、不儲存也繼續、儲存並繼續；沒有 save 時只留前兩顆，第二顆改成主要按鈕。
  // 第二顆的字由呼叫端給：改動會留在畫面上時寫「先不儲存」，會被換掉或丟掉時寫「不儲存，直接…」。
  // 按 Esc 或點對話框外面等於留在這裡，焦點回到原本的按鈕。同一時間只開一個；開著或正在儲存時，新的要求當成留在這裡。
  // options：id、title、body、continueLabel、saveLabel、save（回傳 Promise，值為 true 表示存好了）、focusStay（預設焦點留在「留在這裡」）、
  // proceed（選了不儲存時當下執行，儲存成功後接著執行；save 在按下時當下送出，和原本兩個對話框一樣）。
  // 回傳 Promise<boolean>：true 表示可以繼續（選了不儲存，或儲存成功）；儲存失敗時焦點回到原本的按鈕並回傳 false。
  var unsavedDialogOpen = false;
  function confirmUnsaved(options) {
    if (unsavedDialogOpen) { return Promise.resolve(false); }
    unsavedDialogOpen = true;
    var saveable = typeof options.save === 'function';
    var previousFocus = document.activeElement;
    var modal = document.createElement('div');
    modal.className = 'modal';
    modal.setAttribute('data-bind', options.id + '-modal');
    modal.innerHTML =
      '<div class="modal__backdrop" data-pending-choice="stay"></div>' +
      '<div class="modal__card" role="dialog" aria-modal="true" aria-labelledby="' + options.id + '-title" aria-describedby="' + options.id + '-body">' +
        '<h3 class="modal__title" id="' + options.id + '-title">' + esc(options.title) + '</h3>' +
        '<p class="modal__body" id="' + options.id + '-body">' + esc(options.body) + '</p>' +
        '<div class="modal__actions">' +
          '<button type="button" class="btn btn--ghost" data-pending-choice="stay">留在這裡</button>' +
          '<button type="button" class="' + (saveable ? 'btn btn--ghost' : 'btn') + '" data-pending-choice="continue">' + esc(options.continueLabel) + '</button>' +
          (saveable ? '<button type="button" class="btn" data-pending-choice="save">' + esc(options.saveLabel) + '</button>' : '') +
        '</div>' +
      '</div>';
    function returnFocus() {
      var target = focusTargetOutsideClosedMenu(previousFocus);
      if (target && target.isConnected && target.focus) { target.focus(); }
    }
    return new Promise(function (resolve) {
      function finish(result) {
        unsavedDialogOpen = false;
        resolve(result);
      }
      function close() {
        document.removeEventListener('keydown', onKey, true);
        // 對話框淡出後才移除（見 app.js 的 JetMotion.dismiss），不一瞬間消失。
        if (global.JetMotion) { global.JetMotion.dismiss(modal); } else { modal.remove(); }
      }
      function onKey(event) {
        if (event.key === 'Escape') { event.preventDefault(); close(); returnFocus(); finish(false); return; }
        var card = modal.querySelector('.modal__card');
        if (global.JetFocus && global.JetFocus.trapDialogTab && card) { global.JetFocus.trapDialogTab(card, event); }
      }
      modal.addEventListener('click', function (event) {
        var target = event.target.closest('[data-pending-choice]');
        if (!target) { return; }
        var choice = target.getAttribute('data-pending-choice');
        close();
        if (choice === 'stay') { returnFocus(); finish(false); return; }
        if (choice === 'continue') {
          returnFocus();
          if (options.proceed) { options.proceed(); }
          finish(true);
          return;
        }
        // 儲存失敗或被擋下時留在原畫面，原因由各自的儲存流程顯示。
        var saving;
        try { saving = Promise.resolve(options.save()); } catch (error) { saving = Promise.reject(error); }
        saving.then(function (saved) { return !!saved; }, function () { return false; })
          .then(function (saved) {
            if (!saved) { returnFocus(); } else if (options.proceed) { options.proceed(); }
            finish(saved);
          });
      });
      document.addEventListener('keydown', onKey, true);
      document.body.appendChild(modal);
      var primary = modal.querySelector(options.focusStay || !saveable
        ? '.modal__actions [data-pending-choice="stay"]' : '[data-pending-choice="save"]');
      if (primary) { primary.focus(); }
    });
  }

  // 展開與收合：按鈕形式的展開區（例如「檢視」「查看結果」「列出科目」「預覽」）文字固定，說明它控制的內容，
  // 字後面接同一種細箭頭，收合時朝右、展開時朝下（app.css 的 .jet-disclosure），並寫 aria-expanded 與 aria-controls。
  // 整列的標題（原生 details 的摘要、常用查核條件的分類）箭頭在字前面，方向相同（使用者 2026-10-19 要求同功能的元件只留一套）。
  function disclosureAttributes(open, panelId) {
    return ' aria-expanded="' + (open ? 'true' : 'false') + '"' + (panelId ? ' aria-controls="' + esc(panelId) + '"' : '');
  }

  // 頁籤：同一組裡選一個、切換底下內容的控制項，外觀是底線頁籤或凹槽分段都一樣（使用者 2026-10-19 要求同功能的元件只留一套）。
  // 外層 role="tablist"，每個是 role="tab"，選中的寫 aria-selected="true"，只有它留在 Tab 鍵順序裡。
  // 畫面畫好後呼叫 Ui.bindTabs(範圍)：方向鍵左右、Home、End 直接換到那一個頁籤，等於按下它，所以各組只要處理按下。
  // 頁籤只決定畫面看哪一頁；要存進案件的設定（例如金額模式）用選項按鈕，開關式的選取（例如 KCT 條件卡）用 aria-pressed。
  function tabAttributes(selected, panelId, tabId) {
    return (tabId ? ' id="' + esc(tabId) + '"' : '') + ' role="tab"' +
      (panelId ? ' aria-controls="' + esc(panelId) + '"' : '') +
      ' aria-selected="' + (selected ? 'true' : 'false') + '" tabindex="' + (selected ? '0' : '-1') + '"';
  }

  function tabsIn(list) {
    return Array.prototype.filter.call(list.querySelectorAll('[role="tab"]'), function (tab) {
      return tab.closest('[role="tablist"]') === list && !tab.disabled;
    });
  }

  // 換頁籤後更新同一組的選取與 Tab 鍵順序。
  function selectTab(tab) {
    var list = tab && tab.closest ? tab.closest('[role="tablist"]') : null;
    if (!list) { return; }
    tabsIn(list).forEach(function (item) {
      var selected = item === tab;
      item.setAttribute('aria-selected', selected ? 'true' : 'false');
      item.setAttribute('tabindex', selected ? '0' : '-1');
    });
  }

  function onTabKeydown(event) {
    if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].indexOf(event.key) < 0 || event.altKey || event.ctrlKey || event.metaKey) { return; }
    var tab = event.target && event.target.closest ? event.target.closest('[role="tab"]') : null;
    var list = tab ? tab.closest('[role="tablist"]') : null;
    if (!list) { return; }
    var tabs = tabsIn(list);
    var index = tabs.indexOf(tab);
    if (index < 0) { return; }
    event.preventDefault();
    var next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1
      : (index + (event.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length;
    if (next === index) { return; }
    tabs[next].focus();
    tabs[next].click();
  }

  // 範圍內每一組頁籤接上鍵盤；同一組只接一次，重繪出來的新組再接。
  function bindTabs(root) {
    if (!root || typeof root.querySelectorAll !== 'function') { return; }
    var lists = Array.prototype.slice.call(root.querySelectorAll('[role="tablist"]'));
    if (root.matches && root.matches('[role="tablist"]')) { lists.unshift(root); }
    lists.forEach(function (list) {
      if (list.jetTabsBound) { return; }
      list.jetTabsBound = true;
      list.addEventListener('keydown', onTabKeydown);
    });
  }

  // 選單：按一個按鈕打開的一串選擇，例如已儲存情境的「操作」與資料預覽的「其他資料」（使用者 2026-10-19 要求同功能的元件只留一套）。
  // 外層 .jet-menu；按鈕 .jet-menu__toggle 寫 aria-haspopup="menu"、aria-expanded 與 aria-controls；
  // 選單 .jet-menu__items 是 role="menu"，關閉時 hidden；每一項是 role="menuitem"、tabindex="-1" 的按鈕。
  // 畫面畫好後呼叫 Ui.bindMenus(範圍)。開關、鍵盤與點外面關閉都在這裡處理，各畫面只處理按下項目：
  // 在按鈕上按方向鍵下或上，打開並停在第一或最後一項；項目之間用方向鍵上下循環，Home、End 到第一與最後一項。
  // Esc 關閉並回到按鈕；按 Tab 或焦點移出選單時關閉；按下項目後關閉，焦點原本在選單裡就回到按鈕。同一時間只開一個選單。
  function menuToggle(menu) { return menu.querySelector('.jet-menu__toggle'); }
  function menuList(menu) { return menu.querySelector('.jet-menu__items'); }

  function menuItems(menu) {
    var list = menuList(menu);
    return list ? Array.prototype.filter.call(list.querySelectorAll('[role="menuitem"]'), function (item) {
      return !item.disabled && !item.hidden;
    }) : [];
  }

  function isMenuOpen(menu) {
    var toggle = menuToggle(menu);
    return !!toggle && toggle.getAttribute('aria-expanded') === 'true';
  }

  function setMenuOpen(menu, open) {
    var toggle = menuToggle(menu);
    var list = menuList(menu);
    if (!toggle || !list) { return; }
    toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
    list.hidden = !open;
  }

  function openMenusIn(root) {
    if (!root || typeof root.querySelectorAll !== 'function') { return []; }
    return Array.prototype.map.call(root.querySelectorAll('.jet-menu__toggle[aria-expanded="true"]'), function (toggle) {
      return toggle.closest('.jet-menu');
    }).filter(Boolean);
  }

  function focusMenuItem(menu, index) {
    var items = menuItems(menu);
    if (items.length > 0) { items[(index + items.length) % items.length].focus(); }
  }

  // 按鈕所在的可見範圍：視窗，再扣掉每一層會裁切內容的捲動區。捲動區的 scroll-padding 標出被固定標題列蓋住的那一段，
  // 那一段雖然在捲動區裡，選單長到那裡仍會被蓋住，所以不算可用空間。
  function visibleBand(element) {
    var top = 0;
    var bottom = typeof global.innerHeight === 'number' ? global.innerHeight : Infinity;
    for (var node = element.parentElement; node && node !== document.body; node = node.parentElement) {
      if (typeof node.getBoundingClientRect !== 'function') { continue; }
      var style = global.getComputedStyle(node);
      if (!/^(auto|scroll|hidden|clip)$/.test(style.overflowY || '')) { continue; }
      var inner = node.getBoundingClientRect().top + (node.clientTop || 0);
      top = Math.max(top, inner + (parseFloat(style.scrollPaddingTop) || 0));
      bottom = Math.min(bottom, inner + node.clientHeight - (parseFloat(style.scrollPaddingBottom) || 0));
    }
    return { top: top, bottom: bottom };
  }

  // 選單預設從按鈕下方長出來；下方放不下、上方空間又比較多時，改從按鈕上方長出來。
  // 使用者 2026-10-19 確認：第五步已儲存情境靠近捲動區底邊的那幾列，「操作」選單的最後一項會被底邊切掉。
  // 量的是剛打開、還沒加 jet-menu--up 的位置；打開的動畫以上緣為原點縮放，上緣的位置不受影響。
  function placeMenu(menu) {
    var toggle = menuToggle(menu);
    var list = menuList(menu);
    menu.classList.remove('jet-menu--up');
    if (!toggle || !list || typeof global.getComputedStyle !== 'function' ||
      typeof toggle.getBoundingClientRect !== 'function' || typeof list.getBoundingClientRect !== 'function') { return; }
    var anchor = toggle.getBoundingClientRect();
    var band = visibleBand(menu);
    var overflowsBelow = list.getBoundingClientRect().top + (list.offsetHeight || 0) > band.bottom;
    if (overflowsBelow && anchor.top - band.top > band.bottom - anchor.bottom) { menu.classList.add('jet-menu--up'); }
  }

  function openMenu(menu, focusIndex) {
    openMenusIn(document).forEach(function (other) { if (other !== menu) { setMenuOpen(other, false); } });
    setMenuOpen(menu, true);
    // 先決定方向再移動焦點：焦點落在被切掉的項目上時，瀏覽器會把捲動區捲過去。
    placeMenu(menu);
    if (typeof focusIndex === 'number') { focusMenuItem(menu, focusIndex); }
  }

  function closeMenu(menu, returnFocus) {
    if (!menu || !isMenuOpen(menu)) { return; }
    setMenuOpen(menu, false);
    var toggle = menuToggle(menu);
    if (returnFocus && toggle && toggle.isConnected) { toggle.focus(); }
  }

  // 關掉範圍內打開的選單，例如換案件時。
  function closeMenus(root) {
    openMenusIn(root || document).forEach(function (menu) { setMenuOpen(menu, false); });
  }

  // 焦點原本在已經關上的選單裡（例如按下項目後跳出對話框），回到開選單的按鈕。
  function focusTargetOutsideClosedMenu(element) {
    var menu = element && element.closest ? element.closest('.jet-menu') : null;
    var list = menu ? menuList(menu) : null;
    if (!menu || isMenuOpen(menu) || !list || !list.contains(element)) { return element; }
    return menuToggle(menu) || element;
  }

  function onMenuClick(event) {
    var menu = event.currentTarget;
    var target = event.target && event.target.closest ? event.target : null;
    if (!target) { return; }
    var toggle = target.closest('.jet-menu__toggle');
    if (toggle && toggle.closest('.jet-menu') === menu) {
      if (isMenuOpen(menu)) { closeMenu(menu, false); } else { openMenu(menu); }
      return;
    }
    var item = target.closest('[role="menuitem"]');
    if (!item || item.closest('.jet-menu') !== menu) { return; }
    var list = menuList(menu);
    var active = document.activeElement;
    closeMenu(menu, !!active && (active === item || (!!list && list.contains(active))));
  }

  function onMenuKeydown(event) {
    var menu = event.currentTarget;
    var target = event.target && event.target.closest ? event.target : null;
    if (!target || event.altKey || event.ctrlKey || event.metaKey) { return; }
    var toggle = target.closest('.jet-menu__toggle');
    if (toggle && toggle.closest('.jet-menu') === menu) {
      if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
        event.preventDefault();
        openMenu(menu, event.key === 'ArrowDown' ? 0 : -1);
      } else if (event.key === 'Escape' && isMenuOpen(menu)) {
        event.preventDefault();
        closeMenu(menu, true);
      }
      return;
    }
    var item = target.closest('[role="menuitem"]');
    if (!item || item.closest('.jet-menu') !== menu) { return; }
    var index = menuItems(menu).indexOf(item);
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      focusMenuItem(menu, index + (event.key === 'ArrowDown' ? 1 : -1));
    } else if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      focusMenuItem(menu, event.key === 'Home' ? 0 : -1);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      closeMenu(menu, true);
    } else if (event.key === 'Tab') {
      closeMenu(menu, false);
    }
  }

  function onMenuFocusOut(event) {
    var menu = event.currentTarget;
    var next = event.relatedTarget;
    if (next && !menu.contains(next)) { closeMenu(menu, false); }
  }

  function bindMenus(root) {
    if (!root || typeof root.querySelectorAll !== 'function') { return; }
    var menus = Array.prototype.slice.call(root.querySelectorAll('.jet-menu'));
    if (root.matches && root.matches('.jet-menu')) { menus.unshift(root); }
    menus.forEach(function (menu) {
      if (menu.jetMenuBound) { return; }
      menu.jetMenuBound = true;
      menu.addEventListener('click', onMenuClick);
      menu.addEventListener('keydown', onMenuKeydown);
      menu.addEventListener('focusout', onMenuFocusOut);
    });
  }

  // 點在選單以外的地方時關閉；點的是另一個選單的按鈕，由那個選單自己換成打開。
  if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
    document.addEventListener('click', function (event) {
      var target = event.target;
      openMenusIn(document).forEach(function (menu) {
        if (!target || !menu.contains(target)) { closeMenu(menu, false); }
      });
    });
  }

  // 選了「不儲存」後重新進入離場流程時，改動仍在，所以這一次不再詢問，避免對話框一直重複出現。
  var pendingDepartureConfirmed = false;

  function departAfterPendingConfirmed(depart) {
    pendingDepartureConfirmed = true;
    try { depart(); } finally { pendingDepartureConfirmed = false; }
  }

  // 單一 in-flight 的 latest-value 合併器：進行中只覆寫 pending 最新值；完成後補發一次並持續 drain。
  // 回傳 Promise 涵蓋整個 drain，離場路徑可確實等到最後一筆 settle 再放鎖。
  function createLatestSaveMerger(save, onError) {
    var inFlight = null;
    var hasPending = false;
    var pendingValue = null;

    function flush() {
      if (!hasPending) { return Promise.resolve(); }
      var value = pendingValue;
      hasPending = false;
      return Promise.resolve()
        .then(function () { return save(value); })
        .catch(function (error) {
          if (onError) { onError(error); }
        })
        .then(flush);
    }

    return function merge(value) {
      pendingValue = value;
      hasPending = true;
      if (!inFlight) {
        inFlight = flush().finally(function () {
          inFlight = null;
          // 若 pending 恰在最後一次 flush 判空後抵達，另起一輪；finally 回傳新 Promise，舊等待者也會等完。
          if (hasPending) { return merge(pendingValue); }
        });
      }
      return inFlight;
    };
  }

  // 記下目前步驟失敗時的唯一寫法（使用者 2026-10-13 勾選頁確認 25、27）。開案時回到上次儲存的步驟。
  function stepSaveFailureText(error) {
    return '目前所在的步驟未能儲存：' + trimSentenceEnd(errorText(error)) +
      '。下次開啟此案件時，會回到上次儲存的步驟。';
  }

  var mergeProgressSave = createLatestSaveMerger(function (snapshot) {
    var state = Store.getState();
    if (!global.JetApi.isReady() || !state.project || state.project.projectId !== snapshot.projectId) {
      return Promise.resolve();
    }
    return global.JetApi.projectSaveProgress({ currentStep: snapshot.currentStep });
  }, function (error) {
    Store.addMessage(stepSaveFailureText(error), 'error');
  });

  function persistProgress(index) {
    var state = Store.getState();
    if (!state.project || !global.JetApi.isReady()) { return Promise.resolve(); }
    return mergeProgressSave({ projectId: state.project.projectId, currentStep: index });
  }

  // 儲存並結束：先持久化目前位置，再請 host 關閉視窗。
  // 視窗可能在 response 抵達前關閉，因此不依賴 host.exitApp 的回應。
  function exitApp() {
    if (!global.JetApi.isReady()) {
      Store.addMessage('應用程式尚未連線，請直接關閉視窗。', 'error');
      return;
    }

    var state = Store.getState();
    if (state.busy) {
      showBusyDepartureGuidance('儲存並結束');
      return;
    }
    if (!pendingDepartureConfirmed && pendingAccountCategoryCount()) {
      confirmPendingAccountCategories('結束後，這些改動不會保留。', '不儲存，直接結束').then(function (proceed) {
        if (proceed) { departAfterPendingConfirmed(exitApp); }
      });
      return;
    }
    var save = (state.view === 'workflow' && state.project)
      ? persistProgress(state.currentStepIndex)
      : Promise.resolve();

    Store.setBusy(true, '儲存並結束');
    save.then(function () {
      return releaseProjectLease();
    }).then(
      function () {
        return global.JetApi.hostExitApp({}).catch(function (error) {
          Store.setBusy(false);
          // releaseLock 已成功，後端 session 已離場；若 host 真正關窗失敗，前端不可繼續冒充仍在案件內。
          goBackToPicker();
          Store.setPickerFeedback('案件已安全離開，但應用程式無法關閉：' + errorText(error));
        });
      },
      function (error) {
        handleProjectDepartureFailure(error, '儲存並結束');
      });
  }

  /* ---- 專案載入 / 切換 ------------------------------------------------------ */

  // Picker 清單的共用 latest-response generation。每次本機刷新／線上同步都使前一張 ticket 失效；
  // 開案、建立新案或回到 picker 前另行 invalidate，避免晚到成功或失敗污染新的 view generation。
  var pickerProjectResponseGuard = createLatestResponseGuard();

  function invalidateProjectListRequests() {
    pickerProjectResponseGuard.invalidate();
  }

  function loadProjects() {
    Store.setPickerFeedback(null);
    return run('讀取案件清單', function () {
      var acceptResponse = pickerProjectResponseGuard.issue(function () {
        return Store.getState().view === 'picker';
      });
      return global.JetApi.projectListLocal({})
        .then(function (data) {
          if (!acceptResponse()) { return; }
          // 本機刷新發布新的 local snapshot，並清除先前手動同步留下的線上區狀態。
          Store.setProjects(data.projects || [], null);
        })
        .catch(function (error) {
          // Stale failure 在進入 Ui.run 的共享 catch 前吸收，不能寫入新 view 的訊息或 picker feedback。
          if (!acceptResponse()) { return; }
          throw error;
        });
    }, {
      // 案件清單的錯誤框就是這個操作的就地錯誤框。
      inlineError: true,
      onError: function (message) { Store.setPickerFeedback(message); }
    });
  }

  function syncOnlineProjects() {
    Store.setPickerFeedback(null);
    return run('同步線上案件', function () {
      var acceptResponse = pickerProjectResponseGuard.issue(function () {
        return Store.getState().view === 'picker';
      });
      return global.JetApi.projectList({})
        .then(function (data) {
          if (!acceptResponse()) { return; }
          Store.setProjects(data.projects || [], data.online);
        })
        .catch(function (error) {
          // 取消／失敗只由仍屬目前 picker generation 的 request 進入 Ui.run feedback；snapshot 保持不變。
          if (!acceptResponse()) { return; }
          throw error;
        });
    }, {
      // 案件清單的錯誤框就是這個操作的就地錯誤框。
      inlineError: true,
      onError: function (message) { Store.setPickerFeedback(message); }
    });
  }

  // 統一的「回首頁」：清空 workflow 與步驟模組狀態再切回 picker。
  function goBackToPicker() {
    pickerProjectResponseGuard.invalidate();
    resetStepModules();
    Store.resetWorkflow();
    Store.setView('picker');
    loadProjects();
  }

  // 頁首「回首頁」：比照 exitApp 先保存目前步驟位置，再切回 picker（保存失敗仍允許切回）。
  function backToPickerFromHeader() {
    var state = Store.getState();
    if (state.busy) {
      showBusyDepartureGuidance('回首頁');
      return;
    }
    if (!pendingDepartureConfirmed && pendingAccountCategoryCount()) {
      confirmPendingAccountCategories('離開案件後，這些改動不會保留。', '不儲存，直接離開').then(function (proceed) {
        if (proceed) { departAfterPendingConfirmed(backToPickerFromHeader); }
      });
      return;
    }

    Store.setBusy(true, '回首頁');
    var save = (state.view === 'workflow' && state.project && global.JetApi.isReady())
      ? persistProgress(state.currentStepIndex)
      : Promise.resolve();

    save.catch(function (error) {
      Store.addMessage(stepSaveFailureText(error), 'error');
    }).then(function () {
      return releaseProjectLease();
    }).then(
      function () {
        Store.setBusy(false);
        goBackToPicker();
      },
      function (error) {
        handleProjectDepartureFailure(error, '回首頁');
      });
  }

  // databaseProvider 取自案件清單列。本機找不到案件資料夾時，後端只對 sqlServer 案件查線上登錄。
  function openProject(projectId, databaseProvider) {
    pickerProjectResponseGuard.invalidate();
    Store.setPickerFeedback(null);
    var loadPayload = { projectId: projectId };
    if (databaseProvider) { loadPayload.databaseProvider = databaseProvider; }
    return run('開啟案件', function () {
      return global.JetApi.projectLoad(loadPayload).then(function (data) {
        applyLoadedProject(data); // 是否啟動心跳由 applyLoadedProject 依資料庫種類決定，開案與建案共用。

        // 還原持久化的訊息歷史（log.recent）；失敗不影響專案載入
        return global.JetApi.logRecent({}).then(function (log) {
          Store.seedMessages(log.messages || []);
        }).catch(function () {}).then(function () {
          // 案件名稱就是 projectId（使用者 2026-10-13 勾選頁確認 28）。
          Store.addMessage('已開啟案件「' + data.project.projectId + '」。', 'info');
          // 訊息歷史還原會整批取代畫面訊息，所以改版提示放在還原之後。
          var recalculated = filterScenarioRecalculatedMessage(data.filterScenarioCheck);
          // 報告已過期、需要時要重新產生，屬於「需要注意」（使用者 2026-10-13 裁定訊息分三級）。
          if (recalculated) { Store.addMessage(recalculated, 'warn'); }
        });
      });
    }, {
      inlineError: true,
      onError: function (message, error) {
        Store.setPickerFeedback(message, {
          projectId: projectId,
          errorCode: error && error.code,
          correlationId: error && error.correlationId
        });
      }
    });
  }

  // project.load 的 GL mapping options 鏡像。缺欄時採可顯示的預設（未配核准日、無過帳狀態政策、
  // 人工 1／自動 0、無攸關資料元素欄位）；這只是待確認草稿，不代表已提交。
  function glMappingOptionsFrom(glMapping) {
    var source = glMapping || {};
    var manual = source.manualAutoPolicy || {};
    return {
      approvalDateMode: source.approvalDateMode || 'unmapped',
      postingStatusPolicy: source.postingStatusPolicy || null,
      manualAutoPolicy: Object.assign({},
        manual.unlistedValueKind ? { unlistedValueKind: manual.unlistedValueKind } : {},
        manual.blankValueKind ? { blankValueKind: manual.blankValueKind } : {}, {
        manualValues: Array.isArray(manual.manualValues)
          ? manual.manualValues.slice() : MANUAL_AUTO_DEFAULTS.manualValues.slice(),
        automaticValues: Array.isArray(manual.automaticValues)
          ? manual.automaticValues.slice() : MANUAL_AUTO_DEFAULTS.automaticValues.slice()
      }),
      rdeFields: Array.isArray(source.rdeFields)
        ? source.rdeFields.map(function (field) {
            return {
              fieldId: field.fieldId,
              sourceColumn: field.sourceColumn,
              label: field.label,
              valueType: field.valueType
            };
          })
        : []
    };
  }

  // project.load.filterScenarioCheck：開案時 JET 用目前規則檢查已儲存情境的結果。
  function filterScenarioRecalculatedMessage(check) {
    if (!check || check.status !== 'recalculated') { return null; }
    // 使用者 2026-10-13 勾選頁確認 29。
    return '篩選規則已更新，已重算 ' + check.recalculatedCount +
      ' 個情境；之前的條件篩選報告與底稿需要重新產生。';
  }

  // 待修正清單最後的下一步與沒有逐筆清單時的整批說明（使用者 2026-10-13 勾選頁確認 N1、N2）。
  // 使用者 2026-10-14 要求黃色提示不過度解釋，這兩句和上面的重算訊息依同日確認的清單縮短。
  var FILTER_SCENARIO_NEXT_STEP = '請到「進階條件篩選」修改後儲存，或移除不需要的情境。';
  var FILTER_SCENARIO_RECHECK_NEEDED = '已儲存的篩選情境無法套用目前規則，' +
    '請到「進階條件篩選」按「重新檢查並儲存情境」。';

  function trimSentenceEnd(text) {
    return String(text || '').replace(/[。；\s]+$/, '');
  }

  // 使用者 2026-10-07 裁定：更改前面已設定的資料，會清除後面步驟的篩選情境。已有篩選情境時才提醒一句，不列細節；
  // 2026-10-15 裁定只有未儲存的第五步草稿時也提醒。
  function downstreamResetNoticeHtml(state) {
    return state && Store.hasFilterWork(state.filter)
      ? '<p class="panel__hint panel__hint--wide" data-bind="downstream-reset-notice">更改已設定好的資料，會清除後面步驟的設定。</p>'
      : '';
  }

  // 已儲存情境無法套用目前規則時的說明：先說狀態，逐筆列出哪個情境、哪裡不符合，最後只說一次下一步。
  // 沒有逐筆清單時（資料不一致，或尚未重新開啟案件）改寫一句整批的說明，請審計員按「重新檢查並儲存情境」列出清單。
  // 使用者 2026-10-09 要求提示避免專業術語、不得過長，所以下一步不再逐筆重複。
  function filterScenarioProblemsHtml(state) {
    var check = state.filterScenarioCheck;
    var problems = check && Array.isArray(check.problems) ? check.problems : [];
    if (problems.length > 0) {
      // 情境需要修改並重新儲存，是使用者接下來要去做的事，用琥珀色（使用者 2026-10-11 裁定）。
      return '<div class="form-notice form-notice--warning filter-scenario-problems"><strong>下列情境無法套用目前規則：</strong><ul>' +
        problems.map(function (problem) {
          var messages = (problem.messages || []).map(trimSentenceEnd).filter(Boolean).join('；');
          return '<li>' + esc('情境 ' + problem.position + '「' + problem.name + '」' +
            (messages ? '：' + messages : '') + '。') + '</li>';
        }).join('') + '</ul><p>' + esc(FILTER_SCENARIO_NEXT_STEP) + '</p></div>';
    }
    return '<p class="form-notice form-notice--warning">' + esc(FILTER_SCENARIO_RECHECK_NEEDED) + '</p>';
  }

  function applyLoadedProject(data, options) {
    Store.setProject(data.project);

    Store.setImportResult('gl', data.importState ? data.importState.gl : null);
    Store.setImportResult('tb', data.importState ? data.importState.tb : null);
    Store.setAccountMappingState(data.importState ? data.importState.accountMapping : null);
    Store.setAuthorizedPreparerState(data.importState ? data.importState.authorizedPreparer : null);
    Store.setCalendarState(data.importState ? data.importState.calendar : null);
    resetStepModules();

    // 科目分類先於配對快照；結果過期旗標在所有結果載入後套用。
    Store.setTaxonomy(data.taxonomy || null);
    // 只在某一側沒有有效配對時才有值；它不是已生效的配對，只用來預填草稿。
    var previousMapping = data.previousMapping || {};

    if (data.mapping && data.mapping.gl) {
      Store.replaceMappingDraft('gl', data.mapping.gl.mapping || {});
      Store.setMappingMode('gl', data.mapping.gl.amountMode || 'dual');
      Store.replaceGlMappingOptions(glMappingOptionsFrom(data.mapping.gl));
      // 完整快照供配對步驟判定「草稿是否偏離已提交版本」；resume 無標準化列數（null）。
      Store.setMappingCommitted('gl', {
        committedUtc: data.mapping.gl.committedUtc,
        mapping: data.mapping.gl.mapping || {},
        mode: data.mapping.gl.amountMode || 'dual',
        options: glMappingOptionsFrom(data.mapping.gl),
        projectedRowCount: null
      });
    } else if (previousMapping.gl && data.importState && data.importState.gl) {
      // 重新匯入後還沒重新確認：帶回上次確認的配對當草稿，審計員檢查後按確認即可，不必逐欄重選。
      Store.setMappingCommitted('gl', null);
      Store.restorePreviousMapping('gl', {
        mapping: previousMapping.gl.mapping || {},
        mode: previousMapping.gl.amountMode || 'dual',
        options: glMappingOptionsFrom(previousMapping.gl)
      }, data.importState.gl.columns || []);
    } else {
      Store.replaceMappingDraft('gl', {});
      Store.replaceGlMappingOptions(null);
      Store.setMappingCommitted('gl', null);
    }

    if (data.mapping && data.mapping.tb) {
      Store.replaceMappingDraft('tb', data.mapping.tb.mapping || {});
      Store.setMappingMode('tb', data.mapping.tb.changeMode || 'debitCredit');
      Store.setMappingCommitted('tb', {
        committedUtc: data.mapping.tb.committedUtc,
        mapping: data.mapping.tb.mapping || {},
        mode: data.mapping.tb.changeMode || 'debitCredit',
        projectedRowCount: null
      });
    } else if (previousMapping.tb && data.importState && data.importState.tb) {
      Store.setMappingCommitted('tb', null);
      Store.restorePreviousMapping('tb', {
        mapping: previousMapping.tb.mapping || {},
        mode: previousMapping.tb.changeMode || 'debitCredit'
      }, data.importState.tb.columns || []);
    } else {
      Store.replaceMappingDraft('tb', {});
      Store.setMappingCommitted('tb', null);
    }

    // 規則執行結果與已保存情境的 resume（原樣回放後端 latestRuns / filterScenarios）。
    Store.setLastRun('validate', data.latestRuns ? data.latestRuns.validate : null);
    Store.setLastRun('prescreen', data.latestRuns ? data.latestRuns.prescreen : null);
    Store.setRetiredGlFieldLabels(data.retiredGlFieldLabels || null);
    Store.setSavedScenarios(data.filterScenarios || []);
    Store.setFilterResultRef(data.filterResultRef || null);
    Store.setFilterScenarioCheck(data.filterScenarioCheck || null);
    Store.setReportArtifacts(data.reportArtifacts || []);
    Store.setFilterDraft(null); // 重設為空草稿（避免殘留上一個案件的條件）
    // 載入不是重新執行。即使快照帶有歷史摘要，最後仍以後端的過期判定為準。
    Store.setStaleState(data.staleState || null);

    Store.setView('workflow');

    // 租約鎖心跳只給 SQL Server 案件。開案與建案載入都經過這裡，所以建案後也不會漏掉續租；
    // 漏掉時租約不會續期，120 秒後過期。SQLite 與 DuckDB 案件用本機檔案鎖，不送心跳；
    // 先停掉計時器，避免從 SQL Server 案件切到本機案件時留下舊的心跳。
    // project.load 被鎖時已回 project_locked，走不到這裡。heartbeatSeconds 由 project.load 回應提供。
    if (data.project && data.project.databaseProvider === 'sqlServer') {
      startHeartbeat(data.heartbeatSeconds);
    } else {
      stopHeartbeat();
    }

    // 建立或載入案件時套用後端狀態，但停在目前步驟，不自行推進到匯入。
    if (options && options.stayOnCurrentStep) {
      return;
    }

    // resume 到上次保存的位置；0 也是合法位置（建立案件步驟）。
    var saved = typeof data.project.currentStep === 'number' ? data.project.currentStep : 1;
    var stepIndex = Math.min(Math.max(saved, 0), Store.STEPS.length - 1);

    // 防呆：保存位置若超出進入條件允許範圍（如資料被清除），退回最近可達的步驟。
    while (stepIndex > 0 && !isStepReachable(Store.getState(), stepIndex)) {
      stepIndex--;
    }

    Store.setStepIndex(stepIndex);
  }

  /* ---- 統一步驟頁尾（workflow-centric：每一步同位置、同語彙的前進導引） ------ */

  function stepFooterHtml(state) {
    var steps = Store.STEPS;
    var index = state.currentStepIndex;
    var isLast = index === steps.length - 1;

    var statusHtml = '';
    var nextBtn = '';
    if (!isLast) {
      var nextLabel = steps[index + 1].label;
      var gate = stepGate(state, index + 1);
      // 第五步的未完成原因已集中在步驟標題下方，頁尾不重複列出；下一步限制維持原判定。
      statusHtml = gate.ok || steps[index].id === 'filter'
        ? ''
        : '<p class="step-footer__hint">前往「' + nextLabel + '」前需要：' +
            esc(gate.missing.join('、')) + '。</p>';
      nextBtn = '<button type="button" class="btn" data-action="step-next"' + (gate.ok ? '' : ' disabled') +
        '>下一步：' + nextLabel + '</button>';
    }

    var prevBtn = index === 0
      ? ''
      : '<button type="button" class="btn btn--ghost" data-action="step-prev">上一步：' +
        steps[index - 1].label + '</button>';

    return (
      '<footer class="step-footer">' +
        statusHtml +
        '<div class="step-footer__nav">' + prevBtn + nextBtn + '</div>' +
      '</footer>'
    );
  }

  function bindStepFooter(container) {
    var prev = container.querySelector('[data-action="step-prev"]');
    if (prev) {
      prev.addEventListener('click', function () {
        gotoStep(Store.getState().currentStepIndex - 1);
      });
    }

    var next = container.querySelector('[data-action="step-next"]');
    if (next) {
      next.addEventListener('click', function () {
        gotoStep(Store.getState().currentStepIndex + 1);
      });
    }

  }

  /* ---- 共用 helper：專案內報告產物 --------------------------------------- */

  function sameScalarArray(left, right) {
    if (!Array.isArray(left) || !Array.isArray(right) || left.length !== right.length) { return false; }
    for (var i = 0; i < left.length; i++) {
      if (left[i] !== right[i]) { return false; }
    }
    return true;
  }

  // 只核對 contract 中的 sourceRef；報告是否 current 仍以後端回傳的 stale 為準。
  function reportArtifactMatches(artifact, kind, expectedRefs) {
    if (!artifact || artifact.stale || artifact.kind !== kind) { return false; }
    var source = artifact.sourceRef || {};
    return Object.keys(expectedRefs || {}).every(function (key) {
      var expected = expectedRefs[key];
      if (Array.isArray(expected)) { return sameScalarArray(source[key], expected); }
      return source[key] === expected;
    });
  }

  function findCurrentReportArtifact(state, kind, expectedRefs) {
    return (state.reportArtifacts || []).filter(function (artifact) {
      return reportArtifactMatches(artifact, kind, expectedRefs);
    }).sort(function (left, right) {
      return new Date(right.generatedUtc).getTime() - new Date(left.generatedUtc).getTime();
    })[0] || null;
  }

  function currentReportArtifacts(state, kinds, expectedRefs) {
    return (kinds || []).map(function (kind) {
      return findCurrentReportArtifact(state, kind, expectedRefs);
    }).filter(Boolean);
  }

  function reportArtifactHistory(state, kind) {
    // 索引由舊到新追加；產生時間相同時，較晚追加的版本仍排在上面。
    return (state.reportArtifacts || []).map(function (artifact, index) {
      return { artifact: artifact, index: index };
    }).filter(function (item) { return item.artifact && item.artifact.kind === kind; })
      .sort(function (left, right) {
        var leftTime = new Date(left.artifact.generatedUtc).getTime() || 0;
        var rightTime = new Date(right.artifact.generatedUtc).getTime() || 0;
        return rightTime - leftTime || right.index - left.index;
      }).map(function (item) { return item.artifact; });
  }

  // 報告清單與流程總覽共用的檔案大小格式：以 1,000,000 位元組為 1 MB，固定兩位小數。
  function fileSizeMbText(value) {
    var bytes = Number(value);
    return value == null || !isFinite(bytes) || bytes < 0 ? '大小未知'
      : (bytes / 1000000).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + ' MB';
  }

  // options.motion 是這份清單在同一步裡的名稱：app.js 的 JetMotion 用它讓新產生的報告長出來、換掉的收起來。
  function reportArtifactListHtml(artifacts, emptyText, options) {
    var list = artifacts || [];
    var motion = options && options.motion ? String(options.motion) : '';
    if (list.length === 0) {
      return '<p class="report-artifacts__empty"' + (motion ? ' data-motion-key="' + esc('artifacts-empty-' + motion) + '"' : '') + '>' +
        esc(emptyText || '尚未產生報告。') + '</p>';
    }

    return '<div class="report-artifacts"' + (motion ? ' data-motion-list="' + esc('artifacts-' + motion) + '"' : '') + '>' + list.map(function (artifact) {
      var generated = new Date(artifact.generatedUtc);
      var generatedText = !artifact.generatedUtc || isNaN(generated.getTime())
        ? '時間未知'
        : generated.toLocaleString('zh-Hant', { hour12: false });
      var bytesText = fileSizeMbText(artifact.bytes);
      // 檔案狀態只是提醒：審計員在 JET 之外改過或刪了檔案都不擋，重新匯出就好。
      var fileStateText = artifact.fileState === 'modifiedOutside'
        ? '已在 JET 之外修改'
        : (artifact.fileState === 'missing' ? '檔案不存在，重新匯出即可' : '');
      return (
        '<div class="report-artifact" data-artifact-id="' + esc(artifact.artifactId) + '"' +
          (motion ? ' data-motion-key="' + esc('artifact-' + motion + '-' + artifact.artifactId) + '"' : '') + '>' +
          '<span class="report-artifact__copy">' +
            '<span class="report-artifact__label">' + esc(reportArtifactLabel(artifact.kind)) + '</span>' +
            '<span class="report-artifact__name">' + esc(artifact.fileName || '未命名報告') + '</span>' +
            '<span class="report-artifact__meta">建立時間：' + esc(generatedText) + '　' + esc(bytesText) +
              (fileStateText ? '，<span class="report-artifact__state">' + esc(fileStateText) + '</span>' : '') +
                (options && options.history && artifact.stale
                  ? '，<span class="report-artifact__validity">先前資料或條件的版本</span>' : '') +
              '</span>' +
              (artifact.catalogWarning ? '<span class="report-artifact__meta" role="status">' +
                esc(artifact.catalogWarning) + '</span>' : '') +
          '</span>' +
          (options && options.reveal
            ? '<button type="button" class="btn btn--ghost btn--tiny" data-open-artifact="' +
              esc(artifact.artifactId) + '"' + (artifact.fileState === 'missing' ? ' disabled' : '') +
              '>開啟資料夾</button>' : '') +
        '</div>'
      );
    }).join('') + '</div>';
  }

  function reportArtifactLabel(kind) {
    return {
      validationReport: '資料驗證報告',
      infReport: '資料可靠性抽樣清單',
      prescreenReport: '預篩選報告',
      criteriaSelectionReport: '條件篩選報告',
      workingPaper: '工作底稿'
    }[kind] || '報告';
  }

  function bindReportArtifacts(container) {
    container.querySelectorAll('[data-open-artifact]').forEach(function (button) {
      button.addEventListener('click', function () {
        var artifactId = button.getAttribute('data-open-artifact');
        run('顯示報告檔案', function () {
          return global.JetApi.hostOpenFolder({ artifactId: artifactId });
        });
      });
    });
  }

  function reportFolderButtonHtml(artifacts) {
    var artifact = (artifacts || []).find(function (item) { return item.fileState !== 'missing'; });
    return artifact ? '<button type="button" class="btn btn--ghost" data-open-artifact="' +
      esc(artifact.artifactId) + '">開啟資料夾</button>' : '';
  }

  /* ---- 共用 helper：預覽表格與篩選規則預設值 --------------------------------- */

  // 把 previewRows 陣列轉成 <table class="preview-table"> 的完整標記。
  // 欄位順序：傳票號碼、項次、總帳日期、科目（代碼＋名稱）、摘要、金額、借貸。
  // 供 filter-step 與 validate-step 共用；呼叫端自行包 <div class="preview-table__wrap">。
  // sortAction 有值時表頭可點排序（鍵名對齊該查詢的 wire row），供預篩選命中表；預覽列表不排序時省略。
  function previewTableHtml(previewRows, sortAction) {
    var head = sortAction === 'query.prescreenPage'
      ? sortableHeadCellsHtml('query.prescreenPage', [
          { key: 'documentNumber', label: '傳票號碼' }, { key: 'lineItem', label: '項次' }, { key: 'postDate', label: '總帳日期' },
          { key: 'accountCode', label: '科目' }, { key: 'documentDescription', label: '摘要' }, { key: 'amount', label: '金額' },
          { key: 'drCr', label: '借貸' }])
      : '<th>傳票號碼</th><th>項次</th><th>總帳日期</th><th>科目</th><th>摘要</th><th>金額</th><th>借貸</th>';
    var rows = (previewRows || []).map(function (r) {
      return (
        '<tr>' +
          '<td>' + esc(r.documentNumber) + '</td>' +
          '<td>' + esc(r.lineItem || '—') + '</td>' +
          '<td>' + esc(r.postDate) + '</td>' +
          '<td>' + esc(r.accountCode) + ' ' + esc(r.accountName || '') + '</td>' +
          '<td>' + esc(r.documentDescription || '') + '</td>' +
          '<td class="preview-table__amount">' + money(r.amount) + '</td>' +
          '<td>' + (r.drCr === 'DEBIT' ? '借' : '貸') + '</td>' +
        '</tr>'
      );
    }).join('');

    return (
      '<table class="preview-table">' +
        '<thead><tr>' + head + '</tr></thead>' +
        '<tbody>' + rows + '</tbody>' +
      '</table>'
    );
  }

  // 建立一條篩選規則的預設值物件（純資料，無 Store/Ui 依賴）。
  // 供 filter-step 的「快速加入」與型別切換，以及 validate-step 的預篩選預覽 scenario builder 共用。
  function newFilterRule(type) {
    if (type === 'group' || type === 'voucher') {
      var compound = { type: type, join: 'AND', rules: [] };
      if (type === 'voucher') { compound.quantifier = 'any'; }
      return compound;
    }
    if (type === 'entityFrequency') {
      return { type: type, field: 'accNum', countUnit: 'entries', countOperator: 'equals', countFrom: '1', countTo: '' };
    }
    if (type === 'fieldValue') { return { type: type, join: 'AND', field: 'postDate', operator: 'in', values: [], includeBlank: false }; }
    if (type === 'drCrOnly') { return { type: type, join: 'AND', drCr: 'debit' }; }
    if (type === 'accountSide') { return { type: type, join: 'AND', categoryMode: 'is', categoryIds: ['builtin.cash'], categorySelection: 'subtree' }; }
    return {
      join: 'AND',
      type: type,
      prescreenKey: 'suspiciousKeywords',
      field: type === 'dateRange' ? 'postDate' : (type === 'numRange' ? 'amount' : 'description'),
      // 只有 trailingDigits（KCT 條件 H，特定金額尾數）有預設值：H 要求選取後即帶 000000；
      // 其餘型別無「預設尾數」語意，故維持空字串由使用者自填。
      keywords: type === 'trailingDigits' ? '000000' : (type === 'customKeywords' ? SUSPICIOUS_KEYWORD_DEFAULTS.join(',') : ''),
      values: [],
      mode: 'contains',
      normalization: 'preserve',
      from: '',
      to: '',
      drCr: 'debit',
      isManual: 'true',
      // pairMode 的合法取值依型別而異：accountPair 為 exact/debitAnchor/creditAnchor，
      // specialAccountCategoryPair 為 drAndCr/drNotCr/notDrCr。型別切換經此重建（filter-step
      // 的 type-change handler），故各型別取自己模式集的預設，不會殘留另一型別的 pairMode。
      pairMode: type === 'specialAccountCategoryPair' ? 'drAndCr' : 'exact',
      categorySelection: ['accountPair', 'specialAccountCategoryPair'].indexOf(type) >= 0 ? 'subtree' : undefined,
      // 借貸組合雙側一律以 taxonomy 分類身分陣列承載（2026-08-14 起雙側都可多選）；
      // legacy scalar 只在回放舊定義時讀取，新規則不再產生第二份單選欄位。
      debitCategoryIds: ['builtin.receivables'],
      creditCategoryIds: ['builtin.revenue'],
      // typed（攸關資料元素）條件：fieldId／operator 由使用者在建構器選定，初值留空以避免
      // 前端猜欄位；operand carrier 依 operator 決定，amountBasis 只在 money 條件送出。
      fieldId: '',
      operator: '',
      amountBasis: 'absolute',
      value: '',
      digits: '3',
      maxEntries: type === 'customPreparerEntryCount' ? String(PRESCREEN_DEFAULTS.preparerMaxEntries)
        : type === 'customAccountEntryCount' ? String(PRESCREEN_DEFAULTS.accountMaxEntries) : '',
      windowDays: ''
    };
  }

  /* ---- 共用：依後端回應建立表格列 ------------------------------------------ */

  // 分頁與等待狀態由 bindPagedTable 處理；此函式只建立列，不計算查核結果。
  // columns:每欄一個取值函式 row→cell,或 { cell:fn, className } 帶 td class(沿用既有欄樣式);
  // 呼叫端決定欄序與顯示,沿用既有 table 欄序;一律 esc 後插入,前端不做任何業務轉換(顯示形狀由後端決定)。
  function appendRowsToTbody(tbody, rows, columns) {
    if (!tbody) { return; }
    var html = (rows || []).map(function (row) {
      return '<tr>' + columns.map(function (col) {
        var fn = typeof col === 'function' ? col : col.cell;
        var cls = (col && col.className) ? ' class="' + col.className + '"' : '';
        return '<td' + cls + '>' + esc(fn(row)) + '</td>';
      }).join('') + '</tr>';
    }).join('');
    tbody.insertAdjacentHTML('beforeend', html);
  }

  /* ---- 共用面板片段 ---------------------------------------------------------- */

  function noProjectPanel(title) {
    return (
      '<div class="panel">' +
        '<h2 class="panel__title">' + title + '</h2>' +
        '<p class="panel__hint">尚未開啟案件。</p>' +
        '<div class="panel__actions">' +
          '<button type="button" class="btn" data-action="back-picker">回首頁</button>' +
        '</div>' +
      '</div>'
    );
  }

  function bindNoProjectPanel(container) {
    container.querySelector('[data-action="back-picker"]').addEventListener('click', goBackToPicker);
  }

  global.JetUi = {
    // 契約鏡像常數
    GL_FIELDS: GL_FIELDS,
    TB_FIELDS: TB_FIELDS,
    GL_MODES: GL_MODES,
    TB_MODES: TB_MODES,
    FILTER_RULE_TYPES: FILTER_RULE_TYPES,
    FILTER_KCT_PRESETS: FILTER_KCT_PRESETS,
    FILTER_KCT_ATOM_LABELS: FILTER_KCT_ATOM_LABELS,
    FILTER_KCT_CHECKLIST: FILTER_KCT_CHECKLIST,
    PRESCREEN_KEY_OPTIONS: PRESCREEN_KEY_OPTIONS,
    prescreenConditionDescription: prescreenConditionDescription,
    prescreenConditionLabel: prescreenConditionLabel,
    PRESCREEN_POSITIONING_COPY: PRESCREEN_POSITIONING_COPY,
    fileSizeMbText: fileSizeMbText,
    prescreenPositioningCopy: prescreenPositioningCopy,
    ACCOUNT_PAIR_MODE_OPTIONS: ACCOUNT_PAIR_MODE_OPTIONS,
    SPECIAL_PAIR_MODE_OPTIONS: SPECIAL_PAIR_MODE_OPTIONS,
    ACCOUNT_SIDE_MODE_OPTIONS: ACCOUNT_SIDE_MODE_OPTIONS,
    ACCOUNT_COMBINATION_OPTIONS: ACCOUNT_COMBINATION_OPTIONS,
    FILTER_SCENARIO_TEMPLATES: FILTER_SCENARIO_TEMPLATES,
    FILTER_COMMON_CHECK_GROUPS: FILTER_COMMON_CHECK_GROUPS,
    FILTER_CONDITION_REQUIREMENTS: FILTER_CONDITION_REQUIREMENTS,
    FILTER_CATEGORY_LIST_SEPARATOR: FILTER_CATEGORY_LIST_SEPARATOR,
    TEXT_MODE_OPTIONS: TEXT_MODE_OPTIONS,
    FILTER_MATCH_SCOPE_OPTIONS: FILTER_MATCH_SCOPE_OPTIONS,
    TEXT_SET_MODE_OPTIONS: TEXT_SET_MODE_OPTIONS,
    TEXT_SET_NORMALIZATION_OPTIONS: TEXT_SET_NORMALIZATION_OPTIONS,
    TEXT_SET_MAX_VALUES: TEXT_SET_MAX_VALUES,
    FILTER_MAX_SAVED_SCENARIOS: FILTER_MAX_SAVED_SCENARIOS,
    FILTER_MAX_SCENARIO_NAME_LENGTH: FILTER_MAX_SCENARIO_NAME_LENGTH,
    FILTER_TEXT_FIELDS: FILTER_TEXT_FIELDS,
    FILTER_DATE_FIELDS: FILTER_DATE_FIELDS,
    GL_APPROVAL_DATE_MODES: GL_APPROVAL_DATE_MODES,
    RDE_VALUE_TYPES: RDE_VALUE_TYPES,
    RDE_MAX_LABEL_LENGTH: RDE_MAX_LABEL_LENGTH,
    MANUAL_AUTO_DEFAULTS: MANUAL_AUTO_DEFAULTS,
    VALUE_PROFILE_LIMIT: VALUE_PROFILE_LIMIT,
    PRESCREEN_DEFAULTS: PRESCREEN_DEFAULTS,
    RESULT_PREVIEW_PAGE_SIZE: RESULT_PREVIEW_PAGE_SIZE,
    TABLE_PAGE_SIZE: TABLE_PAGE_SIZE,
    pageControlsHtml: pageControlsHtml,
    REPORT_HISTORY_PAGE_SIZE: REPORT_HISTORY_PAGE_SIZE,
    TYPED_TEXT_OPERATORS: TYPED_TEXT_OPERATORS,
    TYPED_DATE_OPERATORS: TYPED_DATE_OPERATORS,
    TYPED_MONEY_OPERATORS: TYPED_MONEY_OPERATORS,
    TYPED_AMOUNT_BASIS_OPTIONS: TYPED_AMOUNT_BASIS_OPTIONS,
    TYPED_SET_MAX_VALUES: TYPED_SET_MAX_VALUES,
    TAIL_DIGITS_MAX: TAIL_DIGITS_MAX,
    typedOperatorsForValueType: typedOperatorsForValueType,
    typedOperatorLabel: typedOperatorLabel,
    typedOperatorCarrier: typedOperatorCarrier,
    ACCOUNT_TAXONOMY_ROLES: ACCOUNT_TAXONOMY_ROLES,
    ACCOUNT_TAXONOMY_BUILT_INS: ACCOUNT_TAXONOMY_BUILT_INS,
    TAXONOMY_MAX_LABEL_LENGTH: TAXONOMY_MAX_LABEL_LENGTH,
    taxonomyCategories: taxonomyCategories,
    taxonomyCategoryLabel: taxonomyCategoryLabel,
    committedRdeFields: committedRdeFields,
    rdeFieldLabel: rdeFieldLabel,
    rdeFieldOptionLabel: rdeFieldOptionLabel,
    dynamicColumnCells: dynamicColumnCells,
    dynamicColumnHeadHtml: dynamicColumnHeadHtml,
    sortableHeadCellsHtml: sortableHeadCellsHtml,
    pageSearchHtml: pageSearchHtml,
    bindPagedTable: bindPagedTable,
    // DOM / 執行輔助
    $: $,
    setText: setText,
    esc: esc,
    findLive: findLive,
    dateInputHtml: dateInputHtml,
    loadFailureHtml: loadFailureHtml,
    searchBoxHtml: searchBoxHtml,
    bindSearchBox: bindSearchBox,
    money: money,
    moneyDifference: moneyDifference,
    formatDateTime: formatDateTime,
    formatElapsed: formatElapsed,
    elapsedPhrase: elapsedPhrase,
    progressInMessage: progressInMessage,
    createLatestResponseGuard: createLatestResponseGuard,
    run: run,
    errorText: errorText,
    UNEXPECTED_ERROR_MESSAGE: UNEXPECTED_ERROR_MESSAGE,
    downstreamResetNoticeHtml: downstreamResetNoticeHtml,
    runBackground: runBackground,
    isRequired: isRequired,
    glFieldLabel: glFieldLabel,
    glFieldReadback: glFieldReadback,
    missingConditionFieldName: missingConditionFieldName,
    RETIRED_CONDITION_LABEL: RETIRED_CONDITION_LABEL,
    // 註冊表
    registerStep: registerStep,
    registerWorkflowReset: registerWorkflowReset,
    renderStep: renderStep,
    // 流程
    stepGate: stepGate,
    completenessEligibility: completenessEligibility,
    isStepReachable: isStepReachable,
    lockedStepTip: lockedStepTip,
    stepPresentation: stepPresentation,
    stepComplete: stepComplete,
    stepCompletionMissing: stepCompletionMissing,
    filterScenarioMissing: filterScenarioMissing,
    currentWorkpaperArtifact: currentWorkpaperArtifact,
    doneStepCount: doneStepCount,
    gotoStep: gotoStep,
    exitApp: exitApp,
    backToPickerFromHeader: backToPickerFromHeader,
    pendingAccountCategoryCount: pendingAccountCategoryCount,
    confirmPendingAccountCategories: confirmPendingAccountCategories,
    confirmUnsaved: confirmUnsaved,
    disclosureAttributes: disclosureAttributes,
    tabAttributes: tabAttributes,
    selectTab: selectTab,
    bindTabs: bindTabs,
    bindMenus: bindMenus,
    closeMenus: closeMenus,
    loadProjects: loadProjects,
    syncOnlineProjects: syncOnlineProjects,
    invalidateProjectListRequests: invalidateProjectListRequests,
    goBackToPicker: goBackToPicker,
    openProject: openProject,
    applyLoadedProject: applyLoadedProject,
    // 面板片段
    stepFooterHtml: stepFooterHtml,
    bindStepFooter: bindStepFooter,
    noProjectPanel: noProjectPanel,
    bindNoProjectPanel: bindNoProjectPanel,
    // 共用 helper
    previewTableHtml: previewTableHtml,
    newFilterRule: newFilterRule,
    taxonomyTree: taxonomyTree,
    taxonomySelectionCategories: taxonomySelectionCategories,
    taxonomyPickerHtml: taxonomyPickerHtml,
    taxonomyTreeHtml: taxonomyTreeHtml,
    bindTaxonomyPicker: bindTaxonomyPicker,
    reportArtifactMatches: reportArtifactMatches,
    findCurrentReportArtifact: findCurrentReportArtifact,
    currentReportArtifacts: currentReportArtifacts,
    reportArtifactHistory: reportArtifactHistory,
    reportArtifactListHtml: reportArtifactListHtml,
    filterScenarioProblemsHtml: filterScenarioProblemsHtml,
    bindReportArtifacts: bindReportArtifacts,
    reportFolderButtonHtml: reportFolderButtonHtml,
    appendRowsToTbody: appendRowsToTbody
  };
})(window);
