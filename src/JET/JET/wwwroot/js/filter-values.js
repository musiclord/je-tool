/* Value editors only build conditions. Matching and blank/category decisions belong to the backend. */
(function (global) {
  'use strict';
  var Ui = global.JetUi;
  // 值清單上限一律用 Ui.TYPED_SET_MAX_VALUES：fieldValue 的清單與文字包含比對，
  // 後端都以 FilterScenarioLimits.MaxTypedInValuesPerRule 驗證（架構測試守住本檔不寫死上限）。
  // 標籤鏡像 Domain FieldValueConditions.Labels（FilterAstFrontendContractTests 逐鍵守衛）。
  var labels = { equals: '等於', notEquals: '不等於', contains: '包含任一文字', notContains: '不包含任何文字',
    startsWith: '開頭符合', notStartsWith: '開頭不符合', endsWith: '結尾符合', notEndsWith: '結尾不符合', in: '符合清單任一值', notIn: '不在清單中',
    on: '指定日期', before: '早於', onOrBefore: '當日或以前', after: '晚於', onOrAfter: '當日或以後',
    dayOfMonthIn: '每月幾日屬於', dayOfMonthNotIn: '每月幾日不屬於',
    monthStartDays: '每月月初天數', notMonthStartDays: '排除每月月初天數', monthEndDays: '每月月底天數', notMonthEndDays: '排除每月月底天數',
    // 日曆選項的入口、選項與讀回用同一個詞。
    isWeekend: '在週末', isNotWeekend: '不在週末', isHoliday: '在國定假日', isNotHoliday: '不在國定假日',
    isMakeupDay: '在補班日', isNotMakeupDay: '不在補班日', isNonBusinessDay: '在非營業日', isNotNonBusinessDay: '不在非營業日',
    // 舊版「特定數字篩選」的包含與排除，名稱照用。
    containsDigits: '整數部分包含', notContainsDigits: '整數部分不包含',
    endsWithDigits: '整數尾數符合', notEndsWithDigits: '整數尾數不符合',
    // 尾數連續多個 0：值是 0 的個數，讀回寫「尾數連續「6」個 0」。
    endsWithZeros: '尾數連續', notEndsWithZeros: '尾數沒有連續',
    greaterThan: '大於', greaterThanOrEqual: '大於或等於', lessThan: '小於', lessThanOrEqual: '小於或等於',
    between: '介於', notBetween: '不介於', isBlank: '空白', isNotBlank: '非空白' };
  var operators = {
    text: ['contains', 'notContains', 'equals', 'notEquals', 'startsWith', 'notStartsWith', 'endsWith', 'notEndsWith', 'in', 'notIn', 'isBlank', 'isNotBlank'],
    date: ['in', 'notIn', 'dayOfMonthIn', 'dayOfMonthNotIn', 'on', 'notEquals', 'before', 'onOrBefore', 'after', 'onOrAfter', 'between', 'notBetween',
      'monthStartDays', 'notMonthStartDays', 'monthEndDays', 'notMonthEndDays',
      'isWeekend', 'isNotWeekend', 'isHoliday', 'isNotHoliday', 'isMakeupDay', 'isNotMakeupDay', 'isNonBusinessDay', 'isNotNonBusinessDay', 'isBlank', 'isNotBlank'],
    amount: ['equals', 'notEquals', 'greaterThan', 'greaterThanOrEqual', 'lessThan', 'lessThanOrEqual', 'between', 'notBetween', 'in', 'notIn',
      'containsDigits', 'notContainsDigits', 'endsWithDigits', 'notEndsWithDigits', 'endsWithZeros', 'notEndsWithZeros', 'isBlank', 'isNotBlank']
  };
  function isDayOfMonth(op) { return op === 'dayOfMonthIn' || op === 'dayOfMonthNotIn'; }
  function isMonthWindow(op) { return /^(notM|m)onth(Start|End)Days$/.test(op); }
  function carrier(op, type) {
    if (/^(isBlank|isNotBlank)$/.test(op) || isCalendar(op)) { return 'none'; }
    if (/^(notBetween|between)$/.test(op)) { return 'range'; }
    if (/^(notIn|in)$/.test(op) || isDayOfMonth(op)) { return 'set'; }
    if (type === 'text' && (/^(contains|notContains)$/.test(op) || isAffix(op))) { return 'set'; }
    return 'value';
  }
  // 開頭符合與結尾符合和包含一樣收多個值；後端 FieldValueConditions.IsAffix 同一組。
  // 較早儲存的情境只帶 value；values 還不是清單時把 value 當成一個值，和後端 AffixValues 相同。
  function isAffix(op) { return /^(notS|s)tartsWith$|^(notE|e)ndsWith$/.test(op); }
  function isCalendar(op) { return /^is(Not)?(Weekend|Holiday|MakeupDay|NonBusinessDay)$/.test(op); }
  // 日曆選項下方只寫選到的那一項，並寫出本案的實際資料。
  var WEEKDAY_LABELS = [{ v: 1, label: '一' }, { v: 2, label: '二' }, { v: 3, label: '三' }, { v: 4, label: '四' }, { v: 5, label: '五' }, { v: 6, label: '六' }, { v: 0, label: '日' }];
  function weekdayText(nonWorkingDays) {
    var set = Array.isArray(nonWorkingDays) ? nonWorkingDays : [0, 6];
    var labels = WEEKDAY_LABELS.filter(function (day) { return set.indexOf(day.v) >= 0; }).map(function (day) { return day.label; });
    return labels.length ? labels.join('、') : '無（整週皆工作日）';
  }
  function calendarExplanation(operator, state) {
    var calendar = state && state.importState && state.importState.calendar || {};
    if (/Weekend$/.test(operator)) { return '第二步設定的每週休息日：' + weekdayText(calendar.nonWorkingDays); }
    if (/Holiday$/.test(operator)) {
      return Number(calendar.holidayCount) > 0 ? '第二步上傳的國定假日清單，共 ' + Ui.countText(calendar.holidayCount) + ' 天'
        // 沒有清單時這個條件停用並寫原因，原因和停用說明同一句。
        : Ui.rulePrerequisiteReason(state, 'holidayCalendar') + '。';
    }
    if (/MakeupDay$/.test(operator)) {
      return Number(calendar.makeupDayCount) > 0 ? '第二步上傳的補班日清單，共 ' + Ui.countText(calendar.makeupDayCount) + ' 天'
        // 沒有清單時這個條件比照國定假日停用並寫原因，原因和停用說明同一句。
        : Ui.rulePrerequisiteReason(state, 'makeupCalendar') + '。';
    }
    return '週末或國定假日，補班日除外';
  }
  function isTail(op) { return op === 'endsWithDigits' || op === 'notEndsWithDigits'; }
  function isDigitContains(op) { return op === 'containsDigits' || op === 'notContainsDigits'; }
  // 整數尾數與整數部分包含的值都是逗號分隔的數字，輸入、計數與檢查相同。
  function isDigitList(op) { return isTail(op) || isDigitContains(op); }
  function isZeroRun(op) { return op === 'endsWithZeros' || op === 'notEndsWithZeros'; }
  // 尾數連續多個 0 加入時預設的個數：第四步預篩選由 GL 借方平均算出的個數；還沒確認 GL 配對時留空，由審計員填。
  function zeroRunDefault() { var zeros = Ui.trailingZeroDigits(); return zeros ? String(zeros) : ''; }
  // 尾數比對與尾數連續多個 0 都只看整數部分、忽略正負號，不接金額比較基準。
  function ignoresSign(op) { return isTail(op) || isZeroRun(op) || isDigitContains(op); }
  // 條件可用的欄位。核心欄位全部列出，mapped 照後端算好的可用欄位（Ui.glFieldAvailable）；攸關資料元素欄位都已配對。
  function fields(state) {
    return Ui.FILTER_DATE_FIELDS.map(function (key) { return { id: key, label: Ui.glFieldLabel(key), type: 'date' }; })
      .concat(Ui.FILTER_TEXT_FIELDS.map(function (key) { return { id: key, label: Ui.glFieldLabel(key), type: 'text' }; }))
      .concat([{ id: 'amount', label: '金額', type: 'amount' }])
      .concat(Ui.committedRdeFields(state).map(function (field) { return { id: field.fieldId, label: field.label, type: field.valueType, sourceColumn: field.sourceColumn, extra: true }; }))
      .map(function (item) {
        item.optionLabel = item.extra ? Ui.rdeFieldOptionLabel(item) : item.label;
        item.mapped = !!item.extra || Ui.glFieldAvailable(state, item.id);
        return item;
      });
  }
  function field(rule, state) { return fields(state).find(function (item) { return item.id === (rule.fieldId || rule.field); }); }
  function fieldType(rule, state) { var selected = field(rule, state); return selected ? selected.type : (rule.__valueType || 'date'); }
  // 欄位空白的分錄預設不列入，勾選「空白也符合」才列入；與後端 GlRulePredicates.FieldValue 一致。
  // 文字欄位的否定比較預設勾選（見 blankIncludedByDefault），勾選結果照樣明確送出。
  function blankMatches(rule) { return rule.includeBlank === true; }
  function day(year, month, date) { var value = new Date(0); value.setUTCHours(0, 0, 0, 0); value.setUTCFullYear(year, month - 1, date); return value; }
  function iso(value) { return String(value.getUTCFullYear()).padStart(4, '0') + '-' + String(value.getUTCMonth() + 1).padStart(2, '0') + '-' + String(value.getUTCDate()).padStart(2, '0'); }
  function splitDelimited(raw) {
    return String(raw == null ? '' : raw).split(/[,，、\t\r\n]+/).map(function (value) { return value.trim(); }).filter(Boolean);
  }
  function delimitedList(rule, state) {
    return isDayOfMonth(rule.operator) || fieldType(rule, state) === 'text' &&
      (/^(contains|notContains)$/.test(rule.operator) || isAffix(rule.operator) || !rule.fieldId && ['createdBy', 'approvedBy', 'accountNumber'].indexOf(rule.field) >= 0);
  }
  // Mirrors the explicit formats in DateNormalizer. Unknown invariant-culture fallback formats
  // stay raw for the authoritative backend rather than being guessed by browser Date.parse.
  function normalizeDate(raw, state) {
    var text = String(raw).trim();
    var match = /^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})$/.exec(text) || /^(\d{4})(\d{2})(\d{2})$/.exec(text);
    var roc = !state || !state.project || state.project.rocDateEnabled !== false;
    if (!match && roc) {
      match = /^(1\d{2})[/.](\d{1,2})[/.](\d{1,2})$/.exec(text) || /^(1\d{2})(\d{2})(\d{2})$/.exec(text);
      if (match) { match[1] = String(Number(match[1]) + 1911); }
    }
    if (match) {
      if (/^\d{4}-\d{1,2}-\d{1,2}$/.test(text) && !/^\d{4}-\d{2}-\d{2}$/.test(text) &&
          (Number(match[1]) < 1900 || Number(match[1]) > 2100)) { return text; }
      var expected = match[1].padStart(4, '0') + '-' + match[2].padStart(2, '0') + '-' + match[3].padStart(2, '0');
      return Number(match[1]) >= 1 && Number(match[1]) <= 9999 && iso(day(Number(match[1]), Number(match[2]), Number(match[3]))) === expected ? expected : text;
    }
    // 純數字不當成日期，帶千分位的也一樣：和後端 DateNormalizer 相同，
    // 保留原文由 problem() 與後端寫出無法辨識。
    // 兩邊共用 tests/JET.Tests/Fixtures/date-normalization.json 的固定答案。
    return text;
  }
  function values(rule, state) {
    if (isDigitList(rule.operator)) { return Array.from(new Set(splitDelimited(rule.value || ""))); }
    var selected = field(rule, state);
    var list = Array.isArray(rule.values) ? rule.values : isAffix(rule.operator) && rule.value ? [rule.value] : [];
    return Array.from(new Set(list.map(function (value) {
      if (isDayOfMonth(rule.operator)) { return String(value).trim(); }
      return selected && selected.type === 'date' ? normalizeDate(value, state) : String(value).trim();
    }).filter(function (value) { return value.length > 0; })));
  }
  function wire(rule, state) {
    var selected = field(rule, state);
    var result = { type: 'fieldValue', join: rule.join || 'AND', operator: rule.operator, includeBlank: blankMatches(rule) };
    if (rule.drCr) { result.drCr = rule.drCr; }
    if (rule.fieldId) { result.fieldId = rule.fieldId; } else { result.field = rule.field; }
    var mode = carrier(rule.operator, fieldType(rule, state));
    function operand(value) {
      if (isDigitList(rule.operator)) { return values(rule, state).join(','); }
      return selected && selected.type === 'date' && !isDayOfMonth(rule.operator) && !isMonthWindow(rule.operator) ? normalizeDate(value || '', state) : String(value == null ? '' : value);
    }
    if (mode === 'value') { result.value = operand(rule.value); }
    if (mode === 'range') { result.from = operand(rule.from); result.to = operand(rule.to); }
    if (mode === 'set') { result.values = values(rule, state); }
    // 金額比較預設用絕對值，攸關資料元素的數值欄位也一樣，需要時才改成含正負號。
    if (selected && selected.type === 'amount') { result.amountBasis = rule.amountBasis || 'absolute'; }
    return result;
  }
  function summary(rule, state, compact) {
    var selected = field(rule, state);
    var value = wire(rule, state);
    var mode = carrier(rule.operator, fieldType(rule, state));
    var fieldLabel = selected ? selected.label : rule.fieldId || rule.field ? Ui.missingConditionFieldName(state, rule, 'readback') : '尚未選擇欄位';
    if (selected && selected.type === 'amount' && mode !== 'none' && !ignoresSign(rule.operator)) { fieldLabel += value.amountBasis === 'absolute' ? '絕對值' : '含正負號'; }
    var text = fieldLabel + ' ' + (labels[rule.operator] || (rule.operator ? Ui.RETIRED_CONDITION_LABEL : '尚未選擇比較方式'));
    if (mode === 'value') { text += '「' + value.value + '」'; }
    if (isMonthWindow(rule.operator)) { text += ' 天'; }
    if (isZeroRun(rule.operator)) { text += '個 0'; }
    if (mode === 'range') { text += '「' + value.from + '」至「' + value.to + '」'; }
    if (mode === 'set') { text += '「' + value.values.join('、') + '」'; }
    if ((mode !== 'none' || isCalendar(rule.operator)) && (!compact || blankMatches(rule))) { text += blankMatches(rule) ? '；空白也符合' : '；空白不列入'; }
    return text;
  }
  // 條件標題的欄位名稱；欄位已不存在時寫畫面名稱，不印程式代號。
  function fieldTitle(rule, state) {
    var selected = field(rule, state);
    return selected ? selected.label : Ui.missingConditionFieldName(state, rule, 'title');
  }
  // 欄位下拉：同型別欄位與其他型別欄位都列出來，審計員在同一個下拉換欄位就換條件；換到其他型別由本模組重設運算子。
  function groupedFields(state) {
    var all = fields(state);
    function group(label, predicate) { return { label: label, fields: all.filter(predicate) }; }
    var special = ['createdBy', 'approvedBy', 'accountNumber', 'accountName'];
    return [group('日期', function (item) { return item.type === 'date'; }),
      group('金額', function (item) { return item.type === 'amount'; }),
      group('人員', function (item) { return ['createdBy', 'approvedBy'].indexOf(item.id) >= 0; }),
      group('會計科目', function (item) { return ['accountNumber', 'accountName'].indexOf(item.id) >= 0; }),
      group('其他文字', function (item) { return item.type === 'text' && special.indexOf(item.id) < 0; })]
      .filter(function (item) { return item.fields.length > 0; });
  }
  function fieldOptionsHtml(state, selectedId) {
    var groups = groupedFields(state).map(function (group) {
      return '<optgroup label="' + group.label + '">' + group.fields.map(function (item) {
        var suffix = item.mapped || item.id === selectedId ? '' : '（' + Ui.GL_FIELD_UNMAPPED + Ui.GL_FIELD_NEXT_STEP + '）';
        return '<option value="' + Ui.esc(item.id) + '"' + (item.id === selectedId ? ' selected' : '') + (item.mapped ? '' : ' disabled') + '>' +
          Ui.esc(item.optionLabel + suffix) + '</option>';
      }).join('') + '</optgroup>';
    }).join('');
    var available = fields(state).some(function (item) { return item.id === selectedId; });
    var unavailable = selectedId && !available ? '<option value="' + Ui.esc(selectedId) + '" selected disabled>此欄位目前未配對，請重新選擇</option>' : '';
    return unavailable + groups;
  }
  // Presentation groups share an input intent; the original operator remains authoritative.
  var editorViews = new WeakMap();
  var motionSerial = 0;
  function editorView(rule) {
    if (!editorViews.has(rule)) { motionSerial += 1; editorViews.set(rule, { optionsOpen: false, periodEnd: false, days: 7, motionId: 'values-' + motionSerial }); }
    return editorViews.get(rule);
  }
  // app.js 的 JetMotion 比對用的識別：跟著同一個條件物件，不用條件的位置；同一畫面有兩條日期條件選了同一天時也不會撞在一起。
  function motionId(rule) { return editorView(rule).motionId; }
  function dateChipKey(rule, date) { return 'date-' + motionId(rule) + '-' + date; }
  // 已選日期的標籤直接隨畫面輸出，整段重繪時才比得到哪一個新出現、哪一個被移除。
  function dateChipsHtml(rule, state) {
    return values(rule, state).map(function (date) {
      return '<button type="button" class="scenario-pill" data-date-remove="' + Ui.esc(date) + '" data-motion="inline" data-motion-key="' +
        Ui.esc(dateChipKey(rule, date)) + '" aria-label="移除日期 ' + Ui.esc(date) + '">' + Ui.esc(date) + ' ×</button>';
    }).join('');
  }
  function hasAccountTree(rule) {
    return rule.field === 'accountNumber' && !rule.fieldId && (rule.operator === 'in' || rule.operator === 'notIn');
  }
  function accountTreeView(rule, state) {
    var view = editorView(rule), tree = view.accountTree;
    if (!tree || tree.project !== state.project || tree.generation !== state.dataGeneration || tree.accountCategory !== state.accountCategory) {
      tree = { project: state.project, generation: state.dataGeneration, accountCategory: state.accountCategory,
        open: false, categoryId: null, search: '', searchDraft: '', collapsed: {}, rows: [], cursor: null, nextCursor: null, sequence: 0,
        pending: false, error: '', loaded: false, lastRequest: null };
      view.accountTree = tree;
    }
    return tree;
  }
  function accountTreeHtml(rule, state, focusKey) {
    var tree = accountTreeView(rule, state), selected = new Set(values(rule, state));
    // 選分類時後端一律連下層分類的科目一起列出；案件沒有分類設上層時（新案件都沒有），不寫「包含下層分類」。
    var hierarchy = Ui.categoryHierarchyInUse(state);
    tree.focusKey = focusKey + '-category';
    var categoryRows = Ui.categoryTreeHtml(state, tree, function (category) {
      var checked = tree.categoryId === category.categoryId && tree.loaded && !tree.cursor && !tree.nextCursor && tree.rows.length &&
        tree.rows.every(function (row) { return selected.has(row.accountNumber); });
      return '<div class="account-tree__category">' +
        '<button type="button" class="btn btn--ghost btn--tiny" data-account-tree-category="' + Ui.esc(category.categoryId) +
        '" aria-expanded="' + (tree.categoryId === category.categoryId) + '">' + Ui.esc(category.label) + '</button>' +
        '<label><input type="checkbox" data-account-tree-select-category="' + Ui.esc(category.categoryId) + '"' +
        (checked ? ' checked' : '') + '>勾選此分類的科目' + (hierarchy ? '（包含下層分類）' : '') + '</label></div>';
    });
    var groups = [];
    tree.rows.forEach(function (row) {
      var group = groups.find(function (item) { return item.categoryId === row.categoryId; });
      if (!group) { group = { categoryId: row.categoryId, rows: [] }; groups.push(group); }
      group.rows.push(row);
    });
    var accounts = groups.map(function (group) {
      var label = group.categoryId ? Ui.accountCategoryLabel(state, group.categoryId) : '尚未分類';
      // 展開與收合要一致：每個分類的科目區跟著分類標識別，換分類或載入完成時舊的收起、新的長出來。
      return '<section class="account-tree__accounts" data-motion-key="' + Ui.esc('acct-' + motionId(rule) + '-' + (group.categoryId || 'none')) +
        '" aria-label="' + Ui.esc(label) + '的科目"><strong>' + Ui.esc(label) + '</strong>' +
        group.rows.map(function (row) {
          return '<div><label><input type="checkbox" data-account-tree-code="' + Ui.esc(row.accountNumber) + '"' +
            (selected.has(row.accountNumber) ? ' checked' : '') + '>' + Ui.esc(row.accountNumber) +
            (row.accountName ? '　' + Ui.esc(row.accountName) : '') + '</label></div>';
        }).join('') + '</section>';
    }).join('');
    var status = tree.pending ? '正在載入科目…' : tree.loaded ?
      (tree.rows.length ? '本頁 ' + Ui.countText(tree.rows.length) + ' 個科目；已選 ' + Ui.countText(selected.size) + ' 個。' : '目前範圍沒有符合的科目。') : '先選分類，或搜尋本案科目。';
    return '<details class="value-editor__options" data-account-tree' + (tree.open ? ' open' : '') + '><summary>從科目樹選取</summary>' +
      '<div class="value-editor__aux" data-motion-list="account-tree">' +
      Ui.searchBoxHtml('搜尋科目編號或名稱', { attributes: { 'data-account-tree-search-form': true },
        inputAttributes: { 'data-account-tree-search': true, 'data-focus-key': focusKey + '-account-search' }, maxLength: Ui.SEARCH_TEXT_MAX_LENGTH, value: tree.searchDraft }) +
      '<p class="rule-field__hint">' + (hierarchy ? '選分類時包含下層分類的科目；' : '') + '勾選後寫入上方科目編號清單，不改變符合或排除方式。</p>' +
      '<button type="button" class="btn btn--ghost btn--tiny" data-account-tree-category="">查看全部科目</button>' + categoryRows +
      '<p data-account-tree-status data-motion-key="' + Ui.esc('acct-status-' + motionId(rule)) + '" role="status">' + status + '</p>' + accounts +
      (tree.nextCursor ? '<button type="button" class="btn btn--ghost btn--tiny" data-account-tree-more data-motion-key="' + Ui.esc('acct-more-' + motionId(rule)) + '"' +
        (tree.pending ? ' disabled' : '') + '>載入下一頁科目</button>' : '') +
      // 錯誤與重新載入按鈕用同一元件，保留節點識別以沿用展開與收合動態。
      (tree.error ? Ui.loadFailureHtml(tree.error, tree.lastRequest ? { 'data-account-tree-retry': true } : null,
        { 'data-account-tree-error': true, 'data-motion-key': 'acct-error-' + motionId(rule) }) : '') +
      '</div></details>';
  }
  function bindAccountTree(root, rule, state, changed) {
    if (!hasAccountTree(rule)) { return; }
    var details = root.querySelector('[data-account-tree]');
    if (!details) { return; }
    var tree = accountTreeView(rule, state);
    Ui.bindCategoryPicker(details, state, tree);
    function currentState() { return global.JetStore && global.JetStore.getState ? global.JetStore.getState() : state; }
    function containsRule(current) {
      function includes(rules) { return (rules || []).some(function (item) { return item === rule || includes(item.conditions); }); }
      return !!(current.filter && current.filter.draft && (current.filter.draft.groups || []).some(function (group) { return includes(group.conditions); }));
    }
    function active() {
      var current = currentState();
      return hasAccountTree(rule) && editorView(rule).accountTree === tree && current.project === tree.project &&
        current.dataGeneration === tree.generation && current.accountCategory === tree.accountCategory && containsRule(current);
    }
    function redraw() {
      if (global.JetStore && global.JetStore.touch) { global.JetStore.touch(); }
      else { changed(true); }
    }
    function limitMessage() {
      return '每條科目編號清單最多 ' + Ui.TYPED_SET_MAX_VALUES + ' 個不同科目。請縮小選取範圍，或改用科目分類條件。';
    }
    function setCodes(codes, checked) {
      var current = values(rule, currentState());
      var next = checked ? Array.from(new Set(current.concat(codes))) : current.filter(function (code) { return codes.indexOf(code) < 0; });
      if (next.length > Ui.TYPED_SET_MAX_VALUES) { tree.error = limitMessage(); return false; }
      tree.error = ''; rule.values = next; changed(true); return true;
    }
    function readPage(payload, selection) {
      if (!active()) { return; }
      var sequence = ++tree.sequence;
      tree.categoryId = payload.categoryId; tree.search = payload.search || ''; tree.cursor = payload.cursor; tree.open = true;
      tree.pending = true; tree.error = ''; tree.rows = []; tree.nextCursor = null; tree.loaded = false;
      tree.lastRequest = { payload: payload, selection: selection };
      var request;
      try { request = global.JetApi.queryAccountMappingPage(payload); }
      catch (error) { request = Promise.reject(error); }
      redraw();
      Promise.resolve(request).then(function (page) {
        if (!active() || tree.sequence !== sequence) { return; }
        tree.pending = false; tree.rows = page.rows || []; tree.nextCursor = page.nextCursor || null; tree.loaded = true;
        if (selection !== null) {
          if (tree.nextCursor) { tree.error = limitMessage(); }
          else {
            var codes = tree.rows.map(function (row) { return row.accountNumber; });
            setCodes(codes, selection);
          }
        }
        redraw();
      }).catch(function (error) {
        if (!active() || tree.sequence !== sequence) { return; }
        tree.pending = false; tree.error = Ui.errorText(error, '科目清單讀取失敗。') + ' 可按「重新載入」重試。';
        redraw();
      });
    }
    function payload(categoryId, cursor) {
      return { categoryId: categoryId || null, search: tree.search, pageSize: Ui.TYPED_SET_MAX_VALUES, cursor: cursor || null };
    }
    details.addEventListener('toggle', function () { tree.open = details.open; });
    root.querySelectorAll('[data-account-tree-category]').forEach(function (button) {
      button.addEventListener('click', function () { readPage(payload(button.getAttribute('data-account-tree-category'), null), null); });
    });
    root.querySelectorAll('[data-account-tree-select-category]').forEach(function (control) {
      control.addEventListener('change', function () {
        // 勾選分類一律選整個分類與下層分類，不受搜尋字限制；
        // 清單跟著顯示整個分類，搜尋框一併清空，畫面上看到的就是選到的範圍。
        var request = payload(control.getAttribute('data-account-tree-select-category'), null);
        request.search = ''; tree.searchDraft = '';
        readPage(request, control.checked);
      });
    });
    root.querySelectorAll('[data-account-tree-code]').forEach(function (control) {
      control.addEventListener('change', function () {
        if (!active()) { return; }
        tree.lastRequest = null;
        setCodes([control.getAttribute('data-account-tree-code')], control.checked); redraw();
      });
    });
    Ui.bindSearchBox(root.querySelector('[data-account-tree-search-form]'), function (text) {
      tree.search = text; tree.searchDraft = text; readPage(payload(tree.categoryId, null), null);
    }, function (text) { tree.searchDraft = text; });
    var more = root.querySelector('[data-account-tree-more]');
    if (more) more.addEventListener('click', function () {
      if (!tree.pending && tree.nextCursor) { readPage(payload(tree.categoryId, tree.nextCursor), null); }
    });
    var retry = root.querySelector('[data-account-tree-retry]');
    if (retry) retry.addEventListener('click', function () {
      if (tree.lastRequest) { readPage(tree.lastRequest.payload, tree.lastRequest.selection); }
    });
  }
  var commonModes = {
    date: [
      { key: 'dates', label: '指定日期', yes: 'in', no: 'notIn', aliases: ['on', 'notEquals'] },
      { key: 'range', label: '日期區間', yes: 'between', no: 'notBetween' },
      { key: 'monthDays', label: '每月幾日', yes: 'dayOfMonthIn', no: 'dayOfMonthNotIn' },
      { key: 'monthStart', label: '每月月初幾天', yes: 'monthStartDays', no: 'notMonthStartDays' },
      { key: 'monthEnd', label: '每月月底幾天', yes: 'monthEndDays', no: 'notMonthEndDays' },
      { key: 'weekend', label: '週末', yes: 'isWeekend', no: 'isNotWeekend' },
      { key: 'holiday', label: '國定假日', yes: 'isHoliday', no: 'isNotHoliday' },
      { key: 'makeup', label: '補班日', yes: 'isMakeupDay', no: 'isNotMakeupDay' },
      { key: 'nonBusiness', label: '非營業日', yes: 'isNonBusinessDay', no: 'isNotNonBusinessDay' },
      { key: 'before', label: '早於日期', yes: 'before', aliases: ['onOrBefore'] },
      { key: 'after', label: '晚於日期', yes: 'after', aliases: ['onOrAfter'] },
      { key: 'blank', label: '空白日期', yes: 'isBlank', no: 'isNotBlank' }
    ],
    text: [
      { key: 'contains', label: '包含關鍵字', yes: 'contains', no: 'notContains' },
      { key: 'exact', label: '完整內容相同', yes: 'in', no: 'notIn', aliases: ['equals', 'notEquals'] },
      { key: 'starts', label: '開頭符合', yes: 'startsWith', no: 'notStartsWith' },
      { key: 'ends', label: '結尾符合', yes: 'endsWith', no: 'notEndsWith' },
      { key: 'blank', label: '空白內容', yes: 'isBlank', no: 'isNotBlank' }
    ],
    amount: [
      { key: 'exact', label: '指定金額', yes: 'in', no: 'notIn', aliases: ['equals', 'notEquals'] },
      { key: 'range', label: '金額區間', yes: 'between', no: 'notBetween' },
      { key: 'digits', label: '整數部分的特定數字', yes: 'containsDigits', no: 'notContainsDigits' },
      { key: 'tails', label: '整數部分的特定尾數', yes: 'endsWithDigits', no: 'notEndsWithDigits' },
      { key: 'zeros', label: '尾數連續多個 0', yes: 'endsWithZeros', no: 'notEndsWithZeros' },
      { key: 'above', label: '高於金額', yes: 'greaterThan', aliases: ['greaterThanOrEqual'] },
      { key: 'below', label: '低於金額', yes: 'lessThan', aliases: ['lessThanOrEqual'] },
      { key: 'blank', label: '空白金額', yes: 'isBlank', no: 'isNotBlank' }
    ]
  };
  // 篩選方式選單依類別分組並加小標，選項名稱不變。
  // 每一種篩選方式都要列在其中一組；「查核期末最後幾天」只在案件有查核截止日時出現。
  var MODE_GROUPS = {
    date: [['指定日期', ['dates', 'range', 'before', 'after']], ['每月固定日', ['monthDays', 'monthStart', 'monthEnd', 'periodEnd']],
      ['週末與假日', ['weekend', 'holiday', 'makeup', 'nonBusiness']], ['空白', ['blank']]],
    amount: [['指定金額', ['exact', 'range', 'above', 'below']], ['特定數字', ['digits']], ['尾數', ['tails', 'zeros']], ['空白', ['blank']]],
    text: [['比對文字', ['contains', 'exact', 'starts', 'ends']], ['空白', ['blank']]]
  };
  function selectedMode(rule, state) {
    return commonModes[fieldType(rule, state)].find(function (item) {
      return item.yes === rule.operator || item.no === rule.operator || (item.aliases || []).indexOf(rule.operator) >= 0;
    });
  }
  function excluded(rule, mode) { return !!mode && (rule.operator === mode.no || rule.operator === 'notEquals'); }
  // 文字欄位的否定比較預設把欄位空白的分錄也列入，審計員可以取消；日期與金額照舊預設不列入。
  function blankIncludedByDefault(type, operator) {
    return type === 'text' && ['notEquals', 'notContains', 'notStartsWith', 'notEndsWith', 'notIn'].indexOf(operator) >= 0;
  }
  function changeOperator(rule, state, operator) {
    var type = fieldType(rule, state);
    if (carrier(operator, type) !== carrier(rule.operator, type)) { rule.values = []; }
    // 換到預設不同的比較方式時，「空白也列入」回到新比較方式的預設；畫面一律把勾選結果送給後端。
    if (blankIncludedByDefault(type, operator) !== blankIncludedByDefault(type, rule.operator)) { rule.includeBlank = blankIncludedByDefault(type, operator); }
    rule.operator = operator;
  }
  var delimiterHint = '可用換行、半形或全形逗號、頓號與 Tab 分隔。';
  // NumberStyles.Number accepts grouping commas and a leading OR trailing sign, not currency,
  // parentheses or exponent notation. Keep permissive grouping; backend decimal/scaling remains authoritative.
  function moneyNumber(raw) {
    var text = String(raw == null ? '' : raw).trim();
    if (text === '-') { return 0; }
    var match = /^([+-]?)\s*((?:\d[\d,]*(?:\.\d*)?|\.\d+))\s*([+-]?)$/.exec(text);
    if (!match || match[1] && match[3]) { return null; }
    var number = Number((match[1] || match[3]) + match[2].replace(/,/g, ''));
    return Number.isFinite(number) ? number : null;
  }
  function warning(rule, state) {
    var selected = field(rule, state), value = wire(rule, state), mode = carrier(rule.operator, fieldType(rule, state));
    if (selected && selected.type === 'text' && mode === 'value' && /[\r\n]/.test(value.value)) {
      return '目前輸入了多行，會整段當成同一個值。';
    }
    if (selected && selected.type === 'amount' && !ignoresSign(rule.operator) && value.amountBasis === 'absolute') {
      var inputs = mode === 'set' ? value.values : mode === 'range' ? [value.from, value.to] : [value.value];
      if (inputs.some(function (input) { var number = moneyNumber(input); return number !== null && number < 0; })) {
        return selected && selected.extra ? '比較基準是絕對值，但填了負數；要比較正負號請改選「含正負號」。' :
          '金額只比較絕對值，目前填了負數。請填正數；要限定方向請另加「借貸別」條件。';
      }
    }
    return '';
  }
  function render(rule, state, focusKey) {
    var selected = field(rule, state), type = fieldType(rule, state), mode = carrier(rule.operator, type);
    var choice = selectedMode(rule, state), view = editorView(rule);
    var main = '';
    if (choice) {
      // 開頭寫「符合」「排除」；只有一種方向的比較方式只寫「符合」。
      main += choice.no ? '<select data-value-polarity aria-label="符合或排除"><option value="keep"' + (!excluded(rule, choice) ? ' selected' : '') +
        '>符合</option><option value="exclude"' + (excluded(rule, choice) ? ' selected' : '') + '>排除</option></select>' : '<span>符合</span>';
    } else { main += '<span class="value-advanced-current">' + Ui.esc(labels[rule.operator] || '請設定比較方式') + '</span>'; }
    var periodEndOption = type === 'date' && state.project && state.project.periodEnd
      ? '<option value="periodEnd"' + (view.periodEnd ? ' selected' : '') + '>查核期末最後幾天</option>' : '';
    main += '<select data-value-kind aria-label="篩選方式">' + (!choice ? '<option value="advanced" selected>其他比較方式</option>' : '') +
      MODE_GROUPS[type].map(function (group) {
        var options = group[1].map(function (key) {
          if (key === 'periodEnd') { return periodEndOption; }
          var item = commonModes[type].find(function (mode) { return mode.key === key; });
          // 沒有國定假日清單或補班日清單時，只看清單的那一項停用並在名稱後寫原因，和未配對的欄位相同；
          //已選的條件照樣顯示。
          var listKey = !item ? null : item.key === 'holiday' ? 'holidayCalendar' : item.key === 'makeup' ? 'makeupCalendar' : null;
          var blocked = !!listKey && choice !== item && !Ui.rulePrerequisiteMet(state, listKey);
          return item ? '<option value="' + item.key + '"' + (choice === item && !view.periodEnd ? ' selected' : '') + (blocked ? ' disabled' : '') + '>' +
            item.label + (blocked ? '（' + Ui.esc(Ui.rulePrerequisiteReason(state, listKey)) + '）' : '') + '</option>' : '';
        }).join('');
        return options ? '<optgroup label="' + group[0] + '">' + options + '</optgroup>' : '';
      }).join('') + '</select>';
    function input(key, label) {
      if (type === 'date') { return Ui.dateInputHtml({ 'data-value-key': key, 'aria-label': label, value: normalizeDate(rule[key] || '', state) }); }
      // A textarea retains pasted line breaks so single-value comparisons can explain them instead of silently flattening them.
      if (type === 'text' || isDigitList(rule.operator)) { return '<textarea rows="1" class="value-editor__list" data-value-key="' + key + '" aria-label="' + label + '" placeholder="' + label + '">' + Ui.esc(rule[key] || '') + '</textarea>'; }
      return '<input type="text" data-value-key="' + key + '" aria-label="' + label + '" placeholder="' + label + '" value="' + Ui.esc(rule[key] || '') + '">';
    }
    var extra = [];
    var canIncludeBlank = (mode !== 'none' || isCalendar(rule.operator)) && !(rule.field === 'glDate' && !rule.fieldId);
    if (isCalendar(rule.operator)) {
      main += '<p class="rule-explanation">' + Ui.esc(calendarExplanation(rule.operator, state)) + '</p>';
    }
    if (isTail(rule.operator)) { main += '<p class="rule-explanation">忽略正負號及小數。' + delimiterHint + '例如 -1,001.45 符合 001，1 元不符合 001。</p>'; }
    if (isDigitContains(rule.operator)) { main += '<p class="rule-explanation">只看整數部分，忽略正負號及小數。' + delimiterHint + '例如 -12,888.45 的整數部分是 12888，包含 888，也包含 28。</p>'; }
    if (isZeroRun(rule.operator)) { main += '<p class="rule-explanation">只看整數部分，忽略正負號及小數；整數部分為 0 的金額不符合。例如 -3,000,000.45 符合 6 個 0。</p>'; }
    if (!selected) { main += '<p class="form-notice">此欄位目前未配對，請回第三步確認，或移除後重新加入。</p>'; }
    if (isMonthWindow(rule.operator)) {
      main += '<input type="number" data-value-key="value" min="1" max="31" aria-label="每月天數（1 到 31）" value="' + Ui.esc(rule.value || '') + '"><span>天（含當天）</span>';
    } else if (isZeroRun(rule.operator)) {
      main += '<input type="number" data-value-key="value" min="1" max="' + Ui.TAIL_DIGITS_MAX + '" aria-label="連續 0 的個數（1 到 ' + Ui.TAIL_DIGITS_MAX + '）" value="' + Ui.esc(rule.value || '') + '"><span>個 0</span>';
    } else if (mode === 'value') { main += input('value', isTail(rule.operator) ? '尾數（以逗號分隔）' : isDigitContains(rule.operator) ? '數字（以逗號分隔）' : type === 'amount' ? '金額' : '條件值'); }
    if (view.periodEnd) {
      main += '<input type="number" data-period-end-days min="1" max="366" value="' + view.days + '" aria-label="期末最後幾天"><span>天</span>' +
        '<p class="value-period-range">' + Ui.esc(rule.from || '') + ' 至 ' + Ui.esc(rule.to || '') + '（含起迄日）</p>';
    } else if (mode === 'range') { main += '<div class="value-editor__range" aria-label="篩選區間">' + input('from', '起點（包含）') + '<span class="rule-row__sep">至</span>' + input('to', '終點（包含）') + '</div>'; }
    if (choice && ['above', 'below', 'before', 'after'].indexOf(choice.key) >= 0) {
      main += '<label><input type="checkbox" data-value-inclusive' + (/OrEqual$|^onOr/.test(rule.operator) ? ' checked' : '') + '>' + (type === 'date' ? '含當天' : '含門檻金額') + '</label>';
    }
    if (mode === 'set' && isDayOfMonth(rule.operator)) {
      main += '<textarea rows="1" class="value-editor__list value-editor__days" data-value-key="daysOfMonth" inputmode="numeric" aria-label="每月幾日（1 到 31）" placeholder="例如：28,31">' + Ui.esc(values(rule, state).join(',')) + '</textarea><span>日</span><p class="rule-field__hint">' + delimiterHint + '</p>';

    } else if (mode === 'set') {
      var current = values(rule, state);
      var isPerson = ['createdBy', 'approvedBy'].indexOf(rule.field) >= 0 && !rule.fieldId;
      var listHint = type === 'date' ? '每行一個日期' : type === 'text' && /^(contains|notContains)$/.test(rule.operator) ? '每行一個關鍵字' :
        isPerson ? '每行一個人員代號或姓名' : rule.field === 'accountNumber' ? '每行一個科目編號' : '每行一個值';
      var list = '<textarea rows="1" class="value-editor__list value-editor__values" data-value-key="values" data-values-for="' + Ui.esc(motionId(rule)) + '" aria-label="' + listHint + '" placeholder="' + listHint + '">' + Ui.esc((Array.isArray(rule.values) ? rule.values : isAffix(rule.operator) && rule.value ? [rule.value] : []).join('\n')) + '</textarea>';
      if (delimitedList(rule, state)) { list += '<p class="rule-field__hint">' + delimiterHint + '</p>'; }
      else if (type === 'text') { list += '<p class="rule-field__hint">文字值清單每行一個值；值內的逗號、頓號與 Tab 保留，不會拆開。</p>'; }
      if (isPerson) { list += '<p class="help-text">請填 GL 裡的姓名或員工代碼；姓名本身有逗號時，請改填員工代碼。</p>'; }
      if (type === 'date') {
        main += Ui.dateInputHtml({ 'data-date-input': true, 'data-focus-key': focusKey + '-date', 'aria-label': '加入日期' }) +
          '<button type="button" class="btn btn--ghost" data-date-add>加入</button>' +
          '<p class="form-notice value-editor__date-error" data-date-error data-collapsible role="alert" hidden></p>' +
          '<div class="selected-dates" data-selected-dates data-motion-key="' + Ui.esc('dates-' + motionId(rule)) + '" data-motion-list="selected-dates" aria-label="已選日期">' +
          dateChipsHtml(rule, state) + '</div>';
        extra.push('<label>貼上日期清單' + list + '</label>');
      } else { main += list; }
      extra.push('<p class="rule-field__hint">最多 ' + Ui.TYPED_SET_MAX_VALUES + ' 個不同值，空行不列入。' + (type === 'date' ? '日期可用 2025-08-01、2025/8/1、2025.8.1 或 20250801；民國年依案件設定。純數字不當成日期。' : '保留文字前置零；金額千分位逗號不拆開。') + '</p>');
      if (current.length) { extra.push('<button type="button" class="btn btn--ghost btn--tiny" data-values-clear data-motion-key="' + Ui.esc('values-clear-' + motionId(rule)) + '">清空所有值</button>'); }
    }
    if (type === 'amount' && selected && selected.extra && !ignoresSign(rule.operator)) {
      var basis = rule.amountBasis || 'absolute';
      extra.push('<label>金額比較基準<select data-value-key="amountBasis" aria-label="金額比較基準"><option value="absolute"' + (basis !== 'signed' ? ' selected' : '') + '>絕對值（不分借貸）</option><option value="signed"' + (basis === 'signed' ? ' selected' : '') + '>含正負號</option></select></label>');
    }
    if (type === 'text' && mode === 'value') { main += '<p class="rule-field__hint">此比較方式只收一個值；不會將逗號或多行內容拆成多個條件。</p>'; }
    if (mode === 'set' || isDigitList(rule.operator)) { main += '<p class="rule-field__hint" data-value-count role="status">已輸入 ' + values(rule, state).length + ' 個值，上限 ' + Ui.TYPED_SET_MAX_VALUES + '</p>'; }
    // 展開與收合要一致：提醒原地收放，標 data-collapsible；收起時保留原來的字（見 bind）。
    main += '<p class="form-notice form-notice--warning" data-value-warning data-collapsible role="status"' + (warning(rule, state) ? '' : ' hidden') + '>' + Ui.esc(warning(rule, state)) + '</p>';
    if (canIncludeBlank) {
      extra.push('<label class="value-blank"><input type="checkbox" data-value-key="includeBlank"' + (blankMatches(rule) ? ' checked' : '') + '>' +
        (type === 'date' ? '沒有日期的分錄也列入結果' : '此欄位空白的分錄也列入結果') + '</label>');
    }
    if (hasAccountTree(rule)) { main += accountTreeHtml(rule, state, focusKey); }
    else if (rule.field === 'accountNumber' && !rule.fieldId) {
      main += '<p class="rule-field__hint">要從科目樹選取，請將篩選方式改為「完整內容相同」。</p>';
    }
    var activeNotes = [];
    if (canIncludeBlank && blankMatches(rule)) { activeNotes.push('含空白'); }
    if (type === 'amount' && rule.amountBasis === 'signed' && !ignoresSign(rule.operator)) { activeNotes.push('含正負號'); }
    return '<div class="value-editor" data-focus-prefix="' + focusKey + '"><div class="value-editor__main">' + main + '</div>' +
      (extra.length ? '<details class="value-editor__options" data-value-options' + (view.optionsOpen ? ' open' : '') + '><summary>' + (type === 'amount' ? '金額與空白值處理' : type === 'date' && mode === 'set' && !isDayOfMonth(rule.operator) ? (canIncludeBlank ? '貼上日期與空白值處理' : '貼上日期清單') : '空白值處理') +
        (activeNotes.length ? '（' + activeNotes.join('、') + '）' : '') + '</summary><div class="value-editor__aux">' + extra.join('') + '</div></details>' : '') + '</div>';
  }
  function bind(root, rule, state, changed) {
    if (!root) { return; }
    function fitLists() {
      // 值清單（value-editor__values）一開始就是固定的多行高度，貼上或拆成多行都不改高度；
      // 這裡只調整單值與每月幾日這類單行輸入。
      root.querySelectorAll('textarea.value-editor__list:not(.value-editor__values)').forEach(function (input) {
        if (!input.getClientRects().length) { return; }
        input.style.height = 'auto';
        // 起始高度 32px，和旁邊的下拉選單同高（app.css 的 --control-height）。
        input.style.height = Math.min(160, Math.max(32, input.scrollHeight)) + 'px';
      });
    }
    fitLists();
    var options = root.querySelector('[data-value-options]');
    if (options) options.addEventListener('toggle', function () { editorView(rule).optionsOpen = options.open; fitLists(); });
    var kind = root.querySelector('[data-value-kind]');
    function setPeriod(days) {
      var parts = state.project.periodEnd.split('-').map(Number);
      rule.from = iso(day(parts[0], parts[1], parts[2] - (days - 1)));
      rule.to = iso(day(parts[0], parts[1], parts[2]));
      editorView(rule).days = days;
    }
    kind.addEventListener('change', function () {
      var current = selectedMode(rule, state);
      editorView(rule).periodEnd = kind.value === 'periodEnd';
      if (kind.value === 'periodEnd') {
        changeOperator(rule, state, excluded(rule, current) ? 'notBetween' : 'between');
        setPeriod(editorView(rule).days); changed(true); return;
      }
      var next = commonModes[fieldType(rule, state)].find(function (item) { return item.key === kind.value; });
      if (!next) { return; }
      changeOperator(rule, state, excluded(rule, current) && next.no ? next.no : next.yes);
      if (isMonthWindow(rule.operator) && !/^\d{1,2}$/.test(rule.value || '')) { rule.value = '2'; }
      // 改成尾數連續多個 0 時，個數沒填或不是數字就帶第四步預篩選由 GL 算出的個數。
      if (isZeroRun(rule.operator) && !/^\d{1,2}$/.test(rule.value || '')) { rule.value = zeroRunDefault(); }
      changed(true);
    });
    var polarity = root.querySelector('[data-value-polarity]');
    if (polarity) { polarity.addEventListener('change', function () {
      var pairs = { on: ['on', 'notEquals'], equals: ['equals', 'notEquals'], notEquals: [fieldType(rule, state) === 'date' ? 'on' : 'equals', 'notEquals'] };
      var current = selectedMode(rule, state), pair = pairs[rule.operator] || [current.yes, current.no];
      changeOperator(rule, state, pair[polarity.value === 'exclude' ? 1 : 0]); changed(true);
    }); }
    var inclusive = root.querySelector('[data-value-inclusive]');
    if (inclusive) { inclusive.addEventListener('change', function () {
      var current = selectedMode(rule, state);
      var inclusiveOperator = fieldType(rule, state) === 'date' ? (current.key === 'before' ? 'onOrBefore' : 'onOrAfter') : current.yes + 'OrEqual';
      changeOperator(rule, state, inclusive.checked ? inclusiveOperator : current.yes); changed(true);
    }); }
    var periodDays = root.querySelector('[data-period-end-days]');
    if (periodDays && editorView(rule).periodEnd) { periodDays.addEventListener('change', function () {
      if (!periodDays.checkValidity() || !periodDays.value) { periodDays.reportValidity(); return; }
      setPeriod(Number(periodDays.value)); changed(true);
    }); }
    root.querySelectorAll('[data-value-key]').forEach(function (control) {
      control.addEventListener(control.tagName === 'SELECT' || control.type === 'checkbox' ? 'change' : 'input', function () {
        var key = control.getAttribute('data-value-key'), structural = key === 'field' || key === 'operator' || key === 'includeBlank' || key === 'amountBasis' || key === 'drCr';
        if (key === 'field') {
          var selected = fields(state).find(function (item) { return item.id === control.value; });
          if (!selected) { return; }
          delete rule.field; delete rule.fieldId;
          rule[selected.extra ? 'fieldId' : 'field'] = selected.id;
          rule.operator = selected.type === 'date' || !selected.extra && ['createdBy', 'approvedBy', 'accountNumber'].indexOf(selected.id) >= 0 ? 'in' : selected.type === 'text' ? 'contains' : 'equals';
          rule.amountBasis = 'absolute';
          rule.value = ''; rule.from = ''; rule.to = ''; rule.values = []; rule.includeBlank = false;
        } else if (key === 'values') { rule.values = delimitedList(rule, state) ? splitDelimited(control.value) : control.value.split(/\r?\n/); }
        else if (key === 'daysOfMonth') { rule.values = splitDelimited(control.value); }
        else if (key === 'includeBlank') { rule.includeBlank = control.checked; }
        else {
          if (key === 'operator' && carrier(control.value, fieldType(rule, state)) !== carrier(rule.operator, fieldType(rule, state))) { rule.values = []; }
          rule[key] = control.value;
        }
        changed(structural);
        var counter = root.querySelector('[data-value-count]');
        if (counter) { counter.textContent = '已輸入 ' + values(rule, state).length + ' 個值，上限 ' + Ui.TYPED_SET_MAX_VALUES; }
        var notice = root.querySelector('[data-value-warning]');
        if (notice) {
          var noticeText = warning(rule, state);
          if (noticeText) { notice.textContent = noticeText; }
          notice.hidden = !noticeText;
        }
        if (key === 'values') { refreshChips(); fitLists(); }
      });
    });
    function motion() { return global.JetMotion && typeof global.JetMotion.morph === 'function' ? global.JetMotion : null; }
    function bindChips(chips) {
      chips.querySelectorAll('[data-date-remove]').forEach(function (button) {
        button.addEventListener('click', function () {
          var date = button.getAttribute('data-date-remove');
          // 移除時先輕微快速地晃一下：被移除的日期標籤先晃，再在下一次重繪時收起。
          if (motion() && typeof motion().removing === 'function') { motion().removing({ keys: [dateChipKey(rule, date)] }); }
          rule.values = values(rule, state).filter(function (value) { return value !== date; });
          changed(true);
        });
      });
    }
    // 在日期清單裡貼上或刪改時不會整段重繪，標籤在這裡重建；交給 JetMotion.morph，新的日期長出來、刪掉的收起來。
    function refreshChips() {
      var chips = Ui.findLive(root, '[data-selected-dates]');
      if (!chips) { return; }
      function rebuild() { chips.innerHTML = dateChipsHtml(rule, state); bindChips(chips); }
      if (motion()) { motion().morph(chips, rebuild); } else { rebuild(); }
    }
    var dates = root.querySelector('[data-selected-dates]');
    if (dates) { bindChips(dates); }
    var clear = root.querySelector('[data-values-clear]');
    if (clear) { clear.addEventListener('click', function () {
      var jet = motion();
      // 清除時先輕微快速地晃一下：所有日期標籤先晃再收起；值清單不會拿掉，只是被清空，
      // 所以重繪後直接晃新的那一個（重繪是同步的；沒有重繪時晃原本那一個）。
      if (jet && typeof jet.removing === 'function') {
        jet.removing({ keys: values(rule, state).map(function (date) { return dateChipKey(rule, date); }) });
      }
      rule.values = []; changed(true);
      if (jet && typeof jet.shake === 'function') {
        var doc = global.document;
        var list = doc && typeof doc.querySelector === 'function'
          ? doc.querySelector('[data-values-for="' + motionId(rule) + '"]') : null;
        if (!list || !list.isConnected) { list = root.querySelector('[data-value-key="values"]'); }
        if (list) { jet.shake(list); }
      }
    }); }
    var dateInput = root.querySelector('[data-date-input]');
    if (dateInput) {
      var dateError = root.querySelector('[data-date-error]');
      function addDate() {
        if (!dateInput.value || dateInput.checkValidity && !dateInput.checkValidity()) {
          if (dateInput.reportValidity) { dateInput.reportValidity(); }
          return;
        }
        var selected = values(rule, state), date = dateInput.value;
        var duplicate = selected.indexOf(date) >= 0;
        if (!duplicate && selected.length >= Ui.TYPED_SET_MAX_VALUES) {
          dateError.textContent = '最多選取 ' + Ui.TYPED_SET_MAX_VALUES + ' 個不同日期，請先移除不需要的日期。';
          dateError.hidden = false;
          return;
        }
        dateInput.value = ''; dateError.hidden = true;
        var key = dateInput.getAttribute('data-focus-key');
        if (!duplicate) { rule.values = selected.concat(date); changed(true); }
        if (global.JetFocus) { global.JetFocus.defer(function () { return document.querySelector('[data-focus-key="' + key + '"]'); }); }
      }
      root.querySelector('[data-date-add]').addEventListener('click', addDate);
      dateInput.addEventListener('keydown', function (event) {
        if (event.key === 'Enter') { event.preventDefault(); addDate(); }
      });
      dateInput.addEventListener('input', function () { dateError.hidden = true; });
    }
    bindAccountTree(root, rule, state, changed);
  }
  function problem(rule, state) {
    var selected = field(rule, state);
    if (!selected) { return '重新選擇欄位，或回到第三步確認配對'; }
    var value = wire(rule, state), mode = carrier(rule.operator, selected.type);
    if (operators[selected.type].indexOf(rule.operator) < 0) { return selected.label + '需選擇比較方式'; }
    // 分錄方向無效只會出現在存檔內容損毀時，沿用損毀條件的儲存句。
    if (rule.drCr && ['debit', 'credit'].indexOf(rule.drCr) < 0) { return '此條件內容有誤，無法儲存。請刪除此條件後重新設定。'; }
    if (isMonthWindow(rule.operator)) {
      return /^\d{1,2}$/.test(value.value.trim()) && Number(value.value) >= 1 && Number(value.value) <= 31 ? '' : '每月月初或月底天數須為 1 到 31 的整數';
    }
    // 和後端 FieldValueConditions.TryParseZeroRun 同一個範圍與同一句。
    if (isZeroRun(rule.operator)) {
      return /^\d{1,2}$/.test(value.value.trim()) && Number(value.value) >= 1 && Number(value.value) <= Ui.TAIL_DIGITS_MAX ? ''
        : '連續 0 的個數須為 1 到 ' + Ui.TAIL_DIGITS_MAX + ' 的整數';
    }
    if (isDayOfMonth(rule.operator)) {
      var days = value.values;
      // 沒有填與填錯用同一句。
      if (!days.length || days.some(function (item) { return !/^\d{1,2}$/.test(item) || Number(item) < 1 || Number(item) > 31; })) { return '每月日期須為 1 到 31 的整數，至少填寫一個'; }
      return '';
    }
    var inputs = mode === 'set' ? value.values : mode === 'range' ? [value.from, value.to] : mode === 'value' ? [value.value] : [];
    // Count exactly what wire() sends; never guess case equivalence in JavaScript.
    var count = isDigitList(rule.operator) ? values(rule, state).length : mode === 'set' ? inputs.length : 0;
    if (count > Ui.TYPED_SET_MAX_VALUES) {
      // 子條件組的連接在畫面上只寫 AND 與 OR，提示照畫面上的字寫；後端 FieldValueConditions 同一句。
      return '每條最多 ' + Ui.TYPED_SET_MAX_VALUES + ' 個值。請拆成多條條件，放進子條件組，用 ' +
        (/^(notIn|notContains|notStartsWith|notEndsWith|notContainsDigits|notEndsWithDigits)$/.test(rule.operator) ? 'AND' : 'OR') + ' 連接。';
    }
    if (mode === 'set' && !inputs.length) { return '請填 1 到 ' + Ui.TYPED_SET_MAX_VALUES + ' 個值'; }
    if (inputs.some(function (input) { return !input.trim(); })) { return selected.label + '請填好條件值'; }
    if (isDigitList(rule.operator)) {
      var tails = splitDelimited(value.value);
      if (!tails.length || tails.length > Ui.TYPED_SET_MAX_VALUES ||
          tails.some(function (item) { return !/^\d+$/.test(item) || item.length > Ui.TAIL_DIGITS_MAX; })) {
        return (isTail(rule.operator) ? '尾數' : '數字') + '請填入 1 到 ' + Ui.TYPED_SET_MAX_VALUES + ' 組純數字，每組 1 到 ' + Ui.TAIL_DIGITS_MAX + ' 位';
      }
      return '';
    }
    // 原因句寫出無法辨識的那個值。
    if (selected.type === 'amount') {
      var badAmount = inputs.find(function (input) { return moneyNumber(input) === null; });
      if (badAmount !== undefined) { return '金額「' + badAmount + '」不是數字'; }
      if (mode === 'range' && moneyNumber(value.from) > moneyNumber(value.to)) { return '下限金額大於上限金額'; }
    }
    var badDate = selected.type !== 'date' ? undefined : inputs.find(function (input) {
      if (!/^\d{4}-\d{2}-\d{2}$/.test(input)) {
        // Recognizable invalid dates can be corrected here. Other invariant-culture formats
        // are forwarded unchanged to DateNormalizer; browser parsing must not redefine GL dates.
        // 純數字不是日期，在這裡就寫出無法辨識。
        return /^\d{1,4}[-/.]\d{1,2}[-/.]\d{1,2}$/.test(input) || moneyNumber(input) !== null;
      }
      var p = input.split('-').map(Number); return p[0] < 1 || iso(day(p[0], p[1], p[2])) !== input;
    });
    if (badDate !== undefined) { return '日期「' + badDate + '」無法辨識，請以 2026-12-31 的格式輸入'; }
    if (selected.type === 'date' && mode === 'range' && /^\d{4}-\d{2}-\d{2}$/.test(value.from) && /^\d{4}-\d{2}-\d{2}$/.test(value.to) && value.from > value.to) { return '起日晚於迄日'; }
    return '';
  }
  function create(type) {
    return { type: 'fieldValue', join: 'AND', field: type === 'date' ? 'glDate' : type === 'amount' ? 'amount' : 'description',
      operator: type === 'date' ? 'in' : type === 'text' ? 'contains' : 'equals', values: [], value: '', includeBlank: false, amountBasis: 'absolute', __valueType: type };
  }
  Ui.FilterValues = { create: create, render: render, bind: bind, wire: wire, summary: summary, field: field, fieldTitle: fieldTitle, fields: fields, groupedFields: groupedFields, fieldOptionsHtml: fieldOptionsHtml, values: values, normalizeDate: normalizeDate, splitDelimited: splitDelimited, problem: problem, isDayOfMonth: isDayOfMonth,
    isZeroRun: isZeroRun, zeroRunDefault: zeroRunDefault };
})(window);
