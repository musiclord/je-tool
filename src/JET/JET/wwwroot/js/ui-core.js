/*
  前端共用核心（JetUi namespace）。
  持有：DOM 輔助、run() 包裝、流程閘門與導航、統一步驟頁尾、
  步驟渲染器註冊表與共用契約鏡像常數。
  各步驟渲染器以獨立檔案自行註冊（js/steps/*.js）；本檔不承載任何權威業務規則。
*/
(function (global) {
  'use strict';

  var Store = global.JetStore;

  /* ---- 共用契約鏡像（key/label 對齊 docs/action-contract-manifest.md） ------ */

  // GL 邏輯欄位。dcDebitCode 是借方代碼字面值（文字輸入），不是來源欄位 select。
  var GL_FIELDS = [
    { key: 'docNum', label: '傳票號碼', req: 'always' },
    { key: 'lineID', label: '傳票文件項次', req: 'optional' },
    { key: 'postDate', label: '總帳日期', req: 'always' },
    { key: 'docDate', label: '傳票核准日', req: 'optional' },
    { key: 'voucherDate', label: '傳票日期', req: 'optional' },
    { key: 'accNum', label: '會計科目編號', req: 'always' },
    { key: 'accName', label: '會計科目名稱', req: 'always' },
    { key: 'description', label: '傳票摘要', req: 'always' },
    { key: 'jeSource', label: '分錄來源模組', req: 'optional' },
    { key: 'createBy', label: '傳票建立人員', req: 'optional' },
    { key: 'approveBy', label: '傳票核准人員', req: 'optional' },
    { key: 'manual', label: '人工/自動分錄', req: 'optional' },
    { key: 'amount', label: '傳票金額（單欄）', req: ['signed', 'side', 'flag'] },
    { key: 'debitAmount', label: '借方金額', req: ['dual'] },
    { key: 'creditAmount', label: '貸方金額', req: ['dual'] },
    { key: 'dcField', label: '借貸別欄位', req: ['side', 'flag'] },
    { key: 'dcDebitCode', label: '借方標識代碼', req: ['side', 'flag'], literal: true },
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

  // GL 核准日三態（manifest mapping.commit.gl 的 approvalDateMode closed values）。
  // mapped 必須且只可配「傳票核准日」來源欄；sameAsPostDate 直接沿用標準化後的總帳日期，
  // 兩者互斥由前端就近引導，後端 mapping.commit.gl 仍是權威。
  var GL_APPROVAL_DATE_MODES = [
    { value: 'unmapped', label: '沒有核准日' },
    { value: 'mapped', label: '由來源欄提供' },
    { value: 'sameAsPostDate', label: '與總帳日期相同' }
  ];

  // 攸關資料元素（RDE）欄位型別（manifest rdeFields.valueType closed values）。
  var RDE_VALUE_TYPES = [
    { value: 'text', label: '文字' },
    { value: 'date', label: '日期' },
    { value: 'money', label: '金額' }
  ];

  // 後端 GlMappingOptionRules 的 UI 鏡像上限；後端仍是權威。
  var RDE_MAX_LABEL_LENGTH = 400;

  // 人工／自動代碼的後端預設（manifest manualAutoPolicy 缺省值）。
  var MANUAL_AUTO_DEFAULTS = { manualValues: ['1'], automaticValues: ['0'] };

  // mapping.valueProfile 的有界取樣上限（manifest limit 允許 1–100）。
  var VALUE_PROFILE_LIMIT = 50;

  var GL_MODES = [
    { value: 'dual', label: '借方欄＋貸方欄' },
    { value: 'signed', label: '單一帶號金額欄' },
    { value: 'side', label: '金額＋借貸別（文字）' },
    { value: 'flag', label: '金額＋借方旗標（數字）' }
  ];

  var TB_MODES = [
    { value: 'debitCredit', label: '借方欄−貸方欄' },
    { value: 'direct', label: '直接變動金額欄' },
    { value: 'openClose', label: '期初＋期末餘額' },
    { value: 'openCloseBySide', label: '期初借貸＋期末借貸' }
  ];

  // 進階篩選的條件型別。label 逐鍵鏡像 Domain FilterConditionLabels.RuleTypes；
  // quickLabel、group、accountMappingRequirement 與 requiresRdeFields 是前端呈現／可用性 metadata。
  // accountMappingRequirement 逐條鏡像 target 內容資格，不以來源檔存在代替（權威驗證仍在後端）。
  // group 只剩 'kct' 有意義（風險樣態下拉的「KCT 專屬」分組）；其餘分組與 quickLabel 是舊挑選區的遺留 metadata，
  // 2026-09-07 改版後畫面改用三個家族（filter-step 的 RULE_FAMILIES）。value 是 AST 型別鍵（wire 契約），不隨標籤調整改動。
  var FILTER_RULE_TYPES = [
    { value: 'fieldValue', label: '欄位值比較', quickLabel: '指定值與排除', group: 'field' },
    { value: 'accountSide', label: '借貸科目分類', group: 'nature' },
    { value: 'prescreen', label: '預篩選', quickLabel: '選擇風險訊號', group: 'risk' },
    { value: 'text', label: '文字條件', group: 'field' },
    { value: 'textSet', label: '文字值清單', group: 'field' },
    { value: 'numRange', label: '金額區間', group: 'field' },
    { value: 'dateRange', label: '日期區間', group: 'field' },
    { value: 'customKeywords', label: '自訂關鍵字', group: 'field' },
    { value: 'drCrOnly', label: '借貸限定', group: 'nature' },
    { value: 'manualAuto', label: '人工/自動', group: 'nature' },
    { value: 'customTrailingZeros', label: '自訂尾數位數', group: 'pattern' },
    // 借貸科目組合：畫面上只有一張卡（specialAccountCategoryPair 為預設 wire 型別），五種白話模式跨兩個
    // wire 型別（ACCOUNT_COMBINATION_OPTIONS）；accountPair 只在型別下拉與舊情境讀回出現，不進快速加入。
    { value: 'accountPair', label: '借貸科目組合（看對方科目）', group: 'pattern', accountMappingRequirement: 'any', pickerHidden: true },
    { value: 'specialAccountCategoryPair', label: '借貸科目組合', group: 'pattern', accountMappingRequirement: 'any' },
    { value: 'customPreparerEntryCount', label: '自訂編製人員分錄筆數', group: 'pattern' },
    { value: 'customAccountEntryCount', label: '自訂科目分錄筆數', group: 'pattern' },
    { value: 'entityFrequency', label: '科目與人員統計', group: 'pattern' },
    { value: 'group', label: '條件括號', group: 'compound' },
    { value: 'voucher', label: '傳票量詞', group: 'compound' },
    // 攸關資料元素條件：只在欄位配對已提交至少一個額外欄位時才可用（requiresRdeFields）。
    { value: 'typed', label: '攸關資料元素條件', quickLabel: '額外欄位條件', group: 'field',
      requiresRdeFields: true },
    // KCT 小組條件（清單 A/C/D/H/J；分組獨立於其他四組）。Revenue 型依內容事實逐條解鎖。
    { value: 'revenueDebitNearQuarterEnd', label: '季末前借記收入', group: 'kct', accountMappingRequirement: 'revenue' },
    { value: 'revenueWithoutNormalCounterpart', label: '收入無一般對方科目', group: 'kct', accountMappingRequirement: 'revenueAndCounterpart' },
    { value: 'manualRevenueEntry', label: '收入之人工分錄', group: 'kct', accountMappingRequirement: 'revenue' },
    { value: 'trailingDigits', label: '特定金額尾數', group: 'kct' },
    { value: 'preparerEqualsApprover', label: '編製與核准同一人', group: 'kct' }
  ];

  // 條件型別的分組（依審計意圖，非資料格式）。顯示順序即此陣列順序；
  // 每組約 3–4 項，讓使用者只看自己要的那一塊（NN/g chunking / progressive disclosure）。

  // KCT 小組「重用既有型別」的預設條件（清單 E/F/G/I）：點按鈕即帶入既有型別的預填規則，
  // 不另立 wire 型別（避免重複既有述詞，單一事實在後端述詞）。overrides 套在 newFilterRule 之上；
  // newGroup 為「整組帶入」（非營業日 = weekendPosting OR holidayPosting，自成一組以免 OR 結合錯位）。
  var FILTER_KCT_PRESETS = [
    { key: 'kctSpecificPreparer', label: '特定人員建立之分錄',
      overrides: { type: 'text', field: 'createBy', mode: 'exact' } },
    { key: 'kctSpecificKeywords', label: '特定摘要',
      overrides: { type: 'customKeywords' } },
    { key: 'kctBlankDescription', label: '空白摘要',
      overrides: { type: 'prescreen', prescreenKey: 'blankDescription' } },
    { key: 'kctNonBusinessDay', label: '非營業日分錄',
      newGroup: [
        { type: 'prescreen', prescreenKey: 'weekendPosting' },
        { type: 'prescreen', prescreenKey: 'holidayPosting', join: 'OR' }
      ] }
  ];

  // 預設群組（newGroup）在彙總區「扁平檢視」呈現為單一原子條件時的白話標籤（以卡的 ref＝preset key 為鍵）。
  // 目前僅非營業日(I) 為 newGroup（週末 OR 假日）；其餘預設為單規則、不走原子行。
  var FILTER_KCT_ATOM_LABELS = {
    kctNonBusinessDay: '非營業日（週末或假日）'
  };

  // KCT 小組方法學檢核清單（A–J）：獨立顯著面板的「單一資料來源」，十顆按鈕由此一份資料驅動，
  // 而非十段重複 HTML（Linus：讓分支消失而非加 if）。每筆只是「指向既有述詞」的標記：
  //   kind:'type'   → ref 是既有 FILTER_RULE_TYPES 的 value；點按帶入一條 newFilterRule(ref)。
  //   kind:'preset' → ref 是既有 FILTER_KCT_PRESETS 的 key；點按沿用該預設（含 newGroup 整組帶入）。
  //   disabled:true → Phase 2 佔位（B：待 KCT 交付 BS/IS 分類表），只渲染為停用，不實作述詞。
  // label 為 KCT 清單用語（卡片顯示文字）。每張卡是可複選 toggle：選取即把其規格落地成 rule
  // 併入草稿、並在每條 rule 打上 __kctLetter 身分標記（UI-only，剝除後才送 wire）；取消即移除帶
  // 該字母標記的 rule。送出時由 marker 推導 source:'kct'；KCT 名稱／動機可沿用自動值、手改或留白，
  // 一般自訂情境仍必填。已存情境由 project.load 回傳 canonical source，重存不得遺失。
  // 不在此引入任何新 wire 型別／述詞；A/C/D/H/J 與 E/F/G/I 全部重用既有 type/preset。
  var FILTER_KCT_CHECKLIST = [
    { letter: 'A', kind: 'type', ref: 'revenueDebitNearQuarterEnd', label: '在該季度前 X 天借記收入的會計分錄' },
    { letter: 'B', kind: 'type', ref: null, disabled: true, note: '尚未支援此條件',
      label: '借記固定資產(PPE，不含在建)且貸記費用之分錄' },
    { letter: 'C', kind: 'type', ref: 'revenueWithoutNormalCounterpart', label: '貸方為收入但借方非一般對方科目之分錄' },
    { letter: 'D', kind: 'type', ref: 'manualRevenueEntry', label: '收入之人工分錄' },
    { letter: 'E', kind: 'preset', ref: 'kctSpecificPreparer', label: '特定人員(財務長/執行長/高階主管等)建立之分錄' },
    { letter: 'F', kind: 'preset', ref: 'kctSpecificKeywords', label: '特定摘要(如迴轉、調整等)' },
    { letter: 'G', kind: 'preset', ref: 'kctBlankDescription', label: '空白摘要' },
    { letter: 'H', kind: 'type', ref: 'trailingDigits', label: '特定尾數(如 999999/000000 結尾)' },
    { letter: 'I', kind: 'preset', ref: 'kctNonBusinessDay', label: '非營業日之分錄' },
    { letter: 'J', kind: 'type', ref: 'preparerEqualsApprover', label: '編製人員與核准人員相同' }
  ];

  // 預篩選 row-tag 鍵與中文名（guide §4 命名登錄表；代號已退役，一律用具體名稱）。
  // 本表的 value/label 鏡像 Domain FilterConditionLabels.PrescreenKeys（正本在 Domain），
  // 由 FilterConditionLabelMirrorTests 雙向守衛；新增鍵須同步 Domain 標籤表。
  var PRESCREEN_KEY_OPTIONS = [
    { value: 'postPeriodApproval', label: '財報準備日起核准' },
    { value: 'suspiciousKeywords', label: '摘要特定描述' },
    { value: 'unexpectedAccountPair', label: '未預期借貸組合', requiresAccountMapping: true },
    { value: 'trailingZeros', label: '連續零尾數金額' },
    { value: 'weekendPosting', label: '週末過帳' },
    { value: 'weekendApproval', label: '週末核准' },
    { value: 'holidayPosting', label: '假日過帳' },
    { value: 'holidayApproval', label: '假日核准' },
    { value: 'blankDescription', label: '摘要空白' },
    { value: 'backdatedPosting', label: '回溯過帳' },
    { value: 'nonAuthorizedPreparer', label: '非授權編製人員', requiresAuthorizedPreparers: true },
    { value: 'lowFrequencyPreparer', label: '低頻編製者' },
    { value: 'lowFrequencyAccount', label: '低頻科目' }
  ];

  // 鏡像 Domain SuspiciousKeywordDefaults；繁簡詞逐一保留，不改寫其他識別值。
  var SUSPICIOUS_KEYWORD_DEFAULTS = ['ADJ', 'REV', 'RECLASS', 'SUSPENSE', 'ERROR', 'WRONG',
    '調整', '迴轉', '沖銷', '重分類', '避險', '重編', '錯誤', '計畫外', '預算外', '帳外',
    '调整', '回转', '冲销', '重分类', '避险', '重编', '错误', '计画外', '预算外'];

  // AuditCore PrescreenPositioningRenderer 的逐字鏡像。新 prescreen.run 會回 positioning；
  // 舊摘要沒有此加法欄位時才使用本 fallback。文字由 mirror 守衛逐欄比對，前端不得自行改寫審計定位。
  var PRESCREEN_POSITIONING_COPY = {
    aggregateGuidance: '先看依分錄編製者與較少使用科目的全期彙總；這兩項是常用的母體判讀面。',
    signalGuidance: '逐筆命中只供初步判讀，不是高風險裁定；要形成測試範圍，請到「進階條件篩選」組合 KCT 與其他條件。',
    reportGuidance: 'Pre-screening Report 預設隨匯出底稿一併產出，這裡可以先單獨產生；不產生也不影響進階條件篩選、Criteria Selection Report 或 Working Paper。',
    overviewGuidance: '彙總只描述母體分布；逐筆命中不等於錯誤，也不是高風險裁定；兩者都不代替審計判斷。',
    exportDefaultGuidance: '匯出底稿時預設一併產出 Pre-screening Report；取消勾選只會少這一份，其餘報告與底稿內容都不受影響。',
    exportPendingRunGuidance: '目前沒有可用的預篩選結果。維持勾選並按下產生，系統會先執行一次預篩選再產出這份報告；大型案件的預篩選可能需要數分鐘到十餘分鐘。'
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

  // 科目配對分析三模式（guide §6.1）。
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

  // 常用情境範本（2026-09-04 裁定）：一鍵把草稿填成常見的審計問題，審計員再改數值或直接預覽。
  // 純前端資料；規則形狀與快速加入相同，套用時走 filter-step 的 materializeRule，不打 KCT 標記。
  // requires: 'accountMapping' 表示需要科目配對；'periodEnd' 表示套用時用查核截止日填日期。
  var FILTER_SCENARIO_TEMPLATES = [
    { key: 'cashDebitNonCashCredit', label: '借現金、貸非現金', requires: 'accountMapping',
      rationale: '借方為現金，而整張傳票沒有貸方現金的分錄。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'specialAccountCategoryPair', pairMode: 'drNotCr', debitCategoryIds: ['builtin.cash'], creditCategoryIds: ['builtin.cash'] }] }] },
    { key: 'cashCreditNonCashDebit', label: '貸現金、借非現金', requires: 'accountMapping',
      rationale: '貸方為現金，而整張傳票沒有借方現金的分錄。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'specialAccountCategoryPair', pairMode: 'notDrCr', debitCategoryIds: ['builtin.cash'], creditCategoryIds: ['builtin.cash'] }] }] },
    { key: 'nonBusinessDayExcludingDates', label: '非營業日且排除指定日期', kct: 'I',
      rationale: '非營業日過帳的分錄，另排除個案已知的例外日期。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'fieldValue', field: 'postDate', operator: 'notIn', values: [], includeBlank: false, __valueType: 'date' }] }] },
    { key: 'periodEndManual', label: '期末最後 7 天的人工分錄', requires: 'periodEnd',
      rationale: '期末最後幾天入帳的人工分錄，容易用來調整期末數字。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'fieldValue', field: 'postDate', operator: 'between', from: '', to: '', values: [], includeBlank: false, __valueType: 'date', __periodEndDays: 7 },
        { type: 'manualAuto', isManual: 'true' }] }] },
    { key: 'blankDescriptionOrApprover', label: '空白摘要或空白核准人員',
      rationale: '缺少說明或缺少核准人員的分錄。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'fieldValue', field: 'description', operator: 'isBlank', values: [], includeBlank: false, __valueType: 'text' },
        { type: 'fieldValue', join: 'OR', field: 'approveBy', operator: 'isBlank', values: [], includeBlank: false, __valueType: 'text' }] }] },
    { key: 'preparerLargeAmount', label: '特定人員的大額分錄',
      rationale: '指定建立人員且金額超過門檻的分錄；人員與門檻請自行填入。',
      groups: [{ join: 'AND', matchScope: 'row', rules: [
        { type: 'fieldValue', field: 'createBy', operator: 'in', values: [], includeBlank: false, __valueType: 'text' },
        { type: 'fieldValue', field: 'amount', operator: 'greaterThanOrEqual', value: '', values: [], includeBlank: false, amountBasis: 'absolute', __valueType: 'money' }] }] }
  ];

  // 特殊科目類別配對三模式（manifest specialAccountCategoryPair；A=借方類別、B=貸方類別）。
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
    { value: 'isNot', label: '科目不屬於指定分類' },
    { value: 'absent', label: '整張傳票的這一側都不屬於指定分類' }
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

  // Stage 5 AST 的封閉選項。顯示字串鏡像 FilterConditionRenderer；前端只負責編輯 wire，
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
    { value: 'preserve', label: '保留 ASCII 空白' },
    { value: 'removeAsciiSpaces', label: '移除 ASCII 空白' }
  ];

  // 後端 FilterScenarioLimits.MaxTextSetValuesPerRule 的 UI 鏡像；後端仍是權威。
  var TEXT_SET_MAX_VALUES = 100;

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

  // typed in／notIn 的值數上限（manifest 1–100；後端仍是權威）。
  var TYPED_SET_MAX_VALUES = 100;

  // RDE 型別 → 可用 operator 集合；未知型別回空陣列（fail closed，同後端 ForValueType）。
  function typedOperatorsForValueType(valueType) {
    if (valueType === 'text') { return TYPED_TEXT_OPERATORS; }
    if (valueType === 'date') { return TYPED_DATE_OPERATORS; }
    if (valueType === 'money') { return TYPED_MONEY_OPERATORS; }
    return [];
  }

  function typedOperatorLabel(op) {
    var all = TYPED_TEXT_OPERATORS.concat(TYPED_DATE_OPERATORS, TYPED_MONEY_OPERATORS);
    var hit = all.filter(function (o) { return o.value === op; })[0];
    return hit ? hit.label : (op || '');
  }

  // operand carrier 判定（manifest「Operand carrier 固定三種」）：只決定要渲染哪一種輸入框，
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

  // legacy 單選 scalar（'Revenue' 等內建顯示名）→ 內建分類身分；只在回放舊定義時使用。
  function builtInCategoryIdForLegacyLabel(label) {
    var hit = ACCOUNT_TAXONOMY_BUILT_INS.filter(function (c) { return c.label === label; })[0];
    return hit ? hit.categoryId : null;
  }

  // 可作文字／日期條件的邏輯欄位（白名單的前端鏡像；權威驗證在後端）。
  var FILTER_TEXT_FIELDS = ['docNum', 'lineID', 'accNum', 'accName', 'description', 'jeSource', 'createBy', 'approveBy'];
  var FILTER_DATE_FIELDS = ['postDate', 'docDate', 'voucherDate'];

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

  // 顯示金額固定四位小數（＝log10(標準 MoneyScale 10^4)，讓 scaled 整數的完整精度顯示出來），
  // 保留千分位。純顯示格式化，非計算——前端不得計算金額（見 AGENTS.md 前端邊界）。
  // 空值／無效值一律 '—'（含 null/undefined/空字串/NaN）；其餘一律四位小數（0 → 0.0000、1.39 → 1.3900）。
  function money(value) {
    if (value === null || value === undefined || value === '') { return '—'; }
    var n = Number(value);
    return isNaN(n)
      ? '—'
      : n.toLocaleString('en-US', { minimumFractionDigits: 4, maximumFractionDigits: 4 });
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
  // 錯誤在此吸收（已顯示給使用者），避免 unhandled rejection；後端 operation_in_progress 誠實呈現「請稍候」。
  // options.logCompletion 只由關鍵長操作明示 opt-in，成功時留下單一總耗時摘要；
  // logCompletionWhen 可排除「檔案選擇已取消」這類 resolved no-op。取消／失敗則一律記錄總耗時，
  // 並在有 busyDetail 時濃縮最後進度。options.refresh 由呼叫點宣告完成後刷新；
  // refreshWhen 可為 success 或 settled（預設 settled）。
  // refresh 一律等主作業解除 busy 後才執行，且 refresh 失敗只另記訊息，不改寫主作業結果。
  function monotonicNowMilliseconds() {
    if (global.performance && typeof global.performance.now === 'function') {
      return global.performance.now();
    }
    return Date.now();
  }

  function formatElapsedMilliseconds(value) {
    return Math.max(0, Math.round(value)).toLocaleString('zh-Hant') + ' 毫秒';
  }

  function run(label, promiseFactory, options) {
    if (Store.getState().busy) {
      var current = Store.getState();
      Store.addMessage(
        current.busyLabel ? current.busyLabel + '仍在處理中，完成後再試。' : '目前仍有作業進行中，完成後再試。',
        'info');
      return Promise.resolve();
    }
    options = options || {};
    var startedAt = monotonicNowMilliseconds();
    Store.setBusy(true, label);
    var operationSucceeded = false;
    var promise;
    try {
      promise = promiseFactory();
    } catch (error) {
      promise = Promise.reject(error);
    }
    if (promise && promise.requestId) {
      Store.setBusyRequest(promise.requestId);
    }
    return Promise.resolve(promise)
      .then(function (value) {
        operationSucceeded = true;
        var shouldLogCompletion = options.logCompletion
          && (typeof options.logCompletionWhen !== 'function'
            || options.logCompletionWhen(value));
        if (shouldLogCompletion) {
          Store.addMessage(
            label + '完成，耗時 ' +
              formatElapsedMilliseconds(monotonicNowMilliseconds() - startedAt) + '。',
            'info');
        }
        return value;
      })
      .catch(function (error) {
        var message;
        var level = 'warn';
        var lastProgress = Store.getState().busyDetail;
        if (error && error.code === 'operation_in_progress') {
          message = '請稍候，另一項作業進行中';
        } else if (error && error.code === 'operation_cancelled') {
          message = label + '已取消';
          level = 'info';
        } else if (error && error.code === 'completeness_prerequisite_failed') {
          message = label + '已阻擋：' + error.message;
        } else {
          message = label + '失敗：' + error.message;
        }
        message += '，耗時 ' +
          formatElapsedMilliseconds(monotonicNowMilliseconds() - startedAt) + '。';
        if (lastProgress) {
          message += ' 最後進度：' + lastProgress + '。';
        }
        Store.addMessage(message, level);
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
            Store.addMessage((options.refreshLabel || '重新整理') + '失敗：' + error.message, 'warn');
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
        Store.addMessage(label + '失敗：' + error.message, 'warn');
      });
  }

  /* ---- 專案租約鎖：心跳保活 / 離場釋放（控制面第六輪） ------------------------ */
  // 鏡像後端：project.load 成功後每 heartbeatSeconds 秒續租一次；實際離開專案時釋放。純鏡像、零商業邏輯。

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
      runBackground('租約續期', function () { return global.JetApi.projectHeartbeat({}); });
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

  function departureRetryMessage(state, retryAction) {
    return state.activeRequestId
      ? '目前作業尚未結束。請按「取消作業」，待取消完成後再重試「' + retryAction + '」。'
      : '目前作業尚未結束。請等待作業完成後再重試「' + retryAction + '」。';
  }

  // busy 時主畫面與訊息 rail 會被 overlay 遮住；提示必須同步寫進可見的 busyDetail。
  function showBusyDepartureGuidance(retryAction) {
    var state = Store.getState();
    var message = departureRetryMessage(state, retryAction);
    Store.setBusyDetail(message);
    Store.addMessage(message, 'warn');
  }

  function handleProjectDepartureFailure(error, retryAction) {
    if (error && error.code === 'operation_in_progress') {
      var state = Store.getState();
      var message = departureRetryMessage(state, retryAction);
      if (state.activeRequestId) {
        Store.setBusyDetail(message);
      } else {
        Store.setBusy(false);
      }
      Store.addMessage(message, 'warn');
      return;
    }
    Store.setBusy(false);
    Store.addMessage(
      '無法完成離場，已保留目前畫面：' + (error && error.message ? error.message : '未知錯誤'),
      'warn');
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

  /* ---- 專案層契約鏡像的讀取輔助（純讀取，不重算任何審計語意） -------------- */

  // 目前專案的科目分類；project.load 尚未回傳時退回內建五類，讓畫面有可讀初值。
  // 這只是顯示用退路：保存與判定一律以後端 taxonomy revision 為準。
  function taxonomyCategories(state) {
    var snapshot = state && state.taxonomy;
    var categories = snapshot && Array.isArray(snapshot.categories) ? snapshot.categories : [];
    return categories.length ? categories : ACCOUNT_TAXONOMY_BUILT_INS;
  }

  // 分類身分 → 目前顯示名稱；未知身分退回原字串（同後端 renderer 的 fallback 慣例）。
  function taxonomyCategoryLabel(state, categoryId) {
    var hit = taxonomyCategories(state).filter(function (c) { return c.categoryId === categoryId; })[0];
    if (hit) { return hit.label; }
    var builtIn = ACCOUNT_TAXONOMY_BUILT_INS.filter(function (c) {
      return c.categoryId === categoryId;
    })[0];
    return builtIn ? builtIn.label : categoryId;
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

  // 目前已提交 GL 配對的攸關資料元素欄位定義；未提交或舊版配對時為空陣列。
  function committedRdeFields(state) {
    var committed = state && state.mapping ? state.mapping.gl.committed : null;
    var options = committed ? committed.options : null;
    return options && Array.isArray(options.rdeFields) ? options.rdeFields : [];
  }

  function rdeFieldLabel(state, fieldId) {
    var hit = committedRdeFields(state).filter(function (f) { return f.fieldId === fieldId; })[0];
    return hit ? hit.label : fieldId;
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
        '<button type="button" class="th-sort" data-sort-key="' + esc(col.key) + '" title="點一下排序，再點一下反向">' +
        esc(col.label) + '<span class="th-sort__mark" aria-hidden="true"></span></button></th>';
    }).join('');
  }

  // 「依傳票號碼查看」或「依科目編號查看」：送出後從第一頁重載，只在資料庫端比對，不在畫面過濾。
  function pageSearchHtml(label) {
    return '<form class="page-search" data-page-search>' +
      '<label class="page-search__label"><span>' + esc(label) + '</span>' +
        '<input type="search" class="page-search__input" data-page-search-input maxlength="200" placeholder="輸入一部分即可"></label>' +
      '<button type="submit" class="btn btn--ghost btn--tiny">查看</button>' +
      '<button type="button" class="btn btn--ghost btn--tiny" data-page-search-clear hidden>清除</button>' +
    '</form>';
  }

  // 可排序、可搜尋的分頁表。狀態只有游標、排序與搜尋文字；換排序或搜尋就清掉列從第一頁重載，
  // 「載入更多」接續同一個排序與搜尋。首擊（或第一次重載）先清掉呼叫端放的首屏預覽列，避免兩套順序混排。
  // opts: fetchPage(cursor, sort, search) → Promise({ rows, nextCursor })、appendRows(rows)、clearRows()、
  //       loadMore（按鈕，可為 null）、table（含 th[data-sort-key] 的表）、search（pageSearchHtml 的表單，可為 null）、
  //       autoLoad（掛上就載入第一頁）。
  function bindPagedTable(root, opts) {
    var state = { cursor: null, sort: null, search: '', started: false, busy: false };
    var loadMore = opts.loadMore || null;
    var table = opts.table || null;
    var form = opts.search || null;

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

    // 重載失敗時把排序與搜尋退回上一次成功的狀態：表格裡還是舊列，表頭與輸入框不能宣稱新的排序或搜尋。
    function fetch(reset, label, previous) {
      if (state.busy) { return Promise.resolve(); }
      state.busy = true;
      var prev = loadMore ? loadMore.textContent : '';
      if (loadMore) { loadMore.disabled = true; loadMore.textContent = '載入中…'; }
      // Only explicitly read-only tables may refresh while another operation owns the busy overlay.
      var execute = opts.background ? runBackground : run;
      return execute(label, function () {
        return opts.fetchPage(reset ? null : state.cursor, state.sort, state.search).then(function (data) {
          if (reset || !state.started) { opts.clearRows(); state.started = true; }
          opts.appendRows((data && data.rows) || []);
          applySortMarks();
          state.cursor = data ? data.nextCursor : null;
          if (loadMore) { loadMore.hidden = state.cursor == null; loadMore.disabled = false; loadMore.textContent = prev; }
        }).catch(function (error) {
          if (previous) { state.sort = previous.sort; state.search = previous.search; applySortMarks(); applySearchBox(); }
          if (loadMore) { loadMore.disabled = false; loadMore.textContent = prev; }
          throw error;
        }).finally(function () { state.busy = false; });
      });
    }
    function snapshot() { return { sort: state.sort, search: state.search }; }

    if (loadMore) { loadMore.addEventListener('click', function () { fetch(false, '載入更多'); }); }
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
    if (form) {
      form.addEventListener('submit', function (event) {
        event.preventDefault();
        if (state.busy) { return; }
        var previous = snapshot();
        state.search = (input && input.value ? input.value : '').trim();
        applySearchBox();
        fetch(true, '依號碼查看明細', previous);
      });
      if (clear) {
        clear.addEventListener('click', function () {
          if (state.busy) { return; }
          var previous = snapshot();
          state.search = '';
          applySearchBox();
          fetch(true, '依號碼查看明細', previous);
        });
      }
    }
    if (opts.autoLoad) { fetch(true, '載入明細'); }
    return { reload: function () { return fetch(true, '載入明細'); } };
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

  /* ---- 後端生命週期狀態的鏡像片段 ------------------------------------------ */

  // 資料版本已失效：只鏡射 project.load.staleState 的布林，不由 latestRuns === null 猜測。
  function staleNoticeHtml(state, kind, message) {
    var stale = state && state.staleState ? state.staleState[kind] : false;
    return stale
      ? '<p class="panel__warn" data-bind="stale-' + esc(kind) + '">' + esc(message) + '</p>'
      : '';
  }

  // 舊版欄位配對需重新確認：後端在新邏輯 action 前一律 mapping_review_required fail closed，
  // 畫面只鏡射該旗標並指路回「欄位配對」，不自行判定哪一側是舊版。
  function mappingReviewBannerHtml(state) {
    if (!state || !state.mappingReviewRequired) { return ''; }
    return '<p class="panel__warn" data-bind="mapping-review-required">' +
      '這個案件的欄位配對是舊版本，需要重新確認後才能執行驗證、預篩選、篩選與匯出。' +
      '請在「欄位配對」逐一檢查 GL 與 TB 的設定並重新確認配對。</p>';
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

  /* ---- 流程閘門（state-oriented：每一步的進入條件與缺漏） ------------------- */

  function ruleRunId(run) {
    return run && run.resultRef ? run.resultRef.runId : null;
  }

  var COMPLETENESS_RERUN_REASON = '請重新執行資料驗證，以取得目前資料的結果';

  // 舊版欄位配對的閘門說明（與 mappingReviewBannerHtml 同一份事實，措辭就近可行動）。
  var MAPPING_REVIEW_MISSING = '重新確認 GL 與 TB 欄位配對（目前為舊版本）';

  // 完整性適格只鏡射 validate.run 的 backend verdict。舊摘要缺少 additive eligibility
  // 時 fail closed 並要求重跑；不得在前端以 part A／B raw facts 重算第二份裁定。
  function completenessEligibility(validation) {
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

  function allScenarioPositions(state) {
    return (state.filter.savedScenarios || []).map(function (_, index) { return index + 1; });
  }

  // 步驟 index 的進入條件；missing 描述「還缺什麼」。
  // 對齊 docs/jet-frontend-description.md §5 閘門模型（6 步版本）。
  function stepGate(state, index) {
    var missing = [];
    switch (index) {
      case 1:
        if (!state.project) { missing.push('建立或載入案件'); }
        break;
      case 2:
        if (!state.importState.gl) { missing.push('匯入 GL 檔案'); }
        if (!state.importState.tb) { missing.push('匯入 TB 檔案'); }
        break;
      case 3:
        if (!state.mapping.gl.committed) { missing.push('確認 GL 欄位配對'); }
        if (!state.mapping.tb.committed) { missing.push('確認 TB 欄位配對'); }
        // 舊版配對：後端在驗證、預篩選、篩選與匯出前一律 fail closed，畫面只鏡射同一封鎖，
        // 不讓使用者走進一條必定被拒絕的路徑；修復入口仍在「欄位配對」。
        if (state.mappingReviewRequired) { missing.push(MAPPING_REVIEW_MISSING); }
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
        if (state.filter.savedScenarios.length === 0) { missing.push('保存至少一個篩選情境'); }
        if (!state.filterResultRef) {
          missing.push('完成目前版本的條件篩選');
        } else if (!findCurrentReportArtifact(state, 'criteriaSelectionReport', {
          validationRunId: ruleRunId(state.lastRuns.validate),
          scenarioRevision: state.filterResultRef.revision,
          scenarioPositions: allScenarioPositions(state)
        })) {
          missing.push('產生目前版本的條件篩選報告');
        }
        break;
    }
    return { ok: missing.length === 0, missing: missing };
  }

  // 線性流程：進入 index 必須滿足沿路所有閘門。
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

  // 鎖定步驟對使用者顯示的簡短前置條件（鏡像 INTEGRATION_MAP §E）。
  // 這是純顯示文字，非閘門：放行與否仍以 stepGate / isStepReachable 為準。
  var STEP_LOCKED_REASON = {
    1: '需先建立案件',
    2: '需先完成資料匯入',
    3: '需先完成欄位配對',
    4: '需先完成資料驗證',
    5: '需完成條件篩選並產生報告'
  };

  // 鎖定原因沿線找第一個未滿足閘門；完整性所在的 Step 4 使用
  // backend eligibility 的動態原因，其餘步驟維持既有短句。
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

  // 文件流／目錄的「呈現狀態」彙整：完全由既有閘門推導，不新增任何放行語意。
  // status = 'current'（=currentStepIndex）｜'done'（本步輸出已滿足下一步閘門）｜
  //          'available'（可達但未完成）｜'locked'（不可達）。
  // 「完成」定義沿用 app.js 既有規則（stepGate(index+1).ok），最後一步永不計為 done。
  function stepPresentation(state, index) {
    var steps = Store.STEPS;
    var status;
    if (index === state.currentStepIndex) {
      status = 'current';
    } else if (index < steps.length - 1 && stepGate(state, index + 1).ok) {
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

  // 已完成步數（進度列用）：本步輸出已滿足下一步閘門者計入；與 done 呈現一致。
  function doneStepCount(state) {
    var steps = Store.STEPS;
    var count = 0;
    for (var i = 0; i < steps.length - 1; i++) {
      if (stepGate(state, i + 1).ok) { count++; }
    }
    return count;
  }

  // 統一導航入口：切換步驟並把目前位置持久化到專案（resume 用）。
  function gotoStep(index) {
    var state = Store.getState();
    if (index < 0 || index >= Store.STEPS.length || !isStepReachable(state, index)) {
      return;
    }
    Store.setStepIndex(index);
    persistProgress(index);
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

  var mergeProgressSave = createLatestSaveMerger(function (snapshot) {
    var state = Store.getState();
    if (!global.JetApi.isReady() || !state.project || state.project.projectId !== snapshot.projectId) {
      return Promise.resolve();
    }
    return global.JetApi.projectSaveProgress({ currentStep: snapshot.currentStep });
  }, function (error) {
    Store.addMessage('進度保存失敗：' + error.message, 'warn');
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
      Store.addMessage('應用程式尚未連線，請直接關閉視窗。', 'warn');
      return;
    }

    var state = Store.getState();
    if (state.busy) {
      showBusyDepartureGuidance('儲存並結束');
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
          Store.setPickerFeedback('案件已安全離開，但應用程式無法關閉：' + error.message);
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
    return run('載入專案清單', function () {
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
      onError: function (message) { Store.setPickerFeedback(message); }
    });
  }

  // 統一的「回專案選擇」：清空 workflow 與步驟模組狀態再切回 picker。
  function goBackToPicker() {
    pickerProjectResponseGuard.invalidate();
    resetStepModules();
    Store.resetWorkflow();
    Store.setView('picker');
    loadProjects();
  }

  // 頁首「回專案選擇」：比照 exitApp 先保存目前步驟位置，再切回 picker（保存失敗仍允許切回）。
  function backToPickerFromHeader() {
    var state = Store.getState();
    if (state.busy) {
      showBusyDepartureGuidance('回專案選擇');
      return;
    }

    Store.setBusy(true, '回專案選擇');
    var save = (state.view === 'workflow' && state.project && global.JetApi.isReady())
      ? persistProgress(state.currentStepIndex)
      : Promise.resolve();

    save.catch(function (error) {
      Store.addMessage('進度保存失敗：' + error.message, 'warn');
    }).then(function () {
      return releaseProjectLease();
    }).then(
      function () {
        Store.setBusy(false);
        goBackToPicker();
      },
      function (error) {
        handleProjectDepartureFailure(error, '回專案選擇');
      });
  }

  function openProject(projectId) {
    pickerProjectResponseGuard.invalidate();
    Store.setPickerFeedback(null);
    return run('載入專案', function () {
      return global.JetApi.projectLoad({ projectId: projectId }).then(function (data) {
        applyLoadedProject(data); // 心跳於 applyLoadedProject 內起（picker/建案共用單一起點）

        // 還原持久化的訊息歷史（log.recent）；失敗不影響專案載入
        return global.JetApi.logRecent({}).then(function (log) {
          Store.seedMessages(log.messages || []);
        }).catch(function () {}).then(function () {
          Store.addMessage('已載入專案「' + data.project.projectId + '」。', 'info');
        });
      });
    }, {
      onError: function (message, error) {
        Store.setPickerFeedback(message, {
          projectId: projectId,
          errorCode: error && error.code,
          correlationId: error && error.correlationId
        });
      }
    });
  }

  // project.load 的 GL mapping v2 options 鏡像。缺欄時採後端同一份 legacy normalization
  // 的可顯示預設（未配核准日、無過帳狀態政策、人工 1／自動 0、無攸關資料元素欄位）；
  // 這只是待確認草稿，不代表已提交，也不會把 v1 的 formatVersion 偽升為 2。
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

  function applyLoadedProject(data, options) {
    Store.setProject(data.project);

    Store.setImportResult('gl', data.importState ? data.importState.gl : null);
    Store.setImportResult('tb', data.importState ? data.importState.tb : null);
    Store.setAccountMappingState(data.importState ? data.importState.accountMapping : null);
    Store.setAuthorizedPreparerState(data.importState ? data.importState.authorizedPreparer : null);
    Store.setCalendarState(data.importState ? data.importState.calendar : null);
    resetStepModules();

    // 專案層契約鏡像：科目分類與後端 stale 判定必須在配對快照之前套用，
    // 讓 setMappingCommitted 重新推導的舊版旗標不被稍後的整份覆寫蓋掉。
    Store.setTaxonomy(data.taxonomy || null);
    Store.setStaleState(data.staleState || null);

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
        formatVersion: data.mapping.gl.formatVersion || null,
        projectedRowCount: null
      });
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
        formatVersion: data.mapping.tb.formatVersion || null,
        projectedRowCount: null
      });
    } else {
      Store.replaceMappingDraft('tb', {});
      Store.setMappingCommitted('tb', null);
    }

    // 後端旗標優先：v1 mapping 的判定包含前端沒有的 metadata 事實，因此以 response 為準；
    // 之後的成功 recommit 才由 Store 依 formatVersion 重新推導。
    if (typeof data.mappingReviewRequired === 'boolean') {
      Store.setMappingReviewRequired(data.mappingReviewRequired);
    }

    // 規則執行結果與已保存情境的 resume（原樣回放後端 latestRuns / filterScenarios）。
    Store.setLastRun('validate', data.latestRuns ? data.latestRuns.validate : null);
    Store.setLastRun('prescreen', data.latestRuns ? data.latestRuns.prescreen : null);
    Store.setSavedScenarios(data.filterScenarios || []);
    Store.setFilterResultRef(data.filterResultRef || null);
    Store.restoreFilterPopulationScope(data.filterResultRef || null, data.filterScenarios || []);
    Store.setReportArtifacts(data.reportArtifacts || []);
    Store.setFilterDraft(null); // 重設為空草稿（避免殘留上一個案件的條件）

    Store.setView('workflow');

    // 租約鎖心跳：任何「載入成功、進入 workflow」的路徑都在此起計時器——picker 開案（openProject）
    // 與建案流程（create-step 的 project.load→applyLoadedProject）共用此單一起點，避免建案路徑漏起心跳
    // （租約永不續期、120 秒後過期）。project.load 被鎖時已拋 project_locked、走不到這裡。heartbeatSeconds
    // 由 project.load 回應提供（本地專案為常數、其 heartbeat 是後端 no-op）。
    startHeartbeat(data.heartbeatSeconds);

    // 建立或載入案件時套用後端狀態，但停在目前步驟，不自行推進到匯入。
    if (options && options.stayOnCurrentStep) {
      return;
    }

    // resume 到上次保存的位置；0 也是合法位置（建立案件步驟）。
    var saved = typeof data.project.currentStep === 'number' ? data.project.currentStep : 1;
    var stepIndex = Math.min(Math.max(saved, 0), Store.STEPS.length - 1);

    // 防呆：保存位置若超出閘門允許範圍（如資料被清除），退回最近可達的步驟。
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

  // 只核對 contract 中的 sourceRef；報告是否 current 仍以後端回傳的 stale 為第一道閘門。
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

  function reportArtifactListHtml(artifacts, emptyText, options) {
    var list = artifacts || [];
    if (list.length === 0) {
      return '<p class="report-artifacts__empty">' + esc(emptyText || '尚未產生報告。') + '</p>';
    }

    return '<div class="report-artifacts">' + list.map(function (artifact) {
      var generated = new Date(artifact.generatedUtc);
      var generatedText = isNaN(generated.getTime())
        ? '時間未知'
        : generated.toLocaleString('zh-Hant', { hour12: false });
      var bytes = Number(artifact.bytes);
      var bytesText = isNaN(bytes) ? '大小未知' : bytes.toLocaleString('en-US') + ' 位元組';
      // 檔案狀態只是提醒：審計員在 JET 之外改過或刪了檔案都不擋，重新匯出就好。
      var fileStateText = artifact.fileState === 'modifiedOutside'
        ? '已在 JET 之外修改'
        : (artifact.fileState === 'missing' ? '檔案不存在，重新匯出即可' : '');
      return (
        '<div class="report-artifact" data-artifact-id="' + esc(artifact.artifactId) + '">' +
          '<span class="report-artifact__copy">' +
            '<span class="report-artifact__name">' + esc(artifact.fileName || '未命名報告') + '</span>' +
            (artifact.fullPath ? '<span class="report-artifact__path" style="display:block;overflow-wrap:anywhere;user-select:text">儲存位置：' + esc(artifact.fullPath) + '</span>' : '') +
            '<span class="report-artifact__meta">' + esc(generatedText) + '，' + esc(bytesText) +
              (fileStateText ? '，<span class="report-artifact__state">' + esc(fileStateText) + '</span>' : '') +
              (options && options.history && artifact.stale
                ? '，<span class="report-artifact__validity">先前資料或條件的版本</span>' : '') +
            '</span>' +
          '</span>' +
          (options && options.reveal
            ? '<button type="button" class="btn btn--ghost btn--tiny" data-open-artifact="' +
              esc(artifact.artifactId) + '"' + (artifact.fileState === 'missing' ? ' disabled' : '') +
              '>在資料夾中顯示</button>' : '') +
        '</div>'
      );
    }).join('') + '</div>';
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
      if (type === 'voucher') { compound.side = 'all'; compound.quantifier = 'any'; }
      return compound;
    }
    if (type === 'entityFrequency') {
      return { type: type, field: 'accNum', countUnit: 'entries', countOperator: 'equals', countFrom: '1', countTo: '' };
    }
    if (type === 'fieldValue') { return { type: type, join: 'AND', field: 'postDate', operator: 'in', values: [], includeBlank: false }; }
    if (type === 'accountSide') { return { type: type, join: 'AND', drCr: 'debit', categoryMode: 'is', categoryIds: ['builtin.cash'] }; }
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
      // 借貸組合雙側一律以 taxonomy 分類身分陣列承載（manifest 2026-08-14 雙側多選）；
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
      maxEntries: (type === 'customPreparerEntryCount' || type === 'customAccountEntryCount') ? '11' : '',
      windowDays: ''
    };
  }

  /* ---- 共用：「載入更多」keyset 接列膠水 ------------------------------------ */

  // 綁一顆「載入更多」鈕到 keyset 分頁:點擊 → 帶當前 cursor 呼叫 fetchPage(cursor)
  // → 後端回 { rows, nextCursor };appendRows(rows) 由呼叫端提供(把列接到既有 tbody);
  // cursor 以閉包保存累進;nextCursor 為 null 表已到底 → 移除鈕。
  // 純 DOM／呼叫膠水:不算差異、不判命中、不組 SQL;只發 action、接 data.rows、管 cursor 與載入態。
  // fetchPage 回傳 Promise<{ rows, nextCursor }>。
  //
  // 重要:cursor 一律自 null 起(keyset 第一頁,ASC)——不從鈕讀任何 seed。展開時上方的預覽列
  // 是另一套排序(ABS-DESC top-50 / filter 前 10),與 page 的 keyset ASC 不一致。因此「首擊」時
  // 先呼叫 clearTarget()(由呼叫端提供,清空目標 tbody 既有預覽列)再 append 第一頁:一旦點「載入
  // 更多」即由預覽切換為單一一致排序、無重複的完整列表。clearTarget 省略時不清(首屏即空表的情形)。
  function bindLoadMore(buttonEl, fetchPage, appendRows, clearTarget) {
    if (!buttonEl) { return; }
    var cursor = null;
    var cleared = false;
    buttonEl.addEventListener('click', function () {
      buttonEl.disabled = true;
      var prev = buttonEl.textContent;
      buttonEl.textContent = '載入中…';
      run('載入更多', function () {
        return fetchPage(cursor).then(function (data) {
          if (!cleared) {
            // 首擊:清掉上方預覽列一次,再接 page 第一頁。
            if (typeof clearTarget === 'function') { clearTarget(); }
            cleared = true;
          }
          appendRows((data && data.rows) || []);
          cursor = data ? data.nextCursor : null;
          if (cursor == null) {
            buttonEl.remove();
          } else {
            buttonEl.disabled = false;
            buttonEl.textContent = prev;
          }
        }).catch(function (error) {
          // 還原鈕態讓使用者可重試;錯誤交回 run() 顯示給使用者。
          buttonEl.disabled = false;
          buttonEl.textContent = prev;
          throw error;
        });
      });
    });
  }

  // 把後端回傳的列(物件陣列)依欄位鍵序轉成 <tr> 並接到 tbody。
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
        '<p class="panel__hint">尚未建立或載入專案。</p>' +
        '<div class="panel__actions">' +
          '<button type="button" class="btn" data-action="back-picker">回專案選擇</button>' +
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
    PRESCREEN_POSITIONING_COPY: PRESCREEN_POSITIONING_COPY,
    prescreenPositioningCopy: prescreenPositioningCopy,
    ACCOUNT_PAIR_MODE_OPTIONS: ACCOUNT_PAIR_MODE_OPTIONS,
    SPECIAL_PAIR_MODE_OPTIONS: SPECIAL_PAIR_MODE_OPTIONS,
    ACCOUNT_SIDE_MODE_OPTIONS: ACCOUNT_SIDE_MODE_OPTIONS,
    ACCOUNT_COMBINATION_OPTIONS: ACCOUNT_COMBINATION_OPTIONS,
    FILTER_SCENARIO_TEMPLATES: FILTER_SCENARIO_TEMPLATES,
    FILTER_CATEGORY_LIST_SEPARATOR: FILTER_CATEGORY_LIST_SEPARATOR,
    TEXT_MODE_OPTIONS: TEXT_MODE_OPTIONS,
    FILTER_MATCH_SCOPE_OPTIONS: FILTER_MATCH_SCOPE_OPTIONS,
    TEXT_SET_MODE_OPTIONS: TEXT_SET_MODE_OPTIONS,
    TEXT_SET_NORMALIZATION_OPTIONS: TEXT_SET_NORMALIZATION_OPTIONS,
    TEXT_SET_MAX_VALUES: TEXT_SET_MAX_VALUES,
    FILTER_TEXT_FIELDS: FILTER_TEXT_FIELDS,
    FILTER_DATE_FIELDS: FILTER_DATE_FIELDS,
    GL_APPROVAL_DATE_MODES: GL_APPROVAL_DATE_MODES,
    RDE_VALUE_TYPES: RDE_VALUE_TYPES,
    RDE_MAX_LABEL_LENGTH: RDE_MAX_LABEL_LENGTH,
    MANUAL_AUTO_DEFAULTS: MANUAL_AUTO_DEFAULTS,
    VALUE_PROFILE_LIMIT: VALUE_PROFILE_LIMIT,
    TYPED_TEXT_OPERATORS: TYPED_TEXT_OPERATORS,
    TYPED_DATE_OPERATORS: TYPED_DATE_OPERATORS,
    TYPED_MONEY_OPERATORS: TYPED_MONEY_OPERATORS,
    TYPED_AMOUNT_BASIS_OPTIONS: TYPED_AMOUNT_BASIS_OPTIONS,
    TYPED_SET_MAX_VALUES: TYPED_SET_MAX_VALUES,
    typedOperatorsForValueType: typedOperatorsForValueType,
    typedOperatorLabel: typedOperatorLabel,
    typedOperatorCarrier: typedOperatorCarrier,
    ACCOUNT_TAXONOMY_ROLES: ACCOUNT_TAXONOMY_ROLES,
    ACCOUNT_TAXONOMY_BUILT_INS: ACCOUNT_TAXONOMY_BUILT_INS,
    TAXONOMY_MAX_LABEL_LENGTH: TAXONOMY_MAX_LABEL_LENGTH,
    builtInCategoryIdForLegacyLabel: builtInCategoryIdForLegacyLabel,
    taxonomyCategories: taxonomyCategories,
    taxonomyCategoryLabel: taxonomyCategoryLabel,
    committedRdeFields: committedRdeFields,
    rdeFieldLabel: rdeFieldLabel,
    dynamicColumnCells: dynamicColumnCells,
    dynamicColumnHeadHtml: dynamicColumnHeadHtml,
    sortableHeadCellsHtml: sortableHeadCellsHtml,
    pageSearchHtml: pageSearchHtml,
    bindPagedTable: bindPagedTable,
    staleNoticeHtml: staleNoticeHtml,
    mappingReviewBannerHtml: mappingReviewBannerHtml,
    // DOM / 執行輔助
    $: $,
    setText: setText,
    esc: esc,
    money: money,
    createLatestResponseGuard: createLatestResponseGuard,
    run: run,
    runBackground: runBackground,
    isRequired: isRequired,
    glFieldLabel: glFieldLabel,
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
    doneStepCount: doneStepCount,
    gotoStep: gotoStep,
    exitApp: exitApp,
    backToPickerFromHeader: backToPickerFromHeader,
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
    reportArtifactMatches: reportArtifactMatches,
    findCurrentReportArtifact: findCurrentReportArtifact,
    currentReportArtifacts: currentReportArtifacts,
    reportArtifactHistory: reportArtifactHistory,
    reportArtifactListHtml: reportArtifactListHtml,
    bindLoadMore: bindLoadMore,
    appendRowsToTbody: appendRowsToTbody
  };
})(window);
