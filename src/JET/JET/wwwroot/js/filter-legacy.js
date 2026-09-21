/* 2024 JE form entry definitions. Only constructs the common AST; never evaluates ledger rows. */
(function (global) {
  'use strict';
  var catalogue = /* legacy-catalog:start */
  {
    "id": "je-form-2024-1210",
    "source": "XXX_2024_JE篩選條件_1210.xlsm",
    "conditions": [
      { "letter": "A", "label": "於期末財務報表日後核准之分錄", "source": "JE篩選條件組合!A2:C2",
        "help": "依核准日大於或等於案件的財報準備開始日判斷，沿用 IDEA 實際規則。不是查核期末日，也不是總帳日期。",
        "rules": [{ "type": "prescreen", "prescreenKey": "postPeriodApproval" }] },
      { "letter": "B", "label": "分錄摘要出現預設之特定描述", "source": "(i)JE篩選條件說明!A3:B3",
        "help": "使用現行 25 個預設詞，包含舊版繁簡體詞及新版帳外。需改清單時，可加入自訂的摘要關鍵字。",
        "rules": [{ "type": "prescreen", "prescreenKey": "suspiciousKeywords" }] },
      { "letter": "C", "label": "未預期出現之特定借貸組合", "source": "(i)JE篩選條件說明!A4:B4",
        "help": "貸方收入，且整張傳票沒有應收、現金或預收的借方。現行規則也會選出只有收入貸方的傳票；命中標記在收入貸方，其餘明細供覆核。與 KCT C 不同。",
        "rules": [{ "type": "prescreen", "prescreenKey": "unexpectedAccountPair" }] },
      { "letter": "D", "label": "分錄金額中有連續 0 的尾數", "source": "(i)JE篩選條件說明!A5:B5",
        "help": "依已確認的新版方式，固定預設 6 位連續零，忽略小數，整數部分為零不命中。沒有沿用舊版平均金額的動態門檻；可另選自訂位數。",
        "rules": [{ "type": "prescreen", "prescreenKey": "trailingZeros" }] },
      { "letter": "E", "label": "人工傳票", "source": "(i)JE篩選條件說明!A6:B6",
        "help": "依已配對的人工或自動來源，選取人工分錄。要判斷整張或整側全部人工，可使用整張傳票或指定側的條件。",
        "rules": [{ "type": "manualAuto", "isManual": "true" }] },
      { "letter": "F", "label": "分錄無摘要描述（空白摘要）", "source": "(i)JE篩選條件說明!A7:B7",
        "help": "摘要為空值或只有空白字元。沒有配對摘要欄時，先回欄位配對指定來源。",
        "rules": [{ "type": "prescreen", "prescreenKey": "blankDescription" }] },
      { "letter": "G", "label": "總帳日期在非工作日之週末", "source": "(i)JE篩選條件說明!A8:B8",
        "help": "依案件設定的週末判斷總帳日期。補班或加班例外用 I 加入，不會自行排除。",
        "rules": [{ "type": "fieldValue", "field": "postDate", "operator": "isWeekend" }] },
      { "letter": "H", "label": "總帳日期在國定假日", "source": "(i)JE篩選條件說明!A9:B9",
        "help": "使用已匯入的假日清單，不自動加入颱風假。額外休假可用指定日期條件組合；例外用 I。",
        "rules": [{ "type": "fieldValue", "field": "postDate", "operator": "isHoliday" }] },
      { "letter": "I", "label": "排除總帳日期的補班日或加班日", "source": "其他條件!A2:C4",
        "help": "配合 G 或 H 使用。先帶入排除補班日與每月 1–5 號的工作簿範例，可改日子，或改成指定日期及月底天數。只影響總帳日期。",
        "rules": [{ "type": "group", "rules": [
          { "type": "fieldValue", "field": "postDate", "operator": "isNotMakeupDay" },
          { "type": "fieldValue", "field": "postDate", "operator": "dayOfMonthNotIn", "values": ["1","2","3","4","5"] }
        ] }] },
      { "letter": "J", "label": "核准日期在非工作日之週末", "source": "(i)JE篩選條件說明!A11:B11",
        "help": "使用核准日期，與 G 的總帳日期分開。補班或加班例外用 L。",
        "rules": [{ "type": "fieldValue", "field": "docDate", "operator": "isWeekend" }] },
      { "letter": "K", "label": "核准日期在國定假日", "source": "(i)JE篩選條件說明!A12:B12",
        "help": "核准日期比對已匯入的假日清單。與 H 分開，例外用 L。",
        "rules": [{ "type": "fieldValue", "field": "docDate", "operator": "isHoliday" }] },
      { "letter": "L", "label": "排除核准日期的補班日或加班日", "source": "其他條件!A13:C15",
        "help": "配合 J 或 K 使用。先帶入排除補班日與每月 1–5 號的範例，可改日子或日期。與 I 各自保存。",
        "rules": [{ "type": "group", "rules": [
          { "type": "fieldValue", "field": "docDate", "operator": "isNotMakeupDay" },
          { "type": "fieldValue", "field": "docDate", "operator": "dayOfMonthNotIn", "values": ["1","2","3","4","5"] }
        ] }] },
      { "letter": "M", "label": "僅考量借方傳票", "source": "(i)JE篩選條件說明!A14:B14",
        "help": "只標記符合條件的借方分錄，完整傳票仍可展開覆核。零元沿用新版借方判斷。",
        "rules": [{ "type": "drCrOnly", "drCr": "debit" }] },
      { "letter": "N", "label": "僅考量貸方傳票", "source": "(i)JE篩選條件說明!A15:B15",
        "help": "只標記符合條件的貸方分錄，完整傳票仍可展開覆核。",
        "rules": [{ "type": "drCrOnly", "drCr": "credit" }] },
      { "letter": "O", "label": "其他特定欄位篩選", "source": "其他條件!A24:D26",
        "help": "先選已配對的文字欄位，再填關鍵字。可用摘要、人員、部門等額外欄位，沒有來源時先回欄位配對。",
        "rules": [{ "type": "fieldValue", "field": "description", "operator": "contains", "values": ["迴轉"] }] },
      { "letter": "P", "label": "其他借貸組合", "source": "其他條件!A35:E37；特定借貸組合填寫範例!A2:E11",
        "help": "可選科目代號或科目分類。同張傳票至少一筆指定借方及一筆指定貸方，兩份清單各自視為集合，不逐項配對。排除整側科目用不存在符合；分類可直接選借貸組合的查找方式。",
        "categoryRules": [{ "type": "specialAccountCategoryPair", "pairMode": "drAndCr", "categorySelection": "node", "debitCategoryIds": [], "creditCategoryIds": [] }],
        "rules": [{ "type": "group", "rules": [
          { "type": "voucher", "side": "debit", "quantifier": "any", "rules": [{ "type": "fieldValue", "field": "accNum", "operator": "in", "values": [] }] },
          { "type": "voucher", "side": "credit", "quantifier": "any", "rules": [{ "type": "fieldValue", "field": "accNum", "operator": "in", "values": [] }] }
        ] }] },
      { "letter": "Q", "label": "其他特定尾數", "source": "其他條件!A46:C47",
        "help": "帶入工作簿範例 99999，可改尾數。忽略正負與小數，保留所填位數及前導零。",
        "rules": [{ "type": "fieldValue", "field": "amount", "operator": "endsWithDigits", "value": "99999", "amountBasis": "absolute" }] },
      { "letter": "R", "label": "特定日期篩選", "source": "其他條件!A55:C56",
        "help": "先選日期欄，再填起訖日期；區間包含兩端，也可改用指定多日、每月幾日或月初月底。",
        "rules": [{ "type": "fieldValue", "field": "postDate", "operator": "between", "from": "", "to": "" }] },
      { "letter": "S", "label": "特定金額篩選", "source": "其他條件!A64:C65",
        "help": "帶入工作簿範例大於 300,000，可改門檻或區間。預設比較金額絕對值，含正負號可在條件內切換。",
        "rules": [{ "type": "fieldValue", "field": "amount", "operator": "greaterThan", "value": "300000", "amountBasis": "absolute" }] },
      { "letter": "T", "label": "科目出現次數或傳票張數", "source": "其他條件!A73:C74；(i)JE篩選條件說明!A21:B21",
        "help": "帶入少於 5 筆分錄的範例。同張傳票的多列各算一筆；要計張時改選去重傳票張數。統計以所選母體為準。",
        "rules": [{ "type": "entityFrequency", "field": "accNum", "countUnit": "entries", "countOperator": "lessThan", "countFrom": "5" }] },
      { "letter": "U", "label": "特定人員或編製傳票張數", "source": "其他條件!A82:D84",
        "help": "可選建立人員的傳票張數，或建立及核准人員的識別值。帶入少於 5 張範例；同案件相同傳票號碼算一張，空白號碼不計張。這不是授權人員名單。",
        "rules": [{ "type": "entityFrequency", "field": "createBy", "countUnit": "vouchers", "countOperator": "lessThan", "countFrom": "5" }] }
    ],
    "examples": [
      { "key": "example1", "combination": "E+G+O", "label": "範例 1：人工、週末及指定摘要", "source": "JE篩選條件組合!E2:G2；其他條件!C25:C26", "rationale": "工作簿範例：檢查人工週末分錄中的指定描述，請依案件修改理由。", "rules": [
        { "type": "manualAuto", "isManual": "true" }, { "type": "fieldValue", "field": "postDate", "operator": "isWeekend" },
        { "type": "fieldValue", "field": "description", "operator": "contains", "values": ["迴轉"] }] },
      { "key": "example2", "combination": "E+O", "label": "範例 2：人工及指定部門", "source": "JE篩選條件組合!E3:G3；其他條件!D25:D26", "rationale": "工作簿範例：檢查人工分錄的指定部門，請依案件修改理由。", "rules": [
        { "type": "manualAuto", "isManual": "true" }, { "type": "fieldValue", "fieldId": "", "operator": "contains", "values": ["總經理室"] }] },
      { "key": "example3", "combination": "P+S", "label": "範例 3：借貸科目組合及金額", "source": "JE篩選條件組合!E4:G4；特定借貸組合填寫範例!A3:B8；其他條件!C65", "rationale": "工作簿範例：檢查指定借貸組合中金額較高的分錄，請依案件修改理由。", "rules": [
        { "type": "group", "rules": [
          { "type": "voucher", "side": "debit", "quantifier": "any", "rules": [{ "type": "fieldValue", "field": "accNum", "operator": "in", "values": ["1111","1113","1114","1115","1116","2223"] }] },
          { "type": "voucher", "side": "credit", "quantifier": "any", "rules": [{ "type": "fieldValue", "field": "accNum", "operator": "in", "values": ["5550","5551","6501"] }] }] },
        { "type": "fieldValue", "field": "amount", "operator": "greaterThan", "value": "300000", "amountBasis": "absolute" }] },
      { "key": "example4", "combination": "D", "label": "範例 4：連續零尾數", "source": "JE篩選條件組合!E5:G5", "rationale": "工作簿範例：檢查規律尾數。新版固定預設 6 位，請依案件修改理由。", "rules": [
        { "type": "prescreen", "prescreenKey": "trailingZeros" }] },
      { "key": "example5", "combination": "A+E+H+I", "label": "範例 5：財報準備期間的人工假日分錄", "source": "JE篩選條件組合!E6:H6", "rationale": "工作簿範例：檢查財報準備期間的人工假日分錄，排除每月 1–3 號。請依案件修改理由。", "rules": [
        { "type": "prescreen", "prescreenKey": "postPeriodApproval" }, { "type": "manualAuto", "isManual": "true" },
        { "type": "fieldValue", "field": "postDate", "operator": "isHoliday" },
        { "type": "group", "rules": [{ "type": "fieldValue", "field": "postDate", "operator": "isNotMakeupDay" },
          { "type": "fieldValue", "field": "postDate", "operator": "dayOfMonthNotIn", "values": ["1","2","3"] }] }] }
    ]
  }
  /* legacy-catalog:end */;

  function rules(item, field) {
    var result = JSON.parse(JSON.stringify(item.rules));
    if (field) result.forEach(function (rule) {
      if (rule.type !== 'fieldValue') { return; }
      delete rule.field; delete rule.fieldId;
      rule[field.extra ? 'fieldId' : 'field'] = field.id;
    });
    return result;
  }
  global.JetLegacyFilters = { catalogue: catalogue, rules: rules };
})(window);
